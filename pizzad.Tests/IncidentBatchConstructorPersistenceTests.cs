using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IncidentBatchConstructorPersistenceTests
{
    [Fact]
    public async Task LedgerAndProjectionAreHashVerifiedAndAppendOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-batch-constructor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "pizzad.db");
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = path, AudioRoot = root }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = new DateTimeOffset(2026, 7, 22, 2, 0, 0, TimeSpan.Zero);
            var observation = new IncidentEventStateSourceObservation(
                "call:1", 1, now.ToUnixTimeSeconds(), string.Empty, null,
                [new IncidentEventStateTranscriptObservation("transcript:1", "Tree down across the roadway.", "test", now)],
                new Dictionary<string, IncidentEventStateMetadataObservation>());
            var bundle = new IncidentEventStateObservationBundle("bundle:1", now, [observation], []);
            var proposal = new IncidentBatchProposal(
                "proposal:1", now, "test-model", IncidentBatchPrompt.PromptIdentity,
                [new IncidentBatchEventProposal(
                    "event:1", IncidentBatchEventDisposition.NewEvent, string.Empty, ["call:1"],
                    "Tree blocking roadway", "A tree is blocking the roadway.", "The call reports a roadway obstruction.", 0.1,
                    [new IncidentEventStateTranscriptCitation("transcript:1", "Tree down")], [], [], [])]);
            var coordinator = new IncidentBatchCoordinator(new FixedProposer(proposal), database, new FixedTimeProvider(now));
            var result = await coordinator.RunAsync(
                new IncidentBatchRunRequest("run:1", "ledger:1", "projection:1", [new IncidentBatchSingletonIdentity("call:1", "event:singleton:1")], "test", "test-config"),
                bundle, null, ["call:1"], [], CancellationToken.None);

            var loadedEntry = await database.GetLatestIncidentBatchLedgerEntryAsync("run:1", CancellationToken.None);
            var loadedProjection = await database.GetLatestIncidentBatchProjectionAsync("run:1", CancellationToken.None);
            var processedCallIds = await database.ListIncidentBatchProcessedCallIdsAsync("run:1", CancellationToken.None);
            Assert.Equal(result.LedgerEntry.ContentHash, loadedEntry?.ContentHash);
            Assert.Equal(result.Projection.ContentHash, loadedProjection?.ContentHash);
            Assert.Equal([1L], processedCallIds);
            var loadedEvent = Assert.Single(loadedProjection!.Projection.Events);
            Assert.False(loadedEvent.OperatorVisible);
            Assert.True(loadedEvent.OperatorReview);
            await using (var indexedConnection = new SqliteConnection($"Data Source={path}"))
            {
                await indexedConnection.OpenAsync();
                await using var indexed = indexedConnection.CreateCommand();
                indexed.CommandText = "SELECT ledger_entry_id, source_start_time FROM incident_batch_processed_calls WHERE run_id='run:1' AND call_id=1;";
                await using var indexedReader = await indexed.ExecuteReaderAsync();
                Assert.True(await indexedReader.ReadAsync());
                Assert.Equal("ledger:1", indexedReader.GetString(0));
                Assert.Equal(now.ToUnixTimeSeconds(), indexedReader.GetInt64(1));
            }
            var report = await database.GetIncidentBatchShadowReportAsync(true, "run:1", null, 100, CancellationToken.None);
            Assert.True(report.Enabled);
            Assert.Equal(1, report.Totals.Batches);
            Assert.Equal(1, report.Totals.NewObservations);
            Assert.Equal(1, report.Totals.ProposedEvents);
            Assert.Equal(1, report.Totals.AcceptedEvents);
            Assert.Equal(0, report.Totals.RejectedEvents);
            Assert.Equal(0, report.Totals.NewEvents);
            Assert.Equal(1, report.Totals.ProvisionalEvents);
            Assert.Equal(0, report.Totals.UnresolvedObservations);
            Assert.Equal("Tree down", Assert.Single(report.ProjectedEvents).Title);
            var attempt = Assert.Single(report.Attempts);
            Assert.Equal(IncidentBatchPrompt.PromptIdentity, attempt.PromptIdentity);
            Assert.Equal("test-config", attempt.ConfigurationIdentity);
            Assert.Equal("Tree down", Assert.Single(attempt.EventTitles));
            Assert.Empty(attempt.Relationships);
            Assert.DoesNotContain("Tree blocking roadway", attempt.EventTitles);

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE incident_batch_constructor_shadow_ledger SET run_id='changed' WHERE sequence=1;";
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
            command.CommandText = "DELETE FROM incident_batch_processed_calls WHERE run_id='run:1' AND call_id=1;";
            error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProvisionalIntakeAtomicallyPersistsVerificationRequestWithoutMergingMembership()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-batch-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "pizzad.db");
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = path, AudioRoot = root }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = new DateTimeOffset(2026, 7, 22, 3, 0, 0, TimeSpan.Zero);
            var priorObservation = new IncidentEventStateSourceObservation(
                "call:10", 10, now.AddMinutes(-1).ToUnixTimeSeconds(), string.Empty, null,
                [new IncidentEventStateTranscriptObservation("transcript:10", "White truck crashed on County Road 725.", "test", now)],
                new Dictionary<string, IncidentEventStateMetadataObservation>());
            var newObservation = new IncidentEventStateSourceObservation(
                "call:11", 11, now.ToUnixTimeSeconds(), string.Empty, null,
                [new IncidentEventStateTranscriptObservation("transcript:11", "Critical injuries in that white truck crash.", "test", now)],
                new Dictionary<string, IncidentEventStateMetadataObservation>());
            var bundle = new IncidentEventStateObservationBundle("bundle:async", now, [priorObservation, newObservation], []);
            var prior = new IncidentBatchProjection(
                "run:async",
                "projection:prior",
                now.AddMinutes(-1),
                ["ledger:async"],
                [new IncidentBatchProjectionEvent(
                    "projection:event:existing", ["call:10"], "Vehicle crash", "A vehicle crash is active.", true, false, ["ledger:async"])],
                []);
            var candidate = new IncidentBatchCandidate("candidate:1", "projection:event:existing", ["call:10"]);
            var proposal = new IncidentBatchProposal(
                "proposal:async", now, "test-model", IncidentBatchPrompt.PromptIdentity,
                [new IncidentBatchEventProposal(
                    "event:update", IncidentBatchEventDisposition.ConfirmedMembership, candidate.CandidateToken, ["call:11"],
                    "Critical-injury crash", "Critical injuries were reported.", "Both sides explicitly describe the same white-truck crash.", 0.1,
                    [new IncidentEventStateTranscriptCitation("transcript:11", "Critical injuries")],
                    [new IncidentEventStateTranscriptCitation("transcript:10", "White truck crashed")], [], [])]);
            var coordinator = new IncidentBatchCoordinator(
                new FixedProposer(proposal),
                new IncidentBatchProvisionalStore(database),
                new FixedTimeProvider(now));

            var result = await coordinator.RunAsync(
                new IncidentBatchRunRequest(
                    "run:async", "ledger:async", "projection:async", [new IncidentBatchSingletonIdentity("call:11", "projection:event:source")],
                    "test", $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchContract.PerCitationAcceptanceConfigurationToken};{IncidentBatchExecutionArchitecture.AsynchronousProvisionalToken}"),
                bundle, prior, ["call:11"], [candidate], CancellationToken.None);

            var requests = await database.ListIncidentBatchVerificationRequestsAsync("run:async", 10, CancellationToken.None);
            var request = Assert.Single(requests).Request;
            Assert.Equal(IncidentBatchEventDisposition.ConfirmedMembership, request.ProposedDisposition);
            Assert.Equal("event:update", request.SourceProposalToken);
            Assert.Equal(2, result.Projection.Projection.Events.Count);
            Assert.Single(result.Projection.Projection.ProvisionalAssociations);

            var confirmation = new IncidentBatchConfirmationProposal(
                "confirmation:async",
                now.AddSeconds(1),
                "test-verifier",
                IncidentBatchConfirmationPrompt.PromptIdentity,
                [new IncidentBatchConfirmationDecision(
                    "event:update",
                    "candidate:1",
                    IncidentBatchConfirmationDecisionKind.Verify,
                    "Both transcripts explicitly identify the same white-truck crash.",
                    [new IncidentEventStateTranscriptCitation("transcript:11", "white truck crash")],
                    [new IncidentEventStateTranscriptCitation("transcript:10", "White truck crashed")],
                    [],
                    [])]);
            var verificationResult = IncidentBatchVerificationQueueContract.BuildResult(
                result.LedgerEntry.Entry,
                request,
                confirmation,
                new IncidentBatchConfirmationExecutionContext(1200, string.Empty),
                now.AddSeconds(2));
            Assert.Equal(IncidentBatchVerificationOutcome.Verified, verificationResult.Outcome);
            var verifiedProjection = IncidentBatchVerificationProjector.Apply(
                result.Projection.Projection,
                result.LedgerEntry.Entry,
                request,
                verificationResult,
                "projection:verified",
                now.AddSeconds(2));
            await database.AppendIncidentBatchVerificationResultAsync(
                result.Projection.Sequence,
                result.LedgerEntry.Entry,
                request,
                verificationResult,
                verifiedProjection,
                CancellationToken.None);

            Assert.Empty(await database.ListPendingIncidentBatchVerificationRequestsAsync("run:async", 10, CancellationToken.None));
            Assert.Single(await database.ListIncidentBatchVerificationResultsAsync("run:async", 10, CancellationToken.None));
            var latestProjection = await database.GetLatestIncidentBatchProjectionAsync("run:async", CancellationToken.None);
            var merged = Assert.Single(latestProjection!.Projection.Events);
            Assert.Equal(["call:10", "call:11"], merged.ObservationIds);
            Assert.Empty(latestProjection.Projection.ProvisionalAssociations);
            var report = await database.GetIncidentBatchVerificationShadowReportAsync(true, "run:async", 10, CancellationToken.None);
            Assert.Equal(1, report.Totals.Enqueued);
            Assert.Equal(0, report.Totals.Pending);
            Assert.Equal(1, report.Totals.Verified);

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE incident_batch_verification_shadow_requests SET run_id='changed' WHERE sequence=1;";
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReplacementHealthUsesDurableProcessedCursorInsteadOfDisabledLegacyQueue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-batch-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = root
                }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var firstCallId = await AddCallAsync(database, "First eligible replacement call.", now.AddMinutes(-1));
            var secondCallId = await AddCallAsync(database, "Second eligible replacement call.", now.AddMinutes(-120));
            var first = await AppendSingletonAsync(
                database,
                "run:health",
                "health:ledger:1",
                "health:projection:1",
                firstCallId,
                now.AddMinutes(-1),
                null,
                0);

            var stale = await database.GetIncidentBatchPipelineHealthAsync(
                "run:health",
                0,
                60,
                CancellationToken.None);
            Assert.Equal("degraded", stale.Status);
            Assert.Equal(1, stale.PendingCalls);
            Assert.Equal(1, stale.StalePendingCalls);
            Assert.Contains("Replacement incident intake is stale", stale.Message, StringComparison.Ordinal);

            var staleWrite = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                AppendSingletonAsync(
                    database,
                    "run:health",
                    "health:ledger:stale",
                    "health:projection:stale",
                    secondCallId,
                    now.AddMinutes(-120),
                    first.Projection.Projection,
                    0));
            Assert.Contains("projection advanced", staleWrite.Message, StringComparison.Ordinal);
            Assert.Equal([firstCallId], await database.ListIncidentBatchProcessedCallIdsAsync(
                "run:health",
                CancellationToken.None));

            await AppendSingletonAsync(
                database,
                "run:health",
                "health:ledger:2",
                "health:projection:2",
                secondCallId,
                now.AddMinutes(-120),
                first.Projection.Projection,
                first.Projection.Sequence);
            var current = await database.GetIncidentBatchPipelineHealthAsync(
                "run:health",
                0,
                60,
                CancellationToken.None);
            Assert.Equal("ok", current.Status);
            Assert.Equal(0, current.PendingCalls);
            Assert.Contains("Replacement incident pipeline is current", current.Message, StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task<long> AddCallAsync(
        EngineDatabase database,
        string transcript,
        DateTimeOffset timestamp) =>
        await database.UpsertCallAsync(new EngineCall
        {
            UniqueKey = $"batch-health:{Guid.NewGuid():N}",
            StartTime = timestamp.ToUnixTimeSeconds(),
            StopTime = timestamp.AddSeconds(3).ToUnixTimeSeconds(),
            SystemShortName = "test-system",
            Talkgroup = 1,
            AudioPath = "test.wav",
            Transcription = transcript,
            TranscriptionStatus = "complete",
            QualityReason = "ok"
        }, CancellationToken.None);

    private static async Task<IncidentBatchRunResult> AppendSingletonAsync(
        EngineDatabase database,
        string runId,
        string ledgerEntryId,
        string projectionId,
        long callId,
        DateTimeOffset observedAt,
        IncidentBatchProjection? prior,
        long? baseProjectionSequence = null)
    {
        var observationId = $"call:{callId}";
        var transcriptId = $"transcript:{callId}";
        var observation = new IncidentEventStateSourceObservation(
            observationId,
            callId,
            observedAt.ToUnixTimeSeconds(),
            string.Empty,
            null,
            [new IncidentEventStateTranscriptObservation(
                transcriptId,
                $"Eligible transcript for call {callId}.",
                "test",
                observedAt)],
            new Dictionary<string, IncidentEventStateMetadataObservation>());
        var proposal = new IncidentBatchProposal(
            $"proposal:{callId}",
            observedAt,
            "test-model",
            IncidentBatchPrompt.PromptIdentity,
            [new IncidentBatchEventProposal(
                $"event:{callId}",
                IncidentBatchEventDisposition.NewEvent,
                string.Empty,
                [observationId],
                "Grounded event",
                $"Eligible transcript for call {callId}.",
                "The source transcript is retained for Review.",
                0.1,
                [new IncidentEventStateTranscriptCitation(transcriptId, "Eligible transcript")],
                [],
                [],
                [])]);
        var coordinator = new IncidentBatchCoordinator(
            new FixedProposer(proposal),
            database,
            new FixedTimeProvider(observedAt));
        return await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                runId,
                ledgerEntryId,
                projectionId,
                [new IncidentBatchSingletonIdentity(observationId, $"singleton:{callId}")],
                "test",
                "test-config",
                BaseProjectionSequence: baseProjectionSequence),
            new IncidentEventStateObservationBundle($"bundle:{callId}", observedAt, [observation], []),
            prior,
            [observationId],
            [],
            CancellationToken.None);
    }

    private sealed class FixedProposer(IncidentBatchProposal proposal) : IIncidentBatchProposer
    {
        public Task<IncidentBatchProposal> ProposeAsync(IncidentEventStateObservationBundle bundle, IReadOnlyList<string> newObservationIds, IReadOnlyList<IncidentBatchCandidate> candidates, CancellationToken ct) => Task.FromResult(proposal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
