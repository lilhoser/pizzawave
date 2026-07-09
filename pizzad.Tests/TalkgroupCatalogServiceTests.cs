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
    public void PreviewCsv_MarksGenericOperationalTalkgroupsNotIncidentEligible()
    {
        const string csv = """
Decimal,Hex,Mode,Alpha Tag,Description,Tag,Category
2001,7D1,D,FAC OPS,Facilities maintenance,Operations,Other
2002,7D2,D,FIRE DISP,Fire dispatch,Fire Dispatch,Public Safety
2003,7D3,D,VALET,Hospital valet parking,Operations,Other
2004,7D4,D,MEDCOM,Medical transport,EMS Dispatch,Public Safety
""";

        var preview = TalkgroupCatalogService.PreviewCsv(csv);

        Assert.False(preview.Included.Single(row => row.Id == 2001).IncidentEligible);
        Assert.True(preview.Included.Single(row => row.Id == 2002).IncidentEligible);
        Assert.False(preview.Included.Single(row => row.Id == 2003).IncidentEligible);
        Assert.True(preview.Included.Single(row => row.Id == 2004).IncidentEligible);
    }

    [Fact]
    public async Task Resolve_ReturnsIncidentEligibilityFromCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = root },
                TrunkRecorder = new TrunkRecorderConfig { TalkgroupCatalogPath = Path.Combine(root, "talkgroups.json") }
            };
            var service = new TalkgroupCatalogService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<TalkgroupCatalogService>.Instance);
            await service.SaveAsync(new TalkgroupCatalogDocument
            {
                Items = [new TalkgroupCatalogItem { Id = 1001, AlphaTag = "Maintenance", IncidentEligible = false }]
            }, generateTrCsv: false, CancellationToken.None);

            Assert.False(service.Resolve(1001).IncidentEligible);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    [Fact]
    public async Task GenerateTrCsv_UsesCatalogStateNotActiveProfileOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profileId = Guid.NewGuid();
            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = root },
                TrunkRecorder = new TrunkRecorderConfig
                {
                    TalkgroupCatalogPath = Path.Combine(root, "talkgroups.json"),
                    TalkgroupsPath = Path.Combine(root, "talkgroups.csv")
                },
                Profiles = new ProfileConfig
                {
                    ActiveProfileId = profileId,
                    Items =
                    [
                        new ProcessingProfile
                        {
                            Id = profileId,
                            Name = "Troubleshooting",
                            Talkgroups =
                            [
                                new ProfileTalkgroupSetting { Id = 1001, Enabled = false },
                                new ProfileTalkgroupSetting { Id = 1002, Enabled = true, Category = "traffic", Label = "Road Ops", IncidentEligible = false }
                            ]
                        }
                    ]
                }
            };
            config.ApplyDefaults();
            var service = new TalkgroupCatalogService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<TalkgroupCatalogService>.Instance);
            var document = new TalkgroupCatalogDocument
            {
                Items =
                [
                    new TalkgroupCatalogItem { Id = 1001, AlphaTag = "Police", OpsCategory = "police", Enabled = true, IncidentEligible = true },
                    new TalkgroupCatalogItem { Id = 1002, AlphaTag = "Fire", OpsCategory = "fire", Enabled = false, IncidentEligible = true }
                ]
            };

            await service.SaveAsync(document, generateTrCsv: true, CancellationToken.None);
            var csv = await File.ReadAllTextAsync(config.TrunkRecorder.TalkgroupsPath);

            Assert.Contains("1001,3E9,D,Police,,,police", csv);
            Assert.DoesNotContain("1002", csv);
            var resolved = service.Resolve(1001);
            Assert.Equal("Police", resolved.Label);
            Assert.Equal("police", resolved.Category);
            Assert.True(resolved.IncidentEligible);

            var effective = service.EffectiveItemsForActiveProfile(document).Single(row => row.Id == 1002);
            Assert.Equal("Road Ops", effective.AlphaTag);
            Assert.Equal("traffic", effective.OpsCategory);
            Assert.False(effective.IncidentEligible);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_UsesCatalogIdentityNotProfileOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profileId = Guid.NewGuid();
            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = root },
                TrunkRecorder = new TrunkRecorderConfig { TalkgroupCatalogPath = Path.Combine(root, "talkgroups.json") },
                Profiles = new ProfileConfig
                {
                    ActiveProfileId = profileId,
                    Items =
                    [
                        new ProcessingProfile
                        {
                            Id = profileId,
                            Name = "Quiet",
                            Talkgroups = [new ProfileTalkgroupSetting { Id = 1001, Enabled = false, Label = "Muted", Category = "traffic" }]
                        }
                    ]
                }
            };
            config.ApplyDefaults();
            var service = new TalkgroupCatalogService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<TalkgroupCatalogService>.Instance);
            await service.SaveAsync(new TalkgroupCatalogDocument
            {
                Items = [new TalkgroupCatalogItem { Id = 1001, AlphaTag = "Police Dispatch", OpsCategory = "police", Enabled = true }]
            }, generateTrCsv: false, CancellationToken.None);

            var resolved = service.Resolve(1001);

            Assert.Equal("Police Dispatch", resolved.Label);
            Assert.Equal("police", resolved.Category);
            Assert.True(resolved.Found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdatePolicyAsync_DisablesOnlyExactSystemScopedTalkgroup()
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
            await service.SaveAsync(new TalkgroupCatalogDocument
            {
                Items =
                [
                    new TalkgroupCatalogItem { SystemShortName = "mswin", Id = 42518, AlphaTag = "Jackson PD", OpsCategory = "police", Enabled = true },
                    new TalkgroupCatalogItem { SystemShortName = "entergy", Id = 42518, AlphaTag = "Utility Ops", OpsCategory = "utilities", Enabled = true },
                    new TalkgroupCatalogItem { SystemShortName = "entergy", Id = 76, AlphaTag = "SYS Telecom", OpsCategory = "utilities", Enabled = true }
                ]
            }, generateTrCsv: true, CancellationToken.None);

            var result = await service.UpdatePolicyAsync(new TalkgroupCatalogPolicyUpdateRequest(
                [new TalkgroupCatalogPolicyTarget(SystemShortName: "entergy", Talkgroup: 42518)],
                Enabled: false), CancellationToken.None);

            var rows = service.Load().Items.ToDictionary(TalkgroupCatalogService.ItemKey, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(1, result.Updated);
            Assert.True(rows["mswin:42518"].Enabled);
            Assert.False(rows["entergy:42518"].Enabled);
            Assert.True(rows["entergy:76"].Enabled);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DownstreamProfilePolicy_DisabledTalkgroupBlocksPostTranscriptionWork()
    {
        var profileId = Guid.NewGuid();
        var config = new EngineConfig
        {
            Profiles = new ProfileConfig
            {
                ActiveProfileId = profileId,
                Items =
                [
                    new ProcessingProfile
                    {
                        Id = profileId,
                        Name = "Quiet",
                        Talkgroups = [new ProfileTalkgroupSetting { Id = 1001, Enabled = false }]
                    }
                ]
            }
        };
        config.ApplyDefaults();

        Assert.False(DownstreamProfilePolicy.Allows(config, "police", 1001));
        Assert.True(DownstreamProfilePolicy.Allows(config, "police", 1002));
    }
}
