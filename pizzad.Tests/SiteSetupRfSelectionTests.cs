namespace pizzad.Tests;

using System.Reflection;

public sealed class SiteSetupRfSelectionTests
{
    [Fact]
    public void NormalizeRfSelections_KeepsMeasurementsBoundToDifferentSources()
    {
        var values = new[]
        {
            new SiteSetupRfSelection { FrequencyHz = 773_000_000, SourceIndex = 0, SourceSerial = "A0", Gain = "15", SampleRateHz = 6_000_000, ErrorHz = 1_250 },
            new SiteSetupRfSelection { FrequencyHz = 773_000_000, SourceIndex = 1, SourceSerial = "B1", Gain = "12", SampleRateHz = 3_000_000, ErrorHz = -500 },
            new SiteSetupRfSelection { FrequencyHz = 773_000_000, SourceIndex = 0, SourceSerial = "a0", Gain = "14", SampleRateHz = 6_000_000, ErrorHz = 1_100 }
        };

        var normalized = InvokeNormalize(values);

        Assert.Equal(2, normalized.Count);
        Assert.Collection(normalized,
            first =>
            {
                Assert.Equal(0, first.SourceIndex);
                Assert.Equal("a0", first.SourceSerial);
                Assert.Equal("14", first.Gain);
                Assert.Equal(1_100, first.ErrorHz);
            },
            second =>
            {
                Assert.Equal(1, second.SourceIndex);
                Assert.Equal("B1", second.SourceSerial);
                Assert.Equal("12", second.Gain);
                Assert.Equal(-500, second.ErrorHz);
            });
    }

    private static IReadOnlyList<SiteSetupRfSelection> InvokeNormalize(IEnumerable<SiteSetupRfSelection> values)
    {
        var method = typeof(SiteSetupService).GetMethod("NormalizeRfSelections", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SiteSetupService).FullName, "NormalizeRfSelections");
        return (IReadOnlyList<SiteSetupRfSelection>)(method.Invoke(null, [values])
            ?? throw new InvalidOperationException("NormalizeRfSelections returned null."));
    }
}
