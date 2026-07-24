using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace pizzad;

public sealed partial class EngineDatabase : IIncidentBatchStore
{
    public Task<IncidentBatchRunResult> AppendIncidentBatchRunAsync(
        IncidentBatchLedgerEntry entry,
        IncidentBatchProjection projection,
        CancellationToken ct) =>
        AppendIncidentBatchRunWithVerificationRequestsAsync(entry, projection, [], ct);

    public async Task<IncidentBatchRunResult> AppendIncidentBatchRunWithVerificationRequestsAsync(
        IncidentBatchLedgerEntry entry,
        IncidentBatchProjection projection,
        IReadOnlyList<IncidentBatchVerificationRequest> verificationRequests,
        CancellationToken ct)
    {
        var entryValidation = IncidentBatchContract.ValidateLedgerEntry(entry);
        if (!entryValidation.IsValid)
            throw new ArgumentException(string.Join("; ", entryValidation.Errors), nameof(entry));
        var projectionValidation = IncidentBatchContract.ValidateProjection(projection);
        if (!projectionValidation.IsValid)
            throw new ArgumentException(string.Join("; ", projectionValidation.Errors), nameof(projection));
        if (entry.RunId != projection.RunId || !projection.LedgerEntryIds.Contains(entry.LedgerEntryId, StringComparer.Ordinal))
            throw new ArgumentException("batch projection does not belong to or reference the appended ledger entry", nameof(projection));
        var requestValidation = IncidentBatchVerificationQueueContract.Validate(entry, verificationRequests);
        if (!requestValidation.IsValid)
            throw new ArgumentException(string.Join("; ", requestValidation.Errors), nameof(verificationRequests));

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction();
        if (entry.Execution.BaseProjectionSequence is long expectedBaseProjectionSequence)
        {
            await using var baseCommand = connection.CreateCommand();
            baseCommand.Transaction = transaction;
            baseCommand.CommandText = """
                SELECT COALESCE(MAX(sequence), 0)
                FROM incident_batch_constructor_shadow_projections
                WHERE run_id=$run_id;
                """;
            baseCommand.Parameters.AddWithValue("$run_id", entry.RunId);
            var actualBaseProjectionSequence = Convert.ToInt64(await baseCommand.ExecuteScalarAsync(ct));
            if (actualBaseProjectionSequence != expectedBaseProjectionSequence)
            {
                throw new InvalidOperationException(
                    $"incident batch projection advanced while intake was running: expected {expectedBaseProjectionSequence}, actual {actualBaseProjectionSequence}");
            }
        }
        var entryPayload = JsonSerializer.Serialize(entry, EngineConfig.JsonOptions());
        var entryHash = ContentHash(entryPayload);
        await using var entryCommand = connection.CreateCommand();
        entryCommand.Transaction = transaction;
        entryCommand.CommandText = """
            INSERT INTO incident_batch_constructor_shadow_ledger (
                run_id, ledger_entry_id, recorded_at_utc, bundle_id, proposal_id, content_hash, payload_json)
            VALUES ($run_id, $ledger_entry_id, $recorded_at_utc, $bundle_id, $proposal_id, $content_hash, $payload_json);
            SELECT last_insert_rowid();
            """;
        entryCommand.Parameters.AddWithValue("$run_id", entry.RunId);
        entryCommand.Parameters.AddWithValue("$ledger_entry_id", entry.LedgerEntryId);
        entryCommand.Parameters.AddWithValue("$recorded_at_utc", entry.RecordedAtUtc.UtcDateTime.ToString("O"));
        entryCommand.Parameters.AddWithValue("$bundle_id", entry.Bundle.BundleId);
        entryCommand.Parameters.AddWithValue("$proposal_id", entry.Proposal.ProposalId);
        entryCommand.Parameters.AddWithValue("$content_hash", entryHash);
        entryCommand.Parameters.AddWithValue("$payload_json", entryPayload);
        var entrySequence = Convert.ToInt64(await entryCommand.ExecuteScalarAsync(ct));

        var observationsById = entry.Bundle.Observations.ToDictionary(
            item => item.ObservationId,
            StringComparer.Ordinal);
        foreach (var observationId in entry.NewObservationIds)
        {
            var observation = observationsById[observationId];
            if (observation.CallId is not long callId)
                continue;
            await using var processedCommand = connection.CreateCommand();
            processedCommand.Transaction = transaction;
            processedCommand.CommandText = """
                INSERT INTO incident_batch_processed_calls (
                    run_id, call_id, ledger_entry_id, processed_at_utc, source_start_time)
                VALUES (
                    $run_id, $call_id, $ledger_entry_id, $processed_at_utc, $source_start_time);
                """;
            processedCommand.Parameters.AddWithValue("$run_id", entry.RunId);
            processedCommand.Parameters.AddWithValue("$call_id", callId);
            processedCommand.Parameters.AddWithValue("$ledger_entry_id", entry.LedgerEntryId);
            processedCommand.Parameters.AddWithValue("$processed_at_utc", entry.RecordedAtUtc.UtcDateTime.ToString("O"));
            processedCommand.Parameters.AddWithValue("$source_start_time", observation.ObservedAtUnixSeconds);
            await processedCommand.ExecuteNonQueryAsync(ct);
        }

        await ValidateBatchProjectionLedgerReferencesAsync(connection, transaction, projection, ct);
        var projectionPayload = JsonSerializer.Serialize(projection, EngineConfig.JsonOptions());
        var projectionHash = ContentHash(projectionPayload);
        await using var projectionCommand = connection.CreateCommand();
        projectionCommand.Transaction = transaction;
        projectionCommand.CommandText = """
            INSERT INTO incident_batch_constructor_shadow_projections (
                run_id, projection_id, generated_at_utc, content_hash, payload_json)
            VALUES ($run_id, $projection_id, $generated_at_utc, $content_hash, $payload_json);
            SELECT last_insert_rowid();
            """;
        projectionCommand.Parameters.AddWithValue("$run_id", projection.RunId);
        projectionCommand.Parameters.AddWithValue("$projection_id", projection.ProjectionId);
        projectionCommand.Parameters.AddWithValue("$generated_at_utc", projection.GeneratedAtUtc.UtcDateTime.ToString("O"));
        projectionCommand.Parameters.AddWithValue("$content_hash", projectionHash);
        projectionCommand.Parameters.AddWithValue("$payload_json", projectionPayload);
        var projectionSequence = Convert.ToInt64(await projectionCommand.ExecuteScalarAsync(ct));

        foreach (var request in verificationRequests)
        {
            var requestPayload = JsonSerializer.Serialize(request, EngineConfig.JsonOptions());
            var requestHash = ContentHash(requestPayload);
            await using var requestCommand = connection.CreateCommand();
            requestCommand.Transaction = transaction;
            requestCommand.CommandText = """
                INSERT INTO incident_batch_verification_shadow_requests (
                    run_id, request_id, source_ledger_entry_id, enqueued_at_utc, content_hash, payload_json)
                VALUES ($run_id, $request_id, $source_ledger_entry_id, $enqueued_at_utc, $content_hash, $payload_json);
                """;
            requestCommand.Parameters.AddWithValue("$run_id", request.RunId);
            requestCommand.Parameters.AddWithValue("$request_id", request.RequestId);
            requestCommand.Parameters.AddWithValue("$source_ledger_entry_id", request.SourceLedgerEntryId);
            requestCommand.Parameters.AddWithValue("$enqueued_at_utc", request.EnqueuedAtUtc.UtcDateTime.ToString("O"));
            requestCommand.Parameters.AddWithValue("$content_hash", requestHash);
            requestCommand.Parameters.AddWithValue("$payload_json", requestPayload);
            await requestCommand.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new IncidentBatchRunResult(
            new IncidentBatchStoredLedgerEntry(entrySequence, entryHash, entry),
            new IncidentBatchStoredProjection(projectionSequence, projectionHash, projection));
    }

    public async Task<IncidentBatchStoredProjection?> GetLatestIncidentBatchProjectionAsync(string runId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_batch_constructor_shadow_projections
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
        VerifyContentHash("incident batch projection", sequence, payload, hash);
        var projection = JsonSerializer.Deserialize<IncidentBatchProjection>(payload, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException($"Incident batch projection {sequence} has an empty payload.");
        return new IncidentBatchStoredProjection(sequence, hash, projection);
    }

    public async Task<IncidentBatchStoredLedgerEntry?> GetLatestIncidentBatchLedgerEntryAsync(string runId, CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_batch_constructor_shadow_ledger
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
        VerifyContentHash("incident batch ledger entry", sequence, payload, hash);
        var entry = JsonSerializer.Deserialize<IncidentBatchLedgerEntry>(payload, EngineConfig.JsonOptions())
                    ?? throw new InvalidDataException($"Incident batch ledger entry {sequence} has an empty payload.");
        return new IncidentBatchStoredLedgerEntry(sequence, hash, entry);
    }

    public async Task<IncidentBatchStoredLedgerEntry?> GetIncidentBatchLedgerEntryAsync(
        string runId,
        string ledgerEntryId,
        CancellationToken ct)
    {
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_batch_constructor_shadow_ledger
            WHERE run_id=$run_id AND ledger_entry_id=$ledger_entry_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$ledger_entry_id", ledgerEntryId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        var sequence = reader.GetInt64(0);
        var hash = reader.GetString(1);
        var payload = reader.GetString(2);
        VerifyContentHash("incident batch ledger entry", sequence, payload, hash);
        var entry = JsonSerializer.Deserialize<IncidentBatchLedgerEntry>(payload, EngineConfig.JsonOptions())
                    ?? throw new InvalidDataException($"Incident batch ledger entry {sequence} has an empty payload.");
        return new IncidentBatchStoredLedgerEntry(sequence, hash, entry);
    }

    public async Task<IReadOnlyList<IncidentBatchStoredLedgerEntry>> ListIncidentBatchLedgerEntriesAsync(string runId, int limit, CancellationToken ct)
    {
        var rows = new List<IncidentBatchStoredLedgerEntry>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_batch_constructor_shadow_ledger
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
            var hash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("incident batch ledger entry", sequence, payload, hash);
            var entry = JsonSerializer.Deserialize<IncidentBatchLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident batch ledger entry {sequence} has an empty payload.");
            rows.Add(new IncidentBatchStoredLedgerEntry(sequence, hash, entry));
        }
        return rows;
    }

    public async Task<IReadOnlySet<long>> ListIncidentBatchProcessedCallIdsAsync(string runId, CancellationToken ct)
    {
        var callIds = new HashSet<long>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT call_id
            FROM incident_batch_processed_calls
            WHERE run_id=$run_id
            ORDER BY call_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            callIds.Add(reader.GetInt64(0));
        return callIds;
    }

    public async Task<IncidentAnalysisQueueHealthDto> GetIncidentBatchPipelineHealthAsync(
        string runId,
        long startAfterCallId,
        int maximumAgeMinutes,
        CancellationToken ct)
    {
        runId = runId.Trim();
        maximumAgeMinutes = Math.Clamp(maximumAgeMinutes, 15, 360);
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMinutes(-maximumAgeMinutes).ToUnixTimeSeconds();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN processed.call_id IS NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN processed.call_id IS NULL AND calls.start_time < $cutoff THEN 1 ELSE 0 END), 0),
                MIN(CASE WHEN processed.call_id IS NULL THEN calls.start_time END),
                (
                    SELECT MAX(source_start_time)
                    FROM incident_batch_processed_calls
                    WHERE run_id=$run_id
                ),
                (
                    SELECT COUNT(*)
                    FROM incident_batch_verification_shadow_requests request
                    LEFT JOIN incident_batch_verification_shadow_results result
                        ON result.request_id=request.request_id
                    WHERE request.run_id=$run_id AND result.request_id IS NULL
                ),
                (
                    SELECT MIN(request.enqueued_at_utc)
                    FROM incident_batch_verification_shadow_requests request
                    LEFT JOIN incident_batch_verification_shadow_results result
                        ON result.request_id=request.request_id
                    WHERE request.run_id=$run_id AND result.request_id IS NULL
                )
            FROM calls
            LEFT JOIN incident_batch_processed_calls processed
                ON processed.run_id=$run_id AND processed.call_id=calls.id
            WHERE calls.id > $start_after_call_id
              AND LENGTH(TRIM(COALESCE(calls.transcription, ''))) >= $minimum_characters;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$start_after_call_id", Math.Max(0, startAfterCallId));
        command.Parameters.AddWithValue("$minimum_characters", TranscriptRetrievalEvidence.MinimumCharacters);
        command.Parameters.AddWithValue("$cutoff", cutoff);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var pending = reader.GetInt64(0);
        var stale = reader.GetInt64(1);
        var pendingVerifications = reader.GetInt64(4);
        DateTime? oldestUtc = null;
        var oldestAgeMinutes = 0d;
        if (!reader.IsDBNull(2))
        {
            var oldest = reader.GetInt64(2);
            oldestUtc = DateTimeOffset.FromUnixTimeSeconds(oldest).UtcDateTime;
            oldestAgeMinutes = Math.Max(0, (now - DateTimeOffset.FromUnixTimeSeconds(oldest)).TotalMinutes);
        }
        DateTime? latestProcessedUtc = null;
        var latestProcessedAgeMinutes = 0d;
        if (!reader.IsDBNull(3))
        {
            var latest = reader.GetInt64(3);
            latestProcessedUtc = DateTimeOffset.FromUnixTimeSeconds(latest).UtcDateTime;
            latestProcessedAgeMinutes = Math.Max(0, (now - DateTimeOffset.FromUnixTimeSeconds(latest)).TotalMinutes);
        }
        DateTimeOffset? oldestVerificationUtc = null;
        var oldestVerificationAgeMinutes = 0d;
        if (!reader.IsDBNull(5) &&
            DateTimeOffset.TryParse(
                reader.GetString(5),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsedVerificationUtc))
        {
            oldestVerificationUtc = parsedVerificationUtc;
            oldestVerificationAgeMinutes = Math.Max(0, (now - parsedVerificationUtc).TotalMinutes);
        }

        var sourceWorkStale = stale > 0;
        var verificationWorkStale = pendingVerifications > 0 &&
                                    oldestVerificationAgeMinutes > maximumAgeMinutes;
        var status = sourceWorkStale || verificationWorkStale ? "degraded" : "ok";
        var message = status == "degraded"
            ? sourceWorkStale
                ? $"Replacement incident intake is stale: {stale:N0} eligible call(s) have waited more than {maximumAgeMinutes:N0} minutes."
                : $"Replacement incident verification is stale: {pendingVerifications:N0} decision(s) are pending and the oldest has waited {oldestVerificationAgeMinutes:N0} minutes."
            : pending > 0 || pendingVerifications > 0
                ? $"Replacement incident pipeline is current: {pending:N0} eligible call(s) and {pendingVerifications:N0} verification decision(s) are pending."
                : "Replacement incident pipeline is current.";
        return new IncidentAnalysisQueueHealthDto(
            status,
            message,
            pending,
            stale,
            0,
            oldestUtc,
            oldestAgeMinutes,
            latestProcessedUtc,
            latestProcessedAgeMinutes,
            maximumAgeMinutes);
    }

    public async Task<IReadOnlyList<IncidentBatchStoredVerificationRequest>> ListIncidentBatchVerificationRequestsAsync(
        string runId,
        int limit,
        CancellationToken ct)
    {
        var rows = new List<IncidentBatchStoredVerificationRequest>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_batch_verification_shadow_requests
            WHERE run_id=$run_id
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var hash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("incident batch verification request", sequence, payload, hash);
            var request = JsonSerializer.Deserialize<IncidentBatchVerificationRequest>(payload, EngineConfig.JsonOptions())
                          ?? throw new InvalidDataException($"Incident batch verification request {sequence} has an empty payload.");
            rows.Add(new IncidentBatchStoredVerificationRequest(sequence, hash, request));
        }
        return rows;
    }

    public async Task<IReadOnlyList<IncidentBatchStoredVerificationRequest>> ListPendingIncidentBatchVerificationRequestsAsync(
        string runId,
        int limit,
        CancellationToken ct)
    {
        var rows = new List<IncidentBatchStoredVerificationRequest>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request.sequence, request.content_hash, request.payload_json
            FROM incident_batch_verification_shadow_requests request
            LEFT JOIN incident_batch_verification_shadow_results result ON result.request_id=request.request_id
            WHERE request.run_id=$run_id AND result.sequence IS NULL
            ORDER BY request.sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var hash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("incident batch verification request", sequence, payload, hash);
            var request = JsonSerializer.Deserialize<IncidentBatchVerificationRequest>(payload, EngineConfig.JsonOptions())
                          ?? throw new InvalidDataException($"Incident batch verification request {sequence} has an empty payload.");
            rows.Add(new IncidentBatchStoredVerificationRequest(sequence, hash, request));
        }
        return rows;
    }

    public async Task<IReadOnlyList<IncidentBatchStoredVerificationResult>> ListIncidentBatchVerificationResultsAsync(
        string runId,
        int limit,
        CancellationToken ct)
    {
        var rows = new List<IncidentBatchStoredVerificationResult>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_batch_verification_shadow_results
            WHERE run_id=$run_id
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var hash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("incident batch verification result", sequence, payload, hash);
            var result = JsonSerializer.Deserialize<IncidentBatchVerificationResult>(payload, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException($"Incident batch verification result {sequence} has an empty payload.");
            rows.Add(new IncidentBatchStoredVerificationResult(sequence, hash, result));
        }
        return rows;
    }

    public async Task<IncidentBatchStoredVerificationResult> AppendIncidentBatchVerificationResultAsync(
        long baseProjectionSequence,
        IncidentBatchLedgerEntry sourceEntry,
        IncidentBatchVerificationRequest request,
        IncidentBatchVerificationResult result,
        IncidentBatchProjection projection,
        CancellationToken ct)
    {
        var appended = await AppendIncidentBatchVerificationResultCoreAsync(
            baseProjectionSequence,
            sourceEntry,
            request,
            result,
            projection,
            null,
            ct);
        return appended.Result;
    }

    public async Task<(IncidentBatchStoredVerificationResult Result, IncidentBatchStoredCanaryCommit Commit)>
        AppendIncidentBatchVerificationResultWithCanaryAsync(
            long baseProjectionSequence,
            IncidentBatchLedgerEntry sourceEntry,
            IncidentBatchVerificationRequest request,
            IncidentBatchVerificationResult result,
            IncidentBatchProjection projection,
            CancellationToken ct)
    {
        var blocked = IncidentBatchCanaryGate.BlockReason(_config.AiInsights);
        if (!string.IsNullOrEmpty(blocked))
            throw new InvalidOperationException($"incident batch canary persistence is blocked: {blocked}");
        if (!string.Equals(
                request.RunId,
                _config.AiInsights.IncidentBatchConstructorShadowRunId.Trim(),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "incident batch canary persistence is blocked: verification belongs to a different run");
        }
        var intent = IncidentBatchCanaryContract.BuildIntent(sourceEntry, request, result, projection)
                     ?? throw new InvalidOperationException("only independently verified incident state can be persisted by the incident batch canary");
        var appended = await AppendIncidentBatchVerificationResultCoreAsync(
            baseProjectionSequence,
            sourceEntry,
            request,
            result,
            projection,
            intent,
            ct);
        return (appended.Result, appended.Commit
            ?? throw new InvalidOperationException("incident batch canary commit was not recorded"));
    }

    private async Task<(IncidentBatchStoredVerificationResult Result, IncidentBatchStoredCanaryCommit? Commit)>
        AppendIncidentBatchVerificationResultCoreAsync(
            long baseProjectionSequence,
            IncidentBatchLedgerEntry sourceEntry,
            IncidentBatchVerificationRequest request,
            IncidentBatchVerificationResult result,
            IncidentBatchProjection projection,
            IncidentBatchCanaryPersistenceIntent? canaryIntent,
            CancellationToken ct)
    {
        var resultValidation = IncidentBatchVerificationQueueContract.ValidateResult(sourceEntry, request, result);
        if (!resultValidation.IsValid)
            throw new ArgumentException(string.Join("; ", resultValidation.Errors), nameof(result));
        var projectionValidation = IncidentBatchContract.ValidateProjection(projection);
        if (!projectionValidation.IsValid)
            throw new ArgumentException(string.Join("; ", projectionValidation.Errors), nameof(projection));
        if (projection.RunId != request.RunId)
            throw new ArgumentException("verification projection belongs to a different run", nameof(projection));

        await using var connection = OpenConnection();
        await using var transaction = connection.BeginTransaction();
        await using (var latestCommand = connection.CreateCommand())
        {
            latestCommand.Transaction = transaction;
            latestCommand.CommandText = """
                SELECT sequence, content_hash, payload_json
                FROM incident_batch_constructor_shadow_projections
                WHERE run_id=$run_id
                ORDER BY sequence DESC
                LIMIT 1;
                """;
            latestCommand.Parameters.AddWithValue("$run_id", request.RunId);
            await using var reader = await latestCommand.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("incident batch verification has no base projection");
            var latestSequence = reader.GetInt64(0);
            if (latestSequence != baseProjectionSequence)
                throw new InvalidOperationException("incident batch projection advanced while verification was running");
            var latestHash = reader.GetString(1);
            var latestPayload = reader.GetString(2);
            VerifyContentHash("incident batch projection", latestSequence, latestPayload, latestHash);
            var latestProjection = JsonSerializer.Deserialize<IncidentBatchProjection>(
                                       latestPayload,
                                       EngineConfig.JsonOptions())
                                   ?? throw new InvalidDataException(
                                       $"Incident batch projection {latestSequence} has an empty payload.");
            var expectedProjection = IncidentBatchVerificationProjector.Apply(
                latestProjection,
                sourceEntry,
                request,
                result,
                projection.ProjectionId,
                projection.GeneratedAtUtc);
            var expectedPayload = JsonSerializer.Serialize(expectedProjection, EngineConfig.JsonOptions());
            var suppliedPayload = JsonSerializer.Serialize(projection, EngineConfig.JsonOptions());
            if (!string.Equals(expectedPayload, suppliedPayload, StringComparison.Ordinal))
                throw new InvalidOperationException("incident batch verification projection does not match the verified transition");
        }

        await using (var requestCommand = connection.CreateCommand())
        {
            requestCommand.Transaction = transaction;
            requestCommand.CommandText = "SELECT content_hash, payload_json FROM incident_batch_verification_shadow_requests WHERE request_id=$request_id;";
            requestCommand.Parameters.AddWithValue("$request_id", request.RequestId);
            await using var reader = await requestCommand.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException("incident batch verification request is not persisted");
            var hash = reader.GetString(0);
            var payload = reader.GetString(1);
            VerifyContentHash("incident batch verification request", 0, payload, hash);
            var persisted = JsonSerializer.Deserialize<IncidentBatchVerificationRequest>(payload, EngineConfig.JsonOptions());
            if (persisted != request)
                throw new InvalidOperationException("persisted incident batch verification request does not match the result");
        }

        var resultPayload = JsonSerializer.Serialize(result, EngineConfig.JsonOptions());
        var resultHash = ContentHash(resultPayload);
        await using (var resultCommand = connection.CreateCommand())
        {
            resultCommand.Transaction = transaction;
            resultCommand.CommandText = """
                INSERT INTO incident_batch_verification_shadow_results (
                    run_id, result_id, request_id, recorded_at_utc, content_hash, payload_json)
                VALUES ($run_id, $result_id, $request_id, $recorded_at_utc, $content_hash, $payload_json);
                SELECT last_insert_rowid();
                """;
            resultCommand.Parameters.AddWithValue("$run_id", result.RunId);
            resultCommand.Parameters.AddWithValue("$result_id", result.ResultId);
            resultCommand.Parameters.AddWithValue("$request_id", result.RequestId);
            resultCommand.Parameters.AddWithValue("$recorded_at_utc", result.RecordedAtUtc.UtcDateTime.ToString("O"));
            resultCommand.Parameters.AddWithValue("$content_hash", resultHash);
            resultCommand.Parameters.AddWithValue("$payload_json", resultPayload);
            var resultSequence = Convert.ToInt64(await resultCommand.ExecuteScalarAsync(ct));

            await ValidateBatchProjectionLedgerReferencesAsync(connection, transaction, projection, ct);
            var projectionPayload = JsonSerializer.Serialize(projection, EngineConfig.JsonOptions());
            var projectionHash = ContentHash(projectionPayload);
            await using var projectionCommand = connection.CreateCommand();
            projectionCommand.Transaction = transaction;
            projectionCommand.CommandText = """
                INSERT INTO incident_batch_constructor_shadow_projections (
                    run_id, projection_id, generated_at_utc, content_hash, payload_json)
                VALUES ($run_id, $projection_id, $generated_at_utc, $content_hash, $payload_json);
                """;
            projectionCommand.Parameters.AddWithValue("$run_id", projection.RunId);
            projectionCommand.Parameters.AddWithValue("$projection_id", projection.ProjectionId);
            projectionCommand.Parameters.AddWithValue("$generated_at_utc", projection.GeneratedAtUtc.UtcDateTime.ToString("O"));
            projectionCommand.Parameters.AddWithValue("$content_hash", projectionHash);
            projectionCommand.Parameters.AddWithValue("$payload_json", projectionPayload);
            await projectionCommand.ExecuteNonQueryAsync(ct);

            IncidentBatchStoredCanaryCommit? canaryCommit = null;
            if (canaryIntent is not null)
            {
                canaryCommit = await PersistIncidentBatchCanaryAsync(
                    connection,
                    transaction,
                    canaryIntent,
                    result.RecordedAtUtc,
                    ct);
            }
            await transaction.CommitAsync(ct);
            return (
                new IncidentBatchStoredVerificationResult(resultSequence, resultHash, result),
                canaryCommit);
        }
    }

    public async Task<IReadOnlyList<IncidentBatchStoredCanaryCommit>> ListIncidentBatchCanaryCommitsAsync(
        string runId,
        int limit,
        CancellationToken ct)
    {
        var rows = new List<IncidentBatchStoredCanaryCommit>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_batch_canary_commits
            WHERE run_id=$run_id
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var hash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("incident batch canary commit", sequence, payload, hash);
            var commit = JsonSerializer.Deserialize<IncidentBatchCanaryCommit>(payload, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException($"Incident batch canary commit {sequence} has an empty payload.");
            rows.Add(new IncidentBatchStoredCanaryCommit(sequence, hash, commit));
        }
        return rows;
    }

    private static async Task<IncidentBatchStoredCanaryCommit> PersistIncidentBatchCanaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IncidentBatchCanaryPersistenceIntent intent,
        DateTimeOffset recordedAtUtc,
        CancellationToken ct)
    {
        var callParameters = intent.CallIds.Select((_, index) => $"$call{index}").ToList();
        var calls = new List<(long Id, long StartTime, string SystemShortName)>();
        await using (var callsCommand = connection.CreateCommand())
        {
            callsCommand.Transaction = transaction;
            callsCommand.CommandText = $"""
                SELECT id, start_time, COALESCE(system_short_name, '')
                FROM calls
                WHERE id IN ({string.Join(",", callParameters)})
                ORDER BY start_time, id;
                """;
            for (var index = 0; index < intent.CallIds.Count; index++)
                callsCommand.Parameters.AddWithValue(callParameters[index], intent.CallIds[index]);
            await using var reader = await callsCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                calls.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2)));
        }
        if (calls.Count != intent.CallIds.Count)
            throw new InvalidDataException("verified canary incident references a call that is absent from the database");

        long incidentId;
        await using (var existingIncident = connection.CreateCommand())
        {
            existingIncident.Transaction = transaction;
            existingIncident.CommandText = "SELECT id FROM incidents WHERE incident_key=$incident_key;";
            existingIncident.Parameters.AddWithValue("$incident_key", intent.IncidentKey);
            incidentId = Convert.ToInt64(await existingIncident.ExecuteScalarAsync(ct) ?? 0L);
        }

        var conflictingOwners = new List<(long CallId, long IncidentId)>();
        await using (var ownerCommand = connection.CreateCommand())
        {
            ownerCommand.Transaction = transaction;
            ownerCommand.CommandText = $"""
                SELECT call_id, incident_id
                FROM incident_calls
                WHERE call_id IN ({string.Join(",", callParameters)})
                  AND incident_id <> $canary_incident_id
                ORDER BY call_id, incident_id;
                """;
            for (var index = 0; index < intent.CallIds.Count; index++)
                ownerCommand.Parameters.AddWithValue(callParameters[index], intent.CallIds[index]);
            ownerCommand.Parameters.AddWithValue("$canary_incident_id", incidentId);
            await using var reader = await ownerCommand.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                conflictingOwners.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        var created = incidentId == 0;
        var outcome = IncidentBatchCanaryCommitOutcome.Persisted;
        string reason;
        if (conflictingOwners.Count > 0)
        {
            outcome = IncidentBatchCanaryCommitOutcome.Conflict;
            var conflict = conflictingOwners[0];
            reason = $"call {conflict.CallId} is already owned by incident {conflict.IncidentId}";
        }
        else
        {
            var firstSeen = calls.Min(item => item.StartTime);
            var lastSeen = calls.Max(item => item.StartTime);
            var now = recordedAtUtc.UtcDateTime.ToString("O");
            if (created)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO incidents (
                        incident_key, title, detail, category, status, first_seen, last_seen,
                        incident_score, source_summary_ids, created_at_utc, updated_at_utc)
                    VALUES (
                        $incident_key, $title, $detail, 'other', 'active', $first_seen, $last_seen,
                        $score, '[]', $now, $now);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$incident_key", intent.IncidentKey);
                insert.Parameters.AddWithValue("$title", intent.Title);
                insert.Parameters.AddWithValue("$detail", intent.Detail);
                insert.Parameters.AddWithValue("$first_seen", firstSeen);
                insert.Parameters.AddWithValue("$last_seen", lastSeen);
                insert.Parameters.AddWithValue("$score", intent.Score);
                insert.Parameters.AddWithValue("$now", now);
                incidentId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
            }
            else
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE incidents
                    SET title=$title,
                        detail=$detail,
                        status='active',
                        first_seen=$first_seen,
                        last_seen=$last_seen,
                        incident_score=$score,
                        updated_at_utc=$now
                    WHERE id=$incident_id;
                    """;
                update.Parameters.AddWithValue("$title", intent.Title);
                update.Parameters.AddWithValue("$detail", intent.Detail);
                update.Parameters.AddWithValue("$first_seen", firstSeen);
                update.Parameters.AddWithValue("$last_seen", lastSeen);
                update.Parameters.AddWithValue("$score", intent.Score);
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$incident_id", incidentId);
                await update.ExecuteNonQueryAsync(ct);

                await using var clearLinks = connection.CreateCommand();
                clearLinks.Transaction = transaction;
                clearLinks.CommandText = "DELETE FROM incident_calls WHERE incident_id=$incident_id;";
                clearLinks.Parameters.AddWithValue("$incident_id", incidentId);
                await clearLinks.ExecuteNonQueryAsync(ct);
            }

            foreach (var callId in intent.CallIds)
            {
                await using var link = connection.CreateCommand();
                link.Transaction = transaction;
                link.CommandText = "INSERT INTO incident_calls (incident_id, call_id) VALUES ($incident_id, $call_id);";
                link.Parameters.AddWithValue("$incident_id", incidentId);
                link.Parameters.AddWithValue("$call_id", callId);
                await link.ExecuteNonQueryAsync(ct);
            }
            reason = created
                ? "independently verified incident state created the canary incident"
                : "independently verified incident state updated the canary incident";
        }

        var commit = new IncidentBatchCanaryCommit(
            $"{intent.RequestId}:canary:{intent.ResultId}",
            intent.RunId,
            intent.RequestId,
            intent.ResultId,
            intent.ProjectionId,
            intent.ProjectionEventId,
            recordedAtUtc,
            outcome,
            incidentId,
            intent.IncidentKey,
            intent.CallIds,
            reason);
        var payload = JsonSerializer.Serialize(commit, EngineConfig.JsonOptions());
        var hash = ContentHash(payload);
        long sequence;
        await using (var commitCommand = connection.CreateCommand())
        {
            commitCommand.Transaction = transaction;
            commitCommand.CommandText = """
                INSERT INTO incident_batch_canary_commits (
                    run_id, commit_id, request_id, result_id, projection_id,
                    projection_event_id, recorded_at_utc, outcome, incident_id,
                    incident_key, content_hash, payload_json)
                VALUES (
                    $run_id, $commit_id, $request_id, $result_id, $projection_id,
                    $projection_event_id, $recorded_at_utc, $outcome, $incident_id,
                    $incident_key, $content_hash, $payload_json);
                SELECT last_insert_rowid();
                """;
            commitCommand.Parameters.AddWithValue("$run_id", commit.RunId);
            commitCommand.Parameters.AddWithValue("$commit_id", commit.CommitId);
            commitCommand.Parameters.AddWithValue("$request_id", commit.RequestId);
            commitCommand.Parameters.AddWithValue("$result_id", commit.ResultId);
            commitCommand.Parameters.AddWithValue("$projection_id", commit.ProjectionId);
            commitCommand.Parameters.AddWithValue("$projection_event_id", commit.ProjectionEventId);
            commitCommand.Parameters.AddWithValue("$recorded_at_utc", commit.RecordedAtUtc.UtcDateTime.ToString("O"));
            commitCommand.Parameters.AddWithValue("$outcome", commit.Outcome.ToString());
            commitCommand.Parameters.AddWithValue("$incident_id", commit.IncidentId);
            commitCommand.Parameters.AddWithValue("$incident_key", commit.IncidentKey);
            commitCommand.Parameters.AddWithValue("$content_hash", hash);
            commitCommand.Parameters.AddWithValue("$payload_json", payload);
            sequence = Convert.ToInt64(await commitCommand.ExecuteScalarAsync(ct));
        }

        var systemShortNames = calls
            .Select(item => item.SystemShortName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var systemShortName = systemShortNames.Count == 1 ? systemShortNames[0] : "cross-system";
        var accepted = outcome == IncidentBatchCanaryCommitOutcome.Persisted;
        var auditReason = accepted
            ? created
                ? "accepted:create incident via verified batch pipeline"
                : "accepted:update incident via verified batch pipeline"
            : $"rejected:verified batch canary ownership conflict: {reason}";
        var metadata = JsonSerializer.Serialize(new
        {
            title = intent.Title,
            detail = intent.Detail,
            runId = intent.RunId,
            requestId = intent.RequestId,
            resultId = intent.ResultId,
            projectionId = intent.ProjectionId,
            projectionEventId = intent.ProjectionEventId,
            canaryOutcome = outcome.ToString()
        }, EngineConfig.JsonOptions());
        await using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO incident_operation_audit (
                    timestamp_utc, system_short_name, incident_key, operation, accepted,
                    reason, score, call_ids_json, metadata_json, candidate_trace_key)
                VALUES (
                    $timestamp_utc, $system_short_name, $incident_key, 'incident_batch_canary_persist', $accepted,
                    $reason, $score, $call_ids_json, $metadata_json, $candidate_trace_key);
                """;
            audit.Parameters.AddWithValue("$timestamp_utc", recordedAtUtc.UtcDateTime.ToString("O"));
            audit.Parameters.AddWithValue("$system_short_name", systemShortName);
            audit.Parameters.AddWithValue("$incident_key", intent.IncidentKey);
            audit.Parameters.AddWithValue("$accepted", accepted ? 1 : 0);
            audit.Parameters.AddWithValue("$reason", auditReason);
            audit.Parameters.AddWithValue("$score", intent.Score);
            audit.Parameters.AddWithValue("$call_ids_json", JsonSerializer.Serialize(intent.CallIds));
            audit.Parameters.AddWithValue("$metadata_json", metadata);
            audit.Parameters.AddWithValue("$candidate_trace_key", intent.RequestId);
            await audit.ExecuteNonQueryAsync(ct);
        }

        return new IncidentBatchStoredCanaryCommit(sequence, hash, commit);
    }

    private static async Task ValidateBatchProjectionLedgerReferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IncidentBatchProjection projection,
        CancellationToken ct)
    {
        foreach (var ledgerEntryId in projection.LedgerEntryIds.Distinct(StringComparer.Ordinal))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence, content_hash, payload_json
                FROM incident_batch_constructor_shadow_ledger
                WHERE run_id=$run_id AND ledger_entry_id=$ledger_entry_id;
                """;
            command.Parameters.AddWithValue("$run_id", projection.RunId);
            command.Parameters.AddWithValue("$ledger_entry_id", ledgerEntryId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                throw new InvalidOperationException($"Incident batch ledger entry '{ledgerEntryId}' does not exist.");
            VerifyContentHash("incident batch ledger entry", reader.GetInt64(0), reader.GetString(2), reader.GetString(1));
        }
    }
}
