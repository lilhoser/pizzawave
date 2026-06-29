using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class TrHealthCollector : BackgroundService
{
    private static readonly Regex CcSummaryDecodeRateRegex = new(
        @"\[(?<scope>[^\]]+)\]\s+(?<freq>\d+(?:\.\d+)?)\s+MHz\s+(?<rate>-?\d+(?:\.\d+)?)\s+msg/sec",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LowDecodeWarningRateRegex = new(
        @"Control Channel Message Decode Rate:\s*(?<rate>-?\d+(?:\.\d+)?)\s*/sec",
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
    private readonly LiveTrActivityMonitor _liveTrActivity;
    private readonly ILogger<TrHealthCollector> _logger;

    public TrHealthCollector(EngineConfig config, EngineDatabase database, EventStream events, LiveTrActivityMonitor liveTrActivity, ILogger<TrHealthCollector> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _liveTrActivity = liveTrActivity;
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
        var runtime = await ReadRuntimeResourceSnapshotAsync(ct);
        var log = await ReadJournalAsync(start, end, ct);
        if (string.IsNullOrWhiteSpace(log) || IsEmptyJournalResult(log))
        {
            _logger.LogDebug("TR health collection found no journal rows for {Start:u} - {End:u}", start, end);
            log = string.Empty;
        }

        var global = BuildSample("global", start, end, log) with
        {
            TrCpuPercent = runtime.TrCpuPercent,
            TrRssMb = runtime.TrRssMb,
            TrVszMb = runtime.TrVszMb,
            TrThreadCount = runtime.TrThreadCount,
            HostTempC = runtime.HostTempC,
            HostThrottledFlags = runtime.HostThrottledFlags,
            HostLoad1 = runtime.HostLoad1,
            HostLoad5 = runtime.HostLoad5,
            HostLoad15 = runtime.HostLoad15
        };
        if (!HasAnyHealthSignal(global) && !HasAnyResourceSignal(global))
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
        if (HasLiveTrCaptureSignal(global))
            _liveTrActivity.MarkTrHealth(DateTime.UtcNow);
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

    private async Task<RuntimeResourceSnapshot> ReadRuntimeResourceSnapshotAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return new RuntimeResourceSnapshot();

        var process = await ReadTrProcessSnapshotAsync(ct);
        var temp = await ReadHostTemperatureCAsync(ct);
        var throttled = await ReadThrottledFlagsAsync(ct);
        var load = await ReadLoadAverageAsync(ct);
        return process with
        {
            HostTempC = temp,
            HostThrottledFlags = throttled,
            HostLoad1 = load.Load1,
            HostLoad5 = load.Load5,
            HostLoad15 = load.Load15
        };
    }

    private async Task<RuntimeResourceSnapshot> ReadTrProcessSnapshotAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ps")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add("trunk-recorder");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("pcpu=,rss=,vsz=,nlwp=");

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return new RuntimeResourceSnapshot();
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line))
                return new RuntimeResourceSnapshot();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
                return new RuntimeResourceSnapshot();
            return new RuntimeResourceSnapshot(
                ParseDouble(parts[0]),
                ParseDouble(parts[1]) / 1024d,
                ParseDouble(parts[2]) / 1024d,
                (int)Math.Round(ParseDouble(parts[3])),
                0,
                string.Empty,
                0,
                0,
                0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read trunk-recorder process resource metrics");
            return new RuntimeResourceSnapshot();
        }
    }

    private async Task<double> ReadHostTemperatureCAsync(CancellationToken ct)
    {
        const string thermalPath = "/sys/class/thermal/thermal_zone0/temp";
        try
        {
            if (File.Exists(thermalPath))
            {
                var raw = (await File.ReadAllTextAsync(thermalPath, ct)).Trim();
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var milliC))
                    return milliC / 1000d;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read host thermal zone");
        }

        return 0;
    }

    private async Task<string> ReadThrottledFlagsAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("vcgencmd")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("get_throttled");

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
                return string.Empty;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
                return string.Empty;
            return output.Trim().Replace("throttled=", "", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<(double Load1, double Load5, double Load15)> ReadLoadAverageAsync(CancellationToken ct)
    {
        try
        {
            var raw = await File.ReadAllTextAsync("/proc/loadavg", ct);
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 3)
                return (ParseDouble(parts[0]), ParseDouble(parts[1]), ParseDouble(parts[2]));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read host load average");
        }
        return (0, 0, 0);
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
        || sample.RecorderExhausted > 0
        || sample.SampleStops > 0
        || sample.UnableSource > 0
        || sample.TuningErrSamples > 0;

    private static bool HasLiveTrCaptureSignal(TrHealthSampleDto sample) =>
        sample.DecodeLines > 0
        || sample.CcSummaryDecodeLines > 0
        || sample.LowDecodeWarningLines > 0
        || sample.CallsStarted > 0
        || sample.CallsConcluded > 0
        || sample.Retunes > 0;

    private static bool HasAnyResourceSignal(TrHealthSampleDto sample) =>
        sample.TrCpuPercent > 0
        || sample.TrRssMb > 0
        || sample.TrVszMb > 0
        || sample.TrThreadCount > 0
        || sample.HostTempC > 0
        || !string.IsNullOrWhiteSpace(sample.HostThrottledFlags)
        || sample.HostLoad1 > 0
        || sample.HostLoad5 > 0
        || sample.HostLoad15 > 0;

    public static TrHealthSampleDto BuildSample(string scope, DateTime startUtc, DateTime endUtc, string log)
    {
        var ccSummaryDecodeLines = 0;
        var ccSummaryDecodeZero = 0;
        var ccSummaryDecodeRateTotal = 0.0;
        var lowDecodeWarningLines = 0;
        var lowDecodeWarningZero = 0;
        var lowDecodeWarningRateTotal = 0.0;
        var tuningErrSamples = 0;
        var tuningErrTotalAbsHz = 0.0;
        var tuningErrMaxAbsHz = 0.0;
        foreach (var line in log.Split('\n'))
        {
            if (TryParseCcSummaryDecodeRate(line, out var ccRate))
            {
                ccSummaryDecodeLines++;
                ccSummaryDecodeRateTotal += ccRate;
                if (Math.Abs(ccRate) < 0.0001)
                    ccSummaryDecodeZero++;
            }

            if (TryParseLowDecodeWarningRate(line, out var warningRate))
            {
                lowDecodeWarningLines++;
                lowDecodeWarningRateTotal += warningRate;
                if (Math.Abs(warningRate) < 0.0001)
                    lowDecodeWarningZero++;
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

        var decodeLines = ccSummaryDecodeLines > 0 ? ccSummaryDecodeLines : lowDecodeWarningLines;
        var decodeZero = ccSummaryDecodeLines > 0 ? ccSummaryDecodeZero : lowDecodeWarningZero;
        var decodeRateTotal = ccSummaryDecodeLines > 0 ? ccSummaryDecodeRateTotal : lowDecodeWarningRateTotal;
        return new TrHealthSampleDto
        {
            WindowStartUtc = startUtc,
            WindowEndUtc = endUtc,
            Scope = NormalizeScope(scope),
            DecodeLines = decodeLines,
            DecodeZero = decodeZero,
            DecodeZeroPct = decodeLines == 0 ? 0 : decodeZero * 100.0 / decodeLines,
            DecodeRateTotal = decodeRateTotal,
            CcSummaryDecodeLines = ccSummaryDecodeLines,
            CcSummaryDecodeZero = ccSummaryDecodeZero,
            CcSummaryDecodeRateTotal = ccSummaryDecodeRateTotal,
            LowDecodeWarningLines = lowDecodeWarningLines,
            LowDecodeWarningZero = lowDecodeWarningZero,
            LowDecodeWarningRateTotal = lowDecodeWarningRateTotal,
            Retunes = Count(log, "Retuning to Control Channel"),
            CallsStarted = Count(log, "Starting P25 Recorder"),
            CallsConcluded = Count(log, "Concluding Recorded Call"),
            UpdateNotGrant = Count(log, "update not grant") + Count(log, "This was an UPDATE"),
            NoTxRecorded = Count(log, "No Transmissions were recorded")
                + Count(log, "no transmission")
                + Count(log, "no tx")
                + Count(log, "not recording transmission"),
            RecorderExhausted = Count(log, "only 0 recorders are available"),
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

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static bool TryParseCcSummaryDecodeRate(string line, out double rate)
    {
        rate = 0;
        if (!line.Contains("msg/sec", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = CcSummaryDecodeRateRegex.Match(line);
        if (!match.Success)
            return false;

        return double.TryParse(match.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate);
    }

    private static bool TryParseLowDecodeWarningRate(string line, out double rate)
    {
        rate = 0;
        if (!line.Contains("Control Channel Message Decode Rate", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = LowDecodeWarningRateRegex.Match(line);
        if (!match.Success)
            return false;

        return double.TryParse(match.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate);
    }

    private sealed record RuntimeResourceSnapshot(
        double TrCpuPercent = 0,
        double TrRssMb = 0,
        double TrVszMb = 0,
        int TrThreadCount = 0,
        double HostTempC = 0,
        string HostThrottledFlags = "",
        double HostLoad1 = 0,
        double HostLoad5 = 0,
        double HostLoad15 = 0);
}
