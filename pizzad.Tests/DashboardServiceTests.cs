using System.Reflection;

namespace pizzad.Tests;

public sealed class DashboardServiceTests
{
    [Fact]
    public void LocationHelpers_TolerateNullText()
    {
        var normalize = typeof(DashboardService).GetMethod("NormalizeLocationKey", BindingFlags.NonPublic | BindingFlags.Static);
        var plausible = typeof(DashboardService).GetMethod("IsPlausibleLocation", BindingFlags.NonPublic | BindingFlags.Static);
        var extract = typeof(DashboardService).GetMethod("ExtractLocations", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(normalize);
        Assert.NotNull(plausible);
        Assert.NotNull(extract);
        Assert.Equal(string.Empty, normalize.Invoke(null, [null]));
        Assert.False((bool)plausible.Invoke(null, [null])!);
        var rows = Assert.IsAssignableFrom<IEnumerable<string>>(extract.Invoke(null, [null]));
        Assert.Empty(rows);
    }
}
