using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IncidentAssociationReviewTests
{
    private static readonly DateTimeOffset FixedNow = DateTimeOffset.Parse("2026-07-21T14:00:00Z");

    [Fact]
    public async Task MultiCallProposalIsPlacedInlineAndReviewCannotMutateProductionMembership()
    {
        await using var temp = await TestDatabase.CreateAsync();
        var first = await temp.AddCallAsync("Unit 469, did you receive the message?", 1_000);
        var second = await temp.AddCallAsync("469, your radio is breaking up.", 1_010);
        var third = await temp.AddCallAsync("469, move to the alternate channel.", 1_020);
        var establishedCompanion = await temp.AddCallAsync("Existing incident companion call.", 990);
        var incidentId = await temp.Database.AddIncidentAsync(new IncidentDto
        {
            Title = "Established radio incident",
            Status = "active",
            FirstSeen = 990,
            LastSeen = 1_000,
            Confidence = 0.9,
            Calls = [IncidentCall(first, 1_000), IncidentCall(establishedCompanion, 990)]
        }, CancellationToken.None);
        Assert.True(incidentId > 0);

        var prior = new IncidentAssociationProjection(
            "run-review",
            "projection-prior",
            FixedNow.AddMinutes(-3),
            [],
            [new IncidentAssociationProjectionEvent("event-radio", [$"call:{first}"], [])],
            []);
        var coordinator = new IncidentAssociationShadowCoordinator(
            new ProvisionalSourceCitedProposer(),
            temp.Database,
            new FixedTimeProvider(FixedNow));

        var firstLink = await coordinator.RunAsync(
            Request("ledger-1", "projection-1", "singleton-2"),
            Bundle(first, second),
            prior,
            $"call:{second}",
            [new IncidentAssociationCandidate("candidate-1", "event-radio", [$"call:{first}"])],
            CancellationToken.None);
        var secondLink = await coordinator.RunAsync(
            Request("ledger-2", "projection-2", "singleton-3"),
            Bundle(first, second, third),
            firstLink.Projection.Projection,
            $"call:{third}",
            [new IncidentAssociationCandidate("candidate-1", "event-radio", [$"call:{first}"])],
            CancellationToken.None);

        var report = await temp.Database.GetIncidentAssociationReviewReportAsync(
            true,
            "run-review",
            900,
            1_100,
            CancellationToken.None);
        var group = Assert.Single(report.Groups);
        Assert.Equal("inlineIncident", group.Placement);
        Assert.Equal([incidentId], group.AnchorIncidentIds);
        Assert.Equal(3, group.Calls.Count);
        Assert.Equal("established", Assert.Single(group.Calls, call => call.CallId == first).ReviewState);
        Assert.All(group.Calls.Where(call => call.CallId != first), call => Assert.Equal("pending", call.ReviewState));
        Assert.Equal(2, group.PendingCallCount);
        Assert.Equal(2, group.Evidence.Count);

        var review = new IncidentAssociationReviewLedgerEntry(
            "review-1",
            FixedNow.AddMinutes(1),
            group.ProposalKey,
            group.RunId,
            group.ProjectionEventId,
            IncidentAssociationReviewAction.ConfirmMembership,
            incidentId,
            [second, third],
            "operator",
            string.Empty);
        var stored = await temp.Database.AppendIncidentAssociationReviewAsync(review, CancellationToken.None);
        Assert.Equal(1, stored.Sequence);

        var after = await temp.Database.GetIncidentAssociationReviewReportAsync(
            true,
            "run-review",
            900,
            1_100,
            CancellationToken.None);
        var reviewedGroup = Assert.Single(after.Groups);
        Assert.Equal("confirmed", reviewedGroup.Status);
        Assert.Equal(0, reviewedGroup.PendingCallCount);
        Assert.Equal(0, after.PendingGroupCount);
        Assert.All(reviewedGroup.Calls.Where(call => call.CallId != first), call => Assert.Equal("confirmed", call.ReviewState));

        var productionIncident = Assert.Single(await temp.Database.ListIncidentsAsync(900, 1_100, CancellationToken.None));
        Assert.Equal(incidentId, productionIncident.Id);
        Assert.Equal(new[] { establishedCompanion, first }.Order().ToArray(), productionIncident.Calls.Select(call => call.CallId).Order().ToArray());
        Assert.DoesNotContain(productionIncident.Calls, call => call.CallId == second || call.CallId == third);

        await using var connection = temp.Database.OpenConnection();
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "UPDATE incident_association_review_ledger SET action='changed' WHERE review_entry_id='review-1';"));
        await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(
            connection,
            "DELETE FROM incident_association_review_ledger WHERE review_entry_id='review-1';"));
    }

    [Fact]
    public async Task UnanchoredProposalIsPlacedInReviewAndRejectingOneCallLeavesOthersPending()
    {
        await using var temp = await TestDatabase.CreateAsync();
        var first = await temp.AddCallAsync("Worker is locked in the room.", 2_000);
        var second = await temp.AddCallAsync("Did the worker notify her supervisor?", 2_010);
        await AddSingleLinkAsync(temp.Database, "run-standalone", first, second, "event-worker");

        var initial = await temp.Database.GetIncidentAssociationReviewReportAsync(
            true,
            "run-standalone",
            1_900,
            2_100,
            CancellationToken.None);
        var group = Assert.Single(initial.Groups);
        Assert.Equal("review", group.Placement);
        Assert.Equal(1, initial.StandalonePendingGroupCount);

        await temp.Database.AppendIncidentAssociationReviewAsync(new IncidentAssociationReviewLedgerEntry(
            "review-reject",
            FixedNow,
            group.ProposalKey,
            group.RunId,
            group.ProjectionEventId,
            IncidentAssociationReviewAction.RejectMembership,
            0,
            [second],
            "operator",
            string.Empty), CancellationToken.None);

        var after = await temp.Database.GetIncidentAssociationReviewReportAsync(
            true,
            "run-standalone",
            1_900,
            2_100,
            CancellationToken.None);
        var reviewed = Assert.Single(after.Groups);
        Assert.Equal("partiallyReviewed", reviewed.Status);
        Assert.Equal("rejected", Assert.Single(reviewed.Calls, call => call.CallId == second).ReviewState);
        Assert.Equal("pending", Assert.Single(reviewed.Calls, call => call.CallId == first).ReviewState);
    }

    private static async Task AddSingleLinkAsync(EngineDatabase database, string runId, long priorCallId, long newCallId, string eventId)
    {
        var coordinator = new IncidentAssociationShadowCoordinator(
            new ProvisionalSourceCitedProposer(),
            database,
            new FixedTimeProvider(FixedNow));
        await coordinator.RunAsync(
            new IncidentAssociationShadowRunRequest(runId, $"{runId}:ledger", $"{runId}:projection", $"{runId}:singleton", "software", "configuration"),
            Bundle(priorCallId, newCallId),
            new IncidentAssociationProjection(runId, $"{runId}:prior", FixedNow.AddMinutes(-1), [], [new IncidentAssociationProjectionEvent(eventId, [$"call:{priorCallId}"], [])], []),
            $"call:{newCallId}",
            [new IncidentAssociationCandidate("candidate-1", eventId, [$"call:{priorCallId}"])],
            CancellationToken.None);
    }

    private static IncidentAssociationShadowRunRequest Request(string ledgerId, string projectionId, string singletonId) =>
        new("run-review", ledgerId, projectionId, singletonId, "software", "configuration");

    private static IncidentEventStateObservationBundle Bundle(params long[] callIds) =>
        new(
            $"bundle:{string.Join('-', callIds)}",
            FixedNow,
            callIds.Select(callId => new IncidentEventStateSourceObservation(
                $"call:{callId}",
                callId,
                callId == callIds[0] ? 1_000 : 1_000 + Array.IndexOf(callIds, callId) * 10,
                $"audio/{callId}.wav",
                3_000,
                [new IncidentEventStateTranscriptObservation($"transcript:{callId}", $"Transcript for call {callId}", "source", FixedNow)],
                new Dictionary<string, IncidentEventStateMetadataObservation>())).ToList(),
            []);

    private static IncidentCallDto IncidentCall(long callId, long timestamp) =>
        new(callId, timestamp, string.Empty, string.Empty);

    private sealed class ProvisionalSourceCitedProposer : IIncidentAssociationProposer
    {
        public Task<IncidentAssociationProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            string newObservationId,
            IReadOnlyList<IncidentAssociationCandidate> candidates,
            CancellationToken ct)
        {
            var selected = candidates[0];
            var newTranscript = bundle.Observations.Single(observation => observation.ObservationId == newObservationId).Transcripts[0];
            var candidateTranscript = bundle.Observations.Single(observation => observation.ObservationId == selected.ObservationIds[0]).Transcripts[0];
            return Task.FromResult(new IncidentAssociationProposal(
                $"proposal:{newObservationId}",
                FixedNow,
                "model",
                "prompt",
                [new IncidentAssociationRelationship(
                    selected.CandidateToken,
                    IncidentAssociationDisposition.ProvisionalAssociation,
                    "The transmissions may continue the same event.",
                    0.35,
                    [new IncidentEventStateTranscriptCitation(newTranscript.TranscriptId, newTranscript.Text)],
                    [new IncidentEventStateTranscriptCitation(candidateTranscript.TranscriptId, candidateTranscript.Text)],
                    ["The similarity could be coincidental."],
                    ["Is there additional corroborating traffic?"])]));
        }
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
            var root = Path.Combine(Path.GetTempPath(), $"pizzawave-association-review-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio")
                }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            return new TestDatabase(root, database);
        }

        public async Task<long> AddCallAsync(string transcript, long timestamp) =>
            await Database.UpsertCallAsync(new EngineCall
            {
                UniqueKey = $"call-{timestamp}",
                StartTime = timestamp,
                StopTime = timestamp + 3,
                SystemShortName = "system-a",
                Talkgroup = 42,
                TalkgroupName = "Dispatch",
                AudioPath = $"{timestamp}.wav",
                Transcription = transcript,
                TranscriptionStatus = "complete",
                QualityReason = "ok"
            }, CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
            return ValueTask.CompletedTask;
        }
    }
}
