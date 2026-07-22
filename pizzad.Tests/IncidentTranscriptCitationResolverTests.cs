namespace pizzad.Tests;

public sealed class IncidentTranscriptCitationResolverTests
{
    [Fact]
    public void Resolve_ReturnsLiteralSourceSubstringForTypographicApostrophe()
    {
        const string transcript = "Engine 1, I'll show you responding. It's at Kinsor Road.";

        var resolved = IncidentTranscriptCitationResolver.Resolve(
            transcript,
            "Engine 1, I’ll show you responding. It’s at Kinsor Road.");

        Assert.Equal(transcript, resolved);
    }

    [Fact]
    public void Resolve_ReturnsLiteralSourceSubstringForTypographicDashAndQuotes()
    {
        const string transcript = "Caller said \"four-door Ford Ranger\" near the lot.";

        var resolved = IncidentTranscriptCitationResolver.Resolve(
            transcript,
            "Caller said “four—door Ford Ranger”");

        Assert.Equal("Caller said \"four-door Ford Ranger\"", resolved);
    }

    [Fact]
    public void Resolve_DoesNotRepairSemanticOrWhitespaceChanges()
    {
        const string transcript = "A 90-year-old female is having difficulty breathing.";
        const string proposed = "A 91-year-old female is having difficulty breathing.";

        Assert.Equal(proposed, IncidentTranscriptCitationResolver.Resolve(transcript, proposed));
        Assert.Equal("A 90 year old female", IncidentTranscriptCitationResolver.Resolve(transcript, "A 90 year old female"));
    }

    [Fact]
    public void ResolveSegments_SeparatesOrderedLiteralPassagesJoinedByEllipsis()
    {
        const string transcript = "2321 Lifestyle Way at the Embassy Suisse, radio traffic omitted here. There's going to be a party in the Crown Victoria.";

        var resolved = IncidentTranscriptCitationResolver.ResolveSegments(
            transcript,
            "2321 Lifestyle Way at the Embassy Suisse... There’s going to be a party in the Crown Victoria.");

        Assert.Equal(
            ["2321 Lifestyle Way at the Embassy Suisse", "There's going to be a party in the Crown Victoria."],
            resolved);
    }

    [Fact]
    public void ResolveSegments_FailsClosedWhenAnyOmittedSpanSegmentIsNotInSource()
    {
        const string transcript = "2321 Lifestyle Way at the Embassy Suisse. The vehicle was empty.";
        const string proposed = "2321 Lifestyle Way at the Embassy Suisse... A patient was in the vehicle.";

        Assert.Equal([proposed], IncidentTranscriptCitationResolver.ResolveSegments(transcript, proposed));
    }
}
