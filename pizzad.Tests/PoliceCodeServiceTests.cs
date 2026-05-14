namespace pizzad.Tests;

public sealed class PoliceCodeServiceTests
{
    [Fact]
    public void Detect_NormalizesHyphenlessTenCodes()
    {
        var service = new PoliceCodeService();

        var rows = service.Detect("unit is 1049 at the intersection");

        var row = Assert.Single(rows);
        Assert.Equal("10-49", row.NormalizedCode);
        Assert.Equal("generic-unverified", row.Source);
        Assert.False(string.IsNullOrWhiteSpace(row.Meaning));
    }

    [Fact]
    public void Detect_FindsCodeAndSignalTokensWithoutInventingMeaning()
    {
        var service = new PoliceCodeService();

        var rows = service.Detect("show me code 16 and signal 7");

        Assert.Contains(rows, r => r.NormalizedCode == "code-16" && r.Source == "detected");
        Assert.Contains(rows, r => r.NormalizedCode == "signal-7" && r.Source == "detected");
    }
}
