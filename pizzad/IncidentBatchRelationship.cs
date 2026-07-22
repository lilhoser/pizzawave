using System.Text;
using System.Text.Json;

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

public static class IncidentBatchRelationshipContract
{
    public const int MaximumSourceCount = IncidentBatchPrompt.MaximumReturnedEvents;
    public const int MaximumCandidateCount = IncidentBatchContract.MaximumCandidateCount;
    public const string ConfigurationToken = "relationship-stage=source-isolated-v1";

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
