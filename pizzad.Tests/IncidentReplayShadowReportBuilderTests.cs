namespace pizzad.Tests;

public sealed class IncidentReplayShadowReportBuilderTests
{
    [Fact]
    public void Build_MarksCasesMissingV2UntilHypothesesAreAvailable()
    {
        var corpus = Corpus([
            Case(100, accepted: true, [10], "accepted:create incident; identity:new server-owned incident"),
            Case(101, accepted: false, [20], "rejected:final membership failed validation: single-call incident lacks a strong emergency/event signal")
        ]);

        var report = IncidentReplayShadowReportBuilder.Build(corpus, new Dictionary<long, PersistenceDecisionV2>());

        Assert.Equal(2, report.CaseCount);
        Assert.Equal(2, report.Summary.MissingV2);
        Assert.All(report.Diffs, diff => Assert.Equal("v2_missing", diff.DiffKind));
    }

    [Fact]
    public void Build_ReportsV2RejectingV1Accept()
    {
        var corpus = Corpus([
            Case(100, accepted: true, [10, 11], "accepted:update incident")
        ]);
        var v2 = new PersistenceDecisionV2(
            "shadow_reject",
            "shadow",
            [],
            [10, 11],
            "",
            "",
            "other",
            ["hypothesis contains explicit blocking conflicts"],
            []);

        var report = IncidentReplayShadowReportBuilder.Build(corpus, new Dictionary<long, PersistenceDecisionV2> { [100] = v2 });

        var diff = Assert.Single(report.Diffs);
        Assert.Equal("v2_rejects_v1_accept", diff.DiffKind);
        Assert.Equal([10, 11], diff.CallsOnlyInV1);
        Assert.Equal(1, report.Summary.AcceptanceChanged);
    }

    [Fact]
    public void Build_ReportsV2AcceptingV1Reject()
    {
        var corpus = Corpus([
            Case(101, accepted: false, [20], "rejected:unsupported narrative lacked a specific evidence-backed fallback")
        ]);
        var v2 = new PersistenceDecisionV2(
            "shadow_accept",
            "shadow",
            [20],
            [],
            "Roadway hazard",
            "Tree down.",
            "traffic",
            ["accepted by v2 shadow guardrails"],
            []);

        var report = IncidentReplayShadowReportBuilder.Build(corpus, new Dictionary<long, PersistenceDecisionV2> { [101] = v2 });

        var diff = Assert.Single(report.Diffs);
        Assert.Equal("v2_accepts_v1_reject", diff.DiffKind);
        Assert.Equal([20], diff.CallsOnlyInV2);
        Assert.Equal(1, report.Summary.AcceptanceChanged);
    }

    [Fact]
    public void Build_ReportsSameAcceptanceCallSetDiff()
    {
        var corpus = Corpus([
            Case(102, accepted: true, [30, 31, 32], "accepted:assembler retained 3/4 verifier call(s)")
        ]);
        var v2 = new PersistenceDecisionV2(
            "shadow_accept",
            "shadow",
            [30, 31],
            [32],
            "MVC",
            "MVC.",
            "traffic",
            ["accepted by v2 shadow guardrails"],
            []);

        var report = IncidentReplayShadowReportBuilder.Build(corpus, new Dictionary<long, PersistenceDecisionV2> { [102] = v2 });

        var diff = Assert.Single(report.Diffs);
        Assert.Equal("same_acceptance_call_diff", diff.DiffKind);
        Assert.Equal([32], diff.CallsOnlyInV1);
        Assert.Equal(1, report.Summary.CallSetChanged);
    }

    [Fact]
    public void Build_ReportsPendingAsSeparateOutcome()
    {
        var corpus = Corpus([
            Case(103, accepted: false, [40], "rejected:single call needs more evidence")
        ]);
        var v2 = new PersistenceDecisionV2(
            "shadow_pending",
            "shadow",
            [],
            [40],
            "Medical emergency",
            "Awaiting corroborating calls.",
            "ems",
            ["candidate remains pending because no accepted call is a complete incident anchor yet"],
            [],
            [40]);

        var report = IncidentReplayShadowReportBuilder.Build(corpus, new Dictionary<long, PersistenceDecisionV2> { [103] = v2 });

        var diff = Assert.Single(report.Diffs);
        Assert.Equal("v2_pending_v1_reject", diff.DiffKind);
        Assert.Equal([40], diff.V2PendingCallIds);
        Assert.Empty(diff.V2AcceptedCallIds);
        Assert.Equal(1, report.Summary.V2Pending);
        Assert.Equal(0, report.Summary.V2Rejected);
    }

    private static IncidentReplayCorpus Corpus(IReadOnlyList<IncidentReplayCase> cases) => new(
        new DateTime(2026, 6, 18, 23, 0, 0, DateTimeKind.Utc),
        8,
        new IncidentReplayBaseline(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        cases,
        [],
        []);

    private static IncidentReplayCase Case(long id, bool accepted, IReadOnlyList<long> callIds, string reason) => new(
        id,
        new DateTime(2026, 6, 18, 23, 0, 0, DateTimeKind.Utc),
        "ot",
        accepted ? "llm:ot:10:event" : "new",
        accepted ? "upsert_incident" : "reject_incident",
        accepted,
        reason,
        0.5,
        callIds,
        IncidentReplayCorpusBuilder.ClassifyAuditReason(accepted, reason),
        "{}");
}
