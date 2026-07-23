using System.Globalization;
using System.Reflection;
using System.Diagnostics;

namespace pizzad;

public sealed class IncidentBatchConstructorShadowService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EmbeddingService _embeddings;
    private readonly ILogger<IncidentBatchConstructorShadowService> _logger;
    private string _activeRunId = string.Empty;
    private HashSet<long>? _processedCallIds;
    private long _effectiveStartAfterCallId;
    private DateTimeOffset? _pendingSinceUtc;

    public IncidentBatchConstructorShadowService(
        EngineConfig config,
        EngineDatabase database,
        EmbeddingService embeddings,
        ILogger<IncidentBatchConstructorShadowService> logger)
    {
        _config = config;
        _database = database;
        _embeddings = embeddings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsEnabled())
            {
                _pendingSinceUtc = null;
                await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }
            var iterationStarted = Stopwatch.GetTimestamp();
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Incident batch constructor shadow failed; production incident state was not changed");
            }
            await DelayAsync(IncidentBatchShadowCadence.NextDelay(
                _config.AiInsights.IncidentBatchConstructorShadowContinuous,
                _config.AiInsights.IncidentBatchConstructorShadowIntervalSeconds,
                Stopwatch.GetElapsedTime(iterationStarted)), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var runId = _config.AiInsights.IncidentBatchConstructorShadowRunId.Trim();
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-_config.AiInsights.IncidentBatchConstructorShadowLookbackMinutes).ToUnixTimeSeconds();
        var calls = (await _database.ListCallsAsync(start, now.ToUnixTimeSeconds(), null, ct))
            .Where(IncidentBatchLiveSelection.IsEligibleSourceObservation)
            .OrderBy(call => call.Id)
            .ToList();
        if (_processedCallIds is null || !string.Equals(_activeRunId, runId, StringComparison.Ordinal))
        {
            _processedCallIds = (await _database.ListIncidentBatchProcessedCallIdsAsync(runId, ct)).ToHashSet();
            _activeRunId = runId;
            _pendingSinceUtc = null;
            _effectiveStartAfterCallId = IncidentBatchLiveCursor.ResolveStartFence(
                _config.AiInsights.IncidentBatchConstructorShadowStartAfterCallId,
                _processedCallIds,
                calls);
            _logger.LogInformation(
                "Incident batch constructor shadow run {RunId} initialized above fence {EffectiveStartCallId} (configured {ConfiguredStartCallId}) with {ProcessedCount} durably processed observations; continuous={Continuous}",
                runId,
                _effectiveStartAfterCallId,
                _config.AiInsights.IncidentBatchConstructorShadowStartAfterCallId,
                _processedCallIds.Count,
                _config.AiInsights.IncidentBatchConstructorShadowContinuous);
            return;
        }

        if (!_config.AiInsights.IncidentBatchConstructorShadowExclusiveInferenceWindow)
        {
            var queueHealth = await _database.GetIncidentAnalysisQueueHealthAsync(
                _config.AiInsights.IncidentAnalysisMaximumAgeMinutes,
                ct);
            if (!string.Equals(queueHealth.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Incident batch constructor shadow paused because production incident analysis is not current: {Reason}",
                    queueHealth.Message);
                return;
            }
        }

        var batchSize = _config.AiInsights.IncidentBatchConstructorShadowBatchSize;
        var newCalls = IncidentBatchLiveCursor.SelectNext(
            calls,
            _effectiveStartAfterCallId,
            _processedCallIds,
            batchSize);
        if (newCalls.Count == 0)
        {
            _pendingSinceUtc = null;
            return;
        }
        _pendingSinceUtc ??= now;
        if (!IncidentBatchAdmissionPolicy.ShouldProcess(
                _config.AiInsights.IncidentBatchConstructorShadowContinuous,
                newCalls.Count,
                batchSize,
                _config.AiInsights.IncidentBatchConstructorShadowMinimumBatchSize,
                now - _pendingSinceUtc.Value,
                TimeSpan.FromSeconds(_config.AiInsights.IncidentBatchConstructorShadowMaximumWaitSeconds)))
            return;
        var retrievalTimer = Stopwatch.StartNew();
        var priorStored = await _database.GetLatestIncidentBatchProjectionAsync(runId, ct);
        var prior = priorStored?.Projection;
        var matches = new List<VectorSearchMatchDto>();
        var relationshipEnabled = RelationshipEnabled();
        if (prior is not null &&
            (!_config.AiInsights.IncidentBatchConstructorShadowSourceIsolated || relationshipEnabled))
        {
            var matchSets = await _embeddings.SearchSimilarStoredCallsAcrossSystemsBatchAsync(
                newCalls.Select(call => new StoredVectorSearchSource(call.Id, call.Transcription)).ToList(),
                start,
                now.ToUnixTimeSeconds(),
                12,
                ct);
            matches.AddRange(matchSets.SelectMany(items => items));
        }
        var selection = IncidentBatchLiveSelection.BuildConstructorContext(
            newCalls,
            calls,
            matches,
            prior,
            _config.AiInsights.IncidentBatchConstructorShadowCandidateLimit,
            now,
            _config.AiInsights.IncidentBatchConstructorShadowSourceIsolated,
            includeRelationshipCandidates: relationshipEnabled);
        retrievalTimer.Stop();
        var batchIdentity = $"{newCalls.First().Id.ToString(CultureInfo.InvariantCulture)}-{newCalls.Last().Id.ToString(CultureInfo.InvariantCulture)}";
        var singletons = newCalls.Select(call => new IncidentBatchSingletonIdentity(
            $"call:{call.Id.ToString(CultureInfo.InvariantCulture)}",
            $"batch-live:{runId}:event:call:{call.Id.ToString(CultureInfo.InvariantCulture)}")).ToList();
        var proposer = new OpenAiIncidentBatchProposer(
            _config,
            _database,
            _logger,
            runId,
            asynchronousProvisional: true,
            observationIsolated: _config.AiInsights.IncidentBatchConstructorShadowObservationIsolated);
        var store = new IncidentBatchProvisionalStore(_database);
        var coordinator = relationshipEnabled
            ? new IncidentBatchCoordinator(
                proposer,
                new OpenAiIncidentBatchRelationshipProposer(_config, _database, _logger, runId),
                store)
            : new IncidentBatchCoordinator(proposer, store);
        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                runId,
                $"batch-live:{runId}:ledger:{batchIdentity}",
                $"batch-live:{runId}:projection:{batchIdentity}",
                singletons,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                ConfigurationIdentity(),
                retrievalTimer.ElapsedMilliseconds),
            selection.Bundle,
            prior,
            selection.NewObservationIds,
            selection.Candidates,
            ct);
        foreach (var call in newCalls)
            _processedCallIds.Add(call.Id);
        _pendingSinceUtc = null;
        var validEvents = IncidentBatchContract.AcceptedEvents(result.LedgerEntry.Entry);
        var queuedVerificationCount = IncidentBatchVerificationQueueContract.BuildRequests(result.LedgerEntry.Entry).Count;
        _logger.LogInformation(
            "Incident batch constructor shadow run {RunId} processed {CallCount} calls through {LastCallId}: new={NewCount}, review={ProvisionalEventCount}, verificationQueued={VerificationQueuedCount}, unresolved={UnresolvedCount}, candidates={CandidateCount}, retrievalMs={RetrievalDurationMs}, constructorMs={DurationMs}, invalid={Invalid}, proposerError={HasError}; production incident state unchanged",
            runId,
            newCalls.Count,
            newCalls.Max(call => call.Id),
            validEvents.Count(IncidentBatchContract.IsOperatorVisibleNewEvent),
            validEvents.Count(item => IncidentBatchContract.IsOperatorReviewEvent(item) ||
                                      item.Disposition is IncidentBatchEventDisposition.ConfirmedMembership or IncidentBatchEventDisposition.ProvisionalAssociation),
            queuedVerificationCount,
            newCalls.Count - validEvents.SelectMany(item => item.NewObservationIds).Distinct(StringComparer.Ordinal).Count(),
            selection.Candidates.Count,
            result.LedgerEntry.Entry.Execution.RetrievalDurationMilliseconds,
            result.LedgerEntry.Entry.Execution.ProposerDurationMilliseconds,
            result.LedgerEntry.Entry.ProposalValidationErrors.Count > 0,
            !string.IsNullOrWhiteSpace(result.LedgerEntry.Entry.Execution.ProposerError));
    }

    private bool IsEnabled() =>
        _config.Setup.Completed
        && _config.AiInsights.Enabled
        && _config.AiInsights.IncidentBatchConstructorShadowEnabled
        && !string.IsNullOrWhiteSpace(_config.AiInsights.IncidentBatchConstructorShadowRunId)
        && !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl)
        && !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel)
        && (!_config.AiInsights.IncidentBatchRelationshipShadowEnabled ||
            IncidentBatchExperimentWindow.AllowsExclusiveReplacementWork(
                _config.AiInsights.IncidentBatchConstructorShadowExclusiveInferenceWindow,
                _config.AiInsights.IncidentAnalysisExecutionEnabled));

    private string ConfigurationIdentity() =>
        $"{IncidentBatchPrompt.Identity(true, _config.AiInsights.IncidentBatchConstructorShadowObservationIsolated)};{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchContract.PerCitationAcceptanceConfigurationToken};{IncidentBatchContract.EvidenceSummaryProjectionConfigurationToken};cursor=durable-processed-observations-v2;{IncidentBatchContract.CorroboratedVisibilityConfigurationToken};{IncidentTranscriptCitationResolver.ConfigurationToken};{IncidentBatchLiveSelection.ConfigurationToken};{(_config.AiInsights.IncidentBatchRelationshipShadowEnabled ? IncidentBatchExecutionArchitecture.StagedRelationshipAsynchronousConfirmationToken : IncidentBatchExecutionArchitecture.AsynchronousProvisionalToken)};{IncidentBatchAdmissionPolicy.ConfigurationToken};{(_config.AiInsights.IncidentBatchConstructorShadowObservationIsolated ? IncidentBatchContract.ObservationIsolatedOwnershipConfigurationToken : "source-ownership=grouped-v1")};{(_config.AiInsights.IncidentBatchRelationshipShadowEnabled ? IncidentBatchRelationshipContract.ConfigurationToken : "relationship-stage=disabled")};{(_config.AiInsights.IncidentBatchConstructorShadowExclusiveInferenceWindow ? IncidentBatchExperimentWindow.ConfigurationToken : "inference-window=shared-production-v1")};sourceContext={(_config.AiInsights.IncidentBatchConstructorShadowSourceIsolated ? "isolated-v1" : "candidate-aware-v1")};run={_config.AiInsights.IncidentBatchConstructorShadowRunId.Trim()};interval={_config.AiInsights.IncidentBatchConstructorShadowIntervalSeconds};lookback={_config.AiInsights.IncidentBatchConstructorShadowLookbackMinutes};batch={_config.AiInsights.IncidentBatchConstructorShadowBatchSize};minimumBatch={_config.AiInsights.IncidentBatchConstructorShadowMinimumBatchSize};maximumWait={_config.AiInsights.IncidentBatchConstructorShadowMaximumWaitSeconds};candidates={_config.AiInsights.IncidentBatchConstructorShadowCandidateLimit};continuous={_config.AiInsights.IncidentBatchConstructorShadowContinuous};startAfter={_config.AiInsights.IncidentBatchConstructorShadowStartAfterCallId}";

    private bool RelationshipEnabled() =>
        _config.AiInsights.IncidentBatchRelationshipShadowEnabled;

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

public static class IncidentBatchExperimentWindow
{
    public const string ConfigurationToken = "inference-window=exclusive-maintenance-v1";

    public static bool AllowsExclusiveReplacementWork(
        bool exclusiveInferenceWindow,
        bool productionIncidentExecutionEnabled) =>
        exclusiveInferenceWindow && !productionIncidentExecutionEnabled;
}

public static class IncidentBatchAdmissionPolicy
{
    public const string ConfigurationToken = "admission=bounded-dwell-v1";

    public static bool ShouldProcess(
        bool continuous,
        int pendingCount,
        int maximumBatchSize,
        int minimumBatchSize,
        TimeSpan pendingAge,
        TimeSpan maximumWait)
    {
        if (pendingCount <= 0)
            return false;
        if (!continuous)
            return true;
        if (pendingCount >= Math.Max(1, maximumBatchSize))
            return true;
        if (pendingCount >= Math.Clamp(minimumBatchSize, 1, Math.Max(1, maximumBatchSize)))
            return true;
        return pendingAge >= maximumWait;
    }
}

public static class IncidentBatchShadowCadence
{
    public static TimeSpan NextDelay(bool continuous, int intervalSeconds, TimeSpan elapsed)
    {
        if (continuous)
            return TimeSpan.FromSeconds(5);
        var remaining = TimeSpan.FromSeconds(Math.Max(1, intervalSeconds)) - elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(30);
    }
}

public static class IncidentBatchLiveCursor
{
    public static long ResolveStartFence(
        long configuredStartAfterCallId,
        IReadOnlySet<long> processedCallIds,
        IReadOnlyList<EngineCall> eligibleCalls)
    {
        if (configuredStartAfterCallId > 0)
            return configuredStartAfterCallId;
        if (processedCallIds.Count > 0)
            return Math.Max(0, processedCallIds.Min() - 1);
        return eligibleCalls.LastOrDefault()?.Id ?? 0;
    }

    public static IReadOnlyList<EngineCall> SelectNext(
        IReadOnlyList<EngineCall> eligibleCalls,
        long startAfterCallId,
        IReadOnlySet<long> processedCallIds,
        int batchSize) =>
        eligibleCalls
            .Where(call => call.Id > startAfterCallId && !processedCallIds.Contains(call.Id))
            .OrderBy(call => call.Id)
            .Take(Math.Max(1, batchSize))
            .ToList();
}

public sealed record IncidentBatchLiveSelection(
    IncidentEventStateObservationBundle Bundle,
    IReadOnlyList<string> NewObservationIds,
    IReadOnlyList<IncidentBatchCandidate> Candidates)
{
    public const string ConfigurationToken = "candidate-context=balanced-state-v3;retrieval=cross-system-transcript-evidence-v1";

    public static bool IsEligibleSourceObservation(EngineCall call) =>
        TranscriptRetrievalEvidence.IsUsable(call);

    public static IncidentBatchLiveSelection BuildConstructorContext(
        IReadOnlyList<EngineCall> newCalls,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyList<VectorSearchMatchDto> matches,
        IncidentBatchProjection? priorProjection,
        int candidateLimit,
        DateTimeOffset createdAtUtc,
        bool sourceIsolated,
        bool includeRelationshipCandidates = false) =>
        Build(
            newCalls,
            recentCalls,
            sourceIsolated && !includeRelationshipCandidates ? [] : matches,
            sourceIsolated && !includeRelationshipCandidates ? null : priorProjection,
            candidateLimit,
            createdAtUtc);

    public static IncidentBatchLiveSelection Build(
        IReadOnlyList<EngineCall> newCalls,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyList<VectorSearchMatchDto> matches,
        IncidentBatchProjection? priorProjection,
        int candidateLimit,
        DateTimeOffset createdAtUtc)
    {
        var newIds = newCalls.Select(call => ObservationId(call.Id)).ToHashSet(StringComparer.Ordinal);
        var callsByObservation = recentCalls.ToDictionary(call => ObservationId(call.Id), StringComparer.Ordinal);
        var scores = matches
            .Where(match => !newIds.Contains(ObservationId(match.CallId)))
            .GroupBy(match => ObservationId(match.CallId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Score), StringComparer.Ordinal);
        var eligibleGroups = (priorProjection?.Events ?? [])
            .Select(projectedEvent => new
            {
                Event = projectedEvent,
                SourceCalls = projectedEvent.ObservationIds
                    .Where(callsByObservation.ContainsKey)
                    .Select(observationId => new { ObservationId = observationId, Call = callsByObservation[observationId], Score = scores.GetValueOrDefault(observationId, double.NegativeInfinity) })
                    .ToList()
            })
            .Where(item => item.SourceCalls.Count > 0 && (item.Event.OperatorVisible || item.Event.OperatorReview || item.SourceCalls.Any(source => double.IsFinite(source.Score))))
            .ToList();
        var rankedGroups = eligibleGroups
            .OrderByDescending(item => item.SourceCalls.Any(source => double.IsFinite(source.Score)))
            .ThenByDescending(item => item.Event.OperatorVisible)
            .ThenByDescending(item => item.Event.OperatorReview)
            .ThenByDescending(item => item.SourceCalls.Max(source => source.Score))
            .ThenByDescending(item => item.SourceCalls.Max(source => source.Call.StartTime))
            .ThenBy(item => item.Event.ProjectionEventId, StringComparer.Ordinal);
        var stateAnchors = eligibleGroups
            .Where(item => item.Event.OperatorVisible)
            .OrderByDescending(item => item.SourceCalls.Max(source => source.Call.StartTime))
            .Take(1)
            .Concat(eligibleGroups
                .Where(item => item.Event.OperatorReview)
                .OrderByDescending(item => item.SourceCalls.Max(source => source.Call.StartTime))
                .Take(2));
        var groups = stateAnchors
            .Concat(rankedGroups)
            .DistinctBy(item => item.Event.ProjectionEventId, StringComparer.Ordinal)
            .Take(Math.Clamp(candidateLimit, 1, IncidentBatchContract.MaximumCandidateCount))
            .ToList();
        var sourceCalls = newCalls.ToList();
        var candidates = new List<IncidentBatchCandidate>();
        for (var index = 0; index < groups.Count; index++)
        {
            var selected = groups[index].SourceCalls
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Call.StartTime)
                .Take(3)
                .ToList();
            sourceCalls.AddRange(selected.Select(item => item.Call));
            candidates.Add(new IncidentBatchCandidate($"candidate-{index + 1}", groups[index].Event.ProjectionEventId, selected.Select(item => item.ObservationId).ToList()));
        }
        var raw = IncidentEventStateCorpusExporter.BuildObservationBundle(
            $"batch-live:bundle:{newCalls.First().Id.ToString(CultureInfo.InvariantCulture)}-{newCalls.Last().Id.ToString(CultureInfo.InvariantCulture)}",
            createdAtUtc,
            sourceCalls.DistinctBy(call => call.Id));
        var bundle = raw with
        {
            Observations = raw.Observations.Select(observation => observation with
            {
                AudioReference = string.Empty,
                Metadata = new Dictionary<string, IncidentEventStateMetadataObservation>(StringComparer.Ordinal)
            }).ToList()
        };
        return new IncidentBatchLiveSelection(bundle, newCalls.Select(call => ObservationId(call.Id)).ToList(), candidates);
    }

    private static string ObservationId(long callId) => $"call:{callId.ToString(CultureInfo.InvariantCulture)}";
}
