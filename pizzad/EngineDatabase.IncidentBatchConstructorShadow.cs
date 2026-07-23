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
            SELECT sequence, content_hash, payload_json
            FROM incident_batch_constructor_shadow_ledger
            WHERE run_id=$run_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var hash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("incident batch ledger entry", sequence, payload, hash);
            var entry = JsonSerializer.Deserialize<IncidentBatchLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident batch ledger entry {sequence} has an empty payload.");
            var newObservationIds = entry.NewObservationIds.ToHashSet(StringComparer.Ordinal);
            foreach (var observation in entry.Bundle.Observations.Where(item => newObservationIds.Contains(item.ObservationId)))
            {
                if (observation.CallId is long callId)
                    callIds.Add(callId);
            }
        }
        return callIds;
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
            latestCommand.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM incident_batch_constructor_shadow_projections WHERE run_id=$run_id;";
            latestCommand.Parameters.AddWithValue("$run_id", request.RunId);
            var latestSequence = Convert.ToInt64(await latestCommand.ExecuteScalarAsync(ct));
            if (latestSequence != baseProjectionSequence)
                throw new InvalidOperationException("incident batch projection advanced while verification was running");
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
            await transaction.CommitAsync(ct);
            return new IncidentBatchStoredVerificationResult(resultSequence, resultHash, result);
        }
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
