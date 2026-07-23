using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public enum IncidentBatchConfirmationDecisionKind
{
    Reject,
    Verify,
    Review
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

public sealed record IncidentBatchConfirmationEvidenceSpan(
    string EvidenceId,
    string ObservationId,
    string TranscriptId,
    string ExactQuote);

public static class IncidentBatchConfirmationEvidenceCatalog
{
    public const int MaximumSpanLength = 240;
    private const int TargetOverlapLength = 48;

    public static IReadOnlyList<IncidentBatchConfirmationEvidenceSpan> Build(
        IncidentEventStateObservationBundle bundle)
    {
        var result = new List<IncidentBatchConfirmationEvidenceSpan>();
        for (var observationIndex = 0; observationIndex < bundle.Observations.Count; observationIndex++)
        {
            var observation = bundle.Observations[observationIndex];
            for (var transcriptIndex = 0; transcriptIndex < observation.Transcripts.Count; transcriptIndex++)
            {
                var transcript = observation.Transcripts[transcriptIndex];
                var spans = SplitExactSpans(transcript.Text);
                for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
                {
                    result.Add(new IncidentBatchConfirmationEvidenceSpan(
                        $"evidence:{observationIndex + 1}:{transcriptIndex + 1}:{spanIndex + 1}",
                        observation.ObservationId,
                        transcript.TranscriptId,
                        spans[spanIndex]));
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<IncidentEventStateTranscriptCitation> Resolve(
        IEnumerable<string> evidenceIds,
        IReadOnlyList<IncidentBatchConfirmationEvidenceSpan> catalog)
    {
        var spans = catalog
            .GroupBy(item => item.EvidenceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IncidentEventStateTranscriptCitation>();
        foreach (var evidenceId in evidenceIds)
        {
            if (!spans.TryGetValue(evidenceId, out var span))
                throw new InvalidDataException($"Unknown confirmation evidence id '{evidenceId}'.");
            if (!seen.Add(evidenceId))
                continue;
            result.Add(new IncidentEventStateTranscriptCitation(span.TranscriptId, span.ExactQuote));
        }

        return result;
    }

    private static IReadOnlyList<string> SplitExactSpans(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var result = new List<string>();
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;

        while (start < text.Length)
        {
            var limit = Math.Min(text.Length, start + MaximumSpanLength);
            var end = limit;
            if (limit < text.Length)
            {
                var lowerBound = Math.Min(limit, start + (MaximumSpanLength / 2));
                for (var index = limit; index > lowerBound; index--)
                {
                    if (!char.IsWhiteSpace(text[index - 1]))
                        continue;
                    end = index - 1;
                    break;
                }
            }

            while (end > start && char.IsWhiteSpace(text[end - 1]))
                end--;
            if (end <= start)
                end = limit;

            result.Add(text[start..end]);
            if (end >= text.Length)
                break;

            var next = Math.Max(start + 1, end - TargetOverlapLength);
            while (next < end && !char.IsWhiteSpace(text[next]))
                next++;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
                next++;
            start = next <= start ? end : next;
        }

        return result;
    }
}

public interface IIncidentBatchConfirmationVerifier
{
    Task<IncidentBatchConfirmationProposal> VerifyAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> relationships,
        CancellationToken ct);
}

public static class IncidentBatchConfirmationContract
{
    public const string LegacyConfigurationToken = "confirmation=independent-verifier-v1";
    public const string PreviousIndependentConfigurationToken = "relationship-verification=independent-v2";
    public const string ReviewDecisionConfigurationToken = "relationship-verification=independent-v3";
    public const string PerCitationEvidenceToken = "evidence=per-citation-v1";
    public const string ApplicationOwnedEvidenceToken = "evidence=application-spans-v1";
    public const string ReviewDowngradeToken = "confirmation=review-downgrade-v1";
    public const string ConfigurationToken = $"{ReviewDecisionConfigurationToken};{ApplicationOwnedEvidenceToken};{ReviewDowngradeToken}";
    public const double ReviewUncertaintyFloor = 0.5;

    public static IncidentEventStateContractValidationResult ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> relationships,
        IncidentBatchConfirmationProposal proposal)
    {
        var errors = ValidateHeader(proposal).ToList();
        var expected = relationships
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
        IReadOnlyList<IncidentBatchRelationship> relationships,
        IncidentBatchConfirmationProposal proposal,
        bool retainOnlyExactEvidence = false)
    {
        return AcceptedDecisions(
                bundle,
                sources,
                candidates,
                relationships,
                proposal,
                retainOnlyExactEvidence)
            .Where(item => item.Value.Decision == IncidentBatchConfirmationDecisionKind.Verify)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, IncidentBatchConfirmationDecision> AcceptedDecisions(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> relationships,
        IncidentBatchConfirmationProposal proposal,
        bool retainOnlyExactEvidence = false)
    {
        if (ValidateHeader(proposal).Count > 0)
            return new Dictionary<string, IncidentBatchConfirmationDecision>(StringComparer.Ordinal);
        var expected = relationships
            .Select(RelationshipKey)
            .ToHashSet(StringComparer.Ordinal);
        var duplicate = proposal.Decisions
            .GroupBy(DecisionKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var decisions = proposal.Decisions
            .Where(item => expected.Contains(DecisionKey(item)))
            .Where(item => !duplicate.Contains(DecisionKey(item)));
        if (retainOnlyExactEvidence)
            decisions = decisions.Select(item => RetainExactEvidence(bundle, sources, candidates, item));
        return decisions
            .Where(item => ValidateDecision(bundle, sources, candidates, expected, item).Count == 0)
            .ToDictionary(DecisionKey, item => item, StringComparer.Ordinal);
    }

    public static IncidentBatchRelationship ApplyAcceptedDecision(
        IncidentBatchRelationship relationship,
        IncidentBatchConfirmationDecision decision)
    {
        if (decision.Decision == IncidentBatchConfirmationDecisionKind.Reject)
            throw new InvalidOperationException("A rejected confirmation decision cannot be applied.");
        if (decision.Decision == IncidentBatchConfirmationDecisionKind.Verify)
            return relationship;
        return relationship with
        {
            Disposition = IncidentBatchRelationshipDisposition.ProvisionalAssociation,
            RelationshipStatement = decision.VerificationStatement,
            Uncertainty = Math.Max(relationship.Uncertainty, ReviewUncertaintyFloor),
            SourceEvidence = decision.SourceEvidence,
            CandidateEvidence = decision.CandidateEvidence,
            AlternativeInterpretations = decision.CounterEvidence,
            UnresolvedQuestions = decision.UnresolvedQuestions
        };
    }

    public static string RelationshipKey(IncidentBatchRelationship relationship) =>
        PairKey(relationship.SourceProposalToken, relationship.CandidateToken);

    public static string DecisionKey(IncidentBatchConfirmationDecision decision) =>
        PairKey(decision.SourceProposalToken, decision.CandidateToken);

    public static bool UsesIndependentVerifier(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, LegacyConfigurationToken, StringComparison.Ordinal) ||
                          string.Equals(token, PreviousIndependentConfigurationToken, StringComparison.Ordinal) ||
                          string.Equals(token, ReviewDecisionConfigurationToken, StringComparison.Ordinal));

    public static bool VerifiesAllRelationships(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, PreviousIndependentConfigurationToken, StringComparison.Ordinal) ||
                          string.Equals(token, ReviewDecisionConfigurationToken, StringComparison.Ordinal));

    public static bool UsesPerCitationEvidence(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, PerCitationEvidenceToken, StringComparison.Ordinal) ||
                          string.Equals(token, ApplicationOwnedEvidenceToken, StringComparison.Ordinal));

    private static IncidentBatchConfirmationDecision RetainExactEvidence(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IncidentBatchConfirmationDecision decision)
    {
        var source = sources.Single(item => item.SourceProposalToken == decision.SourceProposalToken);
        var candidate = candidates.Single(item => item.CandidateToken == decision.CandidateToken);
        return decision with
        {
            SourceEvidence = RetainExactCitations(bundle, source.NewObservationIds, decision.SourceEvidence),
            CandidateEvidence = RetainExactCitations(bundle, candidate.ObservationIds, decision.CandidateEvidence)
        };
    }

    private static IReadOnlyList<IncidentEventStateTranscriptCitation> RetainExactCitations(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> allowedObservationIds,
        IReadOnlyList<IncidentEventStateTranscriptCitation> citations)
    {
        var transcripts = bundle.Observations
            .Where(item => allowedObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
            .SelectMany(item => item.Transcripts)
            .GroupBy(item => item.TranscriptId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Text, StringComparer.Ordinal);
        return citations
            .Where(item => !string.IsNullOrWhiteSpace(item.TranscriptId) && !string.IsNullOrWhiteSpace(item.ExactQuote))
            .Where(item => transcripts.TryGetValue(item.TranscriptId, out var text) && text.Contains(item.ExactQuote, StringComparison.Ordinal))
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<string> ValidateHeader(IncidentBatchConfirmationProposal proposal)
    {
        var errors = new List<string>();
        RequireValue(proposal.ProposalId, "confirmation proposal id", errors);
        RequireValue(proposal.ModelIdentity, "confirmation model identity", errors);
        if (!string.Equals(proposal.PromptIdentity, IncidentBatchConfirmationPrompt.PromptIdentity, StringComparison.Ordinal) &&
            !string.Equals(proposal.PromptIdentity, IncidentBatchConfirmationPrompt.ApplicationOwnedPromptIdentity, StringComparison.Ordinal) &&
            !string.Equals(proposal.PromptIdentity, IncidentBatchConfirmationPrompt.PriorPromptIdentity, StringComparison.Ordinal) &&
            !string.Equals(proposal.PromptIdentity, IncidentBatchConfirmationPrompt.PreviousPromptIdentity, StringComparison.Ordinal) &&
            !string.Equals(proposal.PromptIdentity, IncidentBatchConfirmationPrompt.LegacyPromptIdentity, StringComparison.Ordinal))
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
        if (decision.VerificationStatement?.Length > IncidentBatchRelationshipContract.MaximumTextLength)
            errors.Add($"confirmation statement for '{DisplayKey(key)}' exceeds {IncidentBatchRelationshipContract.MaximumTextLength} characters");
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
        if (decision.SourceEvidence.Count > IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide)
            errors.Add($"confirmation source evidence contains more than {IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide} spans");
        if (decision.CandidateEvidence.Count > IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide)
            errors.Add($"confirmation candidate evidence contains more than {IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide} spans");
        if (decision.CounterEvidence.Count > IncidentBatchRelationshipContract.MaximumAlternatives)
            errors.Add($"confirmation contains more than {IncidentBatchRelationshipContract.MaximumAlternatives} counterevidence items");
        if (decision.UnresolvedQuestions.Count > IncidentBatchRelationshipContract.MaximumUnresolvedQuestions)
            errors.Add($"confirmation contains more than {IncidentBatchRelationshipContract.MaximumUnresolvedQuestions} unresolved questions");
        if (decision.Decision == IncidentBatchConfirmationDecisionKind.Verify &&
            (decision.CounterEvidence.Count > 0 || decision.UnresolvedQuestions.Count > 0))
        {
            errors.Add($"verified confirmation '{DisplayKey(key)}' cannot retain counterevidence or unresolved questions");
        }
        if (decision.Decision == IncidentBatchConfirmationDecisionKind.Review &&
            decision.CounterEvidence.Count == 0 &&
            decision.UnresolvedQuestions.Count == 0)
        {
            errors.Add($"review confirmation '{DisplayKey(key)}' must explain its remaining uncertainty");
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
            if (value?.Length > IncidentBatchRelationshipContract.MaximumTextLength)
                errors.Add($"{owner} exceeds {IncidentBatchRelationshipContract.MaximumTextLength} characters");
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
    public const string LegacyPromptIdentity = "incident-batch-confirmation-verifier-v1";
    public const string PreviousPromptIdentity = "incident-batch-relationship-verifier-v2";
    public const string PriorPromptIdentity = "incident-batch-relationship-verifier-v3";
    public const string ApplicationOwnedPromptIdentity = "incident-batch-relationship-verifier-v4-application-evidence";
    public const string PromptIdentity = "incident-batch-relationship-verifier-v5-review-downgrade";

    public static IncidentBatchConfirmationPromptPayload Build(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> relationships)
    {
        if (relationships.Count == 0)
            throw new ArgumentException("relationship verification requires at least one proposed relationship", nameof(relationships));
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var sourceMap = sources.ToDictionary(item => item.SourceProposalToken, StringComparer.Ordinal);
        var candidateMap = candidates.ToDictionary(item => item.CandidateToken, StringComparer.Ordinal);
        var evidenceCatalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle);
        var spansByTranscript = evidenceCatalog
            .GroupBy(item => item.TranscriptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        object PromptObservation(string id) => new
        {
            observation_id = id,
            observed_at_unix_seconds = observations[id].ObservedAtUnixSeconds,
            transcripts = observations[id].Transcripts.Select(item => new
            {
                transcript_id = item.TranscriptId,
                evidence_spans = spansByTranscript[item.TranscriptId]
                    .Select(span => new { evidence_id = span.EvidenceId, exact_quote = span.ExactQuote })
                    .ToList()
            }).ToList()
        };
        var pairs = relationships.Select(item => new
        {
            source_proposal_token = item.SourceProposalToken,
            candidate_token = item.CandidateToken,
            proposed_disposition = item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership
                ? "confirmed_membership"
                : "provisional_association",
            untrusted_proposer_statement = item.RelationshipStatement,
            source_observations = sourceMap[item.SourceProposalToken].NewObservationIds.Select(PromptObservation).ToList(),
            candidate_observations = candidateMap[item.CandidateToken].ObservationIds.Select(PromptObservation).ToList()
        }).ToList();
        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Independently test every proposed relationship. The earlier proposer is untrusted and may have invented a match, promoted generic similarity, or ignored contradictions.");
        user.AppendLine("Choose verify, review, or reject. Review is a non-merging operator-visible association; it never grants event membership.");
        user.AppendLine("For confirmed_membership, use verify when exact transcript evidence from both sides supports one unfolding real-world event and no material contradiction remains. Use review when specific continuity is plausible but a material identity question prevents safe membership.");
        user.AppendLine("Radio follow-ups are often elliptical: a later transmission need not repeat every address component, clinical detail, vehicle detail, or circumstance from an earlier transmission. Information present on only one side is omission, not contradiction.");
        user.AppendLine("ASR text is noisy evidence, not authoritative spelling. Plausible near-homophones, partial identifiers, and differently transcribed fragments are not matches or contradictions by themselves. Evaluate whether multiple independent details converge, and put any remaining ambiguity in review.");
        user.AppendLine("A sufficiently specific shared identity or a coherent sequence of dispatch and follow-up facts can establish continuity even when one side adds detail. Judge the combined evidence and chronology rather than requiring each side to restate the other verbatim. Timing alone remains insufficient.");
        user.AppendLine("For provisional_association, use verify when exact evidence establishes a specific operational connection or cross-reference even though it does not establish shared event membership. Use review when exact evidence supports a concrete plausible connection but a material question remains. Both outcomes remain non-merging Review links.");
        user.AppendLine("Reject topical resemblance, isolated shared words, concurrent but independent responses, explicit conflicts, and proposals with no specific operational connection. Shared generic event type, response language, color, age range, radio timing, retrieval rank, or broad location type is insufficient.");
        user.AppendLine("Explicitly compare concrete subjects, locations, vehicles, identifiers, circumstances, operational progression, and chronology. Do not manufacture a mismatch from a detail that is merely absent or plausibly mistranscribed.");
        user.AppendLine("For verify, counter_evidence and unresolved_questions must both be empty. For review, include at least one concrete unresolved question or counterevidence item explaining why membership is not safe. Reject only when the specific relationship itself is unsupported or contradicted.");
        user.AppendLine("Every decision must select evidence_id values from both source boundaries. Evidence spans and their exact quote text are owned by the application; return only their IDs and never generate, copy, edit, or paraphrase quote text.");
        user.AppendLine("Rejection prevents a merge but does not prove the events are unrelated; each source group remains independently reviewable.");
        user.AppendLine();
        user.AppendLine("Proposed relationships:");
        user.AppendLine(JsonSerializer.Serialize(pairs, EngineConfig.JsonOptions()));
        return new IncidentBatchConfirmationPromptPayload(
            "You are an independent evidence-bounded verifier for proposed incident relationships. You cannot construct events, write quote text, or alter evidence. Application code owns evidence spans and state transitions. Preserve plausible but uncertain specific continuity as review instead of forcing either membership or rejection.",
            user.ToString(),
            ResponseFormat(bundle, sources, candidates, relationships, evidenceCatalog));
    }

    private static object ResponseFormat(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationship> relationships,
        IReadOnlyList<IncidentBatchConfirmationEvidenceSpan> evidenceCatalog)
    {
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var sourceIds = sources.SelectMany(item => item.NewObservationIds).SelectMany(id => observations[id].Transcripts).Select(item => item.TranscriptId).Distinct(StringComparer.Ordinal).ToArray();
        var candidateIds = candidates.SelectMany(item => item.ObservationIds).SelectMany(id => observations[id].Transcripts).Select(item => item.TranscriptId).Distinct(StringComparer.Ordinal).ToArray();
        var sourceEvidenceIds = evidenceCatalog.Where(item => sourceIds.Contains(item.TranscriptId, StringComparer.Ordinal)).Select(item => item.EvidenceId).ToArray();
        var candidateEvidenceIds = evidenceCatalog.Where(item => candidateIds.Contains(item.TranscriptId, StringComparer.Ordinal)).Select(item => item.EvidenceId).ToArray();
        object EvidenceIds(string[] ids) => new
        {
            type = "array",
            minItems = 1,
            maxItems = IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide,
            items = new { type = "string", @enum = ids }
        };
        object Strings() => new { type = "array", maxItems = 2, items = new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength } };
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_batch_relationship_verifier_v5",
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
                            minItems = relationships.Count,
                            maxItems = relationships.Count,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    source_proposal_token = new { type = "string", @enum = relationships.Select(item => item.SourceProposalToken).Distinct(StringComparer.Ordinal).ToArray() },
                                    candidate_token = new { type = "string", @enum = relationships.Select(item => item.CandidateToken).Distinct(StringComparer.Ordinal).ToArray() },
                                    decision = new { type = "string", @enum = new[] { "verify", "review", "reject" } },
                                    verification_statement = new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength },
                                    source_evidence_ids = EvidenceIds(sourceEvidenceIds),
                                    candidate_evidence_ids = EvidenceIds(candidateEvidenceIds),
                                    counter_evidence = Strings(),
                                    unresolved_questions = Strings()
                                },
                                required = new[] { "source_proposal_token", "candidate_token", "decision", "verification_statement", "source_evidence_ids", "candidate_evidence_ids", "counter_evidence", "unresolved_questions" }
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
        IReadOnlyList<IncidentBatchRelationship> relationships,
        CancellationToken ct)
    {
        var prompt = IncidentBatchConfirmationPrompt.Build(bundle, sources, candidates, relationships);
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
            var evidenceCatalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle);
            var decisions = parsed.Decisions.Select(item => new IncidentBatchConfirmationDecision(
                item.SourceProposalToken,
                item.CandidateToken,
                item.Decision switch
                {
                    "verify" => IncidentBatchConfirmationDecisionKind.Verify,
                    "review" => IncidentBatchConfirmationDecisionKind.Review,
                    "reject" => IncidentBatchConfirmationDecisionKind.Reject,
                    _ => throw new InvalidDataException($"Unsupported confirmation decision '{item.Decision}'.")
                },
                item.VerificationStatement,
                IncidentBatchConfirmationEvidenceCatalog.Resolve(item.SourceEvidenceIds, evidenceCatalog),
                IncidentBatchConfirmationEvidenceCatalog.Resolve(item.CandidateEvidenceIds, evidenceCatalog),
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
        [property: JsonPropertyName("source_evidence_ids")] IReadOnlyList<string> SourceEvidenceIds,
        [property: JsonPropertyName("candidate_evidence_ids")] IReadOnlyList<string> CandidateEvidenceIds,
        [property: JsonPropertyName("counter_evidence")] IReadOnlyList<string> CounterEvidence,
        [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);
}
