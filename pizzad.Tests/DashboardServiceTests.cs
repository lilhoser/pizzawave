namespace pizzad.Tests;

public sealed class TranscriptLocationServiceTests
{
    [Fact]
    public void LocationHelpers_TolerateNullText()
    {
        Assert.Equal(string.Empty, TranscriptLocationService.NormalizeLocationKey(null));
        Assert.False(TranscriptLocationService.IsPlausibleLocation(null));
        Assert.Empty(TranscriptLocationService.ExtractLocations(null));
    }

    [Theory]
    [InlineData("Boulevard")]
    [InlineData("Highway")]
    [InlineData("East Highway")]
    [InlineData("S Dr")]
    public void LocationHelpers_RejectBareStreetTypes(string text)
    {
        Assert.False(TranscriptLocationService.IsPlausibleLocation(text));
        Assert.DoesNotContain(text, TranscriptLocationService.ExtractLocations(text), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Main Boulevard")]
    [InlineData("7 Cherokee Boulevard")]
    [InlineData("East Martin Luther King Boulevard")]
    [InlineData("Highway 58")]
    public void LocationHelpers_KeepSpecificLocations(string text)
    {
        Assert.True(TranscriptLocationService.IsPlausibleLocation(text));
        Assert.Contains(text, TranscriptLocationService.ExtractLocations(text), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocationExtractor_CapturesInterstateMarkerAndLandmark()
    {
        var locations = TranscriptLocationService.ExtractLocations(
            "Medical 16, respond to the 180.8 Interstate 24 eastbound near Barn Nursery for a car accident with injuries.").ToArray();

        Assert.Contains("I-24 MM 180.8", locations);
        Assert.Contains("Barn Nursery", locations);
    }

    [Fact]
    public void LocationExtractor_CapturesNumberedHighwayStreetAddress()
    {
        var locations = TranscriptLocationService.ExtractLocations(
            "Medic 14 responding to 4510 Highway 58 for a diabetic emergency.").ToArray();

        Assert.Contains("4510 Highway 58", locations);
    }
}
