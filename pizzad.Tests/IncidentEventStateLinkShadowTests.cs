using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IncidentEventStateLinkShadowTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-07-20T19:30:00Z");

    [Fact]
    public async Task ValidSourceCitedLinkAddsNewObservationToExistingShadowEvent()
    {
        var store = new RecordingStore();
        var coordinator = new IncidentEventStateLinkShadowCoordinator(
            new RecordingProposer(LinkProposal()),
            store,
            new FixedTimeProvider(FixedNow));

        var result = await coordinator.RunAsync(
            Request(),
            Bundle(),
            PriorProjection(),
            "observation-new",
            Candidates(),
            CancellationToken.None);

        Assert.Equal(IncidentEventStateLinkTransitionOutcome.LinkedToExistingEvent, result.LedgerEntry.Entry.Transition.Outcome);
        Assert.Empty(result.LedgerEntry.Entry.ProposalValidationErrors);
        var projectedEvent = Assert.Single(result.Projection.Projection.Events);
        Assert.Equal("event-existing", projectedEvent.ProjectionEventId);
        Assert.Equal(["observation-prior", "observation-new"], projectedEvent.ObservationIds);
        Assert.Equal(["ledger-link-1"], projectedEvent.SourceLedgerEntryIds);
        Assert.Same(store.Entry, result.LedgerEntry.Entry);
        Assert.Same(store.Projection, result.Projection.Projection);
    }

    [Fact]
    public async Task AbstentionLeavesNewObservationAsUnresolvedSingleton()
    {
        var proposal = LinkProposal() with
        {
            Decision = IncidentEventStateLinkDecision.Abstain,
            CandidateToken = string.Empty,
            RelationshipStatement = "The source does not establish whether the calls describe one event.",
            CandidateEvidence = [],
            UnresolvedQuestions = ["Does the second transmission continue the earlier dispatch?"]
        };
        var coordinator = new IncidentEventStateLinkShadowCoordinator(
            new RecordingProposer(proposal),
            new RecordingStore(),
            new FixedTimeProvider(FixedNow));

        var result = await coordinator.RunAsync(
            Request(),
            Bundle(),
            PriorProjection(),
            "observation-new",
            Candidates(),
            CancellationToken.None);

        Assert.Equal(IncidentEventStateLinkTransitionOutcome.UnresolvedSingleton, result.LedgerEntry.Entry.Transition.Outcome);
        Assert.Empty(result.LedgerEntry.Entry.ProposalValidationErrors);
        Assert.Equal(2, result.Projection.Projection.Events.Count);
        var singleton = Assert.Single(result.Projection.Projection.Events, item => item.ProjectionEventId == "event-singleton-new");
        Assert.Equal(["observation-new"], singleton.ObservationIds);
    }

    [Fact]
    public async Task InvalidQuoteCannotLinkAndIsRecordedAsUnresolved()
    {
        var invalid = LinkProposal() with
        {
            NewObservationEvidence = [new IncidentEventStateTranscriptCitation("transcript-new", "words not in the transcript")]
        };
        var coordinator = new IncidentEventStateLinkShadowCoordinator(
            new RecordingProposer(invalid),
            new RecordingStore(),
            new FixedTimeProvider(FixedNow));

        var result = await coordinator.RunAsync(
            Request(),
            Bundle(),
            PriorProjection(),
            "observation-new",
            Candidates(),
            CancellationToken.None);

        Assert.Equal(IncidentEventStateLinkTransitionOutcome.UnresolvedSingleton, result.LedgerEntry.Entry.Transition.Outcome);
        Assert.Contains(result.LedgerEntry.Entry.ProposalValidationErrors, error =>
            error.Contains("does not occur exactly", StringComparison.Ordinal));
        Assert.Contains(result.Projection.Projection.Events, item =>
            item.ProjectionEventId == "event-singleton-new" && item.ObservationIds.SequenceEqual(["observation-new"]));
    }

    [Fact]
    public async Task CandidateSetMustComeFromPriorProjectionAndCannotContainNewObservation()
    {
        var invalidCandidates =
        new[]
        {
            Candidates()[0] with { ObservationIds = ["observation-prior", "observation-new"] }
        };
        var proposer = new RecordingProposer(LinkProposal());
        var coordinator = new IncidentEventStateLinkShadowCoordinator(
            proposer,
            new RecordingStore());

        var error = await Assert.ThrowsAsync<ArgumentException>(() => coordinator.RunAsync(
            Request(),
            Bundle(),
            PriorProjection(),
            "observation-new",
            invalidCandidates,
            CancellationToken.None));

        Assert.Contains("already contains new observation", error.Message, StringComparison.Ordinal);
        Assert.False(proposer.WasCalled);
    }

    [Fact]
    public void PromptExposesOnlyLinkOrAbstainAndOpaqueCandidateTokens()
    {
        var prompt = IncidentEventStateLinkPrompt.Build(Bundle(), "observation-new", Candidates());
        var responseFormat = System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat);

        Assert.Contains("propose_link", responseFormat, StringComparison.Ordinal);
        Assert.Contains("abstain", responseFormat, StringComparison.Ordinal);
        Assert.DoesNotContain("distinct", responseFormat, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("candidate-1", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("event-existing", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("does not mean the observations describe different events", prompt.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoCandidatesCreatesSingletonWithoutCallingModel()
    {
        var proposer = new RecordingProposer(LinkProposal());
        var coordinator = new IncidentEventStateLinkShadowCoordinator(
            proposer,
            new RecordingStore(),
            new FixedTimeProvider(FixedNow));
        var bundle = Bundle() with { Observations = [Bundle().Observations[1]] };

        var result = await coordinator.RunAsync(
            Request(),
            bundle,
            null,
            "observation-new",
            [],
            CancellationToken.None);

        Assert.False(proposer.WasCalled);
        Assert.Equal("application", result.LedgerEntry.Entry.Proposal.ModelIdentity);
        Assert.Equal(0, result.LedgerEntry.Entry.Execution.ProposerDurationMilliseconds);
        Assert.Equal(IncidentEventStateLinkTransitionOutcome.UnresolvedSingleton, result.LedgerEntry.Entry.Transition.Outcome);
        Assert.Equal(["observation-new"], Assert.Single(result.Projection.Projection.Events).ObservationIds);
    }

    [Fact]
    public async Task LinkLedgerAndProjectionAreAppendOnlyAndDoNotTouchProductionIncidentTables()
    {
        await using var temp = await TestDatabase.CreateAsync();
        var coordinator = new IncidentEventStateLinkShadowCoordinator(
            new RecordingProposer(LinkProposal()),
            temp.Database,
            new FixedTimeProvider(FixedNow));

        var result = await coordinator.RunAsync(
            Request(),
            Bundle(),
            PriorProjection(),
            "observation-new",
            Candidates(),
            CancellationToken.None);

        Assert.Equal(1, result.LedgerEntry.Sequence);
        Assert.Equal(1, result.Projection.Sequence);
        var listed = Assert.Single(await temp.Database.ListIncidentEventStateLinkShadowLedgerEntriesAsync(
            0,
            10,
            CancellationToken.None));
        Assert.Equal(result.LedgerEntry.ContentHash, listed.ContentHash);
        var latest = await temp.Database.GetLatestIncidentEventStateLinkShadowProjectionAsync(CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal(result.Projection.ContentHash, latest.ContentHash);

        await using var connection = temp.Database.OpenConnection();
        Assert.Equal(0, await CountAsync(connection, "incidents"));
        Assert.Equal(0, await CountAsync(connection, "incident_calls"));
        Assert.Equal(0, await CountAsync(connection, "incident_operation_audit"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM incident_event_state_link_shadow_ledger WHERE ledger_entry_id='ledger-link-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE incident_event_state_link_shadow_projections SET projection_id='changed' WHERE projection_id='projection-link-1';"));
    }

    private static IncidentEventStateLinkShadowRunRequest Request() =>
        new(
            "ledger-link-1",
            "projection-link-1",
            "event-singleton-new",
            "software-v1",
            "configuration-v1");

    private static IncidentEventStateObservationBundle Bundle() =>
        new(
            "bundle-link-1",
            FixedNow.AddMinutes(-2),
            [
                Observation(
                    "observation-prior",
                    1001,
                    "transcript-prior",
                    "Unit 469, did you receive the message?"),
                Observation(
                    "observation-new",
                    1002,
                    "transcript-new",
                    "469, your radio is breaking up.")
            ],
            []);

    private static IncidentEventStateSourceObservation Observation(
        string observationId,
        long callId,
        string transcriptId,
        string text) =>
        new(
            observationId,
            callId,
            callId,
            $"audio/{callId}.wav",
            3_000,
            [new IncidentEventStateTranscriptObservation(transcriptId, text, "source", FixedNow.AddMinutes(-3))],
            new Dictionary<string, IncidentEventStateMetadataObservation>());

    private static IncidentEventStateLinkProjection PriorProjection() =>
        new(
            "projection-prior",
            FixedNow.AddMinutes(-1),
            [],
            [
                new IncidentEventStateLinkProjectionEvent(
                    "event-existing",
                    ["observation-prior"],
                    [])
            ]);

    private static IReadOnlyList<IncidentEventStateLinkCandidate> Candidates() =>
        [
            new IncidentEventStateLinkCandidate(
                "candidate-1",
                "event-existing",
                ["observation-prior"])
        ];

    private static IncidentEventStateLinkProposal LinkProposal() =>
        new(
            "proposal-link-1",
            FixedNow.AddSeconds(-10),
            "model-under-test",
            "link-only-v1",
            IncidentEventStateLinkDecision.ProposeLink,
            "candidate-1",
            "Both transmissions directly address unit 469 during one exchange.",
            0.1,
            [new IncidentEventStateTranscriptCitation("transcript-new", "469, your radio is breaking up")],
            [new IncidentEventStateTranscriptCitation("transcript-prior", "Unit 469")],
            []);

    private sealed class RecordingProposer(IncidentEventStateLinkProposal proposal) : IIncidentEventStateLinkProposer
    {
        public bool WasCalled { get; private set; }

        public Task<IncidentEventStateLinkProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            string newObservationId,
            IReadOnlyList<IncidentEventStateLinkCandidate> candidates,
            CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(proposal);
        }
    }

    private sealed class RecordingStore : IIncidentEventStateLinkShadowStore
    {
        public IncidentEventStateLinkLedgerEntry? Entry { get; private set; }
        public IncidentEventStateLinkProjection? Projection { get; private set; }

        public Task<IncidentEventStateLinkShadowRunResult> AppendIncidentEventStateLinkShadowRunAsync(
            IncidentEventStateLinkLedgerEntry entry,
            IncidentEventStateLinkProjection projection,
            CancellationToken ct)
        {
            Entry = entry;
            Projection = projection;
            return Task.FromResult(new IncidentEventStateLinkShadowRunResult(
                new IncidentEventStateStoredLinkLedgerEntry(1, new string('A', 64), entry),
                new IncidentEventStateStoredLinkProjection(1, new string('B', 64), projection)));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

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
            var root = Path.Combine(Path.GetTempPath(), $"pizzawave-link-shadow-{Guid.NewGuid():N}");
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
