using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad.Tests;

public sealed class IncidentTargetMembershipAdapterTests
{
    [Fact]
    public async Task MapsDecisionToApplicationOwnedIncidentAndCandidate()
    {
        var context = Context();
        var result = await new IncidentTargetMembershipAdapter(
            new FixedDecider(IncidentTargetMembershipDisposition.Include))
            .DecideAsync(context, default);

        Assert.Equal(7001, result.TargetIncident.IncidentId);
        Assert.Equal("private-incident", result.TargetIncident.ObservationId);
        Assert.Equal(103, result.Candidate.CallId);
        Assert.Equal("private-candidate", result.Candidate.ObservationId);
        Assert.Equal(IncidentTargetMembershipDisposition.Include, result.Disposition);
    }

    [Fact]
    public void PromptContainsCompleteIncidentDirectLinkAndCandidateWithoutPrivateIdentity()
    {
        var prompt = IncidentTargetMembershipPromptBuilder.Build(Context());

        Assert.Contains("First established transcript", prompt.UserPrompt);
        Assert.Contains("Directly linked established transcript", prompt.UserPrompt);
        Assert.Equal(2, Count(prompt.UserPrompt, "Directly linked established transcript"));
        Assert.Contains("Candidate transcript", prompt.UserPrompt);
        Assert.DoesNotContain("7001", prompt.UserPrompt);
        Assert.DoesNotContain("private-incident", prompt.UserPrompt);
        Assert.DoesNotContain("private-first", prompt.UserPrompt);
        Assert.DoesNotContain("private-linked", prompt.UserPrompt);
        Assert.DoesNotContain("private-candidate", prompt.UserPrompt);
    }

    [Fact]
    public async Task DuplicateTranscriptTextRetainsCandidateIdentityWithoutModelReproducingIt()
    {
        var same = "The same transcript can occur more than once.";
        var context = new IncidentTargetMembershipContext(
            new IncidentTargetIdentity(44, "incident-private"),
            [Source(1, "member-private", same)],
            new IncidentMembershipSourceIdentity(1, "member-private"),
            Source(2, "candidate-private", same));
        var decider = new CapturingDecider(IncidentTargetMembershipDisposition.Unresolved);

        var result = await new IncidentTargetMembershipAdapter(decider).DecideAsync(context, default);

        Assert.Same(context.Candidate, decider.Candidate);
        Assert.Equal(2, result.Candidate.CallId);
        Assert.DoesNotContain("candidate-private", decider.Prompt!.UserPrompt);
        Assert.Equal(3, Count(decider.Prompt.UserPrompt, same));
    }

    [Fact]
    public void RejectsDirectLinkThatIsNotAnEstablishedIncidentMember()
    {
        var exception = Assert.Throws<IncidentMembershipContractException>(() =>
            new IncidentTargetMembershipContext(
                new IncidentTargetIdentity(1, "incident"),
                [Source(10, "member", "Member")],
                new IncidentMembershipSourceIdentity(11, "outsider"),
                Source(12, "candidate", "Candidate")));

        Assert.Contains("must be one", exception.Message);
    }

    [Fact]
    public void RejectsCandidateAlreadyInEstablishedIncident()
    {
        var exception = Assert.Throws<IncidentMembershipContractException>(() =>
            new IncidentTargetMembershipContext(
                new IncidentTargetIdentity(1, "incident"),
                [Source(10, "same", "Member")],
                new IncidentMembershipSourceIdentity(10, "same"),
                Source(10, "same", "Candidate")));

        Assert.Contains("already an established member", exception.Message);
    }

    [Fact]
    public void RejectsOversizedIncidentRatherThanTruncatingEvidence()
    {
        var members = Enumerable.Range(1, IncidentTargetMembershipContext.MaximumEstablishedCalls + 1)
            .Select(index => Source(index, $"member-{index}", $"Transcript {index}"))
            .ToList();

        var exception = Assert.Throws<IncidentMembershipContractException>(() =>
            new IncidentTargetMembershipContext(
                new IncidentTargetIdentity(1, "incident"),
                members,
                members[0].Identity,
                Source(99, "candidate", "Candidate")));

        Assert.Contains("complete target incident", exception.Message);
    }

    [Fact]
    public async Task ModelFailureFailsClosed()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new IncidentTargetMembershipAdapter(new FailingDecider()).DecideAsync(Context(), default));
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
                    {"model":"membership-model","choices":[{"message":{"content":"{\"decision\":\"unresolved\"}"}}],"usage":{"prompt_tokens":21,"completion_tokens":4,"total_tokens":25}}
                    """, Encoding.UTF8, "application/json")
            };
        });
        var decider = new OpenAiIncidentTargetMembershipDecider(
            new HttpClient(handler), "http://model/v1", "", "membership-model");
        var context = Context();

        var result = await new IncidentTargetMembershipAdapter(decider).DecideAsync(context, default);

        Assert.Equal(IncidentTargetMembershipDisposition.Unresolved, result.Disposition);
        Assert.Equal(25, result.TotalTokens);
        using var request = JsonDocument.Parse(requestBody!);
        var schema = request.RootElement.GetProperty("response_format").GetProperty("json_schema");
        Assert.True(schema.GetProperty("strict").GetBoolean());
        var choices = schema.GetProperty("schema").GetProperty("properties").GetProperty("decision").GetProperty("enum")
            .EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(["include", "do_not_include", "unresolved"], choices);
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
                {"model":"wrong-model","choices":[{"message":{"content":"{\"decision\":\"include\"}"}}]}
                """, Encoding.UTF8, "application/json")
        }));
        var decider = new OpenAiIncidentTargetMembershipDecider(
            new HttpClient(handler), "http://model/v1", "", "membership-model");

        await Assert.ThrowsAsync<InvalidDataException>(() => decider.DecideAsync(
            IncidentTargetMembershipPromptBuilder.Build(Context()), Context().Candidate, default));
    }

    private static IncidentTargetMembershipContext Context() => new(
        new IncidentTargetIdentity(7001, "private-incident"),
        [
            Source(101, "private-first", "First established transcript"),
            Source(102, "private-linked", "Directly linked established transcript")
        ],
        new IncidentMembershipSourceIdentity(102, "private-linked"),
        Source(103, "private-candidate", "Candidate transcript"));

    private static (IncidentMembershipSourceIdentity Identity, IncidentMembershipModelEvidence Evidence) Source(
        long callId,
        string observationId,
        string transcript) =>
        (new IncidentMembershipSourceIdentity(callId, observationId),
            new IncidentMembershipModelEvidence(
                DateTimeOffset.Parse("2026-07-31T16:00:00Z").AddSeconds(callId),
                transcript,
                "system-name",
                "talkgroup-name",
                TimeSpan.FromSeconds(4)));

    private static int Count(string value, string needle) =>
        (value.Length - value.Replace(needle, string.Empty, StringComparison.Ordinal).Length) / needle.Length;

    private sealed class FixedDecider(IncidentTargetMembershipDisposition disposition) : IIncidentTargetMembershipDecider
    {
        public Task<IncidentTargetMembershipDecision> DecideAsync(
            IncidentTargetMembershipPrompt prompt,
            IncidentMembershipSourceBinding candidate,
            CancellationToken ct) => Task.FromResult(new IncidentTargetMembershipDecision(
            disposition, "membership-model", 12, 20, 3, 23));
    }

    private sealed class CapturingDecider(IncidentTargetMembershipDisposition disposition) : IIncidentTargetMembershipDecider
    {
        public IncidentTargetMembershipPrompt? Prompt { get; private set; }
        public IncidentMembershipSourceBinding? Candidate { get; private set; }

        public Task<IncidentTargetMembershipDecision> DecideAsync(
            IncidentTargetMembershipPrompt prompt,
            IncidentMembershipSourceBinding candidate,
            CancellationToken ct)
        {
            Prompt = prompt;
            Candidate = candidate;
            return Task.FromResult(new IncidentTargetMembershipDecision(
                disposition, "membership-model", 12, 20, 3, 23));
        }
    }

    private sealed class FailingDecider : IIncidentTargetMembershipDecider
    {
        public Task<IncidentTargetMembershipDecision> DecideAsync(
            IncidentTargetMembershipPrompt prompt,
            IncidentMembershipSourceBinding candidate,
            CancellationToken ct) => throw new InvalidOperationException("model unavailable");
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
