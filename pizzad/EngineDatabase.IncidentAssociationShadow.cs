using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace pizzad;

public sealed partial class EngineDatabase : IIncidentAssociationShadowStore
{
    public async Task<IncidentAssociationShadowRunResult> AppendIncidentAssociationShadowRunAsync(
        IncidentAssociationLedgerEntry entry,
        IncidentAssociationProjection projection,
        CancellationToken ct)
    {
        var entryValidation = IncidentAssociationContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new ArgumentException(string.Join("; ", entryValidation.Errors), nameof(entry));
        var projectionValidation = IncidentAssociationContract.ValidateProjection(projection);
        if (!projectionValidation.IsValid)
            throw new ArgumentException(string.Join("; ", projectionValidation.Errors), nameof(projection));
        if (!projection.LedgerEntryIds.Contains(entry.LedgerEntryId, StringComparer.Ordinal))
            throw new ArgumentException("association projection does not reference the appended ledger entry", nameof(projection));
        if (entry.RunId != projection.RunId)
            throw new ArgumentException("association ledger entry and projection belong to different runs", nameof(projection));

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction();
        var entryPayload = JsonSerializer.Serialize(entry, EngineConfig.JsonOptions());
        var entryHash = ContentHash(entryPayload);
        await using var entryCommand = connection.CreateCommand();
        entryCommand.Transaction = transaction;
        entryCommand.CommandText = """
            INSERT INTO incident_association_shadow_ledger (
                run_id, ledger_entry_id, recorded_at_utc, bundle_id, proposal_id,
                new_observation_id, transition_outcome, projection_event_id, content_hash, payload_json
            ) VALUES (
                $run_id, $ledger_entry_id, $recorded_at_utc, $bundle_id, $proposal_id,
                $new_observation_id, $transition_outcome, $projection_event_id, $content_hash, $payload_json
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

        await ValidateAssociationProjectionLedgerReferencesAsync(connection, transaction, projection, ct);
        var projectionPayload = JsonSerializer.Serialize(projection, EngineConfig.JsonOptions());
        var projectionHash = ContentHash(projectionPayload);
        await using var projectionCommand = connection.CreateCommand();
        projectionCommand.Transaction = transaction;
        projectionCommand.CommandText = """
            INSERT INTO incident_association_shadow_projections (
                run_id, projection_id, generated_at_utc, content_hash, payload_json
            ) VALUES ($run_id, $projection_id, $generated_at_utc, $content_hash, $payload_json);
            SELECT last_insert_rowid();
            """;
        projectionCommand.Parameters.AddWithValue("$run_id", projection.RunId);
        projectionCommand.Parameters.AddWithValue("$projection_id", projection.ProjectionId);
        projectionCommand.Parameters.AddWithValue("$generated_at_utc", projection.GeneratedAtUtc.UtcDateTime.ToString("O"));
        projectionCommand.Parameters.AddWithValue("$content_hash", projectionHash);
        projectionCommand.Parameters.AddWithValue("$payload_json", projectionPayload);
        var projectionSequence = Convert.ToInt64(await projectionCommand.ExecuteScalarAsync(ct));
        await transaction.CommitAsync(ct);
        return new IncidentAssociationShadowRunResult(
            new IncidentAssociationStoredLedgerEntry(entrySequence, entryHash, entry),
            new IncidentAssociationStoredProjection(projectionSequence, projectionHash, projection));
    }

    public async Task<IReadOnlyList<IncidentAssociationStoredLedgerEntry>> ListIncidentAssociationShadowLedgerEntriesAsync(
        string runId,
        long afterSequence,
        int limit,
        CancellationToken ct)
    {
        var rows = new List<IncidentAssociationStoredLedgerEntry>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_association_shadow_ledger
            WHERE run_id=$run_id AND sequence > $after_sequence
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
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

    public async Task<IncidentAssociationStoredProjection?> GetLatestIncidentAssociationShadowProjectionAsync(string runId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_association_shadow_projections
            WHERE run_id=$run_id
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var sequence = reader.GetInt64(0);
        var hash = reader.GetString(1);
        var payload = reader.GetString(2);
        VerifyContentHash("association projection", sequence, payload, hash);
        var projection = JsonSerializer.Deserialize<IncidentAssociationProjection>(payload, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException($"Incident association projection {sequence} has an empty payload.");
        return new IncidentAssociationStoredProjection(sequence, hash, projection);
    }

    public async Task<IncidentAssociationStoredLedgerEntry?> GetLatestIncidentAssociationShadowLedgerEntryAsync(string runId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_association_shadow_ledger
            WHERE run_id=$run_id
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var sequence = reader.GetInt64(0);
        var hash = reader.GetString(1);
        var payload = reader.GetString(2);
        VerifyContentHash("association ledger entry", sequence, payload, hash);
        var entry = JsonSerializer.Deserialize<IncidentAssociationLedgerEntry>(payload, EngineConfig.JsonOptions())
                    ?? throw new InvalidDataException($"Incident association ledger entry {sequence} has an empty payload.");
        return new IncidentAssociationStoredLedgerEntry(sequence, hash, entry);
    }

    private static async Task ValidateAssociationProjectionLedgerReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IncidentAssociationProjection projection,
        CancellationToken ct)
    {
        foreach (var ledgerEntryId in projection.LedgerEntryIds.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence, content_hash, payload_json
                FROM incident_association_shadow_ledger
                WHERE run_id=$run_id AND ledger_entry_id=$ledger_entry_id;
                """;
            command.Parameters.AddWithValue("$run_id", projection.RunId);
            command.Parameters.AddWithValue("$ledger_entry_id", ledgerEntryId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Incident association ledger entry '{ledgerEntryId}' does not exist.");
            VerifyContentHash("association ledger entry", reader.GetInt64(0), reader.GetString(2), reader.GetString(1));
        }
    }
}
