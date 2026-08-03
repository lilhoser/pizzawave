using System.Net;
using System.Text;
using System.Text.Json;

namespace pizzad.Tests;

public sealed class IncidentMembershipConstrainedAdapterTests
{
    [Fact]
    public async Task GeneratesMultipleHypothesesAndCompleteResidualCoverage()
    {
        var session = Session(
            (11, "alpha", "Fire at Oak Street"),
            (12, "bravo", "Engine arriving at Oak Street"),
            (13, "charlie", "Traffic stop on Pine Road"),
            (14, "delta", "Unit clear"));
        var decider = new ScriptedDecider(
            membershipPasses:
            [
                new Dictionary<string, IncidentMembershipCellChoice>
                {
                    ["Fire at Oak Street"] = IncidentMembershipCellChoice.Member,
                    ["Engine arriving at Oak Street"] = IncidentMembershipCellChoice.Member,
                    ["Traffic stop on Pine Road"] = IncidentMembershipCellChoice.NotMember,
                    ["Unit clear"] = IncidentMembershipCellChoice.NotMember
                },
                new Dictionary<string, IncidentMembershipCellChoice>
                {
                    ["Traffic stop on Pine Road"] = IncidentMembershipCellChoice.Member,
                    ["Unit clear"] = IncidentMembershipCellChoice.NotMember
                },
                new Dictionary<string, IncidentMembershipCellChoice>
                {
                    ["Unit clear"] = IncidentMembershipCellChoice.NotMember
                }
            ],
            residuals: new Dictionary<string, IncidentMembershipResidualDisposition>
            {
                ["Unit clear"] = IncidentMembershipResidualDisposition.NonIncident
            });

        var result = await new IncidentMembershipConstrainedAdapter(decider, 6).GenerateAsync(session, default);

        Assert.Equal(2, result.Membership.Hypotheses.Count);
        Assert.Equal([11L, 12L], result.Membership.Hypotheses[0].Sources.Select(source => source.CallId));
        Assert.Equal([13L], result.Membership.Hypotheses[1].Sources.Select(source => source.CallId));
        Assert.Empty(result.Membership.UnresolvedSources);
        Assert.Equal(14, Assert.Single(result.Membership.NonIncidentSources).CallId);
        Assert.Equal(8, result.ModelRequests);
        Assert.Equal("test-membership-model", result.ModelIdentity);
    }

    [Fact]
    public async Task DuplicateTranscriptsRemainDistinctApplicationOwnedSources()
    {
        var session = Session((21, "first-copy", "duplicate"), (22, "second-copy", "duplicate"));
        var decider = new BindingAwareDecider(
            memberSource: session.Sources[1],
            residualDisposition: IncidentMembershipResidualDisposition.Unresolved);

        var result = await new IncidentMembershipConstrainedAdapter(decider, 1).GenerateAsync(session, default);

        Assert.Equal(22, Assert.Single(Assert.Single(result.Membership.Hypotheses).Sources).CallId);
        Assert.Equal(21, Assert.Single(result.Membership.UnresolvedSources).CallId);
        Assert.Empty(result.Membership.NonIncidentSources);
    }

    [Fact]
    public async Task MappingIsStableWhenInputOrderChanges()
    {
        var first = await ResolveSelectedCallAsync(Session((31, "one", "Alpha"), (32, "two", "Bravo")));
        var second = await ResolveSelectedCallAsync(Session((32, "two", "Bravo"), (31, "one", "Alpha")));

        Assert.Equal(32, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void PromptsNeverContainApplicationIdentity()
    {
        var session = Session((987654321, "private-observation-token", "Dispatch to Elm Street"));

        var membership = IncidentMembershipCellPromptBuilder.BuildMembership(session.Sources, session.Sources[0]);
        var residual = IncidentMembershipCellPromptBuilder.BuildResidual(session.Sources, session.Sources[0]);

        foreach (var prompt in new[] { membership, residual })
        {
            Assert.DoesNotContain("987654321", prompt.UserPrompt, StringComparison.Ordinal);
            Assert.DoesNotContain("private-observation-token", prompt.UserPrompt, StringComparison.Ordinal);
            Assert.Contains("Dispatch to Elm Street", prompt.UserPrompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NoSupportedHypothesisCarriesEverySourceForwardWithoutSingletons()
    {
        var session = Session((41, "one", "garbled"), (42, "two", "also garbled"));
        var decider = new ConstantDecider(
            IncidentMembershipCellChoice.NotMember,
            IncidentMembershipResidualDisposition.Unresolved);

        var result = await new IncidentMembershipConstrainedAdapter(decider, 6).GenerateAsync(session, default);

        Assert.Empty(result.Membership.Hypotheses);
        Assert.Equal([41L, 42L], result.Membership.UnresolvedSources.Select(source => source.CallId));
        Assert.Empty(result.Membership.NonIncidentSources);
        Assert.Equal(4, result.ModelRequests);
    }

    [Fact]
    public async Task ModelFailureFailsClosedWithoutReturningPartialMembership()
    {
        var session = Session((51, "one", "Alpha"), (52, "two", "Bravo"));
        var decider = new ThrowingDecider();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new IncidentMembershipConstrainedAdapter(decider, 6).GenerateAsync(session, default));

        Assert.Throws<IncidentMembershipContractException>(() => session.Complete());
    }

    [Fact]
    public async Task InconsistentModelIdentityFailsClosed()
    {
        var session = Session((61, "one", "Alpha"));
        var decider = new ChangingIdentityDecider();

        var error = await Assert.ThrowsAsync<IncidentMembershipContractException>(() =>
            new IncidentMembershipConstrainedAdapter(decider, 6).GenerateAsync(session, default));

        Assert.Contains("inconsistent model identities", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceLimitIsEnforcedBeforeAnyModelRequest()
    {
        var session = Session(Enumerable.Range(1, 7)
            .Select(number => ((long)number, $"observation-{number}", $"Transcript {number}"))
            .ToArray());
        var decider = new ConstantDecider(
            IncidentMembershipCellChoice.NotMember,
            IncidentMembershipResidualDisposition.Unresolved);

        var error = await Assert.ThrowsAsync<IncidentMembershipContractException>(() =>
            new IncidentMembershipConstrainedAdapter(decider, 6).GenerateAsync(session, default));

        Assert.Contains("at most 6 sources", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, decider.Requests);
    }

    [Fact]
    public async Task OpenAiDeciderSendsIdentityFreePromptAndParsesStrictDecision()
    {
        string? requestBody = null;
        var handler = new StubHttpHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("test-membership-model", "{\"decision\":\"member\"}");
        });
        var source = Session((71, "secret-observation", "Caller reports smoke")).Sources[0];
        var prompt = IncidentMembershipCellPromptBuilder.BuildMembership([source], source);
        var decider = new OpenAiIncidentMembershipCellDecider(
            new HttpClient(handler), "http://model.local/v1", "", "test-membership-model");

        var result = await decider.DecideMembershipAsync(prompt, source, default);

        Assert.Equal(IncidentMembershipCellChoice.Member, result.Choice);
        Assert.NotNull(requestBody);
        Assert.DoesNotContain("secret-observation", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"71\"", requestBody, StringComparison.Ordinal);
        Assert.Contains("json_schema", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiDeciderRejectsModelIdentityMismatch()
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(JsonResponse("wrong-model", "{\"decision\":\"member\"}")));
        var source = Session((81, "one", "Alpha")).Sources[0];
        var decider = new OpenAiIncidentMembershipCellDecider(
            new HttpClient(handler), "http://model.local/v1", "", "expected-model");

        await Assert.ThrowsAsync<InvalidDataException>(() => decider.DecideMembershipAsync(
            IncidentMembershipCellPromptBuilder.BuildMembership([source], source), source, default));
    }

    [Fact]
    public async Task OpenAiDeciderRejectsUnexpectedDecision()
    {
        var handler = new StubHttpHandler(_ => Task.FromResult(JsonResponse("test-membership-model", "{\"decision\":\"maybe\"}")));
        var source = Session((91, "one", "Alpha")).Sources[0];
        var decider = new OpenAiIncidentMembershipCellDecider(
            new HttpClient(handler), "http://model.local/v1", "", "test-membership-model");

        await Assert.ThrowsAsync<InvalidDataException>(() => decider.DecideMembershipAsync(
            IncidentMembershipCellPromptBuilder.BuildMembership([source], source), source, default));
    }

    private static async Task<long> ResolveSelectedCallAsync(IncidentMembershipContractSession session)
    {
        var result = await new IncidentMembershipConstrainedAdapter(
            new TranscriptDecider("Bravo"), 1).GenerateAsync(session, default);
        return Assert.Single(Assert.Single(result.Membership.Hypotheses).Sources).CallId;
    }

    private static HttpResponseMessage JsonResponse(string model, string content)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            choices = new[] { new { message = new { content } } },
            usage = new { prompt_tokens = 10, completion_tokens = 2, total_tokens = 12 }
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static IncidentMembershipContractSession Session(params (long CallId, string ObservationId, string Transcript)[] sources) =>
        new(sources.Select((source, index) => (
            new IncidentMembershipSourceIdentity(source.CallId, source.ObservationId),
            new IncidentMembershipModelEvidence(
                DateTimeOffset.Parse("2026-07-31T12:00:00Z").AddSeconds(index * 10),
                source.Transcript,
                "OT",
                "Dispatch",
                TimeSpan.FromSeconds(4)))));

    private sealed class ScriptedDecider(
        IReadOnlyList<IReadOnlyDictionary<string, IncidentMembershipCellChoice>> membershipPasses,
        IReadOnlyDictionary<string, IncidentMembershipResidualDisposition> residuals) : IIncidentMembershipCellDecider
    {
        private int _passIndex;
        private int _requestsInPass;

        public Task<IncidentMembershipCellDecision<IncidentMembershipCellChoice>> DecideMembershipAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct)
        {
            var pass = membershipPasses[_passIndex];
            var choice = pass[source.Evidence.Transcript];
            _requestsInPass++;
            if (_requestsInPass == pass.Count)
            {
                _passIndex++;
                _requestsInPass = 0;
            }
            return Task.FromResult(Decision(choice));
        }

        public Task<IncidentMembershipCellDecision<IncidentMembershipResidualDisposition>> DecideResidualAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            Task.FromResult(Decision(residuals[source.Evidence.Transcript]));
    }

    private sealed class BindingAwareDecider(
        IncidentMembershipSourceBinding memberSource,
        IncidentMembershipResidualDisposition residualDisposition) : IIncidentMembershipCellDecider
    {
        public Task<IncidentMembershipCellDecision<IncidentMembershipCellChoice>> DecideMembershipAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            Task.FromResult(Decision(ReferenceEquals(source, memberSource)
                ? IncidentMembershipCellChoice.Member
                : IncidentMembershipCellChoice.NotMember));

        public Task<IncidentMembershipCellDecision<IncidentMembershipResidualDisposition>> DecideResidualAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            Task.FromResult(Decision(residualDisposition));
    }

    private sealed class TranscriptDecider(string memberTranscript) : IIncidentMembershipCellDecider
    {
        public Task<IncidentMembershipCellDecision<IncidentMembershipCellChoice>> DecideMembershipAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            Task.FromResult(Decision(source.Evidence.Transcript == memberTranscript
                ? IncidentMembershipCellChoice.Member
                : IncidentMembershipCellChoice.NotMember));

        public Task<IncidentMembershipCellDecision<IncidentMembershipResidualDisposition>> DecideResidualAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            Task.FromResult(Decision(IncidentMembershipResidualDisposition.Unresolved));
    }

    private sealed class ConstantDecider(
        IncidentMembershipCellChoice membership,
        IncidentMembershipResidualDisposition residual) : IIncidentMembershipCellDecider
    {
        public int Requests { get; private set; }

        public Task<IncidentMembershipCellDecision<IncidentMembershipCellChoice>> DecideMembershipAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(Decision(membership));
        }

        public Task<IncidentMembershipCellDecision<IncidentMembershipResidualDisposition>> DecideResidualAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(Decision(residual));
        }
    }

    private sealed class ThrowingDecider : IIncidentMembershipCellDecider
    {
        public Task<IncidentMembershipCellDecision<IncidentMembershipCellChoice>> DecideMembershipAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            throw new InvalidOperationException("model unavailable");

        public Task<IncidentMembershipCellDecision<IncidentMembershipResidualDisposition>> DecideResidualAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            throw new InvalidOperationException("model unavailable");
    }

    private sealed class ChangingIdentityDecider : IIncidentMembershipCellDecider
    {
        public Task<IncidentMembershipCellDecision<IncidentMembershipCellChoice>> DecideMembershipAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            Task.FromResult(new IncidentMembershipCellDecision<IncidentMembershipCellChoice>(
                IncidentMembershipCellChoice.NotMember, "model-one", 1, 1, 1, 2));

        public Task<IncidentMembershipCellDecision<IncidentMembershipResidualDisposition>> DecideResidualAsync(
            IncidentMembershipCellPrompt prompt, IncidentMembershipSourceBinding source, CancellationToken ct) =>
            Task.FromResult(new IncidentMembershipCellDecision<IncidentMembershipResidualDisposition>(
                IncidentMembershipResidualDisposition.Unresolved, "model-two", 1, 1, 1, 2));
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }

    private static IncidentMembershipCellDecision<T> Decision<T>(T choice) where T : struct, Enum =>
        new(choice, "test-membership-model", 1, 10, 2, 12);
}
