using System.Globalization;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class DashboardService
{
    private readonly EngineDatabase _database;

    public DashboardService(EngineDatabase database)
    {
        _database = database;
    }

    public async Task<DashboardDto> BuildDashboardAsync(long start, long end, CancellationToken ct)
    {
        var calls = await _database.ListCallsAsync(start, end, null, ct);
        var alerts = await _database.ListAlertMatchesAsync(start, end, ct);
        var incidents = await ListIncidentsAsync(start, end, ct);
        var total = calls.Count;
        var alertRate = total == 0 ? 0 : alerts.Select(a => a.CallId).Distinct().Count() * 100.0 / total;
        var qualityProblems = calls.Count(IsProblemTranscript);
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
                new("Quality Problems", qualityProblems.ToString("N0", CultureInfo.CurrentCulture), "Empty, failed, inaudible, or short transcripts"),
                new("Busiest Hour", busiest == null ? "--" : $"{busiest.Key:00}:00", busiest == null ? "No calls" : $"{busiest.Count():N0} calls"),
                new("Unique Talkgroups", calls.Select(c => c.Talkgroup).Distinct().Count().ToString("N0", CultureInfo.CurrentCulture), "Heard in selected range"),
                new("Avg Insight Confidence", "--", "Generated summaries only")
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
        var calls = await _database.ListCallsAsync(start, end, category, ct);
        if (string.Equals(groupBy, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new CategoryPageDto(category, "none", [new CategoryGroupDto("All calls", calls)]);
        }

        var groups = calls
            .GroupBy(c => DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().ToString("dddd, MMM d h tt", CultureInfo.CurrentCulture))
            .Select(g => new CategoryGroupDto(g.Key, g.ToList()))
            .ToList();

        return new CategoryPageDto(category, "time", groups);
    }

    public Task<List<IncidentDto>> ListIncidentsAsync(long start, long end, CancellationToken ct)
    {
        return _database.ListIncidentsAsync(start, end, ct);
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
        return calls.GroupBy(c => c.Talkgroup)
            .OrderByDescending(g => g.Count())
            .Take(12)
            .Select(g => new TopTalkgroupDto(
                GetTalkgroupLabel(g.First()),
                g.Key,
                g.Count(),
                g.Count() / (double)total,
                g.Max(c => c.StartTime),
                BuildTrend(g.ToList(), start, end, 12),
                FormatEndpoint(start),
                FormatBucket(start, end, 12),
                FormatEndpoint(end)))
            .ToList();
    }

    private static IReadOnlyList<double> BuildTrend(List<EngineCall> calls, long start, long end, int bins)
    {
        var counts = new double[bins];
        var span = Math.Max(1, end - start);
        foreach (var call in calls)
        {
            var index = (int)Math.Floor((call.StartTime - start) / (double)span * bins);
            index = Math.Clamp(index, 0, bins - 1);
            counts[index]++;
        }
        var max = Math.Max(1, counts.Max());
        return counts.Select(c => c / max).ToList();
    }

    public static string NormalizeCategory(string category)
    {
        category = (category ?? string.Empty).Trim().ToLowerInvariant();
        return category is "police" or "fire" or "ems" or "traffic" ? category : "other";
    }

    public static bool IsProblemTranscript(EngineCall call) =>
        IsTranscriptEmpty(call) || IsTranscriptFailureHint(call) || IsTranscriptInaudibleHint(call) || IsTranscriptShort(call);

    public static bool IsTranscriptEmpty(EngineCall call) => string.IsNullOrWhiteSpace(call.Transcription);

    public static bool IsTranscriptShort(EngineCall call) =>
        !IsTranscriptEmpty(call) &&
        !IsTranscriptFailureHint(call) &&
        !IsTranscriptInaudibleHint(call) &&
        call.Transcription.Trim().Length < 20;

    public static bool IsTranscriptFailureHint(EngineCall call) =>
        Regex.IsMatch(call.Transcription ?? string.Empty, @"(failed|error|exception|unable to transcribe)", RegexOptions.IgnoreCase);

    public static bool IsTranscriptInaudibleHint(EngineCall call) =>
        Regex.IsMatch(call.Transcription ?? string.Empty, @"(\[?inaudible\]?|\[?blank_audio\]?|blank audio|no audio|silence|static|garbled|unintelligible|unclear|distorted|muffled|beeping|music|phone ringing|crickets chirping|\[?pause\]?)", RegexOptions.IgnoreCase);

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
}
