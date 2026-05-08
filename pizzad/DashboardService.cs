using System.Globalization;
namespace pizzad;

public sealed class DashboardService
{
    private readonly EngineDatabase _database;
    private readonly TalkgroupResolver _talkgroups;

    public DashboardService(EngineDatabase database, TalkgroupResolver talkgroups)
    {
        _database = database;
        _talkgroups = talkgroups;
    }

    public async Task<DashboardDto> BuildDashboardAsync(long start, long end, CancellationToken ct)
    {
        var calls = Enrich(await _database.ListCallsAsync(start, end, null, ct));
        var alerts = await _database.ListAlertMatchesAsync(start, end, ct);
        var incidents = await ListIncidentsAsync(start, end, ct);
        var total = calls.Count;
        var alertRate = total == 0 ? 0 : alerts.Select(a => a.CallId).Distinct().Count() * 100.0 / total;
        var qualityProblems = calls.Count(IsProblemTranscript);
        var problemCalls = calls.Where(IsProblemTranscript).ToList();
        var qualityBreakdown = BuildQualityBreakdown(problemCalls);
        var topProblemSystem = BuildTopProblemSystem(calls);
        var healthRows = await _database.ListHealthSamplesAsync(start, end, ct);
        var decodeKpis = BuildDecodeKpis(healthRows);
        var busiest = calls
            .GroupBy(c => DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().Hour)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        return new DashboardDto
        {
            Kpis =
            [
                new("Total Calls", total.ToString("N0", CultureInfo.CurrentCulture), "Selected range"),
                new("Alert Rate", $"{alertRate:F1}%", "Calls matching alert rules"),
                new("Incidents", incidents.Count.ToString("N0", CultureInfo.CurrentCulture), "An Incident consists of multiple, related calls"),
                new("Quality Problems", qualityProblems.ToString("N0", CultureInfo.CurrentCulture), total == 0 ? "No calls in selected range" : $"{qualityProblems * 100.0 / total:F1}% of calls; {qualityBreakdown}"),
                new("Top Problem System", topProblemSystem.Label, topProblemSystem.ValueText),
                decodeKpis.Global,
                decodeKpis.Systems,
                new("Busiest Hour", busiest == null ? "--" : $"{busiest.Key:00}:00", busiest == null ? "No calls" : $"{busiest.Count():N0} calls"),
                new("Unique Talkgroups", calls.Select(c => c.Talkgroup).Distinct().Count().ToString("N0", CultureInfo.CurrentCulture), "Heard in selected range")
            ],
            VolumeByHourCategory = BuildVolume(calls),
            QualityByHour = BuildQuality(calls),
            ProblemTalkgroups = BuildProblemTalkgroups(calls),
            InaudibleBySystem = BuildInaudibleBySystem(calls),
            CategoryShare = BuildCategoryShare(calls),
            TopTalkgroups = BuildTopTalkgroups(calls, start, end),
            Alerts = alerts,
            Incidents = incidents
        };
    }

    public async Task<CategoryPageDto> BuildCategoryPageAsync(string category, string groupBy, long start, long end, CancellationToken ct)
    {
        category = NormalizeCategory(category);
        var calls = Enrich(await _database.ListCallsAsync(start, end, null, ct))
            .Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var insights = BuildCategoryInsights(await _database.ListInsightEventsAsync(start, end, ct), calls);
        if (string.Equals(groupBy, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new CategoryPageDto(category, "none", [new CategoryGroupDto("All calls", calls)], insights);
        }

        var groups = calls
            .GroupBy(c => string.IsNullOrWhiteSpace(c.TalkgroupName) ? $"TG {c.Talkgroup}" : c.TalkgroupName)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new CategoryGroupDto(g.Key, g.OrderByDescending(c => c.StartTime).ToList()))
            .ToList();

        return new CategoryPageDto(category, "talkgroup", groups, insights);
    }

    public Task<List<IncidentDto>> ListIncidentsAsync(long start, long end, CancellationToken ct)
    {
        return _database.ListIncidentsAsync(start, end, ct);
    }

    private List<EngineCall> Enrich(List<EngineCall> calls) =>
        calls.Select(_talkgroups.Enrich).ToList();

    private static IReadOnlyList<CategoryInsightDto> BuildCategoryInsights(List<InsightEventRecordDto> insightEvents, List<EngineCall> categoryCalls)
    {
        var categoryCallIds = categoryCalls.Select(c => c.Id).ToHashSet();
        return insightEvents
            .Select(i => new
            {
                Insight = i,
                Calls = i.Calls.Where(c => categoryCallIds.Contains(c.CallId)).ToList()
            })
            .Where(x => x.Calls.Count > 0)
            .OrderByDescending(x => x.Insight.LastSeen)
            .ThenByDescending(x => x.Insight.Confidence)
            .Select(x => new CategoryInsightDto(
                x.Insight.Id,
                x.Insight.Title,
                x.Insight.Detail,
                x.Insight.FirstSeen,
                x.Insight.LastSeen,
                x.Insight.Confidence,
                x.Calls.Count,
                x.Calls))
            .Take(100)
            .ToList();
    }

    private static IReadOnlyList<HourCategoryDto> BuildVolume(List<EngineCall> calls) =>
        calls.GroupBy(c => new { DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().Hour, c.Category })
            .Select(g => new HourCategoryDto(g.Key.Hour, g.Key.Category, g.Count()))
            .OrderBy(r => r.Hour)
            .ThenBy(r => r.Category)
            .ToList();

    private static IReadOnlyList<QualityHourDto> BuildQuality(List<EngineCall> calls) =>
        calls.GroupBy(c => DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().Hour)
            .Select(g => new QualityHourDto(
                g.Key,
                g.Count(IsTranscriptEmpty),
                g.Count(IsTranscriptFailureHint),
                g.Count(IsTranscriptInaudibleHint),
                g.Count(IsTranscriptShort)))
            .OrderBy(r => r.Hour)
            .ToList();

    private static IReadOnlyList<BarStatDto> BuildProblemTalkgroups(List<EngineCall> calls)
    {
        var problems = calls.Where(IsProblemTranscript).ToList();
        var max = Math.Max(1, problems.GroupBy(c => c.Talkgroup).Select(g => g.Count()).DefaultIfEmpty(0).Max());
        return problems.GroupBy(c => c.Talkgroup)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new BarStatDto(GetTalkgroupLabel(g.First()), g.Count(), g.Count() / (double)max, $"{g.Count():N0} problems"))
            .ToList();
    }

    private static IReadOnlyList<BarStatDto> BuildInaudibleBySystem(List<EngineCall> calls)
    {
        var rows = calls.GroupBy(c => string.IsNullOrWhiteSpace(c.SystemShortName) ? "Unknown system" : c.SystemShortName)
            .Select(g =>
            {
                var total = g.Count();
                var inaudible = g.Count(IsTranscriptInaudibleHint);
                return new { System = g.Key, Total = total, Inaudible = inaudible };
            })
            .Where(r => r.Inaudible > 0)
            .OrderByDescending(r => r.Inaudible)
            .ToList();
        var max = Math.Max(1, rows.Select(r => r.Inaudible).DefaultIfEmpty(0).Max());
        return rows.Select(r => new BarStatDto(r.System, r.Inaudible, r.Inaudible / (double)max, $"{r.Inaudible:N0}/{r.Total:N0} ({(r.Inaudible * 100.0 / Math.Max(1, r.Total)):F1}%)"))
            .ToList();
    }

    private static string BuildQualityBreakdown(List<EngineCall> problemCalls)
    {
        if (problemCalls.Count == 0) return "no quality problems";
        var parts = new[]
        {
            (Label: "inaudible", Count: problemCalls.Count(IsTranscriptInaudibleHint)),
            (Label: "short", Count: problemCalls.Count(IsTranscriptShort)),
            (Label: "empty", Count: problemCalls.Count(IsTranscriptEmpty)),
            (Label: "failed", Count: problemCalls.Count(IsTranscriptFailureHint))
        }
        .Where(p => p.Count > 0)
        .Select(p => $"{p.Count:N0} {p.Label}");
        return string.Join(", ", parts);
    }

    private static BarStatDto BuildTopProblemSystem(List<EngineCall> calls)
    {
        var row = calls.GroupBy(c => string.IsNullOrWhiteSpace(c.SystemShortName) ? "Unknown system" : c.SystemShortName)
            .Select(g =>
            {
                var total = g.Count();
                var problems = g.Count(IsProblemTranscript);
                return new { Label = g.Key, Total = total, Problems = problems };
            })
            .Where(r => r.Problems > 0)
            .OrderByDescending(r => r.Problems)
            .ThenByDescending(r => r.Total)
            .FirstOrDefault();
        return row == null
            ? new BarStatDto("--", 0, 0, "No quality problems")
            : new BarStatDto(row.Label, row.Problems, 1, $"{row.Problems:N0}/{row.Total:N0} calls ({row.Problems * 100.0 / Math.Max(1, row.Total):F1}%)");
    }

    private static IReadOnlyList<BarStatDto> BuildCategoryShare(List<EngineCall> calls)
    {
        var total = Math.Max(1, calls.Count);
        return calls.GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new BarStatDto(g.Key, g.Count(), g.Count() / (double)total, $"{g.Count() * 100.0 / total:F1}%"))
            .ToList();
    }

    private static IReadOnlyList<TopTalkgroupDto> BuildTopTalkgroups(List<EngineCall> calls, long start, long end)
    {
        var total = Math.Max(1, calls.Count);
        var trendBins = TrendBinCount(start, end);
        return calls.GroupBy(c => c.Talkgroup)
            .OrderByDescending(g => g.Count())
            .Take(12)
            .Select(g =>
            {
                var trendCounts = BuildTrendCounts(g.ToList(), start, end, trendBins);
                return new TopTalkgroupDto(
                    GetTalkgroupLabel(g.First()),
                    g.Key,
                    g.Count(),
                    g.Count() / (double)total,
                    g.Max(c => c.StartTime),
                    NormalizeTrend(trendCounts),
                    trendCounts,
                    BuildTrendLabels(start, end, trendBins),
                    FormatEndpoint(start),
                    FormatBucket(start, end, trendBins),
                    FormatEndpoint(end));
            })
            .ToList();
    }

    private static int TrendBinCount(long start, long end)
    {
        var hours = Math.Max(1, (int)Math.Ceiling((end - start) / 3600.0));
        return Math.Clamp(hours, 12, 48);
    }

    private static IReadOnlyList<int> BuildTrendCounts(List<EngineCall> calls, long start, long end, int bins)
    {
        var counts = new int[bins];
        var span = Math.Max(1, end - start);
        foreach (var call in calls)
        {
            var index = (int)Math.Floor((call.StartTime - start) / (double)span * bins);
            index = Math.Clamp(index, 0, bins - 1);
            counts[index]++;
        }
        return counts.ToList();
    }

    private static IReadOnlyList<double> NormalizeTrend(IReadOnlyList<int> counts)
    {
        var max = Math.Max(1, counts.Max());
        return counts.Select(c => c / (double)max).ToList();
    }

    private static IReadOnlyList<string> BuildTrendLabels(long start, long end, int bins)
    {
        var labels = new List<string>(bins);
        var span = Math.Max(1, end - start);
        for (var i = 0; i < bins; i++)
        {
            var bucketStart = start + (long)Math.Floor(span * (i / (double)bins));
            labels.Add(DateTimeOffset.FromUnixTimeSeconds(bucketStart).ToLocalTime().ToString("M/d HH:mm", CultureInfo.CurrentCulture));
        }
        return labels;
    }

    public static string NormalizeCategory(string category)
    {
        category = (category ?? string.Empty).Trim().ToLowerInvariant();
        return category is "police" or "fire" or "ems" or "traffic" ? category : "other";
    }

    public static bool IsProblemTranscript(EngineCall call) =>
        TranscriptionQualityClassifier.IsProblem(call);

    public static bool IsTranscriptEmpty(EngineCall call) =>
        string.Equals(call.QualityReason, "empty", StringComparison.OrdinalIgnoreCase);

    public static bool IsTranscriptShort(EngineCall call) =>
        string.Equals(call.QualityReason, "too_short", StringComparison.OrdinalIgnoreCase);

    public static bool IsTranscriptFailureHint(EngineCall call) =>
        string.Equals(call.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(call.QualityReason, "transcription_error", StringComparison.OrdinalIgnoreCase);

    public static bool IsTranscriptInaudibleHint(EngineCall call) =>
        call.QualityReason is "inaudible" or "blank_audio" or "marker_only";

    private static string GetTalkgroupLabel(EngineCall call) =>
        string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"TG {call.Talkgroup}" : call.TalkgroupName;

    private static string FormatEndpoint(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("M/d HH:mm", CultureInfo.CurrentCulture);

    private static string FormatBucket(long start, long end, int bins)
    {
        var seconds = Math.Max(1, end - start) / Math.Max(1, bins);
        if (seconds < 3600) return $"{Math.Max(1, seconds / 60)}m each";
        if (seconds < 86400) return $"{Math.Max(1, seconds / 3600)}h each";
        return $"{seconds / 86400.0:F1}d each";
    }

    private static (KpiDto Global, KpiDto Systems) BuildDecodeKpis(List<TrHealthSampleDto> rows)
    {
        var systems = rows
            .Where(r => IsDisplaySystemScope(r.Scope))
            .GroupBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupRows = g.ToList();
                var decodeLines = groupRows.Sum(r => r.DecodeLines);
                var decodeZero = groupRows.Sum(r => r.DecodeZero);
                var calls = groupRows.Sum(r => r.CallsConcluded);
                var pct = decodeLines == 0 ? 0 : decodeZero * 100.0 / decodeLines;
                return new DecodeSystemStat(g.Key, decodeLines, decodeZero, calls, pct);
            })
            .Where(s => s.DecodeLines > 0)
            .OrderByDescending(s => s.Calls)
            .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var global = WeightedDecodeZeroPercent(systems, rows);
        var globalKpi = global == null
            ? new KpiDto("TR Decode 0%", "N/A", "No TR decode samples in selected range")
            : new KpiDto("TR Decode 0%", $"{global.Value.Percent:F1}%", $"Weighted by {global.Value.Weight:N0} concluded calls");

        var worst = systems.OrderByDescending(s => s.DecodeZeroPercent).FirstOrDefault();
        var systemSummary = string.Join(", ", systems.Take(3).Select(s => $"{s.Label} {s.DecodeZeroPercent:F1}%/{s.Calls:N0} calls"));
        var systemsKpi = worst == null
            ? new KpiDto("TR Decode Systems", "N/A", "No system-scoped decode samples")
            : new KpiDto("TR Decode Systems", $"{worst.Label} {worst.DecodeZeroPercent:F1}%", string.IsNullOrWhiteSpace(systemSummary) ? "Worst system decode-zero rate" : systemSummary);

        return (globalKpi, systemsKpi);
    }

    private static (double Percent, int Weight)? WeightedDecodeZeroPercent(List<DecodeSystemStat> systems, List<TrHealthSampleDto> fallbackRows)
    {
        var weighted = systems.Where(s => s.Calls > 0).ToList();
        if (weighted.Count > 0)
        {
            var totalCalls = weighted.Sum(s => s.Calls);
            var percent = weighted.Sum(s => s.DecodeZeroPercent * s.Calls) / Math.Max(1, totalCalls);
            return (percent, totalCalls);
        }

        var globalRows = fallbackRows.Where(r => string.Equals(r.Scope, "global", StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = globalRows.Count > 0 ? globalRows : fallbackRows;
        var lines = rows.Sum(r => r.DecodeLines);
        if (lines == 0)
            return null;
        return (rows.Sum(r => r.DecodeZero) * 100.0 / lines, lines);
    }

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

    private sealed record DecodeSystemStat(string Label, int DecodeLines, int DecodeZero, int Calls, double DecodeZeroPercent);
}
