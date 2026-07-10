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

        var normalized = InvokeNormalize(values, []);

        Assert.Equal(2, normalized.Count);
        Assert.Collection(normalized,
            first =>
            {
                Assert.Equal(0, first.SourceIndex);
                Assert.Equal("a0", first.SourceSerial);
                Assert.Equal("14", first.Gain);
                Assert.Equal(1_100, first.MeasuredSignalOffsetHz);
                Assert.Null(first.ErrorHz);
            },
            second =>
            {
                Assert.Equal(1, second.SourceIndex);
                Assert.Equal("B1", second.SourceSerial);
                Assert.Equal("12", second.Gain);
                Assert.Equal(-500, second.MeasuredSignalOffsetHz);
                Assert.Null(second.ErrorHz);
            });
    }

    [Fact]
    public void NormalizeSources_DerivesConfiguredHardwareSerial()
    {
        var method = typeof(SiteSetupService).GetMethod("NormalizeSources", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SiteSetupService).FullName, "NormalizeSources");
        var sources = new[] { new RfSurveySourceDto(0, "airspy=637862DC2E3A19D7", "", "airspy", 773_000_000, 6_000_000, 3_600, "21") };

        var normalized = (IReadOnlyList<RfSurveySourceDto>)(method.Invoke(null, [sources])
            ?? throw new InvalidOperationException("NormalizeSources returned null."));

        Assert.Equal("637862DC2E3A19D7", normalized[0].Serial);
    }

    private static IReadOnlyList<SiteSetupRfSelection> InvokeNormalize(IEnumerable<SiteSetupRfSelection> values, IEnumerable<RfSurveySourceDto> sources)
    {
        var method = typeof(SiteSetupService).GetMethod("NormalizeRfSelections", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SiteSetupService).FullName, "NormalizeRfSelections");
        return (IReadOnlyList<SiteSetupRfSelection>)(method.Invoke(null, [values, sources])
            ?? throw new InvalidOperationException("NormalizeRfSelections returned null."));
    }
}
