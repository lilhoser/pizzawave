namespace pizzad.Tests;

public sealed class IncidentBatchConstructorPipelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OmittedObservationsRemainInvisibleUnresolvedSingletons()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "Routine traffic."), Observation("call:2", "transcript:2", "Nothing further."));
        var result = await RunAsync(bundle, ["call:1", "call:2"], [], new FixedProposer(Proposal([])));

        Assert.Empty(result.LedgerEntry.Entry.ProposalValidationErrors);
        Assert.Equal(2, result.Projection.Projection.Events.Count);
        Assert.All(result.Projection.Projection.Events, item => Assert.False(item.OperatorVisible));
        Assert.Empty(result.Projection.Projection.ProvisionalAssociations);
    }

    [Fact]
    public async Task SourceCitedNewEventGroupsObservationsAndBecomesVisible()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Tree down across the southbound lane."),
            Observation("call:2", "transcript:2", "The same tree is blocking both lanes."));
        var item = Event(
            "event:tree",
            IncidentBatchEventDisposition.NewEvent,
            string.Empty,
            ["call:1", "call:2"],
            "Tree blocking roadway",
            "A fallen tree is blocking the roadway.",
            [Citation("transcript:1", "Tree down"), Citation("transcript:2", "same tree")],
            []);
        var result = await RunAsync(bundle, ["call:1", "call:2"], [], new FixedProposer(Proposal([item])));

        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.True(projected.OperatorVisible);
        Assert.Equal(["call:1", "call:2"], projected.ObservationIds);
        Assert.Equal("Tree blocking roadway", projected.Title);
    }

    [Fact]
    public async Task ConfirmedMembershipAddsNewObservationsToExistingEvent()
    {
        var prior = PriorProjection(new IncidentBatchProjectionEvent(
            "projection:event:existing",
            ["call:10"],
            "Vehicle crash",
            "A vehicle crash is active.",
            true,
            ["ledger:prior"]));
        var bundle = Bundle(
            Observation("call:10", "transcript:10", "White truck crashed on County Road 725."),
            Observation("call:11", "transcript:11", "Critical injuries in that white truck crash."));
        var candidate = new IncidentBatchCandidate("candidate:1", "projection:event:existing", ["call:10"]);
        var item = Event(
            "event:crash-update",
            IncidentBatchEventDisposition.ConfirmedMembership,
            candidate.CandidateToken,
            ["call:11"],
            "Critical-injury crash on County Road 725",
            "The white-truck crash now includes reported critical injuries.",
            [Citation("transcript:11", "Critical injuries")],
            [Citation("transcript:10", "White truck crashed")]);
        var result = await RunAsync(bundle, ["call:11"], [candidate], new FixedProposer(Proposal([item])), prior);

        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.Equal(["call:10", "call:11"], projected.ObservationIds);
        Assert.True(projected.OperatorVisible);
        Assert.Equal("Critical-injury crash on County Road 725", projected.Title);
    }

    [Fact]
    public async Task ProvisionalAssociationDoesNotMutateCandidateMembership()
    {
        var prior = PriorProjection(new IncidentBatchProjectionEvent(
            "projection:event:existing",
            ["call:10"],
            "Worker locked in room",
            "A cleaning worker was locked in a room.",
            true,
            ["ledger:prior"]));
        var bundle = Bundle(
            Observation("call:10", "transcript:10", "Cleaning worker is locked inside the room."),
            Observation("call:11", "transcript:11", "Did the worker notify her supervisor?"));
        var candidate = new IncidentBatchCandidate("candidate:1", "projection:event:existing", ["call:10"]);
        var item = Event(
            "event:worker-question",
            IncidentBatchEventDisposition.ProvisionalAssociation,
            candidate.CandidateToken,
            ["call:11"],
            "Worker supervisor question",
            "A responder asked whether a worker contacted her supervisor.",
            [Citation("transcript:11", "worker notify her supervisor")],
            [Citation("transcript:10", "Cleaning worker")]);
        var result = await RunAsync(bundle, ["call:11"], [candidate], new FixedProposer(Proposal([item])), prior);

        var projection = result.Projection.Projection;
        var existing = projection.Events.Single(row => row.ProjectionEventId == "projection:event:existing");
        Assert.Equal(["call:10"], existing.ObservationIds);
        var source = projection.Events.Single(row => row.ProjectionEventId != existing.ProjectionEventId);
        Assert.Equal(["call:11"], source.ObservationIds);
        Assert.False(source.OperatorVisible);
        var link = Assert.Single(projection.ProvisionalAssociations);
        Assert.Equal(source.ProjectionEventId, link.SourceProjectionEventId);
        Assert.Equal(existing.ProjectionEventId, link.CandidateProjectionEventId);
    }

    [Fact]
    public async Task InvalidCitationFailsClosedToUnresolvedSingletons()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "MVC with critical injuries."));
        var item = Event(
            "event:mvc",
            IncidentBatchEventDisposition.NewEvent,
            string.Empty,
            ["call:1"],
            "Critical-injury MVC",
            "A crash caused critical injuries.",
            [Citation("transcript:1", "phrase absent from transcript")],
            []);
        var result = await RunAsync(bundle, ["call:1"], [], new FixedProposer(Proposal([item])));

        Assert.Contains(result.LedgerEntry.Entry.ProposalValidationErrors, error => error.Contains("does not occur exactly", StringComparison.Ordinal));
        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.False(projected.OperatorVisible);
        Assert.Empty(projected.Title);
    }

    [Fact]
    public async Task InvalidSiblingDoesNotDiscardIndependentlyValidEvent()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Tree down across the roadway."),
            Observation("call:2", "transcript:2", "Medical response at Salem Road for a hand laceration."));
        var valid = Event(
            "event:tree", IncidentBatchEventDisposition.NewEvent, string.Empty, ["call:1"],
            "Tree blocking roadway", "A tree is blocking the roadway.",
            [Citation("transcript:1", "Tree down across the roadway")], []);
        var invalid = Event(
            "event:medical", IncidentBatchEventDisposition.NewEvent, string.Empty, ["call:2"],
            "Medical response", "A medical response is active.",
            [Citation("transcript:2", "Medical response ... hand laceration")], []);

        var result = await RunAsync(bundle, ["call:1", "call:2"], [], new FixedProposer(Proposal([valid, invalid])));

        Assert.Single(result.LedgerEntry.Entry.ProposalValidationErrors);
        Assert.Equal(["event:tree"], IncidentBatchContract.AcceptedEvents(result.LedgerEntry.Entry).Select(item => item.ProposalToken));
        var visible = Assert.Single(result.Projection.Projection.Events, item => item.OperatorVisible);
        Assert.Equal(["call:1"], visible.ObservationIds);
        var unresolved = Assert.Single(result.Projection.Projection.Events, item => !item.OperatorVisible);
        Assert.Equal(["call:2"], unresolved.ObservationIds);
    }

    [Fact]
    public async Task ObservationCannotBelongToTwoProposals()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "MVC with critical injuries."));
        var first = Event("event:1", IncidentBatchEventDisposition.NewEvent, string.Empty, ["call:1"], "MVC", "A crash occurred.", [Citation("transcript:1", "MVC")], []);
        var second = Event("event:2", IncidentBatchEventDisposition.NewEvent, string.Empty, ["call:1"], "Injuries", "Critical injuries were reported.", [Citation("transcript:1", "critical injuries")], []);

        var validation = IncidentBatchContract.ValidateProposal(bundle, ["call:1"], [], Proposal([first, second]));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("more than one event proposal", StringComparison.Ordinal));
        var result = await RunAsync(bundle, ["call:1"], [], new FixedProposer(Proposal([first, second])));
        Assert.Empty(IncidentBatchContract.AcceptedEvents(result.LedgerEntry.Entry));
        Assert.False(Assert.Single(result.Projection.Projection.Events).OperatorVisible);
    }

    [Fact]
    public async Task ProposerFailureIsRetainedAndFailsClosed()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "MVC with critical injuries."));
        var result = await RunAsync(bundle, ["call:1"], [], new ThrowingProposer());

        Assert.Equal("model offline", result.LedgerEntry.Entry.Execution.ProposerError);
        Assert.Empty(result.LedgerEntry.Entry.Proposal.Events);
        Assert.False(Assert.Single(result.Projection.Projection.Events).OperatorVisible);
    }

    [Fact]
    public void LiveSelectionUsesRetrievalOnlyToBoundCandidatesAndExcludesRadioLabels()
    {
        var oldCall = Call(10, "White truck crashed on County Road 725.", 1000) with { TalkgroupName = "Static label", Talkgroup = 999 };
        var firstNew = Call(11, "Critical injuries in that white truck crash.", 1010) with { TalkgroupName = "Other label", Talkgroup = 123 };
        var secondNew = Call(12, "Routine status traffic.", 1020);
        var prior = PriorProjection(new IncidentBatchProjectionEvent("projection:event:existing", ["call:10"], "Crash", "Crash", true, ["ledger:prior"]));

        var selection = IncidentBatchLiveSelection.Build(
            [firstNew, secondNew],
            [oldCall, firstNew, secondNew],
            [new VectorSearchMatchDto(10, 0.8, "similar")],
            prior,
            4,
            Now);

        Assert.Equal(["call:11", "call:12"], selection.NewObservationIds);
        Assert.Equal("projection:event:existing", Assert.Single(selection.Candidates).ProjectionEventId);
        Assert.All(selection.Bundle.Observations, observation => Assert.Empty(observation.Metadata));
        var prompt = IncidentBatchPrompt.Build(selection.Bundle, selection.NewObservationIds, selection.Candidates);
        Assert.DoesNotContain("Static label", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Other label", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("999", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Critical injuries", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("A radio transmission is not automatically an event", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("underlying real-world condition", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("one short contiguous verbatim substring", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Review every new observation", prompt.UserPrompt, StringComparison.Ordinal);
        var schema = System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions());
        Assert.Contains("operator_basis", schema, StringComparison.Ordinal);
        Assert.Contains("exact_quotes", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("relationship_statement", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveSelectionKeepsRecentVisibleEventsInCandidateSetWithoutEmbeddingMatch()
    {
        var priorCall = Call(10, "Firefighter emergency traffic with one firefighter on board.", 1000);
        var newCall = Call(11, "We need to pick that firefighter up.", 1010);
        var prior = PriorProjection(new IncidentBatchProjectionEvent(
            "projection:event:firefighter", ["call:10"], "Firefighter emergency", "Emergency traffic involving a firefighter.", true, ["ledger:prior"]));

        var selection = IncidentBatchLiveSelection.Build(
            [newCall],
            [priorCall, newCall],
            [],
            prior,
            4,
            Now);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal("projection:event:firefighter", candidate.ProjectionEventId);
        Assert.Contains(selection.Bundle.Observations, item => item.ObservationId == "call:10");
    }

    [Fact]
    public async Task MultiCallHostageProposalIsNotSubjectToStaticSemanticAdmission()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Caller reports a man holding a woman inside."),
            Observation("call:2", "transcript:2", "The man has a knife."),
            Observation("call:3", "transcript:3", "Units are setting a perimeter at North Bishop Drive."),
            Observation("call:4", "transcript:4", "The woman may still be inside with him."));
        var item = Event(
            "event:hostage",
            IncidentBatchEventDisposition.NewEvent,
            string.Empty,
            ["call:1", "call:2", "call:3", "call:4"],
            "Woman held by armed man on North Bishop Drive",
            "Several calls describe a woman potentially being held by a man with a knife while units establish a perimeter.",
            [
                Citation("transcript:1", "holding a woman"),
                Citation("transcript:2", "has a knife"),
                Citation("transcript:3", "setting a perimeter"),
                Citation("transcript:4", "still be inside")
            ],
            []);

        var result = await RunAsync(bundle, ["call:1", "call:2", "call:3", "call:4"], [], new FixedProposer(Proposal([item])));

        Assert.Empty(result.LedgerEntry.Entry.ProposalValidationErrors);
        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.True(projected.OperatorVisible);
        Assert.Equal(4, projected.ObservationIds.Count);
    }

    [Fact]
    public async Task OneTranscriptMaySupplySeveralSeparatelyVerifiedEvidenceSpans()
    {
        var bundle = Bundle(Observation(
            "call:1",
            "transcript:1",
            "1717 East Rebel Road. He sent a message to a friend and said he wanted it to be over."));
        var item = Event(
            "event:threat",
            IncidentBatchEventDisposition.NewEvent,
            string.Empty,
            ["call:1"],
            "Potential threat at East Rebel Road",
            "A message indicated a potential threat at a stated location.",
            [
                Citation("transcript:1", "1717 East Rebel Road"),
                Citation("transcript:1", "said he wanted it to be over")
            ],
            []);

        var result = await RunAsync(bundle, ["call:1"], [], new FixedProposer(Proposal([item])));

        Assert.Empty(result.LedgerEntry.Entry.ProposalValidationErrors);
        Assert.True(Assert.Single(result.Projection.Projection.Events).OperatorVisible);
    }

    private static async Task<IncidentBatchRunResult> RunAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IIncidentBatchProposer proposer,
        IncidentBatchProjection? prior = null)
    {
        var singletons = newObservationIds.Select(id => new IncidentBatchSingletonIdentity(id, $"projection:singleton:{id}")).ToList();
        var coordinator = new IncidentBatchCoordinator(proposer, new MemoryStore(), new FixedTimeProvider(Now));
        return await coordinator.RunAsync(
            new IncidentBatchRunRequest("run:1", "ledger:1", "projection:1", singletons, "test", $"test-config;{IncidentBatchContract.PerEventAcceptanceConfigurationToken}"),
            bundle,
            prior,
            newObservationIds,
            candidates,
            CancellationToken.None);
    }

    private static IncidentBatchProposal Proposal(IReadOnlyList<IncidentBatchEventProposal> events) =>
        new("proposal:1", Now, "test-model", "incident-batch-constructor-v1", events);

    private static IncidentBatchEventProposal Event(
        string token,
        IncidentBatchEventDisposition disposition,
        string candidateToken,
        IReadOnlyList<string> observations,
        string title,
        string summary,
        IReadOnlyList<IncidentEventStateTranscriptCitation> newEvidence,
        IReadOnlyList<IncidentEventStateTranscriptCitation> candidateEvidence) =>
        new(token, disposition, candidateToken, observations, title, summary, "The cited observations may describe one event.", 0.2, newEvidence, candidateEvidence, [], []);

    private static IncidentEventStateTranscriptCitation Citation(string transcriptId, string quote) => new(transcriptId, quote);

    private static IncidentEventStateObservationBundle Bundle(params IncidentEventStateSourceObservation[] observations) =>
        new("bundle:1", Now, observations, []);

    private static IncidentEventStateSourceObservation Observation(string observationId, string transcriptId, string text) =>
        new(observationId, long.Parse(observationId["call:".Length..]), Now.ToUnixTimeSeconds(), string.Empty, null,
            [new IncidentEventStateTranscriptObservation(transcriptId, text, "test", Now)],
            new Dictionary<string, IncidentEventStateMetadataObservation>());

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

    private static IncidentBatchProjection PriorProjection(params IncidentBatchProjectionEvent[] events) =>
        new("run:1", "projection:prior", Now.AddMinutes(-1), ["ledger:prior"], events, []);

    private sealed class FixedProposer(IncidentBatchProposal proposal) : IIncidentBatchProposer
    {
        public Task<IncidentBatchProposal> ProposeAsync(IncidentEventStateObservationBundle bundle, IReadOnlyList<string> newObservationIds, IReadOnlyList<IncidentBatchCandidate> candidates, CancellationToken ct) => Task.FromResult(proposal);
    }

    private sealed class ThrowingProposer : IIncidentBatchProposer
    {
        public Task<IncidentBatchProposal> ProposeAsync(IncidentEventStateObservationBundle bundle, IReadOnlyList<string> newObservationIds, IReadOnlyList<IncidentBatchCandidate> candidates, CancellationToken ct) => throw new InvalidOperationException("model offline");
    }

    private sealed class MemoryStore : IIncidentBatchStore
    {
        public Task<IncidentBatchRunResult> AppendIncidentBatchRunAsync(IncidentBatchLedgerEntry entry, IncidentBatchProjection projection, CancellationToken ct) =>
            Task.FromResult(new IncidentBatchRunResult(new IncidentBatchStoredLedgerEntry(1, "entry-hash", entry), new IncidentBatchStoredProjection(1, "projection-hash", projection)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
