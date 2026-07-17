namespace pizzad;

public sealed record IncidentEventStateBlindReviewPackage(
    string CorpusId,
    string CorpusVersion,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<IncidentEventStateObservationBundle> Bundles);

public sealed record IncidentEventStateHumanReviewEvent(
    string ReviewEventId,
    string Description,
    double Uncertainty,
    IReadOnlyList<string> ObservationIds,
    IReadOnlyList<IncidentEventStateClaim> Claims,
    IReadOnlyList<IncidentEventStateAlternative> Alternatives,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentEventStateHumanReview(
    string ReviewId,
    string CorpusId,
    string CorpusVersion,
    string BundleId,
    string ReviewerIdentity,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string> SourceLimitations,
    IReadOnlyList<IncidentEventStateHumanReviewEvent> PossibleEvents);

public sealed record IncidentEventStateHumanReconciliation(
    string ReconciliationId,
    string CorpusId,
    string CorpusVersion,
    string BundleId,
    string AdjudicatorIdentity,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<string> SourceReviewIds,
    IReadOnlyList<IncidentEventStateHumanReviewEvent> PossibleEvents,
    IReadOnlyList<string> UnresolvedDisagreements);

public static class IncidentEventStateAdjudicationPackageBuilder
{
    public static IncidentEventStateBlindReviewPackage BuildBlindPackage(
        IncidentEventStateCorpusDocument corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        var bundles = corpus.Bundles.Select(bundle => bundle with
        {
            PriorState = [],
            Observations = bundle.Observations.Select(observation => observation with
            {
                Metadata = observation.Metadata
                    .Where(item => item.Value.Origin != IncidentEventStateMetadataOrigin.ApplicationDerived)
                    .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            }).ToList()
        }).ToList();
        return new IncidentEventStateBlindReviewPackage(
            corpus.Manifest.CorpusId,
            corpus.Manifest.CorpusVersion,
            corpus.Manifest.CreatedAtUtc,
            bundles);
    }
}

public static class IncidentEventStateHumanReviewValidator
{
    public static IncidentEventStateContractValidationResult Validate(
        IncidentEventStateBlindReviewPackage package,
        IncidentEventStateHumanReview review)
    {
        var errors = new List<string>();
        Require(review.ReviewId, "review id", errors);
        Require(review.ReviewerIdentity, "reviewer identity", errors);
        if (review.CompletedAtUtc == default)
            errors.Add("review completion timestamp is required");
        if (!string.Equals(review.CorpusId, package.CorpusId, StringComparison.Ordinal) ||
            !string.Equals(review.CorpusVersion, package.CorpusVersion, StringComparison.Ordinal))
        {
            errors.Add("review corpus identity does not match the blind package");
        }
        var bundle = package.Bundles.FirstOrDefault(candidate =>
            string.Equals(candidate.BundleId, review.BundleId, StringComparison.Ordinal));
        if (bundle is null)
        {
            errors.Add($"review references unknown bundle '{review.BundleId}'");
            return new IncidentEventStateContractValidationResult(false, errors);
        }

        ValidateEvents(bundle, review.PossibleEvents, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult Validate(
        IncidentEventStateBlindReviewPackage package,
        IncidentEventStateHumanReconciliation reconciliation,
        IReadOnlyList<IncidentEventStateHumanReview> sourceReviews)
    {
        var errors = new List<string>();
        Require(reconciliation.ReconciliationId, "reconciliation id", errors);
        Require(reconciliation.AdjudicatorIdentity, "adjudicator identity", errors);
        if (reconciliation.CompletedAtUtc == default)
            errors.Add("reconciliation completion timestamp is required");
        if (!string.Equals(reconciliation.CorpusId, package.CorpusId, StringComparison.Ordinal) ||
            !string.Equals(reconciliation.CorpusVersion, package.CorpusVersion, StringComparison.Ordinal))
        {
            errors.Add("reconciliation corpus identity does not match the blind package");
        }
        if (reconciliation.SourceReviewIds.Count < 2)
            errors.Add("reconciliation requires at least two independent source reviews");
        if (reconciliation.SourceReviewIds.Distinct(StringComparer.Ordinal).Count() != reconciliation.SourceReviewIds.Count)
            errors.Add("reconciliation source review ids must be unique");
        foreach (var duplicateReviewId in sourceReviews
                     .Where(review => !string.IsNullOrWhiteSpace(review.ReviewId))
                     .GroupBy(review => review.ReviewId, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate supplied source review id '{duplicateReviewId}'");
        }
        var knownReviews = sourceReviews
            .Where(review => !string.IsNullOrWhiteSpace(review.ReviewId))
            .GroupBy(review => review.ReviewId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var reviewId in reconciliation.SourceReviewIds)
        {
            if (!knownReviews.TryGetValue(reviewId, out var review))
            {
                errors.Add($"reconciliation references unknown review '{reviewId}'");
                continue;
            }
            if (!string.Equals(review.BundleId, reconciliation.BundleId, StringComparison.Ordinal))
                errors.Add($"source review '{reviewId}' belongs to a different bundle");
            var reviewValidation = Validate(package, review);
            errors.AddRange(reviewValidation.Errors.Select(error => $"source review '{reviewId}': {error}"));
        }
        if (reconciliation.SourceReviewIds
            .Where(knownReviews.ContainsKey)
            .Select(reviewId => knownReviews[reviewId].ReviewerIdentity)
            .Distinct(StringComparer.Ordinal)
            .Count() < 2)
        {
            errors.Add("reconciliation requires reviews from at least two reviewer identities");
        }

        var bundle = package.Bundles.FirstOrDefault(candidate =>
            string.Equals(candidate.BundleId, reconciliation.BundleId, StringComparison.Ordinal));
        if (bundle is null)
            errors.Add($"reconciliation references unknown bundle '{reconciliation.BundleId}'");
        else
            ValidateEvents(bundle, reconciliation.PossibleEvents, errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateEvents(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentEventStateHumanReviewEvent> events,
        List<string> errors)
    {
        var duplicateIds = events
            .Where(reviewEvent => !string.IsNullOrWhiteSpace(reviewEvent.ReviewEventId))
            .GroupBy(reviewEvent => reviewEvent.ReviewEventId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var duplicateId in duplicateIds)
            errors.Add($"duplicate human review event id '{duplicateId}'");
        foreach (var reviewEvent in events)
        {
            Require(reviewEvent.ReviewEventId, "human review event id", errors);
            Require(reviewEvent.Description, $"description for human review event '{reviewEvent.ReviewEventId}'", errors);
            if (!double.IsFinite(reviewEvent.Uncertainty) || reviewEvent.Uncertainty < 0 || reviewEvent.Uncertainty > 1)
                errors.Add($"human review event '{reviewEvent.ReviewEventId}' uncertainty must be between 0 and 1");
            if (reviewEvent.ObservationIds.Count == 0)
                errors.Add($"human review event '{reviewEvent.ReviewEventId}' must reference at least one observation");
            var referenceValidation = IncidentEventStateContractValidator.ValidateObservationReferences(
                bundle,
                reviewEvent.ObservationIds,
                $"human review event '{reviewEvent.ReviewEventId}'");
            errors.AddRange(referenceValidation.Errors);
            if (reviewEvent.Claims.Count == 0)
                errors.Add($"human review event '{reviewEvent.ReviewEventId}' must include at least one grounded claim");
            AddDuplicateErrors(
                reviewEvent.Claims.Select(claim => claim.ClaimId),
                "human review claim id",
                errors);
            foreach (var claim in reviewEvent.Claims)
            {
                Require(claim.ClaimId, "human review claim id", errors);
                Require(claim.Statement, $"statement for human review claim '{claim.ClaimId}'", errors);
                if (!double.IsFinite(claim.Uncertainty) || claim.Uncertainty < 0 || claim.Uncertainty > 1)
                    errors.Add($"human review claim '{claim.ClaimId}' uncertainty must be between 0 and 1");
                errors.AddRange(IncidentEventStateContractValidator.ValidateProvenanceReferences(
                    bundle,
                    claim.Provenance,
                    $"human review claim '{claim.ClaimId}'").Errors);
            }
            AddDuplicateErrors(
                reviewEvent.Alternatives.Select(alternative => alternative.AlternativeId),
                "human review alternative id",
                errors);
            foreach (var alternative in reviewEvent.Alternatives)
            {
                Require(alternative.AlternativeId, "human review alternative id", errors);
                Require(alternative.Statement, $"statement for human review alternative '{alternative.AlternativeId}'", errors);
                if (!double.IsFinite(alternative.Uncertainty) || alternative.Uncertainty < 0 || alternative.Uncertainty > 1)
                    errors.Add($"human review alternative '{alternative.AlternativeId}' uncertainty must be between 0 and 1");
                errors.AddRange(IncidentEventStateContractValidator.ValidateProvenanceReferences(
                    bundle,
                    alternative.Provenance,
                    $"human review alternative '{alternative.AlternativeId}'").Errors);
            }
        }
    }

    private static void AddDuplicateErrors(
        IEnumerable<string> values,
        string description,
        List<string> errors)
    {
        foreach (var duplicate in values
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .GroupBy(value => value, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1)
                     .Select(group => group.Key))
        {
            errors.Add($"duplicate {description} '{duplicate}'");
        }
    }

    private static void Require(string value, string description, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{description} is required");
    }
}
