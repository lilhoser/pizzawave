using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IncidentCapacityPipelineKind
{
    Legacy,
    ProvisionalIntake
}

public sealed record IncidentCapacityTrace(
    string TraceId,
    string CohortId,
    string SystemId,
    IncidentCapacityPipelineKind Pipeline,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    long StartAfterCallId,
    int UsableObservations,
    int ProcessedObservations,
    int Requests,
    int FailedRequests,
    int PromptTokens,
    int CompletionTokens,
    long RequestDurationMilliseconds,
    int CandidateBackedBatches,
    bool IncludesVerification)
{
    public double DurationMinutes => (WindowEndUtc - WindowStartUtc).TotalMinutes;
    public int TotalTokens => PromptTokens + CompletionTokens;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TraceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(CohortId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SystemId);
        if (WindowStartUtc == default || WindowEndUtc <= WindowStartUtc)
            throw new ArgumentException($"trace '{TraceId}' has an invalid window");
        if (StartAfterCallId < 0 || UsableObservations < 0 || ProcessedObservations < 0 || Requests < 0 ||
            FailedRequests < 0 || FailedRequests > Requests || PromptTokens < 0 ||
            CompletionTokens < 0 || RequestDurationMilliseconds < 0 || CandidateBackedBatches < 0)
        {
            throw new ArgumentException($"trace '{TraceId}' contains a negative or inconsistent count");
        }
        if (Pipeline == IncidentCapacityPipelineKind.ProvisionalIntake &&
            (ProcessedObservations == 0 || Requests == 0))
        {
            throw new ArgumentException($"replacement trace '{TraceId}' requires processed observations and requests");
        }
        if (Pipeline == IncidentCapacityPipelineKind.Legacy && ProcessedObservations != 0)
            throw new ArgumentException($"legacy trace '{TraceId}' cannot claim constructor-processed observations");
        if (Pipeline == IncidentCapacityPipelineKind.Legacy && StartAfterCallId != 0)
            throw new ArgumentException($"legacy trace '{TraceId}' cannot use a replacement startup fence");
        if (Pipeline == IncidentCapacityPipelineKind.ProvisionalIntake && StartAfterCallId == 0)
            throw new ArgumentException($"replacement trace '{TraceId}' requires its no-backfill startup fence");
        if (!IncludesVerification)
            throw new ArgumentException($"trace '{TraceId}' must include verification workload");
    }
}

public sealed record IncidentCapacityScenario(
    string Name,
    string Evidence,
    IReadOnlyDictionary<string, string> PipelineBySystem,
    double ObservationDemandPerMinute,
    double MeasuredProcessedObservationsPerMinute,
    double RequestsPerMinute,
    double FailedRequestsPerMinute,
    double TokensPerMinute,
    double RequestOccupancy,
    double MeanRequestDurationMilliseconds,
    double ReplacementCoverage,
    bool IncludesVerification);

public sealed record IncidentCombinedCapacityReplayReport(
    string ReplayId,
    string ProtocolIdentity,
    DateTimeOffset CreatedAtUtc,
    string ControlCohortId,
    string MixedCohortId,
    string ReplacementSystemId,
    double HeadroomMultiplier,
    double ReplacementTokensPerProcessedObservation,
    double ReplacementDurationMillisecondsPerProcessedObservation,
    double ReplacementObservationsPerRequest,
    IReadOnlyList<IncidentCapacityTrace> Traces,
    IReadOnlyList<IncidentCapacityScenario> Scenarios,
    string ContentHash);

public static class IncidentCombinedCapacityReplayPlanner
{
    public const string ProtocolIdentity = "incident-combined-capacity-replay-v2-full-request-occupancy";

    public static IncidentCombinedCapacityReplayReport Build(
        string replayId,
        DateTimeOffset createdAtUtc,
        string controlCohortId,
        string mixedCohortId,
        string replacementSystemId,
        double headroomMultiplier,
        IReadOnlyList<IncidentCapacityTrace> traces)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayId);
        ArgumentException.ThrowIfNullOrWhiteSpace(controlCohortId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mixedCohortId);
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementSystemId);
        if (createdAtUtc == default)
            throw new ArgumentException("replay creation timestamp is required", nameof(createdAtUtc));
        if (!double.IsFinite(headroomMultiplier) || headroomMultiplier < 1)
            throw new ArgumentOutOfRangeException(nameof(headroomMultiplier));
        ArgumentNullException.ThrowIfNull(traces);
        foreach (var trace in traces)
            trace.Validate();
        if (traces.Select(trace => trace.TraceId).Distinct(StringComparer.Ordinal).Count() != traces.Count)
            throw new ArgumentException("trace ids must be unique", nameof(traces));

        var systems = traces.Select(trace => trace.SystemId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (systems.Count < 2)
            throw new ArgumentException("combined capacity replay requires at least two systems", nameof(traces));
        var byBoundary = traces
            .GroupBy(trace => (trace.CohortId, trace.SystemId, trace.Pipeline))
            .ToDictionary(group => group.Key, group => group.ToList());
        if (byBoundary.Any(item => item.Value.Count != 1))
            throw new ArgumentException("each cohort, system, and pipeline boundary requires exactly one trace", nameof(traces));
        foreach (var system in systems)
        {
            if (!byBoundary.ContainsKey((controlCohortId, system, IncidentCapacityPipelineKind.Legacy)))
                throw new ArgumentException($"system '{system}' is missing its legacy control trace in cohort '{controlCohortId}'", nameof(traces));
        }
        if (!byBoundary.TryGetValue((mixedCohortId, replacementSystemId, IncidentCapacityPipelineKind.ProvisionalIntake), out var selectedReplacementRows))
            throw new ArgumentException($"replacement system '{replacementSystemId}' is missing its provisional-intake trace in cohort '{mixedCohortId}'", nameof(traces));
        foreach (var system in systems.Where(system => !string.Equals(system, replacementSystemId, StringComparison.Ordinal)))
        {
            if (!byBoundary.ContainsKey((mixedCohortId, system, IncidentCapacityPipelineKind.Legacy)))
                throw new ArgumentException($"system '{system}' is missing its legacy mixed-cohort trace in cohort '{mixedCohortId}'", nameof(traces));
        }

        var replacementTraces = traces
            .Where(trace => trace.CohortId == mixedCohortId && trace.Pipeline == IncidentCapacityPipelineKind.ProvisionalIntake)
            .ToList();
        var replacementProcessed = replacementTraces.Sum(trace => trace.ProcessedObservations);
        var replacementRequests = replacementTraces.Sum(trace => trace.Requests);
        var replacementTokensPerObservation = replacementTraces.Sum(trace => trace.TotalTokens) / (double)replacementProcessed;
        var replacementDurationPerObservation =
            replacementTraces.Sum(trace => trace.RequestDurationMilliseconds) / (double)replacementProcessed;
        var replacementObservationsPerRequest = replacementProcessed / (double)replacementRequests;

        var oldOldTraces = systems.Select(system => byBoundary[(controlCohortId, system, IncidentCapacityPipelineKind.Legacy)].Single()).ToList();
        ValidateAlignedCohort(controlCohortId, oldOldTraces);
        var oldOld = MeasuredScenario("old-old", "measured-control", oldOldTraces);

        var selectedReplacement = selectedReplacementRows.Single();
        var newOldTraces = systems.Select(system => string.Equals(system, replacementSystemId, StringComparison.Ordinal)
            ? selectedReplacement
            : byBoundary[(mixedCohortId, system, IncidentCapacityPipelineKind.Legacy)].Single()).ToList();
        ValidateAlignedCohort(mixedCohortId, newOldTraces);
        var newOld = MeasuredScenario("new-old", "measured-mixed", newOldTraces);

        var combinedDemand = Math.Max(oldOld.ObservationDemandPerMinute, newOld.ObservationDemandPerMinute);
        var allReplacementPipelines = systems.ToDictionary(system => system, _ => IncidentCapacityPipelineKind.ProvisionalIntake.ToString(), StringComparer.Ordinal);
        var newNew = new IncidentCapacityScenario(
            "new-new",
            "projected-from-replacement-cost-at-proven-demand",
            allReplacementPipelines,
            combinedDemand,
            0,
            combinedDemand / replacementObservationsPerRequest,
            0,
            combinedDemand * replacementTokensPerObservation,
            combinedDemand * replacementDurationPerObservation / 60_000d,
            replacementDurationPerObservation * replacementObservationsPerRequest,
            0,
            true);
        var headroomDemand = combinedDemand * headroomMultiplier;
        var headroom = new IncidentCapacityScenario(
            "new-new-headroom-target",
            "capacity-gate",
            allReplacementPipelines,
            headroomDemand,
            0,
            headroomDemand / replacementObservationsPerRequest,
            0,
            headroomDemand * replacementTokensPerObservation,
            headroomDemand * replacementDurationPerObservation / 60_000d,
            replacementDurationPerObservation * replacementObservationsPerRequest,
            0,
            true);
        var orderedTraces = traces
            .OrderBy(trace => trace.CohortId, StringComparer.Ordinal)
            .ThenBy(trace => trace.SystemId, StringComparer.Ordinal)
            .ThenBy(trace => trace.Pipeline)
            .ThenBy(trace => trace.TraceId, StringComparer.Ordinal)
            .ToList();
        var scenarios = new[] { oldOld, newOld, newNew, headroom };
        var contentHash = Hash(new
        {
            replayId,
            protocolIdentity = ProtocolIdentity,
            controlCohortId,
            mixedCohortId,
            replacementSystemId,
            headroomMultiplier,
            traces = orderedTraces,
            scenarios
        });
        return new IncidentCombinedCapacityReplayReport(
            replayId,
            ProtocolIdentity,
            createdAtUtc,
            controlCohortId,
            mixedCohortId,
            replacementSystemId,
            headroomMultiplier,
            replacementTokensPerObservation,
            replacementDurationPerObservation,
            replacementObservationsPerRequest,
            orderedTraces,
            scenarios,
            contentHash);
    }

    private static IncidentCapacityScenario MeasuredScenario(
        string name,
        string evidence,
        IReadOnlyList<IncidentCapacityTrace> traces)
    {
        var demand = traces.Sum(trace => Rate(trace.UsableObservations, trace.DurationMinutes));
        var processed = traces
            .Where(trace => trace.Pipeline == IncidentCapacityPipelineKind.ProvisionalIntake)
            .Sum(trace => Rate(trace.ProcessedObservations, trace.DurationMinutes));
        var replacementDemand = traces
            .Where(trace => trace.Pipeline == IncidentCapacityPipelineKind.ProvisionalIntake)
            .Sum(trace => Rate(trace.UsableObservations, trace.DurationMinutes));
        var totalRequests = traces.Sum(trace => trace.Requests);
        var totalRequestDuration = traces.Sum(trace => trace.RequestDurationMilliseconds);
        var windowDurationMilliseconds = traces[0].DurationMinutes * 60_000d;
        return new IncidentCapacityScenario(
            name,
            evidence,
            traces.ToDictionary(trace => trace.SystemId, trace => trace.Pipeline.ToString(), StringComparer.Ordinal),
            demand,
            processed,
            traces.Sum(trace => Rate(trace.Requests, trace.DurationMinutes)),
            traces.Sum(trace => Rate(trace.FailedRequests, trace.DurationMinutes)),
            traces.Sum(trace => Rate(trace.TotalTokens, trace.DurationMinutes)),
            totalRequestDuration / windowDurationMilliseconds,
            totalRequests == 0 ? 0 : totalRequestDuration / (double)totalRequests,
            replacementDemand == 0 ? 0 : processed / replacementDemand,
            traces.All(trace => trace.IncludesVerification));
    }

    private static double Rate(double value, double durationMinutes) => value / durationMinutes;

    private static void ValidateAlignedCohort(string cohortId, IReadOnlyList<IncidentCapacityTrace> traces)
    {
        var window = (traces[0].WindowStartUtc, traces[0].WindowEndUtc);
        if (traces.Any(trace => trace.WindowStartUtc != window.WindowStartUtc || trace.WindowEndUtc != window.WindowEndUtc))
            throw new ArgumentException($"cohort '{cohortId}' traces must use one aligned half-open window");
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, EngineConfig.JsonOptions()))));
}
