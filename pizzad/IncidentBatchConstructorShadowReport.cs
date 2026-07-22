using System.Globalization;

namespace pizzad;

public sealed record IncidentBatchShadowRunSummaryDto(string RunId, int Batches, DateTime? FirstBatchUtc, DateTime? LastBatchUtc, bool IsConfiguredRun);

public sealed record IncidentBatchShadowTotalsDto(
    int Batches,
    int NewObservations,
    int ProposedEvents,
    int NewEvents,
    int ConfirmedMemberships,
    int ProvisionalAssociations,
    int UnresolvedObservations,
    int InvalidProposals,
    int ProposerErrors,
    double AverageProposerMilliseconds,
    long MaximumProposerMilliseconds,
    int ProjectedEvents,
    int OperatorVisibleEvents);

public sealed record IncidentBatchShadowAttemptDto(
    long Sequence,
    DateTime RecordedAtUtc,
    long FirstCallId,
    long LastCallId,
    int NewObservationCount,
    int CandidateCount,
    int ProposedEventCount,
    int NewEventCount,
    int ConfirmedMembershipCount,
    int ProvisionalAssociationCount,
    int UnresolvedObservationCount,
    string ModelIdentity,
    string PromptIdentity,
    string ConfigurationIdentity,
    IReadOnlyList<string> EventTitles,
    IReadOnlyList<string> ValidationErrors,
    long ProposerMilliseconds,
    string ProposerError);

public sealed record IncidentBatchShadowProjectedEventDto(string ProjectionEventId, int ObservationCount, string Title, string Summary, bool OperatorVisible);

public sealed record IncidentBatchShadowReportDto(
    bool Enabled,
    string ConfiguredRunId,
    string SelectedRunId,
    IReadOnlyList<IncidentBatchShadowRunSummaryDto> Runs,
    IncidentBatchShadowTotalsDto Totals,
    IReadOnlyList<IncidentBatchShadowAttemptDto> Attempts,
    IReadOnlyList<IncidentBatchShadowProjectedEventDto> ProjectedEvents);

public sealed partial class EngineDatabase
{
    public async Task<IncidentBatchShadowReportDto> GetIncidentBatchShadowReportAsync(
        bool enabled,
        string configuredRunId,
        string? requestedRunId,
        int limit,
        CancellationToken ct)
    {
        configuredRunId = configuredRunId.Trim();
        var runs = await ListIncidentBatchRunsAsync(configuredRunId, ct);
        var selectedRunId = string.IsNullOrWhiteSpace(requestedRunId) ? configuredRunId : requestedRunId.Trim();
        if (string.IsNullOrWhiteSpace(selectedRunId)) selectedRunId = runs.FirstOrDefault()?.RunId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedRunId))
            return new IncidentBatchShadowReportDto(enabled, configuredRunId, string.Empty, runs, EmptyBatchTotals(), [], []);
        var entries = await ListIncidentBatchLedgerEntriesAsync(selectedRunId, 500, ct);
        var attempts = entries.Take(Math.Clamp(limit, 1, 500)).Select(ToAttempt).ToList();
        var projection = await GetLatestIncidentBatchProjectionAsync(selectedRunId, ct);
        var projectedEvents = (projection?.Projection.Events ?? [])
            .Select(item => new IncidentBatchShadowProjectedEventDto(item.ProjectionEventId, item.ObservationIds.Count, item.Title, item.Summary, item.OperatorVisible))
            .OrderByDescending(item => item.OperatorVisible)
            .ThenByDescending(item => item.ObservationCount)
            .ThenBy(item => item.ProjectionEventId, StringComparer.Ordinal)
            .ToList();
        var totals = new IncidentBatchShadowTotalsDto(
            entries.Count,
            entries.Sum(row => row.Entry.NewObservationIds.Count),
            entries.Where(IsValid).Sum(row => row.Entry.Proposal.Events.Count),
            entries.Where(IsValid).Sum(row => row.Entry.Proposal.Events.Count(item => item.Disposition == IncidentBatchEventDisposition.NewEvent)),
            entries.Where(IsValid).Sum(row => row.Entry.Proposal.Events.Count(item => item.Disposition == IncidentBatchEventDisposition.ConfirmedMembership)),
            entries.Where(IsValid).Sum(row => row.Entry.Proposal.Events.Count(item => item.Disposition == IncidentBatchEventDisposition.ProvisionalAssociation)),
            entries.Sum(row => ToAttempt(row).UnresolvedObservationCount),
            entries.Count(row => !IsValid(row)),
            entries.Count(row => !string.IsNullOrWhiteSpace(row.Entry.Execution.ProposerError)),
            entries.Count == 0 ? 0 : entries.Average(row => row.Entry.Execution.ProposerDurationMilliseconds),
            entries.Count == 0 ? 0 : entries.Max(row => row.Entry.Execution.ProposerDurationMilliseconds),
            projectedEvents.Count,
            projectedEvents.Count(item => item.OperatorVisible));
        return new IncidentBatchShadowReportDto(enabled, configuredRunId, selectedRunId, runs, totals, attempts, projectedEvents);
    }

    private async Task<IReadOnlyList<IncidentBatchShadowRunSummaryDto>> ListIncidentBatchRunsAsync(string configuredRunId, CancellationToken ct)
    {
        var rows = new List<IncidentBatchShadowRunSummaryDto>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, COUNT(*), MIN(recorded_at_utc), MAX(recorded_at_utc), MAX(sequence)
            FROM incident_batch_constructor_shadow_ledger
            GROUP BY run_id
            ORDER BY MAX(sequence) DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var runId = reader.GetString(0);
            rows.Add(new IncidentBatchShadowRunSummaryDto(runId, reader.GetInt32(1), ParseBatchUtc(reader.GetString(2)), ParseBatchUtc(reader.GetString(3)), runId == configuredRunId));
        }
        if (!string.IsNullOrWhiteSpace(configuredRunId) && rows.All(row => row.RunId != configuredRunId))
            rows.Insert(0, new IncidentBatchShadowRunSummaryDto(configuredRunId, 0, null, null, true));
        return rows;
    }

    private static IncidentBatchShadowAttemptDto ToAttempt(IncidentBatchStoredLedgerEntry row)
    {
        var entry = row.Entry;
        var events = IsValid(row) ? entry.Proposal.Events : [];
        var callIds = entry.Bundle.Observations
            .Where(item => entry.NewObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
            .Select(item => item.CallId ?? 0)
            .Where(id => id > 0)
            .Order()
            .ToList();
        var proposedObservationCount = events.SelectMany(item => item.NewObservationIds).Distinct(StringComparer.Ordinal).Count();
        return new IncidentBatchShadowAttemptDto(
            row.Sequence,
            entry.RecordedAtUtc.UtcDateTime,
            callIds.FirstOrDefault(),
            callIds.LastOrDefault(),
            entry.NewObservationIds.Count,
            entry.Candidates.Count,
            events.Count,
            events.Count(item => item.Disposition == IncidentBatchEventDisposition.NewEvent),
            events.Count(item => item.Disposition == IncidentBatchEventDisposition.ConfirmedMembership),
            events.Count(item => item.Disposition == IncidentBatchEventDisposition.ProvisionalAssociation),
            Math.Max(0, entry.NewObservationIds.Count - proposedObservationCount),
            entry.Proposal.ModelIdentity,
            entry.Proposal.PromptIdentity,
            entry.Execution.ConfigurationIdentity,
            events.Select(item => item.Title).ToList(),
            entry.ProposalValidationErrors,
            entry.Execution.ProposerDurationMilliseconds,
            entry.Execution.ProposerError);
    }

    private static bool IsValid(IncidentBatchStoredLedgerEntry row) => row.Entry.ProposalValidationErrors.Count == 0;
    private static IncidentBatchShadowTotalsDto EmptyBatchTotals() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static DateTime? ParseBatchUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
}
