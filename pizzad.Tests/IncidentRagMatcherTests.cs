namespace pizzad.Tests;

public sealed class IncidentRagMatcherTests
{
    [Fact]
    public void SelectCandidates_BoostsGeolocatedMatches()
    {
        var matcher = new IncidentRagMatcher();
        var calls = new[]
        {
            Call(1, 1000, "Engine responding to a crash at Mahan Gap Road."),
            Call(2, 1040, "County unit still blocking traffic at Mahan Gap."),
            Call(3, 1060, "Routine status check at the jail.")
        };
        var incident = new IncidentDto
        {
            IncidentKey = "llm:test:1:crash",
            Title = "Crash at Mahan Gap Road",
            Detail = "Units are responding to a crash at Mahan Gap Road.",
            LastSeen = 1000,
            Calls = [new IncidentCallDto(1, 1000, calls[0].Transcription, "/audio", "fire", "Fire Dispatch", "test")]
        };
        var locations = new[]
        {
            Location(1, "mahan gap road"),
            Location(2, "mahan gap road")
        };

        var result = matcher.SelectCandidates("test", [incident], calls, [2, 3], locations);

        Assert.Contains(result, row => row.Call.Id == 2 && row.GeoScore > 0);
        Assert.DoesNotContain(result, row => row.Call.Id == 1);
    }

    [Fact]
    public void SelectCandidates_DoesNotTrustProviderNoneLocation()
    {
        var matcher = new IncidentRagMatcher();
        var calls = new[] { Call(10, 2000, "Police checking 000 Highway Southwest.") };
        var locations = new[]
        {
            Location(10, "000 hwy sw", provider: "none", precision: "none", confidence: 0, latitude: 0, longitude: 0)
        };

        var result = matcher.SelectCandidates("test", [], calls, [10], locations);

        var row = Assert.Single(result);
        Assert.Equal("000 hwy sw", row.LocationLabel);
        Assert.False(row.LocationIsHighConfidenceGeocode);
        Assert.Equal("none", row.LocationGeocodeProvider);
        Assert.Equal("none", row.LocationGeocodePrecision);
    }

    [Fact]
    public void SelectCandidates_DoesNotTrustGeocodeWithoutCoordinates()
    {
        var matcher = new IncidentRagMatcher();
        var calls = new[] { Call(11, 2000, "Police checking 100 Main Street.") };
        var locations = new[]
        {
            Location(11, "100 main st", provider: "cache", precision: "address", confidence: 0.92, latitude: 0, longitude: 0)
        };

        var result = matcher.SelectCandidates("test", [], calls, [11], locations);

        var row = Assert.Single(result);
        Assert.False(row.LocationIsHighConfidenceGeocode);
    }

    [Fact]
    public void SelectCandidates_DoesNotTrustUnsupportedGeocodePrecision()
    {
        var matcher = new IncidentRagMatcher();
        var calls = new[] { Call(12, 2000, "Police checking near Cleveland.") };
        var locations = new[]
        {
            Location(12, "cleveland", provider: "cache", precision: "city", confidence: 0.95, latitude: 35.1595, longitude: -84.8766)
        };

        var result = matcher.SelectCandidates("test", [], calls, [12], locations);

        var row = Assert.Single(result);
        Assert.False(row.LocationIsHighConfidenceGeocode);
    }

    [Fact]
    public void SelectCandidates_TrustsSupportedGeocodeWithCoordinates()
    {
        var matcher = new IncidentRagMatcher();
        var calls = new[] { Call(13, 2000, "Police checking near Mahan Gap Road.") };
        var locations = new[]
        {
            Location(13, "mahan gap road", provider: "cache", precision: "road", confidence: 0.9, latitude: 35.0, longitude: -85.0)
        };

        var result = matcher.SelectCandidates("test", [], calls, [13], locations);

        var row = Assert.Single(result);
        Assert.True(row.LocationIsHighConfidenceGeocode);
        Assert.Equal(35.0, row.LocationLatitude);
        Assert.Equal(-85.0, row.LocationLongitude);
    }

    [Fact]
    public void SelectCandidates_IncludesNewSeedCallsEvenWithoutMatches()
    {
        var matcher = new IncidentRagMatcher();
        var calls = new[] { Call(10, 2000, "Tree down blocking the road near station 4.") };

        var result = matcher.SelectCandidates("test", [], calls, [10], []);

        var row = Assert.Single(result);
        Assert.Equal(10, row.Call.Id);
        Assert.Equal("new call seed", row.Reason);
    }

    private static EngineCall Call(long id, long start, string text) => new()
    {
        Id = id,
        StartTime = start,
        StopTime = start + 10,
        SystemShortName = "test",
        Talkgroup = 100,
        TalkgroupName = "Fire Dispatch",
        Category = "fire",
        Transcription = text,
        TranscriptionStatus = "complete",
        QualityReason = "ok"
    };

    private static CallLocationDashboardRow Location(
        long callId,
        string key,
        string provider = "cache",
        string precision = "road",
        double confidence = 0.9,
        double latitude = 35.0,
        double longitude = -85.0) => new(
        callId,
        0,
        "test",
        100,
        "Fire Dispatch",
        "fire",
        "",
        "test",
        "Test",
        "test",
        key,
        key,
        "transcript",
        key,
        key,
        provider,
        precision,
        confidence,
        latitude,
        longitude);
}
