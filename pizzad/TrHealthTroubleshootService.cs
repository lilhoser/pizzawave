using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class TrHealthTroubleshootService
{
    private static readonly Regex TrTimestampRegex = new(
        @"\[(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]",
        RegexOptions.Compiled);
    private static readonly Regex TrScopeRegex = new(
        @"\]\s+\((?:info|error|warning|debug)\)\s+\[(?<scope>[^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly TrConfigService _trConfig;
    private readonly ILogger<TrHealthTroubleshootService> _logger;

    public TrHealthTroubleshootService(EngineConfig config, EngineDatabase database, TrConfigService trConfig, ILogger<TrHealthTroubleshootService> logger)
    {
        _config = config;
        _database = database;
        _trConfig = trConfig;
        _logger = logger;
    }

    public async Task<TrTroubleshootDto> BuildAsync(long start, long end, bool bySystem, string baseline, CancellationToken ct)
    {
        var summaryStart = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
        var baselineStart = DateTimeOffset.UtcNow.AddDays(-BaselineDays(baseline)).ToUnixTimeSeconds();
        var log = await ReadJournalAsync(2000, ct);
        var rows = await _database.ListHealthSamplesAsync(Math.Min(start, baselineStart), end, ct);
        if (!rows.Any(r => IsDisplaySystemScope(r.Scope)))
            rows.AddRange(ParseJournalSamples(log));
        var summaryRows = rows.Where(r => new DateTimeOffset(r.WindowEndUtc).ToUnixTimeSeconds() >= summaryStart).ToList();
        var selectedRows = rows.Where(r =>
        {
            var rowStart = new DateTimeOffset(r.WindowStartUtc).ToUnixTimeSeconds();
            var rowEnd = new DateTimeOffset(r.WindowEndUtc).ToUnixTimeSeconds();
            return rowStart >= start && rowEnd <= end;
        }).ToList();

        var logOutput = TailLines(log, 300);
        var health = BuildSummary(summaryRows, selectedRows, rows, bySystem, baseline);
        var diagnostics = BuildDiagnostics(rows, summaryRows, selectedRows, baseline, bySystem, logOutput);
        return new TrTroubleshootDto(
            health,
            _trConfig.Validate(),
            logOutput,
            diagnostics,
            "TR health AI recommendations are not wired in the web engine yet. Use the health summary, remedies, and metrics tabs for deterministic findings.");
    }

    private TrHealthSummaryDto BuildSummary(List<TrHealthSampleDto> recentRows, List<TrHealthSampleDto> selectedRows, List<TrHealthSampleDto> baselineRows, bool bySystem, string baseline)
    {
        if (recentRows.Count == 0)
        {
            return new TrHealthSummaryDto
            {
                Title = "TR health summary unavailable",
                Window = "Window: last 24h",
                Source = "No TR health rows are stored for the last 24 hours.",
                SummaryText = "No usable TR health rows are available yet.",
                Samples = selectedRows.OrderByDescending(r => r.WindowStartUtc).Take(200).ToList()
            };
        }

        var globalRows = recentRows.Where(IsGlobal).ToList();
        var global = Aggregate(globalRows.Count > 0 ? globalRows : recentRows);
        var metrics = BuildMetricRows(global);
        var systemRows = BuildSystemRows(recentRows);
        var remedies = BuildRemedies(global);
        var last = recentRows.Max(r => r.WindowEndUtc);
        var hasIssue = metrics.Any(m => m.IsIssue) || systemRows.Any(m => m.IsIssue);

        return new TrHealthSummaryDto
        {
            Title = hasIssue ? "TR health summary: issues detected" : "TR health summary: no obvious issues",
            Window = "Window: last 24h (health summary uses last 24h only)",
            LastWindow = $"Last parsed bucket: {last.ToLocalTime():yyyy-MM-dd HH:mm:ss}",
            Source = $"Source: pizzad SQLite health samples from journald service '{_config.TrunkRecorder.LogServiceName}'",
            SummaryText = BuildSummaryText(global, systemRows, last),
            Metrics = metrics,
            Systems = systemRows.Count == 0 ? [new TrHealthMetricDto("No system rows", "-", "No system-scoped log lines were parsed for this window.", false)] : systemRows,
            Remedies = remedies,
            Charts = BuildCharts(selectedRows, baselineRows, bySystem, baseline),
            Samples = selectedRows.OrderByDescending(r => r.WindowStartUtc).Take(500).ToList()
        };
    }

    private static List<TrHealthMetricDto> BuildMetricRows(TrHealthAggregate agg) =>
    [
        new("Decode samples at 0/sec", $"{agg.DecodeZeroPercent:F2}%", agg.DecodeZeroPercent >= 25 ? "Frequent zero decode samples suggest unstable control-channel decode." : "Lower is better.", agg.DecodeZeroPercent >= 25.0),
        new("Average decode rate", FormatDecodeRateWithConfidence(agg), "Computed from Control Channel Message Decode Rate lines.", HasSufficientDecodeSamples(agg) && agg.AvgDecodeRate <= 0.10),
        new("Decode sample lines", agg.DecodeSampleLines.ToString("N0", CultureInfo.InvariantCulture), "Decode-rate confidence is low when this is small (shown as N/A).", agg.DecodeSampleLines < 20),
        new("Control-channel retunes", agg.Retunes.ToString("N0", CultureInfo.InvariantCulture), "High counts can indicate weak/unstable control-channel reception or incorrect channel list.", agg.Retunes >= Math.Max(20, agg.Windows * 4)),
        new("Recorded calls concluded", agg.CallsConcluded.ToString("N0", CultureInfo.InvariantCulture), "Count of Concluding Recorded Call lines.", false),
        new("No transmissions recorded", agg.NoTxRecorded.ToString("N0", CultureInfo.InvariantCulture), "Includes no transmissions and recorder capacity failures.", agg.NoTxRecorded > 0),
        new("Sample-source stops", agg.SampleStops.ToString("N0", CultureInfo.InvariantCulture), "Nonzero means trunk-recorder reported a source stopped receiving samples.", agg.SampleStops > 0),
        new("No source covering frequency", agg.UnableSource.ToString("N0", CultureInfo.InvariantCulture), "Usually points to source min/max coverage or SDR count/configuration.", agg.UnableSource > 0),
        new("5-minute windows parsed", agg.Windows.ToString("N0", CultureInfo.InvariantCulture), "Number of summary buckets generated from logs.", false)
    ];

    private static List<TrHealthMetricDto> BuildSystemRows(List<TrHealthSampleDto> rows)
    {
        return rows
            .Where(r => IsDisplaySystemScope(r.Scope))
            .GroupBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var agg = Aggregate(g.ToList());
                var issue = agg.DecodeZeroPercent >= 25.0
                    || agg.SampleStops > 0
                    || agg.UnableSource > 0
                    || agg.NoTxRecorded > 0
                    || (agg.CallsConcluded >= 100 && HasSufficientDecodeSamples(agg) && agg.AvgDecodeRate <= 0.10)
                    || (agg.CallsConcluded >= 100 && agg.DecodeSampleLines < 20);
                var notes = $"decodeSamples={agg.DecodeSampleLines:N0}, decode0={agg.DecodeZeroPercent:F2}%, avg={FormatDecodeRateWithConfidence(agg)}, retunes={agg.Retunes:N0}, calls={agg.CallsConcluded:N0}, noTx={agg.NoTxRecorded:N0}";
                return new TrHealthMetricDto(g.Key, issue ? "Issues detected" : "No obvious issues", notes, issue);
            })
            .ToList();
    }

    private static List<TrHealthMetricDto> BuildRemedies(TrHealthAggregate agg)
    {
        var rows = new List<TrHealthMetricDto>();
        if (agg.SampleStops > 0)
            rows.Add(new("Sample-source stops", "-", "Check SDR USB stability, power, hub/cable quality, and whether the SDR process is being starved.", true));
        if (agg.DecodeZeroPercent >= 40.0)
            rows.Add(new("Decode rate frequently zero", "-", "Verify antenna/feedline, control-channel frequency list, gain/PPM calibration, and local RF noise.", true));
        else if (agg.DecodeZeroPercent >= 25.0)
            rows.Add(new("Decode rate intermittently zero", "-", "Watch signal quality and consider gain/PPM adjustment.", true));
        if (agg.Retunes >= Math.Max(20, agg.Windows * 4))
            rows.Add(new("High retunes", "-", "Confirm configured control channels and site coverage; frequent retunes often mean weak/unstable control-channel decode.", true));
        if (agg.UnableSource > 0)
            rows.Add(new("No source covering frequency", "-", "Check source min/max ranges and whether enough SDRs cover all voice channels.", true));
        if (agg.NoTxRecorded > 0)
            rows.Add(new("No transmissions recorded", "-", "This can follow UPDATE-not-GRANT events, poor decode, or recorder contention.", true));
        if (rows.Count == 0)
            rows.Add(new("No obvious remedies", "-", "The current summary did not cross the built-in thresholds.", false));
        return rows;
    }

    private static IReadOnlyList<TrHealthChartDto> BuildCharts(List<TrHealthSampleDto> selectedRows, List<TrHealthSampleDto> baselineRows, bool bySystem, string baseline)
    {
        var rows = selectedRows.Count > 0 ? selectedRows : baselineRows.Where(r => new DateTimeOffset(r.WindowEndUtc).ToUnixTimeSeconds() >= DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds()).ToList();
        var labels = BuildHourLabels(rows);
        if (labels.Count == 0)
            return [];

        return bySystem
            ? BuildSystemCharts(rows, baselineRows, labels, baseline)
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
            BuildChart("Decode Zero Samples", "Y axis: count of Control Channel Message Decode Rate samples at 0/sec", "N0", hours, [("", Hourly(global, hours, a => a.DecodeZero))], baselineGlobal, baseline, a => a.DecodeZero),
            BuildChart("Average Decode Rate", "Y axis: average Control Channel Message Decode Rate per second", "F1", hours, [("", Hourly(global, hours, a => a.AvgDecodeRate))], baselineGlobal, baseline, a => a.AvgDecodeRate),
            BuildChart("Control-Channel Retunes", "Y axis: retune events per hour", "N0", hours, [("", Hourly(global, hours, a => a.Retunes))], baselineGlobal, baseline, a => a.Retunes),
            BuildChart("No Transmissions Recorded", "Y axis: calls with no recorded transmissions per hour", "N0", hours, [("", Hourly(global, hours, a => a.NoTxRecorded))], baselineGlobal, baseline, a => a.NoTxRecorded),
            BuildChart("Sample Source Stops", "Y axis: source stopped receiving samples events per hour", "N0", hours, [("", Hourly(global, hours, a => a.SampleStops))], baselineGlobal, baseline, a => a.SampleStops)
        ];
    }

    private static IReadOnlyList<TrHealthChartDto> BuildSystemCharts(List<TrHealthSampleDto> rows, List<TrHealthSampleDto> baselineRows, List<DateTime> hours, string baseline)
    {
        var scopes = rows.Where(r => IsDisplaySystemScope(r.Scope))
            .GroupBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(r => r.DecodeZero + r.Retunes + r.NoTxRecorded + r.SampleStops))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .Select(g => g.Key)
            .ToList();
        if (scopes.Count == 0)
            return [];

        List<(string Label, IReadOnlyList<double> Values)> Series(Func<TrHealthAggregate, double> selector) =>
            scopes.Select(scope => (scope, (IReadOnlyList<double>)Hourly(rows.Where(r => string.Equals(r.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList(), hours, selector))).ToList();

        return
        [
            BuildChart("Decode Zero Samples by System", "Y axis: count of 0/sec decode samples per hour", "N0", hours, Series(a => a.DecodeZero), baselineRows, baseline, a => a.DecodeZero, scopes),
            BuildChart("Average Decode Rate by System", "Y axis: average Control Channel Message Decode Rate per second", "F1", hours, Series(a => a.AvgDecodeRate), baselineRows, baseline, a => a.AvgDecodeRate, scopes),
            BuildChart("Retunes by System", "Y axis: retune events per hour", "N0", hours, Series(a => a.Retunes), baselineRows, baseline, a => a.Retunes, scopes),
            BuildChart("No Transmissions by System", "Y axis: calls with no recorded transmissions per hour", "N0", hours, Series(a => a.NoTxRecorded), baselineRows, baseline, a => a.NoTxRecorded, scopes),
            BuildChart("Sample Source Stops by System", "Y axis: source stopped receiving samples events per hour", "N0", hours, Series(a => a.SampleStops), baselineRows, baseline, a => a.SampleStops, scopes)
        ];
    }

    private static TrHealthChartDto BuildChart(string title, string yAxis, string format, List<DateTime> hours, List<(string Label, IReadOnlyList<double> Values)> series, List<TrHealthSampleDto> baselineRows, string baseline, Func<TrHealthAggregate, double> selector, IReadOnlyList<string>? scopes = null)
    {
        var chartSeries = series.Select(s => new TrHealthSeriesDto(s.Label, s.Values)).ToList();
        var comparisonRows = scopes == null
            ? baselineRows.Where(IsGlobal).ToList()
            : baselineRows.Where(r => scopes.Contains(r.Scope, StringComparer.OrdinalIgnoreCase)).ToList();
        var hasBaselineHistory = HasBaselineHistory(comparisonRows, baseline);
        var baselineValues = BaselineSeries(comparisonRows, hours, selector, baseline);
        chartSeries.Add(new TrHealthSeriesDto(hasBaselineHistory ? $"{baseline} baseline" : $"{baseline} baseline (no history yet)", baselineValues, true));

        return new TrHealthChartDto(title, yAxis, format, hours.Select(h => h.ToString("MM-dd HH:00", CultureInfo.InvariantCulture)).ToList(), chartSeries);
    }

    private static List<DateTime> BuildHourLabels(List<TrHealthSampleDto> rows) =>
        rows.Select(r => r.WindowEndUtc.ToLocalTime())
            .Select(d => new DateTime(d.Year, d.Month, d.Day, d.Hour, 0, 0))
            .Distinct()
            .OrderBy(d => d)
            .TakeLast(24)
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

    private static List<double> BaselineSeries(List<TrHealthSampleDto> rows, List<DateTime> hours, Func<TrHealthAggregate, double> selector, string baseline)
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
            return selector(Aggregate(matching)) / dayCount;
        }).ToList();
    }

    private static bool HasBaselineHistory(List<TrHealthSampleDto> rows, string baseline)
    {
        var cutoff = DateTime.UtcNow.AddDays(-BaselineDays(baseline));
        return rows.Any(r => r.WindowEndUtc >= cutoff && r.WindowEndUtc < DateTime.UtcNow.AddHours(-24));
    }

    private static string BuildSummaryText(TrHealthAggregate agg, IReadOnlyList<TrHealthMetricDto> systems, DateTime last)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Metric                         Value");
        sb.AppendLine("-----------------------------------------------");
        sb.AppendLine($"5-minute windows               {agg.Windows}");
        sb.AppendLine($"Decode sample lines            {agg.DecodeSampleLines}");
        sb.AppendLine($"Decode samples at 0/sec        {agg.DecodeZeroPercent:F2}%");
        sb.AppendLine($"Average decode rate            {FormatDecodeRateWithConfidence(agg)}");
        sb.AppendLine($"Control-channel retunes        {agg.Retunes}");
        sb.AppendLine($"Recorded calls concluded       {agg.CallsConcluded}");
        sb.AppendLine($"No transmissions recorded      {agg.NoTxRecorded}");
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

    private static List<TrHealthSampleDto> ParseJournalSamples(string log)
    {
        var buckets = new Dictionary<(DateTime StartUtc, string Scope), List<string>>();
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var ts = ParseTrTimestamp(line);
            if (ts == null)
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

    private static TrHealthAggregate Aggregate(List<TrHealthSampleDto> rows)
    {
        var decodeLines = rows.Sum(r => r.DecodeLines);
        var decodeZero = rows.Sum(r => r.DecodeZero);
        return new TrHealthAggregate(
            rows.Count,
            decodeLines,
            decodeZero,
            rows.Sum(r => r.DecodeRateTotal),
            rows.Sum(r => r.Retunes),
            rows.Sum(r => r.CallsConcluded),
            rows.Sum(r => r.UpdateNotGrant),
            rows.Sum(r => r.NoTxRecorded),
            rows.Sum(r => r.SampleStops),
            rows.Sum(r => r.UnableSource));
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

    private static string FormatDecodeRateWithConfidence(TrHealthAggregate agg) =>
        HasSufficientDecodeSamples(agg) ? $"{agg.AvgDecodeRate:F2}/sec" : "N/A";

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
        int Retunes,
        int CallsConcluded,
        int UpdateNotGrant,
        int NoTxRecorded,
        int SampleStops,
        int UnableSource)
    {
        public double DecodeZeroPercent => DecodeSampleLines == 0 ? 0 : DecodeZero * 100.0 / DecodeSampleLines;
        public double AvgDecodeRate => DecodeSampleLines == 0 ? 0 : DecodeRateTotal / DecodeSampleLines;
    }
}
