using System.Diagnostics;
using System.Globalization;

namespace pizzad;

public sealed class SystemCpuSnapshotService
{
    private readonly EngineDatabase _database;
    private readonly EngineConfig _config;

    public SystemCpuSnapshotService(EngineDatabase database, EngineConfig config)
    {
        _database = database;
        _config = config;
    }

    public async Task<SystemCpuSnapshotDto> BuildAsync(CancellationToken ct)
    {
        const int windowMinutes = 120;
        var hostCpuTask = ReadHostCpuPercentAsync(ct);
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
        var peakCpu = samples.Select(s => s.TrCpuPercent).DefaultIfEmpty(0).Max();
        var peakLoad = samples.Select(s => s.HostLoad1).DefaultIfEmpty(0).Max();
        var peak = new SystemCpuSampleDto(
            null,
            peakCpu,
            HostPercent(peakCpu, processorCount),
            samples.Select(s => s.TrRssMb).DefaultIfEmpty(0).Max(),
            samples.Select(s => s.TrThreadCount).DefaultIfEmpty(0).Max(),
            samples.Select(s => s.HostTempC).DefaultIfEmpty(0).Max(),
            peakLoad,
            HostPercent(peakLoad * 100d, processorCount),
            samples.Select(s => s.HostThrottledFlags).FirstOrDefault(HasHistoricalThrottleFlag) ?? string.Empty);

        var latestDto = latest == null ? null : ToSample(latest, processorCount);
        var hostMemory = ReadHostMemory();
        var processesTask = ReadStackProcessesAsync(processorCount, ct);
        var usbTask = ReadUsbEvidenceAsync(ct);
        var hostCpuPercent = await hostCpuTask;
        var insights = BuildInsights(latestDto, peak, processorCount, hostMemory, hostCpuPercent);
        var actionableInsights = insights.Where(i => i.Label != "TR threads").ToList();
        var processes = await processesTask;
        var usb = await usbTask;
        var severity = actionableInsights.Any(i => i.Status == "error") || usb.Status == "error" ? "error" : actionableInsights.Any(i => i.Status == "warning") || usb.Status == "warning" ? "warning" : "ok";
        var summary = latestDto == null
            ? "No recent Trunk Recorder resource samples are available; current host, process, and passive USB evidence is shown below."
            : severity == "error"
                ? "Resource or thermal pressure needs operator attention."
                : severity == "warning"
                    ? "One or more resource or passive USB signals need review."
                    : "PizzaWave stack resources and passive USB evidence are within the current operating envelope.";

        return new SystemCpuSnapshotDto(now, windowMinutes, processorCount, latestDto, peak, severity, summary, insights, hostCpuPercent, hostMemory, processes, usb);
    }

    public async Task<SystemRuntimeResourceSampleDto> BuildLiveAsync(CancellationToken ct)
    {
        var generatedAtUtc = DateTime.UtcNow;
        var processorCount = Math.Max(1, Environment.ProcessorCount);
        var hostCpuTask = ReadHostCpuPercentAsync(ct);
        var processesTask = ReadStackProcessesAsync(processorCount, ct);
        var hostMemory = ReadHostMemory();
        await Task.WhenAll(hostCpuTask, processesTask);
        return new SystemRuntimeResourceSampleDto(generatedAtUtc, await hostCpuTask, hostMemory, await processesTask);
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

    private static IReadOnlyList<SystemCpuInsightDto> BuildInsights(SystemCpuSampleDto? latest, SystemCpuSampleDto peak, int processorCount, SystemHostMemoryDto memory, double? hostCpuPercent)
    {
        if (latest == null)
            return
            [
                new("Latest TR sample", "n/a", "warning", "No recent Trunk Recorder health resource sample was found."),
                HostCpuInsight(hostCpuPercent),
                MemoryInsight(memory)
            ];

        var currentThrottle = HasCurrentThrottleFlag(latest.HostThrottledFlags);
        var historicalThrottle = HasHistoricalThrottleFlag(peak.HostThrottledFlags);
        var cpuStatus = latest.TrCpuHostPercent >= 90 ? "error" : latest.TrCpuHostPercent >= 75 ? "warning" : "ok";
        var tempStatus = latest.HostTempC >= 80 || currentThrottle ? "error" : latest.HostTempC >= 70 ? "warning" : "ok";
        var loadStatus = latest.HostLoadHostPercent >= 150 ? "warning" : "ok";
        var rssStatus = latest.TrRssMb >= 2048 ? "warning" : "ok";
        var threadStatus = latest.TrThreadCount >= 250 ? "warning" : "ok";

        return
        [
            HostCpuInsight(hostCpuPercent),
            new("TR CPU", $"{latest.TrCpuPercent:F0}% ({latest.TrCpuHostPercent:F0}% of host)", cpuStatus, $"100% equals one saturated core. This host reports {processorCount:N0} processor(s), so full host saturation is about {processorCount * 100:N0}%."),
            new("Host load", $"{latest.HostLoad1:F2} ({latest.HostLoadHostPercent:F0}% of host)", loadStatus, "1-minute load average normalized by processor count. Sustained values over 100% mean runnable work is queued."),
            new("Temperature", $"{latest.HostTempC:F1} C current, {peak.HostTempC:F1} C peak", tempStatus, "70 C is a warning band for enclosed Raspberry Pi operation. 80 C or current throttling is actionable."),
            new("Throttle flags", string.IsNullOrWhiteSpace(latest.HostThrottledFlags) ? "none" : latest.HostThrottledFlags, currentThrottle ? "error" : historicalThrottle ? "warning" : "ok", "Raspberry Pi throttle flags are definitive thermal/power evidence when available."),
            MemoryInsight(memory),
            new("TR memory", $"{latest.TrRssMb:F0} MB RSS", rssStatus, "Resident memory used by trunk-recorder. This is actionable when it crowds system memory, not by itself."),
            new("TR threads", $"{latest.TrThreadCount:N0}", threadStatus, "High thread count means many recorder/DSP workers. Treat it as capture complexity, not as proof of thermal danger.")
        ];
    }

    private static SystemCpuInsightDto HostCpuInsight(double? hostCpuPercent)
    {
        if (hostCpuPercent == null)
            return new("Host CPU", "unavailable", "warning", "The host CPU summary could not be read from /proc/stat.");
        var status = hostCpuPercent >= 90 ? "error" : hostCpuPercent >= 75 ? "warning" : "ok";
        return new("Host CPU", $"{hostCpuPercent:F0}%", status, "Current total CPU use across all processors, measured passively from /proc/stat.");
    }

    private static SystemCpuInsightDto MemoryInsight(SystemHostMemoryDto memory)
    {
        if (memory.TotalMb <= 0)
            return new("Host memory", "unavailable", "warning", "The host memory summary could not be read from /proc/meminfo.");
        var availablePercent = 100d - memory.UsedPercent;
        var status = availablePercent <= 10 ? "error" : availablePercent <= 20 ? "warning" : "ok";
        return new("Host memory", $"{memory.UsedMb:N0} MB used ({memory.UsedPercent:F0}%), {memory.AvailableMb:N0} MB available", status, "Available memory includes reclaimable cache and is more useful than process virtual-memory size.");
    }

    private async Task<IReadOnlyList<SystemProcessResourceDto>> ReadStackProcessesAsync(int processorCount, CancellationToken ct)
    {
        var units = new[]
        {
            (Component: "PizzaWave", Unit: "pizzad.service", FallbackPid: Environment.ProcessId),
            (Component: "Trunk Recorder", Unit: UnitName(_config.TrunkRecorder.LogServiceName, "trunk-recorder"), FallbackPid: 0),
            (Component: "Local LM Studio service", Unit: "lmstudio.service", FallbackPid: 0),
            (Component: "Qdrant", Unit: UnitName(_config.Embeddings.QdrantServiceName, "qdrant"), FallbackPid: 0)
        };
        var unitsWithState = new List<(string Component, string Unit, int MainPid, bool Active, string Cgroup, CgroupResourceSample? First)>();
        foreach (var unit in units)
        {
            var pid = unit.FallbackPid;
            var stateResult = await CaptureAsync("systemctl", ["show", unit.Unit, "--property=ActiveState,MainPID,ControlGroup", "--no-page"], ct);
            var state = ParseKeyValueLines(stateResult.Stdout);
            if (int.TryParse(state.GetValueOrDefault("MainPID"), out var systemdPid) && systemdPid > 0)
                pid = systemdPid;
            var active = string.Equals(state.GetValueOrDefault("ActiveState"), "active", StringComparison.OrdinalIgnoreCase);
            var cgroup = state.GetValueOrDefault("ControlGroup") ?? string.Empty;
            var first = active ? ReadCgroupResourceSample(cgroup) : null;
            unitsWithState.Add((unit.Component, unit.Unit, pid, active, cgroup, first));
        }

        await Task.Delay(250, ct);
        var rows = new List<SystemProcessResourceDto>();
        foreach (var unit in unitsWithState)
        {
            if (!unit.Active)
            {
                rows.Add(new(unit.Component, unit.Unit, 0, string.Empty, 0, 0, 0, 0, "not-running"));
                continue;
            }
            var second = ReadCgroupResourceSample(unit.Cgroup);
            if (unit.First != null && second != null && second.CpuUsageUsec >= unit.First.CpuUsageUsec)
            {
                var elapsedSeconds = Math.Max(0.001, (second.Timestamp - unit.First.Timestamp) / (double)Stopwatch.Frequency);
                var cpuPercent = (second.CpuUsageUsec - unit.First.CpuUsageUsec) / 1_000_000d / elapsedSeconds * 100d;
                var process = second.ProcessCount == 1 ? ReadProcessName(second.Pids[0]) : $"{second.ProcessCount:N0} processes";
                rows.Add(new(unit.Component, unit.Unit, unit.MainPid, process, cpuPercent, NormalizedServiceCpuPercent(cpuPercent, processorCount), second.MemoryBytes / 1024d / 1024d, second.ProcessCount, "running"));
                continue;
            }
            if (unit.MainPid > 0)
            {
                var processResult = await CaptureAsync("ps", ["-p", unit.MainPid.ToString(CultureInfo.InvariantCulture), "-o", "pcpu=,rss=,comm="], ct);
                rows.Add(ParseProcessResource(unit.Component, unit.Unit, unit.MainPid, processResult.Stdout, processorCount));
                continue;
            }
            rows.Add(new(unit.Component, unit.Unit, 0, unit.Unit, 0, 0, 0, 0, "running"));
        }
        return rows;
    }

    internal static SystemProcessResourceDto ParseProcessResource(string component, string unit, int pid, string output, int processorCount)
    {
        var parts = output.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var cpu = parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedCpu) ? parsedCpu : 0;
        var rssKb = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRss) ? parsedRss : 0;
        var process = parts.Length > 2 ? string.Join(' ', parts.Skip(2)) : string.Empty;
        return new(component, unit, pid, process, cpu, NormalizedServiceCpuPercent(cpu, processorCount), rssKb / 1024d, string.IsNullOrWhiteSpace(process) ? 0 : 1, string.IsNullOrWhiteSpace(process) ? "unavailable" : "running");
    }

    private sealed record CgroupResourceSample(long CpuUsageUsec, long MemoryBytes, IReadOnlyList<int> Pids, long Timestamp)
    {
        public int ProcessCount => Pids.Count;
    }

    private static CgroupResourceSample? ReadCgroupResourceSample(string cgroup)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cgroup))
                return null;
            var path = Path.Combine("/sys/fs/cgroup", cgroup.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            var cpu = ParseKeyValueLines(File.ReadAllText(Path.Combine(path, "cpu.stat")));
            var pids = File.ReadAllLines(Path.Combine(path, "cgroup.procs"))
                .Select(value => int.TryParse(value.Trim(), out var pid) ? pid : 0)
                .Where(pid => pid > 0)
                .Distinct()
                .ToList();
            if (!long.TryParse(cpu.GetValueOrDefault("usage_usec"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpuUsageUsec))
                return null;
            var memoryBytes = pids.Sum(ReadProcessRssBytes);
            return new(cpuUsageUsec, memoryBytes, pids, Stopwatch.GetTimestamp());
        }
        catch
        {
            return null;
        }
    }

    private static long ReadProcessRssBytes(int pid)
    {
        try
        {
            return ParseProcStatusRssBytes(File.ReadAllText($"/proc/{pid}/status"));
        }
        catch
        {
            return 0;
        }
    }

    private static long ParseProcStatusRssBytes(string text)
    {
        var row = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("VmRSS:", StringComparison.OrdinalIgnoreCase));
        if (row == null)
            return 0;
        var token = row["VmRSS:".Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rssKb) ? rssKb * 1024 : 0;
    }

    private static Dictionary<string, string> ParseKeyValueLines(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(['=', ' '], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
                values[parts[0]] = parts[1];
        }
        return values;
    }

    private static string ReadProcessName(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/comm").Trim();
        }
        catch
        {
            return pid.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static SystemHostMemoryDto ReadHostMemory()
    {
        try
        {
            var values = File.ReadLines("/proc/meminfo")
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => ParseMemInfoKb(parts[1]), StringComparer.OrdinalIgnoreCase);
            var totalMb = values.GetValueOrDefault("MemTotal") / 1024;
            var availableMb = values.GetValueOrDefault("MemAvailable") / 1024;
            var usedMb = Math.Max(0, totalMb - availableMb);
            var usedPercent = totalMb > 0 ? usedMb * 100d / totalMb : 0;
            return new(totalMb, availableMb, usedMb, usedPercent);
        }
        catch
        {
            return new(0, 0, 0, 0);
        }
    }

    private static long ParseMemInfoKb(string value)
    {
        var token = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static async Task<double?> ReadHostCpuPercentAsync(CancellationToken ct)
    {
        try
        {
            var first = ParseHostCpuCounters(await File.ReadAllTextAsync("/proc/stat", ct));
            await Task.Delay(250, ct);
            var second = ParseHostCpuCounters(await File.ReadAllTextAsync("/proc/stat", ct));
            return CalculateHostCpuPercent(first, second);
        }
        catch
        {
            return null;
        }
    }

    private static (long Total, long Idle) ParseHostCpuCounters(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(row => row.StartsWith("cpu ", StringComparison.Ordinal));
        var values = line?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
            .ToArray() ?? [];
        if (values.Length < 4)
            return (0, 0);
        var total = values.Sum();
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return (total, idle);
    }

    private static double? CalculateHostCpuPercent((long Total, long Idle) first, (long Total, long Idle) second)
    {
        var totalDelta = second.Total - first.Total;
        var idleDelta = second.Idle - first.Idle;
        if (totalDelta <= 0 || idleDelta < 0)
            return null;
        return Math.Clamp((totalDelta - idleDelta) * 100d / totalDelta, 0, 100);
    }

    private static async Task<SystemUsbEvidenceDto> ReadUsbEvidenceAsync(CancellationToken ct)
    {
        var usb = await CaptureAsync("lsusb", [], ct);
        var dmesg = await CaptureAsync("dmesg", ["--time-format", "iso", "--level", "warn,err"], ct);
        var source = "dmesg --time-format iso --level warn,err (current kernel ring buffer)";
        var evidencePeriod = "since boot";
        var evidenceAlreadyRecent = false;
        if (dmesg.ExitCode != 0)
        {
            dmesg = await CaptureAsync("journalctl", ["-k", "--since", "24 hours ago", "--no-pager", "-p", "warning"], ct);
            source = "kernel journal fallback, last 24 hours (dmesg was unavailable)";
            evidencePeriod = "last 24 hours";
            evidenceAlreadyRecent = true;
        }
        var devices = usb.ExitCode == 0
            ? usb.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(100).ToList()
            : [];
        var errors = FilterUsbKernelLines(dmesg.Stdout).TakeLast(100).ToList();
        var currentIssues = SelectCurrentUsbIssues(errors, DateTimeOffset.UtcNow, evidenceAlreadyRecent);
        var status = currentIssues.Count > 0 ? "warning" : dmesg.ExitCode == 0 ? "ok" : "unavailable";
        var message = errors.Count > 0
            ? $"{errors.Count:N0} USB-related kernel event(s) {evidencePeriod}; {currentIssues.Count:N0} currently actionable."
            : dmesg.ExitCode == 0
                ? $"No USB-related warnings or errors were found {evidencePeriod}."
                : "Kernel USB evidence is unavailable to the PizzaWave service account.";
        return new(status, message, devices, errors, source, currentIssues.Count, evidencePeriod);
    }

    internal static IReadOnlyList<string> FilterUsbKernelLines(string output)
    {
        string[] markers = ["usb", "xhci", "dwc2", "libusb", "over-current", "overcurrent"];
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => markers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static IReadOnlyList<string> SelectCurrentUsbIssues(IReadOnlyList<string> lines, DateTimeOffset now, bool evidenceAlreadyRecent)
    {
        var recent = lines.Where(line => evidenceAlreadyRecent || IsWithinUsbCurrentWindow(line, now)).ToList();
        var claimConflicts = recent.Where(IsUsbClaimConflict).ToList();
        return recent.Where(line => IsDisruptiveUsbEvent(line) || (IsUsbClaimConflict(line) && claimConflicts.Count >= 3)).ToList();
    }

    private static bool IsWithinUsbCurrentWindow(string line, DateTimeOffset now)
    {
        var token = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
            return false;
        token = token.Replace(',', '.');
        return DateTimeOffset.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
            && timestamp >= now.AddHours(-24)
            && timestamp <= now.AddMinutes(5);
    }

    private static bool IsUsbClaimConflict(string line) =>
        line.Contains("interface", StringComparison.OrdinalIgnoreCase)
        && line.Contains("claimed", StringComparison.OrdinalIgnoreCase);

    private static bool IsDisruptiveUsbEvent(string line)
    {
        string[] markers =
        [
            "disconnect", "reset ", "transfer failed", "device descriptor", "unable to enumerate",
            "over-current", "overcurrent", "xhci_hcd", "dwc2", "error -", "can't set config",
            "cannot set config", "not accepting address", "host controller died"
        ];
        return markers.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static string UnitName(string? value, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return name.EndsWith(".service", StringComparison.OrdinalIgnoreCase) ? name : name + ".service";
    }

    private static async Task<(int ExitCode, string Stdout)> CaptureAsync(string fileName, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var start = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process == null) return (-1, string.Empty);
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, (await stdout) + (await stderr));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static double HostPercent(double cpuPercent, int processorCount) => cpuPercent / Math.Max(1, processorCount);

    private static double NormalizedServiceCpuPercent(double cpuPercent, int processorCount) =>
        Math.Clamp(HostPercent(cpuPercent, processorCount), 0, 100);

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
