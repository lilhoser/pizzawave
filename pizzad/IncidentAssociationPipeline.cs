using System.Diagnostics;

namespace pizzad;

public enum IncidentAssociationDisposition
{
    ConfirmedMembership,
    ProvisionalAssociation
}

public enum IncidentAssociationTransitionOutcome
{
    SingletonCreated,
    ConfirmedMembership
}

public sealed record IncidentAssociationCandidate(
    string CandidateToken,
    string ProjectionEventId,
    IReadOnlyList<string> ObservationIds);

public sealed record IncidentAssociationRelationship(
    string CandidateToken,
    IncidentAssociationDisposition Disposition,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateTranscriptCitation> NewObservationEvidence,
    IReadOnlyList<IncidentEventStateTranscriptCitation> CandidateEvidence,
    IReadOnlyList<string> AlternativeInterpretations,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentAssociationProposal(
    string ProposalId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentAssociationRelationship> Relationships);

public sealed record IncidentAssociationProjectionEvent(
    string ProjectionEventId,
    IReadOnlyList<string> ObservationIds,
    IReadOnlyList<string> SourceLedgerEntryIds);

public sealed record IncidentAssociationProjectedLink(
    string AssociationId,
    string SourceObservationId,
    string SourceProjectionEventId,
    string CandidateProjectionEventId,
    string CandidateToken,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateTranscriptCitation> NewObservationEvidence,
    IReadOnlyList<IncidentEventStateTranscriptCitation> CandidateEvidence,
    IReadOnlyList<string> AlternativeInterpretations,
    IReadOnlyList<string> UnresolvedQuestions,
    string SourceProposalId,
    string SourceLedgerEntryId);

public sealed record IncidentAssociationProjection(
    string RunId,
    string ProjectionId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> LedgerEntryIds,
    IReadOnlyList<IncidentAssociationProjectionEvent> Events,
    IReadOnlyList<IncidentAssociationProjectedLink> ProvisionalAssociations);

public sealed record IncidentAssociationTransition(
    IncidentAssociationTransitionOutcome Outcome,
    string NewObservationId,
    string ProjectionEventId,
    string ConfirmedCandidateToken,
    string Reason);

public sealed record IncidentAssociationExecutionContext(
    string SoftwareVersion,
    string ConfigurationIdentity,
    long ProposerDurationMilliseconds,
    string ProposerError);

public sealed record IncidentAssociationLedgerEntry(
    string RunId,
    string LedgerEntryId,
    DateTimeOffset RecordedAtUtc,
    IncidentEventStateObservationBundle Bundle,
    string NewObservationId,
    string SingletonProjectionEventId,
    IReadOnlyList<IncidentAssociationCandidate> Candidates,
    IncidentAssociationProposal Proposal,
    IReadOnlyList<string> ProposalValidationErrors,
    IncidentAssociationTransition Transition,
    IncidentAssociationExecutionContext Execution);

public sealed record IncidentAssociationStoredLedgerEntry(
    long Sequence,
    string ContentHash,
    IncidentAssociationLedgerEntry Entry);

public sealed record IncidentAssociationStoredProjection(
    long Sequence,
    string ContentHash,
    IncidentAssociationProjection Projection);

public sealed record IncidentAssociationShadowRunRequest(
    string RunId,
    string LedgerEntryId,
    string ProjectionId,
    string SingletonProjectionEventId,
    string SoftwareVersion,
    string ConfigurationIdentity);

public sealed record IncidentAssociationShadowRunResult(
    IncidentAssociationStoredLedgerEntry LedgerEntry,
    IncidentAssociationStoredProjection Projection);

public interface IIncidentAssociationProposer
{
    Task<IncidentAssociationProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentAssociationCandidate> candidates,
        CancellationToken ct);
}

public interface IIncidentAssociationShadowStore
{
    Task<IncidentAssociationShadowRunResult> AppendIncidentAssociationShadowRunAsync(
        IncidentAssociationLedgerEntry entry,
        IncidentAssociationProjection projection,
        CancellationToken ct);
}

public static class IncidentAssociationContract
{
    public const int MaximumCandidateCount = 8;
    public const int MaximumObservationsPerCandidate = 12;
    public const int MaximumCandidateObservationCount = 48;

    public static IncidentEventStateContractValidationResult ValidateInput(
        IncidentEventStateObservationBundle bundle,
        IncidentAssociationProjection? priorProjection,
        string newObservationId,
        IReadOnlyList<IncidentAssociationCandidate> candidates)
    {
        var errors = IncidentEventStateContractValidator.ValidateBundle(bundle).Errors.ToList();
        RequireValue(newObservationId, "new observation id", errors);
        var observationsById = bundle.Observations
            .GroupBy(observation => observation.ObservationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(newObservationId) && !observationsById.ContainsKey(newObservationId))
            errors.Add($"new observation '{newObservationId}' is not present in the source bundle");
        if (candidates.Count > MaximumCandidateCount)
            errors.Add($"association request contains {candidates.Count} candidates; maximum is {MaximumCandidateCount}");
        if (candidates.Sum(candidate => candidate.ObservationIds.Count) > MaximumCandidateObservationCount)
            errors.Add($"association request contains more than {MaximumCandidateObservationCount} candidate observations");
        RequireUnique(candidates.Select(candidate => candidate.CandidateToken), "candidate token", errors);
        RequireUnique(candidates.Select(candidate => candidate.ProjectionEventId), "candidate projection event id", errors);

        var priorEvents = (priorProjection?.Events ?? [])
            .ToDictionary(item => item.ProjectionEventId, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            RequireValue(candidate.CandidateToken, "candidate token", errors);
            RequireValue(candidate.ProjectionEventId, "candidate projection event id", errors);
            RequireUnique(candidate.ObservationIds, $"observation id in candidate '{candidate.CandidateToken}'", errors);
            if (candidate.ObservationIds.Count == 0)
                errors.Add($"candidate '{candidate.CandidateToken}' must contain at least one observation");
            if (candidate.ObservationIds.Count > MaximumObservationsPerCandidate)
                errors.Add($"candidate '{candidate.CandidateToken}' contains too many observations");
            if (candidate.ObservationIds.Contains(newObservationId, StringComparer.Ordinal))
                errors.Add($"candidate '{candidate.CandidateToken}' already contains new observation '{newObservationId}'");
            foreach (var observationId in candidate.ObservationIds)
            {
                if (!observationsById.ContainsKey(observationId))
                    errors.Add($"candidate '{candidate.CandidateToken}' references observation '{observationId}' outside the source bundle");
            }
            if (priorProjection is null)
            {
                errors.Add($"candidate '{candidate.CandidateToken}' has no prior projection");
                continue;
            }
            if (!priorEvents.TryGetValue(candidate.ProjectionEventId, out var priorEvent))
            {
                errors.Add($"candidate '{candidate.CandidateToken}' references unknown projected event '{candidate.ProjectionEventId}'");
                continue;
            }
            if (!candidate.ObservationIds.ToHashSet(StringComparer.Ordinal).IsSubsetOf(priorEvent.ObservationIds))
                errors.Add($"candidate '{candidate.CandidateToken}' includes observations outside projected event '{candidate.ProjectionEventId}'");
        }
        if (priorProjection is not null)
            errors.AddRange(ValidateProjection(priorProjection).Errors.Select(error => $"prior projection: {error}"));
        return Result(errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentAssociationCandidate> candidates,
        IncidentAssociationProposal proposal)
    {
        var errors = new List<string>();
        RequireValue(proposal.ProposalId, "association proposal id", errors);
        RequireValue(proposal.ModelIdentity, "association proposal model identity", errors);
        RequireValue(proposal.PromptIdentity, "association proposal prompt identity", errors);
        if (proposal.GeneratedAtUtc == default)
            errors.Add("association proposal generated timestamp is required");
        RequireUnique(proposal.Relationships.Select(item => item.CandidateToken), "relationship candidate token", errors);
        if (proposal.Relationships.Count(item => item.Disposition == IncidentAssociationDisposition.ConfirmedMembership) > 1)
            errors.Add("association proposal may contain at most one confirmed membership");

        foreach (var relationship in proposal.Relationships)
        {
            if (!Enum.IsDefined(relationship.Disposition))
                errors.Add($"relationship for '{relationship.CandidateToken}' has an invalid disposition");
            RequireValue(relationship.RelationshipStatement, $"relationship statement for '{relationship.CandidateToken}'", errors);
            if (double.IsNaN(relationship.Uncertainty) || double.IsInfinity(relationship.Uncertainty) || relationship.Uncertainty is < 0 or > 1)
                errors.Add($"relationship uncertainty for '{relationship.CandidateToken}' must be between 0 and 1");
            var candidate = candidates.FirstOrDefault(item => string.Equals(item.CandidateToken, relationship.CandidateToken, StringComparison.Ordinal));
            if (candidate is null)
            {
                errors.Add($"relationship references unknown candidate token '{relationship.CandidateToken}'");
                continue;
            }
            ValidateCitations(bundle, [newObservationId], relationship.NewObservationEvidence, "new-observation relationship evidence", errors);
            ValidateCitations(bundle, candidate.ObservationIds, relationship.CandidateEvidence, "candidate relationship evidence", errors);
            ValidateStrings(relationship.AlternativeInterpretations, "alternative interpretation", errors);
            ValidateStrings(relationship.UnresolvedQuestions, "unresolved question", errors);
        }
        return Result(errors);
    }

    public static IncidentEventStateContractValidationResult ValidateLedgerEntry(IncidentAssociationLedgerEntry entry)
    {
        var errors = IncidentEventStateContractValidator.ValidateBundle(entry.Bundle).Errors.ToList();
        RequireValue(entry.RunId, "association shadow run id", errors);
        RequireValue(entry.LedgerEntryId, "association ledger entry id", errors);
        RequireValue(entry.SingletonProjectionEventId, "singleton projection event id", errors);
        if (entry.RecordedAtUtc == default)
            errors.Add("association ledger recorded timestamp is required");
        RequireValue(entry.Execution.SoftwareVersion, "association execution software version", errors);
        RequireValue(entry.Execution.ConfigurationIdentity, "association execution configuration identity", errors);
        if (entry.Execution.ProposerDurationMilliseconds < 0)
            errors.Add("association proposer duration cannot be negative");
        if (!entry.Bundle.Observations.Any(item => item.ObservationId == entry.NewObservationId))
            errors.Add($"new observation '{entry.NewObservationId}' is not present in the source bundle");
        if (entry.Candidates.Count > MaximumCandidateCount)
            errors.Add($"association ledger contains more than {MaximumCandidateCount} candidates");
        RequireUnique(entry.Candidates.Select(item => item.CandidateToken), "candidate token", errors);
        RequireUnique(entry.Candidates.Select(item => item.ProjectionEventId), "candidate projection event id", errors);
        var bundleObservationIds = entry.Bundle.Observations.Select(item => item.ObservationId).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in entry.Candidates)
        {
            RequireValue(candidate.CandidateToken, "candidate token", errors);
            RequireValue(candidate.ProjectionEventId, "candidate projection event id", errors);
            RequireUnique(candidate.ObservationIds, $"observation id in candidate '{candidate.CandidateToken}'", errors);
            if (candidate.ObservationIds.Count == 0 || candidate.ObservationIds.Count > MaximumObservationsPerCandidate)
                errors.Add($"candidate '{candidate.CandidateToken}' has an invalid observation count");
            if (candidate.ObservationIds.Contains(entry.NewObservationId, StringComparer.Ordinal))
                errors.Add($"candidate '{candidate.CandidateToken}' contains the new observation");
            foreach (var observationId in candidate.ObservationIds)
                if (!bundleObservationIds.Contains(observationId))
                    errors.Add($"candidate '{candidate.CandidateToken}' references observation '{observationId}' outside the source bundle");
        }

        var proposalValidation = ValidateProposal(entry.Bundle, entry.NewObservationId, entry.Candidates, entry.Proposal);
        if (!entry.ProposalValidationErrors.SequenceEqual(proposalValidation.Errors, StringComparer.Ordinal))
            errors.Add("association ledger proposal validation errors do not match deterministic validation");
        if (entry.Transition.NewObservationId != entry.NewObservationId)
            errors.Add("association transition new observation id does not match the ledger entry");
        if (!Enum.IsDefined(entry.Transition.Outcome))
            errors.Add("association transition outcome is invalid");
        RequireValue(entry.Transition.ProjectionEventId, "association transition projection event id", errors);
        RequireValue(entry.Transition.Reason, "association transition reason", errors);
        var confirmed = proposalValidation.IsValid
            ? entry.Proposal.Relationships.SingleOrDefault(item => item.Disposition == IncidentAssociationDisposition.ConfirmedMembership)
            : null;
        if (entry.Transition.Outcome == IncidentAssociationTransitionOutcome.ConfirmedMembership)
        {
            if (confirmed is null)
                errors.Add("confirmed membership transition requires one valid confirmed relationship");
            var candidate = entry.Candidates.FirstOrDefault(item => string.Equals(item.CandidateToken, confirmed?.CandidateToken, StringComparison.Ordinal));
            if (candidate is null || candidate.ProjectionEventId != entry.Transition.ProjectionEventId || confirmed?.CandidateToken != entry.Transition.ConfirmedCandidateToken)
                errors.Add("confirmed membership transition does not match its candidate");
        }
        else
        {
            if (entry.Transition.ProjectionEventId != entry.SingletonProjectionEventId)
                errors.Add("singleton transition must target the application-owned singleton event");
            if (!string.IsNullOrWhiteSpace(entry.Transition.ConfirmedCandidateToken))
                errors.Add("singleton transition cannot identify a confirmed candidate");
        }
        return Result(errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProjection(IncidentAssociationProjection projection)
    {
        var errors = new List<string>();
        RequireValue(projection.RunId, "association shadow run id", errors);
        RequireValue(projection.ProjectionId, "association projection id", errors);
        if (projection.GeneratedAtUtc == default)
            errors.Add("association projection generated timestamp is required");
        RequireUnique(projection.LedgerEntryIds, "association projection ledger entry id", errors);
        RequireUnique(projection.Events.Select(item => item.ProjectionEventId), "association projection event id", errors);
        RequireUnique(projection.ProvisionalAssociations.Select(item => item.AssociationId), "provisional association id", errors);
        var eventMap = projection.Events.ToDictionary(item => item.ProjectionEventId, StringComparer.Ordinal);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in projection.Events)
        {
            RequireValue(item.ProjectionEventId, "association projection event id", errors);
            RequireUnique(item.ObservationIds, $"observation id in projected event '{item.ProjectionEventId}'", errors);
            if (item.ObservationIds.Count == 0)
                errors.Add($"projected event '{item.ProjectionEventId}' must contain at least one observation");
            foreach (var observationId in item.ObservationIds)
            {
                if (!owners.TryAdd(observationId, item.ProjectionEventId))
                    errors.Add($"observation '{observationId}' belongs to more than one projected event");
            }
            foreach (var sourceLedgerEntryId in item.SourceLedgerEntryIds)
            {
                if (!projection.LedgerEntryIds.Contains(sourceLedgerEntryId, StringComparer.Ordinal))
                    errors.Add($"projected event '{item.ProjectionEventId}' references an unknown ledger entry");
            }
        }
        foreach (var link in projection.ProvisionalAssociations)
        {
            RequireValue(link.AssociationId, "provisional association id", errors);
            RequireValue(link.CandidateToken, $"candidate token in provisional association '{link.AssociationId}'", errors);
            RequireValue(link.RelationshipStatement, $"relationship statement in provisional association '{link.AssociationId}'", errors);
            RequireValue(link.SourceProposalId, $"source proposal id in provisional association '{link.AssociationId}'", errors);
            RequireValue(link.SourceLedgerEntryId, $"source ledger entry id in provisional association '{link.AssociationId}'", errors);
            if (double.IsNaN(link.Uncertainty) || double.IsInfinity(link.Uncertainty) || link.Uncertainty is < 0 or > 1)
                errors.Add($"provisional association '{link.AssociationId}' has invalid uncertainty");
            if (link.NewObservationEvidence.Count == 0 || link.CandidateEvidence.Count == 0)
                errors.Add($"provisional association '{link.AssociationId}' must retain evidence from both sides");
            if (!eventMap.TryGetValue(link.SourceProjectionEventId, out var source) || !source.ObservationIds.Contains(link.SourceObservationId, StringComparer.Ordinal))
                errors.Add($"provisional association '{link.AssociationId}' has an invalid source event");
            if (!eventMap.ContainsKey(link.CandidateProjectionEventId))
                errors.Add($"provisional association '{link.AssociationId}' has an invalid candidate event");
            if (link.SourceProjectionEventId == link.CandidateProjectionEventId)
                errors.Add($"provisional association '{link.AssociationId}' cannot connect an event to itself");
            if (!projection.LedgerEntryIds.Contains(link.SourceLedgerEntryId, StringComparer.Ordinal))
                errors.Add($"provisional association '{link.AssociationId}' references an unknown ledger entry");
        }
        return Result(errors);
    }

    private static void ValidateCitations(IncidentEventStateObservationBundle bundle, IReadOnlyList<string> allowedObservationIds, IReadOnlyList<IncidentEventStateTranscriptCitation> citations, string owner, List<string> errors)
    {
        if (citations.Count == 0)
            errors.Add($"{owner} must include at least one exact transcript citation");
        var transcripts = bundle.Observations
            .Where(observation => allowedObservationIds.Contains(observation.ObservationId, StringComparer.Ordinal))
            .SelectMany(observation => observation.Transcripts)
            .GroupBy(transcript => transcript.TranscriptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        RequireUnique(citations.Select(item => item.TranscriptId), $"transcript id in {owner}", errors);
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

    private static IncidentEventStateContractValidationResult Result(List<string> errors) => new(errors.Count == 0, errors);
}

public static class IncidentAssociationProjector
{
    public static IncidentAssociationProjection Apply(
        IncidentAssociationProjection? priorProjection,
        IncidentAssociationLedgerEntry entry,
        string projectionId,
        DateTimeOffset generatedAtUtc)
    {
        var validation = IncidentAssociationContract.ValidateLedgerEntry(entry);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(entry));
        if (priorProjection is not null && priorProjection.RunId != entry.RunId)
            throw new ArgumentException("prior projection belongs to a different association run", nameof(priorProjection));
        var events = (priorProjection?.Events ?? []).Select(item => item with
        {
            ObservationIds = item.ObservationIds.ToList(),
            SourceLedgerEntryIds = item.SourceLedgerEntryIds.ToList()
        }).ToList();
        if (events.Any(item => item.ObservationIds.Contains(entry.NewObservationId, StringComparer.Ordinal)))
            throw new InvalidOperationException($"observation '{entry.NewObservationId}' already belongs to a projected event");

        if (entry.Transition.Outcome == IncidentAssociationTransitionOutcome.ConfirmedMembership)
        {
            var index = events.FindIndex(item => item.ProjectionEventId == entry.Transition.ProjectionEventId);
            if (index < 0)
                throw new InvalidOperationException($"confirmed target '{entry.Transition.ProjectionEventId}' is absent from the prior projection");
            var target = events[index];
            events[index] = target with
            {
                ObservationIds = target.ObservationIds.Append(entry.NewObservationId).Distinct(StringComparer.Ordinal).ToList(),
                SourceLedgerEntryIds = target.SourceLedgerEntryIds.Append(entry.LedgerEntryId).Distinct(StringComparer.Ordinal).ToList()
            };
        }
        else
        {
            if (events.Any(item => item.ProjectionEventId == entry.SingletonProjectionEventId))
                throw new InvalidOperationException($"singleton event '{entry.SingletonProjectionEventId}' already exists");
            events.Add(new IncidentAssociationProjectionEvent(entry.SingletonProjectionEventId, [entry.NewObservationId], [entry.LedgerEntryId]));
        }

        var links = (priorProjection?.ProvisionalAssociations ?? []).ToList();
        if (entry.ProposalValidationErrors.Count == 0)
        {
            foreach (var relationship in entry.Proposal.Relationships.Where(item => item.Disposition == IncidentAssociationDisposition.ProvisionalAssociation))
            {
                var candidate = entry.Candidates.Single(item => item.CandidateToken == relationship.CandidateToken);
                links.Add(new IncidentAssociationProjectedLink(
                    $"{entry.LedgerEntryId}:{relationship.CandidateToken}",
                    entry.NewObservationId,
                    entry.Transition.ProjectionEventId,
                    candidate.ProjectionEventId,
                    candidate.CandidateToken,
                    relationship.RelationshipStatement,
                    relationship.Uncertainty,
                    relationship.NewObservationEvidence,
                    relationship.CandidateEvidence,
                    relationship.AlternativeInterpretations,
                    relationship.UnresolvedQuestions,
                    entry.Proposal.ProposalId,
                    entry.LedgerEntryId));
            }
        }

        var projection = new IncidentAssociationProjection(
            entry.RunId,
            projectionId,
            generatedAtUtc,
            (priorProjection?.LedgerEntryIds ?? []).Append(entry.LedgerEntryId).Distinct(StringComparer.Ordinal).ToList(),
            events,
            links);
        var projectionValidation = IncidentAssociationContract.ValidateProjection(projection);
        if (!projectionValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", projectionValidation.Errors));
        return projection;
    }
}

public sealed class IncidentAssociationShadowCoordinator
{
    private readonly IIncidentAssociationProposer _proposer;
    private readonly IIncidentAssociationShadowStore _store;
    private readonly TimeProvider _timeProvider;

    public IncidentAssociationShadowCoordinator(IIncidentAssociationProposer proposer, IIncidentAssociationShadowStore store, TimeProvider? timeProvider = null)
    {
        _proposer = proposer;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IncidentAssociationShadowRunResult> RunAsync(
        IncidentAssociationShadowRunRequest request,
        IncidentEventStateObservationBundle bundle,
        IncidentAssociationProjection? priorProjection,
        string newObservationId,
        IReadOnlyList<IncidentAssociationCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LedgerEntryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SingletonProjectionEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SoftwareVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfigurationIdentity);
        var inputValidation = IncidentAssociationContract.ValidateInput(bundle, priorProjection, newObservationId, candidates);
        if (!inputValidation.IsValid)
            throw new ArgumentException(string.Join("; ", inputValidation.Errors), nameof(bundle));
        if (priorProjection is not null && priorProjection.RunId != request.RunId)
            throw new ArgumentException("prior projection belongs to a different association shadow run", nameof(priorProjection));

        var now = _timeProvider.GetUtcNow();
        IncidentAssociationProposal proposal;
        var proposerError = string.Empty;
        long proposerDurationMilliseconds = 0;
        if (candidates.Count == 0)
        {
            proposal = EmptyProposal($"application:no-candidate:{request.LedgerEntryId}", now, "association-no-candidate-v1");
        }
        else
        {
            var timer = Stopwatch.StartNew();
            try
            {
                proposal = await _proposer.ProposeAsync(bundle, newObservationId, candidates, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                proposal = EmptyProposal($"application:proposer-error:{request.LedgerEntryId}", now, IncidentAssociationPrompt.PromptIdentity);
                proposerError = ex.GetBaseException().Message;
            }
            finally
            {
                timer.Stop();
                proposerDurationMilliseconds = timer.ElapsedMilliseconds;
            }
        }

        var proposalValidation = IncidentAssociationContract.ValidateProposal(bundle, newObservationId, candidates, proposal);
        var confirmed = proposalValidation.IsValid
            ? proposal.Relationships.SingleOrDefault(item => item.Disposition == IncidentAssociationDisposition.ConfirmedMembership)
            : null;
        IncidentAssociationTransition transition;
        if (confirmed is not null)
        {
            var selected = candidates.Single(item => item.CandidateToken == confirmed.CandidateToken);
            transition = new IncidentAssociationTransition(
                IncidentAssociationTransitionOutcome.ConfirmedMembership,
                newObservationId,
                selected.ProjectionEventId,
                selected.CandidateToken,
                "one valid source-cited relationship confirms membership in an existing shadow event");
        }
        else
        {
            transition = new IncidentAssociationTransition(
                IncidentAssociationTransitionOutcome.SingletonCreated,
                newObservationId,
                request.SingletonProjectionEventId,
                string.Empty,
                proposalValidation.IsValid
                    ? "no relationship confirmed membership; the observation starts a singleton shadow event"
                    : "model output failed deterministic provenance validation; the observation starts a singleton shadow event");
        }

        var entry = new IncidentAssociationLedgerEntry(
            request.RunId,
            request.LedgerEntryId,
            now,
            bundle,
            newObservationId,
            request.SingletonProjectionEventId,
            candidates,
            proposal,
            proposalValidation.Errors,
            transition,
            new IncidentAssociationExecutionContext(request.SoftwareVersion, request.ConfigurationIdentity, proposerDurationMilliseconds, proposerError));
        var entryValidation = IncidentAssociationContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", entryValidation.Errors));
        var projection = IncidentAssociationProjector.Apply(priorProjection, entry, request.ProjectionId, now);
        return await _store.AppendIncidentAssociationShadowRunAsync(entry, projection, ct);
    }

    private static IncidentAssociationProposal EmptyProposal(string id, DateTimeOffset now, string promptIdentity) =>
        new(id, now, "application", promptIdentity, []);
}
