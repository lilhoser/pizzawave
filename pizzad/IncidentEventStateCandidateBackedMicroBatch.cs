namespace pizzad;

public sealed record IncidentEventStateCandidateBackedLink(
    string CandidateToken,
    string NewObservationId,
    string TargetObservationId);

public sealed record IncidentEventStateCandidateBackedMicroBatchPrompt(
    IncidentEventStateMicroBatchReplayBatch Batch,
    IncidentEventStateMicroBatchPromptPayload Prompt,
    IReadOnlyList<IncidentEventStateCandidateBackedLink> CandidateLinks);

public static class IncidentEventStateCandidateBackedMicroBatch
{
    public static IncidentEventStateCandidateBackedMicroBatchPrompt Build(
        string batchId,
        int sequence,
        IReadOnlyList<IncidentEventStateMicroBatchCandidate> candidates,
        IReadOnlyDictionary<string, string> observationIdsByToken,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(observationIdsByToken);
        ArgumentNullException.ThrowIfNull(observationsById);
        if (candidates.Count == 0)
            throw new ArgumentException("at least one retrieved candidate is required", nameof(candidates));

        var links = candidates.Select((candidate, index) => new IncidentEventStateCandidateBackedLink(
            $"candidate-{index + 1}",
            Resolve(candidate.NewObservationToken),
            Resolve(candidate.TargetObservationToken))).ToList();
        if (links.Select(link => (link.NewObservationId, link.TargetObservationId)).Distinct().Count() != links.Count)
            throw new ArgumentException("retrieved candidate pairs must be unique", nameof(candidates));

        var newIdSet = links.Select(link => link.NewObservationId).ToHashSet(StringComparer.Ordinal);
        var newIds = newIdSet.OrderByTimestamp(observationsById).ToList();
        var contextIds = links
            .Select(link => link.TargetObservationId)
            .Where(id => !newIdSet.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderByTimestamp(observationsById)
            .ToList();
        var included = contextIds.Concat(newIds).Select(id => observationsById[id]).ToList();
        var batch = new IncidentEventStateMicroBatchReplayBatch(
            batchId,
            sequence,
            included.Min(observation => observation.ObservedAtUnixSeconds),
            included.Max(observation => observation.ObservedAtUnixSeconds),
            newIds,
            contextIds,
            batchId);
        return new IncidentEventStateCandidateBackedMicroBatchPrompt(
            batch,
            IncidentEventStateMicroBatchPrompt.Build(batch, observationsById),
            links);

        string Resolve(string token)
        {
            if (!observationIdsByToken.TryGetValue(token, out var observationId) ||
                !observationsById.ContainsKey(observationId))
            {
                throw new ArgumentException($"candidate token '{token}' does not resolve to a source observation", nameof(candidates));
            }
            return observationId;
        }
    }

    public static IncidentEventStateContractValidationResult ValidateDecision(
        IncidentEventStateCandidateBackedMicroBatchPrompt plan,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById,
        IncidentEventStateMicroBatchObservationDecision decision)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(observationsById);
        ArgumentNullException.ThrowIfNull(decision);
        var baseValidation = IncidentEventStateMicroBatchProposalValidator.ValidateDecision(
            plan.Batch,
            observationsById,
            plan.Prompt,
            decision);
        if (!baseValidation.IsValid || decision.Decision != IncidentEventStateMicroBatchDecision.ProposeLink)
            return baseValidation;
        if (!plan.Prompt.ObservationIdsByToken.TryGetValue(decision.NewObservationToken, out var newId) ||
            !plan.Prompt.ObservationIdsByToken.TryGetValue(decision.TargetObservationToken, out var targetId))
        {
            return new IncidentEventStateContractValidationResult(false, ["linked decision tokens do not resolve"]);
        }
        if (plan.CandidateLinks.Any(link =>
                string.Equals(link.NewObservationId, newId, StringComparison.Ordinal) &&
                string.Equals(link.TargetObservationId, targetId, StringComparison.Ordinal)))
        {
            return baseValidation;
        }
        return new IncidentEventStateContractValidationResult(
            false,
            [$"linked decision '{decision.NewObservationToken}' selected a target outside retrieved candidates"]);
    }

    public static string? CandidateTokenFor(
        IncidentEventStateCandidateBackedMicroBatchPrompt plan,
        IncidentEventStateMicroBatchObservationDecision decision)
    {
        if (decision.Decision != IncidentEventStateMicroBatchDecision.ProposeLink ||
            !plan.Prompt.ObservationIdsByToken.TryGetValue(decision.NewObservationToken, out var newId) ||
            !plan.Prompt.ObservationIdsByToken.TryGetValue(decision.TargetObservationToken, out var targetId))
        {
            return null;
        }
        return plan.CandidateLinks.FirstOrDefault(link =>
            string.Equals(link.NewObservationId, newId, StringComparison.Ordinal) &&
            string.Equals(link.TargetObservationId, targetId, StringComparison.Ordinal))?.CandidateToken;
    }

    private static IOrderedEnumerable<string> OrderByTimestamp(
        this IEnumerable<string> ids,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById) =>
        ids.OrderBy(id => observationsById[id].ObservedAtUnixSeconds)
            .ThenBy(id => observationsById[id].CallId ?? long.MaxValue)
            .ThenBy(id => id, StringComparer.Ordinal);
}
