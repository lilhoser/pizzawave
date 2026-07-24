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
        var storedRequest = (await _database.ListPendingIncidentBatchVerificationRequestsAsync(runId, 1, ct)).SingleOrDefault();
        if (storedRequest is null)
            return false;
        var request = storedRequest.Request;
        var sourceEntry = await _database.GetIncidentBatchLedgerEntryAsync(runId, request.SourceLedgerEntryId, ct)
                          ?? throw new InvalidDataException($"Verification request '{request.RequestId}' references a missing batch ledger entry.");
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
