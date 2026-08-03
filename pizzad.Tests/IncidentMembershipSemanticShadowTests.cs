namespace pizzad.Tests;

public sealed class IncidentMembershipSemanticShadowTests
{
    [Fact]
    public void PackageBuilderKeepsFiveBaselineCallsAndOneRadioAddedCall()
    {
        var baseline = Enumerable.Range(1, 6).Select(number => Candidate(number, number * 10)).ToList();
        var participant = baseline.Concat([Candidate(20, 33, participantLinked: true)]).ToList();
        var comparison = Comparison(20);

        var package = IncidentMembershipSemanticShadowPackageBuilder.Build(
            "OT", new HashSet<long> { 3 }, baseline, participant, comparison, 5, 1);

        Assert.NotNull(package);
        Assert.Equal(5, package.BaselineCalls.Count);
        Assert.Equal(6, package.ParticipantCalls.Count);
        Assert.Contains(package.BaselineCalls, call => call.Id == 3);
        Assert.Equal(20, Assert.Single(package.AddedCallIds));
        Assert.Contains(package.ParticipantCalls, call => call.Id == 20);
    }

    [Fact]
    public void PackageBuilderReturnsNothingWithoutUsableAddedEvidence()
    {
        var baseline = new[] { Candidate(1, 10) };
        var participant = baseline.Concat([Candidate(2, 12, participantLinked: true, transcript: "")]).ToList();

        var package = IncidentMembershipSemanticShadowPackageBuilder.Build(
            "OT", new HashSet<long> { 1 }, baseline, participant, Comparison(2), 5, 1);

        Assert.Null(package);
    }

    [Fact]
    public void ComparisonReportsAddedMembershipWithoutCallingEventExpansionAChangedBaseline()
    {
        var baseline = Result(events: [[1, 2]], unresolved: [3]);
        var participant = Result(events: [[1, 2, 9]], unresolved: [3]);

        var comparison = IncidentMembershipSemanticShadowComparer.Compare(baseline, participant, [9]);

        Assert.Equal([9L], comparison.AddedMemberCallIds);
        Assert.Empty(comparison.AddedUnresolvedCallIds);
        Assert.Empty(comparison.AddedNonIncidentCallIds);
        Assert.Empty(comparison.SharedCallsWhoseDispositionChanged);
    }

    [Fact]
    public void ComparisonDetectsChangedGroupingAmongSharedCalls()
    {
        var baseline = Result(events: [[1, 2]], unresolved: [3]);
        var participant = Result(events: [[1, 9], [2]], unresolved: [3]);

        var comparison = IncidentMembershipSemanticShadowComparer.Compare(baseline, participant, [9]);

        Assert.Equal([1L, 2L], comparison.SharedCallsWhoseDispositionChanged);
    }

    [Fact]
    public void ComparisonSeparatesUnresolvedAndNonIncidentAddedEvidence()
    {
        var baseline = Result(events: [[1]]);
        var participant = Result(events: [[1]], unresolved: [9], nonIncident: [10]);

        var comparison = IncidentMembershipSemanticShadowComparer.Compare(baseline, participant, [9, 10]);

        Assert.Equal([9L], comparison.AddedUnresolvedCallIds);
        Assert.Equal([10L], comparison.AddedNonIncidentCallIds);
    }

    [Fact]
    public void ConfigDefaultsKeepSemanticShadowDisabledAndBounded()
    {
        var config = new EngineConfig
        {
            AiInsights = new AiInsightsConfig
            {
                IncidentMembershipSemanticShadowBaselineSourceLimit = 100,
                IncidentMembershipSemanticShadowAddedSourceLimit = 100
            }
        };

        config.ApplyDefaults();

        Assert.False(config.AiInsights.IncidentMembershipSemanticShadowEnabled);
        Assert.Equal(5, config.AiInsights.IncidentMembershipSemanticShadowBaselineSourceLimit);
        Assert.Equal(1, config.AiInsights.IncidentMembershipSemanticShadowAddedSourceLimit);
    }

    private static ParticipantLinkCandidateShadowComparison Comparison(params long[] addedCallIds) =>
        new(1, 2,
            addedCallIds.Select(callId => new ParticipantLinkCandidateShadowItem(
                callId,
                ParticipantLinkCandidateShadowGroups.SameTalkgroupUpTo60Seconds,
                10_000,
                1,
                2,
                true,
                false)).ToList(),
            [],
            []);

    private static IncidentRagCandidate Candidate(
        long callId,
        long startTime,
        bool participantLinked = false,
        string? transcript = null) =>
        new(
            new EngineCall
            {
                Id = callId,
                StartTime = startTime,
                StopTime = startTime + 4,
                SystemShortName = "OT",
                TalkgroupName = "Dispatch",
                Transcription = transcript ?? $"Transcript {callId}"
            },
            0.15,
            0,
            0,
            0,
            0.15,
            "test",
            "",
            ParticipantLinked: participantLinked,
            SharedRadioCount: participantLinked ? 1 : 0,
            ParticipantGapMilliseconds: participantLinked ? 10_000 : 0,
            SharedRadioNearbySegmentCount: participantLinked ? 2 : 0,
            ParticipantSameTalkgroup: participantLinked);

    private static IncidentMembershipContractResult Result(
        IReadOnlyList<IReadOnlyList<long>> events,
        IReadOnlyList<long>? unresolved = null,
        IReadOnlyList<long>? nonIncident = null) =>
        new(
            events.Select((members, eventIndex) => new IncidentMembershipHypothesis(
                members.Select(callId => new IncidentMembershipSourceIdentity(callId, $"event-{eventIndex}:{callId}")).ToList())).ToList(),
            (unresolved ?? []).Select(callId => new IncidentMembershipSourceIdentity(callId, $"unresolved:{callId}")).ToList(),
            (nonIncident ?? []).Select(callId => new IncidentMembershipSourceIdentity(callId, $"non-incident:{callId}")).ToList());
}
