namespace pizzad.Tests;

public sealed class IncidentEventStateMicroBatchReplayTests
{
    [Fact]
    public void PlannerCoversEveryObservationExactlyOnceInChronologicalBatches()
    {
        var observations = new[]
        {
            Observation("call:4", 121),
            Observation("call:2", 105),
            Observation("call:1", 100),
            Observation("call:3", 120)
        };

        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(2, 10, 2, 60));

        Assert.Equal(4, plan.SourceObservationCount);
        Assert.Equal(4, plan.PlannedObservationCount);
        Assert.Equal(0, plan.DuplicateObservationCount);
        Assert.Equal(2, plan.Batches.Count);
        Assert.Equal(["call:1", "call:2"], plan.Batches[0].NewObservationIds);
        Assert.Empty(plan.Batches[0].ContextObservationIds);
        Assert.Equal(["call:3", "call:4"], plan.Batches[1].NewObservationIds);
        Assert.Equal(["call:1", "call:2"], plan.Batches[1].ContextObservationIds);
        Assert.All(plan.Batches, batch => Assert.Equal(64, batch.ContentHash.Length));
        Assert.Equal(64, plan.ContentHash.Length);
    }

    [Fact]
    public void PlannerUsesResourceLimitsWithoutDiscardingSparseObservations()
    {
        var observations = new[]
        {
            Observation("call:1", 100),
            Observation("call:2", 110),
            Observation("call:3", 111),
            Observation("call:4", 500)
        };

        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(24, 10, 48, 30));

        Assert.Equal(3, plan.Batches.Count);
        Assert.Equal(["call:1", "call:2"], plan.Batches[0].NewObservationIds);
        Assert.Equal(["call:3"], plan.Batches[1].NewObservationIds);
        Assert.Equal(["call:4"], plan.Batches[2].NewObservationIds);
        Assert.Equal(observations.Length, plan.Batches.Sum(batch => batch.NewObservationIds.Count));
    }

    [Fact]
    public void PlannerRejectsDuplicateObservationIdentity()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:1", 101) };

        var error = Assert.Throws<ArgumentException>(() => IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(12, 60, 24, 1200)));

        Assert.Contains("unique", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptExposesOpaqueTokensAndTranscriptEvidenceButNoSemanticMetadata()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(1, 60, 1, 1200));
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);

        var prompt = IncidentEventStateMicroBatchPrompt.Build(plan.Batches[1], lookup);

        Assert.Equal("call:1", prompt.ObservationIdsByToken["context-1"]);
        Assert.Equal("call:2", prompt.ObservationIdsByToken["new-1"]);
        Assert.Contains("transcript for call:2", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("forbidden-category", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("forbidden-talkgroup", prompt.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorAcceptsOneDecisionPerObservationAndGroundedEarlierLinks()
    {
        var observations = new[]
        {
            Observation("call:1", 100),
            Observation("call:2", 101),
            Observation("call:3", 102)
        };
        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(2, 60, 1, 1200));
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var batch = plan.Batches[0];
        var prompt = IncidentEventStateMicroBatchPrompt.Build(batch, lookup);
        var proposal = new IncidentEventStateMicroBatchProposal(
            "model",
            IncidentEventStateMicroBatchPrompt.PromptIdentity,
            [
                new IncidentEventStateMicroBatchObservationDecision(
                    "new-1", IncidentEventStateMicroBatchDecision.Unresolved, string.Empty,
                    string.Empty, 0.8, [], [], ["No earlier positive connection is available."]),
                new IncidentEventStateMicroBatchObservationDecision(
                    "new-2", IncidentEventStateMicroBatchDecision.ProposeLink, "new-1",
                    "continued source-grounded exchange", 0.1,
                    ["call:2:transcript"], ["call:1:transcript"], [])
            ]);

        var validation = IncidentEventStateMicroBatchProposalValidator.Validate(batch, lookup, prompt, proposal);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void ValidatorRejectsLaterTargetsAndCrossObservationEvidence()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(2, 60, 0, 0));
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var batch = plan.Batches[0];
        var prompt = IncidentEventStateMicroBatchPrompt.Build(batch, lookup);
        var proposal = new IncidentEventStateMicroBatchProposal(
            "model",
            IncidentEventStateMicroBatchPrompt.PromptIdentity,
            [
                new IncidentEventStateMicroBatchObservationDecision(
                    "new-1", IncidentEventStateMicroBatchDecision.ProposeLink, "new-2",
                    "unsupported", 0.2,
                    ["call:2:transcript"], ["call:2:transcript"], []),
                new IncidentEventStateMicroBatchObservationDecision(
                    "new-2", IncidentEventStateMicroBatchDecision.Unresolved, string.Empty,
                    string.Empty, 0.8, [], [], ["unknown"])
            ]);

        var validation = IncidentEventStateMicroBatchProposalValidator.Validate(batch, lookup, prompt, proposal);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("not earlier", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("another observation", StringComparison.Ordinal));
    }

    [Fact]
    public void UnresolvedDecisionNeedsNoInventedExplanationOrEvidence()
    {
        var observations = new[] { Observation("call:1", 100) };
        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(1, 60, 0, 0));
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var batch = plan.Batches[0];
        var prompt = IncidentEventStateMicroBatchPrompt.Build(batch, lookup);
        var decision = new IncidentEventStateMicroBatchObservationDecision(
            "new-1", IncidentEventStateMicroBatchDecision.Unresolved, string.Empty,
            string.Empty, 0.5, [], [], []);

        var validation = IncidentEventStateMicroBatchProposalValidator.ValidateDecision(
            batch,
            lookup,
            prompt,
            decision);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void CandidateRetrievalIsStructurallySeparateFromMembershipDecision()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(2, 60, 0, 0));
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var batch = plan.Batches[0];
        var prompt = IncidentEventStateMicroBatchCandidatePrompt.Build(batch, lookup);
        var proposal = new IncidentEventStateMicroBatchCandidateProposal(
            "retriever-model",
            IncidentEventStateMicroBatchCandidatePrompt.PromptIdentity,
            [new IncidentEventStateMicroBatchCandidate("new-2", "new-1", "worth final comparison")]);

        var validation = IncidentEventStateMicroBatchCandidateValidator.Validate(batch, prompt, proposal);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        Assert.Contains("not an incident-membership decision", prompt.UserPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CandidateRetrievalRejectsFutureTargetsAndDuplicatePairs()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
            "replay-1",
            DateTimeOffset.Parse("2026-07-21T13:00:00Z"),
            observations,
            new IncidentEventStateMicroBatchReplayOptions(2, 60, 0, 0));
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var batch = plan.Batches[0];
        var prompt = IncidentEventStateMicroBatchCandidatePrompt.Build(batch, lookup);
        var proposal = new IncidentEventStateMicroBatchCandidateProposal(
            "retriever-model",
            IncidentEventStateMicroBatchCandidatePrompt.PromptIdentity,
            [
                new IncidentEventStateMicroBatchCandidate("new-1", "new-2", string.Empty),
                new IncidentEventStateMicroBatchCandidate("new-1", "new-2", string.Empty)
            ]);

        var validation = IncidentEventStateMicroBatchCandidateValidator.Validate(batch, prompt, proposal);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("not earlier", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("duplicated", StringComparison.Ordinal));
    }

    [Fact]
    public void GroupedVerifierRequiresIndependentGroundedEvidenceForApprovedLinks()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var candidates = new[]
        {
            new IncidentEventStateMicroBatchCandidate("new-2", "new-1", "retrieval is not proof")
        };
        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["new-1"] = "call:1",
            ["new-2"] = "call:2"
        };
        var prompt = IncidentEventStateMicroBatchVerificationPrompt.Build(candidates, tokenMap, lookup);
        var proposal = new IncidentEventStateMicroBatchVerificationProposal(
            "verifier-model",
            IncidentEventStateMicroBatchVerificationPrompt.PromptIdentity,
            [
                new IncidentEventStateMicroBatchVerificationDecision(
                    "candidate-1",
                    IncidentEventStateMicroBatchVerificationDecisionKind.VerifyLink,
                    "continued exchange",
                    0.1,
                    ["call:2:transcript"],
                    ["call:1:transcript"],
                    [])
            ]);

        var validation = IncidentEventStateMicroBatchVerificationValidator.Validate(prompt, lookup, proposal);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        Assert.Contains("candidate generator is retrieval only", prompt.UserPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GroupedVerifierRejectsEvidenceFromWrongEndpoint()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var candidates = new[]
        {
            new IncidentEventStateMicroBatchCandidate("new-2", "new-1", string.Empty)
        };
        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["new-1"] = "call:1",
            ["new-2"] = "call:2"
        };
        var prompt = IncidentEventStateMicroBatchVerificationPrompt.Build(candidates, tokenMap, lookup);
        var proposal = new IncidentEventStateMicroBatchVerificationProposal(
            "verifier-model",
            IncidentEventStateMicroBatchVerificationPrompt.PromptIdentity,
            [
                new IncidentEventStateMicroBatchVerificationDecision(
                    "candidate-1",
                    IncidentEventStateMicroBatchVerificationDecisionKind.VerifyLink,
                    "unsupported",
                    0.1,
                    ["call:1:transcript"],
                    ["call:2:transcript"],
                    [])
            ]);

        var validation = IncidentEventStateMicroBatchVerificationValidator.Validate(prompt, lookup, proposal);

        Assert.False(validation.IsValid);
        Assert.Equal(2, validation.Errors.Count(error => error.Contains("another observation", StringComparison.Ordinal)));
    }

    [Fact]
    public void CandidateBackedPromptKeepsOnlyRetrievedTargetsInChronologicalContext()
    {
        var observations = new[]
        {
            Observation("call:1", 100),
            Observation("call:2", 101),
            Observation("call:3", 102)
        };
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["new-1"] = "call:1",
            ["new-2"] = "call:2",
            ["new-3"] = "call:3"
        };

        var plan = IncidentEventStateCandidateBackedMicroBatch.Build(
            "candidate-backed",
            1,
            [
                new IncidentEventStateMicroBatchCandidate("new-3", "new-1", string.Empty),
                new IncidentEventStateMicroBatchCandidate("new-3", "new-2", string.Empty)
            ],
            tokenMap,
            lookup);

        Assert.Equal(["call:3"], plan.Batch.NewObservationIds);
        Assert.Equal(["call:1", "call:2"], plan.Batch.ContextObservationIds);
        Assert.Equal(2, plan.CandidateLinks.Count);
    }

    [Fact]
    public void ExhaustiveCandidatesEnumerateEveryEarlierObservationWithoutMetadataRules()
    {
        var observations = Enumerable.Range(1, 5)
            .Select(index => Observation($"call:{index}", 100 + index))
            .ToList();
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var batch = new IncidentEventStateMicroBatchReplayBatch(
            "exhaustive",
            1,
            101,
            105,
            ["call:3", "call:4", "call:5"],
            ["call:1", "call:2"],
            "hash");
        var prompt = IncidentEventStateMicroBatchCandidatePrompt.Build(batch, lookup);

        var candidates = IncidentEventStateMicroBatchExhaustiveCandidates.Build(batch, prompt);

        Assert.Equal(9, candidates.Count);
        Assert.Equal(
            [
                ("new-1", "context-1"), ("new-1", "context-2"),
                ("new-2", "context-1"), ("new-2", "context-2"), ("new-2", "new-1"),
                ("new-3", "context-1"), ("new-3", "context-2"), ("new-3", "new-1"), ("new-3", "new-2")
            ],
            candidates.Select(candidate => (candidate.NewObservationToken, candidate.TargetObservationToken)));
        Assert.All(candidates, candidate => Assert.Equal(string.Empty, candidate.ReasonToCompare));
    }

    [Fact]
    public void CandidateBackedValidationRejectsAChronologicalButUnretrievedTarget()
    {
        var observations = new[]
        {
            Observation("call:1", 100),
            Observation("call:2", 101),
            Observation("call:3", 102)
        };
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["new-1"] = "call:1",
            ["new-2"] = "call:2",
            ["new-3"] = "call:3"
        };
        var plan = IncidentEventStateCandidateBackedMicroBatch.Build(
            "candidate-backed",
            1,
            [
                new IncidentEventStateMicroBatchCandidate("new-2", "new-1", string.Empty),
                new IncidentEventStateMicroBatchCandidate("new-3", "new-1", string.Empty)
            ],
            tokenMap,
            lookup);
        var decision = new IncidentEventStateMicroBatchObservationDecision(
            "new-2",
            IncidentEventStateMicroBatchDecision.ProposeLink,
            "new-1",
            "continued exchange",
            0.2,
            ["call:3:transcript"],
            ["call:2:transcript"],
            []);

        var validation = IncidentEventStateCandidateBackedMicroBatch.ValidateDecision(plan, lookup, decision);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("outside retrieved candidates", StringComparison.Ordinal));
    }

    [Fact]
    public void SparseLinkPromptContainsCandidatesAndTranscriptEvidenceOnly()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var plan = IncidentEventStateCandidateBackedMicroBatch.Build(
            "sparse",
            1,
            [new IncidentEventStateMicroBatchCandidate("new-2", "new-1", string.Empty)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["new-1"] = "call:1",
                ["new-2"] = "call:2"
            },
            lookup);

        var prompt = IncidentEventStateSparseLinkPrompt.Build(plan, lookup);

        Assert.Contains("candidate-1", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("call:1:transcript", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("forbidden-category", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("forbidden-talkgroup", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved", prompt.UserPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SparseLinkValidatorAcceptsCompleteEmptyEnvelopeAndGroundedLink()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var plan = IncidentEventStateCandidateBackedMicroBatch.Build(
            "sparse",
            1,
            [new IncidentEventStateMicroBatchCandidate("new-2", "new-1", string.Empty)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["new-1"] = "call:1",
                ["new-2"] = "call:2"
            },
            lookup);
        var empty = new IncidentEventStateSparseLinkEnvelope(
            "model",
            IncidentEventStateSparseLinkPrompt.PromptIdentity,
            true,
            []);
        var linked = empty with
        {
            Links =
            [
                new IncidentEventStateSparseLinkProposal(
                    "candidate-1",
                    "the second transmission explicitly continues the first",
                    0.1,
                    ["call:2:transcript"],
                    ["call:1:transcript"])
            ]
        };

        Assert.True(IncidentEventStateSparseLinkValidator.Validate(plan, lookup, empty).IsValid);
        Assert.True(IncidentEventStateSparseLinkValidator.Validate(plan, lookup, linked).IsValid);
    }

    [Fact]
    public void SparseLinkValidatorFailsClosedOnDuplicateOrForeignEvidence()
    {
        var observations = new[] { Observation("call:1", 100), Observation("call:2", 101) };
        var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var plan = IncidentEventStateCandidateBackedMicroBatch.Build(
            "sparse",
            1,
            [new IncidentEventStateMicroBatchCandidate("new-2", "new-1", string.Empty)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["new-1"] = "call:1",
                ["new-2"] = "call:2"
            },
            lookup);
        var badLink = new IncidentEventStateSparseLinkProposal(
            "candidate-1",
            "unsupported",
            0.1,
            ["call:1:transcript"],
            ["call:2:transcript"]);
        var envelope = new IncidentEventStateSparseLinkEnvelope(
            "model",
            IncidentEventStateSparseLinkPrompt.PromptIdentity,
            true,
            [badLink, badLink]);

        var validation = IncidentEventStateSparseLinkValidator.Validate(plan, lookup, envelope);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("more than once", StringComparison.Ordinal));
        Assert.Equal(4, validation.Errors.Count(error => error.Contains("another observation", StringComparison.Ordinal)));
    }

    private static IncidentEventStateSourceObservation Observation(string id, long observedAt) => new(
        id,
        long.Parse(id.AsSpan("call:".Length)),
        observedAt,
        string.Empty,
        1000,
        [new IncidentEventStateTranscriptObservation($"{id}:transcript", $"transcript for {id}", "test", null)],
        new Dictionary<string, IncidentEventStateMetadataObservation>(StringComparer.Ordinal)
        {
            ["category"] = new("forbidden-category", IncidentEventStateMetadataOrigin.ApplicationDerived),
            ["talkgroupName"] = new("forbidden-talkgroup", IncidentEventStateMetadataOrigin.SourceRecord)
        });
}
