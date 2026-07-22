using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace pizzad;

public sealed partial class EngineDatabase : IIncidentEventStateLinkShadowStore
{
    public async Task<IncidentEventStateLinkShadowRunResult> AppendIncidentEventStateLinkShadowRunAsync(
        IncidentEventStateLinkLedgerEntry entry,
        IncidentEventStateLinkProjection projection,
        CancellationToken ct)
    {
        var entryValidation = IncidentEventStateLinkContractValidator.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new ArgumentException(string.Join("; ", entryValidation.Errors), nameof(entry));
        var projectionValidation = IncidentEventStateLinkContractValidator.ValidateProjection(projection);
        if (!projectionValidation.IsValid)
            throw new ArgumentException(string.Join("; ", projectionValidation.Errors), nameof(projection));
        if (!projection.LedgerEntryIds.Contains(entry.LedgerEntryId, StringComparer.Ordinal))
            throw new ArgumentException("link projection does not reference the appended ledger entry", nameof(projection));
        if (!string.Equals(entry.RunId, projection.RunId, StringComparison.Ordinal))
            throw new ArgumentException("link ledger entry and projection belong to different runs", nameof(projection));

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction();

        var entryPayload = JsonSerializer.Serialize(entry, EngineConfig.JsonOptions());
        var entryHash = ContentHash(entryPayload);
        await using var entryCommand = connection.CreateCommand();
        entryCommand.Transaction = transaction;
        entryCommand.CommandText = """
            INSERT INTO incident_event_state_link_shadow_ledger (
                run_id,
                ledger_entry_id,
                recorded_at_utc,
                bundle_id,
                proposal_id,
                new_observation_id,
                transition_outcome,
                projection_event_id,
                content_hash,
                payload_json
            ) VALUES (
                $run_id,
                $ledger_entry_id,
                $recorded_at_utc,
                $bundle_id,
                $proposal_id,
                $new_observation_id,
                $transition_outcome,
                $projection_event_id,
                $content_hash,
                $payload_json
            );
            SELECT last_insert_rowid();
            """;
        entryCommand.Parameters.AddWithValue("$run_id", entry.RunId);
        entryCommand.Parameters.AddWithValue("$ledger_entry_id", entry.LedgerEntryId);
        entryCommand.Parameters.AddWithValue("$recorded_at_utc", entry.RecordedAtUtc.UtcDateTime.ToString("O"));
        entryCommand.Parameters.AddWithValue("$bundle_id", entry.Bundle.BundleId);
        entryCommand.Parameters.AddWithValue("$proposal_id", entry.Proposal.ProposalId);
        entryCommand.Parameters.AddWithValue("$new_observation_id", entry.NewObservationId);
        entryCommand.Parameters.AddWithValue("$transition_outcome", entry.Transition.Outcome.ToString());
        entryCommand.Parameters.AddWithValue("$projection_event_id", entry.Transition.ProjectionEventId);
        entryCommand.Parameters.AddWithValue("$content_hash", entryHash);
        entryCommand.Parameters.AddWithValue("$payload_json", entryPayload);
        var entrySequence = Convert.ToInt64(await entryCommand.ExecuteScalarAsync(ct));

        await ValidateLinkProjectionLedgerReferencesAsync(connection, transaction, projection, ct);

        var projectionPayload = JsonSerializer.Serialize(projection, EngineConfig.JsonOptions());
        var projectionHash = ContentHash(projectionPayload);
        await using var projectionCommand = connection.CreateCommand();
        projectionCommand.Transaction = transaction;
        projectionCommand.CommandText = """
            INSERT INTO incident_event_state_link_shadow_projections (
                run_id,
                projection_id,
                generated_at_utc,
                content_hash,
                payload_json
            ) VALUES (
                $run_id,
                $projection_id,
                $generated_at_utc,
                $content_hash,
                $payload_json
            );
            SELECT last_insert_rowid();
            """;
        projectionCommand.Parameters.AddWithValue("$run_id", projection.RunId);
        projectionCommand.Parameters.AddWithValue("$projection_id", projection.ProjectionId);
        projectionCommand.Parameters.AddWithValue("$generated_at_utc", projection.GeneratedAtUtc.UtcDateTime.ToString("O"));
        projectionCommand.Parameters.AddWithValue("$content_hash", projectionHash);
        projectionCommand.Parameters.AddWithValue("$payload_json", projectionPayload);
        var projectionSequence = Convert.ToInt64(await projectionCommand.ExecuteScalarAsync(ct));

        await transaction.CommitAsync(ct);
        return new IncidentEventStateLinkShadowRunResult(
            new IncidentEventStateStoredLinkLedgerEntry(entrySequence, entryHash, entry),
            new IncidentEventStateStoredLinkProjection(projectionSequence, projectionHash, projection));
    }

    public async Task<IReadOnlyList<IncidentEventStateStoredLinkLedgerEntry>> ListIncidentEventStateLinkShadowLedgerEntriesAsync(
        string runId,
        long afterSequence,
        int limit,
        CancellationToken ct)
    {
        var rows = new List<IncidentEventStateStoredLinkLedgerEntry>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_event_state_link_shadow_ledger
            WHERE run_id=$run_id AND sequence > $after_sequence
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var contentHash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("link ledger entry", sequence, payload, contentHash);
            var entry = JsonSerializer.Deserialize<IncidentEventStateLinkLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident event-state link ledger entry {sequence} has an empty payload.");
            rows.Add(new IncidentEventStateStoredLinkLedgerEntry(sequence, contentHash, entry));
        }

        return rows;
    }

    public async Task<IncidentEventStateStoredLinkProjection?> GetLatestIncidentEventStateLinkShadowProjectionAsync(
        string runId,
        CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_event_state_link_shadow_projections
            WHERE run_id=$run_id
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var sequence = reader.GetInt64(0);
        var contentHash = reader.GetString(1);
        var payload = reader.GetString(2);
        VerifyContentHash("link projection", sequence, payload, contentHash);
        var projection = JsonSerializer.Deserialize<IncidentEventStateLinkProjection>(payload, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException($"Incident event-state link projection {sequence} has an empty payload.");
        return new IncidentEventStateStoredLinkProjection(sequence, contentHash, projection);
    }

    public async Task<IncidentEventStateStoredLinkLedgerEntry?> GetLatestIncidentEventStateLinkShadowLedgerEntryAsync(
        string runId,
        CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_event_state_link_shadow_ledger
            WHERE run_id=$run_id
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var sequence = reader.GetInt64(0);
        var contentHash = reader.GetString(1);
        var payload = reader.GetString(2);
        VerifyContentHash("link ledger entry", sequence, payload, contentHash);
        var entry = JsonSerializer.Deserialize<IncidentEventStateLinkLedgerEntry>(payload, EngineConfig.JsonOptions())
                    ?? throw new InvalidDataException($"Incident event-state link ledger entry {sequence} has an empty payload.");
        return new IncidentEventStateStoredLinkLedgerEntry(sequence, contentHash, entry);
    }

    private static async Task ValidateLinkProjectionLedgerReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IncidentEventStateLinkProjection projection,
        CancellationToken ct)
    {
        foreach (var ledgerEntryId in projection.LedgerEntryIds.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence, content_hash, payload_json
                FROM incident_event_state_link_shadow_ledger
                WHERE run_id=$run_id AND ledger_entry_id=$ledger_entry_id;
                """;
            command.Parameters.AddWithValue("$run_id", projection.RunId);
            command.Parameters.AddWithValue("$ledger_entry_id", ledgerEntryId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Incident event-state link ledger entry '{ledgerEntryId}' does not exist.");
            var sequence = reader.GetInt64(0);
            var contentHash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("link ledger entry", sequence, payload, contentHash);
        }
    }
}
