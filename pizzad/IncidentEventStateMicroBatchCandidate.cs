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
