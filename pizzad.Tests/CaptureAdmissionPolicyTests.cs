namespace pizzad.Tests;

public sealed class CaptureAdmissionPolicyTests
{
    [Fact]
    public void Decide_SuppressesUnattachedSubSecondLateEntry()
    {
        var decision = CaptureAdmissionPolicy.Decide(Metadata("update", 7999, ["possibly_incomplete"]), null);

        Assert.Equal("suppressed_incomplete_fragment", decision.Disposition);
        Assert.False(decision.PersistAudio);
        Assert.False(decision.Transcribe);
        Assert.False(decision.CanSeedIncident);
    }

    [Fact]
    public void Decide_AttachesStrictlyMatchedSubSecondLateEntryWithoutTranscribingIt()
    {
        var decision = CaptureAdmissionPolicy.Decide(Metadata("update", 4000, ["possibly_incomplete"]), 42);

        Assert.Equal("attached_incomplete_fragment", decision.Disposition);
        Assert.True(decision.PersistAudio);
        Assert.False(decision.Transcribe);
        Assert.Equal(42, decision.ContinuationOfCallId);
    }

    [Fact]
    public void Decide_RetainsLaterObservedTransmissionsButDoesNotAllowLateEntryCallToSeed()
    {
        var decision = CaptureAdmissionPolicy.Decide(
            Metadata("update", 4000, ["possibly_incomplete", "observed_boundary"]), null);

        Assert.Equal("late_entry_with_retained_evidence", decision.Disposition);
        Assert.True(decision.PersistAudio);
        Assert.True(decision.Transcribe);
        Assert.False(decision.CanSeedIncident);
    }

    [Fact]
    public void Decide_AllowsObservedGrantStartToSeed()
    {
        var decision = CaptureAdmissionPolicy.Decide(Metadata("grant", 4000, ["observed_boundary"]), null);

        Assert.Equal("complete_assignment_start", decision.Disposition);
        Assert.True(decision.CanSeedIncident);
    }

    [Fact]
    public void CanCreateIncident_RequiresAtLeastOneObservedAssignmentStart()
    {
        Assert.False(CaptureAdmissionPolicy.CanCreateIncident(
        [
            new EngineCall { Id = 1, CanSeedIncident = false },
            new EngineCall { Id = 2, CanSeedIncident = false }
        ]));
        Assert.True(CaptureAdmissionPolicy.CanCreateIncident(
        [
            new EngineCall { Id = 1, CanSeedIncident = false },
            new EngineCall { Id = 2, CanSeedIncident = true }
        ]));
    }

    private static CallstreamMetadata Metadata(string start, int firstSamples, IReadOnlyList<string> statuses) =>
        new(3, 10, 15, 10000, 15000, "ham", 99, 123, 1, 851.1, 8000, "exact_live", [],
            statuses.Select((status, index) => new CallstreamTransmission(
                index + 1, "unknown", 123, 10000 + index * 1000, 10500 + index * 1000,
                index == 0 ? 0 : firstSamples, index == 0 ? firstSamples : 4000, 851.1, 0, 0, 0, status)).ToList(),
            start, start == "grant", start == "update" ? 10000 : null);
}
