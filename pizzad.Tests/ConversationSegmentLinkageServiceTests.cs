namespace pizzad.Tests;

public sealed class ConversationSegmentLinkageServiceTests
{
    [Fact]
    public void BuildContext_LinksAdjacentCallsWithinTheSameSystem()
    {
        var calls = new[]
        {
            Call(1, "alpha", 100, 1000, 1010),
            Call(2, "alpha", 200, 1015, 1025),
            Call(3, "alpha", 300, 1100, 1110)
        };
        var participants = new[]
        {
            Participant(1, "alpha", 42, 1_000_000, 1_010_000, 2),
            Participant(2, "alpha", 42, 1_015_000, 1_025_000, 1),
            Participant(1, "alpha", 77, 1_001_000, 1_006_000, 1),
            Participant(2, "alpha", 77, 1_017_000, 1_022_000, 2),
            Participant(3, "alpha", 42, 1_100_000, 1_110_000, 1)
        };

        var context = ConversationSegmentLinkageService.BuildContext(calls, participants, 30);

        var link = Assert.Single(context.Links);
        Assert.Equal(1, link.EarlierCallId);
        Assert.Equal(2, link.LaterCallId);
        Assert.Equal(2, link.SharedRadioCount);
        Assert.Equal(5_000, link.GapMilliseconds);
        Assert.Equal(3, link.MostFrequentSharedRadioSegmentCount);
        Assert.False(link.SameTalkgroup);
        Assert.Equal(2, context.ParticipantsByCallId[1].IdentifiedRadioCount);
        Assert.Equal(3, context.ParticipantsByCallId[1].IdentifiedTransmissionCount);
    }

    [Fact]
    public void BuildContext_DoesNotLinkTheSameNumericRadioAcrossSystems()
    {
        var calls = new[]
        {
            Call(1, "alpha", 100, 1000, 1010),
            Call(2, "bravo", 100, 1011, 1020)
        };
        var participants = new[]
        {
            Participant(1, "alpha", 42, 1_000_000, 1_010_000, 1),
            Participant(2, "bravo", 42, 1_011_000, 1_020_000, 1)
        };

        var context = ConversationSegmentLinkageService.BuildContext(calls, participants, 30);

        Assert.Empty(context.Links);
    }

    [Fact]
    public void BuildContext_LinksOnlyConsecutiveAppearancesOfACommonRadio()
    {
        var calls = new[]
        {
            Call(1, "alpha", 100, 1000, 1010),
            Call(2, "alpha", 100, 1011, 1020),
            Call(3, "alpha", 100, 1021, 1030)
        };
        var participants = calls.Select(call => Participant(call.Id, "alpha", 42, call.StartTime * 1000, call.StopTime * 1000, 1)).ToList();

        var context = ConversationSegmentLinkageService.BuildContext(calls, participants, 30);

        Assert.Equal(2, context.Links.Count);
        Assert.DoesNotContain(context.Links, link => link.EarlierCallId == 1 && link.LaterCallId == 3);
    }

    [Fact]
    public void BuildContext_DoesNotHideAnInterveningCallOutsideTheCandidateSet()
    {
        var calls = new[]
        {
            Call(1, "alpha", 100, 1000, 1010),
            Call(3, "alpha", 100, 1021, 1030)
        };
        var participants = new[]
        {
            Participant(1, "alpha", 42, 1_000_000, 1_010_000, 1),
            Participant(2, "alpha", 42, 1_011_000, 1_020_000, 1),
            Participant(3, "alpha", 42, 1_021_000, 1_030_000, 1)
        };

        var context = ConversationSegmentLinkageService.BuildContext(calls, participants, 30);

        Assert.Empty(context.Links);
    }

    private static EngineCall Call(long id, string system, long talkgroup, long start, long stop) => new()
    {
        Id = id,
        SystemShortName = system,
        Talkgroup = talkgroup,
        StartTime = start,
        StopTime = stop
    };

    private static ConversationSegmentParticipantRecord Participant(long callId, string system, long sourceId, long startMs, long stopMs, int count) =>
        new(callId, system, sourceId, startMs, stopMs, count);
}
