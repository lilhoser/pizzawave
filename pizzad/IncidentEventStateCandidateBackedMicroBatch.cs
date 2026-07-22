using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentEventStateCandidateBackedLink(
    string CandidateToken,
    string NewObservationId,
    string TargetObservationId);

public sealed record IncidentEventStateCandidateBackedMicroBatchPrompt(
    IncidentEventStateMicroBatchReplayBatch Batch,
    IncidentEventStateMicroBatchPromptPayload Prompt,
    IReadOnlyList<IncidentEventStateCandidateBackedLink> CandidateLinks);

public sealed record IncidentEventStatePairwiseAdjudicationPlan(
    string SourceCandidateToken,
    string NewObservationId,
    string TargetObservationId,
    IncidentEventStateCandidateBackedMicroBatchPrompt PairwisePrompt);

public sealed record IncidentEventStateSparseLinkProposal(
    string CandidateToken,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<string> NewEvidenceTranscriptIds,
    IReadOnlyList<string> TargetEvidenceTranscriptIds);

public sealed record IncidentEventStateSparseLinkEnvelope(
    string ModelIdentity,
    string PromptIdentity,
    bool Completed,
    IReadOnlyList<IncidentEventStateSparseLinkProposal> Links);

public sealed record IncidentEventStateSparseLinkPromptPayload(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat);

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

public static class IncidentEventStatePairwiseAdjudication
{
    public static IncidentEventStatePairwiseAdjudicationPlan Build(
        string batchId,
        int sequence,
        string sourceCandidateToken,
        IReadOnlyList<IncidentEventStateMicroBatchCandidate> candidates,
        IReadOnlyDictionary<string, string> observationIdsByToken,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCandidateToken);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(observationIdsByToken);
        ArgumentNullException.ThrowIfNull(observationsById);

        var sourcePlan = IncidentEventStateCandidateBackedMicroBatch.Build(
            batchId,
            sequence,
            candidates,
            observationIdsByToken,
            observationsById);
        var indexedLink = sourcePlan.CandidateLinks
            .Select((link, index) => (Link: link, Index: index))
            .SingleOrDefault(item => string.Equals(item.Link.CandidateToken, sourceCandidateToken, StringComparison.Ordinal));
        if (indexedLink.Link is null)
            throw new ArgumentException($"unknown source candidate token '{sourceCandidateToken}'", nameof(sourceCandidateToken));

        var pairwisePrompt = IncidentEventStateCandidateBackedMicroBatch.Build(
            $"{batchId}:pairwise:{sourceCandidateToken}",
            sequence,
            [candidates[indexedLink.Index]],
            observationIdsByToken,
            observationsById);
        var pairwiseLink = pairwisePrompt.CandidateLinks.Single();
        if (!string.Equals(pairwiseLink.NewObservationId, indexedLink.Link.NewObservationId, StringComparison.Ordinal) ||
            !string.Equals(pairwiseLink.TargetObservationId, indexedLink.Link.TargetObservationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("pairwise candidate endpoints do not match the source candidate");
        }

        return new IncidentEventStatePairwiseAdjudicationPlan(
            sourceCandidateToken,
            indexedLink.Link.NewObservationId,
            indexedLink.Link.TargetObservationId,
            pairwisePrompt);
    }
}

public static class IncidentEventStateSparseLinkPrompt
{
    public const string PromptIdentity = "incident-event-candidate-sparse-links-v1";

    public static IncidentEventStateSparseLinkPromptPayload Build(
        IncidentEventStateCandidateBackedMicroBatchPrompt plan,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(observationsById);
        var tokenById = plan.Prompt.ObservationIdsByToken.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);

        object PromptObservation(string id)
        {
            var observation = observationsById[id];
            return new
            {
                observation_token = tokenById[id],
                observed_at_unix_seconds = observation.ObservedAtUnixSeconds,
                transcripts = observation.Transcripts.Select(transcript => new
                {
                    transcript_id = transcript.TranscriptId,
                    text = transcript.Text,
                    producer = transcript.Producer
                }).ToList()
            };
        }

        var source = new
        {
            context_observations = plan.Batch.ContextObservationIds.Select(PromptObservation).ToList(),
            new_observations = plan.Batch.NewObservationIds.Select(PromptObservation).ToList(),
            candidates = plan.CandidateLinks.Select(link => new
            {
                candidate_token = link.CandidateToken,
                new_observation_token = tokenById[link.NewObservationId],
                target_observation_token = tokenById[link.TargetObservationId]
            }).ToList()
        };
        var user = new StringBuilder();
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Examine every supplied candidate, then set completed to true.");
        user.AppendLine("Return a candidate in links only when both endpoint transcripts contain positive evidence that they belong to the same unfolding real-world event.");
        user.AppendLine("Omit every unsupported or uncertain candidate. An empty links array is a complete and valid answer.");
        user.AppendLine("Do not infer a link from timing, ordering, radio channel, agency, topic similarity, unit-number similarity, or generic operational language alone.");
        user.AppendLine("Do not create categories, assign roles, summarize an event, or change event state.");
        user.AppendLine("For each returned link, cite transcript_id values belonging to both endpoints. Copy candidate and transcript identifiers exactly; do not invent them.");
        user.AppendLine("Return at most one link for each new observation.");
        user.AppendLine();
        user.AppendLine("Source observations and candidate pairs:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));

        var candidateTokens = plan.CandidateLinks.Select(link => link.CandidateToken).ToArray();
        var transcriptIds = plan.Batch.ContextObservationIds.Concat(plan.Batch.NewObservationIds)
            .Select(id => observationsById[id])
            .SelectMany(observation => observation.Transcripts)
            .Select(transcript => transcript.TranscriptId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var maximumLinks = plan.CandidateLinks.Select(link => link.NewObservationId).Distinct(StringComparer.Ordinal).Count();
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_event_candidate_sparse_links_v1",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        completed = new { type = "boolean", @enum = new[] { true } },
                        links = new
                        {
                            type = "array",
                            minItems = 0,
                            maxItems = maximumLinks,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    candidate_token = new { type = "string", @enum = candidateTokens },
                                    relationship_statement = new { type = "string" },
                                    uncertainty = new { type = "number", minimum = 0, maximum = 1 },
                                    new_evidence_transcript_ids = StringArray(transcriptIds),
                                    target_evidence_transcript_ids = StringArray(transcriptIds)
                                },
                                required = new[]
                                {
                                    "candidate_token", "relationship_statement", "uncertainty",
                                    "new_evidence_transcript_ids", "target_evidence_transcript_ids"
                                }
                            }
                        }
                    },
                    required = new[] { "completed", "links" }
                }
            }
        };
        return new IncidentEventStateSparseLinkPromptPayload(
            "You emit only positive, source-grounded incident link proposals. Omission means no link. Application code owns validation, projection, and persistence.",
            user.ToString(),
            responseFormat);
    }

    private static object StringArray(string[] values) => new
    {
        type = "array",
        minItems = 1,
        items = new { type = "string", @enum = values }
    };
}

public static class IncidentEventStateSparseLinkValidator
{
    public static IncidentEventStateContractValidationResult Validate(
        IncidentEventStateCandidateBackedMicroBatchPrompt plan,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById,
        IncidentEventStateSparseLinkEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(observationsById);
        ArgumentNullException.ThrowIfNull(envelope);
        var errors = new List<string>();
        if (!string.Equals(envelope.PromptIdentity, IncidentEventStateSparseLinkPrompt.PromptIdentity, StringComparison.Ordinal))
            errors.Add("proposal prompt identity does not match the sparse link contract");
        if (!envelope.Completed)
            errors.Add("sparse link proposal is not marked complete");

        var duplicateCandidates = envelope.Links
            .GroupBy(link => link.CandidateToken, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);
        foreach (var candidateToken in duplicateCandidates)
            errors.Add($"candidate '{candidateToken}' was proposed more than once");

        var resolved = new List<(IncidentEventStateSparseLinkProposal Proposal, IncidentEventStateCandidateBackedLink Candidate)>();
        foreach (var proposal in envelope.Links)
        {
            var candidate = plan.CandidateLinks.SingleOrDefault(link =>
                string.Equals(link.CandidateToken, proposal.CandidateToken, StringComparison.Ordinal));
            if (candidate is null)
            {
                errors.Add($"unknown candidate token '{proposal.CandidateToken}'");
                continue;
            }
            resolved.Add((proposal, candidate));
            if (string.IsNullOrWhiteSpace(proposal.RelationshipStatement))
                errors.Add($"candidate '{proposal.CandidateToken}' must describe the relationship");
            if (proposal.Uncertainty is < 0 or > 1)
                errors.Add($"candidate '{proposal.CandidateToken}' uncertainty must be between zero and one");
            ValidateEvidence(proposal.NewEvidenceTranscriptIds, observationsById[candidate.NewObservationId], proposal.CandidateToken, "new", errors);
            ValidateEvidence(proposal.TargetEvidenceTranscriptIds, observationsById[candidate.TargetObservationId], proposal.CandidateToken, "target", errors);
        }

        foreach (var duplicateNewObservation in resolved
                     .GroupBy(item => item.Candidate.NewObservationId, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            errors.Add($"new observation '{duplicateNewObservation.Key}' received more than one link proposal");
        }
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateEvidence(
        IReadOnlyList<string> transcriptIds,
        IncidentEventStateSourceObservation observation,
        string candidateToken,
        string owner,
        List<string> errors)
    {
        if (transcriptIds.Count == 0)
            errors.Add($"candidate '{candidateToken}' requires {owner} transcript evidence");
        var allowed = observation.Transcripts.Select(transcript => transcript.TranscriptId).ToHashSet(StringComparer.Ordinal);
        foreach (var transcriptId in transcriptIds)
        {
            if (!allowed.Contains(transcriptId))
                errors.Add($"candidate '{candidateToken}' cites {owner} transcript '{transcriptId}' from another observation");
        }
    }
}
