using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public sealed record IncidentBatchPromptPayload(string SystemPrompt, string UserPrompt, object ResponseFormat);

public static class IncidentBatchPrompt
{
    public const string PromptIdentity = "incident-batch-constructor-v13";
    public const int MaximumReturnedEvents = 6;

    public static IncidentBatchPromptPayload Build(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates)
    {
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var newObservations = newObservationIds.Select(id => PromptObservation(observations[id])).ToList();
        object source = candidates.Count == 0
            ? new { new_observations = newObservations }
            : new
            {
                new_observations = newObservations,
                candidate_events = candidates.Select(candidate => new
                {
                    candidate_token = candidate.CandidateToken,
                    source_observations = candidate.ObservationIds.Select(id => PromptObservation(observations[id])).ToList()
                }).ToList()
            };
        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Construct concrete operator-relevant real-world events from the new radio observations using only the supplied transcripts.");
        user.AppendLine("A radio transmission is not automatically an event. Return an event only when its cited words establish a concrete unfolding situation that merits operator awareness or follow-up beyond the exchange itself.");
        user.AppendLine("Operator relevance must come from the underlying real-world condition affecting people, property, or public safety. The mechanics of communicating, documenting, identifying, or coordinating workflow are not themselves an operator-relevant event when the underlying situation is absent or unknown.");
        user.AppendLine("Do not promote an observation merely because it mentions a person, vehicle, place, identifier, action, or unit activity.");
        user.AppendLine("Omit routine, unclear, unsupported, low-information, or non-event observations. Omitted observations remain unresolved and are not classified as non-incidents.");
        user.AppendLine("Returning an empty events array is correct when none of the supplied observations clears that bar.");
        user.AppendLine($"Return at most {MaximumReturnedEvents} events. Choose the strongest source-grounded events and leave lower-priority observations unresolved rather than producing an oversized response.");
        user.AppendLine("An event may contain one or several new observations. Each new observation may appear in at most one returned event.");
        if (candidates.Count > 0)
            user.AppendLine("When new observations relate to a candidate event, return either confirmed_membership or provisional_association for those observations; do not also return them in a new_event or any second proposal.");
        user.AppendLine("Use disposition new_event only when at least two separately cited new observations mutually corroborate the same real-world event without relying on a candidate event. A candidate-free new_event remains in Review; it does not become operator-visible until a later confirmed_membership transition or operator action.");
        user.AppendLine("Use provisional_event for a source-grounded candidate-free possible situation that lacks two-observation corroboration or otherwise has meaningful uncertainty. A provisional event is review evidence, not an operator-visible incident. A single-observation event must always be provisional_event.");
        user.AppendLine("No candidate-free proposal becomes operator-visible directly, regardless of how many observations it groups. Automatic visibility requires a separately evaluated confirmed_membership against prior Review state.");
        if (candidates.Count > 0)
        {
            user.AppendLine("Use confirmed_membership only when cited evidence on both sides directly supports one unfolding real-world event.");
            user.AppendLine("Use provisional_association when cited evidence makes a relationship plausible and operator-relevant but meaningful uncertainty remains. Provisional associations never merge membership.");
            user.AppendLine("A provisional association does not require the two sides to be the same event. Use it when new evidence explicitly refers back to, recurs after, follows up on, or may be confused with a supplied candidate, but should remain a distinct event. Cite both sides; the association is review context and does not merge their observations.");
            user.AppendLine("Do not return a candidate-free event when its operator_basis, alternatives, or unresolved questions rely on a supplied candidate to explain a prior clearance, recurrence, follow-up, relationship, or distinction.");
        }
        else
        {
            user.AppendLine("This source-isolated construction stage contains no prior event state. Do not infer, describe, confirm, reject, or cite a relationship to an earlier event. A separate stage evaluates relationships after construction.");
        }
        user.AppendLine("Do not create or rely on event classes, categories, roles, talkgroup rules, radio-system meaning, retrieval rank, or timing as proof.");
        user.AppendLine("Review every new observation before choosing events; finding one event is not a reason to stop evaluating the remaining observations.");
        user.AppendLine("Every returned event must cite a transcript in every included new observation. Put each separate supporting span in exact_quotes; every item must be one short contiguous verbatim substring. When evidence is separated in a transcript, return several exact_quotes items. Never insert ellipses, omit intervening words inside an item, normalize wording, or join separated spans.");
        user.AppendLine("Every new_observation_evidence citation must come from an observation listed in that same event's new_observation_ids. If evidence from another observation is necessary, either include that observation as a member of the same event or omit the event when membership is unresolved.");
        if (candidates.Count > 0)
            user.AppendLine("Confirmed and provisional relationships must also cite exact source spans from candidate-event transcripts.");
        user.AppendLine(candidates.Count > 0
            ? "For operator_basis, explain what the cited words establish and why the situation merits operator awareness or follow-up. For confirmed or provisional association, also explain how the two cited sides relate."
            : "For operator_basis, explain what the cited words establish and why the situation merits operator awareness or follow-up.");
        user.AppendLine("For each event, use only that event's own exact_quotes to write its title, summary, and operator_basis. Never borrow facts from omitted observations, candidate evidence not cited for that event, or a sibling event. Preserve alternatives, unresolved questions, and uncertainty instead of forcing certainty.");
        user.AppendLine("Compare every pair of drafted events before returning them. If two drafts may describe the same unfolding situation and the cited evidence cannot reliably separate them, combine their observations and evidence into one provisional_event for review instead of returning parallel new_event drafts.");
        user.AppendLine("Before returning JSON, silently reconsider every proposed event. Omit it if operator_basis cannot be supported directly by its exact quotes without inference from radio metadata or generic workflow.");
        user.AppendLine("Remove every discarded draft from the events array entirely. Never return an event that you decided should be omitted, and never return an event without exact evidence for every included new observation.");
        user.AppendLine("Copy every identifier exactly.");
        user.AppendLine();
        user.AppendLine("Source bundle:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));
        return new IncidentBatchPromptPayload(
            candidates.Count == 0
                ? "You construct immutable source-grounded incident groups from new observations only. Application code validates identity, ownership, and citations. A separate source-cited stage evaluates relationships to prior state."
                : "You construct source-grounded incident events. Application code validates identity, ownership, and citations but does not interpret semantic meaning. Unsupported observations remain unresolved. Provisional associations never change event membership.",
            user.ToString(),
            ResponseFormat(bundle, newObservationIds, candidates));
    }

    private static object ResponseFormat(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates)
    {
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var newTranscriptIds = newObservationIds.SelectMany(id => observations[id].Transcripts).Select(item => item.TranscriptId).Distinct(StringComparer.Ordinal).ToArray();
        var candidateTranscriptIds = candidates.SelectMany(candidate => candidate.ObservationIds).SelectMany(id => observations[id].Transcripts).Select(item => item.TranscriptId).Distinct(StringComparer.Ordinal).ToArray();
        var candidateTokens = new[] { string.Empty }.Concat(candidates.Select(item => item.CandidateToken)).ToArray();
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_batch_constructor_v1",
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
                            maxItems = MaximumReturnedEvents,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    proposal_token = new { type = "string" },
                                    disposition = new
                                    {
                                        type = "string",
                                        @enum = candidates.Count == 0
                                            ? new[] { "new_event", "provisional_event" }
                                            : new[] { "new_event", "provisional_event", "confirmed_membership", "provisional_association" }
                                    },
                                    candidate_token = new { type = "string", @enum = candidateTokens },
                                    new_observation_ids = StringArraySchema(newObservationIds),
                                    title = new { type = "string" },
                                    summary = new { type = "string" },
                                    operator_basis = new { type = "string" },
                                    uncertainty = new { type = "number", minimum = 0, maximum = 1 },
                                    new_observation_evidence = CitationArraySchema(newTranscriptIds),
                                    candidate_evidence = CitationArraySchema(candidateTranscriptIds),
                                    alternative_interpretations = OpenStringArraySchema(),
                                    unresolved_questions = OpenStringArraySchema()
                                },
                                required = new[]
                                {
                                    "proposal_token", "disposition", "candidate_token", "new_observation_ids", "title", "summary",
                                    "operator_basis", "uncertainty", "new_observation_evidence", "candidate_evidence",
                                    "alternative_interpretations", "unresolved_questions"
                                }
                            }
                        }
                    },
                    required = new[] { "events" }
                }
            }
        };
    }

    private static object PromptObservation(IncidentEventStateSourceObservation observation) => new
    {
        observation_id = observation.ObservationId,
        observed_at_unix_seconds = observation.ObservedAtUnixSeconds,
        transcripts = observation.Transcripts.Select(transcript => new { transcript_id = transcript.TranscriptId, text = transcript.Text, producer = transcript.Producer }).ToList()
    };

    private static object CitationArraySchema(IEnumerable<string> transcriptIds) => new
    {
        type = "array",
        items = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                transcript_id = new { type = "string", @enum = transcriptIds.ToArray() },
                exact_quotes = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = 4,
                    items = new { type = "string" }
                }
            },
            required = new[] { "transcript_id", "exact_quotes" }
        }
    };

    private static object StringArraySchema(IEnumerable<string> values) => new
    {
        type = "array",
        items = new { type = "string", @enum = values.ToArray() }
    };

    private static object OpenStringArraySchema() => new { type = "array", items = new { type = "string" } };
}

public sealed class OpenAiIncidentBatchProposer : IIncidentBatchProposer
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger _logger;
    private readonly string _runId;

    public OpenAiIncidentBatchProposer(EngineConfig config, EngineDatabase database, ILogger logger, string runId)
    {
        _config = config;
        _database = database;
        _logger = logger;
        _runId = runId;
    }

    public async Task<IncidentBatchProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        CancellationToken ct)
    {
        var prompt = IncidentBatchPrompt.Build(bundle, newObservationIds, candidates);
        var model = _config.AiInsights.OpenAiModel;
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 4000,
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
                throw new InvalidOperationException($"Batch constructor returned HTTP {(int)response.StatusCode}: {Trim(responseText, 500)}");
            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.TryGetProperty("model", out var modelElement) ? modelElement.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"Batch constructor model identity mismatch: requested '{model}', received '{responseModel}'.");
            var json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Batch constructor response content was empty.");
            var parsed = JsonSerializer.Deserialize<BatchResponse>(json, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException("Batch constructor JSON was empty.");
            var transcripts = bundle.Observations
                .SelectMany(observation => observation.Transcripts)
                .ToDictionary(transcript => transcript.TranscriptId, transcript => transcript.Text, StringComparer.Ordinal);
            var events = parsed.Events.Select(item => new IncidentBatchEventProposal(
                item.ProposalToken,
                item.Disposition switch
                {
                    "new_event" => IncidentBatchEventDisposition.NewEvent,
                    "provisional_event" => IncidentBatchEventDisposition.ProvisionalEvent,
                    "confirmed_membership" => IncidentBatchEventDisposition.ConfirmedMembership,
                    "provisional_association" => IncidentBatchEventDisposition.ProvisionalAssociation,
                    _ => throw new InvalidDataException($"Unsupported batch event disposition '{item.Disposition}'.")
                },
                item.CandidateToken,
                item.NewObservationIds,
                item.Title,
                item.Summary,
                item.OperatorBasis,
                item.Uncertainty,
                ResolveCitations(item.NewObservationEvidence, transcripts),
                ResolveCitations(item.CandidateEvidence, transcripts),
                item.AlternativeInterpretations,
                item.UnresolvedQuestions)).ToList();
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, ct);
            return new IncidentBatchProposal($"model:incident-batch:{Guid.NewGuid():N}", DateTimeOffset.UtcNow, responseModel, IncidentBatchPrompt.PromptIdentity, events);
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
                0, DateTime.UtcNow, $"incident batch constructor shadow:{_runId}", "chat.completions", success, error,
                endpoint, requestedModel, usage.ResponseModel, usage.FinishReason, payloadChars, payloadChars,
                usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not record incident batch constructor model usage");
        }
    }

    private static (int PromptTokens, int CompletionTokens, int TotalTokens, string ResponseModel, string FinishReason) ReadUsage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0, 0, string.Empty, string.Empty);
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

    private static IReadOnlyList<IncidentEventStateTranscriptCitation> ResolveCitations(
        IEnumerable<BatchCitationResponse> citations,
        IReadOnlyDictionary<string, string> transcripts) =>
        citations.SelectMany(citation => citation.ExactQuotes.SelectMany(quote =>
        {
            var quotes = transcripts.TryGetValue(citation.TranscriptId, out var transcript)
                ? IncidentTranscriptCitationResolver.ResolveSegments(transcript, quote)
                : [quote];
            return quotes.Select(resolved => new IncidentEventStateTranscriptCitation(citation.TranscriptId, resolved));
        })).ToList();

    private sealed record BatchResponse([property: JsonPropertyName("events")] IReadOnlyList<BatchEventResponse> Events);
    private sealed record BatchEventResponse(
        [property: JsonPropertyName("proposal_token")] string ProposalToken,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("candidate_token")] string CandidateToken,
        [property: JsonPropertyName("new_observation_ids")] IReadOnlyList<string> NewObservationIds,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("operator_basis")] string OperatorBasis,
        [property: JsonPropertyName("uncertainty")] double Uncertainty,
        [property: JsonPropertyName("new_observation_evidence")] IReadOnlyList<BatchCitationResponse> NewObservationEvidence,
        [property: JsonPropertyName("candidate_evidence")] IReadOnlyList<BatchCitationResponse> CandidateEvidence,
        [property: JsonPropertyName("alternative_interpretations")] IReadOnlyList<string> AlternativeInterpretations,
        [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);
    private sealed record BatchCitationResponse(
        [property: JsonPropertyName("transcript_id")] string TranscriptId,
        [property: JsonPropertyName("exact_quotes")] IReadOnlyList<string> ExactQuotes);
}
