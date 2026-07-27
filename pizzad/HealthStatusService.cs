using System.Reflection;

namespace pizzad;

public sealed class HealthStatusService
{
    private const int ThroughputWindowMinutes = 10;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(5);
    private readonly EngineConfig _config;
    private readonly EnginePipeline _pipeline;
    private readonly EngineDatabase _database;
    private readonly IngestControlService _ingestControl;
    private readonly LiveTrActivityMonitor _liveTrActivity;
    private readonly EmbeddingService _embeddings;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private HealthDto? _cached;
    private DateTime _cachedUntilUtc = DateTime.MinValue;

    public HealthStatusService(
        EngineConfig config,
        EnginePipeline pipeline,
        EngineDatabase database,
        IngestControlService ingestControl,
        LiveTrActivityMonitor liveTrActivity,
        EmbeddingService embeddings)
    {
        _config = config;
        _pipeline = pipeline;
        _database = database;
        _ingestControl = ingestControl;
        _liveTrActivity = liveTrActivity;
        _embeddings = embeddings;
    }

    public async Task<HealthDto> GetAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_cached != null && now < _cachedUntilUtc)
            return _cached with { ServerTimeUtc = now };

        await _cacheLock.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if (_cached != null && now < _cachedUntilUtc)
                return _cached with { ServerTimeUtc = now };

            var health = await BuildAsync(now, ct);
            _cached = health;
            _cachedUntilUtc = now.Add(CacheDuration);
            return health;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<HealthDto> BuildAsync(DateTime now, CancellationToken ct)
    {
        var pendingTranscriptions = await _database.CountPendingTranscriptionCallsAsync(ct);
        var recentStartUtc = now.AddMinutes(-ThroughputWindowMinutes);
        var recentStartUnix = new DateTimeOffset(recentStartUtc).ToUnixTimeSeconds();
        var recentCalls = await _database.CountCallsStartedSinceAsync(recentStartUnix, ct);
        var recentTranscribed = await _database.CountTranscriptionCompletionsSinceAsync(recentStartUtc, ct);
        var recentAudioIngested = await _database.SumAudioSecondsStartedSinceAsync(recentStartUnix, ct);
        var recentAudioTranscribed = await _database.SumAudioSecondsTranscriptionCompletionsSinceAsync(recentStartUtc, ct);
        var pendingAudioSeconds = await _database.SumPendingTranscriptionAudioSecondsAsync(ct);
        var transcriptionPerformance = _pipeline.GetTranscriptionPerformance(TimeSpan.FromMinutes(ThroughputWindowMinutes));
        var aiQueueLimit = _config.AiInsights.MaxQueueDepthForManualSummary;
        var aiBlockedReason = aiQueueLimit > 0 && _pipeline.QueueDepth > aiQueueLimit
            ? $"AI summary generation is paused while transcription queue depth is above the configured limit. Queue depth: {_pipeline.QueueDepth:N0}; limit: {aiQueueLimit:N0}."
            : null;
        var aiCompletionHealth = await _database.GetAiCompletionHealthAsync(30, ct);
        var aiCompletionBlockedReason = !string.Equals(aiCompletionHealth.Status, "ok", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(aiCompletionHealth.Status, "unknown", StringComparison.OrdinalIgnoreCase)
            ? aiCompletionHealth.Message
            : null;
        var replacementOwnsProduction =
            IncidentBatchProductionGate.OwnsProduction(_config.AiInsights);
        var incidentAnalysisQueueHealth = replacementOwnsProduction
            ? await _database.GetIncidentBatchPipelineHealthAsync(
                _config.AiInsights.IncidentBatchConstructorShadowRunId,
                _config.AiInsights.IncidentBatchConstructorShadowStartAfterCallId,
                _config.AiInsights.IncidentAnalysisMaximumAgeMinutes,
                ct)
            : await _database.GetIncidentAnalysisQueueHealthAsync(
                _config.AiInsights.IncidentAnalysisMaximumAgeMinutes,
                ct);
        var incidentAnalysisBlockedReason = !string.Equals(incidentAnalysisQueueHealth.Status, "ok", StringComparison.OrdinalIgnoreCase)
            ? incidentAnalysisQueueHealth.Message
            : null;
        var embeddingHealth = await _embeddings.GetHealthAsync(ct);
        var embeddingBlockedReason = EmbeddingBlockedReason(embeddingHealth);
        var blockedReason = string.Join(" ", new[] { aiBlockedReason, aiCompletionBlockedReason, incidentAnalysisBlockedReason, embeddingBlockedReason }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (string.IsNullOrWhiteSpace(blockedReason))
            blockedReason = null;
        var trFault = TrServiceFaultReader.ReadLatest();
        var trControlState = TrServiceControlStateReader.ReadLatest();
        var liveTrStatus = _liveTrActivity.GetStatus(now, trFault, trControlState);
        return new HealthDto(
            aiCompletionBlockedReason is not null || incidentAnalysisBlockedReason is not null || embeddingBlockedReason is not null ? "degraded" : "ok",
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev",
            _config.Branding.StackName,
            _config.Storage.DatabasePath,
            _config.Storage.AudioRoot,
            _pipeline.QueueDepth,
            _pipeline.LiveQueueDepth,
            _pipeline.PriorityLiveQueueDepth,
            _pipeline.BacklogQueueDepth,
            _pipeline.IsUnderLivePressure,
            _pipeline.LivePressureQueueDepth,
            pendingTranscriptions,
            _pipeline.LiveTranscriptionWorkerCount,
            _pipeline.WhisperThreadsPerWorker,
            ThroughputWindowMinutes,
            _pipeline.DeferredLiveQueueDepth,
            recentCalls,
            recentTranscribed,
            recentCalls / (double)ThroughputWindowMinutes,
            recentTranscribed / (double)ThroughputWindowMinutes,
            recentAudioIngested,
            recentAudioTranscribed,
            recentAudioIngested / (double)ThroughputWindowMinutes,
            recentAudioTranscribed / (double)ThroughputWindowMinutes,
            pendingAudioSeconds,
            transcriptionPerformance.Count,
            transcriptionPerformance.AverageWallSeconds,
            transcriptionPerformance.AverageAudioSeconds,
            transcriptionPerformance.AverageRealtimeFactor,
            _ingestControl.GetStatus(_pipeline.QueueDepth),
            liveTrStatus,
            aiBlockedReason,
            incidentAnalysisQueueHealth,
            aiCompletionHealth,
            embeddingHealth,
            blockedReason,
            now);
    }

    private static string? EmbeddingBlockedReason(EmbeddingPipelineHealthDto health)
    {
        if (!health.Enabled)
            return null;
        if (string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!health.QdrantOk)
            return "Embedding queue is paused because Qdrant is not reachable.";
        if (!health.EmbeddingEndpointOk)
            return "Embedding queue is paused because the embedding endpoint is not reachable.";
        return string.IsNullOrWhiteSpace(health.LastError)
            ? "Embedding queue is paused due to degraded embedding health."
            : $"Embedding queue is paused: {health.LastError}";
    }
}
