using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IncidentBatchCanaryPersistenceTests
{
    [Fact]
    public async Task VerifiedStagedMembershipAtomicallyPersistsIncidentAndAudit()
    {
        await using var fixture = await CanaryFixture.CreateAsync();
        var staged = await fixture.StageVerifiedMembershipAsync();

        var appended = await fixture.Database.AppendIncidentBatchVerificationResultWithCanaryAsync(
            staged.BaseProjectionSequence,
            staged.SourceEntry,
            staged.Request,
            staged.Result,
            staged.Projection,
            CancellationToken.None);

        Assert.Equal(IncidentBatchCanaryCommitOutcome.Persisted, appended.Commit.Commit.Outcome);
        Assert.True(appended.Commit.Commit.IncidentId > 0);
        Assert.Equal([fixture.PriorCallId, fixture.SourceCallId], appended.Commit.Commit.CallIds);

        var incidents = await fixture.Database.ListIncidentsAsync(
            fixture.Now.AddMinutes(-5).ToUnixTimeSeconds(),
            fixture.Now.AddMinutes(5).ToUnixTimeSeconds(),
            CancellationToken.None);
        var incident = Assert.Single(incidents);
        Assert.Equal(appended.Commit.Commit.IncidentId, incident.Id);
        Assert.Equal(appended.Commit.Commit.IncidentKey, incident.IncidentKey);
        Assert.Equal([fixture.PriorCallId, fixture.SourceCallId], incident.Calls.Select(call => call.CallId).Order());
        Assert.Contains("White truck crash is active", incident.Detail, StringComparison.Ordinal);
        Assert.Contains("Critical injuries", incident.Detail, StringComparison.Ordinal);

        var commits = await fixture.Database.ListIncidentBatchCanaryCommitsAsync(
            fixture.RunId,
            10,
            CancellationToken.None);
        Assert.Equal(appended.Commit.ContentHash, Assert.Single(commits).ContentHash);
        var report = await fixture.Database.GetIncidentBatchVerificationShadowReportAsync(
            true,
            fixture.RunId,
            10,
            CancellationToken.None);
        Assert.Equal(1, report.Totals.Verified);
        Assert.Equal(1, report.Totals.CanaryPersisted);
        Assert.Equal(0, report.Totals.CanaryConflicts);
        Assert.Equal(incident.Id, Assert.Single(report.Items).CanaryIncidentId);

        await using var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE incident_batch_canary_commits SET run_id='changed' WHERE sequence=1;";
        var error = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("append-only", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExistingIncidentOwnershipRecordsConflictWithoutPartialCanaryWrite()
    {
        await using var fixture = await CanaryFixture.CreateAsync();
        var staged = await fixture.StageVerifiedMembershipAsync();
        var thirdCallId = await fixture.AddCallAsync(
            "legacy-companion",
            fixture.Now.AddSeconds(10),
            "A separate legacy incident companion call.");
        var legacyId = await fixture.Database.AddIncidentAsync(new IncidentDto
        {
            Title = "Existing incident",
            Detail = "Existing ownership must not be stolen.",
            FirstSeen = fixture.Now.ToUnixTimeSeconds(),
            LastSeen = fixture.Now.AddSeconds(10).ToUnixTimeSeconds(),
            Calls =
            [
                new IncidentCallDto(fixture.SourceCallId, fixture.Now.ToUnixTimeSeconds(), "", ""),
                new IncidentCallDto(thirdCallId, fixture.Now.AddSeconds(10).ToUnixTimeSeconds(), "", "")
            ]
        }, CancellationToken.None);
        Assert.True(legacyId > 0);

        var appended = await fixture.Database.AppendIncidentBatchVerificationResultWithCanaryAsync(
            staged.BaseProjectionSequence,
            staged.SourceEntry,
            staged.Request,
            staged.Result,
            staged.Projection,
            CancellationToken.None);

        Assert.Equal(IncidentBatchCanaryCommitOutcome.Conflict, appended.Commit.Commit.Outcome);
        Assert.Equal(0, appended.Commit.Commit.IncidentId);
        Assert.Contains($"incident {legacyId}", appended.Commit.Commit.Reason, StringComparison.Ordinal);
        Assert.Single(await fixture.Database.ListIncidentBatchVerificationResultsAsync(
            fixture.RunId,
            10,
            CancellationToken.None));
        var incidents = await fixture.Database.ListIncidentsAsync(
            fixture.Now.AddMinutes(-5).ToUnixTimeSeconds(),
            fixture.Now.AddMinutes(5).ToUnixTimeSeconds(),
            CancellationToken.None);
        Assert.DoesNotContain(incidents, incident => incident.IncidentKey == appended.Commit.Commit.IncidentKey);
        Assert.Contains(incidents, incident => incident.Id == legacyId);
    }

    [Fact]
    public async Task UnsafeConfigurationCannotPersistOrConsumeVerificationRequest()
    {
        await using var fixture = await CanaryFixture.CreateAsync();
        var staged = await fixture.StageVerifiedMembershipAsync();
        fixture.Config.AiInsights.IncidentAnalysisExecutionEnabled = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Database.AppendIncidentBatchVerificationResultWithCanaryAsync(
                staged.BaseProjectionSequence,
                staged.SourceEntry,
                staged.Request,
                staged.Result,
                staged.Projection,
                CancellationToken.None));

        Assert.Contains("legacy incident execution", error.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.Database.ListIncidentBatchVerificationResultsAsync(
            fixture.RunId,
            10,
            CancellationToken.None));
        Assert.Single(await fixture.Database.ListPendingIncidentBatchVerificationRequestsAsync(
            fixture.RunId,
            10,
            CancellationToken.None));
        Assert.Empty(await fixture.Database.ListIncidentBatchCanaryCommitsAsync(
            fixture.RunId,
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task TamperedVerifiedProjectionCannotPersistOrConsumeVerificationRequest()
    {
        await using var fixture = await CanaryFixture.CreateAsync();
        var staged = await fixture.StageVerifiedMembershipAsync();
        var target = Assert.Single(staged.Projection.Events);
        var tampered = staged.Projection with
        {
            Events = [target with { Title = "Unsupported replacement title" }]
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Database.AppendIncidentBatchVerificationResultWithCanaryAsync(
                staged.BaseProjectionSequence,
                staged.SourceEntry,
                staged.Request,
                staged.Result,
                tampered,
                CancellationToken.None));

        Assert.Contains("does not match the verified transition", error.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.Database.ListIncidentBatchVerificationResultsAsync(
            fixture.RunId,
            10,
            CancellationToken.None));
        Assert.Single(await fixture.Database.ListPendingIncidentBatchVerificationRequestsAsync(
            fixture.RunId,
            10,
            CancellationToken.None));
    }

    [Fact]
    public async Task VerifiedProvisionalAssociationAppearsInOperatorReviewWithoutPersistingIncident()
    {
        await using var fixture = await CanaryFixture.CreateAsync();
        var staged = await fixture.StageVerifiedProvisionalAssociationAsync();
        await fixture.Database.AppendIncidentBatchVerificationResultAsync(
            staged.BaseProjectionSequence,
            staged.SourceEntry,
            staged.Request,
            staged.Result,
            staged.Projection,
            CancellationToken.None);

        var report = await fixture.Database.GetIncidentAssociationReviewReportAsync(
            true,
            fixture.RunId,
            fixture.Now.AddMinutes(-5).ToUnixTimeSeconds(),
            fixture.Now.AddMinutes(5).ToUnixTimeSeconds(),
            CancellationToken.None);
        var group = Assert.Single(report.Groups);
        Assert.Equal("review", group.Placement);
        Assert.Equal("pending", group.Status);
        Assert.Equal([fixture.PriorCallId, fixture.SourceCallId], group.Calls.Select(call => call.CallId).Order());
        Assert.Empty(await fixture.Database.ListIncidentsAsync(
            fixture.Now.AddMinutes(-5).ToUnixTimeSeconds(),
            fixture.Now.AddMinutes(5).ToUnixTimeSeconds(),
            CancellationToken.None));

        await fixture.Database.AppendIncidentAssociationReviewAsync(
            new IncidentAssociationReviewLedgerEntry(
                "review:canary",
                fixture.Now.AddSeconds(3),
                group.ProposalKey,
                fixture.RunId,
                group.ProjectionEventId,
                IncidentAssociationReviewAction.ConfirmMembership,
                0,
                group.Calls.Select(call => call.CallId).ToList(),
                "operator",
                "Reviewed during canary."),
            CancellationToken.None);
        var reviewed = await fixture.Database.GetIncidentAssociationReviewReportAsync(
            true,
            fixture.RunId,
            fixture.Now.AddMinutes(-5).ToUnixTimeSeconds(),
            fixture.Now.AddMinutes(5).ToUnixTimeSeconds(),
            CancellationToken.None);
        Assert.Equal("confirmed", Assert.Single(reviewed.Groups).Status);
        Assert.Equal(0, reviewed.PendingGroupCount);
    }

    private sealed record StagedVerification(
        long BaseProjectionSequence,
        IncidentBatchLedgerEntry SourceEntry,
        IncidentBatchVerificationRequest Request,
        IncidentBatchVerificationResult Result,
        IncidentBatchProjection Projection);

    private sealed class CanaryFixture : IAsyncDisposable
    {
        private readonly string _root;

        private CanaryFixture(
            string root,
            string databasePath,
            EngineConfig config,
            EngineDatabase database,
            DateTimeOffset now,
            long priorCallId,
            long sourceCallId)
        {
            _root = root;
            DatabasePath = databasePath;
            Config = config;
            Database = database;
            Now = now;
            PriorCallId = priorCallId;
            SourceCallId = sourceCallId;
        }

        public string RunId => "run:canary";
        public string DatabasePath { get; }
        public EngineConfig Config { get; }
        public EngineDatabase Database { get; }
        public DateTimeOffset Now { get; }
        public long PriorCallId { get; }
        public long SourceCallId { get; }

        public static async Task<CanaryFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), $"pizzawave-batch-canary-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var databasePath = Path.Combine(root, "pizzad.db");
            var config = new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = databasePath, AudioRoot = root },
                AiInsights = new AiInsightsConfig
                {
                    IncidentBatchCanaryPersistenceEnabled = true,
                    IncidentAnalysisExecutionEnabled = false,
                    IncidentBatchConstructorShadowExclusiveInferenceWindow = true,
                    IncidentBatchConstructorShadowEnabled = true,
                    IncidentBatchRelationshipShadowEnabled = true,
                    IncidentBatchVerificationShadowEnabled = true,
                    IncidentBatchConstructorShadowObservationIsolated = true,
                    IncidentBatchConstructorShadowSourceIsolated = true,
                    IncidentBatchConstructorShadowRunId = "run:canary"
                }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = new DateTimeOffset(2026, 7, 23, 14, 0, 0, TimeSpan.Zero);
            var fixture = new CanaryFixture(root, databasePath, config, database, now, 0, 0);
            var priorCallId = await fixture.AddCallAsync(
                "prior",
                now.AddMinutes(-1),
                "White truck crashed on County Road 725.");
            var sourceCallId = await fixture.AddCallAsync(
                "source",
                now,
                "Critical injuries in that white truck crash.");
            return new CanaryFixture(root, databasePath, config, database, now, priorCallId, sourceCallId);
        }

        public Task<long> AddCallAsync(string key, DateTimeOffset timestamp, string transcript) =>
            Database.UpsertCallAsync(new EngineCall
            {
                UniqueKey = key,
                StartTime = timestamp.ToUnixTimeSeconds(),
                StopTime = timestamp.AddSeconds(5).ToUnixTimeSeconds(),
                SystemShortName = "test-system",
                Talkgroup = 1,
                TalkgroupName = "Test",
                Category = "other",
                Transcription = transcript,
                TranscriptionStatus = "complete",
                QualityReason = "model_needs_review"
            }, CancellationToken.None);

        public Task<StagedVerification> StageVerifiedMembershipAsync() =>
            StageVerificationAsync(
                IncidentBatchRelationshipDisposition.ConfirmedMembership,
                0);

        public Task<StagedVerification> StageVerifiedProvisionalAssociationAsync() =>
            StageVerificationAsync(
                IncidentBatchRelationshipDisposition.ProvisionalAssociation,
                0.4);

        private async Task<StagedVerification> StageVerificationAsync(
            IncidentBatchRelationshipDisposition disposition,
            double uncertainty)
        {
            var priorObservationId = $"call:{PriorCallId}";
            var sourceObservationId = $"call:{SourceCallId}";
            var priorObservation = new IncidentEventStateSourceObservation(
                priorObservationId,
                PriorCallId,
                Now.AddMinutes(-1).ToUnixTimeSeconds(),
                string.Empty,
                null,
                [new IncidentEventStateTranscriptObservation("transcript:prior", "White truck crashed on County Road 725.", "test", Now)],
                new Dictionary<string, IncidentEventStateMetadataObservation>());
            var sourceObservation = new IncidentEventStateSourceObservation(
                sourceObservationId,
                SourceCallId,
                Now.ToUnixTimeSeconds(),
                string.Empty,
                null,
                [new IncidentEventStateTranscriptObservation("transcript:source", "Critical injuries in that white truck crash.", "test", Now)],
                new Dictionary<string, IncidentEventStateMetadataObservation>());
            var bundle = new IncidentEventStateObservationBundle(
                "bundle:canary",
                Now,
                [priorObservation, sourceObservation],
                []);
            var prior = new IncidentBatchProjection(
                RunId,
                "projection:prior",
                Now.AddMinutes(-1),
                ["ledger:canary"],
                [new IncidentBatchProjectionEvent(
                    "projection:event:existing",
                    [priorObservationId],
                    "Vehicle crash",
                    "White truck crash is active.",
                    false,
                    true,
                    ["ledger:canary"])],
                []);
            var candidate = new IncidentBatchCandidate(
                "candidate:1",
                "projection:event:existing",
                [priorObservationId]);
            var constructorProposal = new IncidentBatchProposal(
                "proposal:canary",
                Now,
                "test-model",
                IncidentBatchPrompt.ObservationIsolatedProvisionalPromptIdentity,
                [new IncidentBatchEventProposal(
                    "event:source",
                    IncidentBatchEventDisposition.ProvisionalEvent,
                    string.Empty,
                    [sourceObservationId],
                    "Critical-injury crash",
                    "Critical injuries were reported.",
                    "This source observation is retained as a review event until relationship verification completes.",
                    0.1,
                    [new IncidentEventStateTranscriptCitation("transcript:source", "Critical injuries")],
                    [],
                    [],
                    [])]);
            var relationshipProposal = new IncidentBatchRelationshipProposal(
                "relationship:canary",
                Now,
                "test-model",
                IncidentBatchRelationshipPrompt.PromptIdentity,
                [new IncidentBatchRelationship(
                    "event:source",
                    candidate.CandidateToken,
                    disposition,
                    "Both calls explicitly describe the same white-truck crash.",
                    uncertainty,
                    [new IncidentEventStateTranscriptCitation("transcript:source", "white truck crash")],
                    [new IncidentEventStateTranscriptCitation("transcript:prior", "White truck crashed")],
                    [],
                    [])]);
            var coordinator = new IncidentBatchCoordinator(
                new FixedProposer(constructorProposal),
                new FixedRelationshipProposer(relationshipProposal),
                new IncidentBatchProvisionalStore(Database),
                new FixedTimeProvider(Now));
            var executionIdentity = string.Join(";",
                "test",
                IncidentBatchContract.PerEventAcceptanceConfigurationToken,
                IncidentBatchContract.PerCitationAcceptanceConfigurationToken,
                IncidentBatchExecutionArchitecture.StagedRelationshipAsynchronousConfirmationToken,
                IncidentBatchContract.ObservationIsolatedOwnershipConfigurationToken,
                IncidentBatchRelationshipContract.ConfigurationToken,
                IncidentBatchCanaryGate.ConfigurationToken);
            var batch = await coordinator.RunAsync(
                new IncidentBatchRunRequest(
                    RunId,
                    "ledger:canary",
                    "projection:canary",
                    [new IncidentBatchSingletonIdentity(sourceObservationId, "projection:event:source")],
                    "test",
                    executionIdentity),
                bundle,
                prior,
                [sourceObservationId],
                [candidate],
                CancellationToken.None);
            Assert.True(
                batch.LedgerEntry.Entry.ProposalValidationErrors.Count == 0,
                string.Join("; ", batch.LedgerEntry.Entry.ProposalValidationErrors));
            Assert.True(
                (batch.LedgerEntry.Entry.RelationshipProposalValidationErrors ?? []).Count == 0,
                string.Join("; ", batch.LedgerEntry.Entry.RelationshipProposalValidationErrors ?? []));
            Assert.Single(IncidentBatchRelationshipContract.AcceptedRelationships(batch.LedgerEntry.Entry));
            Assert.Single(IncidentBatchVerificationQueueContract.BuildRequests(batch.LedgerEntry.Entry));
            var request = Assert.Single(await Database.ListIncidentBatchVerificationRequestsAsync(
                RunId,
                10,
                CancellationToken.None)).Request;
            var confirmation = new IncidentBatchConfirmationProposal(
                "confirmation:canary",
                Now.AddSeconds(1),
                "test-verifier",
                IncidentBatchConfirmationPrompt.PromptIdentity,
                [new IncidentBatchConfirmationDecision(
                    "event:source",
                    candidate.CandidateToken,
                    IncidentBatchConfirmationDecisionKind.Verify,
                    "Both transcripts explicitly identify the same white-truck crash.",
                    [new IncidentEventStateTranscriptCitation("transcript:source", "white truck crash")],
                    [new IncidentEventStateTranscriptCitation("transcript:prior", "White truck crashed")],
                    [],
                    [])]);
            var result = IncidentBatchVerificationQueueContract.BuildResult(
                batch.LedgerEntry.Entry,
                request,
                confirmation,
                new IncidentBatchConfirmationExecutionContext(1200, string.Empty),
                Now.AddSeconds(2));
            var projection = IncidentBatchVerificationProjector.Apply(
                batch.Projection.Projection,
                batch.LedgerEntry.Entry,
                request,
                result,
                "projection:verified",
                Now.AddSeconds(2));
            return new StagedVerification(
                batch.Projection.Sequence,
                batch.LedgerEntry.Entry,
                request,
                result,
                projection);
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedProposer(IncidentBatchProposal proposal) : IIncidentBatchProposer
    {
        public Task<IncidentBatchProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<string> newObservationIds,
            IReadOnlyList<IncidentBatchCandidate> candidates,
            CancellationToken ct) =>
            Task.FromResult(proposal);
    }

    private sealed class FixedRelationshipProposer(IncidentBatchRelationshipProposal proposal) : IIncidentBatchRelationshipProposer
    {
        public Task<IncidentBatchRelationshipProposal> ProposeAsync(
            IncidentEventStateObservationBundle bundle,
            IReadOnlyList<IncidentBatchRelationshipSource> sources,
            IReadOnlyList<IncidentBatchCandidate> candidates,
            CancellationToken ct) =>
            Task.FromResult(proposal);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
