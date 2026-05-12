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
        _logger.LogInformation(
            "TR health collector starting: service={ServiceName}, windowMinutes={WindowMinutes}",
            _config.TrunkRecorder.LogServiceName,
            Math.Max(1, _config.TrunkRecorder.HealthWindowMinutes));

        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("TR health collector polling");
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

        var windowMinutes = Math.Max(1, _config.TrunkRecorder.HealthWindowMinutes);
        var end = FloorWindow(DateTime.UtcNow, windowMinutes);
        var start = end.AddMinutes(-windowMinutes);
        var log = await ReadJournalAsync(start, end, ct);
        if (string.IsNullOrWhiteSpace(log) || IsEmptyJournalResult(log))
        {
            _logger.LogDebug("TR health collection found no journal rows for {Start:u} - {End:u}", start, end);
            return;
        }

        var global = BuildSample("global", start, end, log);
        if (!HasAnyHealthSignal(global))
        {
            _logger.LogDebug("TR health collection skipped empty window {Start:u} - {End:u}", start, end);
            return;
        }

        var samplesWritten = 1;
        await _database.InsertHealthSampleAsync(global, ct);

        var logLines = log.Split('\n');
        foreach (var scope in ExtractSystemScopes(log))
        {
            var scopedLines = string.Join('\n', logLines.Where(line => string.Equals(ExtractSystemScope(line), scope, StringComparison.OrdinalIgnoreCase)));
            if (scopedLines.Length == 0)
                continue;
            await _database.InsertHealthSampleAsync(BuildSample(scope, start, end, scopedLines), ct);
            samplesWritten++;
        }

        await _events.PublishAsync("health_updated", new { windowStartUtc = start, windowEndUtc = end }, ct);
        _logger.LogInformation(
            "TR health collected {SamplesWritten} sample(s) for {Start:u} - {End:u}: decodeLines={DecodeLines}, callsStarted={CallsStarted}, callsConcluded={CallsConcluded}",
            samplesWritten,
            start,
            end,
            global.DecodeLines,
            global.CallsStarted,
            global.CallsConcluded);
    }

    private async Task<string> ReadJournalAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
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

        using var process = Process.Start(psi);
        if (process == null)
            return string.Empty;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                _logger.LogWarning(
                    "TR health journalctl exited with code {ExitCode} for {Start:u} - {End:u}: {Error}",
                    process.ExitCode,
                    startUtc,
                    endUtc,
                    TrimForLog(error));
            }

            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            _logger.LogWarning("TR health journalctl timed out for {Start:u} - {End:u}", startUtc, endUtc);
            return string.Empty;
        }
    }

    private static DateTime FloorWindow(DateTime value, int windowMinutes)
    {
        value = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var ticks = TimeSpan.FromMinutes(windowMinutes).Ticks;
        return new DateTime(value.Ticks - value.Ticks % ticks, DateTimeKind.Utc);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static string TrimForLog(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500];
    }

    private static bool IsEmptyJournalResult(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(line => line.StartsWith("-- No entries --", StringComparison.OrdinalIgnoreCase));

    private static bool HasAnyHealthSignal(TrHealthSampleDto sample) =>
        sample.DecodeLines > 0
        || sample.Retunes > 0
        || sample.CallsStarted > 0
        || sample.CallsConcluded > 0
        || sample.UpdateNotGrant > 0
        || sample.NoTxRecorded > 0
        || sample.SampleStops > 0
        || sample.UnableSource > 0
        || sample.TuningErrSamples > 0;

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
            Scope = NormalizeScope(scope),
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
        log.Split('\n')
            .Select(ExtractSystemScope)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !s.All(char.IsDigit) && !s.StartsWith("source", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string ExtractSystemScope(string line)
    {
        var match = SystemScopeRegex.Match(line);
        return match.Success ? NormalizeScope(match.Groups["scope"].Value) : string.Empty;
    }

    private static string NormalizeScope(string scope) => scope.Trim();

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
