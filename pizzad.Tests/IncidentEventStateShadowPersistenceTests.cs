using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IncidentEventStateShadowPersistenceTests
{
    [Fact]
    public async Task LedgerAndProjectionRoundTripWithoutTouchingProductionIncidentTables()
    {
        await using var temp = await TestDatabase.CreateAsync();
        var storedEntry = await temp.Database.AppendIncidentEventStateShadowLedgerEntryAsync(
            LedgerEntry("ledger-1"),
            CancellationToken.None);

        Assert.Equal(1, storedEntry.Sequence);
        Assert.Equal(64, storedEntry.ContentHash.Length);
        var listed = Assert.Single(await temp.Database.ListIncidentEventStateShadowLedgerEntriesAsync(
            0,
            10,
            CancellationToken.None));
        Assert.Equal(storedEntry.Sequence, listed.Sequence);
        Assert.Equal(storedEntry.ContentHash, listed.ContentHash);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(storedEntry.Entry),
            System.Text.Json.JsonSerializer.Serialize(listed.Entry));

        var projection = Projection("projection-1", "ledger-1");
        var storedProjection = await temp.Database.AppendIncidentEventStateShadowProjectionAsync(
            projection,
            CancellationToken.None);
        Assert.Equal(1, storedProjection.Sequence);
        var latestProjection = await temp.Database.GetLatestIncidentEventStateShadowProjectionAsync(
            CancellationToken.None);
        Assert.NotNull(latestProjection);
        Assert.Equal(storedProjection.Sequence, latestProjection.Sequence);
        Assert.Equal(storedProjection.ContentHash, latestProjection.ContentHash);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(storedProjection.Projection),
            System.Text.Json.JsonSerializer.Serialize(latestProjection.Projection));

        await using var connection = temp.Database.OpenConnection();
        Assert.Equal(0, await CountAsync(connection, "incidents"));
        Assert.Equal(0, await CountAsync(connection, "incident_calls"));
        Assert.Equal(0, await CountAsync(connection, "incident_operation_audit"));
    }

    [Fact]
    public async Task AppendRejectsUnknownSupersessionAndProjectionReferences()
    {
        await using var temp = await TestDatabase.CreateAsync();
        var unknownSupersession = LedgerEntry("ledger-2", ["ledger-missing"]);

        var ledgerError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            temp.Database.AppendIncidentEventStateShadowLedgerEntryAsync(
                unknownSupersession,
                CancellationToken.None));
        Assert.Contains("does not exist", ledgerError.Message, StringComparison.Ordinal);

        var projectionError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            temp.Database.AppendIncidentEventStateShadowProjectionAsync(
                Projection("projection-2", "ledger-missing"),
                CancellationToken.None));
        Assert.Contains("does not exist", projectionError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DatabaseTriggersRejectLedgerAndProjectionMutation()
    {
        await using var temp = await TestDatabase.CreateAsync();
        await temp.Database.AppendIncidentEventStateShadowLedgerEntryAsync(
            LedgerEntry("ledger-1"),
            CancellationToken.None);
        await temp.Database.AppendIncidentEventStateShadowProjectionAsync(
            Projection("projection-1", "ledger-1"),
            CancellationToken.None);

        await using var connection = temp.Database.OpenConnection();
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE incident_event_state_shadow_ledger SET bundle_id='changed' WHERE ledger_entry_id='ledger-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM incident_event_state_shadow_ledger WHERE ledger_entry_id='ledger-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE incident_event_state_shadow_projections SET projection_id='changed' WHERE projection_id='projection-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM incident_event_state_shadow_projections WHERE projection_id='projection-1';"));
    }

    [Fact]
    public async Task ReadRejectsPayloadWhoseContentHashNoLongerMatches()
    {
        await using var temp = await TestDatabase.CreateAsync();
        await temp.Database.AppendIncidentEventStateShadowLedgerEntryAsync(
            LedgerEntry("ledger-1"),
            CancellationToken.None);

        await using (var connection = temp.Database.OpenConnection())
        {
            await ExecuteAsync(connection, "DROP TRIGGER incident_event_state_shadow_ledger_no_update;");
            await ExecuteAsync(
                connection,
                "UPDATE incident_event_state_shadow_ledger SET payload_json='{}' WHERE ledger_entry_id='ledger-1';");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            temp.Database.ListIncidentEventStateShadowLedgerEntriesAsync(
                0,
                10,
                CancellationToken.None));
        Assert.Contains("content-integrity verification", error.Message, StringComparison.Ordinal);
    }

    private static IncidentEventStateLedgerEntry LedgerEntry(
        string ledgerEntryId,
        IReadOnlyList<string>? supersedes = null)
    {
        var supersessionIds = supersedes ?? [];
        return new IncidentEventStateLedgerEntry(
            ledgerEntryId,
            DateTimeOffset.Parse("2026-07-17T15:00:00Z"),
            Bundle(),
            Proposal(supersessionIds),
            Critique(),
            supersessionIds);
    }

    private static IncidentEventStateObservationBundle Bundle() =>
        new(
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T14:58:00Z"),
            [
                new IncidentEventStateSourceObservation(
                    "observation-1",
                    67113,
                    1784300280,
                    "audio/call-67113.wav",
                    10_000,
                    [
                        new IncidentEventStateTranscriptObservation(
                            "transcript-1",
                            "A caller described several loud sounds.",
                            "source-engine",
                            DateTimeOffset.Parse("2026-07-17T14:59:00Z"))
                    ],
                    new Dictionary<string, string>
                    {
                        ["source-field"] = "source value"
                    })
            ],
            []);

    private static IncidentEventStateProposal Proposal(IReadOnlyList<string> supersedes) =>
        new(
            "proposal-1",
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T15:00:00Z"),
            "proposer-model",
            "proposer-prompt",
            [
                new IncidentEventStateHypothesis(
                    "hypothesis-1",
                    "The source may describe a real-world event.",
                    0.4,
                    ["observation-1"],
                    [
                        new IncidentEventStateClaim(
                            "claim-1",
                            "A caller described several loud sounds.",
                            0.1,
                            [TranscriptProvenance()])
                    ],
                    [],
                    [],
                    ["The cause of the sounds is unresolved."])
            ],
            supersedes);

    private static IncidentEventStateCritique Critique() =>
        new(
            "critique-1",
            "proposal-1",
            DateTimeOffset.Parse("2026-07-17T15:01:00Z"),
            "critic-model",
            "critic-prompt",
            "The proposal retains uncertainty about the source.",
            [
                new IncidentEventStateCritiqueFinding(
                    "finding-1",
                    "The observation does not establish what produced the sounds.",
                    0.2,
                    [TranscriptProvenance()])
            ]);

    private static IncidentEventStateProjection Projection(string projectionId, string ledgerEntryId) =>
        new(
            projectionId,
            DateTimeOffset.Parse("2026-07-17T15:02:00Z"),
            [ledgerEntryId],
            [
                new IncidentEventStateProjectedEvent(
                    "projected-event-1",
                    "The current evidence describes several unexplained loud sounds.",
                    0.4,
                    ["observation-1"],
                    [
                        new IncidentEventStateClaim(
                            "claim-1",
                            "A caller described several loud sounds.",
                            0.1,
                            [TranscriptProvenance()])
                    ],
                    [],
                    ["The cause of the sounds is unresolved."],
                    [ledgerEntryId])
            ]);

    private static IncidentEventStateProvenance TranscriptProvenance() =>
        new(
            "observation-1",
            "transcript-1",
            "several loud sounds",
            null,
            null,
            string.Empty);

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _root;

        private TestDatabase(string root, EngineDatabase database)
        {
            _root = root;
            Database = database;
        }

        public EngineDatabase Database { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"pizzawave-event-state-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = new EngineDatabase(
                new EngineConfig
                {
                    Storage = new StorageConfig
                    {
                        DatabasePath = Path.Combine(root, "pizzad.db"),
                        AudioRoot = Path.Combine(root, "audio")
                    }
                },
                NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            return new TestDatabase(root, database);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
            return ValueTask.CompletedTask;
        }
    }
}
