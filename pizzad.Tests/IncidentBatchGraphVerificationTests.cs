namespace pizzad.Tests;

public sealed class IncidentBatchGraphVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GraphPromptEncodesEvidenceOnceAndJudgesEverySource()
    {
        var entry = await BuildEntryAsync();
        var requests = IncidentBatchVerificationQueueContract.BuildRequests(entry);

        Assert.Equal(4, requests.Count);
        Assert.Equal(3, requests.Count(item => item.Kind == IncidentBatchVerificationKind.StandaloneEvent));
        Assert.Single(requests, item => item.Kind == IncidentBatchVerificationKind.Relationship);

        var prompt = IncidentBatchGraphVerification.BuildPrompt(entry, requests);
        foreach (var observation in entry.Bundle.Observations)
        {
            var quote = Assert.Single(observation.Transcripts).Text;
            Assert.Equal(1, prompt.UserPrompt.Split(quote, StringSplitOptions.None).Length - 1);
        }
        Assert.Contains("complete evidence window", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("Being unmatched is never evidence of being standalone", prompt.UserPrompt, StringComparison.Ordinal);
        Assert.Contains("complete connected component", prompt.UserPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphSchemaRestrictsEachDecisionToItsApplicationOwnedEvidence()
    {
        var entry = await BuildEntryAsync();
        var requests = IncidentBatchVerificationQueueContract.BuildRequests(entry);
        var prompt = IncidentBatchGraphVerification.BuildPrompt(entry, requests);
        var root = System.Text.Json.Nodes.JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(prompt.ResponseFormat, EngineConfig.JsonOptions()))!;
        var standalones = root["json_schema"]!["schema"]!["properties"]!["standalone_decisions"]!["prefixItems"]!.AsArray();
        var catalog = IncidentBatchConfirmationEvidenceCatalog.Build(entry.Bundle);
        int Evidence(string observationId) => catalog.Select((item, index) => new { item, index })
            .Single(value => value.item.ObservationId == observationId).index;

        Assert.Equal(3, standalones.Count);
        for (var row = 0; row < standalones.Count; row++)
        {
            var allowed = standalones[row]!["prefixItems"]![4]!["items"]!["enum"]!.AsArray();
            Assert.Single(allowed);
            Assert.Equal(Evidence($"call:{row + 1}"), allowed[0]!.GetValue<int>());
        }
    }

    [Fact]
    public async Task IncoherentComponentFailsClosedAndCannotSuppressIndependentIncidents()
    {
        var entry = await BuildEntryAsync();
        var requests = IncidentBatchVerificationQueueContract.BuildRequests(entry);
        var relationship = Assert.Single(requests, item => item.Kind == IncidentBatchVerificationKind.Relationship);
        var standalone = requests.Where(item => item.Kind == IncidentBatchVerificationKind.StandaloneEvent).ToList();
        var catalog = IncidentBatchConfirmationEvidenceCatalog.Build(entry.Bundle);
        int Evidence(string observationId) => catalog.Select((item, index) => new { item, index })
            .Single(value => value.item.ObservationId == observationId).index;

        var proposal = IncidentBatchGraphVerification.Parse(
            entry,
            requests,
            $$"""
            {"relationship_decisions":[[1,"verify",false,"Wrong merged title","The edge is locally plausible.",[{{Evidence("call:2")}}],[{{Evidence("call:1")}}],[],[]]],"standalone_decisions":[[1,"verify","Vehicle fire","A vehicle is burning.",[{{Evidence("call:1")}}],""],[2,"reject","","This is only a response update.",[{{Evidence("call:2")}}],"fragment"],[3,"verify","Apartment fire","An apartment fire is reported.",[{{Evidence("call:3")}}],""]]}
            """,
            "test-model",
            Now);

        Assert.Equal(IncidentBatchConfirmationDecisionKind.Review, Assert.Single(proposal.RelationshipProposal.Decisions).Decision);
        Assert.Equal(2, proposal.StandaloneProposals.Values.Count(item =>
            item.Decision.Decision == IncidentBatchConfirmationDecisionKind.Verify));

        var relationshipResult = IncidentBatchVerificationQueueContract.BuildResult(
            entry,
            relationship,
            proposal.RelationshipProposal,
            new IncidentBatchConfirmationExecutionContext(1, string.Empty),
            Now);
        Assert.Equal(IncidentBatchVerificationOutcome.Review, relationshipResult.Outcome);
        Assert.Empty(IncidentBatchGraphVerification.TerminalPersistenceRequests(
            entry,
            requests,
            new Dictionary<string, IncidentBatchVerificationResult>(StringComparer.Ordinal)
            {
                [relationship.RequestId] = relationshipResult
            }));
        Assert.All(standalone, request => Assert.True(proposal.StandaloneProposals.ContainsKey(request.RequestId)));
    }

    [Fact]
    public async Task VerifiedComponentHasOneTerminalWriteAndOwnsItsMemberStandalones()
    {
        var entry = await BuildEntryAsync();
        var requests = IncidentBatchVerificationQueueContract.BuildRequests(entry);
        var relationship = Assert.Single(requests, item => item.Kind == IncidentBatchVerificationKind.Relationship);
        var context = IncidentBatchVerificationQueueContract.BuildContext(entry, relationship);
        var decision = new IncidentBatchConfirmationDecision(
            context.Source.SourceProposalToken,
            context.Candidate.CandidateToken,
            IncidentBatchConfirmationDecisionKind.Verify,
            "Both calls describe the same vehicle fire response.",
            [Citation(entry, "call:2")],
            [Citation(entry, "call:1")],
            [],
            [],
            "Vehicle fire response");
        var proposal = new IncidentBatchConfirmationProposal(
            "graph:verified",
            Now,
            "test-model",
            IncidentBatchGraphVerification.PromptIdentity,
            [decision]);
        var result = IncidentBatchVerificationQueueContract.BuildResult(
            entry,
            relationship,
            proposal,
            new IncidentBatchConfirmationExecutionContext(1, string.Empty),
            Now);
        var results = new Dictionary<string, IncidentBatchVerificationResult>(StringComparer.Ordinal)
        {
            [relationship.RequestId] = result
        };

        Assert.Equal(
            [relationship.RequestId],
            IncidentBatchGraphVerification.TerminalPersistenceRequests(entry, requests, results));
        Assert.Equal(
            ["call:1", "call:2"],
            IncidentBatchGraphVerification.VerifiedRelationshipObservationIds(entry, requests, results)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ThreeCallChainCollapsesIntoOneIncidentBeforeItsSingleTerminalWrite()
    {
        var run = await BuildChainRunAsync();
        var entry = run.LedgerEntry.Entry;
        var requests = IncidentBatchVerificationQueueContract.BuildRequests(entry);
        var relationshipRequests = requests.Where(item => item.Kind == IncidentBatchVerificationKind.Relationship).ToList();
        var catalog = IncidentBatchConfirmationEvidenceCatalog.Build(entry.Bundle);
        int Evidence(string observationId) => catalog.Select((item, index) => new { item, index })
            .Single(value => value.item.ObservationId == observationId).index;
        var graph = IncidentBatchGraphVerification.Parse(
            entry,
            requests,
            $$"""
            {"relationship_decisions":[[1,"verify",true,"Vehicle fire response","The response continues the same vehicle fire.",[{{Evidence("call:2")}}],[{{Evidence("call:1")}}],[],[]],[2,"verify",true,"Vehicle fire response","The arrival update continues the same response.",[{{Evidence("call:3")}}],[{{Evidence("call:2")}}],[],[]]],"standalone_decisions":[[1,"verify","Vehicle fire","A vehicle fire is reported.",[{{Evidence("call:1")}}],""],[2,"reject","","A response fragment.",[{{Evidence("call:2")}}],"fragment"],[3,"reject","","An arrival fragment.",[{{Evidence("call:3")}}],"fragment"]]}
            """,
            "test-model",
            Now);
        var results = new Dictionary<string, IncidentBatchVerificationResult>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            results[request.RequestId] = request.Kind == IncidentBatchVerificationKind.Relationship
                ? IncidentBatchVerificationQueueContract.BuildResult(
                    entry,
                    request,
                    graph.RelationshipProposal with
                    {
                        Decisions = [graph.RelationshipProposal.Decisions.Single(decision =>
                            decision.SourceProposalToken == request.SourceProposalToken &&
                            decision.CandidateToken == request.CandidateToken)]
                    },
                    new IncidentBatchConfirmationExecutionContext(1, string.Empty),
                    Now)
                : IncidentBatchVerificationQueueContract.BuildStandaloneResult(
                    entry,
                    request,
                    graph.StandaloneProposals[request.RequestId],
                    new IncidentBatchConfirmationExecutionContext(1, string.Empty),
                    Now);
        }

        var order = IncidentBatchGraphVerification.ApplicationOrder(entry, requests, results);
        Assert.Equal(
            ["source:call:3", "source:call:2"],
            order.Where(item => item.Kind == IncidentBatchVerificationKind.Relationship)
                .Select(item => item.SourceProposalToken));
        var projection = run.Projection.Projection;
        foreach (var request in order)
        {
            if (IncidentBatchGraphVerification.IsOwnedByVerifiedRelationship(
                    entry,
                    request,
                    IncidentBatchGraphVerification.VerifiedRelationshipObservationIds(entry, requests, results)))
                continue;
            projection = IncidentBatchVerificationProjector.Apply(
                projection,
                entry,
                request,
                results[request.RequestId],
                $"projection:{request.RequestId}",
                Now);
        }

        var incident = Assert.Single(projection.Events);
        Assert.Equal(["call:1", "call:2", "call:3"], incident.ObservationIds.Order(StringComparer.Ordinal));
        Assert.Equal("Vehicle fire response", incident.Title);
        Assert.Equal(
            [relationshipRequests.Single(item => item.SourceProposalToken == "source:call:2").RequestId],
            IncidentBatchGraphVerification.TerminalPersistenceRequests(entry, requests, results));
    }

    [Fact]
    public void CompactRelationshipPromptDoesNotRepeatTwentyFourTranscriptsPerPair()
    {
        var observations = Enumerable.Range(1, 24)
            .Select(index => Observation(
                $"call:{index}",
                $"transcript:{index}",
                $"Unique transmission {index:D2}: " + new string((char)('a' + index % 20), 160)))
            .ToList();
        var bundle = new IncidentEventStateObservationBundle("bundle:24", Now, observations, []);
        var sources = observations.Select(item =>
            new IncidentBatchRelationshipSource($"source:{item.ObservationId}", [item.ObservationId])).ToList();
        var candidates = observations.Take(23).Select(item =>
            new IncidentBatchCandidate($"candidate:{item.ObservationId}", $"projection:{item.ObservationId}", [item.ObservationId])).ToList();

        var prompt = IncidentBatchRelationshipPrompt.Build(bundle, sources, candidates);

        Assert.True(prompt.UserPrompt.Length < 35_000, $"compact relationship prompt was {prompt.UserPrompt.Length} characters");
        Assert.True(IncidentBatchRelationshipContract.MaximumReturnedRelationships >= observations.Count - 1);
        foreach (var observation in observations)
        {
            var quote = Assert.Single(observation.Transcripts).Text;
            Assert.Equal(1, prompt.UserPrompt.Split(quote, StringSplitOptions.None).Length - 1);
        }
    }

    private static async Task<IncidentBatchLedgerEntry> BuildEntryAsync()
    {
        var bundle = new IncidentEventStateObservationBundle(
            "bundle:graph",
            Now,
            [
                Observation("call:1", "transcript:1", "A vehicle is on fire beside the roadway."),
                Observation("call:2", "transcript:2", "Engine four is responding to that vehicle fire."),
                Observation("call:3", "transcript:3", "An apartment fire is reported on the third floor.")
            ],
            []);
        var singletons = bundle.Observations.Select(item =>
            new IncidentBatchSingletonIdentity(item.ObservationId, $"projection:{item.ObservationId}")).ToList();
        var coordinator = new IncidentBatchCoordinator(
            new ApplicationIncidentBatchExhaustiveSourceProposer(new FixedTimeProvider(Now)),
            new OneRelationshipProposer(),
            new MemoryStore(),
            new FixedTimeProvider(Now));
        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                "run:graph",
                "ledger:graph",
                "projection:graph",
                singletons,
                "test",
                $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchContract.PerCitationAcceptanceConfigurationToken};{IncidentBatchContract.ObservationIsolatedOwnershipConfigurationToken};{IncidentBatchContract.ExhaustiveSourceIntakeConfigurationToken};{IncidentBatchExecutionArchitecture.StagedRelationshipAsynchronousConfirmationToken};{IncidentBatchStandaloneVerificationContract.ConfigurationToken};{IncidentBatchStandaloneBatchVerification.ConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken};{IncidentBatchGraphVerification.ConfigurationToken}"),
            bundle,
            null,
            bundle.Observations.Select(item => item.ObservationId).ToList(),
            [],
            CancellationToken.None);
        var validation = IncidentBatchContract.ValidateLedgerEntry(result.LedgerEntry.Entry);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        return result.LedgerEntry.Entry;
    }

    private static async Task<IncidentBatchRunResult> BuildChainRunAsync()
    {
        var bundle = new IncidentEventStateObservationBundle(
            "bundle:chain",
            Now,
            [
                Observation("call:1", "transcript:1", "A vehicle is on fire beside the roadway."),
                Observation("call:2", "transcript:2", "Engine four is responding to that vehicle fire."),
                Observation("call:3", "transcript:3", "Engine four has arrived at the vehicle fire.")
            ],
            []);
        var coordinator = new IncidentBatchCoordinator(
            new ApplicationIncidentBatchExhaustiveSourceProposer(new FixedTimeProvider(Now)),
            new ChainRelationshipProposer(),
            new MemoryStore(),
            new FixedTimeProvider(Now));
        return await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                "run:chain",
                "ledger:chain",
                "projection:chain",
                bundle.Observations.Select(item =>
                    new IncidentBatchSingletonIdentity(item.ObservationId, $"projection:{item.ObservationId}")).ToList(),
                "test",
                $"test;{IncidentBatchContract.PerEventAcceptanceConfigurationToken};{IncidentBatchContract.PerCitationAcceptanceConfigurationToken};{IncidentBatchContract.ObservationIsolatedOwnershipConfigurationToken};{IncidentBatchContract.ExhaustiveSourceIntakeConfigurationToken};{IncidentBatchExecutionArchitecture.StagedRelationshipAsynchronousConfirmationToken};{IncidentBatchStandaloneVerificationContract.ConfigurationToken};{IncidentBatchStandaloneBatchVerification.ConfigurationToken};{IncidentBatchRelationshipContract.ConfigurationToken};{IncidentBatchGraphVerification.ConfigurationToken}"),
            bundle,
            null,
            bundle.Observations.Select(item => item.ObservationId).ToList(),
            [],
            CancellationToken.None);
    }

    private static IncidentEventStateTranscriptCitation Citation(IncidentBatchLedgerEntry entry, string observationId)
    {
        var span = IncidentBatchConfirmationEvidenceCatalog.Build(entry.Bundle)
            .Single(item => item.ObservationId == observationId);
        return new IncidentEventStateTranscriptCitation(span.TranscriptId, span.ExactQuote);
    }

    private static IncidentEventStateSourceObservation Observation(string observationId, string transcriptId, string text) =>
        new(observationId, long.Parse(observationId.AsSpan("call:".Length)),
            1000 + long.Parse(observationId.AsSpan("call:".Length)), string.Empty, null,
            [new IncidentEventStateTranscriptObservation(transcriptId, text, "test", Now)],
            new Dictionary<string, IncidentEventStateMetadataObservation>());

    private sealed class OneRelationshipProposer : IIncidentBatchRelationshipProposer
    {
        public Task<IncidentBatchRelationshipProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<IncidentBatchRelationshipSource> sources,
            IReadOnlyList<IncidentBatchCandidate> candidates,
            CancellationToken ct)
        {
            var source = sources.Single(item => item.NewObservationIds.SequenceEqual(["call:2"]));
            var candidate = candidates.Single(item => item.ObservationIds.SequenceEqual(["call:1"]));
            return Task.FromResult(new IncidentBatchRelationshipProposal(
                "proposal:graph",
                Now,
                "test-model",
                IncidentBatchRelationshipPrompt.PromptIdentity,
                [new IncidentBatchRelationship(
                    source.SourceProposalToken,
                    candidate.CandidateToken,
                    IncidentBatchRelationshipDisposition.ConfirmedMembership,
                    "The responding engine explicitly refers to the vehicle fire.",
                    0,
                    [new IncidentEventStateTranscriptCitation("transcript:2", "responding to that vehicle fire")],
                    [new IncidentEventStateTranscriptCitation("transcript:1", "vehicle is on fire")],
                    [],
                    [])]));
        }
    }

    private sealed class ChainRelationshipProposer : IIncidentBatchRelationshipProposer
    {
        public Task<IncidentBatchRelationshipProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<IncidentBatchRelationshipSource> sources,
            IReadOnlyList<IncidentBatchCandidate> candidates,
            CancellationToken ct)
        {
            IncidentBatchRelationship Edge(string sourceObservation, string candidateObservation, string sourceQuote, string candidateQuote)
            {
                var source = sources.Single(item => item.NewObservationIds.SequenceEqual([sourceObservation]));
                var candidate = candidates.Single(item => item.ObservationIds.SequenceEqual([candidateObservation]));
                return new IncidentBatchRelationship(
                    source.SourceProposalToken,
                    candidate.CandidateToken,
                    IncidentBatchRelationshipDisposition.ConfirmedMembership,
                    "The later call explicitly continues the same vehicle fire.",
                    0,
                    [new IncidentEventStateTranscriptCitation($"transcript:{sourceObservation["call:".Length..]}", sourceQuote)],
                    [new IncidentEventStateTranscriptCitation($"transcript:{candidateObservation["call:".Length..]}", candidateQuote)],
                    [],
                    []);
            }
            return Task.FromResult(new IncidentBatchRelationshipProposal(
                "proposal:chain",
                Now,
                "test-model",
                IncidentBatchRelationshipPrompt.PromptIdentity,
                [
                    Edge("call:2", "call:1", "responding to that vehicle fire", "vehicle is on fire"),
                    Edge("call:3", "call:2", "arrived at the vehicle fire", "responding to that vehicle fire")
                ]));
        }
    }

    private sealed class MemoryStore : IIncidentBatchStore
    {
        public Task<IncidentBatchRunResult> AppendIncidentBatchRunAsync(
            IncidentBatchLedgerEntry entry,
            IncidentBatchProjection projection,
            CancellationToken ct) =>
            Task.FromResult(new IncidentBatchRunResult(
                new IncidentBatchStoredLedgerEntry(1, "entry-hash", entry),
                new IncidentBatchStoredProjection(1, "projection-hash", projection)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
