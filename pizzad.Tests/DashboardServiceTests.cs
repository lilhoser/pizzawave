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
}
