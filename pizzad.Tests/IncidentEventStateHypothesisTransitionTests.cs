namespace pizzad.Tests;

public sealed class IncidentEventStateHypothesisTransitionTests
{
    [Fact]
    public void ValidatorAcceptsRevisionGroundedByBoundedRelationshipEvidence()
    {
        var result = IncidentEventStateContractValidator.ValidateHypothesisTransitionProposal(
            Bundle(),
            [Evidence(withProposalRelationship: true, withCriticFinding: false)],
            Proposal(["relationship-evidence-1"]));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidatorRejectsCriticFindingAsMembershipAuthorityWhenProposerMissedRelationship()
    {
        var result = IncidentEventStateContractValidator.ValidateHypothesisTransitionProposal(
            Bundle(),
            [Evidence(withProposalRelationship: false, withCriticFinding: true)],
            Proposal(["relationship-evidence-1"]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("without a validated relationship proposal", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsObservationOutsidePriorStateAndCitedEvidence()
    {
        var invalid = Proposal(["relationship-evidence-1"]) with
        {
            Revisions =
            [
                Proposal(["relationship-evidence-1"]).Revisions[0] with
                {
                    Hypothesis = Proposal(["relationship-evidence-1"]).Revisions[0].Hypothesis with
                    {
                        ObservationIds = ["observation-1", "observation-3"]
                    }
                }
            ]
        };

        var result = IncidentEventStateContractValidator.ValidateHypothesisTransitionProposal(
            Bundle(),
            [Evidence(withProposalRelationship: true, withCriticFinding: false)],
            invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("observation-3", StringComparison.Ordinal) &&
            error.Contains("without cited relationship evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsMembershipGrowthFromEmptyModelAgreement()
    {
        var result = IncidentEventStateContractValidator.ValidateHypothesisTransitionProposal(
            Bundle(),
            [Evidence(withProposalRelationship: false, withCriticFinding: false)],
            Proposal(["relationship-evidence-1"]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("without a validated relationship proposal", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorDoesNotLetOnePositivePairAuthorizeAnUnrelatedObservation()
    {
        var unrelated = Evidence(withProposalRelationship: false, withCriticFinding: false) with
        {
            EvidenceId = "relationship-evidence-2",
            Proposal = Evidence(false, false).Proposal with
            {
                ProposalId = "relationship-proposal-2",
                ObservationIds = ["observation-2", "observation-3"]
            },
            Critique = Evidence(false, false).Critique with
            {
                CritiqueId = "relationship-critique-2",
                ProposalId = "relationship-proposal-2"
            }
        };
        var invalid = Proposal(["relationship-evidence-1", "relationship-evidence-2"]) with
        {
            Revisions =
            [
                Proposal(["relationship-evidence-1", "relationship-evidence-2"]).Revisions[0] with
                {
                    Hypothesis = Proposal(["relationship-evidence-1", "relationship-evidence-2"]).Revisions[0].Hypothesis with
                    {
                        ObservationIds = ["observation-1", "observation-3"]
                    }
                }
            ]
        };

        var result = IncidentEventStateContractValidator.ValidateHypothesisTransitionProposal(
            Bundle(),
            [Evidence(withProposalRelationship: true, withCriticFinding: false), unrelated],
            invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("observation-3", StringComparison.Ordinal) &&
            error.Contains("without cited relationship evidence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CoordinatorRunsProposerAndCriticWithoutPersistence()
    {
        var bundle = Bundle();
        var evidence = new[] { Evidence(withProposalRelationship: true, withCriticFinding: false) };
        var proposer = new RecordingProposer(Proposal(["relationship-evidence-1"]));
        var critic = new RecordingCritic(Critique());
        var coordinator = new IncidentEventStateHypothesisTransitionCoordinator(proposer, critic);

        var result = await coordinator.RunAsync(
            new IncidentEventStateHypothesisTransitionRunRequest("software-v1", "configuration-v1"),
            bundle,
            evidence,
            CancellationToken.None);

        Assert.Same(bundle, proposer.Bundle);
        Assert.Same(evidence, proposer.Evidence);
        Assert.Same(proposer.Result, critic.Proposal);
        Assert.Same(critic.Result, result.Critique);
        Assert.Equal("software-v1", result.Execution.SoftwareVersion);
        Assert.Equal("configuration-v1", result.Execution.ConfigurationIdentity);
    }

    private static IncidentEventStateObservationBundle Bundle() =>
        new(
            "bundle-1",
            DateTimeOffset.Parse("2026-07-19T12:00:00Z"),
            [
                Observation("observation-1", 1, "Traffic 469?", "transcript-1"),
                Observation("observation-2", 2, "Repeat 469, you are breaking up.", "transcript-2"),
                Observation("observation-3", 3, "Unrelated traffic.", "transcript-3")
            ],
            [
                new IncidentEventStateProjectedEvent(
                    "hypothesis-prior",
                    "Traffic involving identifier 469 may be active.",
                    0.7,
                    ["observation-1"],
                    [],
                    [],
                    ["What does 469 identify?"],
                    ["ledger-prior"])
            ]);

    private static IncidentEventStateSourceObservation Observation(
        string observationId,
        long callId,
        string text,
        string transcriptId) =>
        new(
            observationId,
            callId,
            1783941000 + callId,
            $"audio/{callId}.wav",
            1000,
            [new IncidentEventStateTranscriptObservation(transcriptId, text, "source-a", null)],
            new Dictionary<string, IncidentEventStateMetadataObservation>());

    private static IncidentEventStateObservationRelationshipEvidence Evidence(
        bool withProposalRelationship,
        bool withCriticFinding)
    {
        var proposal = new IncidentEventStateObservationRelationshipProposal(
            "relationship-proposal-1",
            "bundle-1",
            ["observation-1", "observation-2"],
            DateTimeOffset.Parse("2026-07-19T12:01:00Z"),
            "relationship-model",
            "relationship-prompt",
            withProposalRelationship
                ? [RelationshipStatement()]
                : [],
            [],
            []);
        var critique = new IncidentEventStateObservationRelationshipCritique(
            "relationship-critique-1",
            proposal.ProposalId,
            DateTimeOffset.Parse("2026-07-19T12:02:00Z"),
            "critic-model",
            "critic-prompt",
            withCriticFinding ? "The proposer missed repeated identifier 469." : "No finding.",
            withCriticFinding
                ?
                [
                    new IncidentEventStateCritiqueFinding(
                        "finding-1",
                        "Both observations repeat identifier 469.",
                        0.2,
                        BothProvenance())
                ]
                : []);
        return new IncidentEventStateObservationRelationshipEvidence(
            "relationship-evidence-1",
            proposal,
            critique);
    }

    private static IncidentEventStateObservationRelationshipStatement RelationshipStatement() =>
        new(
            "relationship-1",
            "Both observations repeat identifier 469.",
            0.2,
            BothProvenance());

    private static IncidentEventStateHypothesisTransitionProposal Proposal(
        IReadOnlyList<string> evidenceIds) =>
        new(
            "transition-proposal-1",
            "bundle-1",
            DateTimeOffset.Parse("2026-07-19T12:03:00Z"),
            "transition-model",
            "transition-prompt",
            [
                new IncidentEventStateHypothesisRevision(
                    "revision-1",
                    ["hypothesis-prior"],
                    new IncidentEventStateHypothesis(
                        "hypothesis-revised",
                        "Traffic involving identifier 469 may continue.",
                        0.4,
                        ["observation-1", "observation-2"],
                        [],
                        [
                            new IncidentEventStateRelationship(
                                "relationship-1",
                                "Both observations repeat identifier 469.",
                                0.2,
                                ["observation-1", "observation-2"],
                                BothProvenance())
                        ],
                        [],
                        ["What does 469 identify?"]),
                    evidenceIds,
                    ["What does 469 identify?"])
            ]);

    private static IncidentEventStateHypothesisTransitionCritique Critique() =>
        new(
            "transition-critique-1",
            "transition-proposal-1",
            DateTimeOffset.Parse("2026-07-19T12:04:00Z"),
            "transition-critic-model",
            "transition-critic-prompt",
            "The revision preserves uncertainty.",
            []);

    private static IReadOnlyList<IncidentEventStateProvenance> BothProvenance() =>
        [
            new("observation-1", "transcript-1", "469", null, null, string.Empty),
            new("observation-2", "transcript-2", "469", null, null, string.Empty)
        ];

    private sealed class RecordingProposer(IncidentEventStateHypothesisTransitionProposal result)
        : IIncidentEventStateHypothesisTransitionProposer
    {
        public IncidentEventStateObservationBundle? Bundle { get; private set; }
        public IReadOnlyList<IncidentEventStateObservationRelationshipEvidence>? Evidence { get; private set; }
        public IncidentEventStateHypothesisTransitionProposal Result { get; } = result;

        public Task<IncidentEventStateHypothesisTransitionProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<IncidentEventStateObservationRelationshipEvidence> evidence,
            CancellationToken ct)
        {
            Bundle = bundle;
            Evidence = evidence;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingCritic(IncidentEventStateHypothesisTransitionCritique result)
        : IIncidentEventStateHypothesisTransitionCritic
    {
        public IncidentEventStateHypothesisTransitionProposal? Proposal { get; private set; }
        public IncidentEventStateHypothesisTransitionCritique Result { get; } = result;

        public Task<IncidentEventStateHypothesisTransitionCritique> CritiqueAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<IncidentEventStateObservationRelationshipEvidence> evidence,
            IncidentEventStateHypothesisTransitionProposal proposal,
            CancellationToken ct)
        {
            Proposal = proposal;
            return Task.FromResult(Result);
        }
    }
}
