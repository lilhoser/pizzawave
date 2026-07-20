using System.Diagnostics;

namespace pizzad;

public enum IncidentEventStateLinkDecision
{
    Abstain,
    ProposeLink
}

public enum IncidentEventStateLinkTransitionOutcome
{
    UnresolvedSingleton,
    LinkedToExistingEvent
}

public sealed record IncidentEventStateLinkCandidate(
    string CandidateToken,
    string ProjectionEventId,
    IReadOnlyList<string> ObservationIds);

public sealed record IncidentEventStateTranscriptCitation(
    string TranscriptId,
    string ExactQuote);

public sealed record IncidentEventStateLinkProposal(
    string ProposalId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    IncidentEventStateLinkDecision Decision,
    string CandidateToken,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<IncidentEventStateTranscriptCitation> NewObservationEvidence,
    IReadOnlyList<IncidentEventStateTranscriptCitation> CandidateEvidence,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentEventStateLinkProjectionEvent(
    string ProjectionEventId,
    IReadOnlyList<string> ObservationIds,
    IReadOnlyList<string> SourceLedgerEntryIds);

public sealed record IncidentEventStateLinkProjection(
    string ProjectionId,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> LedgerEntryIds,
    IReadOnlyList<IncidentEventStateLinkProjectionEvent> Events);

public sealed record IncidentEventStateLinkTransition(
    IncidentEventStateLinkTransitionOutcome Outcome,
    string NewObservationId,
    string ProjectionEventId,
    string Reason);

public sealed record IncidentEventStateLinkExecutionContext(
    string SoftwareVersion,
    string ConfigurationIdentity,
    long ProposerDurationMilliseconds,
    string ProposerError);

public sealed record IncidentEventStateLinkLedgerEntry(
    string LedgerEntryId,
    DateTimeOffset RecordedAtUtc,
    IncidentEventStateObservationBundle Bundle,
    string NewObservationId,
    string SingletonProjectionEventId,
    IReadOnlyList<IncidentEventStateLinkCandidate> Candidates,
    IncidentEventStateLinkProposal Proposal,
    IReadOnlyList<string> ProposalValidationErrors,
    IncidentEventStateLinkTransition Transition,
    IncidentEventStateLinkExecutionContext Execution);

public sealed record IncidentEventStateStoredLinkLedgerEntry(
    long Sequence,
    string ContentHash,
    IncidentEventStateLinkLedgerEntry Entry);

public sealed record IncidentEventStateStoredLinkProjection(
    long Sequence,
    string ContentHash,
    IncidentEventStateLinkProjection Projection);

public sealed record IncidentEventStateLinkShadowRunRequest(
    string LedgerEntryId,
    string ProjectionId,
    string SingletonProjectionEventId,
    string SoftwareVersion,
    string ConfigurationIdentity);

public sealed record IncidentEventStateLinkShadowRunResult(
    IncidentEventStateStoredLinkLedgerEntry LedgerEntry,
    IncidentEventStateStoredLinkProjection Projection);

public interface IIncidentEventStateLinkProposer
{
    Task<IncidentEventStateLinkProposal> ProposeAsync(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentEventStateLinkCandidate> candidates,
        CancellationToken ct);
}

public interface IIncidentEventStateLinkShadowStore
{
    Task<IncidentEventStateLinkShadowRunResult> AppendIncidentEventStateLinkShadowRunAsync(
        IncidentEventStateLinkLedgerEntry entry,
        IncidentEventStateLinkProjection projection,
        CancellationToken ct);
}

public static class IncidentEventStateLinkContractValidator
{
    public const int MaximumCandidateCount = 8;
    public const int MaximumObservationsPerCandidate = 12;
    public const int MaximumCandidateObservationCount = 48;

    public static IncidentEventStateContractValidationResult ValidateInput(
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateLinkProjection? priorProjection,
        string newObservationId,
        IReadOnlyList<IncidentEventStateLinkCandidate> candidates)
    {
        var errors = IncidentEventStateContractValidator.ValidateBundle(bundle).Errors.ToList();
        RequireValue(newObservationId, "new observation id", errors);

        var observationsById = bundle.Observations
            .GroupBy(observation => observation.ObservationId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(newObservationId) && !observationsById.ContainsKey(newObservationId))
            errors.Add($"new observation '{newObservationId}' is not present in the source bundle");

        if (candidates.Count > MaximumCandidateCount)
            errors.Add($"link request contains {candidates.Count} candidates; maximum is {MaximumCandidateCount}");
        if (candidates.Sum(candidate => candidate.ObservationIds.Count) > MaximumCandidateObservationCount)
            errors.Add($"link request contains more than {MaximumCandidateObservationCount} candidate observations");
        RequireUnique(candidates.Select(candidate => candidate.CandidateToken), "candidate token", errors);
        RequireUnique(candidates.Select(candidate => candidate.ProjectionEventId), "candidate projection event id", errors);

        var priorEvents = (priorProjection?.Events ?? [])
            .GroupBy(item => item.ProjectionEventId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            RequireValue(candidate.CandidateToken, "candidate token", errors);
            RequireValue(candidate.ProjectionEventId, "candidate projection event id", errors);
            RequireUnique(candidate.ObservationIds, $"observation id in candidate '{candidate.CandidateToken}'", errors);
            if (candidate.ObservationIds.Count == 0)
                errors.Add($"candidate '{candidate.CandidateToken}' must contain at least one observation");
            if (candidate.ObservationIds.Count > MaximumObservationsPerCandidate)
                errors.Add($"candidate '{candidate.CandidateToken}' contains more than {MaximumObservationsPerCandidate} observations");
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
            if (!candidate.ObservationIds.ToHashSet(StringComparer.Ordinal)
                    .IsSubsetOf(priorEvent.ObservationIds))
            {
                errors.Add($"candidate '{candidate.CandidateToken}' includes observations outside projected event '{candidate.ProjectionEventId}'");
            }
        }

        if (priorProjection is not null)
            errors.AddRange(ValidateProjection(priorProjection).Errors.Select(error => $"prior projection: {error}"));

        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentEventStateLinkCandidate> candidates,
        IncidentEventStateLinkProposal proposal)
    {
        var errors = new List<string>();
        RequireValue(proposal.ProposalId, "link proposal id", errors);
        RequireValue(proposal.ModelIdentity, "link proposal model identity", errors);
        RequireValue(proposal.PromptIdentity, "link proposal prompt identity", errors);
        if (proposal.GeneratedAtUtc == default)
            errors.Add("link proposal generated timestamp is required");
        if (!Enum.IsDefined(proposal.Decision))
            errors.Add("link proposal decision is invalid");
        if (double.IsNaN(proposal.Uncertainty) || double.IsInfinity(proposal.Uncertainty) || proposal.Uncertainty is < 0 or > 1)
            errors.Add("link proposal uncertainty must be between 0 and 1");

        RequireUnique(proposal.UnresolvedQuestions, "link proposal unresolved question", errors);
        foreach (var question in proposal.UnresolvedQuestions)
            RequireValue(question, "link proposal unresolved question", errors);

        if (proposal.Decision == IncidentEventStateLinkDecision.ProposeLink)
        {
            RequireValue(proposal.CandidateToken, "linked candidate token", errors);
            RequireValue(proposal.RelationshipStatement, "link relationship statement", errors);
            var candidate = candidates.FirstOrDefault(item =>
                string.Equals(item.CandidateToken, proposal.CandidateToken, StringComparison.Ordinal));
            if (candidate is null)
            {
                errors.Add($"link proposal references unknown candidate token '{proposal.CandidateToken}'");
            }
            else
            {
                ValidateCitations(
                    bundle,
                    [newObservationId],
                    proposal.NewObservationEvidence,
                    "new-observation link evidence",
                    requireEvidence: true,
                    errors);
                ValidateCitations(
                    bundle,
                    candidate.ObservationIds,
                    proposal.CandidateEvidence,
                    "candidate link evidence",
                    requireEvidence: true,
                    errors);
            }
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(proposal.CandidateToken))
                errors.Add("abstaining link proposal cannot select a candidate token");
            if (proposal.CandidateEvidence.Count > 0)
                errors.Add("abstaining link proposal cannot cite candidate evidence");
            if (proposal.UnresolvedQuestions.Count == 0)
                errors.Add("abstaining link proposal must preserve at least one unresolved question");
            ValidateCitations(
                bundle,
                [newObservationId],
                proposal.NewObservationEvidence,
                "abstention evidence",
                requireEvidence: false,
                errors);
        }

        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateLedgerEntry(
        IncidentEventStateLinkLedgerEntry entry)
    {
        var errors = new List<string>();
        RequireValue(entry.LedgerEntryId, "link ledger entry id", errors);
        RequireValue(entry.SingletonProjectionEventId, "singleton projection event id", errors);
        if (entry.RecordedAtUtc == default)
            errors.Add("link ledger recorded timestamp is required");
        RequireValue(entry.Execution.SoftwareVersion, "link execution software version", errors);
        RequireValue(entry.Execution.ConfigurationIdentity, "link execution configuration identity", errors);
        if (entry.Execution.ProposerDurationMilliseconds < 0)
            errors.Add("link proposer duration cannot be negative");

        var proposalValidation = ValidateProposal(
            entry.Bundle,
            entry.NewObservationId,
            entry.Candidates,
            entry.Proposal);
        var expectedErrors = proposalValidation.Errors;
        if (!entry.ProposalValidationErrors.SequenceEqual(expectedErrors, StringComparer.Ordinal))
            errors.Add("link ledger proposal validation errors do not match deterministic validation");

        if (entry.Transition.NewObservationId != entry.NewObservationId)
            errors.Add("link transition new observation id does not match the ledger entry");
        if (!Enum.IsDefined(entry.Transition.Outcome))
            errors.Add("link transition outcome is invalid");
        RequireValue(entry.Transition.ProjectionEventId, "link transition projection event id", errors);
        RequireValue(entry.Transition.Reason, "link transition reason", errors);
        if (entry.Transition.Outcome == IncidentEventStateLinkTransitionOutcome.LinkedToExistingEvent)
        {
            if (!proposalValidation.IsValid || entry.Proposal.Decision != IncidentEventStateLinkDecision.ProposeLink)
                errors.Add("accepted link transition requires a valid positive link proposal");
            var selected = entry.Candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.CandidateToken, entry.Proposal.CandidateToken, StringComparison.Ordinal));
            if (selected is null || !string.Equals(selected.ProjectionEventId, entry.Transition.ProjectionEventId, StringComparison.Ordinal))
                errors.Add("accepted link transition target does not match the selected candidate");
        }
        else if (!string.Equals(entry.Transition.ProjectionEventId, entry.SingletonProjectionEventId, StringComparison.Ordinal))
        {
            errors.Add("unresolved transition must target the application-owned singleton event");
        }

        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateProjection(
        IncidentEventStateLinkProjection projection)
    {
        var errors = new List<string>();
        RequireValue(projection.ProjectionId, "link projection id", errors);
        if (projection.GeneratedAtUtc == default)
            errors.Add("link projection generated timestamp is required");
        RequireUnique(projection.LedgerEntryIds, "link projection ledger entry id", errors);
        RequireUnique(projection.Events.Select(item => item.ProjectionEventId), "link projection event id", errors);

        var assignedObservations = new HashSet<string>(StringComparer.Ordinal);
        var ledgerIds = projection.LedgerEntryIds.ToHashSet(StringComparer.Ordinal);
        foreach (var item in projection.Events)
        {
            RequireValue(item.ProjectionEventId, "link projection event id", errors);
            RequireUnique(item.ObservationIds, $"observation id in projected event '{item.ProjectionEventId}'", errors);
            if (item.ObservationIds.Count == 0)
                errors.Add($"projected event '{item.ProjectionEventId}' must contain at least one observation");
            foreach (var observationId in item.ObservationIds)
            {
                RequireValue(observationId, $"observation id in projected event '{item.ProjectionEventId}'", errors);
                if (!assignedObservations.Add(observationId))
                    errors.Add($"observation '{observationId}' belongs to more than one projected event");
            }
            RequireUnique(item.SourceLedgerEntryIds, $"source ledger entry id in projected event '{item.ProjectionEventId}'", errors);
            foreach (var sourceLedgerEntryId in item.SourceLedgerEntryIds)
            {
                if (!ledgerIds.Contains(sourceLedgerEntryId))
                    errors.Add($"projected event '{item.ProjectionEventId}' references ledger entry '{sourceLedgerEntryId}' outside the projection");
            }
        }

        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateCitations(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> allowedObservationIds,
        IReadOnlyList<IncidentEventStateTranscriptCitation> citations,
        string owner,
        bool requireEvidence,
        List<string> errors)
    {
        if (requireEvidence && citations.Count == 0)
            errors.Add($"{owner} must include at least one exact transcript citation");

        var transcripts = bundle.Observations
            .Where(observation => allowedObservationIds.Contains(observation.ObservationId, StringComparer.Ordinal))
            .SelectMany(observation => observation.Transcripts.Select(transcript =>
                (observation.ObservationId, Transcript: transcript)))
            .GroupBy(item => item.Transcript.TranscriptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        foreach (var citation in citations)
        {
            RequireValue(citation.TranscriptId, $"transcript id in {owner}", errors);
            RequireValue(citation.ExactQuote, $"exact quote in {owner}", errors);
            if (!transcripts.TryGetValue(citation.TranscriptId, out var matches))
            {
                errors.Add($"{owner} cites unknown transcript '{citation.TranscriptId}'");
                continue;
            }
            if (matches.Count != 1)
            {
                errors.Add($"{owner} transcript id '{citation.TranscriptId}' is ambiguous across observations");
                continue;
            }
            if (!string.IsNullOrEmpty(citation.ExactQuote) &&
                !matches[0].Transcript.Text.Contains(citation.ExactQuote, StringComparison.Ordinal))
            {
                errors.Add($"{owner} quote does not occur exactly in transcript '{citation.TranscriptId}'");
            }
        }
    }

    private static void RequireUnique(IEnumerable<string> values, string description, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value ?? string.Empty))
                errors.Add($"duplicate {description} '{value}'");
        }
    }

    private static void RequireValue(string? value, string description, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{description} is required");
    }
}

public static class IncidentEventStateLinkProjector
{
    public static IncidentEventStateLinkProjection Apply(
        IncidentEventStateLinkProjection? priorProjection,
        IncidentEventStateLinkLedgerEntry entry,
        string projectionId,
        DateTimeOffset generatedAtUtc)
    {
        var entryValidation = IncidentEventStateLinkContractValidator.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new ArgumentException(string.Join("; ", entryValidation.Errors), nameof(entry));
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionId);
        if (generatedAtUtc == default)
            throw new ArgumentException("projection generated timestamp is required", nameof(generatedAtUtc));

        var events = (priorProjection?.Events ?? [])
            .Select(item => item with
            {
                ObservationIds = item.ObservationIds.ToList(),
                SourceLedgerEntryIds = item.SourceLedgerEntryIds.ToList()
            })
            .ToList();
        var existingOwner = events.FirstOrDefault(item =>
            item.ObservationIds.Contains(entry.NewObservationId, StringComparer.Ordinal));
        if (existingOwner is not null)
            throw new InvalidOperationException($"observation '{entry.NewObservationId}' already belongs to projected event '{existingOwner.ProjectionEventId}'");

        if (entry.Transition.Outcome == IncidentEventStateLinkTransitionOutcome.LinkedToExistingEvent)
        {
            var index = events.FindIndex(item =>
                string.Equals(item.ProjectionEventId, entry.Transition.ProjectionEventId, StringComparison.Ordinal));
            if (index < 0)
                throw new InvalidOperationException($"link target '{entry.Transition.ProjectionEventId}' is not present in the prior projection");
            var target = events[index];
            events[index] = target with
            {
                ObservationIds = target.ObservationIds.Append(entry.NewObservationId).Distinct(StringComparer.Ordinal).ToList(),
                SourceLedgerEntryIds = target.SourceLedgerEntryIds.Append(entry.LedgerEntryId).Distinct(StringComparer.Ordinal).ToList()
            };
        }
        else
        {
            if (events.Any(item => string.Equals(item.ProjectionEventId, entry.SingletonProjectionEventId, StringComparison.Ordinal)))
                throw new InvalidOperationException($"singleton projection event '{entry.SingletonProjectionEventId}' already exists");
            events.Add(new IncidentEventStateLinkProjectionEvent(
                entry.SingletonProjectionEventId,
                [entry.NewObservationId],
                [entry.LedgerEntryId]));
        }

        var projection = new IncidentEventStateLinkProjection(
            projectionId,
            generatedAtUtc,
            (priorProjection?.LedgerEntryIds ?? []).Append(entry.LedgerEntryId).Distinct(StringComparer.Ordinal).ToList(),
            events);
        var validation = IncidentEventStateLinkContractValidator.ValidateProjection(projection);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));
        return projection;
    }
}

public sealed class IncidentEventStateLinkShadowCoordinator
{
    private readonly IIncidentEventStateLinkProposer _proposer;
    private readonly IIncidentEventStateLinkShadowStore _store;
    private readonly TimeProvider _timeProvider;

    public IncidentEventStateLinkShadowCoordinator(
        IIncidentEventStateLinkProposer proposer,
        IIncidentEventStateLinkShadowStore store,
        TimeProvider? timeProvider = null)
    {
        _proposer = proposer;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IncidentEventStateLinkShadowRunResult> RunAsync(
        IncidentEventStateLinkShadowRunRequest request,
        IncidentEventStateObservationBundle bundle,
        IncidentEventStateLinkProjection? priorProjection,
        string newObservationId,
        IReadOnlyList<IncidentEventStateLinkCandidate> candidates,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LedgerEntryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProjectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SingletonProjectionEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SoftwareVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfigurationIdentity);

        var inputValidation = IncidentEventStateLinkContractValidator.ValidateInput(
            bundle,
            priorProjection,
            newObservationId,
            candidates);
        if (!inputValidation.IsValid)
            throw new ArgumentException(string.Join("; ", inputValidation.Errors), nameof(bundle));

        var now = _timeProvider.GetUtcNow();
        IncidentEventStateLinkProposal proposal;
        long proposerDurationMilliseconds;
        var proposerError = string.Empty;
        if (candidates.Count == 0)
        {
            proposal = new IncidentEventStateLinkProposal(
                $"application:no-candidate:{request.LedgerEntryId}",
                now,
                "application",
                "link-only-no-candidate-v1",
                IncidentEventStateLinkDecision.Abstain,
                string.Empty,
                "No retrieved candidate event was available for comparison.",
                1,
                [],
                [],
                ["Could this observation relate to an event outside the bounded candidate set?"]);
            proposerDurationMilliseconds = 0;
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
                proposal = new IncidentEventStateLinkProposal(
                    $"application:proposer-error:{request.LedgerEntryId}",
                    now,
                    "application",
                    IncidentEventStateLinkPrompt.PromptIdentity,
                    IncidentEventStateLinkDecision.Abstain,
                    string.Empty,
                    "The link proposer failed; the observation remains unresolved.",
                    1,
                    [],
                    [],
                    ["Would a successful source-grounded comparison connect this observation to a candidate event?"]);
                proposerError = ex.GetBaseException().Message;
            }
            finally
            {
                timer.Stop();
            }
            proposerDurationMilliseconds = timer.ElapsedMilliseconds;
        }
        var proposalValidation = IncidentEventStateLinkContractValidator.ValidateProposal(
            bundle,
            newObservationId,
            candidates,
            proposal);

        IncidentEventStateLinkTransition transition;
        if (proposalValidation.IsValid && proposal.Decision == IncidentEventStateLinkDecision.ProposeLink)
        {
            var selected = candidates.Single(candidate =>
                string.Equals(candidate.CandidateToken, proposal.CandidateToken, StringComparison.Ordinal));
            transition = new IncidentEventStateLinkTransition(
                IncidentEventStateLinkTransitionOutcome.LinkedToExistingEvent,
                newObservationId,
                selected.ProjectionEventId,
                "valid source-cited proposal links the observation to an existing shadow event");
        }
        else
        {
            transition = new IncidentEventStateLinkTransition(
                IncidentEventStateLinkTransitionOutcome.UnresolvedSingleton,
                newObservationId,
                request.SingletonProjectionEventId,
                proposalValidation.IsValid
                    ? "model abstained; observation remains an unresolved singleton"
                    : "model output failed deterministic validation; observation remains an unresolved singleton");
        }

        var entry = new IncidentEventStateLinkLedgerEntry(
            request.LedgerEntryId,
            now,
            bundle,
            newObservationId,
            request.SingletonProjectionEventId,
            candidates,
            proposal,
            proposalValidation.Errors,
            transition,
            new IncidentEventStateLinkExecutionContext(
                request.SoftwareVersion,
                request.ConfigurationIdentity,
                proposerDurationMilliseconds,
                proposerError));
        var entryValidation = IncidentEventStateLinkContractValidator.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", entryValidation.Errors));

        var projection = IncidentEventStateLinkProjector.Apply(
            priorProjection,
            entry,
            request.ProjectionId,
            now);
        return await _store.AppendIncidentEventStateLinkShadowRunAsync(entry, projection, ct);
    }
}
