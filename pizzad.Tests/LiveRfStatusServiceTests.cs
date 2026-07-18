using System.Globalization;

namespace pizzad.Tests;

public sealed class LiveRfStatusServiceTests
{
    [Fact]
    public void BuildSnapshotSeparatesSitesAndUsesRecentDecodeAndRetuneWindows()
    {
        var now = new DateTime(2026, 7, 18, 16, 0, 0, DateTimeKind.Utc);
        var lines = new List<string>();
        for (var index = 0; index < 6; index++)
        {
            lines.Add(DecodeLine(now.AddSeconds(-10 - index * 10), "RAYMOND", 40));
            lines.Add(DecodeLine(now.AddSeconds(-12 - index * 10), "OOLTEWAH", 18));
        }
        lines.Add(RetuneLine(now.AddSeconds(-30), "OOLTEWAH"));

        var result = LiveRfStatusService.BuildSnapshot(now, string.Join('\n', lines));

        Assert.Equal(2, result.Sites.Count);
        var raymond = Assert.Single(result.Sites, site => site.SystemShortName == "RAYMOND");
        var ooltewah = Assert.Single(result.Sites, site => site.SystemShortName == "OOLTEWAH");
        Assert.Equal("ok", raymond.Tone);
        Assert.Equal(40, raymond.DecodeRate);
        Assert.Equal(0, raymond.Retunes);
        Assert.Equal("warning", ooltewah.Tone);
        Assert.Equal(18, ooltewah.DecodeRate);
        Assert.Equal(1, ooltewah.Retunes);
        Assert.Equal("warning", result.Tone);
    }

    [Fact]
    public void BuildSnapshotClassifiesDecodeLossAndElevatedRetunesAsCritical()
    {
        var now = new DateTime(2026, 7, 18, 16, 0, 0, DateTimeKind.Utc);
        var lines = Enumerable.Range(0, 6)
            .Select(index => DecodeLine(now.AddSeconds(-10 - index * 10), "RAYMOND", 0))
            .Concat(Enumerable.Range(0, 9).Select(index => RetuneLine(now.AddSeconds(-20 - index * 20), "RAYMOND")));

        var site = Assert.Single(LiveRfStatusService.BuildSnapshot(now, string.Join('\n', lines)).Sites);

        Assert.Equal("error", site.Tone);
        Assert.Equal("Critical", site.Status);
        Assert.Equal(100, site.ZeroDecodePercent);
        Assert.Equal(9, site.Retunes);
        Assert.Equal(108, site.RetunesPerHour);
    }

    [Fact]
    public void BuildSnapshotMarksSiteStaleWhenOnlyOlderDecodeSamplesRemain()
    {
        var now = new DateTime(2026, 7, 18, 16, 0, 0, DateTimeKind.Utc);
        var log = string.Join('\n',
            DecodeLine(now.AddSeconds(-180), "RAYMOND", 40),
            RetuneLine(now.AddSeconds(-20), "RAYMOND"));

        var site = Assert.Single(LiveRfStatusService.BuildSnapshot(now, log).Sites);

        Assert.Equal("stale", site.Tone);
        Assert.Equal(0, site.DecodeSamples);
        Assert.Equal(1, site.Retunes);
    }

    [Fact]
    public void DisplayScopeRejectsSourceAndNumericScopes()
    {
        Assert.Equal("RAYMOND", TrHealthCollector.ExtractDisplaySystemScope("[2026-07-18 12:00:00.000] (info) [RAYMOND] 769.1 MHz 40 msg/sec"));
        Assert.Equal(string.Empty, TrHealthCollector.ExtractDisplaySystemScope("[2026-07-18 12:00:00.000] (info) [source0] Retuning to Control Channel"));
        Assert.Equal(string.Empty, TrHealthCollector.ExtractDisplaySystemScope("[2026-07-18 12:00:00.000] (info) [1234] Retuning to Control Channel"));
    }

    [Fact]
    public void LiveDecodeParserUsesFrequentControlChannelRateRows()
    {
        const string line = "[2026-07-18 12:00:00.000] (info) [RAYMOND] freq: 773.781250 MHz Control Channel Message Decode Rate: 39/sec, count: 117";

        Assert.True(TrHealthCollector.TryParseLiveControlChannelDecodeRate(line, out var rate));
        Assert.Equal(39, rate);
    }

    private static string DecodeLine(DateTime utc, string site, double rate) =>
        $"[{LocalTimestamp(utc)}] (info) [{site}] 769.10625 MHz {rate.ToString("F1", CultureInfo.InvariantCulture)} msg/sec";

    private static string RetuneLine(DateTime utc, string site) =>
        $"[{LocalTimestamp(utc)}] (info) [{site}] Retuning to Control Channel 769.10625";

    private static string LocalTimestamp(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
