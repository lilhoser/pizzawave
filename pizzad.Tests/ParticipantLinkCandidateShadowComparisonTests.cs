namespace pizzad.Tests;

public sealed class ParticipantLinkCandidateShadowComparisonTests
{
    [Fact]
    public void SelectEligibleLinks_RequiresSameTalkgroupAndShortGap()
    {
        var eligible = Link(1, 2, 60_000, nearbySegments: 9, sameTalkgroup: true);
        var frequentRadio = Link(5, 6, 1_000, nearbySegments: 10, sameTalkgroup: true);
        var links = new[]
        {
            eligible,
            Link(3, 4, 60_001, nearbySegments: 9, sameTalkgroup: true),
            frequentRadio,
            Link(7, 8, 1_000, nearbySegments: 2, sameTalkgroup: false)
        };

        var selected = ParticipantLinkCandidateShadowComparer.SelectEligibleLinks(links);

        Assert.Equal([eligible, frequentRadio], selected);
    }

    [Fact]
    public void SelectEligibleLinks_DoesNotMutateTheObservedEvidence()
    {
        var links = new[]
        {
            Link(1, 2, 10_000, nearbySegments: 2, sameTalkgroup: true),
            Link(3, 4, 90_000, nearbySegments: 2, sameTalkgroup: true)
        };

        var selected = ParticipantLinkCandidateShadowComparer.SelectEligibleLinks(links);

        Assert.Single(selected);
        Assert.Equal(2, links.Length);
    }

    [Theory]
    [InlineData(true, 60_000, ParticipantLinkCandidateShadowGroups.SameTalkgroupUpTo60Seconds)]
    [InlineData(true, 60_001, ParticipantLinkCandidateShadowGroups.SameTalkgroup61To120Seconds)]
    [InlineData(true, 120_000, ParticipantLinkCandidateShadowGroups.SameTalkgroup61To120Seconds)]
    [InlineData(true, 120_001, ParticipantLinkCandidateShadowGroups.SameTalkgroupOver120Seconds)]
    [InlineData(false, 1_000, ParticipantLinkCandidateShadowGroups.DifferentTalkgroup)]
    public void Classify_UsesAuditableTalkgroupAndGapGroups(bool sameTalkgroup, long gapMilliseconds, string expected)
    {
        var candidate = Candidate(1, participantLinked: true, sameTalkgroup, gapMilliseconds, nearbySegments: 2);

        Assert.Equal(expected, ParticipantLinkCandidateShadowComparer.Classify(candidate));
    }

    [Fact]
    public void Compare_SeparatesAddedAugmentedAndDisplacedCandidates()
    {
        var baseline = new[]
        {
            Candidate(1),
            Candidate(2)
        };
        var participant = new[]
        {
            Candidate(1, participantLinked: true, sameTalkgroup: true, gapMilliseconds: 7_000, nearbySegments: 2),
            Candidate(3, participantLinked: true, sameTalkgroup: false, gapMilliseconds: 15_000, nearbySegments: 12)
        };

        var comparison = ParticipantLinkCandidateShadowComparer.Compare(baseline, participant);

        var added = Assert.Single(comparison.AddedCandidates);
        Assert.Equal(3, added.CallId);
        Assert.Equal(ParticipantLinkCandidateShadowGroups.DifferentTalkgroup, added.Group);
        Assert.True(added.InvolvesFrequentlyObservedRadio);

        var augmented = Assert.Single(comparison.ExistingCandidatesWithRadioEvidence);
        Assert.Equal(1, augmented.CallId);
        Assert.Equal(ParticipantLinkCandidateShadowGroups.SameTalkgroupUpTo60Seconds, augmented.Group);
        Assert.False(augmented.InvolvesFrequentlyObservedRadio);

        Assert.Equal([2L], comparison.DisplacedBaselineCallIds);
        Assert.True(comparison.HasDifference);
    }

    [Fact]
    public void Compare_ReportsNoDifferenceWhenRadioEvidenceChangesNothing()
    {
        var candidates = new[] { Candidate(1) };

        var comparison = ParticipantLinkCandidateShadowComparer.Compare(candidates, candidates);

        Assert.False(comparison.HasDifference);
        Assert.Empty(comparison.AddedCandidates);
        Assert.Empty(comparison.ExistingCandidatesWithRadioEvidence);
        Assert.Empty(comparison.DisplacedBaselineCallIds);
    }

    [Fact]
    public void SelectProductionCandidates_KeepsBaselineWhenLiveUseIsDisabled()
    {
        var baseline = new[] { Candidate(1) };
        var participant = new[] { Candidate(2, participantLinked: true) };

        var selected = ParticipantLinkCandidateShadowComparer.SelectProductionCandidates(
            baseline,
            participant,
            participantLinkCandidateEnabled: false);

        Assert.Same(baseline, selected);
    }

    [Fact]
    public void SelectProductionCandidates_UsesParticipantSetOnlyWhenExplicitlyEnabled()
    {
        var baseline = new[] { Candidate(1) };
        var participant = new[] { Candidate(2, participantLinked: true) };

        var selected = ParticipantLinkCandidateShadowComparer.SelectProductionCandidates(
            baseline,
            participant,
            participantLinkCandidateEnabled: true);

        Assert.Same(participant, selected);
    }

    private static IncidentRagCandidate Candidate(
        long callId,
        bool participantLinked = false,
        bool sameTalkgroup = false,
        long gapMilliseconds = 0,
        int nearbySegments = 0) =>
        new(
            new EngineCall { Id = callId },
            0.15,
            0,
            0,
            0,
            0.15,
            "test",
            "",
            ParticipantLinked: participantLinked,
            SharedRadioCount: participantLinked ? 1 : 0,
            ParticipantGapMilliseconds: gapMilliseconds,
            SharedRadioNearbySegmentCount: nearbySegments,
            ParticipantSameTalkgroup: sameTalkgroup);

    private static ConversationSegmentLinkEvidence Link(
        long earlierCallId,
        long laterCallId,
        long gapMilliseconds,
        int nearbySegments,
        bool sameTalkgroup) =>
        new(
            earlierCallId,
            laterCallId,
            1,
            gapMilliseconds,
            nearbySegments,
            sameTalkgroup);
}
