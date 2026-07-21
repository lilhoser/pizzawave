using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentEventStateMicroBatchReplayOptions(
    int MaximumNewObservations,
    int MaximumBatchSpanSeconds,
    int MaximumContextObservations,
    int ContextLookbackSeconds)
{
    public const int AbsoluteMaximumNewObservations = 24;
    public const int AbsoluteMaximumContextObservations = 48;

    public void Validate()
    {
        if (MaximumNewObservations is < 1 or > AbsoluteMaximumNewObservations)
            throw new ArgumentOutOfRangeException(nameof(MaximumNewObservations));
        if (MaximumBatchSpanSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(MaximumBatchSpanSeconds));
        if (MaximumContextObservations is < 0 or > AbsoluteMaximumContextObservations)
            throw new ArgumentOutOfRangeException(nameof(MaximumContextObservations));
        if (ContextLookbackSeconds is < 0 or > 7200)
            throw new ArgumentOutOfRangeException(nameof(ContextLookbackSeconds));
    }
}

public sealed record IncidentEventStateMicroBatchReplayBatch(
    string BatchId,
    int Sequence,
    long WindowStartUnixSeconds,
    long WindowEndUnixSeconds,
    IReadOnlyList<string> NewObservationIds,
    IReadOnlyList<string> ContextObservationIds,
    string ContentHash);

public sealed record IncidentEventStateMicroBatchReplayPlan(
    string ReplayId,
    string ProtocolIdentity,
    DateTimeOffset CreatedAtUtc,
    IncidentEventStateMicroBatchReplayOptions Options,
    int SourceObservationCount,
    int PlannedObservationCount,
    int DuplicateObservationCount,
    IReadOnlyList<IncidentEventStateMicroBatchReplayBatch> Batches,
    string ContentHash);

public static class IncidentEventStateMicroBatchReplayPlanner
{
    public const string ProtocolIdentity = "incident-event-microbatch-all-observations-v1";

    public static IncidentEventStateMicroBatchReplayPlan Build(
        string replayId,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<IncidentEventStateSourceObservation> sourceObservations,
        IncidentEventStateMicroBatchReplayOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replayId);
        if (createdAtUtc == default)
            throw new ArgumentException("replay creation timestamp is required", nameof(createdAtUtc));
        ArgumentNullException.ThrowIfNull(sourceObservations);
        options.Validate();

        var duplicateCount = sourceObservations.Count - sourceObservations
            .Select(observation => observation.ObservationId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (duplicateCount != 0)
            throw new ArgumentException("source observation ids must be unique", nameof(sourceObservations));

        var ordered = sourceObservations
            .OrderBy(observation => observation.ObservedAtUnixSeconds)
            .ThenBy(observation => observation.CallId ?? long.MaxValue)
            .ThenBy(observation => observation.ObservationId, StringComparer.Ordinal)
            .ToList();
        var batches = new List<IncidentEventStateMicroBatchReplayBatch>();
        for (var offset = 0; offset < ordered.Count;)
        {
            var first = ordered[offset];
            var newObservations = ordered
                .Skip(offset)
                .TakeWhile((observation, index) =>
                    index < options.MaximumNewObservations &&
                    observation.ObservedAtUnixSeconds - first.ObservedAtUnixSeconds <= options.MaximumBatchSpanSeconds)
                .ToList();
            var contextFloor = first.ObservedAtUnixSeconds - options.ContextLookbackSeconds;
            var context = ordered
                .Take(offset)
                .Where(observation => observation.ObservedAtUnixSeconds >= contextFloor)
                .TakeLast(options.MaximumContextObservations)
                .ToList();
            var sequence = batches.Count + 1;
            var batchIdentity = new
            {
                replayId,
                sequence,
                windowStartUnixSeconds = first.ObservedAtUnixSeconds,
                windowEndUnixSeconds = newObservations[^1].ObservedAtUnixSeconds,
                newObservationIds = newObservations.Select(observation => observation.ObservationId).ToList(),
                contextObservationIds = context.Select(observation => observation.ObservationId).ToList()
            };
            batches.Add(new IncidentEventStateMicroBatchReplayBatch(
                $"{replayId}:batch:{sequence:D5}",
                sequence,
                first.ObservedAtUnixSeconds,
                newObservations[^1].ObservedAtUnixSeconds,
                newObservations.Select(observation => observation.ObservationId).ToList(),
                context.Select(observation => observation.ObservationId).ToList(),
                Hash(batchIdentity)));
            offset += newObservations.Count;
        }

        var plannedIds = batches.SelectMany(batch => batch.NewObservationIds).ToList();
        if (plannedIds.Count != ordered.Count ||
            plannedIds.Distinct(StringComparer.Ordinal).Count() != ordered.Count)
        {
            throw new InvalidDataException("the replay plan did not assign every source observation exactly once");
        }
        var planIdentity = new
        {
            replayId,
            protocolIdentity = ProtocolIdentity,
            createdAtUtc,
            options,
            sourceObservationIds = ordered.Select(observation => observation.ObservationId).ToList(),
            batches
        };
        return new IncidentEventStateMicroBatchReplayPlan(
            replayId,
            ProtocolIdentity,
            createdAtUtc,
            options,
            ordered.Count,
            plannedIds.Count,
            duplicateCount,
            batches,
            Hash(planIdentity));
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, EngineConfig.JsonOptions()))));
}

public enum IncidentEventStateMicroBatchDecision
{
    Unresolved,
    ProposeLink
}

public sealed record IncidentEventStateMicroBatchObservationDecision(
    string NewObservationToken,
    IncidentEventStateMicroBatchDecision Decision,
    string TargetObservationToken,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<string> NewEvidenceTranscriptIds,
    IReadOnlyList<string> TargetEvidenceTranscriptIds,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentEventStateMicroBatchProposal(
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentEventStateMicroBatchObservationDecision> Decisions);

public sealed record IncidentEventStateMicroBatchPromptPayload(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat,
    IReadOnlyDictionary<string, string> ObservationIdsByToken);

public static class IncidentEventStateMicroBatchPrompt
{
    public const string PromptIdentity = "incident-event-microbatch-link-only-v2";

    public static IncidentEventStateMicroBatchPromptPayload Build(
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
        user.AppendLine("For every new observation, decide whether positive transcript evidence supports linking it to one earlier observation from the same unfolding real-world event.");
        user.AppendLine("A target may be a context observation or an earlier new observation. It may not be the same observation or a later observation.");
        user.AppendLine("Use propose_link only when both observations contain positive evidence of one event. Otherwise use unresolved.");
        user.AppendLine("Unresolved does not mean different events. Do not infer a link from timing, ordering, radio channel, agency, topic similarity, unit-number similarity, or generic operational language alone.");
        user.AppendLine("Do not create categories, assign roles, summarize an event, or change event state.");
        user.AppendLine("For propose_link, cite transcript_id values belonging to both endpoints. Copy identifiers exactly; do not invent them.");
        user.AppendLine("Return exactly one decision for every supplied new observation token, in supplied order.");
        user.AppendLine();
        user.AppendLine("Source observations:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));

        var newTokens = newRows.Select((_, index) => $"new-{index + 1}").ToArray();
        var targetTokens = tokenMap.Keys.Append(string.Empty).ToArray();
        var transcriptIds = batch.ContextObservationIds.Concat(batch.NewObservationIds)
            .Select(id => observationsById[id])
            .SelectMany(observation => observation.Transcripts)
            .Select(transcript => transcript.TranscriptId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_event_microbatch_link_only_v2",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        decisions = new
                        {
                            type = "array",
                            minItems = newTokens.Length,
                            maxItems = newTokens.Length,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    new_observation_token = new { type = "string", @enum = newTokens },
                                    decision = new { type = "string", @enum = new[] { "propose_link", "unresolved" } },
                                    target_observation_token = new { type = "string", @enum = targetTokens },
                                    relationship_statement = new { type = "string" },
                                    uncertainty = new { type = "number", minimum = 0, maximum = 1 },
                                    new_evidence_transcript_ids = StringArray(transcriptIds),
                                    target_evidence_transcript_ids = StringArray(transcriptIds),
                                    unresolved_questions = StringArray(null)
                                },
                                required = new[]
                                {
                                    "new_observation_token", "decision", "target_observation_token",
                                    "relationship_statement", "uncertainty", "new_evidence_transcript_ids",
                                    "target_evidence_transcript_ids", "unresolved_questions"
                                }
                            }
                        }
                    },
                    required = new[] { "decisions" }
                }
            }
        };
        return new IncidentEventStateMicroBatchPromptPayload(
            "You identify only positive, source-grounded links among chronological radio observations. Your output is replay evidence; application code owns identifiers, validation, projection, and persistence.",
            user.ToString(),
            responseFormat,
            tokenMap);
    }

    private static object StringArray(string[]? values) => new
    {
        type = "array",
        items = values is null ? new { type = "string" } : (object)new { type = "string", @enum = values }
    };
}

public static class IncidentEventStateMicroBatchProposalValidator
{
    public static IncidentEventStateContractValidationResult Validate(
        IncidentEventStateMicroBatchReplayBatch batch,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById,
        IncidentEventStateMicroBatchPromptPayload prompt,
        IncidentEventStateMicroBatchProposal proposal)
    {
        var errors = new List<string>();
        if (!string.Equals(proposal.PromptIdentity, IncidentEventStateMicroBatchPrompt.PromptIdentity, StringComparison.Ordinal))
            errors.Add("proposal prompt identity does not match the micro-batch contract");
        var expectedTokens = batch.NewObservationIds
            .Select(id => prompt.ObservationIdsByToken.Single(item => item.Value == id).Key)
            .ToList();
        if (!proposal.Decisions.Select(decision => decision.NewObservationToken).SequenceEqual(expectedTokens, StringComparer.Ordinal))
            errors.Add("proposal must contain exactly one decision per new observation in supplied order");

        foreach (var decision in proposal.Decisions)
        {
            errors.AddRange(ValidateDecision(batch, observationsById, prompt, decision).Errors);
        }
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateDecision(
        IncidentEventStateMicroBatchReplayBatch batch,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById,
        IncidentEventStateMicroBatchPromptPayload prompt,
        IncidentEventStateMicroBatchObservationDecision decision)
    {
        var errors = new List<string>();
        if (!prompt.ObservationIdsByToken.TryGetValue(decision.NewObservationToken, out var newId) ||
            !batch.NewObservationIds.Contains(newId, StringComparer.Ordinal))
        {
            errors.Add($"unknown new observation token '{decision.NewObservationToken}'");
            return new IncidentEventStateContractValidationResult(false, errors);
        }
        if (decision.Uncertainty is < 0 or > 1)
            errors.Add($"decision '{decision.NewObservationToken}' uncertainty must be between zero and one");
        if (decision.Decision == IncidentEventStateMicroBatchDecision.Unresolved)
        {
            if (!string.IsNullOrEmpty(decision.TargetObservationToken))
                errors.Add($"unresolved decision '{decision.NewObservationToken}' must not select a target");
            if (decision.TargetEvidenceTranscriptIds.Count != 0)
                errors.Add($"unresolved decision '{decision.NewObservationToken}' must not cite target evidence");
            return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
        }
        if (!prompt.ObservationIdsByToken.TryGetValue(decision.TargetObservationToken, out var targetId))
        {
            errors.Add($"decision '{decision.NewObservationToken}' selects an unknown target token");
            return new IncidentEventStateContractValidationResult(false, errors);
        }
        var newTokenPositions = batch.NewObservationIds
            .Select(id => prompt.ObservationIdsByToken.Single(item => item.Value == id).Key)
            .Select((token, index) => (token, index))
            .ToDictionary(item => item.token, item => item.index, StringComparer.Ordinal);
        var targetIsEarlierNew = newTokenPositions.TryGetValue(decision.TargetObservationToken, out var targetPosition) &&
                                 targetPosition < newTokenPositions[decision.NewObservationToken];
        var targetIsContext = decision.TargetObservationToken.StartsWith("context-", StringComparison.Ordinal);
        if (!targetIsContext && !targetIsEarlierNew)
            errors.Add($"decision '{decision.NewObservationToken}' target is not earlier");
        if (string.IsNullOrWhiteSpace(decision.RelationshipStatement))
            errors.Add($"linked decision '{decision.NewObservationToken}' must describe the relationship");
        ValidateEvidence(
            decision.NewEvidenceTranscriptIds,
            observationsById[newId],
            decision.NewObservationToken,
            "new",
            errors);
        ValidateEvidence(
            decision.TargetEvidenceTranscriptIds,
            observationsById[targetId],
            decision.NewObservationToken,
            "target",
            errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateEvidence(
        IReadOnlyList<string> transcriptIds,
        IncidentEventStateSourceObservation observation,
        string decisionToken,
        string owner,
        List<string> errors)
    {
        if (transcriptIds.Count == 0)
            errors.Add($"linked decision '{decisionToken}' requires {owner} transcript evidence");
        var allowed = observation.Transcripts.Select(transcript => transcript.TranscriptId).ToHashSet(StringComparer.Ordinal);
        foreach (var transcriptId in transcriptIds)
        {
            if (!allowed.Contains(transcriptId))
                errors.Add($"linked decision '{decisionToken}' cites {owner} transcript '{transcriptId}' from another observation");
        }
    }
}
