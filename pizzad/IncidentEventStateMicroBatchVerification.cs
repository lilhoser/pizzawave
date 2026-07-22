using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentEventStateMicroBatchVerificationPair(
    string CandidateToken,
    string NewObservationId,
    string TargetObservationId);

public enum IncidentEventStateMicroBatchVerificationDecisionKind
{
    Reject,
    VerifyLink
}

public sealed record IncidentEventStateMicroBatchVerificationDecision(
    string CandidateToken,
    IncidentEventStateMicroBatchVerificationDecisionKind Decision,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<string> NewEvidenceTranscriptIds,
    IReadOnlyList<string> TargetEvidenceTranscriptIds,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentEventStateMicroBatchVerificationProposal(
    string ModelIdentity,
    string PromptIdentity,
    IReadOnlyList<IncidentEventStateMicroBatchVerificationDecision> Decisions);

public sealed record IncidentEventStateMicroBatchVerificationPromptPayload(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat,
    IReadOnlyList<IncidentEventStateMicroBatchVerificationPair> Pairs);

public static class IncidentEventStateMicroBatchVerificationPrompt
{
    public const string PromptIdentity = "incident-event-microbatch-candidate-verification-v1";
    public const int MaximumPairs = 12;

    public static IncidentEventStateMicroBatchVerificationPromptPayload Build(
        IReadOnlyList<IncidentEventStateMicroBatchCandidate> candidates,
        IReadOnlyDictionary<string, string> observationIdsByToken,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(observationIdsByToken);
        ArgumentNullException.ThrowIfNull(observationsById);
        if (candidates.Count is < 1 or > MaximumPairs)
            throw new ArgumentOutOfRangeException(nameof(candidates));

        var pairs = candidates.Select((candidate, index) =>
        {
            if (!observationIdsByToken.TryGetValue(candidate.NewObservationToken, out var newId) ||
                !observationIdsByToken.TryGetValue(candidate.TargetObservationToken, out var targetId))
            {
                throw new ArgumentException("candidate tokens must resolve to source observations", nameof(candidates));
            }
            return new IncidentEventStateMicroBatchVerificationPair($"candidate-{index + 1}", newId, targetId);
        }).ToList();
        object PromptObservation(IncidentEventStateSourceObservation observation) => new
        {
            observed_at_unix_seconds = observation.ObservedAtUnixSeconds,
            transcripts = observation.Transcripts.Select(transcript => new
            {
                transcript_id = transcript.TranscriptId,
                text = transcript.Text,
                producer = transcript.Producer
            }).ToList()
        };
        var source = pairs.Select(pair => new
        {
            candidate_token = pair.CandidateToken,
            new_observation = PromptObservation(observationsById[pair.NewObservationId]),
            earlier_observation = PromptObservation(observationsById[pair.TargetObservationId])
        }).ToList();
        var user = new StringBuilder();
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Independently verify every supplied candidate pair.");
        user.AppendLine("Use verify_link only when both transcripts contain positive evidence that the observations belong to one unfolding real-world event.");
        user.AppendLine("Otherwise use reject. Rejection means unresolved and does not prove the observations describe different events.");
        user.AppendLine("The candidate generator is retrieval only and is not evidence. Do not defer to its selection.");
        user.AppendLine("Timing, ordering, radio channel, agency, topic similarity, unit-number similarity, and generic operational language do not prove a link.");
        user.AppendLine("For verify_link, cite transcript_id values belonging to both endpoints and describe the concrete relationship.");
        user.AppendLine("Return exactly one decision for every candidate_token, in supplied order.");
        user.AppendLine();
        user.AppendLine("Candidate pairs:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));

        var candidateTokens = pairs.Select(pair => pair.CandidateToken).ToArray();
        var transcriptIds = pairs
            .SelectMany(pair => new[] { pair.NewObservationId, pair.TargetObservationId })
            .Distinct(StringComparer.Ordinal)
            .SelectMany(id => observationsById[id].Transcripts)
            .Select(transcript => transcript.TranscriptId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_event_microbatch_candidate_verification_v1",
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
                            minItems = pairs.Count,
                            maxItems = pairs.Count,
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    candidate_token = new { type = "string", @enum = candidateTokens },
                                    decision = new { type = "string", @enum = new[] { "verify_link", "reject" } },
                                    relationship_statement = new { type = "string" },
                                    uncertainty = new { type = "number", minimum = 0, maximum = 1 },
                                    new_evidence_transcript_ids = StringArray(transcriptIds),
                                    target_evidence_transcript_ids = StringArray(transcriptIds),
                                    unresolved_questions = StringArray(null)
                                },
                                required = new[]
                                {
                                    "candidate_token", "decision", "relationship_statement", "uncertainty",
                                    "new_evidence_transcript_ids", "target_evidence_transcript_ids", "unresolved_questions"
                                }
                            }
                        }
                    },
                    required = new[] { "decisions" }
                }
            }
        };
        return new IncidentEventStateMicroBatchVerificationPromptPayload(
            "You are the final source-grounded verifier for retrieved radio-observation pairs. You may verify a positive link or leave it unresolved. Application code owns identifiers, validation, projection, and persistence.",
            user.ToString(),
            responseFormat,
            pairs);
    }

    private static object StringArray(string[]? values) => new
    {
        type = "array",
        items = values is null ? new { type = "string" } : (object)new { type = "string", @enum = values }
    };
}

public static class IncidentEventStateMicroBatchVerificationValidator
{
    public static IncidentEventStateContractValidationResult Validate(
        IncidentEventStateMicroBatchVerificationPromptPayload prompt,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById,
        IncidentEventStateMicroBatchVerificationProposal proposal)
    {
        var errors = new List<string>();
        if (!string.Equals(proposal.PromptIdentity, IncidentEventStateMicroBatchVerificationPrompt.PromptIdentity, StringComparison.Ordinal))
            errors.Add("verification proposal prompt identity does not match the verifier contract");
        var expectedTokens = prompt.Pairs.Select(pair => pair.CandidateToken).ToList();
        if (!proposal.Decisions.Select(decision => decision.CandidateToken).SequenceEqual(expectedTokens, StringComparer.Ordinal))
            errors.Add("verifier must contain exactly one decision per candidate in supplied order");
        foreach (var decision in proposal.Decisions)
        {
            errors.AddRange(ValidateDecision(prompt, observationsById, decision).Errors);
        }
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentEventStateContractValidationResult ValidateDecision(
        IncidentEventStateMicroBatchVerificationPromptPayload prompt,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById,
        IncidentEventStateMicroBatchVerificationDecision decision)
    {
        var errors = new List<string>();
        var pair = prompt.Pairs.FirstOrDefault(pair =>
            string.Equals(pair.CandidateToken, decision.CandidateToken, StringComparison.Ordinal));
        if (pair is null)
        {
            errors.Add($"unknown verification candidate token '{decision.CandidateToken}'");
            return new IncidentEventStateContractValidationResult(false, errors);
        }
        if (decision.Uncertainty is < 0 or > 1)
            errors.Add($"verification '{decision.CandidateToken}' uncertainty must be between zero and one");
        if (decision.Decision == IncidentEventStateMicroBatchVerificationDecisionKind.Reject)
            return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
        if (string.IsNullOrWhiteSpace(decision.RelationshipStatement))
            errors.Add($"verified candidate '{decision.CandidateToken}' must describe the relationship");
        ValidateEvidence(
            decision.NewEvidenceTranscriptIds,
            observationsById[pair.NewObservationId],
            decision.CandidateToken,
            "new",
            errors);
        ValidateEvidence(
            decision.TargetEvidenceTranscriptIds,
            observationsById[pair.TargetObservationId],
            decision.CandidateToken,
            "target",
            errors);
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
            errors.Add($"verified candidate '{candidateToken}' requires {owner} transcript evidence");
        var allowed = observation.Transcripts.Select(transcript => transcript.TranscriptId).ToHashSet(StringComparer.Ordinal);
        foreach (var transcriptId in transcriptIds)
        {
            if (!allowed.Contains(transcriptId))
                errors.Add($"verified candidate '{candidateToken}' cites {owner} transcript '{transcriptId}' from another observation");
        }
    }
}
