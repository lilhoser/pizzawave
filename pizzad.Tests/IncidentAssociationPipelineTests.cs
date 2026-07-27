using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IncidentAssociationPipelineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-21T16:00:00Z");

    [Fact]
    public async Task ConfirmedMembershipAssignsObservationToExactlyOneExistingEvent()
    {
        var store = new MemoryStore();
        var coordinator = new IncidentAssociationShadowCoordinator(
            new FixedProposer(Relationship("candidate-1", IncidentAssociationDisposition.ConfirmedMembership, "call:new", "call:old")),
            store,
            new FixedTimeProvider(Now));
        var result = await coordinator.RunAsync(
            Request("confirmed"),
            Bundle("call:new", "call:old"),
            Prior(("event-old", new[] { "call:old" })),
            "call:new",
            [new IncidentAssociationCandidate("candidate-1", "event-old", ["call:old"])],
            CancellationToken.None);

        Assert.Equal(IncidentAssociationTransitionOutcome.ConfirmedMembership, result.LedgerEntry.Entry.Transition.Outcome);
        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.Equal(["call:old", "call:new"], projected.ObservationIds);
        Assert.Empty(result.Projection.Projection.ProvisionalAssociations);
    }

    [Fact]
    public async Task SeveralProvisionalAssociationsKeepSingletonAndNeverMergeEvents()
    {
        var relationships = new[]
        {
            Relationship("candidate-1", IncidentAssociationDisposition.ProvisionalAssociation, "call:new", "call:a"),
            Relationship("candidate-2", IncidentAssociationDisposition.ProvisionalAssociation, "call:new", "call:b")
        };
        var coordinator = new IncidentAssociationShadowCoordinator(new FixedProposer(relationships), new MemoryStore(), new FixedTimeProvider(Now));
        var result = await coordinator.RunAsync(
            Request("provisional"),
            Bundle("call:new", "call:a", "call:b"),
            Prior(("event-a", new[] { "call:a" }), ("event-b", new[] { "call:b" })),
            "call:new",
            [
                new IncidentAssociationCandidate("candidate-1", "event-a", ["call:a"]),
                new IncidentAssociationCandidate("candidate-2", "event-b", ["call:b"])
            ],
            CancellationToken.None);

        Assert.Equal(IncidentAssociationTransitionOutcome.SingletonCreated, result.LedgerEntry.Entry.Transition.Outcome);
        Assert.Equal(3, result.Projection.Projection.Events.Count);
        Assert.Equal("event:provisional", result.Projection.Projection.Events.Single(item => item.ObservationIds.Contains("call:new")).ProjectionEventId);
        Assert.Equal(2, result.Projection.Projection.ProvisionalAssociations.Count);
        Assert.All(result.Projection.Projection.ProvisionalAssociations, item => Assert.Equal("event:provisional", item.SourceProjectionEventId));
    }

    [Fact]
    public async Task ConfirmedAndProvisionalRelationshipsUseConfirmedEventAsProjectedSource()
    {
        var coordinator = new IncidentAssociationShadowCoordinator(
            new FixedProposer(
                Relationship("candidate-1", IncidentAssociationDisposition.ConfirmedMembership, "call:new", "call:a"),
                Relationship("candidate-2", IncidentAssociationDisposition.ProvisionalAssociation, "call:new", "call:b")),
            new MemoryStore(),
            new FixedTimeProvider(Now));
        var result = await coordinator.RunAsync(
            Request("mixed"),
            Bundle("call:new", "call:a", "call:b"),
            Prior(("event-a", new[] { "call:a" }), ("event-b", new[] { "call:b" })),
            "call:new",
            [
                new IncidentAssociationCandidate("candidate-1", "event-a", ["call:a"]),
                new IncidentAssociationCandidate("candidate-2", "event-b", ["call:b"])
            ],
            CancellationToken.None);

        var link = Assert.Single(result.Projection.Projection.ProvisionalAssociations);
        Assert.Equal("event-a", link.SourceProjectionEventId);
        Assert.Equal("event-b", link.CandidateProjectionEventId);
        Assert.Contains("call:new", result.Projection.Projection.Events.Single(item => item.ProjectionEventId == "event-a").ObservationIds);
    }

    [Fact]
    public async Task MultipleConfirmedRelationshipsFailClosedToSingletonAndDiscardAllAssociations()
    {
        var coordinator = new IncidentAssociationShadowCoordinator(
            new FixedProposer(
                Relationship("candidate-1", IncidentAssociationDisposition.ConfirmedMembership, "call:new", "call:a"),
                Relationship("candidate-2", IncidentAssociationDisposition.ConfirmedMembership, "call:new", "call:b")),
            new MemoryStore(),
            new FixedTimeProvider(Now));
        var result = await coordinator.RunAsync(
            Request("invalid"),
            Bundle("call:new", "call:a", "call:b"),
            Prior(("event-a", new[] { "call:a" }), ("event-b", new[] { "call:b" })),
            "call:new",
            [
                new IncidentAssociationCandidate("candidate-1", "event-a", ["call:a"]),
                new IncidentAssociationCandidate("candidate-2", "event-b", ["call:b"])
            ],
            CancellationToken.None);

        Assert.Equal(IncidentAssociationTransitionOutcome.SingletonCreated, result.LedgerEntry.Entry.Transition.Outcome);
        Assert.Contains(result.LedgerEntry.Entry.ProposalValidationErrors, error => error.Contains("at most one confirmed", StringComparison.Ordinal));
        Assert.Empty(result.Projection.Projection.ProvisionalAssociations);
    }

    [Fact]
    public async Task WrongSideCitationFailsClosedWithoutSemanticHeuristics()
    {
        var invalid = Relationship("candidate-1", IncidentAssociationDisposition.ConfirmedMembership, "call:new", "call:old") with
        {
            NewObservationEvidence = [Citation("call:old")]
        };
        var coordinator = new IncidentAssociationShadowCoordinator(new FixedProposer(invalid), new MemoryStore(), new FixedTimeProvider(Now));
        var result = await coordinator.RunAsync(
            Request("citation"),
            Bundle("call:new", "call:old"),
            Prior(("event-old", new[] { "call:old" })),
            "call:new",
            [new IncidentAssociationCandidate("candidate-1", "event-old", ["call:old"])],
            CancellationToken.None);

        Assert.Equal(IncidentAssociationTransitionOutcome.SingletonCreated, result.LedgerEntry.Entry.Transition.Outcome);
        Assert.NotEmpty(result.LedgerEntry.Entry.ProposalValidationErrors);
    }

    [Fact]
    public async Task NoCandidatesCreatesSingletonWithoutCallingModel()
    {
        var proposer = new CountingProposer();
        var coordinator = new IncidentAssociationShadowCoordinator(proposer, new MemoryStore(), new FixedTimeProvider(Now));
        var result = await coordinator.RunAsync(
            Request("none"),
            Bundle("call:new"),
            null,
            "call:new",
            [],
            CancellationToken.None);

        Assert.Equal(0, proposer.CallCount);
        Assert.Equal(IncidentAssociationTransitionOutcome.SingletonCreated, result.LedgerEntry.Entry.Transition.Outcome);
        Assert.Empty(result.LedgerEntry.Entry.Proposal.Relationships);
    }

    [Fact]
    public async Task DatabasePersistsConstructorAtomicallyAndKeepsBothArtifactsAppendOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-association-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var coordinator = new IncidentAssociationShadowCoordinator(new CountingProposer(), database, new FixedTimeProvider(Now));
            var result = await coordinator.RunAsync(Request("stored"), Bundle("call:new"), null, "call:new", [], CancellationToken.None);
            Assert.Equal(1, result.LedgerEntry.Sequence);
            Assert.Equal(1, result.Projection.Sequence);
            Assert.Single(await database.ListIncidentAssociationShadowLedgerEntriesAsync("run", 0, 10, CancellationToken.None));
            Assert.NotNull(await database.GetLatestIncidentAssociationShadowProjectionAsync("run", CancellationToken.None));
            var report = await database.GetIncidentAssociationShadowReportAsync(true, "run", null, 100, CancellationToken.None);
            Assert.Equal(1, report.Totals.Attempts);
            Assert.Equal(1, report.Totals.SingletonEvents);
            Assert.Equal(1, report.Totals.ProjectedEvents);
            Assert.Equal(0, report.Totals.ConfirmedMemberships);
            Assert.Equal(0, report.Totals.ProvisionalAssociations);

            await using var connection = database.OpenConnection();
            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "DELETE FROM incident_association_shadow_ledger;"));
            await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(connection, "UPDATE incident_association_shadow_projections SET projection_id='changed';"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PromptAndSelectionExposeNoSemanticLabelsOrRadioMetadata()
    {
        var oldCall = Call(1, "Older transcript", 1000) with { TalkgroupName = "Static label", Talkgroup = 999 };
        var newCall = Call(2, "New transcript", 1010) with { TalkgroupName = "Other label", Talkgroup = 123 };
        var prior = Prior(("event-old", new[] { "call:1" }));
        var selection = IncidentAssociationLiveSelection.Build(newCall, [oldCall, newCall], [], prior, 4, Now);
        Assert.All(selection.Bundle.Observations, observation => Assert.Empty(observation.Metadata));
        var prompt = IncidentAssociationPrompt.Build(selection.Bundle, selection.NewObservationId, selection.Candidates);
        Assert.DoesNotContain("Static label", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Other label", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("999", prompt.UserPrompt, StringComparison.Ordinal);
    }

    private static IncidentAssociationShadowRunRequest Request(string suffix) =>
        new("run", $"ledger:{suffix}", $"projection:{suffix}", $"event:{suffix}", "software", "configuration");

    private static IncidentAssociationProjection Prior(params (string EventId, string[] ObservationIds)[] events) =>
        new("run", "prior", Now.AddMinutes(-1), [], events.Select(item => new IncidentAssociationProjectionEvent(item.EventId, item.ObservationIds, [])).ToList(), []);

    private static IncidentEventStateObservationBundle Bundle(params string[] observationIds) =>
        new(
            $"bundle:{string.Join('-', observationIds)}",
            Now,
            observationIds.Select((id, index) => new IncidentEventStateSourceObservation(
                id,
                index + 1,
                1000 + index * 10,
                string.Empty,
                3000,
                [new IncidentEventStateTranscriptObservation($"transcript:{id}", $"Transcript for {id}", "source", Now)],
                new Dictionary<string, IncidentEventStateMetadataObservation>())).ToList(),
            []);

    private static IncidentAssociationRelationship Relationship(string token, IncidentAssociationDisposition disposition, string newObservationId, string candidateObservationId) =>
        new(
            token,
            disposition,
            "The transmissions contain a source-grounded relationship.",
            disposition == IncidentAssociationDisposition.ConfirmedMembership ? 0.1 : 0.45,
            [Citation(newObservationId)],
            [Citation(candidateObservationId)],
            ["The similarity could be coincidental."],
            ["Is there additional traffic?"]);

    private static IncidentEventStateTranscriptCitation Citation(string observationId) =>
        new($"transcript:{observationId}", $"Transcript for {observationId}");

    private static EngineCall Call(long id, string transcript, long start) => new()
    {
        Id = id,
        StartTime = start,
        StopTime = start + 3,
        SystemShortName = "system-a",
        Talkgroup = 42,
        TalkgroupName = "Dispatch",
        Transcription = transcript,
        QualityReason = "ok"
    };

    private sealed class FixedProposer(params IncidentAssociationRelationship[] relationships) : IIncidentAssociationProposer
    {
        public Task<IncidentAssociationProposal> ProposeAsync(IncidentEventStateObservationBundle bundle, string newObservationId, IReadOnlyList<IncidentAssociationCandidate> candidates, CancellationToken ct) =>
            Task.FromResult(new IncidentAssociationProposal("proposal", Now, "model", IncidentAssociationPrompt.PromptIdentity, relationships));
    }

    private sealed class CountingProposer : IIncidentAssociationProposer
    {
        public int CallCount { get; private set; }
        public Task<IncidentAssociationProposal> ProposeAsync(IncidentEventStateObservationBundle bundle, string newObservationId, IReadOnlyList<IncidentAssociationCandidate> candidates, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(new IncidentAssociationProposal("proposal", Now, "model", IncidentAssociationPrompt.PromptIdentity, []));
        }
    }

    private sealed class MemoryStore : IIncidentAssociationShadowStore
    {
        public Task<IncidentAssociationShadowRunResult> AppendIncidentAssociationShadowRunAsync(IncidentAssociationLedgerEntry entry, IncidentAssociationProjection projection, CancellationToken ct) =>
            Task.FromResult(new IncidentAssociationShadowRunResult(new IncidentAssociationStoredLedgerEntry(1, "entry", entry), new IncidentAssociationStoredProjection(1, "projection", projection)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
