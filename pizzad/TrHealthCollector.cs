using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class TrHealthCollector : BackgroundService
{
    private static readonly Regex DecodeRateRegex = new(
        @"(?:decode|decoded)[^0-9-]*(?<rate>-?\d+(?:\.\d+)?)\s*(?:/sec|per sec|hz)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TuningErrorRegex = new(
        @"(?:tuning|tune)[^0-9-]*(?:error|err)[^0-9-]*(?<hz>-?\d+(?:\.\d+)?)\s*hz",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SystemScopeRegex = new(
        @"\]\s+\((?:info|error|warning|debug)\)\s+\[(?<scope>[^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly ILogger<TrHealthCollector> _logger;

    public TrHealthCollector(EngineConfig config, EngineDatabase database, EventStream events, ILogger<TrHealthCollector> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TR health collection failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _config.TrunkRecorder.HealthWindowMinutes)), stoppingToken);
        }
    }

    private async Task CollectOnceAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var end = DateTime.UtcNow;
        var start = end.AddMinutes(-Math.Max(1, _config.TrunkRecorder.HealthWindowMinutes));
        var log = await ReadJournalAsync(start, end, ct);
        if (string.IsNullOrWhiteSpace(log))
            return;

        var global = BuildSample("global", start, end, log);
        await _database.InsertHealthSampleAsync(global, ct);

        foreach (var scope in ExtractSystemScopes(log))
        {
            var scopedLines = string.Join('\n', log.Split('\n').Where(line => line.Contains($"[{scope}]", StringComparison.OrdinalIgnoreCase)));
            if (scopedLines.Length == 0)
                continue;
            await _database.InsertHealthSampleAsync(BuildSample(scope, start, end, scopedLines), ct);
        }

        await _events.PublishAsync("health_updated", new { windowStartUtc = start, windowEndUtc = end }, ct);
    }

    private async Task<string> ReadJournalAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var args = $"-u {_config.TrunkRecorder.LogServiceName} --since \"{startUtc:yyyy-MM-dd HH:mm:ss}\" --until \"{endUtc:yyyy-MM-dd HH:mm:ss}\" --no-pager";
        var psi = new ProcessStartInfo("journalctl", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi);
        if (process == null)
            return string.Empty;

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }

    public static TrHealthSampleDto BuildSample(string scope, DateTime startUtc, DateTime endUtc, string log)
    {
        var decodeLines = 0;
        var decodeZero = 0;
        var decodeRateTotal = 0.0;
        var tuningErrSamples = 0;
        var tuningErrTotalAbsHz = 0.0;
        var tuningErrMaxAbsHz = 0.0;
        foreach (var line in log.Split('\n'))
        {
            if (TryParseDecodeRate(line, out var rate))
            {
                decodeLines++;
                decodeRateTotal += rate;
                if (Math.Abs(rate) < 0.0001)
                    decodeZero++;
            }

            var tuningMatch = TuningErrorRegex.Match(line);
            if (tuningMatch.Success && double.TryParse(tuningMatch.Groups["hz"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz))
            {
                var absHz = Math.Abs(hz);
                tuningErrSamples++;
                tuningErrTotalAbsHz += absHz;
                tuningErrMaxAbsHz = Math.Max(tuningErrMaxAbsHz, absHz);
            }
        }

        return new TrHealthSampleDto
        {
            WindowStartUtc = startUtc,
            WindowEndUtc = endUtc,
            Scope = scope,
            DecodeLines = decodeLines,
            DecodeZero = decodeZero,
            DecodeZeroPct = decodeLines == 0 ? 0 : decodeZero * 100.0 / decodeLines,
            DecodeRateTotal = decodeRateTotal,
            Retunes = Count(log, "Retuning to Control Channel"),
            CallsStarted = Count(log, "Starting P25 Recorder"),
            CallsConcluded = Count(log, "Concluding Recorded Call"),
            UpdateNotGrant = Count(log, "update not grant") + Count(log, "This was an UPDATE"),
            NoTxRecorded = Count(log, "No Transmissions were recorded")
                + Count(log, "no transmission")
                + Count(log, "no tx")
                + Count(log, "not recording transmission")
                + Count(log, "only 0 recorders are available"),
            SampleStops = Count(log, "has stopped receiving samples")
                + Count(log, "sample stop")
                + Count(log, "stopped samples"),
            UnableSource = Count(log, "no source covering") + Count(log, "Unable to find a source"),
            TuningErrSamples = tuningErrSamples,
            TuningErrTotalAbsHz = tuningErrTotalAbsHz,
            TuningErrMaxAbsHz = tuningErrMaxAbsHz
        };
    }

    private static IReadOnlyList<string> ExtractSystemScopes(string log) =>
        SystemScopeRegex.Matches(log)
            .Select(m => m.Groups["scope"].Value)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !s.All(char.IsDigit) && !s.StartsWith("source", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int Count(string haystack, string needle) =>
        Regex.Matches(haystack, Regex.Escape(needle), RegexOptions.IgnoreCase).Count;

    private static bool TryParseDecodeRate(string line, out double rate)
    {
        rate = 0;
        if (!line.Contains("decode", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = DecodeRateRegex.Match(line);
        if (!match.Success)
            return false;

        return double.TryParse(match.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate);
    }
}
