namespace pizzad;

public sealed record IncidentReplayShadowReport(
    DateTime GeneratedAtUtc,
    long CaseCount,
    IncidentReplayShadowSummary Summary,
    IReadOnlyList<IncidentReplayShadowDiff> Diffs);

public sealed record IncidentReplayShadowSummary(
    long V1Accepted,
    long V1Rejected,
    long V2Accepted,
    long V2Rejected,
    long V2Pending,
    long MissingV2,
    long SameAcceptance,
    long AcceptanceChanged,
    long CallSetChanged);

public sealed record IncidentReplayShadowDiff(
    long AuditId,
    string FailureClass,
    string IncidentKey,
    bool V1Accepted,
    IReadOnlyList<long> V1CallIds,
    string V1Reason,
    string V2Status,
    bool? V2Accepted,
    IReadOnlyList<long> V2AcceptedCallIds,
    IReadOnlyList<long> V2PendingCallIds,
    IReadOnlyList<long> V2RejectedCallIds,
    string DiffKind,
    IReadOnlyList<long> CallsOnlyInV1,
    IReadOnlyList<long> CallsOnlyInV2,
    IReadOnlyList<string> V2Reasons);

public static class IncidentReplayShadowReportBuilder
{
    public static IncidentReplayShadowReport Build(
        IncidentReplayCorpus corpus,
        IReadOnlyDictionary<long, PersistenceDecisionV2> v2DecisionsByAuditId,
        DateTime? generatedAtUtc = null)
    {
        var diffs = corpus.Cases
            .Select(row => BuildDiff(row, v2DecisionsByAuditId))
            .ToList();
        return new IncidentReplayShadowReport(
            generatedAtUtc ?? DateTime.UtcNow,
            diffs.Count,
            BuildSummary(diffs),
            diffs);
    }

    private static IncidentReplayShadowDiff BuildDiff(
        IncidentReplayCase row,
        IReadOnlyDictionary<long, PersistenceDecisionV2> v2DecisionsByAuditId)
    {
        var v1CallIds = row.Accepted
            ? row.CallIds.Distinct().Order().ToList()
            : [];
        if (!v2DecisionsByAuditId.TryGetValue(row.AuditId, out var v2))
        {
            return new IncidentReplayShadowDiff(
                row.AuditId,
                row.FailureClass,
                row.IncidentKey,
                row.Accepted,
                v1CallIds,
                row.Reason,
                "missing_hypothesis",
                null,
                [],
                [],
                [],
                "v2_missing",
                v1CallIds,
                [],
                ["no v2 decision supplied for replay case"]);
        }

        var v2Accepted = string.Equals(v2.Decision, "shadow_accept", StringComparison.OrdinalIgnoreCase);
        var v2Pending = string.Equals(v2.Decision, "shadow_pending", StringComparison.OrdinalIgnoreCase);
        var v2AcceptedCallIds = v2Accepted ? v2.AcceptedCallIds.Distinct().Order().ToList() : [];
        var v2PendingCallIds = v2.PendingCallIds?.Distinct().Order().ToList() ?? [];
        var onlyInV1 = v1CallIds.Except(v2AcceptedCallIds).Order().ToList();
        var onlyInV2 = v2AcceptedCallIds.Except(v1CallIds).Order().ToList();
        var callSetChanged = onlyInV1.Count > 0 || onlyInV2.Count > 0;
        var diffKind = v2Pending
            ? row.Accepted ? "v2_pending_v1_accept" : "v2_pending_v1_reject"
            : (row.Accepted, v2Accepted, callSetChanged) switch
        {
            (true, false, _) => "v2_rejects_v1_accept",
            (false, true, _) => "v2_accepts_v1_reject",
            (false, false, _) => "both_reject",
            (true, true, true) => "same_acceptance_call_diff",
            (true, true, false) => "same_acceptance_no_call_diff"
        };

        return new IncidentReplayShadowDiff(
            row.AuditId,
            row.FailureClass,
            row.IncidentKey,
            row.Accepted,
            v1CallIds,
            row.Reason,
            v2.Decision,
            v2Accepted,
            v2AcceptedCallIds,
            v2PendingCallIds,
            v2.RejectedCallIds.Distinct().Order().ToList(),
            diffKind,
            onlyInV1,
            onlyInV2,
            v2.Reasons);
    }

    private static IncidentReplayShadowSummary BuildSummary(IReadOnlyList<IncidentReplayShadowDiff> diffs)
    {
        return new IncidentReplayShadowSummary(
            diffs.LongCount(row => row.V1Accepted),
            diffs.LongCount(row => !row.V1Accepted),
            diffs.LongCount(row => row.V2Accepted == true),
            diffs.LongCount(row => row.V2Accepted == false && !string.Equals(row.V2Status, "shadow_pending", StringComparison.OrdinalIgnoreCase)),
            diffs.LongCount(row => string.Equals(row.V2Status, "shadow_pending", StringComparison.OrdinalIgnoreCase)),
            diffs.LongCount(row => row.DiffKind == "v2_missing"),
            diffs.LongCount(row => row.V2Accepted.HasValue
                                   && !string.Equals(row.V2Status, "shadow_pending", StringComparison.OrdinalIgnoreCase)
                                   && row.V2Accepted.Value == row.V1Accepted),
            diffs.LongCount(row => row.V2Accepted.HasValue
                                   && !string.Equals(row.V2Status, "shadow_pending", StringComparison.OrdinalIgnoreCase)
                                   && row.V2Accepted.Value != row.V1Accepted),
            diffs.LongCount(row => row.CallsOnlyInV1.Count > 0 || row.CallsOnlyInV2.Count > 0));
    }
}
