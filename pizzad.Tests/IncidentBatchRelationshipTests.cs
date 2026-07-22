namespace pizzad.Tests;

public sealed class IncidentBatchRelationshipTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ConstructionPromptOmitsAllCandidateStateAndRelationshipChoices()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "A vehicle is on fire beside the roadway."));

        var prompt = IncidentBatchPrompt.Build(bundle, ["call:1"], []);

        Assert.Contains("source-isolated construction stage", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("candidate_events", prompt.UserPrompt, StringComparison.Ordinal);
        var schema = System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions());
        Assert.DoesNotContain("confirmed_membership", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("provisional_association", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyBatchLedgerPayloadWithoutRelationshipFieldsRemainsReadable()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "Routine radio traffic."));
        var entry = new IncidentBatchLedgerEntry(
            "run:legacy",
            "ledger:legacy",
            Now,
            bundle,
            ["call:1"],
            [new IncidentBatchSingletonIdentity("call:1", "projection:call:1")],
            [],
            new IncidentBatchProposal("proposal:legacy", Now, "test-model", "legacy-prompt", []),
            [],
            new IncidentBatchExecutionContext(
                "test",
                $"legacy;{IncidentBatchContract.PerEventAcceptanceConfigurationToken}",
                10,
                string.Empty));
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(entry, EngineConfig.JsonOptions()))!.AsObject();
        json.Remove("relationshipProposal");
        json.Remove("relationshipProposalValidationErrors");
        json.Remove("relationshipExecution");

        var restored = System.Text.Json.JsonSerializer.Deserialize<IncidentBatchLedgerEntry>(json.ToJsonString(), EngineConfig.JsonOptions());

        Assert.NotNull(restored);
        Assert.Null(restored.RelationshipProposal);
        var validation = IncidentBatchContract.ValidateLedgerEntry(restored);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void PromptKeepsConstructionAndCandidateSourcesExplicitlySeparated()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "The patient fell out of his chair and hurt his tailbone."),
            Observation("call:2", "transcript:2", "The earlier patient hit his head and has Parkinson's."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:new-fall", ["call:1"]) };
        var candidates = new[] { new IncidentBatchCandidate("candidate:prior-fall", "projection:prior-fall", ["call:2"]) };

        var prompt = IncidentBatchRelationshipPrompt.Build(bundle, sources, candidates);

        Assert.Contains("constructed groups are immutable", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Never borrow a candidate fact", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("event:new-fall", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("candidate:prior-fall", prompt.UserPrompt, StringComparison.Ordinal);
        var schema = System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions());
        Assert.Contains("source_proposal_token", schema, StringComparison.Ordinal);
        Assert.Contains("confirmed_membership", schema, StringComparison.Ordinal);
        Assert.Contains("provisional_association", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalCanRetainSeveralProvisionalAssociationsForOneConstructedGroup()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Did the worker notify her supervisor?"),
            Observation("call:2", "transcript:2", "A cleaning worker is locked inside the room."),
            Observation("call:3", "transcript:3", "The supervisor is responding to the building."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:worker-question", ["call:1"]) };
        var candidates = new[]
        {
            new IncidentBatchCandidate("candidate:locked-worker", "projection:locked-worker", ["call:2"]),
            new IncidentBatchCandidate("candidate:supervisor", "projection:supervisor", ["call:3"])
        };
        var proposal = Proposal(
            Relationship("event:worker-question", "candidate:locked-worker", "transcript:1", "worker notify her supervisor", "transcript:2", "cleaning worker"),
            Relationship("event:worker-question", "candidate:supervisor", "transcript:1", "supervisor", "transcript:3", "supervisor is responding"));

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, proposal);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void CandidateFactCannotValidateAsConstructedGroupEvidence()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "The patient fell out of his chair and hurt his tailbone."),
            Observation("call:2", "transcript:2", "The patient hit his head."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:new-fall", ["call:1"]) };
        var candidates = new[] { new IncidentBatchCandidate("candidate:prior-fall", "projection:prior-fall", ["call:2"]) };
        var relationship = Relationship(
            "event:new-fall",
            "candidate:prior-fall",
            "transcript:2",
            "hit his head",
            "transcript:2",
            "hit his head");

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, Proposal(relationship));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("outside its source boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstructedGroupCannotConfirmMembershipInTwoCandidates()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "The same truck crash has critical injuries."),
            Observation("call:2", "transcript:2", "A truck crashed on County Road 725."),
            Observation("call:3", "transcript:3", "A truck crashed on Salem Road."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:update", ["call:1"]) };
        var candidates = new[]
        {
            new IncidentBatchCandidate("candidate:one", "projection:one", ["call:2"]),
            new IncidentBatchCandidate("candidate:two", "projection:two", ["call:3"])
        };
        var first = Relationship("event:update", "candidate:one", "transcript:1", "same truck crash", "transcript:2", "truck crashed") with { Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership };
        var second = Relationship("event:update", "candidate:two", "transcript:1", "same truck crash", "transcript:3", "truck crashed") with { Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership };

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, Proposal(first, second));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("more than one confirmed membership", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CoordinatorKeepsCandidatesOutOfConstructionThenAppliesConfirmedRelationship()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "White truck crashed on County Road 725."),
            Observation("call:2", "transcript:2", "Critical injuries in that same white truck crash."));
        var construction = new IncidentBatchEventProposal(
            "event:update",
            IncidentBatchEventDisposition.ProvisionalEvent,
            string.Empty,
            ["call:2"],
            "model title",
            "model summary",
            "The cited source describes an operator-relevant update.",
            0.2,
            [new IncidentEventStateTranscriptCitation("transcript:2", "Critical injuries")],
            [],
            [],
            []);
        var constructor = new CapturingConstructorProposer(new IncidentBatchProposal(
            "proposal:construction", Now, "test-model", IncidentBatchPrompt.PromptIdentity, [construction]));
        var confirmed = Relationship(
            "event:update",
            "candidate:crash",
            "transcript:2",
            "same white truck crash",
            "transcript:1",
            "White truck crashed") with { Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership };
        var relationship = new CapturingRelationshipProposer(Proposal(confirmed));
        var coordinator = new IncidentBatchCoordinator(constructor, relationship, new MemoryStore(), new FixedTimeProvider(Now));
        var prior = new IncidentBatchProjection(
            "run:two-pass",
            "projection:prior",
            Now.AddMinutes(-1),
            ["ledger:prior"],
            [new IncidentBatchProjectionEvent("projection:crash", ["call:1"], "Existing crash", "White truck crash.", false, true, ["ledger:prior"])],
            []);

        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                "run:two-pass",
                "ledger:new",
                "projection:new",
                [new IncidentBatchSingletonIdentity("call:2", "projection:update")],
                "test",
                $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken}"),
            bundle,
            prior,
            ["call:2"],
            [new IncidentBatchCandidate("candidate:crash", "projection:crash", ["call:1"])],
            CancellationToken.None);

        Assert.Empty(constructor.ReceivedCandidates);
        Assert.Equal("event:update", Assert.Single(relationship.ReceivedSources).SourceProposalToken);
        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.Equal("projection:crash", projected.ProjectionEventId);
        Assert.Equal(["call:1", "call:2"], projected.ObservationIds);
        Assert.True(projected.OperatorVisible);
        Assert.False(projected.OperatorReview);
        Assert.NotNull(result.LedgerEntry.Entry.RelationshipExecution);
    }

    [Fact]
    public async Task CoordinatorProjectsSeveralProvisionalAssociationsWithoutMerging()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "A cleaning worker is locked inside the room."),
            Observation("call:2", "transcript:2", "The supervisor is responding to the building."),
            Observation("call:3", "transcript:3", "Did the worker notify her supervisor?"));
        var construction = new IncidentBatchEventProposal(
            "event:question", IncidentBatchEventDisposition.ProvisionalEvent, string.Empty, ["call:3"],
            "model title", "model summary", "The cited source describes a follow-up question.", 0.5,
            [new IncidentEventStateTranscriptCitation("transcript:3", "worker notify her supervisor")], [], [], []);
        var constructor = new CapturingConstructorProposer(new IncidentBatchProposal(
            "proposal:construction", Now, "test-model", IncidentBatchPrompt.PromptIdentity, [construction]));
        var relationships = new CapturingRelationshipProposer(Proposal(
            Relationship("event:question", "candidate:worker", "transcript:3", "worker notify her supervisor", "transcript:1", "cleaning worker"),
            Relationship("event:question", "candidate:supervisor", "transcript:3", "supervisor", "transcript:2", "supervisor is responding")));
        var coordinator = new IncidentBatchCoordinator(constructor, relationships, new MemoryStore(), new FixedTimeProvider(Now));
        var prior = new IncidentBatchProjection(
            "run:links", "projection:prior", Now.AddMinutes(-1), ["ledger:prior"],
            [
                new IncidentBatchProjectionEvent("projection:worker", ["call:1"], "Worker", "Worker", false, true, ["ledger:prior"]),
                new IncidentBatchProjectionEvent("projection:supervisor", ["call:2"], "Supervisor", "Supervisor", false, true, ["ledger:prior"])
            ],
            []);

        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                "run:links", "ledger:new", "projection:new",
                [new IncidentBatchSingletonIdentity("call:3", "projection:question")],
                "test", $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken}"),
            bundle,
            prior,
            ["call:3"],
            [
                new IncidentBatchCandidate("candidate:worker", "projection:worker", ["call:1"]),
                new IncidentBatchCandidate("candidate:supervisor", "projection:supervisor", ["call:2"])
            ],
            CancellationToken.None);

        Assert.Equal(3, result.Projection.Projection.Events.Count);
        Assert.Equal(2, result.Projection.Projection.ProvisionalAssociations.Count);
        Assert.All(result.Projection.Projection.ProvisionalAssociations, link => Assert.Equal("projection:question", link.SourceProjectionEventId));
    }

    private static IncidentBatchRelationship Relationship(
        string source,
        string candidate,
        string sourceTranscript,
        string sourceQuote,
        string candidateTranscript,
        string candidateQuote) =>
        new(
            source,
            candidate,
            IncidentBatchRelationshipDisposition.ProvisionalAssociation,
            "The two source boundaries may describe related operational activity.",
            0.4,
            [new IncidentEventStateTranscriptCitation(sourceTranscript, sourceQuote)],
            [new IncidentEventStateTranscriptCitation(candidateTranscript, candidateQuote)],
            [],
            []);

    private static IncidentBatchRelationshipProposal Proposal(params IncidentBatchRelationship[] relationships) =>
        new("proposal:relationships", Now, "test-model", IncidentBatchRelationshipPrompt.PromptIdentity, relationships);

    private static IncidentEventStateObservationBundle Bundle(params IncidentEventStateSourceObservation[] observations) =>
        new("bundle:relationship", Now, observations, []);

    private static IncidentEventStateSourceObservation Observation(string observationId, string transcriptId, string text) =>
        new(observationId, long.Parse(observationId.AsSpan("call:".Length)), 1000, string.Empty, null,
            [new IncidentEventStateTranscriptObservation(transcriptId, text, "test", Now)],
            new Dictionary<string, IncidentEventStateMetadataObservation>());

    private sealed class CapturingConstructorProposer(IncidentBatchProposal proposal) : IIncidentBatchProposer
    {
        public IReadOnlyList<IncidentBatchCandidate> ReceivedCandidates { get; private set; } = [];

        public Task<IncidentBatchProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<string> newObservationIds,
            IReadOnlyList<IncidentBatchCandidate> candidates,
            CancellationToken ct)
        {
            ReceivedCandidates = candidates;
            return Task.FromResult(proposal);
        }
    }

    private sealed class CapturingRelationshipProposer(IncidentBatchRelationshipProposal proposal) : IIncidentBatchRelationshipProposer
    {
        public IReadOnlyList<IncidentBatchRelationshipSource> ReceivedSources { get; private set; } = [];

        public Task<IncidentBatchRelationshipProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<IncidentBatchRelationshipSource> sources,
            IReadOnlyList<IncidentBatchCandidate> candidates,
            CancellationToken ct)
        {
            ReceivedSources = sources;
            return Task.FromResult(proposal);
        }
    }

    private sealed class MemoryStore : IIncidentBatchStore
    {
        public Task<IncidentBatchRunResult> AppendIncidentBatchRunAsync(
            IncidentBatchLedgerEntry entry,
            IncidentBatchProjection projection,
            CancellationToken ct) =>
            Task.FromResult(new IncidentBatchRunResult(
                new IncidentBatchStoredLedgerEntry(1, "entry-hash", entry),
                new IncidentBatchStoredProjection(1, "projection-hash", projection)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
