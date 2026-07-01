using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class AutomaticInsightsService : BackgroundService
{
    private const int DefaultBatchSize = 50;
    private const int NormalPromptCharLimit = 24_000;
    private const int CompactPromptCharLimit = 12_000;
    private const int NormalTranscriptCharLimit = 500;
    private const int CompactTranscriptCharLimit = 220;
    private const int IncidentPromptCharLimit = 12_000;
    private const int IncidentTranscriptCharLimit = 180;
    private const int IncidentTitleCharLimit = 90;
    private const int IncidentDetailCharLimit = 180;
    private const int IncidentMaxReturnedItems = 3;
    private const int IncidentMaxOutputTokens = 1_800;
    private const int IncidentFallbackChunkTimeoutBaseSeconds = 45;
    private const int IncidentFallbackChunkTimeoutMinSeconds = 90;
    private const int IncidentFallbackChunkTimeoutMaxSeconds = 300;
    private const int IncidentFallbackMaxConsecutiveTerminalTimeoutSkips = 3;
    private const int IncidentV2ShadowMaxOutputTokens = 3_500;
    private const int EvidenceVerifierPromptCharLimit = 9_500;
    private const int EvidenceVerifierTranscriptCharLimit = 160;
    private const int EvidenceVerifierMaxOutputTokens = 1_600;
    private const int NormalMaxOutputTokens = 2_000;
    private const int CompactMaxOutputTokens = 1_200;
    private const int CompactMaxEvents = 4;
    private const string ModelEventLocationAnchorSource = "model_event_location";
    private const double ModelEventLocationAnchorConfidence = 0.86;
    private static readonly TimeSpan MaxIncidentSpan = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan IncidentV2ShadowCandidateMaturity = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan IncidentV2ShadowCandidateRetention = TimeSpan.FromMinutes(30);
    private readonly ConcurrentQueue<EngineCall> _queue = new();
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly EmbeddingService _embeddings;
    private readonly IncidentReconciliationService _reconciliation;
    private readonly TalkgroupCatalogService _catalog;
    private readonly ILogger<AutomaticInsightsService> _logger;
    private readonly IncidentRagMatcher _ragMatcher = new();
    private readonly IncidentFrameBuilderV3 _incidentFrameBuilderV3 = new();
    private readonly IncidentPlanExecutorV3 _incidentPlanExecutorV3 = new();
    private readonly Dictionary<string, ShadowCandidateState> _incidentV2ShadowCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<EngineCall> _pending = new();
    private readonly object _gate = new();
    private readonly object _incidentV2ShadowGate = new();
    private string? _priorSummary;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextIncidentRunAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextQueueGateLogAt = DateTimeOffset.MinValue;
    private int _failureStreak;

    public AutomaticInsightsService(
        EngineConfig config,
        EngineDatabase database,
        EventStream events,
        EmbeddingService embeddings,
        IncidentReconciliationService reconciliation,
        TalkgroupCatalogService catalog,
        ILogger<AutomaticInsightsService> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _embeddings = embeddings;
        _reconciliation = reconciliation;
        _catalog = catalog;
        _logger = logger;
    }

    public void Enqueue(EngineCall call)
    {
        if (!_config.Setup.Completed || !IsEnabled() || !DownstreamProfilePolicy.Allows(_config, call))
            return;
        _queue.Enqueue(call);
    }

    public int ConfiguredBatchSize => BatchSize();

    public bool IsConfiguredAndEnabled => IsEnabled();

    public bool IsSetupComplete => _config.Setup.Completed;

    public async Task<int> GenerateWindowForCallsAsync(List<EngineCall> calls, CancellationToken ct)
    {
        if (!IsEnabled())
            throw new InvalidOperationException("AI insights are disabled or not fully configured.");

        calls = calls
            .Where(c => string.Equals(c.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.QualityReason, "ok", StringComparison.OrdinalIgnoreCase) &&
                        DownstreamProfilePolicy.Allows(_config, c) &&
                        !string.IsNullOrWhiteSpace(c.Transcription))
            .OrderBy(c => c.StartTime)
            .ToList();
        if (calls.Count == 0)
            return 0;

        var start = calls.Min(c => c.StartTime);
        var end = calls.Max(c => Math.Max(c.StartTime, c.StopTime));
        var result = await SummarizeWindowAsync(calls, start, end, InsightPromptMode.CompactManual, ct);
        var windowId = await _database.AddInsightWindowAsync(start, end, result.SummaryText, ct);
        await PersistInsightEventsAsync(windowId, result, calls, ct);
        _priorSummary = result.SummaryText;
        await _events.PublishAsync("summary_updated", new { windowId, start, end, incidents = 0 }, ct);
        _logger.LogInformation("Manual insights generated window {WindowId} with insight events only from {Calls} calls", windowId, calls.Count);
        return 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_config.Setup.Completed && IsEnabled())
                {
                    DrainQueue();
                    await PumpAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic insights loop failed");
            }

            await Task.Delay(1000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private void DrainQueue()
    {
        lock (_gate)
        {
            while (_queue.TryDequeue(out var call))
            {
                if (_pending.Any(c => c.Id == call.Id))
                    continue;
                _pending.Add(call);
            }

            var max = Math.Max(_config.AiInsights.MaxPendingCalls, BatchSize());
            if (_pending.Count > max)
                _pending.RemoveRange(0, _pending.Count - max);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextAttemptAt)
            return;

        var maxQueueDepth = _config.AiInsights.MaxQueueDepthForManualSummary;
        if (maxQueueDepth > 0)
        {
            var pendingTranscriptions = await _database.CountPendingTranscriptionCallsAsync(ct);
            if (pendingTranscriptions > maxQueueDepth)
            {
                if (DateTimeOffset.UtcNow >= _nextQueueGateLogAt)
                {
                    _logger.LogInformation(
                        "Automatic AI insights paused while transcription backlog is high: {Pending:N0} pending call(s), configured limit {Limit:N0}",
                        pendingTranscriptions,
                        maxQueueDepth);
                    _nextQueueGateLogAt = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                return;
            }
        }

        if (DateTimeOffset.UtcNow < _nextIncidentRunAt)
            return;

        List<EngineCall> batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;

            batch = _pending.ToList();
        }

        if (batch.Count == 0)
            return;

        var start = DateTimeOffset.UtcNow.Add(MaxIncidentSpan.Negate()).ToUnixTimeSeconds();
        var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var incidents = 0;
            var truncatedSystems = 0;
            var handledCallIds = batch.Select(c => c.Id).ToHashSet();
            var stale = await _database.ConcludeStaleManagedIncidentsAsync(start, ct);
            foreach (var system in batch.Select(c => c.SystemShortName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var newCallIds = batch
                    .Where(c => string.Equals(c.SystemShortName, system, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Id)
                    .ToHashSet();
                if (newCallIds.Count == 0)
                    continue;

                try
                {
                    incidents += await ExtractIncidentsForSystemAsync(system, start, end, newCallIds, ct);
                }
                catch (InsightResponseTruncatedException ex)
                {
                    truncatedSystems++;
                    _logger.LogWarning(
                        ex,
                        "Automatic incident extraction truncated for {System} after split fallback; preserving {Count} triggering call(s) for a later retry.",
                        system,
                        newCallIds.Count);
                    throw;
                }
            }

            lock (_gate)
            {
                _pending.RemoveAll(c => handledCallIds.Contains(c.Id));
            }
            _failureStreak = 0;
            _nextAttemptAt = DateTimeOffset.UtcNow;
            _nextIncidentRunAt = DateTimeOffset.UtcNow.AddSeconds(IncidentRunIntervalSeconds());
            await _events.PublishAsync("summary_updated", new { windowId = 0, start, end, incidents }, ct);
            _logger.LogInformation(
                "Automatic incident extraction updated {Incidents} incident(s), concluded {StaleIncidents} stale incident(s), from {Calls} new call(s); truncated systems={TruncatedSystems}",
                incidents,
                stale,
                handledCallIds.Count,
                truncatedSystems);
        }
        catch (Exception ex)
        {
            _failureStreak++;
            var cooldownSeconds = Math.Min(300, 5 * (1 << Math.Min(_failureStreak, 5)));
            _nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);
            _nextIncidentRunAt = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);
            RotateFailedBatch(batch);
            _logger.LogWarning(ex, "Automatic incident extraction failed for {Count} calls; backing off {Seconds}s", batch.Count, cooldownSeconds);
        }
    }

    private async Task<int> ExtractIncidentsForSystemAsync(string systemShortName, long start, long end, HashSet<long> newCallIds, CancellationToken ct)
    {
        var recent = (await _database.ListCallsAsync(start, end, null, ct))
            .Where(c => string.Equals(c.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase))
            .Where(c => DownstreamProfilePolicy.Allows(_config, c))
            .Where(IsIncidentEligibleCall)
            .Where(IsCatalogIncidentEligibleOrStrongSignal)
            .OrderBy(c => c.StartTime)
            .ToList();
        if (recent.Count == 0)
            return 0;

        var activeIncidents = (await _database.ListIncidentsAsync(start, end, ct))
            .Where(i => !string.Equals(i.Status, "concluded", StringComparison.OrdinalIgnoreCase))
            .Where(i => i.Calls.Any(c => string.Equals(c.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var locationRows = await _database.ListCallLocationsAsync(start, end, ct);
        var vectorMatches = await SearchVectorCandidatesAsync(systemShortName, activeIncidents, recent, newCallIds, start, end, ct);
        var ragCandidates = _ragMatcher.SelectCandidates(systemShortName, activeIncidents, recent, newCallIds, locationRows, vectorMatches);
        if (ragCandidates.Count == 0 && activeIncidents.Count == 0)
            return 0;

        _logger.LogInformation(
            "Prepared RAG incident candidates for {System}: {NewCandidates} new, {CarryoverCandidates} carryover/RAG, {CandidateCalls} total, {RecentEligibleCalls} eligible calls in rolling state window, {ActiveIncidents} active incident(s)",
            systemShortName,
            ragCandidates.Count(c => newCallIds.Contains(c.Call.Id)),
            ragCandidates.Count(c => !newCallIds.Contains(c.Call.Id)),
            ragCandidates.Count,
            recent.Count,
            activeIncidents.Count);

        try
        {
            return await ExtractAndPersistIncidentStateAsync(systemShortName, activeIncidents, recent, ragCandidates, locationRows, start, end, ct, recordTruncationUsage: false);
        }
        catch (InsightResponseTruncatedException ex)
        {
            var prioritized = PrioritizeIncidentCandidates(ragCandidates);
            if (prioritized.Count <= 3)
                throw;

            var chunkSize = Math.Min(8, Math.Max(3, (int)Math.Ceiling(prioritized.Count / 2d)));
            _logger.LogWarning(
                ex,
                "Incident extraction for {System} truncated with {CandidateCalls} candidate calls; retrying bounded split fallback with chunk size {ChunkSize}.",
                systemShortName,
                prioritized.Count,
                chunkSize);
            return await ExtractAndPersistIncidentStateInChunksAsync(systemShortName, activeIncidents, recent, prioritized, locationRows, start, end, chunkSize, depth: 1, recoveredFallback: true, new IncidentExtractionFallbackState(), ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsIncidentExtractionTimeout(ex))
        {
            var prioritized = PrioritizeIncidentCandidates(ragCandidates);
            if (prioritized.Count <= 3)
                throw;

            var chunkSize = Math.Min(8, Math.Max(3, (int)Math.Ceiling(prioritized.Count / 2d)));
            _logger.LogWarning(
                ex,
                "Incident extraction for {System} timed out with {CandidateCalls} candidate calls; retrying bounded split fallback with chunk size {ChunkSize}.",
                systemShortName,
                prioritized.Count,
                chunkSize);
            return await ExtractAndPersistIncidentStateInChunksAsync(systemShortName, activeIncidents, recent, prioritized, locationRows, start, end, chunkSize, depth: 1, recoveredFallback: true, new IncidentExtractionFallbackState(), ct);
        }
    }

    private async Task<int> ExtractAndPersistIncidentStateAsync(
        string systemShortName,
        List<IncidentDto> activeIncidents,
        List<EngineCall> recent,
        IReadOnlyList<IncidentRagCandidate> ragCandidates,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        long start,
        long end,
        CancellationToken ct,
        bool recordTruncationUsage = true)
    {
        var extraction = await ExtractIncidentStateAsync(systemShortName, activeIncidents, ragCandidates, start, end, ct, recordTruncationUsage: recordTruncationUsage);
        var updated = await PersistIncidentStateAsync(systemShortName, extraction.Result, activeIncidents, recent, ragCandidates, locationRows, ct);
        await ApplyIncidentV3LiveUpdateCurrentAsync(systemShortName, extraction.IncidentV3Execution, activeIncidents, ragCandidates, locationRows, ct);
        return updated;
    }

    private async Task<int> ExtractAndPersistIncidentStateInChunksAsync(
        string systemShortName,
        List<IncidentDto> activeIncidents,
        List<EngineCall> recent,
        IReadOnlyList<IncidentRagCandidate> ragCandidates,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        long start,
        long end,
        int chunkSize,
        int depth,
        bool recoveredFallback,
        IncidentExtractionFallbackState fallbackState,
        CancellationToken ct)
    {
        var incidents = 0;
        var chunks = ragCandidates.Chunk(chunkSize).Select(c => c.ToList()).ToList();
        for (var index = 0; index < chunks.Count; index++)
        {
            if (fallbackState.TimeoutCircuitOpen)
                break;

            var chunk = chunks[index];
            using var chunkTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var chunkTimeoutBudget = IncidentExtractionFallbackChunkTimeout(chunk.Count, _config.AiInsights.TimeoutMs);
            chunkTimeout.CancelAfter(chunkTimeoutBudget);
            try
            {
                incidents += recoveredFallback
                    ? await ExtractAndPersistIncidentStateRecoveredAsync(systemShortName, activeIncidents, recent, chunk, locationRows, start, end, chunkTimeout.Token)
                    : await ExtractAndPersistIncidentStateAsync(systemShortName, activeIncidents, recent, chunk, locationRows, start, end, chunkTimeout.Token, recordTruncationUsage: false);
                fallbackState.ConsecutiveTerminalTimeoutSkips = 0;
            }
            catch (InsightResponseTruncatedException ex) when (chunk.Count > 1)
            {
                var smallerChunkSize = Math.Max(1, (int)Math.Ceiling(chunk.Count / 2d));
                _logger.LogWarning(
                    ex,
                    "Incident extraction split fallback for {System} still truncated with {CandidateCalls} candidate calls; retrying chunk size {ChunkSize}.",
                    systemShortName,
                    chunk.Count,
                    smallerChunkSize);
                incidents += await ExtractAndPersistIncidentStateInChunksAsync(systemShortName, activeIncidents, recent, chunk, locationRows, start, end, smallerChunkSize, depth + 1, recoveredFallback, fallbackState, ct);
            }
            catch (InsightResponseTruncatedException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Incident extraction split fallback for {System} still truncated with a terminal single-call chunk; skipping call {CallId} for this run.",
                    systemShortName,
                    chunk.Select(candidate => candidate.Call.Id).FirstOrDefault());
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsIncidentExtractionTimeout(ex) && chunk.Count > 1)
            {
                var smallerChunkSize = Math.Max(1, (int)Math.Ceiling(chunk.Count / 2d));
                _logger.LogWarning(
                    ex,
                    "Incident extraction split fallback for {System} still timed out after {TimeoutSeconds}s with {CandidateCalls} candidate calls; retrying chunk size {ChunkSize}.",
                    systemShortName,
                    (int)Math.Ceiling(chunkTimeoutBudget.TotalSeconds),
                    chunk.Count,
                    smallerChunkSize);
                incidents += await ExtractAndPersistIncidentStateInChunksAsync(systemShortName, activeIncidents, recent, chunk, locationRows, start, end, smallerChunkSize, depth + 1, recoveredFallback, fallbackState, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsIncidentExtractionTimeout(ex))
            {
                fallbackState.ConsecutiveTerminalTimeoutSkips++;
                _logger.LogWarning(
                    ex,
                    "Incident extraction split fallback for {System} still timed out after {TimeoutSeconds}s with a terminal single-call chunk; skipping call {CallId} for this run.",
                    systemShortName,
                    (int)Math.Ceiling(chunkTimeoutBudget.TotalSeconds),
                    chunk.Select(candidate => candidate.Call.Id).FirstOrDefault());
                if (ShouldStopIncidentExtractionFallbackAfterTerminalTimeouts(fallbackState.ConsecutiveTerminalTimeoutSkips))
                {
                    fallbackState.TimeoutCircuitOpen = true;
                    var remainingCandidateCalls = chunks.Skip(index + 1).Sum(remaining => remaining.Count);
                    _logger.LogWarning(
                        "Incident extraction split fallback for {System} reached {TerminalTimeoutSkips} consecutive terminal timeout skips; abandoning {RemainingCandidateCalls} remaining candidate call(s) for this run.",
                        systemShortName,
                        fallbackState.ConsecutiveTerminalTimeoutSkips,
                        remainingCandidateCalls);
                }
            }
        }

        return incidents;
    }

    private async Task<int> ExtractAndPersistIncidentStateRecoveredAsync(
        string systemShortName,
        List<IncidentDto> activeIncidents,
        List<EngineCall> recent,
        IReadOnlyList<IncidentRagCandidate> ragCandidates,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        long start,
        long end,
        CancellationToken ct)
    {
        var extraction = await ExtractIncidentStateAsync(systemShortName, activeIncidents, ragCandidates, start, end, ct, recoveredFallback: true, recordTruncationUsage: false);
        var updated = await PersistIncidentStateAsync(systemShortName, extraction.Result, activeIncidents, recent, ragCandidates, locationRows, ct);
        await ApplyIncidentV3LiveUpdateCurrentAsync(systemShortName, extraction.IncidentV3Execution, activeIncidents, ragCandidates, locationRows, ct);
        return updated;
    }

    private async Task<IncidentExtractionRunResult> ExtractIncidentStateAsync(string systemShortName, List<IncidentDto> activeIncidents, IReadOnlyList<IncidentRagCandidate> candidateCalls, long start, long end, CancellationToken ct, bool recoveredFallback = false, bool recordTruncationUsage = true)
    {
        var baseUrl = InsightBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var prompt = BuildIncidentExtractionPrompt(systemShortName, activeIncidents, candidateCalls, start, end);
        var body = new
        {
            model = InsightModel(),
            temperature = 0.1,
            max_tokens = IncidentMaxOutputTokens,
            response_format = IncidentExtractionResponseFormat(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You maintain an incremental incident state from public safety radio transcripts. Output JSON only. Incidents are site-local real-world events supported by concrete transcript evidence. Do not group by category; a single incident may span police, fire, EMS, traffic, utilities, or other talkgroups. Do not create incidents for routine acknowledgements, generic status/admin traffic, or weak topical buckets."
                },
                new { role = "user", content = prompt }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling incident extraction endpoint {Endpoint} with model {Model} for {System} ({ActiveIncidents} active, {CandidateCalls} candidate calls, {PayloadChars} chars)", endpoint, InsightModel(), systemShortName, activeIncidents.Count, candidateCalls.Count, payload.Length);
        Exception? last = null;
        for (var attempt = 0; attempt <= 0; attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(endpoint, content, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Incident extraction request failed with HTTP {(int)response.StatusCode}: {Trim(text, 1000)}");
                var usage = ExtractUsage(text);
                if (string.Equals(usage.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    var message = $"LM incident extraction response was truncated at max_tokens={IncidentMaxOutputTokens}.";
                    if (recordTruncationUsage)
                        await RecordUsageAsync(text, endpoint, payload.Length, candidateCalls.Sum(c => c.Call.Transcription?.Length ?? 0), attempt + 1, false, message, ct);
                    throw new InsightResponseTruncatedException(message);
                }

                var parsed = ParseIncidentExtractionResponse(text);
                await RecordUsageAsync(text, endpoint, payload.Length, candidateCalls.Sum(c => c.Call.Transcription?.Length ?? 0), attempt + 1, true, recoveredFallback ? "recovered_after_truncation_split" : string.Empty, ct);
                var incidentV3Execution = await RunIncidentV3FrameShadowAsync(systemShortName, activeIncidents, candidateCalls, parsed, ct);
                await RunIncidentV2ShadowAsync(systemShortName, activeIncidents, candidateCalls, parsed, endpoint, ct);
                return new IncidentExtractionRunResult(parsed, incidentV3Execution);
            }
            catch (InsightResponseTruncatedException)
            {
                throw;
            }
            catch (Exception ex) when (IsCallerRequestedCompletionCancellation(ex, ct))
            {
                _logger.LogInformation("Incident extraction completion was canceled by the caller for {System}; skipping AI completion failure health record.", systemShortName);
                throw;
            }
            catch (Exception ex) when (attempt < Math.Max(0, _config.AiInsights.MaxRetries))
            {
                last = ex;
                await RecordUsageAsync(string.Empty, endpoint, payload.Length, candidateCalls.Sum(c => c.Call.Transcription?.Length ?? 0), attempt + 1, false, ex.Message, CancellationToken.None);
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                last = ex;
                await RecordUsageAsync(string.Empty, endpoint, payload.Length, candidateCalls.Sum(c => c.Call.Transcription?.Length ?? 0), attempt + 1, false, ex.Message, CancellationToken.None);
                break;
            }
        }

        throw new InvalidOperationException(last?.Message ?? "Incident extraction request failed.", last);
    }

    private async Task<IncidentPlanExecutionResultV3?> RunIncidentV3FrameShadowAsync(
        string systemShortName,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentRagCandidate> candidateCalls,
        IncidentExtractionResult currentResult,
        CancellationToken ct)
    {
        if (!_config.AiInsights.IncidentV3FrameShadowEnabled || candidateCalls.Count == 0)
            return null;

        try
        {
            var validCandidateCallTokens = candidateCalls
                .SelectMany(row => IncidentV3CurrentCallTokens(row.Call.Id))
                .ToHashSet();
            var currentIncidents = currentResult.Incidents
                .Take(IncidentMaxReturnedItems)
                .Select(incident => BuildIncidentV3CurrentIncident(incident, validCandidateCallTokens))
                .ToList();
            var frames = _incidentFrameBuilderV3.Build(
                systemShortName,
                activeIncidents,
                candidateCalls,
                currentIncidents,
                IncidentV3FrameCandidateLimit());
            if (frames.Count == 0)
            {
                _logger.LogInformation(
                    "Incident v3 frame shadow for {System}: frameCount=0; currentIncidentCount={CurrentIncidentCount}; candidateCallIds={CandidateCallIds}",
                    systemShortName,
                    currentIncidents.Count,
                    string.Join(",", candidateCalls.Select(row => row.Call.Id)));
                _logger.LogInformation(
                    "Incident v3 promotion shadow for {System}: decisionCount=0; currentIncidentCount={CurrentIncidentCount}; createCount=0; matchCurrentCount=0; matchCurrentWithAmbiguousMembersCount=0; matchCurrentLocationDisagreementCount=0; holdPendingCount=0; holdStaleCount=0; ambiguousCount=0; conflictCount=0; decisions=[]",
                    systemShortName,
                    currentIncidents.Count);
                _logger.LogInformation(
                    "Incident v3 resolver shadow for {System}: resolverDecisionCount=0; attachCurrentCount=0; attachNewCount=0; detachCreateCount=0; holdPendingCount=0; ambiguousHoldCount=0; ambiguousDropCount=0; suppressStaleCount=0; duplicateWinnerCount=0; decisions=[]",
                    systemShortName);
                _logger.LogInformation(
                    "Incident v3 incident plan shadow for {System}: planCount=0; updateCurrentCount=0; createNewCount=0; detachCreateCount=0; holdPendingCount=0; holdAmbiguousCount=0; dropAmbiguousCount=0; suppressStaleCount=0; duplicateFinalCallCount=0; plans=[]",
                    systemShortName);
                return BuildAndLogIncidentV3ExecutionShadowAsync(systemShortName, []);
            }

            var promotionDecisions = _incidentFrameBuilderV3.BuildPromotionDecisions(frames);
            var resolverDecisions = _incidentFrameBuilderV3.BuildResolverDecisions(frames, candidateCalls);
            var incidentPlans = _incidentFrameBuilderV3.BuildIncidentPlanDecisions(frames, resolverDecisions);
            incidentPlans = await ApplyIncidentV3AcceptedAuditPlanGuardsAsync(incidentPlans, activeIncidents, ct);
            _logger.LogInformation(
                "Incident v3 frame shadow for {System}: frameCount={FrameCount}; currentIncidentCount={CurrentIncidentCount}; candidateCallIds={CandidateCallIds}; frames={Frames}",
                systemShortName,
                frames.Count,
                currentIncidents.Count,
                string.Join(",", candidateCalls.Select(row => row.Call.Id)),
                JsonSerializer.Serialize(frames, EngineConfig.JsonOptions()));
            _logger.LogInformation(
                "Incident v3 promotion shadow for {System}: decisionCount={DecisionCount}; currentIncidentCount={CurrentIncidentCount}; createCount={CreateCount}; matchCurrentCount={MatchCurrentCount}; matchCurrentWithAmbiguousMembersCount={MatchCurrentWithAmbiguousMembersCount}; matchCurrentLocationDisagreementCount={MatchCurrentLocationDisagreementCount}; holdPendingCount={HoldPendingCount}; holdStaleCount={HoldStaleCount}; ambiguousCount={AmbiguousCount}; conflictCount={ConflictCount}; decisions={Decisions}",
                systemShortName,
                promotionDecisions.Count,
                currentIncidents.Count,
                promotionDecisions.Count(decision => string.Equals(decision.Action, "create", StringComparison.OrdinalIgnoreCase)),
                promotionDecisions.Count(decision => string.Equals(decision.Action, "match_current", StringComparison.OrdinalIgnoreCase)),
                promotionDecisions.Count(decision => string.Equals(decision.Action, "match_current_with_ambiguous_members", StringComparison.OrdinalIgnoreCase)),
                promotionDecisions.Count(decision => string.Equals(decision.Action, "match_current_location_disagreement", StringComparison.OrdinalIgnoreCase) ||
                                                     string.Equals(decision.Action, "match_current_location_disagreement_with_ambiguous_members", StringComparison.OrdinalIgnoreCase)),
                promotionDecisions.Count(decision => string.Equals(decision.Action, "hold_pending", StringComparison.OrdinalIgnoreCase)),
                promotionDecisions.Count(decision => string.Equals(decision.Action, "hold_stale", StringComparison.OrdinalIgnoreCase)),
                promotionDecisions.Count(decision => string.Equals(decision.Action, "mark_ambiguous", StringComparison.OrdinalIgnoreCase)),
                promotionDecisions.Count(decision => string.Equals(decision.Action, "flag_conflict", StringComparison.OrdinalIgnoreCase)),
                JsonSerializer.Serialize(promotionDecisions, EngineConfig.JsonOptions()));
            _logger.LogInformation(
                "Incident v3 resolver shadow for {System}: resolverDecisionCount={ResolverDecisionCount}; attachCurrentCount={AttachCurrentCount}; attachNewCount={AttachNewCount}; detachCreateCount={DetachCreateCount}; holdPendingCount={HoldPendingCount}; ambiguousHoldCount={AmbiguousHoldCount}; ambiguousDropCount={AmbiguousDropCount}; suppressStaleCount={SuppressStaleCount}; duplicateWinnerCount={DuplicateWinnerCount}; decisions={Decisions}",
                systemShortName,
                resolverDecisions.Count,
                resolverDecisions.Count(decision => string.Equals(decision.Decision, "attach_current", StringComparison.OrdinalIgnoreCase)),
                resolverDecisions.Count(decision => string.Equals(decision.Decision, "attach_new", StringComparison.OrdinalIgnoreCase)),
                resolverDecisions.Count(decision => string.Equals(decision.Decision, "detach_create", StringComparison.OrdinalIgnoreCase)),
                resolverDecisions.Count(decision => string.Equals(decision.Decision, "hold_pending", StringComparison.OrdinalIgnoreCase)),
                resolverDecisions.Count(decision => string.Equals(decision.Decision, "ambiguous_hold", StringComparison.OrdinalIgnoreCase)),
                resolverDecisions.Count(decision => string.Equals(decision.Decision, "ambiguous_drop", StringComparison.OrdinalIgnoreCase)),
                resolverDecisions.Count(decision => string.Equals(decision.Decision, "suppress_stale", StringComparison.OrdinalIgnoreCase)),
                resolverDecisions.GroupBy(decision => decision.CallId).Count(group => group.Count() > 1),
                JsonSerializer.Serialize(resolverDecisions, EngineConfig.JsonOptions()));
            _logger.LogInformation(
                "Incident v3 incident plan shadow for {System}: planCount={PlanCount}; updateCurrentCount={UpdateCurrentCount}; createNewCount={CreateNewCount}; detachCreateCount={DetachCreateCount}; holdPendingCount={HoldPendingCount}; holdAmbiguousCount={HoldAmbiguousCount}; dropAmbiguousCount={DropAmbiguousCount}; suppressStaleCount={SuppressStaleCount}; duplicateFinalCallCount={DuplicateFinalCallCount}; plans={Plans}",
                systemShortName,
                incidentPlans.Count,
                incidentPlans.Count(plan => string.Equals(plan.Action, "update_current", StringComparison.OrdinalIgnoreCase)),
                incidentPlans.Count(plan => string.Equals(plan.Action, "create_new", StringComparison.OrdinalIgnoreCase)),
                incidentPlans.Count(plan => string.Equals(plan.Action, "detach_create", StringComparison.OrdinalIgnoreCase)),
                incidentPlans.Count(plan => string.Equals(plan.Action, "hold_pending", StringComparison.OrdinalIgnoreCase)),
                incidentPlans.Count(plan => string.Equals(plan.Action, "hold_ambiguous", StringComparison.OrdinalIgnoreCase)),
                incidentPlans.Count(plan => string.Equals(plan.Action, "drop_ambiguous", StringComparison.OrdinalIgnoreCase)),
                incidentPlans.Count(plan => string.Equals(plan.Action, "suppress_stale", StringComparison.OrdinalIgnoreCase)),
                incidentPlans
                    .Where(plan => string.Equals(plan.Action, "update_current", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(plan.Action, "create_new", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(plan.Action, "detach_create", StringComparison.OrdinalIgnoreCase))
                    .SelectMany(plan => plan.CallIds)
                    .GroupBy(callId => callId)
                    .Count(group => group.Count() > 1),
                JsonSerializer.Serialize(incidentPlans, EngineConfig.JsonOptions()));
            return BuildAndLogIncidentV3ExecutionShadowAsync(systemShortName, incidentPlans);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incident v3 frame shadow failed for {System}", systemShortName);
            return null;
        }
    }

    private IncidentPlanExecutionResultV3 BuildAndLogIncidentV3ExecutionShadowAsync(
        string systemShortName,
        IReadOnlyList<IncidentPlanDecisionV3> incidentPlans)
    {
        var execution = _incidentPlanExecutorV3.BuildExecutionPlan(
            incidentPlans,
            new IncidentPlanExecutionOptionsV3(
                _config.AiInsights.IncidentV3PlanExecutorEnabled,
                _config.AiInsights.IncidentV3PlanExecutorDryRun,
                _config.AiInsights.IncidentV3PlanExecutorAllowLiveUpdateCurrent));
        _logger.LogInformation(
            "Incident v3 plan executor shadow for {System}: mode={Mode}; enabled={Enabled}; dryRun={DryRun}; canMutate={CanMutate}; operationCount={OperationCount}; mutatingOperationCount={MutatingOperationCount}; blockedOperationCount={BlockedOperationCount}; blockReasons={BlockReasons}; operations={Operations}",
            systemShortName,
            execution.Mode,
            execution.Enabled,
            execution.DryRun,
            execution.CanMutate,
            execution.OperationCount,
            execution.MutatingOperationCount,
            execution.BlockedOperationCount,
            string.Join(",", execution.BlockReasons),
            JsonSerializer.Serialize(execution.Operations, EngineConfig.JsonOptions()));
        return execution;
    }

    private async Task<IReadOnlyList<IncidentPlanDecisionV3>> ApplyIncidentV3AcceptedAuditPlanGuardsAsync(
        IReadOnlyList<IncidentPlanDecisionV3> incidentPlans,
        IReadOnlyList<IncidentDto> activeIncidents,
        CancellationToken ct)
    {
        var activeIncidentsById = activeIncidents
            .Where(incident => incident.Id > 0)
            .GroupBy(incident => incident.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var targetIncidents = incidentPlans
            .Where(plan => string.Equals(plan.Action, "update_current", StringComparison.OrdinalIgnoreCase))
            .Select(plan => TryParseActiveIncidentTarget(plan.TargetIncidentId, out var incidentId) &&
                            activeIncidentsById.TryGetValue(incidentId, out var incident)
                ? incident
                : null)
            .Where(incident => incident is not null)
            .DistinctBy(incident => incident!.IncidentKey)
            .Cast<IncidentDto>()
            .ToList();
        if (targetIncidents.Count == 0)
            return incidentPlans;

        var acceptedAuditsByIncidentKey = new Dictionary<string, IReadOnlyList<IncidentOperationAuditRowDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var incident in targetIncidents)
        {
            if (string.IsNullOrWhiteSpace(incident.IncidentKey))
                continue;
            try
            {
                acceptedAuditsByIncidentKey[incident.IncidentKey] =
                    await _database.ListAcceptedIncidentOperationAuditForKeyAsync(incident.IncidentKey, 40, ct);
            }
            catch (Exception ex) when (!IsCallerRequestedCompletionCancellation(ex, ct))
            {
                _logger.LogWarning(
                    ex,
                    "Incident v3 accepted-audit plan guard unavailable for active incident {IncidentId} ({IncidentKey}); leaving shadow plans unmodified.",
                    incident.Id,
                    incident.IncidentKey);
            }
        }

        return BlockLegacyRejectedUpdateCurrentPlans(incidentPlans, activeIncidentsById, acceptedAuditsByIncidentKey);
    }

    private async Task ApplyIncidentV3LiveUpdateCurrentAsync(
        string systemShortName,
        IncidentPlanExecutionResultV3? execution,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentRagCandidate> candidateCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        CancellationToken ct)
    {
        if (execution is null || !execution.CanMutate)
            return;

        var applied = await ExecuteIncidentV3LiveUpdateCurrentAsync(systemShortName, execution, activeIncidents, candidateCalls, locationRows, ct);
        _logger.LogInformation(
            "Incident v3 plan executor live update_current for {System}: appliedOperationCount={AppliedOperationCount}; requestedOperationCount={RequestedOperationCount}",
            systemShortName,
            applied,
            execution.Operations.Count(operation => string.Equals(operation.PlanAction, "update_current", StringComparison.OrdinalIgnoreCase) && operation.WouldMutate && !operation.Blocked));
    }

    private async Task<int> ExecuteIncidentV3LiveUpdateCurrentAsync(
        string systemShortName,
        IncidentPlanExecutionResultV3 execution,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentRagCandidate> candidateCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        CancellationToken ct)
    {
        var operations = execution.Operations
            .Where(operation => string.Equals(operation.PlanAction, "update_current", StringComparison.OrdinalIgnoreCase))
            .Where(operation => operation.WouldMutate && !operation.Blocked)
            .ToList();
        if (operations.Count == 0)
            return 0;

        var candidateCallsById = candidateCalls
            .Select(candidate => candidate.Call)
            .DistinctBy(call => call.Id)
            .ToDictionary(call => call.Id);
        var applied = 0;
        foreach (var operation in operations)
        {
            if (!TryParseActiveIncidentTarget(operation.TargetIncidentId, out var activeIncidentId))
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    null,
                    operation.CallIds,
                    [],
                    false,
                    "rejected:target incident did not resolve to an active managed incident",
                    candidateCallsById,
                    ct);
                _logger.LogWarning(
                    "Incident v3 live update_current skipped for {System}: target incident '{TargetIncidentId}' did not resolve to an active managed incident.",
                    systemShortName,
                    operation.TargetIncidentId);
                continue;
            }

            var activeIncident = await _database.GetIncidentByIdAsync(activeIncidentId, ct);
            if (activeIncident is null ||
                string.Equals(activeIncident.Status, "concluded", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(activeIncident.IncidentKey))
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    null,
                    operation.CallIds,
                    [],
                    false,
                    "rejected:target incident did not resolve to an active managed incident",
                    candidateCallsById,
                    ct);
                _logger.LogWarning(
                    "Incident v3 live update_current skipped for {System}: target incident '{TargetIncidentId}' did not resolve to an active managed incident.",
                    systemShortName,
                    operation.TargetIncidentId);
                continue;
            }

            var existingCallsById = activeIncident.Calls
                .Select(IncidentCallToEngineCall)
                .DistinctBy(call => call.Id)
                .ToDictionary(call => call.Id);
            var proposedCalls = new List<EngineCall>();
            foreach (var callId in operation.CallIds.Distinct().Order())
            {
                if (candidateCallsById.TryGetValue(callId, out var candidateCall))
                    proposedCalls.Add(candidateCall);
                else if (existingCallsById.TryGetValue(callId, out var existingCall))
                    proposedCalls.Add(existingCall);
            }

            if (proposedCalls.Count != operation.CallIds.Distinct().Count())
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    activeIncident,
                    operation.CallIds,
                    proposedCalls.Select(call => call.Id).ToList(),
                    false,
                    "rejected:plan call ids were not fully resolvable",
                    candidateCallsById,
                    ct);
                _logger.LogWarning(
                    "Incident v3 live update_current skipped for {System}: plan call ids were not fully resolvable. target={TargetIncidentId}; planned={PlannedCallIds}; resolved={ResolvedCallIds}",
                    systemShortName,
                    operation.TargetIncidentId,
                    string.Join(",", operation.CallIds.Distinct().Order()),
                    string.Join(",", proposedCalls.Select(call => call.Id).Order()));
                continue;
            }

            var existingCallIds = existingCallsById.Keys.ToHashSet();
            var missingExistingCallIds = MissingCurrentCallIdsForAddOnlyUpdate(activeIncident, operation.CallIds);
            if (missingExistingCallIds.Count > 0)
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    activeIncident,
                    operation.CallIds,
                    proposedCalls.Select(call => call.Id).ToList(),
                    false,
                    $"rejected:v3 live update_current plan would replace current membership; missingExistingCallIds={string.Join(",", missingExistingCallIds)}",
                    candidateCallsById,
                    ct);
                _logger.LogWarning(
                    "Incident v3 live update_current skipped for {System}: plan would replace current membership. target={TargetIncidentId}; missingExistingCallIds={MissingExistingCallIds}; planned={PlannedCallIds}",
                    systemShortName,
                    operation.TargetIncidentId,
                    string.Join(",", missingExistingCallIds),
                    string.Join(",", operation.CallIds.Distinct().Order()));
                continue;
            }

            var recentAcceptedAudits = await _database.ListAcceptedIncidentOperationAuditForKeyAsync(activeIncident.IncidentKey, 40, ct);
            var legacyRejectedExtraCallIds = LegacyRejectedExtraCallIdsForAddOnlyUpdate(activeIncident, operation.CallIds, recentAcceptedAudits);
            if (legacyRejectedExtraCallIds.Count > 0)
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    activeIncident,
                    operation.CallIds,
                    proposedCalls.Select(call => call.Id).ToList(),
                    false,
                    $"rejected:v3 live update_current would add calls previously excluded by accepted legacy audit; rejectedCallIds={string.Join(",", legacyRejectedExtraCallIds)}",
                    candidateCallsById,
                    ct);
                _logger.LogWarning(
                    "Incident v3 live update_current skipped for {System}: plan would add calls previously excluded by accepted legacy audit. target={TargetIncidentId}; rejectedCallIds={RejectedCallIds}; planned={PlannedCallIds}",
                    systemShortName,
                    operation.TargetIncidentId,
                    string.Join(",", legacyRejectedExtraCallIds),
                    string.Join(",", operation.CallIds.Distinct().Order()));
                continue;
            }

            var proposedUnion = proposedCalls
                .Concat(existingCallsById.Values)
                .DistinctBy(call => call.Id)
                .OrderBy(call => call.StartTime)
                .ToList();
            var validationCallIds = proposedUnion.Select(call => call.Id).Distinct().ToList();
            var anchorsByCall = await _database.GetCallAnchorsAsync(validationCallIds, ct);
            var operationItem = BuildIncidentV3LiveUpdateItem(operation, activeIncident, validationCallIds);
            var ragById = candidateCalls
                .Where(candidate => validationCallIds.Contains(candidate.Call.Id))
                .DistinctBy(candidate => candidate.Call.Id)
                .ToDictionary(candidate => candidate.Call.Id);
            var finalMembership = FinalizeServerOwnedMembership(operationItem, activeIncident, proposedUnion, locationRows, anchorsByCall, ragById);
            if (!finalMembership.Accepted)
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    activeIncident,
                    operation.CallIds,
                    proposedCalls.Select(call => call.Id).ToList(),
                    false,
                    $"rejected:v3 live update_current failed final membership validation: {finalMembership.Reason}",
                    candidateCallsById,
                    ct);
                continue;
            }

            var finalCallIds = finalMembership.Calls.Select(call => call.Id).ToHashSet();
            if (!existingCallIds.IsSubsetOf(finalCallIds))
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    activeIncident,
                    operation.CallIds,
                    finalMembership.Calls.Select(call => call.Id).ToList(),
                    false,
                    "rejected:v3 live update_current validation would drop existing active incident calls",
                    candidateCallsById,
                    ct);
                continue;
            }

            var addedCallIds = finalCallIds.Except(existingCallIds).Order().ToList();
            if (addedCallIds.Count == 0)
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    activeIncident,
                    operation.CallIds,
                    finalMembership.Calls.Select(call => call.Id).ToList(),
                    true,
                    $"accepted:v3 live update_current validation produced no membership delta: {finalMembership.Reason}",
                    candidateCallsById,
                    ct);
                continue;
            }

            var calls = finalMembership.Calls;
            var incident = activeIncident with
            {
                Title = string.IsNullOrWhiteSpace(operation.Title) ? activeIncident.Title : operation.Title,
                Category = string.IsNullOrWhiteSpace(operation.Category) ? activeIncident.Category : operation.Category,
                FirstSeen = calls.Min(call => call.StartTime),
                LastSeen = calls.Max(call => call.StartTime),
                Calls = calls
                    .OrderBy(call => call.StartTime)
                    .Select(ToIncidentCallDto)
                    .ToList()
            };
            var id = await _database.UpsertManagedIncidentAsync(incident, ct);
            if (id <= 0)
            {
                await AuditIncidentV3LiveUpdateCurrentAsync(
                    systemShortName,
                    operation,
                    activeIncident,
                    operation.CallIds,
                    calls.Select(call => call.Id).ToList(),
                    false,
                    "rejected:database upsert returned no row",
                    candidateCallsById,
                    ct);
                _logger.LogWarning(
                    "Incident v3 live update_current skipped for {System}: database upsert returned no row. target={TargetIncidentId}; calls={CallIds}",
                    systemShortName,
                    operation.TargetIncidentId,
                    string.Join(",", operation.CallIds));
                continue;
            }

            await AuditIncidentV3LiveUpdateCurrentAsync(
                systemShortName,
                operation,
                activeIncident,
                operation.CallIds,
                calls.Select(call => call.Id).ToList(),
                true,
                $"accepted:v3 live update_current applied after legacy persistence; addedCallIds={string.Join(",", addedCallIds)}; validation={finalMembership.Reason}",
                candidateCallsById,
                ct);
            applied++;
        }

        return applied;
    }

    private static IReadOnlyList<long> MissingCurrentCallIdsForAddOnlyUpdate(
        IncidentDto activeIncident,
        IReadOnlyList<long> plannedCallIds)
    {
        var planned = plannedCallIds.ToHashSet();
        return activeIncident.Calls
            .Select(call => call.CallId)
            .Distinct()
            .Except(planned)
            .Order()
            .ToList();
    }

    private static IReadOnlyList<long> LegacyRejectedExtraCallIdsForAddOnlyUpdate(
        IncidentDto activeIncident,
        IReadOnlyList<long> plannedCallIds,
        IReadOnlyList<IncidentOperationAuditRowDto> acceptedAudits)
    {
        var currentCallIds = activeIncident.Calls.Select(call => call.CallId).ToHashSet();
        var extraCallIds = plannedCallIds
            .Distinct()
            .Except(currentCallIds)
            .ToHashSet();
        if (extraCallIds.Count == 0)
            return [];

        var rejectedCallIds = new HashSet<long>();
        foreach (var audit in acceptedAudits)
        {
            if (!audit.Accepted)
                continue;
            foreach (var callId in ExtractLegacyExcludedCallIds(audit.Reason))
                rejectedCallIds.Add(callId);
        }

        return rejectedCallIds
            .Intersect(extraCallIds)
            .Order()
            .ToList();
    }

    private static IReadOnlyList<IncidentPlanDecisionV3> BlockLegacyRejectedUpdateCurrentPlans(
        IReadOnlyList<IncidentPlanDecisionV3> incidentPlans,
        IReadOnlyDictionary<long, IncidentDto> activeIncidentsById,
        IReadOnlyDictionary<string, IReadOnlyList<IncidentOperationAuditRowDto>> acceptedAuditsByIncidentKey)
    {
        return incidentPlans
            .Select(plan =>
            {
                if (!string.Equals(plan.Action, "update_current", StringComparison.OrdinalIgnoreCase) ||
                    !TryParseActiveIncidentTarget(plan.TargetIncidentId, out var activeIncidentId) ||
                    !activeIncidentsById.TryGetValue(activeIncidentId, out var activeIncident) ||
                    string.IsNullOrWhiteSpace(activeIncident.IncidentKey) ||
                    !acceptedAuditsByIncidentKey.TryGetValue(activeIncident.IncidentKey, out var acceptedAudits))
                {
                    return plan;
                }

                var legacyRejectedExtraCallIds = LegacyRejectedExtraCallIdsForAddOnlyUpdate(activeIncident, plan.CallIds, acceptedAudits);
                if (legacyRejectedExtraCallIds.Count == 0)
                    return plan;

                return plan with
                {
                    Action = "hold_pending",
                    TargetIncidentId = string.Empty,
                    TargetIncidentTitle = string.Empty,
                    Reason = AppendPlanReason(
                        plan.Reason,
                        $"planDroppedBecause=legacy_rejected_extra_call_ids:{string.Join(",", legacyRejectedExtraCallIds)}")
                };
            })
            .ToList();
    }

    private static string AppendPlanReason(string reason, string addition) =>
        string.IsNullOrWhiteSpace(reason)
            ? addition
            : $"{reason}; {addition}";

    private static IReadOnlyList<long> ExtractLegacyExcludedCallIds(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return [];

        var callIds = new HashSet<long>();
        foreach (Match match in Regex.Matches(
                     reason,
                     @"excluded\s+(?:weak/unrelated|verifier-rejected)\s+calls\s+([0-9,\s]+)",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            foreach (var part in match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var callId))
                    callIds.Add(callId);
            }
        }

        return callIds.Order().ToList();
    }

    private async Task AuditIncidentV3LiveUpdateCurrentAsync(
        string systemShortName,
        IncidentPlanExecutionOperationV3 operation,
        IncidentDto? activeIncident,
        IReadOnlyList<long> plannedCallIds,
        IReadOnlyList<long> resolvedCallIds,
        bool accepted,
        string reason,
        IReadOnlyDictionary<long, EngineCall> candidateCallsById,
        CancellationToken ct)
    {
        var callIds = plannedCallIds.Distinct().Order().ToList();
        var metadata = new
        {
            source = "incident_v3_plan_executor",
            targetIncidentId = operation.TargetIncidentId,
            targetIncidentKey = activeIncident?.IncidentKey ?? string.Empty,
            targetIncidentTitle = activeIncident?.Title ?? string.Empty,
            title = operation.Title,
            frameTitle = operation.FrameTitle,
            category = operation.Category,
            locationLabel = operation.LocationLabel,
            planReason = operation.Reason,
            plannedCallIds = callIds,
            resolvedCallIds = resolvedCallIds.Distinct().Order().ToList(),
            calls = callIds.Select(callId =>
            {
                var hasCandidate = candidateCallsById.TryGetValue(callId, out var call);
                return new
                {
                    callId,
                    source = hasCandidate ? "v3_candidate" : "active_incident_existing",
                    systemShortName = hasCandidate ? call!.SystemShortName : string.Empty,
                    category = hasCandidate ? call!.Category : string.Empty,
                    talkgroupName = hasCandidate ? call!.TalkgroupName : string.Empty
                };
            }).ToList()
        };
        await _database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(
            0,
            DateTime.UtcNow,
            systemShortName,
            activeIncident?.IncidentKey ?? operation.TargetIncidentId,
            "v3_update_current",
            accepted,
            reason,
            0,
            JsonSerializer.Serialize(callIds),
            JsonSerializer.Serialize(metadata, EngineConfig.JsonOptions())), ct);
    }

    private static IncidentStateItem BuildIncidentV3LiveUpdateItem(
        IncidentPlanExecutionOperationV3 operation,
        IncidentDto activeIncident,
        IReadOnlyList<long> callIds)
    {
        var title = string.IsNullOrWhiteSpace(operation.Title)
            ? activeIncident.Title
            : operation.Title;
        var category = string.IsNullOrWhiteSpace(operation.Category)
            ? activeIncident.Category
            : operation.Category;
        var location = string.IsNullOrWhiteSpace(operation.LocationLabel)
            ? null
            : new IncidentEventLocation(
                "location",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                operation.LocationLabel,
                operation.LocationLabel,
                callIds.Select(CallToken).ToList(),
                0.7);

        return new IncidentStateItem(
            activeIncident.IncidentKey,
            activeIncident.Status,
            title,
            string.IsNullOrWhiteSpace(operation.FrameTitle) ? activeIncident.Detail : operation.FrameTitle,
            category,
            0.5,
            callIds.Select(CallToken).ToList(),
            callIds.Select(CallToken).ToList(),
            [],
            [],
            category,
            new[] { title, operation.FrameTitle }.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
            false,
            location);
    }

    private static bool TryParseActiveIncidentTarget(string? value, out long incidentId)
    {
        incidentId = 0;
        var text = (value ?? string.Empty).Trim();
        if (!text.StartsWith("active:", StringComparison.OrdinalIgnoreCase))
            return false;
        return long.TryParse(text["active:".Length..], CultureInfo.InvariantCulture, out incidentId) && incidentId > 0;
    }

    private static IncidentCallDto ToIncidentCallDto(EngineCall call) =>
        new(call.Id, call.StartTime, call.Transcription, $"/api/v1/calls/{call.Id}/audio", call.Category, call.TalkgroupName, call.SystemShortName);

    private static IncidentFrameCurrentIncidentV3 BuildIncidentV3CurrentIncident(
        IncidentStateItem incident,
        IReadOnlySet<string> validCallTokens)
    {
        var callIds = FilterIncidentV3CurrentCallIds(incident.CallIds, validCallTokens);
        var rawCallIds = FilterIncidentV3CurrentCallIds(incident.RawCallIds, validCallTokens);
        return new IncidentFrameCurrentIncidentV3(
            incident.IncidentId,
            incident.Status,
            incident.Title,
            incident.Category,
            callIds,
            rawCallIds,
            incident.DroppedCallIds);
    }

    private static IReadOnlyList<string> FilterIncidentV3CurrentCallIds(
        IEnumerable<string> values,
        IReadOnlySet<string> validCallTokens)
    {
        return values
            .Select(value => TryNormalizeModelCallId(value, out var token) ? token : string.Empty)
            .Where(token => validCallTokens.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> IncidentV3CurrentCallTokens(long callId)
    {
        yield return callId.ToString(CultureInfo.InvariantCulture);
        yield return callId.ToString("X12", CultureInfo.InvariantCulture);
        yield return CallToken(callId);
    }

    private async Task RunIncidentV2ShadowAsync(
        string systemShortName,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentRagCandidate> candidateCalls,
        IncidentExtractionResult currentResult,
        string endpoint,
        CancellationToken ct)
    {
        if (!_config.AiInsights.IncidentV2ShadowEnabled || candidateCalls.Count == 0)
            return;

        try
        {
            var promptCalls = candidateCalls
                .OrderByDescending(row => row.Score)
                .ThenByDescending(row => row.GeoScore)
                .ThenByDescending(row => row.TimeScore)
                .Take(IncidentV2ShadowCandidateLimit())
                .OrderBy(row => row.Call.StartTime)
                .Select(row => new IncidentEvidencePromptCallV2(
                    row.Call.Id,
                    DateTimeOffset.FromUnixTimeSeconds(row.Call.StartTime).UtcDateTime,
                    row.Call.SystemShortName,
                    row.Call.Category,
                    row.Call.Transcription ?? string.Empty,
                    row.Reason,
                    row.Score))
                .ToList();
            if (promptCalls.Count == 0)
                return;

            var currentSummary = currentResult.Incidents
                .Take(IncidentMaxReturnedItems)
                .Select(incident => new
                {
                    incidentId = incident.IncidentId,
                    status = incident.Status,
                    title = incident.Title,
                    category = incident.Category,
                    callIds = incident.CallIds,
                    rawCallIds = incident.RawCallIds,
                    droppedCallIds = incident.DroppedCallIds,
                    malformedCallIdCount = incident.DroppedCallIds.Count
                })
                .ToList();
            _logger.LogInformation(
                "Incident v2 shadow baseline for {System}: currentIncidentCount={CurrentIncidentCount}; currentIncidents={CurrentIncidents}; candidateCallIds={CandidateCallIds}",
                systemShortName,
                currentResult.Incidents.Count,
                JsonSerializer.Serialize(currentSummary, EngineConfig.JsonOptions()),
                string.Join(",", promptCalls.Select(row => row.CallId)));
            foreach (var notice in ResolveShadowCandidatesFromCurrentResult(systemShortName, currentResult, DateTimeOffset.UtcNow))
            {
                _logger.LogInformation(
                    "Incident v2 shadow candidate lifecycle for {System}: candidateKey={CandidateKey}; lifecycle={Lifecycle}; acceptedCallIds={AcceptedCallIds}; pendingCallIds={PendingCallIds}; currentIncidentId={CurrentIncidentId}; currentTitle={CurrentTitle}; firstSeenUtc={FirstSeenUtc}; observations={Observations}",
                    systemShortName,
                    notice.CandidateKey,
                    notice.Lifecycle,
                    string.Join(",", notice.AcceptedCallIds),
                    string.Join(",", notice.PendingCallIds),
                    notice.CurrentIncidentId,
                    notice.CurrentTitle,
                    notice.FirstSeenUtc,
                    notice.Observations);
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
            var apiKey = InsightApiKey();
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var callContextsByCallId = candidateCalls
                .GroupBy(row => row.Call.Id)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var call = group.First().Call;
                        return new IncidentEvidenceCallContextV2(
                            call.Id,
                            call.Transcription ?? string.Empty,
                            call.Category,
                            call.TalkgroupName,
                            call.SystemShortName);
                    });
            var attemptPromptCalls = promptCalls;
            for (var shadowAttempt = 1; ; shadowAttempt++)
            {
                var prompt = IncidentEvidencePromptV2.Build(systemShortName, attemptPromptCalls, activeIncidents);
                var messages = new[]
                {
                    new { role = "system", content = prompt.SystemPrompt },
                    new { role = "user", content = prompt.UserPrompt }
                };
                var payload = JsonSerializer.Serialize(new
                {
                    model = InsightModel(),
                    temperature = 0.0,
                    max_tokens = IncidentV2ShadowMaxOutputTokens,
                    response_format = prompt.ResponseFormat,
                    messages
                }, EngineConfig.JsonOptions());
                _logger.LogInformation(
                    "Calling incident v2 shadow endpoint {Endpoint} with model {Model} for {System} ({CandidateCalls} candidate calls, {PayloadChars} chars, attempt {Attempt}).",
                    endpoint,
                    InsightModel(),
                    systemShortName,
                    attemptPromptCalls.Count,
                    payload.Length,
                    shadowAttempt);

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(endpoint, content, ct);
                var responseText = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode &&
                    (int)response.StatusCode == 400 &&
                    responseText.Contains("Invalid JSON Schema", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Incident v2 shadow endpoint rejected response_format for {System}; retrying without response_format.",
                        systemShortName);
                    payload = JsonSerializer.Serialize(new
                    {
                        model = InsightModel(),
                        temperature = 0.0,
                        max_tokens = IncidentV2ShadowMaxOutputTokens,
                        messages
                    }, EngineConfig.JsonOptions());
                    using var fallbackContent = new StringContent(payload, Encoding.UTF8, "application/json");
                    using var fallbackResponse = await client.PostAsync(endpoint, fallbackContent, ct);
                    responseText = await fallbackResponse.Content.ReadAsStringAsync(ct);
                    if (!fallbackResponse.IsSuccessStatusCode)
                        throw new InvalidOperationException($"Incident v2 shadow request failed with HTTP {(int)fallbackResponse.StatusCode}: {Trim(responseText, 1000)}");
                }
                else
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException($"Incident v2 shadow request failed with HTTP {(int)response.StatusCode}: {Trim(responseText, 1000)}");
                }

                var usage = ExtractUsage(responseText);
                if (string.Equals(usage.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    if (attemptPromptCalls.Count <= 3)
                        throw new InsightResponseTruncatedException($"LM incident v2 shadow response was truncated at max_tokens={IncidentV2ShadowMaxOutputTokens}.");

                    var smallerCount = Math.Max(3, (int)Math.Ceiling(attemptPromptCalls.Count / 2d));
                    _logger.LogWarning(
                        "Incident v2 shadow response for {System} truncated with {CandidateCalls} candidate calls; retrying with {RetryCandidateCalls} candidate calls.",
                        systemShortName,
                        attemptPromptCalls.Count,
                        smallerCount);
                    attemptPromptCalls = attemptPromptCalls
                        .OrderByDescending(row => row.RetrievalScore)
                        .Take(smallerCount)
                        .OrderBy(row => row.TimestampUtc)
                        .ThenBy(row => row.CallId)
                        .ToList();
                    continue;
                }

                IncidentHypothesisResponseV2 parsed;
                try
                {
                    parsed = IncidentEvidenceResponseParserV2.ParseOpenAiChatCompletion(responseText, systemShortName);
                }
                catch (JsonException) when (attemptPromptCalls.Count > 3)
                {
                    var smallerCount = Math.Max(3, (int)Math.Ceiling(attemptPromptCalls.Count / 2d));
                    _logger.LogWarning(
                        "Incident v2 shadow response for {System} was malformed JSON with {CandidateCalls} candidate calls; retrying with {RetryCandidateCalls} candidate calls.",
                        systemShortName,
                        attemptPromptCalls.Count,
                        smallerCount);
                    attemptPromptCalls = attemptPromptCalls
                        .OrderByDescending(row => row.RetrievalScore)
                        .Take(smallerCount)
                        .OrderBy(row => row.TimestampUtc)
                        .ThenBy(row => row.CallId)
                        .ToList();
                    continue;
                }
                var promptCandidateCallIds = attemptPromptCalls
                    .Select(row => row.CallId)
                    .Distinct()
                    .Order()
                    .ToList();
                if (parsed.Hypotheses.Count == 0)
                {
                    _logger.LogInformation(
                        "Incident v2 shadow returned no hypotheses for {System}; candidateCallIds={CandidateCallIds}; evaluating server recovery fallback.",
                        systemShortName,
                        string.Join(",", promptCandidateCallIds));
                    parsed = new IncidentHypothesisResponseV2(
                    [
                        new IncidentHypothesisV2(
                            "server-recovery",
                            systemShortName,
                            promptCandidateCallIds,
                            0,
                            [],
                            [],
                            [],
                            [],
                            new NarrativeEvidenceV2(string.Empty, string.Empty, []))
                    ]);
                }

                var shadowDecisions = new List<PersistenceDecisionV2>();
                foreach (var rawHypothesis in parsed.Hypotheses.Take(IncidentMaxReturnedItems))
                {
                    var hypothesis = rawHypothesis.CandidateCallIds.Count == 0
                        ? rawHypothesis with { CandidateCallIds = promptCandidateCallIds }
                        : rawHypothesis;
                    var decisions = IncidentEvidenceDecisionEngineV2.DecideMany(
                        hypothesis,
                        callContextsByCallId,
                        $"shadow:{systemShortName}:{hypothesis.HypothesisId}");
                    shadowDecisions.AddRange(decisions);
                }

                foreach (var decision in DeduplicateShadowDecisions(shadowDecisions)
                             .Select(decision => decision with
                             {
                                 CandidateRelationship = ClassifyShadowDecisionRelationship(systemShortName, decision, currentResult, DateTimeOffset.UtcNow)
                             }))
                {
                    _logger.LogInformation(
                        "Incident v2 shadow decision for {System}: hypothesisId={HypothesisId}; decision={Decision}; relationship={Relationship}; acceptedCallIds={AcceptedCallIds}; pendingCallIds={PendingCallIds}; rejectedCallIds={RejectedCallIds}; title={Title}; category={Category}; reasons={Reasons}; conflicts={Conflicts}",
                        systemShortName,
                        decision.IncidentKey,
                        decision.Decision,
                        decision.CandidateRelationship,
                        string.Join(",", decision.AcceptedCallIds),
                        string.Join(",", decision.PendingCallIds ?? []),
                        string.Join(",", decision.RejectedCallIds),
                        decision.Title,
                        decision.Category,
                        string.Join(" | ", decision.Reasons),
                        JsonSerializer.Serialize(decision.Conflicts, EngineConfig.JsonOptions()));
                }

                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Incident v2 shadow evaluation failed for {System}; live incident extraction will continue unchanged.",
                systemShortName);
        }
    }

    private static IReadOnlyList<PersistenceDecisionV2> DeduplicateShadowDecisions(IEnumerable<PersistenceDecisionV2> decisions) =>
        decisions
            .GroupBy(ShadowDecisionIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(ShadowDecisionTitleQuality).ThenBy(item => item.IncidentKey, StringComparer.OrdinalIgnoreCase).First())
            .ToList();

    private static string ShadowDecisionIdentityKey(PersistenceDecisionV2 decision) =>
        string.Join('|',
            decision.Decision,
            string.Join(",", decision.AcceptedCallIds.Distinct().Order()),
            string.Join(",", (decision.PendingCallIds ?? []).Distinct().Order()),
            string.Join(",", decision.RejectedCallIds.Distinct().Order()),
            decision.Category);

    private static int ShadowDecisionTitleQuality(PersistenceDecisionV2 decision)
    {
        var title = decision.Title ?? string.Empty;
        var score = title.Length;
        if (title.Contains(" at ", StringComparison.OrdinalIgnoreCase))
            score += 40;
        return score;
    }

    private string ClassifyShadowDecisionRelationship(
        string systemShortName,
        PersistenceDecisionV2 decision,
        IncidentExtractionResult currentResult,
        DateTimeOffset now)
    {
        if (string.Equals(decision.Decision, "shadow_reject", StringComparison.OrdinalIgnoreCase))
            return "rejected_candidate_batch";

        var decisionCallIds = decision.AcceptedCallIds
            .Concat(decision.PendingCallIds ?? [])
            .Distinct()
            .ToHashSet();
        if (decisionCallIds.Count == 0)
            return "no_candidate_members";

        var currentIncidentMatches = CurrentIncidentSnapshots(currentResult)
            .Where(incident => incident.CallIds.Overlaps(decisionCallIds))
            .ToList();
        if (currentIncidentMatches.Count > 1)
            return "competing_existing_incident_candidate";
        if (currentIncidentMatches.Count > 0)
            return string.Equals(decision.Decision, "shadow_pending", StringComparison.OrdinalIgnoreCase)
                ? "pending_existing_incident_update"
                : "existing_incident_update";

        var candidateKey = ShadowCandidateKey(systemShortName, decision);
        lock (_incidentV2ShadowGate)
        {
            PruneShadowCandidatesLocked(now);
            var relatedState = FindRelatedShadowCandidateLocked(systemShortName, decision.Category, decisionCallIds);
            if (relatedState is not null)
                candidateKey = relatedState.CandidateKey;

            if (!_incidentV2ShadowCandidates.TryGetValue(candidateKey, out var state))
            {
                state = new ShadowCandidateState(
                    candidateKey,
                    systemShortName,
                    decision.Decision,
                    decision.Category,
                    decision.Title,
                    decision.AcceptedCallIds.Distinct().Order().ToList(),
                    (decision.PendingCallIds ?? []).Distinct().Order().ToList(),
                    now,
                    now,
                    1,
                    false,
                    string.Empty,
                    string.Empty,
                    null);
            }
            else
            {
                var acceptedCallIds = MergeCallIds(state.AcceptedCallIds, decision.AcceptedCallIds);
                var pendingCallIds = MergeCallIds(state.PendingCallIds, decision.PendingCallIds ?? []);
                state = state with
                {
                    LastSeenUtc = now,
                    Observations = state.Observations + 1,
                    Title = PreferShadowTitle(state.Title, decision.Title),
                    AcceptedCallIds = acceptedCallIds,
                    PendingCallIds = pendingCallIds
                };
            }

            _incidentV2ShadowCandidates[candidateKey] = state;
            if (state.Resolved)
                return "resolved_existing_incident_followup";

            var candidateCallIds = state.AcceptedCallIds.Concat(state.PendingCallIds).ToHashSet();
            var hasCompetingPendingCandidate = _incidentV2ShadowCandidates.Values
                .Any(candidate => !candidate.Resolved
                                  && !string.Equals(candidate.CandidateKey, candidateKey, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(candidate.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase)
                                  && candidate.AcceptedCallIds.Concat(candidate.PendingCallIds).Any(candidateCallIds.Contains));
            if (hasCompetingPendingCandidate)
                return "competing_pending_candidate";
            if (state.Observations > 1 && now - state.FirstSeenUtc >= IncidentV2ShadowCandidateMaturity)
                return string.Equals(decision.Decision, "shadow_pending", StringComparison.OrdinalIgnoreCase)
                    ? "reobserved_pending_candidate_incident"
                    : "reobserved_new_candidate_incident";
        }

        return string.Equals(decision.Decision, "shadow_pending", StringComparison.OrdinalIgnoreCase)
            ? "pending_new_candidate_incident"
            : "pending_new_candidate_incident";
    }

    private IReadOnlyList<ShadowCandidateLifecycleNotice> ResolveShadowCandidatesFromCurrentResult(
        string systemShortName,
        IncidentExtractionResult currentResult,
        DateTimeOffset now)
    {
        var currentIncidents = CurrentIncidentSnapshots(currentResult);
        var notices = new List<ShadowCandidateLifecycleNotice>();
        lock (_incidentV2ShadowGate)
        {
            PruneShadowCandidatesLocked(now);
            foreach (var state in _incidentV2ShadowCandidates.Values.ToList())
            {
                if (state.Resolved || !string.Equals(state.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var candidateCallIds = state.AcceptedCallIds.Concat(state.PendingCallIds).ToHashSet();
                var currentIncident = currentIncidents.FirstOrDefault(incident => incident.CallIds.Overlaps(candidateCallIds));
                if (currentIncident is null)
                    continue;

                _incidentV2ShadowCandidates[state.CandidateKey] = state with
                {
                    Resolved = true,
                    LastSeenUtc = now,
                    ResolvedIncidentId = currentIncident.IncidentId,
                    ResolvedTitle = currentIncident.Title,
                    ResolvedAtUtc = now
                };
                notices.Add(new ShadowCandidateLifecycleNotice(
                    state.CandidateKey,
                    "resolved_to_existing_incident",
                    state.AcceptedCallIds,
                    state.PendingCallIds,
                    currentIncident.IncidentId,
                    currentIncident.Title,
                    state.FirstSeenUtc,
                    state.Observations));
            }
        }

        return notices;
    }

    private void PruneShadowCandidatesLocked(DateTimeOffset now)
    {
        foreach (var key in _incidentV2ShadowCandidates
                     .Where(row => now - row.Value.LastSeenUtc > IncidentV2ShadowCandidateRetention)
                     .Select(row => row.Key)
                     .ToList())
            _incidentV2ShadowCandidates.Remove(key);
    }

    private static IReadOnlyList<CurrentIncidentSnapshot> CurrentIncidentSnapshots(IncidentExtractionResult currentResult) =>
        currentResult.Incidents
            .Select(incident => new CurrentIncidentSnapshot(incident.IncidentId, incident.Title, ParseIncidentStateCallIds(incident)))
            .Where(incident => incident.CallIds.Count > 0)
            .ToList();

    private ShadowCandidateState? FindRelatedShadowCandidateLocked(string systemShortName, string category, HashSet<long> decisionCallIds) =>
        _incidentV2ShadowCandidates.Values
            .Where(candidate => string.Equals(candidate.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => string.Equals(candidate.Category, category, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => candidate.AcceptedCallIds.Concat(candidate.PendingCallIds).Any(decisionCallIds.Contains))
            .OrderByDescending(candidate => candidate.Resolved)
            .ThenByDescending(candidate => candidate.LastSeenUtc)
            .FirstOrDefault();

    private static string ShadowCandidateKey(string systemShortName, PersistenceDecisionV2 decision)
    {
        var callIds = decision.AcceptedCallIds
            .Concat(decision.PendingCallIds ?? [])
            .Distinct()
            .Order()
            .ToList();
        return $"{systemShortName}|{decision.Category}|{string.Join(",", callIds)}";
    }

    private static string PreferShadowTitle(string existing, string next)
    {
        if (string.IsNullOrWhiteSpace(existing))
            return next;
        if (string.IsNullOrWhiteSpace(next))
            return existing;
        return next.Length > existing.Length ? next : existing;
    }

    private static IReadOnlyList<long> MergeCallIds(IReadOnlyList<long> existing, IReadOnlyList<long> next) =>
        existing.Concat(next).Distinct().Order().ToList();

    private static HashSet<long> ParseIncidentStateCallIds(IncidentStateItem incident)
    {
        var ids = new HashSet<long>();
        foreach (var value in incident.CallIds)
        {
            if (!TryNormalizeModelCallId(value, out var token))
                continue;

            if (token.Length == 13
                && token[0] == 'C'
                && long.TryParse(token[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexId))
            {
                ids.Add(hexId);
                continue;
            }

            if (long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var decimalId))
                ids.Add(decimalId);
        }

        return ids;
    }

    private static bool IsIncidentExtractionTimeout(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;
            if (current is OperationCanceledException)
                return true;
            if (current is TaskCanceledException &&
                current.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (current.Message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static TimeSpan IncidentExtractionFallbackChunkTimeout(int candidateCount, int configuredTimeoutMs)
    {
        var configured = TimeSpan.FromMilliseconds(Math.Max(1000, configuredTimeoutMs));
        var scaledSeconds = Math.Max(1, candidateCount) * IncidentFallbackChunkTimeoutBaseSeconds;
        var boundedSeconds = Math.Clamp(
            scaledSeconds,
            IncidentFallbackChunkTimeoutMinSeconds,
            IncidentFallbackChunkTimeoutMaxSeconds);
        var fallback = TimeSpan.FromSeconds(boundedSeconds);
        return fallback < configured ? fallback : configured;
    }

    private static bool ShouldStopIncidentExtractionFallbackAfterTerminalTimeouts(int consecutiveTerminalTimeoutSkips) =>
        consecutiveTerminalTimeoutSkips >= IncidentFallbackMaxConsecutiveTerminalTimeoutSkips;

    private async Task<IReadOnlyDictionary<long, VectorSearchMatchDto>> SearchVectorCandidatesAsync(
        string systemShortName,
        List<IncidentDto> activeIncidents,
        List<EngineCall> recent,
        HashSet<long> newCallIds,
        long start,
        long end,
        CancellationToken ct)
    {
        var matches = new Dictionary<long, VectorSearchMatchDto>();
        var recentById = recent.ToDictionary(c => c.Id);
        foreach (var call in recent.Where(c => newCallIds.Contains(c.Id)).OrderBy(c => c.StartTime).Take(IncidentNewVectorQueryLimit()))
        {
            var rows = await _embeddings.SearchSimilarAsync(call.Transcription, systemShortName, start, end, 20, ct);
            foreach (var row in rows)
            {
                if (row.CallId == call.Id || !recentById.ContainsKey(row.CallId))
                    continue;
                AddVectorMatch(matches, row with { Reason = $"qdrant:new:{CallToken(call.Id)}" });
            }
        }

        foreach (var incident in activeIncidents.OrderByDescending(i => i.LastSeen).Take(IncidentActiveVectorQueryLimit()))
        {
            var query = $"{incident.Title}\n{incident.Detail}\n{string.Join('\n', incident.Calls.OrderByDescending(c => c.RawTimestamp).Take(6).Select(c => c.Transcript))}";
            var rows = await _embeddings.SearchSimilarAsync(query, systemShortName, start, end, 20, ct);
            foreach (var row in rows)
            {
                if (!recentById.ContainsKey(row.CallId))
                    continue;
                AddVectorMatch(matches, row with { Reason = $"qdrant:incident:{incident.IncidentKey}" });
            }
        }
        return matches;
    }

    private static void AddVectorMatch(Dictionary<long, VectorSearchMatchDto> matches, VectorSearchMatchDto row)
    {
        if (!matches.TryGetValue(row.CallId, out var existing) || row.Score > existing.Score)
            matches[row.CallId] = row;
    }

    private async Task<int> PersistIncidentStateAsync(string systemShortName, IncidentExtractionResult result, List<IncidentDto> activeIncidents, List<EngineCall> recentCalls, IReadOnlyList<IncidentRagCandidate> ragCandidates, IReadOnlyList<CallLocationDashboardRow> locationRows, CancellationToken ct)
    {
        var byToken = new Dictionary<string, EngineCall>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in recentCalls)
        {
            byToken[CallToken(call.Id)] = call;
            byToken[call.Id.ToString(CultureInfo.InvariantCulture)] = call;
            byToken[call.Id.ToString("X12", CultureInfo.InvariantCulture)] = call;
        }
        var anchorsByCall = await _database.GetCallAnchorsAsync(recentCalls.Select(c => c.Id).Distinct().ToList(), ct);

        var existingByKey = activeIncidents
            .Where(i => !string.IsNullOrWhiteSpace(i.IncidentKey))
            .ToDictionary(i => i.IncidentKey, StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        var claimed = new HashSet<long>();
        var ragById = ragCandidates.ToDictionary(c => c.Call.Id);
        var preparedItems = new List<(IncidentStateItem Item, List<EngineCall> Calls)>();
        foreach (var item in result.Incidents.Take(40))
        {
            var callIds = item.CallIds
                .Select(id => byToken.TryGetValue(id.Trim(), out var call) ? call : null)
                .Where(c => c != null)
                .DistinctBy(c => c!.Id)
                .Cast<EngineCall>()
                .ToList();
            if (callIds.Count == 0)
            {
                await AuditIncidentOperationAsync(systemShortName, item, "rejected:fewer than 1 resolved call", false, callIds, ragById, ct);
                continue;
            }

            if (IsStandaloneLogisticsIncident(item, callIds))
            {
                await AuditIncidentOperationAsync(systemShortName, item, $"rejected:{item.EventClass} lacks parent emergency/event evidence", false, callIds, ragById, ct);
                continue;
            }

            preparedItems.Add((item, callIds));
        }

        var modelAnchorCallIds = preparedItems
            .SelectMany(row => row.Calls.Select(call => call.Id))
            .Distinct()
            .ToList();
        var modelEventLocationAnchors = BuildModelEventLocationAnchors(preparedItems, byToken);
        anchorsByCall = ReplaceAnchorsBySourceInMemory(anchorsByCall, modelAnchorCallIds, ModelEventLocationAnchorSource, modelEventLocationAnchors);
        var acceptedModelEventLocationAnchors = new List<CallAnchorRecord>();

        var reconciliationCandidates = preparedItems
            .Select((row, index) => new IncidentReconciliationCandidate(
                index,
                row.Item.IncidentId,
                TrimIncidentText(row.Item.Title ?? string.Empty, IncidentTitleCharLimit),
                TrimIncidentText(row.Item.Detail ?? string.Empty, IncidentDetailCharLimit),
                row.Item.Category,
                row.Item.Confidence,
                row.Calls))
            .ToList();
        var reconciliation = _reconciliation.Reconcile(reconciliationCandidates, activeIncidents, locationRows, anchorsByCall, ragById);
        foreach (var audit in reconciliation.AuditRows)
        {
            if (audit.CandidateIndex >= 0 && audit.CandidateIndex < preparedItems.Count)
                await AuditReconciliationOperationAsync(systemShortName, preparedItems[audit.CandidateIndex].Item, audit, ct);
        }

        foreach (var decision in reconciliation.Decisions)
        {
            if (decision.PrimaryIndex < 0 || decision.PrimaryIndex >= preparedItems.Count)
                continue;

            var item = preparedItems[decision.PrimaryIndex].Item with
            {
                IncidentId = decision.IncidentId,
                Title = decision.Title,
                Detail = decision.Detail,
                Category = decision.Category,
                Confidence = decision.Confidence,
                CallIds = decision.CallIds.Select(CallToken).ToList()
            };
            var callIds = decision.CallIds
                .Select(id => byToken.TryGetValue(CallToken(id), out var call) ? call : null)
                .Where(c => c != null)
                .Cast<EngineCall>()
                .OrderBy(c => c.StartTime)
                .ToList();
            if (callIds.Count == 0)
            {
                await AuditIncidentOperationAsync(systemShortName, item, "rejected:reconciliation left no resolved calls", false, callIds, ragById, ct);
                continue;
            }

            var status = string.Equals(item.Status, "concluded", StringComparison.OrdinalIgnoreCase) ? "concluded" : "active";
            var suppliedIncidentId = (item.IncidentId ?? string.Empty).Trim();
            var suppliedIsCreate = IncidentIdentity.IsCreateMarker(suppliedIncidentId);
            var suppliedUnknownIncidentId = !suppliedIsCreate
                                            && !string.IsNullOrWhiteSpace(suppliedIncidentId)
                                            && !existingByKey.ContainsKey(suppliedIncidentId);
            IncidentDto? suppliedExistingIncident = null;
            var ignoredSuppliedIncidentReason = string.Empty;
            IncidentDto? existingIncident = null;
            if (!suppliedIsCreate && existingByKey.TryGetValue(suppliedIncidentId, out var suppliedExisting))
            {
                suppliedExistingIncident = suppliedExisting;
                var suppliedMatch = ScoreIncidentEvidenceMatch(callIds, suppliedExisting, locationRows, anchorsByCall, ragById);
                if (suppliedMatch.Accepted)
                {
                    existingIncident = suppliedExisting;
                }
                else
                {
                    ignoredSuppliedIncidentReason = $"ignored exact existing incident_id '{suppliedIncidentId}' because candidate calls lacked same-event evidence";
                }
            }

            if (existingIncident is null)
            {
                var matchPool = suppliedExistingIncident is null
                    ? activeIncidents
                    : activeIncidents.Where(incident => incident.Id != suppliedExistingIncident.Id).ToList();
                existingIncident = FindBestExistingIncidentMatch(callIds, matchPool, locationRows, anchorsByCall, ragById).Incident;
            }

            var title = TrimIncidentText(item.Title ?? string.Empty, IncidentTitleCharLimit);
            var detail = TrimIncidentText(item.Detail ?? string.Empty, IncidentDetailCharLimit);
            if (existingIncident is not null)
            {
                var existingCalls = existingIncident.Calls
                    .Select(IncidentCallToEngineCall)
                    .ToList();
                callIds = callIds
                    .Concat(existingCalls)
                    .DistinctBy(c => c.Id)
                    .Where(c => !claimed.Contains(c.Id) || existingIncident.Calls.Any(existing => existing.CallId == c.Id))
                    .OrderBy(c => c.StartTime)
                    .ToList();
            }
            else
            {
                callIds = callIds
                    .Where(c => !claimed.Contains(c.Id))
                    .OrderBy(c => c.StartTime)
                    .ToList();
            }

            if (callIds.Count == 0)
            {
                await AuditIncidentOperationAsync(systemShortName, item, "rejected:reconciliation left no unclaimed calls", false, callIds, ragById, ct);
                continue;
            }

            if (!IsIncidentNarrativeAcceptable(title, detail, callIds))
            {
                _logger.LogInformation(
                    "Rejected incident state item '{Title}' for {System}: detail is too long or transcript-like. DetailLength={DetailLength}; Calls={CallIds}",
                    item.Title,
                    systemShortName,
                    item.Detail?.Length ?? 0,
                    string.Join(",", callIds.Select(c => c.Id)));
                await AuditIncidentOperationAsync(systemShortName, item, "rejected:narrative failed validation", false, callIds, ragById, ct);
                continue;
            }

            var verifierIncidentKey = existingIncident?.IncidentKey ?? "new";
            var verification = await VerifyIncidentEvidenceAsync(systemShortName, title, detail, verifierIncidentKey, callIds, existingIncident, recentCalls, ragCandidates, locationRows, claimed, ct);
            if (verification.Calls.Count == 0)
            {
                _logger.LogInformation("Rejected incident state item '{Title}' for {System}: evidence verifier retained no calls", title, systemShortName);
                await AuditIncidentOperationAsync(systemShortName, item, "rejected:evidence verifier retained no calls", false, verification.Calls, ragById, ct);
                continue;
            }
            var target = await ResolveIncidentTargetAsync(
                systemShortName,
                item,
                existingIncident,
                suppliedUnknownIncidentId,
                ignoredSuppliedIncidentReason,
                verification,
                activeIncidents,
                locationRows,
                anchorsByCall,
                ragById,
                ct);
            if (!target.Accepted)
            {
                await AuditIncidentOperationAsync(systemShortName, item, target.Reason, false, verification.Calls, ragById, ct);
                continue;
            }

            existingIncident = target.Incident;
            verification = AddExistingTargetEvidence(verification, existingIncident, target.MergeIncidents);

            var assembly = await AssembleIncidentMembershipAsync(item, existingIncident, target.MergeIncidents, verification, locationRows, anchorsByCall, ragById, ct);
            if (!assembly.Accepted)
            {
                await AuditIncidentOperationAsync(systemShortName, item, assembly.Reason, false, verification.Calls, ragById, ct);
                continue;
            }
            if (assembly.ForceNewIncident)
                existingIncident = null;
            if (verification.Calls.Count != assembly.Calls.Count)
                await AuditIncidentOperationAsync(systemShortName, item, $"accepted:assembler retained {assembly.Calls.Count}/{verification.Calls.Count} verifier call(s): {assembly.Reason}", true, assembly.Calls, ragById, ct);

            var finalMembership = FinalizeServerOwnedMembership(item, existingIncident, assembly.Calls, locationRows, anchorsByCall, ragById);
            if (!finalMembership.Accepted)
            {
                await AuditIncidentOperationAsync(systemShortName, item, finalMembership.Reason, false, assembly.Calls, ragById, ct);
                continue;
            }
            if (finalMembership.Calls.Count != assembly.Calls.Count || finalMembership.Reason.Contains("validator matched", StringComparison.OrdinalIgnoreCase))
                await AuditIncidentOperationAsync(systemShortName, item, $"accepted:final membership retained {finalMembership.Calls.Count}/{assembly.Calls.Count} assembler call(s): {finalMembership.Reason}", true, finalMembership.Calls, ragById, ct);
            callIds = finalMembership.Calls;

            var key = existingIncident?.IncidentKey ?? IncidentIdentity.BuildServerOwnedKey(systemShortName, callIds.Select(c => c.Id).ToList());
            var category = NormalizeIncidentCategory(item.Category, item.EventClass, title, detail, callIds);
            var narrativeCalls = callIds.Select(c => ToIncidentCandidateCall(c, anchorsByCall)).ToList();
            var groundedNarrative = IncidentNarrativeGrounder.Ground(
                title,
                detail,
                category,
                narrativeCalls);
            var rewroteExistingNarrative = false;
            if (existingIncident is not null)
            {
                var existingNarrative = IncidentNarrativeGrounder.Ground(
                    existingIncident.Title,
                    existingIncident.Detail,
                    category,
                    narrativeCalls);
                if (existingNarrative.WasRewritten && existingNarrative.HasSpecificEvidence)
                {
                    title = TrimIncidentText(existingNarrative.Title, IncidentTitleCharLimit);
                    detail = TrimIncidentText(existingNarrative.Detail, IncidentDetailCharLimit);
                    groundedNarrative = existingNarrative;
                    rewroteExistingNarrative = true;
                }
                else
                {
                    title = TrimIncidentText(existingIncident.Title, IncidentTitleCharLimit);
                    detail = TrimIncidentText(existingIncident.Detail, IncidentDetailCharLimit);
                }
            }
            else
            {
                if (!groundedNarrative.HasSpecificEvidence)
                {
                    await AuditIncidentOperationAsync(systemShortName, item, "rejected:unsupported narrative lacked a specific evidence-backed fallback", false, callIds, ragById, ct);
                    continue;
                }

                title = TrimIncidentText(groundedNarrative.Title, IncidentTitleCharLimit);
                detail = TrimIncidentText(groundedNarrative.Detail, IncidentDetailCharLimit);
            }
            category = NormalizeIncidentCategory(item.Category, item.EventClass, title, detail, callIds);
            var auditItem = item with
            {
                IncidentId = key,
                Title = title,
                Detail = detail,
                Category = category
            };
            var incidentToPersist = new IncidentDto
            {
                Id = existingIncident?.Id ?? 0,
                IncidentKey = key,
                Title = string.IsNullOrWhiteSpace(title) ? "Radio incident" : title,
                Detail = string.IsNullOrWhiteSpace(detail) ? "Multiple related calls describe the same incident." : detail,
                Category = category,
                Status = status,
                FirstSeen = callIds.Min(c => c.StartTime),
                LastSeen = callIds.Max(c => c.StartTime),
                Confidence = Math.Clamp(item.Confidence, 0, 1),
                Calls = callIds.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio", c.Category, c.TalkgroupName, c.SystemShortName)).ToList()
            };
            var mergeIncidentIds = target.MergeIncidents.Select(i => i.Id).ToList();
            var id = mergeIncidentIds.Count == 0
                ? await _database.UpsertManagedIncidentAsync(incidentToPersist, ct)
                : await _database.UpsertManagedIncidentAndMergeAsync(incidentToPersist, mergeIncidentIds, ct);
            if (id <= 0)
            {
                await AuditIncidentOperationAsync(systemShortName, item, "rejected:database upsert returned no row", false, callIds, ragById, ct);
                continue;
            }

            foreach (var call in callIds)
                claimed.Add(call.Id);
            existingByKey[key] = new IncidentDto
            {
                Id = id,
                IncidentKey = key,
                Title = string.IsNullOrWhiteSpace(title) ? "Radio incident" : title,
                Detail = string.IsNullOrWhiteSpace(detail) ? "Multiple related calls describe the same incident." : detail,
                Category = category,
                Status = status,
                FirstSeen = callIds.Min(c => c.StartTime),
                LastSeen = callIds.Max(c => c.StartTime),
                Confidence = Math.Clamp(item.Confidence, 0, 1),
                Calls = callIds.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio", c.Category, c.TalkgroupName, c.SystemShortName)).ToList()
            };
            var acceptedReason = existingIncident is null ? "accepted:create incident" : "accepted:update incident";
            if (!string.IsNullOrWhiteSpace(decision.Reason))
                acceptedReason = $"{acceptedReason}; reconciled:{decision.Reason}";
            if (!string.IsNullOrWhiteSpace(target.Reason))
                acceptedReason = $"{acceptedReason}; identity:{target.Reason}";
            if (mergeIncidentIds.Count > 0)
                acceptedReason = $"{acceptedReason}; merged duplicate incident(s) {string.Join(",", mergeIncidentIds)}";
            if (rewroteExistingNarrative)
                acceptedReason = $"{acceptedReason}; narrative:rewrote existing title/detail from final membership";
            else if (groundedNarrative.WasRewritten && existingIncident is null)
                acceptedReason = $"{acceptedReason}; narrative:{groundedNarrative.Reason}";
            else if (groundedNarrative.WasRewritten)
                acceptedReason = $"{acceptedReason}; narrative:preserved existing title/detail after unsupported update proposal";
            await AuditIncidentOperationAsync(systemShortName, auditItem, acceptedReason, true, callIds, ragById, ct);
            var acceptedCallIds = callIds.Select(c => c.Id).ToHashSet();
            acceptedModelEventLocationAnchors.AddRange(modelEventLocationAnchors.Where(anchor => acceptedCallIds.Contains(anchor.CallId)));
            foreach (var merged in target.MergeIncidents)
                existingByKey.Remove(merged.IncidentKey);
            activeIncidents.RemoveAll(incident => target.MergeIncidents.Any(merged => merged.Id == incident.Id));
            activeIncidents.RemoveAll(incident => incident.Id == id);
            activeIncidents.Add(existingByKey[key]);
            updated++;
        }

        updated += await RepairSiblingIncidentsAsync(systemShortName, activeIncidents, locationRows, anchorsByCall, ragById, ct);

        await _database.ReplaceCallAnchorsBySourceAsync(
            modelAnchorCallIds,
            ModelEventLocationAnchorSource,
            acceptedModelEventLocationAnchors
                .DistinctBy(anchor => (anchor.CallId, anchor.Kind.ToLowerInvariant(), anchor.Value.ToLowerInvariant(), anchor.Source.ToLowerInvariant()))
                .ToList(),
            ct);

        return updated;
    }

    private static List<CallAnchorRecord> BuildModelEventLocationAnchors(
        IReadOnlyList<(IncidentStateItem Item, List<EngineCall> Calls)> preparedItems,
        IReadOnlyDictionary<string, EngineCall> byToken)
    {
        var anchors = new List<CallAnchorRecord>();
        foreach (var (item, calls) in preparedItems)
        {
            var eventLocation = item.EventLocation;
            if (eventLocation is null)
                continue;

            var highwayAnchor = TryBuildHighwayMileMarkerAnchor(item, eventLocation);
            if (highwayAnchor is null)
                continue;

            var callSet = calls.Select(call => call.Id).ToHashSet();
            var supportedCallIds = eventLocation.SupportingCallIds
                .Select(id => byToken.TryGetValue(id.Trim(), out var call) ? call.Id : 0)
                .Where(id => id != 0 && callSet.Contains(id))
                .Distinct()
                .ToList();
            if (supportedCallIds.Count == 0)
                supportedCallIds = calls.Select(call => call.Id).Distinct().ToList();

            anchors.AddRange(supportedCallIds.Select(callId => highwayAnchor with { CallId = callId }));
        }

        return anchors
            .DistinctBy(anchor => (anchor.CallId, anchor.Kind.ToLowerInvariant(), anchor.Value.ToLowerInvariant(), anchor.Source.ToLowerInvariant()))
            .ToList();
    }

    private static CallAnchorRecord? TryBuildHighwayMileMarkerAnchor(IncidentStateItem item, IncidentEventLocation location)
    {
        if (!string.Equals(location.Kind, "highway_mile_marker", StringComparison.OrdinalIgnoreCase))
            return null;

        var route = NormalizeEventRoute(location.Route);
        var marker = NormalizeEventMarker(location.Marker);
        if (string.IsNullOrWhiteSpace(route) || string.IsNullOrWhiteSpace(marker))
            return null;

        var direction = NormalizeEventDirection(location.Direction);
        var value = $"{route.ToLowerInvariant()}|mm:{marker}";
        var display = string.IsNullOrWhiteSpace(direction)
            ? $"{route} MM {marker}"
            : $"{route} MM {marker} {direction}";
        var detailsJson = JsonSerializer.Serialize(new
        {
            incident_id = item.IncidentId,
            title = item.Title,
            location_text = location.Text,
            evidence_text = location.EvidenceText,
            direction,
            model_confidence = location.Confidence
        }, EngineConfig.JsonOptions());

        return new CallAnchorRecord(
            0,
            "highway_mile_marker",
            value,
            display,
            ModelEventLocationAnchorSource,
            Math.Clamp(location.Confidence > 0 ? location.Confidence : ModelEventLocationAnchorConfidence, 0.72, 0.98),
            detailsJson);
    }

    private static string NormalizeEventRoute(string route)
    {
        route = (route ?? string.Empty).Trim().ToUpperInvariant();
        var match = Regex.Match(route, @"^(?:I|INTERSTATE)[-\s]?(\d{1,3})$", RegexOptions.CultureInvariant);
        return match.Success ? $"I-{match.Groups[1].Value}" : string.Empty;
    }

    private static string NormalizeEventMarker(string marker)
    {
        marker = (marker ?? string.Empty).Trim();
        var match = Regex.Match(marker, @"^\d{1,3}(?:\.\d{1,2})?$", RegexOptions.CultureInvariant);
        if (!match.Success || !decimal.TryParse(marker, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return string.Empty;
        return value % 1 == 0
            ? ((int)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string NormalizeEventDirection(string direction)
    {
        direction = (direction ?? string.Empty).Trim().ToUpperInvariant();
        return direction switch
        {
            "N" or "NORTH" or "NORTHBOUND" or "NB" => "NB",
            "S" or "SOUTH" or "SOUTHBOUND" or "SB" => "SB",
            "E" or "EAST" or "EASTBOUND" or "EB" => "EB",
            "W" or "WEST" or "WESTBOUND" or "WB" => "WB",
            _ => string.Empty
        };
    }

    private static IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> ReplaceAnchorsBySourceInMemory(
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyList<long> callIds,
        string source,
        IReadOnlyList<CallAnchorRecord> additionalAnchors)
    {
        if (callIds.Count == 0 && additionalAnchors.Count == 0)
            return anchorsByCall;

        var merged = anchorsByCall.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToList());
        foreach (var callId in callIds)
        {
            if (!merged.TryGetValue(callId, out var anchors))
            {
                merged[callId] = [];
                continue;
            }

            anchors.RemoveAll(anchor => string.Equals(anchor.Source, source, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var anchor in additionalAnchors)
        {
            if (!merged.TryGetValue(anchor.CallId, out var anchors))
            {
                anchors = [];
                merged[anchor.CallId] = anchors;
            }
            if (!anchors.Any(existing =>
                    string.Equals(existing.Kind, anchor.Kind, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Value, anchor.Value, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Source, anchor.Source, StringComparison.OrdinalIgnoreCase)))
                anchors.Add(anchor);
        }

        return merged.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<CallAnchorRecord>)kvp.Value);
    }

    private async Task<int> RepairSiblingIncidentsAsync(
        string systemShortName,
        List<IncidentDto> activeIncidents,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById,
        CancellationToken ct)
    {
        var sameSystem = activeIncidents
            .Where(incident => incident.Id > 0)
            .Where(incident => incident.Calls.Any(call => string.Equals(call.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(incident => incident.FirstSeen)
            .ThenBy(incident => incident.Id)
            .ToList();
        if (sameSystem.Count < 2)
            return 0;

        var repaired = 0;
        var consumed = new HashSet<long>();
        foreach (var seed in sameSystem)
        {
            if (consumed.Contains(seed.Id))
                continue;

            var group = new List<IncidentDto> { seed };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var candidate in sameSystem)
                {
                    if (candidate.Id == seed.Id || consumed.Contains(candidate.Id) || group.Any(existing => existing.Id == candidate.Id))
                        continue;
                    if (!group.Any(existing => AreIncidentsMergeable(existing, candidate, [], locationRows, anchorsByCall, ragById)))
                        continue;

                    group.Add(candidate);
                    changed = true;
                }
            }

            if (group.Count < 2)
                continue;

            var primary = ChoosePrimaryIncidentForMerge(group);
            var duplicates = group.Where(incident => incident.Id != primary.Id).ToList();
            var allCalls = group
                .SelectMany(incident => incident.Calls)
                .DistinctBy(call => call.CallId)
                .OrderBy(call => call.RawTimestamp)
                .ToList();
            var allEngineCalls = allCalls
                .Select(IncidentCallToEngineCall)
                .ToList();
            var repairValidation = FinalizeServerOwnedMembership(
                SiblingRepairValidationItem(primary, allEngineCalls),
                primary,
                allEngineCalls,
                locationRows,
                anchorsByCall,
                ragById);
            var subsetOnlyValidation = repairValidation.Reason.Contains("validator matched", StringComparison.OrdinalIgnoreCase);
            if (!repairValidation.Accepted || repairValidation.Calls.Count != allEngineCalls.Count || subsetOnlyValidation)
            {
                var reason = repairValidation.Accepted
                    ? $"rejected:server sibling merge repair failed final membership validation: {repairValidation.Reason}"
                    : repairValidation.Reason;
                await _database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(
                    0,
                    DateTime.UtcNow,
                    systemShortName,
                    primary.IncidentKey,
                    "reject_incident",
                    false,
                    reason,
                    primary.Confidence,
                    JsonSerializer.Serialize(allEngineCalls.Select(call => call.Id).Order().ToList()),
                    JsonSerializer.Serialize(new
                    {
                        source = "server_sibling_merge_repair",
                        primaryIncidentId = primary.Id,
                        duplicateIncidentIds = duplicates.Select(incident => incident.Id).ToList(),
                        rule = "refused sibling merge repair because final membership validation rejected, pruned, or only proved a subset of the union"
                    }, EngineConfig.JsonOptions())), ct);
                _logger.LogInformation(
                    "Skipped sibling incident repair into {IncidentKey} for {System}: final membership rejected, pruned, or only proved subset of union. Reason={Reason}; DuplicateIncidentIds={DuplicateIncidentIds}",
                    primary.IncidentKey,
                    systemShortName,
                    repairValidation.Reason,
                    string.Join(",", duplicates.Select(incident => incident.Id)));
                continue;
            }

            var mergedIncident = primary with
            {
                Calls = allCalls,
                FirstSeen = allCalls.Min(call => call.RawTimestamp),
                LastSeen = allCalls.Max(call => call.RawTimestamp),
                Confidence = group.Max(incident => incident.Confidence),
                Category = string.IsNullOrWhiteSpace(primary.Category) || primary.Category == "other"
                    ? DominantIncidentCategory(allCalls)
                    : primary.Category
            };

            var id = await _database.UpsertManagedIncidentAndMergeAsync(mergedIncident, duplicates.Select(incident => incident.Id).ToList(), ct);
            if (id <= 0)
                continue;

            foreach (var incident in duplicates)
                consumed.Add(incident.Id);
            consumed.Add(primary.Id);
            activeIncidents.RemoveAll(incident => group.Any(merged => merged.Id == incident.Id));
            activeIncidents.Add(mergedIncident with { Id = id });
            await _database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(
                0,
                DateTime.UtcNow,
                systemShortName,
                mergedIncident.IncidentKey,
                "upsert_incident",
                true,
                $"accepted:server sibling merge repair; merged duplicate incident(s) {string.Join(",", duplicates.Select(incident => incident.Id))}",
                mergedIncident.Confidence,
                JsonSerializer.Serialize(allCalls.Select(call => call.CallId).Order().ToList()),
                JsonSerializer.Serialize(new
                {
                    source = "server_sibling_merge_repair",
                    primaryIncidentId = primary.Id,
                    duplicateIncidentIds = duplicates.Select(incident => incident.Id).ToList(),
                    rule = "same-system active incidents with strong retained call evidence link"
                }, EngineConfig.JsonOptions())), ct);
            _logger.LogInformation(
                "Merged sibling incident(s) {DuplicateIncidentIds} into {IncidentKey} for {System} using server-side evidence repair.",
                string.Join(",", duplicates.Select(incident => incident.Id)),
                mergedIncident.IncidentKey,
                systemShortName);
            repaired++;
        }

        return repaired;
    }

    private static IncidentStateItem SiblingRepairValidationItem(IncidentDto primary, IReadOnlyList<EngineCall> calls) =>
        new(
            primary.IncidentKey,
            string.IsNullOrWhiteSpace(primary.Status) ? "active" : primary.Status,
            primary.Title,
            primary.Detail,
            primary.Category,
            primary.Confidence,
            calls.Select(call => CallToken(call.Id)).ToList(),
            calls.Select(call => CallToken(call.Id)).ToList(),
            [],
            [],
            "public_safety_event",
            [],
            false,
            null);

    private static IncidentDto ChoosePrimaryIncidentForMerge(IReadOnlyList<IncidentDto> incidents) =>
        incidents
            .OrderBy(incident => IsGenericIncidentTitle(incident.Title) ? 1 : 0)
            .ThenBy(incident => incident.FirstSeen)
            .ThenByDescending(incident => incident.Calls.Count)
            .ThenBy(incident => incident.Id)
            .First();

    private static bool IsGenericIncidentTitle(string title) =>
        Regex.IsMatch(title ?? string.Empty, @"^(?:police|ems|fire|traffic|public safety|medical assist|vehicle off roadway|unconscious patient)\s*(?:call|incident)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string DominantIncidentCategory(IReadOnlyList<IncidentCallDto> calls) =>
        calls.GroupBy(call => string.IsNullOrWhiteSpace(call.Category) ? "other" : call.Category)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "other";

    private async Task<IncidentTargetResolution> ResolveIncidentTargetAsync(
        string systemShortName,
        IncidentStateItem item,
        IncidentDto? preliminaryIncident,
        bool suppliedUnknownIncidentId,
        string ignoredSuppliedIncidentReason,
        EvidenceVerificationSelection verification,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById,
        CancellationToken ct)
    {
        var calls = verification.Calls.DistinctBy(c => c.Id).OrderBy(c => c.StartTime).ToList();
        if (calls.Count == 0)
            return new(false, null, [], "rejected:identity resolver received no verifier-retained calls");

        var owners = await _database.GetIncidentCallOwnersAsync(calls.Select(c => c.Id).ToList(), ct);
        var activeById = activeIncidents
            .Where(incident => incident.Id > 0)
            .GroupBy(incident => incident.Id)
            .ToDictionary(g => g.Key, g => g.First());
        var ownedIncidents = owners.Values
            .Select(owner => activeById.TryGetValue(owner.IncidentId, out var incident) ? incident : null)
            .Where(incident => incident is not null)
            .Cast<IncidentDto>()
            .DistinctBy(incident => incident.Id)
            .ToList();

        var reasonParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ignoredSuppliedIncidentReason))
            reasonParts.Add(ignoredSuppliedIncidentReason);

        var target = preliminaryIncident;
        var preliminaryRejected = false;
        if (target is not null)
        {
            var preliminaryMatch = ScoreIncidentEvidenceMatch(calls, target, locationRows, anchorsByCall, ragById);
            if (!preliminaryMatch.Accepted)
            {
                preliminaryRejected = true;
                reasonParts.Add($"ignored exact existing incident_id '{target.IncidentKey}' after verifier because retained calls lacked same-event evidence");
                target = null;
            }
        }

        if (target is null)
        {
            var matchPool = preliminaryRejected && preliminaryIncident is not null
                ? activeIncidents.Where(incident => incident.Id != preliminaryIncident.Id).ToList()
                : activeIncidents;
            var match = FindBestExistingIncidentMatch(calls, matchPool, locationRows, anchorsByCall, ragById);
            target = match.Incident;
        }

        target ??= ownedIncidents
            .Where(incident => !preliminaryRejected || preliminaryIncident is null || incident.Id != preliminaryIncident.Id)
            .Select(incident => new { Incident = incident, Match = ScoreIncidentEvidenceMatch(calls, incident, locationRows, anchorsByCall, ragById) })
            .Where(row => row.Match.Accepted)
            .OrderByDescending(row => row.Match.Score)
            .ThenBy(row => row.Incident.FirstSeen)
            .Select(row => row.Incident)
            .FirstOrDefault();

        if (target is null)
        {
            if (suppliedUnknownIncidentId)
                reasonParts.Add($"ignored unknown model incident_id '{item.IncidentId}'");
            reasonParts.Add("new server-owned incident");
            return new(true, null, [], string.Join("; ", reasonParts));
        }

        var mergeIncidents = ownedIncidents
            .Where(incident => incident.Id != target.Id)
            .Where(incident => AreIncidentsMergeable(target, incident, calls, locationRows, anchorsByCall, ragById))
            .ToList();
        reasonParts.Add("matched existing incident by retained evidence");
        if (preliminaryIncident is not null && preliminaryIncident.Id == target.Id)
            reasonParts[^1] = "accepted exact existing incident_id after retained evidence match";
        if (suppliedUnknownIncidentId)
            reasonParts.Add($"ignored unknown model incident_id '{item.IncidentId}'");
        if (mergeIncidents.Count > 0)
            reasonParts.Add($"will merge sibling incident(s) {string.Join(",", mergeIncidents.Select(i => i.Id))}");
        return new(true, target, mergeIncidents, string.Join("; ", reasonParts));
    }

    private static IncidentEvidenceMatch FindBestExistingIncidentMatch(
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        return activeIncidents
            .Select(incident =>
            {
                var match = ScoreIncidentEvidenceMatch(calls, incident, locationRows, anchorsByCall, ragById);
                return match with { Incident = match.Accepted ? incident : null };
            })
            .Where(match => match.Accepted)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Incident?.FirstSeen ?? long.MaxValue)
            .FirstOrDefault() ?? new(null, false, 0, string.Empty);
    }

    private static IncidentEvidenceMatch ScoreIncidentEvidenceMatch(
        IReadOnlyList<EngineCall> calls,
        IncidentDto incident,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        if (calls.Count == 0 || incident.Calls.Count == 0)
            return new(null, false, 0, string.Empty);

        var incidentCalls = incident.Calls.Select(IncidentCallToEngineCall).ToList();
        var minCallTime = calls.Min(call => call.StartTime);
        var maxCallTime = calls.Max(call => call.StartTime);
        var gap = Math.Min(
            Math.Abs(minCallTime - incident.LastSeen),
            Math.Abs(incident.FirstSeen - maxCallTime));
        if (gap > 75 * 60)
            return new(null, false, 0, "outside matching time range");

        var exactCallIds = calls.Select(call => call.Id).Intersect(incident.Calls.Select(call => call.CallId)).Count();
        var linkedCalls = 0;
        var anchorLinkedCalls = 0;
        foreach (var call in calls)
        {
            var bestLink = incidentCalls
            .Select(existing => ScoreCallEvidenceLink(call, existing, locationRows, anchorsByCall, ragById))
                .OrderByDescending(score => score)
                .FirstOrDefault();
            if (bestLink >= 0.62)
                linkedCalls++;
            if (bestLink >= 0.78)
                anchorLinkedCalls++;
        }

        var score = exactCallIds * 1.0 + linkedCalls * 0.45 + anchorLinkedCalls * 0.35;
        var accepted = exactCallIds > 0
                       || anchorLinkedCalls > 0
                       || (calls.Count >= 2 && linkedCalls >= Math.Min(2, calls.Count) && HasCompatibleIncidentEventText(calls, incidentCalls));
        return new(accepted ? incident : null, accepted, score, accepted ? "shared retained evidence" : "insufficient shared evidence");
    }

    private static bool AreIncidentsMergeable(
        IncidentDto target,
        IncidentDto duplicate,
        IReadOnlyList<EngineCall> currentCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        if (target.Id == duplicate.Id || target.Calls.Count == 0 || duplicate.Calls.Count == 0)
            return false;
        if (Math.Abs(target.FirstSeen - duplicate.LastSeen) > 75 * 60
            && Math.Abs(duplicate.FirstSeen - target.LastSeen) > 75 * 60)
            return false;

        var targetCalls = target.Calls.Select(IncidentCallToEngineCall).ToList();
        var duplicateCalls = duplicate.Calls.Select(IncidentCallToEngineCall).ToList();
        var targetAndCurrentCalls = targetCalls
            .Concat(currentCalls)
            .DistinctBy(call => call.Id)
            .ToList();
        if (HaveConflictingConcreteMergeLocations(targetAndCurrentCalls, duplicateCalls, locationRows, anchorsByCall))
            return false;
        if (HaveConflictingSiblingIncidentLocations(
            target,
            duplicate,
            targetAndCurrentCalls,
            duplicateCalls,
            locationRows,
            anchorsByCall))
            return false;

        if (!HaveCompatibleSpecificEventConcepts(targetCalls, duplicateCalls))
            return false;

        if (AllDuplicateCallsHaveStrongServerLink(targetCalls, duplicateCalls, locationRows, anchorsByCall, ragById))
            return true;

        if (currentCalls.Count > 0
            && HaveCompatibleSpecificEventConcepts(targetAndCurrentCalls, duplicateCalls)
            && HasCompatibleIncidentEventText(targetAndCurrentCalls, duplicateCalls))
            return AllDuplicateCallsHaveStrongServerLink(
                targetAndCurrentCalls,
                duplicateCalls,
                locationRows,
                anchorsByCall,
                ragById);

        return false;
    }

    private static bool HaveConflictingConcreteMergeLocations(
        IReadOnlyList<EngineCall> targetCalls,
        IReadOnlyList<EngineCall> duplicateCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        if (HaveInternalConcreteMergeLocationConflict(targetCalls, locationRows, anchorsByCall)
            || HaveInternalConcreteMergeLocationConflict(duplicateCalls, locationRows, anchorsByCall))
            return true;

        var targetAnchors = targetCalls
            .SelectMany(call => MergeConflictLocationAnchors(call, locationRows, anchorsByCall))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateAnchors = duplicateCalls
            .SelectMany(call => MergeConflictLocationAnchors(call, locationRows, anchorsByCall))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targetAnchors.Count == 0 || duplicateAnchors.Count == 0)
            return false;
        if (AnchorSetsCompatible(targetAnchors, duplicateAnchors))
            return false;

        return !targetCalls.Any(target => duplicateCalls.Any(duplicate => SharePoliceBroadcastIdentity(target, duplicate)));
    }

    private static bool HaveConflictingSiblingIncidentLocations(
        IncidentDto target,
        IncidentDto duplicate,
        IReadOnlyList<EngineCall> targetCalls,
        IReadOnlyList<EngineCall> duplicateCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        var targetAnchors = SiblingIncidentMergeConflictAnchors(target, targetCalls, locationRows, anchorsByCall);
        var duplicateAnchors = SiblingIncidentMergeConflictAnchors(duplicate, duplicateCalls, locationRows, anchorsByCall);
        if (targetAnchors.Count == 0 || duplicateAnchors.Count == 0)
            return false;
        if (AnchorSetsCompatible(targetAnchors, duplicateAnchors))
            return false;

        return !targetCalls.Any(targetCall => duplicateCalls.Any(duplicateCall => SharePoliceBroadcastIdentity(targetCall, duplicateCall)));
    }

    private static HashSet<string> SiblingIncidentMergeConflictAnchors(
        IncidentDto incident,
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        var callAnchors = calls
            .SelectMany(call => MergeConflictLocationAnchors(call, locationRows, anchorsByCall))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var narrativeAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddSiblingIncidentNarrativeConflictAnchors(narrativeAnchors, incident.Title);
        AddSiblingIncidentNarrativeConflictAnchors(narrativeAnchors, incident.Detail);

        if (callAnchors.Count == 0)
            return narrativeAnchors;

        foreach (var anchor in narrativeAnchors)
        {
            if (callAnchors.Any(callAnchor => MembershipAnchorsCompatible(callAnchor, anchor)))
                callAnchors.Add(anchor);
        }

        return callAnchors;
    }

    private static void AddSiblingIncidentNarrativeConflictAnchors(HashSet<string> anchors, string text)
    {
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(0, text))
            AddMergeConflictLocationAnchor(anchors, anchor);
    }

    private static bool HaveInternalConcreteMergeLocationConflict(
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        var anchoredCalls = calls
            .Select(call => new
            {
                Call = call,
                Anchors = MergeConflictLocationAnchors(call, locationRows, anchorsByCall)
            })
            .Where(row => row.Anchors.Count > 0)
            .ToList();

        for (var i = 0; i < anchoredCalls.Count; i++)
        {
            for (var j = i + 1; j < anchoredCalls.Count; j++)
            {
                if (AnchorSetsCompatible(anchoredCalls[i].Anchors, anchoredCalls[j].Anchors)
                    || SharePoliceBroadcastIdentity(anchoredCalls[i].Call, anchoredCalls[j].Call))
                    continue;

                return true;
            }
        }

        return false;
    }

    private static bool HaveCompatibleSpecificEventConcepts(IReadOnlyList<EngineCall> left, IReadOnlyList<EngineCall> right)
    {
        var leftConcepts = SpecificEventConcepts(left);
        var rightConcepts = SpecificEventConcepts(right);
        if (leftConcepts.Count == 0 || rightConcepts.Count == 0)
            return true;

        return leftConcepts.Overlaps(rightConcepts);
    }

    private static HashSet<string> SpecificEventConcepts(IReadOnlyList<EngineCall> calls)
    {
        var text = string.Join(' ', calls.Select(call => call.Transcription ?? string.Empty));
        return new HashSet<string>(
            IncidentEvidenceClassifier.Analyze(text).Concepts,
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool AllDuplicateCallsHaveStrongServerLink(
        IReadOnlyList<EngineCall> seedCalls,
        IReadOnlyList<EngineCall> duplicateCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        if (seedCalls.Count == 0 || duplicateCalls.Count == 0)
            return false;

        var retained = seedCalls.DistinctBy(call => call.Id).ToList();
        var remaining = duplicateCalls.DistinctBy(call => call.Id).ToList();
        var changed = true;
        while (changed && remaining.Count > 0)
        {
            changed = false;
            for (var i = remaining.Count - 1; i >= 0; i--)
            {
                var call = remaining[i];
                var bestLink = retained
                    .Select(existing => ScoreCallEvidenceLink(call, existing, locationRows, anchorsByCall, ragById, allowRagSemanticLink: false))
                    .DefaultIfEmpty(0)
                    .Max();
                if (bestLink < 0.78)
                    continue;

                retained.Add(call);
                remaining.RemoveAt(i);
                changed = true;
            }
        }

        return remaining.Count == 0;
    }

    private static double ScoreCallEvidenceLink(
        EngineCall left,
        EngineCall right,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById,
        bool allowRagSemanticLink = true)
    {
        var minutes = Math.Abs(left.StartTime - right.StartTime) / 60d;
        if (minutes > 75)
            return 0;

        var score = 0d;
        if (HasStructuredIncidentLink(left, right, locationRows, anchorsByCall, ragById, allowRagSemanticLink))
            score = Math.Max(score, 0.82);

        var leftAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(left.Transcription);
        var rightAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(right.Transcription);
        var sharedAnchors = IncidentCandidateValidator.SharedConcreteAnchorCount(leftAnchors, rightAnchors);
        if (sharedAnchors > 0)
            score = Math.Max(score, 0.86);

        if (ShareHouseNumberEventAndContext(left, right))
            score = Math.Max(score, 0.78);

        if (SharePoliceBroadcastIdentity(left, right))
            score = Math.Max(score, 0.78);

        if (HasSameEventContinuationTextLink(left, right))
            score = Math.Max(score, 0.78);

        if (HasSameTalkgroupDispatchDetailLink(left, right))
            score = Math.Max(score, 0.78);

        if (HasSameTalkgroupTrafficCrashDetailLink(left, right))
            score = Math.Max(score, 0.78);

        if (HasCompatibleIncidentEventText([left], [right]) && minutes <= 12 && SameOperationalContext(left, right))
            score = Math.Max(score, 0.64);

        return score;
    }

    private static bool ShareHouseNumberEventAndContext(EngineCall left, EngineCall right)
    {
        var leftNumbers = SignificantLocationNumbers(left.Transcription);
        var rightNumbers = SignificantLocationNumbers(right.Transcription);
        return leftNumbers.Count > 0
               && rightNumbers.Count > 0
               && leftNumbers.Overlaps(rightNumbers)
               && HasCompatibleIncidentEventText([left], [right])
               && (SameOperationalContext(left, right) || SameCrossAgencyEmergencyAddressContext(left, right))
               && Math.Abs(left.StartTime - right.StartTime) <= 15 * 60;
    }

    private static bool SameCrossAgencyEmergencyAddressContext(EngineCall left, EngineCall right)
    {
        if (!string.Equals(left.SystemShortName, right.SystemShortName, StringComparison.OrdinalIgnoreCase))
            return false;

        var evidenceClass = DominantEvidenceClass([left]);
        if (string.IsNullOrWhiteSpace(evidenceClass)
            || !string.Equals(evidenceClass, DominantEvidenceClass([right]), StringComparison.OrdinalIgnoreCase))
            return false;

        if (evidenceClass is "police" or "public_safety")
            return false;

        return IncidentCandidateValidator.HasStrongIncidentSignal(left.Transcription)
               && IncidentCandidateValidator.HasStrongIncidentSignal(right.Transcription);
    }

    private static bool SharePoliceBroadcastIdentity(EngineCall left, EngineCall right)
    {
        if (!string.Equals(left.SystemShortName, right.SystemShortName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Math.Abs(left.StartTime - right.StartTime) > 20 * 60)
            return false;
        if (!string.Equals(DominantEvidenceClass([left]), "police", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(DominantEvidenceClass([right]), "police", StringComparison.OrdinalIgnoreCase))
            return false;

        var leftText = $"{left.TalkgroupName} {left.Transcription}";
        var rightText = $"{right.TalkgroupName} {right.Transcription}";
        if (!IsBroadcastIdentityContext(leftText) || !IsBroadcastIdentityContext(rightText))
            return false;

        var leftTokens = BroadcastIdentityTokens(leftText);
        var rightTokens = BroadcastIdentityTokens(rightText);
        if (leftTokens.Count < 2 || rightTokens.Count < 2)
            return false;

        var sharedTokens = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).ToList();
        var shared = sharedTokens.Count;
        var ratio = shared / (double)Math.Min(leftTokens.Count, rightTokens.Count);
        if (shared >= 2 && ratio >= 0.35)
            return true;

        return shared >= 3 && sharedTokens.Count(IsStrongBroadcastIdentityToken) >= 2;
    }

    private static bool HasSameEventContinuationTextLink(EngineCall left, EngineCall right)
    {
        if (!string.Equals(left.SystemShortName, right.SystemShortName, StringComparison.OrdinalIgnoreCase))
            return false;
        var minutes = Math.Abs(left.StartTime - right.StartTime) / 60d;
        if (minutes > 20)
            return false;

        var leftText = $"{left.TalkgroupName} {left.Transcription}";
        var rightText = $"{right.TalkgroupName} {right.Transcription}";
        if (!IncidentCandidateValidator.HasStrongIncidentSignal($"{leftText} {rightText}"))
            return false;

        var sharedTokens = IncidentContinuationTokens(leftText)
            .Intersect(IncidentContinuationTokens(rightText), StringComparer.OrdinalIgnoreCase)
            .Count();
        if (sharedTokens == 0)
            return false;

        if (SameOperationalContext(left, right)
            && sharedTokens >= 1
            && HasCompatibleIncidentEventText([left], [right]))
            return true;

        if (sharedTokens >= 2
            && HasExplicitSameEventReference(leftText, rightText)
            && HasCompatibleIncidentEventText([left], [right]))
            return true;

        return sharedTokens >= 4
               && HasCompatibleIncidentEventText([left], [right]);
    }

    private static bool HasSameTalkgroupDispatchDetailLink(EngineCall left, EngineCall right)
    {
        if (!SameOperationalContext(left, right))
            return false;
        if (Math.Abs(left.StartTime - right.StartTime) > 6 * 60)
            return false;

        var leftPrimaryStrong = HasPrimaryDispatchIncidentSignal(left.Transcription);
        var rightPrimaryStrong = HasPrimaryDispatchIncidentSignal(right.Transcription);
        if (leftPrimaryStrong != rightPrimaryStrong)
        {
            var patientDetailCall = leftPrimaryStrong ? right : left;
            var patientEventCall = leftPrimaryStrong ? left : right;
            if (!IsTransportOrHospitalOnlyText(patientDetailCall.Transcription)
                && HasImmediateSameTalkgroupPatientDetail(patientEventCall, patientDetailCall))
                return true;
        }

        var leftStrong = IncidentCandidateValidator.HasStrongIncidentSignal(left.Transcription);
        var rightStrong = IncidentCandidateValidator.HasStrongIncidentSignal(right.Transcription);
        if (leftStrong == rightStrong)
            return false;

        var detailCall = leftStrong ? right : left;
        var eventCall = leftStrong ? left : right;
        if (IsTransportOrHospitalOnlyText(detailCall.Transcription))
            return false;

        var leftText = $"{left.TalkgroupName} {left.Transcription}";
        var rightText = $"{right.TalkgroupName} {right.Transcription}";
        var detailText = $"{detailCall.TalkgroupName} {detailCall.Transcription}";

        if (!HasDispatchLocationDetailText(detailText) && !HasExplicitSameEventReference(leftText, rightText))
            return false;

        var detailAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(detailCall.Transcription);
        var detailHasConcreteLocation = detailAnchors.Count > 0 || HasDispatchLocationNumberAndRoad(detailText);
        if (!detailHasConcreteLocation)
            return false;

        var eventAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(eventCall.Transcription);
        return detailAnchors.Count == 0
               || eventAnchors.Count == 0
               || IncidentCandidateValidator.SharedConcreteAnchorCount(eventAnchors, detailAnchors) > 0;
    }

    private static bool HasSameTalkgroupTrafficCrashDetailLink(EngineCall left, EngineCall right)
    {
        if (!SameOperationalContext(left, right))
            return false;
        if (Math.Abs(left.StartTime - right.StartTime) > 8 * 60)
            return false;

        var leftPrimaryCrash = HasPrimaryTrafficCrashSceneText(left.Transcription);
        var rightPrimaryCrash = HasPrimaryTrafficCrashSceneText(right.Transcription);
        if (leftPrimaryCrash == rightPrimaryCrash)
            return false;

        var eventCall = leftPrimaryCrash ? left : right;
        var detailCall = leftPrimaryCrash ? right : left;
        if (IncidentCandidateValidator.IsRoutineStatusText(detailCall.Transcription)
            || IsTransportOrHospitalOnlyText(detailCall.Transcription)
            || !HasTrafficCrashVehicleOrPatientDetailText(detailCall.Transcription))
            return false;

        var eventText = $"{eventCall.TalkgroupName} {eventCall.Transcription}";
        var detailText = $"{detailCall.TalkgroupName} {detailCall.Transcription}";
        var eventAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(eventCall.Transcription);
        if (eventAnchors.Count == 0 && !HasDispatchLocationNumberAndRoad(eventText))
            return false;

        var detailAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(detailCall.Transcription);
        if (detailAnchors.Count > 0
            && eventAnchors.Count > 0
            && IncidentCandidateValidator.SharedConcreteAnchorCount(eventAnchors, detailAnchors) == 0)
            return false;

        return HasSharedTrafficCrashIdentityToken(eventText, detailText);
    }

    private static bool HasPrimaryTrafficCrashSceneText(string text) =>
        Regex.IsMatch(
            text ?? string.Empty,
            @"\b(?:crash|accident|mvc|mva|collision|wreck|rear[- ]?end(?:ed|ing)?|rollover|rolled over|10[- ]?45)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || (Regex.IsMatch(text ?? string.Empty, @"\b[a-z0-9]{3,}\s+(?:versus|vs\.?|v)\s+[a-z0-9]{3,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            && Regex.IsMatch(text ?? string.Empty, @"\b(?:vehicle|car|truck|shoulder|interstate|i[- ]?\d+|highway|hwy|mile|marker|ramp)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    private static bool HasTrafficCrashVehicleOrPatientDetailText(string text) =>
        Regex.IsMatch(
            text ?? string.Empty,
            @"\b(?:air\s*bags?|impact(?:ed)?\s+by\s+(?:the\s+)?air\s*bag|complain(?:ing|s)?|pain|injur(?:y|ies|ed)|ambulance|ems|medic|\d{1,3}\s*(?:year|yr)[- ]?old|male|female)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && Regex.IsMatch(
            text ?? string.Empty,
            @"\b(?:vehicle|car|truck|sedan|suv|pickup|van|tag|plate|kia|altima|ultima|tesla|honda|ford|chevy|toyota|nissan|lexus)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasSharedTrafficCrashIdentityToken(string leftText, string rightText)
    {
        var left = TrafficCrashIdentityTokens(leftText);
        var right = TrafficCrashIdentityTokens(rightText);
        return left.Intersect(right, StringComparer.OrdinalIgnoreCase).Any(IsStrongTrafficCrashIdentityToken);
    }

    private static HashSet<string> TrafficCrashIdentityTokens(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "accident", "advised", "again", "airbag", "another", "back", "caller", "complaining",
            "correct", "county", "dispatch", "female", "highway", "impact", "impacted", "interstate",
            "involved", "left", "marker", "medical", "mile", "north", "old", "pain", "partially",
            "patient", "right", "shoulder", "south", "state", "that", "this", "unit", "vehicle",
            "versus", "white", "with", "year"
        };
        return Regex.Matches((text ?? string.Empty).ToLowerInvariant(), @"[a-z0-9]{3,}")
            .Select(match => NormalizeTrafficCrashIdentityToken(match.Value))
            .Where(token => token.Length >= 3 && !stop.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeTrafficCrashIdentityToken(string token) =>
        token switch
        {
            "ultima" => "altima",
            "blucidan" => "sedan",
            _ => token
        };

    private static bool IsStrongTrafficCrashIdentityToken(string token) =>
        token.Any(char.IsDigit) || token.Length >= 5;

    private static bool HasPrimaryDispatchIncidentSignal(string text)
    {
        var scrubbed = Regex.Replace(
            text ?? string.Empty,
            @"\b(?:fire|ems|medical|police)\s+response\b",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return IncidentCandidateValidator.HasStrongIncidentSignal(scrubbed);
    }

    private static bool HasImmediateSameTalkgroupPatientDetail(EngineCall eventCall, EngineCall detailCall)
    {
        if (Math.Abs(eventCall.StartTime - detailCall.StartTime) > 90)
            return false;
        if (IncidentCandidateValidator.IsRoutineStatusText(detailCall.Transcription))
            return false;

        var eventText = $"{eventCall.TalkgroupName} {eventCall.Transcription}";
        var detailText = $"{detailCall.TalkgroupName} {detailCall.Transcription}";
        var eventAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(eventCall.Transcription);
        if (eventAnchors.Count == 0 && !HasDispatchLocationNumberAndRoad(eventText))
            return false;

        var detailAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(detailCall.Transcription);
        if (detailAnchors.Count > 0 || HasDispatchLocationNumberAndRoad(detailText))
            return false;

        return HasDispatchPatientOrResponseDetailText(detailText);
    }

    private static bool HasDispatchLocationDetailText(string text) =>
        Regex.IsMatch(
            text ?? string.Empty,
            @"\b(?:gave|gives|given|giving|provided|confirmed?|advised|advising|said|says)\s+(?:the\s+)?(?:address|location)\b|\b(?:address|location)\s+(?:is|was|will be|should be|comes back)\b|\bclose to\b|\bdriveway\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasDispatchPatientOrResponseDetailText(string text) =>
        Regex.IsMatch(
            text ?? string.Empty,
            @"\b(?:\d{1,3}\s*(?:year|yr)[- ]?old|patient|male|female|complain(?:ing|s)?|numbness|weakness|pain|facial|face|back|sick party|stroke|seizure|cardiac|unconscious|unresponsive|not\s+(?:completely\s+)?responsive|medic|engine|ladder|squad|rescue|fire response|ems response|responding)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool HasDispatchLocationNumberAndRoad(string text) =>
        SignificantLocationNumbers(text).Count > 0
        && Regex.IsMatch(
            text ?? string.Empty,
            @"\b(?:road|rd|street|st|drive|dr|avenue|ave|lane|ln|pike|highway|hwy|place|pl|circle|cir|court|ct|way|trail|trl|parkway|pkwy|boulevard|blvd|terrace|ter)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static HashSet<string> IncidentContinuationTokens(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "advising", "again", "area", "caller", "calling", "channel", "channels", "copy",
            "dispatch", "door", "further", "house", "party", "parties", "possibly", "reference",
            "route", "routes", "same", "second", "show", "unit", "units",
            "avenue", "bank", "county", "drive", "ems", "fire", "lane", "medical", "road",
            "station", "street"
        };
        return Regex.Matches((text ?? string.Empty).ToLowerInvariant(), @"[a-z0-9]{3,}")
            .Select(match => match.Value)
            .Where(token => token.Length >= 3 && !stop.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasExplicitSameEventReference(string leftText, string rightText)
    {
        const string pattern = @"\b(?:same|second|another|additional|updated?|reference to|in reference to|caller|rp|reporting party|that's correct|thats correct)\b";
        return Regex.IsMatch(leftText ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
               || Regex.IsMatch(rightText ?? string.Empty, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsBroadcastIdentityContext(string text) =>
        Regex.IsMatch(text ?? string.Empty, @"\b(?:bolo|be on (?:the )?lookout|look ?out|attempt to locate|atl|missing (?:person|party|male|female|juvenile)|missing since|entered\s+(?:as\s+)?missing|reported missing|(?:she|he|they|subject|party)(?:'s| is| are)?\s+missing|welfare check|well[- ]?being check|check well[- ]?being|stop hold|stop,?\s*hold)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static HashSet<string> BroadcastIdentityTokens(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "active", "all", "any", "around", "being", "bolo", "cars", "contact", "copy",
            "correct", "county", "days", "dispatch", "driving", "entered", "georgia", "go",
            "going", "hamilton", "headed", "hold", "known", "last", "lookout", "missing",
            "negative", "notify", "party", "person", "phone", "please", "report", "reported",
            "seen", "state", "stop", "tag", "tennessee", "today", "towards", "unit", "units",
            "valid", "walker", "well", "welfare", "yesterday"
        };
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches((text ?? string.Empty).ToLowerInvariant(), @"[a-z0-9]{3,}"))
        {
            var token = NormalizeBroadcastIdentityToken(match.Value);
            if (token.Length < 3 || stop.Contains(token))
                continue;
            tokens.Add(token);
            if (TryPhoneticLetter(token, out var letter))
                tokens.Add($"letter:{letter}");
        }

        return tokens;
    }

    private static bool IsStrongBroadcastIdentityToken(string token) =>
        token.StartsWith("letter:", StringComparison.OrdinalIgnoreCase)
        || token.Any(char.IsDigit)
        || token.Length >= 5;

    private static string NormalizeBroadcastIdentityToken(string token) =>
        token switch
        {
            "lexi" or "lexie" or "lexus" => "lexus",
            "hernandes" or "hernandez" => "hernandez",
            _ => token
        };

    private static bool TryPhoneticLetter(string token, out char letter)
    {
        letter = token switch
        {
            "adam" => 'a',
            "boy" or "bravo" => 'b',
            "charles" or "charlie" => 'c',
            "david" or "delta" => 'd',
            "edward" or "echo" => 'e',
            "frank" or "foxtrot" => 'f',
            "george" or "golf" => 'g',
            "henry" or "hotel" => 'h',
            "ida" or "india" => 'i',
            "john" or "juliet" => 'j',
            "king" or "kilo" => 'k',
            "lincoln" or "lima" => 'l',
            "mary" or "mike" => 'm',
            "nora" or "november" => 'n',
            "ocean" or "oscar" => 'o',
            "paul" or "papa" => 'p',
            "queen" or "quebec" => 'q',
            "robert" or "romeo" => 'r',
            "sam" or "sierra" => 's',
            "tom" or "tango" => 't',
            "union" or "uniform" => 'u',
            "victor" => 'v',
            "william" or "whiskey" => 'w',
            "xray" => 'x',
            "young" or "yankee" => 'y',
            "zebra" or "zulu" => 'z',
            _ => '\0'
        };
        return letter != '\0';
    }

    private static HashSet<string> SignificantLocationNumbers(string? text)
    {
        var numbers = Regex.Matches(text ?? string.Empty, @"\b\d{2,5}\b", RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Where(value => value.Length >= 3 || int.TryParse(value, CultureInfo.InvariantCulture, out var parsed) && parsed >= 80)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var spoken in SpokenStreetAddressNumbers(text))
            numbers.Add(spoken);
        return numbers;
    }

    private static IEnumerable<string> SpokenStreetAddressNumbers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var tokens = Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(match => match.Value)
            .ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!IsNumberWord(tokens[i]))
                continue;

            var numberWords = new List<string>();
            var j = i;
            while (j < tokens.Count && IsNumberWord(tokens[j]) && numberWords.Count < 6)
            {
                numberWords.Add(tokens[j]);
                j++;
            }

            if (numberWords.Count < 2)
                continue;

            var suffixSeen = tokens
                .Skip(j)
                .Take(5)
                .Any(IsStreetSuffix);
            if (!suffixSeen)
                continue;

            var parsed = ParseSpokenAddressNumber(numberWords);
            if (parsed.Length >= 2 && parsed.Length <= 5)
                yield return parsed;
        }
    }

    private static string ParseSpokenAddressNumber(IReadOnlyList<string> words)
    {
        var digits = new List<string>();
        for (var i = 0; i < words.Count; i++)
        {
            var current = words[i];
            if (TrySingleDigitWord(current, out var digit))
            {
                digits.Add(digit.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            if (TryTensWord(current, out var tens))
            {
                if (i + 1 < words.Count && TrySingleDigitWord(words[i + 1], out var ones))
                {
                    digits.Add((tens + ones).ToString(CultureInfo.InvariantCulture));
                    i++;
                }
                else
                {
                    digits.Add(tens.ToString(CultureInfo.InvariantCulture));
                }
                continue;
            }

            if (TryTeenWord(current, out var teen))
                digits.Add(teen.ToString(CultureInfo.InvariantCulture));
        }

        return string.Concat(digits);
    }

    private static bool IsNumberWord(string value) =>
        TrySingleDigitWord(value, out _)
        || TryTensWord(value, out _)
        || TryTeenWord(value, out _);

    private static bool TrySingleDigitWord(string value, out int digit)
    {
        digit = value switch
        {
            "zero" or "oh" or "o" => 0,
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            _ => -1
        };
        return digit >= 0;
    }

    private static bool TryTensWord(string value, out int tens)
    {
        tens = value switch
        {
            "twenty" => 20,
            "thirty" => 30,
            "forty" => 40,
            "fifty" => 50,
            "sixty" => 60,
            "seventy" => 70,
            "eighty" => 80,
            "ninety" => 90,
            _ => -1
        };
        return tens >= 0;
    }

    private static bool TryTeenWord(string value, out int teen)
    {
        teen = value switch
        {
            "ten" => 10,
            "eleven" => 11,
            "twelve" => 12,
            "thirteen" => 13,
            "fourteen" => 14,
            "fifteen" => 15,
            "sixteen" => 16,
            "seventeen" => 17,
            "eighteen" => 18,
            "nineteen" => 19,
            _ => -1
        };
        return teen >= 0;
    }

    private static bool IsStreetSuffix(string token) => token is
        "road" or "rd" or "street" or "st" or "drive" or "dr" or "avenue" or "ave" or "lane" or "ln"
        or "pike" or "highway" or "hwy" or "place" or "pl" or "circle" or "cir" or "court" or "ct"
        or "way" or "trail" or "trl" or "parkway" or "pkwy" or "boulevard" or "blvd" or "terrace" or "ter";

    private static bool SameOperationalContext(EngineCall left, EngineCall right) =>
        string.Equals(left.SystemShortName, right.SystemShortName, StringComparison.OrdinalIgnoreCase)
        && (left.Talkgroup == right.Talkgroup
            || string.Equals(left.TalkgroupName, right.TalkgroupName, StringComparison.OrdinalIgnoreCase));

    private static bool HasCompatibleIncidentEventText(IReadOnlyList<EngineCall> left, IReadOnlyList<EngineCall> right)
    {
        var leftClass = DominantEvidenceClass(left);
        var rightClass = DominantEvidenceClass(right);
        return leftClass.Length > 0
               && rightClass.Length > 0
               && string.Equals(leftClass, rightClass, StringComparison.OrdinalIgnoreCase);
    }

    private static string DominantEvidenceClass(IReadOnlyList<EngineCall> calls)
    {
        var text = string.Join(' ', calls.Select(call => call.Transcription));
        var profile = IncidentEvidenceClassifier.Analyze(text);
        if (!string.IsNullOrWhiteSpace(profile.EvidenceClass))
            return profile.EvidenceClass;
        return IncidentCandidateValidator.HasStrongIncidentSignal(text) ? "public_safety" : string.Empty;
    }

    private static EvidenceVerificationSelection AddExistingTargetEvidence(
        EvidenceVerificationSelection verification,
        IncidentDto? target,
        IReadOnlyList<IncidentDto> mergeIncidents)
    {
        if (target is null && mergeIncidents.Count == 0)
            return verification;

        var calls = verification.Calls
            .Concat(target?.Calls.Select(IncidentCallToEngineCall) ?? [])
            .Concat(mergeIncidents.SelectMany(incident => incident.Calls.Select(IncidentCallToEngineCall)))
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ToList();
        return verification with { Calls = calls };
    }

    private static EngineCall IncidentCallToEngineCall(IncidentCallDto call) => new()
    {
        Id = call.CallId,
        StartTime = call.RawTimestamp,
        StopTime = call.RawTimestamp,
        SystemShortName = call.SystemShortName,
        TalkgroupName = call.TalkgroupName,
        Category = call.Category,
        Transcription = call.Transcript,
        TranscriptionStatus = "complete",
        QualityReason = "ok"
    };

    private async Task<OwnedCallOverlapResult> AssembleIncidentMembershipAsync(
        IncidentStateItem item,
        IncidentDto? existingIncident,
        IReadOnlyList<IncidentDto> mergeIncidents,
        EvidenceVerificationSelection verification,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById,
        CancellationToken ct)
    {
        var calls = verification.Calls
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.StartTime)
            .ToList();
        if (calls.Count == 0)
            return new(false, "rejected:assembler received no verifier-retained calls", []);

        var owners = await _database.GetIncidentCallOwnersAsync(calls.Select(c => c.Id).ToList(), ct);
        var existingCallIds = existingIncident?.Calls.Select(c => c.CallId).ToHashSet() ?? [];
        var mergeIncidentIds = mergeIncidents.Select(i => i.Id).ToHashSet();
        var mergeCallIds = mergeIncidents.SelectMany(i => i.Calls.Select(c => c.CallId)).ToHashSet();
        var input = ToIncidentEventValidationInput(item);
        var included = new List<EngineCall>();
        var pendingNewCalls = new List<EngineCall>();
        var excludedOwned = new List<long>();
        var excludedRejected = new List<long>();
        var excludedWeak = new List<long>();
        var isExistingIncidentUpdate = existingCallIds.Count > 0 || mergeCallIds.Count > 0;

        foreach (var call in calls)
        {
            var decision = verification.Decisions.TryGetValue(call.Id, out var value) ? value : null;
            if (owners.TryGetValue(call.Id, out var owner)
                && (existingIncident is null || owner.IncidentId != existingIncident.Id)
                && !mergeIncidentIds.Contains(owner.IncidentId))
            {
                excludedOwned.Add(call.Id);
                continue;
            }

            var isExistingCall = existingCallIds.Contains(call.Id) || mergeCallIds.Contains(call.Id);
            if (isExistingCall && !IsHardRejectedVerifierDecision(decision))
            {
                included.Add(call);
                continue;
            }

            if (isExistingCall
                && IsHardRejectedVerifierDecision(decision)
                && ExistingCallSupportsProtectedNarrativeAnchor(existingIncident, call, locationRows, anchorsByCall))
            {
                included.Add(call);
                continue;
            }

            if (IsHardRejectedVerifierDecision(decision))
            {
                excludedRejected.Add(call.Id);
                continue;
            }

            if (isExistingIncidentUpdate)
            {
                pendingNewCalls.Add(call);
                continue;
            }

            if (IsVerifierIncludedMembership(decision))
            {
                included.Add(call);
                continue;
            }

            if (decision is null && IsFallbackMembershipCandidate(input, call, calls, locationRows, anchorsByCall, ragById))
            {
                included.Add(call);
                continue;
            }

            excludedWeak.Add(call.Id);
        }

        if (isExistingIncidentUpdate && pendingNewCalls.Count > 0)
        {
            var remaining = pendingNewCalls
                .DistinctBy(call => call.Id)
                .OrderBy(call => call.StartTime)
                .ToList();
            var attachedAny = true;
            while (attachedAny && remaining.Count > 0)
            {
                attachedAny = false;
                foreach (var call in remaining.ToList())
                {
                    var decision = verification.Decisions.TryGetValue(call.Id, out var value) ? value : null;
                    if (!CanAttachNewCallToAcceptedIncidentMembership(input, call, included, decision, locationRows, anchorsByCall, ragById))
                        continue;

                    included.Add(call);
                    remaining.Remove(call);
                    attachedAny = true;
                }
            }

            excludedWeak.AddRange(remaining.Select(call => call.Id));
        }

        included = included.DistinctBy(c => c.Id).OrderBy(c => c.StartTime).ToList();
        if (included.Count == 0)
        {
            var recovered = TryRecoverStandaloneEventMembership(
                input,
                calls,
                excludedOwned,
                excludedRejected,
                anchorsByCall,
                forceNewIncident: isExistingIncidentUpdate);
            if (recovered is not null)
                return recovered;

            if (excludedOwned.Count > 0)
                return new(false, $"rejected:all assembler-retained calls are already owned by other incidents ({string.Join(",", excludedOwned)})", calls);
            return new(false, $"rejected:assembler retained no event-member calls; verifier_rejected=[{string.Join(",", excludedRejected)}]; weak=[{string.Join(",", excludedWeak)}]", calls);
        }

        if (!HasAssemblerEventEvidence(input, included, verification.Decisions))
            return new(false, "rejected:assembler retained calls lack actionable event evidence", included);

        if (RetainedCallsAreOnlyLogistics(included, verification.Decisions.ToDictionary(kvp => CallToken(kvp.Key), kvp => kvp.Value, StringComparer.OrdinalIgnoreCase))
            && !input.ParentEventEvidence.Any(value => !string.IsNullOrWhiteSpace(value)))
            return new(false, "rejected:assembler retained only transport/logistics/status calls without parent event evidence", included);

        var reasonParts = new List<string> { "server-owned event membership" };
        if (excludedOwned.Count > 0)
            reasonParts.Add($"excluded already-owned calls {string.Join(",", excludedOwned)}");
        if (excludedRejected.Count > 0)
            reasonParts.Add($"excluded verifier-rejected calls {string.Join(",", excludedRejected)}");
        if (excludedWeak.Count > 0)
            reasonParts.Add($"excluded weak/unrelated calls {string.Join(",", excludedWeak)}");
        return new(true, string.Join("; ", reasonParts), included);
    }

    private OwnedCallOverlapResult? TryRecoverStandaloneEventMembership(
        IncidentEventValidationInput input,
        IReadOnlyList<EngineCall> calls,
        IReadOnlyCollection<long> excludedOwned,
        IReadOnlyCollection<long> excludedRejected,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        bool forceNewIncident)
    {
        var excludedOwnedIds = excludedOwned.ToHashSet();
        var excludedRejectedIds = excludedRejected.ToHashSet();
        var candidates = calls
            .Where(call => !excludedOwnedIds.Contains(call.Id) && !excludedRejectedIds.Contains(call.Id))
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ToList();
        if (candidates.Count == 0)
            return null;

        var validation = IncidentCandidateValidator.ValidateEvent(
            input,
            candidates.Select(call => ToIncidentCandidateCall(call, anchorsByCall)).ToList());
        if (!validation.IsValid || validation.Calls.Count == 0)
            return null;

        var retainedIds = validation.Calls.Select(call => call.CallId).ToHashSet();
        var retained = candidates
            .Where(call => retainedIds.Contains(call.Id))
            .OrderBy(call => call.StartTime)
            .ToList();
        if (retained.Count == 0)
            return null;

        return new(true, $"server-owned recovered standalone event membership after mixed candidate pruning; validator:{validation.Reason}", retained, forceNewIncident);
    }

    private OwnedCallOverlapResult FinalizeServerOwnedMembership(
        IncidentStateItem item,
        IncidentDto? existingIncident,
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        var distinctCalls = calls
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ToList();
        if (distinctCalls.Count == 0)
            return new(false, "rejected:final membership received no calls", []);

        var pruned = PruneConcreteMembershipConflicts(item, existingIncident, distinctCalls, locationRows, anchorsByCall, ragById)
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ToList();
        if (pruned.Count == 0)
            return new(false, "rejected:final membership pruned every call", distinctCalls);

        if (HasExistingIncidentConcreteAnchorReplacementConflict(existingIncident, pruned, locationRows, anchorsByCall))
            return new(false, "rejected:final membership would add or replace existing concrete incident anchor with conflicting location evidence", pruned);

        var eventValidation = IncidentCandidateValidator.ValidateEvent(
            ToIncidentEventValidationInput(item),
            pruned.Select(call => ToIncidentCandidateCall(call, anchorsByCall)).ToList());
        if (eventValidation.IsValid)
        {
            var retained = RetainValidatorMatchedMembership(
                pruned,
                eventValidation.Calls,
                locationRows,
                anchorsByCall,
                ragById);
            retained = RetainExistingNarrativeSupportedCalls(
                existingIncident,
                pruned,
                retained,
                locationRows,
                anchorsByCall);
            return new(true, FinalMembershipReason("server-owned validated event membership", distinctCalls.Count, pruned.Count, retained.Count, eventValidation.Calls.Count), retained);
        }

        if (existingIncident is not null)
        {
            var existingNarrativeValidation = IncidentCandidateValidator.Validate(
                existingIncident.Title,
                existingIncident.Detail,
                pruned.Select(call => ToIncidentCandidateCall(call, anchorsByCall)).ToList());
            if (existingNarrativeValidation.IsValid)
            {
                var retained = RetainValidatorMatchedMembership(
                    pruned,
                    existingNarrativeValidation.Calls,
                    locationRows,
                    anchorsByCall,
                    ragById);
                retained = RetainExistingNarrativeSupportedCalls(
                    existingIncident,
                    pruned,
                    retained,
                    locationRows,
                    anchorsByCall);
                return new(true, FinalMembershipReason("server-owned validated existing narrative membership", distinctCalls.Count, pruned.Count, retained.Count, existingNarrativeValidation.Calls.Count), retained);
            }
        }

        return new(false, $"rejected:final membership failed validation: {eventValidation.Reason}", pruned);
    }

    private static string FinalMembershipReason(string reason, int inputCount, int conflictPrunedCount, int retainedCount, int validationCount)
    {
        var parts = new List<string> { reason };
        if (inputCount != conflictPrunedCount)
            parts.Add($"conflict pruner removed {inputCount - conflictPrunedCount} call(s)");
        if (conflictPrunedCount != retainedCount)
            parts.Add($"validator subset pruned {conflictPrunedCount - retainedCount} unlinked call(s)");
        if (validationCount != retainedCount)
            parts.Add($"validator matched {validationCount}/{retainedCount} call(s) but did not rewrite membership");
        return string.Join("; ", parts);
    }

    private static List<EngineCall> RetainValidatorMatchedMembership(
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<IncidentCandidateCall> validatorCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        var validatorIds = validatorCalls.Select(call => call.CallId).ToHashSet();
        if (validatorIds.Count == 0)
            return calls.OrderBy(call => call.StartTime).ToList();

        var matched = calls
            .Where(call => validatorIds.Contains(call.Id))
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ToList();
        if (matched.Count == 0 || matched.Count == calls.Select(call => call.Id).Distinct().Count())
            return calls.OrderBy(call => call.StartTime).ToList();

        var retained = matched.ToList();
        foreach (var call in calls.Where(call => !validatorIds.Contains(call.Id)))
        {
            var bestServerLink = matched
                .Select(accepted => ScoreCallEvidenceLink(call, accepted, locationRows, anchorsByCall, ragById, allowRagSemanticLink: false))
                .DefaultIfEmpty(0)
                .Max();
            if (bestServerLink >= 0.78)
                retained.Add(call);
        }

        return retained
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ToList();
    }

    private static List<EngineCall> RetainExistingNarrativeSupportedCalls(
        IncidentDto? existingIncident,
        IReadOnlyList<EngineCall> candidates,
        IReadOnlyList<EngineCall> retained,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        if (existingIncident is null || existingIncident.Calls.Count == 0 || candidates.Count == 0)
            return retained.OrderBy(call => call.StartTime).ToList();

        var retainedIds = retained.Select(call => call.Id).ToHashSet();
        var existingIds = existingIncident.Calls.Select(call => call.CallId).ToHashSet();
        var result = retained.ToList();
        foreach (var call in candidates)
        {
            if (retainedIds.Contains(call.Id) || !existingIds.Contains(call.Id))
                continue;
            if (!ExistingCallSupportsProtectedNarrativeAnchor(existingIncident, call, locationRows, anchorsByCall))
                continue;

            result.Add(call);
            retainedIds.Add(call.Id);
        }

        return result
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ToList();
    }

    private static bool ExistingCallSupportsProtectedNarrativeAnchor(
        IncidentDto? existingIncident,
        EngineCall call,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        if (existingIncident is null)
            return false;

        var narrativeAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNarrativeConcreteTextAnchors(narrativeAnchors, $"{existingIncident.Title} {existingIncident.Detail}");
        var narrativeText = $"{existingIncident.Title} {existingIncident.Detail}";
        if (narrativeAnchors.Count == 0 && DistinctiveStreetNameTokens(narrativeText).Count == 0)
            return false;

        var callAnchors = ExistingIncidentConflictAnchors(call, locationRows, anchorsByCall);
        if (!AnchorSetsCompatible(callAnchors, narrativeAnchors)
            && !HasDistinctiveNarrativeLocationTokenOverlap(narrativeAnchors, callAnchors, call.Transcription)
            && !DistinctiveStreetNameTokens(narrativeText).Overlaps(DistinctiveStreetNameTokens(call.Transcription)))
            return false;

        var narrativeCall = new EngineCall
        {
            Id = 0,
            StartTime = call.StartTime,
            StopTime = call.StopTime,
            SystemShortName = call.SystemShortName,
            Talkgroup = call.Talkgroup,
            TalkgroupName = call.TalkgroupName,
            Category = string.IsNullOrWhiteSpace(existingIncident.Category) ? call.Category : existingIncident.Category,
            Transcription = $"{existingIncident.Title}. {existingIncident.Detail}",
            TranscriptionStatus = "complete",
            QualityReason = "ok"
        };

        return HasCompatibleIncidentEventText([call], [narrativeCall])
               || (IncidentCandidateValidator.HasStrongIncidentSignal(call.Transcription)
                   && IncidentCandidateValidator.HasStrongIncidentSignal(narrativeCall.Transcription));
    }

    private static bool HasDistinctiveNarrativeLocationTokenOverlap(
        IReadOnlySet<string> narrativeAnchors,
        IReadOnlySet<string> callAnchors,
        string callText)
    {
        var narrativeTokens = DistinctiveLocationTokens(narrativeAnchors.Select(anchor => ParseMembershipAnchor(anchor).Value));
        if (narrativeTokens.Count == 0)
            return false;

        var callTokens = callAnchors.Count > 0
            ? DistinctiveLocationTokens(callAnchors.Select(anchor => ParseMembershipAnchor(anchor).Value))
            : DistinctiveLocationTokens([callText]);
        return callTokens.Count > 0 && narrativeTokens.Overlaps(callTokens);
    }

    private static HashSet<string> DistinctiveLocationTokens(IEnumerable<string> values)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var weakDirections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "north", "south", "east", "west", "n", "s", "e", "w"
        };
        foreach (var value in values)
        {
            foreach (var token in MembershipAnchorTokens(value))
            {
                if (weakDirections.Contains(token))
                    continue;
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static HashSet<string> DistinctiveStreetNameTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        foreach (Match match in Regex.Matches(
                     text,
                     @"\b(?:\d+(?:-\d+)?\s+)?(?<name>[A-Za-z][A-Za-z0-9]*(?:\s+[A-Za-z][A-Za-z0-9]*){0,4})\s+(?:street|st|road|rd|drive|dr|avenue|ave|lane|ln|loop|pike|highway|hwy|circle|cir|court|ct|way|trail|trl|place|pl|boulevard|blvd|parkway|pkwy)\b",
                     RegexOptions.IgnoreCase))
        {
            foreach (var token in DistinctiveLocationTokens([match.Groups["name"].Value]))
                tokens.Add(token);
        }

        return tokens;
    }

    private static bool HasExistingIncidentConcreteAnchorReplacementConflict(
        IncidentDto? existingIncident,
        IReadOnlyList<EngineCall> proposedCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        if (existingIncident is null || existingIncident.Calls.Count == 0 || proposedCalls.Count == 0)
            return false;

        var existingNarrativeAnchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNarrativeConcreteTextAnchors(existingNarrativeAnchors, $"{existingIncident.Title} {existingIncident.Detail}");

        var existingRows = existingIncident.Calls
            .Select(call => new
            {
                call.CallId,
                Anchors = ExistingIncidentConflictAnchors(call, locationRows, anchorsByCall)
            })
            .Where(row => row.Anchors.Count > 0)
            .ToList();
        if (existingRows.Count == 0)
            return false;

        var protectedAnchors = existingNarrativeAnchors.Count > 0
            ? existingNarrativeAnchors
            : existingRows.SelectMany(row => row.Anchors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (protectedAnchors.Count == 0)
            return false;

        var protectedExistingCallIds = existingRows
            .Where(row => existingNarrativeAnchors.Count == 0 || AnchorSetsCompatible(row.Anchors, existingNarrativeAnchors))
            .Select(row => row.CallId)
            .ToHashSet();
        if (protectedExistingCallIds.Count == 0)
            return false;

        var proposedRows = proposedCalls
            .Select(call => new MembershipAnchorRow(call, ExistingIncidentConflictAnchors(call, locationRows, anchorsByCall)))
            .Where(row => row.Anchors.Count > 0)
            .ToList();
        if (proposedRows.Count == 0)
            return false;

        var proposedIds = proposedCalls.Select(call => call.Id).ToHashSet();
        var protectedCallWasDropped = protectedExistingCallIds.Any(callId => !proposedIds.Contains(callId));
        var hasProtectedProposedAnchor = proposedRows.Any(row => AnchorSetsCompatible(row.Anchors, protectedAnchors));
        var hasConflictingProposedAnchor = proposedRows.Any(row => !AnchorSetsCompatible(row.Anchors, protectedAnchors));
        if (!hasConflictingProposedAnchor)
            return false;

        return protectedCallWasDropped || hasProtectedProposedAnchor;
    }

    private static List<EngineCall> PruneConcreteMembershipConflicts(
        IncidentStateItem item,
        IncidentDto? existingIncident,
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        var rows = calls
            .Select(call => new MembershipAnchorRow(call, ConcreteMembershipAnchors(call, locationRows, anchorsByCall)))
            .ToList();
        var located = rows.Where(row => row.Anchors.Count > 0).ToList();
        if (located.Count < 2)
            return calls.OrderBy(call => call.StartTime).ToList();

        var preferredAnchors = PreferredMembershipAnchors(item, existingIncident);
        if (preferredAnchors.Count > 0)
        {
            var preferredRows = located
                .Where(row => AnchorSetsCompatible(row.Anchors, preferredAnchors))
                .ToList();
            var conflictingRows = located
                .Where(row => !AnchorSetsCompatible(row.Anchors, preferredAnchors))
                .ToList();
            if (preferredRows.Count > 0 && conflictingRows.Count > 0)
            {
                return BuildPrunedMembership(rows, preferredRows, [conflictingRows], locationRows, anchorsByCall, ragById);
            }
        }

        var components = BuildConcreteLocationComponents(located);
        if (components.Count <= 1)
            return calls.OrderBy(call => call.StartTime).ToList();

        var best = components
            .OrderByDescending(component => component.Count)
            .ThenByDescending(component => component.Count(row => IncidentCandidateValidator.HasStrongIncidentSignal(row.Call.Transcription)))
            .ThenBy(component => component.Min(row => row.Call.StartTime))
            .First();
        var conflicts = components
            .Where(component => !ReferenceEquals(component, best))
            .ToList();
        return BuildPrunedMembership(rows, best, conflicts, locationRows, anchorsByCall, ragById);
    }

    private static List<EngineCall> BuildPrunedMembership(
        IReadOnlyList<MembershipAnchorRow> rows,
        IReadOnlyList<MembershipAnchorRow> primaryRows,
        IReadOnlyList<IReadOnlyList<MembershipAnchorRow>> conflictGroups,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        var primaryIds = primaryRows.Select(row => row.Call.Id).ToHashSet();
        var retained = new List<EngineCall>();
        foreach (var row in rows)
        {
            if (primaryIds.Contains(row.Call.Id))
            {
                retained.Add(row.Call);
                continue;
            }

            if (row.Anchors.Count > 0)
                continue;

            if (LinksToPrimaryMembership(row.Call, primaryRows, conflictGroups, locationRows, anchorsByCall, ragById))
                retained.Add(row.Call);
        }

        return retained
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ToList();
    }

    private static bool LinksToPrimaryMembership(
        EngineCall call,
        IReadOnlyList<MembershipAnchorRow> primaryRows,
        IReadOnlyList<IReadOnlyList<MembershipAnchorRow>> conflictGroups,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        var primaryScore = primaryRows
                .Select(row => ScoreCallEvidenceLink(call, row.Call, locationRows, anchorsByCall, ragById))
            .DefaultIfEmpty(0)
            .Max();
        if (primaryScore < 0.62)
            return false;

        var conflictScore = conflictGroups
            .SelectMany(group => group)
            .Select(row => ScoreCallEvidenceLink(call, row.Call, locationRows, anchorsByCall, ragById))
            .DefaultIfEmpty(0)
            .Max();
        return conflictScore < 0.62 || primaryScore > conflictScore;
    }

    private static List<List<MembershipAnchorRow>> BuildConcreteLocationComponents(IReadOnlyList<MembershipAnchorRow> located)
    {
        var remaining = located.ToDictionary(row => row.Call.Id);
        var components = new List<List<MembershipAnchorRow>>();
        while (remaining.Count > 0)
        {
            var seed = remaining.Values.OrderBy(row => row.Call.StartTime).First();
            remaining.Remove(seed.Call.Id);
            var component = new List<MembershipAnchorRow> { seed };
            var queue = new Queue<MembershipAnchorRow>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var candidate in remaining.Values.ToList())
                {
                    if (!AnchorSetsCompatible(current.Anchors, candidate.Anchors))
                        continue;

                    remaining.Remove(candidate.Call.Id);
                    component.Add(candidate);
                    queue.Enqueue(candidate);
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static HashSet<string> PreferredMembershipAnchors(IncidentStateItem item, IncidentDto? existingIncident)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingIncident is not null)
            AddConcreteTextAnchors(anchors, $"{existingIncident.Title} {existingIncident.Detail}");

        var location = item.EventLocation;
        if (location is not null)
        {
            AddConcreteTextAnchors(anchors, string.Join(' ', new[]
            {
                location.Route,
                location.Marker,
                location.Direction,
                location.Address,
                location.Intersection,
                location.Landmark,
                location.EvidenceText
            }));
        }

        if (anchors.Count == 0)
            AddConcreteTextAnchors(anchors, $"{item.Title} {item.Detail}");
        return anchors;
    }

    private static HashSet<string> ConcreteMembershipAnchors(
        EngineCall call,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(call.Id, call.Transcription))
            AddConcreteMembershipAnchor(anchors, anchor);
        if (anchorsByCall.TryGetValue(call.Id, out var storedAnchors))
        {
            foreach (var anchor in storedAnchors)
                AddConcreteMembershipAnchor(anchors, anchor);
        }

        foreach (var location in LocationKeys(call.Id, locationRows))
            anchors.Add($"location:{location}");
        return anchors;
    }

    private static HashSet<string> ExistingIncidentConflictAnchors(
        IncidentCallDto call,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(call.CallId, call.Transcript))
            AddMergeConflictLocationAnchor(anchors, anchor);
        if (anchorsByCall.TryGetValue(call.CallId, out var storedAnchors))
        {
            foreach (var anchor in storedAnchors)
                AddMergeConflictLocationAnchor(anchors, anchor);
        }

        foreach (var location in LocationKeys(call.CallId, locationRows))
            anchors.Add($"location:{location}");
        return anchors;
    }

    private static HashSet<string> ExistingIncidentConflictAnchors(
        EngineCall call,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(call.Id, call.Transcription))
            AddMergeConflictLocationAnchor(anchors, anchor);
        if (anchorsByCall.TryGetValue(call.Id, out var storedAnchors))
        {
            foreach (var anchor in storedAnchors)
                AddMergeConflictLocationAnchor(anchors, anchor);
        }

        foreach (var location in LocationKeys(call.Id, locationRows))
            anchors.Add($"location:{location}");
        return anchors;
    }

    private static HashSet<string> MergeConflictLocationAnchors(
        EngineCall call,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(call.Id, call.Transcription))
            AddMergeConflictLocationAnchor(anchors, anchor);
        if (anchorsByCall.TryGetValue(call.Id, out var storedAnchors))
        {
            foreach (var anchor in storedAnchors)
                AddMergeConflictLocationAnchor(anchors, anchor);
        }

        foreach (var location in LocationKeys(call.Id, locationRows))
            anchors.Add($"location:{location}");
        return anchors;
    }

    private static void AddConcreteTextAnchors(HashSet<string> anchors, string text)
    {
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(0, text))
            AddConcreteMembershipAnchor(anchors, anchor);
    }

    private static void AddNarrativeConcreteTextAnchors(HashSet<string> anchors, string text)
    {
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(0, text))
        {
            if (string.IsNullOrWhiteSpace(anchor.Value))
                continue;
            if (TranscriptLocationService.LooksLikeTrafficLanePosition(anchor.Value))
                continue;
            if (anchor.Kind is "highway_mile_marker" or "vehicle_tag")
            {
                anchors.Add($"{anchor.Kind}:{anchor.Value}".ToLowerInvariant());
                continue;
            }

            if (anchor.Kind is "address" or "intersection" or "business_or_landmark" or "location"
                && anchor.Confidence >= 0.50)
                anchors.Add($"{anchor.Kind}:{anchor.Value}".ToLowerInvariant());
        }
    }

    private static void AddConcreteMembershipAnchor(HashSet<string> anchors, CallAnchorRecord anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor.Value))
            return;
        if (TranscriptLocationService.LooksLikeTrafficLanePosition(anchor.Value))
            return;
        if (anchor.Kind is "highway_mile_marker" or "vehicle_tag")
        {
            anchors.Add($"{anchor.Kind}:{anchor.Value}".ToLowerInvariant());
            return;
        }

        if (anchor.Kind is "address" or "intersection" or "business_or_landmark"
            && anchor.Confidence >= 0.72
            && !string.Equals(anchor.Source, "deterministic_weak", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(anchor.Source, "location_hint", StringComparison.OrdinalIgnoreCase))
        {
            anchors.Add($"{anchor.Kind}:{anchor.Value}".ToLowerInvariant());
            return;
        }

        if (string.Equals(anchor.Kind, "location", StringComparison.OrdinalIgnoreCase) && anchor.Confidence >= 0.72)
            anchors.Add($"{anchor.Kind}:{anchor.Value}".ToLowerInvariant());
    }

    private static void AddMergeConflictLocationAnchor(HashSet<string> anchors, CallAnchorRecord anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor.Value))
            return;
        if (TranscriptLocationService.LooksLikeTrafficLanePosition(anchor.Value))
            return;
        if (anchor.Kind is "highway_mile_marker")
        {
            anchors.Add($"{anchor.Kind}:{anchor.Value}".ToLowerInvariant());
            return;
        }

        if (anchor.Kind is "address" or "intersection" or "business_or_landmark" or "location"
            && anchor.Confidence >= 0.50)
            anchors.Add($"{anchor.Kind}:{anchor.Value}".ToLowerInvariant());
    }

    private static bool AnchorSetsCompatible(IReadOnlySet<string> left, IReadOnlySet<string> right) =>
        left.Count > 0 && right.Count > 0 && left.Any(l => right.Any(r => MembershipAnchorsCompatible(l, r)));

    private static bool MembershipAnchorsCompatible(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        var leftParsed = ParseMembershipAnchor(left);
        var rightParsed = ParseMembershipAnchor(right);
        if (leftParsed.Value.Length == 0 || rightParsed.Value.Length == 0)
            return false;
        if (leftParsed.Kind == "highway_mile_marker" || rightParsed.Kind == "highway_mile_marker")
            return false;

        var leftTokens = MembershipAnchorTokens(leftParsed.Value);
        var rightTokens = MembershipAnchorTokens(rightParsed.Value);
        return leftTokens.Count > 0
               && rightTokens.Count > 0
               && leftTokens.Overlaps(rightTokens);
    }

    private static (string Kind, string Value) ParseMembershipAnchor(string value)
    {
        value ??= string.Empty;
        var index = value.IndexOf(':');
        if (index <= 0 || index >= value.Length - 1)
            return (string.Empty, value);
        return (value[..index].ToLowerInvariant(), value[(index + 1)..].ToLowerInvariant());
    }

    private static HashSet<string> MembershipAnchorTokens(string value)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "road", "rd", "street", "st", "drive", "dr", "avenue", "ave", "lane", "ln",
            "pike", "highway", "hwy", "place", "circle", "court", "way", "trail",
            "parkway", "boulevard", "the", "and", "near", "at", "of", "block", "location"
        };
        return Regex.Split(value ?? string.Empty, @"[^a-z0-9]+")
            .Where(token => token.Length >= 4 && !stop.Contains(token) && !token.All(char.IsDigit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool CanAttachNewCallToAcceptedIncidentMembership(
        IncidentEventValidationInput input,
        EngineCall call,
        IReadOnlyList<EngineCall> acceptedCalls,
        EvidenceVerificationCall? decision,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById)
    {
        if (acceptedCalls.Count == 0)
            return false;

        var bestServerLink = acceptedCalls
            .Select(accepted => ScoreCallEvidenceLink(call, accepted, locationRows, anchorsByCall, ragById))
            .DefaultIfEmpty(0)
            .Max();
        if (bestServerLink >= 0.78)
            return true;

        if (decision is not null && IsVerifierIncludedMembership(decision))
        {
            var hasStrongVerifierEvidence = decision.SameEventEvidence
                .Select(NormalizeEvidenceToken)
                .Any(IsStrongSameEventEvidence);
            if (hasStrongVerifierEvidence
                && bestServerLink >= 0.62
                && HasCompatibleIncidentEventText([call], acceptedCalls))
                return true;
        }

        return decision is null
               && IsFallbackMembershipCandidate(input, call, acceptedCalls, locationRows, anchorsByCall, ragById, allowSingleCall: false);
    }

    private static bool IsHardRejectedVerifierDecision(EvidenceVerificationCall? decision)
    {
        if (decision == null)
            return false;

        var label = NormalizeEvidenceLabel(decision.Label);
        var confidence = decision.Confidence;
        if (label == "contradicts" && confidence >= 0.70)
            return true;
        if (label == "unrelated" && confidence >= 0.68)
            return true;

        var conflicts = decision.Conflicts
            .Select(NormalizeEvidenceToken)
            .Where(value => value.Length > 0 && value != "none" && value != "no_conflict")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var strongEvidence = decision.SameEventEvidence
            .Select(NormalizeEvidenceToken)
            .Any(IsStrongSameEventEvidence);
        return conflicts.Count > 0 && !strongEvidence;
    }

    private static bool IsVerifierIncludedMembership(EvidenceVerificationCall? decision)
    {
        if (decision == null)
            return false;

        var label = NormalizeEvidenceLabel(decision.Label);
        var role = NormalizeStructuredToken(decision.Role);
        if (role == "unrelated")
            return false;
        if (!IsSameEventDecisionAllowed(decision, true))
            return false;
        if (label == "supporting" && decision.Confidence >= 0.58)
            return true;
        if (label == "related_context" && decision.Confidence >= 0.78)
        {
            var hasStrongEvidence = decision.SameEventEvidence
                .Select(NormalizeEvidenceToken)
                .Any(IsStrongSameEventEvidence);
            return hasStrongEvidence || role is "scene_update" or "responder_status" or "outcome";
        }

        return false;
    }

    private static bool IsFallbackMembershipCandidate(
        IncidentEventValidationInput input,
        EngineCall call,
        IReadOnlyList<EngineCall> allCalls,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById,
        bool allowSingleCall = true)
    {
        if (IncidentCandidateValidator.IsRoutineStatusText(call.Transcription)
            && !IncidentCandidateValidator.HasStrongIncidentSignal(call.Transcription))
            return false;
        if (!HasActionableCallEvidence(input, call))
            return false;
        if (allowSingleCall && allCalls.Count == 1)
            return true;

        return allCalls.Any(other =>
            other.Id != call.Id
            && HasStructuredIncidentLink(call, other, locationRows, anchorsByCall, ragById));
    }

    private static bool HasAssemblerEventEvidence(
        IncidentEventValidationInput input,
        IReadOnlyList<EngineCall> calls,
        IReadOnlyDictionary<long, EvidenceVerificationCall> decisions)
    {
        if (calls.Count == 0)
            return false;
        if (input.ParentEventEvidence.Any(value => !string.IsNullOrWhiteSpace(value)))
            return true;

        foreach (var call in calls)
        {
            if (decisions.TryGetValue(call.Id, out var decision))
            {
                var label = NormalizeEvidenceLabel(decision.Label);
                var role = NormalizeStructuredToken(decision.Role);
                var hasStrongEvidence = decision.SameEventEvidence
                    .Select(NormalizeEvidenceToken)
                    .Any(IsStrongSameEventEvidence);
                if (label == "supporting" && role is "initial_dispatch" or "scene_update" or "outcome" && (hasStrongEvidence || HasActionableCallEvidence(input, call)))
                    return true;
            }

            if (HasActionableCallEvidence(input, call))
                return true;
        }

        return false;
    }

    private static bool HasActionableCallEvidence(IncidentEventValidationInput input, EngineCall call)
    {
        if (IncidentCandidateValidator.HasStrongIncidentSignal(call.Transcription))
            return true;

        var eventClass = NormalizeStructuredToken(input.EventClass);
        if (eventClass is "emergency_event" or "traffic_event" or "fire_alarm")
            return !IncidentCandidateValidator.IsRoutineStatusText(call.Transcription)
                   && !IsTransportOrHospitalOnlyText(call.Transcription);

        return false;
    }

    private static bool IsTransportOrHospitalOnlyText(string text) =>
        Regex.IsMatch(text ?? string.Empty, @"\b(?:transport(?:ing|ed)?|inbound|eta|patient report|vital signs?|blood pressure|hospital|medical center|emergency room|er)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && !IncidentCandidateValidator.HasStrongIncidentSignal(text ?? string.Empty);

    private static bool IsStandaloneLogisticsIncident(IncidentStateItem item, IReadOnlyList<EngineCall> calls)
    {
        var eventClass = NormalizeStructuredToken(item.EventClass);
        if (!item.LogisticsOnly && eventClass is not ("medical_transport_context" or "administrative_or_logistics" or "routine_status"))
            return false;
        if (item.ParentEventEvidence.Any(value => !string.IsNullOrWhiteSpace(value)))
            return false;
        if (calls.Any(call => HasParentEmergencyEvidenceInCall(call.Transcription)))
            return false;
        return calls.Count > 0;
    }

    private static bool HasParentEmergencyEvidenceInCall(string text)
    {
        var scrubbed = Regex.Replace(
            text ?? string.Empty,
            @"\b(?:negative|den(?:y|ies|ied)|no|without)\s+(?:on\s+)?(?:chest pains?|difficulty breathing|shortness of breath|sob)\b",
            " ",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return IncidentCandidateValidator.HasStrongIncidentSignal(scrubbed);
    }

    private static IncidentEventValidationInput ToIncidentEventValidationInput(IncidentStateItem item)
    {
        var location = item.EventLocation;
        var locationText = location is null
            ? string.Empty
            : string.Join(' ', new[]
            {
                location.Route,
                location.Marker,
                location.Direction,
                location.Address,
                location.Intersection,
                location.Landmark,
                location.Text
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return new(
            item.EventClass ?? string.Empty,
            item.LogisticsOnly,
            item.ParentEventEvidence ?? [],
            locationText,
            location?.EvidenceText ?? string.Empty);
    }

    private async Task AuditIncidentOperationAsync(
        string systemShortName,
        IncidentStateItem item,
        string reason,
        bool accepted,
        IReadOnlyList<EngineCall> calls,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById,
        CancellationToken ct)
    {
        var callIds = calls.Select(c => c.Id).Distinct().OrderBy(id => id).ToList();
        var scores = callIds
            .Where(ragById.ContainsKey)
            .Select(id => ragById[id].Score)
            .ToList();
        var evidenceByToken = item.CallEvidence
            .Where(e => !string.IsNullOrWhiteSpace(e.CallId))
            .GroupBy(e => e.CallId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Evidence, StringComparer.OrdinalIgnoreCase);
        var metadata = callIds
            .Select(id =>
            {
                var hasRag = ragById.TryGetValue(id, out var rag);
                var token = CallToken(id);
                return new
                {
                    callId = id,
                    source = hasRag ? "rag_scored" : "extractor_or_existing",
                    score = hasRag ? rag!.Score : (double?)null,
                    semantic = hasRag ? rag!.TextScore : (double?)null,
                    geo = hasRag ? rag!.GeoScore : (double?)null,
                    time = hasRag ? rag!.TimeScore : (double?)null,
                    location = hasRag ? rag!.LocationLabel : string.Empty,
                    reason = hasRag ? rag!.Reason : "not present in current rag candidate set",
                    evidence = evidenceByToken.TryGetValue(token, out var evidence) ? evidence : string.Empty
                };
            })
            .ToList();
        var auditMetadata = new
        {
            eventClass = item.EventClass,
            logisticsOnly = item.LogisticsOnly,
            parentEventEvidence = item.ParentEventEvidence,
            calls = metadata
        };
        await _database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(
            0,
            DateTime.UtcNow,
            systemShortName,
            string.IsNullOrWhiteSpace(item.IncidentId) ? "new" : item.IncidentId,
            accepted ? "upsert_incident" : "reject_incident",
            accepted,
            reason,
            scores.Count == 0 ? 0 : scores.Average(),
            JsonSerializer.Serialize(callIds),
            JsonSerializer.Serialize(auditMetadata, EngineConfig.JsonOptions())), ct);
    }

    private async Task AuditReconciliationOperationAsync(
        string systemShortName,
        IncidentStateItem item,
        IncidentReconciliationAudit audit,
        CancellationToken ct)
    {
        var incidentKey = string.IsNullOrWhiteSpace(item.IncidentId) ? "new" : item.IncidentId;
        await _database.AddIncidentOperationAuditAsync(new IncidentOperationAuditDto(
            0,
            DateTime.UtcNow,
            systemShortName,
            incidentKey,
            audit.Operation,
            true,
            audit.Reason,
            audit.Score,
            JsonSerializer.Serialize(audit.CallIds),
            JsonSerializer.Serialize(new
            {
                title = item.Title,
                candidateIndex = audit.CandidateIndex,
                source = "structured_reconciliation",
                note = "No transcript regex was used; decision used structured anchors, geocodes, time, categories, and RAG scores."
            }, EngineConfig.JsonOptions())), ct);
    }

    private static bool HasStructuredIncidentLink(
        EngineCall left,
        EngineCall right,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragById,
        bool allowRagSemanticLink = true)
    {
        var minutes = Math.Abs(left.StartTime - right.StartTime) / 60d;
        if (minutes > 45)
            return false;

        var leftLocations = LocationKeys(left.Id, locationRows);
        var rightLocations = LocationKeys(right.Id, locationRows);
        if (leftLocations.Count > 0 && leftLocations.Overlaps(rightLocations))
            return true;

        var leftAnchors = StrongAnchorKeys(left.Id, anchorsByCall);
        var rightAnchors = StrongAnchorKeys(right.Id, anchorsByCall);
        if (leftAnchors.Count > 0 && leftAnchors.Overlaps(rightAnchors))
            return true;

        if (!allowRagSemanticLink)
            return false;

        var leftRag = ragById.TryGetValue(left.Id, out var l) ? l.Score : 0;
        var rightRag = ragById.TryGetValue(right.Id, out var r) ? r.Score : 0;
        return minutes <= 10 && leftRag >= 0.78 && rightRag >= 0.78
               && IncidentCandidateValidator.HasStrongIncidentSignal($"{left.Transcription} {right.Transcription}");
    }

    private static HashSet<string> LocationKeys(long callId, IReadOnlyList<CallLocationDashboardRow> locationRows) =>
        locationRows
            .Where(row => row.CallId == callId)
            .Where(row => row.GeocodeConfidence >= 0.55)
            .Select(row => string.IsNullOrWhiteSpace(row.NormalizedKey) ? row.GeocodeDisplayName : row.NormalizedKey)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Where(value => !TranscriptLocationService.LooksLikeTrafficLanePosition(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int SharedLocationCount(IReadOnlySet<string> left, IReadOnlySet<string> right) =>
        left.Count == 0 || right.Count == 0 ? 0 : left.Count(value => right.Contains(value));

    private static bool HasWeakUnmappedLocation(long callId, IReadOnlyList<CallLocationDashboardRow> locationRows) =>
        locationRows
            .Where(row => row.CallId == callId)
            .Any(row => !string.IsNullOrWhiteSpace(row.LocationText) && row.GeocodeConfidence < 0.55);

    private static string BestGeocodeSummary(long callId, IReadOnlyList<CallLocationDashboardRow> locationRows)
    {
        var row = locationRows
            .Where(r => r.CallId == callId)
            .OrderByDescending(r => r.GeocodeConfidence)
            .FirstOrDefault();
        if (row == null)
            return "geo=none";

        var text = string.IsNullOrWhiteSpace(row.GeocodeDisplayName)
            ? string.IsNullOrWhiteSpace(row.LocationText) ? row.NormalizedKey : row.LocationText
            : row.GeocodeDisplayName;
        var provider = string.IsNullOrWhiteSpace(row.GeocodeProvider) ? "none" : row.GeocodeProvider;
        return $"geo={provider}; geocode_conf={row.GeocodeConfidence:0.00}; loc={Trim(text, 70)}";
    }

    private static HashSet<string> StrongAnchorKeys(long callId, IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall) =>
        anchorsByCall.TryGetValue(callId, out var anchors)
            ? anchors
                .Where(anchor => anchor.Confidence >= 0.72)
                .Where(anchor => anchor.Kind is "vehicle_tag" or "highway_mile_marker" or "business_or_landmark")
                .Select(anchor => $"{anchor.Kind}:{anchor.Value}".ToLowerInvariant())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

    private async Task<EvidenceVerificationSelection> VerifyIncidentEvidenceAsync(
        string systemShortName,
        string title,
        string detail,
        string incidentKey,
        List<EngineCall> selectedCalls,
        IncidentDto? existingIncident,
        List<EngineCall> recentCalls,
        IReadOnlyList<IncidentRagCandidate> ragCandidates,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        HashSet<long> claimed,
        CancellationToken ct)
    {
        var selectedIds = selectedCalls.Select(c => c.Id).ToHashSet();
        var existingIds = existingIncident?.Calls.Select(c => c.CallId).ToHashSet() ?? [];
        var first = selectedCalls.Min(c => c.StartTime);
        var last = selectedCalls.Max(c => c.StartTime);
        var recentById = recentCalls.ToDictionary(c => c.Id);
        var reviewStart = Math.Max(recentCalls.Min(c => c.StartTime), first - 3600);
        var reviewEnd = Math.Min(recentCalls.Max(c => c.StartTime), last + 3600);
        var query = $"{title}\n{detail}\n{string.Join('\n', selectedCalls.OrderBy(c => c.StartTime).Select(c => c.Transcription))}";
        var evidenceVerifierRagCandidateLimit = EvidenceVerifierRagCandidateLimit();
        var vectorMatches = evidenceVerifierRagCandidateLimit <= 0
            ? []
            : await _embeddings.SearchSimilarAsync(query, systemShortName, reviewStart, reviewEnd, evidenceVerifierRagCandidateLimit, ct);
        var vectorById = vectorMatches
            .Where(m => recentById.ContainsKey(m.CallId))
            .ToDictionary(m => m.CallId, m => m);
        var ragById = ragCandidates
            .Where(c => string.Equals(c.Call.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Call.Id)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Score).First());
        var selectedAnchorSet = IncidentCandidateValidator.ExtractConcreteAnchors(
            $"{title} {detail} {string.Join(' ', selectedCalls.Select(c => c.Transcription))}");
        var selectedLocationKeys = selectedCalls
            .SelectMany(c => LocationKeys(c.Id, locationRows))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedEvidenceClass = DominantEvidenceClass(selectedCalls);
        IEnumerable<(long CallId, double Score, string Source)> nearbyEvidenceCandidates = selectedEvidenceClass.Length == 0
            ? []
            : recentCalls
                .Where(c => string.Equals(c.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase))
                .Where(c => !selectedIds.Contains(c.Id))
                .Where(c => DistanceFromWindowSeconds(c.StartTime, first, last) <= 15 * 60)
                .Where(c => string.Equals(DominantEvidenceClass([c]), selectedEvidenceClass, StringComparison.OrdinalIgnoreCase))
                .Where(c => IncidentCandidateValidator.HasStrongIncidentSignal(c.Transcription))
                .Select(c => (CallId: c.Id, Score: 0.58, Source: "nearby_evidence"));
        var reviewCandidateIds = vectorMatches
            .Select(m => (CallId: m.CallId, Score: m.Score, Source: "vector"))
            .Concat(ragCandidates.OrderByDescending(c => c.Score).Select(c => (CallId: c.Call.Id, Score: c.Score, Source: "rag")))
            .Concat(nearbyEvidenceCandidates)
            .Where(row => recentById.ContainsKey(row.CallId))
            .Where(row => !selectedIds.Contains(row.CallId))
            .Where(row => !claimed.Contains(row.CallId))
            .GroupBy(row => row.CallId)
            .Select(g => g.OrderByDescending(row => row.Score).First())
            .Where(row => ShouldReviewEvidenceCandidate(recentById[row.CallId], row.Score, selectedAnchorSet, selectedLocationKeys, selectedEvidenceClass, first, last, locationRows))
            .OrderByDescending(row => row.Score)
            .ThenByDescending(row => SharedLocationCount(selectedLocationKeys, LocationKeys(row.CallId, locationRows)))
            .ThenByDescending(row => IncidentCandidateValidator.SharedConcreteAnchorCount(selectedAnchorSet, IncidentCandidateValidator.ExtractConcreteAnchors(recentById[row.CallId].Transcription)))
            .Take(evidenceVerifierRagCandidateLimit)
            .Select(row => row.CallId)
            .ToList();
        var reviewCandidates = reviewCandidateIds
            .Select(id => recentById[id])
            .OrderBy(c => c.StartTime)
            .ToList();
        var reviewCalls = selectedCalls
            .Concat(reviewCandidates)
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.StartTime)
            .Take(EvidenceVerifierMaxCalls())
            .ToList();
        var truncatedCalls = Math.Max(0, selectedCalls.Count + reviewCandidates.Count - reviewCalls.Count);

        if (reviewCalls.Count == selectedCalls.Count && reviewCandidates.Count == 0 && selectedCalls.Count == 1)
            return new EvidenceVerificationSelection(selectedCalls, new Dictionary<long, EvidenceVerificationCall>(), selectedCalls.Count, 0, 0);

        EvidenceVerificationResult result;
        try
        {
            result = await VerifyEvidenceWithModelAsync(systemShortName, title, detail, incidentKey, selectedIds, existingIds, reviewCalls, vectorById, ragById, locationRows, truncatedCalls, ct);
        }
        catch (Exception ex)
        {
            await _database.AddEvidenceVerifierRunAsync(new EvidenceVerifierRunDto(
                0,
                DateTime.UtcNow,
                systemShortName,
                incidentKey,
                title,
                selectedCalls.Count,
                reviewCalls.Count,
                reviewCalls.Count,
                truncatedCalls,
                0,
                0,
                0,
                false,
                ex.Message), CancellationToken.None);
            _logger.LogWarning(ex, "Evidence verifier failed for incident '{Title}' on {System}; keeping extractor-selected call set", title, systemShortName);
            return new EvidenceVerificationSelection(selectedCalls, new Dictionary<long, EvidenceVerificationCall>(), selectedCalls.Count, 0, 0);
        }

        var decisions = result.Calls
            .Where(c => !string.IsNullOrWhiteSpace(c.CallId))
            .GroupBy(c => c.CallId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Confidence).First(), StringComparer.OrdinalIgnoreCase);
        var selectedById = selectedCalls.ToDictionary(c => c.Id);
        var reviewByToken = reviewCalls.ToDictionary(c => CallToken(c.Id), StringComparer.OrdinalIgnoreCase);
        var decisionsByCallId = decisions
            .Where(kvp => reviewByToken.ContainsKey(kvp.Key))
            .ToDictionary(kvp => reviewByToken[kvp.Key].Id, kvp => kvp.Value);
        var retained = new List<EngineCall>();
        var added = new List<long>();
        var dropped = new List<long>();

        foreach (var call in selectedCalls)
        {
            var token = CallToken(call.Id);
            decisions.TryGetValue(token, out var decision);
            var label = NormalizeEvidenceLabel(decision?.Label);
            var confidence = decision?.Confidence ?? 0;
            var isExisting = existingIds.Contains(call.Id);
            if (isExisting && !(label == "contradicts" && confidence >= 0.85))
            {
                retained.Add(call);
                continue;
            }

            if ((label is "" or "supporting" or "related_context" || confidence < 0.80) && IsSameEventDecisionAllowed(decision, selectedCalls.Count > 1))
            {
                retained.Add(call);
                continue;
            }

            dropped.Add(call.Id);
        }

        foreach (var call in reviewCalls)
        {
            if (selectedById.ContainsKey(call.Id))
                continue;
            if (!decisions.TryGetValue(CallToken(call.Id), out var decision))
                continue;
            var label = NormalizeEvidenceLabel(decision.Label);
            var confidence = decision.Confidence;
            if (label == "supporting" && confidence >= 0.62 && IsSameEventDecisionAllowed(decision, true))
            {
                retained.Add(call);
                added.Add(call.Id);
            }
            else if (label == "related_context" && confidence >= 0.82 && IsSameEventDecisionAllowed(decision, true))
            {
                retained.Add(call);
                added.Add(call.Id);
            }
        }

        retained = retained.DistinctBy(c => c.Id).OrderBy(c => c.StartTime).ToList();
        if (retained.Count > 0 && RetainedCallsAreOnlyLogistics(retained, decisions))
        {
            dropped.AddRange(retained.Select(c => c.Id));
            retained.Clear();
        }
        await _database.AddEvidenceVerifierRunAsync(new EvidenceVerifierRunDto(
            0,
            DateTime.UtcNow,
            systemShortName,
            incidentKey,
            title,
            selectedCalls.Count,
            reviewCalls.Count,
            result.ReviewedCalls,
            truncatedCalls + result.PromptOmittedCalls,
            added.Count,
            dropped.Count,
            retained.Count,
            true,
            string.Empty), ct);
        _logger.LogInformation(
            "Evidence verifier reconciled incident '{Title}' on {System}: selected={SelectedCalls}, reviewed={ReviewedCalls}, added={AddedCalls}, dropped={DroppedCalls}, retained={RetainedCalls}, truncated={TruncatedCalls}",
            title,
            systemShortName,
            selectedCalls.Count,
            reviewCalls.Count,
            added.Count == 0 ? "-" : string.Join(",", added),
            dropped.Count == 0 ? "-" : string.Join(",", dropped),
            retained.Count,
            truncatedCalls + result.PromptOmittedCalls);
        return new EvidenceVerificationSelection(retained, decisionsByCallId, selectedCalls.Count, added.Count, dropped.Count);
    }

    private static bool RetainedCallsAreOnlyLogistics(IReadOnlyList<EngineCall> retained, IReadOnlyDictionary<string, EvidenceVerificationCall> decisions)
    {
        var matchedRoles = retained
            .Select(call => decisions.TryGetValue(CallToken(call.Id), out var decision) ? NormalizeStructuredToken(decision.Role) : string.Empty)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .ToList();
        return matchedRoles.Count == retained.Count
               && matchedRoles.All(role => role is "transport_logistics" or "hospital_handoff" or "routine_status");
    }

    private static bool ShouldReviewEvidenceCandidate(
        EngineCall call,
        double score,
        IReadOnlySet<string> selectedAnchors,
        IReadOnlySet<string> selectedLocationKeys,
        string selectedEvidenceClass,
        long selectedFirst,
        long selectedLast,
        IReadOnlyList<CallLocationDashboardRow> locationRows)
    {
        if (score < 0.58)
            return false;
        if (selectedEvidenceClass.Length > 0
            && score >= 0.58
            && DistanceFromWindowSeconds(call.StartTime, selectedFirst, selectedLast) <= 15 * 60
            && string.Equals(DominantEvidenceClass([call]), selectedEvidenceClass, StringComparison.OrdinalIgnoreCase)
            && IncidentCandidateValidator.HasStrongIncidentSignal(call.Transcription))
            return true;

        var candidateAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(call.Transcription);
        var candidateLocations = LocationKeys(call.Id, locationRows);
        var candidateHasWeakUnmappedLocation = HasWeakUnmappedLocation(call.Id, locationRows);
        if (selectedLocationKeys.Count > 0 && candidateLocations.Count == 0 && candidateHasWeakUnmappedLocation && score < 0.78)
            return false;
        if (selectedAnchors.Count > 0)
            return IncidentCandidateValidator.SharedConcreteAnchorCount(selectedAnchors, candidateAnchors) > 0 || score >= 0.72;
        return score >= 0.68 && !IncidentCandidateValidator.IsRoutineStatusText(call.Transcription);
    }

    private static long DistanceFromWindowSeconds(long value, long start, long end)
    {
        if (value < start)
            return start - value;
        if (value > end)
            return value - end;
        return 0;
    }

    private static bool IsSameEventDecisionAllowed(EvidenceVerificationCall? decision, bool requiresJoin)
    {
        if (decision == null)
            return true;

        var evidence = decision.SameEventEvidence
            .Select(NormalizeEvidenceToken)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = decision.Conflicts
            .Select(NormalizeEvidenceToken)
            .Where(value => value.Length > 0 && value != "none" && value != "no_conflict")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var strongEvidence = evidence.Any(IsStrongSameEventEvidence);
        if (conflicts.Count > 0 && !strongEvidence)
            return false;

        if (!requiresJoin)
            return true;

        var hasOnlyWeakJoinSignals = evidence.Count > 0 && evidence.All(IsWeakSameEventEvidence);
        return strongEvidence || !hasOnlyWeakJoinSignals;
    }

    private static string NormalizeStructuredToken(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');

    private static string NormalizeEvidenceToken(string? value) => NormalizeStructuredToken(value);

    private static bool IsStrongSameEventEvidence(string value) => value is
        "exact_location"
        or "event_specific_detail"
        or "vehicle_identity"
        or "person_identity"
        or "patient_identity"
        or "unit_continuation"
        or "explicit_update"
        or "explicit_same_event_reference";

    private static bool IsWeakSameEventEvidence(string value) => value is
        "same_road"
        or "same_highway"
        or "same_area"
        or "same_talkgroup"
        or "same_category"
        or "near_time"
        or "semantic_similarity"
        or "same_agency";

    private async Task<EvidenceVerificationResult> VerifyEvidenceWithModelAsync(
        string systemShortName,
        string title,
        string detail,
        string incidentKey,
        HashSet<long> selectedIds,
        HashSet<long> existingIds,
        List<EngineCall> reviewCalls,
        IReadOnlyDictionary<long, VectorSearchMatchDto> vectorMatches,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragCandidates,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        int truncatedCalls,
        CancellationToken ct)
    {
        var baseUrl = InsightBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var prompt = BuildEvidenceVerificationPrompt(systemShortName, title, detail, incidentKey, selectedIds, existingIds, reviewCalls, vectorMatches, ragCandidates, locationRows, truncatedCalls);
        var body = new
        {
            model = InsightModel(),
            temperature = 0,
            max_tokens = EvidenceVerifierMaxOutputTokens,
            response_format = EvidenceVerificationResponseFormat(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You verify evidence for one public-safety radio incident. Classify each call independently. Output compact JSON only. Favor retaining concrete dispatch, location, continuation, and outcome calls, even across talkgroups/categories, when they support the same site-local real-world event."
                },
                new { role = "user", content = prompt.Text }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling evidence verifier endpoint {Endpoint} with model {Model} for {System} incident '{Title}' ({ReviewCalls} call(s), {PayloadChars} chars, {TruncatedCalls} truncated)", endpoint, InsightModel(), systemShortName, title, prompt.IncludedCalls, payload.Length, truncatedCalls + prompt.OmittedCalls);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(endpoint, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            await RecordUsageAsync(text, endpoint, payload.Length, reviewCalls.Take(prompt.IncludedCalls).Sum(c => c.Transcription?.Length ?? 0), 1, false, $"Evidence verifier HTTP {(int)response.StatusCode}: {Trim(text, 500)}", ct);
            throw new InvalidOperationException($"Evidence verifier request failed with HTTP {(int)response.StatusCode}: {Trim(text, 1000)}");
        }

        var usage = ExtractUsage(text);
        if (string.Equals(usage.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            var message = $"LM evidence verifier response was truncated at max_tokens={EvidenceVerifierMaxOutputTokens}.";
            await RecordUsageAsync(text, endpoint, payload.Length, reviewCalls.Take(prompt.IncludedCalls).Sum(c => c.Transcription?.Length ?? 0), 1, false, message, ct);
            throw new InsightResponseTruncatedException(message);
        }

        var parsed = ParseEvidenceVerificationResponse(text);
        await RecordUsageAsync(text, endpoint, payload.Length, reviewCalls.Take(prompt.IncludedCalls).Sum(c => c.Transcription?.Length ?? 0), 1, true, string.Empty, ct);
        return parsed with { ReviewedCalls = prompt.IncludedCalls, PromptOmittedCalls = prompt.OmittedCalls };
    }

    private EvidenceVerificationPrompt BuildEvidenceVerificationPrompt(
        string systemShortName,
        string title,
        string detail,
        string incidentKey,
        HashSet<long> selectedIds,
        HashSet<long> existingIds,
        List<EngineCall> reviewCalls,
        IReadOnlyDictionary<long, VectorSearchMatchDto> vectorMatches,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragCandidates,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        int truncatedCalls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/no_think");
        sb.AppendLine("Return only JSON in message.content.");
        sb.AppendLine($"Site/system boundary: {systemShortName}.");
        sb.AppendLine($"Incident id: {incidentKey}");
        sb.AppendLine($"Incident title: {title}");
        sb.AppendLine($"Incident detail: {detail}");
        sb.AppendLine("Classify every call below as one of: supporting, related_context, unrelated, contradicts.");
        sb.AppendLine("supporting means the call directly reports, dispatches, locates, continues, or gives outcome/status for this same incident. related_context means useful nearby context but not direct evidence. unrelated means a different event/routine traffic. contradicts means it says the incident did not happen or belongs elsewhere.");
        sb.AppendLine("Do not drop initial dispatch/location calls. Do not require the same category or talkgroup. Use call ids exactly as given.");
        sb.AppendLine("Candidate flags include extractor_selected, existing_incident_evidence, qdrant_candidate, and rag_candidate. RAG scores are advisory; reject same-topic calls that lack a shared address, unit handoff, vehicle/person, or explicit same-event reference. Routine compliance, contact, status, or admin updates are unrelated unless they share a concrete event anchor with this incident.");
        sb.AppendLine("For each call, include same_event_evidence using only these tokens when present: exact_location, event_specific_detail, vehicle_identity, person_identity, patient_identity, unit_continuation, explicit_update, explicit_same_event_reference, same_road, same_highway, same_area, same_talkgroup, same_category, same_agency, near_time, semantic_similarity.");
        sb.AppendLine("For each call, include conflicts using only these tokens when present: vehicle_identity_conflict, location_conflict, direction_conflict, person_conflict, patient_conflict, event_type_conflict, no_conflict.");
        sb.AppendLine("For each call, include role using one of: initial_dispatch, scene_update, responder_status, outcome, transport_logistics, hospital_handoff, routine_status, unrelated. Transport logistics and hospital handoffs can support a parent incident, but cannot be the only retained calls in a newly created incident.");
        sb.AppendLine("Same road/highway/talkgroup/category/agency/time are weak signals only. For THP/highway traffic stops or BOLOs, do not classify as supporting when plates, vehicle descriptions, direction, person, or event type conflict unless the transcript explicitly says it is a correction or update to the same event.");
        sb.AppendLine("Location confidence rule: geocode_conf >= 0.55 supports an exact_location claim when the location text matches. geocode_conf below 0.55, geocode=none, or unmapped location text is weak only; do not use it as the sole shared anchor between calls.");
        sb.AppendLine("Title/detail rule: do not retain calls merely because the title names a stronger event type. If the call transcript does not support the titled event type or a concrete same-event role, mark it unrelated or related_context instead of supporting.");
        sb.AppendLine("Return every included call with only call_id, label, confidence, role, same_event_evidence, and conflicts. Do not include explanations.");
        if (truncatedCalls > 0)
            sb.AppendLine($"Context note: {truncatedCalls} nearby call(s) were omitted by guardrails before this verifier prompt.");
        sb.AppendLine();
        sb.AppendLine("Calls:");
        var used = sb.Length;
        var included = 0;
        foreach (var call in reviewCalls.OrderBy(c => c.StartTime))
        {
            var flags = new List<string>();
            if (selectedIds.Contains(call.Id)) flags.Add("extractor_selected");
            if (existingIds.Contains(call.Id)) flags.Add("existing_incident_evidence");
            if (vectorMatches.TryGetValue(call.Id, out var vector)) flags.Add($"qdrant_candidate:{vector.Score:0.00}");
            if (ragCandidates.TryGetValue(call.Id, out var rag)) flags.Add($"rag_candidate:{rag.Score:0.00}");
            if (flags.Count == 0) flags.Add("review_candidate");
            var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime();
            var geo = BestGeocodeSummary(call.Id, locationRows);
            var line = $"- [id:{CallToken(call.Id)}] [{local:HH:mm:ss}] flags={string.Join("|", flags)} | {call.SystemShortName} | {Label(call)} | category={call.Category} | {geo}: {Trim(call.Transcription, EvidenceVerifierTranscriptCharLimit)}";
            if (used + line.Length + Environment.NewLine.Length > EvidenceVerifierPromptCharLimit)
                break;
            sb.AppendLine(line);
            used += line.Length + Environment.NewLine.Length;
            included++;
        }
        var omitted = Math.Max(0, reviewCalls.Count - included);
        sb.AppendLine($"Included {included}/{reviewCalls.Count} calls under verifier prompt budget {EvidenceVerifierPromptCharLimit:N0} chars.");
        return new EvidenceVerificationPrompt(sb.ToString(), included, omitted);
    }

    private string BuildIncidentExtractionPrompt(string systemShortName, List<IncidentDto> activeIncidents, IReadOnlyList<IncidentRagCandidate> candidateCalls, long start, long end)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/no_think");
        sb.AppendLine("Return only the final JSON object in message.content. Do not place the answer in reasoning_content.");
        sb.AppendLine($"Site/system boundary: {systemShortName}. Do not merge across sites unless an input call explicitly says this is the same cross-site response.");
        sb.AppendLine($"State window: {DateTimeOffset.FromUnixTimeSeconds(start).ToLocalTime()} to {DateTimeOffset.FromUnixTimeSeconds(end).ToLocalTime()}.");
        sb.AppendLine($"Task: review hybrid RAG candidates and return at most {IncidentMaxReturnedItems} incident state items that should be created or updated. Assign useful related calls to an existing incident or a new incident. Leave routine/unrelated calls unassigned or held. If nothing clearly changed, return {{\"incidents\":[]}}.");
        sb.AppendLine("Rules: do not use category as a grouping qualifier; require a concrete shared anchor such as address, road, landmark, patient/person, vehicle/plate, unit handoff, or explicit same-call reference; every incident must include all source_call_ids it should retain; every call_id must be copied exactly from input.");
        sb.AppendLine($"Narrative rule: title <= {IncidentTitleCharLimit} characters. detail <= {IncidentDetailCharLimit} characters and must be one short sentence, not a transcript. Do not concatenate call transcripts, quote radio dialogue, preserve filler, or include speaker turns. Summarize only the event, location/anchor, responders/status, and known outcome.");
        sb.AppendLine("Source support rule: every concrete title/detail fact, including weapons, vehicles, injuries, fire/smoke, location, and subject description, must be supported by at least one call listed in call_ids. Do not borrow facts from candidate calls that are not included in call_ids.");
        sb.AppendLine("Event location rule: for each returned incident include event_location. Use kind=highway_mile_marker when the calls support an interstate/route plus mile marker; normalize route like I-75 or I-24, marker as the spoken marker number, direction as NB/SB/EB/WB when supported, and evidence_text as the shortest supporting phrase. Use kind=unknown with blank fields when no concrete event location is supported.");
        sb.AppendLine("Output rule: do not include call_evidence, rejected calls, unchanged incidents, notes, markdown, or explanations. Return only compact incident objects with incident_id, status, title, detail, category, confidence, call_ids, event_class, parent_event_evidence, logistics_only, and event_location.");
        sb.AppendLine("Identifier rule: for an update, incident_id must exactly copy one active incident_id listed below. For a new incident, incident_id must be exactly \"new\". Never invent incident_id values.");
        sb.AppendLine("Change rule: return only new incidents or active incidents whose call_ids/status/title/detail materially changed. Do not restate unchanged active incidents.");
        sb.AppendLine("Transport rule: hospital handoff/transport calls alone are not incidents. They may be supporting call_ids only when a parent event such as a crash, fire, rescue, assault, pursuit, or medical emergency is also present.");
        sb.AppendLine("Classify event_class as one of emergency_event, public_safety_event, traffic_event, fire_alarm, medical_transport_context, routine_status, administrative_or_logistics, or unknown. If the candidate is only transport, flight planning, hospital handoff, launch/ETA, transfer, or other logistics, set logistics_only=true and event_class=medical_transport_context or administrative_or_logistics. Set parent_event_evidence to concrete parent emergency anchors such as CPR in progress, MVC with injuries, overdose, stabbing, or fire. Leave it empty when no parent emergency/event is present.");
        sb.AppendLine("Event type rule: decide the real event type from transcript evidence, not from the radio channel or talkgroup category. A fire talkgroup may dispatch an elevator entrapment, medical assist, alarm, crash, or rescue; do not title it as a fire unless the transcript describes actual fire/smoke/flames/alarm activation.");
        sb.AppendLine("Status: use active for developing/ongoing incidents and concluded when the event appears complete or stale. Do not return concluded incidents that have no supporting calls in this state window.");
        sb.AppendLine("Categories are labels only: choose one from police, fire, ems, traffic, public_works, utilities, other after deciding the incident.");
        sb.AppendLine("RAG scores are advisory: semantic, geo, and time scores help find candidates, but you must still reject topical/routine matches that do not describe the same real-world event.");
        sb.AppendLine();
        sb.AppendLine("Active incidents:");
        foreach (var incident in activeIncidents.OrderByDescending(i => i.LastSeen).Take(10).OrderBy(i => i.LastSeen))
        {
            var key = string.IsNullOrWhiteSpace(incident.IncidentKey) ? $"legacy-{incident.Id}" : incident.IncidentKey;
            var strongest = incident.Calls.OrderByDescending(c => c.RawTimestamp).Take(3).Select(c => CallToken(c.CallId));
            sb.AppendLine($"- [incident_id:{key}] status={incident.Status}; last={DateTimeOffset.FromUnixTimeSeconds(incident.LastSeen).ToLocalTime():HH:mm}; title={Trim(incident.Title, 70)}; detail={Trim(incident.Detail, 90)}; strongest_call_ids=[{string.Join(",", strongest)}]");
        }

        sb.AppendLine();
        sb.AppendLine("Candidate calls:");
        var used = sb.Length;
        var included = 0;
        var candidates = PrioritizeIncidentCandidates(candidateCalls);
        foreach (var candidate in candidates)
        {
            var call = candidate.Call;
            var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime();
            var text = Trim(call.Transcription, IncidentTranscriptCharLimit);
            var line = $"- [id:{CallToken(call.Id)}] [{local:HH:mm:ss}] score={candidate.Score:0.00}; semantic={candidate.TextScore:0.00}; geo={candidate.GeoScore:0.00}; location={candidate.LocationLabel}; {call.SystemShortName} | {Label(call)} | category={call.Category}: {text}";
            if (used + line.Length + Environment.NewLine.Length > IncidentPromptCharLimit)
                break;
            sb.AppendLine(line);
            used += line.Length + Environment.NewLine.Length;
            included++;
        }

        sb.AppendLine($"Included {included}/{candidateCalls.Count} candidate calls under prompt budget {IncidentPromptCharLimit:N0} chars. Return no more than {IncidentMaxReturnedItems} incident items.");
        return sb.ToString();
    }

    private IReadOnlyList<IncidentRagCandidate> PrioritizeIncidentCandidates(IReadOnlyList<IncidentRagCandidate> candidates)
    {
        return candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.GeoScore)
            .ThenByDescending(c => c.TimeScore)
            .Take(IncidentPromptCandidateLimit())
            .OrderBy(c => c.Call.StartTime)
            .ToList();
    }

    private async Task PersistInsightEventsAsync(long windowId, InsightResult result, List<EngineCall> batch, CancellationToken ct)
    {
        var events = result.Events
            .Where(IsNotableInsightEvent)
            .Select(ev =>
            {
                var calls = ResolveEventCalls(ev, batch);
                if (calls.Count == 0)
                    return null;

                return new InsightEventRecordDto
                {
                    Title = string.IsNullOrWhiteSpace(ev.Title) ? "Radio insight" : ev.Title.Trim(),
                    Detail = string.IsNullOrWhiteSpace(ev.Detail) ? result.SummaryText : ev.Detail.Trim(),
                    Category = string.IsNullOrWhiteSpace(ev.Category) ? "other" : ev.Category.Trim().ToLowerInvariant(),
                    FirstSeen = calls.Min(c => c.StartTime),
                    LastSeen = calls.Max(c => c.StartTime),
                    Confidence = Math.Clamp(ev.Confidence, 0, 1),
                    Calls = calls.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio")).ToList()
                };
            })
            .Where(e => e != null)
            .Cast<InsightEventRecordDto>()
            .ToList();

        await _database.ReplaceInsightEventsAsync(windowId, events, ct);
        if (events.Count > 0)
            _logger.LogInformation("Persisted {Count} insight event(s) for window {WindowId}", events.Count, windowId);
        else
            _logger.LogInformation("Persisted 0 insight events for window {WindowId}; summary was: {Summary}", windowId, Trim(result.SummaryText, 300));
    }

    private async Task<InsightResult> SummarizeWindowAsync(List<EngineCall> calls, long start, long end, InsightPromptMode mode, CancellationToken ct)
    {
        var budget = PromptBudget.For(mode);
        var baseUrl = InsightBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new
        {
            model = InsightModel(),
            temperature = 0.1,
            max_tokens = budget.MaxOutputTokens,
            response_format = InsightResponseFormat(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You summarize radio call transcripts into concise, actionable category insights. Output JSON with fields summary_text and notable_events (list of {title, detail, category, timestamp, confidence, call_ids}). For each notable event: choose exactly one category from [police, fire, ems, traffic, public_works, utilities, other], set timestamp as local HH:mm (24h), and include one or more matching call_ids copied exactly from the provided call lines. Extract useful intelligence from radio shorthand when supported by context, including common 10-codes, status codes, disposition codes, and spoken numeric codes such as 10-7, 10-49, 1049, code 16, signal codes, unit status, locations, hazards, vehicles, people, and outcomes. Preserve the original code in detail text when its meaning is uncertain; do not invent local code meanings. Omit routine acknowledgements and 'no incident' findings."
                },
                new { role = "user", content = BuildPrompt(calls, start, end, budget) }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling LM Studio insights endpoint {Endpoint} with model {Model} for {Calls} calls ({PayloadChars} chars, mode={Mode}, promptLimit={PromptLimit}, transcriptLimit={TranscriptLimit}, maxTokens={MaxTokens})", endpoint, InsightModel(), calls.Count, payload.Length, mode, budget.PromptCharLimit, budget.TranscriptCharLimit, budget.MaxOutputTokens);
        Exception? last = null;
        for (var attempt = 0; attempt <= Math.Max(0, _config.AiInsights.MaxRetries); attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(endpoint, content, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Automatic insights request failed with HTTP {(int)response.StatusCode}: {Trim(text, 1000)}");
                _logger.LogInformation("LM Studio insights response received for {Calls} calls ({ResponseChars} chars)", calls.Count, text.Length);
                var usage = ExtractUsage(text);
                if (string.Equals(usage.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    var message = $"LM response was truncated at max_tokens={budget.MaxOutputTokens}; retry with a smaller summary window.";
                    await RecordUsageAsync(text, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, message, ct);
                    throw new InsightResponseTruncatedException(message);
                }

                var parsed = ParseResponse(text);
                await RecordUsageAsync(text, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, true, string.Empty, ct);
                return parsed;
            }
            catch (InsightResponseTruncatedException)
            {
                throw;
            }
            catch (Exception ex) when (IsCallerRequestedCompletionCancellation(ex, ct))
            {
                _logger.LogInformation("LM Studio insights completion was canceled by the caller; skipping AI completion failure health record.");
                throw;
            }
            catch (Exception ex) when (attempt < Math.Max(0, _config.AiInsights.MaxRetries))
            {
                last = ex;
                await RecordUsageAsync(string.Empty, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, ex.Message, CancellationToken.None);
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                last = ex;
                await RecordUsageAsync(string.Empty, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, ex.Message, CancellationToken.None);
                break;
            }
        }

        throw new InvalidOperationException(last?.Message ?? "Automatic insights request failed.", last);
    }

    private static bool IsCallerRequestedCompletionCancellation(Exception ex, CancellationToken ct) =>
        ct.IsCancellationRequested && ex is OperationCanceledException;

    private async Task RecordUsageAsync(string responseText, string endpoint, int payloadChars, int inputChars, int attempt, bool success, string error, CancellationToken ct)
    {
        var usage = ExtractUsage(responseText);
        if (!success && usage.ReasoningTokens > 0 && !error.Contains("reasoning_tokens=", StringComparison.OrdinalIgnoreCase))
            error = string.IsNullOrWhiteSpace(error)
                ? $"reasoning_tokens={usage.ReasoningTokens}"
                : $"{error} reasoning_tokens={usage.ReasoningTokens}";
        await _database.AddLmUsageAsync(new TokenUsageEntryDto(
            0,
            DateTime.UtcNow,
            "automatic insights",
            "chat.completions",
            success,
            error,
            endpoint,
            InsightModel(),
            usage.ResponseModel,
            usage.FinishReason,
            inputChars,
            payloadChars,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens), ct);
    }

    private static (int PromptTokens, int CompletionTokens, int TotalTokens, int ReasoningTokens, string ResponseModel, string FinishReason) ExtractUsage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (0, 0, 0, 0, string.Empty, string.Empty);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var prompt = ReadInt(root, "usage", "prompt_tokens", "promptTokens");
            var completion = ReadInt(root, "usage", "completion_tokens", "completionTokens");
            var total = ReadInt(root, "usage", "total_tokens", "totalTokens");
            var reasoning = ReadInt(root, "usage", "completion_tokens_details", "reasoning_tokens", "reasoningTokens");
            var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var finish = string.Empty;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("finish_reason", out var f))
                finish = f.GetString() ?? string.Empty;
            return (prompt, completion, total, reasoning, model, finish);
        }
        catch
        {
            return (0, 0, 0, 0, string.Empty, string.Empty);
        }
    }

    private static int ReadInt(JsonElement root, string parent, string snake, string camel)
    {
        if (!root.TryGetProperty(parent, out var obj)) return 0;
        if (obj.TryGetProperty(snake, out var a) && a.TryGetInt32(out var av)) return av;
        if (obj.TryGetProperty(camel, out var b) && b.TryGetInt32(out var bv)) return bv;
        return 0;
    }

    private static int ReadInt(JsonElement root, string parent, string child, string snake, string camel)
    {
        if (!root.TryGetProperty(parent, out var obj)) return 0;
        if (!obj.TryGetProperty(child, out var nested)) return 0;
        if (nested.TryGetProperty(snake, out var a) && a.TryGetInt32(out var av)) return av;
        if (nested.TryGetProperty(camel, out var b) && b.TryGetInt32(out var bv)) return bv;
        return 0;
    }

    private string BuildPrompt(List<EngineCall> calls, long start, long end, PromptBudget budget)
    {
        var sb = new StringBuilder();
        var targetEventCount = budget.MaxEvents > 0 ? budget.MaxEvents : calls.Count switch
        {
            >= 300 => 18,
            >= 200 => 16,
            >= 100 => 14,
            >= 50 => 12,
            _ => 8
        };

        sb.AppendLine("/no_think");
        sb.AppendLine("Return only the final JSON object in message.content. Do not place the answer in reasoning_content.");
        sb.AppendLine($"Window: {DateTimeOffset.FromUnixTimeSeconds(start).ToLocalTime()} to {DateTimeOffset.FromUnixTimeSeconds(end).ToLocalTime()}");
        sb.AppendLine("Category guidance: each notable event must use one of these categories exactly:");
        sb.AppendLine("police, fire, ems, traffic, public_works, utilities, other");
        sb.AppendLine("Timestamp guidance: include timestamp as local HH:mm (24h) using the provided call times.");
        sb.AppendLine("Insight guidance: notable_events are AI summary cards for category pages. They may describe one useful source call or multiple clearly related calls. Incidents are derived later only from notable_events that contain 2 or more strongly related calls.");
        sb.AppendLine("Single-call guidance: include a one-call notable_event when a call contains actionable or situationally useful information, such as a dispatched complaint, medical transport, fire response, BOLO/vehicle/person description, road hazard, pursuit, crash, alarm, disturbance, arrest/custody, welfare check, address/location, agency handoff, or meaningful radio-code/status detail.");
        sb.AppendLine("Incident grouping guidance: a multi-call notable_event must be one real-world event, not a topic bucket. Do not group calls merely because they are close in time, share a category, share an agency, share a talkgroup, or are routine unit/admin/status traffic.");
        sb.AppendLine("Concrete anchor guidance: include multiple call_ids only when every included call shares concrete evidence such as the same address/street/intersection, landmark, patient, person/name, vehicle/plate, unit continuation, radio channel handoff, or an explicit reference to the same call. If a call lacks a concrete anchor to the event, omit it.");
        sb.AppendLine("Routine exclusion guidance: do not create notable_events for pure acknowledgements, availability only, radio checks, administrative chatter, generic dispatch coordination with no event detail, or isolated unclear/inaudible calls. Do not treat the whole window as routine if some individual calls contain useful dispatch intelligence.");
        sb.AppendLine("Linkage guidance: each notable event must include one or more call_ids copied exactly from input lines. Include every clearly related source call in this window, but do not pad an event with weakly related calls. Each call_id may belong to at most one notable_event.");
        sb.AppendLine("Evidence guidance: for every included call_id, add a call_evidence entry {call_id, evidence}. The evidence must be a short anchor phrase explaining why that call belongs to this event, such as an address, road, unit handoff, patient/vehicle detail, or quoted shared phrase.");
        sb.AppendLine("Radio-code guidance: police/fire/EMS traffic often uses compact codes. Treat patterns like 10-7, 10-49/1049, code 16, signal codes, unit status, and disposition codes as important evidence. Include the code and any context-supported meaning in notable event details; if the meaning is ambiguous, keep the code verbatim instead of guessing.");
        sb.AppendLine($"Coverage guidance: target up to {targetEventCount} notable_events for this window when evidence supports it. Prefer several precise single-call insights over one generic 'routine traffic' summary. Return an empty notable_events array only when every call is truly routine or unusable. Keep each detail concise (1 sentence).");

        var prioritizedCalls = calls
            .OrderByDescending(c => c.IsAlertMatch)
            .ThenByDescending(c => c.StopTime - c.StartTime);

        var lines = new List<string>();
        var usedChars = sb.Length;
        foreach (var call in prioritizedCalls)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime();
            var prefix = call.IsAlertMatch ? "[ALERT] " : string.Empty;
            var line = $"- [id:{CallToken(call.Id)}] [{local:h:mm tt}] {call.SystemShortName} | {Label(call)}: {prefix}{Trim(call.Transcription, budget.TranscriptCharLimit)}";
            if (usedChars + line.Length + Environment.NewLine.Length > budget.PromptCharLimit)
                break;

            lines.Add(line);
            usedChars += line.Length + Environment.NewLine.Length;
        }

        var omitted = Math.Max(0, calls.Count - lines.Count);
        sb.AppendLine($"Analyzing {lines.Count} calls (alerts prioritized, prompt budget {budget.PromptCharLimit:N0} chars, transcript trim {budget.TranscriptCharLimit:N0} chars, omitted {omitted}):");
        foreach (var line in lines)
            sb.AppendLine(line);

        return sb.ToString();
    }

    private static InsightResult ParseResponse(string text)
    {
        using var root = JsonDocument.Parse(text);
        var content = text;
        if (root.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentElement))
        {
            content = contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? string.Empty
                : contentElement.GetRawText();

            if (string.IsNullOrWhiteSpace(content) &&
                message.TryGetProperty("reasoning_content", out var reasoningElement) &&
                reasoningElement.ValueKind == JsonValueKind.String)
            {
                content = reasoningElement.GetString() ?? string.Empty;
            }
        }

        content = StripCodeFence(content);
        content = ExtractJsonObject(content);
        using var doc = JsonDocument.Parse(content);
        var summary = doc.RootElement.TryGetProperty("summary_text", out var summaryElement)
            ? summaryElement.GetString() ?? string.Empty
            : string.Empty;
        var events = new List<InsightEvent>();
        if (doc.RootElement.TryGetProperty("notable_events", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(80))
            {
                var ids = new List<string>();
                if (item.TryGetProperty("call_ids", out var callIds) && callIds.ValueKind == JsonValueKind.Array)
                    ids.AddRange(callIds.EnumerateArray().Select(v => v.GetString()).Where(v => !string.IsNullOrWhiteSpace(v))!);

                events.Add(new InsightEvent(
                    GetString(item, "title"),
                    GetString(item, "detail"),
                    GetString(item, "category"),
                    GetString(item, "timestamp"),
                    item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                    ids,
                    ReadEvidence(item)));
            }
        }
        return new InsightResult(summary, events);
    }

    private static IncidentExtractionResult ParseIncidentExtractionResponse(string text)
    {
        using var root = JsonDocument.Parse(text);
        var content = text;
        if (root.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentElement))
        {
            content = contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? string.Empty
                : contentElement.GetRawText();
            if (string.IsNullOrWhiteSpace(content) &&
                message.TryGetProperty("reasoning_content", out var reasoningElement) &&
                reasoningElement.ValueKind == JsonValueKind.String)
                content = reasoningElement.GetString() ?? string.Empty;
        }

        content = StripCodeFence(content);
        content = ExtractJsonObject(content);
        using var doc = JsonDocument.Parse(content);
        var incidents = new List<IncidentStateItem>();
        if (doc.RootElement.TryGetProperty("incidents", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(80))
            {
                var ids = NormalizeModelCallIds(ReadStringArray(item, "call_ids", 80));

                incidents.Add(new IncidentStateItem(
                    GetString(item, "incident_id"),
                    GetString(item, "status"),
                    GetString(item, "title"),
                    GetString(item, "detail"),
                    GetString(item, "category"),
                    item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                    ids.CallIds,
                    ids.RawCallIds,
                    ids.DroppedCallIds,
                    ReadEvidence(item),
                    GetString(item, "event_class"),
                    ReadStringArray(item, "parent_event_evidence"),
                    item.TryGetProperty("logistics_only", out var logisticsOnly) && logisticsOnly.ValueKind == JsonValueKind.True,
                    ReadEventLocation(item)));
            }
        }
        return new IncidentExtractionResult(incidents);
    }

    private static IncidentEventLocation? ReadEventLocation(JsonElement item)
    {
        if (!item.TryGetProperty("event_location", out var location) || location.ValueKind != JsonValueKind.Object)
            return null;

        var supportingCallIds = NormalizeModelCallIds(ReadStringArray(location, "supporting_call_ids", 40)).CallIds;
        return new IncidentEventLocation(
            GetString(location, "kind"),
            GetString(location, "route"),
            GetString(location, "marker"),
            GetString(location, "direction"),
            GetString(location, "address"),
            GetString(location, "intersection"),
            GetString(location, "landmark"),
            GetString(location, "text"),
            GetString(location, "evidence_text"),
            supportingCallIds,
            location.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0);
    }

    private EvidenceVerificationResult ParseEvidenceVerificationResponse(string text)
    {
        using var root = JsonDocument.Parse(text);
        var content = text;
        if (root.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentElement))
        {
            content = contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? string.Empty
                : contentElement.GetRawText();
            if (string.IsNullOrWhiteSpace(content) &&
                message.TryGetProperty("reasoning_content", out var reasoningElement) &&
                reasoningElement.ValueKind == JsonValueKind.String)
                content = reasoningElement.GetString() ?? string.Empty;
        }

        content = StripCodeFence(content);
        content = ExtractJsonObject(content);
        using var doc = JsonDocument.Parse(content);
        var calls = new List<EvidenceVerificationCall>();
        if (doc.RootElement.TryGetProperty("calls", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(EvidenceVerifierMaxCalls()))
            {
                calls.Add(new EvidenceVerificationCall(
                    GetString(item, "call_id"),
                    GetString(item, "label"),
                    item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                    GetString(item, "reason"),
                    ReadStringArray(item, "same_event_evidence"),
                    ReadStringArray(item, "conflicts"),
                    GetString(item, "role")));
            }
        }
        return new EvidenceVerificationResult(calls, 0, 0);
    }

    private static List<string> ReadStringArray(JsonElement item, string propertyName, int maxItems = 8)
    {
        if (!item.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Take(maxItems)
            .ToList();
    }

    private static CallIdReadResult NormalizeModelCallIds(IEnumerable<string> values)
    {
        var normalized = new List<string>();
        foreach (var value in values)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                continue;

            if (TryNormalizeModelCallId(trimmed, out var token))
                normalized.Add(token);
        }

        var distinct = normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new CallIdReadResult(
            distinct,
            distinct,
            []);
    }

    private static bool TryNormalizeModelCallId(string value, out string token)
    {
        token = string.Empty;
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.Length == 13
            && trimmed[0] is 'C' or 'c'
            && trimmed.Skip(1).All(IsAsciiHexDigit))
        {
            token = "C" + trimmed[1..].ToUpperInvariant();
            return true;
        }

        if (trimmed.Length == 12 && trimmed.All(IsAsciiHexDigit))
        {
            token = trimmed.ToUpperInvariant();
            return true;
        }

        if (trimmed.All(char.IsAsciiDigit)
            && long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && id > 0)
        {
            token = id.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static bool IsAsciiHexDigit(char ch) =>
        ch is >= '0' and <= '9'
        || ch is >= 'a' and <= 'f'
        || ch is >= 'A' and <= 'F';

    private List<EngineCall> ResolveEventCalls(InsightEvent ev, List<EngineCall> batch)
    {
        var byToken = new Dictionary<string, EngineCall>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in batch)
        {
            byToken[CallToken(call.Id)] = call;
            byToken[call.Id.ToString(CultureInfo.InvariantCulture)] = call;
            byToken[call.Id.ToString("X12", CultureInfo.InvariantCulture)] = call;
        }

        var resolved = ev.CallIds
            .Select(id => byToken.TryGetValue(id.Trim(), out var call) ? call : null)
            .Where(c => c != null)
            .DistinctBy(c => c!.Id)
            .Cast<EngineCall>()
            .ToList();

        if (resolved.Count > 0)
            return resolved;

        if (DateTimeOffset.TryParse(ev.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp))
        {
            var eventTime = timestamp.ToUnixTimeSeconds();
            return batch
                .Where(c => Math.Abs(c.StartTime - eventTime) <= 90)
                .OrderBy(c => Math.Abs(c.StartTime - eventTime))
                .Take(2)
                .ToList();
        }

        return resolved;
    }

    private static bool IsNotableInsightEvent(InsightEvent ev)
    {
        var title = NormalizeEventText(ev.Title);
        var detail = NormalizeEventText(ev.Detail);
        var combined = $"{title} {detail}".Trim();
        if (string.IsNullOrWhiteSpace(combined) || ev.CallIds.Count == 0)
            return false;

        return IncidentCandidateValidator.IsNotableText(title, detail, ev.CallIds.Count);
    }

    private static string NormalizeEventText(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeEvidenceLabel(string? label)
    {
        var normalized = Regex.Replace((label ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z_]+", "_").Trim('_');
        return normalized switch
        {
            "support" or "supported" or "direct_support" or "directly_supporting" => "supporting",
            "context" or "related" or "related_contextual" => "related_context",
            "unrelated_call" or "not_related" => "unrelated",
            "contradictory" or "contradiction" => "contradicts",
            _ => normalized
        };
    }

    private static bool IsIncidentEligibleCall(EngineCall call)
    {
        if (!string.Equals(call.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase))
            return false;
        var text = call.Transcription?.Trim() ?? string.Empty;
        return !call.IsImported && text.Length >= 12;
    }

    private bool IsCatalogIncidentEligibleOrStrongSignal(EngineCall call)
    {
        if (_catalog.IsIncidentEligible(call.Talkgroup))
            return true;
        return IncidentCandidateValidator.HasStrongIncidentSignal($"{call.TalkgroupName} {call.Transcription}");
    }

    private static string NormalizeIncidentCategory(string category, string eventClass, string title, string detail, List<EngineCall> calls)
    {
        var normalized = (category ?? string.Empty).Trim().ToLowerInvariant();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "police", "fire", "ems", "traffic", "public_works", "utilities", "other" };
        var callCategories = calls
            .Select(c => (c.Category ?? string.Empty).Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();
        var majority = callCategories
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .FirstOrDefault() ?? "other";

        if (callCategories.Count > 0 && callCategories.All(c => string.Equals(c, majority, StringComparison.OrdinalIgnoreCase)))
            return allowed.Contains(majority) ? majority : "other";

        var eventCategory = DeriveEventCategory(eventClass, $"{title} {detail} {string.Join(' ', calls.Select(c => c.Transcription))}");
        if (allowed.Contains(eventCategory) && eventCategory != "other")
            return eventCategory;

        var publicSafetyCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "police", "fire", "ems", "traffic" };
        if (callCategories.Count > 0 &&
            callCategories.All(publicSafetyCategories.Contains) &&
            normalized is "public_works" or "utilities" or "other")
            return publicSafetyCategories.Contains(majority) ? majority : "other";

        if (allowed.Contains(normalized))
            return normalized;

        return allowed.Contains(majority) ? majority : "other";
    }

    private static string DeriveEventCategory(string eventClass, string evidenceText)
    {
        var cls = NormalizeStructuredToken(eventClass);

        if (cls == "traffic_event")
            return "traffic";
        if (cls == "fire_alarm")
            return "fire";
        if (cls == "medical_transport_context")
            return "ems";

        return IncidentEvidenceClassifier.Analyze(evidenceText).Category;
    }

    private bool IsEnabled() =>
        _config.Setup.Completed &&
        _config.AiInsights.Enabled &&
        !string.IsNullOrWhiteSpace(InsightBaseUrl()) &&
        !string.IsNullOrWhiteSpace(InsightModel());

    private int BatchSize() => Math.Max(1, _config.AiInsights.BatchSize <= 0 ? DefaultBatchSize : _config.AiInsights.BatchSize);

    private int IncidentRunIntervalSeconds() => Math.Clamp(_config.AiInsights.IncidentRunIntervalSeconds <= 0 ? 300 : _config.AiInsights.IncidentRunIntervalSeconds, 60, 1800);

    private int IncidentPromptCandidateLimit() => Math.Clamp(_config.AiInsights.IncidentPromptCandidateLimit <= 0 ? 18 : _config.AiInsights.IncidentPromptCandidateLimit, 6, 40);

    private int IncidentV2ShadowCandidateLimit() => Math.Clamp(_config.AiInsights.IncidentV2ShadowCandidateLimit <= 0 ? 18 : _config.AiInsights.IncidentV2ShadowCandidateLimit, 6, 40);

    private int IncidentV3FrameCandidateLimit() => Math.Clamp(_config.AiInsights.IncidentV3FrameCandidateLimit <= 0 ? 18 : _config.AiInsights.IncidentV3FrameCandidateLimit, 6, 40);

    private int IncidentNewVectorQueryLimit() => Math.Clamp(_config.AiInsights.IncidentNewVectorQueryLimit <= 0 ? 8 : _config.AiInsights.IncidentNewVectorQueryLimit, 0, 20);

    private int IncidentActiveVectorQueryLimit() => Math.Clamp(_config.AiInsights.IncidentActiveVectorQueryLimit <= 0 ? 6 : _config.AiInsights.IncidentActiveVectorQueryLimit, 0, 15);

    private int EvidenceVerifierRagCandidateLimit() => Math.Clamp(_config.AiInsights.EvidenceVerifierRagCandidateLimit <= 0 ? 5 : _config.AiInsights.EvidenceVerifierRagCandidateLimit, 0, 10);

    private int EvidenceVerifierMaxCalls() => Math.Clamp(_config.AiInsights.EvidenceVerifierMaxCalls <= 0 ? 8 : _config.AiInsights.EvidenceVerifierMaxCalls, 4, 12);

    private int ComputeAdaptiveBatchSize(int pendingCount)
    {
        if (_failureStreak <= 0)
            return BatchSize();
        return Math.Max(5, BatchSize() / (1 << Math.Min(_failureStreak, 3)));
    }

    private void RotateFailedBatch(List<EngineCall> failedBatch)
    {
        lock (_gate)
        {
            var ids = failedBatch.Select(c => c.Id).ToHashSet();
            var moved = _pending.Where(c => ids.Contains(c.Id)).ToList();
            _pending.RemoveAll(c => ids.Contains(c.Id));
            _pending.AddRange(moved);
        }
    }

    private string InsightBaseUrl() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl)
        ? _config.Transcription.OpenAiBaseUrl
        : _config.AiInsights.OpenAiBaseUrl;

    private string InsightApiKey() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiApiKey)
        ? _config.Transcription.OpenAiApiKey
        : _config.AiInsights.OpenAiApiKey;

    private string InsightModel() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel)
        ? _config.Transcription.OpenAiModel
        : _config.AiInsights.OpenAiModel;

    private static string Label(EngineCall call) => string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"TG {call.Talkgroup}" : call.TalkgroupName;

    private static string CallToken(long id) => $"C{id:X12}";

    private static string Trim(string value, int max) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= max ? value : value[..max];

    private static string TrimIncidentText(string value, int max)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (text.Length <= max)
            return text;

        var sentenceEnd = text.LastIndexOfAny(['.', '!', '?'], Math.Min(text.Length - 1, max - 1));
        if (sentenceEnd >= Math.Max(80, max / 2))
            return text[..(sentenceEnd + 1)].Trim();

        return text[..max].Trim().TrimEnd(',', ';', ':') + ".";
    }

    private static bool IsIncidentNarrativeAcceptable(string title, string detail, List<EngineCall> calls)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(detail))
            return false;
        if (title.Length > IncidentTitleCharLimit || detail.Length > IncidentDetailCharLimit)
            return false;

        var normalized = NormalizeEventText(detail).ToLowerInvariant();
        if (normalized.Contains(">>") ||
            normalized.Contains("[beeping]") ||
            normalized.Contains("you're welcome") ||
            normalized.Contains("thank you") ||
            normalized.Contains(" i copy") ||
            normalized.Contains(" copy ") ||
            normalized.Contains(" i'm ") ||
            normalized.Contains(" we've ") ||
            normalized.Contains(" we'll "))
            return false;

        var detailTokens = MeaningfulTokens(normalized).ToList();
        if (detailTokens.Count < 5)
            return false;

        var transcript = NormalizeEventText(string.Join(' ', calls.Select(c => c.Transcription))).ToLowerInvariant();
        var transcriptTokens = MeaningfulTokens(transcript).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (transcriptTokens.Count == 0)
            return false;

        var overlap = detailTokens.Count(token => transcriptTokens.Contains(token)) / (double)detailTokens.Count;
        return overlap < 0.86 || detailTokens.Count <= 18;
    }

    private static IEnumerable<string> MeaningfulTokens(string text)
    {
        foreach (Match match in Regex.Matches(text, @"[a-z0-9]{3,}"))
        {
            var token = match.Value;
            if (token is "the" or "and" or "for" or "that" or "this" or "with" or "you" or "your" or "are" or "was" or "were" or "have" or "has" or "had" or "they" or "them" or "from" or "will" or "all" or "out" or "now")
                continue;
            yield return token;
        }
    }

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static List<CallEvidence> ReadEvidence(JsonElement item)
    {
        var evidence = new List<CallEvidence>();
        if (!item.TryGetProperty("call_evidence", out var array) || array.ValueKind != JsonValueKind.Array)
            return evidence;
        foreach (var row in array.EnumerateArray())
        {
            evidence.Add(new CallEvidence(GetString(row, "call_id"), GetString(row, "evidence")));
        }
        return evidence;
    }

    private IncidentCandidateCall ToIncidentCandidateCall(EngineCall call) =>
        new(call.Id, call.StartTime, call.Transcription, call.Category, call.TalkgroupName, call.SystemShortName, IncidentEligible: _catalog.IsIncidentEligible(call.Talkgroup));

    private IncidentCandidateCall ToIncidentCandidateCall(EngineCall call, IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall) =>
        new(
            call.Id,
            call.StartTime,
            call.Transcription,
            call.Category,
            call.TalkgroupName,
            call.SystemShortName,
            anchorsByCall.TryGetValue(call.Id, out var anchors) ? anchors : [],
            _catalog.IsIncidentEligible(call.Talkgroup));

    private static string StripCodeFence(string content) =>
        Regex.Replace(content.Trim(), "^```(?:json)?\\s*|\\s*```$", string.Empty, RegexOptions.IgnoreCase);

    private static string ExtractJsonObject(string content)
    {
        content = content.Trim();
        if (content.StartsWith('{') && content.EndsWith('}'))
            return content;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start
            ? content[start..(end + 1)]
            : content;
    }

    private static object InsightResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "pizzawave_insight_result",
            strict = false,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    summary_text = new { type = "string" },
                    notable_events = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                title = new { type = "string" },
                                detail = new { type = "string" },
                                category = new { type = "string" },
                                timestamp = new { type = "string" },
                                confidence = new { type = "number" },
                                call_ids = new
                                {
                                    type = "array",
                                    items = new { type = "string" },
                                    minItems = 1
                                },
                                call_evidence = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            call_id = new { type = "string" },
                                            evidence = new { type = "string" }
                                        },
                                        required = new[] { "call_id", "evidence" }
                                    }
                                }
                            },
                            required = new[] { "title", "detail", "category", "timestamp", "confidence", "call_ids" }
                        }
                    }
                },
                required = new[] { "summary_text", "notable_events" }
            }
        }
    };

    private static object IncidentExtractionResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "pizzawave_incident_state",
            strict = false,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    incidents = new
                    {
                        type = "array",
                        maxItems = IncidentMaxReturnedItems,
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                incident_id = new { type = "string" },
                                status = new { type = "string", @enum = new[] { "active", "concluded" } },
                                title = new { type = "string", maxLength = IncidentTitleCharLimit },
                                detail = new { type = "string", maxLength = IncidentDetailCharLimit },
                                category = new { type = "string" },
                                confidence = new { type = "number" },
                                call_ids = new
                                {
                                    type = "array",
                                    items = new { type = "string" },
                                    minItems = 2
                                },
                                event_class = new
                                {
                                    type = "string",
                                    @enum = new[]
                                    {
                                        "emergency_event",
                                        "public_safety_event",
                                        "traffic_event",
                                        "fire_alarm",
                                        "medical_transport_context",
                                        "routine_status",
                                        "administrative_or_logistics",
                                        "unknown"
                                    }
                                },
                                parent_event_evidence = new
                                {
                                    type = "array",
                                    items = new { type = "string" }
                                },
                                logistics_only = new { type = "boolean" },
                                event_location = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        kind = new
                                        {
                                            type = "string",
                                            @enum = new[] { "address", "intersection", "highway_mile_marker", "road", "landmark", "unknown" }
                                        },
                                        route = new { type = "string" },
                                        marker = new { type = "string" },
                                        direction = new { type = "string" },
                                        address = new { type = "string" },
                                        intersection = new { type = "string" },
                                        landmark = new { type = "string" },
                                        text = new { type = "string" },
                                        evidence_text = new { type = "string" },
                                        supporting_call_ids = new
                                        {
                                            type = "array",
                                            items = new { type = "string" }
                                        },
                                        confidence = new { type = "number" }
                                    },
                                    required = new[] { "kind", "route", "marker", "direction", "address", "intersection", "landmark", "text", "evidence_text", "supporting_call_ids", "confidence" }
                                }
                            },
                            required = new[] { "incident_id", "status", "title", "detail", "category", "confidence", "call_ids", "event_class", "parent_event_evidence", "logistics_only", "event_location" }
                        }
                    }
                },
                required = new[] { "incidents" }
            }
        }
    };

    private static object EvidenceVerificationResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "pizzawave_evidence_verification",
            strict = false,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    calls = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                call_id = new { type = "string" },
                                label = new { type = "string", @enum = new[] { "supporting", "related_context", "unrelated", "contradicts" } },
                                confidence = new { type = "number" },
                                role = new
                                {
                                    type = "string",
                                    @enum = new[]
                                    {
                                        "initial_dispatch",
                                        "scene_update",
                                        "responder_status",
                                        "outcome",
                                        "transport_logistics",
                                        "hospital_handoff",
                                        "routine_status",
                                        "unrelated"
                                    }
                                },
                                same_event_evidence = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "string",
                                        @enum = new[]
                                        {
                                            "exact_location",
                                            "event_specific_detail",
                                            "vehicle_identity",
                                            "person_identity",
                                            "patient_identity",
                                            "unit_continuation",
                                            "explicit_update",
                                            "explicit_same_event_reference",
                                            "same_road",
                                            "same_highway",
                                            "same_area",
                                            "same_talkgroup",
                                            "same_category",
                                            "same_agency",
                                            "near_time",
                                            "semantic_similarity"
                                        }
                                    }
                                },
                                conflicts = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "string",
                                        @enum = new[]
                                        {
                                            "vehicle_identity_conflict",
                                            "location_conflict",
                                            "direction_conflict",
                                            "person_conflict",
                                            "patient_conflict",
                                            "event_type_conflict",
                                            "no_conflict"
                                        }
                                    }
                                }
                            },
                            required = new[] { "call_id", "label", "confidence", "role", "same_event_evidence", "conflicts" }
                        }
                    }
                },
                required = new[] { "calls" }
            }
        }
    };

    private sealed record InsightResult(string SummaryText, List<InsightEvent> Events);
    private sealed record InsightEvent(string Title, string Detail, string Category, string Timestamp, double Confidence, List<string> CallIds, List<CallEvidence> CallEvidence);
    private sealed record IncidentExtractionResult(List<IncidentStateItem> Incidents);
    private sealed record IncidentExtractionRunResult(IncidentExtractionResult Result, IncidentPlanExecutionResultV3? IncidentV3Execution);
    private sealed class IncidentExtractionFallbackState
    {
        public int ConsecutiveTerminalTimeoutSkips { get; set; }
        public bool TimeoutCircuitOpen { get; set; }
    }
    private sealed record CallIdReadResult(List<string> CallIds, List<string> RawCallIds, List<string> DroppedCallIds);
    private sealed record CurrentIncidentSnapshot(string IncidentId, string Title, HashSet<long> CallIds);
    private sealed record ShadowCandidateState(
        string CandidateKey,
        string SystemShortName,
        string Decision,
        string Category,
        string Title,
        IReadOnlyList<long> AcceptedCallIds,
        IReadOnlyList<long> PendingCallIds,
        DateTimeOffset FirstSeenUtc,
        DateTimeOffset LastSeenUtc,
        int Observations,
        bool Resolved,
        string ResolvedIncidentId,
        string ResolvedTitle,
        DateTimeOffset? ResolvedAtUtc);
    private sealed record ShadowCandidateLifecycleNotice(
        string CandidateKey,
        string Lifecycle,
        IReadOnlyList<long> AcceptedCallIds,
        IReadOnlyList<long> PendingCallIds,
        string CurrentIncidentId,
        string CurrentTitle,
        DateTimeOffset FirstSeenUtc,
        int Observations);
    private sealed record IncidentStateItem(
        string IncidentId,
        string Status,
        string Title,
        string Detail,
        string Category,
        double Confidence,
        List<string> CallIds,
        List<string> RawCallIds,
        List<string> DroppedCallIds,
        List<CallEvidence> CallEvidence,
        string EventClass,
        List<string> ParentEventEvidence,
        bool LogisticsOnly,
        IncidentEventLocation? EventLocation);
    private sealed record IncidentEventLocation(
        string Kind,
        string Route,
        string Marker,
        string Direction,
        string Address,
        string Intersection,
        string Landmark,
        string Text,
        string EvidenceText,
        List<string> SupportingCallIds,
        double Confidence);
    private sealed record OwnedCallOverlapResult(bool Accepted, string Reason, List<EngineCall> Calls, bool ForceNewIncident = false);
    private sealed record EvidenceVerificationPrompt(string Text, int IncludedCalls, int OmittedCalls)
    {
        public override string ToString() => Text;
    }
    private sealed record EvidenceVerificationSelection(
        List<EngineCall> Calls,
        IReadOnlyDictionary<long, EvidenceVerificationCall> Decisions,
        int InitialSelectedCalls,
        int AddedCalls,
        int DroppedCalls);
    private sealed record IncidentTargetResolution(
        bool Accepted,
        IncidentDto? Incident,
        IReadOnlyList<IncidentDto> MergeIncidents,
        string Reason);
    private sealed record IncidentEvidenceMatch(
        IncidentDto? Incident,
        bool Accepted,
        double Score,
        string Reason);
    private sealed record MembershipAnchorRow(EngineCall Call, HashSet<string> Anchors);
    private sealed record EvidenceVerificationResult(List<EvidenceVerificationCall> Calls, int ReviewedCalls, int PromptOmittedCalls);
    private sealed record EvidenceVerificationCall(string CallId, string Label, double Confidence, string Reason, List<string> SameEventEvidence, List<string> Conflicts, string Role);
    private sealed record CallEvidence(string CallId, string Evidence);
    private enum InsightPromptMode { NormalLive, CompactManual }
    private sealed record PromptBudget(int PromptCharLimit, int TranscriptCharLimit, int MaxOutputTokens, int MaxEvents = 0)
    {
        public static PromptBudget For(InsightPromptMode mode) => mode switch
        {
            InsightPromptMode.CompactManual => new PromptBudget(CompactPromptCharLimit, CompactTranscriptCharLimit, CompactMaxOutputTokens, CompactMaxEvents),
            _ => new PromptBudget(NormalPromptCharLimit, NormalTranscriptCharLimit, NormalMaxOutputTokens)
        };
    }
}

public sealed class InsightResponseTruncatedException(string message) : InvalidOperationException(message);
