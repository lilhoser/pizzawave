namespace pizzad.Tests;

public sealed class IncidentEventStateObservationRelationshipTests
{
    [Fact]
    public void ValidatorAcceptsSourceGroundedRelationshipWithoutCreatingAnEvent()
    {
        var result = IncidentEventStateContractValidator.ValidateObservationRelationshipProposal(
            Bundle(),
            Proposal());

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidatorRejectsRelationshipThatDoesNotCiteBothObservations()
    {
        var invalid = Proposal() with
        {
            PossibleRelationships =
            [
                Proposal().PossibleRelationships[0] with
                {
                    Provenance = [Provenance("observation-1", "transcript-1", "Pond Express")]
                }
            ]
        };

        var result = IncidentEventStateContractValidator.ValidateObservationRelationshipProposal(
            Bundle(),
            invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("must cite both compared observations", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsCritiqueThatReferencesWrongProposal()
    {
        var invalid = Critique() with { ProposalId = "wrong-proposal" };

        var result = IncidentEventStateContractValidator.ValidateObservationRelationshipCritique(
            Bundle(),
            Proposal(),
            invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("does not match the proposal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CoordinatorRunsBoundedProposerAndCriticWithoutPersistence()
    {
        var bundle = Bundle();
        var proposer = new RecordingProposer(Proposal());
        var critic = new RecordingCritic(Critique());
        var coordinator = new IncidentEventStateObservationRelationshipCoordinator(proposer, critic);

        var result = await coordinator.RunAsync(
            new IncidentEventStateObservationRelationshipRunRequest(
                ["observation-1", "observation-2"],
                "software-v1",
                "configuration-v1"),
            bundle,
            CancellationToken.None);

        Assert.Same(bundle, proposer.Bundle);
        Assert.Equal(["observation-1", "observation-2"], proposer.ObservationIds);
        Assert.Same(bundle, critic.Bundle);
        Assert.Same(proposer.Result, critic.Proposal);
        Assert.Same(proposer.Result, result.Proposal);
        Assert.Same(critic.Result, result.Critique);
        Assert.Equal("software-v1", result.Execution.SoftwareVersion);
        Assert.Equal("configuration-v1", result.Execution.ConfigurationIdentity);
    }

    [Fact]
    public async Task InvalidProposalNeverReachesCritic()
    {
        var invalid = Proposal() with { BundleId = "wrong-bundle" };
        var critic = new RecordingCritic(Critique());
        var coordinator = new IncidentEventStateObservationRelationshipCoordinator(
            new RecordingProposer(invalid),
            critic);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.RunAsync(
            new IncidentEventStateObservationRelationshipRunRequest(
                ["observation-1", "observation-2"],
                "software-v1",
                "configuration-v1"),
            Bundle(),
            CancellationToken.None));

        Assert.Contains("bundle id does not match", error.Message, StringComparison.Ordinal);
        Assert.Null(critic.Bundle);
    }

    [Fact]
    public async Task UnknownObservationNeverReachesProposer()
    {
        var proposer = new RecordingProposer(Proposal());
        var coordinator = new IncidentEventStateObservationRelationshipCoordinator(
            proposer,
            new RecordingCritic(Critique()));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => coordinator.RunAsync(
            new IncidentEventStateObservationRelationshipRunRequest(
                ["observation-1", "missing-observation"],
                "software-v1",
                "configuration-v1"),
            Bundle(),
            CancellationToken.None));

        Assert.Contains("missing-observation", error.Message, StringComparison.Ordinal);
        Assert.Null(proposer.Bundle);
    }

    private static IncidentEventStateObservationBundle Bundle() =>
        new(
            "bundle-1",
            DateTimeOffset.Parse("2026-07-19T12:00:00Z"),
            [
                new IncidentEventStateSourceObservation(
                    "observation-1",
                    25113,
                    1783941035,
                    "audio/25113.wav",
                    12000,
                    [
                        new IncidentEventStateTranscriptObservation(
                            "transcript-1",
                            "Pond Express, Tupelo, Mississippi.",
                            "source-a",
                            null)
                    ],
                    new Dictionary<string, IncidentEventStateMetadataObservation>()),
                new IncidentEventStateSourceObservation(
                    "observation-2",
                    25115,
                    1783941056,
                    "audio/25115.wav",
                    1000,
                    [
                        new IncidentEventStateTranscriptObservation(
                            "transcript-2",
                            "So.",
                            "source-a",
                            null)
                    ],
                    new Dictionary<string, IncidentEventStateMetadataObservation>())
            ],
            []);

    private static IncidentEventStateObservationRelationshipProposal Proposal() =>
        new(
            "relationship-proposal-1",
            "bundle-1",
            ["observation-1", "observation-2"],
            DateTimeOffset.Parse("2026-07-19T12:01:00Z"),
            "relationship-model",
            "relationship-prompt",
            [
                new IncidentEventStateObservationRelationshipStatement(
                    "relationship-1",
                    "The short reply may follow the preceding transmission, but its content does not establish why.",
                    0.9,
                    [
                        Provenance("observation-1", "transcript-1", "Pond Express"),
                        Provenance("observation-2", "transcript-2", "So.")
                    ])
            ],
            [],
            ["Does the second transmission respond to the first?"]);

    private static IncidentEventStateObservationRelationshipCritique Critique() =>
        new(
            "relationship-critique-1",
            "relationship-proposal-1",
            DateTimeOffset.Parse("2026-07-19T12:02:00Z"),
            "relationship-critic-model",
            "relationship-critic-prompt",
            "The proposed relationship remains unresolved.",
            [
                new IncidentEventStateCritiqueFinding(
                    "finding-1",
                    "The words alone do not establish a response relationship.",
                    0.1,
                    [
                        Provenance("observation-1", "transcript-1", "Pond Express"),
                        Provenance("observation-2", "transcript-2", "So.")
                    ])
            ]);

    private static IncidentEventStateProvenance Provenance(
        string observationId,
        string transcriptId,
        string quote) =>
        new(observationId, transcriptId, quote, null, null, string.Empty);

    private sealed class RecordingProposer(IncidentEventStateObservationRelationshipProposal result)
        : IIncidentEventStateObservationRelationshipProposer
    {
        public IncidentEventStateObservationBundle? Bundle { get; private set; }
        public IReadOnlyList<string>? ObservationIds { get; private set; }
        public IncidentEventStateObservationRelationshipProposal Result { get; } = result;

        public Task<IncidentEventStateObservationRelationshipProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<string> observationIds,
            CancellationToken ct)
        {
            Bundle = bundle;
            ObservationIds = observationIds;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingCritic(IncidentEventStateObservationRelationshipCritique result)
        : IIncidentEventStateObservationRelationshipCritic
    {
        public IncidentEventStateObservationBundle? Bundle { get; private set; }
        public IncidentEventStateObservationRelationshipProposal? Proposal { get; private set; }
        public IncidentEventStateObservationRelationshipCritique Result { get; } = result;

        public Task<IncidentEventStateObservationRelationshipCritique> CritiqueAsync(
            IncidentEventStateObservationBundle bundle,
            IncidentEventStateObservationRelationshipProposal proposal,
            CancellationToken ct)
        {
            Bundle = bundle;
            Proposal = proposal;
            return Task.FromResult(Result);
        }
    }
}
