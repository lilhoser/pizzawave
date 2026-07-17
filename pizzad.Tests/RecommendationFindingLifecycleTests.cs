using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class RecommendationFindingLifecycleTests
{
    [Fact]
    public async Task FindingsMoveFromNewToReviewedToResolvedAndCanRecur()
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

            await database.MarkRecommendationReviewedAsync(finding.Id, now.AddMinutes(1), CancellationToken.None);
            var reviewed = await database.SyncRecommendationFindingsAsync([finding], now.AddMinutes(2), CancellationToken.None);
            Assert.Equal("active", Assert.Single(reviewed.Active).Lifecycle);

            var resolved = await database.SyncRecommendationFindingsAsync([], now.AddMinutes(3), CancellationToken.None);
            Assert.Empty(resolved.Active);
            Assert.Equal("resolved", Assert.Single(resolved.Resolved).Lifecycle);

            var recurred = await database.SyncRecommendationFindingsAsync([finding], now.AddMinutes(4), CancellationToken.None);
            Assert.Equal("new", Assert.Single(recurred.Active).Lifecycle);
            Assert.Single(recurred.Resolved);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
