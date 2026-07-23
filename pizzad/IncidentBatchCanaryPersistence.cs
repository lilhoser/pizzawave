using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace pizzad;

public sealed record IncidentBatchCanaryPersistenceIntent(
    string RunId,
    string RequestId,
    string ResultId,
    string ProjectionId,
    string ProjectionEventId,
    string IncidentKey,
    string Title,
    string Detail,
    double Score,
    IReadOnlyList<long> CallIds);

public enum IncidentBatchCanaryCommitOutcome
{
    Persisted,
    Conflict
}

public sealed record IncidentBatchCanaryCommit(
    string CommitId,
    string RunId,
    string RequestId,
    string ResultId,
    string ProjectionId,
    string ProjectionEventId,
    DateTimeOffset RecordedAtUtc,
    IncidentBatchCanaryCommitOutcome Outcome,
    long IncidentId,
    string IncidentKey,
    IReadOnlyList<long> CallIds,
    string Reason);

public sealed record IncidentBatchStoredCanaryCommit(
    long Sequence,
    string ContentHash,
    IncidentBatchCanaryCommit Commit);

public static class IncidentBatchCanaryGate
{
    public const string ConfigurationToken = "persistence=verified-membership-canary-v1";

    public static bool AllowsPersistence(AiInsightsConfig config) =>
        string.IsNullOrEmpty(BlockReason(config));

    public static string BlockReason(AiInsightsConfig config)
    {
        if (!config.IncidentBatchCanaryPersistenceEnabled)
            return "canary persistence is disabled";
        if (config.IncidentAnalysisExecutionEnabled)
            return "legacy incident execution must be disabled";
        if (!config.IncidentBatchConstructorShadowExclusiveInferenceWindow)
            return "an exclusive inference window is required";
        if (!config.IncidentBatchConstructorShadowEnabled)
            return "the batch constructor must be enabled";
        if (!config.IncidentBatchRelationshipShadowEnabled)
            return "the separate relationship stage must be enabled";
        if (!config.IncidentBatchVerificationShadowEnabled)
            return "independent verification must be enabled";
        if (!config.IncidentBatchConstructorShadowObservationIsolated)
            return "observation-isolated source ownership is required";
        if (!config.IncidentBatchConstructorShadowSourceIsolated)
            return "candidate-free source construction is required";
        if (string.IsNullOrWhiteSpace(config.IncidentBatchConstructorShadowRunId))
            return "a canary run id is required";
        return string.Empty;
    }
}

public static class IncidentBatchCanaryContract
{
    public static IncidentBatchCanaryPersistenceIntent? BuildIntent(
        IncidentBatchLedgerEntry sourceEntry,
        IncidentBatchVerificationRequest request,
        IncidentBatchVerificationResult result,
        IncidentBatchProjection projection)
    {
        var resultValidation = IncidentBatchVerificationQueueContract.ValidateResult(sourceEntry, request, result);
        if (!resultValidation.IsValid)
            throw new ArgumentException(string.Join("; ", resultValidation.Errors), nameof(result));
        var projectionValidation = IncidentBatchContract.ValidateProjection(projection);
        if (!projectionValidation.IsValid)
            throw new ArgumentException(string.Join("; ", projectionValidation.Errors), nameof(projection));
        if (projection.RunId != request.RunId)
            throw new ArgumentException("canary projection belongs to a different run", nameof(projection));

        if (result.Outcome != IncidentBatchVerificationOutcome.Verified ||
            request.ProposedDisposition != IncidentBatchEventDisposition.ConfirmedMembership)
            return null;
        var configurationTokens = sourceEntry.Execution.ConfigurationIdentity
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (sourceEntry.RelationshipProposal is null ||
            !configurationTokens.Contains(IncidentBatchExecutionArchitecture.StagedRelationshipAsynchronousConfirmationToken) ||
            !configurationTokens.Contains(IncidentBatchContract.ObservationIsolatedOwnershipConfigurationToken) ||
            !configurationTokens.Contains(IncidentBatchCanaryGate.ConfigurationToken))
        {
            throw new InvalidDataException(
                "verified canary membership did not originate from the staged observation-isolated canary architecture");
        }

        var context = IncidentBatchVerificationQueueContract.BuildContext(sourceEntry, request);
        var target = projection.Events.SingleOrDefault(item =>
            item.ProjectionEventId == context.Candidate.ProjectionEventId);
        if (target is null)
            throw new InvalidDataException("verified canary target is absent from the resulting projection");
        if (!target.OperatorVisible || target.OperatorReview)
            throw new InvalidDataException("verified canary target is not an operator-visible confirmed event");
        if (!context.Source.NewObservationIds.ToHashSet(StringComparer.Ordinal).IsSubsetOf(target.ObservationIds) ||
            !context.Candidate.ObservationIds.ToHashSet(StringComparer.Ordinal).IsSubsetOf(target.ObservationIds))
            throw new InvalidDataException("verified canary target does not own both sides of the confirmed relationship");

        var callIds = target.ObservationIds
            .Select(ParseCallObservationId)
            .Distinct()
            .Order()
            .ToList();
        if (callIds.Count < 2)
            throw new InvalidDataException("verified canary incidents require at least two distinct calls");
        if (string.IsNullOrWhiteSpace(target.Title) || string.IsNullOrWhiteSpace(target.Summary))
            throw new InvalidDataException("verified canary incidents require source-grounded title and detail");

        var score = Math.Clamp(1d - context.Relationship.Uncertainty, 0d, 1d);
        return new IncidentBatchCanaryPersistenceIntent(
            request.RunId,
            request.RequestId,
            result.ResultId,
            projection.ProjectionId,
            target.ProjectionEventId,
            IncidentKey(request.RunId, target.ProjectionEventId),
            target.Title.Trim(),
            target.Summary.Trim(),
            score,
            callIds);
    }

    public static string IncidentKey(string runId, string projectionEventId)
    {
        var source = Encoding.UTF8.GetBytes($"{runId}\n{projectionEventId}");
        var digest = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        return $"batch-canary:{digest[..24]}";
    }

    private static long ParseCallObservationId(string observationId)
    {
        const string prefix = "call:";
        if (!observationId.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(
                observationId.AsSpan(prefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var callId) ||
            callId <= 0)
        {
            throw new InvalidDataException(
                $"verified canary observation '{observationId}' is not a persisted call identity");
        }
        return callId;
    }
}
