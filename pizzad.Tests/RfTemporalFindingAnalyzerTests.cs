namespace pizzad.Tests;

public sealed class RfTemporalFindingAnalyzerTests
{
    [Fact]
    public void RepeatingSymptomsFormOnePatternEvenWhenTimesAndDecimalsVary()
    {
        var now = DateTime.UtcNow;
        var samples = new[]
        {
            Sample(now.Date.AddDays(-12).AddHours(1), 21.41, 5),
            Sample(now.Date.AddDays(-7).AddHours(9), 20.97, 6),
            Sample(now.Date.AddDays(-2).AddHours(17), 21.62, 5)
        };

        var finding = Assert.Single(RfTemporalFindingAnalyzer.Analyze(samples, [], now));

        Assert.Equal("control-instability", finding.Signature);
        Assert.Equal(3, finding.Episodes.Count);
        Assert.Contains("decode_degraded", finding.Conditions);
        Assert.Contains("retunes_elevated", finding.Conditions);
        Assert.Contains("No reliable time-of-day", finding.ScheduleSummary);
        Assert.False(finding.IsActive);
    }

    [Fact]
    public void MaintenanceWindowsAreRetainedAsContextButExcludedFromPatternDetection()
    {
        var now = DateTime.UtcNow;
        var samples = new[]
        {
            Sample(now.AddDays(-8), 20, 5),
            Sample(now.AddDays(-5), 20, 5),
            Sample(now.AddDays(-2), 20, 5)
        };
        var excluded = samples[1];
        var maintenance = new[]
        {
            new MaintenanceIntervalDto(1, excluded.WindowStartUtc.AddMinutes(-1), excluded.WindowEndUtc.AddMinutes(1), "deploy", "PizzaWave deployment", true, "{}", now)
        };

        Assert.Empty(RfTemporalFindingAnalyzer.Analyze(samples, maintenance, now));
    }

    [Fact]
    public void CurrentEpisodeCreatesAnActiveFindingWithoutWaitingForRecurrence()
    {
        var now = DateTime.UtcNow;
        var finding = Assert.Single(RfTemporalFindingAnalyzer.Analyze([Sample(now.AddMinutes(-5), 0.2, 0)], [], now));

        Assert.True(finding.IsActive);
        Assert.Equal("decode-loss", finding.Signature);
        Assert.Equal("provisional", finding.Confidence);
    }

    [Fact]
    public void WideningCollectorViewsDoNotDuplicateEpisodes()
    {
        var now = DateTime.UtcNow;
        var start = now.AddMinutes(-10);
        var samples = new[]
        {
            Sample(start, 20, 5),
            Sample(start, 20, 5) with { WindowEndUtc = start.AddMinutes(10) },
            Sample(start, 20, 5) with { WindowEndUtc = start.AddMinutes(15) },
            Sample(start.AddMinutes(5), 20, 5)
        };

        var finding = Assert.Single(RfTemporalFindingAnalyzer.Analyze(samples, [], now));

        Assert.Single(finding.Episodes);
        Assert.Equal(2, finding.Episodes[0].Evidence.Windows);
        Assert.Equal(start.AddMinutes(10), finding.Episodes[0].EndUtc);
    }

    [Fact]
    public void RelatedSymptomsSeparatedByOneHealthyBucketRemainOneEpisode()
    {
        var now = DateTime.UtcNow;
        var start = now.AddMinutes(-15);
        var decodeLoss = Sample(start, 0.2, 0);
        var healthy = Sample(start.AddMinutes(5), 40, 0);
        var retuneInstability = Sample(start.AddMinutes(10), 20, 5);

        var finding = Assert.Single(RfTemporalFindingAnalyzer.Analyze([decodeLoss, healthy, retuneInstability], [], now));

        var episode = Assert.Single(finding.Episodes);
        Assert.Equal("decode-loss", episode.Signature);
        Assert.Contains("decode_unavailable", episode.Conditions);
        Assert.Contains("retunes_elevated", episode.Conditions);
        Assert.Equal(2, episode.Evidence.Windows);
    }

    private static TrHealthSampleDto Sample(DateTime startUtc, double decodeRate, int retunes) => new()
    {
        WindowStartUtc = startUtc,
        WindowEndUtc = startUtc.AddMinutes(5),
        Scope = "site-a",
        CcSummaryDecodeLines = 1,
        CcSummaryDecodeRateTotal = decodeRate,
        Retunes = retunes,
        CallsConcluded = 8
    };
}
