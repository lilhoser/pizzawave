namespace pizzad.Tests;

public sealed class IncidentReconciliationServiceTests
{
    [Fact]
    public void Reconcile_MergesSiblingCandidatesWithSharedGeocodedLocation()
    {
        var service = new IncidentReconciliationService();
        var call1 = Call(101, 1000, "EMS responding to Dollar General for overdose.");
        var call2 = Call(102, 1030, "Deputy at Dollar General with the same patient.");
        var candidates = new[]
        {
            new IncidentReconciliationCandidate(0, "new-a", "Overdose at Dollar General", "EMS responding to Dollar General.", "ems", 0.84, [call1]),
            new IncidentReconciliationCandidate(1, "new-b", "Welfare check at Dollar General", "Deputy checking on the same patient.", "police", 0.80, [call2])
        };

        var result = service.Reconcile(
            candidates,
            [],
            [Location(101, "dollar general springplace", 35.1, -84.9), Location(102, "dollar general springplace", 35.1, -84.9)],
            new Dictionary<long, IReadOnlyList<CallAnchorRecord>>(),
            new Dictionary<long, IncidentRagCandidate>());

        var decision = Assert.Single(result.Decisions);
        Assert.Equal([101, 102], decision.CallIds);
        Assert.Contains(1, decision.MergedCandidateIndexes);
        Assert.Contains(result.AuditRows, row => row.Operation == "merge_sibling_candidate");
    }

    [Fact]
    public void Reconcile_DoesNotRedirectCandidateToActiveIncidentWithSharedAnchor()
    {
        var service = new IncidentReconciliationService();
        var activeCall = IncidentCall(201, 2000, "Fire on I-75 at mile marker 28.");
        var newCall = Call(202, 2030, "Medic responding to the I-75 mile marker 28 crash.");
        var anchors = new Dictionary<long, IReadOnlyList<CallAnchorRecord>>
        {
            [201] = [Anchor(201, "highway_mile_marker", "i75|mm:28")],
            [202] = [Anchor(202, "highway_mile_marker", "i75|mm:28")]
        };
        var active = new IncidentDto
        {
            IncidentKey = "llm:test:201:i75-crash",
            Title = "Crash at I-75 MM 28",
            Detail = "Fire is responding to a crash at I-75 mile marker 28.",
            Category = "traffic",
            FirstSeen = 2000,
            LastSeen = 2000,
            Calls = [activeCall]
        };

        var result = service.Reconcile(
            [new IncidentReconciliationCandidate(0, "new-c", "EMS response to I-75 crash", "Medic responding to the crash.", "ems", 0.81, [newCall])],
            [active],
            [],
            anchors,
            new Dictionary<long, IncidentRagCandidate>());

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("new-c", decision.IncidentId);
        Assert.DoesNotContain("redirected to active incident", decision.Reason);
        Assert.DoesNotContain(result.AuditRows, row => row.Operation == "redirect_to_active_incident");
    }

    [Fact]
    public void Reconcile_MergesI24BarnNurseryCandidatesWithMarkerAndLandmarkEvidence()
    {
        var service = new IncidentReconciliationService();
        var police = Call(595043, 1780849641, "And on near the 180, 24 eastbound, past revised, there was a white SUV over off the interstate and went into the grass near barn nursery. The female driver possibly passed out or unconscious.");
        var ems = Call(595053, 1780849708, "Medical 16, respond to the 180.8 Interstate 24 eastbound for car accident with injuries. Patient is unconscious.");
        var update = Call(595065, 1780849787, "We're getting multiple calls on this. People giving different descriptions. I believe it's going to be the vehicle lost, the interstate, down into the area of the barn nursery, the patio home store.");
        var anchors = new[] { police, ems, update }
            .ToDictionary(c => c.Id, c => (IReadOnlyList<CallAnchorRecord>)CallAnchorExtractionService.ExtractTranscriptAnchors(c.Id, c.Transcription));

        var result = service.Reconcile(
            [
                new IncidentReconciliationCandidate(0, "INC-20260607-022", "MVC I-24 EB near Exit 180 with unconscious patient", "EMS responding to I-24 eastbound for a vehicle accident with an unconscious patient.", "ems", 0.9, [ems]),
                new IncidentReconciliationCandidate(1, "INC-20260607-023", "Vehicle off interstate I-24 EB near Barn Nursery", "Police reporting a white SUV off the interstate into grass near Barn Nursery; driver possibly unconscious.", "traffic", 0.8, [police, update])
            ],
            [],
            [],
            anchors,
            new Dictionary<long, IncidentRagCandidate>());

        var decision = Assert.Single(result.Decisions);
        Assert.Equal([595043, 595053, 595065], decision.CallIds);
        Assert.Contains(1, decision.MergedCandidateIndexes);
        Assert.Contains("shared strong anchor", decision.Reason);
    }

    [Fact]
    public void Reconcile_DoesNotRedirectOnSharedCallIdAlone()
    {
        var service = new IncidentReconciliationService();
        var call = Call(301, 3000, "Unit checking unrelated status.");
        var active = new IncidentDto
        {
            IncidentKey = "llm:test:301:status",
            Title = "Older status event",
            Detail = "Older status detail.",
            Category = "police",
            FirstSeen = 3000,
            LastSeen = 3000,
            Calls = [new IncidentCallDto(301, 3000, call.Transcription, "/audio", "police", "Dispatch", "test")]
        };

        var result = service.Reconcile(
            [new IncidentReconciliationCandidate(0, "new-status", "New status event", "New status detail.", "police", 0.7, [call])],
            [active],
            [],
            new Dictionary<long, IReadOnlyList<CallAnchorRecord>>(),
            new Dictionary<long, IncidentRagCandidate>());

        var decision = Assert.Single(result.Decisions);
        Assert.Equal("new-status", decision.IncidentId);
        Assert.DoesNotContain(result.AuditRows, row => row.Operation == "redirect_to_active_incident");
    }

    private static EngineCall Call(long id, long start, string text) => new()
    {
        Id = id,
        StartTime = start,
        StopTime = start + 10,
        SystemShortName = "test",
        Talkgroup = 100,
        TalkgroupName = "Dispatch",
        Category = "ems",
        Transcription = text,
        TranscriptionStatus = "complete",
        QualityReason = "ok"
    };

    private static IncidentCallDto IncidentCall(long id, long start, string text) =>
        new(id, start, text, $"/api/v1/calls/{id}/audio", "traffic", "Dispatch", "test");

    private static CallAnchorRecord Anchor(long callId, string kind, string value) =>
        new(callId, kind, value, value, "deterministic", 0.98, "{}");

    private static CallLocationDashboardRow Location(long callId, string key, double lat, double lon) => new(
        callId,
        0,
        "test",
        100,
        "Dispatch",
        "ems",
        "",
        "test",
        "Test",
        "test",
        key,
        key,
        "transcript",
        key,
        key,
        "cache",
        "poi",
        0.9,
        lat,
        lon);
}
