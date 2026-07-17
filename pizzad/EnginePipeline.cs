using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace pizzad;

public sealed class EnginePipeline
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly EngineAlertService _alerts;
    private readonly TalkgroupResolver _talkgroups;
    private readonly TalkgroupCatalogService _catalog;
    private readonly AutomaticInsightsService _insights;
    private readonly EmbeddingService _embeddings;
    private readonly CallAudioService _audio;
    private readonly TranscriptPostProcessingService _postProcessing;
    private readonly RemoteBandwidthEstimatorService _bandwidth;
    private readonly LiveTrActivityMonitor _liveTrActivity;
    private readonly ILogger<EnginePipeline> _logger;
    private readonly RemoteTranscriptionHealthService? _remoteTranscriptionHealth;
    private readonly ConcurrentQueue<TranscriptionQueueItem> _liveQueue = new();
    private readonly ConcurrentQueue<TranscriptionQueueItem> _priorityLiveQueue = new();
    private readonly ConcurrentQueue<TranscriptionQueueItem> _deferredLiveQueue = new();
    private readonly ConcurrentQueue<TranscriptionQueueItem> _backlogQueue = new();
    private readonly SemaphoreSlim _liveSignal = new(0);
    private readonly SemaphoreSlim _backlogSignal = new(0);
    private readonly List<ITranscriber> _liveTranscribers = [];
    private readonly List<BacklogTranscriberSet> _backlogTranscribers = [];
    private readonly List<ITranscriber> _backlogGenericTranscribers = [];
    private readonly ConcurrentQueue<TranscriptionPerformanceSample> _performanceSamples = new();
    private string _provider = "none";
    private int _remoteTranscriptionWorkers;
    private int _remoteBacklogWorkers;
    private int _activeLiveTranscriptions;
    private DateTimeOffset _nextPressureLogAt = DateTimeOffset.MinValue;

    public int QueueDepth => LiveQueueDepth + _backlogQueue.Count;
    public int LiveQueueDepth => _liveQueue.Count + _priorityLiveQueue.Count + _deferredLiveQueue.Count;
    public int PriorityLiveQueueDepth => _priorityLiveQueue.Count;
    public int DeferredLiveQueueDepth => _deferredLiveQueue.Count;
    public int BacklogQueueDepth => _backlogQueue.Count;
    public bool IsUnderLivePressure => LiveQueueDepth >= LivePressureQueueDepth;
    public int LivePressureQueueDepth => Math.Max(1, _config.Transcription.LivePressureQueueDepth);
    public int LiveTranscriptionWorkerCount => _liveTranscribers.Count > 0 ? _liveTranscribers.Count : _remoteTranscriptionWorkers;
    public int WhisperThreadsPerWorker => Math.Max(1, _config.Transcription.WhisperThreads);
    public bool HasActiveLiveTranscription => Volatile.Read(ref _activeLiveTranscriptions) > 0;
    public bool RemoteTranscriptionOutageConfirmed => _remoteTranscriptionHealth?.GetSnapshot().OutageConfirmed == true;

    public EnginePipeline(
        EngineConfig config,
        EngineDatabase database,
        EventStream events,
        EngineAlertService alerts,
        TalkgroupResolver talkgroups,
        TalkgroupCatalogService catalog,
        AutomaticInsightsService insights,
        EmbeddingService embeddings,
        CallAudioService audio,
        TranscriptPostProcessingService postProcessing,
        RemoteBandwidthEstimatorService bandwidth,
        LiveTrActivityMonitor liveTrActivity,
        ILogger<EnginePipeline> logger,
        RemoteTranscriptionHealthService? remoteTranscriptionHealth = null)
    {
        _config = config;
        _database = database;
        _events = events;
        _alerts = alerts;
        _talkgroups = talkgroups;
        _catalog = catalog;
        _insights = insights;
        _embeddings = embeddings;
        _audio = audio;
        _postProcessing = postProcessing;
        _bandwidth = bandwidth;
        _liveTrActivity = liveTrActivity;
        _logger = logger;
        _remoteTranscriptionHealth = remoteTranscriptionHealth;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _provider = (_config.Transcription.Provider ?? "none").Trim().ToLowerInvariant();
        if (_provider is "whisper" or "vosk" or "faster-whisper")
        {
            var liveWorkers = _provider is "whisper" or "faster-whisper" ? ResolveLiveWorkerCount(_config) : 1;
            for (var i = 1; i <= liveWorkers; i++)
            {
                ITranscriber transcriber = _provider switch
                {
                    "vosk" => new VoskTranscriber(_config.Transcription.VoskModelPath, _logger),
                    "faster-whisper" => new FasterWhisperTranscriber(_config, _logger),
                    _ => CreateWhisperTranscriber()
                };
                await transcriber.Initialize();
                _liveTranscribers.Add(transcriber);
            }
            _logger.LogInformation("Live transcription provider initialized: {Provider}; workers={Workers}; whisperThreadsPerWorker={Threads}", _provider, _liveTranscribers.Count, WhisperThreadsPerWorker);

            if (_provider == "whisper")
            {
                var backlogWorkers = ResolveBacklogWorkerCount();
                for (var i = 1; i <= backlogWorkers; i++)
                {
                    var fastModel = ResolveWhisperModelPath("base");
                    var fast = CreateWhisperTranscriber(fastModel);
                    await fast.Initialize();

                    ITranscriber primary;
                    if (string.Equals(fastModel, _config.Transcription.WhisperModelFile, StringComparison.OrdinalIgnoreCase))
                    {
                        primary = fast;
                    }
                    else
                    {
                        primary = CreateWhisperTranscriber();
                        await primary.Initialize();
                    }

                    _backlogTranscribers.Add(new BacklogTranscriberSet(
                        i,
                        fast,
                        primary,
                        fastModel,
                        _config.Transcription.WhisperModelFile ?? string.Empty));
                }

                _logger.LogInformation(
                    "Backlog Whisper worker pool initialized: workers={WorkerCount}, fast={FastModel}, fallback={PrimaryModel}",
                    _backlogTranscribers.Count,
                    _backlogTranscribers.FirstOrDefault()?.FastModel ?? string.Empty,
                    _backlogTranscribers.FirstOrDefault()?.PrimaryModel ?? string.Empty);
            }
            else if (_provider == "faster-whisper")
            {
                var backlogWorkers = ResolveBacklogWorkerCount();
                for (var i = 1; i <= backlogWorkers; i++)
                {
                    var transcriber = new FasterWhisperTranscriber(_config, _logger);
                    await transcriber.Initialize();
                    _backlogGenericTranscribers.Add(transcriber);
                }

                _logger.LogInformation(
                    "Backlog faster-whisper worker pool initialized: workers={WorkerCount}; model={Model}",
                    _backlogGenericTranscribers.Count,
                    _config.Transcription.FasterWhisperModel);
            }
        }
        else if (_provider is "remote-faster-whisper")
        {
            _remoteTranscriptionWorkers = ResolveLiveWorkerCount(_config);
            _remoteBacklogWorkers = ResolveBacklogWorkerCount();
            _logger.LogInformation(
                "Remote faster-whisper transcription provider initialized: endpoint={Endpoint}; model={Model}; liveWorkers={LiveWorkers}; backlogWorkers={BacklogWorkers}",
                _config.Transcription.OpenAiBaseUrl,
                string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel) ? "whisper-1" : _config.Transcription.OpenAiModel,
                _remoteTranscriptionWorkers,
                _remoteBacklogWorkers);
        }

        if (_liveTranscribers.Count > 0)
        {
            foreach (var transcriber in _liveTranscribers)
                _ = Task.Run(() => LiveTranscriptionLoopAsync(transcriber, ct), ct);
        }
        else if (_remoteTranscriptionWorkers > 0)
        {
            for (var i = 0; i < _remoteTranscriptionWorkers; i++)
                _ = Task.Run(() => LiveTranscriptionLoopAsync(null, ct), ct);
        }
        else
        {
            _ = Task.Run(() => LiveTranscriptionLoopAsync(null, ct), ct);
        }
        if (_backlogTranscribers.Count > 0)
        {
            foreach (var transcribers in _backlogTranscribers)
                _ = Task.Run(() => BacklogTranscriptionLoopAsync(transcribers, ct), ct);
        }
        else if (_backlogGenericTranscribers.Count > 0)
        {
            foreach (var transcriber in _backlogGenericTranscribers)
                _ = Task.Run(() => BacklogGenericTranscriptionLoopAsync(transcriber, ct), ct);
        }
        else if (_remoteBacklogWorkers > 0)
        {
            for (var i = 0; i < _remoteBacklogWorkers; i++)
                _ = Task.Run(() => BacklogTranscriptionLoopAsync(null, ct), ct);
        }
        else
        {
            _ = Task.Run(() => BacklogTranscriptionLoopAsync(null, ct), ct);
        }
        await RecoverPendingTranscriptionsAsync(ct);
    }

    public async Task<long> IngestRawCallAsync(CallstreamPayload payload, bool imported, CancellationToken ct)
    {
        if (!imported)
            _liveTrActivity.MarkLiveCall(DateTime.UtcNow);

        var call = BuildPendingCall(payload, imported);
        var callId = await _database.UpsertCallAsync(call, ct);

        var audioPath = await PersistAudioAsync(payload, call, callId, ct);
        call = call with { Id = callId, AudioPath = audioPath };
        await _database.UpsertCallAsync(call, ct);

        await _events.PublishAsync("call_ingested", new { callId, imported }, ct);

        if (CanTranscribe())
        {
            if (imported)
            {
                EnqueueTranscription(new TranscriptionQueueItem(callId, null, audioPath, imported, true, TranscriptionWorkKind.Backlog));
            }
            else
            {
                var kind = IsDeferredTalkgroup(call.Talkgroup) ? TranscriptionWorkKind.DeferredLive : TranscriptionWorkKind.Live;
                EnqueueTranscription(new TranscriptionQueueItem(callId, payload, audioPath, imported, false, kind));
            }
            await _events.PublishAsync("job_updated", new { type = "transcription", queueDepth = QueueDepth }, ct);
        }
        else
        {
            _logger.LogInformation("Stored call {CallId} without transcription queue because setup/transcription is not ready", callId);
        }
        return callId;
    }

    private async Task RecoverPendingTranscriptionsAsync(CancellationToken ct)
    {
        if (!CanTranscribe())
            return;

        var pending = await _database.ListPendingTranscriptionCallsAsync(5000, ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var liveCount = 0;
        var backlogCount = 0;
        foreach (var call in pending)
        {
            var backlog = call.IsImported ||
                          string.Equals(call.QualityReason, "retry_backlog", StringComparison.OrdinalIgnoreCase) ||
                          call.StartTime < now - 2 * 3600;
            var kind = backlog ? TranscriptionWorkKind.Backlog : IsDeferredTalkgroup(call.Talkgroup) ? TranscriptionWorkKind.DeferredLive : TranscriptionWorkKind.Live;
            EnqueueTranscription(new TranscriptionQueueItem(call.Id, null, call.AudioPath, call.IsImported, backlog, kind));
            if (backlog)
                backlogCount++;
            else
                liveCount++;
        }

        if (pending.Count > 0)
        {
            _logger.LogWarning("Recovered {Count} pending transcription call(s) from persisted audio after startup: {LiveCount} live, {BacklogCount} backlog", pending.Count, liveCount, backlogCount);
            await _events.PublishAsync("job_updated", new { type = "transcription", queueDepth = QueueDepth }, ct);
        }
    }

    private bool CanTranscribe() =>
        _config.Setup.Completed &&
        !string.Equals((_config.Transcription.Provider ?? "none").Trim(), "none", StringComparison.OrdinalIgnoreCase);

    private void EnqueueTranscription(TranscriptionQueueItem item)
    {
        if (item.Kind is TranscriptionWorkKind.Live or TranscriptionWorkKind.DeferredLive)
        {
            if (item.Kind == TranscriptionWorkKind.DeferredLive)
            {
                _deferredLiveQueue.Enqueue(item);
            }
            else if (_liveQueue.Count + _priorityLiveQueue.Count >= LivePressureQueueDepth)
            {
                _priorityLiveQueue.Enqueue(item);
                if (DateTimeOffset.UtcNow >= _nextPressureLogAt)
                {
                    _logger.LogWarning(
                        "Live transcription queue is under pressure: {QueueDepth:N0} live call(s) queued. New live calls are being prioritized until the queue drains below {Threshold:N0}.",
                        LiveQueueDepth,
                        LivePressureQueueDepth);
                    _nextPressureLogAt = DateTimeOffset.UtcNow.AddMinutes(1);
                }
            }
            else
            {
                _liveQueue.Enqueue(item);
            }
            _liveSignal.Release();
        }
        else
        {
            _backlogQueue.Enqueue(item);
            _backlogSignal.Release();
        }
    }

    private async Task LiveTranscriptionLoopAsync(ITranscriber? transcriber, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _liveSignal.WaitAsync(ct);
            if (!_priorityLiveQueue.TryDequeue(out var item) && !_liveQueue.TryDequeue(out item) && !_deferredLiveQueue.TryDequeue(out item))
                continue;

            Interlocked.Increment(ref _activeLiveTranscriptions);
            try
            {
                await ProcessTranscriptionItemAsync(item, backlog: false, liveTranscriber: transcriber, backlogTranscribers: null, genericBacklogTranscriber: null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Live transcription worker recovered after an unhandled failure for call {CallId}; requeueing the call", item.CallId);
                EnqueueTranscription(item);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            finally
            {
                Interlocked.Decrement(ref _activeLiveTranscriptions);
            }
        }
    }

    private async Task BacklogTranscriptionLoopAsync(BacklogTranscriberSet? transcribers, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _backlogSignal.WaitAsync(ct);
            if (!_backlogQueue.TryDequeue(out var item))
                continue;

            try
            {
                await ProcessTranscriptionItemAsync(item, backlog: true, liveTranscriber: null, backlogTranscribers: transcribers, genericBacklogTranscriber: null, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Backlog transcription worker recovered after an unhandled failure for call {CallId}; requeueing the call", item.CallId);
                EnqueueTranscription(item);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private async Task BacklogGenericTranscriptionLoopAsync(ITranscriber transcriber, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _backlogSignal.WaitAsync(ct);
            if (!_backlogQueue.TryDequeue(out var item))
                continue;

            try
            {
                await ProcessTranscriptionItemAsync(item, backlog: true, liveTranscriber: null, backlogTranscribers: null, genericBacklogTranscriber: transcriber, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Generic backlog transcription worker recovered after an unhandled failure for call {CallId}; requeueing the call", item.CallId);
                EnqueueTranscription(item);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private async Task ProcessTranscriptionItemAsync(TranscriptionQueueItem item, bool backlog, ITranscriber? liveTranscriber, BacklogTranscriberSet? backlogTranscribers, ITranscriber? genericBacklogTranscriber, CancellationToken ct)
    {
        try
        {
            if (_provider == "remote-faster-whisper" && _remoteTranscriptionHealth != null)
                await _remoteTranscriptionHealth.WaitForAvailabilityAsync(ct);
            var startedAt = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var result = genericBacklogTranscriber != null
                ? await TranscribeAsync(item, genericBacklogTranscriber, ct)
                : backlog
                    ? await TranscribeBacklogAsync(item, backlogTranscribers, ct)
                    : await TranscribeAsync(item, liveTranscriber, ct);
            stopwatch.Stop();
            var call = await _database.GetCallAsync(item.CallId, ct);
            if (call == null)
                return;

            var audioSeconds = Math.Max(0, call.StopTime - call.StartTime);
            var quality = TranscriptionQualityClassifier.Classify(result.Text, audioSeconds: audioSeconds);
            var profileAllowsDownstream = DownstreamProfilePolicy.Allows(_config, _catalog, call);
            var suppressDownstream = item.Imported || item.SuppressDownstream || !profileAllowsDownstream;
            var delayedRecovery = !item.Imported && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - call.StartTime > 60 * 60;
            var alert = suppressDownstream
                ? new EngineAlertMatchResult(false, null, string.Empty, string.Empty, string.Empty, false, string.Empty)
                : _alerts.Evaluate(call, result.Text, item.Imported, delayedRecovery);
            var updatedCall = call with
            {
                Transcription = result.Text,
                TranscriptionStatus = quality.Status,
                QualityReason = quality.Reason,
                IsAlertMatch = alert.IsMatch,
                RawMetadataJson = MergeTranscriptionMetadata(call.RawMetadataJson, result.Metadata)
            };
            await UpdateCallTranscriptionWithRetryAsync(item.CallId, result.Text, quality.Status, quality.Reason, alert.IsMatch, updatedCall.RawMetadataJson, ct);
            await RecordRemoteBandwidthUsageAsync(updatedCall, ct);
            await _postProcessing.ProcessAsync(updatedCall, ct);
            RecordTranscriptionPerformance(startedAt, stopwatch.Elapsed, audioSeconds, backlog);

            if (!quality.IncludeInSummaries)
                _logger.LogInformation("Excluded call {CallId} from AI summaries due to transcription quality: {Reason}", item.CallId, quality.Reason);

            if (alert.IsMatch)
            {
                await _database.AddAlertMatchAsync(new AlertMatchDto
                {
                    CallId = item.CallId,
                    RuleName = alert.RuleName,
                    Detail = $"{alert.Type}:{alert.Detail}",
                    MatchedAt = call.StartTime,
                    IsImported = item.Imported,
                    NotificationSuppressed = item.Imported || delayedRecovery
                }, ct);
                await _events.PublishAsync("alert_matched", new { callId = item.CallId, imported = item.Imported, notificationSuppressed = item.Imported || delayedRecovery }, ct);
            }

            await _events.PublishAsync("call_transcribed", new { callId = item.CallId, imported = item.Imported, backlog, notificationSuppressed = delayedRecovery }, ct);
            if (!suppressDownstream && quality.IncludeInSummaries)
            {
                await _embeddings.EnqueueAsync(updatedCall, ct);
                await _insights.EnqueueAsync(updatedCall, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("Transcription canceled during shutdown for call {CallId}; it will remain pending for recovery", item.CallId);
        }
        catch (Exception ex)
        {
            if (_provider == "remote-faster-whisper" && IsTransientRemoteFailure(ex))
            {
                _remoteTranscriptionHealth?.ReportRequestFailure(ex);
                _logger.LogWarning(ex, "Remote transcription is temporarily unavailable for call {CallId}; leaving it pending and requeueing it", item.CallId);
                EnqueueTranscription(item);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return;
            }

            _logger.LogError(ex, "Transcription failed for call {CallId}", item.CallId);
            var call = await _database.GetCallAsync(item.CallId, ct);
            var metadata = MergeTranscriptionMetadata(call?.RawMetadataJson ?? "{}", CreateTranscriptionFailureMetadata(ex));
            await UpdateCallTranscriptionWithRetryAsync(item.CallId, string.Empty, "failed", "transcription_error", false, metadata, ct);
            if (call != null)
                await RecordRemoteBandwidthUsageAsync(call with { Transcription = string.Empty, TranscriptionStatus = "failed", QualityReason = "transcription_error", IsAlertMatch = false, RawMetadataJson = metadata }, ct);
        }
    }

    private async Task UpdateCallTranscriptionWithRetryAsync(long callId, string transcription, string status, string qualityReason, bool isAlertMatch, string? rawMetadataJson, CancellationToken ct)
    {
        const int maxAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _database.UpdateCallTranscriptionAsync(callId, transcription, status, qualityReason, isAlertMatch, rawMetadataJson, ct);
                return;
            }
            catch (SqliteException ex) when (IsTransientSqliteLock(ex) && attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(2000, 100 * attempt * attempt));
                _logger.LogWarning(ex, "SQLite was locked while updating transcription for call {CallId}; retrying attempt {Attempt}/{MaxAttempts} after {DelayMs}ms", callId, attempt, maxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task RecordRemoteBandwidthUsageAsync(EngineCall call, CancellationToken ct)
    {
        try
        {
            await _bandwidth.RecordTranscriptionAsync(call, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record remote bandwidth usage for call {CallId}", call.Id);
        }
    }

    private static bool IsTransientSqliteLock(SqliteException ex) =>
        ex.SqliteErrorCode is 5 or 6;

    public TranscriptionPerformanceSnapshot GetTranscriptionPerformance(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(window);
        while (_performanceSamples.TryPeek(out var sample) && sample.CompletedAt < cutoff)
            _performanceSamples.TryDequeue(out _);

        var samples = _performanceSamples.Where(s => s.CompletedAt >= cutoff).ToArray();
        if (samples.Length == 0)
            return new TranscriptionPerformanceSnapshot(0, 0, 0, 0, 0, 0);

        var avgWall = samples.Average(s => s.WallSeconds);
        var avgAudio = samples.Average(s => s.AudioSeconds);
        var ratio = avgAudio > 0 ? avgWall / avgAudio : 0;
        return new TranscriptionPerformanceSnapshot(
            samples.Length,
            samples.Count(s => !s.Backlog),
            samples.Count(s => s.Backlog),
            avgWall,
            avgAudio,
            ratio);
    }

    private void RecordTranscriptionPerformance(DateTimeOffset startedAt, TimeSpan wallTime, double audioSeconds, bool backlog)
    {
        _performanceSamples.Enqueue(new TranscriptionPerformanceSample(DateTimeOffset.UtcNow, wallTime.TotalSeconds, audioSeconds, backlog));
        var cutoff = startedAt.Subtract(TimeSpan.FromMinutes(30));
        while (_performanceSamples.TryPeek(out var sample) && sample.CompletedAt < cutoff)
            _performanceSamples.TryDequeue(out _);
    }

    private async Task<TranscriptionResult> TranscribeAsync(TranscriptionQueueItem item, ITranscriber? transcriber, CancellationToken ct)
    {
        if (_provider == "none")
            return TranscriptionResult.Empty(CreateTranscriptionMetadata());

        var wav = item.Raw == null
            ? await ReadPersistedWavAsync(item.AudioPath, ct)
            : await _audio.CreateTranscriptionWavAsync(item.Raw, item.CallId, ct);

        if (_provider is "whisper" or "vosk" or "faster-whisper")
        {
            if (transcriber == null)
            {
                wav.Dispose();
                return TranscriptionResult.Empty(CreateTranscriptionMetadata());
            }

            try
            {
                using var localWav = _provider == "whisper"
                    ? await _audio.Ensure16kMonoPcmAsync(wav, item.CallId, ct)
                    : wav;
                return new TranscriptionResult(await transcriber.TranscribeCall(localWav), CreateTranscriptionMetadata());
            }
            finally
            {
                if (_provider == "whisper")
                    wav.Dispose();
            }
        }

        if (_provider is "lmstudio" or "openai" or "remote-faster-whisper")
        {
            using (wav)
                return await TranscribeOpenAiCompatibleAsync(wav, ct);
        }

        wav.Dispose();
        return TranscriptionResult.Empty(CreateTranscriptionMetadata());
    }

    private async Task<TranscriptionResult> TranscribeBacklogAsync(TranscriptionQueueItem item, BacklogTranscriberSet? transcribers, CancellationToken ct)
    {
        if (_provider != "whisper" || transcribers == null)
            return await TranscribeAsync(item, _liveTranscribers.FirstOrDefault(), ct);

        var fast = await TranscribeWithAsync(item, transcribers.Fast, ct);
        var fastQuality = TranscriptionQualityClassifier.Classify(fast.Text);
        if (fastQuality.IncludeInSummaries)
            return fast;

        _logger.LogInformation("Backlog worker {WorkerId} fast transcription for call {CallId} classified as {Reason}; retrying with primary model.", transcribers.WorkerId, item.CallId, fastQuality.Reason);
        return await TranscribeWithAsync(item, transcribers.Primary, ct);
    }

    private async Task<TranscriptionResult> TranscribeWithAsync(TranscriptionQueueItem item, ITranscriber transcriber, CancellationToken ct)
    {
        var wav = item.Raw == null
            ? await ReadPersistedWavAsync(item.AudioPath, ct)
            : await _audio.CreateTranscriptionWavAsync(item.Raw, item.CallId, ct);
        try
        {
            using var localWav = await _audio.Ensure16kMonoPcmAsync(wav, item.CallId, ct);
            return new TranscriptionResult(await transcriber.TranscribeCall(localWav), CreateTranscriptionMetadata());
        }
        finally
        {
            wav.Dispose();
        }
    }

    private async Task<MemoryStream> ReadPersistedWavAsync(string audioPath, CancellationToken ct)
    {
        var fullPath = Path.IsPathRooted(audioPath) ? audioPath : Path.Combine(_config.Storage.AudioRoot, audioPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Persisted audio not found for pending transcription: {fullPath}", fullPath);

        var bytes = await File.ReadAllBytesAsync(fullPath, ct);
        return new MemoryStream(bytes);
    }

    private async Task<TranscriptionResult> TranscribeOpenAiCompatibleAsync(MemoryStream wav, CancellationToken ct)
    {
        var baseUrl = (_config.Transcription.OpenAiBaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return TranscriptionResult.Empty(CreateTranscriptionMetadata());

        using var client = new HttpClient
        {
            Timeout = _provider == "remote-faster-whisper" ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(10)
        };
        if (!string.IsNullOrWhiteSpace(_config.Transcription.OpenAiApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Transcription.OpenAiApiKey);

        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav.ToArray());
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "call.wav");
        content.Add(new StringContent(string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel) ? "whisper-1" : _config.Transcription.OpenAiModel), "model");

        using var response = await client.PostAsync($"{baseUrl}/audio/transcriptions", content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var metadata = CreateTranscriptionMetadata(doc.RootElement);
        if (doc.RootElement.TryGetProperty("text", out var text))
            return new TranscriptionResult(text.GetString() ?? string.Empty, metadata);
        return new TranscriptionResult(json, metadata);
    }

    public async Task<bool> EnqueueFailedTranscriptionRetryAsync(long callId, CancellationToken ct)
    {
        if (!CanTranscribe())
            throw new InvalidOperationException("Transcription is not configured and ready.");
        var call = await _database.GetCallAsync(callId, ct);
        if (call == null || !string.Equals(call.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(call.QualityReason, "transcription_error", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(call.AudioPath))
            return false;
        await UpdateCallTranscriptionWithRetryAsync(call.Id, string.Empty, "pending", "retry_backlog", false, call.RawMetadataJson, ct);
        EnqueueTranscription(new TranscriptionQueueItem(call.Id, null, call.AudioPath, call.IsImported, call.IsImported, TranscriptionWorkKind.Backlog));
        await _events.PublishAsync("job_updated", new { type = "transcription", queueDepth = QueueDepth }, ct);
        return true;
    }

    private static bool IsTransientRemoteFailure(Exception exception)
    {
        var root = exception.GetBaseException();
        if (root is TimeoutException or TaskCanceledException)
            return true;
        if (root is not HttpRequestException http)
            return false;
        return !http.StatusCode.HasValue
            || http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)http.StatusCode.Value >= 500;
    }

    private TranscriptionMetadata CreateTranscriptionMetadata(JsonElement? response = null)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["provider"] = _provider,
            ["model"] = ResolveConfiguredModelName(),
            ["capturedAtUtc"] = DateTime.UtcNow.ToString("O")
        };

        if (_provider == "whisper")
        {
            values["modelFile"] = _config.Transcription.WhisperModelFile;
            values["threads"] = _config.Transcription.WhisperThreads;
            values["language"] = "en";
            values["noContext"] = true;
            values["singleSegment"] = true;
        }
        else if (_provider == "faster-whisper")
        {
            values["model"] = _config.Transcription.FasterWhisperModel;
            values["device"] = _config.Transcription.FasterWhisperDevice;
            values["computeType"] = _config.Transcription.FasterWhisperComputeType;
            values["workers"] = _config.Transcription.FasterWhisperWorkers;
            values["vadFilter"] = _config.Transcription.FasterWhisperVadFilter;
            values["cpuThreads"] = _config.Transcription.WhisperThreads;
        }
        else if (_provider is "remote-faster-whisper" or "lmstudio" or "openai")
        {
            values["baseUrl"] = _config.Transcription.OpenAiBaseUrl;
        }

        if (response.HasValue && response.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in response.Value.EnumerateObject())
            {
                if (string.Equals(property.Name, "text", StringComparison.OrdinalIgnoreCase))
                    continue;
                values[property.Name] = JsonElementToObject(property.Value);
            }
        }

        return new TranscriptionMetadata(values);
    }

    private TranscriptionMetadata CreateTranscriptionFailureMetadata(Exception ex)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["provider"] = _provider,
            ["model"] = ResolveConfiguredModelName(),
            ["capturedAtUtc"] = DateTime.UtcNow.ToString("O"),
            ["status"] = "failed",
            ["errorType"] = ex.GetType().Name,
            ["errorMessage"] = ex.Message
        };

        if (_provider is "remote-faster-whisper" or "lmstudio" or "openai")
            values["baseUrl"] = _config.Transcription.OpenAiBaseUrl;

        return new TranscriptionMetadata(values);
    }

    private string ResolveConfiguredModelName()
    {
        if (_provider == "whisper")
            return Path.GetFileNameWithoutExtension(_config.Transcription.WhisperModelFile ?? string.Empty);
        if (_provider == "faster-whisper")
            return _config.Transcription.FasterWhisperModel ?? string.Empty;
        if (_provider is "remote-faster-whisper" or "lmstudio" or "openai")
            return string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel) ? "whisper-1" : _config.Transcription.OpenAiModel;
        return string.Empty;
    }

    private static object? JsonElementToObject(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => JsonSerializer.Deserialize<object>(element.GetRawText())
        };

    private static string MergeTranscriptionMetadata(string rawMetadataJson, TranscriptionMetadata metadata)
    {
        Dictionary<string, object?> root;
        try
        {
            root = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawMetadataJson) ?? [];
        }
        catch (JsonException)
        {
            root = [];
        }

        root["transcription"] = metadata.Values;
        return JsonSerializer.Serialize(root);
    }

    private async Task<string> PersistAudioAsync(CallstreamPayload payload, EngineCall call, long callId, CancellationToken ct)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).UtcDateTime;
        var relativeDir = Path.Combine(date.Year.ToString("0000"), date.Month.ToString("00"), date.Day.ToString("00"));
        var absoluteDir = Path.Combine(_config.Storage.AudioRoot, relativeDir);
        Directory.CreateDirectory(absoluteDir);

        var fileName = $"{call.StartTime}-{call.SystemShortName}-{call.Talkgroup}-{call.CallstreamCallId}-{callId}.wav";
        var absolutePath = Path.Combine(absoluteDir, SanitizeFileName(fileName));
        using var wav = _audio.CreateWavStream(payload.PcmS16Le, payload.SampleRate);
        await File.WriteAllBytesAsync(absolutePath, wav.ToArray(), ct);
        return Path.GetRelativePath(_config.Storage.AudioRoot, absolutePath).Replace('\\', '/');
    }

    private EngineCall BuildPendingCall(CallstreamPayload payload, bool imported)
    {
        var metadata = payload.Metadata;
        var start = metadata.StartTime;
        var stop = metadata.StopTime;
        var system = metadata.SystemShortName;
        var talkgroup = metadata.Talkgroup;
        var callstreamCallId = metadata.CallId;
        var source = metadata.Source;
        var frequency = metadata.Frequency;
        var resolved = _talkgroups.Resolve(system, talkgroup);
        var unique = $"{system}|{talkgroup}|{start}|{stop}|{callstreamCallId}|{frequency}";
        return new EngineCall
        {
            UniqueKey = unique,
            StartTime = start,
            StopTime = stop,
            Source = source,
            SystemShortName = system,
            CallstreamCallId = callstreamCallId,
            Talkgroup = talkgroup,
            TalkgroupName = resolved.Label,
            Frequency = frequency,
            Category = resolved.Category,
            TranscriptionStatus = "pending",
            IsImported = imported,
            RawMetadataJson = payload.RawMetadataJson
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '-');
        return fileName;
    }

    private WhisperTranscriber CreateWhisperTranscriber(string? modelPath = null) =>
        new(string.IsNullOrWhiteSpace(modelPath) ? _config.Transcription.WhisperModelFile : modelPath, _config.Transcription.WhisperThreads, _logger);

    private string ResolveWhisperModelPath(string model)
    {
        var dir = Path.Combine(_config.Storage.AppDataRoot, "pizzawave", "model");
        var candidate = Path.Combine(dir, $"ggml-{model}.bin");
        return File.Exists(candidate) ? candidate : _config.Transcription.WhisperModelFile;
    }

    private static int ResolveBacklogWorkerCount()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("PIZZAWAVE_BACKLOG_WORKERS"), out var configured))
            return Math.Clamp(configured, 0, 4);

        if (Environment.ProcessorCount <= 4)
            return 0;
        return 1;
    }

    private static int ResolveLiveWorkerCount(EngineConfig config)
    {
        var configured = config.Transcription.LiveTranscriptionWorkers;
        return Math.Clamp(configured <= 0 ? 1 : configured, 1, 4);
    }

    private bool IsDeferredTalkgroup(long talkgroup) =>
        _config.Transcription.DeferredTalkgroups.Any(tg => tg == talkgroup);

    private sealed record TranscriptionQueueItem(long CallId, CallstreamPayload? Raw, string AudioPath, bool Imported, bool SuppressDownstream, TranscriptionWorkKind Kind);
    private sealed record BacklogTranscriberSet(int WorkerId, ITranscriber Fast, ITranscriber Primary, string FastModel, string PrimaryModel);
    private sealed record TranscriptionPerformanceSample(DateTimeOffset CompletedAt, double WallSeconds, double AudioSeconds, bool Backlog);
    private enum TranscriptionWorkKind { Live, DeferredLive, Backlog }
}

public sealed record TranscriptionPerformanceSnapshot(
    int Count,
    int LiveCount,
    int BacklogCount,
    double AverageWallSeconds,
    double AverageAudioSeconds,
    double AverageRealtimeFactor);
