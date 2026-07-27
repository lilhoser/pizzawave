namespace pizzad.Tests;

public sealed class TalkgroupCatalogServiceTests
{
    [Fact]
    public async Task QueryPage_FallsBackToTalkgroupIdWhenCallSiteAndCatalogSystemDiffer()
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
            await TestCatalogWriter.WriteAsync(config, new TalkgroupCatalogDocument
            {
                Items =
                [
                    new TalkgroupCatalogItem { SystemShortName = "mswin", Id = 13541, AlphaTag = "Capitol Police" },
                    new TalkgroupCatalogItem { SystemShortName = "entergy", Id = 76, AlphaTag = "Utility" }
                ]
            });

            var page = service.QueryPage(null, null, null, null, null, 1, 50, "etv-raymond-hinds:13541");

            var row = Assert.Single(page.Items);
            Assert.Equal("mswin", row.SystemShortName);
            Assert.Equal(13541, row.Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
    public void BuildLabel_PrefersDescriptionAndStructuredJurisdiction()
    {
        var row = new TalkgroupCatalogItem
        {
            Id = 1001,
            AlphaTag = "HC SO DISP",
            Description = "Sheriff - Dispatch",
            Jurisdiction = "Hinds County"
        };

        Assert.Equal("Hinds County — Sheriff Dispatch", TalkgroupCatalogService.BuildLabel(row));
    }

    [Fact]
    public void PreviewRadioReferenceHtml_PreservesTalkgroupJurisdictionHeading()
    {
        const string html = """
<html><body>
  <h3>System Talkgroups</h3>
  <h5>Copiah County (15)<span><a>View Talkgroup Category Details</a></span></h5>
  <table>
    <tr><th>DEC</th><th>HEX</th><th>Mode</th><th>Alpha Tag</th><th>Description</th><th>Tag</th></tr>
    <tr><td>42060</td><td>a44c</td><td>T</td><td>15-CSPD DISPATCH</td><td>Crystal Springs Police - Dispatch</td><td>Law Dispatch</td></tr>
  </table>
  <h3>Other Details</h3>
</body></html>
""";

        var preview = TalkgroupCatalogService.PreviewRadioReferenceHtml(html, "mswin");

        var row = Assert.Single(preview.Included);
        Assert.Equal("Copiah County", row.Jurisdiction);
        Assert.Equal("Copiah County — Crystal Springs Police Dispatch", TalkgroupCatalogService.BuildLabel(row));
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
            await TestCatalogWriter.WriteAsync(config, new TalkgroupCatalogDocument
            {
                Items = [new TalkgroupCatalogItem { Id = 1001, AlphaTag = "Maintenance", IncidentEligible = false }]
            });

            Assert.False(service.Resolve(1001).IncidentEligible);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Resolve_MapsConfiguredReceiverSiteToTalkgroupCatalogSystem()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = root },
                TrunkRecorder = new TrunkRecorderConfig { TalkgroupCatalogPath = Path.Combine(root, "talkgroups.json") },
                SiteSetup = new SiteSetupConfig
                {
                    Systems = [new RfSurveySystemDto("etv-raymond-hinds", "ETV Raymond", [], [], "4879", "mswin")]
                }
            };
            await TestCatalogWriter.WriteAsync(config, new TalkgroupCatalogDocument
            {
                Items =
                [
                    new TalkgroupCatalogItem { SystemShortName = "entergy", Id = 76, Description = "Utility Telecom", Enabled = true },
                    new TalkgroupCatalogItem { SystemShortName = "mswin", Id = 76, Description = "Public Safety", Enabled = true }
                ]
            });
            var service = new TalkgroupCatalogService(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<TalkgroupCatalogService>.Instance);

            var resolved = service.Resolve("etv-raymond-hinds", 76);

            Assert.Equal("mswin", resolved.SystemShortName);
            Assert.Equal("Public Safety", resolved.Label);
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

            await TestCatalogWriter.WriteAsync(config, document);
            await service.GenerateTrCsvAsync(CancellationToken.None);
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
            await TestCatalogWriter.WriteAsync(config, new TalkgroupCatalogDocument
            {
                Items = [new TalkgroupCatalogItem { Id = 1001, AlphaTag = "Police Dispatch", OpsCategory = "police", Enabled = true }]
            });

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
            await TestCatalogWriter.WriteAsync(config, new TalkgroupCatalogDocument
            {
                Items =
                [
                    new TalkgroupCatalogItem { SystemShortName = "mswin", Id = 42518, AlphaTag = "Jackson PD", OpsCategory = "police", Enabled = true },
                    new TalkgroupCatalogItem { SystemShortName = "entergy", Id = 42518, AlphaTag = "Utility Ops", OpsCategory = "utilities", Enabled = true },
                    new TalkgroupCatalogItem { SystemShortName = "entergy", Id = 76, AlphaTag = "SYS Telecom", OpsCategory = "utilities", Enabled = true }
                ]
            });
            await service.GenerateTrCsvAsync(CancellationToken.None);

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

    [Fact]
    public void DownstreamProfilePolicy_UtilitiesHasIndependentVisibility()
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
                        Name = "Public safety only",
                        IncludeUtilities = false,
                        IncludeOther = true
                    }
                ]
            }
        };
        config.ApplyDefaults();

        Assert.False(DownstreamProfilePolicy.Allows(config, "utilities", "entergy", 76));
        Assert.True(DownstreamProfilePolicy.Allows(config, "other", "entergy", 76));
    }
}
