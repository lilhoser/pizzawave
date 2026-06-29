using System.Text.Json;

namespace pizzad.Tests;

public sealed class IncidentEvidenceResponseParserV2Tests
{
    [Fact]
    public void ParseOpenAiChatCompletion_FillsSystemFromContextWhenModelOmitsIt()
    {
        const string transcript = "Medic responding chest pain at 523 Callaway Court.";
        var start = transcript.IndexOf("chest pain", StringComparison.Ordinal);
        var end = start + "chest pain".Length;
        var content = $$"""
        {
          "hypotheses": [
            {
              "hypothesis_id": "h-1",
              "candidate_incident_key": "",
              "candidate_call_ids": ["797519"],
              "model_confidence": "0.91",
              "events": [
                {
                  "event_class": "medical",
                  "event_subtype": "chest_pain",
                  "strength": "strong",
                  "source_call_ids": ["797519"],
                  "spans": [
                    { "call_id": "797519", "start_char": "{{start}}", "end_char": "{{end}}", "text": "chest pain" }
                  ]
                }
              ],
              "locations": [],
              "membership": [
                {
                  "call_id": "797519",
                  "role": "primary_event",
                  "decision": "accept",
                  "reasons": ["source-backed primary medical event"],
                  "spans": [
                    { "call_id": "797519", "start_char": "{{start}}", "end_char": "{{end}}", "text": "chest pain" }
                  ]
                }
              ],
              "conflicts": [],
              "narrative": {
                "title": "Chest pain",
                "detail": "Medic responding for chest pain.",
                "facts": [
                  {
                    "kind": "event",
                    "text": "chest pain",
                    "spans": [
                      { "call_id": "797519", "start_char": "{{start}}", "end_char": "{{end}}", "text": "chest pain" }
                    ]
                  }
                ]
              }
            }
          ]
        }
        """;
        var response = new
        {
            choices = new[]
            {
                new
                {
                    message = new
                    {
                        content
                    }
                }
            }
        };

        var parsed = IncidentEvidenceResponseParserV2.ParseOpenAiChatCompletion(
            JsonSerializer.Serialize(response, EngineConfig.JsonOptions()),
            "ot");

        var hypothesis = Assert.Single(parsed.Hypotheses);
        Assert.Equal("ot", hypothesis.SystemShortName);

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [797519] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([797519], decision.AcceptedCallIds);
    }
}
