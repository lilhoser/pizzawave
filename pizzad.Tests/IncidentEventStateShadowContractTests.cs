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
                    Metadata = new Dictionary<string, string>
                    {
                        ["source-provided-field"] = "unfamiliar value",
                        ["another-field"] = "not mapped by application policy"
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
                    new Dictionary<string, string>
                    {
                        ["radio-system"] = "source-system",
                        ["source-group"] = "source-provided-name"
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
