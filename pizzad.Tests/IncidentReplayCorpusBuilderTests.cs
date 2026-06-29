namespace pizzad.Tests;

public sealed class IncidentReplayCorpusBuilderTests
{
    [Theory]
    [InlineData(false, "rejected:unsupported narrative lacked a specific evidence-backed fallback", "unsupported_narrative")]
    [InlineData(false, "rejected:final membership failed validation: routine status/compliance rollup lacks a shared concrete event anchor", "routine_status")]
    [InlineData(false, "rejected:final membership would add or replace existing concrete incident anchor with conflicting location evidence", "location_conflict")]
    [InlineData(true, "accepted:assembler retained 2/3 verifier call(s): server-owned event membership; excluded weak/unrelated calls 802153", "verifier_selection")]
    [InlineData(true, "accepted:create incident; identity:new server-owned incident", "accepted_create")]
    public void ClassifyAuditReason_MapsKnownAuditReasons(bool accepted, string reason, string expected)
    {
        Assert.Equal(expected, IncidentReplayCorpusBuilder.ClassifyAuditReason(accepted, reason));
    }

    [Fact]
    public void Build_ProducesBaselineCasesClustersAndWatchlistLabels()
    {
        var quality = new QualityCheckSnapshotDto(
            new DateTime(2026, 6, 18, 22, 31, 0, DateTimeKind.Utc),
            1,
            2,
            new QualityCheckCallSummaryDto(4316, 67795, 205, 1, 2),
            [],
            [],
            new QualityCheckAiSummaryDto(313, 306, 7, 0, 630890, 163422, null),
            new QualityCheckEvidenceVerifierSummaryDto(170, 5.6, 0.28, 5, 51, 29, 0),
            new QualityCheckIncidentSummaryDto(35, 0.86, 39, 56, 89, []),
            []);
        var auditRows = new[]
        {
            new IncidentOperationAuditRowDto(
                30046,
                new DateTime(2026, 6, 18, 22, 9, 45, DateTimeKind.Utc),
                "whiteoakmt-cleveland",
                "llm:whiteoakmt-cleveland:802453:event",
                "upsert_incident",
                true,
                "accepted:create incident; identity:new server-owned incident",
                0.56,
                [802453],
                "{}"),
            new IncidentOperationAuditRowDto(
                30051,
                new DateTime(2026, 6, 18, 22, 14, 52, DateTimeKind.Utc),
                "whiteoakmt-hamilton",
                "new",
                "reject_incident",
                false,
                "rejected:final membership failed validation: single-call incident lacks a strong emergency/event signal",
                0.60,
                [802043],
                "{}")
        };
        var watchlist = "- Negative proof caught and fixed: row `30046` created incident `3906` with unsupported location.\n"
                        + "- Proven improvement: rows `30040` through `30042` created incident `3904`.\n";

        var corpus = IncidentReplayCorpusBuilder.Build(
            quality,
            auditRows,
            watchlist,
            hours: 8,
            generatedAtUtc: new DateTime(2026, 6, 18, 22, 40, 0, DateTimeKind.Utc));

        Assert.Equal(4316, corpus.Baseline.Calls);
        Assert.Equal(313, corpus.Baseline.AiRequests);
        Assert.Equal(2, corpus.Cases.Count);
        Assert.Contains(corpus.Cases, row => row.FailureClass == "single_call_gate");
        Assert.Contains(corpus.ReasonClusters, cluster => cluster.FailureClass == "accepted_create" && cluster.Count == 1);
        Assert.Contains(corpus.WatchlistLabels, label => label.Kind == "negative_proof" && label.AuditRows.Contains(30046));
        Assert.Contains(corpus.WatchlistLabels, label => label.Kind == "proven_improvement" && label.AuditRows.Contains(30040));
    }
}
