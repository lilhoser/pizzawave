using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public enum IncidentBatchRelationshipDisposition
{
    ConfirmedMembership,
    ProvisionalAssociation
}

public sealed record IncidentBatchRelationshipSource(
    string SourceProposalToken,
    IReadOnlyList<string> NewObservationIds);

public sealed record IncidentBatchRelationship(
    string SourceProposalToken,
    string CandidateToken,
    IncidentBatchRelationshipDisposition Disposition,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateTranscriptCitation> SourceEvidence,
    IReadOnlyList<IncidentEventStateTranscriptCitation> CandidateEvidence,
    IReadOnlyList<string> AlternativeInterpretations,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentBatchRelationshipProposal(
    string ProposalId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentBatchRelationship> Relationships);

public sealed record IncidentBatchRelationshipExecutionContext(
    long ProposerDurationMilliseconds,
    string ProposerError);

public interface IIncidentBatchRelationshipProposer
{
    Task<IncidentBatchRelationshipProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        CancellationToken ct);
}

public static class IncidentBatchRelationshipContract
{
    public const int MaximumSourceCount = IncidentBatchPrompt.MaximumReturnedEvents;
    public const int MaximumCandidateCount = IncidentBatchContract.MaximumCandidateCount;
    public const string ConfigurationToken = "relationship-stage=source-isolated-v2;confirmation=conflict-free-v1";

    public static IncidentEventStateContractValidationResult ValidateInput(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates)
    {
        var errors = IncidentEventStateContractValidator.ValidateBundle(bundle).Errors.ToList();
        if (sources.Count > MaximumSourceCount)
            errors.Add($"relationship stage contains more than {MaximumSourceCount} constructed groups");
        if (candidates.Count > MaximumCandidateCount)
            errors.Add($"relationship stage contains more than {MaximumCandidateCount} candidates");
        RequireUnique(sources.Select(item => item.SourceProposalToken), "source proposal token", errors);
        RequireUnique(candidates.Select(item => item.CandidateToken), "candidate token", errors);

        var observations = bundle.Observations.Select(item => item.ObservationId).ToHashSet(StringComparer.Ordinal);
        var sourceOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            RequireValue(source.SourceProposalToken, "source proposal token", errors);
            if (source.NewObservationIds.Count == 0)
                errors.Add($"source proposal '{source.SourceProposalToken}' contains no observations");
            RequireUnique(source.NewObservationIds, $"observation in source proposal '{source.SourceProposalToken}'", errors);
            foreach (var observationId in source.NewObservationIds)
            {
                if (!observations.Contains(observationId))
                    errors.Add($"source proposal '{source.SourceProposalToken}' references unknown observation '{observationId}'");
                if (!sourceOwners.Add(observationId))
                    errors.Add($"new observation '{observationId}' belongs to more than one constructed group");
            }
        }

        foreach (var candidate in candidates)
        {
            RequireValue(candidate.CandidateToken, "candidate token", errors);
            RequireValue(candidate.ProjectionEventId, $"projection event id for candidate '{candidate.CandidateToken}'", errors);
            if (candidate.ObservationIds.Count == 0 || candidate.ObservationIds.Count > IncidentBatchContract.MaximumObservationsPerCandidate)
                errors.Add($"candidate '{candidate.CandidateToken}' has an invalid observation count");
            RequireUnique(candidate.ObservationIds, $"observation in candidate '{candidate.CandidateToken}'", errors);
            foreach (var observationId in candidate.ObservationIds)
            {
                if (!observations.Contains(observationId))
                    errors.Add($"candidate '{candidate.CandidateToken}' references unknown observation '{observationId}'");
                if (sourceOwners.Contains(observationId))
                    errors.Add($"candidate '{candidate.CandidateToken}' contains new source observation '{observationId}'");
            }
        }
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IncidentBatchRelationshipProposal proposal)
    {
        var errors = ValidateInput(bundle, sources, candidates).Errors.ToList();
        RequireValue(proposal.ProposalId, "relationship proposal id", errors);
        RequireValue(proposal.ModelIdentity, "relationship model identity", errors);
        RequireValue(proposal.PromptIdentity, "relationship prompt identity", errors);
        if (proposal.GeneratedAtUtc == default)
            errors.Add("relationship proposal generated timestamp is required");

        var sourceMap = sources.ToDictionary(item => item.SourceProposalToken, StringComparer.Ordinal);
        var candidateMap = candidates.ToDictionary(item => item.CandidateToken, StringComparer.Ordinal);
        RequireUnique(
            proposal.Relationships.Select(item => $"{item.SourceProposalToken}\u001f{item.CandidateToken}"),
            "source-candidate relationship pair",
            errors);
        foreach (var group in proposal.Relationships.GroupBy(item => item.SourceProposalToken, StringComparer.Ordinal))
            if (group.Count(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership) > 1)
                errors.Add($"source proposal '{group.Key}' has more than one confirmed membership");

        foreach (var relationship in proposal.Relationships)
        {
            if (!sourceMap.TryGetValue(relationship.SourceProposalToken, out var source))
            {
                errors.Add($"relationship references unknown source proposal '{relationship.SourceProposalToken}'");
                continue;
            }
            if (!candidateMap.TryGetValue(relationship.CandidateToken, out var candidate))
            {
                errors.Add($"relationship references unknown candidate '{relationship.CandidateToken}'");
                continue;
            }
            if (!Enum.IsDefined(relationship.Disposition))
                errors.Add($"relationship '{relationship.SourceProposalToken}' to '{relationship.CandidateToken}' has an invalid disposition");
            RequireValue(relationship.RelationshipStatement, "relationship statement", errors);
            if (double.IsNaN(relationship.Uncertainty) || double.IsInfinity(relationship.Uncertainty) || relationship.Uncertainty is < 0 or > 1)
                errors.Add($"relationship '{relationship.SourceProposalToken}' to '{relationship.CandidateToken}' has invalid uncertainty");
            if (relationship.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership)
            {
                if (relationship.Uncertainty != 0)
                    errors.Add($"confirmed membership '{relationship.SourceProposalToken}' to '{relationship.CandidateToken}' cannot express uncertainty");
                if (relationship.AlternativeInterpretations.Count > 0 || relationship.UnresolvedQuestions.Count > 0)
                    errors.Add($"confirmed membership '{relationship.SourceProposalToken}' to '{relationship.CandidateToken}' cannot retain counterinterpretations or unresolved questions");
            }
            ValidateCitations(bundle, source.NewObservationIds, relationship.SourceEvidence, "constructed-group evidence", errors);
            ValidateCitations(bundle, candidate.ObservationIds, relationship.CandidateEvidence, "candidate evidence", errors);
            ValidateStrings(relationship.AlternativeInterpretations, "alternative interpretation", errors);
            ValidateStrings(relationship.UnresolvedQuestions, "unresolved question", errors);
        }
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
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
        foreach (var citation in citations)
        {
            RequireValue(citation.TranscriptId, $"transcript id in {owner}", errors);
            RequireValue(citation.ExactQuote, $"exact quote in {owner}", errors);
            if (!transcripts.TryGetValue(citation.TranscriptId, out var matches) || matches.Count != 1)
                errors.Add($"{owner} cites a transcript outside its source boundary");
            else if (!matches[0].Text.Contains(citation.ExactQuote, StringComparison.Ordinal))
                errors.Add($"{owner} quote does not occur exactly in transcript '{citation.TranscriptId}'");
        }
    }

    private static void ValidateStrings(IReadOnlyList<string> values, string description, List<string> errors)
    {
        RequireUnique(values, description, errors);
        foreach (var value in values)
            RequireValue(value, description, errors);
    }

    private static void RequireUnique(IEnumerable<string> values, string description, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!seen.Add(value ?? string.Empty))
                errors.Add($"duplicate {description} '{value}'");
    }

    private static void RequireValue(string? value, string description, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{description} is required");
    }
}

public sealed record IncidentBatchRelationshipPromptPayload(string SystemPrompt, string UserPrompt, object ResponseFormat);

public static class IncidentBatchRelationshipPrompt
{
    public const string PromptIdentity = "incident-batch-relationship-v1-source-isolated";

    public static IncidentBatchRelationshipPromptPayload Build(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates)
    {
        var validation = IncidentBatchRelationshipContract.ValidateInput(bundle, sources, candidates);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(bundle));
        if (sources.Count == 0 || candidates.Count == 0)
            throw new ArgumentException("relationship prompts require at least one constructed group and candidate");

        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        object PromptObservation(string id) => new
        {
            observation_id = id,
            observed_at_unix_seconds = observations[id].ObservedAtUnixSeconds,
            transcripts = observations[id].Transcripts.Select(item => new { transcript_id = item.TranscriptId, text = item.Text, producer = item.Producer }).ToList()
        };
        var source = new
        {
            constructed_groups = sources.Select(item => new
            {
                source_proposal_token = item.SourceProposalToken,
                source_observations = item.NewObservationIds.Select(PromptObservation).ToList()
            }).ToList(),
            candidate_events = candidates.Select(item => new
            {
                candidate_token = item.CandidateToken,
                source_observations = item.ObservationIds.Select(PromptObservation).ToList()
            }).ToList()
        };
        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Evaluate relationships between every constructed group and every supplied candidate using only their transcripts.");
        user.AppendLine("The constructed groups are immutable. Do not rewrite, split, combine, discard, or add facts to them.");
        user.AppendLine("Return confirmed_membership only when exact evidence from both sides directly establishes one unfolding real-world event. Return at most one confirmed membership for each constructed group.");
        user.AppendLine("A confirmed_membership must use uncertainty 0 and empty alternative_interpretations and unresolved_questions. If either side contains a material discrepancy, counterinterpretation, or unresolved question, return provisional_association or omit the pair; never confirm it.");
        user.AppendLine("Return provisional_association when exact evidence from both sides establishes a specific operational relationship but meaningful uncertainty remains. Several provisional associations may connect one group to several candidates. A provisional association never merges membership.");
        user.AppendLine("Omit unsupported pairs. Timing, retrieval rank, radio metadata, generic similarity, and shared event type do not prove a relationship.");
        user.AppendLine("Each returned relationship must cite short contiguous verbatim spans from both source boundaries. Never borrow a candidate fact into constructed-group evidence or the reverse.");
        user.AppendLine("Copy source_proposal_token, candidate_token, and transcript_id values exactly.");
        user.AppendLine();
        user.AppendLine("Source bundle:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));
        return new IncidentBatchRelationshipPromptPayload(
            "You evaluate typed relationships between immutable source-grounded event groups. You may attach evidence-cited relationships but cannot construct or rewrite events. Application code validates both citation boundaries and owns all state transitions.",
            user.ToString(),
            ResponseFormat(bundle, sources, candidates));
    }

    private static object ResponseFormat(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates)
    {
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var sourceTranscriptIds = sources.SelectMany(item => item.NewObservationIds).SelectMany(id => observations[id].Transcripts).Select(item => item.TranscriptId).Distinct(StringComparer.Ordinal).ToArray();
        var candidateTranscriptIds = candidates.SelectMany(item => item.ObservationIds).SelectMany(id => observations[id].Transcripts).Select(item => item.TranscriptId).Distinct(StringComparer.Ordinal).ToArray();
        object CitationSchema(string[] transcriptIds) => new
        {
            type = "array",
            minItems = 1,
            items = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    transcript_id = new { type = "string", @enum = transcriptIds },
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
                name = "pizzawave_incident_batch_relationship_v1",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        relationships = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    source_proposal_token = new { type = "string", @enum = sources.Select(item => item.SourceProposalToken).ToArray() },
                                    candidate_token = new { type = "string", @enum = candidates.Select(item => item.CandidateToken).ToArray() },
                                    disposition = new { type = "string", @enum = new[] { "confirmed_membership", "provisional_association" } },
                                    relationship_statement = new { type = "string" },
                                    uncertainty = new { type = "number", minimum = 0, maximum = 1 },
                                    source_evidence = CitationSchema(sourceTranscriptIds),
                                    candidate_evidence = CitationSchema(candidateTranscriptIds),
                                    alternative_interpretations = Strings(),
                                    unresolved_questions = Strings()
                                },
                                required = new[] { "source_proposal_token", "candidate_token", "disposition", "relationship_statement", "uncertainty", "source_evidence", "candidate_evidence", "alternative_interpretations", "unresolved_questions" }
                            }
                        }
                    },
                    required = new[] { "relationships" }
                }
            }
        };
    }
}

public sealed class OpenAiIncidentBatchRelationshipProposer : IIncidentBatchRelationshipProposer
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger _logger;
    private readonly string _runId;

    public OpenAiIncidentBatchRelationshipProposer(EngineConfig config, EngineDatabase database, ILogger logger, string runId)
    {
        _config = config;
        _database = database;
        _logger = logger;
        _runId = runId;
    }

    public async Task<IncidentBatchRelationshipProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        CancellationToken ct)
    {
        var prompt = IncidentBatchRelationshipPrompt.Build(bundle, sources, candidates);
        var model = _config.AiInsights.OpenAiModel;
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 2400,
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
                throw new InvalidOperationException($"Batch relationship proposer returned HTTP {(int)response.StatusCode}: {Trim(responseText, 500)}");
            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.TryGetProperty("model", out var modelElement) ? modelElement.GetString() ?? string.Empty : string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"Batch relationship model identity mismatch: requested '{model}', received '{responseModel}'.");
            var json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Batch relationship response content was empty.");
            var parsed = JsonSerializer.Deserialize<RelationshipResponse>(json, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException("Batch relationship JSON was empty.");
            var transcripts = bundle.Observations
                .SelectMany(item => item.Transcripts)
                .ToDictionary(item => item.TranscriptId, item => item.Text, StringComparer.Ordinal);
            var relationships = parsed.Relationships.Select(item => new IncidentBatchRelationship(
                item.SourceProposalToken,
                item.CandidateToken,
                item.Disposition switch
                {
                    "confirmed_membership" => IncidentBatchRelationshipDisposition.ConfirmedMembership,
                    "provisional_association" => IncidentBatchRelationshipDisposition.ProvisionalAssociation,
                    _ => throw new InvalidDataException($"Unsupported batch relationship disposition '{item.Disposition}'.")
                },
                item.RelationshipStatement,
                item.Uncertainty,
                ResolveCitations(item.SourceEvidence, transcripts),
                ResolveCitations(item.CandidateEvidence, transcripts),
                item.AlternativeInterpretations,
                item.UnresolvedQuestions)).ToList();
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, ct);
            return new IncidentBatchRelationshipProposal(
                $"model:incident-batch-relationship:{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                responseModel,
                IncidentBatchRelationshipPrompt.PromptIdentity,
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
                0, DateTime.UtcNow, $"incident batch relationship shadow:{_runId}", "chat.completions", success, error,
                endpoint, requestedModel, usage.ResponseModel, usage.FinishReason, payloadChars, payloadChars,
                usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not record incident batch relationship model usage");
        }
    }

    private static IReadOnlyList<IncidentEventStateTranscriptCitation> ResolveCitations(
        IEnumerable<RelationshipCitationResponse> citations,
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

    private sealed record RelationshipResponse([property: JsonPropertyName("relationships")] IReadOnlyList<RelationshipItemResponse> Relationships);
    private sealed record RelationshipItemResponse(
        [property: JsonPropertyName("source_proposal_token")] string SourceProposalToken,
        [property: JsonPropertyName("candidate_token")] string CandidateToken,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("relationship_statement")] string RelationshipStatement,
        [property: JsonPropertyName("uncertainty")] double Uncertainty,
        [property: JsonPropertyName("source_evidence")] IReadOnlyList<RelationshipCitationResponse> SourceEvidence,
        [property: JsonPropertyName("candidate_evidence")] IReadOnlyList<RelationshipCitationResponse> CandidateEvidence,
        [property: JsonPropertyName("alternative_interpretations")] IReadOnlyList<string> AlternativeInterpretations,
        [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);
    private sealed record RelationshipCitationResponse(
        [property: JsonPropertyName("transcript_id")] string TranscriptId,
        [property: JsonPropertyName("exact_quotes")] IReadOnlyList<string> ExactQuotes);
}
