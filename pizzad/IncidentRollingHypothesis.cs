using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public sealed record IncidentRollingPromptPayload(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat,
    IReadOnlyDictionary<string, string> ObservationIdsByEvidenceRecord,
    IReadOnlyList<string> AmbiguousObservationIds);

public sealed record IncidentRollingHypothesisDraft(
    string Title,
    string Summary,
    string Location,
    IReadOnlyList<string> Members,
    [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentRollingHypothesisProposal(
    IReadOnlyList<IncidentRollingHypothesisDraft> Events);

public sealed record IncidentRollingResolvedEvent(
    string Title,
    string Summary,
    string Location,
    IReadOnlyList<string> ObservationIds,
    IReadOnlyList<string> EvidenceRecords,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentRollingHypothesisResolution(
    IReadOnlyList<IncidentRollingResolvedEvent> Events,
    IReadOnlyList<string> PendingObservationIds,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class IncidentRollingHypothesis
{
    public const string PromptIdentity = "incident-rolling-hypothesis-v4-grounded-membership";
    public const string ConfigurationToken = "membership=rolling-evidence-record-v5";
    public const string ExecutionToken = "execution=rolling-single-pass-v2";
    public const int MaximumObservationCount = 24;
    public const int MaximumReturnedEvents = 20;
    public const int MaximumTitleLength = 80;
    public const int MaximumSummaryLength = 320;
    public const int MaximumLocationLength = 160;
    // A returned event is an affirmative membership decision. Ambiguous
    // evidence is omitted and remains pending instead of being returned as a
    // quasi-event that a later application gate silently discards.
    public const int MaximumUnresolvedQuestions = 0;

    public static IncidentRollingPromptPayload BuildPrompt(
        IReadOnlyList<IncidentEventStateSourceObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count is < 1 or > MaximumObservationCount)
            throw new ArgumentOutOfRangeException(nameof(observations), $"A rolling window must contain 1 to {MaximumObservationCount} observations.");

        var records = observations
            .Select(observation => new
            {
                observation.ObservationId,
                Record = BuildEvidenceRecord(observation)
            })
            .ToList();
        var collisions = records
            .GroupBy(item => item.Record, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item => item.ObservationId))
            .ToHashSet(StringComparer.Ordinal);
        var usable = records
            .Where(item => !collisions.Contains(item.ObservationId))
            .ToDictionary(item => item.Record, item => item.ObservationId, StringComparer.Ordinal);

        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Construct coherent operator-relevant incidents from the supplied evidence records.");
        user.AppendLine("An incident is one real-world event. A radio transmission is evidence and is not automatically an incident.");
        user.AppendLine("Publish only when the selected evidence establishes an operator-relevant event or operational response. Related lookups, record checks, status exchanges, and administrative communications are not an incident merely because they concern similar activity.");
        user.AppendLine("Group every evidence record that clearly concerns the same event. Keep unrelated events separate.");
        user.AppendLine("A legitimate incident may have one member when that record alone clearly describes a self-contained real-world event.");
        user.AppendLine("A concrete dispatch or response may be a legitimate one-member incident even when contact, outcome, or corroborating radio traffic has not arrived yet. Do not omit it solely because it has one source.");
        user.AppendLine("Do not create a one-member incident merely because a record does not fit another group.");
        user.AppendLine("Omit unclear, routine, administrative, or still-ambiguous evidence. Omitted evidence remains pending for a later window; omission does not reject it.");
        user.AppendLine("For every event you return, event existence and membership are decided; unresolved_questions must be an empty array. If either remains uncertain, omit that event. Missing contact, outcome, disposition, or other follow-up detail is not by itself membership uncertainty: use a neutral evidence-grounded description and return the event when the dispatch, report, or operational response is clear.");
        user.AppendLine("For members, select the complete supplied evidence_record strings verbatim. Do not return identifiers, row numbers, positions, indices, hashes, or invented references.");
        user.AppendLine("Use only facts supported by the selected members for title, summary, and location. Leave location empty when the evidence does not establish it.");
        user.AppendLine("You may paraphrase the ordinary event description, but never silently repair or expand garbled ASR. Do not infer a proper name, location, agency, medication, diagnosis, condition, relationship, or status that is not clearly stated in the evidence text. When a name, place, or event type is unclear, use a neutral description or omit it.");
        user.AppendLine("Prefer a coherent multi-call incident over transmission-sized fragments when the selected evidence clearly describes one event.");
        user.AppendLine("Do not combine records based only on similar category, wording, agency, talkgroup, radio system, timing, or retrieval proximity.");
        user.AppendLine();
        user.AppendLine("Evidence records:");
        foreach (var record in usable.Keys)
        {
            user.AppendLine("--- evidence_record ---");
            user.AppendLine(record);
        }

        var evidenceRecords = usable.Keys.ToArray();
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_rolling_hypothesis_v4",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        events = new
                        {
                            type = "array",
                            minItems = 0,
                            maxItems = evidenceRecords.Length == 0 ? 0 : MaximumReturnedEvents,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    title = new { type = "string", maxLength = MaximumTitleLength },
                                    summary = new { type = "string", maxLength = MaximumSummaryLength },
                                    location = new { type = "string", maxLength = MaximumLocationLength },
                                    members = new
                                    {
                                        type = "array",
                                        minItems = 1,
                                        uniqueItems = true,
                                        items = evidenceRecords.Length == 0
                                            ? (object)new { type = "string" }
                                            : new { type = "string", @enum = evidenceRecords }
                                    },
                                    unresolved_questions = new
                                    {
                                        type = "array",
                                        maxItems = MaximumUnresolvedQuestions,
                                        items = new { type = "string" }
                                    }
                                },
                                required = new[] { "title", "summary", "location", "members", "unresolved_questions" }
                            }
                        }
                    },
                    required = new[] { "events" }
                }
            }
        };

        return new IncidentRollingPromptPayload(
            "You construct complete incident hypotheses from bounded radio evidence. The application validates exact evidence ownership; omitted evidence remains pending.",
            user.ToString(),
            responseFormat,
            usable,
            collisions.Order(StringComparer.Ordinal).ToList());
    }

    public static IncidentRollingHypothesisResolution Resolve(
        IncidentRollingPromptPayload prompt,
        IncidentRollingHypothesisProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(proposal);
        var errors = new List<string>();
        if (proposal.Events.Count > MaximumReturnedEvents)
            errors.Add($"proposal contains more than {MaximumReturnedEvents} events");
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        var events = new List<IncidentRollingResolvedEvent>();

        for (var eventIndex = 0; eventIndex < proposal.Events.Count; eventIndex++)
        {
            var draft = proposal.Events[eventIndex];
            if (string.IsNullOrWhiteSpace(draft.Title))
                errors.Add($"event {eventIndex + 1} has no title");
            else if (draft.Title.Length > MaximumTitleLength)
                errors.Add($"event {eventIndex + 1} title is too long");
            if (string.IsNullOrWhiteSpace(draft.Summary))
                errors.Add($"event {eventIndex + 1} has no summary");
            else if (draft.Summary.Length > MaximumSummaryLength)
                errors.Add($"event {eventIndex + 1} summary is too long");
            if (draft.Location.Length > MaximumLocationLength)
                errors.Add($"event {eventIndex + 1} location is too long");
            if (draft.UnresolvedQuestions.Count > MaximumUnresolvedQuestions)
                errors.Add($"event {eventIndex + 1} has too many unresolved questions");
            if (draft.Members.Count == 0)
                errors.Add($"event {eventIndex + 1} has no members");
            if (draft.Members.Count != draft.Members.Distinct(StringComparer.Ordinal).Count())
                errors.Add($"event {eventIndex + 1} repeats an evidence record");

            var observationIds = new List<string>();
            foreach (var member in draft.Members)
            {
                if (!prompt.ObservationIdsByEvidenceRecord.TryGetValue(member, out var observationId))
                {
                    errors.Add($"event {eventIndex + 1} contains an unknown or ambiguous evidence record");
                    continue;
                }
                if (owners.TryGetValue(observationId, out var priorEventIndex))
                    errors.Add($"observation '{observationId}' belongs to events {priorEventIndex + 1} and {eventIndex + 1}");
                else
                    owners[observationId] = eventIndex;
                observationIds.Add(observationId);
            }
            events.Add(new IncidentRollingResolvedEvent(
                draft.Title.Trim(),
                draft.Summary.Trim(),
                draft.Location.Trim(),
                observationIds.Distinct(StringComparer.Ordinal).ToList(),
                draft.Members.ToList(),
                draft.UnresolvedQuestions.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).Distinct(StringComparer.Ordinal).ToList()));
        }

        var pending = prompt.ObservationIdsByEvidenceRecord.Values
            .Concat(prompt.AmbiguousObservationIds)
            .Where(observationId => !owners.ContainsKey(observationId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new IncidentRollingHypothesisResolution(events, pending, errors);
    }

    public static IncidentRollingHypothesisProposal ParseProposal(string json) =>
        JsonSerializer.Deserialize<IncidentRollingHypothesisProposal>(json, EngineConfig.JsonOptions())
        ?? throw new InvalidDataException("Rolling incident hypothesis JSON was empty.");

    public static string BuildEvidenceRecord(IncidentEventStateSourceObservation observation)
    {
        var timestamp = DateTimeOffset.FromUnixTimeSeconds(observation.ObservedAtUnixSeconds)
            .UtcDateTime
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var transcripts = observation.Transcripts
            .Select(item => item.Text.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return $"observed_at: {timestamp}\ntranscript: {string.Join("\ntranscript: ", transcripts)}";
    }
}

public sealed record IncidentRollingModelResult(
    IncidentRollingHypothesisProposal Proposal,
    IncidentRollingHypothesisResolution Resolution,
    string ModelIdentity,
    long DurationMilliseconds,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

public sealed class OpenAiIncidentRollingHypothesisProposer
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger _logger;
    private readonly string _runId;

    public OpenAiIncidentRollingHypothesisProposer(
        EngineConfig config,
        EngineDatabase database,
        ILogger logger,
        string runId)
    {
        _config = config;
        _database = database;
        _logger = logger;
        _runId = runId;
    }

    public async Task<IncidentRollingModelResult> ProposeAsync(
        IReadOnlyList<IncidentEventStateSourceObservation> observations,
        CancellationToken ct)
    {
        var prompt = IncidentRollingHypothesis.BuildPrompt(observations);
        var model = _config.AiInsights.OpenAiModel;
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 6000,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        var endpoint = $"{_config.AiInsights.OpenAiBaseUrl.TrimEnd('/')}/chat/completions";
        var responseText = string.Empty;
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs))
            };
            if (!string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiApiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.AiInsights.OpenAiApiKey);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, ct);
            responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Rolling incident hypothesis returned HTTP {(int)response.StatusCode}: {Trim(responseText, 500)}");
            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"Rolling incident model identity mismatch: requested '{model}', received '{responseModel}'.");
            var json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Rolling incident response content was empty.");
            var proposal = IncidentRollingHypothesis.ParseProposal(json);
            var resolution = IncidentRollingHypothesis.Resolve(prompt, proposal);
            var usage = ReadUsage(envelope.RootElement);
            var duration = ElapsedMilliseconds(started);
            await RecordUsageAsync(endpoint, model, responseModel, payload.Length, true, string.Empty, usage, duration, ct);
            return new IncidentRollingModelResult(proposal, resolution, responseModel, duration, usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var duration = ElapsedMilliseconds(started);
            await RecordUsageAsync(endpoint, model, string.Empty, payload.Length, false, ex.GetBaseException().Message, default, duration, CancellationToken.None);
            throw;
        }
    }

    private async Task RecordUsageAsync(
        string endpoint,
        string requestedModel,
        string responseModel,
        int payloadChars,
        bool success,
        string error,
        (int PromptTokens, int CompletionTokens, int TotalTokens) usage,
        long durationMilliseconds,
        CancellationToken ct)
    {
        try
        {
            await _database.AddLmUsageAsync(new TokenUsageEntryDto(
                0,
                DateTime.UtcNow,
                $"incident rolling hypothesis shadow:{_runId}",
                "chat.completions",
                success,
                error,
                endpoint,
                requestedModel,
                responseModel,
                string.Empty,
                payloadChars,
                payloadChars,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                durationMilliseconds), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not record rolling incident hypothesis model usage");
        }
    }

    private static (int PromptTokens, int CompletionTokens, int TotalTokens) ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return default;
        return (
            usage.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : 0,
            usage.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : 0,
            usage.TryGetProperty("total_tokens", out var total) ? total.GetInt32() : 0);
    }

    private static string Trim(string value, int limit) => value.Length <= limit ? value : value[..limit];
    private static long ElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}

public sealed class IncidentRollingBatchProposerAdapter : IIncidentBatchProposer
{
    private readonly OpenAiIncidentRollingHypothesisProposer _proposer;

    public IncidentRollingBatchProposerAdapter(
        EngineConfig config,
        EngineDatabase database,
        ILogger logger,
        string runId)
    {
        _proposer = new OpenAiIncidentRollingHypothesisProposer(config, database, logger, runId);
    }

    public async Task<IncidentBatchProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        CancellationToken ct)
    {
        var result = await _proposer.ProposeAsync(bundle.Observations, ct);
        return BuildBatchProposal(bundle, newObservationIds, candidates, result);
    }

    public static IncidentBatchProposal BuildBatchProposal(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IncidentRollingModelResult result)
    {
        if (!result.Resolution.IsValid)
            throw new InvalidDataException($"Rolling incident hypothesis failed exact evidence validation: {string.Join("; ", result.Resolution.Errors)}");

        var newIds = newObservationIds.ToHashSet(StringComparer.Ordinal);
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var events = new List<IncidentBatchEventProposal>();
        foreach (var resolved in result.Resolution.Events)
        {
            var resolvedNewIds = resolved.ObservationIds.Where(newIds.Contains).ToList();
            if (resolvedNewIds.Count == 0)
                continue;
            var matchingCandidates = candidates
                .Where(candidate => candidate.ObservationIds.Intersect(resolved.ObservationIds, StringComparer.Ordinal).Any())
                .ToList();
            var matchingPublishedCandidates = matchingCandidates.Where(candidate => candidate.OperatorVisible).ToList();
            if (matchingPublishedCandidates.Count > 1)
                throw new InvalidDataException("A rolling incident hypothesis overlaps more than one existing incident; automatic reconciliation failed closed.");

            var candidate = matchingPublishedCandidates.SingleOrDefault();
            var newEvidence = BuildCitations(resolvedNewIds, observations);
            var candidateEvidence = candidate is null
                ? []
                : BuildCitations(
                    resolved.ObservationIds.Intersect(candidate.ObservationIds, StringComparer.Ordinal).ToList(),
                    observations);
            events.Add(new IncidentBatchEventProposal(
                $"rolling:{Guid.NewGuid():N}",
                candidate is null ? IncidentBatchEventDisposition.NewEvent : IncidentBatchEventDisposition.ConfirmedMembership,
                candidate?.CandidateToken ?? string.Empty,
                resolvedNewIds,
                resolved.Title,
                resolved.Summary,
                candidate is null
                    ? "The selected evidence records describe one proposed incident."
                    : "The selected new and existing evidence records describe one proposed incident.",
                // The legacy proposal contract requires a numeric field, but the
                // rolling path neither asks for nor gates on model confidence.
                0.5,
                newEvidence,
                candidateEvidence,
                [],
                resolved.UnresolvedQuestions));
        }

        return new IncidentBatchProposal(
            $"model:incident-rolling:{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            result.ModelIdentity,
            IncidentRollingHypothesis.PromptIdentity,
            events);
    }

    private static IReadOnlyList<IncidentEventStateTranscriptCitation> BuildCitations(
        IReadOnlyList<string> observationIds,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observations) =>
        observationIds
            .SelectMany(observationId => observations[observationId].Transcripts
                .Where(transcript => !string.IsNullOrWhiteSpace(transcript.Text))
                .Take(1)
                .Select(transcript => new IncidentEventStateTranscriptCitation(transcript.TranscriptId, transcript.Text.Trim())))
            .ToList();
}
