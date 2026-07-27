using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class TranscriptLocationServiceTests
{
    [Fact]
    public void LocationHelpers_TolerateNullText()
    {
        Assert.Equal(string.Empty, TranscriptLocationService.NormalizeLocationKey(null));
        Assert.False(TranscriptLocationService.IsPlausibleLocation(null));
        Assert.Empty(TranscriptLocationService.ExtractLocations(null));
    }

    [Theory]
    [InlineData("Boulevard")]
    [InlineData("Highway")]
    [InlineData("East Highway")]
    [InlineData("S Dr")]
    public void LocationHelpers_RejectBareStreetTypes(string text)
    {
        Assert.False(TranscriptLocationService.IsPlausibleLocation(text));
        Assert.DoesNotContain(text, TranscriptLocationService.ExtractLocations(text), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Main Boulevard")]
    [InlineData("7 Cherokee Boulevard")]
    [InlineData("East Martin Luther King Boulevard")]
    [InlineData("Highway 58")]
    public void LocationHelpers_KeepSpecificLocations(string text)
    {
        Assert.True(TranscriptLocationService.IsPlausibleLocation(text));
        Assert.Contains(text, TranscriptLocationService.ExtractLocations(text), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void LocationExtractor_CapturesInterstateMarkerAndLandmark()
    {
        var locations = TranscriptLocationService.ExtractLocations(
            "Medical 16, respond to the 180.8 Interstate 24 eastbound near Barn Nursery for a car accident with injuries.").ToArray();

        Assert.Contains("I-24 MM 180.8", locations);
        Assert.Contains("Barn Nursery", locations);
    }

    [Fact]
    public void LocationExtractor_CapturesNumberedHighwayStreetAddress()
    {
        var locations = TranscriptLocationService.ExtractLocations(
            "Medic 14 responding to 4510 Highway 58 for a diabetic emergency.").ToArray();

        Assert.Contains("4510 Highway 58", locations);
    }

    [Fact]
    public async Task CallLocation_UsesTalkgroupJurisdictionAndIgnoresLegacyAreaAuthority()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-location-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = root, AudioRoot = Path.Combine(root, "audio"), DatabasePath = Path.Combine(root, "test.db") },
                TrunkRecorder = new TrunkRecorderConfig { TalkgroupCatalogPath = Path.Combine(root, "talkgroups.json") },
                SiteSetup = new SiteSetupConfig
                {
                    Systems = [new RfSurveySystemDto("hinds", "Hinds County", [851_100_000], [], "123", "mswin")]
                },
                Locations = new LocationConfig
                {
                    MonitoredAreas = [new MonitoredAreaConfig { AreaId = "legacy-tn", AreaLabel = "Montgomery County, Tennessee", SystemShortName = "hinds", North = 37, South = 36, East = -86, West = -88 }]
                }
            };
            await TestCatalogWriter.WriteAsync(config, new TalkgroupCatalogDocument
            {
                Items = [new TalkgroupCatalogItem { Key = TalkgroupCatalogService.CatalogKey("mswin", 101), SystemShortName = "mswin", Id = 101, AlphaTag = "Hinds Dispatch", Jurisdiction = "Hinds County, Mississippi", Enabled = true }]
            });
            var catalog = new TalkgroupCatalogService(config, NullLogger<TalkgroupCatalogService>.Instance);
            var service = new TranscriptLocationService(config, catalog);
            var rows = service.ExtractCallLocations(new EngineCall { Id = 7, SystemShortName = "hinds", Talkgroup = 101, Transcription = "Respond to 4510 Highway 58." });

            Assert.Contains(rows, value => value.LocationText == "4510 Highway 58");
            var row = rows.First(value => value.LocationText == "4510 Highway 58");
            Assert.All(rows, value => Assert.Equal("Hinds County, Mississippi", value.AreaLabel));
            Assert.Equal("Hinds County, Mississippi", row.AreaLabel);
            Assert.StartsWith("rr-", row.AreaId);
            Assert.Equal("Hinds County, Mississippi", service.ResolveAreaById(row.AreaId)?.AreaLabel);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task CallLocation_ReusesCompatibleBoundedAreaFromSiteSetup()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-location-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = root, AudioRoot = Path.Combine(root, "audio"), DatabasePath = Path.Combine(root, "test.db") },
                TrunkRecorder = new TrunkRecorderConfig { TalkgroupCatalogPath = Path.Combine(root, "talkgroups.json") },
                SiteSetup = new SiteSetupConfig
                {
                    Systems = [new RfSurveySystemDto("etv-raymond-hinds", "ETV Raymond Hinds", [773_781_250], [], "4879", "")]
                },
                Locations = new LocationConfig
                {
                    MonitoredAreas =
                    [
                        new MonitoredAreaConfig
                        {
                            AreaId = "hinds-ms",
                            AreaLabel = "Hinds County, Mississippi",
                            SystemShortName = "etv-raymond-hinds",
                            North = 32.573783,
                            South = 32.048352,
                            East = -90.065681,
                            West = -90.728630,
                            Aliases = ["etv-raymond-hinds"]
                        }
                    ]
                }
            };
            await TestCatalogWriter.WriteAsync(config, new TalkgroupCatalogDocument());
            var catalog = new TalkgroupCatalogService(config, NullLogger<TalkgroupCatalogService>.Instance);
            var service = new TranscriptLocationService(config, catalog);

            var rows = service.ExtractCallLocations(new EngineCall
            {
                Id = 8,
                SystemShortName = "etv-raymond-hinds",
                Talkgroup = 47575,
                Transcription = "Respond to 120 John L. May Road."
            });

            var row = Assert.Single(rows, value => value.LocationText == "120 John L. May Road");
            Assert.Equal("hinds-ms", row.AreaId);
            Assert.Equal("Hinds County, Mississippi", row.AreaLabel);
            Assert.Equal("Hinds County, Mississippi", service.ResolveAreaById(row.AreaId)?.AreaLabel);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task Dashboard_CallTimelinePreservesChronologyAcrossDays()
    {
        using var temp = new TempStore();
        var config = temp.CreateConfig();
        var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var catalog = new TalkgroupCatalogService(config, NullLogger<TalkgroupCatalogService>.Instance);
        var start = DateTimeOffset.UtcNow.AddHours(-48).ToUnixTimeSeconds();
        await database.UpsertCallAsync(Call("first-day", start + 600, "site-a", 10, "First", "police"), CancellationToken.None);
        await database.UpsertCallAsync(Call("second-day", start + 24 * 3600 + 600, "site-a", 11, "Second", "fire"), CancellationToken.None);

        var dashboard = await Service(config, database, catalog)
            .BuildDashboardAsync(start, start + 48 * 3600, CancellationToken.None);

        Assert.Equal(3600, dashboard.CallActivity.BucketSeconds);
        Assert.Equal(2, dashboard.CallActivity.TotalCalls);
        Assert.Equal(2, dashboard.CallActivity.UniqueTalkgroups);
        Assert.Equal(2, dashboard.CallVolumeTimeline.Select(row => row.Start).Distinct().Count());
        Assert.All(dashboard.CallVolumeTimeline, row => Assert.Equal(0, row.Start % 3600));
        Assert.Contains(dashboard.TopTalkgroups, row => row.Talkgroup == 10 && row.Category == "police");
        Assert.Contains(dashboard.TopTalkgroups, row => row.Talkgroup == 11 && row.Category == "fire");
    }

    [Fact]
    public async Task CategoryPage_UsesCatalogCategoryOverrideWithoutLoadingAllCalls()
    {
        using var temp = new TempStore();
        var config = temp.CreateConfig();
        var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var catalog = new TalkgroupCatalogService(config, NullLogger<TalkgroupCatalogService>.Instance);
        await TestCatalogWriter.WriteAsync(config, new TalkgroupCatalogDocument
        {
            Items =
            [
                new TalkgroupCatalogItem
                {
                    Key = TalkgroupCatalogService.CatalogKey("entergy", 76),
                    SystemShortName = "entergy",
                    Id = 76,
                    AlphaTag = "WC MS Ops",
                    OpsCategory = "utilities",
                    Enabled = true,
                    IncidentEligible = true,
                    Source = "test"
                }
            ]
        });

        var start = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var callId = await database.UpsertCallAsync(Call("call-76", start + 10, "entergy", 76, "WC MS Ops", "other"), CancellationToken.None);
        var service = Service(config, database, catalog);

        var page = await service.BuildCategoryPageAsync("utilities", "talkgroup", start, start + 300, string.Empty, CancellationToken.None);
        var group = Assert.Single(page.Groups);
        Assert.Equal("WC MS Ops", group.Label);
        Assert.Equal(TalkgroupCatalogService.CatalogKey("entergy", 76), group.TalkgroupKey);
        Assert.Equal(1, group.Count);
        Assert.Equal(1, group.StrongCount);

        var expanded = await service.BuildCategoryTalkgroupCallsAsync("utilities", group.TalkgroupKey, start, start + 300, 20, CancellationToken.None);
        var call = Assert.Single(expanded.Calls);
        Assert.Equal(callId, call.Id);
        Assert.Equal("utilities", call.Category);
    }

    [Fact]
    public async Task TopAudioTalkgroups_UsesPersistedQualityReasonForRepetitiveCounts()
    {
        using var temp = new TempStore();
        var config = temp.CreateConfig();
        var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        await database.UpsertCallAsync(Call("repetitive-1", start + 10, "entergy", 76, "WC MS Ops", "utilities") with
        {
            QualityReason = "repetitive",
            Transcription = "yeah"
        }, CancellationToken.None);

        var rows = await database.ListTopAudioTalkgroupsAsync(start, 10, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.RepetitiveCalls);
    }

    private static DashboardService Service(EngineConfig config, EngineDatabase database, TalkgroupCatalogService catalog) =>
        new(
            database,
            config,
            new GeocodingService(database, NullLogger<GeocodingService>.Instance, new HttpClient()),
            catalog);

    private static EngineCall Call(string key, long start, string system, long talkgroup, string label, string category) => new()
    {
        UniqueKey = key,
        StartTime = start,
        StopTime = start + 5,
        Source = 0,
        SystemShortName = system,
        CallstreamCallId = start,
        Talkgroup = talkgroup,
        TalkgroupName = label,
        Frequency = 854_000_000,
        Category = category,
        AudioPath = string.Empty,
        Transcription = "unit test",
        TranscriptionStatus = "complete",
        QualityReason = "ok",
        RawMetadataJson = "{}"
    };

    private sealed class TempStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pizzawave-dashboard-test-" + Guid.NewGuid().ToString("N"));

        public EngineConfig CreateConfig()
        {
            Directory.CreateDirectory(_root);
            return new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(_root, "pizzad.db"),
                    AudioRoot = Path.Combine(_root, "audio"),
                    AppDataRoot = _root
                },
                TrunkRecorder = new TrunkRecorderConfig
                {
                    TalkgroupCatalogPath = Path.Combine(_root, "talkgroups.json"),
                    TalkgroupsPath = Path.Combine(_root, "talkgroups.csv")
                }
            };
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
                // Best effort cleanup for Windows file handles.
            }
        }
    }
}
