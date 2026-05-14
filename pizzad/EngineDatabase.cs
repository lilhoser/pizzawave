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
        await EnsureSchemaMigrationsAsync(connection, ct);
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
                transcription_status, quality_reason, is_imported, is_alert_match, raw_metadata_json, created_at_utc, updated_at_utc)
            VALUES (
                $unique_key, $start_time, $stop_time, $source, $system_short_name, $callstream_call_id,
                $talkgroup, $talkgroup_name, $frequency, $category, $audio_path, $transcription,
                $transcription_status, $quality_reason, $is_imported, $is_alert_match, $raw_metadata_json, $now, $now)
            ON CONFLICT(unique_key) DO UPDATE SET
                talkgroup_name=excluded.talkgroup_name,
                category=excluded.category,
                audio_path=excluded.audio_path,
                transcription=excluded.transcription,
                transcription_status=excluded.transcription_status,
                quality_reason=excluded.quality_reason,
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
        Add(command, "$quality_reason", call.QualityReason);
        Add(command, "$is_imported", call.IsImported ? 1 : 0);
        Add(command, "$is_alert_match", call.IsAlertMatch ? 1 : 0);
        Add(command, "$raw_metadata_json", call.RawMetadataJson);
        Add(command, "$now", now);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public async Task UpdateCallTranscriptionAsync(long callId, string transcription, string status, string qualityReason, bool isAlertMatch, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE calls
            SET transcription=$transcription, transcription_status=$status, quality_reason=$quality_reason, is_alert_match=$is_alert_match, updated_at_utc=$now
            WHERE id=$id;
            """;
        Add(command, "$transcription", transcription);
        Add(command, "$status", status);
        Add(command, "$quality_reason", qualityReason);
        Add(command, "$is_alert_match", isAlertMatch ? 1 : 0);
        Add(command, "$now", DateTime.UtcNow.ToString("O"));
        Add(command, "$id", callId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ReplaceCallAnnotationsAsync(long callId, IReadOnlyList<TranscriptAnnotation> annotations, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)tx;
            delete.CommandText = "DELETE FROM call_annotations WHERE call_id=$call_id;";
            Add(delete, "$call_id", callId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var annotation in annotations)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO call_annotations (
                    call_id, kind, code, normalized_code, matched_text, meaning, confidence, source, details_json, created_at_utc)
                VALUES (
                    $call_id, $kind, $code, $normalized_code, $matched_text, $meaning, $confidence, $source, $details_json, $created_at_utc);
                """;
            Add(insert, "$call_id", callId);
            Add(insert, "$kind", annotation.Kind);
            Add(insert, "$code", annotation.Code);
            Add(insert, "$normalized_code", annotation.NormalizedCode);
            Add(insert, "$matched_text", annotation.MatchedText);
            Add(insert, "$meaning", annotation.Meaning);
            Add(insert, "$confidence", annotation.Confidence);
            Add(insert, "$source", annotation.Source);
            Add(insert, "$details_json", annotation.DetailsJson);
            Add(insert, "$created_at_utc", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task ReplaceCallLocationsAsync(long callId, IReadOnlyList<CallLocationRecord> locations, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)tx;
            delete.CommandText = "DELETE FROM call_locations WHERE call_id=$call_id;";
            Add(delete, "$call_id", callId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var location in locations)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO call_locations (
                    call_id, area_id, area_label, system_short_name, location_text, normalized_key, geocode_cache_key, source, created_at_utc)
                VALUES (
                    $call_id, $area_id, $area_label, $system_short_name, $location_text, $normalized_key, $geocode_cache_key, $source, $created_at_utc);
                """;
            Add(insert, "$call_id", callId);
            Add(insert, "$area_id", location.AreaId);
            Add(insert, "$area_label", location.AreaLabel);
            Add(insert, "$system_short_name", location.SystemShortName);
            Add(insert, "$location_text", location.LocationText);
            Add(insert, "$normalized_key", location.NormalizedKey);
            Add(insert, "$geocode_cache_key", location.GeocodeCacheKey);
            Add(insert, "$source", location.Source);
            Add(insert, "$created_at_utc", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<long> CountCallsStartedSinceAsync(long startUnix, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        return await CountAsync(connection, "SELECT COUNT(*) FROM calls WHERE start_time >= $start;", startUnix, 0, ct);
    }

    public async Task<long> SumAudioSecondsStartedSinceAsync(long startUnix, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN stop_time > start_time THEN stop_time - start_time ELSE 0 END), 0)
            FROM calls
            WHERE start_time >= $start;
            """;
        Add(command, "$start", startUnix);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task<long> CountTranscriptionCompletionsSinceAsync(DateTime utcStart, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM calls
            WHERE updated_at_utc >= $start
              AND transcription_status IN ('complete', 'poor_quality', 'failed');
            """;
        Add(command, "$start", utcStart.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task<long> SumAudioSecondsTranscriptionCompletionsSinceAsync(DateTime utcStart, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN stop_time > start_time THEN stop_time - start_time ELSE 0 END), 0)
            FROM calls
            WHERE updated_at_utc >= $start
              AND transcription_status IN ('complete', 'poor_quality', 'failed');
            """;
        Add(command, "$start", utcStart.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task<long> SumPendingTranscriptionAudioSecondsAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN stop_time > start_time THEN stop_time - start_time ELSE 0 END), 0)
            FROM calls
            WHERE transcription_status='pending'
              AND length(trim(audio_path)) > 0;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task<List<QueueTalkgroupLoadDto>> ListTopAudioTalkgroupsAsync(long startUnix, int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                system_short_name,
                talkgroup,
                COALESCE(NULLIF(talkgroup_name, ''), 'TG ' || talkgroup) AS talkgroup_name,
                COALESCE(NULLIF(category, ''), 'other') AS category,
                COUNT(*) AS calls,
                COALESCE(SUM(CASE WHEN stop_time > start_time THEN stop_time - start_time ELSE 0 END), 0) AS audio_seconds,
                COALESCE(AVG(CASE WHEN stop_time > start_time THEN stop_time - start_time ELSE NULL END), 0) AS average_audio_seconds,
                COALESCE(SUM(CASE WHEN transcription_status='pending' THEN 1 ELSE 0 END), 0) AS pending_calls,
                COALESCE(SUM(CASE WHEN transcription_status='pending' AND stop_time > start_time THEN stop_time - start_time ELSE 0 END), 0) AS pending_audio_seconds
            FROM calls
            WHERE start_time >= $start
            GROUP BY system_short_name, talkgroup, talkgroup_name, category
            ORDER BY audio_seconds DESC
            LIMIT $limit;
            """;
        Add(command, "$start", startUnix);
        Add(command, "$limit", Math.Clamp(limit, 1, 100));
        var rows = new List<QueueTalkgroupLoadDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new QueueTalkgroupLoadDto(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetDouble(6),
                reader.GetInt64(7),
                reader.GetInt64(8)));
        }
        return rows;
    }

    public async Task<Dictionary<string, DateTime?>> ListRecommendationStatesAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT recommendation_id, snoozed_until_utc FROM recommendation_states;";
        var rows = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                var snoozedUntil = string.IsNullOrWhiteSpace(value) ? (DateTime?)null : DateTime.Parse(value).ToUniversalTime();
                rows[id] = snoozedUntil;
            }
        }
        catch (SqliteException)
        {
            return rows;
        }
        return rows;
    }

    public async Task SaveRecommendationStateAsync(string recommendationId, DateTime? snoozedUntilUtc, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO recommendation_states (recommendation_id, snoozed_until_utc, updated_at_utc)
            VALUES ($id, $snoozed_until_utc, $updated_at_utc)
            ON CONFLICT(recommendation_id) DO UPDATE SET
                snoozed_until_utc=excluded.snoozed_until_utc,
                updated_at_utc=excluded.updated_at_utc;
            """;
        Add(command, "$id", recommendationId);
        Add(command, "$snoozed_until_utc", snoozedUntilUtc?.ToString("O") ?? string.Empty);
        Add(command, "$updated_at_utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearRecommendationStateAsync(string recommendationId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recommendation_states WHERE recommendation_id=$id;";
        Add(command, "$id", recommendationId);
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

    public async Task AddLmUsageAsync(TokenUsageEntryDto entry, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO lm_usage (
                timestamp_utc, trigger_activity, request_kind, success, error, endpoint, request_model, response_model,
                finish_reason, input_chars, payload_chars, prompt_tokens, completion_tokens, total_tokens)
            VALUES (
                $timestamp_utc, $trigger_activity, $request_kind, $success, $error, $endpoint, $request_model, $response_model,
                $finish_reason, $input_chars, $payload_chars, $prompt_tokens, $completion_tokens, $total_tokens);
            """;
        Add(command, "$timestamp_utc", entry.TimestampUtc.ToString("O"));
        Add(command, "$trigger_activity", entry.TriggerActivity);
        Add(command, "$request_kind", entry.RequestKind);
        Add(command, "$success", entry.Success ? 1 : 0);
        Add(command, "$error", entry.Error);
        Add(command, "$endpoint", entry.Endpoint);
        Add(command, "$request_model", entry.RequestModel);
        Add(command, "$response_model", entry.ResponseModel);
        Add(command, "$finish_reason", entry.FinishReason);
        Add(command, "$input_chars", entry.InputChars);
        Add(command, "$payload_chars", entry.PayloadChars);
        Add(command, "$prompt_tokens", entry.PromptTokens);
        Add(command, "$completion_tokens", entry.CompletionTokens);
        Add(command, "$total_tokens", entry.TotalTokens);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<GeocodeCacheDto?> GetGeocodeCacheAsync(string cacheKey, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM geocode_cache WHERE cache_key=$cache_key;";
        Add(command, "$cache_key", cacheKey);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new GeocodeCacheDto
        {
            CacheKey = reader.GetString(reader.GetOrdinal("cache_key")),
            Provider = reader.GetString(reader.GetOrdinal("provider")),
            Query = reader.GetString(reader.GetOrdinal("query")),
            AreaId = reader.GetString(reader.GetOrdinal("area_id")),
            LocationText = reader.GetString(reader.GetOrdinal("location_text")),
            DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
            Precision = reader.GetString(reader.GetOrdinal("precision")),
            Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
            Latitude = reader.GetDouble(reader.GetOrdinal("latitude")),
            Longitude = reader.GetDouble(reader.GetOrdinal("longitude")),
            CreatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
            UpdatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc")))
        };
    }

    public async Task UpsertGeocodeCacheAsync(GeocodeCacheDto row, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO geocode_cache (
                cache_key, provider, query, area_id, location_text, display_name, precision,
                confidence, latitude, longitude, created_at_utc, updated_at_utc)
            VALUES (
                $cache_key, $provider, $query, $area_id, $location_text, $display_name, $precision,
                $confidence, $latitude, $longitude, $created_at_utc, $updated_at_utc)
            ON CONFLICT(cache_key) DO UPDATE SET
                provider=excluded.provider,
                query=excluded.query,
                display_name=excluded.display_name,
                precision=excluded.precision,
                confidence=excluded.confidence,
                latitude=excluded.latitude,
                longitude=excluded.longitude,
                updated_at_utc=excluded.updated_at_utc;
            """;
        var now = DateTime.UtcNow.ToString("O");
        Add(command, "$cache_key", row.CacheKey);
        Add(command, "$provider", row.Provider);
        Add(command, "$query", row.Query);
        Add(command, "$area_id", row.AreaId);
        Add(command, "$location_text", row.LocationText);
        Add(command, "$display_name", row.DisplayName);
        Add(command, "$precision", row.Precision);
        Add(command, "$confidence", row.Confidence);
        Add(command, "$latitude", row.Latitude);
        Add(command, "$longitude", row.Longitude);
        Add(command, "$created_at_utc", row.CreatedAtUtc == default ? now : row.CreatedAtUtc.ToString("O"));
        Add(command, "$updated_at_utc", now);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<CallLocationDashboardRow>> ListCallLocationsAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                c.id AS call_id,
                c.start_time,
                COALESCE(c.system_short_name, '') AS system_short_name,
                c.talkgroup,
                COALESCE(NULLIF(c.talkgroup_name, ''), 'TG ' || c.talkgroup) AS talkgroup_name,
                COALESCE(NULLIF(c.category, ''), 'other') AS category,
                COALESCE(c.transcription, '') AS transcription,
                l.area_id,
                l.area_label,
                l.system_short_name AS area_system_short_name,
                l.location_text,
                l.normalized_key,
                l.source,
                COALESCE(g.query, '') AS geocode_query,
                COALESCE(g.display_name, '') AS geocode_display_name,
                COALESCE(g.provider, '') AS geocode_provider,
                COALESCE(g.precision, '') AS geocode_precision,
                COALESCE(g.confidence, 0) AS geocode_confidence,
                COALESCE(g.latitude, 0) AS latitude,
                COALESCE(g.longitude, 0) AS longitude
            FROM call_locations l
            JOIN calls c ON c.id = l.call_id
            LEFT JOIN geocode_cache g ON g.cache_key = l.geocode_cache_key
            WHERE c.start_time BETWEEN $start AND $end
            ORDER BY c.start_time DESC;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        var rows = new List<CallLocationDashboardRow>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CallLocationDashboardRow(
                reader.GetInt64(reader.GetOrdinal("call_id")),
                reader.GetInt64(reader.GetOrdinal("start_time")),
                reader.GetString(reader.GetOrdinal("system_short_name")),
                reader.GetInt64(reader.GetOrdinal("talkgroup")),
                reader.GetString(reader.GetOrdinal("talkgroup_name")),
                reader.GetString(reader.GetOrdinal("category")),
                reader.GetString(reader.GetOrdinal("transcription")),
                reader.GetString(reader.GetOrdinal("area_id")),
                reader.GetString(reader.GetOrdinal("area_label")),
                reader.GetString(reader.GetOrdinal("area_system_short_name")),
                reader.GetString(reader.GetOrdinal("location_text")),
                reader.GetString(reader.GetOrdinal("normalized_key")),
                reader.GetString(reader.GetOrdinal("source")),
                reader.GetString(reader.GetOrdinal("geocode_query")),
                reader.GetString(reader.GetOrdinal("geocode_display_name")),
                reader.GetString(reader.GetOrdinal("geocode_provider")),
                reader.GetString(reader.GetOrdinal("geocode_precision")),
                reader.GetDouble(reader.GetOrdinal("geocode_confidence")),
                reader.GetDouble(reader.GetOrdinal("latitude")),
                reader.GetDouble(reader.GetOrdinal("longitude"))));
        }
        return rows;
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
            ORDER BY start_time DESC;
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

    public async Task<List<EngineCall>> ListPendingTranscriptionCallsAsync(int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM calls
            WHERE transcription_status='pending'
              AND length(trim(audio_path)) > 0
            ORDER BY start_time ASC, id ASC
            LIMIT $limit;
            """;
        Add(command, "$limit", Math.Max(1, limit));
        var calls = new List<EngineCall>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            calls.Add(ReadCall(reader));
        return calls;
    }

    public async Task<long> CountPendingTranscriptionCallsAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM calls
            WHERE transcription_status='pending'
              AND length(trim(audio_path)) > 0;
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<List<EngineCall>> ListTranscriptionErrorCallsAsync(int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM calls
            WHERE transcription_status='failed'
              AND quality_reason='transcription_error'
              AND length(trim(audio_path)) > 0
            ORDER BY start_time DESC, id DESC
            LIMIT $limit;
            """;
        Add(command, "$limit", Math.Max(1, limit));
        var calls = new List<EngineCall>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            calls.Add(ReadCall(reader));
        return calls;
    }

    public async Task<StatusSummaryDto> BuildStatusSummaryAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        var calls = await CountAsync(connection, "SELECT COUNT(*) FROM calls WHERE start_time >= $start AND start_time <= $end;", start, end, ct);
        var alerts = await CountAsync(connection, "SELECT COUNT(*) FROM alert_matches WHERE matched_at >= $start AND matched_at <= $end;", start, end, ct);
        var incidents = await CountAsync(connection, """
            SELECT COUNT(*)
            FROM incidents i
            WHERE i.last_seen >= $start AND i.first_seen <= $end
              AND (SELECT COUNT(*) FROM incident_calls ic WHERE ic.incident_id = i.id) >= 2;
            """, start, end, ct);
        var tokens = await CountIsoAsync(connection, "SELECT COALESCE(SUM(CASE WHEN total_tokens > 0 THEN total_tokens ELSE prompt_tokens + completion_tokens END), 0) FROM lm_usage WHERE timestamp_utc >= $start AND timestamp_utc <= $end;", start, end, ct);
        return new StatusSummaryDto((int)calls, (int)incidents, (int)alerts, tokens);
    }

    public async Task<List<EngineCall>> ListCompletedCallsAfterAsync(long startExclusive, int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM calls
            WHERE start_time > $start
              AND transcription_status='complete'
              AND quality_reason='ok'
              AND length(trim(transcription)) > 0
              AND is_imported=0
            ORDER BY start_time ASC, id ASC
            LIMIT $limit;
            """;
        Add(command, "$start", startExclusive);
        Add(command, "$limit", limit);
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
            SELECT am.id, am.call_id, am.rule_name, am.detail, am.matched_at, am.is_imported, am.notification_suppressed,
                   COALESCE(c.system_short_name, ''),
                   COALESCE(c.talkgroup, 0),
                   COALESCE(c.talkgroup_name, ''),
                   COALESCE(c.category, 'other'),
                   COALESCE(c.transcription, ''),
                   COALESCE(c.transcription_status, ''),
                   COALESCE(c.quality_reason, '')
            FROM alert_matches am
            LEFT JOIN calls c ON c.id = am.call_id
            WHERE am.matched_at >= $start AND am.matched_at <= $end
            ORDER BY am.matched_at DESC
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
                NotificationSuppressed = reader.GetInt64(6) != 0,
                SystemShortName = reader.GetString(7),
                Talkgroup = reader.GetInt64(8),
                TalkgroupName = reader.GetString(9),
                Category = reader.GetString(10),
                Transcription = reader.GetString(11),
                TranscriptionStatus = reader.GetString(12),
                QualityReason = reader.GetString(13),
                AudioUrl = $"/api/v1/calls/{reader.GetInt64(1)}/audio"
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

    public async Task AddJobLogAsync(long jobId, string stream, string text, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO job_logs (job_id, timestamp_utc, stream, text)
            VALUES ($job_id, $timestamp_utc, $stream, $text);
            """;
        Add(command, "$job_id", jobId);
        Add(command, "$timestamp_utc", DateTime.UtcNow.ToString("O"));
        Add(command, "$stream", string.IsNullOrWhiteSpace(stream) ? "info" : stream.Trim());
        Add(command, "$text", text ?? string.Empty);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> HasActiveJobAsync(string type, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM jobs
            WHERE type = $type
              AND status IN ('queued', 'running', 'paused');
            """;
        Add(command, "$type", type);
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result) > 0;
    }

    public async Task<int> CancelStaleActiveJobsAsync(string type, TimeSpan minAge, string message, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        var now = DateTime.UtcNow;
        var cutoff = now.Subtract(minAge).ToString("O");
        command.CommandText = """
            UPDATE jobs
            SET status = 'canceled',
                message = $message,
                finished_at_utc = $now
            WHERE type = $type
              AND status IN ('queued', 'running', 'paused')
              AND created_at_utc < $cutoff;
            """;
        Add(command, "$type", type);
        Add(command, "$message", message);
        Add(command, "$now", now.ToString("O"));
        Add(command, "$cutoff", cutoff);
        return await command.ExecuteNonQueryAsync(ct);
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

    public async Task<TokenUsageReportDto> GetTokenUsageAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM lm_usage
            WHERE timestamp_utc >= $start AND timestamp_utc <= $end
            ORDER BY timestamp_utc DESC
            LIMIT 500;
            """;
        Add(command, "$start", DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime.ToString("O"));
        Add(command, "$end", DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime.ToString("O"));
        var rows = new List<TokenUsageEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(ReadLmUsage(reader));
        var summary = SummarizeTokenUsage(rows);
        var byDay = rows.GroupBy(r => r.TimestampUtc.ToLocalTime().ToString("MM/dd"))
            .Select(g => TokenBucket(g.Key, g)).OrderBy(x => x.Label).ToList();
        var byTrigger = rows.GroupBy(r => string.IsNullOrWhiteSpace(r.TriggerActivity) ? "other" : r.TriggerActivity)
            .Select(g => TokenBucket(g.Key, g)).OrderByDescending(x => x.TotalTokens).ToList();
        return new TokenUsageReportDto("sqlite:lm_usage", summary, byDay, byTrigger, rows);
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

    public async Task<List<JobLogDto>> ListJobLogsAsync(long jobId, long afterId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, job_id, timestamp_utc, stream, text
            FROM job_logs
            WHERE job_id=$job_id AND id > $after_id
            ORDER BY id ASC
            LIMIT 500;
            """;
        Add(command, "$job_id", jobId);
        Add(command, "$after_id", afterId);
        var rows = new List<JobLogDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new JobLogDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                DateTime.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return rows;
    }

    public async Task<bool> DeleteJobAsync(long id, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using (var logs = connection.CreateCommand())
        {
            logs.CommandText = "DELETE FROM job_logs WHERE job_id=$id;";
            Add(logs, "$id", id);
            await logs.ExecuteNonQueryAsync(ct);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM jobs WHERE id=$id;";
        Add(command, "$id", id);
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> DeleteJobsByTypePrefixAsync(string typePrefix, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using (var deleteResults = connection.CreateCommand())
        {
            deleteResults.CommandText = """
                DELETE FROM diagnostic_results
                WHERE job_id IN (
                    SELECT id FROM jobs
                    WHERE type LIKE $type_prefix
                      AND status NOT IN ('running', 'queued', 'paused')
                );
                """;
            Add(deleteResults, "$type_prefix", $"{typePrefix}%");
            await deleteResults.ExecuteNonQueryAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM jobs
            WHERE type LIKE $type_prefix
              AND status NOT IN ('running', 'queued', 'paused');
            """;
        Add(command, "$type_prefix", $"{typePrefix}%");
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveDiagnosticResultAsync(DiagnosticToolResultDto result, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO diagnostic_results (job_id, tool, result_json, created_at_utc)
            VALUES ($job_id, $tool, $result_json, $created_at_utc)
            ON CONFLICT(job_id) DO UPDATE SET
                tool=excluded.tool,
                result_json=excluded.result_json,
                created_at_utc=excluded.created_at_utc;
            """;
        Add(command, "$job_id", result.JobId);
        Add(command, "$tool", result.Tool);
        Add(command, "$result_json", JsonSerializer.Serialize(result.Rows, EngineConfig.JsonOptions()));
        Add(command, "$created_at_utc", result.CreatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<DiagnosticToolResultDto?> GetDiagnosticResultAsync(long jobId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id, tool, result_json, created_at_utc FROM diagnostic_results WHERE job_id=$job_id;";
        Add(command, "$job_id", jobId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new DiagnosticToolResultDto(
            reader.GetInt64(0),
            reader.GetString(1),
            DateTime.Parse(reader.GetString(3)),
            JsonSerializer.Deserialize<List<DiagnosticToolRowDto>>(reader.GetString(2), EngineConfig.JsonOptions()) ?? []);
    }

    public async Task InsertHealthSampleAsync(TrHealthSampleDto sample, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await InsertHealthSampleAsync(connection, sample, ct);
    }

    public async Task UpsertHealthSamplesAsync(IEnumerable<TrHealthSampleDto> samples, CancellationToken ct)
    {
        var rows = samples.ToList();
        if (rows.Count == 0)
            return;

        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        foreach (var sample in rows)
            await InsertHealthSampleAsync(connection, sample, ct, (SqliteTransaction)tx);
        await tx.CommitAsync(ct);
    }

    private static async Task InsertHealthSampleAsync(SqliteConnection connection, TrHealthSampleDto sample, CancellationToken ct, SqliteTransaction? tx = null)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = tx;
            delete.CommandText = """
                DELETE FROM tr_health_samples
                WHERE window_start_utc=$window_start_utc AND window_end_utc=$window_end_utc AND scope=$scope;
                """;
            Add(delete, "$window_start_utc", sample.WindowStartUtc.ToString("O"));
            Add(delete, "$window_end_utc", sample.WindowEndUtc.ToString("O"));
            Add(delete, "$scope", sample.Scope);
            await delete.ExecuteNonQueryAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO tr_health_samples (
                window_start_utc, window_end_utc, scope, decode_lines, decode_zero, decode_zero_pct,
                cc_summary_decode_lines, cc_summary_decode_zero, cc_summary_decode_rate_total,
                low_decode_warning_lines, low_decode_warning_zero, low_decode_warning_rate_total,
                decode_rate_total, retunes, calls_started, calls_concluded, update_not_grant, no_tx_recorded, recorder_exhausted,
                sample_stops, unable_source, tuning_err_samples, tuning_err_total_abs_hz, tuning_err_max_abs_hz)
            VALUES (
                $window_start_utc, $window_end_utc, $scope, $decode_lines, $decode_zero, $decode_zero_pct,
                $cc_summary_decode_lines, $cc_summary_decode_zero, $cc_summary_decode_rate_total,
                $low_decode_warning_lines, $low_decode_warning_zero, $low_decode_warning_rate_total,
                $decode_rate_total, $retunes, $calls_started, $calls_concluded, $update_not_grant, $no_tx_recorded, $recorder_exhausted,
                $sample_stops, $unable_source, $tuning_err_samples, $tuning_err_total_abs_hz, $tuning_err_max_abs_hz);
            """;
        Add(command, "$window_start_utc", sample.WindowStartUtc.ToString("O"));
        Add(command, "$window_end_utc", sample.WindowEndUtc.ToString("O"));
        Add(command, "$scope", sample.Scope);
        Add(command, "$decode_lines", sample.DecodeLines);
        Add(command, "$decode_zero", sample.DecodeZero);
        Add(command, "$decode_zero_pct", sample.DecodeZeroPct);
        Add(command, "$cc_summary_decode_lines", sample.CcSummaryDecodeLines);
        Add(command, "$cc_summary_decode_zero", sample.CcSummaryDecodeZero);
        Add(command, "$cc_summary_decode_rate_total", sample.CcSummaryDecodeRateTotal);
        Add(command, "$low_decode_warning_lines", sample.LowDecodeWarningLines);
        Add(command, "$low_decode_warning_zero", sample.LowDecodeWarningZero);
        Add(command, "$low_decode_warning_rate_total", sample.LowDecodeWarningRateTotal);
        Add(command, "$decode_rate_total", sample.DecodeRateTotal);
        Add(command, "$retunes", sample.Retunes);
        Add(command, "$calls_started", sample.CallsStarted);
        Add(command, "$calls_concluded", sample.CallsConcluded);
        Add(command, "$update_not_grant", sample.UpdateNotGrant);
        Add(command, "$no_tx_recorded", sample.NoTxRecorded);
        Add(command, "$recorder_exhausted", sample.RecorderExhausted);
        Add(command, "$sample_stops", sample.SampleStops);
        Add(command, "$unable_source", sample.UnableSource);
        Add(command, "$tuning_err_samples", sample.TuningErrSamples);
        Add(command, "$tuning_err_total_abs_hz", sample.TuningErrTotalAbsHz);
        Add(command, "$tuning_err_max_abs_hz", sample.TuningErrMaxAbsHz);
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
                DecodeRateTotal = reader.GetDouble(reader.GetOrdinal("decode_rate_total")),
                CcSummaryDecodeLines = reader.GetInt32(reader.GetOrdinal("cc_summary_decode_lines")),
                CcSummaryDecodeZero = reader.GetInt32(reader.GetOrdinal("cc_summary_decode_zero")),
                CcSummaryDecodeRateTotal = reader.GetDouble(reader.GetOrdinal("cc_summary_decode_rate_total")),
                LowDecodeWarningLines = reader.GetInt32(reader.GetOrdinal("low_decode_warning_lines")),
                LowDecodeWarningZero = reader.GetInt32(reader.GetOrdinal("low_decode_warning_zero")),
                LowDecodeWarningRateTotal = reader.GetDouble(reader.GetOrdinal("low_decode_warning_rate_total")),
                Retunes = reader.GetInt32(reader.GetOrdinal("retunes")),
                CallsStarted = reader.GetInt32(reader.GetOrdinal("calls_started")),
                CallsConcluded = reader.GetInt32(reader.GetOrdinal("calls_concluded")),
                UpdateNotGrant = reader.GetInt32(reader.GetOrdinal("update_not_grant")),
                NoTxRecorded = reader.GetInt32(reader.GetOrdinal("no_tx_recorded")),
                RecorderExhausted = reader.GetInt32(reader.GetOrdinal("recorder_exhausted")),
                SampleStops = reader.GetInt32(reader.GetOrdinal("sample_stops")),
                UnableSource = reader.GetInt32(reader.GetOrdinal("unable_source")),
                TuningErrSamples = reader.GetInt32(reader.GetOrdinal("tuning_err_samples")),
                TuningErrTotalAbsHz = reader.GetDouble(reader.GetOrdinal("tuning_err_total_abs_hz")),
                TuningErrMaxAbsHz = reader.GetDouble(reader.GetOrdinal("tuning_err_max_abs_hz"))
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
            INSERT INTO incidents (title, detail, first_seen, last_seen, incident_score, source_summary_ids, created_at_utc)
            VALUES ($title, $detail, $first_seen, $last_seen, $incident_score, '[]', $created_at_utc);
            SELECT last_insert_rowid();
            """;
        Add(command, "$title", incident.Title);
        Add(command, "$detail", incident.Detail);
        Add(command, "$first_seen", incident.FirstSeen);
        Add(command, "$last_seen", incident.LastSeen);
        Add(command, "$incident_score", incident.Confidence);
        Add(command, "$created_at_utc", DateTime.UtcNow.ToString("O"));
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(ct));

        var callIds = incident.Calls.Select(c => c.CallId).Distinct().ToList();
        if (callIds.Count < 2)
        {
            await tx.RollbackAsync(ct);
            return 0;
        }

        foreach (var callId in callIds)
        {
            await using var existing = connection.CreateCommand();
            existing.Transaction = (SqliteTransaction)tx;
            existing.CommandText = "SELECT incident_id FROM incident_calls WHERE call_id=$call_id LIMIT 1;";
            Add(existing, "$call_id", callId);
            if (await existing.ExecuteScalarAsync(ct) != null)
            {
                await tx.RollbackAsync(ct);
                return 0;
            }
        }

        foreach (var call in incident.Calls)
        {
            await using var link = connection.CreateCommand();
            link.Transaction = (SqliteTransaction)tx;
            link.CommandText = "INSERT INTO incident_calls (incident_id, call_id) VALUES ($incident_id, $call_id);";
            Add(link, "$incident_id", id);
            Add(link, "$call_id", call.CallId);
            await link.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<int> RebuildIncidentsFromInsightEventsAsync(long start, long end, CancellationToken ct)
    {
        var events = await ListInsightEventsAsync(start, end, ct);
        var candidates = events
            .Where(e => e.Calls.Count >= 2)
            .OrderByDescending(e => e.Confidence)
            .ThenByDescending(e => e.LastSeen)
            .ToList();
        if (candidates.Count == 0)
            return 0;

        await using var connection = OpenConnection();
        await using (var deleteCalls = connection.CreateCommand())
        {
            deleteCalls.CommandText = """
                DELETE FROM incident_calls
                WHERE incident_id IN (
                    SELECT id FROM incidents WHERE last_seen >= $start AND first_seen <= $end
                );
                """;
            Add(deleteCalls, "$start", start);
            Add(deleteCalls, "$end", end);
            await deleteCalls.ExecuteNonQueryAsync(ct);
        }

        await using (var deleteIncidents = connection.CreateCommand())
        {
            deleteIncidents.CommandText = "DELETE FROM incidents WHERE last_seen >= $start AND first_seen <= $end;";
            Add(deleteIncidents, "$start", start);
            Add(deleteIncidents, "$end", end);
            await deleteIncidents.ExecuteNonQueryAsync(ct);
        }

        var count = 0;
        var claimedCallIds = new HashSet<long>();
        foreach (var ev in candidates)
        {
            var calls = ev.Calls.Where(c => !claimedCallIds.Contains(c.CallId)).ToList();
            if (calls.Count < 2)
                continue;

            var validation = IncidentCandidateValidator.Validate(ev.Title, ev.Detail, calls.Select(ToIncidentCandidateCall).ToList());
            if (!validation.IsValid)
                continue;

            var validatedCallIds = validation.Calls.Select(c => c.CallId).ToHashSet();
            calls = calls.Where(c => validatedCallIds.Contains(c.CallId)).ToList();
            if (calls.Count < 2)
                continue;

            var id = await AddIncidentAsync(new IncidentDto
            {
                Title = ev.Title,
                Detail = ev.Detail,
                FirstSeen = calls.Min(c => c.RawTimestamp),
                LastSeen = calls.Max(c => c.RawTimestamp),
                Confidence = ev.Confidence,
                Calls = calls
            }, ct);
            if (id > 0)
            {
                foreach (var call in calls)
                    claimedCallIds.Add(call.CallId);
                count++;
            }
        }

        return count;
    }

    public async Task<long> AddInsightWindowAsync(long start, long end, string summaryText, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO insight_windows (window_start, window_end, summary_text, generated_at_utc)
            VALUES ($window_start, $window_end, $summary_text, $generated_at_utc);
            SELECT id FROM insight_windows WHERE window_start=$window_start AND window_end=$window_end;
            """;
        Add(command, "$window_start", start);
        Add(command, "$window_end", end);
        Add(command, "$summary_text", summaryText);
        Add(command, "$generated_at_utc", DateTime.UtcNow.ToString("O"));
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public async Task ReplaceInsightEventsAsync(long windowId, IReadOnlyList<InsightEventRecordDto> events, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)tx;
            delete.CommandText = "DELETE FROM insight_events WHERE window_id=$window_id;";
            Add(delete, "$window_id", windowId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var ev in events)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO insight_events (window_id, title, detail, category, first_seen, last_seen, confidence, call_ids_json)
                VALUES ($window_id, $title, $detail, $category, $first_seen, $last_seen, $confidence, $call_ids_json);
                """;
            Add(insert, "$window_id", windowId);
            Add(insert, "$title", ev.Title);
            Add(insert, "$detail", ev.Detail);
            Add(insert, "$category", ev.Category);
            Add(insert, "$first_seen", ev.FirstSeen);
            Add(insert, "$last_seen", ev.LastSeen);
            Add(insert, "$confidence", ev.Confidence);
            Add(insert, "$call_ids_json", JsonSerializer.Serialize(ev.Calls.Select(c => c.CallId)));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<List<InsightEventRecordDto>> ListInsightEventsAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, detail, category, first_seen, last_seen, confidence, call_ids_json
            FROM insight_events
            WHERE last_seen >= $start AND first_seen <= $end
            ORDER BY last_seen DESC, confidence DESC
            LIMIT 1000;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        var rawRows = new List<(long Id, string Title, string Detail, string Category, long FirstSeen, long LastSeen, double Confidence, IReadOnlyList<long> CallIds)>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rawRows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetDouble(6),
                    ParseCallIds(reader.GetString(7))));
            }
        }

        var rows = new List<InsightEventRecordDto>();
        foreach (var row in rawRows)
        {
            rows.Add(new InsightEventRecordDto
            {
                Id = row.Id,
                Title = row.Title,
                Detail = row.Detail,
                Category = row.Category,
                FirstSeen = row.FirstSeen,
                LastSeen = row.LastSeen,
                Confidence = row.Confidence,
                Calls = await ListSpecificIncidentCallsAsync(connection, row.CallIds, ct)
            });
        }

        return rows;
    }

    public async Task<long> GetLatestInsightWindowEndAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(window_end), 0) FROM insight_windows;";
        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    public async Task<List<IncidentDto>> ListIncidentsAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, detail, first_seen, last_seen, incident_score
            FROM incidents
            WHERE last_seen >= $start AND first_seen <= $end
              AND (SELECT COUNT(*) FROM incident_calls ic WHERE ic.incident_id = incidents.id) >= 2
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
                Confidence = reader.GetDouble(5),
                Calls = await ListIncidentCallsAsync(connection, incidentId, ct)
            });
        }
        return incidents
            .Where(i => i.Calls.Count >= 2)
            .Select(i => i with { Category = DominantIncidentCategory(i.Calls) })
            .ToList();
    }

    private static string DominantIncidentCategory(IReadOnlyList<IncidentCallDto> calls) =>
        calls.GroupBy(c => string.IsNullOrWhiteSpace(c.Category) ? "other" : c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .FirstOrDefault() ?? "other";

    private static async Task<List<IncidentCallDto>> ListIncidentCallsAsync(SqliteConnection connection, long incidentId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id, c.start_time, c.transcription, COALESCE(c.category, 'other'), COALESCE(c.talkgroup_name, ''), COALESCE(c.system_short_name, '')
            FROM incident_calls ic
            JOIN calls c ON c.id = ic.call_id
            WHERE ic.incident_id = $incident_id
              AND c.transcription_status = 'complete'
              AND c.quality_reason = 'ok'
              AND length(trim(c.transcription)) > 0
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
                $"/api/v1/calls/{callId}/audio",
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return calls;
    }

    private static async Task<List<IncidentCallDto>> ListSpecificIncidentCallsAsync(SqliteConnection connection, IReadOnlyList<long> callIds, CancellationToken ct)
    {
        if (callIds.Count == 0)
            return [];

        var parameters = callIds.Select((_, i) => $"$id{i}").ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, start_time, transcription, COALESCE(category, 'other'), COALESCE(talkgroup_name, ''), COALESCE(system_short_name, '')
            FROM calls
            WHERE id IN ({string.Join(",", parameters)})
              AND transcription_status = 'complete'
              AND quality_reason = 'ok'
              AND length(trim(transcription)) > 0
            ORDER BY start_time ASC;
            """;
        for (var i = 0; i < callIds.Count; i++)
            Add(command, parameters[i], callIds[i]);

        var calls = new List<IncidentCallDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var callId = reader.GetInt64(0);
            calls.Add(new IncidentCallDto(
                callId,
                reader.GetInt64(1),
                reader.GetString(2),
                $"/api/v1/calls/{callId}/audio",
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5)));
        }
        return calls;
    }

    private static IncidentCandidateCall ToIncidentCandidateCall(IncidentCallDto call) =>
        new(call.CallId, call.RawTimestamp, call.Transcript, call.Category, call.TalkgroupName, call.SystemShortName);

    private static IReadOnlyList<long> ParseCallIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<long>>(json) ?? [];
        }
        catch
        {
            return [];
        }
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
        QualityReason = reader.GetString(reader.GetOrdinal("quality_reason")),
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

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, long start, long end, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$start", start);
        Add(command, "$end", end);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task<long> CountIsoAsync(SqliteConnection connection, string sql, long start, long end, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        Add(command, "$start", DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime.ToString("O"));
        Add(command, "$end", DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static TokenUsageEntryDto ReadLmUsage(SqliteDataReader reader) => new(
        reader.GetInt64(reader.GetOrdinal("id")),
        DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp_utc"))),
        reader.GetString(reader.GetOrdinal("trigger_activity")),
        reader.GetString(reader.GetOrdinal("request_kind")),
        reader.GetInt64(reader.GetOrdinal("success")) != 0,
        reader.GetString(reader.GetOrdinal("error")),
        reader.GetString(reader.GetOrdinal("endpoint")),
        reader.GetString(reader.GetOrdinal("request_model")),
        reader.GetString(reader.GetOrdinal("response_model")),
        reader.GetString(reader.GetOrdinal("finish_reason")),
        reader.GetInt32(reader.GetOrdinal("input_chars")),
        reader.GetInt32(reader.GetOrdinal("payload_chars")),
        reader.GetInt32(reader.GetOrdinal("prompt_tokens")),
        reader.GetInt32(reader.GetOrdinal("completion_tokens")),
        reader.GetInt32(reader.GetOrdinal("total_tokens")));

    private static TokenUsageBucketDto TokenBucket(string label, IEnumerable<TokenUsageEntryDto> rows)
    {
        var list = rows.ToList();
        return new TokenUsageBucketDto(
            label,
            list.Sum(r => (long)(r.TotalTokens > 0 ? r.TotalTokens : r.PromptTokens + r.CompletionTokens)),
            list.Sum(r => (long)r.PromptTokens),
            list.Sum(r => (long)r.CompletionTokens),
            list.Count);
    }

    private static TokenUsageSummaryDto SummarizeTokenUsage(IEnumerable<TokenUsageEntryDto> rows)
    {
        var list = rows.ToList();
        var prompt = list.Sum(r => (long)r.PromptTokens);
        var completion = list.Sum(r => (long)r.CompletionTokens);
        var total = list.Sum(r => (long)(r.TotalTokens > 0 ? r.TotalTokens : r.PromptTokens + r.CompletionTokens));
        return new TokenUsageSummaryDto(list.Count, list.Count(r => r.Success), list.Count(r => !r.Success), prompt, completion, total, (prompt / 1_000_000d * 2.00) + (completion / 1_000_000d * 8.00));
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureSchemaMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await AddColumnIfMissingAsync(connection, "incidents", "incident_score", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "calls", "quality_reason", "TEXT NOT NULL DEFAULT 'ok'", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "decode_rate_total", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "cc_summary_decode_lines", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "cc_summary_decode_zero", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "cc_summary_decode_rate_total", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "low_decode_warning_lines", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "low_decode_warning_zero", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "low_decode_warning_rate_total", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "update_not_grant", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "no_tx_recorded", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "recorder_exhausted", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "tuning_err_samples", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "tuning_err_total_abs_hz", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "tuning_err_max_abs_hz", "REAL NOT NULL DEFAULT 0", ct);
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS recommendation_states (
                recommendation_id TEXT PRIMARY KEY,
                snoozed_until_utc TEXT NOT NULL DEFAULT '',
                updated_at_utc TEXT NOT NULL
            );
            """, ct);
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS insight_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                window_id INTEGER NOT NULL,
                title TEXT NOT NULL,
                detail TEXT NOT NULL,
                category TEXT NOT NULL DEFAULT 'other',
                first_seen INTEGER NOT NULL,
                last_seen INTEGER NOT NULL,
                confidence REAL NOT NULL DEFAULT 0,
                call_ids_json TEXT NOT NULL DEFAULT '[]',
                FOREIGN KEY(window_id) REFERENCES insight_windows(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS idx_insight_events_time ON insight_events(last_seen DESC, confidence DESC);
            CREATE TABLE IF NOT EXISTS diagnostic_results (
                job_id INTEGER PRIMARY KEY,
                tool TEXT NOT NULL,
                result_json TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS lm_usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                trigger_activity TEXT NOT NULL DEFAULT 'other',
                request_kind TEXT NOT NULL DEFAULT 'chat.completions',
                success INTEGER NOT NULL DEFAULT 0,
                error TEXT NOT NULL DEFAULT '',
                endpoint TEXT NOT NULL DEFAULT '',
                request_model TEXT NOT NULL DEFAULT '',
                response_model TEXT NOT NULL DEFAULT '',
                finish_reason TEXT NOT NULL DEFAULT '',
                input_chars INTEGER NOT NULL DEFAULT 0,
                payload_chars INTEGER NOT NULL DEFAULT 0,
                prompt_tokens INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                total_tokens INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_lm_usage_time ON lm_usage(timestamp_utc DESC);

            CREATE TABLE IF NOT EXISTS job_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id INTEGER NOT NULL,
                timestamp_utc TEXT NOT NULL,
                stream TEXT NOT NULL DEFAULT 'info',
                text TEXT NOT NULL DEFAULT '',
                FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_job_logs_job ON job_logs(job_id, id);
            """, ct);
        await ExecuteNonQueryAsync(connection, CallAnnotationsSchemaSql, ct);
        await ExecuteNonQueryAsync(connection, CallLocationsSchemaSql, ct);
        await BackfillTranscriptionQualityAsync(connection, ct);
        await NormalizeTrHealthScopesAsync(connection, ct);
        await RemoveDuplicateIncidentCallLinksAsync(connection, ct);
        await RemoveInvalidIncidentLinksAsync(connection, ct);
    }

    private static async Task AddColumnIfMissingAsync(SqliteConnection connection, string table, string column, string definition, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        try
        {
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private async Task BackfillTranscriptionQualityAsync(SqliteConnection connection, CancellationToken ct)
    {
        var updates = new List<(long Id, string Status, string Reason)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = """
                SELECT id, transcription, transcription_status, quality_reason
                FROM calls
                WHERE transcription_status IN ('complete', 'failed', 'poor_quality')
                   OR (transcription_status = 'pending' AND length(trim(transcription)) > 0);
                """;
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                var transcript = reader.GetString(1);
                var status = reader.GetString(2);
                var reason = reader.GetString(3);
                var quality = TranscriptionQualityClassifier.Classify(transcript, status);
                if (!string.Equals(status, quality.Status, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(reason, quality.Reason, StringComparison.OrdinalIgnoreCase))
                {
                    updates.Add((id, quality.Status, quality.Reason));
                }
            }
        }

        foreach (var update in updates)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE calls
                SET transcription_status=$status,
                    quality_reason=$reason,
                    updated_at_utc=$now
                WHERE id=$id;
                """;
            Add(command, "$status", update.Status);
            Add(command, "$reason", update.Reason);
            Add(command, "$now", DateTime.UtcNow.ToString("O"));
            Add(command, "$id", update.Id);
            await command.ExecuteNonQueryAsync(ct);
        }

        if (updates.Count > 0)
        {
            var byReason = updates
                .GroupBy(u => u.Reason)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Key}:{g.Count():N0}");
            _logger.LogInformation("Reclassified {Count:N0} existing calls for transcription quality ({Reasons})", updates.Count, string.Join(", ", byReason));
        }
    }

    private async Task NormalizeTrHealthScopesAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using (var deleteEmptyWhitespaceScopes = connection.CreateCommand())
        {
            deleteEmptyWhitespaceScopes.CommandText = """
                DELETE FROM tr_health_samples
                WHERE scope != trim(scope)
                  AND decode_lines = 0
                  AND decode_zero = 0
                  AND decode_rate_total = 0
                  AND cc_summary_decode_lines = 0
                  AND cc_summary_decode_zero = 0
                  AND cc_summary_decode_rate_total = 0
                  AND low_decode_warning_lines = 0
                  AND low_decode_warning_zero = 0
                  AND low_decode_warning_rate_total = 0
                  AND retunes = 0
                  AND calls_started = 0
                  AND calls_concluded = 0
                  AND update_not_grant = 0
                  AND no_tx_recorded = 0
                  AND recorder_exhausted = 0
                  AND sample_stops = 0
                  AND unable_source = 0
                  AND tuning_err_samples = 0;
                """;
            var removed = await deleteEmptyWhitespaceScopes.ExecuteNonQueryAsync(ct);
            if (removed > 0)
                _logger.LogInformation("Removed {Count:N0} empty TR health sample(s) with whitespace-padded scope names", removed);
        }

        await using var trimScopes = connection.CreateCommand();
        trimScopes.CommandText = """
            UPDATE tr_health_samples
            SET scope = trim(scope)
            WHERE scope != trim(scope);
            """;
        var normalized = await trimScopes.ExecuteNonQueryAsync(ct);
        if (normalized > 0)
            _logger.LogInformation("Normalized {Count:N0} TR health sample scope name(s)", normalized);
    }

    private async Task RemoveInvalidIncidentLinksAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using (var deleteLinks = connection.CreateCommand())
        {
            deleteLinks.CommandText = """
                DELETE FROM incident_calls
                WHERE call_id IN (
                    SELECT id
                    FROM calls
                    WHERE transcription_status != 'complete'
                       OR quality_reason != 'ok'
                       OR transcription IS NULL
                       OR length(trim(transcription)) = 0
                );
                """;
            var removedLinks = await deleteLinks.ExecuteNonQueryAsync(ct);
            if (removedLinks > 0)
                _logger.LogInformation("Removed {Count:N0} invalid incident call link(s) whose calls are not eligible for incidents", removedLinks);
        }

        await using (var deleteIncidents = connection.CreateCommand())
        {
            deleteIncidents.CommandText = """
                DELETE FROM incidents
                WHERE (
                    SELECT COUNT(*)
                    FROM incident_calls ic
                    WHERE ic.incident_id = incidents.id
                ) < 2;
                """;
            var removedIncidents = await deleteIncidents.ExecuteNonQueryAsync(ct);
            if (removedIncidents > 0)
                _logger.LogInformation("Removed {Count:N0} incident(s) left with fewer than two valid source calls", removedIncidents);
        }
    }

    private async Task RemoveDuplicateIncidentCallLinksAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using (var deleteDuplicateLinks = connection.CreateCommand())
        {
            deleteDuplicateLinks.CommandText = """
                DELETE FROM incident_calls
                WHERE rowid NOT IN (
                    SELECT kept_rowid
                    FROM (
                        SELECT
                            ic.rowid AS kept_rowid,
                            ROW_NUMBER() OVER (
                                PARTITION BY ic.call_id
                                ORDER BY i.incident_score DESC, i.last_seen DESC, i.id DESC
                            ) AS rn
                        FROM incident_calls ic
                        JOIN incidents i ON i.id = ic.incident_id
                    )
                    WHERE rn = 1
                );
                """;
            var removed = await deleteDuplicateLinks.ExecuteNonQueryAsync(ct);
            if (removed > 0)
                _logger.LogInformation("Removed {Count:N0} duplicate incident call link(s); each call may belong to only one incident", removed);
        }

        await using (var deleteIncidents = connection.CreateCommand())
        {
            deleteIncidents.CommandText = """
                DELETE FROM incidents
                WHERE (
                    SELECT COUNT(*)
                    FROM incident_calls ic
                    WHERE ic.incident_id = incidents.id
                ) < 2;
                """;
            var removedIncidents = await deleteIncidents.ExecuteNonQueryAsync(ct);
            if (removedIncidents > 0)
                _logger.LogInformation("Removed {Count:N0} incident(s) left with fewer than two source calls after duplicate-link cleanup", removedIncidents);
        }
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
            quality_reason TEXT NOT NULL DEFAULT 'ok',
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

        CREATE TABLE IF NOT EXISTS call_annotations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id INTEGER NOT NULL,
            kind TEXT NOT NULL,
            code TEXT NOT NULL DEFAULT '',
            normalized_code TEXT NOT NULL DEFAULT '',
            matched_text TEXT NOT NULL DEFAULT '',
            meaning TEXT NOT NULL DEFAULT '',
            confidence REAL NOT NULL DEFAULT 0,
            source TEXT NOT NULL DEFAULT '',
            details_json TEXT NOT NULL DEFAULT '{}',
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_call_annotations_call ON call_annotations(call_id, kind);

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

        CREATE TABLE IF NOT EXISTS geocode_cache (
            cache_key TEXT PRIMARY KEY,
            provider TEXT NOT NULL,
            query TEXT NOT NULL,
            area_id TEXT NOT NULL,
            location_text TEXT NOT NULL,
            display_name TEXT NOT NULL,
            precision TEXT NOT NULL,
            confidence REAL NOT NULL DEFAULT 0,
            latitude REAL NOT NULL,
            longitude REAL NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_geocode_cache_area ON geocode_cache(area_id, location_text);

        CREATE TABLE IF NOT EXISTS call_locations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id INTEGER NOT NULL,
            area_id TEXT NOT NULL,
            area_label TEXT NOT NULL,
            system_short_name TEXT NOT NULL,
            location_text TEXT NOT NULL,
            normalized_key TEXT NOT NULL,
            geocode_cache_key TEXT NOT NULL,
            source TEXT NOT NULL DEFAULT 'transcription',
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_call_locations_call ON call_locations(call_id);
        CREATE INDEX IF NOT EXISTS idx_call_locations_area_key ON call_locations(area_id, normalized_key);
        CREATE INDEX IF NOT EXISTS idx_call_locations_geocode ON call_locations(geocode_cache_key);

        CREATE TABLE IF NOT EXISTS incidents (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            title TEXT NOT NULL,
            detail TEXT NOT NULL,
            first_seen INTEGER NOT NULL,
            last_seen INTEGER NOT NULL,
            incident_score REAL NOT NULL DEFAULT 0,
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

        CREATE UNIQUE INDEX IF NOT EXISTS idx_insight_windows_range ON insight_windows(window_start, window_end);

        CREATE TABLE IF NOT EXISTS insight_events (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            window_id INTEGER NOT NULL,
            title TEXT NOT NULL,
            detail TEXT NOT NULL,
            category TEXT NOT NULL DEFAULT 'other',
            first_seen INTEGER NOT NULL,
            last_seen INTEGER NOT NULL,
            confidence REAL NOT NULL DEFAULT 0,
            call_ids_json TEXT NOT NULL DEFAULT '[]',
            FOREIGN KEY(window_id) REFERENCES insight_windows(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_insight_events_time ON insight_events(last_seen DESC, confidence DESC);

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
            decode_rate_total REAL NOT NULL DEFAULT 0,
            cc_summary_decode_lines INTEGER NOT NULL DEFAULT 0,
            cc_summary_decode_zero INTEGER NOT NULL DEFAULT 0,
            cc_summary_decode_rate_total REAL NOT NULL DEFAULT 0,
            low_decode_warning_lines INTEGER NOT NULL DEFAULT 0,
            low_decode_warning_zero INTEGER NOT NULL DEFAULT 0,
            low_decode_warning_rate_total REAL NOT NULL DEFAULT 0,
            retunes INTEGER NOT NULL DEFAULT 0,
            calls_started INTEGER NOT NULL DEFAULT 0,
            calls_concluded INTEGER NOT NULL DEFAULT 0,
            update_not_grant INTEGER NOT NULL DEFAULT 0,
            no_tx_recorded INTEGER NOT NULL DEFAULT 0,
            recorder_exhausted INTEGER NOT NULL DEFAULT 0,
            sample_stops INTEGER NOT NULL DEFAULT 0,
            unable_source INTEGER NOT NULL DEFAULT 0,
            tuning_err_samples INTEGER NOT NULL DEFAULT 0,
            tuning_err_total_abs_hz REAL NOT NULL DEFAULT 0,
            tuning_err_max_abs_hz REAL NOT NULL DEFAULT 0
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

        CREATE TABLE IF NOT EXISTS job_logs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            job_id INTEGER NOT NULL,
            timestamp_utc TEXT NOT NULL,
            stream TEXT NOT NULL DEFAULT 'info',
            text TEXT NOT NULL DEFAULT '',
            FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_job_logs_job ON job_logs(job_id, id);

        CREATE TABLE IF NOT EXISTS diagnostic_results (
            job_id INTEGER PRIMARY KEY,
            tool TEXT NOT NULL,
            result_json TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS lm_usage (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            trigger_activity TEXT NOT NULL DEFAULT 'other',
            request_kind TEXT NOT NULL DEFAULT 'chat.completions',
            success INTEGER NOT NULL DEFAULT 0,
            error TEXT NOT NULL DEFAULT '',
            endpoint TEXT NOT NULL DEFAULT '',
            request_model TEXT NOT NULL DEFAULT '',
            response_model TEXT NOT NULL DEFAULT '',
            finish_reason TEXT NOT NULL DEFAULT '',
            input_chars INTEGER NOT NULL DEFAULT 0,
            payload_chars INTEGER NOT NULL DEFAULT 0,
            prompt_tokens INTEGER NOT NULL DEFAULT 0,
            completion_tokens INTEGER NOT NULL DEFAULT 0,
            total_tokens INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_lm_usage_time ON lm_usage(timestamp_utc DESC);

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

    private const string CallAnnotationsSchemaSql = """
        CREATE TABLE IF NOT EXISTS call_annotations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id INTEGER NOT NULL,
            kind TEXT NOT NULL,
            code TEXT NOT NULL DEFAULT '',
            normalized_code TEXT NOT NULL DEFAULT '',
            matched_text TEXT NOT NULL DEFAULT '',
            meaning TEXT NOT NULL DEFAULT '',
            confidence REAL NOT NULL DEFAULT 0,
            source TEXT NOT NULL DEFAULT '',
            details_json TEXT NOT NULL DEFAULT '{}',
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_call_annotations_call ON call_annotations(call_id, kind);
        """;

    private const string CallLocationsSchemaSql = """
        CREATE TABLE IF NOT EXISTS call_locations (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id INTEGER NOT NULL,
            area_id TEXT NOT NULL,
            area_label TEXT NOT NULL,
            system_short_name TEXT NOT NULL,
            location_text TEXT NOT NULL,
            normalized_key TEXT NOT NULL,
            geocode_cache_key TEXT NOT NULL,
            source TEXT NOT NULL DEFAULT 'transcription',
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_call_locations_call ON call_locations(call_id);
        CREATE INDEX IF NOT EXISTS idx_call_locations_area_key ON call_locations(area_id, normalized_key);
        CREATE INDEX IF NOT EXISTS idx_call_locations_geocode ON call_locations(geocode_cache_key);
        """;
}
