namespace pizzad;

public sealed class SystemCpuSnapshotService
{
    private readonly EngineDatabase _database;

    public SystemCpuSnapshotService(EngineDatabase database)
    {
        _database = database;
    }

    public async Task<SystemCpuSnapshotDto> BuildAsync(CancellationToken ct)
    {
        const int windowMinutes = 120;
        var now = DateTime.UtcNow;
        var startUnix = new DateTimeOffset(now.AddMinutes(-windowMinutes)).ToUnixTimeSeconds();
        var endUnix = new DateTimeOffset(now).ToUnixTimeSeconds();
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var samples = (await _database.ListHealthSamplesAsync(startUnix, endUnix, ct))
            .Where(s => string.Equals(s.Scope, "global", StringComparison.OrdinalIgnoreCase))
            .Where(s => s.TrCpuPercent > 0 || s.TrRssMb > 0 || s.HostTempC > 0 || s.HostLoad1 > 0)
            .OrderBy(s => s.WindowEndUtc)
            .ToList();
        var latest = samples.OrderByDescending(s => s.WindowEndUtc).FirstOrDefault();
        var live = ReadLiveHostSample(latest, processorCount);
        var peakCpu = samples.Select(s => s.TrCpuPercent).DefaultIfEmpty(0).Max();
        var peakLoad = samples.Select(s => s.HostLoad1).DefaultIfEmpty(0).Max();
        var peak = new SystemCpuSampleDto(
            null,
            peakCpu,
            HostPercent(peakCpu, processorCount),
            samples.Select(s => s.TrRssMb).DefaultIfEmpty(0).Max(),
            samples.Select(s => s.TrThreadCount).DefaultIfEmpty(0).Max(),
            Math.Max(samples.Select(s => s.HostTempC).DefaultIfEmpty(0).Max(), live?.HostTempC ?? 0),
            Math.Max(peakLoad, live?.HostLoad1 ?? 0),
            Math.Max(HostPercent(peakLoad * 100d, processorCount), live?.HostLoadHostPercent ?? 0),
            FirstNonEmpty(samples.Select(s => s.HostThrottledFlags).FirstOrDefault(HasHistoricalThrottleFlag), live?.HostThrottledFlags));

        var latestDto = live ?? (latest == null ? null : ToSample(latest, processorCount));
        var insights = BuildInsights(latestDto, peak, processorCount);
        var actionableInsights = insights.Where(i => i.Label != "TR threads").ToList();
        var severity = actionableInsights.Any(i => i.Status == "error") ? "error" : actionableInsights.Any(i => i.Status == "warning") ? "warning" : "ok";
        var summary = latestDto == null
            ? "No recent TR resource samples are available."
            : severity == "error"
                ? "Resource or thermal pressure needs operator attention."
                : severity == "warning"
                    ? "Resource load is elevated but not currently thermal-critical."
                    : "CPU and thermal load are within the current operating envelope.";

        return new SystemCpuSnapshotDto(now, windowMinutes, processorCount, latestDto, peak, severity, summary, insights);
    }

    private static SystemCpuSampleDto ToSample(TrHealthSampleDto sample, int processorCount) =>
        new(
            sample.WindowEndUtc,
            sample.TrCpuPercent,
            HostPercent(sample.TrCpuPercent, processorCount),
            sample.TrRssMb,
            sample.TrThreadCount,
            sample.HostTempC,
            sample.HostLoad1,
            HostPercent(sample.HostLoad1 * 100d, processorCount),
            sample.HostThrottledFlags);

    private static IReadOnlyList<SystemCpuInsightDto> BuildInsights(SystemCpuSampleDto? latest, SystemCpuSampleDto peak, int processorCount)
    {
        if (latest == null)
            return [new("Latest sample", "n/a", "warning", "No recent TR health resource sample was found.")];

        var currentThrottle = HasCurrentThrottleFlag(latest.HostThrottledFlags);
        var historicalThrottle = HasHistoricalThrottleFlag(peak.HostThrottledFlags);
        var cpuStatus = latest.TrCpuHostPercent >= 90 ? "error" : latest.TrCpuHostPercent >= 75 ? "warning" : "ok";
        var tempStatus = latest.HostTempC >= 80 || currentThrottle ? "error" : latest.HostTempC >= 70 ? "warning" : "ok";
        var loadStatus = latest.HostLoadHostPercent >= 150 ? "warning" : "ok";
        var rssStatus = latest.TrRssMb >= 2048 ? "warning" : "ok";
        var threadStatus = latest.TrThreadCount >= 250 ? "warning" : "ok";

        return
        [
            new("TR CPU", $"{latest.TrCpuPercent:F0}% ({latest.TrCpuHostPercent:F0}% of host)", cpuStatus, $"100% equals one saturated core. This host reports {processorCount:N0} processor(s), so full host saturation is about {processorCount * 100:N0}%."),
            new("Host load", $"{latest.HostLoad1:F2} ({latest.HostLoadHostPercent:F0}% of host)", loadStatus, "1-minute load average normalized by processor count. Sustained values over 100% mean runnable work is queued."),
            new("Temperature", $"{latest.HostTempC:F1} C current, {peak.HostTempC:F1} C peak", tempStatus, "70 C is a warning band for enclosed Raspberry Pi operation. 80 C or current throttling is actionable."),
            new("Throttle flags", string.IsNullOrWhiteSpace(latest.HostThrottledFlags) ? "none" : latest.HostThrottledFlags, currentThrottle ? "error" : historicalThrottle ? "warning" : "ok", "Raspberry Pi throttle flags are definitive thermal/power evidence when available."),
            new("TR memory", $"{latest.TrRssMb:F0} MB RSS", rssStatus, "Resident memory used by trunk-recorder. This is actionable when it crowds system memory, not by itself."),
            new("TR threads", $"{latest.TrThreadCount:N0}", threadStatus, "High thread count means many recorder/DSP workers. Treat it as capture complexity, not as proof of thermal danger.")
        ];
    }

    private static double HostPercent(double cpuPercent, int processorCount) => cpuPercent / Math.Max(1, processorCount);

    private static SystemCpuSampleDto? ReadLiveHostSample(TrHealthSampleDto? latest, int processorCount)
    {
        var load = ReadLoadAverage();
        var temp = ReadTemperatureC();
        var throttled = ReadThrottleFlags();
        if (load <= 0 && temp <= 0 && string.IsNullOrWhiteSpace(throttled))
            return null;

        var latestDto = latest == null ? null : ToSample(latest, processorCount);
        return new SystemCpuSampleDto(
            DateTime.UtcNow,
            latestDto?.TrCpuPercent ?? 0,
            latestDto?.TrCpuHostPercent ?? 0,
            latestDto?.TrRssMb ?? 0,
            latestDto?.TrThreadCount ?? 0,
            temp > 0 ? temp : latestDto?.HostTempC ?? 0,
            load > 0 ? load : latestDto?.HostLoad1 ?? 0,
            load > 0 ? HostPercent(load * 100d, processorCount) : latestDto?.HostLoadHostPercent ?? 0,
            FirstNonEmpty(throttled, latestDto?.HostThrottledFlags));
    }

    private static double ReadLoadAverage()
    {
        try
        {
            var first = File.ReadAllText("/proc/loadavg").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return double.TryParse(first, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double ReadTemperatureC()
    {
        try
        {
            var raw = File.ReadAllText("/sys/class/thermal/thermal_zone0/temp").Trim();
            return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value / 1000d : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadThrottleFlags()
    {
        try
        {
            return File.Exists("/sys/devices/platform/soc/soc:firmware/get_throttled")
                ? File.ReadAllText("/sys/devices/platform/soc/soc:firmware/get_throttled").Trim()
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool HasCurrentThrottleFlag(string flags)
    {
        if (string.IsNullOrWhiteSpace(flags)) return false;
        if (flags.StartsWith("throttled=", StringComparison.OrdinalIgnoreCase))
            flags = flags["throttled=".Length..];
        return int.TryParse(flags.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out var bits)
            && (bits & 0xF) != 0;
    }

    private static bool HasHistoricalThrottleFlag(string flags)
    {
        if (string.IsNullOrWhiteSpace(flags)) return false;
        if (flags.StartsWith("throttled=", StringComparison.OrdinalIgnoreCase))
            flags = flags["throttled=".Length..];
        return int.TryParse(flags.Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out var bits)
            && bits != 0;
    }
}
