using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class RecommendationFindingLifecycleTests
{
    [Fact]
    public async Task OperatorOwnsWorkflowWhileEvidenceActivityChangesIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio")
                }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = DateTime.UtcNow;
            var finding = new SystemRecommendationDto(
                "test-finding", "runtime", "medium", "Test finding", "Evidence", "Review it",
                new RecommendationTargetDto("cpu", "", ""), []);

            var first = await database.SyncRecommendationFindingsAsync([finding], now, CancellationToken.None);
            Assert.Equal("new", Assert.Single(first.Active).Lifecycle);

            Assert.True(await database.SetRecommendationWorkflowAsync(first.Active[0].FindingId, new("investigating", "Checking the RF path."), now.AddMinutes(1), CancellationToken.None));
            var investigating = await database.SyncRecommendationFindingsAsync([finding], now.AddMinutes(2), CancellationToken.None);
            Assert.Equal("investigating", Assert.Single(investigating.Active).WorkflowStatus);
            Assert.Contains(investigating.Active[0].Audit, row => row.EventType == "workflow_changed" && row.Actor == "operator");

            var quiet = await database.SyncRecommendationFindingsAsync([], now.AddMinutes(3), CancellationToken.None);
            Assert.Equal("investigating", Assert.Single(quiet.Active).WorkflowStatus);
            Assert.Equal("quiet", quiet.Active[0].ActivityState);

            Assert.True(await database.SetRecommendationWorkflowAsync(quiet.Active[0].FindingId, new("known_issue", "External interference; review weekly.", 7), now.AddMinutes(4), CancellationToken.None));
            var known = await database.SyncRecommendationFindingsAsync([], now.AddMinutes(5), CancellationToken.None);
            Assert.Empty(known.Active);
            Assert.Equal("known_issue", Assert.Single(known.KnownIssues).WorkflowStatus);

            Assert.True(await database.SetRecommendationWorkflowAsync(known.KnownIssues[0].FindingId, new("resolved", "RF path repaired."), now.AddMinutes(6), CancellationToken.None));
            var resolved = await database.SyncRecommendationFindingsAsync([], now.AddMinutes(7), CancellationToken.None);
            Assert.Equal("resolved", Assert.Single(resolved.Resolved).WorkflowStatus);

            var recurred = await database.SyncRecommendationFindingsAsync([finding], now.AddMinutes(8), CancellationToken.None);
            Assert.Equal("new", Assert.Single(recurred.Active).Lifecycle);
            Assert.Single(recurred.Resolved);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task RfFindingMovesToHistoryWhenPresentationEvidenceAgesOut()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio")
                }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = DateTime.UtcNow;
            var finding = new SystemRecommendationDto(
                "tr-rf-temporal-v2:site-a", "trunk-recorder", "medium", "RF finding", "Evidence", "Review it",
                new RecommendationTargetDto("tr", "metrics", "site-a"), []);

            var active = await database.SyncRecommendationFindingsAsync([finding], now, CancellationToken.None);
            Assert.Single(active.Active);

            var agedOut = await database.SyncRecommendationFindingsAsync([], now.AddMinutes(5), CancellationToken.None);

            Assert.Empty(agedOut.Active);
            var historical = Assert.Single(agedOut.Resolved);
            Assert.Equal("resolved", historical.WorkflowStatus);
            Assert.Contains("No active degradation", historical.Resolution);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
