using System.Text.RegularExpressions;

namespace pizzad;

public sealed record IncidentReplayCorpus(
    DateTime GeneratedAtUtc,
    int Hours,
    IncidentReplayBaseline Baseline,
    IReadOnlyList<IncidentReplayCase> Cases,
    IReadOnlyList<IncidentWatchlistLabel> WatchlistLabels,
    IReadOnlyList<IncidentReplayReasonCluster> ReasonClusters);

public sealed record IncidentReplayBaseline(
    long Calls,
    long AiRequests,
    long EvidenceVerifierRuns,
    long Incidents,
    long Creates,
    long Updates,
    long Rejects,
    long AiFailures,
    long AiTruncated,
    long VerifierRetentionMismatches,
    double AverageVerifierTruncatedCalls);

public sealed record IncidentReplayCase(
    long AuditId,
    DateTime TimestampUtc,
    string SystemShortName,
    string IncidentKey,
    string Operation,
    bool Accepted,
    string Reason,
    double Score,
    IReadOnlyList<long> CallIds,
    string FailureClass,
    string MetadataJson);

public sealed record IncidentWatchlistLabel(
    string LabelId,
    string Kind,
    IReadOnlyList<long> AuditRows,
    IReadOnlyList<long> IncidentIds,
    string Text);

public sealed record IncidentReplayReasonCluster(
    string FailureClass,
    bool Accepted,
    long Count);

public static partial class IncidentReplayCorpusBuilder
{
    public static IncidentReplayCorpus Build(
        QualityCheckSnapshotDto? quality,
        IReadOnlyList<IncidentOperationAuditRowDto> auditRows,
        string watchlistText,
        int hours,
        DateTime? generatedAtUtc = null)
    {
        var cases = auditRows
            .OrderBy(row => row.TimestampUtc)
            .ThenBy(row => row.Id)
            .Select(row => new IncidentReplayCase(
                row.Id,
                row.TimestampUtc,
                row.SystemShortName,
                row.IncidentKey,
                row.Operation,
                row.Accepted,
                row.Reason,
                row.Score,
                row.CallIds,
                ClassifyAuditReason(row.Accepted, row.Reason),
                row.MetadataJson))
            .ToList();

        var clusters = cases
            .GroupBy(row => (row.FailureClass, row.Accepted))
            .Select(group => new IncidentReplayReasonCluster(group.Key.FailureClass, group.Key.Accepted, group.LongCount()))
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.FailureClass, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new IncidentReplayCorpus(
            generatedAtUtc ?? DateTime.UtcNow,
            hours,
            BuildBaseline(quality),
            cases,
            ParseWatchlistLabels(watchlistText),
            clusters);
    }

    public static string ClassifyAuditReason(bool accepted, string reason)
    {
        var value = (reason ?? string.Empty).ToLowerInvariant();
        if (value.Contains("semantic", StringComparison.OrdinalIgnoreCase))
            return "semantic_only_join";
        if (value.Contains("unsupported narrative", StringComparison.OrdinalIgnoreCase))
            return "unsupported_narrative";
        if (value.Contains("conflicting location", StringComparison.OrdinalIgnoreCase)
            || value.Contains("concrete incident anchor", StringComparison.OrdinalIgnoreCase)
            || value.Contains("different locations", StringComparison.OrdinalIgnoreCase))
            return "location_conflict";
        if (value.Contains("transport", StringComparison.OrdinalIgnoreCase)
            || value.Contains("hospital handoff", StringComparison.OrdinalIgnoreCase)
            || value.Contains("medical_transport_context", StringComparison.OrdinalIgnoreCase))
            return "transport_handoff";
        if (value.Contains("routine status", StringComparison.OrdinalIgnoreCase)
            || value.Contains("operational chatter", StringComparison.OrdinalIgnoreCase)
            || value.Contains("radio/test traffic", StringComparison.OrdinalIgnoreCase))
            return "routine_status";
        if (value.Contains("verifier", StringComparison.OrdinalIgnoreCase))
            return "verifier_selection";
        if (value.Contains("single-call incident lacks", StringComparison.OrdinalIgnoreCase))
            return "single_call_gate";
        if (value.Contains("retained", StringComparison.OrdinalIgnoreCase)
            || value.Contains("pruned", StringComparison.OrdinalIgnoreCase)
            || value.Contains("excluded weak/unrelated", StringComparison.OrdinalIgnoreCase))
            return "membership_pruning";
        if (value.Contains("accepted:create", StringComparison.OrdinalIgnoreCase))
            return "accepted_create";
        if (value.Contains("accepted:update", StringComparison.OrdinalIgnoreCase))
            return "accepted_update";
        return accepted ? "accepted_other" : "rejected_other";
    }

    public static IReadOnlyList<IncidentWatchlistLabel> ParseWatchlistLabels(string watchlistText)
    {
        var labels = new List<IncidentWatchlistLabel>();
        var index = 0;
        foreach (var rawLine in (watchlistText ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            var kind = ClassifyWatchlistLine(line);
            if (kind.Length == 0)
                continue;

            index++;
            var auditRows = RowPattern()
                .Matches(line)
                .Select(match => long.TryParse(match.Groups["id"].Value, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var incidents = IncidentPattern()
                .Matches(line)
                .Select(match => long.TryParse(match.Groups["id"].Value, out var id) ? id : 0)
                .Where(id => id > 0)
                .Distinct()
                .ToList();
            var labelId = auditRows.Count > 0
                ? $"{kind}:row-{auditRows[0]}"
                : $"{kind}:watchlist-{index}";
            labels.Add(new IncidentWatchlistLabel(labelId, kind, auditRows, incidents, line));
        }

        return labels;
    }

    private static IncidentReplayBaseline BuildBaseline(QualityCheckSnapshotDto? quality)
    {
        if (quality is null)
            return new IncidentReplayBaseline(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        return new IncidentReplayBaseline(
            quality.Calls.TotalCalls,
            quality.Ai.Requests,
            quality.EvidenceVerifier.Runs,
            quality.Incidents.Incidents,
            quality.Incidents.Creates,
            quality.Incidents.Updates,
            quality.Incidents.Rejects,
            quality.Ai.Failures,
            quality.Ai.Truncated,
            quality.EvidenceVerifier.RetentionMismatches,
            quality.EvidenceVerifier.AverageTruncatedCalls);
    }

    private static string ClassifyWatchlistLine(string line)
    {
        if (!line.StartsWith("-", StringComparison.Ordinal))
            return string.Empty;
        if (line.Contains("Negative proof caught and fixed", StringComparison.OrdinalIgnoreCase))
            return "negative_proof";
        if (line.Contains("Proven improvement", StringComparison.OrdinalIgnoreCase))
            return "proven_improvement";
        if (line.Contains("Early clean signal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Continued clean signal", StringComparison.OrdinalIgnoreCase))
            return "clean_signal";
        if (line.Contains("Watch-only", StringComparison.OrdinalIgnoreCase)
            || line.Contains("watch-only", StringComparison.OrdinalIgnoreCase))
            return "watch_only";
        if (line.Contains("Still uncertain", StringComparison.OrdinalIgnoreCase))
            return "uncertain";
        return string.Empty;
    }

    [GeneratedRegex(@"row[s]?\s+`?(?<id>\d{4,})`?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"incident\s+`?(?<id>\d{3,})`?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IncidentPattern();
}
