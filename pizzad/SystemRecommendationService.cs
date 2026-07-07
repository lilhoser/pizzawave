using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class SystemRecommendationService
{
    private static readonly Regex RetuneTargetRegex = new(
        @"\[(?<scope>[^\]]+)\]\s+Retuning to Control Channel:\s+(?<freq>\d+(?:\.\d+)?)\s+MHz",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] BaselineEligibleRecommendationIds =
    [
        "tr-decode-zero",
        "tr-retunes",
        "tr-resource-pressure"
    ];
    private static readonly TimeSpan BaselineDemotionAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan RecommendationCacheTtl = TimeSpan.FromSeconds(30);
    private const long CriticalRemoteBandwidthBytesPerDay = 1_000_000_000;

    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EnginePipeline _pipeline;
    private readonly IngestControlService _ingestControl;
    private readonly LiveTrActivityMonitor _liveTrActivity;
    private readonly TalkgroupCatalogService _catalog;
    private readonly RemoteBandwidthEstimatorService _bandwidth;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private SystemRecommendationsDto? _cachedRecommendations;
    private DateTimeOffset _cachedRecommendationsAt = DateTimeOffset.MinValue;
    private Task<SystemRecommendationsDto>? _recommendationBuildTask;

    public SystemRecommendationService(EngineConfig config, EngineDatabase database, EnginePipeline pipeline, IngestControlService ingestControl, LiveTrActivityMonitor liveTrActivity, TalkgroupCatalogService catalog, RemoteBandwidthEstimatorService bandwidth)
    {
        _config = config;
        _database = database;
        _pipeline = pipeline;
        _ingestControl = ingestControl;
        _liveTrActivity = liveTrActivity;
        _catalog = catalog;
        _bandwidth = bandwidth;
    }

    public async Task<SystemRecommendationsDto> BuildAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Task<SystemRecommendationsDto> buildTask;
        await _cacheGate.WaitAsync(ct);
        try
        {
            if (_cachedRecommendations != null && now - _cachedRecommendationsAt <= RecommendationCacheTtl)
                return _cachedRecommendations;

            if (_recommendationBuildTask is { IsCompleted: false } running)
            {
                buildTask = running;
            }
            else
            {
                _recommendationBuildTask = BuildCoreAsync(CancellationToken.None);
                buildTask = _recommendationBuildTask;
            }
        }
        finally
        {
            _cacheGate.Release();
        }

        var result = await buildTask.WaitAsync(ct);
        await _cacheGate.WaitAsync(ct);
        try
        {
            if (ReferenceEquals(_recommendationBuildTask, buildTask))
            {
                _cachedRecommendations = result;
                _cachedRecommendationsAt = DateTimeOffset.UtcNow;
                _recommendationBuildTask = null;
            }
        }
        finally
        {
            _cacheGate.Release();
        }

        return result;
    }

    private async Task<SystemRecommendationsDto> BuildCoreAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        const int windowMinutes = 60;
        var startUnix = new DateTimeOffset(now.AddMinutes(-windowMinutes)).ToUnixTimeSeconds();
        var tenMinuteStartUnix = new DateTimeOffset(now.AddMinutes(-10)).ToUnixTimeSeconds();
        var rows = new List<SystemRecommendationDto>();
        var states = await _database.ListRecommendationStatesAsync(ct);

        var recentAudioIn = await _database.SumAudioSecondsStartedSinceAsync(tenMinuteStartUnix, ct);
        var recentAudioOut = await _database.SumAudioSecondsTranscriptionCompletionsSinceAsync(now.AddMinutes(-10), ct);
        var pendingAudio = await _database.SumPendingTranscriptionAudioSecondsAsync(ct);
        var pendingCalls = await _database.CountPendingTranscriptionCallsAsync(ct);
        var audioInPerMinute = recentAudioIn / 10d;
        var audioOutPerMinute = recentAudioOut / 10d;
        var audioGrowthPerMinute = audioInPerMinute - audioOutPerMinute;
        var hasActiveQueuePressure = pendingAudio >= 5 * 60
            || audioGrowthPerMinute >= 15
            || (_config.Transcription.LivePressureQueueDepth > 0 && _pipeline.QueueDepth >= _config.Transcription.LivePressureQueueDepth);
        var ingest = _ingestControl.GetStatus(_pipeline.QueueDepth);
        var queueDiagnostics = new List<RecommendationDiagnosticDto>
        {
            new("Audio in", $"{audioInPerMinute:F0}s/min", audioGrowthPerMinute >= 15 && pendingAudio >= 5 * 60 ? "issue" : "ok", "Recent live/imported audio entering transcription over the last 10 minutes."),
            new("Audio out", $"{audioOutPerMinute:F0}s/min", audioGrowthPerMinute >= 15 && pendingAudio >= 5 * 60 ? "issue" : "ok", "Recent completed transcription audio over the last 10 minutes."),
            new("Pending audio", FormatDuration(pendingAudio), pendingAudio > 10 * 60 ? "issue" : "ok", $"{pendingCalls:N0} call(s) currently pending transcription."),
            new("Workers", $"{_config.Transcription.LiveTranscriptionWorkers} x {_config.Transcription.WhisperThreads}", "info", "Configured live transcription workers x Whisper threads per worker."),
            new("Queue limit for AI", $"{_config.AiInsights.MaxQueueDepthForManualSummary:N0}", _pipeline.QueueDepth > _config.AiInsights.MaxQueueDepthForManualSummary && _config.AiInsights.MaxQueueDepthForManualSummary > 0 ? "issue" : "ok", "Manual AI summary work is blocked above this queue depth when the limit is enabled.")
        };

        if (pendingAudio >= 5 * 60 && audioGrowthPerMinute >= 15)
        {
            rows.Add(new SystemRecommendationDto(
                "queue-audio-pressure",
                "pizzad",
                "high",
                "Transcription queue is growing by audio duration",
                $"The last 10 minutes ingested about {audioInPerMinute:F0}s audio/min and transcribed about {audioOutPerMinute:F0}s audio/min, a net growth of {audioGrowthPerMinute:F0}s/min. Pending audio is {FormatDuration(pendingAudio)}.",
                "Review queue health and high-load talkgroups before increasing worker count.",
                new RecommendationTargetDto("pizzad", "jobs", ""),
                [])
                { Runbook = BuildRunbook("queue-audio-pressure") with { Diagnostics = queueDiagnostics } });
        }
        else if (pendingAudio > 10 * 60)
        {
            rows.Add(new SystemRecommendationDto(
                "queue-drain-watch",
                "pizzad",
                "medium",
                "Transcription queue still has meaningful pending audio",
                $"Pending transcription audio is {FormatDuration(pendingAudio)}. Current audio throughput is {audioOutPerMinute:F0}s/min out vs {audioInPerMinute:F0}s/min in.",
                "Let the queue drain before starting manual AI summary generation.",
                new RecommendationTargetDto("pizzad", "jobs", ""),
                [])
                { Runbook = BuildRunbook("queue-drain-watch") with { Diagnostics = queueDiagnostics } });
        }

        if (ingest.Paused)
        {
            rows.Add(new SystemRecommendationDto(
                "ingest-paused",
                "pizzad",
                "high",
                "Live ingest is paused",
                $"New live callstream payloads are being dropped. Dropped this pause: {ingest.DroppedCallsThisPause:N0}; total since service start: {ingest.DroppedCalls:N0}.",
                ingest.UntilQueueClear ? "Wait for auto-resume when the queue clears, or resume manually if situational awareness is more important than backlog recovery." : "Resume ingest when ready.",
                new RecommendationTargetDto("pizzad", "service", ""),
                [])
                {
                    Runbook = BuildRunbook("ingest-paused") with
                    {
                        Diagnostics =
                        [
                            new("Paused", ingest.Paused ? "yes" : "no", ingest.Paused ? "issue" : "ok", ingest.Reason),
                            new("Auto resume", ingest.UntilQueueClear ? "queue clear" : "manual", ingest.UntilQueueClear ? "info" : "issue", "Whether ingest resumes automatically after queue recovery."),
                            new("Dropped this pause", $"{ingest.DroppedCallsThisPause:N0}", ingest.DroppedCallsThisPause > 0 ? "issue" : "ok", "Live payloads dropped during the current pause window."),
                            new("Queue depth", $"{_pipeline.QueueDepth:N0}", _pipeline.QueueDepth > 0 ? "issue" : "ok", "Current in-memory transcription queue depth.")
                        ]
                    }
                });
        }

        var bandwidthEndUnix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var bandwidthStartUnix = new DateTimeOffset(now.AddHours(-24)).ToUnixTimeSeconds();
        var bandwidth = await _bandwidth.BuildUsageSnapshotAsync(bandwidthStartUnix, bandwidthEndUnix, ct);
        var remoteTranscription = bandwidth.ByActivity.FirstOrDefault(row => row.Activity.Equals("remote transcription", StringComparison.OrdinalIgnoreCase));
        var remoteTranscriptionBytes = remoteTranscription?.TotalBytes ?? 0;
        if (bandwidth.TranscriptionIncluded && remoteTranscriptionBytes >= CriticalRemoteBandwidthBytesPerDay)
        {
            var diagnostics = new List<RecommendationDiagnosticDto>
            {
                new("24h remote transcription", FormatBytes(remoteTranscriptionBytes), "issue", $"{remoteTranscription?.Requests ?? 0:N0} remote transcription upload(s) estimated in the last 24 hours."),
                new("Request bytes", FormatBytes(remoteTranscription?.RequestBytes ?? 0), "issue", "Estimated outbound audio upload volume to the remote transcription endpoint."),
                new("Response bytes", FormatBytes(remoteTranscription?.ResponseBytes ?? 0), "info", "Estimated response payload volume from the remote transcription endpoint."),
                new("Endpoint", bandwidth.TranscriptionEndpoint, "issue", $"Remote host: {bandwidth.RemoteHost}."),
                new("Estimate basis", "derived", "info", bandwidth.Notes)
            };

            rows.Add(new SystemRecommendationDto(
                "remote-transcription-bandwidth-critical",
                "network",
                "critical",
                "Remote transcription bandwidth is critically high",
                $"Remote transcription is estimated at {FormatBytes(remoteTranscriptionBytes)} in the last 24 hours. On a metered or QoS-limited link, this can starve LM Link, remote transcription, and normal operator access.",
                ingest.Paused ? "Live ingest is already paused. Remedy the transcription routing or bandwidth policy before resuming." : "Pause ingest indefinitely if the link is constrained, then switch transcription local/deferred or reduce captured call volume before resuming.",
                new RecommendationTargetDto("metrics", "bandwidth", ""),
                ingest.Paused
                    ? []
                    : [new RecommendationActionDto("pause-ingest", "Pause ingest indefinitely", "Drop new live callstream payloads until an operator manually resumes ingest.")])
                { Runbook = BuildRunbook("remote-transcription-bandwidth-critical") with { Diagnostics = diagnostics } });
        }

        var fault = TrServiceFaultReader.ReadLatest();
        var controlState = TrServiceControlStateReader.ReadLatest();
        var liveTrActivity = _liveTrActivity.GetStatus(now, fault, controlState);
        if (_config.Setup.Completed && liveTrActivity.Stale && !ingest.Paused)
        {
            var diagnostics = new List<RecommendationDiagnosticDto>
            {
                new("Last live TR data", liveTrActivity.LastActivityUtc?.ToString("O") ?? "none", "issue", liveTrActivity.Message),
                new("Last callstream call", liveTrActivity.LastLiveCallUtc?.ToString("O") ?? "none", liveTrActivity.LastLiveCallUtc.HasValue ? "info" : "issue", "Most recent non-imported callstream payload accepted by PizzaWave."),
                new("Last TR health sample", liveTrActivity.LastTrHealthUtc?.ToString("O") ?? "none", liveTrActivity.LastTrHealthUtc.HasValue ? "info" : "issue", "Most recent trunk-recorder health window observed by PizzaWave."),
                new("Silence threshold", $"{liveTrActivity.ThresholdSeconds:N0}s", "info", "PizzaWave marks live TR capture stale when neither callstream nor TR health data arrives within this window.")
            };
            if (fault != null)
            {
                diagnostics.Add(new("Latest service fault", fault.CreatedAtUtc.ToString("O"), "issue", $"systemd result={fault.ServiceResult}; exit={fault.ExitCode}/{fault.ExitStatus}."));
                if (fault.Signatures.Count > 0)
                    diagnostics.Add(new("Fault signatures", string.Join(", ", fault.Signatures.Take(6)), "issue", "Patterns found in the recent trunk-recorder journal tail."));
            }

            rows.Add(new SystemRecommendationDto(
                "tr-live-silent",
                "trunk-recorder",
                "high",
                "Live TR data has gone stale",
                liveTrActivity.Message,
                "Open System > Services and troubleshoot trunk-recorder before assuming PizzaWave ingest or transcription is the bottleneck.",
                new RecommendationTargetDto("system", "services", ""),
                [])
                { Runbook = BuildRunbook("tr-live-silent") with { Diagnostics = diagnostics } });
        }

        if (_config.Embeddings.Enabled && !await QdrantReachableAsync(ct))
        {
            rows.Add(new SystemRecommendationDto(
                "qdrant-unreachable",
                "qdrant",
                "high",
                "Qdrant vector database is not reachable",
                $"Embeddings are enabled, but pizzad cannot reach Qdrant at {_config.Embeddings.QdrantBaseUrl}. Incident RAG will fall back to weaker non-vector matching until this is fixed.",
                "Open System > Services, check qdrant.service, and restart Qdrant if needed.",
                new RecommendationTargetDto("qdrant", "service", ""),
                []));
        }

        var topAudio = await _database.ListTopAudioTalkgroupsAsync(startUnix, 50, ct);
        var noisyTalkgroups = BuildTalkgroupNoiseCandidates(topAudio)
            .Where(t => _catalog.IsGloballyEnabled(t.SystemShortName, t.Talkgroup))
            .Take(8)
            .ToList();
        if (noisyTalkgroups.Count > 0)
        {
            var detail = string.Join("; ", noisyTalkgroups.Take(4).Select(t => $"{t.TalkgroupName} (score {t.Score:F0}, {t.Calls:N0} calls, weak {t.WeakPct:F0}%)"));
            rows.Add(new SystemRecommendationDto(
                "priority-low-value-audio",
                "pizzad",
                hasActiveQueuePressure ? "medium" : "low",
                "Talkgroup noise candidates need review",
                detail,
                "Review these candidates in System Recommendations, then exclude the noisy talkgroups from the generated TR CSV if they are low value for every profile.",
                new RecommendationTargetDto("system", "recommendations", "talkgroup-noise"),
                [new RecommendationActionDto(
                    "exclude-talkgroups-from-tr",
                    $"Exclude all ({noisyTalkgroups.Count:N0})",
                    "Disable all current noise candidates in the talkgroup catalog so regenerated TR CSVs omit them.",
                    noisyTalkgroups.Select(row => row.Talkgroup).Distinct().OrderBy(id => id).ToList())])
                {
                    Runbook = BuildRunbook("priority-low-value-audio") with
                    {
                        Diagnostics =
                        [
                            new("Candidate window", $"{windowMinutes} min", "info", "Recent audio-load window used to find candidates."),
                            new("Candidate count", $"{noisyTalkgroups.Count:N0}", noisyTalkgroups.Count > 0 ? "issue" : "ok", "Talkgroups matching the current noisy/low-yield heuristic."),
                            new("Already deferred", $"{noisyTalkgroups.Count(t => t.AlreadyDeferred):N0}", "info", "Candidates already present in the deferred queue policy."),
                            new("Queue pressure", hasActiveQueuePressure ? "active" : "not active", hasActiveQueuePressure ? "issue" : "info", "Queue pressure raises the priority, but candidates can be reviewed proactively.")
                        ],
                        TalkgroupCandidates = noisyTalkgroups
                    }
                });
        }

        const int rfCurrentWindowMinutes = 15;
        var rfCurrentStart = now.AddMinutes(-rfCurrentWindowMinutes);
        var healthStart = new DateTimeOffset(now.AddHours(-2)).ToUnixTimeSeconds();
        var health = await _database.ListHealthSamplesAsync(healthStart, new DateTimeOffset(now).ToUnixTimeSeconds(), ct);
        var global = health.Where(h => string.Equals(h.Scope, "global", StringComparison.OrdinalIgnoreCase)).ToList();
        var currentGlobal = global.Where(h => h.WindowEndUtc.ToUniversalTime() >= rfCurrentStart).ToList();
        var bySystemAll = health
            .Where(h => !string.Equals(h.Scope, "global", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(h.Scope))
            .GroupBy(h => h.Scope)
            .Select(g =>
            {
                var lines = g.Sum(h => h.CcSummaryDecodeLines);
                var zeros = g.Sum(h => h.CcSummaryDecodeZero);
                var warningLines = g.Sum(h => h.LowDecodeWarningLines);
                return new
                {
                    Scope = g.Key,
                    DecodeLines = lines,
                    DecodeZeroPct = zeros / Math.Max(1d, lines) * 100d,
                    LowDecodeWarnings = warningLines,
                    Retunes = g.Sum(h => h.Retunes),
                    CallsConcluded = g.Sum(h => h.CallsConcluded),
                    NoTxRecorded = g.Sum(h => h.NoTxRecorded)
                };
            })
            .ToList();
        var bySystem = bySystemAll
            .OrderByDescending(s => s.DecodeZeroPct)
            .ThenByDescending(s => s.Retunes)
            .Take(5)
            .ToList();
        var currentBySystemAll = health
            .Where(h => h.WindowEndUtc.ToUniversalTime() >= rfCurrentStart)
            .Where(h => !string.Equals(h.Scope, "global", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(h.Scope))
            .GroupBy(h => h.Scope)
            .Select(g =>
            {
                var lines = g.Sum(h => h.CcSummaryDecodeLines);
                var zeros = g.Sum(h => h.CcSummaryDecodeZero);
                var warningLines = g.Sum(h => h.LowDecodeWarningLines);
                return new
                {
                    Scope = g.Key,
                    DecodeLines = lines,
                    DecodeZeroPct = zeros / Math.Max(1d, lines) * 100d,
                    LowDecodeWarnings = warningLines,
                    Retunes = g.Sum(h => h.Retunes),
                    CallsConcluded = g.Sum(h => h.CallsConcluded),
                    NoTxRecorded = g.Sum(h => h.NoTxRecorded)
                };
            })
            .ToList();
        var currentBySystem = currentBySystemAll
            .OrderByDescending(s => s.DecodeZeroPct)
            .ThenByDescending(s => s.Retunes)
            .Take(5)
            .ToList();
        var decodeLines = global.Sum(h => h.CcSummaryDecodeLines);
        var decodeZero = global.Sum(h => h.CcSummaryDecodeZero);
        var lowDecodeWarnings = global.Sum(h => h.LowDecodeWarningLines);
        var retunes = global.Sum(h => h.Retunes);
        var currentDecodeLines = currentGlobal.Sum(h => h.CcSummaryDecodeLines);
        var currentDecodeZero = currentGlobal.Sum(h => h.CcSummaryDecodeZero);
        var currentLowDecodeWarnings = currentGlobal.Sum(h => h.LowDecodeWarningLines);
        var currentRetunes = currentGlobal.Sum(h => h.Retunes);
        var resourceRows = global.Where(h => h.TrCpuPercent > 0 || h.TrRssMb > 0 || h.HostTempC > 0 || h.HostLoad1 > 0).ToList();
        var latestResource = resourceRows.OrderByDescending(h => h.WindowEndUtc).FirstOrDefault();
        var maxTrCpu = resourceRows.Select(h => h.TrCpuPercent).DefaultIfEmpty(0).Max();
        var maxTrRssMb = resourceRows.Select(h => h.TrRssMb).DefaultIfEmpty(0).Max();
        var maxTrThreads = resourceRows.Select(h => h.TrThreadCount).DefaultIfEmpty(0).Max();
        var maxTempC = resourceRows.Select(h => h.HostTempC).DefaultIfEmpty(0).Max();
        var maxLoad1 = resourceRows.Select(h => h.HostLoad1).DefaultIfEmpty(0).Max();
        var currentTrCpu = latestResource?.TrCpuPercent ?? 0;
        var currentTrRssMb = latestResource?.TrRssMb ?? 0;
        var currentTrThreads = latestResource?.TrThreadCount ?? 0;
        var currentTempC = latestResource?.HostTempC ?? 0;
        var currentLoad1 = latestResource?.HostLoad1 ?? 0;
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var currentTrCpuHostPct = currentTrCpu / processorCount;
        var maxTrCpuHostPct = maxTrCpu / processorCount;
        var currentLoadHostPct = currentLoad1 / processorCount * 100d;
        var latestResourceAge = latestResource == null
            ? TimeSpan.MaxValue
            : now - latestResource.WindowEndUtc.ToUniversalTime();
        var resourceFreshnessLimit = TimeSpan.FromMinutes(Math.Max(10, _config.TrunkRecorder.HealthWindowMinutes * 3));
        var hasFreshResourceSample = latestResource != null && latestResourceAge <= resourceFreshnessLimit;
        var currentThrottled = latestResource != null && HasCurrentThrottleFlag(latestResource.HostThrottledFlags);
        var historicalThrottled = resourceRows.Any(h => HasHistoricalThrottleFlag(h.HostThrottledFlags));
        var currentDecodeZeroPct = currentDecodeZero / Math.Max(1d, currentDecodeLines) * 100d;
        var twoHourDecodeZeroPct = decodeZero / Math.Max(1d, decodeLines) * 100d;
        var trDiagnostics = new List<RecommendationDiagnosticDto>
        {
            new("Health window", "2 hr", "info", "Recent trunk-recorder health samples in the engine database."),
            new("Current RF window", $"{rfCurrentWindowMinutes} min", "info", "Short window used to decide whether RF recommendations are active right now."),
            new("Latest resource sample", latestResource == null ? "n/a" : $"{FormatDuration((long)Math.Max(0, latestResourceAge.TotalSeconds))} ago", hasFreshResourceSample ? "ok" : "issue", "Current resource/thermal recommendations require a fresh TR health sample."),
            new("Current CC summary zero", currentDecodeLines > 0 ? $"{currentDecodeZeroPct:F1}%" : "n/a", currentDecodeLines >= 3 && currentDecodeZeroPct >= 10 ? "issue" : "ok", $"{currentDecodeZero:N0} zero periodic CC summary sample(s) across {currentDecodeLines:N0} current sample(s)."),
            new("2h CC summary zero", decodeLines > 0 ? $"{twoHourDecodeZeroPct:F1}%" : "n/a", decodeLines > 0 && twoHourDecodeZeroPct >= 10 ? "issue" : "ok", $"{decodeZero:N0} zero periodic CC summary sample(s) across {decodeLines:N0} CC summary sample(s)."),
            new("Current CC message-rate samples", $"{currentLowDecodeWarnings:N0}", currentLowDecodeWarnings >= 5 ? "issue" : "ok", "Per-frequency Control Channel Message Decode Rate samples in the current RF window."),
            new("2h CC message-rate samples", $"{lowDecodeWarnings:N0}", lowDecodeWarnings >= 20 ? "issue" : "ok", "Per-frequency Control Channel Message Decode Rate samples in the two-hour context window."),
            new("Current retunes", $"{currentRetunes:N0}", currentRetunes >= 5 ? "issue" : "ok", "Control-channel retunes in the current RF window."),
            new("2h retunes", $"{retunes:N0}", retunes >= 20 ? "issue" : "ok", "Control-channel retunes in the two-hour context window."),
            new("TR CPU", latestResource == null ? "n/a" : $"{currentTrCpu:F0}% current ({currentTrCpuHostPct:F0}% of host), {maxTrCpu:F0}% max ({maxTrCpuHostPct:F0}% of host)", hasFreshResourceSample && currentTrCpuHostPct >= 75 ? "issue" : "ok", $"trunk-recorder process CPU percent from the latest health sample. 100% is one saturated core; this host reports {processorCount:N0} processor(s)."),
            new("TR RSS", latestResource == null ? "n/a" : $"{currentTrRssMb:F0} MB current, {maxTrRssMb:F0} MB max", hasFreshResourceSample && currentTrRssMb >= 1536 ? "issue" : "ok", "Resident memory used by trunk-recorder, not virtual address space."),
            new("TR threads", latestResource == null ? "n/a" : $"{currentTrThreads:N0} current, {maxTrThreads:N0} max", hasFreshResourceSample && currentTrThreads >= 250 ? "issue" : "ok", "Large thread counts usually mean many active GNU Radio/op25 recorder pipelines."),
            new("Host temp", latestResource == null ? "n/a" : $"{currentTempC:F1} C current, {maxTempC:F1} C max", hasFreshResourceSample && currentTempC >= 75 ? "issue" : "ok", "Current temperature controls this recommendation; max temperature is shown only as context."),
            new("Host load", latestResource == null ? "n/a" : $"{currentLoad1:F2} current ({currentLoadHostPct:F0}% of host), {maxLoad1:F2} max", hasFreshResourceSample && currentLoad1 >= processorCount * 1.5 ? "issue" : "ok", $"Latest 1-minute load average. This host reports {processorCount:N0} processor(s) to pizzad."),
            new("Throttle flags", latestResource?.HostThrottledFlags ?? "n/a", hasFreshResourceSample && currentThrottled ? "issue" : "ok", historicalThrottled ? "Current throttle bits drive severity; historical throttle bits are context only." : "Raspberry Pi vcgencmd current throttling flags from the latest health sample when available.")
        };
        trDiagnostics.AddRange(currentBySystem.Select(s => new RecommendationDiagnosticDto(
            $"Current {s.Scope}",
            $"{s.DecodeZeroPct:F1}% CC zero, {s.LowDecodeWarnings:N0} message-rate samples, {s.Retunes:N0} retunes",
            s.DecodeZeroPct >= 10 || s.LowDecodeWarnings >= 5 || s.Retunes >= 5 ? "issue" : "ok",
            $"{s.CallsConcluded:N0} concluded call(s), {s.NoTxRecorded:N0} no-transmission result(s), {s.DecodeLines:N0} current CC summary sample(s).")));
        trDiagnostics.AddRange(bySystem.Select(s => new RecommendationDiagnosticDto(
            $"2h {s.Scope}",
            $"{s.DecodeZeroPct:F1}% CC zero, {s.LowDecodeWarnings:N0} message-rate samples, {s.Retunes:N0} retunes",
            s.DecodeZeroPct >= 10 || s.LowDecodeWarnings >= 20 || s.Retunes >= 20 ? "issue" : "ok",
            $"{s.CallsConcluded:N0} concluded call(s), {s.NoTxRecorded:N0} no-transmission result(s), {s.DecodeLines:N0} CC summary sample(s).")));
        if (currentDecodeLines >= 3)
        {
            if (currentDecodeZeroPct >= 10)
            {
                rows.Add(new SystemRecommendationDto(
                    "tr-decode-zero",
                    "trunk-recorder",
                    "high",
                    "TR CC summary decode-zero rate is high",
                    $"Global periodic control-channel summary decode-zero rate over the last {rfCurrentWindowMinutes} minutes is about {currentDecodeZeroPct:F1}% across {currentDecodeLines:N0} samples. The two-hour context is {twoHourDecodeZeroPct:F1}% across {decodeLines:N0} samples.",
                    "Inspect System > Metrics > RF to identify whether this is isolated to one monitored system/source.",
                    new RecommendationTargetDto("tr", "metrics", ""),
                    [])
                    { Runbook = BuildRunbook("tr-decode-zero") with { Diagnostics = trDiagnostics } });
            }
        }
        if (currentRetunes >= 5)
        {
            var worstCurrentRetuneSystem = currentBySystemAll
                .OrderByDescending(s => s.Retunes)
                .ThenByDescending(s => s.DecodeZeroPct)
                .FirstOrDefault(s => s.Retunes > 0);
            var worstTwoHourRetuneSystem = bySystemAll
                .OrderByDescending(s => s.Retunes)
                .ThenByDescending(s => s.DecodeZeroPct)
                .FirstOrDefault(s => s.Retunes > 0);
            var retuneTargets = await BuildRetuneTargetDiagnosticsAsync(rfCurrentStart, now, ct);
            var scopedRetuneTargets = worstCurrentRetuneSystem == null
                ? retuneTargets
                : retuneTargets.Where(t => string.Equals(t.Scope, worstCurrentRetuneSystem.Scope, StringComparison.OrdinalIgnoreCase)).ToList();
            var retuneTargetText = FormatRetuneTargetSummary(scopedRetuneTargets.Count > 0 ? scopedRetuneTargets : retuneTargets);
            var retuneDetail = worstCurrentRetuneSystem == null
                ? $"Observed {currentRetunes:N0} control-channel retunes over the last {rfCurrentWindowMinutes} minutes. The two-hour context is {retunes:N0} retunes."
                : $"Observed {currentRetunes:N0} control-channel retunes over the last {rfCurrentWindowMinutes} minutes; {worstCurrentRetuneSystem.Scope} accounts for {worstCurrentRetuneSystem.Retunes:N0} of them. The two-hour context is {retunes:N0} retunes"
                    + (worstTwoHourRetuneSystem == null ? "." : $", led by {worstTwoHourRetuneSystem.Scope} with {worstTwoHourRetuneSystem.Retunes:N0}.")
                    + (string.IsNullOrWhiteSpace(retuneTargetText) ? string.Empty : $" Recent retune targets: {retuneTargetText}.");
            var retuneAction = worstCurrentRetuneSystem == null
                ? "Compare retunes with decode-zero rate and recent gain/error changes before locking control channels."
                : $"Focus on {worstCurrentRetuneSystem.Scope}: validate the listed control-channel targets and source RF settings before changing healthy systems.";
            var retuneDiagnostics = retuneTargets.Count == 0
                ? trDiagnostics
                : trDiagnostics.Concat(retuneTargets.Select(t => new RecommendationDiagnosticDto(
                    $"Retune target {t.Scope}",
                    $"{t.FrequencyMHz} MHz x {t.Count:N0}",
                    t.Scope.Equals(worstCurrentRetuneSystem?.Scope ?? string.Empty, StringComparison.OrdinalIgnoreCase) ? "issue" : "info",
                    $"Observed in the last {rfCurrentWindowMinutes} minutes of trunk-recorder journald."))).ToList();
            rows.Add(new SystemRecommendationDto(
                "tr-retunes",
                "trunk-recorder",
                "medium",
                worstCurrentRetuneSystem == null ? "TR control-channel retunes are elevated" : $"{worstCurrentRetuneSystem.Scope} control-channel retunes are elevated",
                retuneDetail,
                retuneAction,
                new RecommendationTargetDto("tr", "metrics", ""),
                [])
                { Runbook = BuildRunbook("tr-retunes") with { Diagnostics = retuneDiagnostics } });
        }
        var currentResourcePressure = hasFreshResourceSample && (currentTrCpuHostPct >= 75
            || currentTrRssMb >= 1536
            || currentTempC >= 75
            || currentLoad1 >= processorCount * 1.5
            || currentThrottled);
        if (resourceRows.Count > 0 && currentResourcePressure)
        {
            var highCurrentPressure = currentTempC >= 80 || currentThrottled || currentTrCpuHostPct >= 90;
            rows.Add(new SystemRecommendationDto(
                "tr-resource-pressure",
                "trunk-recorder",
                highCurrentPressure ? "high" : "medium",
                "TR resource or thermal pressure is high",
                $"Latest TR health sample shows TR CPU {currentTrCpu:F0}% ({currentTrCpuHostPct:F0}% of host), load {currentLoad1:F2} ({currentLoadHostPct:F0}% of host), TR RSS {currentTrRssMb:F0} MB, {currentTrThreads:N0} TR threads, and host temp {currentTempC:F1} C. Two-hour peaks: CPU {maxTrCpu:F0}%, RSS {maxTrRssMb:F0} MB, temp {maxTempC:F1} C.",
                "Review System > CPU before adding capture load; reduce recorder count or low-value talkgroups if load or temperature stays elevated.",
                new RecommendationTargetDto("cpu", "", ""),
                [])
                { Runbook = BuildRunbook("tr-resource-pressure") with { Diagnostics = trDiagnostics } });
        }

        if (_pipeline.QueueDepth > _config.AiInsights.MaxQueueDepthForManualSummary && _config.AiInsights.MaxQueueDepthForManualSummary > 0)
        {
            rows.Add(new SystemRecommendationDto(
                "ai-blocked-queue",
                "ai",
                "low",
                "AI summary work is blocked by queue depth",
                $"Queue depth is {_pipeline.QueueDepth:N0}; configured AI queue limit is {_config.AiInsights.MaxQueueDepthForManualSummary:N0}.",
                "This is expected while transcription is catching up. Avoid raising this limit on constrained hosts unless live transcription is stable.",
                new RecommendationTargetDto("pizzad", "jobs", ""),
                [])
                { Runbook = BuildRunbook("ai-blocked-queue") with { Diagnostics = queueDiagnostics } });
        }

        var aiRecentWindowMinutes = 30;
        var aiImmediateWindowMinutes = 10;
        var aiRecentUsage = await _database.GetTokenUsageAsync(
            new DateTimeOffset(now.AddMinutes(-aiRecentWindowMinutes)).ToUnixTimeSeconds(),
            new DateTimeOffset(now).ToUnixTimeSeconds(),
            ct);
        var aiImmediateUsage = await _database.GetTokenUsageAsync(
            new DateTimeOffset(now.AddMinutes(-aiImmediateWindowMinutes)).ToUnixTimeSeconds(),
            new DateTimeOffset(now).ToUnixTimeSeconds(),
            ct);
        var aiUsage = await _database.GetTokenUsageAsync(
            new DateTimeOffset(now.AddHours(-24)).ToUnixTimeSeconds(),
            new DateTimeOffset(now).ToUnixTimeSeconds(),
            ct);
        var recentTruncationRate = aiRecentUsage.Summary.Requests <= 0 ? 0 : aiRecentUsage.Summary.Truncated / (double)aiRecentUsage.Summary.Requests * 100d;
        var immediateTruncationRate = aiImmediateUsage.Summary.Requests <= 0 ? 0 : aiImmediateUsage.Summary.Truncated / (double)aiImmediateUsage.Summary.Requests * 100d;
        var dailyTruncationRate = aiUsage.Summary.Requests <= 0 ? 0 : aiUsage.Summary.Truncated / (double)aiUsage.Summary.Requests * 100d;
        var activeAiTruncation = aiRecentUsage.Summary.Truncated >= 3 && recentTruncationRate >= 10;
        var immediateAiTruncationSpike = aiImmediateUsage.Summary.Truncated >= 2 && immediateTruncationRate >= 25;
        var recentAiServiceFailures = aiRecentUsage.Summary.HttpOrOtherErrors + aiRecentUsage.Summary.TimeoutFailures + aiRecentUsage.Summary.NoValidResultFailures;
        var immediateAiServiceFailures = aiImmediateUsage.Summary.HttpOrOtherErrors + aiImmediateUsage.Summary.TimeoutFailures + aiImmediateUsage.Summary.NoValidResultFailures;
        var dailyAiServiceFailures = aiUsage.Summary.HttpOrOtherErrors + aiUsage.Summary.TimeoutFailures + aiUsage.Summary.NoValidResultFailures;
        var recentAiFailureRate = aiRecentUsage.Summary.Requests <= 0 ? 0 : recentAiServiceFailures / (double)aiRecentUsage.Summary.Requests * 100d;
        var immediateAiFailureRate = aiImmediateUsage.Summary.Requests <= 0 ? 0 : immediateAiServiceFailures / (double)aiImmediateUsage.Summary.Requests * 100d;
        var activeAiServiceFailure = recentAiServiceFailures >= 3 && recentAiFailureRate >= 15;
        var immediateAiServiceFailureSpike = immediateAiServiceFailures >= 2 && immediateAiFailureRate >= 25;
        if (activeAiServiceFailure || immediateAiServiceFailureSpike)
        {
            var serviceFailureKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "completion-timeout",
                "no-valid-completion",
                "http-or-other"
            };
            var topFailure = aiRecentUsage.FailuresByKind.FirstOrDefault(f => serviceFailureKinds.Contains(f.Kind))
                ?? aiImmediateUsage.FailuresByKind.FirstOrDefault(f => serviceFailureKinds.Contains(f.Kind))
                ?? aiUsage.FailuresByKind.FirstOrDefault(f => serviceFailureKinds.Contains(f.Kind));
            var diagnostics = new List<RecommendationDiagnosticDto>
            {
                new("Current AI service failures", $"{recentAiServiceFailures:N0}", activeAiServiceFailure ? "issue" : "ok", $"{recentAiFailureRate:F1}% of AI requests in the last {aiRecentWindowMinutes} minutes failed for service/liveness reasons."),
                new("Current completion timeouts", $"{aiRecentUsage.Summary.TimeoutFailures:N0}", aiRecentUsage.Summary.TimeoutFailures > 0 ? "issue" : "ok", "Requests that reached the completion endpoint but did not return before the configured timeout."),
                new("Current no-valid-result failures", $"{aiRecentUsage.Summary.NoValidResultFailures:N0}", aiRecentUsage.Summary.NoValidResultFailures > 0 ? "issue" : "ok", "Requests that exited without usable completion tokens or a parseable completion result."),
                new("Immediate AI service failures", $"{immediateAiServiceFailures:N0}", immediateAiServiceFailureSpike ? "issue" : "ok", $"{immediateAiFailureRate:F1}% of AI requests in the last {aiImmediateWindowMinutes} minutes failed for service/liveness reasons."),
                new("24h AI service failures", $"{dailyAiServiceFailures:N0}", dailyAiServiceFailures > 0 ? "info" : "ok", "Historical context only; recovered failures do not keep this recommendation active."),
                new("Configured model", string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel) ? "unset" : _config.AiInsights.OpenAiModel, string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel) ? "issue" : "info", "Model ID used for AI Insights requests."),
                new("Endpoint", string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl) ? "unset" : _config.AiInsights.OpenAiBaseUrl, string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl) ? "issue" : "info", "OpenAI-compatible endpoint used by AI Insights.")
            };
            if (topFailure is not null)
                diagnostics.Add(new("Latest failure sample", $"{topFailure.Requests:N0} request(s)", "issue", topFailure.Example));

            rows.Add(new SystemRecommendationDto(
                "ai-service-failures",
                "ai",
                immediateAiServiceFailureSpike ? "high" : "medium",
                "AI completions are not returning valid results",
                $"The last {aiRecentWindowMinutes} minutes recorded {recentAiServiceFailures:N0} AI service/liveness failure(s), {recentAiFailureRate:F1}% of current AI calls. This includes {aiRecentUsage.Summary.TimeoutFailures:N0} timeout(s) and {aiRecentUsage.Summary.NoValidResultFailures:N0} no-valid-result failure(s). Queue backlog can be zero while LM Studio/LMLink is still not producing valid completions.",
                "Probe the configured completion endpoint with a small chat completion, then check LM Studio/LM Link model load, generation state, and routing before changing incident prompts.",
                new RecommendationTargetDto("metrics", "ai", ""),
                [])
                { Runbook = BuildRunbook("ai-service-failures") with { Diagnostics = diagnostics } });
        }
        if (activeAiTruncation || immediateAiTruncationSpike)
        {
            var topFailure = aiRecentUsage.FailuresByKind.FirstOrDefault() ?? aiUsage.FailuresByKind.FirstOrDefault();
            var diagnostics = new List<RecommendationDiagnosticDto>
            {
                new("Current truncations", $"{aiRecentUsage.Summary.Truncated:N0}", activeAiTruncation ? "issue" : "ok", $"{recentTruncationRate:F1}% of AI requests in the last {aiRecentWindowMinutes} minutes ended at the model output limit."),
                new("Immediate truncations", $"{aiImmediateUsage.Summary.Truncated:N0}", immediateAiTruncationSpike ? "issue" : "ok", $"{immediateTruncationRate:F1}% of AI requests in the last {aiImmediateWindowMinutes} minutes ended at the model output limit."),
                new("24h truncations", $"{aiUsage.Summary.Truncated:N0}", dailyTruncationRate >= 5 ? "info" : "ok", $"{dailyTruncationRate:F1}% of AI requests in the last 24 hours ended at the model output limit. This is context only and does not keep the recommendation active by itself."),
                new("Current service/liveness AI failures", $"{recentAiServiceFailures:N0}", recentAiServiceFailures > 0 ? "issue" : "ok", $"Timeout, no-valid-result, HTTP, parsing, and non-truncation failures in the last {aiRecentWindowMinutes} minutes."),
                new("24h total tokens", $"{aiUsage.Summary.TotalTokens:N0}", "info", "Recorded token volume for the 24-hour context window.")
            };
            if (topFailure is not null)
                diagnostics.Add(new("Top failure class", $"{topFailure.Kind} ({topFailure.Requests:N0})", topFailure.Kind == "truncated" ? "issue" : "info", topFailure.Example));

            rows.Add(new SystemRecommendationDto(
                "ai-truncation-pressure",
                "ai",
                recentAiServiceFailures > 0 || immediateAiTruncationSpike ? "high" : "medium",
                "AI incident generation is hitting output limits",
                $"The last {aiRecentWindowMinutes} minutes recorded {aiRecentUsage.Summary.Truncated:N0} truncated AI request(s), {recentTruncationRate:F1}% of current AI calls. The 24-hour context is {aiUsage.Summary.Truncated:N0} truncation(s), but recovered historical spikes no longer keep this recommendation active.",
                "Reduce incident prompt/output pressure before increasing max_tokens or adding broad retries.",
                new RecommendationTargetDto("metrics", "ai", ""),
                [])
                { Runbook = BuildRunbook("ai-truncation-pressure") with { Diagnostics = diagnostics } });
        }

        var baselines = await _database.UpdateRecommendationBaselinesAsync(
            rows.Where(r => BaselineEligibleRecommendationIds.Contains(r.Id, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(r => r.Id, BaselineValueFor, StringComparer.OrdinalIgnoreCase),
            BaselineEligibleRecommendationIds,
            now,
            ct);
        rows = rows.Select(r => ApplyBaseline(r, baselines, now)).ToList();

        var active = rows
            .Where(r => !IsSuppressed(r.Id, states, now))
            .OrderByDescending(r => SeverityRank(r.Severity))
            .ThenBy(r => r.Section)
            .ThenBy(r => r.Title)
            .Select(r => r.Runbook is null ? r with { Runbook = BuildRunbook(r.Id) } : r)
            .ToList();
        var ignored = rows
            .Where(r => IsIgnored(r.Id, states))
            .OrderByDescending(r => SeverityRank(r.Severity))
            .ThenBy(r => r.Section)
            .ThenBy(r => r.Title)
            .Select(r => r.Runbook is null ? r with { Runbook = BuildRunbook(r.Id) } : r)
            .ToList();

        return new SystemRecommendationsDto(
            active.Count(r => r.Severity is "critical" or "high" or "medium"),
            active.Count(r => r.Severity is "critical" or "high"),
            active.Count(r => r.Severity == "medium"),
            active.Count(r => r.Severity == "low"),
            active,
            ignored);
    }

    private async Task<bool> QdrantReachableAsync(CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            if (!string.IsNullOrWhiteSpace(_config.Embeddings.QdrantApiKey))
                client.DefaultRequestHeaders.Add("api-key", _config.Embeddings.QdrantApiKey);
            using var response = await client.GetAsync($"{_config.Embeddings.QdrantBaseUrl.TrimEnd('/')}/collections", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task SetStateAsync(string recommendationId, string action, CancellationToken ct)
    {
        switch (action)
        {
            case "ignore":
            case "dismiss":
                await _database.SaveRecommendationStateAsync(recommendationId, null, ct);
                break;
            case "restore":
            case "clear":
                await _database.ClearRecommendationStateAsync(recommendationId, ct);
                break;
            case "reset-baseline":
                await _database.ClearRecommendationBaselineAsync(recommendationId, ct);
                break;
            case "snooze":
                await _database.SaveRecommendationStateAsync(recommendationId, DateTime.UtcNow.AddHours(24), ct);
                break;
            default:
                throw new InvalidOperationException("Unknown recommendation state action.");
        }
    }

    private static bool IsIgnored(string id, Dictionary<string, DateTime?> states)
    {
        return states.TryGetValue(id, out var snoozedUntil) && snoozedUntil is null;
    }

    private static bool IsSuppressed(string id, Dictionary<string, DateTime?> states, DateTime now)
    {
        if (!states.TryGetValue(id, out var snoozedUntil))
            return false;
        if (snoozedUntil is null)
            return true;
        return snoozedUntil.Value > now;
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 4,
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    private static SystemRecommendationDto ApplyBaseline(SystemRecommendationDto recommendation, IReadOnlyDictionary<string, RecommendationBaselineDto> baselines, DateTime now)
    {
        if (!baselines.TryGetValue(recommendation.Id, out var baseline))
            return recommendation;

        var age = now - baseline.FirstSeenUtc;
        if (age < BaselineDemotionAge || recommendation.Severity == "low")
            return recommendation with { Baseline = BaselineInfo(baseline, false, recommendation.Severity, now) };

        var detail = $"{recommendation.Detail} This recommendation has persisted for {FormatDuration((long)age.TotalSeconds)} and is currently treated as a local baseline; reset the baseline if conditions changed or you want it to regain normal priority.";
        return recommendation with
        {
            Severity = "low",
            Detail = detail,
            Baseline = BaselineInfo(baseline, true, recommendation.Severity, now),
            Runbook = recommendation.Runbook is null
                ? null
                : recommendation.Runbook with
                {
                    Diagnostics = recommendation.Runbook.Diagnostics.Concat(
                    [
                        new RecommendationDiagnosticDto(
                            "Baseline",
                            baseline.BaselineValue,
                            "info",
                            $"First observed {baseline.FirstSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm}; last observed {baseline.LastSeenUtc.ToLocalTime():yyyy-MM-dd HH:mm}. Reset the baseline to restore normal priority.")
                    ]).ToList()
                }
        };
    }

    private static RecommendationBaselineInfoDto BaselineInfo(RecommendationBaselineDto baseline, bool priorityDemoted, string originalSeverity, DateTime now) =>
        new(
            baseline.FirstSeenUtc,
            baseline.LastSeenUtc,
            baseline.BaselineValue,
            baseline.ActiveObservations,
            Math.Max(0, (now - baseline.FirstSeenUtc).TotalHours),
            priorityDemoted,
            originalSeverity);

    private static string BaselineValueFor(SystemRecommendationDto recommendation) => recommendation.Id switch
    {
        "tr-decode-zero" => ExtractBaselineValue(recommendation.Detail, "Global periodic control-channel summary decode-zero rate "),
        "tr-retunes" => ExtractBaselineValue(recommendation.Detail, "Observed "),
        "tr-resource-pressure" => ExtractBaselineValue(recommendation.Detail, "Latest TR health sample shows "),
        _ => recommendation.Title
    };

    private static string ExtractBaselineValue(string detail, string prefix)
    {
        if (detail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            detail = detail[prefix.Length..];
        var sentenceEnd = detail.IndexOf(". ", StringComparison.Ordinal);
        return (sentenceEnd >= 0 ? detail[..sentenceEnd] : detail).Trim();
    }

    private static bool HasCurrentThrottleFlag(string flags)
    {
        return TryParseThrottleFlags(flags, out var value) && (value & 0xF) != 0;
    }

    private static bool HasHistoricalThrottleFlag(string flags)
    {
        return TryParseThrottleFlags(flags, out var value) && value != 0;
    }

    private static bool TryParseThrottleFlags(string flags, out int value)
    {
        flags = (flags ?? string.Empty).Trim();
        if (flags.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(flags[2..], System.Globalization.NumberStyles.HexNumber, null, out value))
            return true;
        return int.TryParse(flags, out value);
    }

    private async Task<IReadOnlyList<RetuneTargetSummary>> BuildRetuneTargetDiagnosticsAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return [];

        var psi = new ProcessStartInfo("journalctl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(_config.TrunkRecorder.LogServiceName);
        psi.ArgumentList.Add("--utc");
        psi.ArgumentList.Add("--since");
        psi.ArgumentList.Add(startUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        psi.ArgumentList.Add("--until");
        psi.ArgumentList.Add(endUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC");
        psi.ArgumentList.Add("--no-pager");

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return [];

            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                return [];

            var output = await outputTask;
            return RetuneTargetRegex.Matches(output)
                .Select(match => new
                {
                    Scope = match.Groups["scope"].Value.Trim(),
                    Frequency = match.Groups["freq"].Value.Trim()
                })
                .Where(row => !string.IsNullOrWhiteSpace(row.Scope) && !string.IsNullOrWhiteSpace(row.Frequency))
                .GroupBy(row => new { row.Scope, row.Frequency })
                .Select(group => new RetuneTargetSummary(group.Key.Scope, group.Key.Frequency, group.Count()))
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Scope, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.FrequencyMHz, StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string FormatRetuneTargetSummary(IReadOnlyList<RetuneTargetSummary> targets)
    {
        if (targets.Count == 0)
            return string.Empty;
        return string.Join(", ", targets.Take(4).Select(t => $"{t.FrequencyMHz} MHz x {t.Count:N0}"));
    }

    private IReadOnlyList<RecommendationTalkgroupCandidateDto> BuildTalkgroupNoiseCandidates(IEnumerable<QueueTalkgroupLoadDto> rows)
    {
        return rows
            .Select(row =>
            {
                var calls = Math.Max(1d, row.Calls);
                var weakPct = row.WeakCalls / calls * 100d;
                var failedPct = row.FailedCalls / calls * 100d;
                var repetitivePct = row.RepetitiveCalls / calls * 100d;
                var incidentYieldPct = row.IncidentCalls / calls * 100d;
                var category = NormalizeCategory(row.Category);
                var lowValueCategory = category is "other" or "traffic";
                var lowYieldPenalty = Math.Max(0, 25 - incidentYieldPct) / 25d;
                var score =
                    Math.Min(40, row.AudioSeconds / 60d * 1.25)
                    + Math.Min(30, weakPct * 0.45)
                    + Math.Min(20, failedPct * 0.7)
                    + Math.Min(20, repetitivePct * 1.2)
                    + Math.Min(15, row.PendingAudioSeconds / 60d)
                    + (lowValueCategory ? 10 : 0)
                    + lowYieldPenalty * 10;
                var reasonParts = new List<string>();
                if (row.AudioSeconds >= 5 * 60) reasonParts.Add($"{FormatDuration(row.AudioSeconds)} recent audio");
                if (weakPct >= 20) reasonParts.Add($"{weakPct:F0}% weak/filtered calls");
                if (failedPct >= 10) reasonParts.Add($"{failedPct:F0}% transcription failures");
                if (repetitivePct >= 5) reasonParts.Add($"{repetitivePct:F0}% repetitive transcripts");
                if (incidentYieldPct <= 5) reasonParts.Add($"{incidentYieldPct:F0}% incident yield");
                if (lowValueCategory) reasonParts.Add($"{category} category");
                var reason = reasonParts.Count > 0
                    ? string.Join("; ", reasonParts)
                    : "Recent call pattern is worth operator review.";

                return new RecommendationTalkgroupCandidateDto(
                    row.SystemShortName,
                    row.Talkgroup,
                    row.TalkgroupName,
                    category,
                    row.Calls,
                    row.AudioSeconds,
                    row.AverageAudioSeconds,
                    row.PendingCalls,
                    row.PendingAudioSeconds,
                    _config.Transcription.DeferredTalkgroups.Contains(row.Talkgroup),
                    reason,
                    score,
                    row.WeakCalls,
                    weakPct,
                    row.FailedCalls,
                    failedPct,
                    row.RepetitiveCalls,
                    repetitivePct,
                    row.IncidentCalls,
                    incidentYieldPct);
            })
            .Where(row => row.Calls >= 3 && row.Score >= 18)
            .OrderByDescending(row => row.Score)
            .ThenByDescending(row => row.AudioSeconds)
            .ToList();
    }

    private static string NormalizeCategory(string category)
    {
        category = (category ?? string.Empty).Trim().ToLowerInvariant();
        return category is "police" or "fire" or "ems" or "traffic" ? category : "other";
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        var minutes = seconds / 60d;
        return minutes < 90 ? $"{minutes:F1} min" : $"{minutes / 60d:F1} hr";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{display:N1} {units[unit]}";
    }

    private static RecommendationRunbookDto BuildRunbook(string id) => id switch
    {
        "tr-decode-zero" => new RecommendationRunbookDto(
            "Diagnose High Decode-Zero Rate",
            "Determine whether decode-zero is an RF/control-channel problem, a source coverage issue, or a noisy metric that is not reducing call capture.",
            [
                "Compare current and two-hour global/by-system decode-zero.",
                "Check retunes and no-tx-recorded alongside decode-zero.",
                "Inspect source coverage before changing gain or locking control channels."
            ],
            [
                new RecommendationRunbookStepDto("Scope the failing system", "Use the system rows in Current System Snapshot to identify whether one monitored system is responsible for most decode-zero samples.", new RecommendationTargetDto("tr", "metrics", "")),
                new RecommendationRunbookStepDto("Correlate with retunes and call capture", "If decode-zero and retunes rise together, suspect control-channel stability. If decode-zero is high but calls are still captured, treat it as a lower-confidence RF symptom.", new RecommendationTargetDto("tr", "summary", "")),
                new RecommendationRunbookStepDto("Check configured source coverage", "Review source center/rate coverage against the configured systems. Missing or marginal control-channel coverage should be fixed before gain experiments.", new RecommendationTargetDto("tr", "tools", "")),
                new RecommendationRunbookStepDto("Run calibration only after config checks", "Use the calibration plan/sweep after confirming the relevant SDR source and control channel. Do not lock control channels unless you accept losing traffic during site control-channel changes.", new RecommendationTargetDto("tr", "tools", ""))
            ],
            "This runbook intentionally stops short of auto-changing gain/error or control-channel behavior. Those changes are site and hardware dependent."),
        "tr-retunes" => new RecommendationRunbookDto(
            "Investigate Elevated Retunes",
            "Decide whether retunes are caused by weak CC lock, an over-broad control-channel list, or normal site behavior.",
            ["Review retunes by system.", "Compare retunes with decode-zero and calls concluded.", "Avoid permanent CC locking unless operationally acceptable."],
            [
                new RecommendationRunbookStepDto("Open retune charts", "Use TR Metrics by-system mode and compare retunes against decode-zero for the same windows.", new RecommendationTargetDto("tr", "metrics", "")),
                new RecommendationRunbookStepDto("Check whether capture is actually impaired", "If calls concluded remains healthy, retunes may be noisy but not urgent. If calls drop, investigate RF/source coverage first.", new RecommendationTargetDto("tr", "summary", "")),
                new RecommendationRunbookStepDto("Use calibration as an experiment", "Try short gain/error trials and monitor for a few hours. Different times of day can favor different gain settings.", new RecommendationTargetDto("tr", "tools", ""))
            ],
            "TR retune behavior is partly inherent to P25/site operation. The safest fix is usually improving CC stability rather than disabling retunes."),
        "tr-resource-pressure" => new RecommendationRunbookDto(
            "Reduce TR Resource Pressure",
            "Determine whether trunk-recorder load is acceptable for this host or whether capture policy needs to be reduced.",
            ["High TR CPU/RSS or host temperature means capture and DSP work are stressing the box.", "Virtual memory alone is not evidence of RAM pressure; use RSS and temperature/load.", "Thermal throttling means the host is already protecting itself."],
            [
                new RecommendationRunbookStepDto("Confirm the pressure source", "Check TR CPU/RSS/thread count, host temperature, and throttle flags in the diagnostics. Compare with queue health so transcription is not blamed for capture-side load.", new RecommendationTargetDto("tr", "summary", "")),
                new RecommendationRunbookStepDto("Reduce low-value capture first", "Disable or defer chatty non-incident talkgroups before reducing public-safety coverage. School bus, traffic, utilities, and other long low-yield TGs are usually first candidates.", new RecommendationTargetDto("settings", "talkgroups", "")),
                new RecommendationRunbookStepDto("Revisit recorder count", "If temperature/RSS stays high, back down digitalRecorders or split capture onto a stronger host. More recorders reduce missed calls but create more GNU Radio/op25 pipelines.", new RecommendationTargetDto("tr", "tools", "")),
                new RecommendationRunbookStepDto("Check TR audio processing", "Transient ffmpeg jobs launched by TR can spike CPU during heavy call volume. If spikes align with fan/thermal pressure, review TR audio normalization settings and call volume.", new RecommendationTargetDto("pizzad", "service", ""))
            ],
            "Do not treat this as an automatic reason to lower transcription quality. If PizzaWave queues are healthy, the bottleneck is capture-side heat/load."),
        "queue-audio-pressure" => new RecommendationRunbookDto(
            "Diagnose Transcription Queue Pressure",
            "Use audio-duration metrics to find whether this host is behind because of raw audio volume, long low-value calls, or transcription throughput.",
            ["Compare audio seconds/min in vs out.", "Review pending audio duration and ETA.", "Run the pipeline benchmark if the queue math looks suspicious."],
            [
                new RecommendationRunbookStepDto("Read the queue in audio units", "Compare Audio In, Audio Out, and Pending Audio in Current System Snapshot. Calls/min is useful, but duration-blind.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Find the chattiest talkgroups", "Use Top Audio Talkgroups to identify long non-public-safety traffic that can be deferred without losing public-safety situational awareness.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Benchmark the local path", "Run the Pipeline Benchmark. If Whisper share is above 90%, app overhead is not the main issue.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Choose a policy", "Prefer defer rules before ignore rules. Defer preserves the call while letting normal live calls catch up first.", new RecommendationTargetDto("pizzad", "jobs", ""))
            ],
            "A busy system can have low call count and still exceed this host's capacity if calls are long."),
        "queue-drain-watch" => new RecommendationRunbookDto(
            "Watch Queue Drain",
            "Confirm the queue is recovering before starting manual AI summary work.",
            ["Audio out should exceed audio in.", "Pending audio should trend down.", "Manual AI work should stay blocked while recovering."],
            [
                new RecommendationRunbookStepDto("Verify queue recovery", "Audio out should exceed audio in and pending audio should continue falling before manual AI work is started.", new RecommendationTargetDto("queue", "", "")),
                new RecommendationRunbookStepDto("Avoid extra work", "Do not start manual summary generation until pending audio is near zero.", new RecommendationTargetDto("metrics", "ai", ""))
            ],
            "This is usually temporary after traffic spikes or service restarts."),
        "priority-low-value-audio" => new RecommendationRunbookDto(
            "Review Talkgroup Noise Candidates",
            "Identify high-volume or low-yield talkgroups that may deserve a Setup-level TR exclusion.",
            ["Recommendations ranks candidates only; the operator decides whether to exclude them.", "Exclude writes the talkgroup catalog and regenerates TR CSVs; TR must reload the CSV before capture policy changes.", "Use volume, weak-call rate, repetition, failure rate, and incident yield together."],
            [
                new RecommendationRunbookStepDto("Review candidates", "Review the candidate table on this page. Candidates are generated from recent audio volume, weak-call rate, transcription failure rate, repetition hints, and incident yield.", new RecommendationTargetDto("system", "recommendations", "talkgroup-noise")),
                new RecommendationRunbookStepDto("Exclude only global noise", "Use Exclude on candidates that are noisy or useless for every profile. Use profile hiding instead when some users still need the TG.", new RecommendationTargetDto("system", "recommendations", "talkgroup-noise")),
                new RecommendationRunbookStepDto("Apply and restart capture when needed", "Changing TR exclusion regenerates talkgroup CSV policy. Apply the Setup plan so trunk-recorder reloads the generated talkgroup list.", new RecommendationTargetDto("setup", "apply", "")),
                new RecommendationRunbookStepDto("Monitor outcome", "After exclusion, watch transcription queue, incident yield, and category pages for expected volume reduction without losing useful operational traffic.", new RecommendationTargetDto("pizzad", "jobs", ""))
            ],
            "A high score is not an automatic block decision. It is an operator review queue for Setup."),
        "ingest-paused" => new RecommendationRunbookDto(
            "Resolve Paused Ingest",
            "Decide whether to resume live callstream intake or keep dropping new calls until the transcription queue clears.",
            ["Dropped calls while paused are not recoverable from callstream unless separately archived/imported.", "Pause is useful only as a short recovery tool."],
            [
                new RecommendationRunbookStepDto("Review pause state", "Use Current System Snapshot to confirm whether pause is manual or until-clear, then decide whether live situational awareness is more important than backlog recovery.", new RecommendationTargetDto("pizzad", "service", "")),
                new RecommendationRunbookStepDto("Check queue health", "Resume when queue pressure is acceptable or when live situational awareness matters more than backlog recovery.", new RecommendationTargetDto("pizzad", "jobs", ""))
            ],
            "Use pause sparingly on live systems."),
        "remote-transcription-bandwidth-critical" => new RecommendationRunbookDto(
            "Reduce Remote Transcription Bandwidth",
            "Stop runaway remote audio upload volume before it destabilizes a constrained uplink or the remote LM/transcription path.",
            ["The finding is based on estimated audio upload volume over the last 24 hours.", "Remote-faster-whisper sends audio files over the network for each completed call.", "Pausing ingest is a protective operator action; it drops new live payloads until manually resumed."],
            [
                new RecommendationRunbookStepDto("Pause ingest if the link is constrained", "Use Pause ingest indefinitely when Starlink/QoS throttling or remote transcription instability is more dangerous than dropping additional live calls.", new RecommendationTargetDto("system", "recommendations", "")),
                new RecommendationRunbookStepDto("Open bandwidth metrics", "Review Metrics > Bandwidth for daily totals, request counts, and whether remote transcription is the dominant source.", new RecommendationTargetDto("metrics", "bandwidth", "")),
                new RecommendationRunbookStepDto("Reduce the source of uploads", "Prefer local transcription, deferred/batch transcription, or Setup-level exclusion of noisy talkgroups before resuming live ingest on a constrained uplink.", new RecommendationTargetDto("setup", "talkgroups", "")),
                new RecommendationRunbookStepDto("Resume only after policy is fixed", "Resume ingest after the transcription route or capture policy no longer pushes gigabytes per day across the remote link.", new RecommendationTargetDto("pizzad", "service", ""))
            ],
            "This recommendation uses derived estimates, not packet capture. Treat it as a critical operational warning when the order of magnitude is gigabytes per day."),
        "tr-live-silent" => new RecommendationRunbookDto(
            "Restore Live TR Data",
            "Determine whether trunk-recorder stopped, restart-looped, lost its SDR source, or is running but no longer sending callstream or health data to PizzaWave.",
            ["The primary signal is generic: PizzaWave has not received recent TR-originated data.", "Service fault details and USB/tool output are supporting evidence, not the definition of the outage.", "Do not run RF sweeps until basic service and hardware visibility are confirmed."],
            [
                new RecommendationRunbookStepDto("Check TR service state", "Open System > Services and verify whether trunk-recorder is active, failed, or stuck in auto-restart. If it is restart-looping, stop it before changing hardware.", new RecommendationTargetDto("system", "services", "")),
                new RecommendationRunbookStepDto("Read the latest fault evidence", "Use the diagnostic rows and service log tail to identify the first concrete failure: missing SDR, permission error, config parse error, source stopped receiving samples, or callstream/plugin failure.", new RecommendationTargetDto("system", "recommendations", "")),
                new RecommendationRunbookStepDto("Confirm live config and hardware", "Compare the live TR source config with currently detected SDR hardware before restarting capture.", new RecommendationTargetDto("tr", "tools", "")),
                new RecommendationRunbookStepDto("Restart after the cause is fixed", "Restart trunk-recorder only after the configured receiver is visible and the live config still matches the intended Radio Setup workspace.", new RecommendationTargetDto("system", "services", ""))
            ],
            "A quiet radio site can also produce no calls. Treat the red live indicator as an operator alarm to inspect TR, not as automatic proof of RF failure."),
        "ai-blocked-queue" => new RecommendationRunbookDto(
            "Review AI Work Blocking",
            "Confirm AI summaries/incidents are blocked because transcription queue pressure is high, not because LM Link is broken.",
            ["Queue limit is intentional on constrained hosts.", "AI work can consume compute and tokens.", "Large historical AI work can take a long time on slower or remote LLM setups."],
            [
                new RecommendationRunbookStepDto("Wait for transcription recovery", "Manual summaries should stay blocked until the queue is under the configured limit, unless the host has enough headroom for AI and transcription at the same time.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Review AI settings", "If this is a fast machine, tune the queue depth limit in AI Insights settings.", new RecommendationTargetDto("settings", "ai-insights", ""))
            ],
            "Do not raise AI limits until transcription is stable on this host."),
        "ai-service-failures" => new RecommendationRunbookDto(
            "Restore AI Service Availability",
            "Determine whether incident generation is failing because the completion endpoint is unavailable, wedged, returning no valid result, or routed to the wrong model.",
            ["Completion timeouts can create silent incident gaps even when transcription, Qdrant, and queue depth look healthy.", "An endpoint that accepts a request is not healthy unless a bounded completion returns usable content.", "LM Studio LMLink may expose localhost while forwarding to another machine; verify the actual generation host and model state."],
            [
                new RecommendationRunbookStepDto("Check AI metrics", "Open Metrics > AI and inspect the latest failed requests, endpoint, request model, and error text.", new RecommendationTargetDto("metrics", "ai", "")),
                new RecommendationRunbookStepDto("Probe generation, not just reachability", "Send a tiny chat completion with a short timeout. Treat timeout, empty content with zero tokens, or repeated client-disconnect cancellation as unavailable even when /v1/models works.", new RecommendationTargetDto("metrics", "ai", "")),
                new RecommendationRunbookStepDto("Check LM Studio/LMLink state", "Confirm the configured base URL, LMLink route, loaded model, and active generation state on the host that actually runs the model.", new RecommendationTargetDto("system", "services", "")),
                new RecommendationRunbookStepDto("Watch incident yield", "After service failures stop, compare calls, accepted creates, rejects, and incidents per 1,000 calls for the next hour.", new RecommendationTargetDto("metrics", "incidents", ""))
            ],
            "Do not use queue backlog as the only health signal. Fix generation liveness first so the next scheduled extraction succeeds normally."),
        "ai-truncation-pressure" => new RecommendationRunbookDto(
            "Reduce AI Output Pressure",
            "Lower incident extraction and evidence-verifier truncation without creating retry storms or starving live transcription.",
            ["finish_reason=length means the model hit the configured output limit.", "Blind retries can multiply LM load while producing the same failure.", "The first fix should reduce prompt/output size before raising max_tokens."],
            [
                new RecommendationRunbookStepDto("Review failure classes", "Open Metrics > AI and confirm whether failures are mostly truncation, cancellation, or HTTP/model errors.", new RecommendationTargetDto("metrics", "ai", "")),
                new RecommendationRunbookStepDto("Reduce incident payload size", "Prefer compact incident schemas, lower candidate/carryover caps during high volume, and shorter transcript excerpts before increasing max_tokens.", new RecommendationTargetDto("settings", "ai-insights", "")),
                new RecommendationRunbookStepDto("Avoid broad automatic retries", "If a retry is added, make it one compact low-priority retry with a smaller payload and explicit telemetry so it cannot flood the shared LM pipeline.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Watch the next day", "Confirm truncations fall while incident association quality and AI token generation latency stay acceptable.", new RecommendationTargetDto("metrics", "ai", ""))
            ],
            "Raising max_tokens may hide the symptom while increasing GPU/LM occupancy. Use it only after reducing output pressure."),
        _ => new RecommendationRunbookDto(
            "Troubleshoot Recommendation",
            "Review the linked evidence and decide whether to act elsewhere or ignore this finding.",
            [],
            [new RecommendationRunbookStepDto("Open relevant page", "Use the recommendation target to inspect the source diagnostics.", new RecommendationTargetDto("recommendations", "", ""))],
            "")
    };
}

public sealed record SystemRecommendationDto(
    string Id,
    string Section,
    string Severity,
    string Title,
    string Detail,
    string Action,
    RecommendationTargetDto Target,
    IReadOnlyList<RecommendationActionDto> Actions)
{
    public RecommendationRunbookDto? Runbook { get; init; }
    public RecommendationBaselineInfoDto? Baseline { get; init; }
}

public sealed record RecommendationBaselineDto(
    string RecommendationId,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    int ActiveObservations,
    string BaselineValue);

public sealed record RecommendationBaselineInfoDto(
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    string BaselineValue,
    int ActiveObservations,
    double AgeHours,
    bool PriorityDemoted,
    string OriginalSeverity);

public sealed record RecommendationTargetDto(string TopTab, string SubTab, string Anchor);

public sealed record RecommendationActionDto(string Kind, string Label, string Description, IReadOnlyList<long>? Talkgroups = null);

public sealed record RecommendationRunbookDto(
    string Title,
    string Goal,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<RecommendationRunbookStepDto> Steps,
    string Caveat)
{
    public IReadOnlyList<RecommendationDiagnosticDto> Diagnostics { get; init; } = [];
    public IReadOnlyList<RecommendationTalkgroupCandidateDto> TalkgroupCandidates { get; init; } = [];
}

public sealed record RecommendationRunbookStepDto(
    string Title,
    string Detail,
    RecommendationTargetDto Target);

public sealed record RecommendationTalkgroupCandidateDto(
    string SystemShortName,
    long Talkgroup,
    string TalkgroupName,
    string Category,
    long Calls,
    long AudioSeconds,
    double AverageAudioSeconds,
    long PendingCalls,
    long PendingAudioSeconds,
    bool AlreadyDeferred,
    string Reason,
    double Score = 0,
    long WeakCalls = 0,
    double WeakPct = 0,
    long FailedCalls = 0,
    double FailedPct = 0,
    long RepetitiveCalls = 0,
    double RepetitivePct = 0,
    long IncidentCalls = 0,
    double IncidentYieldPct = 0);

public sealed record RecommendationDiagnosticDto(
    string Label,
    string Value,
    string Status,
    string Detail);

public sealed record RetuneTargetSummary(
    string Scope,
    string FrequencyMHz,
    int Count);

public sealed record SystemRecommendationsDto(
    int OpenCount,
    int HighCount,
    int MediumCount,
    int LowCount,
    IReadOnlyList<SystemRecommendationDto> Items,
    IReadOnlyList<SystemRecommendationDto> IgnoredItems);

public sealed record RecommendationStateRequest(string Action);
