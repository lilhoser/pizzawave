namespace pizzad.Tests;

public sealed class IncidentTargetMembershipShadowTests
{
    [Fact]
    public void BuilderProducesExactCompleteIncidentPackage()
    {
        var first = Call(10, 100);
        var linked = Call(11, 110);
        var candidate = Call(12, 120);
        var incident = Incident(700, first, linked);

        var packages = IncidentTargetMembershipShadowPackageBuilder.Build(
            "OT",
            [first, linked, candidate],
            [incident],
            [Rag(candidate, participantLinked: true)],
            [new ConversationSegmentLinkEvidence(linked.Id, candidate.Id, 1, 5_000, 3, true)],
            Comparison(candidate.Id));

        var package = Assert.Single(packages);
        Assert.Equal(700, package.IncidentId);
        Assert.Equal([10L, 11L], package.EstablishedCalls.Select(call => call.Id));
        Assert.Equal(11, package.DirectlyLinkedCall.Id);
        Assert.Equal(12, package.Candidate.Id);
        Assert.Equal(5_000, package.SourceLink.GapMilliseconds);
    }

    [Fact]
    public void BuilderRejectsReverseLinkAndIncompleteIncidentEvidence()
    {
        var linked = Call(11, 110);
        var candidate = Call(12, 100);
        var missing = Call(13, 90);
        var incident = Incident(700, linked, missing);

        var packages = IncidentTargetMembershipShadowPackageBuilder.Build(
            "OT",
            [linked, candidate],
            [incident],
            [Rag(candidate, participantLinked: true)],
            [new ConversationSegmentLinkEvidence(candidate.Id, linked.Id, 1, 5_000, 3, true)],
            Comparison(candidate.Id));

        Assert.Empty(packages);
    }

    [Fact]
    public void BuilderRejectsDifferentTalkgroupAndAlreadyPresentCandidate()
    {
        var linked = Call(11, 100);
        var candidate = Call(12, 110) with { Talkgroup = 2 };
        var incident = Incident(700, linked, candidate);

        var packages = IncidentTargetMembershipShadowPackageBuilder.Build(
            "OT",
            [linked, candidate],
            [incident],
            [Rag(candidate, participantLinked: true)],
            [new ConversationSegmentLinkEvidence(linked.Id, candidate.Id, 1, 5_000, 3, false)],
            Comparison(candidate.Id));

        Assert.Empty(packages);
    }

    [Fact]
    public void WorkPolicyRequiresHealthyBoundedAndRecentlyCompletingProductionWork()
    {
        Assert.True(IncidentTargetMembershipShadowWorkPolicy.CanRun(
            Health(pending: 100, stale: 0, completedAge: 5), 250, 15, out var allowedReason));
        Assert.Empty(allowedReason);

        Assert.False(IncidentTargetMembershipShadowWorkPolicy.CanRun(
            Health(pending: 251, stale: 0, completedAge: 5), 250, 15, out var pendingReason));
        Assert.Contains("exceed limit", pendingReason);

        Assert.True(IncidentTargetMembershipShadowWorkPolicy.CanRun(
            Health(pending: 100, stale: 1, completedAge: 5), 250, 15, out var staleReason));
        Assert.Empty(staleReason);

        Assert.False(IncidentTargetMembershipShadowWorkPolicy.CanRun(
            Health(pending: 100, stale: 0, completedAge: 16), 250, 15, out var ageReason));
        Assert.Contains("latest completed", ageReason);
    }

    [Fact]
    public void ConfigDefaultsKeepObservationDisabledAndBounded()
    {
        var config = new EngineConfig
        {
            AiInsights = new AiInsightsConfig
            {
                IncidentTargetMembershipShadowMinimumIntervalSeconds = 1,
                IncidentTargetMembershipShadowDelaySeconds = 999,
                IncidentTargetMembershipShadowMaximumPackages = 999,
                IncidentTargetMembershipShadowMaximumPendingCalls = 0,
                IncidentTargetMembershipShadowMaximumCompletedAgeMinutes = 0
            }
        };

        config.ApplyDefaults();

        Assert.False(config.AiInsights.IncidentTargetMembershipShadowEnabled);
        Assert.Equal(60, config.AiInsights.IncidentTargetMembershipShadowMinimumIntervalSeconds);
        Assert.Equal(300, config.AiInsights.IncidentTargetMembershipShadowDelaySeconds);
        Assert.Equal(100, config.AiInsights.IncidentTargetMembershipShadowMaximumPackages);
        Assert.Equal(250, config.AiInsights.IncidentTargetMembershipShadowMaximumPendingCalls);
        Assert.Equal(15, config.AiInsights.IncidentTargetMembershipShadowMaximumCompletedAgeMinutes);
    }

    private static EngineCall Call(long id, long start) => new()
    {
        Id = id,
        StartTime = start,
        StopTime = start + 4,
        SystemShortName = "OT",
        Talkgroup = 1,
        TalkgroupName = "Dispatch",
        Transcription = $"Transcript {id}",
        TranscriptionStatus = "complete",
        QualityReason = "ok"
    };

    private static IncidentDto Incident(long id, params EngineCall[] calls) => new()
    {
        Id = id,
        Status = "active",
        Calls = calls.Select(call => new IncidentCallDto(
            call.Id,
            call.StartTime,
            call.Transcription,
            string.Empty,
            call.Category,
            call.TalkgroupName,
            call.SystemShortName,
            call.Talkgroup)).ToList()
    };

    private static IncidentRagCandidate Rag(EngineCall call, bool participantLinked) => new(
        call, 0.1, 0, 0, 0, 0.1, "test", string.Empty,
        ParticipantLinked: participantLinked,
        SharedRadioCount: participantLinked ? 1 : 0,
        ParticipantGapMilliseconds: participantLinked ? 5_000 : 0,
        SharedRadioNearbySegmentCount: participantLinked ? 3 : 0,
        ParticipantSameTalkgroup: participantLinked);

    private static ParticipantLinkCandidateShadowComparison Comparison(long candidateCallId) => new(
        1,
        2,
        [new ParticipantLinkCandidateShadowItem(
            candidateCallId,
            ParticipantLinkCandidateShadowGroups.SameTalkgroupUpTo60Seconds,
            5_000,
            1,
            3,
            true,
            false)],
        [],
        []);

    private static IncidentAnalysisQueueHealthDto Health(long pending, long stale, double completedAge) => new(
        "ok",
        "test",
        pending,
        stale,
        0,
        DateTime.UtcNow.AddMinutes(-10),
        10,
        DateTime.UtcNow.AddMinutes(-completedAge),
        completedAge,
        60);
}
