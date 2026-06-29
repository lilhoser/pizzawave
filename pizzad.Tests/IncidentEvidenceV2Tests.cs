using System.Text.Json;

namespace pizzad.Tests;

public sealed class IncidentEvidenceV2Tests
{
    [Fact]
    public void VerifySpans_AcceptsExactSourceBackedClaims()
    {
        const string transcript = "Engine 3 responding CPR in progress at 804 Main Street.";
        var start = transcript.IndexOf("CPR in progress", StringComparison.Ordinal);
        var hypothesis = HypothesisWithSpan(new EvidenceSpanV2(802453, start, start + "CPR in progress".Length, "CPR in progress"));

        var result = IncidentEvidenceClaimVerifier.VerifySpans(
            hypothesis,
            new Dictionary<long, string> { [802453] = transcript });

        Assert.True(result.Accepted);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void VerifySpans_RejectsUngroundedClaimText()
    {
        const string transcript = "Engine 3 responding CPR in progress at 804 Main Street.";
        var start = transcript.IndexOf("CPR in progress", StringComparison.Ordinal);
        var hypothesis = HypothesisWithSpan(new EvidenceSpanV2(802453, start, start + "CPR in progress".Length, "vehicle crash"));

        var result = IncidentEvidenceClaimVerifier.VerifySpans(
            hypothesis,
            new Dictionary<long, string> { [802453] = transcript });

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, error => error.Contains("text mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VerifySpans_AcceptsExactQuotedTextWhenModelOffsetsAreWrong()
    {
        const string transcript = "Medic responding chest pain at 523 Callaway Court.";
        var hypothesis = HypothesisWithSpan(new EvidenceSpanV2(802453, 999, 1009, "chest pain"));

        var result = IncidentEvidenceClaimVerifier.VerifySpans(
            hypothesis,
            new Dictionary<long, string> { [802453] = transcript });

        Assert.True(result.Accepted);
    }

    [Fact]
    public void VerifySpans_AcceptsQuoteEquivalentTextWhenPunctuationDiffers()
    {
        const string transcript = "Food City Lafayette, 311 Northam Street at Food City.";
        var hypothesis = HypothesisWithSpan(new EvidenceSpanV2(802453, 999, 1009, "Food City Lafayette 311 Northam Street"));

        var result = IncidentEvidenceClaimVerifier.VerifySpans(
            hypothesis,
            new Dictionary<long, string> { [802453] = transcript });

        Assert.True(result.Accepted);
    }

    [Fact]
    public void SnakeCaseModelHypothesis_DeserializesAndCanBeServerDecided()
    {
        const string transcript = "Medic responding chest pain at 523 Callaway Court.";
        var start = transcript.IndexOf("chest pain", StringComparison.Ordinal);
        var end = start + "chest pain".Length;
        var json = $$"""
        {
          "hypothesis_id": "h-1",
          "system_short_name": "ot",
          "candidate_call_ids": [797519],
          "model_confidence": 0.91,
          "events": [
            {
              "event_class": "medical",
              "event_subtype": "chest_pain",
              "strength": "strong",
              "source_call_ids": [797519],
              "spans": [
                { "call_id": 797519, "start_char": {{start}}, "end_char": {{end}}, "text": "chest pain" }
              ]
            }
          ],
          "locations": [],
          "membership": [
            {
              "call_id": 797519,
              "role": "primary_event",
              "decision": "accept",
              "reasons": ["source-backed primary medical event"],
              "spans": [
                { "call_id": 797519, "start_char": {{start}}, "end_char": {{end}}, "text": "chest pain" }
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
                  { "call_id": 797519, "start_char": {{start}}, "end_char": {{end}}, "text": "chest pain" }
                ]
              }
            ]
          }
        }
        """;

        var hypothesis = JsonSerializer.Deserialize<IncidentHypothesisV2>(json, EngineConfig.JsonOptions());
        Assert.NotNull(hypothesis);

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [797519] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([797519], decision.AcceptedCallIds);
    }

    private static IncidentHypothesisV2 HypothesisWithSpan(EvidenceSpanV2 span) => new(
        "hypothesis-1",
        "ot",
        [span.CallId],
        0.9,
        [new EventEvidenceV2("medical", "non_breathing", "strong", [span.CallId], [span])],
        [],
        [new MembershipEvidenceV2(span.CallId, "primary_event", "accept", ["source-backed event evidence"], [span])],
        [],
        new NarrativeEvidenceV2("Non-breathing patient", "CPR in progress.", [new NarrativeFactV2("event", "CPR in progress", [span])]));
}
