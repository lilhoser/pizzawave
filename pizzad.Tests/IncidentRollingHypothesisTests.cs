using System.Text.Json;

namespace pizzad.Tests;

public sealed class IncidentRollingHypothesisTests
{
    [Fact]
    public void PromptUsesCompleteEvidenceRecordsInsteadOfModelFacingIdentifiers()
    {
        var first = Observation("call:100", 1_700_000_000, "Medic 4 respond to 10 Oak Street for chest pain.");
        var second = Observation("call:101", 1_700_000_015, "Medic 4 is en route to 10 Oak Street.");

        var prompt = IncidentRollingHypothesis.BuildPrompt([first, second]);
        var schema = JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions());

        Assert.DoesNotContain("call:100", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("call:101", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("observation_id", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Medic 4 respond to 10 Oak Street", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("\"enum\"", schema, StringComparison.Ordinal);
        Assert.Contains("Medic 4 is en route to 10 Oak Street", schema, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(schema);
        Assert.Equal(
            IncidentRollingHypothesis.MaximumReturnedEvents,
            document.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties").GetProperty("events").GetProperty("maxItems").GetInt32());
    }

    [Fact]
    public void ResolutionMapsSelectedEvidenceRecordsToApplicationOwnedObservations()
    {
        var first = Observation("call:100", 1_700_000_000, "Medic 4 respond to 10 Oak Street for chest pain.");
        var second = Observation("call:101", 1_700_000_015, "Medic 4 is en route to 10 Oak Street.");
        var unrelated = Observation("call:102", 1_700_000_030, "Unit 12 beginning a traffic stop.");
        var prompt = IncidentRollingHypothesis.BuildPrompt([first, second, unrelated]);
        var members = prompt.ObservationIdsByEvidenceRecord
            .Where(item => item.Value is "call:100" or "call:101")
            .Select(item => item.Key)
            .ToList();

        var result = IncidentRollingHypothesis.Resolve(prompt, new IncidentRollingHypothesisProposal([
            new IncidentRollingHypothesisDraft(
                "Medical response at 10 Oak Street",
                "Medic 4 was dispatched and responded to a chest-pain call.",
                "10 Oak Street",
                members,
                [])
        ]));

        Assert.True(result.IsValid);
        var incident = Assert.Single(result.Events);
        Assert.Equal(["call:100", "call:101"], incident.ObservationIds.Order(StringComparer.Ordinal));
        Assert.Equal(["call:102"], result.PendingObservationIds);
    }

    [Fact]
    public void OmittedEvidenceRemainsPendingRatherThanBecomingAStandaloneIncident()
    {
        var clear = Observation("call:100", 1_700_000_000, "Working structure fire at 10 Oak Street.");
        var unclear = Observation("call:101", 1_700_000_015, "Ten four.");
        var prompt = IncidentRollingHypothesis.BuildPrompt([clear, unclear]);
        var clearRecord = prompt.ObservationIdsByEvidenceRecord.Single(item => item.Value == "call:100").Key;

        var result = IncidentRollingHypothesis.Resolve(prompt, new IncidentRollingHypothesisProposal([
            new IncidentRollingHypothesisDraft("Structure fire", "A working structure fire was reported.", "10 Oak Street", [clearRecord], [])
        ]));

        Assert.True(result.IsValid);
        Assert.Equal(["call:101"], result.PendingObservationIds);
        Assert.DoesNotContain(result.Events, item => item.ObservationIds.Contains("call:101", StringComparer.Ordinal));
    }

    [Fact]
    public void DuplicateEvidenceRecordsFailClosedToPending()
    {
        var first = Observation("call:100", 1_700_000_000, "Medic 4 responding.");
        var duplicate = Observation("call:101", 1_700_000_000, "Medic 4 responding.");

        var prompt = IncidentRollingHypothesis.BuildPrompt([first, duplicate]);
        var result = IncidentRollingHypothesis.Resolve(prompt, new IncidentRollingHypothesisProposal([]));

        Assert.Empty(prompt.ObservationIdsByEvidenceRecord);
        Assert.Equal(["call:100", "call:101"], prompt.AmbiguousObservationIds);
        Assert.Equal(["call:100", "call:101"], result.PendingObservationIds);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void OneObservationCannotBelongToTwoProposedIncidents()
    {
        var observation = Observation("call:100", 1_700_000_000, "Vehicle rollover at 10 Oak Street.");
        var prompt = IncidentRollingHypothesis.BuildPrompt([observation]);
        var member = Assert.Single(prompt.ObservationIdsByEvidenceRecord).Key;

        var result = IncidentRollingHypothesis.Resolve(prompt, new IncidentRollingHypothesisProposal([
            new IncidentRollingHypothesisDraft("Rollover", "A rollover was reported.", "10 Oak Street", [member], []),
            new IncidentRollingHypothesisDraft("Crash", "A crash was reported.", "10 Oak Street", [member], [])
        ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("belongs to events 1 and 2", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownEvidenceRecordFailsValidation()
    {
        var observation = Observation("call:100", 1_700_000_000, "Vehicle rollover at 10 Oak Street.");
        var prompt = IncidentRollingHypothesis.BuildPrompt([observation]);

        var result = IncidentRollingHypothesis.Resolve(prompt, new IncidentRollingHypothesisProposal([
            new IncidentRollingHypothesisDraft("Rollover", "A rollover was reported.", "10 Oak Street", ["invented evidence"], [])
        ]));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("unknown or ambiguous evidence record", StringComparison.Ordinal));
        Assert.Equal(["call:100"], result.PendingObservationIds);
    }

    [Fact]
    public void BatchAdapterCreatesOneGroupedProposalWithoutModelIdentifiers()
    {
        var first = Observation("call:100", 1_700_000_000, "Medic 4 respond to 10 Oak Street for chest pain.");
        var second = Observation("call:101", 1_700_000_015, "Medic 4 is en route to 10 Oak Street.");
        var prompt = IncidentRollingHypothesis.BuildPrompt([first, second]);
        var draft = new IncidentRollingHypothesisDraft(
            "Medical response at 10 Oak Street",
            "Medic 4 responded to a chest-pain call.",
            "10 Oak Street",
            prompt.ObservationIdsByEvidenceRecord.Keys.ToList(),
            []);
        var resolution = IncidentRollingHypothesis.Resolve(prompt, new IncidentRollingHypothesisProposal([draft]));
        var bundle = new IncidentEventStateObservationBundle("bundle:1", DateTimeOffset.UtcNow, [first, second], []);

        var proposal = IncidentRollingBatchProposerAdapter.BuildBatchProposal(
            bundle,
            [first.ObservationId, second.ObservationId],
            [],
            ModelResult(resolution, draft));

        var incident = Assert.Single(proposal.Events);
        Assert.Equal(IncidentBatchEventDisposition.NewEvent, incident.Disposition);
        Assert.Equal(["call:100", "call:101"], incident.NewObservationIds.Order(StringComparer.Ordinal));
        Assert.Equal(2, incident.NewObservationEvidence.Count);
        Assert.Equal(IncidentRollingHypothesis.PromptIdentity, proposal.PromptIdentity);
    }

    [Fact]
    public void BatchAdapterRejectsAProposalThatWouldMergeTwoExistingIncidents()
    {
        var first = Observation("call:100", 1_700_000_000, "Fire response at 10 Oak Street.");
        var second = Observation("call:101", 1_700_000_015, "Medical response at 20 Pine Street.");
        var update = Observation("call:102", 1_700_000_030, "Additional unit responding.");
        var prompt = IncidentRollingHypothesis.BuildPrompt([first, second, update]);
        var draft = new IncidentRollingHypothesisDraft(
            "Combined response",
            "Multiple units are responding.",
            string.Empty,
            prompt.ObservationIdsByEvidenceRecord.Keys.ToList(),
            []);
        var resolution = IncidentRollingHypothesis.Resolve(prompt, new IncidentRollingHypothesisProposal([draft]));
        var bundle = new IncidentEventStateObservationBundle("bundle:1", DateTimeOffset.UtcNow, [first, second, update], []);

        var error = Assert.Throws<InvalidDataException>(() => IncidentRollingBatchProposerAdapter.BuildBatchProposal(
            bundle,
            [update.ObservationId],
            [
                new IncidentBatchCandidate("candidate:a", "event:a", [first.ObservationId], true),
                new IncidentBatchCandidate("candidate:b", "event:b", [second.ObservationId], true)
            ],
            ModelResult(resolution, draft)));

        Assert.Contains("overlaps more than one existing incident", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolutionRejectsReturnedHypothesesWithUnresolvedMembership()
    {
        var observation = Observation("call:100", 1_700_000_000, "Possible vehicle incident near Oak Street.");
        var prompt = IncidentRollingHypothesis.BuildPrompt([observation]);
        var draft = new IncidentRollingHypothesisDraft(
            "Possible vehicle incident",
            "A possible vehicle incident was mentioned.",
            string.Empty,
            prompt.ObservationIdsByEvidenceRecord.Keys.ToList(),
            ["It is unclear whether a response is required."]);
        var resolution = IncidentRollingHypothesis.Resolve(prompt, new IncidentRollingHypothesisProposal([draft]));
        Assert.False(resolution.IsValid);
        Assert.Contains(resolution.Errors, error => error.Contains("too many unresolved questions", StringComparison.Ordinal));
    }

    private static IncidentEventStateSourceObservation Observation(string id, long observedAt, string transcript) =>
        new(
            id,
            long.TryParse(id.AsSpan(id.IndexOf(':') + 1), out var callId) ? callId : null,
            observedAt,
            string.Empty,
            null,
            [new IncidentEventStateTranscriptObservation($"transcript:{id}", transcript, "test", null)],
            new Dictionary<string, IncidentEventStateMetadataObservation>());

    private static IncidentRollingModelResult ModelResult(
        IncidentRollingHypothesisResolution resolution,
        params IncidentRollingHypothesisDraft[] drafts) =>
        new(new IncidentRollingHypothesisProposal(drafts), resolution, "test-model", 10, 100, 10, 110);
}
