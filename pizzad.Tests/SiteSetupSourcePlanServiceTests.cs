namespace pizzad.Tests;

public sealed class SiteSetupSourcePlanServiceTests
{
    [Fact]
    public void Projection_RecommendsBroadestServerOwnedFitWithExactAssignments()
    {
        var desired = Setup(version: 42);
        var projection = new SiteSetupSourcePlanService().Project(desired);

        Assert.Equal(42, projection.DesiredVersion);
        Assert.NotEmpty(projection.ProjectionVersion);
        var recommended = Assert.Single(projection.Options, option => option.Id == projection.RecommendedOptionId);
        Assert.True(recommended.Fits);
        Assert.Equal(2, recommended.SystemShortNames.Count);
        Assert.Single(recommended.Windows);
        Assert.Equal(0, recommended.SelectedSourceIndexes.Single());
        Assert.Equal(0, recommended.SourceAssignments["hinds"]);
        Assert.Equal(0, recommended.SourceAssignments["rankin"]);
    }

    [Fact]
    public void Selection_RejectsProjectionFromAnOlderSetupRevision()
    {
        var planner = new SiteSetupSourcePlanService();
        var original = Setup(version: 42);
        var projection = planner.Project(original);
        var current = Setup(version: 43);
        var request = new SiteSetupSourcePlanSelectionRequest(43, projection.ProjectionVersion, projection.RecommendedOptionId, projection.SampleRateHz);

        var error = Assert.Throws<InvalidOperationException>(() => planner.Select(current, request));
        Assert.Contains("changed after this projection", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static SiteSetupConfig Setup(long version) => new()
    {
        DesiredVersion = version,
        Systems =
        [
            new RfSurveySystemDto("hinds", "Hinds County", [851_100_000], [852_000_000], "1", "mswin"),
            new RfSurveySystemDto("rankin", "Rankin County", [851_500_000], [852_400_000], "1", "mswin")
        ],
        Sources = [new RfSurveySourceDto(0, "rtl=serial:abc", "abc", "rtl-sdr", 851_750_000, 2_400_000, 0, "auto")]
    };
}
