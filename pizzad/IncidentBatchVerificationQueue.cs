namespace pizzad;

public enum IncidentBatchVerificationKind
{
    Relationship,
    StandaloneEvent
}

public sealed record IncidentBatchVerificationRequest(
    string RequestId,
    string RunId,
    string SourceLedgerEntryId,
    DateTimeOffset EnqueuedAtUtc,
    string SourceProposalToken,
    string CandidateToken,
    IncidentBatchEventDisposition ProposedDisposition,
    IncidentBatchVerificationKind Kind = IncidentBatchVerificationKind.Relationship);

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
    IncidentBatchConfirmationExecutionContext Execution,
    IncidentBatchStandaloneVerificationProposal? StandaloneProposal = null);

public sealed record IncidentBatchStoredVerificationResult(
    long Sequence,
    string ContentHash,
    IncidentBatchVerificationResult Result);

public sealed record IncidentBatchVerificationContext(
    IncidentBatchRelationshipSource Source,
    IncidentBatchCandidate Candidate,
    IncidentBatchRelationship Relationship);

public sealed record IncidentBatchStandaloneVerificationContext(
    IncidentBatchEventProposal Event);

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

        var acceptedEvents = IncidentBatchContract.AcceptedEvents(entry).ToList();
        if (entry.RelationshipProposal is not null)
        {
            var sources = IncidentBatchRelationshipContract.BuildSources(entry);
            var relationships = IncidentBatchRelationshipContract.AcceptedRelationships(
                    entry.Bundle,
                    sources,
                    entry.Candidates,
                    entry.RelationshipProposal)
                .ToList();
            var relatedObservationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var relationship in relationships)
            {
                foreach (var observationId in sources
                             .Single(item => item.SourceProposalToken == relationship.SourceProposalToken)
                             .NewObservationIds)
                {
                    relatedObservationIds.Add(observationId);
                }
                foreach (var observationId in entry.Candidates
                             .Single(item => item.CandidateToken == relationship.CandidateToken)
                             .ObservationIds)
                {
                    relatedObservationIds.Add(observationId);
                }
            }

            return BuildStandaloneRequests(entry, acceptedEvents, relatedObservationIds)
                .Concat(relationships.Select(item => new IncidentBatchVerificationRequest(
                    $"{entry.LedgerEntryId}:verify:{item.SourceProposalToken}:{item.CandidateToken}",
                    entry.RunId,
                    entry.LedgerEntryId,
                    entry.RecordedAtUtc,
                    item.SourceProposalToken,
                    item.CandidateToken,
                    item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership
                        ? IncidentBatchEventDisposition.ConfirmedMembership
                        : IncidentBatchEventDisposition.ProvisionalAssociation,
                    IncidentBatchVerificationKind.Relationship)))
                .ToList();
        }

        return BuildStandaloneRequests(entry, acceptedEvents, new HashSet<string>(StringComparer.Ordinal))
            .Concat(acceptedEvents
            .Where(item => item.Disposition is IncidentBatchEventDisposition.ConfirmedMembership or IncidentBatchEventDisposition.ProvisionalAssociation)
            .Select(item => new IncidentBatchVerificationRequest(
                $"{entry.LedgerEntryId}:verify:{item.ProposalToken}:{item.CandidateToken}",
                entry.RunId,
                entry.LedgerEntryId,
                entry.RecordedAtUtc,
                item.ProposalToken,
                item.CandidateToken,
                item.Disposition,
                IncidentBatchVerificationKind.Relationship)))
            .ToList();
    }

    private static IEnumerable<IncidentBatchVerificationRequest> BuildStandaloneRequests(
        IncidentBatchLedgerEntry entry,
        IReadOnlyList<IncidentBatchEventProposal> acceptedEvents,
        IReadOnlySet<string> relatedObservationIds)
    {
        if (!IncidentBatchStandaloneVerificationContract.IsEnabled(entry.Execution.ConfigurationIdentity))
            return [];
        return acceptedEvents
            .Where(item => item.Disposition is IncidentBatchEventDisposition.NewEvent or IncidentBatchEventDisposition.ProvisionalEvent)
            .Where(item => !item.NewObservationIds.Any(relatedObservationIds.Contains))
            .Select(item => new IncidentBatchVerificationRequest(
                $"{entry.LedgerEntryId}:verify-event:{item.ProposalToken}",
                entry.RunId,
                entry.LedgerEntryId,
                entry.RecordedAtUtc,
                item.ProposalToken,
                string.Empty,
                item.Disposition,
                IncidentBatchVerificationKind.StandaloneEvent));
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
        if (request.Kind != IncidentBatchVerificationKind.Relationship)
            throw new ArgumentException("relationship context requires a relationship verification request", nameof(request));
        var entryValidation = IncidentBatchContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", entryValidation.Errors));
        var expected = BuildRequests(entry).SingleOrDefault(item => item.RequestId == request.RequestId)
                       ?? throw new ArgumentException("verification request does not belong to its source ledger entry", nameof(request));
        if (expected != request)
            throw new ArgumentException("verification request does not match its source proposal", nameof(request));
        var candidate = entry.Candidates.Single(item => item.CandidateToken == request.CandidateToken);
        if (entry.RelationshipProposal is not null)
        {
            var sources = IncidentBatchRelationshipContract.BuildSources(entry);
            var stagedSource = sources.Single(item => item.SourceProposalToken == request.SourceProposalToken);
            var stagedRelationship = IncidentBatchRelationshipContract.AcceptedRelationships(
                    entry.Bundle,
                    sources,
                    entry.Candidates,
                    entry.RelationshipProposal)
                .Single(item =>
                    item.SourceProposalToken == request.SourceProposalToken &&
                    item.CandidateToken == request.CandidateToken);
            return new IncidentBatchVerificationContext(stagedSource, candidate, stagedRelationship);
        }
        var proposal = IncidentBatchContract.AcceptedEvents(entry)
            .Single(item => item.ProposalToken == request.SourceProposalToken);
        var legacySource = new IncidentBatchRelationshipSource(proposal.ProposalToken, proposal.NewObservationIds);
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
        return new IncidentBatchVerificationContext(legacySource, candidate, relationship);
    }

    public static IncidentBatchStandaloneVerificationContext BuildStandaloneContext(
        IncidentBatchLedgerEntry entry,
        IncidentBatchVerificationRequest request)
    {
        if (request.Kind != IncidentBatchVerificationKind.StandaloneEvent)
            throw new ArgumentException("standalone context requires a standalone event verification request", nameof(request));
        var entryValidation = IncidentBatchContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", entryValidation.Errors));
        var expected = BuildRequests(entry).SingleOrDefault(item => item.RequestId == request.RequestId)
                       ?? throw new ArgumentException("verification request does not belong to its source ledger entry", nameof(request));
        if (expected != request)
            throw new ArgumentException("verification request does not match its source proposal", nameof(request));
        var proposal = IncidentBatchContract.AcceptedEvents(entry)
            .Single(item => item.ProposalToken == request.SourceProposalToken);
        if (proposal.Disposition is not (IncidentBatchEventDisposition.NewEvent or IncidentBatchEventDisposition.ProvisionalEvent))
            throw new InvalidDataException("standalone verification source is not an independently proposed event");
        return new IncidentBatchStandaloneVerificationContext(proposal);
    }

    public static IncidentBatchVerificationResult BuildResult(
        IncidentBatchLedgerEntry entry,
        IncidentBatchVerificationRequest request,
        IncidentBatchConfirmationProposal proposal,
        IncidentBatchConfirmationExecutionContext execution,
        DateTimeOffset recordedAtUtc)
    {
        if (request.Kind != IncidentBatchVerificationKind.Relationship)
            throw new ArgumentException("relationship result requires a relationship verification request", nameof(request));
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
            execution,
            null);
    }

    public static IncidentBatchVerificationResult BuildStandaloneResult(
        IncidentBatchLedgerEntry entry,
        IncidentBatchVerificationRequest request,
        IncidentBatchStandaloneVerificationProposal proposal,
        IncidentBatchConfirmationExecutionContext execution,
        DateTimeOffset recordedAtUtc)
    {
        var context = BuildStandaloneContext(entry, request);
        var validation = IncidentBatchStandaloneVerificationContract.ValidateProposal(
            entry.Bundle,
            context.Event,
            proposal);
        var acceptedDecision = validation.IsValid
            ? IncidentBatchStandaloneVerificationContract.AcceptedDecision(
                entry.Bundle,
                context.Event,
                proposal)
            : null;
        var outcome = !validation.IsValid
            ? IncidentBatchVerificationOutcome.Invalid
            : acceptedDecision?.Decision switch
            {
                IncidentBatchConfirmationDecisionKind.Verify => IncidentBatchVerificationOutcome.Verified,
                IncidentBatchConfirmationDecisionKind.Review => IncidentBatchVerificationOutcome.Review,
                _ => IncidentBatchVerificationOutcome.Rejected
            };
        var emptyRelationshipProposal = new IncidentBatchConfirmationProposal(
            $"application:standalone:{proposal.ProposalId}",
            proposal.GeneratedAtUtc,
            proposal.ModelIdentity,
            IncidentBatchConfirmationPrompt.PromptIdentity,
            []);
        return new IncidentBatchVerificationResult(
            $"{request.RequestId}:result:{proposal.ProposalId}",
            request.RequestId,
            request.RunId,
            recordedAtUtc,
            outcome,
            emptyRelationshipProposal,
            validation.Errors,
            execution,
            proposal);
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
        var expected = request.Kind == IncidentBatchVerificationKind.StandaloneEvent
            ? result.StandaloneProposal is null
                ? null
                : BuildStandaloneResult(
                    entry,
                    request,
                    result.StandaloneProposal,
                    result.Execution,
                    result.RecordedAtUtc)
            : result.StandaloneProposal is not null
                ? null
                : BuildResult(entry, request, result.Proposal, result.Execution, result.RecordedAtUtc);
        if (expected is null)
        {
            errors.Add("batch verification result payload does not match its request kind");
            return new IncidentEventStateContractValidationResult(false, errors);
        }
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
        if (request.Kind == IncidentBatchVerificationKind.StandaloneEvent)
            return ApplyStandalone(prior, sourceEntry, request, result, projectionId, generatedAtUtc);
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

        var constructorAcceptedSource = IncidentBatchContract.AcceptedEvents(sourceEntry)
            .Any(item => item.ProposalToken == request.SourceProposalToken);
        if (result.Outcome == IncidentBatchVerificationOutcome.Rejected &&
            !constructorAcceptedSource &&
            sourceIndex >= 0)
        {
            var sourceEventId = events[sourceIndex].ProjectionEventId;
            var sourceRetainsAnotherRelationship = links.Any(item =>
                item.SourceProjectionEventId == sourceEventId ||
                item.CandidateProjectionEventId == sourceEventId);
            if (!sourceRetainsAnotherRelationship)
            {
                var source = events[sourceIndex];
                events[sourceIndex] = source with
                {
                    Title = string.Empty,
                    Summary = string.Empty,
                    OperatorVisible = false,
                    OperatorReview = false
                };
            }
        }

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
            var acceptedDecision = IncidentBatchConfirmationContract.AcceptedDecisions(
                    sourceEntry.Bundle,
                    [context.Source],
                    [context.Candidate],
                    [context.Relationship],
                    result.Proposal,
                    retainOnlyExactEvidence: true)
                .Values
                .Single();
            var source = events[sourceIndex];
            var target = events[targetIndex];
            events[targetIndex] = target with
            {
                ObservationIds = target.ObservationIds.Concat(source.ObservationIds).Distinct(StringComparer.Ordinal).ToList(),
                Title = string.IsNullOrWhiteSpace(acceptedDecision.DisplayTitle)
                    ? string.IsNullOrWhiteSpace(target.Title) ? source.Title : target.Title
                    : acceptedDecision.DisplayTitle.Trim(),
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

    private static IncidentBatchProjection ApplyStandalone(
        IncidentBatchProjection prior,
        IncidentBatchLedgerEntry sourceEntry,
        IncidentBatchVerificationRequest request,
        IncidentBatchVerificationResult result,
        string projectionId,
        DateTimeOffset generatedAtUtc)
    {
        var context = IncidentBatchVerificationQueueContract.BuildStandaloneContext(sourceEntry, request);
        var decision = result.StandaloneProposal is null
            ? null
            : IncidentBatchStandaloneVerificationContract.AcceptedDecision(
                sourceEntry.Bundle,
                context.Event,
                result.StandaloneProposal);
        if (decision is null && result.Outcome != IncidentBatchVerificationOutcome.Invalid)
            throw new InvalidDataException("standalone verification result has no accepted decision");
        var events = prior.Events.Select(item => item with
        {
            ObservationIds = item.ObservationIds.ToList(),
            SourceLedgerEntryIds = item.SourceLedgerEntryIds.ToList()
        }).ToList();
        var sourceObservationIds = context.Event.NewObservationIds.ToHashSet(StringComparer.Ordinal);
        var sourceIndex = events.FindIndex(item => sourceObservationIds.IsSubsetOf(item.ObservationIds));
        if (sourceIndex < 0)
            throw new InvalidDataException("standalone verification source is absent from the projection");
        var source = events[sourceIndex];
        events[sourceIndex] = result.Outcome switch
        {
            IncidentBatchVerificationOutcome.Verified => source with
            {
                Title = decision!.DisplayTitle.Trim(),
                OperatorVisible = true,
                OperatorReview = false
            },
            IncidentBatchVerificationOutcome.Review => source with
            {
                Title = decision!.DisplayTitle.Trim(),
                OperatorVisible = false,
                OperatorReview = true
            },
            _ => source with
            {
                Title = string.Empty,
                Summary = string.Empty,
                OperatorVisible = false,
                OperatorReview = false
            }
        };
        var projection = new IncidentBatchProjection(
            prior.RunId,
            projectionId,
            generatedAtUtc,
            prior.LedgerEntryIds.ToList(),
            events,
            prior.ProvisionalAssociations.ToList());
        var validation = IncidentBatchContract.ValidateProjection(projection);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));
        return projection;
    }
}
