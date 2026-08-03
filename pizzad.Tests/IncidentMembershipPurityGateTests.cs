using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad.Tests;

public sealed class IncidentMembershipPurityGateTests
{
    [Fact]
    public async Task MapsDecisionToApplicationOwnedOwnerWithoutGeneratedIdentity()
    {
        var context = CandidateContext();
        var result = await new EvidencePurityAdapter(
            new FixedDecider(EvidencePurityDisposition.MultipleEvents))
            .DecideAsync(context, default);

        Assert.Equal("candidate", result.Owner.OwnerType);
        Assert.Equal(99001, result.Owner.OwnerId);
        Assert.Equal("private-candidate-owner", result.Owner.ObservationId);
        Assert.Equal(EvidencePurityDisposition.MultipleEvents, result.Disposition);
    }

    [Fact]
    public void CandidatePromptContainsCompleteEvidenceButNoPrivateIdentity()
    {
        var prompt = EvidencePurityPromptBuilder.Build(CandidateContext());

        Assert.Contains("Candidate contains event Alpha and event Beta", prompt.UserPrompt);
        Assert.Contains("complete captured radio conversation segment", prompt.UserPrompt);
        Assert.DoesNotContain("99001", prompt.UserPrompt);
        Assert.DoesNotContain("private-candidate-owner", prompt.UserPrompt);
        Assert.DoesNotContain("private-candidate-call", prompt.UserPrompt);
    }

    [Fact]
    public void IncidentPromptContainsEveryCallAndNoPrivateIdentity()
    {
        var prompt = EvidencePurityPromptBuilder.Build(IncidentContext());

        Assert.Contains("First incident transcript", prompt.UserPrompt);
        Assert.Contains("Second incident transcript", prompt.UserPrompt);
        Assert.Contains("every call currently in this existing incident", prompt.UserPrompt);
        Assert.Contains("Compare every call's specific location, patient or subject", prompt.UserPrompt);
        Assert.Contains("only makes a shared event possible rather than supported", prompt.UserPrompt);
        Assert.DoesNotContain("88002", prompt.UserPrompt);
        Assert.DoesNotContain("private-incident-owner", prompt.UserPrompt);
        Assert.DoesNotContain("private-first", prompt.UserPrompt);
        Assert.DoesNotContain("private-second", prompt.UserPrompt);
    }

    [Fact]
    public async Task DuplicateTranscriptTextRetainsCompleteApplicationBindings()
    {
        var same = "Repeated transcript";
        var context = new EvidencePurityContext(
            new EvidencePurityOwnerIdentity("incident", 55, "owner-private"),
            EvidencePurityScope.ExistingIncident,
            [Source(1, "first-private", same), Source(2, "second-private", same)]);
        var decider = new CapturingDecider(EvidencePurityDisposition.OneEvent);

        var result = await new EvidencePurityAdapter(decider).DecideAsync(context, default);

        Assert.Same(context, decider.Context);
        Assert.Equal(2, Count(decider.Prompt!.UserPrompt, same));
        Assert.DoesNotContain("first-private", decider.Prompt.UserPrompt);
        Assert.DoesNotContain("second-private", decider.Prompt.UserPrompt);
        Assert.Equal(EvidencePurityDisposition.OneEvent, result.Disposition);
    }

    [Fact]
    public void CandidateRequiresExactlyOneCompleteConversationSegment()
    {
        var exception = Assert.Throws<IncidentMembershipContractException>(() =>
            new EvidencePurityContext(
                new EvidencePurityOwnerIdentity("candidate", 1, "owner"),
                EvidencePurityScope.CandidateConversationSegment,
                [Source(1, "one", "One"), Source(2, "two", "Two")]));

        Assert.Contains("exactly one", exception.Message);
    }

    [Fact]
    public void OversizedIncidentFailsInsteadOfTruncatingEvidence()
    {
        var calls = Enumerable.Range(1, IncidentTargetMembershipContext.MaximumEstablishedCalls + 1)
            .Select(index => Source(index, $"call-{index}", $"Transcript {index}"));

        var exception = Assert.Throws<IncidentMembershipContractException>(() =>
            new EvidencePurityContext(
                new EvidencePurityOwnerIdentity("incident", 1, "owner"),
                EvidencePurityScope.ExistingIncident,
                calls));

        Assert.Contains("complete calls", exception.Message);
    }

    [Theory]
    [InlineData(EvidencePurityDisposition.OneEvent, EvidencePurityDisposition.OneEvent, true)]
    [InlineData(EvidencePurityDisposition.MultipleEvents, EvidencePurityDisposition.OneEvent, false)]
    [InlineData(EvidencePurityDisposition.Unresolved, EvidencePurityDisposition.OneEvent, false)]
    [InlineData(EvidencePurityDisposition.OneEvent, EvidencePurityDisposition.MultipleEvents, false)]
    [InlineData(EvidencePurityDisposition.OneEvent, EvidencePurityDisposition.Unresolved, false)]
    public void GateAllowsMembershipOnlyWhenBothInputsContainOneEvent(
        EvidencePurityDisposition incident,
        EvidencePurityDisposition candidate,
        bool expected)
    {
        var result = IncidentMembershipPurityGate.Evaluate(Result("incident", incident), Result("candidate", candidate));

        Assert.Equal(expected, result.MayEvaluateMembership);
        Assert.Equal(incident, result.ExistingIncident);
        Assert.Equal(candidate, result.CandidateConversationSegment);
    }

    [Fact]
    public async Task ModelFailureFailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EvidencePurityAdapter(new FailingDecider()).DecideAsync(CandidateContext(), default));
    }

    [Fact]
    public async Task OpenAiTransportUsesStrictThreeChoiceSchema()
    {
        string? requestBody = null;
        var handler = new StubHttpHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"purity-model","choices":[{"message":{"content":"{\"decision\":\"multiple_events\"}"}}],"usage":{"prompt_tokens":30,"completion_tokens":4,"total_tokens":34}}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var decider = new OpenAiEvidencePurityDecider(
            new HttpClient(handler), "http://model/v1", "", "purity-model");

        var result = await new EvidencePurityAdapter(decider).DecideAsync(CandidateContext(), default);

        Assert.Equal(EvidencePurityDisposition.MultipleEvents, result.Disposition);
        Assert.Equal(34, result.TotalTokens);
        using var request = JsonDocument.Parse(requestBody!);
        var schema = request.RootElement.GetProperty("response_format").GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());
        var choices = schema.GetProperty("schema").GetProperty("properties").GetProperty("decision").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(["one_event", "multiple_events", "unresolved"], choices);
        Assert.Equal(24, request.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0, request.RootElement.GetProperty("temperature").GetInt32());
        Assert.Equal(0, request.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestBody!))),
            result.RequestSha256);
    }

    [Fact]
    public async Task RejectsModelIdentityMismatch()
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"model":"wrong-model","choices":[{"message":{"content":"{\"decision\":\"one_event\"}"}}]}
                """, Encoding.UTF8, "application/json")
        }));
        var decider = new OpenAiEvidencePurityDecider(
            new HttpClient(handler), "http://model/v1", "", "purity-model");

        await Assert.ThrowsAsync<InvalidDataException>(() => decider.DecideAsync(
            EvidencePurityPromptBuilder.Build(CandidateContext()), CandidateContext(), default));
    }

    private static EvidencePurityContext CandidateContext() => new(
        new EvidencePurityOwnerIdentity("candidate", 99001, "private-candidate-owner"),
        EvidencePurityScope.CandidateConversationSegment,
        [Source(701, "private-candidate-call", "Candidate contains event Alpha and event Beta")]);

    private static EvidencePurityContext IncidentContext() => new(
        new EvidencePurityOwnerIdentity("incident", 88002, "private-incident-owner"),
        EvidencePurityScope.ExistingIncident,
        [
            Source(801, "private-first", "First incident transcript"),
            Source(802, "private-second", "Second incident transcript")
        ]);

    private static (IncidentMembershipSourceIdentity Identity, IncidentMembershipModelEvidence Evidence) Source(
        long callId,
        string observationId,
        string transcript) =>
        (new IncidentMembershipSourceIdentity(callId, observationId),
            new IncidentMembershipModelEvidence(
                DateTimeOffset.Parse("2026-08-01T00:00:00Z").AddSeconds(callId),
                transcript,
                "system-name",
                "talkgroup-name",
                TimeSpan.FromSeconds(5)));

    private static EvidencePurityResult Result(string ownerType, EvidencePurityDisposition disposition) =>
        new(new EvidencePurityOwnerIdentity(ownerType, 1, $"{ownerType}-private"), disposition, "purity-model", 1, 1, 1, 2);

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    private sealed class FixedDecider(EvidencePurityDisposition disposition) : IEvidencePurityDecider
    {
        public Task<EvidencePurityDecision> DecideAsync(
            EvidencePurityPrompt prompt,
            EvidencePurityContext context,
            CancellationToken ct) => Task.FromResult(new EvidencePurityDecision(
            disposition, "purity-model", 10, 20, 3, 23));
    }

    private sealed class CapturingDecider(EvidencePurityDisposition disposition) : IEvidencePurityDecider
    {
        public EvidencePurityPrompt? Prompt { get; private set; }
        public EvidencePurityContext? Context { get; private set; }

        public Task<EvidencePurityDecision> DecideAsync(
            EvidencePurityPrompt prompt,
            EvidencePurityContext context,
            CancellationToken ct)
        {
            Prompt = prompt;
            Context = context;
            return Task.FromResult(new EvidencePurityDecision(disposition, "purity-model", 10, 20, 3, 23));
        }
    }

    private sealed class FailingDecider : IEvidencePurityDecider
    {
        public Task<EvidencePurityDecision> DecideAsync(
            EvidencePurityPrompt prompt,
            EvidencePurityContext context,
            CancellationToken ct) => throw new InvalidOperationException("model unavailable");
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
