using System.Globalization;

namespace pizzad;

public sealed record IncidentBatchShadowRunSummaryDto(string RunId, int Batches, DateTime? FirstBatchUtc, DateTime? LastBatchUtc, bool IsConfiguredRun);

public sealed record IncidentBatchShadowTotalsDto(
    int Batches,
    int NewObservations,
    int ProposedEvents,
    int AcceptedEvents,
    int RejectedEvents,
    int NewEvents,
    int ProvisionalEvents,
    int ConfirmedMemberships,
    int ProvisionalAssociations,
    int UnresolvedObservations,
    int InvalidProposals,
    int ProposerErrors,
    double AverageProposerMilliseconds,
    long MaximumProposerMilliseconds,
    int ProjectedEvents,
    int OperatorVisibleEvents,
    int OperatorReviewEvents);

public sealed record IncidentBatchShadowAttemptDto(
    long Sequence,
    DateTime RecordedAtUtc,
    long FirstCallId,
    long LastCallId,
    int NewObservationCount,
    int CandidateCount,
    int ProposedEventCount,
    int AcceptedEventCount,
    int RejectedEventCount,
    int NewEventCount,
    int ProvisionalEventCount,
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

public sealed record IncidentBatchShadowProjectedEventDto(string ProjectionEventId, int ObservationCount, string Title, string Summary, bool OperatorVisible, bool OperatorReview);

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
        var allAttempts = entries.Select(ToAttempt).ToList();
        var projection = await GetLatestIncidentBatchProjectionAsync(selectedRunId, ct);
        var projectedEvents = (projection?.Projection.Events ?? [])
            .Select(item => new IncidentBatchShadowProjectedEventDto(item.ProjectionEventId, item.ObservationIds.Count, item.Title, item.Summary, item.OperatorVisible, item.OperatorReview))
            .OrderByDescending(item => item.OperatorVisible)
            .ThenByDescending(item => item.OperatorReview)
            .ThenByDescending(item => item.ObservationCount)
            .ThenBy(item => item.ProjectionEventId, StringComparer.Ordinal)
            .ToList();
        var totals = new IncidentBatchShadowTotalsDto(
            entries.Count,
            entries.Sum(row => row.Entry.NewObservationIds.Count),
            allAttempts.Sum(row => row.ProposedEventCount),
            allAttempts.Sum(row => row.AcceptedEventCount),
            allAttempts.Sum(row => row.RejectedEventCount),
            allAttempts.Sum(row => row.NewEventCount),
            allAttempts.Sum(row => row.ProvisionalEventCount),
            allAttempts.Sum(row => row.ConfirmedMembershipCount),
            allAttempts.Sum(row => row.ProvisionalAssociationCount),
            allAttempts.Sum(row => row.UnresolvedObservationCount),
            entries.Count(row => !IsValid(row)),
            allAttempts.Count(row => !string.IsNullOrWhiteSpace(row.ProposerError)),
            allAttempts.Count == 0 ? 0 : allAttempts.Average(row => row.ProposerMilliseconds),
            allAttempts.Count == 0 ? 0 : allAttempts.Max(row => row.ProposerMilliseconds),
            projectedEvents.Count,
            projectedEvents.Count(item => item.OperatorVisible),
            projectedEvents.Count(item => item.OperatorReview));
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
        var events = IncidentBatchContract.AcceptedEvents(entry);
        var proposedEvents = entry.Proposal.Events.Count;
        var callIds = entry.Bundle.Observations
            .Where(item => entry.NewObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
            .Select(item => item.CallId ?? 0)
            .Where(id => id > 0)
            .Order()
            .ToList();
        var proposedObservationCount = events.SelectMany(item => item.NewObservationIds).Distinct(StringComparer.Ordinal).Count();
        var relationships = (entry.RelationshipProposalValidationErrors ?? []).Count == 0
            ? entry.RelationshipProposal?.Relationships ?? []
            : [];
        var validationErrors = entry.ProposalValidationErrors
            .Concat(entry.RelationshipProposalValidationErrors ?? [])
            .ToList();
        var proposerError = string.Join("; ", new[] { entry.Execution.ProposerError, entry.RelationshipExecution?.ProposerError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return new IncidentBatchShadowAttemptDto(
            row.Sequence,
            entry.RecordedAtUtc.UtcDateTime,
            callIds.FirstOrDefault(),
            callIds.LastOrDefault(),
            entry.NewObservationIds.Count,
            entry.Candidates.Count,
            proposedEvents,
            events.Count,
            Math.Max(0, proposedEvents - events.Count),
            events.Count(IncidentBatchContract.IsOperatorVisibleNewEvent),
            events.Count(IncidentBatchContract.IsOperatorReviewEvent),
            entry.RelationshipProposal is null
                ? events.Count(item => item.Disposition == IncidentBatchEventDisposition.ConfirmedMembership)
                : relationships.Count(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership),
            entry.RelationshipProposal is null
                ? events.Count(item => item.Disposition == IncidentBatchEventDisposition.ProvisionalAssociation)
                : relationships.Count(item => item.Disposition == IncidentBatchRelationshipDisposition.ProvisionalAssociation),
            Math.Max(0, entry.NewObservationIds.Count - proposedObservationCount),
            entry.Proposal.ModelIdentity,
            entry.Proposal.PromptIdentity,
            entry.Execution.ConfigurationIdentity,
            events.Select(item => IncidentBatchProjector.BuildEvidenceTitle(
                IncidentBatchProjector.BuildEvidenceSummary(item.NewObservationEvidence))).ToList(),
            validationErrors,
            entry.Execution.ProposerDurationMilliseconds + (entry.RelationshipExecution?.ProposerDurationMilliseconds ?? 0),
            proposerError);
    }

    private static bool IsValid(IncidentBatchStoredLedgerEntry row) =>
        row.Entry.ProposalValidationErrors.Count == 0 && (row.Entry.RelationshipProposalValidationErrors ?? []).Count == 0;
    private static IncidentBatchShadowTotalsDto EmptyBatchTotals() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static DateTime? ParseBatchUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
}
