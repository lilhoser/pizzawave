namespace pizzad.Tests;

public sealed class IncidentCombinedCapacityReplayTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-07-23T00:00:00Z");

    [Fact]
    public void PlannerKeepsMeasuredControlMixedAndProjectedReplacementScenariosSeparate()
    {
        var report = IncidentCombinedCapacityReplayPlanner.Build(
            "combined-1",
            DateTimeOffset.Parse("2026-07-23T02:00:00Z"),
            "control",
            "run-f",
            "ot",
            1.5,
            Traces());

        Assert.Equal(IncidentCombinedCapacityReplayPlanner.ProtocolIdentity, report.ProtocolIdentity);
        Assert.Equal(500, report.ReplacementTokensPerProcessedObservation);
        Assert.Equal(10, report.ReplacementObservationsPerRequest);
        Assert.Equal(64, report.ContentHash.Length);

        var oldOld = Assert.Single(report.Scenarios, item => item.Name == "old-old");
        Assert.Equal("measured-control", oldOld.Evidence);
        Assert.Equal(9, oldOld.ObservationDemandPerMinute);
        Assert.Equal(3, oldOld.RequestsPerMinute);
        Assert.Equal(0, oldOld.FailedRequestsPerMinute);
        Assert.Equal(6000, oldOld.TokensPerMinute);
        Assert.Equal(0, oldOld.MeasuredProcessedObservationsPerMinute);

        var newOld = Assert.Single(report.Scenarios, item => item.Name == "new-old");
        Assert.Equal("measured-mixed", newOld.Evidence);
        Assert.Equal(8, newOld.ObservationDemandPerMinute);
        Assert.Equal(6, newOld.MeasuredProcessedObservationsPerMinute);
        Assert.Equal(1.6, newOld.RequestsPerMinute, 6);
        Assert.Equal(0, newOld.FailedRequestsPerMinute);
        Assert.Equal(5000, newOld.TokensPerMinute);
        Assert.Equal(6d / 7d, newOld.ReplacementCoverage, 6);
        Assert.Equal("ProvisionalIntake", newOld.PipelineBySystem["ot"]);
        Assert.Equal("Legacy", newOld.PipelineBySystem["rpi"]);

        var newNew = Assert.Single(report.Scenarios, item => item.Name == "new-new");
        Assert.Equal("projected-from-replacement-cost-at-proven-demand", newNew.Evidence);
        Assert.Equal(9, newNew.ObservationDemandPerMinute);
        Assert.Equal(0.9, newNew.RequestsPerMinute, 6);
        Assert.Equal(4500, newNew.TokensPerMinute);
        Assert.Equal(0, newNew.MeasuredProcessedObservationsPerMinute);

        var headroom = Assert.Single(report.Scenarios, item => item.Name == "new-new-headroom-target");
        Assert.Equal("capacity-gate", headroom.Evidence);
        Assert.Equal(13.5, headroom.ObservationDemandPerMinute);
        Assert.Equal(1.35, headroom.RequestsPerMinute, 6);
        Assert.Equal(6750, headroom.TokensPerMinute);
        Assert.All(report.Scenarios, scenario => Assert.False(scenario.IncludesVerification));
    }

    [Fact]
    public void ContentHashDoesNotDependOnInputTraceOrderOrReportTimestamp()
    {
        var traces = Traces();
        var first = IncidentCombinedCapacityReplayPlanner.Build("combined-1", Start.AddHours(2), "control", "run-f", "ot", 1.5, traces);
        var second = IncidentCombinedCapacityReplayPlanner.Build("combined-1", Start.AddHours(3), "control", "run-f", "ot", 1.5, traces.Reverse().ToList());

        Assert.Equal(first.ContentHash, second.ContentHash);
    }

    [Fact]
    public void PlannerRequiresOneLegacyControlPerSystem()
    {
        var traces = Traces().Where(trace => trace.TraceId != "rpi-old-control").ToList();

        var error = Assert.Throws<ArgumentException>(() => IncidentCombinedCapacityReplayPlanner.Build(
            "combined-1", Start.AddHours(2), "control", "run-f", "ot", 1.5, traces));

        Assert.Contains("legacy control", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerRejectsDuplicatePipelineBoundary()
    {
        var traces = Traces().Append(Traces()[0] with { TraceId = "ot-old-copy" }).ToList();

        var error = Assert.Throws<ArgumentException>(() => IncidentCombinedCapacityReplayPlanner.Build(
            "combined-1", Start.AddHours(2), "control", "run-f", "ot", 1.5, traces));

        Assert.Contains("exactly one trace", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlannerRejectsCohortWindowsThatAreNotAlignedAcrossSystems()
    {
        var traces = Traces().Select(trace => trace.TraceId == "rpi-old-run-f"
            ? trace with { WindowEndUtc = trace.WindowEndUtc.AddMinutes(1) }
            : trace).ToList();

        var error = Assert.Throws<ArgumentException>(() => IncidentCombinedCapacityReplayPlanner.Build(
            "combined-1", Start.AddHours(2), "control", "run-f", "ot", 1.5, traces));

        Assert.Contains("aligned half-open window", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacementTraceRequiresMeasuredWork()
    {
        var trace = Traces().Single(item => item.TraceId == "ot-new-run-f") with { ProcessedObservations = 0 };

        var error = Assert.Throws<ArgumentException>(trace.Validate);

        Assert.Contains("processed observations", error.Message, StringComparison.Ordinal);
    }

    private static IReadOnlyList<IncidentCapacityTrace> Traces() =>
    [
        Trace("ot-old-control", "control", "ot", IncidentCapacityPipelineKind.Legacy, 70, 0, 20, 30_000, 10_000, 0, 0),
        Trace("rpi-old-control", "control", "rpi", IncidentCapacityPipelineKind.Legacy, 20, 0, 10, 15_000, 5_000, 0, 0),
        Trace("ot-new-run-f", "run-f", "ot", IncidentCapacityPipelineKind.ProvisionalIntake, 70, 60, 6, 24_000, 6_000, 600_000, 5),
        Trace("rpi-old-run-f", "run-f", "rpi", IncidentCapacityPipelineKind.Legacy, 10, 0, 10, 15_000, 5_000, 0, 0)
    ];

    private static IncidentCapacityTrace Trace(
        string traceId,
        string cohortId,
        string systemId,
        IncidentCapacityPipelineKind pipeline,
        int usable,
        int processed,
        int requests,
        int promptTokens,
        int completionTokens,
        long durationMilliseconds,
        int candidateBatches) => new(
            traceId,
            cohortId,
            systemId,
            pipeline,
            Start,
            Start.AddMinutes(10),
            pipeline == IncidentCapacityPipelineKind.ProvisionalIntake ? 100 : 0,
            usable,
            processed,
            requests,
            0,
            promptTokens,
            completionTokens,
            durationMilliseconds,
            candidateBatches);
}
