namespace pizzad.Tests;

public sealed class IncidentEventStateObservationInterpretationCoordinatorTests
{
    [Fact]
    public async Task RunsInterpreterAndCriticWithoutCreatingOrPersistingAnEvent()
    {
        var bundle = Bundle();
        var interpreter = new RecordingInterpreter(Interpretation());
        var critic = new RecordingCritic(Critique());
        var coordinator = new IncidentEventStateObservationInterpretationCoordinator(interpreter, critic);

        var result = await coordinator.RunAsync(
            new IncidentEventStateObservationInterpretationRunRequest(
                "observation-1",
                "software-v1",
                "configuration-v1"),
            bundle,
            CancellationToken.None);

        Assert.Same(bundle, interpreter.Bundle);
        Assert.Equal("observation-1", interpreter.ObservationId);
        Assert.Same(bundle, critic.Bundle);
        Assert.Same(interpreter.Result, critic.Interpretation);
        Assert.Same(interpreter.Result, result.Interpretation);
        Assert.Same(critic.Result, result.Critique);
        Assert.Equal("bundle-1", result.BundleId);
        Assert.Equal("software-v1", result.Execution.SoftwareVersion);
        Assert.Equal("configuration-v1", result.Execution.ConfigurationIdentity);
    }

    [Fact]
    public async Task InvalidInterpretationNeverReachesCritic()
    {
        var invalidInterpretation = Interpretation() with { ObservationId = "missing-observation" };
        var critic = new RecordingCritic(Critique());
        var coordinator = new IncidentEventStateObservationInterpretationCoordinator(
            new RecordingInterpreter(invalidInterpretation),
            critic);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.RunAsync(
            new IncidentEventStateObservationInterpretationRunRequest(
                "observation-1",
                "software-v1",
                "configuration-v1"),
            Bundle(),
            CancellationToken.None));

        Assert.Contains("missing-observation", error.Message, StringComparison.Ordinal);
        Assert.Null(critic.Bundle);
    }

    [Fact]
    public async Task InvalidCritiqueIsRejectedFromTheResult()
    {
        var invalidCritique = Critique() with { InterpretationId = "wrong-interpretation" };
        var coordinator = new IncidentEventStateObservationInterpretationCoordinator(
            new RecordingInterpreter(Interpretation()),
            new RecordingCritic(invalidCritique));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.RunAsync(
            new IncidentEventStateObservationInterpretationRunRequest(
                "observation-1",
                "software-v1",
                "configuration-v1"),
            Bundle(),
            CancellationToken.None));

        Assert.Contains("critique id reference does not match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestForUnknownObservationNeverReachesInterpreter()
    {
        var interpreter = new RecordingInterpreter(Interpretation());
        var coordinator = new IncidentEventStateObservationInterpretationCoordinator(
            interpreter,
            new RecordingCritic(Critique()));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => coordinator.RunAsync(
            new IncidentEventStateObservationInterpretationRunRequest(
                "missing-observation",
                "software-v1",
                "configuration-v1"),
            Bundle(),
            CancellationToken.None));

        Assert.Contains("does not exist in bundle", error.Message, StringComparison.Ordinal);
        Assert.Null(interpreter.Bundle);
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
                            "Pond Express, Tupelo, Mississippi.",
                            "source-a",
                            null),
                        new IncidentEventStateTranscriptObservation(
                            "transcript-2",
                            "Pawn Express, Tipolo, Mississippi.",
                            "source-b",
                            null)
                    ],
                    new Dictionary<string, IncidentEventStateMetadataObservation>())
            ],
            []);

    private static IncidentEventStateObservationInterpretation Interpretation() =>
        new(
            "interpretation-1",
            "observation-1",
            DateTimeOffset.Parse("2026-07-17T15:01:00Z"),
            "interpreter-model",
            "interpreter-prompt",
            [
                new IncidentEventStateObservationInterpretationStatement(
                    "reading-1",
                    "The place name may be Pond Express in Tupelo, Mississippi.",
                    0.5,
                    [Provenance("transcript-1", "Pond Express, Tupelo, Mississippi")]),
                new IncidentEventStateObservationInterpretationStatement(
                    "reading-2",
                    "The place name may be Pawn Express in Tipolo, Mississippi.",
                    0.7,
                    [Provenance("transcript-2", "Pawn Express, Tipolo, Mississippi")])
            ],
            [],
            ["The place name and spelling are unresolved."]);

    private static IncidentEventStateObservationInterpretationCritique Critique() =>
        new(
            "interpretation-critique-1",
            "interpretation-1",
            DateTimeOffset.Parse("2026-07-17T15:02:00Z"),
            "critic-model",
            "critic-prompt",
            "The interpretation preserves both transcript readings.",
            [
                new IncidentEventStateCritiqueFinding(
                    "finding-1",
                    "Neither transcript establishes an incident or action.",
                    0.1,
                    [Provenance("transcript-1", "Pond Express")])
            ]);

    private static IncidentEventStateProvenance Provenance(string transcriptId, string quote) =>
        new("observation-1", transcriptId, quote, null, null, string.Empty);

    private sealed class RecordingInterpreter(IncidentEventStateObservationInterpretation result)
        : IIncidentEventStateObservationInterpreter
    {
        public IncidentEventStateObservationBundle? Bundle { get; private set; }
        public string? ObservationId { get; private set; }
        public IncidentEventStateObservationInterpretation Result { get; } = result;

        public Task<IncidentEventStateObservationInterpretation> InterpretAsync(
            IncidentEventStateObservationBundle bundle,
            string observationId,
            CancellationToken ct)
        {
            Bundle = bundle;
            ObservationId = observationId;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingCritic(IncidentEventStateObservationInterpretationCritique result)
        : IIncidentEventStateObservationInterpretationCritic
    {
        public IncidentEventStateObservationBundle? Bundle { get; private set; }
        public IncidentEventStateObservationInterpretation? Interpretation { get; private set; }
        public IncidentEventStateObservationInterpretationCritique Result { get; } = result;

        public Task<IncidentEventStateObservationInterpretationCritique> CritiqueAsync(
            IncidentEventStateObservationBundle bundle,
            IncidentEventStateObservationInterpretation interpretation,
            CancellationToken ct)
        {
            Bundle = bundle;
            Interpretation = interpretation;
            return Task.FromResult(Result);
        }
    }
}
