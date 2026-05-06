using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace pizzad;

public sealed class EngineDatabase
{
    private readonly EngineConfig _config;
    private readonly ILogger<EngineDatabase> _logger;
    private readonly string _connectionString;

    public EngineDatabase(EngineConfig config, ILogger<EngineDatabase> logger)
    {
        _config = config;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _config.Storage.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_config.Storage.DatabasePath) ?? ".");
        Directory.CreateDirectory(_config.Storage.AudioRoot);
        Directory.CreateDirectory(_config.Storage.ImportCacheRoot);

        await using var connection = OpenConnection();
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", ct);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", ct);
        await ExecuteNonQueryAsync(connection, SchemaSql, ct);
        _logger.LogInformation("SQLite engine store ready at {Path}", _config.Storage.DatabasePath);
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public async Task<long> UpsertCallAsync(EngineCall call, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO calls (
                unique_key, start_time, stop_time, source, system_short_name, callstream_call_id,
                talkgroup, talkgroup_name, frequency, category, audio_path, transcription,
                transcription_status, is_imported, is_alert_match, raw_metadata_json, created_at_utc, updated_at_utc)
            VALUES (
                $unique_key, $start_time, $stop_time, $source, $system_short_name, $callstream_call_id,
                $talkgroup, $talkgroup_name, $frequency, $category, $audio_path, $transcription,
                $transcription_status, $is_imported, $is_alert_match, $raw_metadata_json, $now, $now)
            ON CONFLICT(unique_key) DO UPDATE SET
                audio_path=excluded.audio_path,
                transcription=excluded.transcription,
                transcription_status=excluded.transcription_status,
                is_alert_match=excluded.is_alert_match,
                updated_at_utc=excluded.updated_at_utc
            RETURNING id;
            """;
        var now = DateTime.UtcNow.ToString("O");
        Add(command, "$unique_key", call.UniqueKey);
        Add(command, "$start_time", call.StartTime);
        Add(command, "$stop_time", call.StopTime);
        Add(command, "$source", call.Source);
        Add(command, "$system_short_name", call.SystemShortName);
        Add(command, "$callstream_call_id", call.CallstreamCallId);
        Add(command, "$talkgroup", call.Talkgroup);
        Add(command, "$talkgroup_name", call.TalkgroupName);
        Add(command, "$frequency", call.Frequency);
        Add(command, "$category", call.Category);
        Add(command, "$audio_path", call.AudioPath);
        Add(command, "$transcription", call.Transcription);
        Add(command, "$transcription_status", call.TranscriptionStatus);
        Add(command, "$is_imported", call.IsImported ? 1 : 0);
        Add(command, "$is_alert_match", call.IsAlertMatch ? 1 : 0);
        Add(command, "$raw_metadata_json", call.RawMetadataJson);
        Add(command, "$now", now);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public async Task UpdateCallTranscriptionAsync(long callId, string transcription, string status, bool isAlertMatch, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE calls
            SET transcription=$transcription, transcription_status=$status, is_alert_match=$is_alert_match, updated_at_utc=$now
            WHERE id=$id;
            """;
        Add(command, "$transcription", transcription);
        Add(command, "$status", status);
        Add(command, "$is_alert_match", isAlertMatch ? 1 : 0);
        Add(command, "$now", DateTime.UtcNow.ToString("O"));
        Add(command, "$id", callId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAlertMatchAsync(AlertMatchDto match, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_matches (call_id, rule_name, detail, matched_at, is_imported, notification_suppressed)
            VALUES ($call_id, $rule_name, $detail, $matched_at, $is_imported, $notification_suppressed);
            """;
        Add(command, "$call_id", match.CallId);
        Add(command, "$rule_name", match.RuleName);
        Add(command, "$detail", match.Detail);
        Add(command, "$matched_at", match.MatchedAt);
        Add(command, "$is_imported", match.IsImported ? 1 : 0);
        Add(command, "$notification_suppressed", match.NotificationSuppressed ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<EngineCall?> GetCallAsync(long id, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM calls WHERE id=$id;";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadCall(reader) : null;
    }

    public async Task<List<EngineCall>> ListCallsAsync(long start, long end, string? category, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM calls
            WHERE start_time >= $start AND start_time <= $end
              AND ($category IS NULL OR category=$category)
            ORDER BY start_time DESC
            LIMIT 5000;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        Add(command, "$category", string.IsNullOrWhiteSpace(category) ? DBNull.Value : category);
        var calls = new List<EngineCall>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            calls.Add(ReadCall(reader));
        return calls;
    }

    public async Task<List<AlertMatchDto>> ListAlertMatchesAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, call_id, rule_name, detail, matched_at, is_imported, notification_suppressed
            FROM alert_matches
            WHERE matched_at >= $start AND matched_at <= $end
            ORDER BY matched_at DESC
            LIMIT 500;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        var rows = new List<AlertMatchDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AlertMatchDto
            {
                Id = reader.GetInt64(0),
                CallId = reader.GetInt64(1),
                RuleName = reader.GetString(2),
                Detail = reader.GetString(3),
                MatchedAt = reader.GetInt64(4),
                IsImported = reader.GetInt64(5) != 0,
                NotificationSuppressed = reader.GetInt64(6) != 0
            });
        }
        return rows;
    }

    public async Task<long> AddJobAsync(JobDto job, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs (type, status, total, completed, failed, message, created_at_utc)
            VALUES ($type, $status, $total, $completed, $failed, $message, $created_at_utc);
            SELECT last_insert_rowid();
            """;
        Add(command, "$type", job.Type);
        Add(command, "$status", job.Status);
        Add(command, "$total", job.Total);
        Add(command, "$completed", job.Completed);
        Add(command, "$failed", job.Failed);
        Add(command, "$message", job.Message);
        Add(command, "$created_at_utc", job.CreatedAtUtc.ToString("O"));
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public async Task UpdateJobAsync(long id, string status, int? total, int? completed, int? failed, string? message, bool setStarted, bool setFinished, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE jobs
            SET status = $status,
                total = COALESCE($total, total),
                completed = COALESCE($completed, completed),
                failed = COALESCE($failed, failed),
                message = COALESCE($message, message),
                started_at_utc = CASE WHEN $set_started = 1 THEN COALESCE(started_at_utc, $now) ELSE started_at_utc END,
                finished_at_utc = CASE WHEN $set_finished = 1 THEN $now ELSE finished_at_utc END
            WHERE id = $id;
            """;
        Add(command, "$status", status);
        Add(command, "$total", total.HasValue ? total.Value : DBNull.Value);
        Add(command, "$completed", completed.HasValue ? completed.Value : DBNull.Value);
        Add(command, "$failed", failed.HasValue ? failed.Value : DBNull.Value);
        Add(command, "$message", message == null ? DBNull.Value : message);
        Add(command, "$set_started", setStarted ? 1 : 0);
        Add(command, "$set_finished", setFinished ? 1 : 0);
        Add(command, "$now", DateTime.UtcNow.ToString("O"));
        Add(command, "$id", id);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<JobDto>> ListJobsAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs ORDER BY id DESC LIMIT 200;";
        var jobs = new List<JobDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            jobs.Add(new JobDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Type = reader.GetString(reader.GetOrdinal("type")),
                Status = reader.GetString(reader.GetOrdinal("status")),
                Total = reader.GetInt32(reader.GetOrdinal("total")),
                Completed = reader.GetInt32(reader.GetOrdinal("completed")),
                Failed = reader.GetInt32(reader.GetOrdinal("failed")),
                Message = reader.GetString(reader.GetOrdinal("message")),
                CreatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
                StartedAtUtc = ParseNullableDate(reader, "started_at_utc"),
                FinishedAtUtc = ParseNullableDate(reader, "finished_at_utc")
            });
        }
        return jobs;
    }

    public async Task<JobDto?> GetJobAsync(long id, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM jobs WHERE id=$id;";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new JobDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Type = reader.GetString(reader.GetOrdinal("type")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            Total = reader.GetInt32(reader.GetOrdinal("total")),
            Completed = reader.GetInt32(reader.GetOrdinal("completed")),
            Failed = reader.GetInt32(reader.GetOrdinal("failed")),
            Message = reader.GetString(reader.GetOrdinal("message")),
            CreatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            StartedAtUtc = ParseNullableDate(reader, "started_at_utc"),
            FinishedAtUtc = ParseNullableDate(reader, "finished_at_utc")
        };
    }

    public async Task InsertHealthSampleAsync(TrHealthSampleDto sample, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tr_health_samples (
                window_start_utc, window_end_utc, scope, decode_lines, decode_zero, decode_zero_pct,
                retunes, calls_started, calls_concluded, sample_stops, unable_source)
            VALUES (
                $window_start_utc, $window_end_utc, $scope, $decode_lines, $decode_zero, $decode_zero_pct,
                $retunes, $calls_started, $calls_concluded, $sample_stops, $unable_source);
            """;
        Add(command, "$window_start_utc", sample.WindowStartUtc.ToString("O"));
        Add(command, "$window_end_utc", sample.WindowEndUtc.ToString("O"));
        Add(command, "$scope", sample.Scope);
        Add(command, "$decode_lines", sample.DecodeLines);
        Add(command, "$decode_zero", sample.DecodeZero);
        Add(command, "$decode_zero_pct", sample.DecodeZeroPct);
        Add(command, "$retunes", sample.Retunes);
        Add(command, "$calls_started", sample.CallsStarted);
        Add(command, "$calls_concluded", sample.CallsConcluded);
        Add(command, "$sample_stops", sample.SampleStops);
        Add(command, "$unable_source", sample.UnableSource);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetBackfillItemStatusAsync(string source, string remotePath, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM backfill_items WHERE source=$source AND remote_path=$remote_path LIMIT 1;";
        Add(command, "$source", source);
        Add(command, "$remote_path", remotePath);
        var result = await command.ExecuteScalarAsync(ct);
        return result?.ToString();
    }

    public async Task UpsertBackfillItemAsync(string source, string remotePath, string localCachePath, string uniqueKey, long startTime, long byteCount, string status, string error, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO backfill_items (source, remote_path, local_cache_path, unique_key, start_time, byte_count, status, error, discovered_at_utc, updated_at_utc)
            VALUES ($source, $remote_path, $local_cache_path, $unique_key, $start_time, $byte_count, $status, $error, $now, $now)
            ON CONFLICT(source, remote_path) DO UPDATE SET
                local_cache_path=excluded.local_cache_path,
                unique_key=excluded.unique_key,
                start_time=excluded.start_time,
                byte_count=excluded.byte_count,
                status=excluded.status,
                error=excluded.error,
                updated_at_utc=excluded.updated_at_utc;
            """;
        Add(command, "$source", source);
        Add(command, "$remote_path", remotePath);
        Add(command, "$local_cache_path", localCachePath);
        Add(command, "$unique_key", uniqueKey);
        Add(command, "$start_time", startTime);
        Add(command, "$byte_count", byteCount);
        Add(command, "$status", status);
        Add(command, "$error", error);
        Add(command, "$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<TrHealthSampleDto>> ListHealthSamplesAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM tr_health_samples
            WHERE unixepoch(window_start_utc) >= $start AND unixepoch(window_end_utc) <= $end
            ORDER BY window_start_utc DESC
            LIMIT 2000;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        var rows = new List<TrHealthSampleDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TrHealthSampleDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                WindowStartUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("window_start_utc"))),
                WindowEndUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("window_end_utc"))),
                Scope = reader.GetString(reader.GetOrdinal("scope")),
                DecodeLines = reader.GetInt32(reader.GetOrdinal("decode_lines")),
                DecodeZero = reader.GetInt32(reader.GetOrdinal("decode_zero")),
                DecodeZeroPct = reader.GetDouble(reader.GetOrdinal("decode_zero_pct")),
                Retunes = reader.GetInt32(reader.GetOrdinal("retunes")),
                CallsStarted = reader.GetInt32(reader.GetOrdinal("calls_started")),
                CallsConcluded = reader.GetInt32(reader.GetOrdinal("calls_concluded")),
                SampleStops = reader.GetInt32(reader.GetOrdinal("sample_stops")),
                UnableSource = reader.GetInt32(reader.GetOrdinal("unable_source"))
            });
        }
        return rows;
    }

    public async Task<long> AddIncidentAsync(IncidentDto incident, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)tx;
        command.CommandText = """
            INSERT INTO incidents (title, detail, first_seen, last_seen, source_summary_ids, created_at_utc)
            VALUES ($title, $detail, $first_seen, $last_seen, '[]', $created_at_utc);
            SELECT last_insert_rowid();
            """;
        Add(command, "$title", incident.Title);
        Add(command, "$detail", incident.Detail);
        Add(command, "$first_seen", incident.FirstSeen);
        Add(command, "$last_seen", incident.LastSeen);
        Add(command, "$created_at_utc", DateTime.UtcNow.ToString("O"));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct));

        foreach (var call in incident.Calls)
        {
            await using var link = connection.CreateCommand();
            link.Transaction = (SqliteTransaction)tx;
            link.CommandText = "INSERT OR IGNORE INTO incident_calls (incident_id, call_id) VALUES ($incident_id, $call_id);";
            Add(link, "$incident_id", id);
            Add(link, "$call_id", call.CallId);
            await link.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<List<IncidentDto>> ListIncidentsAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, detail, first_seen, last_seen
            FROM incidents
            WHERE last_seen >= $start AND first_seen <= $end
            ORDER BY last_seen DESC
            LIMIT 200;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        var incidents = new List<IncidentDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var incidentId = reader.GetInt64(0);
            incidents.Add(new IncidentDto
            {
                Id = incidentId,
                Title = reader.GetString(1),
                Detail = reader.GetString(2),
                FirstSeen = reader.GetInt64(3),
                LastSeen = reader.GetInt64(4),
                Calls = await ListIncidentCallsAsync(connection, incidentId, ct)
            });
        }
        return incidents;
    }

    private static async Task<List<IncidentCallDto>> ListIncidentCallsAsync(SqliteConnection connection, long incidentId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.start_time, c.transcription
            FROM incident_calls ic
            JOIN calls c ON c.id = ic.call_id
            WHERE ic.incident_id = $incident_id
            ORDER BY c.start_time ASC;
            """;
        Add(command, "$incident_id", incidentId);
        var calls = new List<IncidentCallDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var callId = reader.GetInt64(0);
            calls.Add(new IncidentCallDto(
                callId,
                reader.GetInt64(1),
                reader.GetString(2),
                $"/api/v1/calls/{callId}/audio"));
        }
        return calls;
    }

    private static EngineCall ReadCall(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        UniqueKey = reader.GetString(reader.GetOrdinal("unique_key")),
        StartTime = reader.GetInt64(reader.GetOrdinal("start_time")),
        StopTime = reader.GetInt64(reader.GetOrdinal("stop_time")),
        Source = reader.GetInt32(reader.GetOrdinal("source")),
        SystemShortName = reader.GetString(reader.GetOrdinal("system_short_name")),
        CallstreamCallId = reader.GetInt64(reader.GetOrdinal("callstream_call_id")),
        Talkgroup = reader.GetInt64(reader.GetOrdinal("talkgroup")),
        TalkgroupName = reader.GetString(reader.GetOrdinal("talkgroup_name")),
        Frequency = reader.GetDouble(reader.GetOrdinal("frequency")),
        Category = reader.GetString(reader.GetOrdinal("category")),
        AudioPath = reader.GetString(reader.GetOrdinal("audio_path")),
        Transcription = reader.GetString(reader.GetOrdinal("transcription")),
        TranscriptionStatus = reader.GetString(reader.GetOrdinal("transcription_status")),
        IsImported = reader.GetInt64(reader.GetOrdinal("is_imported")) != 0,
        IsAlertMatch = reader.GetInt64(reader.GetOrdinal("is_alert_match")) != 0,
        RawMetadataJson = reader.GetString(reader.GetOrdinal("raw_metadata_json"))
    };

    private static DateTime? ParseNullableDate(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : DateTime.Parse(reader.GetString(ordinal));
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS calls (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            unique_key TEXT NOT NULL UNIQUE,
            start_time INTEGER NOT NULL,
            stop_time INTEGER NOT NULL,
            source INTEGER NOT NULL,
            system_short_name TEXT NOT NULL,
            callstream_call_id INTEGER NOT NULL,
            talkgroup INTEGER NOT NULL,
            talkgroup_name TEXT NOT NULL DEFAULT '',
            frequency REAL NOT NULL,
            category TEXT NOT NULL DEFAULT 'other',
            audio_path TEXT NOT NULL,
            transcription TEXT NOT NULL DEFAULT '',
            transcription_status TEXT NOT NULL DEFAULT 'pending',
            is_imported INTEGER NOT NULL DEFAULT 0,
            is_alert_match INTEGER NOT NULL DEFAULT 0,
            raw_metadata_json TEXT NOT NULL DEFAULT '{}',
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_calls_start ON calls(start_time DESC);
        CREATE INDEX IF NOT EXISTS idx_calls_category_start ON calls(category, start_time DESC);
        CREATE INDEX IF NOT EXISTS idx_calls_tg_start ON calls(talkgroup, start_time DESC);
        CREATE INDEX IF NOT EXISTS idx_calls_system_start ON calls(system_short_name, start_time DESC);

        CREATE TABLE IF NOT EXISTS alert_matches (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id INTEGER NOT NULL,
            rule_name TEXT NOT NULL,
            detail TEXT NOT NULL,
            matched_at INTEGER NOT NULL,
            is_imported INTEGER NOT NULL DEFAULT 0,
            notification_suppressed INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_alert_matches_time ON alert_matches(matched_at DESC);

        CREATE TABLE IF NOT EXISTS incidents (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title TEXT NOT NULL,
            detail TEXT NOT NULL,
            first_seen INTEGER NOT NULL,
            last_seen INTEGER NOT NULL,
            source_summary_ids TEXT NOT NULL DEFAULT '[]',
            created_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS insight_windows (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            window_start INTEGER NOT NULL,
            window_end INTEGER NOT NULL,
            summary_text TEXT NOT NULL,
            generated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS incident_calls (
            incident_id INTEGER NOT NULL,
            call_id INTEGER NOT NULL,
            PRIMARY KEY(incident_id, call_id),
            FOREIGN KEY(incident_id) REFERENCES incidents(id) ON DELETE CASCADE,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS tr_health_samples (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            window_start_utc TEXT NOT NULL,
            window_end_utc TEXT NOT NULL,
            scope TEXT NOT NULL,
            decode_lines INTEGER NOT NULL DEFAULT 0,
            decode_zero INTEGER NOT NULL DEFAULT 0,
            decode_zero_pct REAL NOT NULL DEFAULT 0,
            retunes INTEGER NOT NULL DEFAULT 0,
            calls_started INTEGER NOT NULL DEFAULT 0,
            calls_concluded INTEGER NOT NULL DEFAULT 0,
            sample_stops INTEGER NOT NULL DEFAULT 0,
            unable_source INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_tr_health_window ON tr_health_samples(window_start_utc DESC, scope);

        CREATE TABLE IF NOT EXISTS jobs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            type TEXT NOT NULL,
            status TEXT NOT NULL,
            total INTEGER NOT NULL DEFAULT 0,
            completed INTEGER NOT NULL DEFAULT 0,
            failed INTEGER NOT NULL DEFAULT 0,
            message TEXT NOT NULL DEFAULT '',
            created_at_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            finished_at_utc TEXT NULL,
            payload_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS backfill_items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source TEXT NOT NULL,
            remote_path TEXT NOT NULL DEFAULT '',
            local_cache_path TEXT NOT NULL DEFAULT '',
            unique_key TEXT NOT NULL DEFAULT '',
            start_time INTEGER NOT NULL DEFAULT 0,
            byte_count INTEGER NOT NULL DEFAULT 0,
            status TEXT NOT NULL DEFAULT 'discovered',
            error TEXT NOT NULL DEFAULT '',
            discovered_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_backfill_status ON backfill_items(status, start_time);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_backfill_source_remote ON backfill_items(source, remote_path);
        """;
}
