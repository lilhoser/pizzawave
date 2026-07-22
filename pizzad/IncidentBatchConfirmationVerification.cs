using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public enum IncidentBatchConfirmationDecisionKind
{
    Reject,
    Verify
}

public sealed record IncidentBatchConfirmationDecision(
    string SourceProposalToken,
    string CandidateToken,
    IncidentBatchConfirmationDecisionKind Decision,
    string VerificationStatement,
    IReadOnlyList<IncidentEventStateTranscriptCitation> SourceEvidence,
    IReadOnlyList<IncidentEventStateTranscriptCitation> CandidateEvidence,
    IReadOnlyList<string> CounterEvidence,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentBatchConfirmationProposal(
    string ProposalId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentBatchConfirmationDecision> Decisions);

public sealed record IncidentBatchConfirmationExecutionContext(
    long VerifierDurationMilliseconds,
    string VerifierError);

public interface IIncidentBatchConfirmationVerifier
{
    Task<IncidentBatchConfirmationProposal> VerifyAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> confirmations,
        CancellationToken ct);
}

public static class IncidentBatchConfirmationContract
{
    public const string ConfigurationToken = "confirmation=independent-verifier-v1";

    public static IncidentEventStateContractValidationResult ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> confirmations,
        IncidentBatchConfirmationProposal proposal)
    {
        var errors = ValidateHeader(proposal).ToList();
        var expected = confirmations
            .Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership)
            .Select(RelationshipKey)
            .ToHashSet(StringComparer.Ordinal);
        var actual = proposal.Decisions.Select(DecisionKey).ToList();
        foreach (var duplicate in actual.GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key))
            errors.Add($"duplicate confirmation decision '{DisplayKey(duplicate)}'");
        foreach (var missing in expected.Except(actual, StringComparer.Ordinal))
            errors.Add($"confirmation verifier omitted '{DisplayKey(missing)}'");
        foreach (var unknown in actual.Except(expected, StringComparer.Ordinal))
            errors.Add($"confirmation verifier returned unknown pair '{DisplayKey(unknown)}'");
        foreach (var decision in proposal.Decisions)
            errors.AddRange(ValidateDecision(bundle, sources, candidates, expected, decision));
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IReadOnlySet<string> AcceptedVerifiedPairs(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> confirmations,
        IncidentBatchConfirmationProposal proposal)
    {
        if (ValidateHeader(proposal).Count > 0)
            return new HashSet<string>(StringComparer.Ordinal);
        var expected = confirmations
            .Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership)
            .Select(RelationshipKey)
            .ToHashSet(StringComparer.Ordinal);
        var duplicate = proposal.Decisions
            .GroupBy(DecisionKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return proposal.Decisions
            .Where(item => item.Decision == IncidentBatchConfirmationDecisionKind.Verify)
            .Where(item => expected.Contains(DecisionKey(item)))
            .Where(item => !duplicate.Contains(DecisionKey(item)))
            .Where(item => ValidateDecision(bundle, sources, candidates, expected, item).Count == 0)
            .Select(DecisionKey)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static string RelationshipKey(IncidentBatchRelationship relationship) =>
        PairKey(relationship.SourceProposalToken, relationship.CandidateToken);

    public static string DecisionKey(IncidentBatchConfirmationDecision decision) =>
        PairKey(decision.SourceProposalToken, decision.CandidateToken);

    public static bool UsesIndependentVerifier(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(ConfigurationToken, StringComparer.Ordinal);

    private static IReadOnlyList<string> ValidateHeader(IncidentBatchConfirmationProposal proposal)
    {
        var errors = new List<string>();
        RequireValue(proposal.ProposalId, "confirmation proposal id", errors);
        RequireValue(proposal.ModelIdentity, "confirmation model identity", errors);
        if (!string.Equals(proposal.PromptIdentity, IncidentBatchConfirmationPrompt.PromptIdentity, StringComparison.Ordinal))
            errors.Add("confirmation prompt identity does not match the verifier contract");
        if (proposal.GeneratedAtUtc == default)
            errors.Add("confirmation generated timestamp is required");
        return errors;
    }

    private static IReadOnlyList<string> ValidateDecision(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlySet<string> expected,
        IncidentBatchConfirmationDecision decision)
    {
        var errors = new List<string>();
        var key = DecisionKey(decision);
        RequireValue(decision.SourceProposalToken, "confirmation source proposal token", errors);
        RequireValue(decision.CandidateToken, "confirmation candidate token", errors);
        RequireValue(decision.VerificationStatement, $"confirmation statement for '{DisplayKey(key)}'", errors);
        if (!Enum.IsDefined(decision.Decision))
            errors.Add($"confirmation '{DisplayKey(key)}' has an invalid decision");
        if (!expected.Contains(key))
            return errors;
        var source = sources.Single(item => item.SourceProposalToken == decision.SourceProposalToken);
        var candidate = candidates.Single(item => item.CandidateToken == decision.CandidateToken);
        ValidateCitations(bundle, source.NewObservationIds, decision.SourceEvidence, "confirmation source evidence", errors);
        ValidateCitations(bundle, candidate.ObservationIds, decision.CandidateEvidence, "confirmation candidate evidence", errors);
        ValidateStrings(decision.CounterEvidence, "confirmation counterevidence", errors);
        ValidateStrings(decision.UnresolvedQuestions, "confirmation unresolved question", errors);
        if (decision.Decision == IncidentBatchConfirmationDecisionKind.Verify &&
            (decision.CounterEvidence.Count > 0 || decision.UnresolvedQuestions.Count > 0))
        {
            errors.Add($"verified confirmation '{DisplayKey(key)}' cannot retain counterevidence or unresolved questions");
        }
        return errors;
    }

    private static void ValidateCitations(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> allowedObservationIds,
        IReadOnlyList<IncidentEventStateTranscriptCitation> citations,
        string owner,
        List<string> errors)
    {
        if (citations.Count == 0)
            errors.Add($"{owner} must include at least one exact transcript citation");
        var transcripts = bundle.Observations
            .Where(item => allowedObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
            .SelectMany(item => item.Transcripts)
            .GroupBy(item => item.TranscriptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var citation in citations)
        {
            RequireValue(citation.TranscriptId, $"transcript id in {owner}", errors);
            RequireValue(citation.ExactQuote, $"exact quote in {owner}", errors);
            if (!seen.Add($"{citation.TranscriptId}\u001f{citation.ExactQuote}"))
                errors.Add($"duplicate transcript citation in {owner}");
            if (!transcripts.TryGetValue(citation.TranscriptId, out var matches) || matches.Count != 1)
                errors.Add($"{owner} cites a transcript outside its boundary");
            else if (!matches[0].Text.Contains(citation.ExactQuote, StringComparison.Ordinal))
                errors.Add($"{owner} quote does not occur exactly in transcript '{citation.TranscriptId}'");
        }
    }

    private static void ValidateStrings(IReadOnlyList<string> values, string owner, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            RequireValue(value, owner, errors);
            if (!seen.Add(value ?? string.Empty))
                errors.Add($"duplicate {owner}");
        }
    }

    private static string PairKey(string source, string candidate) => $"{source}\u001f{candidate}";
    private static string DisplayKey(string key) => key.Replace('\u001f', '→');
    private static void RequireValue(string? value, string owner, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{owner} is required");
    }
}

public sealed record IncidentBatchConfirmationPromptPayload(string SystemPrompt, string UserPrompt, object ResponseFormat);

public static class IncidentBatchConfirmationPrompt
{
    public const string PromptIdentity = "incident-batch-confirmation-verifier-v1";

    public static IncidentBatchConfirmationPromptPayload Build(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> confirmations)
    {
        if (confirmations.Count == 0 || confirmations.Any(item => item.Disposition != IncidentBatchRelationshipDisposition.ConfirmedMembership))
            throw new ArgumentException("confirmation verification requires at least one confirmed relationship", nameof(confirmations));
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var sourceMap = sources.ToDictionary(item => item.SourceProposalToken, StringComparer.Ordinal);
        var candidateMap = candidates.ToDictionary(item => item.CandidateToken, StringComparer.Ordinal);
        object PromptObservation(string id) => new
        {
            observation_id = id,
            observed_at_unix_seconds = observations[id].ObservedAtUnixSeconds,
            transcripts = observations[id].Transcripts.Select(item => new { transcript_id = item.TranscriptId, text = item.Text }).ToList()
        };
        var pairs = confirmations.Select(item => new
        {
            source_proposal_token = item.SourceProposalToken,
            candidate_token = item.CandidateToken,
            untrusted_proposer_statement = item.RelationshipStatement,
            source_observations = sourceMap[item.SourceProposalToken].NewObservationIds.Select(PromptObservation).ToList(),
            candidate_observations = candidateMap[item.CandidateToken].ObservationIds.Select(PromptObservation).ToList()
        }).ToList();
        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Independently verify every proposed confirmation. The earlier proposer is untrusted and may have invented a match or ignored contradictions.");
        user.AppendLine("Use verify only when exact transcript evidence from both sides directly establishes one unfolding real-world event.");
        user.AppendLine("Shared generic event type, response language, color, age range, radio timing, retrieval rank, or broad location type is insufficient.");
        user.AppendLine("Explicitly compare concrete subjects, locations, vehicles, identifiers, circumstances, and chronology. A material mismatch requires reject unless the transcripts themselves connect or resolve it.");
        user.AppendLine("For verify, counter_evidence and unresolved_questions must both be empty. Otherwise reject.");
        user.AppendLine("Both decisions must cite short contiguous verbatim spans from both source boundaries. Never copy or paraphrase a quote.");
        user.AppendLine("Rejection prevents a merge but does not prove the events are unrelated; each source group remains independently reviewable.");
        user.AppendLine();
        user.AppendLine("Proposed confirmations:");
        user.AppendLine(JsonSerializer.Serialize(pairs, EngineConfig.JsonOptions()));
        return new IncidentBatchConfirmationPromptPayload(
            "You are an independent fail-closed verifier for proposed incident membership merges. You cannot construct events or alter evidence. Application code validates exact citations and owns state transitions.",
            user.ToString(),
            ResponseFormat(bundle, sources, candidates, confirmations));
    }

    private static object ResponseFormat(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> confirmations)
    {
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var sourceIds = sources.SelectMany(item => item.NewObservationIds).SelectMany(id => observations[id].Transcripts).Select(item => item.TranscriptId).Distinct(StringComparer.Ordinal).ToArray();
        var candidateIds = candidates.SelectMany(item => item.ObservationIds).SelectMany(id => observations[id].Transcripts).Select(item => item.TranscriptId).Distinct(StringComparer.Ordinal).ToArray();
        object Citations(string[] ids) => new
        {
            type = "array",
            minItems = 1,
            items = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    transcript_id = new { type = "string", @enum = ids },
                    exact_quotes = new { type = "array", minItems = 1, maxItems = 4, items = new { type = "string" } }
                },
                required = new[] { "transcript_id", "exact_quotes" }
            }
        };
        object Strings() => new { type = "array", items = new { type = "string" } };
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_batch_confirmation_verifier_v1",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        decisions = new
                        {
                            type = "array",
                            minItems = confirmations.Count,
                            maxItems = confirmations.Count,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    source_proposal_token = new { type = "string", @enum = confirmations.Select(item => item.SourceProposalToken).Distinct(StringComparer.Ordinal).ToArray() },
                                    candidate_token = new { type = "string", @enum = confirmations.Select(item => item.CandidateToken).Distinct(StringComparer.Ordinal).ToArray() },
                                    decision = new { type = "string", @enum = new[] { "verify", "reject" } },
                                    verification_statement = new { type = "string" },
                                    source_evidence = Citations(sourceIds),
                                    candidate_evidence = Citations(candidateIds),
                                    counter_evidence = Strings(),
                                    unresolved_questions = Strings()
                                },
                                required = new[] { "source_proposal_token", "candidate_token", "decision", "verification_statement", "source_evidence", "candidate_evidence", "counter_evidence", "unresolved_questions" }
                            }
                        }
                    },
                    required = new[] { "decisions" }
                }
            }
        };
    }
}

public sealed class OpenAiIncidentBatchConfirmationVerifier : IIncidentBatchConfirmationVerifier
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger _logger;
    private readonly string _runId;

    public OpenAiIncidentBatchConfirmationVerifier(EngineConfig config, EngineDatabase database, ILogger logger, string runId)
    {
        _config = config;
        _database = database;
        _logger = logger;
        _runId = runId;
    }

    public async Task<IncidentBatchConfirmationProposal> VerifyAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> confirmations,
        CancellationToken ct)
    {
        var prompt = IncidentBatchConfirmationPrompt.Build(bundle, sources, candidates, confirmations);
        var model = _config.AiInsights.OpenAiModel;
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 1800,
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
                throw new InvalidOperationException($"Batch confirmation verifier returned HTTP {(int)response.StatusCode}: {Trim(responseText, 500)}");
            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.TryGetProperty("model", out var modelElement) ? modelElement.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"Batch confirmation model identity mismatch: requested '{model}', received '{responseModel}'.");
            var json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Batch confirmation response content was empty.");
            var parsed = JsonSerializer.Deserialize<ConfirmationResponse>(json, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException("Batch confirmation JSON was empty.");
            var transcripts = bundle.Observations.SelectMany(item => item.Transcripts).ToDictionary(item => item.TranscriptId, item => item.Text, StringComparer.Ordinal);
            var decisions = parsed.Decisions.Select(item => new IncidentBatchConfirmationDecision(
                item.SourceProposalToken,
                item.CandidateToken,
                item.Decision switch
                {
                    "verify" => IncidentBatchConfirmationDecisionKind.Verify,
                    "reject" => IncidentBatchConfirmationDecisionKind.Reject,
                    _ => throw new InvalidDataException($"Unsupported confirmation decision '{item.Decision}'.")
                },
                item.VerificationStatement,
                ResolveCitations(item.SourceEvidence, transcripts),
                ResolveCitations(item.CandidateEvidence, transcripts),
                item.CounterEvidence,
                item.UnresolvedQuestions)).ToList();
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, ct);
            return new IncidentBatchConfirmationProposal($"model:incident-batch-confirmation:{Guid.NewGuid():N}", DateTimeOffset.UtcNow, responseModel, IncidentBatchConfirmationPrompt.PromptIdentity, decisions);
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
                0, DateTime.UtcNow, $"incident batch confirmation shadow:{_runId}", "chat.completions", success, error,
                endpoint, requestedModel, usage.ResponseModel, usage.FinishReason, payloadChars, payloadChars,
                usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not record incident batch confirmation verifier usage");
        }
    }

    private static IReadOnlyList<IncidentEventStateTranscriptCitation> ResolveCitations(
        IEnumerable<ConfirmationCitationResponse> citations,
        IReadOnlyDictionary<string, string> transcripts) =>
        citations.SelectMany(citation => citation.ExactQuotes.SelectMany(quote =>
        {
            var quotes = transcripts.TryGetValue(citation.TranscriptId, out var transcript)
                ? IncidentTranscriptCitationResolver.ResolveSegments(transcript, quote)
                : [quote];
            return quotes.Select(resolved => new IncidentEventStateTranscriptCitation(citation.TranscriptId, resolved));
        })).ToList();

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
    private sealed record ConfirmationResponse([property: JsonPropertyName("decisions")] IReadOnlyList<ConfirmationItemResponse> Decisions);
    private sealed record ConfirmationItemResponse(
        [property: JsonPropertyName("source_proposal_token")] string SourceProposalToken,
        [property: JsonPropertyName("candidate_token")] string CandidateToken,
        [property: JsonPropertyName("decision")] string Decision,
        [property: JsonPropertyName("verification_statement")] string VerificationStatement,
        [property: JsonPropertyName("source_evidence")] IReadOnlyList<ConfirmationCitationResponse> SourceEvidence,
        [property: JsonPropertyName("candidate_evidence")] IReadOnlyList<ConfirmationCitationResponse> CandidateEvidence,
        [property: JsonPropertyName("counter_evidence")] IReadOnlyList<string> CounterEvidence,
        [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);
    private sealed record ConfirmationCitationResponse(
        [property: JsonPropertyName("transcript_id")] string TranscriptId,
        [property: JsonPropertyName("exact_quotes")] IReadOnlyList<string> ExactQuotes);
}
