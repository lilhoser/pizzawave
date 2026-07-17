namespace pizzad.Tests;

public sealed class IncidentEventStateAdjudicationTests
{
    [Fact]
    public void BlindPackageRemovesPriorModelStateAndApplicationDerivedMetadata()
    {
        var corpus = Corpus();

        var package = IncidentEventStateAdjudicationPackageBuilder.BuildBlindPackage(corpus);

        var bundle = Assert.Single(package.Bundles);
        Assert.Empty(bundle.PriorState);
        var metadata = Assert.Single(bundle.Observations).Metadata;
        Assert.Contains("source-field", metadata.Keys);
        Assert.Contains("raw-payload", metadata.Keys);
        Assert.DoesNotContain("application-category", metadata.Keys);
    }

    [Fact]
    public void HumanReviewAcceptsOpenWorldGroundedEvent()
    {
        var package = IncidentEventStateAdjudicationPackageBuilder.BuildBlindPackage(Corpus());

        var result = IncidentEventStateHumanReviewValidator.Validate(package, Review("review-1", "reviewer-a"));

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void HumanReviewRejectsQuoteThatDoesNotExist()
    {
        var package = IncidentEventStateAdjudicationPackageBuilder.BuildBlindPackage(Corpus());
        var review = Review("review-1", "reviewer-a");
        var invalidClaim = review.PossibleEvents[0].Claims[0] with
        {
            Provenance = [Provenance() with { ExactQuote = "words absent from source" }]
        };
        review = review with
        {
            PossibleEvents = [review.PossibleEvents[0] with { Claims = [invalidClaim] }]
        };

        var result = IncidentEventStateHumanReviewValidator.Validate(package, review);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("does not occur", StringComparison.Ordinal));
    }

    [Fact]
    public void ReconciliationRequiresIndependentReviewers()
    {
        var package = IncidentEventStateAdjudicationPackageBuilder.BuildBlindPackage(Corpus());
        var reviews = new[]
        {
            Review("review-1", "same-reviewer"),
            Review("review-2", "same-reviewer")
        };
        var reconciliation = new IncidentEventStateHumanReconciliation(
            "reconciliation-1",
            package.CorpusId,
            package.CorpusVersion,
            "bundle-1",
            "adjudicator",
            DateTimeOffset.Parse("2026-07-17T16:00:00Z"),
            ["review-1", "review-2"],
            ReviewEvents(),
            ["The reviewers may share a common interpretation."]);

        var result = IncidentEventStateHumanReviewValidator.Validate(package, reconciliation, reviews);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("two reviewer identities", StringComparison.Ordinal));
    }

    private static IncidentEventStateCorpusDocument Corpus()
    {
        var bundle = new IncidentEventStateObservationBundle(
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
                    new Dictionary<string, IncidentEventStateMetadataObservation>
                    {
                        ["source-field"] = new("source value", IncidentEventStateMetadataOrigin.SourceRecord),
                        ["raw-payload"] = new("{}", IncidentEventStateMetadataOrigin.RawPayload),
                        ["application-category"] = new("legacy value", IncidentEventStateMetadataOrigin.ApplicationDerived)
                    })
            ],
            [
                new IncidentEventStateProjectedEvent(
                    "prior-event",
                    "Prior model output must not reach a blind reviewer.",
                    0.5,
                    ["observation-1"],
                    [],
                    [],
                    [],
                    ["ledger-1"])
            ]);
        return new IncidentEventStateCorpusDocument(
            new IncidentEventStateCorpusManifest(
                "corpus-1",
                "v1",
                DateTimeOffset.Parse("2026-07-17T15:00:00Z"),
                100,
                200,
                "selection-v1",
                "software-v1"),
            [bundle]);
    }

    private static IncidentEventStateHumanReview Review(string reviewId, string reviewerIdentity) =>
        new(
            reviewId,
            "corpus-1",
            "v1",
            "bundle-1",
            reviewerIdentity,
            DateTimeOffset.Parse("2026-07-17T15:30:00Z"),
            ["The source does not establish what occurred."],
            ReviewEvents());

    private static IReadOnlyList<IncidentEventStateHumanReviewEvent> ReviewEvents() =>
        [
            new IncidentEventStateHumanReviewEvent(
                "event-1",
                "The source may describe an unresolved event.",
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
                ["What occurred remains unresolved."])
        ];

    private static IncidentEventStateProvenance Provenance() =>
        new(
            "observation-1",
            "transcript-1",
            "unresolved event",
            null,
            null,
            string.Empty);
}
