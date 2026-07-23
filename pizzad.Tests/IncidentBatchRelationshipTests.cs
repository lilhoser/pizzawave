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
        json.Remove("confirmationProposal");
        json.Remove("confirmationProposalValidationErrors");
        json.Remove("confirmationExecution");

        var restored = System.Text.Json.JsonSerializer.Deserialize<IncidentBatchLedgerEntry>(json.ToJsonString(), EngineConfig.JsonOptions());

        Assert.NotNull(restored);
        Assert.Null(restored.RelationshipProposal);
        Assert.Null(restored.ConfirmationProposal);
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
    public void OversizedSourceRelationshipSetIsRejectedWithoutDiscardingIndependentSource()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Source one has a concrete follow-up."),
            Observation("call:2", "transcript:2", "Source two continues candidate one."),
            Observation("call:3", "transcript:3", "Candidate one concrete event."),
            Observation("call:4", "transcript:4", "Candidate two concrete event."),
            Observation("call:5", "transcript:5", "Candidate three concrete event."),
            Observation("call:6", "transcript:6", "Candidate four concrete event."));
        var sources = new[]
        {
            new IncidentBatchRelationshipSource("event:one", ["call:1"]),
            new IncidentBatchRelationshipSource("event:two", ["call:2"])
        };
        var candidates = Enumerable.Range(1, 4)
            .Select(index => new IncidentBatchCandidate($"candidate:{index}", $"projection:{index}", [$"call:{index + 2}"]))
            .ToList();
        var oversized = candidates.Select((candidate, index) => Relationship(
            "event:one", candidate.CandidateToken, "transcript:1", "concrete follow-up",
            $"transcript:{index + 3}", $"Candidate {new[] { "one", "two", "three", "four" }[index]} concrete event"));
        var independent = Relationship(
            "event:two", "candidate:1", "transcript:2", "continues candidate one",
            "transcript:3", "Candidate one concrete event");
        var proposal = Proposal(oversized.Append(independent).ToArray());

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, proposal);
        var accepted = IncidentBatchRelationshipContract.AcceptedRelationships(bundle, sources, candidates, proposal);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains($"more than {IncidentBatchRelationshipContract.MaximumRelationshipsPerSource} relationships", StringComparison.Ordinal));
        Assert.Equal(independent, Assert.Single(accepted));
    }

    [Fact]
    public void RelationshipPromptSchemaBoundsResponseSize()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "A concrete source event."),
            Observation("call:2", "transcript:2", "A concrete candidate event."));
        var prompt = IncidentBatchRelationshipPrompt.Build(
            bundle,
            [new IncidentBatchRelationshipSource("event:source", ["call:1"])],
            [new IncidentBatchCandidate("candidate:one", "projection:one", ["call:2"])]);
        var schema = System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions());

        Assert.Contains($"Return at most {IncidentBatchRelationshipContract.MaximumReturnedRelationships} relationships", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains($"\"maxItems\": {IncidentBatchRelationshipContract.MaximumReturnedRelationships}", schema, StringComparison.Ordinal);
        Assert.Contains($"\"maxLength\": {IncidentBatchRelationshipContract.MaximumTextLength}", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void RelationshipPromptTreatsAbstentionAsExpectedAndRequiresConcreteCrossReference()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "A ceiling is leaking in the surgery area."),
            Observation("call:2", "transcript:2", "A vehicle is stopped beside the roadway."));
        var prompt = IncidentBatchRelationshipPrompt.Build(
            bundle,
            [new IncidentBatchRelationshipSource("event:source", ["call:1"])],
            [new IncidentBatchCandidate("candidate:one", "projection:one", ["call:2"])]);

        Assert.Contains("returning an empty relationships array is a correct and expected result", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("specific operational connection", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("superficial resemblance", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("citations prove what each transcript says; they do not by themselves prove a connection", prompt.UserPrompt, StringComparison.Ordinal);
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
    public void ConfirmedMembershipCannotRetainMaterialCounterevidence()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "A two-year-old female is receiving CPAP."),
            Observation("call:2", "transcript:2", "A 19-year-old female has difficulty breathing at Ringgold Road."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:pediatric", ["call:1"]) };
        var candidates = new[] { new IncidentBatchCandidate("candidate:adult", "projection:adult", ["call:2"]) };
        var relationship = Relationship(
            "event:pediatric",
            "candidate:adult",
            "transcript:1",
            "two-year-old female",
            "transcript:2",
            "19-year-old female") with
        {
            Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership,
            Uncertainty = 0,
            AlternativeInterpretations = ["The ages describe different patients."],
            UnresolvedQuestions = ["Are these the same patient?"]
        };

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, Proposal(relationship));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("cannot retain counterinterpretations", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidConfirmationDoesNotDiscardIndependentProvisionalRelationship()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "A two-year-old female is receiving CPAP."),
            Observation("call:2", "transcript:2", "A worker asked whether her supervisor was notified."),
            Observation("call:3", "transcript:3", "A 19-year-old female has difficulty breathing at Ringgold Road."),
            Observation("call:4", "transcript:4", "A cleaning worker is locked inside the room."));
        var sources = new[]
        {
            new IncidentBatchRelationshipSource("event:pediatric", ["call:1"]),
            new IncidentBatchRelationshipSource("event:worker-question", ["call:2"])
        };
        var candidates = new[]
        {
            new IncidentBatchCandidate("candidate:adult", "projection:adult", ["call:3"]),
            new IncidentBatchCandidate("candidate:worker", "projection:worker", ["call:4"])
        };
        var invalidConfirmation = Relationship(
            "event:pediatric", "candidate:adult", "transcript:1", "two-year-old female", "transcript:3", "19-year-old female") with
        {
            Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership,
            Uncertainty = 0,
            UnresolvedQuestions = ["The ages conflict."]
        };
        var validProvisional = Relationship(
            "event:worker-question", "candidate:worker", "transcript:2", "worker", "transcript:4", "cleaning worker");
        var proposal = Proposal(invalidConfirmation, validProvisional);

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, proposal);
        var accepted = IncidentBatchRelationshipContract.AcceptedRelationships(bundle, sources, candidates, proposal);

        Assert.False(validation.IsValid);
        Assert.Equal(validProvisional, Assert.Single(accepted));
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
            "White truck crashed") with { Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership, Uncertainty = 0 };
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
    public async Task IndependentVerifierRejectsFalseConfirmationWithoutMergingEvents()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Medical 15 at 9701 Mountain Lake Drive."),
            Observation("call:2", "transcript:2", "Medical 13 at 3405 Brandon Avenue."));
        var construction = new IncidentBatchEventProposal(
            "event:brandon", IncidentBatchEventDisposition.ProvisionalEvent, string.Empty, ["call:2"],
            "model title", "model summary", "medical response", 0.1,
            [new IncidentEventStateTranscriptCitation("transcript:2", "Medical 13 at 3405 Brandon Avenue")], [], [], []);
        var constructor = new CapturingConstructorProposer(new IncidentBatchProposal(
            "proposal:construction", Now, "test-model", IncidentBatchPrompt.PromptIdentity, [construction]));
        var confirmed = Relationship(
            "event:brandon", "candidate:mountain", "transcript:2", "Medical 13 at 3405 Brandon Avenue",
            "transcript:1", "Medical 15 at 9701 Mountain Lake Drive") with
        {
            Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership,
            Uncertainty = 0
        };
        var relationship = new CapturingRelationshipProposer(Proposal(confirmed));
        var verifier = new CapturingConfirmationVerifier(new IncidentBatchConfirmationProposal(
            "confirmation:reject", Now, "test-model", IncidentBatchConfirmationPrompt.PromptIdentity,
            [new IncidentBatchConfirmationDecision(
                "event:brandon", "candidate:mountain", IncidentBatchConfirmationDecisionKind.Reject,
                "The explicit locations and medical identifiers differ.",
                [new IncidentEventStateTranscriptCitation("transcript:2", "3405 Brandon Avenue")],
                [new IncidentEventStateTranscriptCitation("transcript:1", "9701 Mountain Lake Drive")],
                ["The source boundaries name different addresses."], [])]));
        var prior = new IncidentBatchProjection(
            "run:verify", "projection:prior", Now.AddMinutes(-1), ["ledger:prior"],
            [new IncidentBatchProjectionEvent("projection:mountain", ["call:1"], "Mountain Lake medical", "Medical response", false, true, ["ledger:prior"])], []);
        var coordinator = new IncidentBatchCoordinator(constructor, relationship, verifier, new MemoryStore(), new FixedTimeProvider(Now));

        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                "run:verify", "ledger:new", "projection:new",
                [new IncidentBatchSingletonIdentity("call:2", "projection:brandon")],
                "test", $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken};{IncidentBatchConfirmationContract.ConfigurationToken}"),
            bundle,
            prior,
            ["call:2"],
            [new IncidentBatchCandidate("candidate:mountain", "projection:mountain", ["call:1"])],
            CancellationToken.None);

        Assert.Single(result.LedgerEntry.Entry.RelationshipProposal!.Relationships);
        Assert.Empty(IncidentBatchRelationshipContract.AcceptedRelationships(result.LedgerEntry.Entry));
        Assert.Equal(2, result.Projection.Projection.Events.Count);
        Assert.Contains(result.Projection.Projection.Events, item => item.ProjectionEventId == "projection:brandon" && item.OperatorReview);
        Assert.NotNull(result.LedgerEntry.Entry.ConfirmationExecution);
    }

    [Fact]
    public async Task StagedRelationshipQueuesConfirmationWithoutMergingDuringIntake()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "White truck crashed on County Road 725."),
            Observation("call:2", "transcript:2", "Critical injuries in that same white truck crash."));
        var construction = new IncidentBatchEventProposal(
            "event:update", IncidentBatchEventDisposition.ProvisionalEvent, string.Empty, ["call:2"],
            "model title", "model summary", "crash update", 0.1,
            [new IncidentEventStateTranscriptCitation("transcript:2", "same white truck crash")], [], [], []);
        var constructor = new CapturingConstructorProposer(new IncidentBatchProposal(
            "proposal:construction", Now, "test-model", IncidentBatchPrompt.PromptIdentity, [construction]));
        var confirmed = Relationship(
            "event:update", "candidate:crash", "transcript:2", "same white truck crash",
            "transcript:1", "White truck crashed") with
        {
            Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership,
            Uncertainty = 0
        };
        var relationship = new CapturingRelationshipProposer(Proposal(confirmed));
        var prior = new IncidentBatchProjection(
            "run:staged", "projection:prior", Now.AddMinutes(-1), ["ledger:prior"],
            [new IncidentBatchProjectionEvent("projection:crash", ["call:1"], "Crash", "White truck crash", false, true, ["ledger:prior"])], []);
        var coordinator = new IncidentBatchCoordinator(
            constructor,
            relationship,
            new MemoryStore(),
            new FixedTimeProvider(Now));

        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                "run:staged", "ledger:new", "projection:new",
                [new IncidentBatchSingletonIdentity("call:2", "projection:update")],
                "test",
                $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchContract.ObservationIsolatedOwnershipConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken};{IncidentBatchExecutionArchitecture.StagedRelationshipAsynchronousConfirmationToken}"),
            bundle,
            prior,
            ["call:2"],
            [new IncidentBatchCandidate("candidate:crash", "projection:crash", ["call:1"])],
            CancellationToken.None);

        Assert.True(IncidentBatchContract.ValidateLedgerEntry(result.LedgerEntry.Entry).IsValid);
        Assert.Equal(2, result.Projection.Projection.Events.Count);
        Assert.All(result.Projection.Projection.Events, item => Assert.False(item.OperatorVisible));
        var pending = Assert.Single(result.Projection.Projection.ProvisionalAssociations);
        Assert.Equal("ledger:new:event:update:candidate:crash", pending.AssociationId);
        var request = Assert.Single(IncidentBatchVerificationQueueContract.BuildRequests(result.LedgerEntry.Entry));
        Assert.Equal(IncidentBatchEventDisposition.ConfirmedMembership, request.ProposedDisposition);
        var context = IncidentBatchVerificationQueueContract.BuildContext(result.LedgerEntry.Entry, request);
        Assert.Equal(confirmed, context.Relationship);

        var reviewConfirmation = new IncidentBatchConfirmationProposal(
            "confirmation:review",
            Now.AddSeconds(1),
            "test-model",
            IncidentBatchConfirmationPrompt.PromptIdentity,
            [new IncidentBatchConfirmationDecision(
                "event:update",
                "candidate:crash",
                IncidentBatchConfirmationDecisionKind.Review,
                "The calls plausibly describe the same crash, but the later call omits the road.",
                [new IncidentEventStateTranscriptCitation("transcript:2", "same white truck crash")],
                [new IncidentEventStateTranscriptCitation("transcript:1", "White truck crashed")],
                [],
                ["The later transmission does not repeat County Road 725."])]);
        var reviewResult = IncidentBatchVerificationQueueContract.BuildResult(
            result.LedgerEntry.Entry,
            request,
            reviewConfirmation,
            new IncidentBatchConfirmationExecutionContext(100, string.Empty),
            Now.AddSeconds(2));
        Assert.Equal(IncidentBatchVerificationOutcome.Review, reviewResult.Outcome);
        var reviewProjection = IncidentBatchVerificationProjector.Apply(
            result.Projection.Projection,
            result.LedgerEntry.Entry,
            request,
            reviewResult,
            "projection:review",
            Now.AddSeconds(2));
        Assert.Equal(2, reviewProjection.Events.Count);
        var reviewedLink = Assert.Single(reviewProjection.ProvisionalAssociations);
        Assert.Equal(IncidentBatchConfirmationContract.ReviewUncertaintyFloor, reviewedLink.Uncertainty);
        Assert.Equal(reviewConfirmation.Decisions[0].VerificationStatement, reviewedLink.RelationshipStatement);
        Assert.Equal(reviewConfirmation.Decisions[0].UnresolvedQuestions, reviewedLink.UnresolvedQuestions);

        var confirmation = new IncidentBatchConfirmationProposal(
            "confirmation:staged",
            Now.AddSeconds(1),
            "test-model",
            IncidentBatchConfirmationPrompt.PromptIdentity,
            [new IncidentBatchConfirmationDecision(
                "event:update",
                "candidate:crash",
                IncidentBatchConfirmationDecisionKind.Verify,
                "Both calls explicitly identify the same white-truck crash.",
                [new IncidentEventStateTranscriptCitation("transcript:2", "same white truck crash")],
                [new IncidentEventStateTranscriptCitation("transcript:1", "White truck crashed")],
                [],
                [])]);
        var verificationResult = IncidentBatchVerificationQueueContract.BuildResult(
            result.LedgerEntry.Entry,
            request,
            confirmation,
            new IncidentBatchConfirmationExecutionContext(100, string.Empty),
            Now.AddSeconds(2));
        Assert.Equal(IncidentBatchVerificationOutcome.Verified, verificationResult.Outcome);
        var verifiedProjection = IncidentBatchVerificationProjector.Apply(
            result.Projection.Projection,
            result.LedgerEntry.Entry,
            request,
            verificationResult,
            "projection:verified",
            Now.AddSeconds(2));
        var merged = Assert.Single(verifiedProjection.Events);
        Assert.Equal(["call:1", "call:2"], merged.ObservationIds);
        Assert.True(merged.OperatorVisible);
        Assert.Empty(verifiedProjection.ProvisionalAssociations);
    }

    [Fact]
    public async Task IndependentVerifierAllowsGroundedConfirmationToMergeEvents()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "White truck crashed on County Road 725."),
            Observation("call:2", "transcript:2", "Critical injuries in that same white truck crash."));
        var construction = new IncidentBatchEventProposal(
            "event:update", IncidentBatchEventDisposition.ProvisionalEvent, string.Empty, ["call:2"],
            "model title", "model summary", "crash update", 0.1,
            [new IncidentEventStateTranscriptCitation("transcript:2", "same white truck crash")], [], [], []);
        var constructor = new CapturingConstructorProposer(new IncidentBatchProposal(
            "proposal:construction", Now, "test-model", IncidentBatchPrompt.PromptIdentity, [construction]));
        var confirmed = Relationship(
            "event:update", "candidate:crash", "transcript:2", "same white truck crash",
            "transcript:1", "White truck crashed") with
        {
            Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership,
            Uncertainty = 0
        };
        var relationship = new CapturingRelationshipProposer(Proposal(confirmed));
        var verifier = new CapturingConfirmationVerifier(new IncidentBatchConfirmationProposal(
            "confirmation:verify", Now, "test-model", IncidentBatchConfirmationPrompt.PromptIdentity,
            [new IncidentBatchConfirmationDecision(
                "event:update", "candidate:crash", IncidentBatchConfirmationDecisionKind.Verify,
                "Both sides explicitly identify the same white-truck crash.",
                [
                    new IncidentEventStateTranscriptCitation("transcript:2", "same white truck crash"),
                    new IncidentEventStateTranscriptCitation("transcript:2", "White truck crashed")
                ],
                [new IncidentEventStateTranscriptCitation("transcript:1", "White truck crashed")],
                [], [])]));
        var prior = new IncidentBatchProjection(
            "run:verify", "projection:prior", Now.AddMinutes(-1), ["ledger:prior"],
            [new IncidentBatchProjectionEvent("projection:crash", ["call:1"], "Crash", "White truck crash", false, true, ["ledger:prior"])], []);
        var coordinator = new IncidentBatchCoordinator(constructor, relationship, verifier, new MemoryStore(), new FixedTimeProvider(Now));

        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                "run:verify", "ledger:new", "projection:new",
                [new IncidentBatchSingletonIdentity("call:2", "projection:update")],
                "test", $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken};{IncidentBatchConfirmationContract.ConfigurationToken}"),
            bundle,
            prior,
            ["call:2"],
            [new IncidentBatchCandidate("candidate:crash", "projection:crash", ["call:1"])],
            CancellationToken.None);

        Assert.Single(IncidentBatchRelationshipContract.AcceptedRelationships(result.LedgerEntry.Entry));
        var projected = Assert.Single(result.Projection.Projection.Events);
        Assert.Equal("projection:crash", projected.ProjectionEventId);
        Assert.Equal(["call:1", "call:2"], projected.ObservationIds);
        Assert.True(projected.OperatorVisible);
        Assert.Contains(
            result.LedgerEntry.Entry.ConfirmationProposalValidationErrors ?? [],
            error => error.Contains("does not occur exactly", StringComparison.Ordinal));
        var legacyEntry = result.LedgerEntry.Entry with
        {
            Execution = result.LedgerEntry.Entry.Execution with
            {
                ConfigurationIdentity = $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken};{IncidentBatchConfirmationContract.LegacyConfigurationToken}"
            }
        };
        Assert.Empty(IncidentBatchRelationshipContract.AcceptedRelationships(legacyEntry));
    }

    [Fact]
    public void RelationshipVerifierPromptSeparatesMembershipFromSpecificAssociation()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Did the worker notify her supervisor?"),
            Observation("call:2", "transcript:2", "A cleaning worker is locked inside the room."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:question", ["call:1"]) };
        var candidates = new[] { new IncidentBatchCandidate("candidate:worker", "projection:worker", ["call:2"]) };
        var relationship = Relationship(
            "event:question", "candidate:worker", "transcript:1", "worker notify her supervisor",
            "transcript:2", "cleaning worker");

        var prompt = IncidentBatchConfirmationPrompt.Build(bundle, sources, candidates, [relationship]);
        var schema = System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions());

        Assert.Contains("For confirmed_membership", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("For provisional_association", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Choose verify, review, or reject", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Review is not a softer reject", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Uncertainty about whether any connection exists belongs in reject", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not invent local geography", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("materially incompatible subjects", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("multiple independent compatible facts", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("shared_connection_facts", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("specific_connection_supported", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("material_conflict", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("unresolved_material_conflict", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("ASR text is noisy evidence, not authoritative spelling", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Timing alone remains insufficient", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("non-merging operator-visible association", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Information present on only one side is omission, not contradiction", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Do not manufacture a mismatch from a detail that is merely absent", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Evidence spans and their exact quote text are owned by the application", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("evidence_id", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("\"review\"", schema, StringComparison.Ordinal);
        Assert.Contains("shared_connection_facts", schema, StringComparison.Ordinal);
        Assert.Contains("specific_connection_supported", schema, StringComparison.Ordinal);
        Assert.Contains("material_conflicts", schema, StringComparison.Ordinal);
        Assert.Contains("unresolved_material_conflict", schema, StringComparison.Ordinal);
        Assert.Contains("source_evidence_ids", schema, StringComparison.Ordinal);
        Assert.Contains("candidate_evidence_ids", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source_evidence\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"candidate_evidence\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("exact_quotes", schema, StringComparison.Ordinal);
        Assert.Contains($"\"maxLength\": {IncidentBatchRelationshipContract.MaximumTextLength}", schema, StringComparison.Ordinal);
        Assert.Contains(IncidentBatchConfirmationContract.ApplicationOwnedEvidenceToken, IncidentBatchConfirmationContract.ConfigurationToken, StringComparison.Ordinal);
        Assert.Contains(IncidentBatchConfirmationContract.ReviewDowngradeToken, IncidentBatchConfirmationContract.ConfigurationToken, StringComparison.Ordinal);
        Assert.Contains(IncidentBatchConfirmationAdmission.ConfigurationToken, IncidentBatchConfirmationContract.ConfigurationToken, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(IncidentBatchConfirmationDecisionKind.Verify, false, false, IncidentBatchConfirmationDecisionKind.Reject)]
    [InlineData(IncidentBatchConfirmationDecisionKind.Review, false, false, IncidentBatchConfirmationDecisionKind.Reject)]
    [InlineData(IncidentBatchConfirmationDecisionKind.Verify, true, true, IncidentBatchConfirmationDecisionKind.Review)]
    [InlineData(IncidentBatchConfirmationDecisionKind.Review, true, true, IncidentBatchConfirmationDecisionKind.Review)]
    [InlineData(IncidentBatchConfirmationDecisionKind.Reject, true, true, IncidentBatchConfirmationDecisionKind.Review)]
    [InlineData(IncidentBatchConfirmationDecisionKind.Verify, true, false, IncidentBatchConfirmationDecisionKind.Verify)]
    [InlineData(IncidentBatchConfirmationDecisionKind.Review, true, false, IncidentBatchConfirmationDecisionKind.Review)]
    public void StructuredAdmissionRoutesUnsupportedAndConflictedPairsSafely(
        IncidentBatchConfirmationDecisionKind proposed,
        bool specificConnectionSupported,
        bool hasMaterialConflict,
        IncidentBatchConfirmationDecisionKind expected)
    {
        var actual = IncidentBatchConfirmationAdmission.ResolveDecision(
            proposed,
            specificConnectionSupported,
            hasMaterialConflict);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ApplicationEvidenceCatalogReturnsOnlyExactBoundedSpansAndDeduplicatesSelections()
    {
        var transcript = string.Join(" ", Enumerable.Range(1, 100).Select(index => $"word{index}"));
        var bundle = Bundle(Observation("call:1", "transcript:1", transcript));

        var catalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle);

        Assert.True(catalog.Count > 1);
        Assert.All(catalog, span =>
        {
            Assert.InRange(span.ExactQuote.Length, 1, IncidentBatchConfirmationEvidenceCatalog.MaximumSpanLength);
            Assert.Contains(span.ExactQuote, transcript, StringComparison.Ordinal);
            Assert.Equal("transcript:1", span.TranscriptId);
        });
        var selected = IncidentBatchConfirmationEvidenceCatalog.Resolve(
            [catalog[0].EvidenceId, catalog[0].EvidenceId, catalog[^1].EvidenceId],
            catalog);
        Assert.Equal(2, selected.Count);
        Assert.Equal(catalog[0].ExactQuote, selected[0].ExactQuote);
        Assert.Equal(catalog[^1].ExactQuote, selected[1].ExactQuote);
        Assert.Throws<InvalidDataException>(() =>
            IncidentBatchConfirmationEvidenceCatalog.Resolve(["evidence:unknown"], catalog));

        var unbrokenTranscript = new string('x', 1000);
        var unbrokenCatalog = IncidentBatchConfirmationEvidenceCatalog.Build(
            Bundle(Observation("call:2", "transcript:2", unbrokenTranscript)));
        Assert.Equal(5, unbrokenCatalog.Count);
        Assert.All(unbrokenCatalog, span =>
        {
            Assert.InRange(span.ExactQuote.Length, 1, IncidentBatchConfirmationEvidenceCatalog.MaximumSpanLength);
            Assert.Contains(span.ExactQuote, unbrokenTranscript, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(IncidentBatchConfirmationPrompt.PreviousPromptIdentity)]
    [InlineData(IncidentBatchConfirmationPrompt.PriorPromptIdentity)]
    [InlineData(IncidentBatchConfirmationPrompt.ApplicationOwnedPromptIdentity)]
    [InlineData(IncidentBatchConfirmationPrompt.PreviousReviewPromptIdentity)]
    [InlineData(IncidentBatchConfirmationPrompt.PriorEvidenceThresholdPromptIdentity)]
    [InlineData(IncidentBatchConfirmationPrompt.PriorStructuredAdmissionPromptIdentity)]
    public void PreviousRelationshipVerifierResultsRemainReadable(string promptIdentity)
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Respond to 260 Low Circle for a 38-year-old female."),
            Observation("call:2", "transcript:2", "260 Low Circle for a 38-year-old female."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:update", ["call:2"]) };
        var candidates = new[] { new IncidentBatchCandidate("candidate:existing", "projection:existing", ["call:1"]) };
        var relationship = Relationship(
            "event:update", "candidate:existing", "transcript:2", "260 Low Circle",
            "transcript:1", "260 Low Circle") with
        {
            Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership,
            Uncertainty = 0
        };
        var previous = new IncidentBatchConfirmationProposal(
            "confirmation:previous",
            Now,
            "test-model",
            promptIdentity,
            [new IncidentBatchConfirmationDecision(
                "event:update",
                "candidate:existing",
                IncidentBatchConfirmationDecisionKind.Verify,
                "Both sides identify the same address and patient.",
                [new IncidentEventStateTranscriptCitation("transcript:2", "260 Low Circle")],
                [new IncidentEventStateTranscriptCitation("transcript:1", "260 Low Circle")],
                [],
                [])]);

        Assert.Single(IncidentBatchConfirmationContract.AcceptedVerifiedPairs(
            bundle,
            sources,
            candidates,
            [relationship],
            previous,
            retainOnlyExactEvidence: true));
    }

    [Fact]
    public async Task IndependentVerifierRejectsGenericProvisionalAssociation()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "A white Cadillac is being recovered without keys."),
            Observation("call:2", "transcript:2", "A Nissan SUV was stopped on Long-Hull Road."));
        var construction = new IncidentBatchEventProposal(
            "event:nissan", IncidentBatchEventDisposition.ProvisionalEvent, string.Empty, ["call:2"],
            "Nissan stop", "Nissan stop", "vehicle stop", 0.2,
            [new IncidentEventStateTranscriptCitation("transcript:2", "Nissan SUV")], [], [], []);
        var constructor = new CapturingConstructorProposer(new IncidentBatchProposal(
            "proposal:construction", Now, "test-model", IncidentBatchPrompt.PromptIdentity, [construction]));
        var proposed = Relationship(
            "event:nissan", "candidate:cadillac", "transcript:2", "Nissan SUV",
            "transcript:1", "white Cadillac");
        var relationship = new CapturingRelationshipProposer(Proposal(proposed));
        var verifier = new CapturingConfirmationVerifier(new IncidentBatchConfirmationProposal(
            "verification:reject", Now, "test-model", IncidentBatchConfirmationPrompt.PromptIdentity,
            [new IncidentBatchConfirmationDecision(
                "event:nissan", "candidate:cadillac", IncidentBatchConfirmationDecisionKind.Reject,
                "The vehicles and circumstances differ and no cross-reference connects them.",
                [new IncidentEventStateTranscriptCitation("transcript:2", "Nissan SUV")],
                [new IncidentEventStateTranscriptCitation("transcript:1", "white Cadillac")],
                ["The vehicle descriptions conflict."], [])]));
        var prior = new IncidentBatchProjection(
            "run:verify-links", "projection:prior", Now.AddMinutes(-1), ["ledger:prior"],
            [new IncidentBatchProjectionEvent("projection:cadillac", ["call:1"], "Cadillac recovery", "Cadillac recovery", false, true, ["ledger:prior"])], []);
        var coordinator = new IncidentBatchCoordinator(constructor, relationship, verifier, new MemoryStore(), new FixedTimeProvider(Now));

        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                "run:verify-links", "ledger:new", "projection:new",
                [new IncidentBatchSingletonIdentity("call:2", "projection:nissan")],
                "test", $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken};{IncidentBatchConfirmationContract.ConfigurationToken}"),
            bundle,
            prior,
            ["call:2"],
            [new IncidentBatchCandidate("candidate:cadillac", "projection:cadillac", ["call:1"])],
            CancellationToken.None);

        Assert.Single(verifier.ReceivedRelationships);
        Assert.Empty(IncidentBatchRelationshipContract.AcceptedRelationships(result.LedgerEntry.Entry));
        Assert.Empty(result.Projection.Projection.ProvisionalAssociations);
        Assert.Equal(2, result.Projection.Projection.Events.Count);
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

    private sealed class CapturingConfirmationVerifier(IncidentBatchConfirmationProposal proposal) : IIncidentBatchConfirmationVerifier
    {
        public IReadOnlyList<IncidentBatchRelationship> ReceivedRelationships { get; private set; } = [];

        public Task<IncidentBatchConfirmationProposal> VerifyAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<IncidentBatchRelationshipSource> sources,
            IReadOnlyList<IncidentBatchCandidate> candidates,
            IReadOnlyList<IncidentBatchRelationship> relationships,
            CancellationToken ct)
        {
            ReceivedRelationships = relationships;
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
