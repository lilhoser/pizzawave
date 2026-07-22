using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentEventStateMicroBatchCandidate(
    string NewObservationToken,
    string TargetObservationToken,
    string ReasonToCompare);

public sealed record IncidentEventStateMicroBatchCandidateProposal(
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentEventStateMicroBatchCandidate> Candidates);

public sealed record IncidentEventStateMicroBatchCandidatePromptPayload(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat,
    IReadOnlyDictionary<string, string> ObservationIdsByToken);

public static class IncidentEventStateMicroBatchCandidatePrompt
{
    public const string PromptIdentity = "incident-event-microbatch-candidate-retrieval-v1";

    public static IncidentEventStateMicroBatchCandidatePromptPayload Build(
        IncidentEventStateMicroBatchReplayBatch batch,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(observationsById);
        var newRows = batch.NewObservationIds.Select(id => observationsById[id]).ToList();
        var contextRows = batch.ContextObservationIds.Select(id => observationsById[id]).ToList();
        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < contextRows.Count; index++)
            tokenMap[$"context-{index + 1}"] = contextRows[index].ObservationId;
        for (var index = 0; index < newRows.Count; index++)
            tokenMap[$"new-{index + 1}"] = newRows[index].ObservationId;

        object PromptObservation(string token, IncidentEventStateSourceObservation observation) => new
        {
            observation_token = token,
            observed_at_unix_seconds = observation.ObservedAtUnixSeconds,
            transcripts = observation.Transcripts.Select(transcript => new
            {
                transcript_id = transcript.TranscriptId,
                text = transcript.Text,
                producer = transcript.Producer
            }).ToList()
        };
        var source = new
        {
            context_observations = contextRows.Select((observation, index) =>
                PromptObservation($"context-{index + 1}", observation)).ToList(),
            new_observations = newRows.Select((observation, index) =>
                PromptObservation($"new-{index + 1}", observation)).ToList()
        };
        var user = new StringBuilder();
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Generate a high-recall list of observation pairs worth final semantic verification.");
        user.AppendLine("Include a pair when transcript content could reasonably be the same event, a continued exchange, a response, a clarification, or an update.");
        user.AppendLine("This is candidate retrieval, not an incident-membership decision. Prefer an extra plausible pair over missing a real continuation.");
        user.AppendLine("Do not add a pair based only on timing, ordering, radio channel, agency, topic similarity, unit-number similarity, or generic operational language.");
        user.AppendLine("The new_observation_token must identify a new observation. The target_observation_token must identify an earlier context observation or earlier new observation.");
        user.AppendLine("Return each pair at most once and no more than two targets for one new observation.");
        user.AppendLine();
        user.AppendLine("Source observations:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));

        var newTokens = newRows.Select((_, index) => $"new-{index + 1}").ToArray();
        var targetTokens = tokenMap.Keys.ToArray();
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_event_microbatch_candidate_retrieval_v1",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        candidates = new
                        {
                            type = "array",
                            maxItems = newTokens.Length * 2,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    new_observation_token = new { type = "string", @enum = newTokens },
                                    target_observation_token = new { type = "string", @enum = targetTokens },
                                    reason_to_compare = new { type = "string" }
                                },
                                required = new[] { "new_observation_token", "target_observation_token", "reason_to_compare" }
                            }
                        }
                    },
                    required = new[] { "candidates" }
                }
            }
        };
        return new IncidentEventStateMicroBatchCandidatePromptPayload(
            "You retrieve plausible chronological radio-observation pairs for a separate final verifier. You do not decide incident membership or mutate state.",
            user.ToString(),
            responseFormat,
            tokenMap);
    }
}

public static class IncidentEventStateMicroBatchCandidateValidator
{
    public static IncidentEventStateContractValidationResult Validate(
        IncidentEventStateMicroBatchReplayBatch batch,
        IncidentEventStateMicroBatchCandidatePromptPayload prompt,
        IncidentEventStateMicroBatchCandidateProposal proposal)
    {
        var errors = new List<string>();
        if (!string.Equals(proposal.PromptIdentity, IncidentEventStateMicroBatchCandidatePrompt.PromptIdentity, StringComparison.Ordinal))
            errors.Add("candidate proposal prompt identity does not match the candidate contract");
        var newTokenPositions = batch.NewObservationIds
            .Select(id => prompt.ObservationIdsByToken.Single(item => item.Value == id).Key)
            .Select((token, index) => (token, index))
            .ToDictionary(item => item.token, item => item.index, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in proposal.Candidates)
        {
            if (!newTokenPositions.ContainsKey(candidate.NewObservationToken))
            {
                errors.Add($"unknown candidate new observation token '{candidate.NewObservationToken}'");
                continue;
            }
            if (!prompt.ObservationIdsByToken.ContainsKey(candidate.TargetObservationToken))
            {
                errors.Add($"candidate '{candidate.NewObservationToken}' selects an unknown target token");
                continue;
            }
            var targetIsEarlierNew = newTokenPositions.TryGetValue(candidate.TargetObservationToken, out var targetPosition) &&
                                     targetPosition < newTokenPositions[candidate.NewObservationToken];
            var targetIsContext = candidate.TargetObservationToken.StartsWith("context-", StringComparison.Ordinal);
            if (!targetIsContext && !targetIsEarlierNew)
                errors.Add($"candidate '{candidate.NewObservationToken}' target is not earlier");
            if (!seen.Add($"{candidate.NewObservationToken}\0{candidate.TargetObservationToken}"))
                errors.Add($"candidate pair '{candidate.NewObservationToken}' to '{candidate.TargetObservationToken}' is duplicated");
        }
        foreach (var group in proposal.Candidates.GroupBy(candidate => candidate.NewObservationToken, StringComparer.Ordinal))
        {
            if (group.Count() > 2)
                errors.Add($"candidate '{group.Key}' exceeds the two-target resource limit");
        }
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }
}

public static class IncidentEventStateMicroBatchExhaustiveCandidates
{
    public const string RetrieverIdentity = "incident-event-microbatch-exhaustive-chronological-v1";

    public static IReadOnlyList<IncidentEventStateMicroBatchCandidate> Build(
        IncidentEventStateMicroBatchReplayBatch batch,
        IncidentEventStateMicroBatchCandidatePromptPayload prompt)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(prompt);
        var tokenById = prompt.ObservationIdsByToken.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var contextTokens = batch.ContextObservationIds.Select(id => tokenById[id]).ToList();
        var newTokens = batch.NewObservationIds.Select(id => tokenById[id]).ToList();
        var candidates = new List<IncidentEventStateMicroBatchCandidate>();
        for (var newIndex = 0; newIndex < newTokens.Count; newIndex++)
        {
            foreach (var targetToken in contextTokens.Concat(newTokens.Take(newIndex)))
            {
                candidates.Add(new IncidentEventStateMicroBatchCandidate(
                    newTokens[newIndex],
                    targetToken,
                    string.Empty));
            }
        }
        return candidates;
    }
}

public static class IncidentEventStateMicroBatchEmbeddingCandidates
{
    public const string RetrieverIdentity = "incident-event-microbatch-embedding-recent-union-v1";

    public static IReadOnlyList<IncidentEventStateMicroBatchCandidate> Build(
        IncidentEventStateMicroBatchReplayBatch batch,
        IncidentEventStateMicroBatchCandidatePromptPayload prompt,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById,
        IReadOnlyDictionary<string, float[]> embeddingsByObservationId,
        int semanticLimit,
        int recentLimit)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(observationsById);
        ArgumentNullException.ThrowIfNull(embeddingsByObservationId);
        if (semanticLimit is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(semanticLimit));
        if (recentLimit is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(recentLimit));
        if (semanticLimit + recentLimit == 0)
            throw new ArgumentException("at least one embedding or recent candidate is required");

        var tokenById = prompt.ObservationIdsByToken.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        var candidates = new List<IncidentEventStateMicroBatchCandidate>();
        for (var newIndex = 0; newIndex < batch.NewObservationIds.Count; newIndex++)
        {
            var newId = batch.NewObservationIds[newIndex];
            if (!embeddingsByObservationId.TryGetValue(newId, out var queryVector))
                continue;
            var eligibleIds = batch.ContextObservationIds.Concat(batch.NewObservationIds.Take(newIndex))
                .Where(embeddingsByObservationId.ContainsKey)
                .ToList();
            var selectedIds = eligibleIds
                .Select(id => new
                {
                    Id = id,
                    Similarity = Cosine(queryVector, embeddingsByObservationId[id])
                })
                .OrderByDescending(item => item.Similarity)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Take(semanticLimit)
                .Select(item => item.Id)
                .Concat(eligibleIds
                    .OrderByDescending(id => observationsById[id].ObservedAtUnixSeconds)
                    .ThenByDescending(id => observationsById[id].CallId ?? long.MinValue)
                    .ThenBy(id => id, StringComparer.Ordinal)
                    .Take(recentLimit))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var targetId in eligibleIds.Where(selectedIds.Contains))
            {
                candidates.Add(new IncidentEventStateMicroBatchCandidate(
                    tokenById[newId],
                    tokenById[targetId],
                    string.Empty));
            }
        }
        return candidates;
    }

    private static double Cosine(float[] left, float[] right)
    {
        if (left.Length == 0 || left.Length != right.Length)
            throw new ArgumentException("embedding vectors must have the same non-zero dimensions");
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }
        return leftNorm == 0 || rightNorm == 0 ? 0 : dot / Math.Sqrt(leftNorm * rightNorm);
    }
}
