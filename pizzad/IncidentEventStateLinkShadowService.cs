using System.Net.Http.Headers;
using System.Reflection;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public sealed class IncidentEventStateLinkShadowService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EmbeddingService _embeddings;
    private readonly ILogger<IncidentEventStateLinkShadowService> _logger;
    private long? _lastSampledCallId;

    public IncidentEventStateLinkShadowService(
        EngineConfig config,
        EngineDatabase database,
        EmbeddingService embeddings,
        ILogger<IncidentEventStateLinkShadowService> logger)
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
                _logger.LogWarning(ex, "Link-only incident shadow sampling failed; production incident state was not changed");
            }

            if (!startupOffsetApplied)
            {
                startupOffsetApplied = true;
                await DelayAsync(
                    TimeSpan.FromSeconds(_config.AiInsights.IncidentEventLinkShadowIntervalSeconds / 2d),
                    stoppingToken);
                continue;
            }

            await DelayAsync(
                TimeSpan.FromSeconds(_config.AiInsights.IncidentEventLinkShadowIntervalSeconds),
                stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var runId = _config.AiInsights.IncidentEventLinkShadowRunId.Trim();
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-_config.AiInsights.IncidentEventLinkShadowLookbackMinutes)
            .ToUnixTimeSeconds();
        var calls = (await _database.ListCallsAsync(start, now.ToUnixTimeSeconds(), null, ct))
            .Where(IncidentEventStateLinkLiveSelection.IsEligibleSourceObservation)
            .OrderBy(call => call.Id)
            .ToList();

        if (_lastSampledCallId is null)
        {
            var latestEntry = await _database.GetLatestIncidentEventStateLinkShadowLedgerEntryAsync(runId, ct);
            _lastSampledCallId = SourceCallId(latestEntry?.Entry) ?? calls.LastOrDefault()?.Id ?? 0;
            _logger.LogInformation(
                "Link-only incident shadow run {RunId} initialized after call {CallId}; historical calls will not be backfilled",
                runId,
                _lastSampledCallId);
            return;
        }

        // Deliberately sample the newest eligible call and advance past intervening calls.
        // This bounds model use independently of radio traffic volume.
        var newCall = calls.LastOrDefault(call => call.Id > _lastSampledCallId.Value);
        if (newCall is null)
            return;

        var priorStored = await _database.GetLatestIncidentEventStateLinkShadowProjectionAsync(runId, ct);
        var priorProjection = priorStored?.Projection;
        var matches = priorProjection is null
            ? []
            : await _embeddings.SearchSimilarAsync(
                newCall.Transcription,
                newCall.SystemShortName,
                start,
                now.ToUnixTimeSeconds(),
                20,
                ct);
        var selection = IncidentEventStateLinkLiveSelection.Build(
            newCall,
            calls,
            matches,
            priorProjection,
            _config.AiInsights.IncidentEventLinkShadowCandidateLimit,
            now);

        var proposer = new OpenAiIncidentEventStateLinkProposer(_config, _database, _logger, runId);
        var coordinator = new IncidentEventStateLinkShadowCoordinator(proposer, _database);
        var callIdentity = newCall.Id.ToString(CultureInfo.InvariantCulture);
        var result = await coordinator.RunAsync(
            new IncidentEventStateLinkShadowRunRequest(
                runId,
                $"link-live:{runId}:ledger:call:{callIdentity}",
                $"link-live:{runId}:projection:call:{callIdentity}",
                $"link-live:{runId}:event:call:{callIdentity}",
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                ConfigurationIdentity()),
            selection.Bundle,
            priorProjection,
            selection.NewObservationId,
            selection.Candidates,
            ct);

        _lastSampledCallId = newCall.Id;
        _logger.LogInformation(
            "Link-only incident shadow run {RunId} sampled call {CallId}: {Outcome}, candidates={CandidateCount}, proposerMs={DurationMs}, proposerError={HasError}; production incident state unchanged",
            runId,
            newCall.Id,
            result.LedgerEntry.Entry.Transition.Outcome,
            selection.Candidates.Count,
            result.LedgerEntry.Entry.Execution.ProposerDurationMilliseconds,
            !string.IsNullOrWhiteSpace(result.LedgerEntry.Entry.Execution.ProposerError));
    }

    private string ConfigurationIdentity() =>
        $"incident-event-link-live-v2;run={_config.AiInsights.IncidentEventLinkShadowRunId.Trim()};interval={_config.AiInsights.IncidentEventLinkShadowIntervalSeconds};lookback={_config.AiInsights.IncidentEventLinkShadowLookbackMinutes};candidates={_config.AiInsights.IncidentEventLinkShadowCandidateLimit}";

    private bool IsEnabled() =>
        _config.Setup.Completed &&
        _config.AiInsights.Enabled &&
        _config.AiInsights.IncidentEventLinkShadowEnabled &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.IncidentEventLinkShadowRunId) &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel);

    private static long? SourceCallId(IncidentEventStateLinkLedgerEntry? entry) =>
        entry?.Bundle.Observations
            .FirstOrDefault(observation => string.Equals(observation.ObservationId, entry.NewObservationId, StringComparison.Ordinal))
            ?.CallId;

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}

public sealed record IncidentEventStateLinkLiveSelection(
    IncidentEventStateObservationBundle Bundle,
    string NewObservationId,
    IReadOnlyList<IncidentEventStateLinkCandidate> Candidates)
{
    public static bool IsEligibleSourceObservation(EngineCall call) =>
        !string.IsNullOrWhiteSpace(call.SystemShortName) &&
        !string.IsNullOrWhiteSpace(call.Transcription) &&
        string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase);

    public static IncidentEventStateLinkLiveSelection Build(
        EngineCall newCall,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyList<VectorSearchMatchDto> matches,
        IncidentEventStateLinkProjection? priorProjection,
        int candidateLimit,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(newCall);
        var newObservationId = ObservationId(newCall.Id);
        var callsById = recentCalls.ToDictionary(call => call.Id);
        var callsByObservation = callsById.Values.ToDictionary(call => ObservationId(call.Id), StringComparer.Ordinal);
        var scoresByObservation = matches
            .Where(match => match.CallId != newCall.Id)
            .GroupBy(match => ObservationId(match.CallId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(match => match.Score), StringComparer.Ordinal);
        var candidateGroups = (priorProjection?.Events ?? [])
            .Select(projectedEvent => new
            {
                Event = projectedEvent,
                SourceCalls = projectedEvent.ObservationIds
                    .Where(callsByObservation.ContainsKey)
                    .Select(observationId => new
                    {
                        ObservationId = observationId,
                        Call = callsByObservation[observationId],
                        Score = scoresByObservation.GetValueOrDefault(observationId, double.NegativeInfinity)
                    })
                    .Where(item => string.Equals(item.Call.SystemShortName, newCall.SystemShortName, StringComparison.OrdinalIgnoreCase))
                    .ToList()
            })
            .Where(item => item.SourceCalls.Count > 0)
            .OrderByDescending(item => item.SourceCalls.Any(source => double.IsFinite(source.Score)))
            .ThenByDescending(item => item.SourceCalls.Max(source => source.Score))
            .ThenByDescending(item => item.SourceCalls.Max(source => source.Call.StartTime))
            .ThenBy(item => item.Event.ProjectionEventId, StringComparer.Ordinal)
            .Take(Math.Clamp(candidateLimit, 1, IncidentEventStateLinkContractValidator.MaximumCandidateCount))
            .ToList();

        var sourceCalls = new List<EngineCall> { newCall };
        var candidates = new List<IncidentEventStateLinkCandidate>();
        for (var index = 0; index < candidateGroups.Count; index++)
        {
            var group = candidateGroups[index];
            var selected = group.SourceCalls
                .OrderByDescending(item => double.IsFinite(item.Score))
                .ThenByDescending(item => item.Score)
                .ThenByDescending(item => item.Call.StartTime)
                .Take(3)
                .ToList();
            sourceCalls.AddRange(selected.Select(item => item.Call));
            candidates.Add(new IncidentEventStateLinkCandidate(
                $"candidate-{index + 1}",
                group.Event.ProjectionEventId,
                selected.Select(item => item.ObservationId).Distinct(StringComparer.Ordinal).ToList()));
        }

        var rawBundle = IncidentEventStateCorpusExporter.BuildObservationBundle(
            $"link-live:bundle:call:{newCall.Id.ToString(CultureInfo.InvariantCulture)}",
            createdAtUtc,
            sourceCalls.DistinctBy(call => call.Id));
        var bundle = rawBundle with
        {
            // Semantic labels and radio metadata are intentionally excluded from the
            // model evidence surface. Retrieval is only a bounded candidate generator.
            Observations = rawBundle.Observations
                .Select(observation => observation with
                {
                    AudioReference = string.Empty,
                    Metadata = new Dictionary<string, IncidentEventStateMetadataObservation>(StringComparer.Ordinal)
                })
                .ToList()
        };
        return new IncidentEventStateLinkLiveSelection(bundle, newObservationId, candidates);
    }

    private static string ObservationId(long callId) =>
        $"call:{callId.ToString(CultureInfo.InvariantCulture)}";
}

public sealed class OpenAiIncidentEventStateLinkProposer : IIncidentEventStateLinkProposer
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger _logger;
    private readonly string _runId;

    public OpenAiIncidentEventStateLinkProposer(EngineConfig config, EngineDatabase database, ILogger logger, string runId)
    {
        _config = config;
        _database = database;
        _logger = logger;
        _runId = runId;
    }

    public async Task<IncidentEventStateLinkProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentEventStateLinkCandidate> candidates,
        CancellationToken ct)
    {
        var prompt = IncidentEventStateLinkPrompt.Build(bundle, newObservationId, candidates);
        var model = _config.AiInsights.OpenAiModel;
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 1200,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs))
        };
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
                throw new InvalidOperationException($"Link proposer returned HTTP {(int)response.StatusCode}: {Trim(responseText, 500)}");

            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"Link proposer model identity mismatch: requested '{model}', received '{responseModel}'.");
            var message = envelope.RootElement.GetProperty("choices")[0].GetProperty("message");
            var json = message.GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Link proposer response content was empty.");
            var parsed = JsonSerializer.Deserialize<LinkResponse>(json, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException("Link proposer JSON was empty.");
            var decision = parsed.Decision switch
            {
                "propose_link" => IncidentEventStateLinkDecision.ProposeLink,
                "abstain" => IncidentEventStateLinkDecision.Abstain,
                _ => throw new InvalidDataException($"Unsupported link proposer decision '{parsed.Decision}'.")
            };
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, ct);
            _logger.LogInformation("Link-only incident proposer returned {Decision} using verified model {Model}", decision, responseModel);
            return new IncidentEventStateLinkProposal(
                $"model:link:{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                responseModel,
                IncidentEventStateLinkPrompt.PromptIdentity,
                decision,
                parsed.CandidateToken,
                parsed.RelationshipStatement,
                parsed.Uncertainty,
                parsed.NewObservationEvidence.Select(citation => new IncidentEventStateTranscriptCitation(citation.TranscriptId, citation.ExactQuote)).ToList(),
                parsed.CandidateEvidence.Select(citation => new IncidentEventStateTranscriptCitation(citation.TranscriptId, citation.ExactQuote)).ToList(),
                parsed.UnresolvedQuestions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, false, ex.GetBaseException().Message, CancellationToken.None);
            throw;
        }
    }

    private static string Trim(string value, int limit) =>
        value.Length <= limit ? value : value[..limit];

    private async Task RecordUsageAsync(
        string responseText,
        string endpoint,
        string requestedModel,
        int payloadChars,
        bool success,
        string error,
        CancellationToken ct)
    {
        var usage = ReadUsage(responseText);
        try
        {
            await _database.AddLmUsageAsync(new TokenUsageEntryDto(
                0,
                DateTime.UtcNow,
                $"incident link shadow:{_runId}",
                "chat.completions",
                success,
                error,
                endpoint,
                requestedModel,
                usage.ResponseModel,
                usage.FinishReason,
                payloadChars,
                payloadChars,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not record link-only incident shadow model usage");
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
            var prompt = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var promptElement) ? promptElement.GetInt32() : 0;
            var completion = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var completionElement) ? completionElement.GetInt32() : 0;
            var total = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("total_tokens", out var totalElement) ? totalElement.GetInt32() : 0;
            var responseModel = root.TryGetProperty("model", out var modelElement) ? modelElement.GetString() ?? string.Empty : string.Empty;
            var finishReason = root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 && choices[0].TryGetProperty("finish_reason", out var finish)
                ? finish.GetString() ?? string.Empty
                : string.Empty;
            return (prompt, completion, total, responseModel, finishReason);
        }
        catch
        {
            return (0, 0, 0, string.Empty, string.Empty);
        }
    }

    private sealed record LinkResponse(
        [property: JsonPropertyName("decision")] string Decision,
        [property: JsonPropertyName("candidate_token")] string CandidateToken,
        [property: JsonPropertyName("relationship_statement")] string RelationshipStatement,
        [property: JsonPropertyName("uncertainty")] double Uncertainty,
        [property: JsonPropertyName("new_observation_evidence")] IReadOnlyList<LinkCitation> NewObservationEvidence,
        [property: JsonPropertyName("candidate_evidence")] IReadOnlyList<LinkCitation> CandidateEvidence,
        [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);

    private sealed record LinkCitation(
        [property: JsonPropertyName("transcript_id")] string TranscriptId,
        [property: JsonPropertyName("exact_quote")] string ExactQuote);
}
