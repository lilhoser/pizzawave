namespace pizzad;

public sealed record IncidentBatchVerificationRequest(
    string RequestId,
    string RunId,
    string SourceLedgerEntryId,
    DateTimeOffset EnqueuedAtUtc,
    string SourceProposalToken,
    string CandidateToken,
    IncidentBatchEventDisposition ProposedDisposition);

public sealed record IncidentBatchStoredVerificationRequest(
    long Sequence,
    string ContentHash,
    IncidentBatchVerificationRequest Request);

public enum IncidentBatchVerificationOutcome
{
    Rejected,
    Verified,
    Invalid,
    Review
}

public sealed record IncidentBatchVerificationResult(
    string ResultId,
    string RequestId,
    string RunId,
    DateTimeOffset RecordedAtUtc,
    IncidentBatchVerificationOutcome Outcome,
    IncidentBatchConfirmationProposal Proposal,
    IReadOnlyList<string> ValidationErrors,
    IncidentBatchConfirmationExecutionContext Execution);

public sealed record IncidentBatchStoredVerificationResult(
    long Sequence,
    string ContentHash,
    IncidentBatchVerificationResult Result);

public sealed record IncidentBatchVerificationContext(
    IncidentBatchRelationshipSource Source,
    IncidentBatchCandidate Candidate,
    IncidentBatchRelationship Relationship);

public static class IncidentBatchExecutionArchitecture
{
    public const string AsynchronousProvisionalToken = "execution=asynchronous-provisional-v1";
    public const string StagedRelationshipAsynchronousConfirmationToken =
        "execution=staged-relationship-async-confirmation-v1";

    public static bool UsesAsynchronousProvisionalVerification(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(token => string.Equals(token, AsynchronousProvisionalToken, StringComparison.Ordinal) ||
                          string.Equals(token, StagedRelationshipAsynchronousConfirmationToken, StringComparison.Ordinal));

    public static bool UsesSourceOnlyAsynchronousIntake(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(AsynchronousProvisionalToken, StringComparer.Ordinal);
}

public static class IncidentBatchVerificationQueueContract
{
    public static IReadOnlyList<IncidentBatchVerificationRequest> BuildRequests(IncidentBatchLedgerEntry entry)
    {
        if (!IncidentBatchExecutionArchitecture.UsesAsynchronousProvisionalVerification(entry.Execution.ConfigurationIdentity))
            return [];

        if (entry.RelationshipProposal is not null)
        {
            var sources = IncidentBatchContract.AcceptedEvents(entry)
                .Select(item => new IncidentBatchRelationshipSource(item.ProposalToken, item.NewObservationIds))
                .ToList();
            return IncidentBatchRelationshipContract.AcceptedRelationships(
                    entry.Bundle,
                    sources,
                    entry.Candidates,
                    entry.RelationshipProposal)
                .Select(item => new IncidentBatchVerificationRequest(
                    $"{entry.LedgerEntryId}:verify:{item.SourceProposalToken}:{item.CandidateToken}",
                    entry.RunId,
                    entry.LedgerEntryId,
                    entry.RecordedAtUtc,
                    item.SourceProposalToken,
                    item.CandidateToken,
                    item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership
                        ? IncidentBatchEventDisposition.ConfirmedMembership
                        : IncidentBatchEventDisposition.ProvisionalAssociation))
                .ToList();
        }

        return IncidentBatchContract.AcceptedEvents(entry)
            .Where(item => item.Disposition is IncidentBatchEventDisposition.ConfirmedMembership or IncidentBatchEventDisposition.ProvisionalAssociation)
            .Select(item => new IncidentBatchVerificationRequest(
                $"{entry.LedgerEntryId}:verify:{item.ProposalToken}:{item.CandidateToken}",
                entry.RunId,
                entry.LedgerEntryId,
                entry.RecordedAtUtc,
                item.ProposalToken,
                item.CandidateToken,
                item.Disposition))
            .ToList();
    }

    public static IncidentEventStateContractValidationResult Validate(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests)
    {
        var errors = new List<string>();
        var expected = BuildRequests(entry).ToDictionary(item => item.RequestId, StringComparer.Ordinal);
        var actualIds = requests.Select(item => item.RequestId).ToList();
        foreach (var duplicate in actualIds.GroupBy(item => item, StringComparer.Ordinal).Where(group => group.Count() > 1))
            errors.Add($"duplicate batch verification request '{duplicate.Key}'");
        foreach (var missing in expected.Keys.Except(actualIds, StringComparer.Ordinal))
            errors.Add($"missing batch verification request '{missing}'");
        foreach (var unknown in actualIds.Except(expected.Keys, StringComparer.Ordinal))
            errors.Add($"unknown batch verification request '{unknown}'");
        foreach (var request in requests.Where(item => expected.ContainsKey(item.RequestId)))
        {
            if (request != expected[request.RequestId])
                errors.Add($"batch verification request '{request.RequestId}' does not match its accepted proposal");
        }
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentBatchVerificationContext BuildContext(
        IncidentBatchLedgerEntry entry,
        IncidentBatchVerificationRequest request)
    {
        var entryValidation = IncidentBatchContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", entryValidation.Errors));
        var expected = BuildRequests(entry).SingleOrDefault(item => item.RequestId == request.RequestId)
                       ?? throw new ArgumentException("verification request does not belong to its source ledger entry", nameof(request));
        if (expected != request)
            throw new ArgumentException("verification request does not match its source proposal", nameof(request));
        var proposal = IncidentBatchContract.AcceptedEvents(entry)
            .Single(item => item.ProposalToken == request.SourceProposalToken);
        var candidate = entry.Candidates.Single(item => item.CandidateToken == request.CandidateToken);
        var source = new IncidentBatchRelationshipSource(proposal.ProposalToken, proposal.NewObservationIds);
        if (entry.RelationshipProposal is not null)
        {
            var stagedRelationship = IncidentBatchRelationshipContract.AcceptedRelationships(
                    entry.Bundle,
                    IncidentBatchContract.AcceptedEvents(entry)
                        .Select(item => new IncidentBatchRelationshipSource(item.ProposalToken, item.NewObservationIds))
                        .ToList(),
                    entry.Candidates,
                    entry.RelationshipProposal)
                .Single(item =>
                    item.SourceProposalToken == request.SourceProposalToken &&
                    item.CandidateToken == request.CandidateToken);
            return new IncidentBatchVerificationContext(source, candidate, stagedRelationship);
        }
        var disposition = request.ProposedDisposition == IncidentBatchEventDisposition.ConfirmedMembership
            ? IncidentBatchRelationshipDisposition.ConfirmedMembership
            : IncidentBatchRelationshipDisposition.ProvisionalAssociation;
        var relationship = new IncidentBatchRelationship(
            proposal.ProposalToken,
            candidate.CandidateToken,
            disposition,
            proposal.RelationshipStatement,
            proposal.Uncertainty,
            proposal.NewObservationEvidence,
            proposal.CandidateEvidence,
            proposal.AlternativeInterpretations,
            proposal.UnresolvedQuestions);
        return new IncidentBatchVerificationContext(source, candidate, relationship);
    }

    public static IncidentBatchVerificationResult BuildResult(
        IncidentBatchLedgerEntry entry,
        IncidentBatchVerificationRequest request,
        IncidentBatchConfirmationProposal proposal,
        IncidentBatchConfirmationExecutionContext execution,
        DateTimeOffset recordedAtUtc)
    {
        var context = BuildContext(entry, request);
        var validation = IncidentBatchConfirmationContract.ValidateProposal(
            entry.Bundle,
            [context.Source],
            [context.Candidate],
            [context.Relationship],
            proposal);
        var acceptedDecisions = validation.IsValid
            ? IncidentBatchConfirmationContract.AcceptedDecisions(
                entry.Bundle,
                [context.Source],
                [context.Candidate],
                [context.Relationship],
                proposal,
                retainOnlyExactEvidence: true)
            : new Dictionary<string, IncidentBatchConfirmationDecision>(StringComparer.Ordinal);
        acceptedDecisions.TryGetValue(
            IncidentBatchConfirmationContract.RelationshipKey(context.Relationship),
            out var acceptedDecision);
        var outcome = !validation.IsValid
            ? IncidentBatchVerificationOutcome.Invalid
            : acceptedDecision?.Decision switch
            {
                IncidentBatchConfirmationDecisionKind.Verify => IncidentBatchVerificationOutcome.Verified,
                IncidentBatchConfirmationDecisionKind.Review => IncidentBatchVerificationOutcome.Review,
                _ => IncidentBatchVerificationOutcome.Rejected
            };
        return new IncidentBatchVerificationResult(
            $"{request.RequestId}:result:{proposal.ProposalId}",
            request.RequestId,
            request.RunId,
            recordedAtUtc,
            outcome,
            proposal,
            validation.Errors,
            execution);
    }

    public static IncidentEventStateContractValidationResult ValidateResult(
        IncidentBatchLedgerEntry entry,
        IncidentBatchVerificationRequest request,
        IncidentBatchVerificationResult result)
    {
        var errors = new List<string>();
        if (result.RequestId != request.RequestId || result.RunId != request.RunId)
            errors.Add("batch verification result does not belong to its request");
        if (string.IsNullOrWhiteSpace(result.ResultId))
            errors.Add("batch verification result id is required");
        if (result.RecordedAtUtc == default)
            errors.Add("batch verification result timestamp is required");
        if (result.Execution.VerifierDurationMilliseconds < 0)
            errors.Add("batch verification duration cannot be negative");
        var expected = BuildResult(entry, request, result.Proposal, result.Execution, result.RecordedAtUtc);
        if (result.Outcome != expected.Outcome || !result.ValidationErrors.SequenceEqual(expected.ValidationErrors, StringComparer.Ordinal))
            errors.Add("batch verification result does not match deterministic validation");
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }
}

public sealed class IncidentBatchProvisionalStore(EngineDatabase database) : IIncidentBatchStore
{
    public Task<IncidentBatchRunResult> AppendIncidentBatchRunAsync(
        IncidentBatchLedgerEntry entry,
        IncidentBatchProjection projection,
        CancellationToken ct) =>
        database.AppendIncidentBatchRunWithVerificationRequestsAsync(
            entry,
            projection,
            IncidentBatchVerificationQueueContract.BuildRequests(entry),
            ct);
}

public static class IncidentBatchVerificationProjector
{
    public static IncidentBatchProjection Apply(
        IncidentBatchProjection prior,
        IncidentBatchLedgerEntry sourceEntry,
        IncidentBatchVerificationRequest request,
        IncidentBatchVerificationResult result,
        string projectionId,
        DateTimeOffset generatedAtUtc)
    {
        var resultValidation = IncidentBatchVerificationQueueContract.ValidateResult(sourceEntry, request, result);
        if (!resultValidation.IsValid)
            throw new ArgumentException(string.Join("; ", resultValidation.Errors), nameof(result));
        var events = prior.Events.Select(item => item with
        {
            ObservationIds = item.ObservationIds.ToList(),
            SourceLedgerEntryIds = item.SourceLedgerEntryIds.ToList()
        }).ToList();
        var links = prior.ProvisionalAssociations.ToList();
        var associationId = sourceEntry.RelationshipProposal is null
            ? $"{sourceEntry.LedgerEntryId}:{request.SourceProposalToken}"
            : $"{sourceEntry.LedgerEntryId}:{request.SourceProposalToken}:{request.CandidateToken}";
        var context = IncidentBatchVerificationQueueContract.BuildContext(sourceEntry, request);
        var sourceObservationIds = context.Source.NewObservationIds.ToHashSet(StringComparer.Ordinal);
        var pendingLink = links.SingleOrDefault(item => item.AssociationId == associationId);
        var sourceIndex = pendingLink is null
            ? events.FindIndex(item => sourceObservationIds.IsSubsetOf(item.ObservationIds))
            : events.FindIndex(item => item.ProjectionEventId == pendingLink.SourceProjectionEventId);
        var resolvedTargetEventId = pendingLink?.CandidateProjectionEventId ?? context.Candidate.ProjectionEventId;
        var targetIndex = events.FindIndex(item => item.ProjectionEventId == resolvedTargetEventId);

        if (result.Outcome == IncidentBatchVerificationOutcome.Rejected ||
            (result.Outcome == IncidentBatchVerificationOutcome.Verified &&
             request.ProposedDisposition == IncidentBatchEventDisposition.ConfirmedMembership))
            links.RemoveAll(item => item.AssociationId == associationId);

        if (result.Outcome == IncidentBatchVerificationOutcome.Review && pendingLink is not null)
        {
            var acceptedDecision = IncidentBatchConfirmationContract.AcceptedDecisions(
                    sourceEntry.Bundle,
                    [context.Source],
                    [context.Candidate],
                    [context.Relationship],
                    result.Proposal,
                    retainOnlyExactEvidence: true)
                .Values
                .Single();
            var reviewed = IncidentBatchConfirmationContract.ApplyAcceptedDecision(
                context.Relationship,
                acceptedDecision);
            links = links
                .Select(item => item.AssociationId == associationId
                    ? item with
                    {
                        RelationshipStatement = reviewed.RelationshipStatement,
                        Uncertainty = reviewed.Uncertainty,
                        NewObservationEvidence = reviewed.SourceEvidence,
                        CandidateEvidence = reviewed.CandidateEvidence,
                        AlternativeInterpretations = reviewed.AlternativeInterpretations,
                        UnresolvedQuestions = reviewed.UnresolvedQuestions
                    }
                    : item)
                .ToList();
        }

        if (result.Outcome == IncidentBatchVerificationOutcome.Verified &&
            request.ProposedDisposition == IncidentBatchEventDisposition.ConfirmedMembership &&
            sourceIndex >= 0 && targetIndex >= 0 && sourceIndex != targetIndex)
        {
            var source = events[sourceIndex];
            var target = events[targetIndex];
            events[targetIndex] = target with
            {
                ObservationIds = target.ObservationIds.Concat(source.ObservationIds).Distinct(StringComparer.Ordinal).ToList(),
                Title = string.IsNullOrWhiteSpace(target.Title) ? source.Title : target.Title,
                Summary = IncidentBatchProjector.AppendEvidenceSummary(target.Summary, source.Summary),
                OperatorVisible = true,
                OperatorReview = false,
                SourceLedgerEntryIds = target.SourceLedgerEntryIds.Concat(source.SourceLedgerEntryIds).Distinct(StringComparer.Ordinal).ToList()
            };
            var sourceEventId = source.ProjectionEventId;
            var targetEventId = target.ProjectionEventId;
            events.RemoveAt(sourceIndex);
            links = links
                .Select(item => item with
                {
                    SourceProjectionEventId = item.SourceProjectionEventId == sourceEventId ? targetEventId : item.SourceProjectionEventId,
                    CandidateProjectionEventId = item.CandidateProjectionEventId == sourceEventId ? targetEventId : item.CandidateProjectionEventId
                })
                .Where(item => item.SourceProjectionEventId != item.CandidateProjectionEventId)
                .DistinctBy(item => new { item.SourceProjectionEventId, item.CandidateProjectionEventId, item.SourceLedgerEntryId })
                .ToList();
        }

        var projection = new IncidentBatchProjection(
            prior.RunId,
            projectionId,
            generatedAtUtc,
            prior.LedgerEntryIds.ToList(),
            events,
            links);
        var validation = IncidentBatchContract.ValidateProjection(projection);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));
        return projection;
    }
}
