using System.Reflection;
namespace pizzad.Tests;
public sealed class RecommendationCacheTests
{
    [Fact]
    public async Task CompletedSharedBuildIsReusedAfterTheOriginalWaiterWasCancelled()
    {
        var service = new SystemRecommendationService(null!,null!,null!,null!,null!,null!,null!,null!);
        var result = new SystemRecommendationsDto(0,0,0,0,0,0,[],[],[],[]) { GeneratedAtUtc = DateTime.UtcNow };
        typeof(SystemRecommendationService).GetField("_recommendationBuildTask",BindingFlags.Instance|BindingFlags.NonPublic)!
            .SetValue(service,Task.FromResult(result));
        Assert.Same(result,await service.BuildAsync(CancellationToken.None));
        Assert.Same(result,await service.BuildAsync(CancellationToken.None));
    }
}
