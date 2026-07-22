using System.Text;

namespace pizzad;

public sealed record IncidentEvidencePromptCallV2(
    long CallId,
    DateTime TimestampUtc,
    string SystemShortName,
    string Category,
    string Transcript,
    string RetrievalReason = "",
    double RetrievalScore = 0);

public sealed record IncidentEvidencePromptPayloadV2(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat);

public static class IncidentEvidencePromptV2
{
    public static IncidentEvidencePromptPayloadV2 Build(
        string systemShortName,
        IReadOnlyList<IncidentEvidencePromptCallV2> candidateCalls,
        IReadOnlyList<IncidentDto>? activeIncidents = null)
    {
        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON that matches the schema. Do not place the answer in reasoning_content.");
        user.AppendLine($"System boundary: {systemShortName}.");
        user.AppendLine("Task: propose zero or more incident hypotheses from the candidate calls.");
        user.AppendLine("The output is evidence for a server decision, not the decision itself.");
        user.AppendLine("For every event, location, membership, conflict, and narrative fact, cite exact source spans by call_id, start_char, end_char, and text.");
        user.AppendLine("span.text must be an exact substring copied from the transcript, including casing, punctuation, grammar errors, and transcription mistakes. Do not paraphrase span.text. Do not use ellipses or combine separate transcript fragments into one span.");
        user.AppendLine("Use short literal spans when possible, usually 2 to 8 words. Do not cite a long sentence unless the whole sentence is exactly copied from the transcript.");
        user.AppendLine("Never output null in spans, reasons, or facts. If you cannot cite an exact span for an event, location, membership decision, conflict, or narrative fact, do not make that claim.");
        user.AppendLine("If hypotheses is not empty, every hypothesis must include at least one events item with strength strong, primary, or confirmed. Do not put event evidence only in narrative.facts.");
        user.AppendLine("Use normalized snake_case event_class and event_subtype values such as traffic/traffic_crash, traffic/injury_crash, fire/fire_alarm, structure_fire/structure_fire, medical/chest_pain, medical/difficulty_breathing, violent_police/assault, shots_fired/shots_fired, burglary/burglary, or service_call/911_hang_up. Keep transcript abbreviations and shorthand only in span.text.");
        user.AppendLine("Every span.call_id must be one of the candidate call IDs. Never use 0 or an invented call_id.");
        user.AppendLine("Do not use retrieval score, semantic similarity, talkgroup, category, or an existing title as proof. Those fields only explain why a call was reviewed.");
        user.AppendLine("Classify every candidate call as primary_event, continuation, logistics, routine, unrelated, or conflicting.");
        user.AppendLine("membership is mandatory. Include exactly one membership row for every candidate_call_id. For the first source-backed dispatch/event call use role=primary_event and decision=accept. For same-event updates use role=continuation and decision=accept. For unrelated or routine calls use decision=reject.");
        user.AppendLine("Do not drop an initial dispatch call merely because later calls have clearer event wording. If it is the same incident, include it with an exact dispatch or location span.");
        user.AppendLine("When a candidate set mixes unrelated calls, keep the source-backed incident subset and reject the unrelated calls. Do not reject the whole hypothesis merely because retrieval brought in an unrelated neighbor.");
        user.AppendLine("Reject standalone routine activity: non-emergency transport, hospital handoff, facility transfer, supervisor/driver/schedule/computer operations, routine traffic stops, tag checks, warrant checks, music/noise complaints without threat, and standalone EMS assist or lift assist. If such traffic clearly belongs to an existing source-backed emergency, mark it as logistics or continuation for that parent incident instead of a new incident.");
        user.AppendLine("Use conflicts for same symptom at different addresses, unrelated locations, unrelated patients, unrelated vehicles, or semantic neighbors with no shared event proof.");
        user.AppendLine("Use narrative facts only when the exact fact is supported by retained source spans.");
        user.AppendLine("If no real incident is source-backed, return hypotheses: [].");
        user.AppendLine("Minimum non-empty structure: hypotheses[0].events has at least one strong event with spans; hypotheses[0].membership has one row per candidate call; hypotheses[0].narrative.facts has at least one fact with spans.");
        user.AppendLine("Set candidate_incident_key to an active incident key only as a suggestion. The server will decide final create, update, split, reject, conclude, or no-op.");
        user.AppendLine();
        user.AppendLine("Active incidents for context:");
        foreach (var incident in (activeIncidents ?? []).OrderByDescending(row => row.LastSeen).Take(12).OrderBy(row => row.LastSeen))
        {
            var key = string.IsNullOrWhiteSpace(incident.IncidentKey) ? $"legacy-{incident.Id}" : incident.IncidentKey;
            user.AppendLine($"- incident_key={key}; title={Trim(incident.Title, 100)}; detail={Trim(incident.Detail, 140)}; call_ids=[{string.Join(",", incident.Calls.Select(call => call.CallId).Order())}]");
        }

        user.AppendLine();
        user.AppendLine("Candidate calls:");
        foreach (var call in candidateCalls.OrderBy(row => row.TimestampUtc).ThenBy(row => row.CallId))
        {
            user.AppendLine($"- call_id={call.CallId}; utc={call.TimestampUtc:O}; system={call.SystemShortName}; category={call.Category}; retrieval_reason={Trim(call.RetrievalReason, 80)}; retrieval_score={call.RetrievalScore:0.000}");
            user.AppendLine($"  transcript={call.Transcript}");
        }

        return new IncidentEvidencePromptPayloadV2(
            "You extract structured incident evidence from public safety radio transcripts. You predict claims with exact source spans. The server, not you, owns persistence, incident identity, final membership, and conflict enforcement.",
            user.ToString(),
            ResponseFormat());
    }

    public static object ResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "pizzawave_incident_hypotheses_v2",
            strict = false,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    hypotheses = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                hypothesis_id = new { type = "string" },
                                candidate_incident_key = new { type = "string" },
                                model_confidence = new { type = "number" },
                                candidate_call_ids = NumberArraySchema(),
                                events = new
                                {
                                    type = "array",
                                    minItems = 1,
                                    items = new
                                    {
                                        type = "object",
                                        additionalProperties = false,
                                        properties = new
                                        {
                                            event_class = new { type = "string" },
                                            event_subtype = new { type = "string" },
                                            strength = new { type = "string", @enum = new[] { "none", "weak", "continuation", "strong", "primary", "confirmed" } },
                                            source_call_ids = NumberArraySchema(),
                                            spans = SpanArraySchema()
                                        },
                                        required = new[] { "event_class", "event_subtype", "strength", "source_call_ids", "spans" }
                                    }
                                },
                                locations = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        additionalProperties = false,
                                        properties = new
                                        {
                                            kind = new { type = "string", @enum = new[] { "address", "intersection", "route", "landmark", "unknown" } },
                                            display = new { type = "string" },
                                            normalized_key = new { type = "string" },
                                            confidence = new { type = "string", @enum = new[] { "unknown", "low", "medium", "high" } },
                                            source_call_ids = NumberArraySchema(),
                                            spans = SpanArraySchema()
                                        },
                                        required = new[] { "kind", "display", "normalized_key", "confidence", "source_call_ids", "spans" }
                                    }
                                },
                                membership = new
                                {
                                    type = "array",
                                    minItems = 1,
                                    items = new
                                    {
                                        type = "object",
                                        additionalProperties = false,
                                        properties = new
                                        {
                                            call_id = new { type = "number" },
                                            role = new { type = "string", @enum = new[] { "primary_event", "continuation", "logistics", "routine", "unrelated", "conflicting" } },
                                            decision = new { type = "string", @enum = new[] { "accept", "reject", "hold" } },
                                            reasons = StringArraySchema(),
                                            spans = SpanArraySchema()
                                        },
                                        required = new[] { "call_id", "role", "decision", "reasons", "spans" }
                                    }
                                },
                                conflicts = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        additionalProperties = false,
                                        properties = new
                                        {
                                            conflict_type = new { type = "string" },
                                            call_ids = NumberArraySchema(),
                                            reason = new { type = "string" },
                                            spans = SpanArraySchema()
                                        },
                                        required = new[] { "conflict_type", "call_ids", "reason", "spans" }
                                    }
                                },
                                narrative = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        title = new { type = "string" },
                                        detail = new { type = "string" },
                                        facts = new
                                        {
                                            type = "array",
                                            minItems = 1,
                                            items = new
                                            {
                                                type = "object",
                                                additionalProperties = false,
                                                properties = new
                                                {
                                                    kind = new { type = "string" },
                                                    text = new { type = "string" },
                                                    spans = SpanArraySchema()
                                                },
                                                required = new[] { "kind", "text", "spans" }
                                            }
                                        }
                                    },
                                    required = new[] { "title", "detail", "facts" }
                                }
                            },
                            required = new[] { "hypothesis_id", "candidate_incident_key", "model_confidence", "candidate_call_ids", "events", "locations", "membership", "conflicts", "narrative" }
                        }
                    }
                },
                required = new[] { "hypotheses" }
            }
        }
    };

    private static object StringArraySchema() => new
    {
        type = "array",
        items = new { type = "string" }
    };

    private static object NumberArraySchema() => new
    {
        type = "array",
        minItems = 1,
        items = new { type = "number" }
    };

    private static object SpanArraySchema() => new
    {
        type = "array",
        minItems = 1,
        items = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                call_id = new { type = "number" },
                start_char = new { type = "number" },
                end_char = new { type = "number" },
                text = new { type = "string" }
            },
            required = new[] { "call_id", "start_char", "end_char", "text" }
        }
    };

    private static string Trim(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "...";
    }
}
