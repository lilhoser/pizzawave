using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record IncidentEventStateLinkPromptPayload(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat);

public static class IncidentEventStateLinkPrompt
{
    public const string PromptIdentity = "incident-event-link-only-v1";

    public static IncidentEventStateLinkPromptPayload Build(
        IncidentEventStateObservationBundle bundle,
        string newObservationId,
        IReadOnlyList<IncidentEventStateLinkCandidate> candidates)
    {
        var validation = IncidentEventStateLinkContractValidator.ValidateInput(
            bundle,
            PriorProjectionForPrompt(candidates),
            newObservationId,
            candidates);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join("; ", validation.Errors), nameof(bundle));
        if (candidates.Count == 0)
            throw new ArgumentException("a link prompt requires at least one candidate", nameof(candidates));

        var observationsById = bundle.Observations.ToDictionary(
            observation => observation.ObservationId,
            StringComparer.Ordinal);
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
        user.AppendLine("Determine whether positive source evidence supports linking the new observation to exactly one candidate event.");
        user.AppendLine("Use propose_link only when the new observation and the selected candidate both contain positive evidence of one unfolding real-world event.");
        user.AppendLine("Otherwise use abstain. Abstention means unresolved; it does not mean the observations describe different events.");
        user.AppendLine("You may not declare a different event, create an event, split an event, assign a category or role, or change event state.");
        user.AppendLine("Timing, system, talkgroup, frequency, retrieval, and metadata may provide context, but none proves a link by itself.");
        user.AppendLine("For propose_link, select exactly one supplied candidate_token and cite literal transcript substrings from both the new observation and that candidate.");
        user.AppendLine("Do not alter a quote, combine separate fragments, or cite a transcript from an unselected candidate.");
        user.AppendLine("For abstain, return an empty candidate_token and at least one concrete unresolved question.");
        user.AppendLine();
        user.AppendLine("Source bundle:");
        user.AppendLine(JsonSerializer.Serialize(source, EngineConfig.JsonOptions()));

        return new IncidentEventStateLinkPromptPayload(
            "You propose one source-grounded connection between a new radio observation and an existing event, or abstain. Your output is shadow evidence only. Application code owns identifiers, state projection, validation, and persistence.",
            user.ToString(),
            ResponseFormat(candidates));
    }

    public static object ResponseFormat(IReadOnlyList<IncidentEventStateLinkCandidate> candidates) => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "pizzawave_incident_event_link_only_v1",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    decision = new { type = "string", @enum = new[] { "propose_link", "abstain" } },
                    candidate_token = new
                    {
                        type = "string",
                        @enum = candidates.Select(candidate => candidate.CandidateToken).Append(string.Empty).Distinct(StringComparer.Ordinal).ToArray()
                    },
                    relationship_statement = new { type = "string" },
                    uncertainty = new { type = "number", minimum = 0, maximum = 1 },
                    new_observation_evidence = CitationArraySchema(),
                    candidate_evidence = CitationArraySchema(),
                    unresolved_questions = StringArraySchema()
                },
                required = new[]
                {
                    "decision",
                    "candidate_token",
                    "relationship_statement",
                    "uncertainty",
                    "new_observation_evidence",
                    "candidate_evidence",
                    "unresolved_questions"
                }
            }
        }
    };

    private static object PromptObservation(IncidentEventStateSourceObservation observation) => new
    {
        observed_at_unix_seconds = observation.ObservedAtUnixSeconds,
        transcripts = observation.Transcripts.Select(transcript => new
        {
            transcript_id = transcript.TranscriptId,
            text = transcript.Text,
            producer = transcript.Producer
        }).ToList(),
        metadata = observation.Metadata.ToDictionary(
            item => item.Key,
            item => new
            {
                value = item.Value.Value,
                origin = item.Value.Origin.ToString()
            },
            StringComparer.Ordinal)
    };

    private static object CitationArraySchema() => new
    {
        type = "array",
        items = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                transcript_id = new { type = "string" },
                exact_quote = new { type = "string" }
            },
            required = new[] { "transcript_id", "exact_quote" }
        }
    };

    private static object StringArraySchema() => new
    {
        type = "array",
        items = new { type = "string" }
    };

    private static IncidentEventStateLinkProjection PriorProjectionForPrompt(
        IReadOnlyList<IncidentEventStateLinkCandidate> candidates) =>
        new(
            "prompt-validation",
            "prompt-validation",
            DateTimeOffset.UnixEpoch,
            [],
            candidates.Select(candidate => new IncidentEventStateLinkProjectionEvent(
                candidate.ProjectionEventId,
                candidate.ObservationIds,
                [])).ToList());
}
