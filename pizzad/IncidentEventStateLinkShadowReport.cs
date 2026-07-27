using System.Globalization;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentEventStateLinkShadowRunSummaryDto(
    string RunId,
    int Attempts,
    DateTime? FirstAttemptUtc,
    DateTime? LastAttemptUtc,
    bool IsConfiguredRun);

public sealed record IncidentEventStateLinkShadowTotalsDto(
    int Attempts,
    int CandidateBackedAttempts,
    int ProposedLinks,
    int AdmittedLinks,
    int Abstentions,
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

public sealed record IncidentEventStateLinkShadowAttemptDto(
    long Sequence,
    DateTime RecordedAtUtc,
    long CallId,
    int CandidateCount,
    string Decision,
    string Outcome,
    string ModelIdentity,
    string ProjectionEventId,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<string> ValidationErrors,
    IReadOnlyList<string> UnresolvedQuestions,
    long ProposerMilliseconds,
    string ProposerError);

public sealed record IncidentEventStateLinkShadowProjectedEventDto(
    string ProjectionEventId,
    int ObservationCount);

public sealed record IncidentEventStateLinkShadowReportDto(
    bool Enabled,
    string ConfiguredRunId,
    string SelectedRunId,
    IReadOnlyList<IncidentEventStateLinkShadowRunSummaryDto> Runs,
    IncidentEventStateLinkShadowTotalsDto Totals,
    IReadOnlyList<IncidentEventStateLinkShadowAttemptDto> Attempts,
    IReadOnlyList<IncidentEventStateLinkShadowProjectedEventDto> ProjectedEvents);

public sealed partial class EngineDatabase
{
    public async Task<IncidentEventStateLinkShadowReportDto> GetIncidentEventStateLinkShadowReportAsync(
        bool enabled,
        string configuredRunId,
        string? requestedRunId,
        int limit,
        CancellationToken ct)
    {
        configuredRunId = configuredRunId.Trim();
        var runs = await ListLinkShadowRunsAsync(configuredRunId, ct);
        var selectedRunId = string.IsNullOrWhiteSpace(requestedRunId)
            ? configuredRunId
            : requestedRunId.Trim();
        if (string.IsNullOrWhiteSpace(selectedRunId))
            selectedRunId = runs.FirstOrDefault()?.RunId ?? string.Empty;

        if (string.IsNullOrWhiteSpace(selectedRunId))
        {
            return new IncidentEventStateLinkShadowReportDto(
                enabled,
                configuredRunId,
                string.Empty,
                runs,
                EmptyLinkShadowTotals(),
                [],
                []);
        }

        var attempts = await ListLinkShadowAttemptsAsync(selectedRunId, limit, ct);
        var projection = await GetLatestIncidentEventStateLinkShadowProjectionAsync(selectedRunId, ct);
        var projectedEvents = (projection?.Projection.Events ?? [])
            .Select(item => new IncidentEventStateLinkShadowProjectedEventDto(item.ProjectionEventId, item.ObservationIds.Count))
            .OrderByDescending(item => item.ObservationCount)
            .ThenBy(item => item.ProjectionEventId, StringComparer.Ordinal)
            .ToList();
        var totals = await GetLinkShadowTotalsAsync(selectedRunId, projectedEvents, ct);
        return new IncidentEventStateLinkShadowReportDto(
            enabled,
            configuredRunId,
            selectedRunId,
            runs,
            totals,
            attempts,
            projectedEvents);
    }

    private async Task<IReadOnlyList<IncidentEventStateLinkShadowRunSummaryDto>> ListLinkShadowRunsAsync(
        string configuredRunId,
        CancellationToken ct)
    {
        var rows = new List<IncidentEventStateLinkShadowRunSummaryDto>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, COUNT(*), MIN(recorded_at_utc), MAX(recorded_at_utc), MAX(sequence)
            FROM incident_event_state_link_shadow_ledger
            GROUP BY run_id
            ORDER BY MAX(sequence) DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var runId = reader.GetString(0);
            rows.Add(new IncidentEventStateLinkShadowRunSummaryDto(
                runId,
                reader.GetInt32(1),
                ParseUtc(reader.GetString(2)),
                ParseUtc(reader.GetString(3)),
                string.Equals(runId, configuredRunId, StringComparison.Ordinal)));
        }

        if (!string.IsNullOrWhiteSpace(configuredRunId) && rows.All(row => !string.Equals(row.RunId, configuredRunId, StringComparison.Ordinal)))
            rows.Insert(0, new IncidentEventStateLinkShadowRunSummaryDto(configuredRunId, 0, null, null, true));
        return rows;
    }

    private async Task<IReadOnlyList<IncidentEventStateLinkShadowAttemptDto>> ListLinkShadowAttemptsAsync(
        string runId,
        int limit,
        CancellationToken ct)
    {
        var rows = new List<IncidentEventStateLinkShadowAttemptDto>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_event_state_link_shadow_ledger
            WHERE run_id=$run_id
            ORDER BY sequence DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var contentHash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("link ledger entry", sequence, payload, contentHash);
            var entry = JsonSerializer.Deserialize<IncidentEventStateLinkLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident event-state link ledger entry {sequence} has an empty payload.");
            var callId = entry.Bundle.Observations
                .FirstOrDefault(observation => string.Equals(observation.ObservationId, entry.NewObservationId, StringComparison.Ordinal))
                ?.CallId ?? 0;
            rows.Add(new IncidentEventStateLinkShadowAttemptDto(
                sequence,
                entry.RecordedAtUtc.UtcDateTime,
                callId,
                entry.Candidates.Count,
                entry.Proposal.Decision.ToString(),
                entry.Transition.Outcome.ToString(),
                entry.Proposal.ModelIdentity,
                entry.Transition.ProjectionEventId,
                entry.Proposal.RelationshipStatement,
                entry.Proposal.Uncertainty,
                entry.ProposalValidationErrors,
                entry.Proposal.UnresolvedQuestions,
                entry.Execution.ProposerDurationMilliseconds,
                entry.Execution.ProposerError));
        }
        return rows;
    }

    private async Task<IncidentEventStateLinkShadowTotalsDto> GetLinkShadowTotalsAsync(
        string runId,
        IReadOnlyList<IncidentEventStateLinkShadowProjectedEventDto> projectedEvents,
        CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*),
                COALESCE(SUM(CASE WHEN json_array_length(payload_json, '$.candidates') > 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN json_extract(payload_json, '$.proposal.decision') = 1 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN transition_outcome='LinkedToExistingEvent' THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN json_extract(payload_json, '$.proposal.decision') = 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN json_array_length(payload_json, '$.proposalValidationErrors') > 0 THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN COALESCE(json_extract(payload_json, '$.execution.proposerError'), '') <> '' THEN 1 ELSE 0 END), 0),
                COALESCE(AVG(json_extract(payload_json, '$.execution.proposerDurationMilliseconds')), 0),
                COALESCE(MAX(json_extract(payload_json, '$.execution.proposerDurationMilliseconds')), 0)
            FROM incident_event_state_link_shadow_ledger
            WHERE run_id=$run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var attempts = reader.GetInt32(0);
        var candidateBacked = reader.GetInt32(1);
        var proposed = reader.GetInt32(2);
        var admitted = reader.GetInt32(3);
        var abstentions = reader.GetInt32(4);
        var invalid = reader.GetInt32(5);
        var proposerErrors = reader.GetInt32(6);
        var averageMs = reader.GetDouble(7);
        var maximumMs = reader.GetInt64(8);
        await reader.DisposeAsync();

        await using var usageCommand = connection.CreateCommand();
        usageCommand.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(prompt_tokens), 0),
                   COALESCE(SUM(completion_tokens), 0),
                   COALESCE(SUM(CASE WHEN total_tokens > 0 THEN total_tokens ELSE prompt_tokens + completion_tokens END), 0)
            FROM lm_usage
            WHERE trigger_activity=$trigger;
            """;
        usageCommand.Parameters.AddWithValue("$trigger", string.Equals(runId, "legacy", StringComparison.Ordinal)
            ? "incident link shadow"
            : $"incident link shadow:{runId}");
        await using var usageReader = await usageCommand.ExecuteReaderAsync(ct);
        await usageReader.ReadAsync(ct);
        return new IncidentEventStateLinkShadowTotalsDto(
            attempts,
            candidateBacked,
            proposed,
            admitted,
            abstentions,
            invalid,
            proposerErrors,
            averageMs,
            maximumMs,
            projectedEvents.Count,
            projectedEvents.Sum(item => item.ObservationCount),
            usageReader.GetInt32(0),
            usageReader.GetInt64(1),
            usageReader.GetInt64(2),
            usageReader.GetInt64(3));
    }

    private static IncidentEventStateLinkShadowTotalsDto EmptyLinkShadowTotals() =>
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static DateTime? ParseUtc(string value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
