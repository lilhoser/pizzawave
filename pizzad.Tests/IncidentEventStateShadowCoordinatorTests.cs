namespace pizzad.Tests;

public sealed class IncidentEventStateShadowCoordinatorTests
{
    [Fact]
    public async Task RunsSeparateProposerAndCriticBoundariesAndAppendsOnlyValidatedEntry()
    {
        var bundle = Bundle();
        var proposer = new RecordingProposer(Proposal());
        var critic = new RecordingCritic(Critique());
        var store = new RecordingStore();
        var coordinator = new IncidentEventStateShadowCoordinator(
            proposer,
            critic,
            store,
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-17T15:03:00Z")));

        var stored = await coordinator.RunAsync(
            new IncidentEventStateShadowRunRequest("ledger-1", "software-v1", "configuration-v1"),
            bundle,
            CancellationToken.None);

        Assert.Same(bundle, proposer.Bundle);
        Assert.Same(bundle, critic.Bundle);
        Assert.Same(proposer.Result, critic.Proposal);
        Assert.Same(store.Entry, stored.Entry);
        Assert.Equal("software-v1", stored.Entry.Execution.SoftwareVersion);
        Assert.Equal("configuration-v1", stored.Entry.Execution.ConfigurationIdentity);
        Assert.Equal(DateTimeOffset.Parse("2026-07-17T15:03:00Z"), stored.Entry.RecordedAtUtc);
    }

    [Fact]
    public async Task InvalidProposalNeverReachesCriticOrStore()
    {
        var invalidProposal = Proposal() with { BundleId = "wrong-bundle" };
        var critic = new RecordingCritic(Critique());
        var store = new RecordingStore();
        var coordinator = new IncidentEventStateShadowCoordinator(
            new RecordingProposer(invalidProposal),
            critic,
            store);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.RunAsync(
            new IncidentEventStateShadowRunRequest("ledger-1", "software-v1", "configuration-v1"),
            Bundle(),
            CancellationToken.None));

        Assert.Contains("bundle id does not match", error.Message, StringComparison.Ordinal);
        Assert.Null(critic.Bundle);
        Assert.Null(store.Entry);
    }

    private static IncidentEventStateObservationBundle Bundle() =>
        new(
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T15:00:00Z"),
            [
                new IncidentEventStateSourceObservation(
                    "observation-1",
                    1,
                    100,
                    "audio/1.wav",
                    5000,
                    [
                        new IncidentEventStateTranscriptObservation(
                            "transcript-1",
                            "An observer described an unresolved event.",
                            "source",
                            null)
                    ],
                    new Dictionary<string, IncidentEventStateMetadataObservation>())
            ],
            []);

    private static IncidentEventStateProposal Proposal() =>
        new(
            "proposal-1",
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T15:01:00Z"),
            "proposer-model",
            "proposer-prompt",
            [
                new IncidentEventStateHypothesis(
                    "hypothesis-1",
                    "The evidence may describe an unresolved event.",
                    0.5,
                    ["observation-1"],
                    [
                        new IncidentEventStateClaim(
                            "claim-1",
                            "An observer described an unresolved event.",
                            0.1,
                            [Provenance()])
                    ],
                    [],
                    [],
                    ["What occurred remains unresolved."])
            ],
            []);

    private static IncidentEventStateCritique Critique() =>
        new(
            "critique-1",
            "proposal-1",
            DateTimeOffset.Parse("2026-07-17T15:02:00Z"),
            "critic-model",
            "critic-prompt",
            "The proposal preserves the unresolved state.",
            [
                new IncidentEventStateCritiqueFinding(
                    "finding-1",
                    "The source does not establish what occurred.",
                    0.2,
                    [Provenance()])
            ]);

    private static IncidentEventStateProvenance Provenance() =>
        new(
            "observation-1",
            "transcript-1",
            "unresolved event",
            null,
            null,
            string.Empty);

    private sealed class RecordingProposer(IncidentEventStateProposal result) : IIncidentEventStateProposer
    {
        public IncidentEventStateObservationBundle? Bundle { get; private set; }
        public IncidentEventStateProposal Result { get; } = result;

        public Task<IncidentEventStateProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            CancellationToken ct)
        {
            Bundle = bundle;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingCritic(IncidentEventStateCritique result) : IIncidentEventStateCritic
    {
        public IncidentEventStateObservationBundle? Bundle { get; private set; }
        public IncidentEventStateProposal? Proposal { get; private set; }

        public Task<IncidentEventStateCritique> CritiqueAsync(
            IncidentEventStateObservationBundle bundle,
            IncidentEventStateProposal proposal,
            CancellationToken ct)
        {
            Bundle = bundle;
            Proposal = proposal;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingStore : IIncidentEventStateShadowStore
    {
        public IncidentEventStateLedgerEntry? Entry { get; private set; }

        public Task<IncidentEventStateStoredLedgerEntry> AppendIncidentEventStateShadowLedgerEntryAsync(
            IncidentEventStateLedgerEntry entry,
            CancellationToken ct)
        {
            Entry = entry;
            return Task.FromResult(new IncidentEventStateStoredLedgerEntry(1, new string('A', 64), entry));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
