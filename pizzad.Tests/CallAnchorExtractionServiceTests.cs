namespace pizzad.Tests;

public sealed class CallAnchorExtractionServiceTests
{
    [Fact]
    public void ExtractTranscriptAnchors_CapturesHighwayMileMarkerAsSingleStrongAnchor()
    {
        var anchors = CallAnchorExtractionService.ExtractTranscriptAnchors(
            207867,
            "Respond to I-75 northbound around mile marker 28 for a Honda Odyssey crash.");

        var anchor = Assert.Single(anchors, a => a.Kind == "highway_mile_marker");
        Assert.Equal("i-75|mm:28", anchor.Value);
        Assert.Equal("I-75 MM 28", anchor.DisplayText);
        Assert.True(anchor.Confidence >= 0.95);
    }

    [Fact]
    public void ExtractTranscriptAnchors_CapturesMarkerBeforeInterstateAndRoundedCompanion()
    {
        var anchors = CallAnchorExtractionService.ExtractTranscriptAnchors(
            595053,
            "Medical 16, respond to the 180.8 Interstate 24 eastbound for car accident with injuries. Patient is unconscious.");

        Assert.Contains(anchors, a => a.Kind == "highway_mile_marker" && a.Value == "i-24|mm:180.8");
        Assert.Contains(anchors, a => a.Kind == "highway_mile_marker" && a.Value == "i-24|mm:180");
    }

    [Fact]
    public void ExtractTranscriptAnchors_CapturesBareMarkerHighwayWithInterstateContextAndLandmark()
    {
        var anchors = CallAnchorExtractionService.ExtractTranscriptAnchors(
            595043,
            "And on near the 180, 24 eastbound, past revised, there was a white SUV over off the interstate and went into the grass near barn nursery.");

        Assert.Contains(anchors, a => a.Kind == "highway_mile_marker" && a.Value == "i-24|mm:180");
        Assert.Contains(anchors, a => a.Kind == "business_or_landmark" && a.Value == "barn nursery");
    }

    [Fact]
    public void ExtractTranscriptAnchors_CapturesAddressAndIntersection()
    {
        var anchors = CallAnchorExtractionService.ExtractTranscriptAnchors(
            1,
            "Engine responding to 7800 Causeway Road at Gold Point Circle for the e-bike crash.");

        Assert.Contains(anchors, a => a.Kind == "address" && a.Value == "7800 causeway rd");
        Assert.Contains(anchors, a => a.Kind == "intersection" && a.Value.Contains("causeway rd", StringComparison.OrdinalIgnoreCase) && a.Value.Contains("gold point cir", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractTranscriptAnchors_CapturesCommaSeparatedDirectionalAddress()
    {
        var anchors = CallAnchorExtractionService.ExtractTranscriptAnchors(
            801557,
            "Can you be in route 1720, Newcastle Drive, northeast? There's a vehicle there parking in the middle of the road.");

        Assert.Contains(anchors, a => a.Kind == "address" && a.Value == "1720 newcastle dr ne");
    }

    [Fact]
    public void ExtractTranscriptAnchors_RejectsCommandWordsAsStreetAddress()
    {
        var anchors = CallAnchorExtractionService.ExtractTranscriptAnchors(
            802453,
            "Engine 3, you are showing out 804, continue the street southeast, CPR in progress.");

        Assert.DoesNotContain(anchors, a => a.Kind == "address" && a.Value.Contains("continue", StringComparison.OrdinalIgnoreCase));
    }
}
