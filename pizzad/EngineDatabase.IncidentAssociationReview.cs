using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace pizzad;

public sealed partial class EngineDatabase
{
    public async Task<IncidentAssociationStoredReviewLedgerEntry> AppendIncidentAssociationReviewAsync(
        IncidentAssociationReviewLedgerEntry entry,
        CancellationToken ct)
    {
        var errors = IncidentAssociationReviewContract.Validate(entry).ToList();

        await using var connection = OpenConnection();
        var sourceEntries = await LoadAdmittedAssociationSourceEntriesAsync(
            connection,
            entry.RunId,
            entry.ProjectionEventId,
            ct);
        if (sourceEntries.Count == 0)
            errors.Add("the source provisional association does not exist");

        var sourceCallIds = SourceCallIds(sourceEntries);
        foreach (var callId in entry.CallIds)
        {
            if (!sourceCallIds.Contains(callId))
                errors.Add($"call {callId} is not part of the source provisional association");
        }

        if (entry.AnchorIncidentId > 0 && !await SourceAssociationContainsIncidentAsync(
                connection,
                sourceCallIds,
                entry.AnchorIncidentId,
                ct))
        {
            errors.Add($"incident {entry.AnchorIncidentId} is not an active owner in the source provisional association");
        }

        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors), nameof(entry));

        var payload = JsonSerializer.Serialize(entry, EngineConfig.JsonOptions());
        var contentHash = ContentHash(payload);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO incident_association_review_ledger (
                review_entry_id,
                recorded_at_utc,
                proposal_key,
                run_id,
                projection_event_id,
                action,
                anchor_incident_id,
                content_hash,
                payload_json
            ) VALUES (
                $review_entry_id,
                $recorded_at_utc,
                $proposal_key,
                $run_id,
                $projection_event_id,
                $action,
                $anchor_incident_id,
                $content_hash,
                $payload_json
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$review_entry_id", entry.ReviewEntryId);
        command.Parameters.AddWithValue("$recorded_at_utc", entry.RecordedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$proposal_key", entry.ProposalKey);
        command.Parameters.AddWithValue("$run_id", entry.RunId);
        command.Parameters.AddWithValue("$projection_event_id", entry.ProjectionEventId);
        command.Parameters.AddWithValue("$action", entry.Action.ToString());
        command.Parameters.AddWithValue("$anchor_incident_id", entry.AnchorIncidentId);
        command.Parameters.AddWithValue("$content_hash", contentHash);
        command.Parameters.AddWithValue("$payload_json", payload);
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        return new IncidentAssociationStoredReviewLedgerEntry(sequence, contentHash, entry);
    }

    public async Task<IReadOnlyList<IncidentAssociationStoredReviewLedgerEntry>> ListIncidentAssociationReviewsAsync(
        string runId,
        CancellationToken ct)
    {
        var rows = new List<IncidentAssociationStoredReviewLedgerEntry>();
        await using var connection = OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_association_review_ledger
            WHERE run_id=$run_id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var contentHash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("association review ledger entry", sequence, payload, contentHash);
            var entry = JsonSerializer.Deserialize<IncidentAssociationReviewLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident association review ledger entry {sequence} has an empty payload.");
            rows.Add(new IncidentAssociationStoredReviewLedgerEntry(sequence, contentHash, entry));
        }
        return rows;
    }

    public async Task<IncidentAssociationReviewReportDto> GetIncidentAssociationReviewReportAsync(
        bool enabled,
        string runId,
        long start,
        long end,
        CancellationToken ct)
    {
        runId = runId.Trim();
        if (string.IsNullOrWhiteSpace(runId))
            return new IncidentAssociationReviewReportDto(enabled, string.Empty, 0, 0, []);

        await using var connection = OpenConnection();
        var sourceEntries = await LoadAdmittedAssociationSourceEntriesAsync(connection, runId, null, ct);
        if (sourceEntries.Count == 0)
            return new IncidentAssociationReviewReportDto(enabled, runId, 0, 0, []);

        var sourceGroups = sourceEntries
            .GroupBy(row => row.Candidate.ProjectionEventId, StringComparer.Ordinal)
            .Select(group => new SourceAssociationGroup(
                group.Key,
                group.OrderBy(row => row.StoredEntry.Sequence).ToList(),
                SourceCallIds(group)))
            .Where(group => group.CallIds.Count >= 2)
            .OrderByDescending(group => group.Entries.Max(row => row.StoredEntry.Entry.RecordedAtUtc))
            .Take(100)
            .ToList();

        var allCallIds = sourceGroups.SelectMany(group => group.CallIds).Distinct().ToList();
        var calls = new List<EngineCall>();
        var owners = new Dictionary<long, IncidentCallOwnerDto>();
        foreach (var chunk in allCallIds.Chunk(200))
        {
            calls.AddRange(await ListCallsByIdsAsync(chunk, ct));
            foreach (var owner in await GetIncidentCallOwnersAsync(chunk, ct))
                owners[owner.Key] = owner.Value;
        }
        var callsById = calls.ToDictionary(call => call.Id);

        var reviews = await ListIncidentAssociationReviewsAsync(runId, ct);
        var reviewState = new Dictionary<(string ProposalKey, long CallId), IncidentAssociationReviewAction>();
        foreach (var review in reviews)
        {
            foreach (var callId in review.Entry.CallIds)
                reviewState[(review.Entry.ProposalKey, callId)] = review.Entry.Action;
        }

        var groups = new List<IncidentAssociationReviewGroupDto>();
        foreach (var sourceGroup in sourceGroups)
        {
            var availableCalls = sourceGroup.CallIds
                .Where(callsById.ContainsKey)
                .Select(callId => callsById[callId])
                .OrderBy(call => call.StartTime)
                .ToList();
            if (availableCalls.Count < 2)
                continue;
            if (availableCalls.Max(call => call.StartTime) < start || availableCalls.Min(call => call.StartTime) > end)
                continue;

            var proposalKey = IncidentAssociationReviewContract.ProposalKey(runId, sourceGroup.ProjectionEventId);
            var activeAnchorIds = sourceGroup.CallIds
                .Select(callId => owners.GetValueOrDefault(callId))
                .Where(owner => owner is not null && string.Equals(owner.Status, "active", StringComparison.OrdinalIgnoreCase))
                .Select(owner => owner!.IncidentId)
                .Distinct()
                .Order()
                .ToList();
            var allOwnerIds = sourceGroup.CallIds
                .Select(callId => owners.GetValueOrDefault(callId)?.IncidentId ?? 0)
                .Where(incidentId => incidentId > 0)
                .Distinct()
                .ToList();
            var placement = activeAnchorIds.Count == 0
                ? "review"
                : allOwnerIds.Count > 1
                    ? "inlineMerge"
                    : "inlineIncident";
            var soleAnchorId = activeAnchorIds.Count == 1 ? activeAnchorIds[0] : 0;

            var callDtos = new List<IncidentAssociationReviewCallDto>();
            foreach (var call in availableCalls)
            {
                var owner = owners.GetValueOrDefault(call.Id);
                var established = soleAnchorId > 0 && owner?.IncidentId == soleAnchorId;
                var state = established
                    ? "established"
                    : reviewState.TryGetValue((proposalKey, call.Id), out var action)
                        ? action switch
                        {
                            IncidentAssociationReviewAction.ConfirmMembership => "confirmed",
                            IncidentAssociationReviewAction.RejectMembership => "rejected",
                            _ => "deferred"
                        }
                        : "pending";
                callDtos.Add(new IncidentAssociationReviewCallDto(
                    call.Id,
                    call.StartTime,
                    call.Transcription,
                    CallAudioLinks.ForCall(call.Id, call.AudioPath),
                    call.SystemShortName,
                    call.Talkgroup,
                    call.TalkgroupName,
                    owner?.IncidentId ?? 0,
                    owner?.Title ?? string.Empty,
                    owner?.Status ?? string.Empty,
                    state));
            }

            if (callDtos.All(call => call.ReviewState == "established"))
                continue;

            var evidence = sourceGroup.Entries.Select(BuildEvidence).ToList();
            var pending = callDtos.Count(call => call.ReviewState == "pending");
            var reviewed = callDtos.Count(call => call.ReviewState is "confirmed" or "rejected" or "deferred");
            var status = pending > 0
                ? reviewed > 0 ? "partiallyReviewed" : "pending"
                : callDtos.Any(call => call.ReviewState == "confirmed")
                    ? "confirmed"
                    : callDtos.Any(call => call.ReviewState == "rejected")
                        ? "rejected"
                        : "deferred";
            groups.Add(new IncidentAssociationReviewGroupDto(
                proposalKey,
                runId,
                sourceGroup.ProjectionEventId,
                placement,
                status,
                sourceGroup.Entries.Max(row => row.StoredEntry.Entry.RecordedAtUtc).UtcDateTime,
                activeAnchorIds,
                callDtos,
                evidence,
                pending));
        }

        var pendingGroups = groups.Count(group => group.PendingCallCount > 0);
        var standalonePendingGroups = groups.Count(group => group.PendingCallCount > 0 && group.Placement == "review");
        return new IncidentAssociationReviewReportDto(
            enabled,
            runId,
            pendingGroups,
            standalonePendingGroups,
            groups);
    }

    private static IncidentAssociationReviewEvidenceDto BuildEvidence(ProvisionalAssociationSource row)
    {
        var entry = row.StoredEntry.Entry;
        var callIdsByObservation = entry.Bundle.Observations
            .Where(observation => observation.CallId > 0)
            .ToDictionary(observation => observation.ObservationId, observation => observation.CallId!.Value, StringComparer.Ordinal);
        return new IncidentAssociationReviewEvidenceDto(
            callIdsByObservation.GetValueOrDefault(entry.NewObservationId),
            row.Candidate.ObservationIds
                .Select(observationId => callIdsByObservation.GetValueOrDefault(observationId))
                .Where(callId => callId > 0)
                .Distinct()
                .ToList(),
            row.Relationship.RelationshipStatement,
            row.Relationship.Uncertainty,
            row.Relationship.NewObservationEvidence.Select(citation => citation.ExactQuote).Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
            row.Relationship.CandidateEvidence.Select(citation => citation.ExactQuote).Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
            row.Relationship.UnresolvedQuestions);
    }

    private static HashSet<long> SourceCallIds(IEnumerable<ProvisionalAssociationSource> entries)
    {
        var callIds = new HashSet<long>();
        foreach (var row in entries)
        {
            var entry = row.StoredEntry.Entry;
            var sourceObservationIds = row.Candidate.ObservationIds.Append(entry.NewObservationId).ToHashSet(StringComparer.Ordinal);
            foreach (var observation in entry.Bundle.Observations)
            {
                if (sourceObservationIds.Contains(observation.ObservationId) && observation.CallId > 0)
                    callIds.Add(observation.CallId.Value);
            }
        }
        return callIds;
    }

    private static async Task<bool> SourceAssociationContainsIncidentAsync(
        SqliteConnection connection,
        IReadOnlyCollection<long> callIds,
        long incidentId,
        CancellationToken ct)
    {
        if (callIds.Count == 0)
            return false;
        await using var command = connection.CreateCommand();
        var parameters = callIds.Select((_, index) => $"$call{index}").ToList();
        command.CommandText = $"""
            SELECT COUNT(*)
            FROM incident_calls ic
            JOIN incidents i ON i.id=ic.incident_id
            WHERE i.id=$incident_id
              AND COALESCE(i.status, 'active')='active'
              AND ic.call_id IN ({string.Join(",", parameters)});
            """;
        command.Parameters.AddWithValue("$incident_id", incidentId);
        var index = 0;
        foreach (var callId in callIds)
            command.Parameters.AddWithValue(parameters[index++], callId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }

    private static async Task<IReadOnlyList<ProvisionalAssociationSource>> LoadAdmittedAssociationSourceEntriesAsync(
        SqliteConnection connection,
        string runId,
        string? projectionEventId,
        CancellationToken ct)
    {
        var rows = new List<ProvisionalAssociationSource>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, content_hash, payload_json
            FROM incident_association_shadow_ledger
            WHERE run_id=$run_id
            ORDER BY sequence DESC
            LIMIT 1000;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sequence = reader.GetInt64(0);
            var contentHash = reader.GetString(1);
            var payload = reader.GetString(2);
            VerifyContentHash("association ledger entry", sequence, payload, contentHash);
            var entry = JsonSerializer.Deserialize<IncidentAssociationLedgerEntry>(payload, EngineConfig.JsonOptions())
                        ?? throw new InvalidDataException($"Incident association ledger entry {sequence} has an empty payload.");
            if (entry.ProposalValidationErrors.Count > 0)
                continue;
            var stored = new IncidentAssociationStoredLedgerEntry(sequence, contentHash, entry);
            foreach (var relationship in entry.Proposal.Relationships.Where(item =>
                         item.Disposition == IncidentAssociationDisposition.ProvisionalAssociation))
            {
                var candidate = entry.Candidates.SingleOrDefault(item => item.CandidateToken == relationship.CandidateToken);
                if (candidate is not null && (projectionEventId is null || candidate.ProjectionEventId == projectionEventId))
                    rows.Add(new ProvisionalAssociationSource(stored, relationship, candidate));
            }
        }
        return rows;
    }

    private sealed record SourceAssociationGroup(
        string ProjectionEventId,
        IReadOnlyList<ProvisionalAssociationSource> Entries,
        HashSet<long> CallIds);

    private sealed record ProvisionalAssociationSource(
        IncidentAssociationStoredLedgerEntry StoredEntry,
        IncidentAssociationRelationship Relationship,
        IncidentAssociationCandidate Candidate);
}
