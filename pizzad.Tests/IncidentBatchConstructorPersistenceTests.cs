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
            Assert.Equal(result.LedgerEntry.ContentHash, loadedEntry?.ContentHash);
            Assert.Equal(result.Projection.ContentHash, loadedProjection?.ContentHash);
            Assert.True(Assert.Single(loadedProjection!.Projection.Events).OperatorVisible);
            var report = await database.GetIncidentBatchShadowReportAsync(true, "run:1", null, 100, CancellationToken.None);
            Assert.True(report.Enabled);
            Assert.Equal(1, report.Totals.Batches);
            Assert.Equal(1, report.Totals.NewObservations);
            Assert.Equal(1, report.Totals.NewEvents);
            Assert.Equal(0, report.Totals.UnresolvedObservations);
            Assert.Equal("Tree blocking roadway", Assert.Single(report.ProjectedEvents).Title);

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE incident_batch_constructor_shadow_ledger SET run_id='changed' WHERE sequence=1;";
            var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
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
