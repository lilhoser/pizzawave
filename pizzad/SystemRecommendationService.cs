namespace pizzad;

public sealed class SystemRecommendationService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EnginePipeline _pipeline;
    private readonly IngestControlService _ingestControl;

    public SystemRecommendationService(EngineConfig config, EngineDatabase database, EnginePipeline pipeline, IngestControlService ingestControl)
    {
        _config = config;
        _database = database;
        _pipeline = pipeline;
        _ingestControl = ingestControl;
    }

    public async Task<SystemRecommendationsDto> BuildAsync(CancellationToken ct)
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
        var ingest = _ingestControl.GetStatus(_pipeline.QueueDepth);
        var queueDiagnostics = new List<RecommendationDiagnosticDto>
        {
            new("Audio in", $"{audioInPerMinute:F0}s/min", audioInPerMinute > audioOutPerMinute && pendingCalls > 0 ? "issue" : "ok", "Recent live/imported audio entering transcription over the last 10 minutes."),
            new("Audio out", $"{audioOutPerMinute:F0}s/min", audioOutPerMinute < audioInPerMinute && pendingCalls > 0 ? "issue" : "ok", "Recent completed transcription audio over the last 10 minutes."),
            new("Pending audio", FormatDuration(pendingAudio), pendingAudio > 10 * 60 ? "issue" : "ok", $"{pendingCalls:N0} call(s) currently pending transcription."),
            new("Workers", $"{_config.Transcription.LiveTranscriptionWorkers} x {_config.Transcription.WhisperThreads}", "info", "Configured live transcription workers x Whisper threads per worker."),
            new("Queue limit for AI", $"{_config.AiInsights.MaxQueueDepthForManualSummary:N0}", _pipeline.QueueDepth > _config.AiInsights.MaxQueueDepthForManualSummary && _config.AiInsights.MaxQueueDepthForManualSummary > 0 ? "issue" : "ok", "Manual AI summary work is blocked above this queue depth when the limit is enabled.")
        };

        if (pendingCalls > 0 && audioOutPerMinute <= audioInPerMinute)
        {
            rows.Add(new SystemRecommendationDto(
                "queue-audio-pressure",
                "pizzad",
                "high",
                "Transcription queue is growing by audio duration",
                $"The last 10 minutes ingested about {audioInPerMinute:F0}s audio/min and transcribed about {audioOutPerMinute:F0}s audio/min. Pending audio is {FormatDuration(pendingAudio)}.",
                "Review the Top Audio Talkgroups table and set lower-priority talkgroups to defer or ignore before increasing worker count.",
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
                "Let the queue drain before starting imports or manual AI summary generation.",
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

        var topAudio = await _database.ListTopAudioTalkgroupsAsync(startUnix, 20, ct);
        var lowValue = topAudio
            .Where(t => t.Category is "other" or "traffic" && t.AudioSeconds >= 5 * 60)
            .Take(5)
            .ToList();
        if (lowValue.Count > 0)
        {
            var detail = string.Join("; ", lowValue.Select(t => $"{t.TalkgroupName} ({t.AudioSeconds / 60d:F1} min, {t.Calls:N0} calls)"));
            rows.Add(new SystemRecommendationDto(
                "priority-low-value-audio",
                "pizzad",
                "medium",
                "Candidate talkgroups for defer/ignore priority",
                detail,
                "Apply a deferred priority rule for these talkgroups, then monitor whether public-safety calls catch up faster.",
                new RecommendationTargetDto("pizzad", "jobs", ""),
                [
                    new RecommendationActionDto("apply-defer-talkgroups", "Defer Selected Talkgroups", "Move the selected talkgroups behind normal live calls in the transcription queue.", lowValue.Select(t => t.Talkgroup).ToList())
                ])
                {
                    Runbook = BuildRunbook("priority-low-value-audio") with
                    {
                        Diagnostics =
                        [
                            new("Candidate window", $"{windowMinutes} min", "info", "Recent audio-load window used to find candidates."),
                            new("Candidate count", $"{lowValue.Count:N0}", lowValue.Count > 0 ? "issue" : "ok", "Talkgroups matching the current defer-first heuristic."),
                            new("Already deferred", $"{lowValue.Count(t => _config.Transcription.DeferredTalkgroups.Contains(t.Talkgroup)):N0}", "info", "Candidates already present in the deferred queue policy.")
                        ],
                        TalkgroupCandidates = lowValue.Select(t => new RecommendationTalkgroupCandidateDto(
                            t.SystemShortName,
                            t.Talkgroup,
                            t.TalkgroupName,
                            t.Category,
                            t.Calls,
                            t.AudioSeconds,
                            t.AverageAudioSeconds,
                            t.PendingCalls,
                            t.PendingAudioSeconds,
                            _config.Transcription.DeferredTalkgroups.Contains(t.Talkgroup),
                            $"High recent audio load in {t.Category}; low-priority category candidate for defer-first policy.")).ToList()
                    }
                });
        }

        var healthStart = new DateTimeOffset(now.AddHours(-2)).ToUnixTimeSeconds();
        var health = await _database.ListHealthSamplesAsync(healthStart, new DateTimeOffset(now).ToUnixTimeSeconds(), ct);
        var global = health.Where(h => string.Equals(h.Scope, "global", StringComparison.OrdinalIgnoreCase)).ToList();
        var bySystem = health
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
            .OrderByDescending(s => s.DecodeZeroPct)
            .ThenByDescending(s => s.Retunes)
            .Take(5)
            .ToList();
        var decodeLines = global.Sum(h => h.CcSummaryDecodeLines);
        var decodeZero = global.Sum(h => h.CcSummaryDecodeZero);
        var lowDecodeWarnings = global.Sum(h => h.LowDecodeWarningLines);
        var retunes = global.Sum(h => h.Retunes);
        var trDiagnostics = new List<RecommendationDiagnosticDto>
        {
            new("Health window", "2 hr", "info", "Recent trunk-recorder health samples in the engine database."),
            new("Global CC summary zero", decodeLines > 0 ? $"{decodeZero / Math.Max(1d, decodeLines) * 100d:F1}%" : "n/a", decodeLines > 0 && decodeZero / Math.Max(1d, decodeLines) * 100d >= 10 ? "issue" : "ok", $"{decodeZero:N0} zero periodic CC summary sample(s) across {decodeLines:N0} CC summary sample(s)."),
            new("Low-decode warnings", $"{lowDecodeWarnings:N0}", lowDecodeWarnings >= 20 ? "issue" : "ok", "Control Channel Message Decode Rate warning lines in the same health window."),
            new("Global retunes", $"{retunes:N0}", retunes >= 20 ? "issue" : "ok", "Control-channel retunes in the same health window.")
        };
        trDiagnostics.AddRange(bySystem.Select(s => new RecommendationDiagnosticDto(
            s.Scope,
            $"{s.DecodeZeroPct:F1}% CC zero, {s.LowDecodeWarnings:N0} warnings, {s.Retunes:N0} retunes",
            s.DecodeZeroPct >= 10 || s.LowDecodeWarnings >= 20 || s.Retunes >= 20 ? "issue" : "ok",
            $"{s.CallsConcluded:N0} concluded call(s), {s.NoTxRecorded:N0} no-transmission result(s), {s.DecodeLines:N0} CC summary sample(s).")));
        if (decodeLines >= 20)
        {
            var decodeZeroPct = decodeZero / Math.Max(1d, decodeLines) * 100d;
            if (decodeZeroPct >= 10)
            {
                rows.Add(new SystemRecommendationDto(
                    "tr-decode-zero",
                    "trunk-recorder",
                    "high",
                    "TR CC summary decode-zero rate is high",
                    $"Global periodic control-channel summary decode-zero rate over the last 2 hours is about {decodeZeroPct:F1}% across {decodeLines:N0} samples.",
                    "Inspect System > Trunk Recorder > Metrics by system to identify whether this is isolated to one monitored system/source.",
                    new RecommendationTargetDto("tr", "metrics", ""),
                    [])
                    { Runbook = BuildRunbook("tr-decode-zero") with { Diagnostics = trDiagnostics } });
            }
        }
        if (retunes >= 20)
        {
            rows.Add(new SystemRecommendationDto(
                "tr-retunes",
                "trunk-recorder",
                "medium",
                "TR control-channel retunes are elevated",
                $"Observed {retunes:N0} control-channel retunes over the last 2 hours.",
                "Compare retunes with decode-zero rate and recent gain/error changes before locking control channels.",
                new RecommendationTargetDto("tr", "metrics", ""),
                [])
                { Runbook = BuildRunbook("tr-retunes") with { Diagnostics = trDiagnostics } });
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

        var ordered = rows
            .Where(r => !IsSuppressed(r.Id, states, now))
            .OrderByDescending(r => SeverityRank(r.Severity))
            .ThenBy(r => r.Section)
            .ThenBy(r => r.Title)
            .Select(r => r.Runbook is null ? r with { Runbook = BuildRunbook(r.Id) } : r)
            .ToList();

        return new SystemRecommendationsDto(
            ordered.Count,
            ordered.Count(r => r.Severity == "high"),
            ordered.Count(r => r.Severity == "medium"),
            ordered.Count(r => r.Severity == "low"),
            ordered);
    }

    public async Task<SystemRecommendationActionResultDto> ApplyAsync(string recommendationId, string action, IReadOnlyList<long>? talkgroups, CancellationToken ct)
    {
        if (recommendationId == "priority-low-value-audio" && action == "apply-defer-talkgroups")
        {
            var candidates = talkgroups?.Where(t => t > 0).Distinct().ToList() ?? [];
            if (candidates.Count == 0)
            {
                var startUnix = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
                candidates = (await _database.ListTopAudioTalkgroupsAsync(startUnix, 20, ct))
                    .Where(t => t.Category is "other" or "traffic" && t.AudioSeconds >= 5 * 60)
                    .Select(t => t.Talkgroup)
                    .Distinct()
                    .ToList();
            }
            if (candidates.Count == 0)
                return new SystemRecommendationActionResultDto(false, "No current low-value audio candidates met the defer threshold.");

            foreach (var tg in candidates)
            {
                if (!_config.Transcription.DeferredTalkgroups.Contains(tg))
                    _config.Transcription.DeferredTalkgroups.Add(tg);
            }
            _config.Save();
            await _database.SaveRecommendationStateAsync(recommendationId, DateTime.UtcNow.AddHours(12), ct);
            return new SystemRecommendationActionResultDto(true, $"Deferred {candidates.Count} talkgroup(s): {string.Join(", ", candidates)}. New live calls on these TGs will run after normal live calls. Existing queued calls are not reordered.");
        }

        return new SystemRecommendationActionResultDto(false, "This recommendation does not have an automatic apply action yet.");
    }

    public async Task SetStateAsync(string recommendationId, string action, CancellationToken ct)
    {
        switch (action)
        {
            case "snooze":
                await _database.SaveRecommendationStateAsync(recommendationId, DateTime.UtcNow.AddHours(24), ct);
                break;
            case "dismiss":
                await _database.SaveRecommendationStateAsync(recommendationId, null, ct);
                break;
            case "clear":
                await _database.ClearRecommendationStateAsync(recommendationId, ct);
                break;
            default:
                throw new InvalidOperationException("Unknown recommendation state action.");
        }
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
        "high" => 3,
        "medium" => 2,
        "low" => 1,
        _ => 0
    };

    private static string FormatDuration(long seconds)
    {
        if (seconds < 60) return $"{seconds}s";
        var minutes = seconds / 60d;
        return minutes < 90 ? $"{minutes:F1} min" : $"{minutes / 60d:F1} hr";
    }

    private static RecommendationRunbookDto BuildRunbook(string id) => id switch
    {
        "tr-decode-zero" => new RecommendationRunbookDto(
            "Diagnose High Decode-Zero Rate",
            "Determine whether decode-zero is an RF/control-channel problem, a source coverage issue, or a noisy metric that is not reducing call capture.",
            [
                "Compare global and by-system decode-zero over the last 2 hours.",
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
            "Confirm the queue is recovering before starting imports or manual AI summary work.",
            ["Audio out should exceed audio in.", "Pending audio should trend down.", "Imports and AI should stay blocked while recovering."],
            [
                new RecommendationRunbookStepDto("Verify queue recovery", "Audio out should exceed audio in and pending audio should continue falling before imports or manual AI work are started.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Avoid extra work", "Do not start SFTP/local imports or manual summary generation until pending audio is near zero.", new RecommendationTargetDto("pizzad", "imports", ""))
            ],
            "This is usually temporary after traffic spikes or service restarts."),
        "priority-low-value-audio" => new RecommendationRunbookDto(
            "Create A Talkgroup Priority Policy",
            "Reduce queue pressure by deferring high-audio, low-priority talkgroups while preserving public-safety calls.",
            ["Start with defer, not ignore.", "Use audio duration and category as the first heuristic.", "Later add self-learning based on alert/incident yield."],
            [
                new RecommendationRunbookStepDto("Review candidates", "Review the candidate table on this page. Candidates are generated from the running system's recent audio load, category, and current deferred state.", new RecommendationTargetDto("recommendations", "", "")),
                new RecommendationRunbookStepDto("Apply defer rule", "Use Defer These Talkgroups to add current candidates to the deferred queue list. New calls on those TGs run after normal live calls.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Monitor outcome", "Watch whether normal public-safety calls catch up faster and whether deferred TGs still get processed during quieter periods.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Future self-learning policy", "The engine should eventually suggest priority using audio share, alert matches, incident contribution, category, and user overrides.", new RecommendationTargetDto("settings", "profiles", ""))
            ],
            "The current automatic action only defers. It does not delete, skip, or ignore calls."),
        "ingest-paused" => new RecommendationRunbookDto(
            "Resolve Paused Ingest",
            "Decide whether to resume live callstream intake or keep dropping new calls until the transcription queue clears.",
            ["Dropped calls while paused are not recoverable from callstream unless separately archived/imported.", "Pause is useful only as a short recovery tool."],
            [
                new RecommendationRunbookStepDto("Review pause state", "Use Current System Snapshot to confirm whether pause is manual or until-clear, then decide whether live situational awareness is more important than backlog recovery.", new RecommendationTargetDto("pizzad", "service", "")),
                new RecommendationRunbookStepDto("Check queue health", "Resume when queue pressure is acceptable or when live situational awareness matters more than backlog recovery.", new RecommendationTargetDto("pizzad", "jobs", ""))
            ],
            "Use pause sparingly on live systems."),
        "ai-blocked-queue" => new RecommendationRunbookDto(
            "Review AI Work Blocking",
            "Confirm AI summaries/incidents are blocked because transcription queue pressure is high, not because LM Link is broken.",
            ["Queue limit is intentional on constrained hosts.", "AI work can consume compute and tokens.", "Backfill can take a long time on slower or remote LLM setups."],
            [
                new RecommendationRunbookStepDto("Wait for transcription recovery", "Manual summaries should stay blocked until the queue is under the configured limit, unless the host has enough headroom for AI and transcription at the same time.", new RecommendationTargetDto("pizzad", "jobs", "")),
                new RecommendationRunbookStepDto("Review AI settings", "If this is a fast machine, tune the queue depth limit in AI Insights settings.", new RecommendationTargetDto("settings", "ai-insights", ""))
            ],
            "Do not raise AI limits until transcription is stable on this host."),
        _ => new RecommendationRunbookDto(
            "Troubleshoot Recommendation",
            "Review the linked evidence and decide whether to apply, snooze, or dismiss.",
            [],
            [new RecommendationRunbookStepDto("Open relevant page", "Use the recommendation action buttons to inspect the source diagnostics.", new RecommendationTargetDto("recommendations", "", ""))],
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
}

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
    string Reason);

public sealed record RecommendationDiagnosticDto(
    string Label,
    string Value,
    string Status,
    string Detail);

public sealed record SystemRecommendationsDto(
    int OpenCount,
    int HighCount,
    int MediumCount,
    int LowCount,
    IReadOnlyList<SystemRecommendationDto> Items);

public sealed record RecommendationStateRequest(string Action);

public sealed record RecommendationApplyRequest(string Action, IReadOnlyList<long>? Talkgroups = null);

public sealed record SystemRecommendationActionResultDto(bool Applied, string Message);
