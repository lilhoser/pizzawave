namespace pizzad.Tests;

public sealed class TranscriptionCandidateAnalyzerTests
{
    [Fact]
    public void BuildReport_FlagsUnlinkedSeverityWithSuspiciousLocation()
    {
        var call = TestCall(
            1001,
            "County, road closed near Oodlewall Georgetown. Units advise traffic is blocked both directions.");

        var report = TranscriptionCandidateAnalyzer.BuildReport(
            [call],
            [],
            call.StartTime - 60,
            call.StopTime + 60,
            20,
            includeIncidentLinked: true);

        var candidate = Assert.Single(report.Candidates);
        Assert.Equal(call.Id, candidate.CallId);
        Assert.False(candidate.IncidentLinked);
        Assert.Contains("road closure", candidate.SeveritySignals);
        Assert.Contains(candidate.Reasons, r => r.Contains("not linked", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(candidate.SuspiciousLocationFragments);
        Assert.True(candidate.Score >= 70);
    }

    [Fact]
    public void BuildReport_IgnoresRoutineNonIncidentTraffic()
    {
        var call = TestCall(1002, "10-4, show me clear. I'll be en route back to station.");

        var report = TranscriptionCandidateAnalyzer.BuildReport(
            [call],
            [],
            call.StartTime - 60,
            call.StopTime + 60,
            20,
            includeIncidentLinked: true);

        Assert.Empty(report.Candidates);
    }

    [Fact]
    public void BuildReport_CanIncludeLinkedFactMissCandidates()
    {
        var call = TestCall(
            1003,
            "Dispatch, road closure near Woodwall Georgetown with a crash blocking both lanes.");
        var incident = new IncidentDto
        {
            Id = 44,
            Title = "Traffic crash",
            Detail = "Crash with traffic impact.",
            Category = "police",
            FirstSeen = call.StartTime,
            LastSeen = call.StopTime,
            Calls =
            [
                new IncidentCallDto(call.Id, call.StartTime, call.Transcription, $"/api/v1/calls/{call.Id}/audio", call.Category, call.TalkgroupName, call.SystemShortName)
            ]
        };

        var includeLinked = TranscriptionCandidateAnalyzer.BuildReport(
            [call],
            [incident],
            call.StartTime - 60,
            call.StopTime + 60,
            20,
            includeIncidentLinked: true);
        var unlinkedOnly = TranscriptionCandidateAnalyzer.BuildReport(
            [call],
            [incident],
            call.StartTime - 60,
            call.StopTime + 60,
            20,
            includeIncidentLinked: false);

        var candidate = Assert.Single(includeLinked.Candidates);
        Assert.True(candidate.IncidentLinked);
        Assert.Contains(candidate.Reasons, r => r.Contains("lost a fact", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(unlinkedOnly.Candidates);
    }

    [Fact]
    public void BuildReport_ReportsSymbolEvidenceMetadata()
    {
        var call = TestCall(
            1004,
            "Engine 7 respond to Main Road for wires down blocking traffic.",
            rawMetadataJson: """{"symbolPath":"C:\\missing\\call-1004.dvcf","CallId":1004}""");

        var report = TranscriptionCandidateAnalyzer.BuildReport(
            [call],
            [],
            call.StartTime - 60,
            call.StopTime + 60,
            20,
            includeIncidentLinked: true);

        var candidate = Assert.Single(report.Candidates);
        Assert.True(candidate.HasSymbolEvidence);
        Assert.Contains(candidate.SymbolEvidence, evidence => evidence.Kind == "symbolPath");
    }

    private static EngineCall TestCall(long id, string transcript, string rawMetadataJson = "{}") => new()
    {
        Id = id,
        UniqueKey = $"test-{id}",
        StartTime = 1780000000 + id,
        StopTime = 1780000012 + id,
        SystemShortName = "test-system",
        CallstreamCallId = id,
        Talkgroup = 1137,
        TalkgroupName = "Dispatch",
        Category = "police",
        AudioPath = $"calls/{id}.wav",
        Transcription = transcript,
        TranscriptionStatus = "complete",
        QualityReason = "ok",
        RawMetadataJson = rawMetadataJson
    };
}
