using System.Globalization;
using Microsoft.Data.Sqlite;

namespace pizzad;

public sealed record DurableIngestPause(bool Paused, bool UntilQueueClear, string Reason, DateTime? PausedAtUtc);

public sealed partial class EngineDatabase
{
    private const string IngestControlSchemaSql = """
        CREATE TABLE IF NOT EXISTS ingest_control_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            schema_version INTEGER NOT NULL CHECK (schema_version = 1),
            paused INTEGER NOT NULL CHECK (paused IN (0, 1)),
            until_queue_clear INTEGER NOT NULL CHECK (until_queue_clear IN (0, 1)),
            reason TEXT NOT NULL,
            paused_at_utc TEXT
        );
        """;

    private static void InitializeIngestControlStore(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='ingest_control_state';";
        var exists = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        if (!exists)
        {
            command.CommandText = IngestControlSchemaSql;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO ingest_control_state VALUES (1,1,0,0,'',NULL);";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public DurableIngestPause LoadIngestPause()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT schema_version, paused, until_queue_clear, reason, paused_at_utc FROM ingest_control_state WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Stored ingest pause row is missing.");
        var paused = reader.GetInt32(1) == 1;
        var until = reader.GetInt32(2) == 1;
        DateTime? at = null;
        if (!reader.IsDBNull(4))
        {
            if (!DateTime.TryParse(reader.GetString(4), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) || parsed.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("Invalid stored ingest pause timestamp.");
            at = parsed;
        }
        if (reader.GetInt32(0) != 1 || (paused && at is null) || (!paused && (until || at is not null)))
            throw new InvalidDataException("Invalid stored ingest pause state.");
        return new DurableIngestPause(paused, until, reader.GetString(3), at);
    }

    public void SaveIngestPause(DurableIngestPause state)
    {
        using var connection = OpenConnection();
        using (var durability = connection.CreateCommand())
        {
            durability.CommandText = "PRAGMA synchronous=FULL;";
            durability.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ingest_control_state (id, schema_version, paused, until_queue_clear, reason, paused_at_utc)
            VALUES (1, 1, $paused, $until, $reason, $at)
            ON CONFLICT(id) DO UPDATE SET schema_version=1, paused=$paused,
                until_queue_clear=$until, reason=$reason, paused_at_utc=$at;
            """;
        command.Parameters.AddWithValue("$paused", state.Paused ? 1 : 0);
        command.Parameters.AddWithValue("$until", state.UntilQueueClear ? 1 : 0);
        command.Parameters.AddWithValue("$reason", state.Reason);
        command.Parameters.AddWithValue("$at", (object?)state.PausedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
