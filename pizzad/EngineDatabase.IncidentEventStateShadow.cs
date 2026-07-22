using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed partial class EngineDatabase : IIncidentEventStateShadowStore
{
    public async Task<IncidentEventStateStoredLedgerEntry> AppendIncidentEventStateShadowLedgerEntryAsync(
        IncidentEventStateLedgerEntry entry,
        CancellationToken ct)
    {
        var validation = IncidentEventStateContractValidator.ValidateLedgerEntry(entry);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(entry));

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction();
        await LoadIncidentEventStateLedgerReferencesAsync(
            connection,
            transaction,
            entry.SupersedesLedgerEntryIds,
            ct);

        var payload = JsonSerializer.Serialize(entry, EngineConfig.JsonOptions());
        var contentHash = ContentHash(payload);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO incident_event_state_shadow_ledger (
                ledger_entry_id,
                recorded_at_utc,
                bundle_id,
                proposal_id,
                critique_id,
                content_hash,
                payload_json
            ) VALUES (
                $ledger_entry_id,
                $recorded_at_utc,
                $bundle_id,
                $proposal_id,
                $critique_id,
                $content_hash,
                $payload_json
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$ledger_entry_id", entry.LedgerEntryId);
        command.Parameters.AddWithValue("$recorded_at_utc", entry.RecordedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$bundle_id", entry.Bundle.BundleId);
        command.Parameters.AddWithValue("$proposal_id", entry.Proposal.ProposalId);
        command.Parameters.AddWithValue("$critique_id", entry.Critique.CritiqueId);
        command.Parameters.AddWithValue("$content_hash", contentHash);
        command.Parameters.AddWithValue("$payload_json", payload);
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        await transaction.CommitAsync(ct);

        return new IncidentEventStateStoredLedgerEntry(sequence, contentHash, entry);
    }

    public async Task<IReadOnlyList<IncidentEventStateStoredLedgerEntry>> ListIncidentEventStateShadowLedgerEntriesAsync(
        long afterSequence,
        int limit,
        CancellationToken ct)
    {
        var rows = new List<IncidentEventStateStoredLedgerEntry>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_event_state_shadow_ledger
            WHERE sequence > $after_sequence
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after_sequence", Math.Max(0, afterSequence));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var contentHash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("ledger entry", sequence, payload, contentHash);
            var entry = JsonSerializer.Deserialize<IncidentEventStateLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident event-state ledger entry {sequence} has an empty payload.");
            rows.Add(new IncidentEventStateStoredLedgerEntry(sequence, contentHash, entry));
        }

        return rows;
    }

    public async Task<IncidentEventStateStoredProjection> AppendIncidentEventStateShadowProjectionAsync(
        IncidentEventStateProjection projection,
        CancellationToken ct)
    {
        var validation = IncidentEventStateContractValidator.ValidateProjection(projection);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(projection));

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction();
        var sourceEntries = await LoadIncidentEventStateLedgerReferencesAsync(
            connection,
            transaction,
            projection.LedgerEntryIds,
            ct);
        validation = IncidentEventStateContractValidator.ValidateProjection(projection, sourceEntries);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(projection));

        var payload = JsonSerializer.Serialize(projection, EngineConfig.JsonOptions());
        var contentHash = ContentHash(payload);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO incident_event_state_shadow_projections (
                projection_id,
                generated_at_utc,
                content_hash,
                payload_json
            ) VALUES (
                $projection_id,
                $generated_at_utc,
                $content_hash,
                $payload_json
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$projection_id", projection.ProjectionId);
        command.Parameters.AddWithValue("$generated_at_utc", projection.GeneratedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$content_hash", contentHash);
        command.Parameters.AddWithValue("$payload_json", payload);
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        await transaction.CommitAsync(ct);

        return new IncidentEventStateStoredProjection(sequence, contentHash, projection);
    }

    public async Task<IncidentEventStateStoredProjection?> GetLatestIncidentEventStateShadowProjectionAsync(
        CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_event_state_shadow_projections
            ORDER BY sequence DESC
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var sequence = reader.GetInt64(0);
        var contentHash = reader.GetString(1);
        var payload = reader.GetString(2);
        VerifyContentHash("projection", sequence, payload, contentHash);
        var projection = JsonSerializer.Deserialize<IncidentEventStateProjection>(payload, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException($"Incident event-state projection {sequence} has an empty payload.");
        return new IncidentEventStateStoredProjection(sequence, contentHash, projection);
    }

    private static async Task<IReadOnlyList<IncidentEventStateLedgerEntry>> LoadIncidentEventStateLedgerReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> ledgerEntryIds,
        CancellationToken ct)
    {
        var entries = new List<IncidentEventStateLedgerEntry>();
        if (ledgerEntryIds.Count == 0)
            return entries;

        foreach (var ledgerEntryId in ledgerEntryIds.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence, content_hash, payload_json
                FROM incident_event_state_shadow_ledger
                WHERE ledger_entry_id=$ledger_entry_id;
                """;
            command.Parameters.AddWithValue("$ledger_entry_id", ledgerEntryId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Incident event-state ledger entry '{ledgerEntryId}' does not exist.");
            var sequence = reader.GetInt64(0);
            var contentHash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("ledger entry", sequence, payload, contentHash);
            entries.Add(JsonSerializer.Deserialize<IncidentEventStateLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident event-state ledger entry {sequence} has an empty payload."));
        }

        return entries;
    }

    private static string ContentHash(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static void VerifyContentHash(
        string recordType,
        long sequence,
        string payload,
        string expectedHash)
    {
        var actualHash = ContentHash(payload);
        if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Incident event-state {recordType} {sequence} failed content-integrity verification.");
        }
    }

    private const string IncidentEventStateShadowSchemaSql = """
        CREATE TABLE IF NOT EXISTS incident_event_state_shadow_ledger (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            ledger_entry_id TEXT NOT NULL UNIQUE,
            recorded_at_utc TEXT NOT NULL,
            bundle_id TEXT NOT NULL,
            proposal_id TEXT NOT NULL,
            critique_id TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE INDEX IF NOT EXISTS idx_incident_event_state_shadow_ledger_recorded
            ON incident_event_state_shadow_ledger(recorded_at_utc, sequence);
        CREATE INDEX IF NOT EXISTS idx_incident_event_state_shadow_ledger_bundle
            ON incident_event_state_shadow_ledger(bundle_id, sequence);

        CREATE TABLE IF NOT EXISTS incident_event_state_shadow_projections (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            projection_id TEXT NOT NULL UNIQUE,
            generated_at_utc TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE INDEX IF NOT EXISTS idx_incident_event_state_shadow_projections_generated
            ON incident_event_state_shadow_projections(generated_at_utc, sequence);

        CREATE TABLE IF NOT EXISTS incident_event_state_link_shadow_ledger (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL DEFAULT 'legacy',
            ledger_entry_id TEXT NOT NULL UNIQUE,
            recorded_at_utc TEXT NOT NULL,
            bundle_id TEXT NOT NULL,
            proposal_id TEXT NOT NULL,
            new_observation_id TEXT NOT NULL,
            transition_outcome TEXT NOT NULL,
            projection_event_id TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE INDEX IF NOT EXISTS idx_incident_event_state_link_shadow_ledger_recorded
            ON incident_event_state_link_shadow_ledger(recorded_at_utc, sequence);
        CREATE INDEX IF NOT EXISTS idx_incident_event_state_link_shadow_ledger_observation
            ON incident_event_state_link_shadow_ledger(new_observation_id, sequence);
        CREATE TABLE IF NOT EXISTS incident_event_state_link_shadow_projections (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL DEFAULT 'legacy',
            projection_id TEXT NOT NULL UNIQUE,
            generated_at_utc TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE INDEX IF NOT EXISTS idx_incident_event_state_link_shadow_projections_generated
            ON incident_event_state_link_shadow_projections(generated_at_utc, sequence);

        CREATE TABLE IF NOT EXISTS incident_association_shadow_ledger (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            ledger_entry_id TEXT NOT NULL UNIQUE,
            recorded_at_utc TEXT NOT NULL,
            bundle_id TEXT NOT NULL,
            proposal_id TEXT NOT NULL,
            new_observation_id TEXT NOT NULL,
            transition_outcome TEXT NOT NULL,
            projection_event_id TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE UNIQUE INDEX IF NOT EXISTS idx_incident_association_shadow_run_observation
            ON incident_association_shadow_ledger(run_id, new_observation_id);
        CREATE INDEX IF NOT EXISTS idx_incident_association_shadow_run_sequence
            ON incident_association_shadow_ledger(run_id, sequence);

        CREATE TABLE IF NOT EXISTS incident_association_shadow_projections (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            projection_id TEXT NOT NULL UNIQUE,
            generated_at_utc TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE INDEX IF NOT EXISTS idx_incident_association_shadow_projection_run_sequence
            ON incident_association_shadow_projections(run_id, sequence);

        CREATE TABLE IF NOT EXISTS incident_batch_constructor_shadow_ledger (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            ledger_entry_id TEXT NOT NULL UNIQUE,
            recorded_at_utc TEXT NOT NULL,
            bundle_id TEXT NOT NULL,
            proposal_id TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE INDEX IF NOT EXISTS idx_incident_batch_constructor_shadow_run_sequence
            ON incident_batch_constructor_shadow_ledger(run_id, sequence);

        CREATE TABLE IF NOT EXISTS incident_batch_constructor_shadow_projections (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            projection_id TEXT NOT NULL UNIQUE,
            generated_at_utc TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE INDEX IF NOT EXISTS idx_incident_batch_constructor_shadow_projection_run_sequence
            ON incident_batch_constructor_shadow_projections(run_id, sequence);

        CREATE TABLE IF NOT EXISTS incident_association_review_ledger (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            review_entry_id TEXT NOT NULL UNIQUE,
            recorded_at_utc TEXT NOT NULL,
            proposal_key TEXT NOT NULL,
            run_id TEXT NOT NULL,
            projection_event_id TEXT NOT NULL,
            action TEXT NOT NULL,
            anchor_incident_id INTEGER NOT NULL DEFAULT 0,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );

        CREATE INDEX IF NOT EXISTS idx_incident_association_review_proposal
            ON incident_association_review_ledger(proposal_key, sequence);
        CREATE INDEX IF NOT EXISTS idx_incident_association_review_recorded
            ON incident_association_review_ledger(recorded_at_utc, sequence);

        CREATE TRIGGER IF NOT EXISTS incident_event_state_shadow_ledger_no_update
        BEFORE UPDATE ON incident_event_state_shadow_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident event-state shadow ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_event_state_shadow_ledger_no_delete
        BEFORE DELETE ON incident_event_state_shadow_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident event-state shadow ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_event_state_shadow_projections_no_update
        BEFORE UPDATE ON incident_event_state_shadow_projections
        BEGIN
            SELECT RAISE(ABORT, 'incident event-state shadow projections are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_event_state_shadow_projections_no_delete
        BEFORE DELETE ON incident_event_state_shadow_projections
        BEGIN
            SELECT RAISE(ABORT, 'incident event-state shadow projections are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_event_state_link_shadow_ledger_no_update
        BEFORE UPDATE ON incident_event_state_link_shadow_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident event-state link shadow ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_event_state_link_shadow_ledger_no_delete
        BEFORE DELETE ON incident_event_state_link_shadow_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident event-state link shadow ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_event_state_link_shadow_projections_no_update
        BEFORE UPDATE ON incident_event_state_link_shadow_projections
        BEGIN
            SELECT RAISE(ABORT, 'incident event-state link shadow projections are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_event_state_link_shadow_projections_no_delete
        BEFORE DELETE ON incident_event_state_link_shadow_projections
        BEGIN
            SELECT RAISE(ABORT, 'incident event-state link shadow projections are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_association_shadow_ledger_no_update
        BEFORE UPDATE ON incident_association_shadow_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident association shadow ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_association_shadow_ledger_no_delete
        BEFORE DELETE ON incident_association_shadow_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident association shadow ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_association_shadow_projections_no_update
        BEFORE UPDATE ON incident_association_shadow_projections
        BEGIN
            SELECT RAISE(ABORT, 'incident association shadow projections are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_association_shadow_projections_no_delete
        BEFORE DELETE ON incident_association_shadow_projections
        BEGIN
            SELECT RAISE(ABORT, 'incident association shadow projections are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_batch_constructor_shadow_ledger_no_update
        BEFORE UPDATE ON incident_batch_constructor_shadow_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident batch constructor shadow ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_batch_constructor_shadow_ledger_no_delete
        BEFORE DELETE ON incident_batch_constructor_shadow_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident batch constructor shadow ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_batch_constructor_shadow_projections_no_update
        BEFORE UPDATE ON incident_batch_constructor_shadow_projections
        BEGIN
            SELECT RAISE(ABORT, 'incident batch constructor shadow projections are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_batch_constructor_shadow_projections_no_delete
        BEFORE DELETE ON incident_batch_constructor_shadow_projections
        BEGIN
            SELECT RAISE(ABORT, 'incident batch constructor shadow projections are append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_association_review_ledger_no_update
        BEFORE UPDATE ON incident_association_review_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident association review ledger is append-only');
        END;

        CREATE TRIGGER IF NOT EXISTS incident_association_review_ledger_no_delete
        BEFORE DELETE ON incident_association_review_ledger
        BEGIN
            SELECT RAISE(ABORT, 'incident association review ledger is append-only');
        END;
        """;
}
