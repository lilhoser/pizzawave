namespace pizzad.Tests;

public sealed class IncidentNarrativeGrounderTests
{
    [Fact]
    public void Ground_RewritesGenericMedicalAssistWhenTranscriptSupportsSpecificComplaint()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Medical Assist at 714 19th St NE",
            "Cleveland Fire responding to a medical assist for an allergic reaction at 714 19th Street Northeast.",
            "fire",
            [Call(668053, "I'm a window rescue woman, 714 19th Street, Northeast. It's going to be allergic reaction.")]);

        Assert.True(grounded.WasRewritten);
        Assert.Contains("Allergic reaction", grounded.Title);
        Assert.DoesNotContain("Medical Assist", grounded.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ground_RemovesUnsupportedChestPainFromCheckingCall()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Chest pain at 1400 God St NE Apt 62B",
            "EMS responding to chest pain and shortness of breath for a 61-year-old female.",
            "ems",
            [Call(649365, "I copy. I have showing you in route to 1400 God Street North apartment 62B for checking call.")]);

        Assert.True(grounded.WasRewritten);
        Assert.DoesNotContain("Chest pain", grounded.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shortness of breath", grounded.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ground_MarksGenericFallbackWhenNoSpecificEvidenceBacksUnsupportedTitle()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Machete assault at 1400 God St NE",
            "Police responding to a machete assault.",
            "police",
            [Call(649365, "I copy. I have showing you in route to 1400 God Street North apartment 62B for checking call.")]);

        Assert.True(grounded.WasRewritten);
        Assert.False(grounded.HasSpecificEvidence);
        Assert.StartsWith("Police call", grounded.Title);
    }

    [Fact]
    public void Ground_KeepsMvcTitleWhenTranscriptsUseAccidentAndInjurySynonyms()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "MVC with injuries at Macaulay Ave and Houston St",
            "Multi-unit response to an accident with injuries at the intersection of Macaulay Avenue and Houston Street.",
            "traffic",
            [
                Call(662709, "10-9, Adam 7, copy an accident with injuries. Muckray Avenue, Houston Avenue."),
                Call(662717, "Squad 1 responding to Macaulay Avenue at Houston Street for an accident with an injury.")
            ]);

        Assert.False(grounded.WasRewritten);
        Assert.Equal("MVC with injuries at Macaulay Ave and Houston St", grounded.Title);
    }

    [Fact]
    public void Ground_RewritesInjuryClaimWhenTranscriptSaysNoInjuries()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "MVC with injuries at Big Ridge and Fairview Rd",
            "Two vehicle accident with injuries at Big Ridge Road and Fairview Road.",
            "police",
            [Call(652989, "Copy an accident, negative injuries. Two vehicle accident involving a Ford Mustang and Ford Explorer.")]);

        Assert.True(grounded.WasRewritten);
        Assert.DoesNotContain("with injuries", grounded.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ground_UsesRoadwayHazardFallbackForVehicleParkedInRoad()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Traffic control on street",
            "Police responding to traffic control.",
            "police",
            [Call(801557, "Can you be in route 1720 Newcastle Drive northeast? There's a vehicle there parking in the middle of the road. It's also a hazard.")]);

        Assert.True(grounded.WasRewritten);
        Assert.True(grounded.HasSpecificEvidence);
        Assert.StartsWith("Roadway hazard", grounded.Title);
    }

    [Fact]
    public void Ground_UsesFixedObjectFallbackForVehicleVersusGuardrail()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Traffic incident",
            "THP responding to a traffic incident.",
            "traffic",
            [Call(801113, "I-24 east of the 163, partial 152.6. Now 45 is a tractor trailer tanker versus guardrail, blocking lane one.")]);

        Assert.True(grounded.WasRewritten);
        Assert.True(grounded.HasSpecificEvidence);
        Assert.StartsWith("Vehicle hit fixed object", grounded.Title);
    }

    [Fact]
    public void Ground_RewritesGenericTrafficControlTitleWhenDetailHasCrashContext()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Traffic control on street",
            "Units managing traffic for I-24 WB mile 57.2 tractor-trailer vs guardrail crash.",
            "police",
            [
                Call(801113, "I-24 east of the 163, partial 152.6. Now 45 is a tractor trailer tanker versus guardrail, blocking lane one."),
                Call(801519, "Can you see if it can have a health truck at this way to us with traffic? We're in a pretty bad curve.")
            ]);

        Assert.True(grounded.WasRewritten);
        Assert.True(grounded.HasSpecificEvidence);
        Assert.StartsWith("Vehicle hit fixed object", grounded.Title);
        Assert.DoesNotContain("Traffic control", grounded.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ground_UsesBurglaryFallbackForVehicleBurglary()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Police response at Dodson Ave",
            "Police responding to a service call.",
            "police",
            [Call(801565, "3115 Dodson Avenue in reference to a vehicle that was burglarized at Dollar General. They stole her blue and black walker.")]);

        Assert.True(grounded.WasRewritten);
        Assert.True(grounded.HasSpecificEvidence);
        Assert.StartsWith("Burglary", grounded.Title);
    }

    [Fact]
    public void Ground_UsesRoadwayHazardFallbackForTreeDownTrafficControl()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Traffic control on street",
            "Police responding to traffic control.",
            "police",
            [Call(801263, "Highway department requesting assistance with traffic control at Walnut and Daisy Dallas in reference to this tree down.")]);

        Assert.True(grounded.WasRewritten);
        Assert.True(grounded.HasSpecificEvidence);
        Assert.StartsWith("Roadway hazard", grounded.Title);
    }

    [Fact]
    public void Ground_RemovesCommandWordStreetLocationFromCprTitle()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "CPR in progress at 804 Continue St SE",
            "Fire dispatch reports CPR in progress for a 6-year-old female turning blue and not breathing.",
            "fire",
            [Call(802453, "Engine 3, you are showing out 804, continue the street southeast. CPR in progress, 6 year old female turning blue and not breathing.")]);

        Assert.True(grounded.WasRewritten);
        Assert.True(grounded.HasSpecificEvidence);
        Assert.DoesNotContain("Continue", grounded.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("804", grounded.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ground_KeepsUnconsciousTitleWhenTranscriptSaysNotMoving()
    {
        var grounded = IncidentNarrativeGrounder.Ground(
            "Unconscious patient at 336 Ringgold Rd",
            "Police and EMS responding to an unconscious person in a black Toyota at the pump.",
            "police",
            [Call(668445, "Caller by a black Toyota at the pump. Party with the door open, slumped over and not moving. Tried to wake up, but he did not react. Fire and EMS en route.")]);

        Assert.False(grounded.WasRewritten);
    }

    private static IncidentCandidateCall Call(long id, string transcript) =>
        new(id, 1000, transcript, "fire", "Dispatch", "test");
}
