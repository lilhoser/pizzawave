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
    private DateTimeOffset _nextPendingVerificationLogAt = DateTimeOffset.MinValue;

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

        if (_config.AiInsights.IncidentBatchVerificationShadowEnabled)
        {
            var pendingVerification = (await _database.ListPendingIncidentBatchVerificationRequestsAsync(
                runId,
                1,
                ct)).SingleOrDefault();
            if (pendingVerification is not null &&
                !IncidentBatchProjectionWriteGate.CanStartIntake(1))
            {
                if (now >= _nextPendingVerificationLogAt)
                {
                    _logger.LogInformation(
                        "Incident batch constructor is waiting for verification request {RequestId} so intake cannot overwrite a newer verified projection",
                        pendingVerification.Request.RequestId);
                    _nextPendingVerificationLogAt = now.AddMinutes(1);
                }
                return;
            }
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
            includeRelationshipCandidates: relationshipEnabled,
            includePendingCandidates: _config.AiInsights.IncidentBatchRollingHypothesisEnabled,
            candidateConsiderationCounts: _config.AiInsights.IncidentBatchRollingHypothesisEnabled
                ? await LoadCandidateConsiderationCountsAsync(runId, ct)
                : null,
            maximumEvidencePerCandidate: _config.AiInsights.IncidentBatchRollingHypothesisEnabled ? 1 : 3);
        retrievalTimer.Stop();
        var batchIdentity = $"{newCalls.First().Id.ToString(CultureInfo.InvariantCulture)}-{newCalls.Last().Id.ToString(CultureInfo.InvariantCulture)}";
        var reconsideredCandidateByObservation = selection.Candidates
            .Where(candidate => !candidate.OperatorVisible)
            .SelectMany(candidate => candidate.ObservationIds.Select(observationId => (ObservationId: observationId, Candidate: candidate)))
            .ToDictionary(item => item.ObservationId, item => item.Candidate, StringComparer.Ordinal);
        var singletons = selection.NewObservationIds.Select(observationId =>
        {
            if (reconsideredCandidateByObservation.TryGetValue(observationId, out var candidate))
                return new IncidentBatchSingletonIdentity(observationId, candidate.ProjectionEventId);
            var callId = observationId["call:".Length..];
            return new IncidentBatchSingletonIdentity(
                observationId,
                $"batch-live:{runId}:event:call:{callId}");
        }).ToList();
        IIncidentBatchProposer proposer = _config.AiInsights.IncidentBatchRollingHypothesisEnabled
            ? new IncidentRollingBatchProposerAdapter(_config, _database, _logger, runId)
            : _config.AiInsights.IncidentBatchExhaustiveBatchedIntakeEnabled
                ? new ApplicationIncidentBatchExhaustiveSourceProposer()
                : new OpenAiIncidentBatchProposer(
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
                retrievalTimer.ElapsedMilliseconds,
                priorStored?.Sequence ?? 0),
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
        && (!_config.AiInsights.IncidentBatchCanaryPersistenceEnabled ||
            IncidentBatchCanaryGate.AllowsPersistence(_config.AiInsights))
        && (!_config.AiInsights.IncidentBatchRelationshipShadowEnabled ||
            IncidentBatchExperimentWindow.AllowsExclusiveReplacementWork(
                _config.AiInsights.IncidentBatchConstructorShadowExclusiveInferenceWindow,
                _config.AiInsights.IncidentAnalysisExecutionEnabled));

    private string ConfigurationIdentity() =>
        $"{(_config.AiInsights.IncidentBatchRollingHypothesisEnabled ? IncidentRollingHypothesis.PromptIdentity : _config.AiInsights.IncidentBatchExhaustiveBatchedIntakeEnabled ? ApplicationIncidentBatchExhaustiveSourceProposer.PromptIdentity : IncidentBatchPrompt.Identity(true, _config.AiInsights.IncidentBatchConstructorShadowObservationIsolated))};{(_config.AiInsights.IncidentBatchRollingHypothesisEnabled ? IncidentRollingHypothesis.ConfigurationToken : "membership=legacy-batch-contract-v1")};{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchContract.PerCitationAcceptanceConfigurationToken};{IncidentBatchContract.EvidenceSummaryProjectionConfigurationToken};cursor=durable-processed-observations-v2;{IncidentBatchProjectionWriteGate.ConfigurationToken};{IncidentBatchContract.CorroboratedVisibilityConfigurationToken};{IncidentTranscriptCitationResolver.ConfigurationToken};{IncidentBatchLiveSelection.ConfigurationToken};{(_config.AiInsights.IncidentBatchRollingHypothesisEnabled ? IncidentRollingHypothesis.ExecutionToken : _config.AiInsights.IncidentBatchRelationshipShadowEnabled ? IncidentBatchExecutionArchitecture.StagedRelationshipAsynchronousConfirmationToken : IncidentBatchExecutionArchitecture.AsynchronousProvisionalToken)};{IncidentBatchAdmissionPolicy.ConfigurationToken};{(_config.AiInsights.IncidentBatchVerificationShadowEnabled ? IncidentBatchStandaloneVerificationContract.ConfigurationToken : "standalone-verification=disabled")};{(_config.AiInsights.IncidentBatchBatchedStandaloneVerificationEnabled ? IncidentBatchStandaloneBatchVerification.ConfigurationToken : "standalone-verification-mode=per-event-v1")};{(UsesTwoPassGraphVerification() ? IncidentBatchGraphVerification.ConfigurationToken : "verification=disabled")};{(_config.AiInsights.IncidentBatchExhaustiveBatchedIntakeEnabled ? $"{IncidentBatchContract.ExhaustiveSourceIntakeConfigurationToken};source-intake=deterministic-v1;source-publication=verified-batch-v1" : "source-intake=model-constructor-v1")};{(_config.AiInsights.IncidentBatchConstructorShadowObservationIsolated ? IncidentBatchContract.ObservationIsolatedOwnershipConfigurationToken : "source-ownership=grouped-v1")};{(_config.AiInsights.IncidentBatchRelationshipShadowEnabled ? IncidentBatchRelationshipContract.ConfigurationToken : "relationship-stage=disabled")};{(_config.AiInsights.IncidentBatchConstructorShadowExclusiveInferenceWindow ? IncidentBatchExperimentWindow.ConfigurationToken : "inference-window=shared-production-v1")};{(_config.AiInsights.IncidentBatchCanaryPersistenceEnabled ? IncidentBatchCanaryGate.ConfigurationToken : "persistence=shadow-only-v1")};{(_config.AiInsights.IncidentBatchProductionOwnershipEnabled ? IncidentBatchProductionGate.ConfigurationToken : "ownership=temporary-shadow-v1")};sourceContext={(_config.AiInsights.IncidentBatchConstructorShadowSourceIsolated ? "isolated-v1" : "candidate-aware-v1")};run={_config.AiInsights.IncidentBatchConstructorShadowRunId.Trim()};interval={_config.AiInsights.IncidentBatchConstructorShadowIntervalSeconds};lookback={_config.AiInsights.IncidentBatchConstructorShadowLookbackMinutes};batch={_config.AiInsights.IncidentBatchConstructorShadowBatchSize};minimumBatch={_config.AiInsights.IncidentBatchConstructorShadowMinimumBatchSize};maximumWait={_config.AiInsights.IncidentBatchConstructorShadowMaximumWaitSeconds};candidates={_config.AiInsights.IncidentBatchConstructorShadowCandidateLimit};continuous={_config.AiInsights.IncidentBatchConstructorShadowContinuous};startAfter={_config.AiInsights.IncidentBatchConstructorShadowStartAfterCallId}";

    private bool UsesTwoPassGraphVerification() =>
        _config.AiInsights.IncidentBatchExhaustiveBatchedIntakeEnabled &&
        _config.AiInsights.IncidentBatchRelationshipShadowEnabled &&
        _config.AiInsights.IncidentBatchVerificationShadowEnabled &&
        _config.AiInsights.IncidentBatchBatchedStandaloneVerificationEnabled;

    private bool RelationshipEnabled() =>
        _config.AiInsights.IncidentBatchRelationshipShadowEnabled;

    private async Task<IReadOnlyDictionary<string, int>> LoadCandidateConsiderationCountsAsync(
        string runId,
        CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var stored in await _database.ListIncidentBatchLedgerEntriesAsync(runId, 500, ct))
        foreach (var candidate in stored.Entry.Candidates)
            counts[candidate.ProjectionEventId] = counts.GetValueOrDefault(candidate.ProjectionEventId) + 1;
        return counts;
    }

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

public static class IncidentBatchProjectionWriteGate
{
    public const string ConfigurationToken = "projection-writer=verification-serialized-optimistic-v1";

    public static bool CanStartIntake(int pendingVerificationCount) =>
        pendingVerificationCount <= 0;
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
    public const string ConfigurationToken = "candidate-context=balanced-state-v4;pending=reconsiderable-evidence-v1;retrieval=cross-system-transcript-evidence-v1";

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
        bool includeRelationshipCandidates = false,
        bool includePendingCandidates = false,
        IReadOnlyDictionary<string, int>? candidateConsiderationCounts = null,
        int maximumEvidencePerCandidate = 3) =>
        Build(
            newCalls,
            recentCalls,
            sourceIsolated && !includeRelationshipCandidates ? [] : matches,
            sourceIsolated && !includeRelationshipCandidates ? null : priorProjection,
            candidateLimit,
            createdAtUtc,
            includePendingCandidates,
            candidateConsiderationCounts,
            maximumEvidencePerCandidate);

    public static IncidentBatchLiveSelection Build(
        IReadOnlyList<EngineCall> newCalls,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyList<VectorSearchMatchDto> matches,
        IncidentBatchProjection? priorProjection,
        int candidateLimit,
        DateTimeOffset createdAtUtc,
        bool includePendingCandidates = false,
        IReadOnlyDictionary<string, int>? candidateConsiderationCounts = null,
        int maximumEvidencePerCandidate = 3)
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
            .Where(item => item.SourceCalls.Count > 0 && (includePendingCandidates || item.Event.OperatorVisible || item.Event.OperatorReview || item.SourceCalls.Any(source => double.IsFinite(source.Score))))
            .ToList();
        candidateConsiderationCounts ??= new Dictionary<string, int>(StringComparer.Ordinal);
        var rankedGroups = eligibleGroups
            .Where(item => !includePendingCandidates || item.Event.OperatorVisible || item.Event.OperatorReview || item.SourceCalls.Any(source => double.IsFinite(source.Score)))
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
        var boundedCandidateLimit = Math.Clamp(candidateLimit, 1, IncidentBatchContract.MaximumCandidateCount);
        var pendingSlots = includePendingCandidates ? Math.Max(1, boundedCandidateLimit / 2) : 0;
        var pendingGroups = eligibleGroups
            .Where(item => !item.Event.OperatorVisible && !item.Event.OperatorReview)
            .OrderBy(item => candidateConsiderationCounts.GetValueOrDefault(item.Event.ProjectionEventId))
            .ThenBy(item => item.SourceCalls.Min(source => source.Call.StartTime))
            .ThenBy(item => item.Event.ProjectionEventId, StringComparer.Ordinal)
            .Take(pendingSlots)
            .ToList();
        var groups = stateAnchors
            .Concat(rankedGroups)
            .DistinctBy(item => item.Event.ProjectionEventId, StringComparer.Ordinal)
            .Where(item => !pendingGroups.Contains(item))
            .Take(Math.Max(0, boundedCandidateLimit - pendingGroups.Count))
            .Concat(pendingGroups)
            .ToList();
        var sourceCalls = newCalls.ToList();
        var reconsideredObservationIds = new List<string>();
        var candidates = new List<IncidentBatchCandidate>();
        for (var index = 0; index < groups.Count; index++)
        {
            var selected = groups[index].SourceCalls
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Call.StartTime)
                .Take(Math.Clamp(maximumEvidencePerCandidate, 1, 3))
                .ToList();
            sourceCalls.AddRange(selected.Select(item => item.Call));
            var selectedObservationIds = selected.Select(item => item.ObservationId).ToList();
            var published = groups[index].Event.OperatorVisible || groups[index].Event.OperatorReview;
            if (!published)
                reconsideredObservationIds.AddRange(selectedObservationIds);
            candidates.Add(new IncidentBatchCandidate(
                $"candidate-{index + 1}",
                groups[index].Event.ProjectionEventId,
                selectedObservationIds,
                groups[index].Event.OperatorVisible));
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
        return new IncidentBatchLiveSelection(
            bundle,
            newCalls.Select(call => ObservationId(call.Id))
                .Concat(reconsideredObservationIds)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            candidates);
    }

    private static string ObservationId(long callId) => $"call:{callId.ToString(CultureInfo.InvariantCulture)}";
}
