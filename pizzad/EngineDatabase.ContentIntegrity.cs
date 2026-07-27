using System.Security.Cryptography;
using System.Text;

namespace pizzad;

public sealed partial class EngineDatabase
{
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
                $"Incident ledger {recordType} {sequence} failed content-integrity verification.");
        }
    }

    private const string IncidentExperimentSchemaSql = """
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

        CREATE TABLE IF NOT EXISTS incident_batch_processed_calls (
            run_id TEXT NOT NULL,
            call_id INTEGER NOT NULL,
            ledger_entry_id TEXT NOT NULL,
            processed_at_utc TEXT NOT NULL,
            source_start_time INTEGER NOT NULL,
            PRIMARY KEY (run_id, call_id)
        );
        CREATE INDEX IF NOT EXISTS idx_incident_batch_processed_calls_run_time
            ON incident_batch_processed_calls(run_id, source_start_time, call_id);

        INSERT OR IGNORE INTO incident_batch_processed_calls (
            run_id, call_id, ledger_entry_id, processed_at_utc, source_start_time)
        SELECT
            ledger.run_id,
            CAST(json_extract(observation.value, '$.callId') AS INTEGER),
            ledger.ledger_entry_id,
            ledger.recorded_at_utc,
            CAST(json_extract(observation.value, '$.observedAtUnixSeconds') AS INTEGER)
        FROM incident_batch_constructor_shadow_ledger ledger,
             json_each(ledger.payload_json, '$.bundle.observations') observation
        WHERE json_extract(observation.value, '$.callId') IS NOT NULL
          AND EXISTS (
              SELECT 1
              FROM json_each(ledger.payload_json, '$.newObservationIds') new_observation
              WHERE new_observation.value = json_extract(observation.value, '$.observationId')
          );

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

        CREATE TABLE IF NOT EXISTS incident_batch_verification_shadow_requests (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            request_id TEXT NOT NULL UNIQUE,
            source_ledger_entry_id TEXT NOT NULL,
            enqueued_at_utc TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );
        CREATE INDEX IF NOT EXISTS idx_incident_batch_verification_shadow_request_run_sequence
            ON incident_batch_verification_shadow_requests(run_id, sequence);

        CREATE TABLE IF NOT EXISTS incident_batch_verification_shadow_results (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            result_id TEXT NOT NULL UNIQUE,
            request_id TEXT NOT NULL UNIQUE,
            recorded_at_utc TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );
        CREATE INDEX IF NOT EXISTS idx_incident_batch_verification_shadow_result_run_sequence
            ON incident_batch_verification_shadow_results(run_id, sequence);

        CREATE TABLE IF NOT EXISTS incident_batch_canary_commits (
            sequence INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id TEXT NOT NULL,
            commit_id TEXT NOT NULL UNIQUE,
            request_id TEXT NOT NULL UNIQUE,
            result_id TEXT NOT NULL UNIQUE,
            projection_id TEXT NOT NULL,
            projection_event_id TEXT NOT NULL,
            recorded_at_utc TEXT NOT NULL,
            outcome TEXT NOT NULL,
            incident_id INTEGER NOT NULL DEFAULT 0,
            incident_key TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            payload_json TEXT NOT NULL CHECK(json_valid(payload_json))
        );
        CREATE INDEX IF NOT EXISTS idx_incident_batch_canary_commit_run_sequence
            ON incident_batch_canary_commits(run_id, sequence);

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

        CREATE TRIGGER IF NOT EXISTS incident_event_state_link_shadow_ledger_no_update
        BEFORE UPDATE ON incident_event_state_link_shadow_ledger BEGIN
            SELECT RAISE(ABORT, 'incident event-state link shadow ledger is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_event_state_link_shadow_ledger_no_delete
        BEFORE DELETE ON incident_event_state_link_shadow_ledger BEGIN
            SELECT RAISE(ABORT, 'incident event-state link shadow ledger is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_event_state_link_shadow_projections_no_update
        BEFORE UPDATE ON incident_event_state_link_shadow_projections BEGIN
            SELECT RAISE(ABORT, 'incident event-state link shadow projections are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_event_state_link_shadow_projections_no_delete
        BEFORE DELETE ON incident_event_state_link_shadow_projections BEGIN
            SELECT RAISE(ABORT, 'incident event-state link shadow projections are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_association_shadow_ledger_no_update
        BEFORE UPDATE ON incident_association_shadow_ledger BEGIN
            SELECT RAISE(ABORT, 'incident association shadow ledger is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_association_shadow_ledger_no_delete
        BEFORE DELETE ON incident_association_shadow_ledger BEGIN
            SELECT RAISE(ABORT, 'incident association shadow ledger is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_association_shadow_projections_no_update
        BEFORE UPDATE ON incident_association_shadow_projections BEGIN
            SELECT RAISE(ABORT, 'incident association shadow projections are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_association_shadow_projections_no_delete
        BEFORE DELETE ON incident_association_shadow_projections BEGIN
            SELECT RAISE(ABORT, 'incident association shadow projections are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_constructor_shadow_ledger_no_update
        BEFORE UPDATE ON incident_batch_constructor_shadow_ledger BEGIN
            SELECT RAISE(ABORT, 'incident batch constructor shadow ledger is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_constructor_shadow_ledger_no_delete
        BEFORE DELETE ON incident_batch_constructor_shadow_ledger BEGIN
            SELECT RAISE(ABORT, 'incident batch constructor shadow ledger is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_processed_calls_no_update
        BEFORE UPDATE ON incident_batch_processed_calls BEGIN
            SELECT RAISE(ABORT, 'incident batch processed calls are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_processed_calls_no_delete
        BEFORE DELETE ON incident_batch_processed_calls BEGIN
            SELECT RAISE(ABORT, 'incident batch processed calls are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_constructor_shadow_projections_no_update
        BEFORE UPDATE ON incident_batch_constructor_shadow_projections BEGIN
            SELECT RAISE(ABORT, 'incident batch constructor shadow projections are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_constructor_shadow_projections_no_delete
        BEFORE DELETE ON incident_batch_constructor_shadow_projections BEGIN
            SELECT RAISE(ABORT, 'incident batch constructor shadow projections are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_verification_shadow_requests_no_update
        BEFORE UPDATE ON incident_batch_verification_shadow_requests BEGIN
            SELECT RAISE(ABORT, 'incident batch verification shadow requests are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_verification_shadow_requests_no_delete
        BEFORE DELETE ON incident_batch_verification_shadow_requests BEGIN
            SELECT RAISE(ABORT, 'incident batch verification shadow requests are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_verification_shadow_results_no_update
        BEFORE UPDATE ON incident_batch_verification_shadow_results BEGIN
            SELECT RAISE(ABORT, 'incident batch verification shadow results are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_verification_shadow_results_no_delete
        BEFORE DELETE ON incident_batch_verification_shadow_results BEGIN
            SELECT RAISE(ABORT, 'incident batch verification shadow results are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_canary_commits_no_update
        BEFORE UPDATE ON incident_batch_canary_commits BEGIN
            SELECT RAISE(ABORT, 'incident batch canary commits are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_batch_canary_commits_no_delete
        BEFORE DELETE ON incident_batch_canary_commits BEGIN
            SELECT RAISE(ABORT, 'incident batch canary commits are append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_association_review_ledger_no_update
        BEFORE UPDATE ON incident_association_review_ledger BEGIN
            SELECT RAISE(ABORT, 'incident association review ledger is append-only');
        END;
        CREATE TRIGGER IF NOT EXISTS incident_association_review_ledger_no_delete
        BEFORE DELETE ON incident_association_review_ledger BEGIN
            SELECT RAISE(ABORT, 'incident association review ledger is append-only');
        END;
        """;
}
