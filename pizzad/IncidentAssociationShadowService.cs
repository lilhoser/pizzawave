using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public sealed class IncidentAssociationShadowService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EmbeddingService _embeddings;
    private readonly ILogger<IncidentAssociationShadowService> _logger;
    private long? _lastSampledCallId;

    public IncidentAssociationShadowService(
        EngineConfig config,
        EngineDatabase database,
        EmbeddingService embeddings,
        ILogger<IncidentAssociationShadowService> logger)
    {
        _config = config;
        _database = database;
        _embeddings = embeddings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startupOffsetApplied = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsEnabled())
            {
                await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Incident association constructor shadow sampling failed; production incident state was not changed");
            }
            var seconds = (double)_config.AiInsights.IncidentAssociationShadowIntervalSeconds;
            if (!startupOffsetApplied)
            {
                startupOffsetApplied = true;
                seconds /= 2d;
            }
            await DelayAsync(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var runId = _config.AiInsights.IncidentAssociationShadowRunId.Trim();
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-_config.AiInsights.IncidentAssociationShadowLookbackMinutes).ToUnixTimeSeconds();
        var calls = (await _database.ListCallsAsync(start, now.ToUnixTimeSeconds(), null, ct))
            .Where(IncidentAssociationLiveSelection.IsEligibleSourceObservation)
            .OrderBy(call => call.Id)
            .ToList();
        if (_lastSampledCallId is null)
        {
            var latest = await _database.GetLatestIncidentAssociationShadowLedgerEntryAsync(runId, ct);
            _lastSampledCallId = SourceCallId(latest?.Entry) ?? calls.LastOrDefault()?.Id ?? 0;
            _logger.LogInformation(
                "Incident association constructor shadow run {RunId} initialized after call {CallId}; historical calls will not be backfilled",
                runId,
                _lastSampledCallId);
            return;
        }

        var newCall = calls.LastOrDefault(call => call.Id > _lastSampledCallId.Value);
        if (newCall is null)
            return;
        var priorStored = await _database.GetLatestIncidentAssociationShadowProjectionAsync(runId, ct);
        var prior = priorStored?.Projection;
        var matches = prior is null
            ? []
            : await _embeddings.SearchSimilarAsync(
                newCall.Transcription,
                newCall.SystemShortName,
                start,
                now.ToUnixTimeSeconds(),
                20,
                ct);
        var selection = IncidentAssociationLiveSelection.Build(
            newCall,
            calls,
            matches,
            prior,
            _config.AiInsights.IncidentAssociationShadowCandidateLimit,
            now);
        var proposer = new OpenAiIncidentAssociationProposer(_config, _database, _logger, runId);
        var coordinator = new IncidentAssociationShadowCoordinator(proposer, _database);
        var callIdentity = newCall.Id.ToString(CultureInfo.InvariantCulture);
        var result = await coordinator.RunAsync(
            new IncidentAssociationShadowRunRequest(
                runId,
                $"association-live:{runId}:ledger:call:{callIdentity}",
                $"association-live:{runId}:projection:call:{callIdentity}",
                $"association-live:{runId}:event:call:{callIdentity}",
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                ConfigurationIdentity()),
            selection.Bundle,
            prior,
            selection.NewObservationId,
            selection.Candidates,
            ct);
        _lastSampledCallId = newCall.Id;
        _logger.LogInformation(
            "Incident association constructor shadow run {RunId} sampled call {CallId}: {Outcome}, provisional={ProvisionalCount}, candidates={CandidateCount}, proposerMs={DurationMs}, proposerError={HasError}; production incident state unchanged",
            runId,
            newCall.Id,
            result.LedgerEntry.Entry.Transition.Outcome,
            result.LedgerEntry.Entry.Proposal.Relationships.Count(item => item.Disposition == IncidentAssociationDisposition.ProvisionalAssociation),
            selection.Candidates.Count,
            result.LedgerEntry.Entry.Execution.ProposerDurationMilliseconds,
            !string.IsNullOrWhiteSpace(result.LedgerEntry.Entry.Execution.ProposerError));
    }

    private bool IsEnabled() =>
        _config.Setup.Completed &&
        _config.AiInsights.Enabled &&
        _config.AiInsights.IncidentAssociationShadowEnabled &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.IncidentAssociationShadowRunId) &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel);

    private string ConfigurationIdentity() =>
        $"incident-association-constructor-v1;run={_config.AiInsights.IncidentAssociationShadowRunId.Trim()};interval={_config.AiInsights.IncidentAssociationShadowIntervalSeconds};lookback={_config.AiInsights.IncidentAssociationShadowLookbackMinutes};candidates={_config.AiInsights.IncidentAssociationShadowCandidateLimit}";

    private static long? SourceCallId(IncidentAssociationLedgerEntry? entry) =>
        entry?.Bundle.Observations.FirstOrDefault(item => item.ObservationId == entry.NewObservationId)?.CallId;

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

public sealed record IncidentAssociationLiveSelection(
    IncidentEventStateObservationBundle Bundle,
    string NewObservationId,
    IReadOnlyList<IncidentAssociationCandidate> Candidates)
{
    public static bool IsEligibleSourceObservation(EngineCall call) =>
        !string.IsNullOrWhiteSpace(call.SystemShortName) &&
        !string.IsNullOrWhiteSpace(call.Transcription) &&
        string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase);

    public static IncidentAssociationLiveSelection Build(
        EngineCall newCall,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyList<VectorSearchMatchDto> matches,
        IncidentAssociationProjection? priorProjection,
        int candidateLimit,
        DateTimeOffset createdAtUtc)
    {
        var newObservationId = ObservationId(newCall.Id);
        var callsByObservation = recentCalls.ToDictionary(call => ObservationId(call.Id), StringComparer.Ordinal);
        var scores = matches
            .Where(match => match.CallId != newCall.Id)
            .GroupBy(match => ObservationId(match.CallId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Score), StringComparer.Ordinal);
        var groups = (priorProjection?.Events ?? [])
            .Select(projectedEvent => new
            {
                Event = projectedEvent,
                SourceCalls = projectedEvent.ObservationIds
                    .Where(callsByObservation.ContainsKey)
                    .Select(observationId => new
                    {
                        ObservationId = observationId,
                        Call = callsByObservation[observationId],
                        Score = scores.GetValueOrDefault(observationId, double.NegativeInfinity)
                    })
                    .Where(item => string.Equals(item.Call.SystemShortName, newCall.SystemShortName, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            })
            .Where(item => item.SourceCalls.Count > 0)
            .OrderByDescending(item => item.SourceCalls.Any(source => double.IsFinite(source.Score)))
            .ThenByDescending(item => item.SourceCalls.Max(source => source.Score))
            .ThenByDescending(item => item.SourceCalls.Max(source => source.Call.StartTime))
            .ThenBy(item => item.Event.ProjectionEventId, StringComparer.Ordinal)
            .Take(Math.Clamp(candidateLimit, 1, IncidentAssociationContract.MaximumCandidateCount))
            .ToList();

        var sourceCalls = new List<EngineCall> { newCall };
        var candidates = new List<IncidentAssociationCandidate>();
        for (var index = 0; index < groups.Count; index++)
        {
            var selected = groups[index].SourceCalls
                .OrderByDescending(item => double.IsFinite(item.Score))
                .ThenByDescending(item => item.Score)
                .ThenByDescending(item => item.Call.StartTime)
                .Take(3)
                .ToList();
            sourceCalls.AddRange(selected.Select(item => item.Call));
            candidates.Add(new IncidentAssociationCandidate(
                $"candidate-{index + 1}",
                groups[index].Event.ProjectionEventId,
                selected.Select(item => item.ObservationId).Distinct(StringComparer.Ordinal).ToList()));
        }
        var raw = IncidentEventStateCorpusExporter.BuildObservationBundle(
            $"association-live:bundle:call:{newCall.Id.ToString(CultureInfo.InvariantCulture)}",
            createdAtUtc,
            sourceCalls.DistinctBy(call => call.Id));
        var bundle = raw with
        {
            Observations = raw.Observations.Select(observation => observation with
            {
                AudioReference = string.Empty,
                Metadata = new Dictionary<string, IncidentEventStateMetadataObservation>(StringComparer.Ordinal)
            }).ToList()
        };
        return new IncidentAssociationLiveSelection(bundle, newObservationId, candidates);
    }

    private static string ObservationId(long callId) => $"call:{callId.ToString(CultureInfo.InvariantCulture)}";
}

public sealed class OpenAiIncidentAssociationProposer : IIncidentAssociationProposer
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger _logger;
    private readonly string _runId;

    public OpenAiIncidentAssociationProposer(EngineConfig config, EngineDatabase database, ILogger logger, string runId)
    {
        _config = config;
        _database = database;
        _logger = logger;
        _runId = runId;
    }

    public async Task<IncidentAssociationProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentAssociationCandidate> candidates,
        CancellationToken ct)
    {
        var prompt = IncidentAssociationPrompt.Build(bundle, newObservationId, candidates);
        var model = _config.AiInsights.OpenAiModel;
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 2200,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        if (!string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.AiInsights.OpenAiApiKey);
        var endpoint = $"{_config.AiInsights.OpenAiBaseUrl.TrimEnd('/')}/chat/completions";
        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        var responseText = string.Empty;
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, ct);
            responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Association proposer returned HTTP {(int)response.StatusCode}: {Trim(responseText, 500)}");
            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.TryGetProperty("model", out var modelElement) ? modelElement.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"Association proposer model identity mismatch: requested '{model}', received '{responseModel}'.");
            var json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Association proposer response content was empty.");
            var parsed = JsonSerializer.Deserialize<AssociationResponse>(json, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException("Association proposer JSON was empty.");
            var relationships = parsed.Relationships.Select(item => new IncidentAssociationRelationship(
                item.CandidateToken,
                item.Disposition switch
                {
                    "confirmed_membership" => IncidentAssociationDisposition.ConfirmedMembership,
                    "provisional_association" => IncidentAssociationDisposition.ProvisionalAssociation,
                    _ => throw new InvalidDataException($"Unsupported association disposition '{item.Disposition}'.")
                },
                item.RelationshipStatement,
                item.Uncertainty,
                item.NewObservationEvidence.Select(citation => IncidentEventStateLinkEvidence.MaterializeCitation(bundle, citation.TranscriptId)).ToList(),
                item.CandidateEvidence.Select(citation => IncidentEventStateLinkEvidence.MaterializeCitation(bundle, citation.TranscriptId)).ToList(),
                item.AlternativeInterpretations,
                item.UnresolvedQuestions)).ToList();
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, ct);
            return new IncidentAssociationProposal(
                $"model:association:{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                responseModel,
                IncidentAssociationPrompt.PromptIdentity,
                relationships);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, false, ex.GetBaseException().Message, CancellationToken.None);
            throw;
        }
    }

    private async Task RecordUsageAsync(string responseText, string endpoint, string requestedModel, int payloadChars, bool success, string error, CancellationToken ct)
    {
        var usage = ReadUsage(responseText);
        try
        {
            await _database.AddLmUsageAsync(new TokenUsageEntryDto(
                0, DateTime.UtcNow, $"incident association shadow:{_runId}", "chat.completions", success, error,
                endpoint, requestedModel, usage.ResponseModel, usage.FinishReason, payloadChars, payloadChars,
                usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not record incident association constructor shadow model usage");
        }
    }

    private static (int PromptTokens, int CompletionTokens, int TotalTokens, string ResponseModel, string FinishReason) ReadUsage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (0, 0, 0, string.Empty, string.Empty);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
            var prompt = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
            var completion = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
            var total = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0;
            var responseModel = root.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var finishReason = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("finish_reason", out var f) ? f.GetString() ?? string.Empty : string.Empty;
            return (prompt, completion, total, responseModel, finishReason);
        }
        catch { return (0, 0, 0, string.Empty, string.Empty); }
    }

    private static string Trim(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private sealed record AssociationResponse(
        [property: JsonPropertyName("relationships")] IReadOnlyList<AssociationRelationshipResponse> Relationships);

    private sealed record AssociationRelationshipResponse(
        [property: JsonPropertyName("candidate_token")] string CandidateToken,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("relationship_statement")] string RelationshipStatement,
        [property: JsonPropertyName("uncertainty")] double Uncertainty,
        [property: JsonPropertyName("new_observation_evidence")] IReadOnlyList<AssociationCitationResponse> NewObservationEvidence,
        [property: JsonPropertyName("candidate_evidence")] IReadOnlyList<AssociationCitationResponse> CandidateEvidence,
        [property: JsonPropertyName("alternative_interpretations")] IReadOnlyList<string> AlternativeInterpretations,
        [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);

    private sealed record AssociationCitationResponse([property: JsonPropertyName("transcript_id")] string TranscriptId);
}
