using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

public sealed record IncidentBatchRelationshipPair(
    string PairToken,
    string SourceProposalToken,
    string CandidateToken);

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

public static class IncidentBatchRelationshipEvidenceCatalog
{
    public static IReadOnlyList<IncidentBatchConfirmationEvidenceSpan> ForObservationIds(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchConfirmationEvidenceSpan> evidenceCatalog,
        IEnumerable<string> observationIds)
    {
        var observationIdSet = observationIds.ToHashSet(StringComparer.Ordinal);
        var transcriptIds = bundle.Observations
            .Where(item => observationIdSet.Contains(item.ObservationId))
            .SelectMany(item => item.Transcripts)
            .Select(item => item.TranscriptId)
            .ToHashSet(StringComparer.Ordinal);
        return evidenceCatalog
            .Where(item => transcriptIds.Contains(item.TranscriptId))
            .ToList();
    }

    public static IReadOnlyList<IncidentEventStateTranscriptCitation> ResolvePairRelativeIndices(
        IEnumerable<int> indices,
        IReadOnlyList<IncidentBatchConfirmationEvidenceSpan> pairSideSpans,
        string side)
    {
        var seen = new HashSet<int>();
        var result = new List<IncidentEventStateTranscriptCitation>();
        foreach (var index in indices)
        {
            if (index < 0 || index >= pairSideSpans.Count)
                throw new InvalidDataException(
                    $"Unknown {side} evidence span index '{index}' for the selected relationship pair.");
            if (!seen.Add(index))
                continue;
            var span = pairSideSpans[index];
            result.Add(new IncidentEventStateTranscriptCitation(span.TranscriptId, span.ExactQuote));
        }

        return result;
    }
}

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
    public const int MaximumSourceCount = IncidentBatchContract.MaximumNewObservationCount;
    public const int MaximumCandidateCount =
        IncidentBatchContract.MaximumCandidateCount + IncidentBatchPrompt.MaximumReturnedEvents;
    public const int MaximumReturnedRelationships = 6;
    public const int MaximumRelationshipsPerSource = 3;
    public const int MaximumEvidenceSpansPerSide = 4;
    public const int MaximumAlternatives = 2;
    public const int MaximumUnresolvedQuestions = 2;
    public const int MaximumTextLength = 320;
    public const string AllObservationSourcesConfigurationToken = "source-admission=all-observations-v1";
    public const string ConfigurationToken = $"relationship-stage=source-isolated-v8;{AllObservationSourcesConfigurationToken};same-batch-peers=accepted-ordered-v2;pair-identity=opaque-eligible-v1;evidence-identity=pair-relative-application-spans-v1;admission=concrete-cross-reference-v2;confirmation=conflict-free-v1;acceptance=per-relationship-v1;output=bounded-selective-v2";

    public static IReadOnlyList<IncidentBatchRelationshipSource> BuildSources(
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchEventProposal> acceptedEvents,
        bool includeUnresolvedObservations = false)
    {
        var order = newObservationIds
            .Select((observationId, index) => new { observationId, index })
            .ToDictionary(item => item.observationId, item => item.index, StringComparer.Ordinal);
        var sources = acceptedEvents
            .OrderBy(item => item.NewObservationIds.Min(observationId => order[observationId]))
            .ThenBy(item => item.ProposalToken, StringComparer.Ordinal)
            .Select(item => new IncidentBatchRelationshipSource(item.ProposalToken, item.NewObservationIds))
            .ToList();
        if (includeUnresolvedObservations)
        {
            var owned = sources
                .SelectMany(item => item.NewObservationIds)
                .ToHashSet(StringComparer.Ordinal);
            sources.AddRange(newObservationIds
                .Where(observationId => !owned.Contains(observationId))
                .Select(observationId => new IncidentBatchRelationshipSource(
                    SingletonSourceToken(observationId),
                    [observationId])));
        }
        return sources
            .OrderBy(item => item.NewObservationIds.Min(observationId => order[observationId]))
            .ThenBy(item => item.SourceProposalToken, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<IncidentBatchRelationshipSource> BuildSources(IncidentBatchLedgerEntry entry) =>
        BuildSources(
            entry.NewObservationIds,
            IncidentBatchContract.AcceptedEvents(entry),
            UsesAllObservationSources(entry.Execution.ConfigurationIdentity));

    public static bool UsesAllObservationSources(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(AllObservationSourcesConfigurationToken, StringComparer.Ordinal);

    public static IReadOnlyList<IncidentBatchCandidate> AddOrderedSameBatchCandidates(
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchSingletonIdentity> singletons,
        IReadOnlyList<IncidentBatchCandidate> priorCandidates,
        IReadOnlySet<string>? eligiblePeerSourceTokens = null)
    {
        if (sources.Count < 2)
            return priorCandidates.ToList();

        var singletonByObservation = singletons.ToDictionary(
            item => item.ObservationId,
            StringComparer.Ordinal);
        var candidates = priorCandidates.ToList();
        var tokens = candidates.Select(item => item.CandidateToken).ToHashSet(StringComparer.Ordinal);
        var projectionIds = candidates.Select(item => item.ProjectionEventId).ToHashSet(StringComparer.Ordinal);
        foreach (var source in sources.Where((item, index) =>
                     index < sources.Count - 1 &&
                     (eligiblePeerSourceTokens is null ||
                      eligiblePeerSourceTokens.Contains(item.SourceProposalToken))))
        {
            var projectionEventId = singletonByObservation[source.NewObservationIds[0]].ProjectionEventId;
            var candidateToken = SameBatchCandidateToken(source.SourceProposalToken);
            if (!tokens.Add(candidateToken))
                throw new InvalidDataException($"same-batch candidate token '{candidateToken}' is not unique");
            if (!projectionIds.Add(projectionEventId))
                throw new InvalidDataException($"same-batch candidate projection '{projectionEventId}' is not unique");
            candidates.Add(new IncidentBatchCandidate(
                candidateToken,
                projectionEventId,
                source.NewObservationIds.ToList()));
        }
        if (candidates.Count > MaximumCandidateCount)
            throw new InvalidDataException(
                $"relationship stage contains more than {MaximumCandidateCount} candidates after adding same-batch peers");
        return candidates;
    }

    public static IReadOnlyList<string> EligibleCandidateTokens(
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IncidentBatchRelationshipSource source)
    {
        var sourceIndex = -1;
        for (var index = 0; index < sources.Count; index++)
        {
            if (string.Equals(
                    sources[index].SourceProposalToken,
                    source.SourceProposalToken,
                    StringComparison.Ordinal))
            {
                sourceIndex = index;
                break;
            }
        }
        if (sourceIndex < 0)
            return [];
        return candidates
            .Where(candidate =>
            {
                var peerIndex = PeerSourceIndex(sources, candidate);
                return peerIndex < 0 || peerIndex < sourceIndex;
            })
            .Select(item => item.CandidateToken)
            .ToList();
    }

    public static IReadOnlyList<IncidentBatchRelationshipPair> EligiblePairs(
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates) =>
        sources
            .SelectMany(source => EligibleCandidateTokens(sources, candidates, source)
                .Select(candidateToken => new IncidentBatchRelationshipPair(
                    RelationshipPairToken(source.SourceProposalToken, candidateToken),
                    source.SourceProposalToken,
                    candidateToken)))
            .ToList();

    private static int PeerSourceIndex(
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IncidentBatchCandidate candidate)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            if (candidate.ObservationIds.SequenceEqual(sources[index].NewObservationIds, StringComparer.Ordinal))
                return index;
        }
        return -1;
    }

    private static string SameBatchCandidateToken(string sourceProposalToken)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceProposalToken)))
            .ToLowerInvariant();
        return $"candidate-peer-{digest[..16]}";
    }

    public static string SingletonSourceToken(string observationId)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observationId)))
            .ToLowerInvariant();
        return $"source-singleton-{digest[..16]}";
    }

    private static string RelationshipPairToken(string sourceProposalToken, string candidateToken)
    {
        var digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes($"{sourceProposalToken}\n{candidateToken}")))
            .ToLowerInvariant();
        return $"relationship-pair-{digest[..20]}";
    }

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
        RequireUnique(candidates.Select(item => item.ProjectionEventId), "candidate projection event id", errors);

        var observations = bundle.Observations.Select(item => item.ObservationId).ToHashSet(StringComparer.Ordinal);
        var sourceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
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
                if (!sourceOwners.TryAdd(observationId, source.SourceProposalToken))
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
            }
            var peerOwners = candidate.ObservationIds
                .Where(sourceOwners.ContainsKey)
                .Select(observationId => sourceOwners[observationId])
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (peerOwners.Count > 0)
            {
                if (peerOwners.Count != 1)
                    errors.Add($"same-batch candidate '{candidate.CandidateToken}' spans more than one constructed group");
                else
                {
                    var peer = sources.Single(item => item.SourceProposalToken == peerOwners[0]);
                    if (!candidate.ObservationIds.SequenceEqual(peer.NewObservationIds, StringComparer.Ordinal))
                        errors.Add($"same-batch candidate '{candidate.CandidateToken}' does not exactly match constructed group '{peer.SourceProposalToken}'");
                    if (!sources
                            .Where(item => !string.Equals(
                                item.SourceProposalToken,
                                peer.SourceProposalToken,
                                StringComparison.Ordinal))
                            .Any(item => EligibleCandidateTokens(sources, [candidate], item).Contains(candidate.CandidateToken, StringComparer.Ordinal)))
                        errors.Add($"same-batch candidate '{candidate.CandidateToken}' has no later constructed group");
                }
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
        if (proposal.Relationships.Count > MaximumReturnedRelationships)
            errors.Add($"relationship proposal contains more than {MaximumReturnedRelationships} relationships");

        var sourceMap = sources.ToDictionary(item => item.SourceProposalToken, StringComparer.Ordinal);
        var candidateMap = candidates.ToDictionary(item => item.CandidateToken, StringComparer.Ordinal);
        RequireUnique(
            proposal.Relationships.Select(item => $"{item.SourceProposalToken}\u001f{item.CandidateToken}"),
            "source-candidate relationship pair",
            errors);
        foreach (var group in proposal.Relationships.GroupBy(item => item.SourceProposalToken, StringComparer.Ordinal))
        {
            if (group.Count() > MaximumRelationshipsPerSource)
                errors.Add($"source proposal '{group.Key}' has more than {MaximumRelationshipsPerSource} relationships");
            if (group.Count(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership) > 1)
                errors.Add($"source proposal '{group.Key}' has more than one confirmed membership");
        }

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
            if (!EligibleCandidateTokens(sources, candidates, source)
                    .Contains(relationship.CandidateToken, StringComparer.Ordinal))
            {
                errors.Add(
                    $"relationship '{relationship.SourceProposalToken}' cannot reference ineligible candidate '{relationship.CandidateToken}'");
                continue;
            }
            if (!Enum.IsDefined(relationship.Disposition))
                errors.Add($"relationship '{relationship.SourceProposalToken}' to '{relationship.CandidateToken}' has an invalid disposition");
            RequireValue(relationship.RelationshipStatement, "relationship statement", errors);
            if (relationship.RelationshipStatement?.Length > MaximumTextLength)
                errors.Add($"relationship statement exceeds {MaximumTextLength} characters");
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
            if (relationship.SourceEvidence.Count > MaximumEvidenceSpansPerSide)
                errors.Add($"relationship source evidence contains more than {MaximumEvidenceSpansPerSide} spans");
            if (relationship.CandidateEvidence.Count > MaximumEvidenceSpansPerSide)
                errors.Add($"relationship candidate evidence contains more than {MaximumEvidenceSpansPerSide} spans");
            if (relationship.AlternativeInterpretations.Count > MaximumAlternatives)
                errors.Add($"relationship contains more than {MaximumAlternatives} alternative interpretations");
            if (relationship.UnresolvedQuestions.Count > MaximumUnresolvedQuestions)
                errors.Add($"relationship contains more than {MaximumUnresolvedQuestions} unresolved questions");
        }
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IReadOnlyList<IncidentBatchRelationship> AcceptedRelationships(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IncidentBatchRelationshipProposal proposal)
    {
        var headerValidation = ValidateProposal(bundle, sources, candidates, proposal with { Relationships = [] });
        if (!headerValidation.IsValid)
            return [];
        if (proposal.Relationships.Count > MaximumReturnedRelationships)
            return [];

        var duplicatePairs = proposal.Relationships
            .GroupBy(item => $"{item.SourceProposalToken}\u001f{item.CandidateToken}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var duplicateConfirmedSources = proposal.Relationships
            .Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership)
            .GroupBy(item => item.SourceProposalToken, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var oversizedSources = proposal.Relationships
            .GroupBy(item => item.SourceProposalToken, StringComparer.Ordinal)
            .Where(group => group.Count() > MaximumRelationshipsPerSource)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return proposal.Relationships
            .Where(item => !duplicatePairs.Contains($"{item.SourceProposalToken}\u001f{item.CandidateToken}"))
            .Where(item => item.Disposition != IncidentBatchRelationshipDisposition.ConfirmedMembership || !duplicateConfirmedSources.Contains(item.SourceProposalToken))
            .Where(item => !oversizedSources.Contains(item.SourceProposalToken))
            .Where(item => ValidateProposal(bundle, sources, candidates, proposal with { Relationships = [item] }).IsValid)
            .ToList();
    }

    public static IReadOnlyList<IncidentBatchRelationship> AcceptedRelationships(IncidentBatchLedgerEntry entry)
    {
        if (entry.RelationshipProposal is null)
            return [];
        var sources = BuildSources(entry);
        var accepted = AcceptedRelationships(entry.Bundle, sources, entry.Candidates, entry.RelationshipProposal);
        if (!IncidentBatchConfirmationContract.UsesIndependentVerifier(entry.Execution.ConfigurationIdentity))
            return accepted;

        if (entry.ConfirmationProposal is null)
            return [];
        var verifiesAll = IncidentBatchConfirmationContract.VerifiesAllRelationships(entry.Execution.ConfigurationIdentity);
        var verifiedInput = verifiesAll
            ? accepted
            : accepted.Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership).ToList();
        var acceptedDecisions = IncidentBatchConfirmationContract.AcceptedDecisions(
            entry.Bundle,
            sources,
            entry.Candidates,
            verifiedInput,
            entry.ConfirmationProposal,
            IncidentBatchConfirmationContract.UsesPerCitationEvidence(entry.Execution.ConfigurationIdentity));
        return accepted
            .Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ProvisionalAssociation && !verifiesAll ||
                           acceptedDecisions.TryGetValue(IncidentBatchConfirmationContract.RelationshipKey(item), out var decision) &&
                           decision.Decision != IncidentBatchConfirmationDecisionKind.Reject)
            .Select(item =>
            {
                if (!verifiesAll)
                    return item;
                var decision = acceptedDecisions[IncidentBatchConfirmationContract.RelationshipKey(item)];
                return IncidentBatchConfirmationContract.ApplyAcceptedDecision(item, decision);
            })
            .ToList();
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
            var exactQuote = citation.ExactQuote ?? string.Empty;
            RequireValue(citation.TranscriptId, $"transcript id in {owner}", errors);
            RequireValue(exactQuote, $"exact quote in {owner}", errors);
            if (exactQuote.Length > MaximumTextLength)
                errors.Add($"exact quote in {owner} exceeds {MaximumTextLength} characters");
            if (!transcripts.TryGetValue(citation.TranscriptId, out var matches) || matches.Count != 1)
                errors.Add($"{owner} cites a transcript outside its source boundary");
            else if (!matches[0].Text.Contains(exactQuote, StringComparison.Ordinal))
                errors.Add($"{owner} quote does not occur exactly in transcript '{citation.TranscriptId}'");
        }
    }

    private static void ValidateStrings(IReadOnlyList<string> values, string description, List<string> errors)
    {
        RequireUnique(values, description, errors);
        foreach (var value in values)
        {
            var text = value ?? string.Empty;
            RequireValue(text, description, errors);
            if (text.Length > MaximumTextLength)
                errors.Add($"{description} exceeds {MaximumTextLength} characters");
        }
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
    public const string PromptIdentity = "incident-batch-relationship-v9-all-observation-pair-relative-evidence";

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

        var eligiblePairs = IncidentBatchRelationshipContract.EligiblePairs(sources, candidates);
        var evidenceCatalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle);
        object PairSide(
            IEnumerable<string> observationIds,
            IReadOnlyList<IncidentBatchConfirmationEvidenceSpan> spans) => new
        {
            observation_ids = observationIds,
            evidence_spans = spans.Select((span, index) => new
            {
                span_index = index,
                observation_id = span.ObservationId,
                transcript_id = span.TranscriptId,
                exact_quote = span.ExactQuote
            }).ToList()
        };
        var source = new
        {
            relationship_sources = sources.Select(item => new
            {
                source_proposal_token = item.SourceProposalToken,
                evidence = PairSide(
                    item.NewObservationIds,
                    IncidentBatchRelationshipEvidenceCatalog.ForObservationIds(
                        bundle,
                        evidenceCatalog,
                        item.NewObservationIds))
            }).ToList(),
            candidate_events = candidates.Select(item => new
            {
                candidate_token = item.CandidateToken,
                evidence = PairSide(
                    item.ObservationIds,
                    IncidentBatchRelationshipEvidenceCatalog.ForObservationIds(
                        bundle,
                        evidenceCatalog,
                        item.ObservationIds))
            }).ToList(),
            eligible_relationship_pairs = eligiblePairs.Select(pair => new
            {
                relationship_pair_token = pair.PairToken,
                source_proposal_token = pair.SourceProposalToken,
                candidate_token = pair.CandidateToken
            }).ToList()
        };
        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Search for a small number of evidence-established relationships between application-owned relationship sources and supplied candidates using only their transcripts. Most pairs are unrelated; returning an empty relationships array is a correct and expected result.");
        user.AppendLine("Relationship sources include both accepted constructed groups and unresolved singleton observations that the constructor did not promote. They are immutable evidence boundaries. Do not rewrite, split, combine, discard, or add facts to them. A relationship may attach an unresolved singleton without asserting that it was independently a complete event.");
        user.AppendLine("Some candidates are earlier accepted groups from this same batch. Evaluate only the application-issued eligible_relationship_pairs. Return only the opaque relationship_pair_token for an eligible pair and return each pair at most once. The application resolves that token back to its source and candidate. This ordered boundary permits later observations to connect to earlier accepted groups without cycles; self, reverse, and other unlisted pairings cannot be expressed.");
        user.AppendLine("Return confirmed_membership only when exact evidence from both sides directly establishes one unfolding real-world event. Return at most one confirmed membership for each constructed group.");
        user.AppendLine("A confirmed_membership must use uncertainty 0 and empty alternative_interpretations and unresolved_questions. If either side contains a material discrepancy, counterinterpretation, or unresolved question, return provisional_association or omit the pair; never confirm it.");
        user.AppendLine("Return provisional_association only when exact evidence from both sides already establishes a specific operational connection: one side continues, answers, updates, acts on, or explicitly refers to a concrete subject, location, vehicle, identifier, circumstance, or request from the other side, but meaningful uncertainty remains about the nature or extent of that connection. Several provisional associations may connect one group to several candidates. A provisional association never merges membership.");
        user.AppendLine("Uncertainty about an already evidenced connection is eligible for provisional_association. Uncertainty about whether any connection exists is not. If the best remaining question is simply whether two nearby, similar, or concurrent transmissions might be related, omit the pair.");
        user.AppendLine("Do not return a provisional association for broad topical similarity, shared words or phonetic fragments, generic dispatch language, a shared responder, the same street or facility without a shared incident fact, different incidents with superficial resemblance, or a pair whose best explanation is that it is unrelated. Exact citations prove what each transcript says; they do not by themselves prove a connection.");
        user.AppendLine("Do not invent local geography. Unless the supplied transcripts establish it, do not assume two streets intersect, two addresses are nearby, or two named places share an operational area.");
        user.AppendLine("Treat explicit incompatible subjects or circumstances as evidence for separate incidents unless the transcripts themselves explain the difference as multiple subjects, an update, or an ASR ambiguity. A shared unit, broad location, or close time does not overcome a materially different patient, vehicle, address, or incident circumstance.");
        user.AppendLine($"Return at most {IncidentBatchRelationshipContract.MaximumReturnedRelationships} relationships total and at most {IncidentBatchRelationshipContract.MaximumRelationshipsPerSource} for one constructed group. Choose the strongest specific relationships and omit weaker pairs rather than producing an oversized response.");
        user.AppendLine("Omit unsupported pairs. Timing, retrieval rank, radio metadata, generic similarity, and shared event type do not prove a relationship. If the relationship statement would need words such as potentially, possibly, merely, or unrelated because no concrete cross-reference exists, omit the pair.");
        user.AppendLine("Each returned relationship must select source_evidence_span_indices and candidate_evidence_span_indices shown inside that exact eligible pair. Span indices are local to each side of each pair: source index 0 means only that pair's source span 0, and candidate index 0 means only that pair's candidate span 0. The application resolves indices after resolving the pair token, so an index cannot cite another pair. Never generate, copy, edit, or paraphrase quote text.");
        user.AppendLine("Copy relationship_pair_token exactly and return only listed non-negative span indices.");
        user.AppendLine();
        user.AppendLine("Source bundle:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));
        return new IncidentBatchRelationshipPromptPayload(
            "You evaluate typed relationships between immutable source-grounded groups and unresolved singleton observations. You may attach evidence-cited relationships but cannot construct or rewrite events. Application code resolves pair-relative evidence, validates citation boundaries, and owns all state transitions.",
            user.ToString(),
            ResponseFormat(bundle, sources, candidates, eligiblePairs, evidenceCatalog));
    }

    private static object ResponseFormat(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchRelationshipSource> sources,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IReadOnlyList<IncidentBatchRelationshipPair> eligiblePairs,
        IReadOnlyList<IncidentBatchConfirmationEvidenceSpan> evidenceCatalog)
    {
        var sourceMap = sources.ToDictionary(item => item.SourceProposalToken, StringComparer.Ordinal);
        var candidateMap = candidates.ToDictionary(item => item.CandidateToken, StringComparer.Ordinal);
        var sourceMaximumSpanIndex = eligiblePairs
            .Select(pair => IncidentBatchRelationshipEvidenceCatalog.ForObservationIds(
                bundle,
                evidenceCatalog,
                sourceMap[pair.SourceProposalToken].NewObservationIds).Count - 1)
            .DefaultIfEmpty(0)
            .Max();
        var candidateMaximumSpanIndex = eligiblePairs
            .Select(pair => IncidentBatchRelationshipEvidenceCatalog.ForObservationIds(
                bundle,
                evidenceCatalog,
                candidateMap[pair.CandidateToken].ObservationIds).Count - 1)
            .DefaultIfEmpty(0)
            .Max();
        object EvidenceSpanIndexArray(int maximumSpanIndex) => new
        {
            type = "array",
            minItems = 1,
            maxItems = IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide,
            uniqueItems = true,
            items = new { type = "integer", minimum = 0, maximum = Math.Max(0, maximumSpanIndex) }
        };
        object Strings(int maximum) => new { type = "array", maxItems = maximum, items = new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength } };
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_batch_relationship_v9_all_observation_pair_relative_evidence",
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
                            maxItems = IncidentBatchRelationshipContract.MaximumReturnedRelationships,
                            uniqueItems = true,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    relationship_pair_token = new
                                    {
                                        type = "string",
                                        @enum = eligiblePairs.Select(item => item.PairToken).ToArray()
                                    },
                                    disposition = new { type = "string", @enum = new[] { "confirmed_membership", "provisional_association" } },
                                    relationship_statement = new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength },
                                    uncertainty = new { type = "number", minimum = 0, maximum = 1 },
                                    source_evidence_span_indices = EvidenceSpanIndexArray(sourceMaximumSpanIndex),
                                    candidate_evidence_span_indices = EvidenceSpanIndexArray(candidateMaximumSpanIndex),
                                    alternative_interpretations = Strings(IncidentBatchRelationshipContract.MaximumAlternatives),
                                    unresolved_questions = Strings(IncidentBatchRelationshipContract.MaximumUnresolvedQuestions)
                                },
                                required = new[] { "relationship_pair_token", "disposition", "relationship_statement", "uncertainty", "source_evidence_span_indices", "candidate_evidence_span_indices", "alternative_interpretations", "unresolved_questions" }
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
        var requestStarted = Stopwatch.GetTimestamp();
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
            var evidenceCatalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle);
            var eligiblePairs = IncidentBatchRelationshipContract.EligiblePairs(sources, candidates)
                .ToDictionary(item => item.PairToken, StringComparer.Ordinal);
            var sourceMap = sources.ToDictionary(item => item.SourceProposalToken, StringComparer.Ordinal);
            var candidateMap = candidates.ToDictionary(item => item.CandidateToken, StringComparer.Ordinal);
            var relationships = parsed.Relationships.Select(item =>
            {
                if (!eligiblePairs.TryGetValue(item.RelationshipPairToken, out var pair))
                    throw new InvalidDataException(
                        $"Unknown batch relationship pair token '{item.RelationshipPairToken}'.");
                var sourceSpans = IncidentBatchRelationshipEvidenceCatalog.ForObservationIds(
                    bundle,
                    evidenceCatalog,
                    sourceMap[pair.SourceProposalToken].NewObservationIds);
                var candidateSpans = IncidentBatchRelationshipEvidenceCatalog.ForObservationIds(
                    bundle,
                    evidenceCatalog,
                    candidateMap[pair.CandidateToken].ObservationIds);
                return new IncidentBatchRelationship(
                    pair.SourceProposalToken,
                    pair.CandidateToken,
                    item.Disposition switch
                    {
                        "confirmed_membership" => IncidentBatchRelationshipDisposition.ConfirmedMembership,
                        "provisional_association" => IncidentBatchRelationshipDisposition.ProvisionalAssociation,
                        _ => throw new InvalidDataException($"Unsupported batch relationship disposition '{item.Disposition}'.")
                    },
                    item.RelationshipStatement,
                    item.Uncertainty,
                    IncidentBatchRelationshipEvidenceCatalog.ResolvePairRelativeIndices(
                        item.SourceEvidenceSpanIndices,
                        sourceSpans,
                        "source"),
                    IncidentBatchRelationshipEvidenceCatalog.ResolvePairRelativeIndices(
                        item.CandidateEvidenceSpanIndices,
                        candidateSpans,
                        "candidate"),
                    item.AlternativeInterpretations,
                    item.UnresolvedQuestions);
            }).ToList();
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, ElapsedMilliseconds(requestStarted), ct);
            return new IncidentBatchRelationshipProposal(
                $"model:incident-batch-relationship:{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                responseModel,
                IncidentBatchRelationshipPrompt.PromptIdentity,
                relationships);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, false, ex.GetBaseException().Message, ElapsedMilliseconds(requestStarted), CancellationToken.None);
            throw;
        }
    }

    private async Task RecordUsageAsync(string responseText, string endpoint, string requestedModel, int payloadChars, bool success, string error, long durationMilliseconds, CancellationToken ct)
    {
        var usage = ReadUsage(responseText);
        try
        {
            await _database.AddLmUsageAsync(new TokenUsageEntryDto(
                0, DateTime.UtcNow, $"incident batch relationship shadow:{_runId}", "chat.completions", success, error,
                endpoint, requestedModel, usage.ResponseModel, usage.FinishReason, payloadChars, payloadChars,
                usage.PromptTokens, usage.CompletionTokens, usage.TotalTokens, durationMilliseconds), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not record incident batch relationship model usage");
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
    private static long ElapsedMilliseconds(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);

    private sealed record RelationshipResponse([property: JsonPropertyName("relationships")] IReadOnlyList<RelationshipItemResponse> Relationships);
    private sealed record RelationshipItemResponse(
        [property: JsonPropertyName("relationship_pair_token")] string RelationshipPairToken,
        [property: JsonPropertyName("disposition")] string Disposition,
        [property: JsonPropertyName("relationship_statement")] string RelationshipStatement,
        [property: JsonPropertyName("uncertainty")] double Uncertainty,
        [property: JsonPropertyName("source_evidence_span_indices")] IReadOnlyList<int> SourceEvidenceSpanIndices,
        [property: JsonPropertyName("candidate_evidence_span_indices")] IReadOnlyList<int> CandidateEvidenceSpanIndices,
        [property: JsonPropertyName("alternative_interpretations")] IReadOnlyList<string> AlternativeInterpretations,
        [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);
}
