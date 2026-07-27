using Microsoft.Data.Sqlite;
using System.Globalization;

namespace pizzad;

public sealed partial class EngineDatabase
{
    public async Task AddWorkspaceAsync(WorkspaceDto workspace, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace.Id))
            throw new ArgumentException("Workspace id is required.", nameof(workspace));
        if (string.IsNullOrWhiteSpace(workspace.Name))
            throw new ArgumentException("Workspace name is required.", nameof(workspace));

        var now = NormalizeUtc(workspace.CreatedAtUtc == default ? DateTime.UtcNow : workspace.CreatedAtUtc);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspaces (
                id, name, kind, status, source_label, source_identity, root_path,
                manifest_json, source_bytes, extracted_bytes, derived_bytes,
                created_at_utc, updated_at_utc)
            VALUES (
                $id, $name, $kind, $status, $source_label, $source_identity, $root_path,
                $manifest_json, $source_bytes, $extracted_bytes, $derived_bytes,
                $created_at_utc, $updated_at_utc);
            """;
        Add(command, "$id", workspace.Id.Trim());
        Add(command, "$name", workspace.Name.Trim());
        Add(command, "$kind", workspace.Kind.Trim());
        Add(command, "$status", workspace.Status.Trim());
        Add(command, "$source_label", workspace.SourceLabel.Trim());
        Add(command, "$source_identity", workspace.SourceIdentity.Trim());
        Add(command, "$root_path", workspace.RootPath.Trim());
        Add(command, "$manifest_json", workspace.ManifestJson);
        Add(command, "$source_bytes", Math.Max(0, workspace.SourceBytes));
        Add(command, "$extracted_bytes", Math.Max(0, workspace.ExtractedBytes));
        Add(command, "$derived_bytes", Math.Max(0, workspace.DerivedBytes));
        Add(command, "$created_at_utc", now.ToString("O"));
        Add(command, "$updated_at_utc", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<WorkspaceDto?> GetWorkspaceAsync(string id, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM workspaces WHERE id=$id;";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadWorkspace(reader) : null;
    }

    public async Task<IReadOnlyList<WorkspaceDto>> ListWorkspacesAsync(CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM workspaces ORDER BY updated_at_utc DESC, name;";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<WorkspaceDto>();
        while (await reader.ReadAsync(ct))
            rows.Add(ReadWorkspace(reader));
        return rows;
    }

    public async Task<long> AddWorkspaceProcessingRunAsync(WorkspaceProcessingRunDto run, CancellationToken ct)
    {
        var queued = NormalizeUtc(run.QueuedAtUtc == default ? DateTime.UtcNow : run.QueuedAtUtc);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO workspace_processing_runs (
                workspace_id, job_id, name, status, profile_json, requested_stages_json,
                estimate_json, actual_summary_json, queued_at_utc, started_at_utc,
                completed_at_utc, updated_at_utc)
            VALUES (
                $workspace_id, $job_id, $name, $status, $profile_json, $requested_stages_json,
                $estimate_json, $actual_summary_json, $queued_at_utc, $started_at_utc,
                $completed_at_utc, $updated_at_utc)
            RETURNING id;
            """;
        Add(command, "$workspace_id", run.WorkspaceId);
        Add(command, "$job_id", run.JobId);
        Add(command, "$name", run.Name);
        Add(command, "$status", run.Status);
        Add(command, "$profile_json", run.ProfileJson);
        Add(command, "$requested_stages_json", run.RequestedStagesJson);
        Add(command, "$estimate_json", run.EstimateJson);
        Add(command, "$actual_summary_json", run.ActualSummaryJson);
        Add(command, "$queued_at_utc", queued.ToString("O"));
        Add(command, "$started_at_utc", run.StartedAtUtc?.ToUniversalTime().ToString("O"));
        Add(command, "$completed_at_utc", run.CompletedAtUtc?.ToUniversalTime().ToString("O"));
        Add(command, "$updated_at_utc", queued.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<long> AddProcessingStageAttemptAsync(ProcessingStageAttemptDto attempt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(attempt.Stage))
            throw new ArgumentException("Processing stage is required.", nameof(attempt));
        var queued = NormalizeUtc(attempt.QueuedAtUtc == default ? DateTime.UtcNow : attempt.QueuedAtUtc);
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_stage_attempts (
                scope, workspace_id, run_id, call_id, job_id, stage, attempt, status,
                queued_at_utc, started_at_utc, completed_at_utc, queue_duration_ms,
                active_duration_ms, paused_duration_ms, wall_duration_ms,
                endpoint_duration_ms, item_count, audio_seconds, prompt_tokens,
                completion_tokens, retry_count, failure_count, pricing_version,
                estimated_cost, actual_cost, message, details_json,
                active_started_at_utc, pause_started_at_utc, updated_at_utc)
            VALUES (
                $scope, $workspace_id, $run_id, $call_id, $job_id, $stage, $attempt, $status,
                $queued_at_utc, NULL, NULL, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, $pricing_version,
                $estimated_cost, 0, $message, $details_json, NULL, NULL, $updated_at_utc)
            RETURNING id;
            """;
        Add(command, "$scope", attempt.Scope);
        Add(command, "$workspace_id", string.IsNullOrWhiteSpace(attempt.WorkspaceId) ? null : attempt.WorkspaceId);
        Add(command, "$run_id", attempt.RunId);
        Add(command, "$call_id", attempt.CallId);
        Add(command, "$job_id", attempt.JobId);
        Add(command, "$stage", attempt.Stage);
        Add(command, "$attempt", Math.Max(1, attempt.Attempt));
        Add(command, "$status", "queued");
        Add(command, "$queued_at_utc", queued.ToString("O"));
        Add(command, "$pricing_version", attempt.PricingVersion);
        Add(command, "$estimated_cost", attempt.EstimatedCost);
        Add(command, "$message", attempt.Message);
        Add(command, "$details_json", attempt.DetailsJson);
        Add(command, "$updated_at_utc", queued.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<ProcessingStageAttemptDto?> GetProcessingStageAttemptAsync(long id, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM processing_stage_attempts WHERE id=$id;";
        Add(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProcessingStageAttempt(reader) : null;
    }

    public async Task<ProcessingStageAttemptDto> TransitionProcessingStageAttemptAsync(
        long id,
        string nextStatus,
        DateTime occurredAtUtc,
        ProcessingStageMetricsDelta? delta,
        string? message,
        string? detailsJson,
        CancellationToken ct)
    {
        var now = NormalizeUtc(occurredAtUtc);
        await using var connection = OpenConnection();
        await using var tx = await connection.BeginTransactionAsync(ct);
        ProcessingStageAttemptDto current;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = (SqliteTransaction)tx;
            read.CommandText = "SELECT * FROM processing_stage_attempts WHERE id=$id;";
            Add(read, "$id", id);
            await using var reader = await read.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Processing stage attempt {id} does not exist.");
            current = ReadProcessingStageAttempt(reader);
        }

        ValidateStageTransition(current.Status, nextStatus);
        var started = current.StartedAtUtc;
        var completed = current.CompletedAtUtc;
        var activeStarted = current.ActiveStartedAtUtc;
        var pauseStarted = current.PauseStartedAtUtc;
        var queueMs = current.QueueDurationMs;
        var activeMs = current.ActiveDurationMs;
        var pausedMs = current.PausedDurationMs;
        var wallMs = current.WallDurationMs;

        if (nextStatus == "running")
        {
            if (started == null)
            {
                started = now;
                queueMs = ElapsedMs(current.QueuedAtUtc, now);
            }
            if (pauseStarted != null)
                pausedMs += ElapsedMs(pauseStarted.Value, now);
            pauseStarted = null;
            activeStarted = now;
        }
        else if (nextStatus == "paused")
        {
            if (activeStarted != null)
                activeMs += ElapsedMs(activeStarted.Value, now);
            activeStarted = null;
            pauseStarted = now;
        }
        else if (IsTerminalStageStatus(nextStatus))
        {
            if (activeStarted != null)
                activeMs += ElapsedMs(activeStarted.Value, now);
            if (pauseStarted != null)
                pausedMs += ElapsedMs(pauseStarted.Value, now);
            activeStarted = null;
            pauseStarted = null;
            completed = now;
            wallMs = ElapsedMs(current.QueuedAtUtc, now);
        }

        delta ??= new ProcessingStageMetricsDelta();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)tx;
            update.CommandText = """
                UPDATE processing_stage_attempts SET
                    status=$status, started_at_utc=$started_at_utc,
                    completed_at_utc=$completed_at_utc, queue_duration_ms=$queue_duration_ms,
                    active_duration_ms=$active_duration_ms, paused_duration_ms=$paused_duration_ms,
                    wall_duration_ms=$wall_duration_ms,
                    endpoint_duration_ms=endpoint_duration_ms+$endpoint_duration_ms,
                    item_count=item_count+$item_count, audio_seconds=audio_seconds+$audio_seconds,
                    prompt_tokens=prompt_tokens+$prompt_tokens,
                    completion_tokens=completion_tokens+$completion_tokens,
                    retry_count=retry_count+$retry_count, failure_count=failure_count+$failure_count,
                    actual_cost=actual_cost+$actual_cost,
                    message=COALESCE($message, message), details_json=COALESCE($details_json, details_json),
                    active_started_at_utc=$active_started_at_utc,
                    pause_started_at_utc=$pause_started_at_utc, updated_at_utc=$updated_at_utc
                WHERE id=$id;
                """;
            Add(update, "$status", nextStatus);
            Add(update, "$started_at_utc", started?.ToString("O"));
            Add(update, "$completed_at_utc", completed?.ToString("O"));
            Add(update, "$queue_duration_ms", queueMs);
            Add(update, "$active_duration_ms", activeMs);
            Add(update, "$paused_duration_ms", pausedMs);
            Add(update, "$wall_duration_ms", wallMs);
            Add(update, "$endpoint_duration_ms", Math.Max(0, delta.EndpointDurationMs));
            Add(update, "$item_count", Math.Max(0, delta.ItemCount));
            Add(update, "$audio_seconds", Math.Max(0, delta.AudioSeconds));
            Add(update, "$prompt_tokens", Math.Max(0, delta.PromptTokens));
            Add(update, "$completion_tokens", Math.Max(0, delta.CompletionTokens));
            Add(update, "$retry_count", Math.Max(0, delta.RetryCount));
            Add(update, "$failure_count", Math.Max(0, delta.FailureCount));
            Add(update, "$actual_cost", Math.Max(0, delta.ActualCost));
            Add(update, "$message", message);
            Add(update, "$details_json", detailsJson);
            Add(update, "$active_started_at_utc", activeStarted?.ToString("O"));
            Add(update, "$pause_started_at_utc", pauseStarted?.ToString("O"));
            Add(update, "$updated_at_utc", now.ToString("O"));
            Add(update, "$id", id);
            await update.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return await GetProcessingStageAttemptAsync(id, ct)
            ?? throw new InvalidOperationException($"Processing stage attempt {id} disappeared after update.");
    }

    private static WorkspaceDto ReadWorkspace(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        Name = reader.GetString(reader.GetOrdinal("name")),
        Kind = reader.GetString(reader.GetOrdinal("kind")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        SourceLabel = reader.GetString(reader.GetOrdinal("source_label")),
        SourceIdentity = reader.GetString(reader.GetOrdinal("source_identity")),
        RootPath = reader.GetString(reader.GetOrdinal("root_path")),
        ManifestJson = reader.GetString(reader.GetOrdinal("manifest_json")),
        SourceBytes = reader.GetInt64(reader.GetOrdinal("source_bytes")),
        ExtractedBytes = reader.GetInt64(reader.GetOrdinal("extracted_bytes")),
        DerivedBytes = reader.GetInt64(reader.GetOrdinal("derived_bytes")),
        CreatedAtUtc = ReadRequiredUtc(reader, "created_at_utc"),
        UpdatedAtUtc = ReadRequiredUtc(reader, "updated_at_utc")
    };

    private static ProcessingStageAttemptDto ReadProcessingStageAttempt(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        Scope = reader.GetString(reader.GetOrdinal("scope")),
        WorkspaceId = ReadNullableString(reader, "workspace_id"),
        RunId = ReadNullableInt64(reader, "run_id"),
        CallId = ReadNullableInt64(reader, "call_id"),
        JobId = ReadNullableInt64(reader, "job_id"),
        Stage = reader.GetString(reader.GetOrdinal("stage")),
        Attempt = reader.GetInt32(reader.GetOrdinal("attempt")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        QueuedAtUtc = ReadRequiredUtc(reader, "queued_at_utc"),
        StartedAtUtc = ReadNullableUtc(reader, "started_at_utc"),
        CompletedAtUtc = ReadNullableUtc(reader, "completed_at_utc"),
        QueueDurationMs = reader.GetInt64(reader.GetOrdinal("queue_duration_ms")),
        ActiveDurationMs = reader.GetInt64(reader.GetOrdinal("active_duration_ms")),
        PausedDurationMs = reader.GetInt64(reader.GetOrdinal("paused_duration_ms")),
        WallDurationMs = reader.GetInt64(reader.GetOrdinal("wall_duration_ms")),
        EndpointDurationMs = reader.GetInt64(reader.GetOrdinal("endpoint_duration_ms")),
        ItemCount = reader.GetInt64(reader.GetOrdinal("item_count")),
        AudioSeconds = reader.GetDouble(reader.GetOrdinal("audio_seconds")),
        PromptTokens = reader.GetInt64(reader.GetOrdinal("prompt_tokens")),
        CompletionTokens = reader.GetInt64(reader.GetOrdinal("completion_tokens")),
        RetryCount = reader.GetInt32(reader.GetOrdinal("retry_count")),
        FailureCount = reader.GetInt32(reader.GetOrdinal("failure_count")),
        PricingVersion = reader.GetString(reader.GetOrdinal("pricing_version")),
        EstimatedCost = Convert.ToDecimal(reader.GetDouble(reader.GetOrdinal("estimated_cost")), CultureInfo.InvariantCulture),
        ActualCost = Convert.ToDecimal(reader.GetDouble(reader.GetOrdinal("actual_cost")), CultureInfo.InvariantCulture),
        Message = reader.GetString(reader.GetOrdinal("message")),
        DetailsJson = reader.GetString(reader.GetOrdinal("details_json")),
        ActiveStartedAtUtc = ReadNullableUtc(reader, "active_started_at_utc"),
        PauseStartedAtUtc = ReadNullableUtc(reader, "pause_started_at_utc"),
        UpdatedAtUtc = ReadRequiredUtc(reader, "updated_at_utc")
    };

    private static string ReadNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
    }

    private static long? ReadNullableInt64(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    }

    private static DateTime ReadRequiredUtc(SqliteDataReader reader, string name) =>
        DateTime.Parse(reader.GetString(reader.GetOrdinal(name)), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static DateTime? ReadNullableUtc(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal)
            ? null
            : DateTime.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static long ElapsedMs(DateTime start, DateTime end) =>
        Math.Max(0, (long)Math.Round((end - start).TotalMilliseconds));

    private static bool IsTerminalStageStatus(string status) =>
        status is "completed" or "failed" or "cancelled" or "interrupted";

    private static void ValidateStageTransition(string current, string next)
    {
        var valid = (current, next) switch
        {
            ("queued", "running" or "cancelled" or "interrupted") => true,
            ("running", "paused" or "completed" or "failed" or "cancelled" or "interrupted") => true,
            ("paused", "running" or "failed" or "cancelled" or "interrupted") => true,
            _ => false
        };
        if (!valid)
            throw new InvalidOperationException($"Cannot transition processing stage attempt from '{current}' to '{next}'.");
    }

    private const string WorkspaceSchemaSql = """
        CREATE TABLE IF NOT EXISTS workspaces (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            kind TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'ready',
            source_label TEXT NOT NULL DEFAULT '',
            source_identity TEXT NOT NULL DEFAULT '',
            root_path TEXT NOT NULL,
            manifest_json TEXT NOT NULL DEFAULT '{}',
            source_bytes INTEGER NOT NULL DEFAULT 0,
            extracted_bytes INTEGER NOT NULL DEFAULT 0,
            derived_bytes INTEGER NOT NULL DEFAULT 0,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_workspaces_updated ON workspaces(updated_at_utc DESC);

        CREATE TABLE IF NOT EXISTS workspace_processing_runs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            workspace_id TEXT NOT NULL,
            job_id INTEGER NULL,
            name TEXT NOT NULL DEFAULT '',
            status TEXT NOT NULL DEFAULT 'queued',
            profile_json TEXT NOT NULL DEFAULT '{}',
            requested_stages_json TEXT NOT NULL DEFAULT '[]',
            estimate_json TEXT NOT NULL DEFAULT '{}',
            actual_summary_json TEXT NOT NULL DEFAULT '{}',
            queued_at_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            completed_at_utc TEXT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY(workspace_id) REFERENCES workspaces(id) ON DELETE CASCADE,
            FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS idx_workspace_runs_workspace ON workspace_processing_runs(workspace_id, queued_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_workspace_runs_status ON workspace_processing_runs(status, queued_at_utc);

        CREATE TABLE IF NOT EXISTS processing_stage_attempts (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            scope TEXT NOT NULL,
            workspace_id TEXT NULL,
            run_id INTEGER NULL,
            call_id INTEGER NULL,
            job_id INTEGER NULL,
            stage TEXT NOT NULL,
            attempt INTEGER NOT NULL DEFAULT 1,
            status TEXT NOT NULL DEFAULT 'queued',
            queued_at_utc TEXT NOT NULL,
            started_at_utc TEXT NULL,
            completed_at_utc TEXT NULL,
            queue_duration_ms INTEGER NOT NULL DEFAULT 0,
            active_duration_ms INTEGER NOT NULL DEFAULT 0,
            paused_duration_ms INTEGER NOT NULL DEFAULT 0,
            wall_duration_ms INTEGER NOT NULL DEFAULT 0,
            endpoint_duration_ms INTEGER NOT NULL DEFAULT 0,
            item_count INTEGER NOT NULL DEFAULT 0,
            audio_seconds REAL NOT NULL DEFAULT 0,
            prompt_tokens INTEGER NOT NULL DEFAULT 0,
            completion_tokens INTEGER NOT NULL DEFAULT 0,
            retry_count INTEGER NOT NULL DEFAULT 0,
            failure_count INTEGER NOT NULL DEFAULT 0,
            pricing_version TEXT NOT NULL DEFAULT '',
            estimated_cost REAL NOT NULL DEFAULT 0,
            actual_cost REAL NOT NULL DEFAULT 0,
            message TEXT NOT NULL DEFAULT '',
            details_json TEXT NOT NULL DEFAULT '{}',
            active_started_at_utc TEXT NULL,
            pause_started_at_utc TEXT NULL,
            updated_at_utc TEXT NOT NULL,
            FOREIGN KEY(workspace_id) REFERENCES workspaces(id) ON DELETE CASCADE,
            FOREIGN KEY(run_id) REFERENCES workspace_processing_runs(id) ON DELETE CASCADE,
            FOREIGN KEY(job_id) REFERENCES jobs(id) ON DELETE SET NULL
        );
        CREATE INDEX IF NOT EXISTS idx_processing_stage_attempts_run ON processing_stage_attempts(run_id, stage, attempt);
        CREATE INDEX IF NOT EXISTS idx_processing_stage_attempts_status ON processing_stage_attempts(scope, status, queued_at_utc);
        CREATE INDEX IF NOT EXISTS idx_processing_stage_attempts_call ON processing_stage_attempts(call_id, stage, attempt);
        """;
}
