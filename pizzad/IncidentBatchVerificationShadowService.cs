using System.Diagnostics;

namespace pizzad;

public sealed class IncidentBatchVerificationShadowService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger<IncidentBatchVerificationShadowService> _logger;

    public IncidentBatchVerificationShadowService(
        EngineConfig config,
        EngineDatabase database,
        ILogger<IncidentBatchVerificationShadowService> logger)
    {
        _config = config;
        _database = database;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsEnabled())
            {
                await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            try
            {
                var processed = await RunOnceAsync(stoppingToken);
                await DelayAsync(
                    TimeSpan.FromSeconds(processed ? 5 : _config.AiInsights.IncidentBatchVerificationShadowIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Incident batch asynchronous verification failed; request remains pending and production incident state was not changed");
                await DelayAsync(TimeSpan.FromSeconds(_config.AiInsights.IncidentBatchVerificationShadowIntervalSeconds), stoppingToken);
            }
        }
    }

    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var runId = _config.AiInsights.IncidentBatchConstructorShadowRunId.Trim();
        var pending = await _database.ListPendingIncidentBatchVerificationRequestsAsync(runId, 32, ct);
        var storedRequest = pending.FirstOrDefault();
        if (storedRequest is null)
            return false;
        var request = storedRequest.Request;
        var sourceEntry = await _database.GetIncidentBatchLedgerEntryAsync(runId, request.SourceLedgerEntryId, ct)
                          ?? throw new InvalidDataException($"Verification request '{request.RequestId}' references a missing batch ledger entry.");
        if (IncidentBatchGraphVerification.IsEnabled(sourceEntry.Entry.Execution.ConfigurationIdentity))
        {
            var graphRequests = pending
                .Where(item => item.Request.SourceLedgerEntryId == request.SourceLedgerEntryId)
                .Select(item => item.Request)
                .ToList();
            return await RunGraphBatchAsync(runId, sourceEntry.Entry, graphRequests, ct);
        }
        if (request.Kind == IncidentBatchVerificationKind.Relationship)
        {
            var relationshipRequests = pending
                .Where(item => item.Request.Kind == IncidentBatchVerificationKind.Relationship &&
                               item.Request.SourceLedgerEntryId == request.SourceLedgerEntryId)
                .Select(item => item.Request)
                .ToList();
            return await RunRelationshipBatchAsync(runId, sourceEntry.Entry, relationshipRequests, ct);
        }
        if (IncidentBatchStandaloneBatchVerification.IsEnabled(sourceEntry.Entry.Execution.ConfigurationIdentity))
        {
            var standaloneRequests = pending
                .Where(item => item.Request.Kind == IncidentBatchVerificationKind.StandaloneEvent &&
                               item.Request.SourceLedgerEntryId == request.SourceLedgerEntryId)
                .Select(item => item.Request)
                .ToList();
            return await RunStandaloneBatchAsync(runId, sourceEntry.Entry, standaloneRequests, ct);
        }
        var timer = Stopwatch.StartNew();
        IncidentBatchConfirmationProposal? relationshipProposal = null;
        IncidentBatchStandaloneVerificationProposal? standaloneProposal = null;
        try
        {
            if (request.Kind == IncidentBatchVerificationKind.StandaloneEvent)
            {
                var context = IncidentBatchVerificationQueueContract.BuildStandaloneContext(sourceEntry.Entry, request);
                var verifier = new OpenAiIncidentBatchStandaloneVerifier(_config, _database, _logger, runId);
                standaloneProposal = await verifier.VerifyAsync(
                    sourceEntry.Entry.Bundle,
                    context.Event,
                    ct);
            }
            else
            {
                var context = IncidentBatchVerificationQueueContract.BuildContext(sourceEntry.Entry, request);
                var verifier = new OpenAiIncidentBatchConfirmationVerifier(_config, _database, _logger, runId);
                relationshipProposal = await verifier.VerifyAsync(
                    sourceEntry.Entry.Bundle,
                    [context.Source],
                    [context.Candidate],
                    [context.Relationship],
                    ct);
            }
        }
        finally
        {
            timer.Stop();
        }

        var now = DateTimeOffset.UtcNow;
        var execution = new IncidentBatchConfirmationExecutionContext(timer.ElapsedMilliseconds, string.Empty);
        var result = request.Kind == IncidentBatchVerificationKind.StandaloneEvent
            ? IncidentBatchVerificationQueueContract.BuildStandaloneResult(
                sourceEntry.Entry,
                request,
                standaloneProposal ?? throw new InvalidDataException("Standalone verifier returned no proposal."),
                execution,
                now)
            : IncidentBatchVerificationQueueContract.BuildResult(
                sourceEntry.Entry,
                request,
                relationshipProposal ?? throw new InvalidDataException("Relationship verifier returned no proposal."),
                execution,
                now);
        var proposalId = request.Kind == IncidentBatchVerificationKind.StandaloneEvent
            ? standaloneProposal!.ProposalId
            : relationshipProposal!.ProposalId;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var latest = await _database.GetLatestIncidentBatchProjectionAsync(runId, ct)
                         ?? throw new InvalidDataException($"Verification request '{request.RequestId}' has no batch projection.");
            var projection = IncidentBatchVerificationProjector.Apply(
                latest.Projection,
                sourceEntry.Entry,
                request,
                result,
                $"batch-verification:{request.RequestId}:{proposalId}:{attempt}",
                now);
            try
            {
                IncidentBatchStoredCanaryCommit? canaryCommit = null;
                if (_config.AiInsights.IncidentBatchCanaryPersistenceEnabled &&
                    result.Outcome == IncidentBatchVerificationOutcome.Verified &&
                    (request.Kind == IncidentBatchVerificationKind.StandaloneEvent ||
                     request.ProposedDisposition == IncidentBatchEventDisposition.ConfirmedMembership))
                {
                    var persisted = await _database.AppendIncidentBatchVerificationResultWithCanaryAsync(
                        latest.Sequence,
                        sourceEntry.Entry,
                        request,
                        result,
                        projection,
                        ct);
                    canaryCommit = persisted.Commit;
                }
                else
                {
                    await _database.AppendIncidentBatchVerificationResultAsync(
                        latest.Sequence,
                        sourceEntry.Entry,
                        request,
                        result,
                        projection,
                        ct);
                }
                _logger.LogInformation(
                    "Incident batch asynchronous verification completed request {RequestId}: kind={Kind}, outcome={Outcome}, durationMs={DurationMs}, validationErrors={ValidationErrorCount}, canaryPersistence={CanaryPersistence}, canaryIncidentId={CanaryIncidentId}",
                    request.RequestId,
                    request.Kind,
                    result.Outcome,
                    timer.ElapsedMilliseconds,
                    result.ValidationErrors.Count,
                    canaryCommit?.Commit.Outcome.ToString() ?? "none",
                    canaryCommit?.Commit.IncidentId ?? 0);
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("projection advanced", StringComparison.Ordinal) && attempt < 2)
            {
                _logger.LogInformation("Retrying verification projection for request {RequestId} because intake advanced", request.RequestId);
            }
        }
        return false;
    }

    private async Task<bool> RunGraphBatchAsync(
        string runId,
        IncidentBatchLedgerEntry sourceEntry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        CancellationToken ct)
    {
        var expected = IncidentBatchVerificationQueueContract.BuildRequests(sourceEntry);
        if (!expected.Select(item => item.RequestId).Order(StringComparer.Ordinal)
                .SequenceEqual(requests.Select(item => item.RequestId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "Two-pass graph verification requires the complete source-batch request set; partial execution fails closed.");
        }

        var timer = Stopwatch.StartNew();
        IncidentBatchGraphVerificationProposal proposal;
        try
        {
            proposal = await new OpenAiIncidentBatchGraphVerifier(_config, _database, _logger, runId)
                .VerifyAsync(sourceEntry, requests, ct);
        }
        finally
        {
            timer.Stop();
        }

        var results = new Dictionary<string, IncidentBatchVerificationResult>(StringComparer.Ordinal);
        var relationshipIndex = 0;
        foreach (var request in requests.Where(item => item.Kind == IncidentBatchVerificationKind.Relationship))
        {
            var context = IncidentBatchVerificationQueueContract.BuildContext(sourceEntry, request);
            var key = IncidentBatchConfirmationContract.RelationshipKey(context.Relationship);
            var decision = proposal.RelationshipProposal.Decisions
                .Single(item => IncidentBatchConfirmationContract.DecisionKey(item) == key);
            var itemProposal = proposal.RelationshipProposal with
            {
                ProposalId = $"{proposal.RelationshipProposal.ProposalId}:item:{relationshipIndex + 1}",
                Decisions = [decision]
            };
            results.Add(request.RequestId, IncidentBatchVerificationQueueContract.BuildResult(
                sourceEntry,
                request,
                itemProposal,
                new IncidentBatchConfirmationExecutionContext(relationshipIndex == 0 ? timer.ElapsedMilliseconds : 0, string.Empty),
                proposal.RelationshipProposal.GeneratedAtUtc));
            relationshipIndex++;
        }
        foreach (var request in requests.Where(item => item.Kind == IncidentBatchVerificationKind.StandaloneEvent))
        {
            var standalone = proposal.StandaloneProposals[request.RequestId];
            results.Add(request.RequestId, IncidentBatchVerificationQueueContract.BuildStandaloneResult(
                sourceEntry,
                request,
                standalone,
                new IncidentBatchConfirmationExecutionContext(relationshipIndex == 0 ? timer.ElapsedMilliseconds : 0, string.Empty),
                standalone.GeneratedAtUtc));
            relationshipIndex++;
        }

        var terminalPersistence = IncidentBatchGraphVerification.TerminalPersistenceRequests(sourceEntry, requests, results);
        var relatedObservations = IncidentBatchGraphVerification.VerifiedRelationshipObservationIds(sourceEntry, requests, results);
        // Standalones are applied before their graph component, and verified edges
        // are applied newest-to-oldest so a chain collapses without losing an
        // intermediate target projection.
        var ordered = IncidentBatchGraphVerification.ApplicationOrder(sourceEntry, requests, results);
        foreach (var request in ordered)
        {
            var result = results[request.RequestId];
            var proposalId = request.Kind == IncidentBatchVerificationKind.Relationship
                ? result.Proposal.ProposalId
                : result.StandaloneProposal!.ProposalId;
            var componentOwnedStandalone = IncidentBatchGraphVerification.IsOwnedByVerifiedRelationship(
                sourceEntry,
                request,
                relatedObservations);
            var allowCanary = request.Kind == IncidentBatchVerificationKind.StandaloneEvent
                ? !componentOwnedStandalone
                : terminalPersistence.Contains(request.RequestId);
            if (!await AppendResultAsync(
                    sourceEntry,
                    request,
                    result,
                    proposalId,
                    result.RecordedAtUtc,
                    ct,
                    allowCanary,
                    applyToProjection: !componentOwnedStandalone))
            {
                return false;
            }
        }
        _logger.LogInformation(
            "Incident two-pass graph verifier completed {RelationshipCount} relationship and {StandaloneCount} standalone decisions in one model request ({DurationMs} ms); terminalComponents={TerminalCount}",
            requests.Count(item => item.Kind == IncidentBatchVerificationKind.Relationship),
            requests.Count(item => item.Kind == IncidentBatchVerificationKind.StandaloneEvent),
            timer.ElapsedMilliseconds,
            terminalPersistence.Count);
        return ordered.Count > 0;
    }

    private async Task<bool> RunRelationshipBatchAsync(
        string runId,
        IncidentBatchLedgerEntry sourceEntry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        CancellationToken ct)
    {
        var contexts = requests
            .Select(request => IncidentBatchVerificationQueueContract.BuildContext(sourceEntry, request))
            .ToList();
        var sources = contexts.Select(item => item.Source)
            .DistinctBy(item => item.SourceProposalToken, StringComparer.Ordinal)
            .ToList();
        var candidates = contexts.Select(item => item.Candidate)
            .DistinctBy(item => item.CandidateToken, StringComparer.Ordinal)
            .ToList();
        var relationships = contexts.Select(item => item.Relationship).ToList();
        var timer = Stopwatch.StartNew();
        IncidentBatchConfirmationProposal proposal;
        try
        {
            var verifier = new OpenAiIncidentBatchConfirmationVerifier(_config, _database, _logger, runId);
            proposal = await verifier.VerifyAsync(
                sourceEntry.Bundle,
                sources,
                candidates,
                relationships,
                ct);
        }
        finally
        {
            timer.Stop();
        }

        var accepted = IncidentBatchConfirmationContract.AcceptedDecisions(
            sourceEntry.Bundle,
            sources,
            candidates,
            relationships,
            proposal,
            retainOnlyExactEvidence: true);
        var processed = 0;
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var context = contexts[index];
            var key = IncidentBatchConfirmationContract.RelationshipKey(context.Relationship);
            if (!accepted.TryGetValue(key, out var decision))
                throw new InvalidDataException($"Batched relationship verifier omitted '{key}'.");
            var singleProposal = proposal with
            {
                ProposalId = $"{proposal.ProposalId}:item:{index + 1}",
                Decisions = [decision]
            };
            var now = DateTimeOffset.UtcNow;
            var result = IncidentBatchVerificationQueueContract.BuildResult(
                sourceEntry,
                request,
                singleProposal,
                new IncidentBatchConfirmationExecutionContext(index == 0 ? timer.ElapsedMilliseconds : 0, string.Empty),
                now);
            if (!await AppendResultAsync(sourceEntry, request, result, singleProposal.ProposalId, now, ct))
                return false;
            processed++;
        }
        _logger.LogInformation(
            "Incident batch verifier completed {Count} relationship decisions in one model request ({DurationMs} ms)",
            processed,
            timer.ElapsedMilliseconds);
        return processed > 0;
    }

    private async Task<bool> RunStandaloneBatchAsync(
        string runId,
        IncidentBatchLedgerEntry sourceEntry,
        IReadOnlyList<IncidentBatchVerificationRequest> requests,
        CancellationToken ct)
    {
        var events = requests
            .Select(request => IncidentBatchVerificationQueueContract.BuildStandaloneContext(sourceEntry, request).Event)
            .ToList();
        var timer = Stopwatch.StartNew();
        IReadOnlyList<IncidentBatchStandaloneVerificationProposal> proposals;
        try
        {
            proposals = await new OpenAiIncidentBatchStandaloneBatchVerifier(_config, _database, _logger, runId)
                .VerifyAsync(sourceEntry.Bundle, events, ct);
        }
        finally
        {
            timer.Stop();
        }
        for (var index = 0; index < requests.Count; index++)
        {
            var now = DateTimeOffset.UtcNow;
            var result = IncidentBatchVerificationQueueContract.BuildStandaloneResult(
                sourceEntry,
                requests[index],
                proposals[index],
                new IncidentBatchConfirmationExecutionContext(index == 0 ? timer.ElapsedMilliseconds : 0, string.Empty),
                now);
            if (!await AppendResultAsync(sourceEntry, requests[index], result, proposals[index].ProposalId, now, ct))
                return false;
        }
        _logger.LogInformation(
            "Incident batch verifier completed {Count} standalone decisions in one model request ({DurationMs} ms)",
            requests.Count,
            timer.ElapsedMilliseconds);
        return requests.Count > 0;
    }

    private async Task<bool> AppendResultAsync(
        IncidentBatchLedgerEntry sourceEntry,
        IncidentBatchVerificationRequest request,
        IncidentBatchVerificationResult result,
        string proposalId,
        DateTimeOffset now,
        CancellationToken ct,
        bool allowCanaryPersistence = true,
        bool applyToProjection = true)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var latest = await _database.GetLatestIncidentBatchProjectionAsync(request.RunId, ct)
                         ?? throw new InvalidDataException($"Verification request '{request.RequestId}' has no batch projection.");
            var projectionId = $"batch-verification:{request.RequestId}:{proposalId}:{attempt}";
            var projection = applyToProjection
                ? IncidentBatchVerificationProjector.Apply(
                    latest.Projection,
                    sourceEntry,
                    request,
                    result,
                    projectionId,
                    now)
                : latest.Projection with
                {
                    ProjectionId = projectionId,
                    GeneratedAtUtc = now
                };
            try
            {
                if (allowCanaryPersistence &&
                    _config.AiInsights.IncidentBatchCanaryPersistenceEnabled &&
                    result.Outcome == IncidentBatchVerificationOutcome.Verified &&
                    (request.Kind == IncidentBatchVerificationKind.StandaloneEvent ||
                     request.ProposedDisposition == IncidentBatchEventDisposition.ConfirmedMembership))
                {
                    await _database.AppendIncidentBatchVerificationResultWithCanaryAsync(
                        latest.Sequence,
                        sourceEntry,
                        request,
                        result,
                        projection,
                        ct);
                }
                else
                {
                    await _database.AppendIncidentBatchVerificationResultAsync(
                        latest.Sequence,
                        sourceEntry,
                        request,
                        result,
                        projection,
                        ct);
                }
                return true;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("projection advanced", StringComparison.Ordinal) && attempt < 2)
            {
                _logger.LogInformation("Retrying verification projection for request {RequestId} because intake advanced", request.RequestId);
            }
        }
        return false;
    }

    private bool IsEnabled() =>
        _config.Setup.Completed &&
        _config.AiInsights.Enabled &&
        _config.AiInsights.IncidentBatchVerificationShadowEnabled &&
        IncidentBatchExperimentWindow.AllowsExclusiveReplacementWork(
            _config.AiInsights.IncidentBatchConstructorShadowExclusiveInferenceWindow,
            _config.AiInsights.IncidentAnalysisExecutionEnabled) &&
        (!_config.AiInsights.IncidentBatchCanaryPersistenceEnabled ||
         IncidentBatchCanaryGate.AllowsPersistence(_config.AiInsights)) &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.IncidentBatchConstructorShadowRunId) &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel);

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
