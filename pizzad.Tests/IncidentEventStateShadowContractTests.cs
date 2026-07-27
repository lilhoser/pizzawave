namespace pizzad.Tests;

public sealed class IncidentEventStateShadowContractTests
{
    [Fact]
    public void Validate_AcceptsOpenWorldClaimsWithVerifiableProvenance()
    {
        var bundle = Bundle();
        var result = IncidentEventStateContractValidator.Validate(bundle, Proposal(), Critique());

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_RejectsUnsupportedQuoteAndUnknownObservationReference()
    {
        var bundle = Bundle();
        var proposal = Proposal() with
        {
            Hypotheses =
            [
                Proposal().Hypotheses[0] with
                {
                    ObservationIds = ["observation-missing"],
                    Claims =
                    [
                        Proposal().Hypotheses[0].Claims[0] with
                        {
                            Provenance = [TranscriptProvenance("words that are not in the source")]
                        }
                    ]
                }
            ]
        };

        var result = IncidentEventStateContractValidator.Validate(bundle, proposal, Critique());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("unknown observation", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("exact quote does not occur", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TreatsArbitraryMetadataAsSourceDataWithoutAnOntology()
    {
        var bundle = Bundle() with
        {
            Observations =
            [
                Bundle().Observations[0] with
                {
                    Metadata = new Dictionary<string, IncidentEventStateMetadataObservation>
                    {
                        ["source-provided-field"] = new("unfamiliar value", IncidentEventStateMetadataOrigin.SourceRecord),
                        ["another-field"] = new("not mapped by application policy", IncidentEventStateMetadataOrigin.SourceRecord)
                    }
                }
            ]
        };
        var metadataProvenance = new IncidentEventStateProvenance(
            "observation-1",
            string.Empty,
            string.Empty,
            null,
            null,
            "source-provided-field");
        var proposal = Proposal() with
        {
            Hypotheses =
            [
                Proposal().Hypotheses[0] with
                {
                    Claims =
                    [
                        Proposal().Hypotheses[0].Claims[0] with
                        {
                            Provenance = [metadataProvenance]
                        }
                    ]
                }
            ]
        };

        var result = IncidentEventStateContractValidator.Validate(bundle, proposal, Critique());

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void ValidateObservationInterpretation_AcceptsCompetingReadingsWithoutCreatingAnEvent()
    {
        var result = IncidentEventStateContractValidator.ValidateObservationInterpretation(
            Bundle(),
            ObservationInterpretation());

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Empty(result.Errors);
        Assert.Equal(2, ObservationInterpretation().PossibleReadings.Count);
    }

    [Fact]
    public void ValidateObservationInterpretation_RejectsQuoteMismatchAndCrossObservationEvidence()
    {
        var secondObservation = Bundle().Observations[0] with
        {
            ObservationId = "observation-2",
            CallId = 67114,
            Transcripts =
            [
                new IncidentEventStateTranscriptObservation(
                    "transcript-2",
                    "A second observation contains different source material.",
                    "source-engine-2",
                    DateTimeOffset.Parse("2026-07-17T11:59:30Z"))
            ]
        };
        var bundle = Bundle() with { Observations = [.. Bundle().Observations, secondObservation] };
        var invalid = ObservationInterpretation() with
        {
            PossibleReadings =
            [
                ObservationInterpretation().PossibleReadings[0] with
                {
                    Provenance =
                    [
                        new IncidentEventStateProvenance(
                            "observation-2",
                            "transcript-2",
                            "words absent from the source",
                            null,
                            null,
                            string.Empty)
                    ]
                }
            ]
        };

        var result = IncidentEventStateContractValidator.ValidateObservationInterpretation(bundle, invalid);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("exact quote does not occur", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("outside interpreted observation", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateObservationInterpretationCritique_RejectsWrongInterpretationReference()
    {
        var critique = ObservationInterpretationCritique() with { InterpretationId = "interpretation-missing" };

        var result = IncidentEventStateContractValidator.ValidateObservationInterpretationCritique(
            Bundle(),
            ObservationInterpretation(),
            critique);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("does not match the interpretation", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Validate_RejectsInvalidUncertaintyWithoutInterpretingMeaning(double uncertainty)
    {
        var proposal = Proposal() with
        {
            Hypotheses = [Proposal().Hypotheses[0] with { Uncertainty = uncertainty }]
        };

        var result = IncidentEventStateContractValidator.Validate(Bundle(), proposal, Critique());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("uncertainty must be between 0 and 1", StringComparison.Ordinal));
    }

    private static IncidentEventStateObservationBundle Bundle() =>
        new(
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T12:00:00Z"),
            [
                new IncidentEventStateSourceObservation(
                    "observation-1",
                    67113,
                    1784289600,
                    "audio/call-67113.wav",
                    18_000,
                    [
                        new IncidentEventStateTranscriptObservation(
                            "transcript-1",
                            "The caller stated that he heard approximately ten loud sounds.",
                            "source-engine-1",
                            DateTimeOffset.Parse("2026-07-17T11:59:00Z"))
                    ],
                    new Dictionary<string, IncidentEventStateMetadataObservation>
                    {
                        ["radio-system"] = new("source-system", IncidentEventStateMetadataOrigin.SourceRecord),
                        ["source-group"] = new("source-provided-name", IncidentEventStateMetadataOrigin.SourceRecord)
                    })
            ],
            []);

    private static IncidentEventStateProposal Proposal() =>
        new(
            "proposal-1",
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T12:01:00Z"),
            "proposer-model-1",
            "proposer-prompt-1",
            [
                new IncidentEventStateHypothesis(
                    "hypothesis-1",
                    "The observation may describe a real-world event requiring further interpretation.",
                    0.35,
                    ["observation-1"],
                    [
                        new IncidentEventStateClaim(
                            "claim-1",
                            "A caller described hearing approximately ten loud sounds.",
                            0.1,
                            [TranscriptProvenance("heard approximately ten loud sounds")])
                    ],
                    [],
                    [
                        new IncidentEventStateAlternative(
                            "alternative-1",
                            "The source does not establish what produced the sounds.",
                            0.5,
                            [TranscriptProvenance("loud sounds")])
                    ],
                    ["What produced the reported sounds?"])
            ],
            []);

    private static IncidentEventStateObservationInterpretation ObservationInterpretation() =>
        new(
            "interpretation-1",
            "observation-1",
            DateTimeOffset.Parse("2026-07-17T12:00:30Z"),
            "interpretation-model-1",
            "interpretation-prompt-1",
            [
                new IncidentEventStateObservationInterpretationStatement(
                    "reading-1",
                    "The speaker may have described hearing approximately ten loud sounds.",
                    0.2,
                    [TranscriptProvenance("heard approximately ten loud sounds")]),
                new IncidentEventStateObservationInterpretationStatement(
                    "reading-2",
                    "The exact number of sounds may be uncertain.",
                    0.6,
                    [TranscriptProvenance("approximately ten")])
            ],
            [
                new IncidentEventStateObservationInterpretationStatement(
                    "shared-1",
                    "The transcript describes loud sounds.",
                    0.1,
                    [TranscriptProvenance("loud sounds")])
            ],
            ["What produced the sounds?"]);

    private static IncidentEventStateObservationInterpretationCritique ObservationInterpretationCritique() =>
        new(
            "interpretation-critique-1",
            "interpretation-1",
            DateTimeOffset.Parse("2026-07-17T12:00:45Z"),
            "interpretation-critic-model-1",
            "interpretation-critic-prompt-1",
            "The possible readings preserve uncertainty in the source.",
            [
                new IncidentEventStateCritiqueFinding(
                    "interpretation-finding-1",
                    "The source does not identify what produced the sounds.",
                    0.2,
                    [TranscriptProvenance("loud sounds")])
            ]);

    private static IncidentEventStateCritique Critique() =>
        new(
            "critique-1",
            "proposal-1",
            DateTimeOffset.Parse("2026-07-17T12:02:00Z"),
            "critic-model-1",
            "critic-prompt-1",
            "The proposal preserves the source uncertainty.",
            [
                new IncidentEventStateCritiqueFinding(
                    "finding-1",
                    "The source does not identify the cause of the sounds.",
                    0.2,
                    [TranscriptProvenance("loud sounds")])
            ]);

    private static IncidentEventStateProvenance TranscriptProvenance(string quote) =>
        new(
            "observation-1",
            "transcript-1",
            quote,
            null,
            null,
            string.Empty);
}
