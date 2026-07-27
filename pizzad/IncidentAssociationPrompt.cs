using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentAssociationPromptPayload(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat);

public static class IncidentAssociationPrompt
{
    public const string PromptIdentity = "incident-association-constructor-v1";

    public static IncidentAssociationPromptPayload Build(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentAssociationCandidate> candidates)
    {
        var validation = IncidentAssociationContract.ValidateInput(
            bundle,
            PriorProjectionForPrompt(candidates),
            newObservationId,
            candidates);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(bundle));
        if (candidates.Count == 0)
            throw new ArgumentException("an association prompt requires at least one candidate", nameof(candidates));

        var observationsById = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var source = new
        {
            new_observation = PromptObservation(observationsById[newObservationId]),
            candidate_events = candidates.Select(candidate => new
            {
                candidate_token = candidate.CandidateToken,
                source_observations = candidate.ObservationIds
                    .Select(observationId => PromptObservation(observationsById[observationId]))
                    .ToList()
            }).ToList()
        };

        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Compare the new radio observation with every supplied candidate event using only the transcripts in this source bundle.");
        user.AppendLine("Return confirmed_membership for at most one candidate, and only when both sides directly support that they belong to one unfolding real-world event.");
        user.AppendLine("Return provisional_association for each candidate that has specific positive evidence of a plausible relationship but retains meaningful uncertainty.");
        user.AppendLine("Several provisional associations are allowed. Omit unsupported candidates. An omitted candidate remains unresolved and is not evidence of a different event.");
        user.AppendLine("Do not create labels, categories, roles, talkgroup rules, or event states. Timing, system identity, retrieval rank, and radio metadata do not prove a relationship.");
        user.AppendLine("Every returned relationship must cite transcript_id values from both the new observation and that relationship's candidate event.");
        user.AppendLine("Copy identifiers exactly. State concrete alternatives and unresolved questions when they materially explain uncertainty; arrays may otherwise be empty.");
        user.AppendLine();
        user.AppendLine("Source bundle:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));

        return new IncidentAssociationPromptPayload(
            "You identify source-grounded incident relationships. Application code owns event identifiers, validates every citation, creates singleton events, and applies only structurally valid confirmed membership. Provisional associations never merge events.",
            user.ToString(),
            ResponseFormat(bundle, newObservationId, candidates));
    }

    private static object ResponseFormat(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentAssociationCandidate> candidates)
    {
        var observations = bundle.Observations.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var allCandidateTranscriptIds = candidates
            .SelectMany(candidate => candidate.ObservationIds)
            .SelectMany(observationId => observations[observationId].Transcripts)
            .Select(transcript => transcript.TranscriptId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var newTranscriptIds = observations[newObservationId].Transcripts
            .Select(transcript => transcript.TranscriptId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_association_constructor_v1",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        relationships = new
                        {
                            type = "array",
                            items = new
                            {
                                type = "object",
                                additionalProperties = false,
                                properties = new
                                {
                                    candidate_token = new { type = "string", @enum = candidates.Select(item => item.CandidateToken).ToArray() },
                                    disposition = new { type = "string", @enum = new[] { "confirmed_membership", "provisional_association" } },
                                    relationship_statement = new { type = "string" },
                                    uncertainty = new { type = "number", minimum = 0, maximum = 1 },
                                    new_observation_evidence = CitationArraySchema(newTranscriptIds),
                                    candidate_evidence = CitationArraySchema(allCandidateTranscriptIds),
                                    alternative_interpretations = StringArraySchema(),
                                    unresolved_questions = StringArraySchema()
                                },
                                required = new[]
                                {
                                    "candidate_token",
                                    "disposition",
                                    "relationship_statement",
                                    "uncertainty",
                                    "new_observation_evidence",
                                    "candidate_evidence",
                                    "alternative_interpretations",
                                    "unresolved_questions"
                                }
                            }
                        }
                    },
                    required = new[] { "relationships" }
                }
            }
        };
    }

    private static object PromptObservation(IncidentEventStateSourceObservation observation) => new
    {
        observed_at_unix_seconds = observation.ObservedAtUnixSeconds,
        transcripts = observation.Transcripts.Select(transcript => new
        {
            transcript_id = transcript.TranscriptId,
            text = transcript.Text,
            producer = transcript.Producer
        }).ToList()
    };

    private static object CitationArraySchema(IEnumerable<string> transcriptIds) => new
    {
        type = "array",
        items = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                transcript_id = new { type = "string", @enum = transcriptIds.ToArray() }
            },
            required = new[] { "transcript_id" }
        }
    };

    private static object StringArraySchema() => new
    {
        type = "array",
        items = new { type = "string" }
    };

    private static IncidentAssociationProjection PriorProjectionForPrompt(IReadOnlyList<IncidentAssociationCandidate> candidates) =>
        new(
            "prompt-validation",
            "prompt-validation",
            DateTimeOffset.UnixEpoch,
            [],
            candidates.Select(candidate => new IncidentAssociationProjectionEvent(candidate.ProjectionEventId, candidate.ObservationIds, [])).ToList(),
            []);
}
