namespace pizzad.Tests;

public sealed class IncidentBatchRelationshipTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PromptKeepsConstructionAndCandidateSourcesExplicitlySeparated()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "The patient fell out of his chair and hurt his tailbone."),
            Observation("call:2", "transcript:2", "The earlier patient hit his head and has Parkinson's."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:new-fall", ["call:1"]) };
        var candidates = new[] { new IncidentBatchCandidate("candidate:prior-fall", "projection:prior-fall", ["call:2"]) };

        var prompt = IncidentBatchRelationshipPrompt.Build(bundle, sources, candidates);

        Assert.Contains("constructed groups are immutable", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Never borrow a candidate fact", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("event:new-fall", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("candidate:prior-fall", prompt.UserPrompt, StringComparison.Ordinal);
        var schema = System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions());
        Assert.Contains("source_proposal_token", schema, StringComparison.Ordinal);
        Assert.Contains("confirmed_membership", schema, StringComparison.Ordinal);
        Assert.Contains("provisional_association", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void ProposalCanRetainSeveralProvisionalAssociationsForOneConstructedGroup()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "Did the worker notify her supervisor?"),
            Observation("call:2", "transcript:2", "A cleaning worker is locked inside the room."),
            Observation("call:3", "transcript:3", "The supervisor is responding to the building."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:worker-question", ["call:1"]) };
        var candidates = new[]
        {
            new IncidentBatchCandidate("candidate:locked-worker", "projection:locked-worker", ["call:2"]),
            new IncidentBatchCandidate("candidate:supervisor", "projection:supervisor", ["call:3"])
        };
        var proposal = Proposal(
            Relationship("event:worker-question", "candidate:locked-worker", "transcript:1", "worker notify her supervisor", "transcript:2", "cleaning worker"),
            Relationship("event:worker-question", "candidate:supervisor", "transcript:1", "supervisor", "transcript:3", "supervisor is responding"));

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, proposal);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void CandidateFactCannotValidateAsConstructedGroupEvidence()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "The patient fell out of his chair and hurt his tailbone."),
            Observation("call:2", "transcript:2", "The patient hit his head."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:new-fall", ["call:1"]) };
        var candidates = new[] { new IncidentBatchCandidate("candidate:prior-fall", "projection:prior-fall", ["call:2"]) };
        var relationship = Relationship(
            "event:new-fall",
            "candidate:prior-fall",
            "transcript:2",
            "hit his head",
            "transcript:2",
            "hit his head");

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, Proposal(relationship));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("outside its source boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstructedGroupCannotConfirmMembershipInTwoCandidates()
    {
        var bundle = Bundle(
            Observation("call:1", "transcript:1", "The same truck crash has critical injuries."),
            Observation("call:2", "transcript:2", "A truck crashed on County Road 725."),
            Observation("call:3", "transcript:3", "A truck crashed on Salem Road."));
        var sources = new[] { new IncidentBatchRelationshipSource("event:update", ["call:1"]) };
        var candidates = new[]
        {
            new IncidentBatchCandidate("candidate:one", "projection:one", ["call:2"]),
            new IncidentBatchCandidate("candidate:two", "projection:two", ["call:3"])
        };
        var first = Relationship("event:update", "candidate:one", "transcript:1", "same truck crash", "transcript:2", "truck crashed") with { Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership };
        var second = Relationship("event:update", "candidate:two", "transcript:1", "same truck crash", "transcript:3", "truck crashed") with { Disposition = IncidentBatchRelationshipDisposition.ConfirmedMembership };

        var validation = IncidentBatchRelationshipContract.ValidateProposal(bundle, sources, candidates, Proposal(first, second));

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("more than one confirmed membership", StringComparison.Ordinal));
    }

    private static IncidentBatchRelationship Relationship(
        string source,
        string candidate,
        string sourceTranscript,
        string sourceQuote,
        string candidateTranscript,
        string candidateQuote) =>
        new(
            source,
            candidate,
            IncidentBatchRelationshipDisposition.ProvisionalAssociation,
            "The two source boundaries may describe related operational activity.",
            0.4,
            [new IncidentEventStateTranscriptCitation(sourceTranscript, sourceQuote)],
            [new IncidentEventStateTranscriptCitation(candidateTranscript, candidateQuote)],
            [],
            []);

    private static IncidentBatchRelationshipProposal Proposal(params IncidentBatchRelationship[] relationships) =>
        new("proposal:relationships", Now, "test-model", IncidentBatchRelationshipPrompt.PromptIdentity, relationships);

    private static IncidentEventStateObservationBundle Bundle(params IncidentEventStateSourceObservation[] observations) =>
        new("bundle:relationship", Now, observations, []);

    private static IncidentEventStateSourceObservation Observation(string observationId, string transcriptId, string text) =>
        new(observationId, long.Parse(observationId.AsSpan("call:".Length)), 1000, string.Empty, null,
            [new IncidentEventStateTranscriptObservation(transcriptId, text, "test", Now)],
            new Dictionary<string, IncidentEventStateMetadataObservation>());
}
