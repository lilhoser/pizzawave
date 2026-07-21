using System.Globalization;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentAssociationShadowRunSummaryDto(
    string RunId,
    int Attempts,
    DateTime? FirstAttemptUtc,
    DateTime? LastAttemptUtc,
    bool IsConfiguredRun);

public sealed record IncidentAssociationShadowTotalsDto(
    int Attempts,
    int CandidateBackedAttempts,
    int ConfirmedMemberships,
    int ProvisionalAssociations,
    int SingletonEvents,
    int InvalidProposals,
    int ProposerErrors,
    double AverageProposerMilliseconds,
    long MaximumProposerMilliseconds,
    int ProjectedEvents,
    int ProjectedObservations,
    int ModelRequests,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens);

public sealed record IncidentAssociationShadowAttemptDto(
    long Sequence,
    DateTime RecordedAtUtc,
    long CallId,
    int CandidateCount,
    int ConfirmedMembershipCount,
    int ProvisionalAssociationCount,
    string Outcome,
    string ModelIdentity,
    string ProjectionEventId,
    IReadOnlyList<string> RelationshipStatements,
    IReadOnlyList<double> Uncertainties,
    IReadOnlyList<string> ValidationErrors,
    IReadOnlyList<string> UnresolvedQuestions,
    long ProposerMilliseconds,
    string ProposerError);

public sealed record IncidentAssociationShadowProjectedEventDto(string ProjectionEventId, int ObservationCount);

public sealed record IncidentAssociationShadowReportDto(
    bool Enabled,
    string ConfiguredRunId,
    string SelectedRunId,
    IReadOnlyList<IncidentAssociationShadowRunSummaryDto> Runs,
    IncidentAssociationShadowTotalsDto Totals,
    IReadOnlyList<IncidentAssociationShadowAttemptDto> Attempts,
    IReadOnlyList<IncidentAssociationShadowProjectedEventDto> ProjectedEvents);

public sealed partial class EngineDatabase
{
    public async Task<IncidentAssociationShadowReportDto> GetIncidentAssociationShadowReportAsync(
        bool enabled,
        string configuredRunId,
        string? requestedRunId,
        int limit,
        CancellationToken ct)
    {
        configuredRunId = configuredRunId.Trim();
        var runs = await ListAssociationShadowRunsAsync(configuredRunId, ct);
        var selectedRunId = string.IsNullOrWhiteSpace(requestedRunId) ? configuredRunId : requestedRunId.Trim();
        if (string.IsNullOrWhiteSpace(selectedRunId))
            selectedRunId = runs.FirstOrDefault()?.RunId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(selectedRunId))
            return new IncidentAssociationShadowReportDto(enabled, configuredRunId, string.Empty, runs, EmptyTotals(), [], []);

        var allEntries = await ListAllAssociationShadowEntriesAsync(selectedRunId, ct);
        var attempts = allEntries.OrderByDescending(row => row.Sequence).Take(Math.Clamp(limit, 1, 500)).Select(ToAttempt).ToList();
        var projection = await GetLatestIncidentAssociationShadowProjectionAsync(selectedRunId, ct);
        var projectedEvents = (projection?.Projection.Events ?? [])
            .Select(item => new IncidentAssociationShadowProjectedEventDto(item.ProjectionEventId, item.ObservationIds.Count))
            .OrderByDescending(item => item.ObservationCount)
            .ThenBy(item => item.ProjectionEventId, StringComparer.Ordinal)
            .ToList();
        var usage = await GetAssociationShadowUsageAsync(selectedRunId, ct);
        var totals = new IncidentAssociationShadowTotalsDto(
            allEntries.Count,
            allEntries.Count(row => row.Entry.Candidates.Count > 0),
            allEntries.Sum(row => row.Entry.ProposalValidationErrors.Count == 0
                ? row.Entry.Proposal.Relationships.Count(item => item.Disposition == IncidentAssociationDisposition.ConfirmedMembership)
                : 0),
            allEntries.Sum(row => row.Entry.ProposalValidationErrors.Count == 0
                ? row.Entry.Proposal.Relationships.Count(item => item.Disposition == IncidentAssociationDisposition.ProvisionalAssociation)
                : 0),
            allEntries.Count(row => row.Entry.Transition.Outcome == IncidentAssociationTransitionOutcome.SingletonCreated),
            allEntries.Count(row => row.Entry.ProposalValidationErrors.Count > 0),
            allEntries.Count(row => !string.IsNullOrWhiteSpace(row.Entry.Execution.ProposerError)),
            allEntries.Count == 0 ? 0 : allEntries.Average(row => row.Entry.Execution.ProposerDurationMilliseconds),
            allEntries.Count == 0 ? 0 : allEntries.Max(row => row.Entry.Execution.ProposerDurationMilliseconds),
            projectedEvents.Count,
            projectedEvents.Sum(item => item.ObservationCount),
            usage.Requests,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens);
        return new IncidentAssociationShadowReportDto(enabled, configuredRunId, selectedRunId, runs, totals, attempts, projectedEvents);
    }

    private async Task<IReadOnlyList<IncidentAssociationShadowRunSummaryDto>> ListAssociationShadowRunsAsync(string configuredRunId, CancellationToken ct)
    {
        var rows = new List<IncidentAssociationShadowRunSummaryDto>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, COUNT(*), MIN(recorded_at_utc), MAX(recorded_at_utc), MAX(sequence)
            FROM incident_association_shadow_ledger
            GROUP BY run_id
            ORDER BY MAX(sequence) DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var runId = reader.GetString(0);
            rows.Add(new IncidentAssociationShadowRunSummaryDto(
                runId,
                reader.GetInt32(1),
                ParseAssociationUtc(reader.GetString(2)),
                ParseAssociationUtc(reader.GetString(3)),
                runId == configuredRunId));
        }
        if (!string.IsNullOrWhiteSpace(configuredRunId) && rows.All(row => row.RunId != configuredRunId))
            rows.Insert(0, new IncidentAssociationShadowRunSummaryDto(configuredRunId, 0, null, null, true));
        return rows;
    }

    private async Task<List<IncidentAssociationStoredLedgerEntry>> ListAllAssociationShadowEntriesAsync(string runId, CancellationToken ct)
    {
        var rows = new List<IncidentAssociationStoredLedgerEntry>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_association_shadow_ledger
            WHERE run_id=$run_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var hash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("association ledger entry", sequence, payload, hash);
            var entry = JsonSerializer.Deserialize<IncidentAssociationLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident association ledger entry {sequence} has an empty payload.");
            rows.Add(new IncidentAssociationStoredLedgerEntry(sequence, hash, entry));
        }
        return rows;
    }

    private async Task<(int Requests, long PromptTokens, long CompletionTokens, long TotalTokens)> GetAssociationShadowUsageAsync(string runId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*), COALESCE(SUM(prompt_tokens), 0), COALESCE(SUM(completion_tokens), 0),
                   COALESCE(SUM(CASE WHEN total_tokens > 0 THEN total_tokens ELSE prompt_tokens + completion_tokens END), 0)
            FROM lm_usage
            WHERE trigger_activity=$trigger;
            """;
        command.Parameters.AddWithValue("$trigger", $"incident association shadow:{runId}");
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static IncidentAssociationShadowAttemptDto ToAttempt(IncidentAssociationStoredLedgerEntry row)
    {
        var entry = row.Entry;
        var relationships = entry.ProposalValidationErrors.Count == 0 ? entry.Proposal.Relationships : [];
        return new IncidentAssociationShadowAttemptDto(
            row.Sequence,
            entry.RecordedAtUtc.UtcDateTime,
            entry.Bundle.Observations.FirstOrDefault(item => item.ObservationId == entry.NewObservationId)?.CallId ?? 0,
            entry.Candidates.Count,
            relationships.Count(item => item.Disposition == IncidentAssociationDisposition.ConfirmedMembership),
            relationships.Count(item => item.Disposition == IncidentAssociationDisposition.ProvisionalAssociation),
            entry.Transition.Outcome.ToString(),
            entry.Proposal.ModelIdentity,
            entry.Transition.ProjectionEventId,
            relationships.Select(item => item.RelationshipStatement).ToList(),
            relationships.Select(item => item.Uncertainty).ToList(),
            entry.ProposalValidationErrors,
            relationships.SelectMany(item => item.UnresolvedQuestions).Distinct(StringComparer.Ordinal).ToList(),
            entry.Execution.ProposerDurationMilliseconds,
            entry.Execution.ProposerError);
    }

    private static IncidentAssociationShadowTotalsDto EmptyTotals() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static DateTime? ParseAssociationUtc(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
}
