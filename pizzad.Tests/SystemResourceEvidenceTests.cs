using System.Reflection;

namespace pizzad.Tests;

public sealed class SystemResourceEvidenceTests
{
    [Fact]
    public void UsbKernelFilterKeepsUsbControllerAndTransferEvidenceOnly()
    {
        var result = Invoke<IReadOnlyList<string>>("FilterUsbKernelLines",
            "2026-07-14 usb 1-1: reset high-speed USB device\n" +
            "2026-07-14 eth0: link down\n" +
            "2026-07-14 xhci_hcd 0000:01:00.0: WARN Event TRB\n" +
            "2026-07-14 libusb transfer failed");

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, line => line.Contains("eth0", StringComparison.Ordinal));
    }

    [Fact]
    public void ProcessReadoutParsesCpuRssAndCommand()
    {
        var row = Invoke<SystemProcessResourceDto>("ParseProcessResource", "Trunk Recorder", "trunk-recorder.service", 42, " 87.5 524288 trunk-recorder\n", 4);

        Assert.Equal("Trunk Recorder", row.Component);
        Assert.Equal(42, row.Pid);
        Assert.Equal(87.5, row.CpuPercent);
        Assert.Equal(21.875, row.HostCpuPercent);
        Assert.Equal(512, row.RssMb);
        Assert.Equal(1, row.ProcessCount);
        Assert.Equal("running", row.Status);
    }

    [Fact]
    public void ServiceCpuNormalizationUsesWholeHostCapacityAndCapsAtOneHundred()
    {
        Assert.Equal(41d, Invoke<double>("NormalizedServiceCpuPercent", 164d, 4));
        Assert.Equal(100d, Invoke<double>("NormalizedServiceCpuPercent", 500d, 4));
    }

    [Fact]
    public void HostCpuReadoutUsesTotalAndIdleCounterDeltas()
    {
        var first = Invoke<(long Total, long Idle)>("ParseHostCpuCounters", "cpu  100 0 50 850 0 0 0 0 0 0\ncpu0 50 0 25 425");
        var second = Invoke<(long Total, long Idle)>("ParseHostCpuCounters", "cpu  130 0 70 900 0 0 0 0 0 0\ncpu0 65 0 35 450");

        Assert.Equal((1000L, 850L), first);
        Assert.Equal((1100L, 900L), second);
        Assert.Equal(50d, Invoke<double>("CalculateHostCpuPercent", first, second));
    }

    [Fact]
    public void SystemdAndCgroupKeyValuesUseTheSameStructuredParser()
    {
        var values = Invoke<Dictionary<string, string>>("ParseKeyValueLines", "ActiveState=active\nControlGroup=/system.slice/lmstudio.service\nusage_usec 12345\n");

        Assert.Equal("active", values["ActiveState"]);
        Assert.Equal("/system.slice/lmstudio.service", values["ControlGroup"]);
        Assert.Equal("12345", values["usage_usec"]);
    }

    [Fact]
    public void CgroupProcessMemoryUsesResidentMemoryFromProcStatus()
    {
        var bytes = Invoke<long>("ParseProcStatusRssBytes", "Name:\tllmster\nVmSize:\t999999 kB\nVmRSS:\t123456 kB\nThreads:\t20\n");

        Assert.Equal(123456L * 1024, bytes);
    }

    [Fact]
    public void UsbCurrentHealthDoesNotPromoteAnOldSingleClaimConflict()
    {
        var now = new DateTimeOffset(2026, 7, 14, 13, 0, 0, TimeSpan.FromHours(-4));
        IReadOnlyList<string> lines =
        [
            "2026-07-11T17:11:16,529787-04:00 usb 3-1: usbfs: interface 0 claimed by usbfs while 'trunk-recorder' sets config #1"
        ];

        var issues = Invoke<IReadOnlyList<string>>("SelectCurrentUsbIssues", lines, now, false);

        Assert.Empty(issues);
    }

    [Fact]
    public void UsbCurrentHealthPromotesRecentDisruptiveEventsAndRepeatedClaims()
    {
        var now = new DateTimeOffset(2026, 7, 14, 13, 0, 0, TimeSpan.FromHours(-4));
        IReadOnlyList<string> lines =
        [
            "2026-07-14T12:30:00-04:00 usb 3-1: USB disconnect, device number 2",
            "2026-07-14T12:31:00-04:00 usb 3-1: interface 0 claimed by usbfs",
            "2026-07-14T12:32:00-04:00 usb 3-1: interface 0 claimed by usbfs",
            "2026-07-14T12:33:00-04:00 usb 3-1: interface 0 claimed by usbfs"
        ];

        var issues = Invoke<IReadOnlyList<string>>("SelectCurrentUsbIssues", lines, now, false);

        Assert.Equal(4, issues.Count);
    }

    private static T Invoke<T>(string name, params object[] args)
    {
        var method = typeof(SystemCpuSnapshotService).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SystemCpuSnapshotService).FullName, name);
        return (T)(method.Invoke(null, args) ?? throw new InvalidOperationException($"{name} returned null."));
    }
}
