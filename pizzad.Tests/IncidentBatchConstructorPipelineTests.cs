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
    public async Task SourceCitedNewEventGroupsObservationsForReviewUntilLaterConfirmation()
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
        Assert.False(projected.OperatorVisible);
        Assert.True(projected.OperatorReview);
        Assert.Equal(["call:1", "call:2"], projected.ObservationIds);
        Assert.Equal("Tree down … same tree", projected.Title);
    }

    [Fact]
    public async Task ProjectionSummaryUsesValidatedEvidenceInsteadOfModelNarrative()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "The vehicle is sparking near Notting Hill."));
        var item = Event(
            "event:vehicle",
            IncidentBatchEventDisposition.NewEvent,
            string.Empty,
            ["call:1"],
            "Sparking vehicle",
            "The road is badly damaged and the occupants are fleeing.",
            [Citation("transcript:1", "vehicle is sparking"), Citation("transcript:1", "near Notting Hill")],
            []);

        var result = await RunAsync(bundle, ["call:1"], [], new FixedProposer(Proposal([item])));

        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.Equal("vehicle is sparking … near Notting Hill", projected.Title);
        Assert.Equal("vehicle is sparking … near Notting Hill", projected.Summary);
        Assert.DoesNotContain("Sparking vehicle", projected.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("road is badly damaged", projected.Summary, StringComparison.Ordinal);
        Assert.Contains("road is badly damaged", result.LedgerEntry.Entry.Proposal.Events[0].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AsynchronousProvisionalProposalDoesNotRequireDiscardedDisplayProse()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "The vehicle is sparking near Notting Hill."));
        var item = Event(
            "event:vehicle",
            IncidentBatchEventDisposition.ProvisionalEvent,
            string.Empty,
            ["call:1"],
            string.Empty,
            string.Empty,
            [Citation("transcript:1", "vehicle is sparking")],
            []);

        var regular = IncidentBatchContract.ValidateProposal(bundle, ["call:1"], [], Proposal([item]));
        var asynchronous = IncidentBatchContract.ValidateProposal(
            bundle,
            ["call:1"],
            [],
            Proposal([item]) with { PromptIdentity = IncidentBatchPrompt.AsynchronousProvisionalPromptIdentity });

        Assert.False(regular.IsValid);
        Assert.Contains(regular.Errors, error => error.Contains("title", StringComparison.Ordinal));
        Assert.Contains(regular.Errors, error => error.Contains("summary", StringComparison.Ordinal));
        Assert.True(asynchronous.IsValid, string.Join(Environment.NewLine, asynchronous.Errors));
    }

    [Fact]
    public async Task ProvisionalEventIsGroupedForReviewWithoutBecomingVisible()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "A young person may need assistance."),
            Observation("call:2", "transcript:2", "The caller is still gathering details."));
        var item = Event(
            "event:possible-assistance",
            IncidentBatchEventDisposition.ProvisionalEvent,
            string.Empty,
            ["call:1", "call:2"],
            "Possible assistance request",
            "A developing situation may require assistance.",
            [Citation("transcript:1", "may need assistance"), Citation("transcript:2", "still gathering details")],
            []);

        var result = await RunAsync(bundle, ["call:1", "call:2"], [], new FixedProposer(Proposal([item])));

        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.False(projected.OperatorVisible);
        Assert.True(projected.OperatorReview);
        Assert.Equal(["call:1", "call:2"], projected.ObservationIds);
        Assert.Equal("may need assistance … still gathering details", projected.Summary);
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
            false,
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
        Assert.Equal("Vehicle crash", projected.Title);
    }

    [Fact]
    public async Task AsynchronousIntakeKeepsProposedMembershipProvisionalAndQueuesVerification()
    {
        var prior = PriorProjection(new IncidentBatchProjectionEvent(
            "projection:event:existing",
            ["call:10"],
            "Vehicle crash",
            "A vehicle crash is active.",
            true,
            false,
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

        var result = await RunAsync(
            bundle,
            ["call:11"],
            [candidate],
            new FixedProposer(Proposal([item])),
            prior,
            IncidentBatchExecutionArchitecture.AsynchronousProvisionalToken);

        Assert.Equal(2, result.Projection.Projection.Events.Count);
        var existing = result.Projection.Projection.Events.Single(row => row.ProjectionEventId == "projection:event:existing");
        var source = result.Projection.Projection.Events.Single(row => row.ProjectionEventId != existing.ProjectionEventId);
        Assert.Equal(["call:10"], existing.ObservationIds);
        Assert.Equal(["call:11"], source.ObservationIds);
        Assert.True(source.OperatorReview);
        var link = Assert.Single(result.Projection.Projection.ProvisionalAssociations);
        Assert.Equal(existing.ProjectionEventId, link.CandidateProjectionEventId);
        var request = Assert.Single(IncidentBatchVerificationQueueContract.BuildRequests(result.LedgerEntry.Entry));
        Assert.Equal(IncidentBatchEventDisposition.ConfirmedMembership, request.ProposedDisposition);
        Assert.Equal(item.ProposalToken, request.SourceProposalToken);
        var unsafeSynchronousEntry = result.LedgerEntry.Entry with
        {
            RelationshipProposal = new IncidentBatchRelationshipProposal(
                "relationship:unsafe", Now, "application", IncidentBatchRelationshipPrompt.PromptIdentity, []),
            RelationshipProposalValidationErrors = [],
            RelationshipExecution = new IncidentBatchRelationshipExecutionContext(0, string.Empty)
        };
        Assert.Contains(
            IncidentBatchContract.ValidateLedgerEntry(unsafeSynchronousEntry).Errors,
            error => error.Contains("cannot contain synchronous", StringComparison.Ordinal));

        var rejectionProposal = new IncidentBatchConfirmationProposal(
            "confirmation:reject",
            Now.AddSeconds(1),
            "test-verifier",
            IncidentBatchConfirmationPrompt.PromptIdentity,
            [new IncidentBatchConfirmationDecision(
                item.ProposalToken,
                candidate.CandidateToken,
                IncidentBatchConfirmationDecisionKind.Reject,
                "The cited excerpts do not independently establish shared event membership.",
                [Citation("transcript:11", "Critical injuries")],
                [Citation("transcript:10", "White truck crashed")],
                ["The excerpts do not provide an independently verified shared identifier."],
                [])]);
        var rejection = IncidentBatchVerificationQueueContract.BuildResult(
            result.LedgerEntry.Entry,
            request,
            rejectionProposal,
            new IncidentBatchConfirmationExecutionContext(10, string.Empty),
            Now.AddSeconds(2));
        Assert.Equal(IncidentBatchVerificationOutcome.Rejected, rejection.Outcome);
        var rejectedProjection = IncidentBatchVerificationProjector.Apply(
            result.Projection.Projection,
            result.LedgerEntry.Entry,
            request,
            rejection,
            "projection:rejected",
            Now.AddSeconds(2));
        Assert.Equal(2, rejectedProjection.Events.Count);
        Assert.Empty(rejectedProjection.ProvisionalAssociations);
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
            false,
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
    public async Task InvalidCitationIsDiscardedWithoutRejectingIndependentlyGroundedEvent()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "All units stand by for a missing person."),
            Observation("call:2", "transcript:2", "The missing person is a 30-year-old male wearing a green shirt."));
        var item = Event(
            "event:missing-person",
            IncidentBatchEventDisposition.NewEvent,
            string.Empty,
            ["call:1", "call:2"],
            "Missing person search",
            "Units are looking for a missing person.",
            [
                Citation("transcript:1", "All units stand by for a missing person"),
                Citation("transcript:1", "The missing person is suicidal"),
                Citation("transcript:2", "30-year-old male wearing a green shirt")
            ],
            []);

        var result = await RunAsync(bundle, ["call:1", "call:2"], [], new FixedProposer(Proposal([item])));

        Assert.Contains(result.LedgerEntry.Entry.ProposalValidationErrors, error => error.Contains("does not occur exactly", StringComparison.Ordinal));
        var accepted = Assert.Single(IncidentBatchContract.AcceptedEvents(result.LedgerEntry.Entry));
        Assert.Equal(2, accepted.NewObservationEvidence.Count);
        Assert.DoesNotContain(accepted.NewObservationEvidence, citation => citation.ExactQuote == "The missing person is suicidal");
        var projected = Assert.Single(result.Projection.Projection.Events, projectedEvent => projectedEvent.OperatorReview);
        Assert.Equal(["call:1", "call:2"], projected.ObservationIds);
        Assert.DoesNotContain("suicidal", projected.Summary, StringComparison.OrdinalIgnoreCase);
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
        var review = Assert.Single(result.Projection.Projection.Events, item => item.OperatorReview);
        Assert.Equal(["call:1"], review.ObservationIds);
        var unresolved = Assert.Single(result.Projection.Projection.Events, item => !item.OperatorVisible && !item.OperatorReview);
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
        var prior = PriorProjection(new IncidentBatchProjectionEvent("projection:event:existing", ["call:10"], "Crash", "Crash", true, false, ["ledger:prior"]));

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
        Assert.Contains("No candidate-free proposal becomes operator-visible directly", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains($"Return at most {IncidentBatchPrompt.MaximumReturnedEvents} events", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("one short contiguous verbatim substring", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Review every new observation", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("do not also return them in a new_event or any second proposal", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("A provisional association does not require the two sides to be the same event", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not return a candidate-free event when its operator_basis", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Never borrow facts from omitted observations", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("combine their observations and evidence into one provisional_event", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Remove every discarded draft from the events array entirely", prompt.UserPrompt, StringComparison.Ordinal);
        var schema = System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions());
        using var schemaDocument = System.Text.Json.JsonDocument.Parse(schema);
        Assert.Equal(
            IncidentBatchPrompt.MaximumReturnedEvents,
            schemaDocument.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("events").GetProperty("maxItems").GetInt32());
        Assert.Contains("operator_basis", schema, StringComparison.Ordinal);
        Assert.Contains("exact_quotes", schema, StringComparison.Ordinal);
        Assert.Contains("provisional_event", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("relationship_statement", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceIsolatedConstructorContextExcludesRetrievedCandidateState()
    {
        var priorCall = Call(10, "White truck crashed on County Road 725.", 1000);
        var newCall = Call(11, "Critical injuries in that white truck crash.", 1010);
        var prior = PriorProjection(new IncidentBatchProjectionEvent(
            "projection:event:existing", ["call:10"], "Crash", "Crash", true, false, ["ledger:prior"]));

        var selection = IncidentBatchLiveSelection.BuildConstructorContext(
            [newCall],
            [priorCall, newCall],
            [new VectorSearchMatchDto(10, 0.8, "similar")],
            prior,
            4,
            Now,
            sourceIsolated: true);

        Assert.Empty(selection.Candidates);
        var observation = Assert.Single(selection.Bundle.Observations);
        Assert.Equal("call:11", observation.ObservationId);
        Assert.DoesNotContain("White truck crashed", IncidentBatchPrompt.Build(
            selection.Bundle,
            selection.NewObservationIds,
            selection.Candidates,
            asynchronousProvisional: true).UserPrompt,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AsynchronousProvisionalPromptOmitsDisplayTextThatProjectionDerivesFromEvidence()
    {
        var bundle = Bundle(Observation("call:1", "transcript:1", "Tree down across the southbound lane."));

        var regular = IncidentBatchPrompt.Build(bundle, ["call:1"], []);
        var asynchronous = IncidentBatchPrompt.Build(bundle, ["call:1"], [], asynchronousProvisional: true);
        var regularSchema = System.Text.Json.JsonSerializer.Serialize(regular.ResponseFormat, EngineConfig.JsonOptions());
        var asynchronousSchema = System.Text.Json.JsonSerializer.Serialize(asynchronous.ResponseFormat, EngineConfig.JsonOptions());
        using var regularDocument = System.Text.Json.JsonDocument.Parse(regularSchema);
        using var asynchronousDocument = System.Text.Json.JsonDocument.Parse(asynchronousSchema);
        var regularProperties = regularDocument.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("events").GetProperty("items").GetProperty("properties");
        var asynchronousProperties = asynchronousDocument.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("events").GetProperty("items").GetProperty("properties");

        Assert.True(regularProperties.TryGetProperty("title", out _));
        Assert.True(regularProperties.TryGetProperty("summary", out _));
        Assert.False(asynchronousProperties.TryGetProperty("title", out _));
        Assert.False(asynchronousProperties.TryGetProperty("summary", out _));
        Assert.Contains("do not return title or summary", asynchronous.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveSelectionAdmitsNonemptyRepetitiveEvidenceAcrossSystemBoundaries()
    {
        var priorCall = Call(10, "1159 Harrison Pike West, apartment 2208.", 1000) with
        {
            SystemShortName = "whiteoakmt-cleveland",
            TranscriptionStatus = "poor_quality",
            QualityReason = "repetitive"
        };
        var newCall = Call(11, "Engine 1 responding to the lift assist at 1159 Harrison Pike.", 1010) with
        {
            SystemShortName = "whiteoakmt-nbradley"
        };
        var prior = PriorProjection(new IncidentBatchProjectionEvent(
            "projection:event:park-hill", ["call:10"], "Park Hill", "Park Hill", false, true, ["ledger:prior"]));

        var selection = IncidentBatchLiveSelection.Build(
            [newCall],
            [priorCall, newCall],
            [new VectorSearchMatchDto(10, 0.74, "cross-system")],
            prior,
            4,
            Now);

        Assert.True(IncidentBatchLiveSelection.IsEligibleSourceObservation(priorCall));
        Assert.Equal("projection:event:park-hill", Assert.Single(selection.Candidates).ProjectionEventId);
        Assert.Contains(selection.Bundle.Observations, item => item.ObservationId == "call:10");
    }

    [Fact]
    public void LiveSelectionKeepsRecentVisibleEventsInCandidateSetWithoutEmbeddingMatch()
    {
        var priorCall = Call(10, "Firefighter emergency traffic with one firefighter on board.", 1000);
        var newCall = Call(11, "We need to pick that firefighter up.", 1010);
        var prior = PriorProjection(new IncidentBatchProjectionEvent(
            "projection:event:firefighter", ["call:10"], "Firefighter emergency", "Emergency traffic involving a firefighter.", true, false, ["ledger:prior"]));

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
    public void LiveSelectionKeepsRecentReviewEventInCandidateSetWithoutEmbeddingMatch()
    {
        var reviewCall = Call(10, "Circle 65 bleeding at 9505 Thornberry Drive.", 1000);
        var retrievedCall = Call(11, "Unrelated structure fire report.", 1010);
        var newCall = Call(12, "Heavy bleeding at 9505 Dornberry Drive.", 1020);
        var prior = PriorProjection(
            new IncidentBatchProjectionEvent("projection:event:review", ["call:10"], "Bleeding", "Bleeding", false, true, ["ledger:prior"]),
            new IncidentBatchProjectionEvent("projection:event:retrieved", ["call:11"], string.Empty, string.Empty, false, false, ["ledger:prior"]));

        var selection = IncidentBatchLiveSelection.Build(
            [newCall],
            [reviewCall, retrievedCall, newCall],
            [new VectorSearchMatchDto(11, 0.9, "similar")],
            prior,
            2,
            Now);

        Assert.Equal(2, selection.Candidates.Count);
        Assert.Contains(selection.Candidates, item => item.ProjectionEventId == "projection:event:review");
        Assert.Contains(selection.Candidates, item => item.ProjectionEventId == "projection:event:retrieved");
        Assert.Contains(selection.Bundle.Observations, item => item.ObservationId == "call:10");
    }

    [Fact]
    public void LiveSelectionKeepsTwoRecentReviewEventsBeforeRetrievalFill()
    {
        var olderReviewCall = Call(10, "Vehicle stopped on the shoulder with flashers.", 1000);
        var newerReviewCall = Call(11, "Medical transport is identifying the patient.", 1010);
        var retrievedCall = Call(12, "Embedding-retrieved unrelated source.", 1020);
        var newCall = Call(13, "Check whether that passenger car is involved.", 1030);
        var prior = PriorProjection(
            new IncidentBatchProjectionEvent("projection:event:older-review", ["call:10"], "Vehicle", "Vehicle", false, true, ["ledger:prior"]),
            new IncidentBatchProjectionEvent("projection:event:newer-review", ["call:11"], "Medical", "Medical", false, true, ["ledger:prior"]),
            new IncidentBatchProjectionEvent("projection:event:retrieved", ["call:12"], string.Empty, string.Empty, false, false, ["ledger:prior"]));

        var selection = IncidentBatchLiveSelection.Build(
            [newCall],
            [olderReviewCall, newerReviewCall, retrievedCall, newCall],
            [new VectorSearchMatchDto(12, 0.9, "similar")],
            prior,
            4,
            Now);

        Assert.Equal(3, selection.Candidates.Count);
        Assert.Equal("projection:event:newer-review", selection.Candidates[0].ProjectionEventId);
        Assert.Equal("projection:event:older-review", selection.Candidates[1].ProjectionEventId);
        Assert.Contains(selection.Candidates, item => item.ProjectionEventId == "projection:event:retrieved");
    }

    [Fact]
    public void LiveCursorProcessesOldestUnseenCallsWithoutSkippingOverflowOrLateEligibility()
    {
        var calls = Enumerable.Range(1, 30).Select(id => Call(id, $"Call {id}", 1000 + id)).ToList();
        var processed = new HashSet<long>();

        var first = IncidentBatchLiveCursor.SelectNext(calls.Where(call => call.Id != 2).ToList(), 0, processed, 24);
        processed.UnionWith(first.Select(call => call.Id));
        var second = IncidentBatchLiveCursor.SelectNext(calls, 0, processed, 24);

        Assert.Equal(new long[] { 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25 }, first.Select(call => call.Id));
        Assert.Equal(new long[] { 2, 26, 27, 28, 29, 30 }, second.Select(call => call.Id));
        Assert.Equal(30, first.Concat(second).Select(call => call.Id).Distinct().Count());
    }

    [Fact]
    public void LiveCursorKeepsNewRunsNoBackfillUnlessAStartFenceIsConfigured()
    {
        var calls = Enumerable.Range(1, 30).Select(id => Call(id, $"Call {id}", 1000 + id)).ToList();

        Assert.Equal(30, IncidentBatchLiveCursor.ResolveStartFence(0, new HashSet<long>(), calls));
        Assert.Equal(12, IncidentBatchLiveCursor.ResolveStartFence(12, new HashSet<long>(), calls));
        Assert.Equal(4, IncidentBatchLiveCursor.ResolveStartFence(0, new HashSet<long> { 5, 9 }, calls));
    }

    [Fact]
    public async Task MultiCallHostageProposalIsAcceptedIntoReviewWithoutStaticSemanticAdmission()
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
        Assert.False(projected.OperatorVisible);
        Assert.True(projected.OperatorReview);
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
        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.False(projected.OperatorVisible);
        Assert.True(projected.OperatorReview);
    }

    private static async Task<IncidentBatchRunResult> RunAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> newObservationIds,
        IReadOnlyList<IncidentBatchCandidate> candidates,
        IIncidentBatchProposer proposer,
        IncidentBatchProjection? prior = null,
        string executionToken = "")
    {
        var singletons = newObservationIds.Select(id => new IncidentBatchSingletonIdentity(id, $"projection:singleton:{id}")).ToList();
        var coordinator = new IncidentBatchCoordinator(proposer, new MemoryStore(), new FixedTimeProvider(Now));
        return await coordinator.RunAsync(
            new IncidentBatchRunRequest("run:1", "ledger:1", "projection:1", singletons, "test", $"test-config;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchContract.PerCitationAcceptanceConfigurationToken};{executionToken}"),
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
