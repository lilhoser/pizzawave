namespace pizzad.Tests;

public sealed class TalkgroupCatalogServiceTests
{
    [Fact]
    public void PreviewCsv_ExcludesEncryptedUnknownAndUnwantedRows()
    {
        const string csv = """
Decimal,Hex,Mode,Alpha Tag,Description,Tag,Category
1001,3E9,D,HC SO DISP,Sheriff Dispatch,Law Dispatch,Public Safety
1002,3EA,E,ENC OPS,Encrypted Ops,Law Tac,Public Safety
1003,3EB,D,UNKNOWN,Unknown talkgroup,Unknown,Public Safety
1004,3EC,D,OLD,Deprecated channel,Deprecated,Public Safety
""";

        var preview = TalkgroupCatalogService.PreviewCsv(csv);

        Assert.Single(preview.Included);
        Assert.Equal(3, preview.Excluded.Count);
        Assert.Equal("police", preview.Included[0].OpsCategory);
    }

    [Theory]
    [InlineData("Law Dispatch", "HC SO", "Sheriff Dispatch", "police")]
    [InlineData("Fire Dispatch", "Engine 1", "Structure fire", "fire")]
    [InlineData("EMS Dispatch", "Medic 1", "Medical transport", "ems")]
    [InlineData("Transportation", "TDOT", "Road service patrol", "traffic")]
    [InlineData("Schools", "Bus", "Routine", "other")]
    public void NormalizeOpsCategory_MapsRadioCategories(string category, string tag, string description, string expected)
    {
        Assert.Equal(expected, TalkgroupCatalogService.NormalizeOpsCategory(category, tag, description));
    }

    [Fact]
    public void BuildLabel_CombinesDifferentAlphaAndDescription()
    {
        var row = new TalkgroupCatalogItem
        {
            Id = 1001,
            AlphaTag = "HC SO DISP",
            Description = "County Sheriff Dispatch"
        };

        Assert.Equal("HC SO DISP - County Sheriff Dispatch", TalkgroupCatalogService.BuildLabel(row));
    }

    [Fact]
    public async Task GenerateTrCsv_WritesOnlyEnabledRowsWithOpsCategory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = root },
                TrunkRecorder = new TrunkRecorderConfig
                {
                    TalkgroupCatalogPath = Path.Combine(root, "talkgroups.json"),
                    TalkgroupsPath = Path.Combine(root, "talkgroups.csv")
                }
            };
            var service = new TalkgroupCatalogService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<TalkgroupCatalogService>.Instance);
            var document = new TalkgroupCatalogDocument
            {
                Items =
                [
                    new TalkgroupCatalogItem { Id = 1001, AlphaTag = "Police", OpsCategory = "police", Enabled = true },
                    new TalkgroupCatalogItem { Id = 1002, AlphaTag = "Chatty", OpsCategory = "other", Enabled = false }
                ]
            };

            await service.GenerateTrCsvAsync(document, CancellationToken.None);

            var csv = await File.ReadAllTextAsync(config.TrunkRecorder.TalkgroupsPath);
            Assert.Contains("1001,3E9,D,Police,,,police", csv);
            Assert.DoesNotContain("1002", csv);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
