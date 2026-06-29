namespace pizzad.Tests;

using System.Text.Json;

public sealed class SetupTrConfigBuilderServiceTests
{
    [Fact]
    public async Task ListSitesAsync_ReturnsSelectableRadioReferenceRows()
    {
        var html = """
            Sites and Frequencies
            2 (2) 008 (8) ETV Raymond Hinds 770.08125 773.03125c 773.28125c
            2 (2) 009 (9) Utica Hinds 769.58125 774.28125c 774.53125c
            2 (2) 013 (D) Jackson Hinds 769.16875 773.06875c 773.31875c
            """;
        var service = new SetupTrConfigBuilderService(new HttpClient(), new EngineConfig());

        var result = await service.ListSitesAsync(new SetupTrConfigSitesRequest("4879", html), CancellationToken.None);

        Assert.Equal(3, result.Sites.Count);
        Assert.Contains(result.Sites, site => site.Name.Equals("ETV Raymond Hinds", StringComparison.OrdinalIgnoreCase) && site.ControlChannelCount == 2);
        Assert.Contains(result.Sites, site => site.Name.Equals("Utica Hinds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Found 3", result.Diagnostics);
    }

    [Fact]
    public async Task DraftAsync_RequestedRadioReferenceSiteStopsAtNextSiteRow()
    {
        var html = """
            Sites and Frequencies
            2 (2) 008 (8) ETV Raymond Hinds 770.08125 770.15625 770.35625 770.40625 770.60625 770.65625 770.85625
                770.85626 771.10625 771.35625 771.65625 771.98125 773.03125c 773.28125c
                773.53125c 773.78125c
            2 (2) 009 (9) Utica Hinds 769.58125 769.83125 774.03125 774.28125c 774.53125c 774.78125c
            2 (2) 013 (D) Jackson Hinds 769.16875 769.41875 773.06875c 773.31875c
            """;
        var config = new EngineConfig();
        var service = new SetupTrConfigBuilderService(new HttpClient(), config);

        var draft = await service.DraftAsync(new SetupTrConfigDraftRequest(
            RadioReferenceSid: "4879",
            HtmlText: html,
            SiteNames: "etv raymond",
            SdrSerials: "00000001",
            SampleRate: 2_400_000), CancellationToken.None);

        var system = Assert.Single(draft.Systems);
        Assert.Equal("etv raymond", system.SiteName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(16, system.FrequenciesMhz.Count);
        Assert.Equal(4, system.ControlChannelsMhz.Count);
        Assert.DoesNotContain(system.FrequenciesMhz, frequency => Math.Abs(frequency - 774.28125) < 0.000001);
        Assert.All(system.ControlChannelsMhz, control => Assert.Contains(control, draft.Sources[0].CoveredFrequenciesMhz));
    }

    [Fact]
    public async Task DraftAsync_DoesNotRequireAdditionalSerialsForNonControlRows()
    {
        var html = """
            Sites and Frequencies
            2 (2) 008 (8) ETV Raymond Hinds 770.08125 770.15625 770.35625 770.40625 770.60625 770.65625 770.85625
                770.85626 771.10625 771.35625 771.65625 771.98125 773.03125c 773.28125c
                773.53125c 773.78125c
            2 (2) 009 (9) Utica Hinds 769.58125 769.83125 774.03125 774.28125c 774.53125c 774.78125c
            """;
        var config = new EngineConfig();
        var service = new SetupTrConfigBuilderService(new HttpClient(), config);

        var draft = await service.DraftAsync(new SetupTrConfigDraftRequest(
            RadioReferenceSid: "4879",
            HtmlText: html,
            SiteNames: "etv raymond",
            SdrSerials: "00000001,00000002",
            SampleRate: 2_400_000), CancellationToken.None);

        var source = Assert.Single(draft.Sources);
        Assert.Equal("00000001", draft.Systems[0].AssignedSerial);
        var covered = draft.Sources.SelectMany(source => source.CoveredFrequenciesMhz).Distinct().ToHashSet();
        Assert.All(draft.Systems[0].ControlChannelsMhz, frequency => Assert.Contains(frequency, covered));
        Assert.Contains("fall outside", draft.Systems[0].Warning);
        Assert.Contains(draft.Warnings, warning => warning.Contains("not needed", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2_400_000, source.SampleRate);
    }

    [Fact]
    public async Task SourcePlanAsync_WarnsWhenSelectedSitesNeedMoreWindowsThanDetectedSdrs()
    {
        var html = """
            Sites and Frequencies
            2 (2) 008 (8) Site One County 770.08125 770.33125c 770.58125c
            2 (2) 009 (9) Site Two County 856.11250 856.36250c 856.61250c
            """;
        var service = new SetupTrConfigBuilderService(new HttpClient(), new EngineConfig());

        var plan = await service.SourcePlanAsync(new SetupTrConfigSourcePlanRequest(
            RadioReferenceSid: "4879",
            HtmlText: html,
            SiteNames: "Site One County,Site Two County",
            SdrSerials: "00000001",
            SampleRate: 2_400_000), CancellationToken.None);

        Assert.Equal(2, plan.RequiredSourceCount);
        Assert.Equal(1, plan.AvailableSourceCount);
        Assert.Single(plan.Sources);
        Assert.Contains(plan.Warnings, warning => warning.Contains("need 2 SDR source window", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, warning => warning.Contains("uncovered control channels", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SourcePlanAsync_PreservesCommaInSelectedSiteNameList()
    {
        var html = """
            Sites and Frequencies
            2 (2) 001 (1) Clarksville Simulcast Montgomery, TN 851.47500 851.66250c 852.17500c
            2 (2) 002 (2) Other Site County, TN 856.11250 856.36250c 856.61250c
            """;
        var service = new SetupTrConfigBuilderService(new HttpClient(), new EngineConfig());

        var plan = await service.SourcePlanAsync(new SetupTrConfigSourcePlanRequest(
            RadioReferenceSid: "6355",
            HtmlText: html,
            SiteNameList: ["Clarksville Simulcast Montgomery, TN"],
            SdrSerials: "00000001",
            SampleRate: 2_048_000), CancellationToken.None);

        var system = Assert.Single(plan.Systems);
        Assert.Equal("Clarksville Simulcast Montgomery, TN", system.SiteName);
        Assert.DoesNotContain(plan.Warnings, warning => warning.Contains("No frequency table matched", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SourcePlanAsync_UsesTrGuardedBandwidthForControlChannelCoverage()
    {
        var html = """
            Sites and Frequencies
            2 (2) 011 (B) Chattanooga Simulcast Hamilton, TN 855.21250c 856.23750c 856.76250c 857.23750c
            """;
        var service = new SetupTrConfigBuilderService(new HttpClient(), new EngineConfig());

        var narrow = await service.SourcePlanAsync(new SetupTrConfigSourcePlanRequest(
            RadioReferenceSid: "6355",
            HtmlText: html,
            SiteNameList: ["Chattanooga Simulcast Hamilton, TN"],
            SdrSerials: "00000001",
            SampleRate: 2_048_000), CancellationToken.None);
        var wide = await service.SourcePlanAsync(new SetupTrConfigSourcePlanRequest(
            RadioReferenceSid: "6355",
            HtmlText: html,
            SiteNameList: ["Chattanooga Simulcast Hamilton, TN"],
            SdrSerials: "00000001",
            SampleRate: 2_400_000), CancellationToken.None);

        Assert.Equal(2, narrow.RequiredSourceCount);
        Assert.Contains(narrow.Warnings, warning => warning.Contains("need 2 SDR source window", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, wide.RequiredSourceCount);
        Assert.Empty(wide.Warnings);
        Assert.All(wide.Systems[0].ControlChannelsMhz, control => Assert.Contains(control, wide.Sources[0].CoveredFrequenciesMhz));
    }

    [Fact]
    public async Task DraftAsync_GeneratesTypedAirspySource()
    {
        var html = """
            Sites and Frequencies
            2 (2) 011 (B) Chattanooga Simulcast Hamilton, TN 855.21250c 856.23750c 856.76250c 857.23750c
            """;
        var service = new SetupTrConfigBuilderService(new HttpClient(), new EngineConfig());
        var airspy = new SetupSdrDeviceDto(
            0,
            "Airspy",
            "26A464DC28793293",
            "Airspy Mini",
            "osmosdr",
            "airspy=26A464DC28793293",
            "",
            [3_000_000, 6_000_000],
            3_000_000,
            "airspy-linearity",
            "15",
            "");

        var draft = await service.DraftAsync(new SetupTrConfigDraftRequest(
            RadioReferenceSid: "6355",
            HtmlText: html,
            SiteNameList: ["Chattanooga Simulcast Hamilton, TN"],
            SdrDevices: [airspy]), CancellationToken.None);

        var source = Assert.Single(draft.Sources);
        Assert.Equal("Airspy", source.Type);
        Assert.Equal("osmosdr", source.Driver);
        Assert.Equal("airspy=26A464DC28793293", source.DeviceArgs);
        Assert.Equal(3_000_000, source.SampleRate);
        Assert.Equal("15", source.Gain);
        Assert.Contains(draft.Warnings, warning => warning.Contains("Verify this with the connected Airspy Mini", StringComparison.OrdinalIgnoreCase));

        using var doc = JsonDocument.Parse(draft.ConfigJson);
        var configSource = doc.RootElement.GetProperty("sources")[0];
        Assert.Equal("osmosdr", configSource.GetProperty("driver").GetString());
        Assert.Equal("airspy=26A464DC28793293", configSource.GetProperty("device").GetString());
        Assert.Equal(3_000_000, configSource.GetProperty("rate").GetInt32());
        Assert.Equal(15, configSource.GetProperty("gain").GetInt32());
    }

    [Fact]
    public async Task SourcePlanAsync_UsesPerDeviceSampleRateWindows()
    {
        var html = """
            Sites and Frequencies
            2 (2) 001 (1) Site One County 770.08125 770.33125c 770.58125c
            2 (2) 002 (2) Site Two County 856.11250 856.36250c 856.61250c
            """;
        var service = new SetupTrConfigBuilderService(new HttpClient(), new EngineConfig());
        var airspy = new SetupSdrDeviceDto(0, "Airspy", "26A464DC28793293", "Airspy Mini", "osmosdr", "airspy=26A464DC28793293", "", [3_000_000, 6_000_000], 3_000_000, "airspy-linearity", "15", "");
        var rtl = new SetupSdrDeviceDto(1, "RTL-SDR", "00000002", "RTL-SDR Blog V4", "osmosdr", "rtl=00000002,buflen=65536", "", [2_400_000], 2_400_000, "rtl-tuner-gain", "32", "");

        var plan = await service.SourcePlanAsync(new SetupTrConfigSourcePlanRequest(
            RadioReferenceSid: "6355",
            HtmlText: html,
            SiteNameList: ["Site One County", "Site Two County"],
            SdrDevices: [airspy, rtl]), CancellationToken.None);

        Assert.Equal(2, plan.AvailableSourceCount);
        Assert.Equal(2, plan.Sources.Count);
        Assert.Contains(plan.Sources, source => source.Type == "Airspy" && source.SampleRate == 3_000_000);
        Assert.Contains(plan.Sources, source => source.Type == "RTL-SDR" && source.SampleRate == 2_400_000);
    }

    [Fact]
    public async Task SourcePlanAsync_AirspyCanCoverMultipleSitesByControlChannels()
    {
        var html = """
            Sites and Frequencies
            2 (2) 011 (B) Site One County 851.01250 855.21250c
            2 (2) 012 (C) Site Two County 859.98750 856.23750c
            """;
        var service = new SetupTrConfigBuilderService(new HttpClient(), new EngineConfig());
        var airspy = new SetupSdrDeviceDto(0, "Airspy", "26A464DC28793293", "Airspy Mini", "osmosdr", "airspy=26A464DC28793293", "", [3_000_000, 6_000_000], 3_000_000, "airspy-linearity", "15", "");

        var plan = await service.SourcePlanAsync(new SetupTrConfigSourcePlanRequest(
            RadioReferenceSid: "6355",
            HtmlText: html,
            SiteNameList: ["Site One County", "Site Two County"],
            SdrDevices: [airspy]), CancellationToken.None);

        Assert.Equal(1, plan.RequiredSourceCount);
        Assert.Equal(1, plan.AvailableSourceCount);
        var source = Assert.Single(plan.Sources);
        Assert.Equal("Airspy", source.Type);
        Assert.All(plan.Systems.SelectMany(system => system.ControlChannelsMhz), control => Assert.Contains(control, source.CoveredFrequenciesMhz));
        Assert.DoesNotContain(plan.Warnings, warning => warning.Contains("need 2 SDR source window", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.Warnings, warning => warning.Contains("uncovered control channels", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveAsync_RejectsUncoveredControlChannels()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-tr-save-coverage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var trConfigPath = Path.Combine(root, "config.json");
        var config = new EngineConfig { ConfigPath = Path.Combine(root, "appsettings.json"), TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath } };
        var service = new SetupTrConfigBuilderService(new HttpClient(), config);

        var result = await service.SaveAsync(new SetupTrConfigSaveRequest("""
            {
              "sources": [
                { "center": 856225000, "rate": 2048000, "driver": "osmosdr", "device": "rtl=00000002" }
              ],
              "systems": [
                { "shortName": "chattanooga-simulcast-hamilton-t", "control_channels": [855212500, 856237500, 856762500, 857237500] },
                { "shortName": "north-bradley-bradley-tn", "control_channels": [769606250, 770531250] }
              ]
            }
            """), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("cannot start", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("855.212500", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("769.606250", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(trConfigPath));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task SaveAsync_AcceptsCoveredControlChannels()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-tr-save-coverage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var trConfigPath = Path.Combine(root, "config.json");
        var config = new EngineConfig { ConfigPath = Path.Combine(root, "appsettings.json"), TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath } };
        var service = new SetupTrConfigBuilderService(new HttpClient(), config);

        var result = await service.SaveAsync(new SetupTrConfigSaveRequest("""
            {
              "sources": [
                { "center": 856225000, "rate": 2400000, "driver": "osmosdr", "device": "rtl=00000002" }
              ],
              "systems": [
                { "shortName": "chattanooga-simulcast-hamilton-t", "control_channels": [855212500, 856237500, 856762500, 857237500] }
              ]
            }
            """), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(File.Exists(trConfigPath));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task DraftAsync_UsesExistingTrConfigAsMigrationTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-tr-template-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var trConfigPath = Path.Combine(root, "config.json");
        await File.WriteAllTextAsync(trConfigPath, """
            {
              "ver": 2,
              "defaultMode": "digital",
              "frequencyFormat": "mhz",
              "systems": [
                {
                  "shortName": "old-site",
                  "type": "p25",
                  "talkgroupsFile": "/etc/trunk-recorder/talkgroups.csv",
                  "control_channels": [855212500],
                  "maxDev": 4000,
                  "recordUnknown": false
                }
              ],
              "sources": [
                {
                  "center": 855312500,
                  "rate": 2048000,
                  "error": -1600,
                  "gain": 32,
                  "digitalRecorders": 5,
                  "analogRecorders": 0,
                  "driver": "osmosdr",
                  "device": "rtl=00000001,buflen=65536",
                  "agc": false
                }
              ],
              "plugins": [
                {
                  "name": "callstream",
                  "library": "libcallstream.so",
                  "clients": [{ "address": "10.0.0.10", "port": 9123 }],
                  "streams": [{ "TGID": 0, "shortName": "old-site" }],
                  "sftpHost": "example.invalid"
                }
              ],
              "controlRetuneLimit": 0,
              "controlWarnRate": 10
            }
            """);
        var html = """
            Sites and Frequencies
            2 (2) 008 (8) ETV Raymond Hinds 770.08125 771.98125 773.03125c 773.28125c
            """;
        var config = new EngineConfig { TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath } };
        var service = new SetupTrConfigBuilderService(new HttpClient(), config);

        var draft = await service.DraftAsync(new SetupTrConfigDraftRequest(
            RadioReferenceSid: "4879",
            HtmlText: html,
            SiteNames: "etv raymond",
            SdrSerials: "00000001",
            SampleRate: 2_400_000), CancellationToken.None);

        using var doc = JsonDocument.Parse(draft.ConfigJson);
        var rootElement = doc.RootElement;
        Assert.Equal("digital", rootElement.GetProperty("defaultMode").GetString());
        Assert.Equal("mhz", rootElement.GetProperty("frequencyFormat").GetString());
        Assert.Equal(0, rootElement.GetProperty("controlRetuneLimit").GetInt32());
        Assert.Equal(-1, rootElement.GetProperty("controlWarnRate").GetInt32());
        Assert.True(rootElement.GetProperty("audioStreaming").GetBoolean());
        var source = rootElement.GetProperty("sources")[0];
        Assert.Equal(2400000, source.GetProperty("rate").GetInt32());
        Assert.Equal(-1600, source.GetProperty("error").GetInt32());
        Assert.Equal(32, source.GetProperty("gain").GetInt32());
        Assert.Equal("rtl=00000001,buflen=65536", source.GetProperty("device").GetString());
        Assert.False(source.GetProperty("agc").GetBoolean());
        var system = rootElement.GetProperty("systems")[0];
        Assert.Equal("etv-raymond", system.GetProperty("shortName").GetString());
        Assert.Equal(4000, system.GetProperty("maxDev").GetInt32());
        Assert.False(system.GetProperty("recordUnknown").GetBoolean());
        Assert.Equal(2, system.GetProperty("control_channels").GetArrayLength());
        Assert.False(system.TryGetProperty("channels", out _));
        var plugin = rootElement.GetProperty("plugins")[0];
        var client = plugin.GetProperty("clients")[0];
        Assert.Equal("127.0.0.1", client.GetProperty("address").GetString());
        Assert.Equal(9123, client.GetProperty("port").GetInt32());
        Assert.False(plugin.TryGetProperty("sftpHost", out _));
        Assert.Equal("etv-raymond", plugin.GetProperty("streams")[0].GetProperty("shortName").GetString());

        Directory.Delete(root, recursive: true);
    }
}
