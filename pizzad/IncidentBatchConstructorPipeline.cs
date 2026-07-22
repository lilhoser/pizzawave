using System.Diagnostics;

namespace pizzad;

public enum IncidentBatchEventDisposition
{
    NewEvent,
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
    string ProposerError);

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
    IncidentBatchExecutionContext Execution);

public sealed record IncidentBatchStoredLedgerEntry(long Sequence, string ContentHash, IncidentBatchLedgerEntry Entry);
public sealed record IncidentBatchStoredProjection(long Sequence, string ContentHash, IncidentBatchProjection Projection);
public sealed record IncidentBatchRunResult(IncidentBatchStoredLedgerEntry LedgerEntry, IncidentBatchStoredProjection Projection);

public sealed record IncidentBatchRunRequest(
    string RunId,
    string LedgerEntryId,
    string ProjectionId,
    IReadOnlyList<IncidentBatchSingletonIdentity> SingletonEvents,
    string SoftwareVersion,
    string ConfigurationIdentity);

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
        IncidentBatchProposal proposal)
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

        foreach (var item in proposal.Events)
        {
            RequireValue(item.ProposalToken, "event proposal token", errors);
            RequireValue(item.Title, $"title for event proposal '{item.ProposalToken}'", errors);
            RequireValue(item.Summary, $"summary for event proposal '{item.ProposalToken}'", errors);
            RequireValue(item.RelationshipStatement, $"relationship statement for event proposal '{item.ProposalToken}'", errors);
            RequireUnique(item.NewObservationIds, $"new observation id in event proposal '{item.ProposalToken}'", errors);
            if (item.NewObservationIds.Count == 0)
                errors.Add($"event proposal '{item.ProposalToken}' contains no new observations");
            foreach (var observationId in item.NewObservationIds)
            {
                if (!newObservationIds.Contains(observationId, StringComparer.Ordinal))
                    errors.Add($"event proposal '{item.ProposalToken}' references non-new observation '{observationId}'");
                else if (!proposedOwners.TryAdd(observationId, item.ProposalToken))
                    errors.Add($"new observation '{observationId}' appears in more than one event proposal");
            }
            if (double.IsNaN(item.Uncertainty) || double.IsInfinity(item.Uncertainty) || item.Uncertainty is < 0 or > 1)
                errors.Add($"uncertainty for event proposal '{item.ProposalToken}' must be between 0 and 1");
            if (item.Disposition == IncidentBatchEventDisposition.NewEvent)
            {
                if (!string.IsNullOrWhiteSpace(item.CandidateToken))
                    errors.Add($"new event proposal '{item.ProposalToken}' cannot reference a candidate");
                if (item.CandidateEvidence.Count > 0)
                    errors.Add($"new event proposal '{item.ProposalToken}' cannot cite candidate evidence");
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
        var proposalValidation = ValidateProposal(entry.Bundle, entry.NewObservationIds, entry.Candidates, entry.Proposal);
        if (!entry.ProposalValidationErrors.SequenceEqual(proposalValidation.Errors, StringComparer.Ordinal))
            errors.Add("batch ledger proposal validation errors do not match deterministic validation");
        return Result(errors);
    }

    public static IReadOnlyList<IncidentBatchEventProposal> AcceptedEvents(IncidentBatchLedgerEntry entry)
    {
        if (!UsesPerEventAcceptance(entry.Execution.ConfigurationIdentity))
            return entry.ProposalValidationErrors.Count == 0 ? entry.Proposal.Events : [];

        var headerValidation = ValidateProposal(
            entry.Bundle,
            entry.NewObservationIds,
            entry.Candidates,
            entry.Proposal with { Events = [] });
        if (!headerValidation.IsValid)
            return [];

        var duplicateProposalTokens = Duplicates(entry.Proposal.Events.Select(item => item.ProposalToken));
        var duplicateObservationIds = Duplicates(entry.Proposal.Events.SelectMany(item => item.NewObservationIds));
        var duplicateConfirmedCandidates = Duplicates(entry.Proposal.Events
            .Where(item => item.Disposition == IncidentBatchEventDisposition.ConfirmedMembership)
            .Select(item => item.CandidateToken));
        return entry.Proposal.Events
            .Where(item => !duplicateProposalTokens.Contains(item.ProposalToken ?? string.Empty))
            .Where(item => !item.NewObservationIds.Any(duplicateObservationIds.Contains))
            .Where(item => item.Disposition != IncidentBatchEventDisposition.ConfirmedMembership || !duplicateConfirmedCandidates.Contains(item.CandidateToken ?? string.Empty))
            .Where(item => ValidateProposal(
                entry.Bundle,
                entry.NewObservationIds,
                entry.Candidates,
                entry.Proposal with { Events = [item] }).IsValid)
            .ToList();
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
        events.AddRange(entry.SingletonEvents.Select(item => new IncidentBatchProjectionEvent(
            item.ProjectionEventId,
            [item.ObservationId],
            string.Empty,
            string.Empty,
            false,
            [entry.LedgerEntryId])));

        var links = (priorProjection?.ProvisionalAssociations ?? []).ToList();
        foreach (var proposal in IncidentBatchContract.AcceptedEvents(entry))
        {
            var singletonIds = entry.SingletonEvents
                .Where(item => proposal.NewObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
                .Select(item => item.ProjectionEventId)
                .ToHashSet(StringComparer.Ordinal);
            var sourceEventId = entry.SingletonEvents.First(item => item.ObservationId == proposal.NewObservationIds[0]).ProjectionEventId;
            if (proposal.Disposition == IncidentBatchEventDisposition.ConfirmedMembership)
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
                    Title = proposal.Title,
                    Summary = proposal.Summary,
                    OperatorVisible = true,
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
                    Title = proposal.Title,
                    Summary = proposal.Summary,
                    OperatorVisible = proposal.Disposition == IncidentBatchEventDisposition.NewEvent,
                    SourceLedgerEntryIds = source.SourceLedgerEntryIds.Append(entry.LedgerEntryId).Distinct(StringComparer.Ordinal).ToList()
                };
                events.RemoveAll(item => item.ProjectionEventId != sourceEventId && singletonIds.Contains(item.ProjectionEventId));
                if (proposal.Disposition == IncidentBatchEventDisposition.ProvisionalAssociation)
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
}

public sealed class IncidentBatchCoordinator
{
    private readonly IIncidentBatchProposer _proposer;
    private readonly IIncidentBatchStore _store;
    private readonly TimeProvider _timeProvider;

    public IncidentBatchCoordinator(IIncidentBatchProposer proposer, IIncidentBatchStore store, TimeProvider? timeProvider = null)
    {
        _proposer = proposer;
        _store = store;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            proposal = await _proposer.ProposeAsync(bundle, newObservationIds, candidates, ct);
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
        var proposalValidation = IncidentBatchContract.ValidateProposal(bundle, newObservationIds, candidates, proposal);
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
            new IncidentBatchExecutionContext(request.SoftwareVersion, request.ConfigurationIdentity, timer.ElapsedMilliseconds, proposerError));
        var entryValidation = IncidentBatchContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", entryValidation.Errors));
        var projection = IncidentBatchProjector.Apply(priorProjection, entry, request.ProjectionId, now);
        return await _store.AppendIncidentBatchRunAsync(entry, projection, ct);
    }
}
