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

    public Task UpdateCallTranscriptionAsync(long callId, string transcription, string status, string qualityReason, bool isAlertMatch, CancellationToken ct) =>
        UpdateCallTranscriptionAsync(callId, transcription, status, qualityReason, isAlertMatch, null, ct);

    public async Task UpdateCallTranscriptionAsync(long callId, string transcription, string status, string qualityReason, bool isAlertMatch, string? rawMetadataJson, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = rawMetadataJson == null
            ? """
                UPDATE calls
                SET transcription=$transcription, transcription_status=$status, quality_reason=$quality_reason, is_alert_match=$is_alert_match, updated_at_utc=$now
                WHERE id=$id;
                """
            : """
                UPDATE calls
                SET transcription=$transcription, transcription_status=$status, quality_reason=$quality_reason, is_alert_match=$is_alert_match, raw_metadata_json=$raw_metadata_json, updated_at_utc=$now
                WHERE id=$id;
                """;
        Add(command, "$transcription", transcription);
        Add(command, "$status", status);
        Add(command, "$quality_reason", qualityReason);
        Add(command, "$is_alert_match", isAlertMatch ? 1 : 0);
        if (rawMetadataJson != null)
            Add(command, "$raw_metadata_json", rawMetadataJson);
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

    public async Task ReplaceCallAnchorsAsync(long callId, IReadOnlyList<CallAnchorRecord> anchors, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)tx;
            delete.CommandText = "DELETE FROM call_anchors WHERE call_id=$call_id AND source <> 'model_event_location';";
            Add(delete, "$call_id", callId);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var anchor in anchors)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO call_anchors (
                    call_id, kind, value, display_text, source, confidence, details_json, created_at_utc)
                VALUES (
                    $call_id, $kind, $value, $display_text, $source, $confidence, $details_json, $created_at_utc);
                """;
            Add(insert, "$call_id", callId);
            Add(insert, "$kind", anchor.Kind);
            Add(insert, "$value", anchor.Value);
            Add(insert, "$display_text", anchor.DisplayText);
            Add(insert, "$source", anchor.Source);
            Add(insert, "$confidence", anchor.Confidence);
            Add(insert, "$details_json", string.IsNullOrWhiteSpace(anchor.DetailsJson) ? "{}" : anchor.DetailsJson);
            Add(insert, "$created_at_utc", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task ReplaceCallAnchorsBySourceAsync(IReadOnlyCollection<long> callIds, string source, IReadOnlyList<CallAnchorRecord> anchors, CancellationToken ct)
    {
        if (callIds.Count == 0)
            return;

        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        var parameters = callIds.Select((_, i) => $"$id{i}").ToArray();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)tx;
            delete.CommandText = $"""
                DELETE FROM call_anchors
                WHERE source=$source
                  AND call_id IN ({string.Join(",", parameters)});
                """;
            Add(delete, "$source", source);
            var i = 0;
            foreach (var id in callIds)
                Add(delete, parameters[i++], id);
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var anchor in anchors.Where(anchor => string.Equals(anchor.Source, source, StringComparison.OrdinalIgnoreCase)))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO call_anchors (
                    call_id, kind, value, display_text, source, confidence, details_json, created_at_utc)
                VALUES (
                    $call_id, $kind, $value, $display_text, $source, $confidence, $details_json, $created_at_utc);
                """;
            Add(insert, "$call_id", anchor.CallId);
            Add(insert, "$kind", anchor.Kind);
            Add(insert, "$value", anchor.Value);
            Add(insert, "$display_text", anchor.DisplayText);
            Add(insert, "$source", anchor.Source);
            Add(insert, "$confidence", anchor.Confidence);
            Add(insert, "$details_json", string.IsNullOrWhiteSpace(anchor.DetailsJson) ? "{}" : anchor.DetailsJson);
            Add(insert, "$created_at_utc", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task MarkCallPostProcessedAsync(long callId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO call_post_processing (call_id, completed_at_utc)
            VALUES ($call_id, $completed_at_utc)
            ON CONFLICT(call_id) DO UPDATE SET completed_at_utc=excluded.completed_at_utc;
            """;
        Add(command, "$call_id", callId);
        Add(command, "$completed_at_utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>>> GetCallAnchorsAsync(IReadOnlyCollection<long> callIds, CancellationToken ct)
    {
        if (callIds.Count == 0)
            return new Dictionary<long, IReadOnlyList<CallAnchorRecord>>();

        await using var connection = OpenConnection();
        var parameters = callIds.Select((_, i) => $"$id{i}").ToArray();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT call_id, kind, value, display_text, source, confidence, details_json
            FROM call_anchors
            WHERE call_id IN ({string.Join(",", parameters)})
            ORDER BY call_id, confidence DESC, kind;
            """;
        var i = 0;
        foreach (var id in callIds)
            Add(command, parameters[i++], id);

        var rows = new Dictionary<long, List<CallAnchorRecord>>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var callId = reader.GetInt64(0);
            if (!rows.TryGetValue(callId, out var anchors))
            {
                anchors = [];
                rows[callId] = anchors;
            }

            anchors.Add(new CallAnchorRecord(
                callId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetDouble(5),
                reader.GetString(6)));
        }

        return rows.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<CallAnchorRecord>)kvp.Value);
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

    public async Task<Dictionary<string, RecommendationBaselineDto>> UpdateRecommendationBaselinesAsync(IReadOnlyDictionary<string, string> activeValues, IReadOnlyCollection<string> baselineEligibleIds, DateTime nowUtc, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var active = activeValues.Keys.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in baselineEligibleIds.Where(id => !active.Contains(id)))
        {
            await using var clear = connection.CreateCommand();
            clear.Transaction = (SqliteTransaction)transaction;
            clear.CommandText = "DELETE FROM recommendation_baselines WHERE recommendation_id=$id;";
            Add(clear, "$id", id);
            await clear.ExecuteNonQueryAsync(ct);
        }

        foreach (var id in active.Where(id => baselineEligibleIds.Contains(id, StringComparer.OrdinalIgnoreCase)))
        {
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = (SqliteTransaction)transaction;
            upsert.CommandText = """
                INSERT INTO recommendation_baselines (recommendation_id, first_seen_utc, last_seen_utc, active_observations, baseline_value)
                VALUES ($id, $now, $now, 1, $value)
                ON CONFLICT(recommendation_id) DO UPDATE SET
                    last_seen_utc=excluded.last_seen_utc,
                    active_observations=recommendation_baselines.active_observations + 1,
                    baseline_value=excluded.baseline_value;
                """;
            Add(upsert, "$id", id);
            Add(upsert, "$now", nowUtc.ToString("O"));
            Add(upsert, "$value", activeValues.TryGetValue(id, out var value) ? value : string.Empty);
            await upsert.ExecuteNonQueryAsync(ct);
        }

        var rows = new Dictionary<string, RecommendationBaselineDto>(StringComparer.OrdinalIgnoreCase);
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = (SqliteTransaction)transaction;
            query.CommandText = "SELECT recommendation_id, first_seen_utc, last_seen_utc, active_observations, baseline_value FROM recommendation_baselines;";
            await using var reader = await query.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                rows[id] = new RecommendationBaselineDto(
                    id,
                    DateTime.Parse(reader.GetString(1)).ToUniversalTime(),
                    DateTime.Parse(reader.GetString(2)).ToUniversalTime(),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4));
            }
        }

        await transaction.CommitAsync(ct);
        return rows;
    }

    public async Task ClearRecommendationBaselineAsync(string recommendationId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM recommendation_baselines WHERE recommendation_id=$id;";
        Add(command, "$id", recommendationId);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAlertMatchAsync(AlertMatchDto match, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO alert_matches (call_id, rule_name, detail, matched_at, is_imported, notification_suppressed, dismissed_at_utc)
            VALUES ($call_id, $rule_name, $detail, $matched_at, $is_imported, $notification_suppressed, $dismissed_at_utc);
            """;
        Add(command, "$call_id", match.CallId);
        Add(command, "$rule_name", match.RuleName);
        Add(command, "$detail", match.Detail);
        Add(command, "$matched_at", match.MatchedAt);
        Add(command, "$is_imported", match.IsImported ? 1 : 0);
        Add(command, "$notification_suppressed", match.NotificationSuppressed ? 1 : 0);
        Add(command, "$dismissed_at_utc", match.DismissedAtUtc ?? string.Empty);
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

    public async Task AddEvidenceVerifierRunAsync(EvidenceVerifierRunDto entry, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO evidence_verifier_runs (
                timestamp_utc, system_short_name, incident_key, title, selected_calls, reviewed_calls,
                model_reviewed_calls, truncated_calls, added_calls, dropped_calls, retained_calls, success, error)
            VALUES (
                $timestamp_utc, $system_short_name, $incident_key, $title, $selected_calls, $reviewed_calls,
                $model_reviewed_calls, $truncated_calls, $added_calls, $dropped_calls, $retained_calls, $success, $error);
            """;
        Add(command, "$timestamp_utc", entry.TimestampUtc.ToString("O"));
        Add(command, "$system_short_name", entry.SystemShortName);
        Add(command, "$incident_key", entry.IncidentKey);
        Add(command, "$title", entry.Title);
        Add(command, "$selected_calls", entry.SelectedCalls);
        Add(command, "$reviewed_calls", entry.ReviewedCalls);
        Add(command, "$model_reviewed_calls", entry.ModelReviewedCalls);
        Add(command, "$truncated_calls", entry.TruncatedCalls);
        Add(command, "$added_calls", entry.AddedCalls);
        Add(command, "$dropped_calls", entry.DroppedCalls);
        Add(command, "$retained_calls", entry.RetainedCalls);
        Add(command, "$success", entry.Success ? 1 : 0);
        Add(command, "$error", entry.Error);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AddIncidentOperationAuditAsync(IncidentOperationAuditDto entry, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO incident_operation_audit (
                timestamp_utc, system_short_name, incident_key, operation, accepted, reason, score, call_ids_json, metadata_json)
            VALUES (
                $timestamp_utc, $system_short_name, $incident_key, $operation, $accepted, $reason, $score, $call_ids_json, $metadata_json);
            """;
        Add(command, "$timestamp_utc", entry.TimestampUtc.ToUniversalTime().ToString("O"));
        Add(command, "$system_short_name", entry.SystemShortName);
        Add(command, "$incident_key", entry.IncidentKey);
        Add(command, "$operation", entry.Operation);
        Add(command, "$accepted", entry.Accepted ? 1 : 0);
        Add(command, "$reason", entry.Reason);
        Add(command, "$score", entry.Score);
        Add(command, "$call_ids_json", entry.CallIdsJson);
        Add(command, "$metadata_json", entry.MetadataJson);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<IncidentOperationAuditRowDto>> ListIncidentOperationAuditAsync(DateTime sinceUtc, int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, timestamp_utc, system_short_name, incident_key, operation, accepted, reason, score, call_ids_json, metadata_json
            FROM incident_operation_audit
            WHERE timestamp_utc >= $since
            ORDER BY timestamp_utc DESC, id DESC
            LIMIT $limit;
            """;
        Add(command, "$since", sinceUtc.ToUniversalTime().ToString("O"));
        Add(command, "$limit", Math.Clamp(limit, 1, 250));
        var rows = new List<IncidentOperationAuditRowDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var callIdsJson = reader.GetString(reader.GetOrdinal("call_ids_json"));
            rows.Add(new IncidentOperationAuditRowDto(
                reader.GetInt64(reader.GetOrdinal("id")),
                DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp_utc")), null, System.Globalization.DateTimeStyles.RoundtripKind),
                reader.GetString(reader.GetOrdinal("system_short_name")),
                reader.GetString(reader.GetOrdinal("incident_key")),
                reader.GetString(reader.GetOrdinal("operation")),
                reader.GetInt32(reader.GetOrdinal("accepted")) != 0,
                reader.GetString(reader.GetOrdinal("reason")),
                reader.GetDouble(reader.GetOrdinal("score")),
                ParseCallIds(callIdsJson),
                reader.GetString(reader.GetOrdinal("metadata_json"))));
        }
        return rows;
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

    public async Task<List<EngineCall>> ListCallsByIdsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var idList = ids.Distinct().Take(200).ToList();
        if (idList.Count == 0)
            return [];

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        var parameters = idList.Select((_, i) => $"$id{i}").ToList();
        command.CommandText = $"SELECT * FROM calls WHERE id IN ({string.Join(",", parameters)});";
        for (var i = 0; i < idList.Count; i++)
            Add(command, parameters[i], idList[i]);
        var rows = new List<EngineCall>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(ReadCall(reader));
        return rows;
    }

    public async Task UpsertEmbeddingJobAsync(long callId, string status, string error, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO call_embedding_jobs (call_id, status, attempts, error, created_at_utc, updated_at_utc)
            VALUES ($call_id, $status, 0, $error, $now, $now)
            ON CONFLICT(call_id) DO UPDATE SET
                status=excluded.status,
                error=excluded.error,
                updated_at_utc=excluded.updated_at_utc
            WHERE call_embedding_jobs.status <> 'embedded';
            """;
        Add(command, "$call_id", callId);
        Add(command, "$status", status);
        Add(command, "$error", error);
        Add(command, "$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<EmbeddingJobDto>> ListPendingEmbeddingJobsAsync(int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT call_id, status, attempts, error, updated_at_utc
            FROM call_embedding_jobs
            WHERE status = 'pending'
               OR (status = 'failed' AND attempts < 5 AND updated_at_utc <= $retry_before)
            ORDER BY CASE status WHEN 'pending' THEN 0 ELSE 1 END, updated_at_utc ASC
            LIMIT $limit;
            """;
        Add(command, "$limit", Math.Clamp(limit, 1, 5000));
        Add(command, "$retry_before", DateTime.UtcNow.AddMinutes(-2).ToString("O"));
        var rows = new List<EmbeddingJobDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new EmbeddingJobDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                DateTime.Parse(reader.GetString(4)).ToUniversalTime()));
        }
        return rows;
    }

    public async Task MarkEmbeddingJobAsync(long callId, string status, string error, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE call_embedding_jobs
            SET status=$status,
                attempts=attempts + 1,
                error=$error,
                updated_at_utc=$now
            WHERE call_id=$call_id;
            """;
        Add(command, "$call_id", callId);
        Add(command, "$status", status);
        Add(command, "$error", error);
        Add(command, "$now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<(long Pending, long Embedded, long Failed, DateTime? OldestPending)> GetEmbeddingJobStatsAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END), 0) AS pending,
                COALESCE(SUM(CASE WHEN status='embedded' THEN 1 ELSE 0 END), 0) AS embedded,
                COALESCE(SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END), 0) AS failed,
                COALESCE(MIN(CASE WHEN status='pending' THEN updated_at_utc ELSE NULL END), '') AS oldest_pending
            FROM call_embedding_jobs;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return (0, 0, 0, null);
        var oldest = reader.GetString(3);
        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            string.IsNullOrWhiteSpace(oldest) ? null : DateTime.Parse(oldest).ToUniversalTime());
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

    public async Task<Dictionary<string, IReadOnlyList<long>>> ListObservedCallFrequenciesBySystemAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT system_short_name, frequency
            FROM calls
            WHERE COALESCE(system_short_name, '') <> ''
              AND frequency > 0
            GROUP BY system_short_name, frequency
            ORDER BY system_short_name COLLATE NOCASE ASC, frequency ASC;
            """;
        var bySystem = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var system = reader.GetString(0);
            var frequency = NormalizeFrequencyHz((long)Math.Round(reader.GetDouble(1)));
            if (frequency <= 0)
                continue;
            if (!bySystem.TryGetValue(system, out var frequencies))
            {
                frequencies = [];
                bySystem[system] = frequencies;
            }
            frequencies.Add(frequency);
        }

        return bySystem.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<long>)kvp.Value.Distinct().Order().ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<List<CategoryGroupDto>> ListCategoryTalkgroupsAsync(long start, long end, string category, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                talkgroup,
                COALESCE(NULLIF(talkgroup_name, ''), 'TG ' || talkgroup) AS label,
                COUNT(*) AS call_count,
                MAX(start_time) AS last_heard
            FROM calls
            WHERE start_time >= $start AND start_time <= $end
              AND category = $category
            GROUP BY talkgroup, label
            ORDER BY label COLLATE NOCASE ASC;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        Add(command, "$category", category);
        var groups = new List<CategoryGroupDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            groups.Add(new CategoryGroupDto(
                reader.GetString(reader.GetOrdinal("label")),
                [],
                reader.GetInt64(reader.GetOrdinal("talkgroup")),
                reader.GetInt32(reader.GetOrdinal("call_count")),
                reader.GetInt64(reader.GetOrdinal("last_heard"))));
        }
        return groups;
    }

    public async Task<List<EngineCall>> ListCategoryTalkgroupCallsAsync(long start, long end, string category, long talkgroup, int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM calls
            WHERE start_time >= $start AND start_time <= $end
              AND category = $category
              AND talkgroup = $talkgroup
            ORDER BY start_time DESC, id DESC
            LIMIT $limit;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        Add(command, "$category", category);
        Add(command, "$talkgroup", talkgroup);
        Add(command, "$limit", Math.Clamp(limit, 1, 500));
        var calls = new List<EngineCall>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            calls.Add(ReadCall(reader));
        return calls;
    }

    public async Task<List<EngineCall>> ListCategorySearchCallsAsync(long start, long end, string category, string query, CancellationToken ct)
    {
        var tokens = query
            .Trim()
            .ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .Take(8)
            .ToList();
        if (tokens.Count == 0)
            return [];

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        var filters = tokens.Select((_, index) => $"""
            (
                lower(COALESCE(talkgroup_name, '')) LIKE $q{index} ESCAPE '\'
                OR lower(COALESCE(transcription, '')) LIKE $q{index} ESCAPE '\'
                OR lower(COALESCE(system_short_name, '')) LIKE $q{index} ESCAPE '\'
                OR CAST(id AS TEXT) LIKE $q{index} ESCAPE '\'
                OR CAST(talkgroup AS TEXT) LIKE $q{index} ESCAPE '\'
            )
            """);
        command.CommandText = $"""
            SELECT * FROM calls
            WHERE start_time >= $start AND start_time <= $end
              AND category = $category
              AND {string.Join(" AND ", filters)}
            ORDER BY start_time DESC, id DESC;
            """;
        Add(command, "$start", start);
        Add(command, "$end", end);
        Add(command, "$category", category);
        for (var i = 0; i < tokens.Count; i++)
            Add(command, $"$q{i}", $"%{EscapeLike(tokens[i])}%");
        var calls = new List<EngineCall>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            calls.Add(ReadCall(reader));
        return calls;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

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

    public async Task<List<EngineCall>> ListCallsMissingPostProcessingAsync(int limit, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.* FROM calls c
            WHERE c.transcription_status='complete'
              AND c.quality_reason='ok'
              AND length(trim(c.transcription)) > 0
              AND NOT EXISTS (SELECT 1 FROM call_post_processing p WHERE p.call_id=c.id)
            ORDER BY c.start_time DESC, c.id DESC
            LIMIT $limit;
            """;
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
            SELECT am.id, am.call_id, am.rule_name, am.detail, am.matched_at, am.is_imported, am.notification_suppressed, COALESCE(am.dismissed_at_utc, ''),
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
                DismissedAtUtc = reader.GetString(7),
                SystemShortName = reader.GetString(8),
                Talkgroup = reader.GetInt64(9),
                TalkgroupName = reader.GetString(10),
                Category = reader.GetString(11),
                Transcription = reader.GetString(12),
                TranscriptionStatus = reader.GetString(13),
                QualityReason = reader.GetString(14),
                AudioUrl = $"/api/v1/calls/{reader.GetInt64(1)}/audio"
            });
        }
        return rows;
    }

    public async Task<int> DismissIncidentAlertsAsync(long incidentId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_matches
            SET dismissed_at_utc = $now
            WHERE COALESCE(dismissed_at_utc, '') = ''
              AND call_id IN (SELECT call_id FROM incident_calls WHERE incident_id = $incident_id);
            """;
        Add(command, "$now", DateTime.UtcNow.ToString("O"));
        Add(command, "$incident_id", incidentId);
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DismissAlertMatchAsync(long alertId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE alert_matches
            SET dismissed_at_utc = $now
            WHERE id = $id
              AND COALESCE(dismissed_at_utc, '') = '';
            """;
        Add(command, "$now", DateTime.UtcNow.ToString("O"));
        Add(command, "$id", alertId);
        return await command.ExecuteNonQueryAsync(ct);
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
        command.CommandText = "SELECT * FROM jobs WHERE created_at_utc >= $cutoff ORDER BY id DESC LIMIT 200;";
        Add(command, "$cutoff", DateTime.UtcNow.AddDays(-30).ToString("O"));
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

    public async Task<int> PruneJobsOlderThanAsync(DateTime cutoffUtc, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using (var logs = connection.CreateCommand())
        {
            logs.CommandText = """
                DELETE FROM job_logs
                WHERE job_id IN (
                    SELECT id FROM jobs
                    WHERE created_at_utc < $cutoff
                      AND status NOT IN ('queued', 'running', 'paused', 'canceling')
                );
                """;
            Add(logs, "$cutoff", cutoffUtc.ToUniversalTime().ToString("O"));
            await logs.ExecuteNonQueryAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM jobs
            WHERE created_at_utc < $cutoff
              AND status NOT IN ('queued', 'running', 'paused', 'canceling');
            """;
        Add(command, "$cutoff", cutoffUtc.ToUniversalTime().ToString("O"));
        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AddRfSurveySessionAsync(RfSurveySessionDto session, string profileJson, string toolPrepJson, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rf_survey_sessions (
                id, status, mode, site_label, system_short_name, verdict, stability,
                sdr_summary, rf_path_summary, best_control_channel, source_plan_summary,
                recommendation_state, artifact_path, profile_json, tool_prep_json,
                created_at_utc, updated_at_utc, completed_at_utc)
            VALUES (
                $id, $status, $mode, $site_label, $system_short_name, $verdict, $stability,
                $sdr_summary, $rf_path_summary, $best_control_channel, $source_plan_summary,
                $recommendation_state, $artifact_path, $profile_json, $tool_prep_json,
                $created_at_utc, $updated_at_utc, $completed_at_utc);
            """;
        Add(command, "$id", session.Id);
        AddRfSurveySessionParameters(command, session);
        Add(command, "$profile_json", profileJson);
        Add(command, "$tool_prep_json", toolPrepJson);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateRfSurveySessionAsync(RfSurveySessionDto session, string profileJson, string toolPrepJson, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE rf_survey_sessions
            SET status=$status,
                mode=$mode,
                site_label=$site_label,
                system_short_name=$system_short_name,
                verdict=$verdict,
                stability=$stability,
                sdr_summary=$sdr_summary,
                rf_path_summary=$rf_path_summary,
                best_control_channel=$best_control_channel,
                source_plan_summary=$source_plan_summary,
                recommendation_state=$recommendation_state,
                artifact_path=$artifact_path,
                profile_json=$profile_json,
                tool_prep_json=$tool_prep_json,
                updated_at_utc=$updated_at_utc,
                completed_at_utc=$completed_at_utc
            WHERE id=$id;
            """;
        Add(command, "$id", session.Id);
        AddRfSurveySessionParameters(command, session);
        Add(command, "$profile_json", profileJson);
        Add(command, "$tool_prep_json", toolPrepJson);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RfSurveySessionDto>> ListRfSurveySessionsAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, status, mode, site_label, system_short_name, verdict, stability,
                   sdr_summary, rf_path_summary, best_control_channel, source_plan_summary,
                   recommendation_state, artifact_path, created_at_utc, updated_at_utc, completed_at_utc
            FROM rf_survey_sessions
            ORDER BY updated_at_utc DESC, created_at_utc DESC
            LIMIT 200;
            """;
        var rows = new List<RfSurveySessionDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(ReadRfSurveySession(reader));
        return rows;
    }

    public async Task<(RfSurveySessionDto Session, string ProfileJson, string ToolPrepJson)?> GetRfSurveySessionAsync(string id, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, status, mode, site_label, system_short_name, verdict, stability,
                   sdr_summary, rf_path_summary, best_control_channel, source_plan_summary,
                   recommendation_state, artifact_path, created_at_utc, updated_at_utc, completed_at_utc,
                   profile_json, tool_prep_json
            FROM rf_survey_sessions
            WHERE id=$id;
            """;
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return (ReadRfSurveySession(reader), reader.GetString(reader.GetOrdinal("profile_json")), reader.GetString(reader.GetOrdinal("tool_prep_json")));
    }

    public async Task<bool> DeleteRfSurveySessionAsync(string id, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction();

        await using (var notes = connection.CreateCommand())
        {
            notes.Transaction = transaction;
            notes.CommandText = "DELETE FROM rf_survey_notes WHERE survey_id=$id;";
            Add(notes, "$id", id);
            await notes.ExecuteNonQueryAsync(ct);
        }

        await using (var experiments = connection.CreateCommand())
        {
            experiments.Transaction = transaction;
            experiments.CommandText = "DELETE FROM rf_survey_experiments WHERE survey_id=$id;";
            Add(experiments, "$id", id);
            await experiments.ExecuteNonQueryAsync(ct);
        }

        await using var session = connection.CreateCommand();
        session.Transaction = transaction;
        session.CommandText = "DELETE FROM rf_survey_sessions WHERE id=$id;";
        Add(session, "$id", id);
        var deleted = await session.ExecuteNonQueryAsync(ct);

        await transaction.CommitAsync(ct);
        return deleted > 0;
    }

    public async Task AddRfSurveyExperimentAsync(string surveyId, RfSurveyExperimentDto experiment, string evidenceJson, string interpretationJson, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rf_survey_experiments (
                id, survey_id, type, status, hypothesis, required_setup, result_summary,
                blocking_issue, evidence_json, interpretation_json, created_at_utc,
                started_at_utc, finished_at_utc)
            VALUES (
                $id, $survey_id, $type, $status, $hypothesis, $required_setup, $result_summary,
                $blocking_issue, $evidence_json, $interpretation_json, $created_at_utc,
                $started_at_utc, $finished_at_utc);
            """;
        Add(command, "$id", experiment.Id);
        Add(command, "$survey_id", surveyId);
        Add(command, "$type", experiment.Type);
        Add(command, "$status", experiment.Status);
        Add(command, "$hypothesis", experiment.Hypothesis);
        Add(command, "$required_setup", experiment.RequiredSetup);
        Add(command, "$result_summary", experiment.ResultSummary);
        Add(command, "$blocking_issue", experiment.BlockingIssue);
        Add(command, "$evidence_json", evidenceJson);
        Add(command, "$interpretation_json", interpretationJson);
        Add(command, "$created_at_utc", experiment.CreatedAtUtc.ToString("O"));
        Add(command, "$started_at_utc", experiment.StartedAtUtc?.ToString("O") ?? string.Empty);
        Add(command, "$finished_at_utc", experiment.FinishedAtUtc?.ToString("O") ?? string.Empty);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteRfSurveyExperimentsAsync(string surveyId, IReadOnlyCollection<string> types, CancellationToken ct)
    {
        if (types.Count == 0)
            return;

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        var parameters = types.Select((_, index) => $"$type{index}").ToArray();
        command.CommandText = $"DELETE FROM rf_survey_experiments WHERE survey_id=$survey_id AND type IN ({string.Join(",", parameters)});";
        Add(command, "$survey_id", surveyId);
        for (var i = 0; i < types.Count; i++)
            Add(command, parameters[i], types.ElementAt(i));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RfSurveyExperimentDto>> ListRfSurveyExperimentsAsync(string surveyId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, type, status, hypothesis, required_setup, result_summary, blocking_issue,
                   evidence_json, interpretation_json, created_at_utc, started_at_utc, finished_at_utc
            FROM rf_survey_experiments
            WHERE survey_id=$survey_id
            ORDER BY created_at_utc, id;
            """;
        Add(command, "$survey_id", surveyId);
        var rows = new List<RfSurveyExperimentDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new RfSurveyExperimentDto
            {
                Id = reader.GetString(reader.GetOrdinal("id")),
                Type = reader.GetString(reader.GetOrdinal("type")),
                Status = reader.GetString(reader.GetOrdinal("status")),
                Hypothesis = reader.GetString(reader.GetOrdinal("hypothesis")),
                RequiredSetup = reader.GetString(reader.GetOrdinal("required_setup")),
                ResultSummary = reader.GetString(reader.GetOrdinal("result_summary")),
                BlockingIssue = reader.GetString(reader.GetOrdinal("blocking_issue")),
                EvidenceJson = reader.GetString(reader.GetOrdinal("evidence_json")),
                InterpretationJson = reader.GetString(reader.GetOrdinal("interpretation_json")),
                CreatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
                StartedAtUtc = ParseDateOrNull(reader.GetString(reader.GetOrdinal("started_at_utc"))),
                FinishedAtUtc = ParseDateOrNull(reader.GetString(reader.GetOrdinal("finished_at_utc")))
            });
        }
        return rows;
    }

    public async Task AddRfSurveyNoteAsync(string surveyId, string text, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO rf_survey_notes (survey_id, text, created_at_utc)
            VALUES ($survey_id, $text, $created_at_utc);
            """;
        Add(command, "$survey_id", surveyId);
        Add(command, "$text", text);
        Add(command, "$created_at_utc", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RfSurveyNoteDto>> ListRfSurveyNotesAsync(string surveyId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT text, created_at_utc
            FROM rf_survey_notes
            WHERE survey_id=$survey_id
            ORDER BY created_at_utc;
            """;
        Add(command, "$survey_id", surveyId);
        var rows = new List<RfSurveyNoteDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new RfSurveyNoteDto(reader.GetString(0), DateTime.Parse(reader.GetString(1))));
        return rows;
    }

    public async Task VacuumAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await ExecuteNonQueryAsync(connection, "VACUUM;", ct);
    }

    public async Task AnalyzeAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await ExecuteNonQueryAsync(connection, "PRAGMA optimize;", ct);
        await ExecuteNonQueryAsync(connection, "ANALYZE;", ct);
    }

    public async Task ClearSiteDataForMigrationAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=OFF;", ct);
        var tables = new[]
        {
            "call_post_processing",
            "call_embedding_jobs",
            "call_anchors",
            "call_locations",
            "call_annotations",
            "alert_matches",
            "incident_calls",
            "incident_operation_audit",
            "evidence_verifier_runs",
            "incidents",
            "insight_events",
            "insight_windows",
            "lm_usage",
            "calls",
            "tr_health_samples",
            "geocode_cache",
            "recommendation_states",
            "recommendation_baselines",
            "job_logs",
            "jobs"
        };
        foreach (var table in tables)
            await ExecuteNonQueryAsync(connection, $"DELETE FROM {table};", ct);
        await ExecuteNonQueryAsync(connection, "DELETE FROM sqlite_sequence WHERE name IN (" + string.Join(",", tables.Select(t => $"'{t}'")) + ");", ct);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", ct);
        await ExecuteNonQueryAsync(connection, "VACUUM;", ct);
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
        var startUtc = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
        var endUtc = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime;
        Add(command, "$start", startUtc.ToString("O"));
        Add(command, "$end", endUtc.ToString("O"));
        var rows = new List<TokenUsageEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(ReadLmUsage(reader));
        var summary = await SummarizeTokenUsageAsync(connection, startUtc, endUtc, ct);
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var monthlySummary = await SummarizeTokenUsageAsync(connection, monthStart, null, ct);
        var allTimeSummary = await SummarizeTokenUsageAsync(connection, null, null, ct);
        var failuresByKind = TokenFailureBreakdown(rows);
        var byDay = rows.GroupBy(r => r.TimestampUtc.ToLocalTime().ToString("MM/dd"))
            .Select(g => TokenBucket(g.Key, g)).OrderBy(x => x.Label).ToList();
        var byTrigger = rows.GroupBy(r => string.IsNullOrWhiteSpace(r.TriggerActivity) ? "other" : r.TriggerActivity)
            .Select(g => TokenBucket(g.Key, g)).OrderByDescending(x => x.TotalTokens).ToList();
        return new TokenUsageReportDto("sqlite:lm_usage", summary, monthlySummary, allTimeSummary, failuresByKind, byDay, byTrigger, rows);
    }

    public async Task<AiCompletionHealthDto> GetAiCompletionHealthAsync(int windowMinutes, CancellationToken ct)
    {
        const int recoveredSuccessThreshold = 3;
        windowMinutes = Math.Clamp(windowMinutes, 1, 240);
        var now = DateTime.UtcNow;
        var rows = await ListLmUsageEntriesAsync(now.AddMinutes(-windowMinutes), now, ct);
        if (rows.Count == 0)
            return new AiCompletionHealthDto(WindowMinutes: windowMinutes);

        var summary = SummarizeTokenUsage(rows);
        var ordered = rows.OrderByDescending(r => r.TimestampUtc).ToList();
        var healthRows = ordered.Where(r => r.Success || !string.Equals(TokenFailureKind(r), "canceled", StringComparison.OrdinalIgnoreCase)).ToList();
        var latestFailure = healthRows.FirstOrDefault(r => !r.Success);
        var consecutiveFailures = healthRows.TakeWhile(r => !r.Success).Count();
        var successfulCompletionsAfterLatestFailure = latestFailure is null
            ? healthRows.Count(r => r.Success)
            : healthRows.TakeWhile(r => r.Success).Count();
        var recoveredAfterLatestFailure = latestFailure is not null &&
                                          consecutiveFailures == 0 &&
                                          successfulCompletionsAfterLatestFailure >= recoveredSuccessThreshold;
        var healthFailures = Math.Max(0, summary.Failures - summary.Canceled);
        var status = "ok";
        if (!recoveredAfterLatestFailure &&
            (summary.TimeoutFailures >= 2
             || summary.NoValidResultFailures >= 2
             || consecutiveFailures >= 2
             || (healthFailures >= 3 && healthFailures >= summary.Successes)))
        {
            status = "error";
        }
        else if (!recoveredAfterLatestFailure && healthFailures > 0)
        {
            status = "warning";
        }

        var message = status switch
        {
            "error" => $"AI completions are failing in the last {windowMinutes} minutes: {healthFailures:N0}/{summary.Requests:N0} request(s) failed, including {summary.TimeoutFailures:N0} timeout(s) and {summary.NoValidResultFailures:N0} no-valid-result failure(s). Queue backlog alone does not prove AI health.",
            "warning" => $"AI completions have recent failures: {healthFailures:N0}/{summary.Requests:N0} request(s) failed in the last {windowMinutes} minutes.",
            _ when recoveredAfterLatestFailure => $"AI completions recovered after the latest failure: {successfulCompletionsAfterLatestFailure:N0} successful request(s) since the last failure.",
            _ when summary.Canceled > 0 => $"AI completions are healthy across {summary.Requests:N0} recent request(s); {summary.Canceled:N0} canceled request(s) were ignored for queue blocking.",
            _ => $"AI completions are healthy across {summary.Requests:N0} recent request(s)."
        };

        return new AiCompletionHealthDto(
            status,
            message,
            windowMinutes,
            summary.Requests,
            summary.Failures,
            summary.TimeoutFailures,
            summary.NoValidResultFailures,
            consecutiveFailures,
            latestFailure?.TimestampUtc,
            latestFailure is null ? string.Empty : TokenFailureKind(latestFailure),
            latestFailure is null ? string.Empty : FailureExample(latestFailure));
    }

    public async Task<List<TokenUsageEntryDto>> ListLmUsageEntriesAsync(DateTime? startUtc, DateTime? endUtc, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM lm_usage
            WHERE ($start IS NULL OR timestamp_utc >= $start)
              AND ($end IS NULL OR timestamp_utc <= $end)
            ORDER BY timestamp_utc DESC;
            """;
        Add(command, "$start", startUtc.HasValue ? startUtc.Value.ToString("O") : DBNull.Value);
        Add(command, "$end", endUtc.HasValue ? endUtc.Value.ToString("O") : DBNull.Value);
        var rows = new List<TokenUsageEntryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(ReadLmUsage(reader));
        return rows;
    }

    public async Task<QualityCheckSnapshotDto> GetQualityCheckSnapshotAsync(long start, long end, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        var startUtc = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
        var endUtc = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime;
        var calls = await ReadSingleAsync(connection, """
            SELECT
                COUNT(*) AS total_calls,
                COALESCE(SUM(CASE WHEN stop_time > start_time THEN stop_time - start_time ELSE 0 END), 0) AS audio_seconds,
                COALESCE(SUM(CASE WHEN length(trim(COALESCE(transcription, ''))) < 30 THEN 1 ELSE 0 END), 0) AS short_transcript_calls,
                COALESCE(MIN(start_time), 0) AS oldest_start_time,
                COALESCE(MAX(start_time), 0) AS newest_start_time
            FROM calls
            WHERE start_time >= $start AND start_time <= $end;
            """,
            command =>
            {
                Add(command, "$start", start);
                Add(command, "$end", end);
            },
            reader => new QualityCheckCallSummaryDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)),
            ct) ?? new QualityCheckCallSummaryDto(0, 0, 0, 0, 0);

        var callsByCategory = await ReadListAsync(connection, """
            SELECT
                COALESCE(NULLIF(category, ''), 'other') AS category,
                COUNT(*) AS calls,
                COALESCE(SUM(CASE WHEN stop_time > start_time THEN stop_time - start_time ELSE 0 END), 0) AS audio_seconds
            FROM calls
            WHERE start_time >= $start AND start_time <= $end
            GROUP BY COALESCE(NULLIF(category, ''), 'other')
            ORDER BY calls DESC;
            """,
            command =>
            {
                Add(command, "$start", start);
                Add(command, "$end", end);
            },
            reader => new QualityCheckCategorySummaryDto(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)),
            ct);

        var transcriptQuality = await ReadListAsync(connection, """
            SELECT
                COALESCE(NULLIF(transcription_status, ''), 'unknown') AS status,
                COALESCE(NULLIF(quality_reason, ''), 'none') AS quality_reason,
                COUNT(*) AS calls
            FROM calls
            WHERE start_time >= $start AND start_time <= $end
            GROUP BY COALESCE(NULLIF(transcription_status, ''), 'unknown'), COALESCE(NULLIF(quality_reason, ''), 'none')
            ORDER BY calls DESC;
            """,
            command =>
            {
                Add(command, "$start", start);
                Add(command, "$end", end);
            },
            reader => new QualityCheckTranscriptSummaryDto(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)),
            ct);

        var ai = await ReadSingleAsync(connection, """
            SELECT
                COUNT(*) AS requests,
                COALESCE(SUM(CASE WHEN success != 0 THEN 1 ELSE 0 END), 0) AS successes,
                COALESCE(SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END), 0) AS failures,
                COALESCE(SUM(CASE WHEN finish_reason = 'length' THEN 1 ELSE 0 END), 0) AS truncated,
                COALESCE(SUM(prompt_tokens), 0) AS prompt_tokens,
                COALESCE(SUM(completion_tokens), 0) AS completion_tokens,
                COALESCE(MAX(timestamp_utc), '') AS latest_utc
            FROM lm_usage
            WHERE timestamp_utc >= $start AND timestamp_utc <= $end;
            """,
            command =>
            {
                Add(command, "$start", startUtc.ToString("O"));
                Add(command, "$end", endUtc.ToString("O"));
            },
            reader => new QualityCheckAiSummaryDto(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                ParseDateOrNull(reader.GetString(6))),
            ct) ?? new QualityCheckAiSummaryDto(0, 0, 0, 0, 0, 0, null);

        var evidenceVerifier = await ReadSingleAsync(connection, """
            SELECT
                COUNT(*) AS runs,
                COALESCE(AVG(reviewed_calls), 0) AS average_reviewed_calls,
                COALESCE(AVG(truncated_calls), 0) AS average_truncated_calls,
                COALESCE(MAX(truncated_calls), 0) AS max_truncated_calls,
                COALESCE(SUM(added_calls), 0) AS added_calls,
                COALESCE(SUM(dropped_calls), 0) AS dropped_calls,
                COALESCE(SUM(CASE
                    WHEN success = 1
                     AND retained_calls > 0
                     AND EXISTS (
                        SELECT 1
                        FROM incident_operation_audit ioa
                        WHERE ioa.timestamp_utc >= $start
                          AND ioa.timestamp_utc <= $end
                          AND ioa.incident_key = evidence_verifier_runs.incident_key
                          AND ioa.reason LIKE '%verifier retained%'
                     )
                    THEN 1 ELSE 0 END), 0) AS retention_mismatches
            FROM evidence_verifier_runs
            WHERE timestamp_utc >= $start AND timestamp_utc <= $end;
            """,
            command =>
            {
                Add(command, "$start", startUtc.ToString("O"));
                Add(command, "$end", endUtc.ToString("O"));
            },
            reader => new QualityCheckEvidenceVerifierSummaryDto(
                reader.GetInt64(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)),
            ct) ?? new QualityCheckEvidenceVerifierSummaryDto(0, 0, 0, 0, 0, 0, 0);

        var operationRows = await ReadListAsync(connection, """
            SELECT
                accepted,
                COALESCE(NULLIF(reason, ''), 'none') AS reason,
                COUNT(*) AS count,
                COALESCE(AVG(score), 0) AS average_score,
                COALESCE(MAX(timestamp_utc), '') AS latest_utc
            FROM incident_operation_audit
            WHERE timestamp_utc >= $start AND timestamp_utc <= $end
            GROUP BY accepted, COALESCE(NULLIF(reason, ''), 'none')
            ORDER BY count DESC, latest_utc DESC
            LIMIT 30;
            """,
            command =>
            {
                Add(command, "$start", startUtc.ToString("O"));
                Add(command, "$end", endUtc.ToString("O"));
            },
            reader => new QualityCheckOperationSummaryDto(
                reader.GetInt32(0) != 0,
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetDouble(3),
                ParseDateOrNull(reader.GetString(4))),
            ct);

        var creates = operationRows.Where(r => r.Accepted && r.Reason.Contains("create incident", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count);
        var updates = operationRows.Where(r => r.Accepted && r.Reason.Contains("update incident", StringComparison.OrdinalIgnoreCase)).Sum(r => r.Count);
        var rejects = operationRows.Where(r => !r.Accepted).Sum(r => r.Count);
        var incidentAggregate = await ReadSingleAsync(connection, """
            SELECT COUNT(*) AS incidents, COALESCE(AVG(incident_score), 0) AS average_score
            FROM incidents
            WHERE last_seen >= $start AND last_seen <= $end;
            """,
            command =>
            {
                Add(command, "$start", start);
                Add(command, "$end", end);
            },
            reader => (Incidents: reader.GetInt64(0), AverageScore: reader.GetDouble(1)),
            ct);
        var recentIncidents = await ReadListAsync(connection, """
            SELECT id, COALESCE(incident_key, ''), title, COALESCE(category, 'other'), incident_score, first_seen, last_seen
            FROM incidents
            WHERE last_seen >= $start AND last_seen <= $end
            ORDER BY last_seen DESC, id DESC
            LIMIT 20;
            """,
            command =>
            {
                Add(command, "$start", start);
                Add(command, "$end", end);
            },
            reader => new QualityCheckRecentIncidentDto(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetDouble(4),
                reader.GetInt64(5),
                reader.GetInt64(6)),
            ct);

        var incidents = new QualityCheckIncidentSummaryDto(
            incidentAggregate.Incidents,
            incidentAggregate.AverageScore,
            creates,
            updates,
            rejects,
            recentIncidents);

        return new QualityCheckSnapshotDto(DateTime.UtcNow, start, end, calls, callsByCategory, transcriptQuality, ai, evidenceVerifier, incidents, operationRows);
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
                sample_stops, unable_source, tuning_err_samples, tuning_err_total_abs_hz, tuning_err_max_abs_hz,
                tr_cpu_percent, tr_rss_mb, tr_vsz_mb, tr_thread_count, host_temp_c, host_throttled_flags, host_load_1, host_load_5, host_load_15)
            VALUES (
                $window_start_utc, $window_end_utc, $scope, $decode_lines, $decode_zero, $decode_zero_pct,
                $cc_summary_decode_lines, $cc_summary_decode_zero, $cc_summary_decode_rate_total,
                $low_decode_warning_lines, $low_decode_warning_zero, $low_decode_warning_rate_total,
                $decode_rate_total, $retunes, $calls_started, $calls_concluded, $update_not_grant, $no_tx_recorded, $recorder_exhausted,
                $sample_stops, $unable_source, $tuning_err_samples, $tuning_err_total_abs_hz, $tuning_err_max_abs_hz,
                $tr_cpu_percent, $tr_rss_mb, $tr_vsz_mb, $tr_thread_count, $host_temp_c, $host_throttled_flags, $host_load_1, $host_load_5, $host_load_15);
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
        Add(command, "$tr_cpu_percent", sample.TrCpuPercent);
        Add(command, "$tr_rss_mb", sample.TrRssMb);
        Add(command, "$tr_vsz_mb", sample.TrVszMb);
        Add(command, "$tr_thread_count", sample.TrThreadCount);
        Add(command, "$host_temp_c", sample.HostTempC);
        Add(command, "$host_throttled_flags", sample.HostThrottledFlags);
        Add(command, "$host_load_1", sample.HostLoad1);
        Add(command, "$host_load_5", sample.HostLoad5);
        Add(command, "$host_load_15", sample.HostLoad15);
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
                TuningErrMaxAbsHz = reader.GetDouble(reader.GetOrdinal("tuning_err_max_abs_hz")),
                TrCpuPercent = reader.GetDouble(reader.GetOrdinal("tr_cpu_percent")),
                TrRssMb = reader.GetDouble(reader.GetOrdinal("tr_rss_mb")),
                TrVszMb = reader.GetDouble(reader.GetOrdinal("tr_vsz_mb")),
                TrThreadCount = reader.GetInt32(reader.GetOrdinal("tr_thread_count")),
                HostTempC = reader.GetDouble(reader.GetOrdinal("host_temp_c")),
                HostThrottledFlags = reader.GetString(reader.GetOrdinal("host_throttled_flags")),
                HostLoad1 = reader.GetDouble(reader.GetOrdinal("host_load_1")),
                HostLoad5 = reader.GetDouble(reader.GetOrdinal("host_load_5")),
                HostLoad15 = reader.GetDouble(reader.GetOrdinal("host_load_15"))
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
            INSERT INTO incidents (incident_key, title, detail, category, status, first_seen, last_seen, incident_score, source_summary_ids, created_at_utc, updated_at_utc)
            VALUES ($incident_key, $title, $detail, $category, $status, $first_seen, $last_seen, $incident_score, '[]', $created_at_utc, $updated_at_utc);
            SELECT last_insert_rowid();
            """;
        Add(command, "$incident_key", string.IsNullOrWhiteSpace(incident.IncidentKey) ? DBNull.Value : incident.IncidentKey);
        Add(command, "$title", incident.Title);
        Add(command, "$detail", incident.Detail);
        Add(command, "$category", string.IsNullOrWhiteSpace(incident.Category) ? "other" : incident.Category);
        Add(command, "$status", string.IsNullOrWhiteSpace(incident.Status) ? "active" : incident.Status);
        Add(command, "$first_seen", incident.FirstSeen);
        Add(command, "$last_seen", incident.LastSeen);
        Add(command, "$incident_score", incident.Confidence);
        var now = DateTime.UtcNow.ToString("O");
        Add(command, "$created_at_utc", now);
        Add(command, "$updated_at_utc", now);
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

    public async Task<long> UpsertManagedIncidentAsync(IncidentDto incident, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(incident.IncidentKey))
            throw new ArgumentException("Managed incidents require an incident key.", nameof(incident));

        var callIds = incident.Calls.Select(c => c.CallId).Distinct().ToList();
        if (callIds.Count == 0)
            return 0;

        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow.ToString("O");

        long id;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = (SqliteTransaction)tx;
            lookup.CommandText = "SELECT id FROM incidents WHERE incident_key=$incident_key;";
            Add(lookup, "$incident_key", incident.IncidentKey);
            var existing = await lookup.ExecuteScalarAsync(ct);
            id = existing == null ? 0 : Convert.ToInt64(existing);
        }

        if (id == 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO incidents (incident_key, title, detail, category, status, first_seen, last_seen, incident_score, source_summary_ids, created_at_utc, updated_at_utc)
                VALUES ($incident_key, $title, $detail, $category, $status, $first_seen, $last_seen, $incident_score, '[]', $created_at_utc, $updated_at_utc);
                SELECT last_insert_rowid();
                """;
            AddIncidentParameters(insert, incident, now);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }
        else
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)tx;
            update.CommandText = """
                UPDATE incidents
                SET title=$title,
                    detail=$detail,
                    category=$category,
                    status=$status,
                    first_seen=$first_seen,
                    last_seen=$last_seen,
                    incident_score=$incident_score,
                    updated_at_utc=$updated_at_utc
                WHERE id=$id;
                """;
            AddIncidentParameters(update, incident, now);
            Add(update, "$id", id);
            await update.ExecuteNonQueryAsync(ct);

            await using var deleteLinks = connection.CreateCommand();
            deleteLinks.Transaction = (SqliteTransaction)tx;
            deleteLinks.CommandText = "DELETE FROM incident_calls WHERE incident_id=$id;";
            Add(deleteLinks, "$id", id);
            await deleteLinks.ExecuteNonQueryAsync(ct);
        }

        var linked = 0;
        foreach (var callId in callIds)
        {
            await using var existingLink = connection.CreateCommand();
            existingLink.Transaction = (SqliteTransaction)tx;
            existingLink.CommandText = "SELECT incident_id FROM incident_calls WHERE call_id=$call_id AND incident_id<>$incident_id LIMIT 1;";
            Add(existingLink, "$call_id", callId);
            Add(existingLink, "$incident_id", id);
            if (await existingLink.ExecuteScalarAsync(ct) != null)
                continue;

            await using var link = connection.CreateCommand();
            link.Transaction = (SqliteTransaction)tx;
            link.CommandText = "INSERT OR IGNORE INTO incident_calls (incident_id, call_id) VALUES ($incident_id, $call_id);";
            Add(link, "$incident_id", id);
            Add(link, "$call_id", callId);
            linked += await link.ExecuteNonQueryAsync(ct);
        }

        if (linked == 0)
        {
            await tx.RollbackAsync(ct);
            return 0;
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<long> UpsertManagedIncidentAndMergeAsync(IncidentDto incident, IReadOnlyCollection<long> duplicateIncidentIds, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(incident.IncidentKey))
            throw new ArgumentException("Managed incidents require an incident key.", nameof(incident));

        var inputCallIds = incident.Calls.Select(c => c.CallId).Distinct().ToHashSet();
        if (inputCallIds.Count == 0)
            return 0;

        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        var now = DateTime.UtcNow.ToString("O");

        long id;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = (SqliteTransaction)tx;
            lookup.CommandText = "SELECT id FROM incidents WHERE incident_key=$incident_key;";
            Add(lookup, "$incident_key", incident.IncidentKey);
            var existing = await lookup.ExecuteScalarAsync(ct);
            id = existing == null ? 0 : Convert.ToInt64(existing);
        }

        if (id == 0 && incident.Id > 0)
            id = incident.Id;

        if (id == 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)tx;
            insert.CommandText = """
                INSERT INTO incidents (incident_key, title, detail, category, status, first_seen, last_seen, incident_score, source_summary_ids, created_at_utc, updated_at_utc)
                VALUES ($incident_key, $title, $detail, $category, $status, $first_seen, $last_seen, $incident_score, '[]', $created_at_utc, $updated_at_utc);
                SELECT last_insert_rowid();
                """;
            AddIncidentParameters(insert, incident, now);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }

        var duplicateIds = duplicateIncidentIds
            .Where(duplicateId => duplicateId > 0 && duplicateId != id)
            .Distinct()
            .ToList();

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)tx;
            update.CommandText = """
                UPDATE incidents
                SET title=$title,
                    detail=$detail,
                    category=$category,
                    status=$status,
                    first_seen=$first_seen,
                    last_seen=$last_seen,
                    incident_score=$incident_score,
                    updated_at_utc=$updated_at_utc
                WHERE id=$id;
                """;
            AddIncidentParameters(update, incident, now);
            Add(update, "$id", id);
            await update.ExecuteNonQueryAsync(ct);
        }

        var incidentIdsToReplace = duplicateIds.Append(id).Distinct().ToList();
        var replaceParameters = incidentIdsToReplace.Select((_, i) => $"$rid{i}").ToList();
        await using (var deleteLinks = connection.CreateCommand())
        {
            deleteLinks.Transaction = (SqliteTransaction)tx;
            deleteLinks.CommandText = $"DELETE FROM incident_calls WHERE incident_id IN ({string.Join(",", replaceParameters)});";
            for (var i = 0; i < incidentIdsToReplace.Count; i++)
                Add(deleteLinks, replaceParameters[i], incidentIdsToReplace[i]);
            await deleteLinks.ExecuteNonQueryAsync(ct);
        }

        var linked = 0;
        foreach (var callId in inputCallIds.Order())
        {
            await using var existingLink = connection.CreateCommand();
            existingLink.Transaction = (SqliteTransaction)tx;
            existingLink.CommandText = "SELECT incident_id FROM incident_calls WHERE call_id=$call_id AND incident_id<>$incident_id LIMIT 1;";
            Add(existingLink, "$call_id", callId);
            Add(existingLink, "$incident_id", id);
            if (await existingLink.ExecuteScalarAsync(ct) != null)
                continue;

            await using var link = connection.CreateCommand();
            link.Transaction = (SqliteTransaction)tx;
            link.CommandText = "INSERT OR IGNORE INTO incident_calls (incident_id, call_id) VALUES ($incident_id, $call_id);";
            Add(link, "$incident_id", id);
            Add(link, "$call_id", callId);
            linked += await link.ExecuteNonQueryAsync(ct);
        }

        if (duplicateIds.Count > 0)
        {
            var deleteParameters = duplicateIds.Select((_, i) => $"$delete{i}").ToList();
            await using var deleteIncidents = connection.CreateCommand();
            deleteIncidents.Transaction = (SqliteTransaction)tx;
            deleteIncidents.CommandText = $"DELETE FROM incidents WHERE id IN ({string.Join(",", deleteParameters)});";
            for (var i = 0; i < duplicateIds.Count; i++)
                Add(deleteIncidents, deleteParameters[i], duplicateIds[i]);
            await deleteIncidents.ExecuteNonQueryAsync(ct);
        }

        if (linked == 0)
        {
            await tx.RollbackAsync(ct);
            return 0;
        }

        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyDictionary<long, IncidentCallOwnerDto>> GetIncidentCallOwnersAsync(IReadOnlyCollection<long> callIds, CancellationToken ct)
    {
        if (callIds.Count == 0)
            return new Dictionary<long, IncidentCallOwnerDto>();

        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        var parameters = callIds.Distinct().Select((_, i) => $"$id{i}").ToList();
        command.CommandText = $"""
            SELECT ic.call_id, i.id, COALESCE(i.incident_key, ''), i.title, COALESCE(i.status, 'active')
            FROM incident_calls ic
            JOIN incidents i ON i.id = ic.incident_id
            WHERE ic.call_id IN ({string.Join(",", parameters)})
            ORDER BY i.last_seen DESC, i.id DESC;
            """;
        var index = 0;
        foreach (var callId in callIds.Distinct())
            Add(command, parameters[index++], callId);

        var owners = new Dictionary<long, IncidentCallOwnerDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var callId = reader.GetInt64(0);
            if (owners.ContainsKey(callId))
                continue;
            owners[callId] = new IncidentCallOwnerDto(
                callId,
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4));
        }
        return owners;
    }

    public async Task<int> ConcludeStaleManagedIncidentsAsync(long cutoffLastSeen, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE incidents
            SET status='concluded',
                updated_at_utc=$updated_at_utc
            WHERE status <> 'concluded'
              AND incident_key IS NOT NULL
              AND incident_key <> ''
              AND last_seen < $cutoff_last_seen;
            """;
        Add(command, "$updated_at_utc", DateTime.UtcNow.ToString("O"));
        Add(command, "$cutoff_last_seen", cutoffLastSeen);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddIncidentParameters(SqliteCommand command, IncidentDto incident, string now)
    {
        Add(command, "$incident_key", incident.IncidentKey);
        Add(command, "$title", incident.Title);
        Add(command, "$detail", incident.Detail);
        Add(command, "$category", string.IsNullOrWhiteSpace(incident.Category) ? "other" : incident.Category);
        Add(command, "$status", string.IsNullOrWhiteSpace(incident.Status) ? "active" : incident.Status);
        Add(command, "$first_seen", incident.FirstSeen);
        Add(command, "$last_seen", incident.LastSeen);
        Add(command, "$incident_score", incident.Confidence);
        Add(command, "$created_at_utc", now);
        Add(command, "$updated_at_utc", now);
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
            SELECT id, COALESCE(incident_key, ''), title, detail, COALESCE(category, 'other'), COALESCE(status, 'active'), first_seen, last_seen, incident_score
            FROM incidents
            WHERE last_seen >= $start AND first_seen <= $end
            ORDER BY last_seen DESC
            LIMIT 1000;
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
                IncidentKey = reader.GetString(1),
                Title = reader.GetString(2),
                Detail = reader.GetString(3),
                Category = reader.GetString(4),
                Status = reader.GetString(5),
                FirstSeen = reader.GetInt64(6),
                LastSeen = reader.GetInt64(7),
                Confidence = reader.GetDouble(8),
                Calls = await ListIncidentCallsAsync(connection, incidentId, ct)
            });
        }
        return incidents
            .Where(i => i.Calls.Count >= 1)
            .Select(i => i with { Category = string.IsNullOrWhiteSpace(i.Category) || i.Category == "other" ? DominantIncidentCategory(i.Calls) : i.Category })
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
            SELECT c.id, c.start_time, c.transcription, COALESCE(c.category, 'other'), COALESCE(c.talkgroup_name, ''), COALESCE(c.system_short_name, ''),
                   COALESCE(MAX(CASE WHEN am.id IS NOT NULL THEN 1 ELSE 0 END), 0),
                   COALESCE(MAX(CASE WHEN am.id IS NOT NULL AND COALESCE(am.dismissed_at_utc, '') = '' THEN 1 ELSE 0 END), 0),
                   COALESCE(group_concat(DISTINCT am.rule_name), '')
            FROM incident_calls ic
            JOIN calls c ON c.id = ic.call_id
            LEFT JOIN alert_matches am ON am.call_id = c.id
            WHERE ic.incident_id = $incident_id
              AND c.transcription_status = 'complete'
              AND c.quality_reason = 'ok'
              AND length(trim(c.transcription)) > 0
            GROUP BY c.id, c.start_time, c.transcription, c.category, c.talkgroup_name, c.system_short_name
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
                reader.GetString(5),
                reader.GetInt64(6) != 0,
                reader.GetInt64(7) != 0,
                reader.GetString(8)));
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

    private static DateTime? ParseDateOrNull(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateTime.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static RfSurveySessionDto ReadRfSurveySession(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        Mode = reader.GetString(reader.GetOrdinal("mode")),
        SiteLabel = reader.GetString(reader.GetOrdinal("site_label")),
        SystemShortName = reader.GetString(reader.GetOrdinal("system_short_name")),
        Verdict = reader.GetString(reader.GetOrdinal("verdict")),
        Stability = reader.GetString(reader.GetOrdinal("stability")),
        SdrSummary = reader.GetString(reader.GetOrdinal("sdr_summary")),
        RfPathSummary = reader.GetString(reader.GetOrdinal("rf_path_summary")),
        BestControlChannel = reader.GetString(reader.GetOrdinal("best_control_channel")),
        SourcePlanSummary = reader.GetString(reader.GetOrdinal("source_plan_summary")),
        RecommendationState = reader.GetString(reader.GetOrdinal("recommendation_state")),
        ArtifactPath = reader.GetString(reader.GetOrdinal("artifact_path")),
        CreatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at_utc"))),
        UpdatedAtUtc = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at_utc"))),
        CompletedAtUtc = ParseDateOrNull(reader.GetString(reader.GetOrdinal("completed_at_utc")))
    };

    private static void AddRfSurveySessionParameters(SqliteCommand command, RfSurveySessionDto session)
    {
        Add(command, "$status", session.Status);
        Add(command, "$mode", session.Mode);
        Add(command, "$site_label", session.SiteLabel);
        Add(command, "$system_short_name", session.SystemShortName);
        Add(command, "$verdict", session.Verdict);
        Add(command, "$stability", session.Stability);
        Add(command, "$sdr_summary", session.SdrSummary);
        Add(command, "$rf_path_summary", session.RfPathSummary);
        Add(command, "$best_control_channel", session.BestControlChannel);
        Add(command, "$source_plan_summary", session.SourcePlanSummary);
        Add(command, "$recommendation_state", session.RecommendationState);
        Add(command, "$artifact_path", session.ArtifactPath);
        Add(command, "$created_at_utc", session.CreatedAtUtc.ToString("O"));
        Add(command, "$updated_at_utc", session.UpdatedAtUtc.ToString("O"));
        Add(command, "$completed_at_utc", session.CompletedAtUtc?.ToString("O") ?? string.Empty);
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static long NormalizeFrequencyHz(long value) =>
        value > 0 && value < 10_000 ? value * 1_000_000 : value;

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

    private static async Task<T?> ReadSingleAsync<T>(
        SqliteConnection connection,
        string sql,
        Action<SqliteCommand> bind,
        Func<SqliteDataReader, T> map,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? map(reader) : default;
    }

    private static async Task<List<T>> ReadListAsync<T>(
        SqliteConnection connection,
        string sql,
        Action<SqliteCommand> bind,
        Func<SqliteDataReader, T> map,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
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
        var failures = list.Where(r => !r.Success).ToList();
        var failureKinds = failures.GroupBy(TokenFailureKind).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var truncated = failureKinds.GetValueOrDefault("truncated");
        var timeout = failureKinds.GetValueOrDefault("completion-timeout");
        var noValidResult = failureKinds.GetValueOrDefault("no-valid-completion");
        var canceled = failureKinds.GetValueOrDefault("canceled");
        var otherFailures = failureKinds.GetValueOrDefault("http-or-other");
        var prompt = list.Sum(r => (long)r.PromptTokens);
        var completion = list.Sum(r => (long)r.CompletionTokens);
        var total = list.Sum(r => (long)(r.TotalTokens > 0 ? r.TotalTokens : r.PromptTokens + r.CompletionTokens));
        return new TokenUsageSummaryDto(
            list.Count,
            list.Count(r => r.Success),
            failures.Count,
            truncated,
            canceled,
            otherFailures,
            prompt,
            completion,
            total,
            (prompt / 1_000_000d * 2.00) + (completion / 1_000_000d * 8.00),
            timeout,
            noValidResult);
    }

    private static async Task<TokenUsageSummaryDto> SummarizeTokenUsageAsync(SqliteConnection connection, DateTime? startUtc, DateTime? endUtc, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) AS requests,
                COALESCE(SUM(CASE WHEN success = 1 THEN 1 ELSE 0 END), 0) AS successes,
                COALESCE(SUM(CASE WHEN success = 0 THEN 1 ELSE 0 END), 0) AS failures,
                COALESCE(SUM(CASE WHEN success = 0 AND (LOWER(finish_reason) = 'length' OR LOWER(error) LIKE '%truncat%') THEN 1 ELSE 0 END), 0) AS truncated,
                COALESCE(SUM(CASE
                    WHEN success = 0
                     AND NOT (LOWER(finish_reason) = 'length' OR LOWER(error) LIKE '%truncat%')
                     AND (LOWER(error) LIKE '%timeout%'
                          OR LOWER(error) LIKE '%timed out%'
                          OR LOWER(error) LIKE '%operation has timed out%'
                          OR LOWER(error) LIKE '%taskcanceled%'
                          OR LOWER(error) LIKE '%request was aborted%'
                          OR LOWER(error) LIKE '%httpclient.timeout%')
                    THEN 1 ELSE 0 END), 0) AS timeout_failures,
                COALESCE(SUM(CASE
                    WHEN success = 0
                     AND NOT (LOWER(finish_reason) = 'length' OR LOWER(error) LIKE '%truncat%')
                     AND NOT (LOWER(error) LIKE '%timeout%'
                          OR LOWER(error) LIKE '%timed out%'
                          OR LOWER(error) LIKE '%operation has timed out%'
                          OR LOWER(error) LIKE '%taskcanceled%'
                          OR LOWER(error) LIKE '%request was aborted%'
                          OR LOWER(error) LIKE '%httpclient.timeout%')
                     AND LOWER(error) LIKE '%cancel%'
                    THEN 1 ELSE 0 END), 0) AS canceled,
                COALESCE(SUM(CASE
                    WHEN success = 0
                     AND prompt_tokens = 0
                     AND completion_tokens = 0
                     AND total_tokens = 0
                     AND NOT (LOWER(finish_reason) = 'length' OR LOWER(error) LIKE '%truncat%')
                     AND NOT (LOWER(error) LIKE '%timeout%'
                          OR LOWER(error) LIKE '%timed out%'
                          OR LOWER(error) LIKE '%operation has timed out%'
                          OR LOWER(error) LIKE '%taskcanceled%'
                          OR LOWER(error) LIKE '%request was aborted%'
                          OR LOWER(error) LIKE '%httpclient.timeout%'
                          OR LOWER(error) LIKE '%cancel%')
                    THEN 1 ELSE 0 END), 0) AS no_valid_result_failures,
                COALESCE(SUM(prompt_tokens), 0) AS prompt_tokens,
                COALESCE(SUM(completion_tokens), 0) AS completion_tokens,
                COALESCE(SUM(CASE WHEN total_tokens > 0 THEN total_tokens ELSE prompt_tokens + completion_tokens END), 0) AS total_tokens
            FROM lm_usage
            WHERE ($start IS NULL OR timestamp_utc >= $start)
              AND ($end IS NULL OR timestamp_utc <= $end);
            """;
        Add(command, "$start", startUtc?.ToString("O"));
        Add(command, "$end", endUtc?.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new TokenUsageSummaryDto();
        var prompt = reader.GetInt64(reader.GetOrdinal("prompt_tokens"));
        var completion = reader.GetInt64(reader.GetOrdinal("completion_tokens"));
        var total = reader.GetInt64(reader.GetOrdinal("total_tokens"));
        var failures = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("failures")));
        var truncated = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("truncated")));
        var timeout = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("timeout_failures")));
        var canceled = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("canceled")));
        var noValidResult = Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("no_valid_result_failures")));
        return new TokenUsageSummaryDto(
            Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("requests"))),
            Convert.ToInt32(reader.GetInt64(reader.GetOrdinal("successes"))),
            failures,
            truncated,
            canceled,
            Math.Max(0, failures - truncated - timeout - canceled - noValidResult),
            prompt,
            completion,
            total,
            (prompt / 1_000_000d * 2.00) + (completion / 1_000_000d * 8.00),
            timeout,
            noValidResult);
    }

    private static IReadOnlyList<TokenUsageFailureBreakdownDto> TokenFailureBreakdown(IEnumerable<TokenUsageEntryDto> rows) =>
        rows.Where(r => !r.Success)
            .GroupBy(TokenFailureKind)
            .Select(g =>
            {
                var list = g.OrderByDescending(r => r.TimestampUtc).ToList();
                return new TokenUsageFailureBreakdownDto(
                    g.Key,
                    list.Count,
                    list.Sum(r => (long)r.PromptTokens),
                    list.Sum(r => (long)r.CompletionTokens),
                    list.Sum(r => (long)(r.TotalTokens > 0 ? r.TotalTokens : r.PromptTokens + r.CompletionTokens)),
                    list.First().TimestampUtc,
                    FailureExample(list.First()));
            })
            .OrderByDescending(r => r.Requests)
            .ToList();

    private static string TokenFailureKind(TokenUsageEntryDto row)
    {
        if (IsTruncatedUsage(row))
            return "truncated";
        if (IsCompletionTimeoutUsage(row))
            return "completion-timeout";
        if (IsNoValidCompletionUsage(row))
            return "no-valid-completion";
        if (IsCanceledUsage(row))
            return "canceled";
        return "http-or-other";
    }

    private static bool IsTruncatedUsage(TokenUsageEntryDto row) =>
        string.Equals(row.FinishReason, "length", StringComparison.OrdinalIgnoreCase) ||
        row.Error.Contains("truncat", StringComparison.OrdinalIgnoreCase);

    private static bool IsCanceledUsage(TokenUsageEntryDto row) =>
        row.Error.Contains("cancel", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompletionTimeoutUsage(TokenUsageEntryDto row) =>
        row.Error.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        row.Error.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        row.Error.Contains("operation has timed out", StringComparison.OrdinalIgnoreCase) ||
        row.Error.Contains("TaskCanceled", StringComparison.OrdinalIgnoreCase) ||
        row.Error.Contains("request was aborted", StringComparison.OrdinalIgnoreCase) ||
        row.Error.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase);

    private static bool IsNoValidCompletionUsage(TokenUsageEntryDto row) =>
        !row.Success
        && row.PromptTokens == 0
        && row.CompletionTokens == 0
        && row.TotalTokens == 0
        && !IsTruncatedUsage(row)
        && !IsCompletionTimeoutUsage(row)
        && !IsCanceledUsage(row);

    private static string FailureExample(TokenUsageEntryDto row) =>
        !string.IsNullOrWhiteSpace(row.Error)
            ? row.Error
            : !string.IsNullOrWhiteSpace(row.FinishReason)
                ? row.FinishReason
                : "No valid completion result was recorded.";

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task EnsureSchemaMigrationsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await AddColumnIfMissingAsync(connection, "incidents", "incident_score", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "incidents", "incident_key", "TEXT", ct);
        await AddColumnIfMissingAsync(connection, "incidents", "category", "TEXT NOT NULL DEFAULT 'other'", ct);
        await AddColumnIfMissingAsync(connection, "incidents", "status", "TEXT NOT NULL DEFAULT 'active'", ct);
        await AddColumnIfMissingAsync(connection, "incidents", "updated_at_utc", "TEXT NOT NULL DEFAULT ''", ct);
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
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "tr_cpu_percent", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "tr_rss_mb", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "tr_vsz_mb", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "tr_thread_count", "INTEGER NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "host_temp_c", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "host_throttled_flags", "TEXT NOT NULL DEFAULT ''", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "host_load_1", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "host_load_5", "REAL NOT NULL DEFAULT 0", ct);
        await AddColumnIfMissingAsync(connection, "tr_health_samples", "host_load_15", "REAL NOT NULL DEFAULT 0", ct);
        await ExecuteNonQueryAsync(connection, """
            CREATE TABLE IF NOT EXISTS recommendation_states (
                recommendation_id TEXT PRIMARY KEY,
                snoozed_until_utc TEXT NOT NULL DEFAULT '',
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS recommendation_baselines (
                recommendation_id TEXT PRIMARY KEY,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                active_observations INTEGER NOT NULL DEFAULT 0,
                baseline_value TEXT NOT NULL DEFAULT ''
            );
            """, ct);
        await ExecuteNonQueryAsync(connection, RfSurveySchemaSql, ct);
        await AddColumnIfMissingAsync(connection, "recommendation_baselines", "baseline_value", "TEXT NOT NULL DEFAULT ''", ct);
        await AddColumnIfMissingAsync(connection, "alert_matches", "dismissed_at_utc", "TEXT NOT NULL DEFAULT ''", ct);
        await ExecuteNonQueryAsync(connection, "CREATE UNIQUE INDEX IF NOT EXISTS idx_incidents_key ON incidents(incident_key) WHERE incident_key IS NOT NULL AND incident_key <> '';", ct);
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

            CREATE TABLE IF NOT EXISTS evidence_verifier_runs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                system_short_name TEXT NOT NULL DEFAULT '',
                incident_key TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                selected_calls INTEGER NOT NULL DEFAULT 0,
                reviewed_calls INTEGER NOT NULL DEFAULT 0,
                model_reviewed_calls INTEGER NOT NULL DEFAULT 0,
                truncated_calls INTEGER NOT NULL DEFAULT 0,
                added_calls INTEGER NOT NULL DEFAULT 0,
                dropped_calls INTEGER NOT NULL DEFAULT 0,
                retained_calls INTEGER NOT NULL DEFAULT 0,
                success INTEGER NOT NULL DEFAULT 0,
                error TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS idx_evidence_verifier_runs_time ON evidence_verifier_runs(timestamp_utc DESC);

            CREATE TABLE IF NOT EXISTS incident_operation_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp_utc TEXT NOT NULL,
                system_short_name TEXT NOT NULL,
                incident_key TEXT NOT NULL,
                operation TEXT NOT NULL,
                accepted INTEGER NOT NULL,
                reason TEXT NOT NULL,
                score REAL NOT NULL DEFAULT 0,
                call_ids_json TEXT NOT NULL DEFAULT '[]',
                metadata_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE INDEX IF NOT EXISTS idx_incident_operation_audit_time ON incident_operation_audit(timestamp_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_incident_operation_audit_key ON incident_operation_audit(incident_key, timestamp_utc DESC);

            CREATE TABLE IF NOT EXISTS call_embedding_jobs (
                call_id INTEGER PRIMARY KEY,
                status TEXT NOT NULL DEFAULT 'pending',
                attempts INTEGER NOT NULL DEFAULT 0,
                error TEXT NOT NULL DEFAULT '',
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_call_embedding_jobs_status ON call_embedding_jobs(status, updated_at_utc);

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
        await ExecuteNonQueryAsync(connection, CallAnchorsSchemaSql, ct);
        await ExecuteNonQueryAsync(connection, CallPostProcessingSchemaSql, ct);
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
            dismissed_at_utc TEXT NOT NULL DEFAULT '',
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
            incident_key TEXT,
            title TEXT NOT NULL,
            detail TEXT NOT NULL,
            category TEXT NOT NULL DEFAULT 'other',
            status TEXT NOT NULL DEFAULT 'active',
            first_seen INTEGER NOT NULL,
            last_seen INTEGER NOT NULL,
            incident_score REAL NOT NULL DEFAULT 0,
            source_summary_ids TEXT NOT NULL DEFAULT '[]',
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL DEFAULT ''
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
            tuning_err_max_abs_hz REAL NOT NULL DEFAULT 0,
            tr_cpu_percent REAL NOT NULL DEFAULT 0,
            tr_rss_mb REAL NOT NULL DEFAULT 0,
            tr_vsz_mb REAL NOT NULL DEFAULT 0,
            tr_thread_count INTEGER NOT NULL DEFAULT 0,
            host_temp_c REAL NOT NULL DEFAULT 0,
            host_throttled_flags TEXT NOT NULL DEFAULT '',
            host_load_1 REAL NOT NULL DEFAULT 0,
            host_load_5 REAL NOT NULL DEFAULT 0,
            host_load_15 REAL NOT NULL DEFAULT 0
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

        CREATE TABLE IF NOT EXISTS evidence_verifier_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            system_short_name TEXT NOT NULL DEFAULT '',
            incident_key TEXT NOT NULL DEFAULT '',
            title TEXT NOT NULL DEFAULT '',
            selected_calls INTEGER NOT NULL DEFAULT 0,
            reviewed_calls INTEGER NOT NULL DEFAULT 0,
            model_reviewed_calls INTEGER NOT NULL DEFAULT 0,
            truncated_calls INTEGER NOT NULL DEFAULT 0,
            added_calls INTEGER NOT NULL DEFAULT 0,
            dropped_calls INTEGER NOT NULL DEFAULT 0,
            retained_calls INTEGER NOT NULL DEFAULT 0,
            success INTEGER NOT NULL DEFAULT 0,
            error TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_evidence_verifier_runs_time ON evidence_verifier_runs(timestamp_utc DESC);

        CREATE TABLE IF NOT EXISTS incident_operation_audit (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            timestamp_utc TEXT NOT NULL,
            system_short_name TEXT NOT NULL,
            incident_key TEXT NOT NULL,
            operation TEXT NOT NULL,
            accepted INTEGER NOT NULL,
            reason TEXT NOT NULL,
            score REAL NOT NULL DEFAULT 0,
            call_ids_json TEXT NOT NULL DEFAULT '[]',
            metadata_json TEXT NOT NULL DEFAULT '{}'
        );
        CREATE INDEX IF NOT EXISTS idx_incident_operation_audit_time ON incident_operation_audit(timestamp_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_incident_operation_audit_key ON incident_operation_audit(incident_key, timestamp_utc DESC);

        CREATE TABLE IF NOT EXISTS call_embedding_jobs (
            call_id INTEGER PRIMARY KEY,
            status TEXT NOT NULL DEFAULT 'pending',
            attempts INTEGER NOT NULL DEFAULT 0,
            error TEXT NOT NULL DEFAULT '',
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_call_embedding_jobs_status ON call_embedding_jobs(status, updated_at_utc);

        CREATE TABLE IF NOT EXISTS recommendation_baselines (
            recommendation_id TEXT PRIMARY KEY,
            first_seen_utc TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL,
            active_observations INTEGER NOT NULL DEFAULT 0,
            baseline_value TEXT NOT NULL DEFAULT ''
        );

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

    private const string CallAnchorsSchemaSql = """
        CREATE TABLE IF NOT EXISTS call_anchors (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            call_id INTEGER NOT NULL,
            kind TEXT NOT NULL,
            value TEXT NOT NULL,
            display_text TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL DEFAULT 'deterministic',
            confidence REAL NOT NULL DEFAULT 0,
            details_json TEXT NOT NULL DEFAULT '{}',
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_call_anchors_call ON call_anchors(call_id);
        CREATE INDEX IF NOT EXISTS idx_call_anchors_kind_value ON call_anchors(kind, value);
        """;

    private const string CallPostProcessingSchemaSql = """
        CREATE TABLE IF NOT EXISTS call_post_processing (
            call_id INTEGER PRIMARY KEY,
            completed_at_utc TEXT NOT NULL,
            FOREIGN KEY(call_id) REFERENCES calls(id) ON DELETE CASCADE
        );
        """;

    private const string RfSurveySchemaSql = """
        CREATE TABLE IF NOT EXISTS rf_survey_sessions (
            id TEXT PRIMARY KEY,
            status TEXT NOT NULL DEFAULT 'draft',
            mode TEXT NOT NULL DEFAULT 'guided',
            site_label TEXT NOT NULL DEFAULT '',
            system_short_name TEXT NOT NULL DEFAULT '',
            verdict TEXT NOT NULL DEFAULT 'not_started',
            stability TEXT NOT NULL DEFAULT 'unknown',
            sdr_summary TEXT NOT NULL DEFAULT '',
            rf_path_summary TEXT NOT NULL DEFAULT '',
            best_control_channel TEXT NOT NULL DEFAULT '',
            source_plan_summary TEXT NOT NULL DEFAULT '',
            recommendation_state TEXT NOT NULL DEFAULT 'none',
            artifact_path TEXT NOT NULL DEFAULT '',
            profile_json TEXT NOT NULL DEFAULT '{}',
            tool_prep_json TEXT NOT NULL DEFAULT '',
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            completed_at_utc TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_rf_survey_sessions_updated ON rf_survey_sessions(updated_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_rf_survey_sessions_system ON rf_survey_sessions(system_short_name, updated_at_utc DESC);

        CREATE TABLE IF NOT EXISTS rf_survey_experiments (
            id TEXT PRIMARY KEY,
            survey_id TEXT NOT NULL,
            type TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'planned',
            hypothesis TEXT NOT NULL DEFAULT '',
            required_setup TEXT NOT NULL DEFAULT '',
            result_summary TEXT NOT NULL DEFAULT '',
            blocking_issue TEXT NOT NULL DEFAULT '',
            evidence_json TEXT NOT NULL DEFAULT '{}',
            interpretation_json TEXT NOT NULL DEFAULT '{}',
            created_at_utc TEXT NOT NULL,
            started_at_utc TEXT NOT NULL DEFAULT '',
            finished_at_utc TEXT NOT NULL DEFAULT '',
            FOREIGN KEY(survey_id) REFERENCES rf_survey_sessions(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_rf_survey_experiments_survey ON rf_survey_experiments(survey_id, created_at_utc);

        CREATE TABLE IF NOT EXISTS rf_survey_notes (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            survey_id TEXT NOT NULL,
            text TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            FOREIGN KEY(survey_id) REFERENCES rf_survey_sessions(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_rf_survey_notes_survey ON rf_survey_notes(survey_id, created_at_utc);
        """;
}
