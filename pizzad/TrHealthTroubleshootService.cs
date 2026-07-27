using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class TrHealthTroubleshootService
{
    private static readonly TimeSpan TroubleshootCacheTtl = TimeSpan.FromSeconds(30);
    private static readonly Regex TrTimestampRegex = new(
        @"\[(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]",
        RegexOptions.Compiled);
    private static readonly Regex TrScopeRegex = new(
        @"\]\s+\((?:info|error|warning|debug)\)\s+\[(?<scope>[^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RetuneTargetRegex = new(
        @"\[(?<scope>[^\]]+)\]\s+Retuning to Control Channel:\s+(?<freq>\d+(?:\.\d+)?)\s+MHz",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly TrConfigService _trConfig;
    private readonly ILogger<TrHealthTroubleshootService> _logger;
    private readonly Func<DateTime, DateTime, CancellationToken, Task<string>>? _journalRangeReader;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private TrTroubleshootCacheEntry? _cachedTroubleshoot;
    private Task<TrTroubleshootDto>? _troubleshootBuildTask;
    private string _troubleshootBuildKey = string.Empty;

    public TrHealthTroubleshootService(
        EngineConfig config,
        EngineDatabase database,
        TrConfigService trConfig,
        ILogger<TrHealthTroubleshootService> logger,
        Func<DateTime, DateTime, CancellationToken, Task<string>>? journalRangeReader = null)
    {
        _config = config;
        _database = database;
        _trConfig = trConfig;
        _logger = logger;
        _journalRangeReader = journalRangeReader;
    }

    public async Task<TrTroubleshootDto> BuildAsync(long start, long end, bool bySystem, string baseline, CancellationToken ct)
    {
        var cacheKey = TroubleshootCacheKey(start, end, bySystem, baseline);
        var now = DateTimeOffset.UtcNow;
        Task<TrTroubleshootDto> buildTask;
        await _cacheGate.WaitAsync(ct);
        try
        {
            if (_cachedTroubleshoot is { } cached &&
                cached.Key == cacheKey &&
                now - cached.CreatedAt <= TroubleshootCacheTtl)
                return cached.Value;

            if (_troubleshootBuildTask is { IsCompleted: false } running && _troubleshootBuildKey == cacheKey)
                buildTask = running;
            else
            {
                _troubleshootBuildKey = cacheKey;
                _troubleshootBuildTask = BuildCoreAsync(start, end, bySystem, baseline, CancellationToken.None);
                buildTask = _troubleshootBuildTask;
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
            if (_troubleshootBuildKey == cacheKey)
            {
                _cachedTroubleshoot = new TrTroubleshootCacheEntry(cacheKey, DateTimeOffset.UtcNow, result);
                _troubleshootBuildTask = null;
            }
        }
        finally
        {
            _cacheGate.Release();
        }

        return result;
    }

    public async Task<IReadOnlyList<TrSystemHealthDto>> BuildSystemAssessmentsAsync(long start, long end, string baseline, CancellationToken ct)
    {
        var baselineStart = DateTimeOffset.UtcNow.AddDays(-BaselineDays(baseline)).ToUnixTimeSeconds();
        var rows = await _database.ListHealthSamplesAsync(Math.Min(start, baselineStart), end, ct);
        var selectedRows = rows.Where(row =>
        {
            var rowStart = new DateTimeOffset(row.WindowStartUtc).ToUnixTimeSeconds();
            var rowEnd = new DateTimeOffset(row.WindowEndUtc).ToUnixTimeSeconds();
            return rowStart >= start && rowEnd <= end;
        }).ToList();
        return BuildSystemSummaries(selectedRows, rows, start);
    }

    public async Task<TranscriptionPerformanceDto> BuildTranscriptionPerformanceAsync(long start, long end, int samplePage, int samplePageSize, CancellationToken ct)
    {
        static bool Eligible(EngineCall call) => !string.IsNullOrWhiteSpace(call.AudioPath);
        static bool Completed(EngineCall call) => call.TranscriptionStatus is "complete" or "poor_quality";
        static bool Usable(EngineCall call) => string.Equals(call.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase) && string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase);
        static bool Failed(EngineCall call) => string.Equals(call.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase) || string.Equals(call.QualityReason, "transcription_error", StringComparison.OrdinalIgnoreCase);
        static bool UnusableAudio(EngineCall call) => call.QualityReason is "inaudible" or "blank_audio" or "marker_only";
        static bool Pending(EngineCall call) => string.Equals(call.TranscriptionStatus, "pending", StringComparison.OrdinalIgnoreCase);
        static bool OtherQuality(EngineCall call) => !Usable(call) && !Failed(call) && !UnusableAudio(call) && !Pending(call);
        static string SystemLabel(EngineCall call) => string.IsNullOrWhiteSpace(call.SystemShortName) ? "Unknown system" : call.SystemShortName.Trim();
        static string TalkgroupLabel(EngineCall call) => string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"TG {call.Talkgroup}" : call.TalkgroupName.Trim();
        static string TalkgroupKey(EngineCall call) => TalkgroupCatalogService.CatalogKey(call.SystemShortName, call.Talkgroup);
        static double Rate(IEnumerable<EngineCall> rows, Func<EngineCall, bool> predicate)
        {
            var list = rows as IReadOnlyCollection<EngineCall> ?? rows.ToList();
            return list.Count == 0 ? 0 : list.Count(predicate) * 100.0 / list.Count;
        }

        var baselineStart = start - 7 * 86400L;
        var selected = (await _database.ListCallsAsync(start, end, null, ct)).Where(Eligible).ToList();
        var baseline = (await _database.ListCallsAsync(baselineStart, start, null, ct)).Where(Eligible).ToList();
        var completionPoints = await _database.ListTranscriptionCompletionPointsAsync(start, end, ct);
        var bucketSeconds = end - start <= 86400 ? 900 : 3600;
        var baselineBySystem = baseline.GroupBy(SystemLabel, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => Rate(group, Usable), StringComparer.OrdinalIgnoreCase);
        var baselineByTalkgroup = baseline.GroupBy(TalkgroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => Rate(group, Usable), StringComparer.OrdinalIgnoreCase);

        TranscriptionGroupDto GroupRow(string label, string system, long talkgroup, string category, List<EngineCall> rows, double baselineUsable) => new(
            label, system, talkgroup, category, rows.Count, rows.Count(Completed), rows.Count(Usable), rows.Count(Failed), rows.Count(UnusableAudio), rows.Count(OtherQuality), rows.Count(Pending),
            Percent(rows.Count(Completed), rows.Count), Percent(rows.Count(Usable), rows.Count), Percent(rows.Count(Failed), rows.Count), Percent(rows.Count(UnusableAudio), rows.Count), baselineUsable);

        var outcomes = selected
            .GroupBy(call => call.StartTime - call.StartTime % bucketSeconds)
            .OrderBy(group => group.Key)
            .Select(group => new TranscriptionOutcomeBucketDto(group.Key, group.Count(), group.Count(Completed), group.Count(Usable), group.Count(Failed), group.Count(UnusableAudio), group.Count(OtherQuality), group.Count(Pending)))
            .ToList();
        var ingestedByBucket = selected.GroupBy(call => call.StartTime - call.StartTime % bucketSeconds)
            .ToDictionary(group => group.Key, group => group.Sum(call => Math.Max(0, call.StopTime - call.StartTime)));
        var completedByBucket = completionPoints.GroupBy(point => point.CompletedAt - point.CompletedAt % bucketSeconds)
            .ToDictionary(group => group.Key, group => group.Sum(point => point.AudioSeconds));
        var firstBucket = start - start % bucketSeconds;
        var throughput = Enumerable.Range(0, Math.Max(0, (int)Math.Ceiling((end - firstBucket) / (double)bucketSeconds)))
            .Select(index => firstBucket + index * (long)bucketSeconds)
            .Select(bucket => new TranscriptionThroughputBucketDto(bucket, ingestedByBucket.GetValueOrDefault(bucket), completedByBucket.GetValueOrDefault(bucket)))
            .ToList();

        static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            if (sorted.Count == 0) return 0;
            var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
            return sorted[index];
        }
        var latency = completionPoints.Where(point => !point.IsImported)
            .GroupBy(point => point.CompletedAt - point.CompletedAt % bucketSeconds)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var values = group.Select(point => point.LatencySeconds).Order().ToList();
                return new TranscriptionLatencyBucketDto(group.Key, values.Count, Percentile(values, .5), Percentile(values, .95));
            }).ToList();

        var problemRows = selected.Where(call => !Usable(call) && !Pending(call)).OrderByDescending(call => call.StartTime).ToList();
        samplePageSize = Math.Clamp(samplePageSize, 10, 50);
        var sampleTotal = problemRows.Count;
        var pageCount = Math.Max(1, (int)Math.Ceiling(sampleTotal / (double)samplePageSize));
        samplePage = Math.Clamp(samplePage, 1, pageCount);
        var talkgroupRows = selected.GroupBy(TalkgroupKey, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var rows = group.ToList();
            var first = rows[0];
            return GroupRow(TalkgroupLabel(first), SystemLabel(first), first.Talkgroup, first.Category, rows, baselineByTalkgroup.GetValueOrDefault(group.Key));
        }).Where(row => row.TotalCalls >= 10).OrderBy(row => row.UsablePercent).ThenByDescending(row => row.TotalCalls).Take(30).ToList();
        var visibleTalkgroupKeys = talkgroupRows.Select(row => TalkgroupCatalogService.CatalogKey(row.SystemShortName, row.Talkgroup)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var samples = problemRows.Where(call => visibleTalkgroupKeys.Contains(TalkgroupKey(call)))
            .GroupBy(TalkgroupKey, StringComparer.OrdinalIgnoreCase).SelectMany(group => group.Take(5))
            .Select(call =>
            {
                var metadataDuration = Math.Max(0, call.StopTime - call.StartTime);
                var path = Path.IsPathRooted(call.AudioPath) ? call.AudioPath : Path.Combine(_config.Storage.AudioRoot, call.AudioPath);
                var measuredDuration = CallAudioService.TryReadWavDurationSeconds(path);
                return new QualityAuditSampleDto(call.Id, call.StartTime, SystemLabel(call), call.Source, call.Talkgroup, TalkgroupLabel(call), call.Category, measuredDuration ?? metadataDuration, call.TranscriptionStatus, call.QualityReason, call.Transcription, $"/api/v1/calls/{call.Id}/audio");
            }).ToList();

        return new TranscriptionPerformanceDto
        {
            RangeStart = start,
            RangeEnd = end,
            BucketSeconds = bucketSeconds,
            TotalCalls = selected.Count,
            CompletedCalls = selected.Count(Completed),
            UsableCalls = selected.Count(Usable),
            EngineFailureCalls = selected.Count(Failed),
            UnusableAudioCalls = selected.Count(UnusableAudio),
            OtherQualityCalls = selected.Count(OtherQuality),
            PendingCalls = selected.Count(Pending),
            CompletionPercent = Percent(selected.Count(Completed), selected.Count),
            UsablePercent = Percent(selected.Count(Usable), selected.Count),
            EngineFailurePercent = Percent(selected.Count(Failed), selected.Count),
            UnusableAudioPercent = Percent(selected.Count(UnusableAudio), selected.Count),
            BaselineUsablePercent = Rate(baseline, Usable),
            Outcomes = outcomes,
            Throughput = throughput,
            Latency = latency,
            Reasons = problemRows.GroupBy(call => string.IsNullOrWhiteSpace(call.QualityReason) ? "unknown" : call.QualityReason, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count()).Select(group => new TranscriptionReasonDto(group.Key, group.Count(), Percent(group.Count(), selected.Count))).ToList(),
            Systems = selected.GroupBy(SystemLabel, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var rows = group.ToList();
                return GroupRow(group.Key, group.Key, 0, string.Empty, rows, baselineBySystem.GetValueOrDefault(group.Key));
            }).OrderBy(row => row.UsablePercent).ThenByDescending(row => row.TotalCalls).ToList(),
            Talkgroups = talkgroupRows,
            Samples = samples,
            SamplePage = samplePage,
            SamplePageSize = samplePageSize,
            SampleTotal = sampleTotal
        };
    }

    private async Task<TrTroubleshootDto> BuildCoreAsync(long start, long end, bool bySystem, string baseline, CancellationToken ct)
    {
        var baselineStart = DateTimeOffset.UtcNow.AddDays(-BaselineDays(baseline)).ToUnixTimeSeconds();
        var log = await ReadJournalAsync(2000, ct);
        var rows = await _database.ListHealthSamplesAsync(Math.Min(start, baselineStart), end, ct);
        if (!rows.Any(r => IsDisplaySystemScope(r.Scope)))
            rows.AddRange(ParseJournalSamples(log));
        var selectedRows = rows.Where(r =>
        {
            var rowStart = new DateTimeOffset(r.WindowStartUtc).ToUnixTimeSeconds();
            var rowEnd = new DateTimeOffset(r.WindowEndUtc).ToUnixTimeSeconds();
            return rowStart >= start && rowEnd <= end;
        }).ToList();
        var summaryRows = selectedRows;

        var calls = await _database.ListCallsAsync(start, end, null, ct);
        var observedFrequencies = await _database.ListObservedCallFrequenciesBySystemAsync(ct);
        var logOutput = TailLines(log, 300);
        var health = BuildSummary(summaryRows, selectedRows, rows, calls, observedFrequencies, bySystem, baseline, start, end);
        var qualityAudit = BuildQualityAudit(calls);
        var diagnostics = BuildDiagnostics(rows, summaryRows, selectedRows, baseline, bySystem, logOutput);
        return new TrTroubleshootDto(
            health,
            qualityAudit,
            _trConfig.Validate(),
            logOutput,
            diagnostics,
            "Click Generate Recommendation to send the current troubleshooting snapshot to the configured AI insights endpoint.");
    }

    private static (long Start, long End) NormalizeTroubleshootRange(long start, long end) =>
        (RoundDownMinute(start), RoundDownMinute(end));

    private static string TroubleshootCacheKey(long start, long end, bool bySystem, string baseline)
    {
        var normalized = NormalizeTroubleshootRange(start, end);
        return $"{normalized.Start}:{normalized.End}:{bySystem}:{(baseline ?? string.Empty).Trim().ToLowerInvariant()}";
    }

    private static long RoundDownMinute(long value) => value - value % 60;

    public async Task<TrRfAnalysisDto> BuildRfAnalysisAsync(string? system, long start, long end, CancellationToken ct)
    {
        var allRows = await ListHealthSamplesWithExactBoundariesAsync(start, end, ct);
        var systems = allRows
            .Where(r => IsDisplaySystemScope(r.Scope))
            .Select(r => r.Scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var selectedSystem = string.IsNullOrWhiteSpace(system)
            ? systems.FirstOrDefault() ?? string.Empty
            : system.Trim();
        var rows = allRows
            .Where(r => string.Equals(r.Scope, selectedSystem, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.WindowStartUtc)
            .ToList();
        var aggregate = Aggregate(rows);
        var metrics = BuildRfMetricRows(aggregate);
        var durationSeconds = Math.Max(1, end - start);
        var priorStart = start - durationSeconds;
        var priorRows = (await ListHealthSamplesWithExactBoundariesAsync(priorStart, start, ct))
            .Where(r => string.Equals(r.Scope, selectedSystem, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var prior = Aggregate(priorRows);
        var comparison = BuildRfComparisonRows(aggregate, prior);
        var retuneTargets = await BuildRetuneTargetsAsync(selectedSystem, start, end, ct);
        var recommendations = BuildRfRecommendations(aggregate, retuneTargets);
        var hasEnoughPostChangeData = rows.Count >= 24 && (rows.Max(r => r.WindowEndUtc) - rows.Min(r => r.WindowStartUtc)).TotalHours >= 2;
        var summary = rows.Count == 0
            ? $"No stored TR health rows were found for {selectedSystem} in this window."
            : $"{selectedSystem}: CC summary average {aggregate.CcSummaryAvgDecodeRate:F2} msg/sec, CC zero {aggregate.CcSummaryDecodeZeroPercent:F1}%, {aggregate.LowDecodeWarningLines:N0} CC message-rate samples, {aggregate.Retunes:N0} retunes.";
        return new TrRfAnalysisDto(
            selectedSystem,
            start,
            end,
            $"{DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime:g} - {DateTimeOffset.FromUnixTimeSeconds(end).LocalDateTime:g}",
            hasEnoughPostChangeData,
            summary,
            metrics,
            comparison,
            retuneTargets,
            recommendations);
    }

    public async Task<TroubleshootInsightResponse> GenerateInsightsAsync(long start, long end, bool bySystem, string baseline, CancellationToken ct)
    {
        if (!_config.AiInsights.Enabled)
            return new TroubleshootInsightResponse("AI insights are disabled in Settings > ai-insights.");

        var baseUrl = InsightBaseUrl().TrimEnd('/');
        var model = InsightModel();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return new TroubleshootInsightResponse("AI insights require aiInsights.openAiBaseUrl and aiInsights.openAiModel, or fallback transcription OpenAI-compatible settings.");

        var snapshot = await BuildAsync(start, end, bySystem, baseline, ct);
        var prompt = BuildTroubleshootInsightPrompt(snapshot, start, end, bySystem, baseline);
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = "You are a practical trunk-recorder and radio-transcription troubleshooting assistant. Give concise, actionable recommendations. Prefer concrete next checks over generic advice." },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            max_tokens = 1200
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling troubleshoot insights endpoint {Endpoint} with model {Model} ({PayloadChars} chars)", $"{baseUrl}/chat/completions", model, payload.Length);
        Exception? last = null;
        for (var attempt = 0; attempt <= Math.Max(0, _config.AiInsights.MaxRetries); attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync($"{baseUrl}/chat/completions", content, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Troubleshoot insights request failed with HTTP {(int)response.StatusCode}: {Trim(text, 1000)}");
                return new TroubleshootInsightResponse(ExtractChatContent(text));
            }
            catch (Exception ex) when (attempt < Math.Max(0, _config.AiInsights.MaxRetries))
            {
                last = ex;
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        return new TroubleshootInsightResponse($"Troubleshoot insights failed: {last?.Message ?? "unknown error"}");
    }

    private static QualityAuditDto BuildQualityAudit(List<EngineCall> calls)
    {
        static bool Problem(EngineCall call) => TranscriptionQualityClassifier.IsProblem(call);
        static bool Inaudible(EngineCall call) => call.QualityReason is "inaudible" or "blank_audio" or "marker_only";
        static string SystemLabel(EngineCall call) => string.IsNullOrWhiteSpace(call.SystemShortName) ? "Unknown system" : call.SystemShortName;
        static string TalkgroupLabel(EngineCall call) => string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"TG {call.Talkgroup}" : call.TalkgroupName;
        static QualityAuditGroupDto GroupRow(string label, IEnumerable<EngineCall> group)
        {
            var rows = group.ToList();
            var total = rows.Count;
            var problems = rows.Count(Problem);
            var inaudible = rows.Count(Inaudible);
            return new QualityAuditGroupDto(
                label,
                total,
                problems,
                inaudible,
                Percent(problems, total),
                Percent(inaudible, total));
        }

        static QualityAuditGroupDto ReasonRow(string label, IEnumerable<EngineCall> group, int totalProblems)
        {
            var rows = group.ToList();
            var count = rows.Count;
            var inaudible = rows.Count(Inaudible);
            return new QualityAuditGroupDto(
                label,
                count,
                count,
                inaudible,
                Percent(count, totalProblems),
                Percent(inaudible, count));
        }

        var total = calls.Count;
        var problemCalls = calls.Count(Problem);
        var inaudibleCalls = calls.Count(Inaudible);
        var problems = calls.Where(Problem).ToList();
        return new QualityAuditDto(
            total,
            problemCalls,
            inaudibleCalls,
            Percent(problemCalls, total),
            Percent(inaudibleCalls, total),
            problems.GroupBy(c => string.IsNullOrWhiteSpace(c.QualityReason) ? "unknown" : c.QualityReason, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => ReasonRow(g.Key, g, problemCalls))
                .ToList(),
            calls.GroupBy(SystemLabel, StringComparer.OrdinalIgnoreCase)
                .Select(g => GroupRow(g.Key, g))
                .Where(r => r.ProblemCalls > 0)
                .OrderByDescending(r => r.InaudibleCalls)
                .ThenByDescending(r => r.ProblemCalls)
                .Take(12)
                .ToList(),
            calls.GroupBy(c => $"{TalkgroupLabel(c)} ({c.Talkgroup})", StringComparer.OrdinalIgnoreCase)
                .Select(g => GroupRow(g.Key, g))
                .Where(r => r.ProblemCalls > 0)
                .OrderByDescending(r => r.InaudibleCalls)
                .ThenByDescending(r => r.ProblemCalls)
                .Take(12)
                .ToList(),
            calls.GroupBy(c => DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().Hour)
                .Select(g => new QualityAuditHourDto(g.Key, g.Count(), g.Count(Problem), g.Count(Inaudible)))
                .OrderBy(r => r.Hour)
                .ToList(),
            problems
                .OrderByDescending(c => Inaudible(c))
                .ThenByDescending(c => c.StartTime)
                .Take(30)
                .Select(c => new QualityAuditSampleDto(
                    c.Id,
                    c.StartTime,
                    SystemLabel(c),
                    c.Source,
                    c.Talkgroup,
                    TalkgroupLabel(c),
                    c.Category,
                    Math.Max(0, c.StopTime - c.StartTime),
                    c.TranscriptionStatus,
                    c.QualityReason,
                    c.Transcription,
                    $"/api/v1/calls/{c.Id}/audio"))
                .ToList());
    }

    private static double Percent(int count, int total) =>
        total <= 0 ? 0 : count * 100.0 / total;

    private TrHealthSummaryDto BuildSummary(
        List<TrHealthSampleDto> recentRows,
        List<TrHealthSampleDto> selectedRows,
        List<TrHealthSampleDto> baselineRows,
        List<EngineCall> selectedCalls,
        IReadOnlyDictionary<string, IReadOnlyList<long>> observedFrequencies,
        bool bySystem,
        string baseline,
        long start,
        long end)
    {
        if (recentRows.Count == 0)
        {
            return new TrHealthSummaryDto
            {
                Title = "TR health summary unavailable",
                Window = FormatHealthWindow(start, end),
                Source = "No Trunk Recorder health rows are stored for this RF-health window.",
                SummaryText = "No usable TR health rows are available yet.",
                Samples = selectedRows.OrderByDescending(r => r.WindowStartUtc).Take(200).ToList()
            };
        }

        var global = AggregateForGlobalDisplay(recentRows);
        var metrics = BuildMetricRows(global);
        var systemSummaries = BuildSystemSummaries(recentRows, baselineRows, start);
        var systemRows = BuildSystemRows(systemSummaries);
        var remedies = BuildRemedies(global);
        var last = recentRows.Max(r => r.WindowEndUtc);
        var hasIssue = metrics.Any(m => m.IsIssue) || systemRows.Any(m => m.IsIssue);

        return new TrHealthSummaryDto
        {
            Title = hasIssue ? "TR health summary: issues detected" : "TR health summary: no obvious issues",
            Window = FormatHealthWindow(start, end),
            LastWindow = $"Last parsed bucket: {last.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            Source = $"Source: pizzad SQLite health samples from journald service '{_config.TrunkRecorder.LogServiceName}'",
            SummaryText = BuildSummaryText(global, systemRows, last),
            Metrics = metrics,
            Systems = systemRows.Count == 0 ? [new TrHealthMetricDto("No system rows", "-", "No system-scoped log lines were parsed for this window.", false)] : systemRows,
            SystemSummaries = systemSummaries,
            SourceCoverage = BuildSourceCoverage(selectedCalls),
            SourcePlan = BuildSourcePlan(selectedCalls, observedFrequencies),
            Remedies = remedies,
            Charts = BuildCharts(selectedRows, baselineRows, bySystem, baseline, start, end),
            Samples = selectedRows.OrderByDescending(r => r.WindowStartUtc).Take(500).ToList()
        };
    }

    private static List<TrHealthMetricDto> BuildMetricRows(TrHealthAggregate agg) =>
    [
        new("CC summary decode rate", FormatCcSummaryDecodeRateWithConfidence(agg), "Periodic trunk-recorder Control Channel Decode Rates value. This is the normal msg/sec metric discussed by TR users.", HasSufficientCcSummarySamples(agg) && agg.CcSummaryAvgDecodeRate < 10.0),
        new("CC summary zero rate", $"{agg.CcSummaryDecodeZeroPercent:F2}%", "Percent of periodic Control Channel Decode Rates samples that were 0 msg/sec; lower is better.", HasSufficientCcSummarySamples(agg) && agg.CcSummaryDecodeZeroPercent >= 10.0),
        new("CC message-rate samples", agg.LowDecodeWarningLines.ToString("N0", CultureInfo.InvariantCulture), "Count of per-frequency Control Channel Message Decode Rate samples. These are short-interval source samples, not warning log lines.", false),
        new("Message-rate zero rate", $"{agg.LowDecodeWarningZeroPercent:F2}%", "Percent of per-frequency Control Channel Message Decode Rate samples reporting 0/sec.", agg.LowDecodeWarningLines >= 20 && agg.LowDecodeWarningZeroPercent >= 25.0),
        new("Control-channel retunes", agg.Retunes.ToString("N0", CultureInfo.InvariantCulture), "High counts can indicate weak/unstable control-channel reception or incorrect channel list.", agg.Retunes >= Math.Max(20, agg.Windows * 4)),
        new("Recorded calls concluded", agg.CallsConcluded.ToString("N0", CultureInfo.InvariantCulture), "Count of Concluding Recorded Call lines.", false),
        new("No transmissions recorded", agg.NoTxRecorded.ToString("N0", CultureInfo.InvariantCulture), "Calls where trunk-recorder reported no transmission audio was recorded.", agg.NoTxRecorded > 0),
        new("Recorder capacity exhausted", agg.RecorderExhausted.ToString("N0", CultureInfo.InvariantCulture), "Calls dropped because all configured recorders for the needed source/system were busy.", agg.RecorderExhausted > 0),
        new("TR CPU max", agg.MaxTrCpuPercent > 0 ? $"{agg.MaxTrCpuPercent:F0}%" : "N/A", "Maximum sampled trunk-recorder CPU percent in this health window. 100% is one fully saturated core.", agg.MaxTrCpuPercent >= 70),
        new("TR RSS max", agg.MaxTrRssMb > 0 ? $"{agg.MaxTrRssMb:F0} MB" : "N/A", "Maximum resident memory used by trunk-recorder. This is real memory, unlike virtual address size.", agg.MaxTrRssMb >= 1536),
        new("TR threads max", agg.MaxTrThreadCount > 0 ? agg.MaxTrThreadCount.ToString("N0", CultureInfo.InvariantCulture) : "N/A", "Maximum sampled trunk-recorder thread count.", agg.MaxTrThreadCount >= 250),
        new("Host temp max", agg.MaxHostTempC > 0 ? $"{agg.MaxHostTempC:F1} C" : "N/A", "Maximum sampled host thermal-zone temperature.", agg.MaxHostTempC >= 75),
        new("Host throttle flags", string.IsNullOrWhiteSpace(agg.LatestHostThrottledFlags) ? "N/A" : agg.LatestHostThrottledFlags, "Raspberry Pi vcgencmd throttling flags when available.", IsThrottledFlagSet(agg.LatestHostThrottledFlags)),
        new("Sample-source stops", agg.SampleStops.ToString("N0", CultureInfo.InvariantCulture), "Nonzero means trunk-recorder reported a source stopped receiving samples.", agg.SampleStops > 0),
        new("No source covering frequency", agg.UnableSource.ToString("N0", CultureInfo.InvariantCulture), "Usually points to source min/max coverage or SDR count/configuration.", agg.UnableSource > 0),
        new("5-minute windows parsed", agg.Windows.ToString("N0", CultureInfo.InvariantCulture), "Number of summary buckets generated from logs.", false)
    ];

    private static List<TrHealthMetricDto> BuildSystemRows(IReadOnlyList<TrSystemHealthDto> summaries) =>
        summaries.Select(summary => new TrHealthMetricDto(
            summary.SystemShortName,
            summary.Status,
            summary.Summary,
            summary.IsIssue)).ToList();

    private static List<TrSystemHealthDto> BuildSystemSummaries(List<TrHealthSampleDto> rows, List<TrHealthSampleDto> allRows, long selectedStart)
    {
        return rows
            .Where(r => IsDisplaySystemScope(r.Scope))
            .GroupBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var currentRows = g.ToList();
                var agg = Aggregate(currentRows);
                var historyRows = allRows.Where(row =>
                    string.Equals(row.Scope, g.Key, StringComparison.OrdinalIgnoreCase)
                    && new DateTimeOffset(row.WindowEndUtc).ToUnixTimeSeconds() < selectedStart).ToList();
                var baselineAvailable = HasLocalBaseline(historyRows);
                var history = baselineAvailable ? Aggregate(historyRows) : null;
                var sufficient = HasSufficientCcSummarySamples(agg);
                var noTxPercent = agg.CallsConcluded > 0 ? agg.NoTxRecorded * 100.0 / agg.CallsConcluded : 0;
                var unavailable = sufficient && (agg.CcSummaryDecodeZeroPercent >= 90.0 || agg.CcSummaryAvgDecodeRate < 1.0);
                var baselineNoTxPercent = history is { CallsConcluded: > 0 } ? history.NoTxRecorded * 100.0 / history.CallsConcluded : (double?)null;
                var callsPerHour = EventsPerHour(currentRows, row => row.CallsConcluded);
                var retunesPerHour = EventsPerHour(currentRows, row => row.Retunes);
                var baselineCallsPerHour = baselineAvailable ? EventsPerHour(historyRows, row => row.CallsConcluded) : (double?)null;
                var baselineRetunesPerHour = baselineAvailable ? EventsPerHour(historyRows, row => row.Retunes) : (double?)null;
                var decodeAssessment = AssessDecode(agg, history?.CcSummaryAvgDecodeRate);
                var zeroAssessment = AssessZeroDecode(agg, history?.CcSummaryDecodeZeroPercent);
                var callsAssessment = AssessCalls(callsPerHour, baselineCallsPerHour, unavailable);
                var noAudioAssessment = AssessNoAudio(agg.CallsConcluded, noTxPercent, baselineNoTxPercent);
                var retunesAssessment = AssessRetunes(retunesPerHour, baselineRetunesPerHour, unavailable);
                var tones = new[] { decodeAssessment.Tone, zeroAssessment.Tone, callsAssessment.Tone, noAudioAssessment.Tone, retunesAssessment.Tone };
                var degraded = tones.Contains("warning", StringComparer.Ordinal);
                var critical = tones.Contains("error", StringComparer.Ordinal);
                var status = !sufficient ? "Insufficient data" : unavailable ? "Unavailable" : critical ? "Critical" : degraded ? "Needs review" : "Healthy";
                var summary = unavailable
                    ? $"Control-channel decode was zero in {agg.CcSummaryDecodeZeroPercent:F1}% of {agg.CcSummaryDecodeSampleLines:N0} summary samples."
                    : sufficient
                        ? $"Control-channel decode averaged {agg.CcSummaryAvgDecodeRate:F1} msg/s with {agg.CcSummaryDecodeZeroPercent:F1}% zero samples; {noTxPercent:F1}% of concluded calls had no audio."
                        : $"Only {agg.CcSummaryDecodeSampleLines:N0} control-channel summary sample(s) are available.";
                return new TrSystemHealthDto(
                    g.Key,
                    status,
                    summary,
                    agg.Windows,
                    agg.CcSummaryDecodeSampleLines,
                    agg.CcSummaryAvgDecodeRate,
                    agg.CcSummaryDecodeZeroPercent,
                    agg.Retunes,
                    agg.CallsConcluded,
                    agg.NoTxRecorded,
                    agg.RecorderExhausted,
                    agg.SampleStops,
                    agg.UnableSource,
                    callsPerHour,
                    retunesPerHour,
                    decodeAssessment,
                    zeroAssessment,
                    callsAssessment,
                    noAudioAssessment,
                    retunesAssessment,
                    g.Max(row => row.WindowEndUtc),
                    unavailable || critical || degraded);
            })
            .ToList();
    }

    private static bool HasLocalBaseline(List<TrHealthSampleDto> rows)
    {
        if (rows.Count < 72)
            return false;
        return rows.Max(row => row.WindowEndUtc) - rows.Min(row => row.WindowStartUtc) >= TimeSpan.FromHours(12);
    }

    private static double EventsPerHour(List<TrHealthSampleDto> rows, Func<TrHealthSampleDto, int> selector)
    {
        var observedHours = rows.Sum(row => Math.Max(1.0, (row.WindowEndUtc - row.WindowStartUtc).TotalSeconds)) / 3600.0;
        return observedHours <= 0 ? 0 : rows.Sum(selector) / observedHours;
    }

    private static TrMetricAssessmentDto AssessDecode(TrHealthAggregate current, double? baseline)
        => AssessDecodeRate(current.CcSummaryDecodeSampleLines, current.CcSummaryAvgDecodeRate, current.CcSummaryDecodeZeroPercent, baseline, 10);

    public static TrMetricAssessmentDto AssessDecodeRate(int samples, double averageRate, double zeroPercent, double? baseline, int minimumSamples = 10)
    {
        if (samples < minimumSamples)
            return new("warning", "insufficient", baseline, "Not enough control-channel samples to classify.");
        if (zeroPercent >= 90.0 || averageRate < 1.0)
            return new("error", "critical", baseline, "Control-channel decoding is effectively unavailable.");
        if (baseline.HasValue)
        {
            var floor = Math.Max(10.0, baseline.Value * 0.75);
            return averageRate < floor
                ? new("warning", "local", baseline, $"Below the local {baseline.Value:F1} msg/s baseline; 40 msg/s remains the strong-system reference.")
                : new("ok", "local", baseline, $"Within the local {baseline.Value:F1} msg/s baseline; 40 msg/s is the strong-system reference.");
        }
        return averageRate >= 35.0
            ? new("ok", "static", null, "Near the 40 msg/s strong-system reference; no local baseline is mature yet.")
            : averageRate >= 10.0
                ? new("warning", "static", null, "Below the 40 msg/s strong-system reference; local baseline is not mature yet.")
                : new("error", "critical", null, "Far below the strong-system reference and approaching decode loss.");
    }

    private static TrMetricAssessmentDto AssessZeroDecode(TrHealthAggregate current, double? baseline)
        => AssessZeroDecodeRate(current.CcSummaryDecodeSampleLines, current.CcSummaryDecodeZeroPercent, baseline, 10);

    public static TrMetricAssessmentDto AssessZeroDecodeRate(int samples, double zeroPercent, double? baseline, int minimumSamples = 10)
    {
        if (samples < minimumSamples)
            return new("warning", "insufficient", baseline, "Not enough control-channel samples to classify.");
        if (zeroPercent >= 50.0)
            return new("error", "critical", baseline, "At least half of control-channel samples decoded nothing.");
        if (baseline.HasValue)
        {
            var ceiling = Math.Max(baseline.Value + 5.0, baseline.Value * 2.0);
            return zeroPercent > ceiling
                ? new("warning", "local", baseline, $"Above the local {baseline.Value:F1}% zero-decode baseline.")
                : new("ok", "local", baseline, $"Within the local {baseline.Value:F1}% zero-decode baseline.");
        }
        return zeroPercent < 10.0
            ? new("ok", "static", null, "Below the static 10% review threshold; no local baseline is mature yet.")
            : new("warning", "static", null, "Above the static 10% review threshold; local baseline is not mature yet.");
    }

    private static TrMetricAssessmentDto AssessCalls(double currentPerHour, double? baselinePerHour, bool unavailable)
    {
        if (unavailable && currentPerHour <= 0)
            return new("error", "critical", baselinePerHour, "No calls were observed while control-channel decoding was unavailable.");
        if (baselinePerHour.HasValue && baselinePerHour.Value >= 1.0)
        {
            var ratio = currentPerHour / baselinePerHour.Value;
            return ratio < 0.1
                ? new("error", "local", baselinePerHour, $"Call activity is under 10% of the local {baselinePerHour.Value:F1}/hour baseline.")
                : ratio < 0.5
                    ? new("warning", "local", baselinePerHour, $"Call activity is below half of the local {baselinePerHour.Value:F1}/hour baseline.")
                    : new("ok", "local", baselinePerHour, $"Call activity is consistent with the local {baselinePerHour.Value:F1}/hour baseline.");
        }
        return currentPerHour > 0
            ? new("ok", "observed", null, "Calls were observed; no mature local activity baseline is available.")
            : new("warning", "undetermined", null, "No calls were observed, but no local activity baseline is available for comparison.");
    }

    private static TrMetricAssessmentDto AssessNoAudio(int calls, double percent, double? baselinePercent)
    {
        if (calls == 0)
            return new("warning", "undetermined", baselinePercent, "No concluded calls are available for audio-outcome classification.");
        if (percent >= 25.0 || (baselinePercent.HasValue && percent >= Math.Max(25.0, baselinePercent.Value * 3.0)))
            return new("error", "critical", baselinePercent, "At least one quarter of concluded calls had no recorded audio.");
        if (baselinePercent.HasValue)
        {
            var ceiling = Math.Max(baselinePercent.Value + 2.5, baselinePercent.Value * 1.5);
            return percent > ceiling
                ? new("warning", "local", baselinePercent, $"Above the local {baselinePercent.Value:F1}% no-audio baseline.")
                : new("ok", "local", baselinePercent, $"Within the local {baselinePercent.Value:F1}% no-audio baseline.");
        }
        return percent <= 7.5
            ? new("ok", "static", null, "Within the temporary 7.5% fallback threshold; no local baseline is mature yet.")
            : new("warning", "static", null, "Above the temporary 7.5% fallback threshold; local baseline is not mature yet.");
    }

    public static TrMetricAssessmentDto AssessRetunes(double currentPerHour, double? baselinePerHour, bool unavailable)
    {
        if (unavailable && currentPerHour <= 0)
            return new("warning", "undetermined", baselinePerHour, "Retune behavior cannot be evaluated while control-channel decoding is unavailable.");
        if (baselinePerHour.HasValue)
        {
            if (currentPerHour >= Math.Max(96.0, baselinePerHour.Value * 4.0))
                return new("error", "critical", baselinePerHour, "Retunes are at least four times the local rate and critically elevated.");
            var ceiling = Math.Max(baselinePerHour.Value + 6.0, baselinePerHour.Value * 1.75);
            return currentPerHour > ceiling
                ? new("warning", "local", baselinePerHour, $"Above the local {baselinePerHour.Value:F1}/hour retune baseline.")
                : new("ok", "local", baselinePerHour, $"Within the local {baselinePerHour.Value:F1}/hour retune baseline.");
        }
        return currentPerHour <= 48.0
            ? new("ok", "static", null, "Below the temporary 48/hour fallback threshold; no local baseline is mature yet.")
            : currentPerHour < 96.0
                ? new("warning", "static", null, "Above the temporary 48/hour fallback threshold; local baseline is not mature yet.")
                : new("error", "critical", null, "Retunes exceed the critical 96/hour fallback threshold.");
    }

    private static string FormatHealthWindow(long start, long end)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(1, end - start));
        var durationText = duration.TotalHours >= 24 && Math.Abs(duration.TotalHours % 24) < 0.05
            ? $"{duration.TotalDays:F0} day{(Math.Round(duration.TotalDays) == 1 ? string.Empty : "s")}"
            : $"{duration.TotalHours:F0} hour{(Math.Round(duration.TotalHours) == 1 ? string.Empty : "s")}";
        return $"RF-health window: {durationText}, ending {DateTimeOffset.FromUnixTimeSeconds(end).LocalDateTime:g}";
    }

    private static List<TrHealthMetricDto> BuildRemedies(TrHealthAggregate agg)
    {
        var rows = new List<TrHealthMetricDto>();
        if (agg.SampleStops > 0)
            rows.Add(new("Sample-source stops", "-", "Check SDR USB stability, power, hub/cable quality, and whether the SDR process is being starved.", true));
        if (HasSufficientCcSummarySamples(agg) && agg.CcSummaryAvgDecodeRate < 10.0)
            rows.Add(new("Low CC summary decode rate", "-", "The periodic Control Channel Decode Rates average is low. Verify antenna/feedline, gain/PPM calibration, local RF noise, and whether this host can reliably hear the active control channel.", true));
        if (HasSufficientCcSummarySamples(agg) && agg.CcSummaryDecodeZeroPercent >= 10.0)
            rows.Add(new("CC summary decode hits zero", "-", "The normal control-channel summary occasionally reaches 0 msg/sec, which indicates real control-channel decode loss.", true));
        if (agg.LowDecodeWarningLines >= 20 && agg.LowDecodeWarningZeroPercent >= 25.0)
            rows.Add(new("Frequent zero message-rate samples", "-", "TR is repeatedly logging per-frequency Control Channel Message Decode Rate samples near or at zero. Compare these with retune targets to identify weak learned alternates or RF dips.", true));
        if (agg.Retunes >= Math.Max(20, agg.Windows * 4))
            rows.Add(new("High retunes", "-", "Confirm configured control channels and site coverage; frequent retunes often mean weak/unstable control-channel decode.", true));
        if (agg.UnableSource > 0)
            rows.Add(new("No source covering frequency", "-", "Check source min/max ranges and whether enough SDRs cover all voice channels.", true));
        if (agg.NoTxRecorded > 0)
            rows.Add(new("No transmissions recorded", "-", "This can follow UPDATE-not-GRANT events or poor decode where trunk-recorder starts a call but captures no usable transmission audio.", true));
        if (agg.RecorderExhausted > 0)
            rows.Add(new("Recorder capacity exhausted", "-", "Increase digitalRecorders or SDR coverage for busy systems; trunk-recorder reported calls it could not assign because no recorder was free.", true));
        if (rows.Count == 0)
            rows.Add(new("No obvious remedies", "-", "The current summary did not cross the built-in thresholds.", false));
        return rows;
    }

    private static IReadOnlyList<TrHealthMetricDto> BuildRfMetricRows(TrHealthAggregate agg) =>
    [
        new("CC summary decode rate", FormatCcSummaryDecodeRateWithConfidence(agg), "Periodic Control Channel Decode Rates msg/sec. This is the primary RF-health decode metric.", HasSufficientCcSummarySamples(agg) && agg.CcSummaryAvgDecodeRate < 10.0),
        new("CC summary zero rate", $"{agg.CcSummaryDecodeZeroPercent:F2}%", $"{agg.CcSummaryDecodeZero:N0} zero sample(s) across {agg.CcSummaryDecodeSampleLines:N0} CC summary sample(s).", HasSufficientCcSummarySamples(agg) && agg.CcSummaryDecodeZeroPercent >= 10.0),
        new("CC message-rate samples", agg.LowDecodeWarningLines.ToString("N0", CultureInfo.InvariantCulture), "Per-frequency Control Channel Message Decode Rate samples. These are evidence volume, not an issue by themselves; the zero-rate row owns the condition.", false),
        new("Message-rate zero rate", $"{agg.LowDecodeWarningZeroPercent:F2}%", $"{agg.LowDecodeWarningZero:N0} zero sample(s) across {agg.LowDecodeWarningLines:N0} per-frequency message-rate sample(s).", agg.LowDecodeWarningLines >= 20 && agg.LowDecodeWarningZeroPercent >= 25.0),
        new("Retunes", agg.Retunes.ToString("N0", CultureInfo.InvariantCulture), "Control-channel retune events in the selected window.", agg.Retunes >= Math.Max(20, agg.Windows * 4)),
        new("No transmissions", agg.NoTxRecorded.ToString("N0", CultureInfo.InvariantCulture), agg.CallsConcluded > 0 ? $"{agg.NoTxRecorded * 100.0 / agg.CallsConcluded:F2}% of concluded calls." : "No concluded calls in window.", agg.NoTxRecorded > 0),
        new("Recorder exhausted", agg.RecorderExhausted.ToString("N0", CultureInfo.InvariantCulture), "Calls TR could not assign because no recorder was free.", agg.RecorderExhausted > 0),
        new("TR CPU max", agg.MaxTrCpuPercent > 0 ? $"{agg.MaxTrCpuPercent:F0}%" : "N/A", "Maximum sampled trunk-recorder CPU percent. 100% is one fully saturated core.", agg.MaxTrCpuPercent >= 70),
        new("TR RSS max", agg.MaxTrRssMb > 0 ? $"{agg.MaxTrRssMb:F0} MB" : "N/A", "Maximum resident memory used by trunk-recorder.", agg.MaxTrRssMb >= 1536),
        new("Host temp max", agg.MaxHostTempC > 0 ? $"{agg.MaxHostTempC:F1} C" : "N/A", "Maximum sampled host thermal-zone temperature.", agg.MaxHostTempC >= 75),
        new("Health rows", agg.Windows.ToString("N0", CultureInfo.InvariantCulture), "Stored 5-minute health buckets used in this analysis.", agg.Windows == 0)
    ];

    private static IReadOnlyList<TrHealthMetricDto> BuildRfComparisonRows(TrHealthAggregate current, TrHealthAggregate prior)
    {
        static string Delta(double current, double prior, string suffix = "") => prior == 0 && current == 0
            ? $"0{suffix}"
            : $"{current - prior:+0.##;-0.##;0}{suffix}";
        static string DeltaInt(int current, int prior) => $"{current - prior:+#,##0;-#,##0;0}";
        return
        [
            new("Compared with previous equal window", prior.Windows > 0 ? "Available" : "Unavailable", prior.Windows > 0 ? $"{prior.Windows:N0} prior health bucket(s)." : "No prior rows available for this system/window length.", false),
            new("CC summary decode delta", Delta(current.CcSummaryAvgDecodeRate, prior.CcSummaryAvgDecodeRate, " msg/sec"), $"Current {current.CcSummaryAvgDecodeRate:F2}, previous {prior.CcSummaryAvgDecodeRate:F2}.", current.CcSummaryAvgDecodeRate < prior.CcSummaryAvgDecodeRate),
            new("CC zero-rate delta", Delta(current.CcSummaryDecodeZeroPercent, prior.CcSummaryDecodeZeroPercent, "%"), $"Current {current.CcSummaryDecodeZeroPercent:F2}%, previous {prior.CcSummaryDecodeZeroPercent:F2}%.", current.CcSummaryDecodeZeroPercent > prior.CcSummaryDecodeZeroPercent),
            new("Low-warning delta", DeltaInt(current.LowDecodeWarningLines, prior.LowDecodeWarningLines), $"Current {current.LowDecodeWarningLines:N0}, previous {prior.LowDecodeWarningLines:N0}.", current.LowDecodeWarningLines > prior.LowDecodeWarningLines),
            new("Retune delta", DeltaInt(current.Retunes, prior.Retunes), $"Current {current.Retunes:N0}, previous {prior.Retunes:N0}.", current.Retunes > prior.Retunes),
            new("No-transmission delta", DeltaInt(current.NoTxRecorded, prior.NoTxRecorded), $"Current {current.NoTxRecorded:N0}, previous {prior.NoTxRecorded:N0}.", current.NoTxRecorded > prior.NoTxRecorded)
        ];
    }

    private static IReadOnlyList<TrHealthMetricDto> BuildRfRecommendations(TrHealthAggregate agg, IReadOnlyList<TrHealthMetricDto> retuneTargets)
    {
        var rows = new List<TrHealthMetricDto>();
        if (!HasSufficientCcSummarySamples(agg))
            rows.Add(new("Collect more data", "Wait", "This window has too few periodic CC summary samples for a confident RF conclusion.", false));
        if (HasSufficientCcSummarySamples(agg) && agg.CcSummaryAvgDecodeRate < 10.0)
            rows.Add(new("Investigate RF path", "Recommended", "CC summary decode rate is below 10 msg/sec. Recheck active CC lock, gain/error, antenna/feedline path, and local RF noise.", true));
        if (agg.Retunes > 0 && retuneTargets.Count(r => r.IsIssue) > 1)
            rows.Add(new("Inspect learned alternates", "Recommended", "Retunes are distributed across multiple target frequencies. If some alternates decode poorly, TR may be learning channels this RF path cannot reliably hear.", true));
        if (agg.LowDecodeWarningLines >= Math.Max(20, agg.Windows * 2))
            rows.Add(new("Correlate message-rate samples to retunes", "Recommended", "Zero or low message-rate sample volume is high. Compare sample bursts with retune targets before changing gain again.", true));
        if (agg.NoTxRecorded > 0)
            rows.Add(new("Review no-transmission calls", "Optional", "No-transmission outcomes can follow poor control decode, UPDATE-not-GRANT behavior, or voice-path coverage problems.", true));
        if (rows.Count == 0)
            rows.Add(new("No obvious RF action", "OK", "This selected window does not cross the built-in RF-analysis thresholds.", false));
        return rows;
    }

    private IReadOnlyList<TrSourceCoverageDto> BuildSourceCoverage(List<EngineCall> selectedCalls)
    {
        var sources = ReadSourceWindows();
        if (sources.Count == 0)
            return [];

        var rows = sources.Select(source =>
        {
            var coverable = selectedCalls
                .Where(call => Covers(source, call.Frequency))
                .ToList();
            var firstMatch = selectedCalls
                .Where(call => sources.FirstOrDefault(sourceWindow => Covers(sourceWindow, call.Frequency))?.Index == source.Index)
                .ToList();
            var shadowed = coverable.Count > 0 && firstMatch.Count == 0;
            var mostlyShadowed = coverable.Count >= 20 && firstMatch.Count < Math.Max(2, coverable.Count * 0.05);
            var idle = selectedCalls.Count > 0 && coverable.Count == 0 && source.DigitalRecorders > 0;
            var notes = shadowed
                ? "Covered calls are already captured by an earlier source window; this SDR may be shadowed."
                : mostlyShadowed
                    ? "Most covered calls are captured by an earlier overlapping source; review source ordering and center/rate overlap."
                : idle
                    ? "No selected-range call frequencies fall inside this source window."
                    : $"First-match frequencies: {firstMatch.Select(c => c.Frequency).Distinct().Count():N0}.";

            return new TrSourceCoverageDto(
                source.Index,
                source.Device,
                HzToMhz(source.CenterHz),
                HzToMhz(source.LowHz),
                HzToMhz(source.HighHz),
                source.DigitalRecorders,
                firstMatch.Count,
                coverable.Count,
                coverable.Select(c => c.Frequency).Distinct().Count(),
                notes,
                shadowed || mostlyShadowed || idle);
        }).ToList();

        var uncovered = selectedCalls
            .Where(call => call.Frequency > 0 && !sources.Any(source => Covers(source, call.Frequency)))
            .ToList();
        if (uncovered.Count > 0)
        {
            rows.Add(new TrSourceCoverageDto(
                -1,
                "Uncovered selected-range calls",
                0,
                0,
                0,
                0,
                0,
                uncovered.Count,
                uncovered.Select(c => c.Frequency).Distinct().Count(),
                "These persisted calls have frequencies outside every configured source window. Check source center/rate coverage.",
                true));
        }

        return rows;
    }

    private IReadOnlyList<TrSourcePlanDto> BuildSourcePlan(List<EngineCall> selectedCalls, IReadOnlyDictionary<string, IReadOnlyList<long>> observedFrequenciesBySystem)
    {
        var sources = ReadSourceWindows();
        var systems = ReadTrSystems();
        if (sources.Count == 0 || systems.Count == 0)
            return [];

        var sampleRate = (int)Math.Max(100_000, sources.FirstOrDefault()?.RateHz ?? 2_400_000);
        var rows = new List<TrSourcePlanDto>();
        var usedSources = new HashSet<int>();
        foreach (var system in systems.OrderBy(s => s.ShortName, StringComparer.OrdinalIgnoreCase))
        {
            var selectedObserved = selectedCalls
                .Where(c => string.Equals(c.SystemShortName, system.ShortName, StringComparison.OrdinalIgnoreCase))
                .Select(c => (long)Math.Round(c.Frequency))
                .Where(f => f > 0);
            var allObserved = observedFrequenciesBySystem.TryGetValue(system.ShortName, out var observedFrequencies)
                ? observedFrequencies
                : [];
            var frequencies = system.ControlChannelsHz
                .Concat(system.VoiceFrequenciesHz)
                .Concat(allObserved)
                .Concat(selectedObserved)
                .Select(NormalizeFrequency)
                .Distinct()
                .Order()
                .ToList();
            foreach (var range in CalculateRequiredRanges(frequencies, sampleRate))
            {
                var full = sources
                    .Where(source => source.LowHz <= range.LowHz && source.HighHz >= range.HighHz)
                    .OrderBy(source => Math.Abs(source.CenterHz - range.CenterHz))
                    .ThenBy(source => source.Index)
                    .FirstOrDefault();
                var best = full ?? BestSourceForRange(range, sources);
                if (best != null)
                    usedSources.Add(best.Index);
                var notes = full != null
                    ? "Covered by configured source window."
                    : best != null
                        ? "Only partially covered by configured source window; adjust center/rate or add another SDR."
                        : "No configured source covers this desired site range.";
                rows.Add(new TrSourcePlanDto(
                    system.ShortName,
                    HzToMhz(range.LowHz),
                    HzToMhz(range.HighHz),
                    HzToMhz(range.CenterHz),
                    best?.Index,
                    best?.Device ?? string.Empty,
                    notes,
                    full == null));
            }
        }

        foreach (var spare in sources.Where(s => !usedSources.Contains(s.Index) && s.DigitalRecorders > 0).OrderBy(s => s.Index))
        {
            rows.Add(new TrSourcePlanDto(
                "Spare SDR",
                HzToMhz(spare.LowHz),
                HzToMhz(spare.HighHz),
                HzToMhz(spare.CenterHz),
                spare.Index,
                spare.Device,
                "Not needed for the current desired site ranges. Keep as spare, retask to a missing range, or remove from active TR config if it is a known-bad RF path.",
                false));
        }

        return rows;
    }

    private List<TrSourceWindow> ReadSourceWindows()
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("sources", out var sourceElement) || sourceElement.ValueKind != JsonValueKind.Array)
                return [];
            return sourceElement.EnumerateArray()
                .Select((source, index) =>
                {
                    var center = ReadJsonNumber(source, "center");
                    var rate = ReadJsonNumber(source, "rate");
                    return new TrSourceWindow(
                        index,
                        source.TryGetProperty("device", out var device) ? device.GetString() ?? string.Empty : string.Empty,
                        center,
                        rate,
                        (int)Math.Max(0, ReadJsonNumber(source, "digitalRecorders")),
                        center - rate / 2.0,
                        center + rate / 2.0);
                })
                .Where(s => s.CenterHz > 0 && s.RateHz > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read TR source windows from {Path}", path);
            return [];
        }
    }

    private List<TrDesiredSystem> ReadTrSystems()
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("systems", out var systemElement) || systemElement.ValueKind != JsonValueKind.Array)
                return [];
            return systemElement.EnumerateArray()
                .Select(system =>
                {
                    var shortName = system.TryGetProperty("shortName", out var name) ? name.GetString() ?? "system" : "system";
                    var controls = ReadFrequencyArray(system, "control_channels");
                    var voice = ReadFrequencyArray(system, "channels").Concat(ReadFrequencyArray(system, "frequencies")).Distinct().Order().ToList();
                    return new TrDesiredSystem(shortName, controls, voice);
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read TR systems from {Path}", path);
            return [];
        }
    }

    private static bool Covers(TrSourceWindow source, double frequencyHz) =>
        frequencyHz > 0 && frequencyHz >= source.LowHz && frequencyHz <= source.HighHz;

    private static List<FrequencyRange> CalculateRequiredRanges(IReadOnlyList<long> frequencies, int sampleRate)
    {
        if (frequencies.Count == 0)
            return [];
        var usableWidth = Math.Max(100_000, (long)(sampleRate * 0.90));
        var sorted = frequencies.Order().ToList();
        var ranges = new List<FrequencyRange>();
        var current = new List<long>();
        foreach (var frequency in sorted)
        {
            if (current.Count == 0 || frequency - current[0] <= usableWidth)
            {
                current.Add(frequency);
                continue;
            }

            ranges.Add(ToRange(current));
            current = [frequency];
        }

        if (current.Count > 0)
            ranges.Add(ToRange(current));
        return ranges;
    }

    private static FrequencyRange ToRange(IReadOnlyList<long> values)
    {
        var low = values.Min();
        var high = values.Max();
        return new FrequencyRange(low, high, (low + high) / 2);
    }

    private static TrSourceWindow? BestSourceForRange(FrequencyRange range, IReadOnlyList<TrSourceWindow> sources)
    {
        return sources
            .Select(source => new { Source = source, Score = SourceRangeScore(source, range) })
            .OrderByDescending(row => row.Score)
            .ThenBy(row => Math.Abs(row.Source.CenterHz - range.CenterHz))
            .FirstOrDefault(row => row.Score > 0)?.Source;
    }

    private static double SourceRangeScore(TrSourceWindow source, FrequencyRange range)
    {
        var overlap = Math.Min(source.HighHz, range.HighHz) - Math.Max(source.LowHz, range.LowHz);
        return Math.Max(0, overlap);
    }

    private static List<long> ReadFrequencyArray(JsonElement system, string property)
    {
        if (!system.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        return values.EnumerateArray()
            .Select(ReadFrequency)
            .Where(v => v > 0)
            .Distinct()
            .Order()
            .ToList();
    }

    private static long ReadFrequency(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var longValue))
                return NormalizeFrequency(longValue);
            if (value.TryGetDouble(out var doubleValue))
                return NormalizeFrequency((long)Math.Round(doubleValue));
        }
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return NormalizeFrequency((long)Math.Round(parsed));
        return 0;
    }

    private static long NormalizeFrequency(long value) => value > 0 && value < 10_000 ? value * 1_000_000 : value;

    private static double ReadJsonNumber(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) => number,
            _ => 0
        };
    }

    private static double HzToMhz(double hz) => Math.Round(hz / 1_000_000.0, 6);

    private static IReadOnlyList<TrHealthChartDto> BuildCharts(List<TrHealthSampleDto> selectedRows, List<TrHealthSampleDto> baselineRows, bool bySystem, string baseline, long start, long end)
    {
        var rows = selectedRows.Count > 0 ? selectedRows : baselineRows.Where(r => new DateTimeOffset(r.WindowEndUtc).ToUnixTimeSeconds() >= DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds()).ToList();
        var labels = BuildHourLabels(rows);
        if (labels.Count == 0)
            return [];

        return bySystem
            ? BuildSystemCharts(rows, baselineRows, baseline, start, end)
            : BuildGlobalCharts(rows, baselineRows, labels, baseline);
    }

    private static IReadOnlyList<TrHealthChartDto> BuildGlobalCharts(List<TrHealthSampleDto> rows, List<TrHealthSampleDto> baselineRows, List<DateTime> hours, string baseline)
    {
        var global = rows.Where(IsGlobal).ToList();
        if (global.Count == 0)
            global = rows;
        var baselineGlobal = baselineRows.Where(IsGlobal).ToList();
        return
        [
            BuildChart("CC Summary Decode Rate", "Y axis: periodic Control Channel Decode Rates msg/sec", "F1", hours, [("", Hourly(global, hours, a => a.CcSummaryAvgDecodeRate))], baselineGlobal, baseline, a => a.CcSummaryAvgDecodeRate, BaselineIsRate: true),
            BuildChart("CC Summary Zero Rate", "Y axis: percent of periodic CC summary samples at 0 msg/sec", "F1", hours, [("", Hourly(global, hours, a => a.CcSummaryDecodeZeroPercent))], baselineGlobal, baseline, a => a.CcSummaryDecodeZeroPercent, BaselineIsRate: true),
            BuildChart("CC Message-Rate Samples", "Y axis: per-frequency Control Channel Message Decode Rate samples per hour", "N0", hours, [("", Hourly(global, hours, a => a.LowDecodeWarningLines))], baselineGlobal, baseline, a => a.LowDecodeWarningLines),
            BuildChart("Message-Rate Zero Rate", "Y axis: percent of per-frequency message-rate samples at 0/sec", "F1", hours, [("", Hourly(global, hours, a => a.LowDecodeWarningZeroPercent))], baselineGlobal, baseline, a => a.LowDecodeWarningZeroPercent, BaselineIsRate: true),
            BuildChart("Control-Channel Retunes", "Y axis: retune events per hour", "N0", hours, [("", Hourly(global, hours, a => a.Retunes))], baselineGlobal, baseline, a => a.Retunes),
            BuildChart("No Transmissions Recorded", "Y axis: calls with no recorded transmissions per hour", "N0", hours, [("", Hourly(global, hours, a => a.NoTxRecorded))], baselineGlobal, baseline, a => a.NoTxRecorded),
            BuildChart("Recorder Capacity Exhausted", "Y axis: calls dropped because no recorder was available per hour", "N0", hours, [("", Hourly(global, hours, a => a.RecorderExhausted))], baselineGlobal, baseline, a => a.RecorderExhausted),
            BuildChart("Sample Source Stops", "Y axis: source stopped receiving samples events per hour", "N0", hours, [("", Hourly(global, hours, a => a.SampleStops))], baselineGlobal, baseline, a => a.SampleStops)
        ];
    }

    private static IReadOnlyList<TrHealthChartDto> BuildSystemCharts(List<TrHealthSampleDto> rows, List<TrHealthSampleDto> baselineRows, string baseline, long start, long end)
    {
        var scopes = rows.Where(r => IsDisplaySystemScope(r.Scope))
            .Select(r => r.Scope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scopes.Count == 0)
            return [];

        var latestEvidence = rows.Max(row => new DateTimeOffset(row.WindowEndUtc).ToUnixTimeSeconds());
        var earliestEvidence = rows.Min(row => new DateTimeOffset(row.WindowStartUtc).ToUnixTimeSeconds());
        var buckets = BuildChartBuckets(Math.Max(start, earliestEvidence), Math.Min(end, latestEvidence));
        var baselineInfo = BaselineInfo(
            baselineRows.Where(r => scopes.Contains(r.Scope, StringComparer.OrdinalIgnoreCase)).ToList(),
            baseline,
            DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime);
        return
        [
            BuildScopedChart("Decode Rate", "Messages per second", "F1", scopes, rows, baselineRows, buckets, baselineInfo.Note, a => a.CcSummaryAvgDecodeRate),
            BuildScopedChart("Zero-Decode Samples", "Percent of control-channel samples", "F1", scopes, rows, baselineRows, buckets, baselineInfo.Note, a => a.CcSummaryDecodeZeroPercent),
            BuildScopedChart("Calls Recorded", "Calls per hour", "N0", scopes, rows, baselineRows, buckets, baselineInfo.Note, a => a.CallsConcluded, ratePerHour: true),
            BuildScopedChart("Calls Without Audio", "Percent of concluded calls", "F1", scopes, rows, baselineRows, buckets, baselineInfo.Note, a => a.CallsConcluded == 0 ? 0 : a.NoTxRecorded * 100.0 / a.CallsConcluded),
            BuildScopedChart("Control-Channel Retunes", "Retunes per hour", "F1", scopes, rows, baselineRows, buckets, baselineInfo.Note, a => a.Retunes, ratePerHour: true),
            BuildScopedEventChart(scopes, rows, buckets)
        ];
    }

    private static TrHealthChartDto BuildScopedChart(
        string title,
        string yAxis,
        string format,
        IReadOnlyList<string> scopes,
        List<TrHealthSampleDto> rows,
        List<TrHealthSampleDto> baselineRows,
        IReadOnlyList<TrChartBucket> buckets,
        string baselineNote,
        Func<TrHealthAggregate, double> selector,
        bool ratePerHour = false)
    {
        var series = new List<TrHealthSeriesDto>();
        foreach (var scope in scopes)
        {
            var scopedRows = rows.Where(row => string.Equals(row.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList();
            var scopedBaseline = baselineRows.Where(row => string.Equals(row.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList();
            series.Add(new TrHealthSeriesDto("Current", BucketValues(scopedRows, buckets, selector, ratePerHour), false, scope));
            var baselineValues = BaselineBucketValues(scopedBaseline, buckets, selector, ratePerHour);
            if (baselineValues.Any(value => value > 0))
                series.Add(new TrHealthSeriesDto("Local baseline", baselineValues, true, scope));
        }
        return new TrHealthChartDto(title, yAxis, format, buckets.Select(bucket => bucket.StartUtc.ToString("O", CultureInfo.InvariantCulture)).ToList(), series, baselineNote);
    }

    private static TrHealthChartDto BuildScopedEventChart(IReadOnlyList<string> scopes, List<TrHealthSampleDto> rows, IReadOnlyList<TrChartBucket> buckets)
    {
        var series = new List<TrHealthSeriesDto>();
        foreach (var scope in scopes)
        {
            var scopedRows = rows.Where(row => string.Equals(row.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList();
            series.Add(new TrHealthSeriesDto("Recorder capacity", BucketValues(scopedRows, buckets, a => a.RecorderExhausted, ratePerHour: true), false, scope));
            series.Add(new TrHealthSeriesDto("Source stops", BucketValues(scopedRows, buckets, a => a.SampleStops, ratePerHour: true), false, scope));
        }
        return new TrHealthChartDto("Capture Interruptions", "Events per hour", "F1", buckets.Select(bucket => bucket.StartUtc.ToString("O", CultureInfo.InvariantCulture)).ToList(), series, string.Empty);
    }

    private static IReadOnlyList<TrChartBucket> BuildChartBuckets(long start, long end)
    {
        var duration = Math.Max(1, end - start);
        var bucketSeconds = duration <= 2 * 3600 ? 15 * 60
            : duration <= 6 * 3600 ? 30 * 60
            : duration <= 24 * 3600 ? 60 * 60
            : 3 * 60 * 60;
        var first = start - start % bucketSeconds;
        var buckets = new List<TrChartBucket>();
        for (var cursor = first; cursor < end; cursor += bucketSeconds)
        {
            var bucketStart = Math.Max(start, cursor);
            var bucketEnd = Math.Min(end, cursor + bucketSeconds);
            if (bucketEnd > bucketStart)
                buckets.Add(new TrChartBucket(DateTimeOffset.FromUnixTimeSeconds(bucketStart).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(bucketEnd).UtcDateTime, bucketSeconds));
        }
        return buckets;
    }

    private static IReadOnlyList<double> BucketValues(List<TrHealthSampleDto> rows, IReadOnlyList<TrChartBucket> buckets, Func<TrHealthAggregate, double> selector, bool ratePerHour)
    {
        return buckets.Select(bucket =>
        {
            var matching = rows.Where(row => row.WindowEndUtc.ToUniversalTime() > bucket.StartUtc && row.WindowEndUtc.ToUniversalTime() <= bucket.EndUtc).ToList();
            if (matching.Count == 0)
                return 0.0;
            var value = selector(Aggregate(matching));
            return ratePerHour ? value / Math.Max(1.0 / 60.0, (bucket.EndUtc - bucket.StartUtc).TotalHours) : value;
        }).ToList();
    }

    private static IReadOnlyList<double> BaselineBucketValues(List<TrHealthSampleDto> rows, IReadOnlyList<TrChartBucket> buckets, Func<TrHealthAggregate, double> selector, bool ratePerHour)
    {
        if (buckets.Count == 0)
            return [];
        var selectedStart = buckets.Min(bucket => bucket.StartUtc);
        var history = rows.Where(row => row.WindowEndUtc.ToUniversalTime() < selectedStart).ToList();
        return buckets.Select(bucket =>
        {
            var bucketLocal = bucket.StartUtc.ToLocalTime();
            var slot = (bucketLocal.Hour * 3600 + bucketLocal.Minute * 60) / bucket.NominalSeconds;
            var matchingDays = history
                .Where(row =>
                {
                    var local = row.WindowStartUtc.ToLocalTime();
                    return (local.Hour * 3600 + local.Minute * 60) / bucket.NominalSeconds == slot;
                })
                .GroupBy(row => row.WindowEndUtc.ToLocalTime().Date)
                .Select(group =>
                {
                    var value = selector(Aggregate(group.ToList()));
                    return ratePerHour ? value / Math.Max(1.0 / 60.0, bucket.NominalSeconds / 3600.0) : value;
                })
                .ToList();
            return matchingDays.Count == 0 ? 0.0 : matchingDays.Average();
        }).ToList();
    }

    private static TrHealthChartDto BuildChart(
        string title,
        string yAxis,
        string format,
        List<DateTime> hours,
        List<(string Label, IReadOnlyList<double> Values)> series,
        List<TrHealthSampleDto> baselineRows,
        string baseline,
        Func<TrHealthAggregate, double> selector,
        IReadOnlyList<string>? scopes = null,
        bool UseGlobalDisplayAggregate = false,
        bool BaselineIsRate = false)
    {
        var chartSeries = series.Select(s => new TrHealthSeriesDto(s.Label, s.Values)).ToList();
        var comparisonRows = UseGlobalDisplayAggregate
            ? baselineRows
            : scopes == null
            ? baselineRows.Where(IsGlobal).ToList()
            : baselineRows.Where(r => scopes.Contains(r.Scope, StringComparer.OrdinalIgnoreCase)).ToList();
        var baselineInfo = BaselineInfo(comparisonRows, baseline);
        if (baselineInfo.ShouldShow)
        {
            var baselineValues = UseGlobalDisplayAggregate
                ? BaselineSeriesForGlobalDisplay(comparisonRows, hours, selector, baseline)
                : BaselineSeries(comparisonRows, hours, selector, baseline, BaselineIsRate);
            chartSeries.Add(new TrHealthSeriesDto($"{baseline} baseline", baselineValues, true));
        }

        return new TrHealthChartDto(title, yAxis, format, hours.Select(h => h.ToString("MM-dd HH:00", CultureInfo.InvariantCulture)).ToList(), chartSeries, baselineInfo.Note);
    }

    private static List<DateTime> BuildHourLabels(List<TrHealthSampleDto> rows) =>
        rows.Select(r => r.WindowEndUtc.ToLocalTime())
            .Select(d => new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

    private static List<double> Hourly(List<TrHealthSampleDto> rows, List<DateTime> hours, Func<TrHealthAggregate, double> selector)
    {
        return hours.Select(hour =>
        {
            var bucket = rows.Where(r =>
            {
                var local = r.WindowEndUtc.ToLocalTime();
                return local.Year == hour.Year && local.Month == hour.Month && local.Day == hour.Day && local.Hour == hour.Hour;
            }).ToList();
            return bucket.Count == 0 ? 0 : selector(Aggregate(bucket));
        }).ToList();
    }

    private static List<double> HourlyForGlobalDisplay(List<TrHealthSampleDto> rows, List<DateTime> hours, Func<TrHealthAggregate, double> selector)
    {
        return hours.Select(hour =>
        {
            var bucket = rows.Where(r =>
            {
                var local = r.WindowEndUtc.ToLocalTime();
                return local.Year == hour.Year && local.Month == hour.Month && local.Day == hour.Day && local.Hour == hour.Hour;
            }).ToList();
            return bucket.Count == 0 ? 0 : selector(AggregateForGlobalDisplay(bucket));
        }).ToList();
    }

    private static List<double> BaselineSeries(List<TrHealthSampleDto> rows, List<DateTime> hours, Func<TrHealthAggregate, double> selector, string baseline, bool isRate)
    {
        var cutoff = DateTime.UtcNow.AddDays(-BaselineDays(baseline));
        var baselineRows = rows.Where(r => r.WindowEndUtc >= cutoff && r.WindowEndUtc < DateTime.UtcNow.AddHours(-24)).ToList();
        if (baselineRows.Count == 0)
            return hours.Select(_ => 0.0).ToList();

        return hours.Select(hour =>
        {
            var matching = baselineRows.Where(r => r.WindowEndUtc.ToLocalTime().Hour == hour.Hour).ToList();
            if (matching.Count == 0)
                return 0.0;
            var dayCount = Math.Max(1, matching.Select(r => r.WindowEndUtc.ToLocalTime().Date).Distinct().Count());
            var value = selector(Aggregate(matching));
            return isRate ? value : value / dayCount;
        }).ToList();
    }

    private static List<double> BaselineSeriesForGlobalDisplay(List<TrHealthSampleDto> rows, List<DateTime> hours, Func<TrHealthAggregate, double> selector, string baseline)
    {
        var cutoff = DateTime.UtcNow.AddDays(-BaselineDays(baseline));
        var baselineRows = rows.Where(r => r.WindowEndUtc >= cutoff && r.WindowEndUtc < DateTime.UtcNow.AddHours(-24)).ToList();
        if (baselineRows.Count == 0)
            return hours.Select(_ => 0.0).ToList();

        return hours.Select(hour =>
        {
            var matching = baselineRows.Where(r => r.WindowEndUtc.ToLocalTime().Hour == hour.Hour).ToList();
            return matching.Count == 0 ? 0.0 : selector(AggregateForGlobalDisplay(matching));
        }).ToList();
    }

    private static (bool ShouldShow, string Note) BaselineInfo(List<TrHealthSampleDto> rows, string baseline, DateTime? beforeUtc = null)
    {
        var baselineEnd = beforeUtc ?? DateTime.UtcNow.AddHours(-24);
        var cutoff = baselineEnd.AddDays(-BaselineDays(baseline));
        var baselineRows = rows.Where(r => r.WindowEndUtc >= cutoff && r.WindowEndUtc < baselineEnd).ToList();
        var requestedDays = BaselineDays(baseline);
        if (baselineRows.Count == 0)
            return (false, $"Baseline hidden: no stored history older than 24h for the selected {baseline} window.");

        var first = baselineRows.Min(r => r.WindowStartUtc);
        var last = baselineRows.Max(r => r.WindowEndUtc);
        var availableDays = Math.Max(0, (last - first).TotalDays);
        var bucketCount = baselineRows.Count;
        var note = $"Baseline: {baseline}, {availableDays:F1}d available from {bucketCount:N0} health bucket{(bucketCount == 1 ? "" : "s")}.";
        if (availableDays < requestedDays * 0.9)
            note += $" Partial history; requested {requestedDays}d.";

        var enoughHistory = bucketCount >= 24 && availableDays >= 0.25;
        return enoughHistory
            ? (true, note)
            : (false, $"Baseline hidden: only {availableDays:F1}d / {bucketCount:N0} bucket{(bucketCount == 1 ? "" : "s")} available.");
    }

    private static string BuildSummaryText(TrHealthAggregate agg, IReadOnlyList<TrHealthMetricDto> systems, DateTime last)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Metric                         Value");
        sb.AppendLine("-----------------------------------------------");
        sb.AppendLine($"5-minute windows               {agg.Windows}");
        sb.AppendLine($"CC summary samples             {agg.CcSummaryDecodeSampleLines}");
        sb.AppendLine($"CC summary decode rate         {FormatCcSummaryDecodeRateWithConfidence(agg)}");
        sb.AppendLine($"CC summary zero rate           {agg.CcSummaryDecodeZeroPercent:F2}%");
        sb.AppendLine($"CC message-rate samples        {agg.LowDecodeWarningLines}");
        sb.AppendLine($"Message-rate zero rate         {agg.LowDecodeWarningZeroPercent:F2}%");
        sb.AppendLine($"Control-channel retunes        {agg.Retunes}");
        sb.AppendLine($"Recorded calls concluded       {agg.CallsConcluded}");
        sb.AppendLine($"No transmissions recorded      {agg.NoTxRecorded}");
        sb.AppendLine($"Recorder capacity exhausted    {agg.RecorderExhausted}");
        sb.AppendLine($"Sample-source stops            {agg.SampleStops}");
        sb.AppendLine($"No source covering frequency   {agg.UnableSource}");
        sb.AppendLine($"last window end: {last.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        if (systems.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Systems:");
            foreach (var system in systems)
                sb.AppendLine($"{system.Metric}: {system.Value} ({system.Notes})");
        }
        return sb.ToString();
    }

    private string BuildDiagnostics(List<TrHealthSampleDto> allRows, List<TrHealthSampleDto> recentRows, List<TrHealthSampleDto> selectedRows, string baseline, bool bySystem, string log)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"service: {_config.TrunkRecorder.LogServiceName}");
        sb.AppendLine($"config: {_config.TrunkRecorder.ConfigPath}");
        sb.AppendLine($"health cadence: {_config.TrunkRecorder.HealthWindowMinutes} minute(s)");
        sb.AppendLine($"stored rows loaded: {allRows.Count:N0}");
        sb.AppendLine($"last-24h rows: {recentRows.Count:N0}");
        sb.AppendLine($"selected-range rows: {selectedRows.Count:N0}");
        sb.AppendLine($"baseline: {baseline}");
        sb.AppendLine($"metrics mode: {(bySystem ? "by system" : "global")}");
        sb.AppendLine($"recent log chars returned: {log.Length:N0}");
        var scopes = allRows.Select(r => r.Scope).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        sb.AppendLine($"scopes: {string.Join(", ", scopes)}");
        return sb.ToString();
    }

    private async Task<IReadOnlyList<TrHealthMetricDto>> BuildRetuneTargetsAsync(string system, long start, long end, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(system))
            return [];

        var window = TimeSpan.FromSeconds(Math.Max(1, end - start));
        if (window > TimeSpan.FromDays(3))
        {
            return
            [
                new("Retune target detail", "Skipped", "Retune target frequency detail reads journald on demand and is limited to windows of 3 days or less.", false)
            ];
        }

        var log = await ReadJournalRangeAsync(DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime, ct);
        var targets = RetuneTargetRegex.Matches(log)
            .Select(m => new
            {
                Scope = m.Groups["scope"].Value.Trim(),
                Frequency = m.Groups["freq"].Value.Trim()
            })
            .Where(m => string.Equals(m.Scope, system, StringComparison.OrdinalIgnoreCase))
            .GroupBy(m => m.Frequency)
            .Select(g => new TrHealthMetricDto(
                $"{g.Key} MHz",
                g.Count().ToString("N0", CultureInfo.InvariantCulture),
                "Retunes to this control-channel target in the selected window.",
                g.Count() > 0))
            .OrderByDescending(r => int.TryParse(r.Value.Replace(",", string.Empty, StringComparison.Ordinal), out var count) ? count : 0)
            .ToList();
        return targets.Count == 0
            ? [new TrHealthMetricDto("Retune targets", "None", "No retune target lines were found in journald for this system/window.", false)]
            : targets;
    }

    private async Task<string> ReadJournalRangeAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        if (_journalRangeReader != null)
            return await _journalRangeReader(startUtc, endUtc, ct);

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
                return string.Empty;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read TR journal range for RF analysis");
            return string.Empty;
        }
    }

    private static string BuildTroubleshootInsightPrompt(TrTroubleshootDto snapshot, long start, long end, bool bySystem, string baseline)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Review this PizzaWave/trunk-recorder troubleshooting snapshot and return actionable suggestions.");
        sb.AppendLine("Focus on root-cause hypotheses, what to check next, and which metric supports each suggestion.");
        sb.AppendLine("Do not restate every metric. Keep the response concise.");
        sb.AppendLine();
        sb.AppendLine($"Range UTC: {DateTimeOffset.FromUnixTimeSeconds(start):O} to {DateTimeOffset.FromUnixTimeSeconds(end):O}");
        sb.AppendLine($"Metrics mode: {(bySystem ? "by system" : "global")}; baseline: {baseline}");
        sb.AppendLine();
        sb.AppendLine("Health title:");
        sb.AppendLine(snapshot.Health.Title);
        sb.AppendLine();
        sb.AppendLine("Health summary:");
        sb.AppendLine(snapshot.Health.SummaryText);
        sb.AppendLine();
        sb.AppendLine("Metric rows:");
        foreach (var metric in snapshot.Health.Metrics)
            sb.AppendLine($"- {metric.Metric}: {metric.Value}; issue={metric.IsIssue}; notes={metric.Notes}");
        sb.AppendLine();
        sb.AppendLine("System rows:");
        foreach (var system in snapshot.Health.Systems.Take(12))
            sb.AppendLine($"- {system.Metric}: {system.Value}; issue={system.IsIssue}; notes={system.Notes}");
        sb.AppendLine();
        sb.AppendLine("Current deterministic remedies:");
        foreach (var remedy in snapshot.Health.Remedies)
            sb.AppendLine($"- {remedy.Metric}: {remedy.Notes}; issue={remedy.IsIssue}");
        sb.AppendLine();
        sb.AppendLine("Chart data:");
        foreach (var chart in snapshot.Health.Charts.Take(8))
        {
            sb.AppendLine($"- {chart.Title} ({chart.YAxisLabel}, format {chart.ValueFormat})");
            sb.AppendLine($"  labels: {string.Join(", ", chart.Labels.TakeLast(12))}");
            foreach (var series in chart.Series.Take(5))
                sb.AppendLine($"  series {series.Label}: {string.Join(", ", series.Values.TakeLast(12).Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)))}");
        }
        sb.AppendLine();
        sb.AppendLine("Transcription quality snapshot:");
        sb.AppendLine($"- total calls: {snapshot.QualityAudit.TotalCalls:N0}");
        sb.AppendLine($"- problem calls: {snapshot.QualityAudit.ProblemCalls:N0} ({snapshot.QualityAudit.ProblemPercent:F1}%)");
        sb.AppendLine($"- inaudible calls: {snapshot.QualityAudit.InaudibleCalls:N0} ({snapshot.QualityAudit.InaudiblePercent:F1}%)");
        sb.AppendLine("- reasons:");
        foreach (var row in snapshot.QualityAudit.ByReason)
            sb.AppendLine($"  - {row.Label}: {row.TotalCalls:N0} calls, share {row.ProblemPercent:F1}%");
        sb.AppendLine("- systems:");
        foreach (var row in snapshot.QualityAudit.BySystem.Take(8))
            sb.AppendLine($"  - {row.Label}: {row.ProblemCalls:N0}/{row.TotalCalls:N0} problem calls ({row.ProblemPercent:F1}%), inaudible {row.InaudibleCalls:N0}");
        sb.AppendLine();
        sb.AppendLine("Return sections: Findings, Next checks, Dashboard/collector improvements. Use bullets.");
        return Trim(sb.ToString(), 18000);
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

    private static string ExtractChatContent(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            var first = choices.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
                return content.GetString() ?? string.Empty;
        }
        return json;
    }

    private static string Trim(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

    private async Task<string> ReadJournalAsync(int lines, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return "TR journald logs are available on Linux hosts only.";

        var args = $"-u {_config.TrunkRecorder.LogServiceName} -n {Math.Clamp(lines, 50, 2000)} --no-pager";
        var psi = new ProcessStartInfo("journalctl", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return "journalctl failed to start.";
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return string.IsNullOrWhiteSpace(error) ? output : $"{output}\n{error}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read TR journal");
            return $"journalctl failed: {ex.Message}";
        }
    }

    private async Task<List<TrHealthSampleDto>> ListHealthSamplesWithExactBoundariesAsync(long start, long end, CancellationToken ct)
    {
        var rows = await _database.ListHealthSamplesAsync(start, end, ct);
        if (end <= start || (OperatingSystem.IsWindows() && _journalRangeReader == null))
            return rows;

        var startUtc = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
        var endUtc = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime;
        foreach (var (boundaryStart, boundaryEnd) in BuildPartialBoundaryRanges(startUtc, endUtc))
        {
            var log = await ReadJournalRangeAsync(boundaryStart, boundaryEnd, ct);
            rows.AddRange(ParseJournalSamples(log, boundaryStart, boundaryEnd));
        }

        return rows
            .OrderBy(r => r.WindowStartUtc)
            .ThenBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> BuildPartialBoundaryRanges(DateTime startUtc, DateTime endUtc)
    {
        if (endUtc <= startUtc)
            return [];

        var firstCompleteBucket = CeilingToFiveMinutes(startUtc);
        var lastCompleteBucketEnd = FloorToFiveMinutes(endUtc);
        if (firstCompleteBucket >= lastCompleteBucketEnd)
            return [(startUtc, endUtc)];

        var ranges = new List<(DateTime StartUtc, DateTime EndUtc)>(2);
        if (startUtc < firstCompleteBucket)
            ranges.Add((startUtc, firstCompleteBucket));
        if (lastCompleteBucketEnd < endUtc)
            ranges.Add((lastCompleteBucketEnd, endUtc));
        return ranges;
    }

    private static List<TrHealthSampleDto> ParseJournalSamples(string log, DateTime? rangeStartUtc = null, DateTime? rangeEndUtc = null)
    {
        var buckets = new Dictionary<(DateTime StartUtc, string Scope), List<string>>();
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var ts = ParseTrTimestamp(line);
            if (ts == null)
                continue;
            if (rangeStartUtc.HasValue && ts.Value < rangeStartUtc.Value)
                continue;
            if (rangeEndUtc.HasValue && ts.Value >= rangeEndUtc.Value)
                continue;

            var start = FloorToFiveMinutes(ts.Value);
            AddLine(buckets, start, "global", line);
            var scope = ParseScope(line);
            if (!string.IsNullOrWhiteSpace(scope))
                AddLine(buckets, start, scope, line);
        }

        return buckets
            .Select(kvp => TrHealthCollector.BuildSample(kvp.Key.Scope, kvp.Key.StartUtc, kvp.Key.StartUtc.AddMinutes(5), string.Join('\n', kvp.Value)))
            .OrderBy(r => r.WindowStartUtc)
            .ThenBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string TailLines(string text, int count)
    {
        var lines = text.Split('\n');
        return string.Join('\n', lines.TakeLast(Math.Max(1, count)));
    }

    private static void AddLine(Dictionary<(DateTime StartUtc, string Scope), List<string>> buckets, DateTime start, string scope, string line)
    {
        var key = (start, scope);
        if (!buckets.TryGetValue(key, out var lines))
        {
            lines = new List<string>();
            buckets[key] = lines;
        }
        lines.Add(line);
    }

    private static DateTime? ParseTrTimestamp(string line)
    {
        var match = TrTimestampRegex.Match(line);
        if (!match.Success)
            return null;
        if (!DateTime.TryParse(match.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return null;
        return local.ToUniversalTime();
    }

    private static string? ParseScope(string line)
    {
        var match = TrScopeRegex.Match(line);
        if (!match.Success)
            return null;
        var scope = match.Groups["scope"].Value.Trim();
        return IsDisplaySystemScope(scope) ? scope : null;
    }

    private static DateTime FloorToFiveMinutes(DateTime timestampUtc)
    {
        var utc = timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute / 5 * 5, 0, DateTimeKind.Utc);
    }

    private static DateTime CeilingToFiveMinutes(DateTime timestampUtc)
    {
        var floor = FloorToFiveMinutes(timestampUtc);
        return floor == timestampUtc ? floor : floor.AddMinutes(5);
    }

    private static TrHealthAggregate Aggregate(List<TrHealthSampleDto> rows)
    {
        var decodeLines = rows.Sum(r => r.DecodeLines);
        var decodeZero = rows.Sum(r => r.DecodeZero);
        var ccSummaryDecodeLines = rows.Sum(r => r.CcSummaryDecodeLines);
        var ccSummaryDecodeZero = rows.Sum(r => r.CcSummaryDecodeZero);
        var ccSummaryDecodeRateTotal = rows.Sum(r => r.CcSummaryDecodeRateTotal);
        var lowDecodeWarningLines = rows.Sum(r => r.LowDecodeWarningLines);
        var lowDecodeWarningZero = rows.Sum(r => r.LowDecodeWarningZero);
        var lowDecodeWarningRateTotal = rows.Sum(r => r.LowDecodeWarningRateTotal);
        return new TrHealthAggregate(
            rows.Count,
            decodeLines,
            decodeZero,
            rows.Sum(r => r.DecodeRateTotal),
            ccSummaryDecodeLines,
            ccSummaryDecodeZero,
            ccSummaryDecodeRateTotal,
            lowDecodeWarningLines,
            lowDecodeWarningZero,
            lowDecodeWarningRateTotal,
            rows.Sum(r => r.Retunes),
            rows.Sum(r => r.CallsConcluded),
            rows.Sum(r => r.UpdateNotGrant),
            rows.Sum(r => r.NoTxRecorded),
            rows.Sum(r => r.RecorderExhausted),
            rows.Sum(r => r.SampleStops),
            rows.Sum(r => r.UnableSource),
            null,
            0,
            rows.Select(r => r.TrCpuPercent).DefaultIfEmpty(0).Max(),
            rows.Select(r => r.TrRssMb).DefaultIfEmpty(0).Max(),
            rows.Select(r => r.TrVszMb).DefaultIfEmpty(0).Max(),
            rows.Select(r => r.TrThreadCount).DefaultIfEmpty(0).Max(),
            rows.Select(r => r.HostTempC).DefaultIfEmpty(0).Max(),
            rows.OrderByDescending(r => r.WindowEndUtc).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.HostThrottledFlags))?.HostThrottledFlags ?? string.Empty,
            rows.Select(r => r.HostLoad1).DefaultIfEmpty(0).Max(),
            rows.Select(r => r.HostLoad5).DefaultIfEmpty(0).Max(),
            rows.Select(r => r.HostLoad15).DefaultIfEmpty(0).Max());
    }

    private static TrHealthAggregate AggregateForGlobalDisplay(List<TrHealthSampleDto> rows)
    {
        var systemRows = rows.Where(r => IsDisplaySystemScope(r.Scope)).ToList();
        if (systemRows.Count == 0)
        {
            var globalRows = rows.Where(IsGlobal).ToList();
            return Aggregate(globalRows.Count > 0 ? globalRows : rows);
        }

        var aggregate = Aggregate(systemRows);
        var weighted = systemRows
            .GroupBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupRows = g.ToList();
                var decodeLines = groupRows.Sum(r => r.DecodeLines);
                var decodeZero = groupRows.Sum(r => r.DecodeZero);
                var calls = groupRows.Sum(r => r.CallsConcluded);
                var percent = decodeLines == 0 ? 0 : decodeZero * 100.0 / decodeLines;
                return new { DecodeLines = decodeLines, Calls = calls, Percent = percent };
            })
            .Where(s => s.DecodeLines > 0 && s.Calls > 0)
            .ToList();

        if (weighted.Count == 0)
            return aggregate;

        var totalCalls = weighted.Sum(s => s.Calls);
        var weightedPercent = weighted.Sum(s => s.Percent * s.Calls) / Math.Max(1, totalCalls);
        return aggregate with { WeightedDecodeZeroPercent = weightedPercent, DecodeWeightCalls = totalCalls };
    }

    private static bool IsGlobal(TrHealthSampleDto row) => string.Equals(row.Scope, "global", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisplaySystemScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return false;
        var value = scope.Trim();
        if (string.Equals(value, "global", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.StartsWith("source", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.All(char.IsDigit))
            return false;
        return value.Any(char.IsLetter);
    }

    private static bool HasSufficientDecodeSamples(TrHealthAggregate agg) => agg.DecodeSampleLines >= 20;

    private static bool HasSufficientCcSummarySamples(TrHealthAggregate agg) => agg.CcSummaryDecodeSampleLines >= 10;

    private static string FormatDecodeRateWithConfidence(TrHealthAggregate agg) =>
        HasSufficientDecodeSamples(agg) ? $"{agg.AvgDecodeRate:F2}/sec" : "N/A";

    private static string FormatCcSummaryDecodeRateWithConfidence(TrHealthAggregate agg) =>
        HasSufficientCcSummarySamples(agg) ? $"{agg.CcSummaryAvgDecodeRate:F2} msg/sec" : "N/A";

    private static bool IsThrottledFlagSet(string flags)
    {
        flags = (flags ?? string.Empty).Trim();
        if (flags.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(flags[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex != 0;
        return int.TryParse(flags, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value != 0;
    }

    private static int BaselineDays(string? baseline) => baseline?.Trim().ToLowerInvariant() switch
    {
        "14d" => 14,
        "30d" => 30,
        _ => 7
    };

    private sealed record TrHealthAggregate(
        int Windows,
        int DecodeSampleLines,
        int DecodeZero,
        double DecodeRateTotal,
        int CcSummaryDecodeSampleLines,
        int CcSummaryDecodeZero,
        double CcSummaryDecodeRateTotal,
        int LowDecodeWarningLines,
        int LowDecodeWarningZero,
        double LowDecodeWarningRateTotal,
        int Retunes,
        int CallsConcluded,
        int UpdateNotGrant,
        int NoTxRecorded,
        int RecorderExhausted,
        int SampleStops,
        int UnableSource,
        double? WeightedDecodeZeroPercent,
        int DecodeWeightCalls,
        double MaxTrCpuPercent,
        double MaxTrRssMb,
        double MaxTrVszMb,
        int MaxTrThreadCount,
        double MaxHostTempC,
        string LatestHostThrottledFlags,
        double MaxHostLoad1,
        double MaxHostLoad5,
        double MaxHostLoad15)
    {
        public double DecodeZeroPercent => WeightedDecodeZeroPercent ?? (DecodeSampleLines == 0 ? 0 : DecodeZero * 100.0 / DecodeSampleLines);
        public double AvgDecodeRate => DecodeSampleLines == 0 ? 0 : DecodeRateTotal / DecodeSampleLines;
        public double CcSummaryDecodeZeroPercent => CcSummaryDecodeSampleLines == 0 ? 0 : CcSummaryDecodeZero * 100.0 / CcSummaryDecodeSampleLines;
        public double CcSummaryAvgDecodeRate => CcSummaryDecodeSampleLines == 0 ? 0 : CcSummaryDecodeRateTotal / CcSummaryDecodeSampleLines;
        public double LowDecodeWarningZeroPercent => LowDecodeWarningLines == 0 ? 0 : LowDecodeWarningZero * 100.0 / LowDecodeWarningLines;
        public double LowDecodeWarningAvgRate => LowDecodeWarningLines == 0 ? 0 : LowDecodeWarningRateTotal / LowDecodeWarningLines;
    }

    private sealed record TrChartBucket(DateTime StartUtc, DateTime EndUtc, int NominalSeconds);

    private sealed record TrSourceWindow(
        int Index,
        string Device,
        double CenterHz,
        double RateHz,
        int DigitalRecorders,
        double LowHz,
        double HighHz);

    private sealed record TrDesiredSystem(string ShortName, IReadOnlyList<long> ControlChannelsHz, IReadOnlyList<long> VoiceFrequenciesHz);

    private sealed record FrequencyRange(long LowHz, long HighHz, long CenterHz);

    private sealed record TrTroubleshootCacheEntry(string Key, DateTimeOffset CreatedAt, TrTroubleshootDto Value);
}
