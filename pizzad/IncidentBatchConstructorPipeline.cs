using System.Diagnostics;

namespace pizzad;

public enum IncidentBatchEventDisposition
{
    NewEvent,
    ProvisionalEvent,
    ConfirmedMembership,
    ProvisionalAssociation
}

public sealed record IncidentBatchCandidate(
    string CandidateToken,
    string ProjectionEventId,
    IReadOnlyList<string> ObservationIds);

public sealed record IncidentBatchEventProposal(
    string ProposalToken,
    IncidentBatchEventDisposition Disposition,
    string CandidateToken,
    IReadOnlyList<string> NewObservationIds,
    string Title,
    string Summary,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateTranscriptCitation> NewObservationEvidence,
    IReadOnlyList<IncidentEventStateTranscriptCitation> CandidateEvidence,
    IReadOnlyList<string> AlternativeInterpretations,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentBatchProposal(
    string ProposalId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentBatchEventProposal> Events);

public sealed record IncidentBatchProjectionEvent(
    string ProjectionEventId,
    IReadOnlyList<string> ObservationIds,
    string Title,
    string Summary,
    bool OperatorVisible,
    bool OperatorReview,
    IReadOnlyList<string> SourceLedgerEntryIds);

public sealed record IncidentBatchProjectedAssociation(
    string AssociationId,
    string SourceProjectionEventId,
    string CandidateProjectionEventId,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateTranscriptCitation> NewObservationEvidence,
    IReadOnlyList<IncidentEventStateTranscriptCitation> CandidateEvidence,
    IReadOnlyList<string> AlternativeInterpretations,
    IReadOnlyList<string> UnresolvedQuestions,
    string SourceProposalId,
    string SourceLedgerEntryId);

public sealed record IncidentBatchProjection(
    string RunId,
    string ProjectionId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> LedgerEntryIds,
    IReadOnlyList<IncidentBatchProjectionEvent> Events,
    IReadOnlyList<IncidentBatchProjectedAssociation> ProvisionalAssociations);

public sealed record IncidentBatchSingletonIdentity(string ObservationId, string ProjectionEventId);

public sealed record IncidentBatchExecutionContext(
    string SoftwareVersion,
    string ConfigurationIdentity,
    long ProposerDurationMilliseconds,
    string ProposerError,
    long RetrievalDurationMilliseconds = 0);

public sealed record IncidentBatchLedgerEntry(
    string RunId,
    string LedgerEntryId,
    DateTimeOffset RecordedAtUtc,
    IncidentEventStateObservationBundle Bundle,
    IReadOnlyList<string> NewObservationIds,
    IReadOnlyList<IncidentBatchSingletonIdentity> SingletonEvents,
    IReadOnlyList<IncidentBatchCandidate> Candidates,
    IncidentBatchProposal Proposal,
    IReadOnlyList<string> ProposalValidationErrors,
    IncidentBatchExecutionContext Execution,
    IncidentBatchRelationshipProposal? RelationshipProposal = null,
    IReadOnlyList<string>? RelationshipProposalValidationErrors = null,
    IncidentBatchRelationshipExecutionContext? RelationshipExecution = null,
    IncidentBatchConfirmationProposal? ConfirmationProposal = null,
    IReadOnlyList<string>? ConfirmationProposalValidationErrors = null,
    IncidentBatchConfirmationExecutionContext? ConfirmationExecution = null);

public sealed record IncidentBatchStoredLedgerEntry(long Sequence, string ContentHash, IncidentBatchLedgerEntry Entry);
public sealed record IncidentBatchStoredProjection(long Sequence, string ContentHash, IncidentBatchProjection Projection);
public sealed record IncidentBatchRunResult(IncidentBatchStoredLedgerEntry LedgerEntry, IncidentBatchStoredProjection Projection);

public sealed record IncidentBatchRunRequest(
    string RunId,
    string LedgerEntryId,
    string ProjectionId,
    IReadOnlyList<IncidentBatchSingletonIdentity> SingletonEvents,
    string SoftwareVersion,
    string ConfigurationIdentity,
    long RetrievalDurationMilliseconds = 0);

public interface IIncidentBatchProposer
{
    Task<IncidentBatchProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        CancellationToken ct);
}

public interface IIncidentBatchStore
{
    Task<IncidentBatchRunResult> AppendIncidentBatchRunAsync(
        IncidentBatchLedgerEntry entry,
        IncidentBatchProjection projection,
        CancellationToken ct);
}

public static class IncidentBatchContract
{
    public const int MaximumNewObservationCount = 24;
    public const int MaximumCandidateCount = 8;
    public const int MaximumObservationsPerCandidate = 12;
    public const string PerEventAcceptanceConfigurationToken = "acceptance=per-event-v1";
    public const string PerCitationAcceptanceConfigurationToken = "acceptance=per-citation-v1";
    public const string EvidenceSummaryProjectionConfigurationToken = "projection=evidence-narrative-v2";
    public const string OldestUnseenCursorConfigurationToken = "cursor=oldest-unseen-v1;cadence=fixed-start-v1";
    public const string CorroboratedVisibilityConfigurationToken = "visibility=confirmed-membership-v2";
    public const string ObservationIsolatedOwnershipConfigurationToken = "source-ownership=one-observation-v1";

    public static bool IsOperatorVisibleNewEvent(IncidentBatchEventProposal _) => false;

    public static bool IsOperatorReviewEvent(IncidentBatchEventProposal proposal) =>
        proposal.Disposition == IncidentBatchEventDisposition.ProvisionalEvent ||
        proposal.Disposition == IncidentBatchEventDisposition.NewEvent;

    public static IncidentEventStateContractValidationResult ValidateInput(
        IncidentEventStateObservationBundle bundle,
        IncidentBatchProjection? priorProjection,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates)
    {
        var errors = IncidentEventStateContractValidator.ValidateBundle(bundle).Errors.ToList();
        RequireUnique(newObservationIds, "new observation id", errors);
        if (newObservationIds.Count == 0 || newObservationIds.Count > MaximumNewObservationCount)
            errors.Add($"batch must contain between 1 and {MaximumNewObservationCount} new observations");
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        foreach (var observationId in newObservationIds)
            if (!observations.ContainsKey(observationId))
                errors.Add($"new observation '{observationId}' is absent from the source bundle");

        if (candidates.Count > MaximumCandidateCount)
            errors.Add($"batch contains more than {MaximumCandidateCount} candidates");
        RequireUnique(candidates.Select(item => item.CandidateToken), "candidate token", errors);
        RequireUnique(candidates.Select(item => item.ProjectionEventId), "candidate projection event id", errors);
        var priorEvents = (priorProjection?.Events ?? []).ToDictionary(item => item.ProjectionEventId, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            RequireValue(candidate.CandidateToken, "candidate token", errors);
            RequireValue(candidate.ProjectionEventId, "candidate projection event id", errors);
            RequireUnique(candidate.ObservationIds, $"observation id in candidate '{candidate.CandidateToken}'", errors);
            if (candidate.ObservationIds.Count == 0 || candidate.ObservationIds.Count > MaximumObservationsPerCandidate)
                errors.Add($"candidate '{candidate.CandidateToken}' has an invalid observation count");
            foreach (var observationId in candidate.ObservationIds)
            {
                if (!observations.ContainsKey(observationId))
                    errors.Add($"candidate '{candidate.CandidateToken}' references observation '{observationId}' outside the source bundle");
                if (newObservationIds.Contains(observationId, StringComparer.Ordinal))
                    errors.Add($"candidate '{candidate.CandidateToken}' contains new observation '{observationId}'");
            }
            if (priorProjection is null || !priorEvents.TryGetValue(candidate.ProjectionEventId, out var priorEvent))
                errors.Add($"candidate '{candidate.CandidateToken}' has no matching prior projected event");
            else if (!candidate.ObservationIds.ToHashSet(StringComparer.Ordinal).IsSubsetOf(priorEvent.ObservationIds))
                errors.Add($"candidate '{candidate.CandidateToken}' includes observations outside projected event '{candidate.ProjectionEventId}'");
        }
        if (priorProjection is not null)
            errors.AddRange(ValidateProjection(priorProjection).Errors.Select(error => $"prior projection: {error}"));
        return Result(errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IncidentBatchProposal proposal,
        string configurationIdentity = "")
    {
        var errors = new List<string>();
        RequireValue(proposal.ProposalId, "batch proposal id", errors);
        RequireValue(proposal.ModelIdentity, "batch proposal model identity", errors);
        RequireValue(proposal.PromptIdentity, "batch proposal prompt identity", errors);
        if (proposal.GeneratedAtUtc == default)
            errors.Add("batch proposal generated timestamp is required");
        RequireUnique(proposal.Events.Select(item => item.ProposalToken), "event proposal token", errors);
        var proposedOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        var confirmedCandidateOwners = new HashSet<string>(StringComparer.Ordinal);
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var candidateMap = candidates.ToDictionary(item => item.CandidateToken, StringComparer.Ordinal);

        var derivesDisplayTextFromEvidence = string.Equals(
            proposal.PromptIdentity,
            IncidentBatchPrompt.AsynchronousProvisionalPromptIdentity,
            StringComparison.Ordinal) || string.Equals(
            proposal.PromptIdentity,
            IncidentBatchPrompt.ObservationIsolatedProvisionalPromptIdentity,
            StringComparison.Ordinal);
        foreach (var item in proposal.Events)
        {
            RequireValue(item.ProposalToken, "event proposal token", errors);
            if (!derivesDisplayTextFromEvidence)
            {
                RequireValue(item.Title, $"title for event proposal '{item.ProposalToken}'", errors);
                RequireValue(item.Summary, $"summary for event proposal '{item.ProposalToken}'", errors);
            }
            RequireValue(item.RelationshipStatement, $"relationship statement for event proposal '{item.ProposalToken}'", errors);
            RequireUnique(item.NewObservationIds, $"new observation id in event proposal '{item.ProposalToken}'", errors);
            if (item.NewObservationIds.Count == 0)
                errors.Add($"event proposal '{item.ProposalToken}' contains no new observations");
            if (UsesObservationIsolatedOwnership(configurationIdentity) && item.NewObservationIds.Count != 1)
                errors.Add($"observation-isolated event proposal '{item.ProposalToken}' must contain exactly one new observation");
            foreach (var observationId in item.NewObservationIds)
            {
                if (!newObservationIds.Contains(observationId, StringComparer.Ordinal))
                    errors.Add($"event proposal '{item.ProposalToken}' references non-new observation '{observationId}'");
                else if (!proposedOwners.TryAdd(observationId, item.ProposalToken))
                    errors.Add($"new observation '{observationId}' appears in more than one event proposal");
            }
            if (double.IsNaN(item.Uncertainty) || double.IsInfinity(item.Uncertainty) || item.Uncertainty is < 0 or > 1)
                errors.Add($"uncertainty for event proposal '{item.ProposalToken}' must be between 0 and 1");
            if (item.Disposition is IncidentBatchEventDisposition.NewEvent or IncidentBatchEventDisposition.ProvisionalEvent)
            {
                if (!string.IsNullOrWhiteSpace(item.CandidateToken))
                    errors.Add($"candidate-free event proposal '{item.ProposalToken}' cannot reference a candidate");
                if (item.CandidateEvidence.Count > 0)
                    errors.Add($"candidate-free event proposal '{item.ProposalToken}' cannot cite candidate evidence");
            }
            else if (!candidateMap.TryGetValue(item.CandidateToken, out var candidate))
            {
                errors.Add($"event proposal '{item.ProposalToken}' references unknown candidate '{item.CandidateToken}'");
            }
            else
            {
                ValidateCitations(bundle, candidate.ObservationIds, item.CandidateEvidence, $"candidate evidence for event proposal '{item.ProposalToken}'", true, errors);
                if (item.Disposition == IncidentBatchEventDisposition.ConfirmedMembership && !confirmedCandidateOwners.Add(item.CandidateToken))
                    errors.Add($"candidate '{item.CandidateToken}' receives more than one confirmed membership in the batch");
            }
            ValidateCitations(bundle, item.NewObservationIds, item.NewObservationEvidence, $"new-observation evidence for event proposal '{item.ProposalToken}'", true, errors);
            foreach (var observationId in item.NewObservationIds.Where(observations.ContainsKey))
            {
                var transcriptIds = observations[observationId].Transcripts.Select(transcript => transcript.TranscriptId).ToHashSet(StringComparer.Ordinal);
                if (!item.NewObservationEvidence.Any(citation => transcriptIds.Contains(citation.TranscriptId)))
                    errors.Add($"event proposal '{item.ProposalToken}' does not cite new observation '{observationId}'");
            }
            ValidateStrings(item.AlternativeInterpretations, "alternative interpretation", errors);
            ValidateStrings(item.UnresolvedQuestions, "unresolved question", errors);
        }
        return Result(errors);
    }

    public static IncidentEventStateContractValidationResult ValidateLedgerEntry(IncidentBatchLedgerEntry entry)
    {
        var errors = IncidentEventStateContractValidator.ValidateBundle(entry.Bundle).Errors.ToList();
        RequireValue(entry.RunId, "batch run id", errors);
        RequireValue(entry.LedgerEntryId, "batch ledger entry id", errors);
        if (entry.RecordedAtUtc == default)
            errors.Add("batch ledger recorded timestamp is required");
        RequireUnique(entry.SingletonEvents.Select(item => item.ObservationId), "singleton observation id", errors);
        RequireUnique(entry.SingletonEvents.Select(item => item.ProjectionEventId), "singleton projection event id", errors);
        if (!entry.NewObservationIds.Order(StringComparer.Ordinal).SequenceEqual(entry.SingletonEvents.Select(item => item.ObservationId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            errors.Add("singleton identities do not exactly cover the batch's new observations");
        foreach (var singleton in entry.SingletonEvents)
        {
            RequireValue(singleton.ObservationId, "singleton observation id", errors);
            RequireValue(singleton.ProjectionEventId, "singleton projection event id", errors);
        }
        RequireValue(entry.Execution.SoftwareVersion, "batch execution software version", errors);
        RequireValue(entry.Execution.ConfigurationIdentity, "batch execution configuration identity", errors);
        if (entry.Execution.ProposerDurationMilliseconds < 0)
            errors.Add("batch proposer duration cannot be negative");
        if (IncidentBatchExecutionArchitecture.UsesSourceOnlyAsynchronousIntake(entry.Execution.ConfigurationIdentity) &&
            (entry.RelationshipProposal is not null || entry.ConfirmationProposal is not null))
        {
            errors.Add("asynchronous provisional intake cannot contain synchronous relationship or confirmation output");
        }
        var constructionCandidates = entry.RelationshipProposal is null ? entry.Candidates : [];
        var proposalValidation = ValidateProposal(
            entry.Bundle,
            entry.NewObservationIds,
            constructionCandidates,
            entry.Proposal,
            entry.Execution.ConfigurationIdentity);
        if (!entry.ProposalValidationErrors.SequenceEqual(proposalValidation.Errors, StringComparer.Ordinal))
            errors.Add("batch ledger proposal validation errors do not match deterministic validation");
        if (entry.RelationshipProposal is not null)
        {
            var sources = AcceptedEvents(entry)
                .Select(item => new IncidentBatchRelationshipSource(item.ProposalToken, item.NewObservationIds))
                .ToList();
            var relationshipValidation = IncidentBatchRelationshipContract.ValidateProposal(
                entry.Bundle,
                sources,
                entry.Candidates,
                entry.RelationshipProposal);
            if (!(entry.RelationshipProposalValidationErrors ?? []).SequenceEqual(relationshipValidation.Errors, StringComparer.Ordinal))
                errors.Add("batch ledger relationship validation errors do not match deterministic validation");
            if (entry.RelationshipExecution is null)
                errors.Add("source-isolated relationship proposal requires execution provenance");
            else if (entry.RelationshipExecution.ProposerDurationMilliseconds < 0)
                errors.Add("relationship proposer duration cannot be negative");

            var acceptedRelationships = IncidentBatchRelationshipContract.AcceptedRelationships(
                entry.Bundle,
                sources,
                entry.Candidates,
                entry.RelationshipProposal);
            var relationshipsToVerify = IncidentBatchConfirmationContract.VerifiesAllRelationships(entry.Execution.ConfigurationIdentity)
                ? acceptedRelationships
                : acceptedRelationships.Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership).ToList();
            if (entry.ConfirmationProposal is not null)
            {
                var confirmationValidation = IncidentBatchConfirmationContract.ValidateProposal(
                    entry.Bundle,
                    sources,
                    entry.Candidates,
                    relationshipsToVerify,
                    entry.ConfirmationProposal);
                if (!(entry.ConfirmationProposalValidationErrors ?? []).SequenceEqual(confirmationValidation.Errors, StringComparer.Ordinal))
                    errors.Add("batch ledger confirmation validation errors do not match deterministic validation");
                if (entry.ConfirmationExecution is null)
                    errors.Add("confirmation proposal requires execution provenance");
                else if (entry.ConfirmationExecution.VerifierDurationMilliseconds < 0)
                    errors.Add("confirmation verifier duration cannot be negative");
            }
            else if (IncidentBatchConfirmationContract.UsesIndependentVerifier(entry.Execution.ConfigurationIdentity))
            {
                errors.Add("independent confirmation configuration requires a confirmation proposal");
            }
        }
        return Result(errors);
    }

    public static IReadOnlyList<IncidentBatchEventProposal> AcceptedEvents(IncidentBatchLedgerEntry entry)
    {
        var constructionCandidates = entry.RelationshipProposal is null ? entry.Candidates : [];
        return AcceptedEvents(
            entry.Bundle,
            entry.NewObservationIds,
            constructionCandidates,
            entry.Proposal,
            entry.ProposalValidationErrors,
            entry.Execution.ConfigurationIdentity);
    }

    public static IReadOnlyList<IncidentBatchEventProposal> AcceptedEvents(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IncidentBatchProposal proposal,
        IReadOnlyList<string> proposalValidationErrors,
        string configurationIdentity)
    {
        if (!UsesPerEventAcceptance(configurationIdentity))
            return proposalValidationErrors.Count == 0 ? proposal.Events : [];

        var headerValidation = ValidateProposal(
            bundle,
            newObservationIds,
            candidates,
            proposal with { Events = [] },
            configurationIdentity);
        if (!headerValidation.IsValid)
            return [];

        var duplicateProposalTokens = Duplicates(proposal.Events.Select(item => item.ProposalToken));
        var duplicateObservationIds = Duplicates(proposal.Events.SelectMany(item => item.NewObservationIds));
        var duplicateConfirmedCandidates = Duplicates(proposal.Events
            .Where(item => item.Disposition == IncidentBatchEventDisposition.ConfirmedMembership)
            .Select(item => item.CandidateToken));
        var events = UsesPerCitationAcceptance(configurationIdentity)
            ? proposal.Events.Select(item => RetainExactCitations(bundle, candidates, item)).ToList()
            : proposal.Events;
        return events
            .Where(item => !duplicateProposalTokens.Contains(item.ProposalToken ?? string.Empty))
            .Where(item => !item.NewObservationIds.Any(duplicateObservationIds.Contains))
            .Where(item => item.Disposition != IncidentBatchEventDisposition.ConfirmedMembership || !duplicateConfirmedCandidates.Contains(item.CandidateToken ?? string.Empty))
            .Where(item => ValidateProposal(
                bundle,
                newObservationIds,
                candidates,
                proposal with { Events = [item] },
                configurationIdentity).IsValid)
            .ToList();
    }

    private static IncidentBatchEventProposal RetainExactCitations(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IncidentBatchEventProposal proposal)
    {
        var newEvidence = proposal.NewObservationEvidence
            .Where(citation => IsExactCitation(bundle, proposal.NewObservationIds, citation))
            .ToList();
        var candidateEvidence = proposal.CandidateEvidence;
        if (proposal.Disposition is IncidentBatchEventDisposition.ConfirmedMembership or IncidentBatchEventDisposition.ProvisionalAssociation)
        {
            var candidate = candidates.SingleOrDefault(item => item.CandidateToken == proposal.CandidateToken);
            if (candidate is not null)
                candidateEvidence = proposal.CandidateEvidence
                    .Where(citation => IsExactCitation(bundle, candidate.ObservationIds, citation))
                    .ToList();
        }
        return proposal with
        {
            NewObservationEvidence = newEvidence,
            CandidateEvidence = candidateEvidence
        };
    }

    private static bool IsExactCitation(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> allowedObservationIds,
        IncidentEventStateTranscriptCitation citation)
    {
        if (string.IsNullOrWhiteSpace(citation.TranscriptId) || string.IsNullOrWhiteSpace(citation.ExactQuote))
            return false;
        var matches = bundle.Observations
            .Where(observation => allowedObservationIds.Contains(observation.ObservationId, StringComparer.Ordinal))
            .SelectMany(observation => observation.Transcripts)
            .Where(transcript => transcript.TranscriptId == citation.TranscriptId)
            .ToList();
        return matches.Count == 1 && matches[0].Text.Contains(citation.ExactQuote, StringComparison.Ordinal);
    }

    public static IncidentEventStateContractValidationResult ValidateProjection(IncidentBatchProjection projection)
    {
        var errors = new List<string>();
        RequireValue(projection.RunId, "batch projection run id", errors);
        RequireValue(projection.ProjectionId, "batch projection id", errors);
        if (projection.GeneratedAtUtc == default)
            errors.Add("batch projection generated timestamp is required");
        RequireUnique(projection.LedgerEntryIds, "batch projection ledger entry id", errors);
        RequireUnique(projection.Events.Select(item => item.ProjectionEventId), "batch projection event id", errors);
        RequireUnique(projection.ProvisionalAssociations.Select(item => item.AssociationId), "batch provisional association id", errors);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var eventIds = projection.Events.Select(item => item.ProjectionEventId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in projection.Events)
        {
            RequireValue(item.ProjectionEventId, "batch projection event id", errors);
            RequireUnique(item.ObservationIds, $"observation id in projected event '{item.ProjectionEventId}'", errors);
            if (item.ObservationIds.Count == 0)
                errors.Add($"projected event '{item.ProjectionEventId}' contains no observations");
            if (item.OperatorVisible && (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Summary)))
                errors.Add($"operator-visible projected event '{item.ProjectionEventId}' lacks a title or summary");
            if (item.OperatorReview && (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.Summary)))
                errors.Add($"operator-review projected event '{item.ProjectionEventId}' lacks a title or summary");
            if (item.OperatorVisible && item.OperatorReview)
                errors.Add($"projected event '{item.ProjectionEventId}' cannot be both operator-visible and operator-review");
            foreach (var observationId in item.ObservationIds)
                if (!owners.TryAdd(observationId, item.ProjectionEventId))
                    errors.Add($"observation '{observationId}' belongs to more than one projected event");
            foreach (var ledgerId in item.SourceLedgerEntryIds)
                if (!projection.LedgerEntryIds.Contains(ledgerId, StringComparer.Ordinal))
                    errors.Add($"projected event '{item.ProjectionEventId}' references unknown ledger entry '{ledgerId}'");
        }
        foreach (var link in projection.ProvisionalAssociations)
        {
            RequireValue(link.AssociationId, "batch provisional association id", errors);
            RequireValue(link.RelationshipStatement, $"relationship statement in provisional association '{link.AssociationId}'", errors);
            if (!eventIds.Contains(link.SourceProjectionEventId) || !eventIds.Contains(link.CandidateProjectionEventId))
                errors.Add($"provisional association '{link.AssociationId}' references an unknown event");
            if (link.SourceProjectionEventId == link.CandidateProjectionEventId)
                errors.Add($"provisional association '{link.AssociationId}' connects an event to itself");
            if (link.NewObservationEvidence.Count == 0 || link.CandidateEvidence.Count == 0)
                errors.Add($"provisional association '{link.AssociationId}' lacks evidence from both sides");
            if (!projection.LedgerEntryIds.Contains(link.SourceLedgerEntryId, StringComparer.Ordinal))
                errors.Add($"provisional association '{link.AssociationId}' references an unknown ledger entry");
        }
        return Result(errors);
    }

    private static void ValidateCitations(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> allowedObservationIds,
        IReadOnlyList<IncidentEventStateTranscriptCitation> citations,
        string owner,
        bool required,
        List<string> errors)
    {
        if (required && citations.Count == 0)
            errors.Add($"{owner} must include at least one exact transcript citation");
        RequireUnique(citations.Select(item => $"{item.TranscriptId}\u001f{item.ExactQuote}"), $"transcript citation in {owner}", errors);
        var transcripts = bundle.Observations
            .Where(observation => allowedObservationIds.Contains(observation.ObservationId, StringComparer.Ordinal))
            .SelectMany(observation => observation.Transcripts)
            .GroupBy(transcript => transcript.TranscriptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        foreach (var citation in citations)
        {
            RequireValue(citation.TranscriptId, $"transcript id in {owner}", errors);
            RequireValue(citation.ExactQuote, $"exact quote in {owner}", errors);
            if (!transcripts.TryGetValue(citation.TranscriptId, out var matches) || matches.Count != 1)
                errors.Add($"{owner} cites unknown or ambiguous transcript '{citation.TranscriptId}'");
            else if (!matches[0].Text.Contains(citation.ExactQuote, StringComparison.Ordinal))
                errors.Add($"{owner} quote does not occur exactly in transcript '{citation.TranscriptId}'");
        }
    }

    private static void ValidateStrings(IReadOnlyList<string> values, string owner, List<string> errors)
    {
        RequireUnique(values, owner, errors);
        foreach (var value in values)
            RequireValue(value, owner, errors);
    }

    private static void RequireUnique(IEnumerable<string> values, string owner, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!seen.Add(value ?? string.Empty))
                errors.Add($"duplicate {owner} '{value}'");
    }

    private static HashSet<string> Duplicates(IEnumerable<string> values) => values
        .Select(value => value ?? string.Empty)
        .GroupBy(value => value, StringComparer.Ordinal)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .ToHashSet(StringComparer.Ordinal);

    private static bool UsesPerEventAcceptance(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(PerEventAcceptanceConfigurationToken, StringComparer.Ordinal);

    private static bool UsesPerCitationAcceptance(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(PerCitationAcceptanceConfigurationToken, StringComparer.Ordinal);

    public static bool UsesObservationIsolatedOwnership(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(ObservationIsolatedOwnershipConfigurationToken, StringComparer.Ordinal);

    private static void RequireValue(string? value, string owner, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{owner} is required");
    }

    private static IncidentEventStateContractValidationResult Result(IReadOnlyList<string> errors) => new(errors.Count == 0, errors);
}

public static class IncidentBatchProjector
{
    public static IncidentBatchProjection Apply(
        IncidentBatchProjection? priorProjection,
        IncidentBatchLedgerEntry entry,
        string projectionId,
        DateTimeOffset generatedAtUtc)
    {
        var entryValidation = IncidentBatchContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new ArgumentException(string.Join("; ", entryValidation.Errors), nameof(entry));
        var events = (priorProjection?.Events ?? []).Select(item => item with
        {
            ObservationIds = item.ObservationIds.ToList(),
            SourceLedgerEntryIds = item.SourceLedgerEntryIds.ToList()
        }).ToList();
        if (events.SelectMany(item => item.ObservationIds).Intersect(entry.NewObservationIds, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("batch contains an observation already owned by the prior projection");
        var asynchronousProvisional = IncidentBatchExecutionArchitecture.UsesAsynchronousProvisionalVerification(
            entry.Execution.ConfigurationIdentity);
        events.AddRange(entry.SingletonEvents.Select(item => new IncidentBatchProjectionEvent(
            item.ProjectionEventId,
            [item.ObservationId],
            string.Empty,
            string.Empty,
            false,
            false,
            [entry.LedgerEntryId])));

        var links = (priorProjection?.ProvisionalAssociations ?? []).ToList();
        foreach (var proposal in IncidentBatchContract.AcceptedEvents(entry))
        {
            var requiresAsynchronousVerification = asynchronousProvisional &&
                                                   (proposal.Disposition == IncidentBatchEventDisposition.ConfirmedMembership ||
                                                    proposal.Disposition == IncidentBatchEventDisposition.ProvisionalAssociation);
            var evidenceSummary = BuildEvidenceSummary(proposal.NewObservationEvidence);
            var evidenceTitle = BuildEvidenceTitle(evidenceSummary);
            var singletonIds = entry.SingletonEvents
                .Where(item => proposal.NewObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
                .Select(item => item.ProjectionEventId)
                .ToHashSet(StringComparer.Ordinal);
            var sourceEventId = entry.SingletonEvents.First(item => item.ObservationId == proposal.NewObservationIds[0]).ProjectionEventId;
            if (proposal.Disposition == IncidentBatchEventDisposition.ConfirmedMembership && !asynchronousProvisional)
            {
                var candidate = entry.Candidates.Single(item => item.CandidateToken == proposal.CandidateToken);
                sourceEventId = candidate.ProjectionEventId;
                var targetIndex = events.FindIndex(item => item.ProjectionEventId == candidate.ProjectionEventId);
                if (targetIndex < 0)
                    throw new InvalidOperationException($"confirmed candidate event '{candidate.ProjectionEventId}' is absent from the projection");
                var target = events[targetIndex];
                events[targetIndex] = target with
                {
                    ObservationIds = target.ObservationIds.Concat(proposal.NewObservationIds).Distinct(StringComparer.Ordinal).ToList(),
                    Title = string.IsNullOrWhiteSpace(target.Title) ? evidenceTitle : target.Title,
                    Summary = AppendEvidenceSummary(target.Summary, evidenceSummary),
                    OperatorVisible = true,
                    OperatorReview = false,
                    SourceLedgerEntryIds = target.SourceLedgerEntryIds.Append(entry.LedgerEntryId).Distinct(StringComparer.Ordinal).ToList()
                };
                events.RemoveAll(item => singletonIds.Contains(item.ProjectionEventId));
            }
            else
            {
                var sourceIndex = events.FindIndex(item => item.ProjectionEventId == sourceEventId);
                var source = events[sourceIndex];
                events[sourceIndex] = source with
                {
                    ObservationIds = proposal.NewObservationIds.ToList(),
                    Title = evidenceTitle,
                    Summary = evidenceSummary,
                    OperatorVisible = IncidentBatchContract.IsOperatorVisibleNewEvent(proposal),
                    OperatorReview = IncidentBatchContract.IsOperatorReviewEvent(proposal) || requiresAsynchronousVerification,
                    SourceLedgerEntryIds = source.SourceLedgerEntryIds.Append(entry.LedgerEntryId).Distinct(StringComparer.Ordinal).ToList()
                };
                events.RemoveAll(item => item.ProjectionEventId != sourceEventId && singletonIds.Contains(item.ProjectionEventId));
                if (proposal.Disposition == IncidentBatchEventDisposition.ProvisionalAssociation || requiresAsynchronousVerification)
                {
                    var candidate = entry.Candidates.Single(item => item.CandidateToken == proposal.CandidateToken);
                    links.Add(new IncidentBatchProjectedAssociation(
                        $"{entry.LedgerEntryId}:{proposal.ProposalToken}",
                        sourceEventId,
                        candidate.ProjectionEventId,
                        proposal.RelationshipStatement,
                        proposal.Uncertainty,
                        proposal.NewObservationEvidence,
                        proposal.CandidateEvidence,
                        proposal.AlternativeInterpretations,
                        proposal.UnresolvedQuestions,
                        entry.Proposal.ProposalId,
                        entry.LedgerEntryId));
                }
            }
        }

        if (entry.RelationshipProposal is not null)
        {
            var acceptedSourceEvents = IncidentBatchContract.AcceptedEvents(entry);
            var acceptedSources = acceptedSourceEvents
                .Select(item => new IncidentBatchRelationshipSource(item.ProposalToken, item.NewObservationIds))
                .ToList();
            var acceptedRelationships = IncidentBatchRelationshipContract.AcceptedRelationships(entry);
            var sourceEventsByToken = acceptedSourceEvents
                .ToDictionary(item => item.ProposalToken, StringComparer.Ordinal);
            var sourceEventIds = sourceEventsByToken.ToDictionary(
                item => item.Key,
                item => entry.SingletonEvents.First(singleton => singleton.ObservationId == item.Value.NewObservationIds[0]).ProjectionEventId,
                StringComparer.Ordinal);

            foreach (var relationship in acceptedRelationships
                         .Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership))
            {
                var sourceEventId = sourceEventIds[relationship.SourceProposalToken];
                var candidate = entry.Candidates.Single(item => item.CandidateToken == relationship.CandidateToken);
                if (asynchronousProvisional)
                {
                    links.Add(new IncidentBatchProjectedAssociation(
                        $"{entry.LedgerEntryId}:{relationship.SourceProposalToken}:{relationship.CandidateToken}",
                        sourceEventId,
                        candidate.ProjectionEventId,
                        relationship.RelationshipStatement,
                        relationship.Uncertainty,
                        relationship.SourceEvidence,
                        relationship.CandidateEvidence,
                        relationship.AlternativeInterpretations,
                        relationship.UnresolvedQuestions,
                        entry.RelationshipProposal.ProposalId,
                        entry.LedgerEntryId));
                    continue;
                }
                var sourceIndex = events.FindIndex(item => item.ProjectionEventId == sourceEventId);
                var targetIndex = events.FindIndex(item => item.ProjectionEventId == candidate.ProjectionEventId);
                if (sourceIndex < 0 || targetIndex < 0)
                    throw new InvalidOperationException("confirmed relationship references an event absent from the projection");
                var source = events[sourceIndex];
                var target = events[targetIndex];
                events[targetIndex] = target with
                {
                    ObservationIds = target.ObservationIds.Concat(source.ObservationIds).Distinct(StringComparer.Ordinal).ToList(),
                    Title = string.IsNullOrWhiteSpace(target.Title) ? source.Title : target.Title,
                    Summary = AppendEvidenceSummary(target.Summary, source.Summary),
                    OperatorVisible = true,
                    OperatorReview = false,
                    SourceLedgerEntryIds = target.SourceLedgerEntryIds.Concat(source.SourceLedgerEntryIds).Distinct(StringComparer.Ordinal).ToList()
                };
                events.RemoveAt(sourceIndex);
                sourceEventIds[relationship.SourceProposalToken] = candidate.ProjectionEventId;
            }

            foreach (var relationship in acceptedRelationships
                         .Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ProvisionalAssociation))
            {
                var candidate = entry.Candidates.Single(item => item.CandidateToken == relationship.CandidateToken);
                var sourceEventId = sourceEventIds[relationship.SourceProposalToken];
                if (sourceEventId == candidate.ProjectionEventId)
                    continue;
                links.Add(new IncidentBatchProjectedAssociation(
                    $"{entry.LedgerEntryId}:{relationship.SourceProposalToken}:{relationship.CandidateToken}",
                    sourceEventId,
                    candidate.ProjectionEventId,
                    relationship.RelationshipStatement,
                    relationship.Uncertainty,
                    relationship.SourceEvidence,
                    relationship.CandidateEvidence,
                    relationship.AlternativeInterpretations,
                    relationship.UnresolvedQuestions,
                    entry.RelationshipProposal.ProposalId,
                    entry.LedgerEntryId));
            }
        }

        var projection = new IncidentBatchProjection(
            entry.RunId,
            projectionId,
            generatedAtUtc,
            (priorProjection?.LedgerEntryIds ?? []).Append(entry.LedgerEntryId).Distinct(StringComparer.Ordinal).ToList(),
            events,
            links);
        var validation = IncidentBatchContract.ValidateProjection(projection);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));
        return projection;
    }

    public static string BuildEvidenceSummary(IReadOnlyList<IncidentEventStateTranscriptCitation> citations) =>
        string.Join(" … ", citations
            .Select(item => item.ExactQuote.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal));

    public static string BuildEvidenceTitle(string evidenceSummary)
    {
        const int maximumLength = 120;
        if (evidenceSummary.Length <= maximumLength)
            return evidenceSummary;

        return $"{evidenceSummary[..(maximumLength - 1)].TrimEnd()}\u2026";
    }

    private static string AppendEvidenceSummary(string existing, string added)
    {
        if (string.IsNullOrWhiteSpace(existing)) return added;
        if (string.IsNullOrWhiteSpace(added) || existing.Contains(added, StringComparison.Ordinal)) return existing;
        return $"{existing} … {added}";
    }
}

public sealed class IncidentBatchCoordinator
{
    private readonly IIncidentBatchProposer _proposer;
    private readonly IIncidentBatchRelationshipProposer? _relationshipProposer;
    private readonly IIncidentBatchConfirmationVerifier? _confirmationVerifier;
    private readonly IIncidentBatchStore _store;
    private readonly TimeProvider _timeProvider;

    public IncidentBatchCoordinator(IIncidentBatchProposer proposer, IIncidentBatchStore store, TimeProvider? timeProvider = null)
    {
        _proposer = proposer;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IncidentBatchCoordinator(
        IIncidentBatchProposer proposer,
        IIncidentBatchRelationshipProposer relationshipProposer,
        IIncidentBatchStore store,
        TimeProvider? timeProvider = null)
        : this(proposer, store, timeProvider)
    {
        _relationshipProposer = relationshipProposer;
    }

    public IncidentBatchCoordinator(
        IIncidentBatchProposer proposer,
        IIncidentBatchRelationshipProposer relationshipProposer,
        IIncidentBatchConfirmationVerifier confirmationVerifier,
        IIncidentBatchStore store,
        TimeProvider? timeProvider = null)
        : this(proposer, relationshipProposer, store, timeProvider)
    {
        _confirmationVerifier = confirmationVerifier;
    }

    public async Task<IncidentBatchRunResult> RunAsync(
        IncidentBatchRunRequest request,
        IncidentEventStateObservationBundle bundle,
        IncidentBatchProjection? priorProjection,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        CancellationToken ct)
    {
        var inputValidation = IncidentBatchContract.ValidateInput(bundle, priorProjection, newObservationIds, candidates);
        if (!inputValidation.IsValid)
            throw new ArgumentException(string.Join("; ", inputValidation.Errors), nameof(bundle));
        if (request.SingletonEvents.Count != newObservationIds.Count)
            throw new ArgumentException("singleton identity count does not match new observation count", nameof(request));
        var now = _timeProvider.GetUtcNow();
        IncidentBatchProposal proposal;
        var proposerError = string.Empty;
        var timer = Stopwatch.StartNew();
        try
        {
            proposal = await _proposer.ProposeAsync(
                bundle,
                newObservationIds,
                _relationshipProposer is null ? candidates : [],
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            proposal = new IncidentBatchProposal(
                $"application:proposer-error:{request.LedgerEntryId}",
                now,
                "application",
                IncidentBatchPrompt.PromptIdentity,
                []);
            proposerError = ex.GetBaseException().Message;
        }
        finally
        {
            timer.Stop();
        }
        var constructionCandidates = _relationshipProposer is null ? candidates : [];
        var proposalValidation = IncidentBatchContract.ValidateProposal(
            bundle,
            newObservationIds,
            constructionCandidates,
            proposal,
            request.ConfigurationIdentity);
        IncidentBatchRelationshipProposal? relationshipProposal = null;
        IReadOnlyList<string>? relationshipValidationErrors = null;
        IncidentBatchRelationshipExecutionContext? relationshipExecution = null;
        IncidentBatchConfirmationProposal? confirmationProposal = null;
        IReadOnlyList<string>? confirmationValidationErrors = null;
        IncidentBatchConfirmationExecutionContext? confirmationExecution = null;
        if (_relationshipProposer is not null)
        {
            var sources = IncidentBatchContract.AcceptedEvents(
                    bundle,
                    newObservationIds,
                    [],
                    proposal,
                    proposalValidation.Errors,
                    request.ConfigurationIdentity)
                .Where(item => item.Disposition is IncidentBatchEventDisposition.NewEvent or IncidentBatchEventDisposition.ProvisionalEvent)
                .Select(item => new IncidentBatchRelationshipSource(item.ProposalToken, item.NewObservationIds))
                .ToList();
            var relationshipTimer = Stopwatch.StartNew();
            var relationshipError = string.Empty;
            try
            {
                relationshipProposal = sources.Count == 0 || candidates.Count == 0
                    ? new IncidentBatchRelationshipProposal(
                        $"application:no-relationship-input:{request.LedgerEntryId}",
                        now,
                        "application",
                        IncidentBatchRelationshipPrompt.PromptIdentity,
                        [])
                    : await _relationshipProposer.ProposeAsync(bundle, sources, candidates, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                relationshipProposal = new IncidentBatchRelationshipProposal(
                    $"application:relationship-proposer-error:{request.LedgerEntryId}",
                    now,
                    "application",
                    IncidentBatchRelationshipPrompt.PromptIdentity,
                    []);
                relationshipError = ex.GetBaseException().Message;
            }
            finally
            {
                relationshipTimer.Stop();
            }
            relationshipValidationErrors = IncidentBatchRelationshipContract.ValidateProposal(
                bundle,
                sources,
                candidates,
                relationshipProposal).Errors;
            relationshipExecution = new IncidentBatchRelationshipExecutionContext(relationshipTimer.ElapsedMilliseconds, relationshipError);

            if (_confirmationVerifier is not null)
            {
                var acceptedRelationships = IncidentBatchRelationshipContract.AcceptedRelationships(bundle, sources, candidates, relationshipProposal);
                var relationshipsToVerify = acceptedRelationships;
                var confirmationTimer = Stopwatch.StartNew();
                var confirmationError = string.Empty;
                try
                {
                    confirmationProposal = relationshipsToVerify.Count == 0
                        ? new IncidentBatchConfirmationProposal(
                            $"application:no-confirmation-input:{request.LedgerEntryId}",
                            now,
                            "application",
                            IncidentBatchConfirmationPrompt.PromptIdentity,
                            [])
                        : await _confirmationVerifier.VerifyAsync(bundle, sources, candidates, relationshipsToVerify, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    confirmationProposal = new IncidentBatchConfirmationProposal(
                        $"application:confirmation-verifier-error:{request.LedgerEntryId}",
                        now,
                        "application",
                        IncidentBatchConfirmationPrompt.PromptIdentity,
                        []);
                    confirmationError = ex.GetBaseException().Message;
                }
                finally
                {
                    confirmationTimer.Stop();
                }
                confirmationValidationErrors = IncidentBatchConfirmationContract.ValidateProposal(
                    bundle,
                    sources,
                    candidates,
                    relationshipsToVerify,
                    confirmationProposal).Errors;
                confirmationExecution = new IncidentBatchConfirmationExecutionContext(confirmationTimer.ElapsedMilliseconds, confirmationError);
            }
        }
        var entry = new IncidentBatchLedgerEntry(
            request.RunId,
            request.LedgerEntryId,
            now,
            bundle,
            newObservationIds,
            request.SingletonEvents,
            candidates,
            proposal,
            proposalValidation.Errors,
            new IncidentBatchExecutionContext(
                request.SoftwareVersion,
                request.ConfigurationIdentity,
                timer.ElapsedMilliseconds,
                proposerError,
                request.RetrievalDurationMilliseconds),
            relationshipProposal,
            relationshipValidationErrors,
            relationshipExecution,
            confirmationProposal,
            confirmationValidationErrors,
            confirmationExecution);
        var entryValidation = IncidentBatchContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", entryValidation.Errors));
        var projection = IncidentBatchProjector.Apply(priorProjection, entry, request.ProjectionId, now);
        return await _store.AppendIncidentBatchRunAsync(entry, projection, ct);
    }
}
