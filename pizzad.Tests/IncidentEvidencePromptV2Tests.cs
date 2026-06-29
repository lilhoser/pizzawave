using System.Text.Json;

namespace pizzad.Tests;

public sealed class IncidentEvidencePromptV2Tests
{
    [Fact]
    public void Build_DefinesLlmAsClaimPredictorNotPersistenceAuthority()
    {
        var payload = IncidentEvidencePromptV2.Build(
            "ot",
            [
                new IncidentEvidencePromptCallV2(
                    801565,
                    new DateTime(2026, 6, 18, 22, 0, 0, DateTimeKind.Utc),
                    "ot",
                    "police",
                    "Report of vehicle burglary at 3115 Dodson Avenue.")
            ]);

        Assert.Contains("The server, not you, owns persistence", payload.SystemPrompt);
        Assert.Contains("source spans", payload.UserPrompt);
        Assert.Contains("Do not use ellipses", payload.UserPrompt);
        Assert.Contains("Never output null", payload.UserPrompt);
        Assert.Contains("Do not drop an initial dispatch call", payload.UserPrompt);
        Assert.Contains("keep the source-backed incident subset", payload.UserPrompt);
        Assert.Contains("Reject standalone routine activity", payload.UserPrompt);
        Assert.Contains("standalone EMS assist or lift assist", payload.UserPrompt);
        Assert.Contains("Use normalized snake_case event_class and event_subtype", payload.UserPrompt);
        Assert.Contains("Keep transcript abbreviations and shorthand only in span.text", payload.UserPrompt);
        Assert.Contains("Minimum non-empty structure", payload.UserPrompt);
        Assert.Contains("Do not use retrieval score", payload.UserPrompt);
        Assert.Contains("The server will decide final create, update, split, reject, conclude, or no-op", payload.UserPrompt);
    }

    [Fact]
    public void ResponseFormat_RequiresEvidenceObjectsAndSpans()
    {
        var json = JsonSerializer.Serialize(IncidentEvidencePromptV2.ResponseFormat(), EngineConfig.JsonOptions());

        Assert.Contains("pizzawave_incident_hypotheses_v2", json);
        Assert.Contains("events", json);
        Assert.Contains("locations", json);
        Assert.Contains("membership", json);
        Assert.Contains("conflicts", json);
        Assert.Contains("narrative", json);
        Assert.Contains("minItems", json);
        Assert.Contains("pattern", json);
        Assert.Contains("start_char", json);
        Assert.Contains("end_char", json);
    }
}
