using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class RfSurveyMultiSystemTests
{
    [Fact]
    public void BuildProfile_AggregatesSelectedSystems()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 772000000, "rate": 3000000, "error": 0, "gain": 15, "device": "airspy=26A464DC28793293" }
                  ],
                  "systems": [
                    { "shortName": "raymond", "modulation": "qpsk", "control_channels": [773031250], "channels": [773531250] },
                    { "shortName": "utica", "modulation": "qpsk", "control_channels": [774281250], "channels": [774531250] }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);

            var profile = service.BuildProfile(new RfSurveyCreateRequest(
                SystemShortName: "raymond",
                SystemShortNames: ["raymond", "utica"],
                RadioReferenceSid: "12345"));

            Assert.Equal("12345", profile.RadioReferenceSid);
            Assert.Equal(["raymond", "utica"], profile.SystemShortNames);
            Assert.Equal("raymond", profile.SystemShortName);
            Assert.Equal([773031250, 774281250], profile.ControlChannelsHz);
            Assert.Equal([773531250, 774531250], profile.VoiceFrequenciesHz);
            Assert.Equal([0], profile.SelectedSourceIndexes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProfile_EmptyRequestDoesNotAdoptLiveSystemOrSources()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 856225000, "rate": 2400000, "error": 0, "gain": 28, "device": "rtl=00000002" }
                  ],
                  "systems": [
                    { "shortName": "chattanooga", "modulation": "qpsk", "control_channels": [856237500] }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);

            var profile = service.BuildProfile(new RfSurveyCreateRequest(
                SiteLabel: "Radio Setup",
                SelectedSourceIndexes: []));

            Assert.Empty(profile.SystemShortNames);
            Assert.Empty(profile.Systems);
            Assert.Empty(profile.ControlChannelsHz);
            Assert.Empty(profile.Sources);
            Assert.Empty(profile.SelectedSourceIndexes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateDraft_AppliedPlanIgnoresEmptySelectedSourceAutosave()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-applied-autosave-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var artifactPath = Path.Combine(root, "rf-test");
            Directory.CreateDirectory(artifactPath);
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 813421875, "rate": 6000000, "error": 0, "gain": 15, "device": "airspy=637862DC2E3A19D7" }
                  ],
                  "systems": [
                    { "shortName": "chattanooga", "control_channels": [855212500] },
                    { "shortName": "cleveland", "control_channels": [851050000] }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);
            var profile = new RfSurveyProfileDto
            {
                SiteLabel = "airspy-test-1",
                SystemShortName = "chattanooga",
                SystemShortNames = ["chattanooga", "cleveland"],
                SourcePlanSystemShortNames = ["chattanooga", "cleveland"],
                SourcePlanMode = "control",
                Systems =
                [
                    new("chattanooga", "Chattanooga", [855212500], []),
                    new("cleveland", "Cleveland", [851050000], [])
                ],
                ControlChannelsHz = [851050000, 855212500],
                Sources =
                [
                    new(0, "airspy=637862DC2E3A19D7", "637862DC2E3A19D7", "Airspy", 813421875, 6000000, 0, "15")
                ],
                SourceOverride = true,
                SelectedSourceIndexes = [0],
                CurrentStep = 4
            };
            var summary = "Applied 1 SDR source window for 2 systems: chattanooga, cleveland. Updated source index(es): 0.";
            var session = new RfSurveySessionDto
            {
                Id = "rf-test",
                Status = "source_plan_applied",
                SiteLabel = profile.SiteLabel,
                SystemShortName = profile.SystemShortName,
                SourcePlanSummary = summary,
                ArtifactPath = artifactPath
            };
            await database.AddRfSurveySessionAsync(
                session,
                JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()),
                JsonSerializer.Serialize(new RfSurveyToolPrepDto(DateTime.UtcNow, true, true, true, true, [], []), EngineConfig.JsonOptions()),
                CancellationToken.None);

            var updated = await service.UpdateDraftAsync("rf-test", new RfSurveyDraftUpdateRequest(
                SystemShortName: profile.SystemShortName,
                SiteLabel: profile.SiteLabel,
                SelectedSourceIndexes: [],
                CurrentStep: 3,
                SystemShortNames: profile.SystemShortNames,
                SourcePlanSystemShortNames: profile.SourcePlanSystemShortNames,
                SourcePlanMode: profile.SourcePlanMode,
                SystemDefinitions: profile.Systems,
                SdrSources: profile.Sources), CancellationToken.None);

            Assert.Equal("source_plan_applied", updated.Session.Status);
            Assert.Equal(summary, updated.Session.SourcePlanSummary);
            Assert.Equal([0], updated.Profile.SelectedSourceIndexes);
            Assert.Equal(3, updated.Profile.CurrentStep);

            updated = await service.UpdateDraftAsync("rf-test", new RfSurveyDraftUpdateRequest(
                SystemShortName: profile.SystemShortName,
                SiteLabel: profile.SiteLabel,
                SelectedSourceIndexes: [0],
                CurrentStep: 4,
                SystemShortNames: profile.SystemShortNames,
                SourcePlanSystemShortNames: profile.SourcePlanSystemShortNames,
                SourcePlanMode: profile.SourcePlanMode,
                SystemDefinitions: profile.Systems,
                SdrSources: profile.Sources), CancellationToken.None);

            Assert.Equal("source_plan_applied", updated.Session.Status);
            Assert.Equal(summary, updated.Session.SourcePlanSummary);
            Assert.Equal([0], updated.Profile.SelectedSourceIndexes);
            Assert.Equal(4, updated.Profile.CurrentStep);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_ShowsReusablePrerequisiteCheckWithoutMutatingWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-toolprep-reuse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 772000000, "rate": 3000000, "error": 0, "gain": 15, "device": "airspy=26A464DC28793293" }
                  ],
                  "systems": [
                    { "shortName": "raymond", "control_channels": [773031250] }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);
            var profile = new RfSurveyProfileDto
            {
                SiteLabel = "raymond",
                SystemShortName = "raymond",
                SystemShortNames = ["raymond"],
                Systems = [new("raymond", "Raymond", [773031250], [])],
                ControlChannelsHz = [773031250]
            };
            var realPrep = new RfSurveyToolPrepDto(
                DateTime.UtcNow.AddMinutes(-5),
                true,
                true,
                true,
                true,
                [new("p25", "Configured P25 probe command", "p25", true, true, "configured template", "rx.py", "P25 probe", "Installed.")],
                []);
            var olderPrep = realPrep with
            {
                GeneratedAtUtc = DateTime.UtcNow.AddMinutes(-30),
                Tools = [new("older", "Older check", "p25", true, true, "older", "older", "Older prerequisite check", "Installed.")]
            };
            var staleEditedSession = new RfSurveySessionDto
            {
                Id = "rf-stale-edited",
                Status = "draft",
                SiteLabel = "stale edited",
                SystemShortName = "raymond",
                ArtifactPath = Path.Combine(root, "rf-stale-edited"),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
            };
            var reusableSession = new RfSurveySessionDto
            {
                Id = "rf-reusable",
                Status = "tool_prep",
                SiteLabel = "reusable",
                SystemShortName = "raymond",
                ArtifactPath = Path.Combine(root, "rf-reusable"),
                UpdatedAtUtc = DateTime.UtcNow
            };
            var emptySession = new RfSurveySessionDto
            {
                Id = "rf-empty",
                Status = "draft",
                SiteLabel = "empty",
                SystemShortName = "raymond",
                ArtifactPath = Path.Combine(root, "rf-empty"),
                CreatedAtUtc = DateTime.UtcNow.AddHours(-2),
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            };
            Directory.CreateDirectory(staleEditedSession.ArtifactPath);
            Directory.CreateDirectory(reusableSession.ArtifactPath);
            Directory.CreateDirectory(emptySession.ArtifactPath);
            await database.AddRfSurveySessionAsync(
                staleEditedSession,
                JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()),
                JsonSerializer.Serialize(olderPrep, EngineConfig.JsonOptions()),
                CancellationToken.None);
            await database.AddRfSurveySessionAsync(
                reusableSession,
                JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()),
                JsonSerializer.Serialize(realPrep, EngineConfig.JsonOptions()),
                CancellationToken.None);
            await database.AddRfSurveySessionAsync(
                emptySession,
                JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()),
                JsonSerializer.Serialize(new RfSurveyToolPrepDto(DateTime.UtcNow, false, false, false, false, [], ["Tool prep has not run yet."]), EngineConfig.JsonOptions()),
                CancellationToken.None);

            var detail = await service.GetAsync("rf-empty", CancellationToken.None);
            var stored = await database.GetRfSurveySessionAsync("rf-empty", CancellationToken.None);
            var storedPrep = JsonSerializer.Deserialize<RfSurveyToolPrepDto>(stored!.Value.ToolPrepJson, EngineConfig.JsonOptions());

            Assert.NotNull(detail);
            Assert.True(detail!.ToolPrep?.ReadyForControlChannelTests);
            Assert.Single(detail.ToolPrep!.Tools);
            Assert.Equal("p25", detail.ToolPrep.Tools[0].Id);
            Assert.Equal(emptySession.UpdatedAtUtc.ToUniversalTime(), stored.Value.Session.UpdatedAtUtc.ToUniversalTime());
            Assert.False(storedPrep?.ReadyForControlChannelTests);
            Assert.Empty(storedPrep!.Tools);
            Assert.False(File.Exists(Path.Combine(emptySession.ArtifactPath, "tool-prep.json")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateDraft_DoesNotRefreshSavedWorkspaceFromLiveTr()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-draft-preserve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var artifactPath = Path.Combine(root, "rf-test");
            Directory.CreateDirectory(artifactPath);
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 856412500, "rate": 2400000, "error": 0, "gain": 28, "device": "rtl=active-tr" }
                  ],
                  "systems": [
                    { "shortName": "hinds-county-simulcast-2-hinds", "control_channels": [856237500] }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);
            var profile = new RfSurveyProfileDto
            {
                SiteLabel = "airspyMini-Hinds-Simulcast2",
                RadioReferenceSid = "12345",
                SystemShortName = "hinds-county-simulcast-2-hinds",
                SystemShortNames = ["hinds-county-simulcast-2-hinds"],
                SourcePlanSystemShortNames = ["hinds-county-simulcast-2-hinds"],
                Systems =
                [
                    new("hinds-county-simulcast-2-hinds", "Hinds County Simulcast 2 Hinds", [851775000, 852212500], [])
                ],
                ControlChannelsHz = [851775000, 852212500],
                Sources =
                [
                    new(0, "airspy=637862DC2E457DD7", "637862DC2E457DD7", "Airspy", 852506250, 6000000, 0, "15")
                ],
                SourceOverride = false,
                SelectedSourceIndexes = [0],
                CurrentStep = 1
            };
            var session = new RfSurveySessionDto
            {
                Id = "rf-test",
                Status = "draft",
                SiteLabel = profile.SiteLabel,
                SystemShortName = profile.SystemShortName,
                ArtifactPath = artifactPath
            };
            await database.AddRfSurveySessionAsync(
                session,
                JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()),
                JsonSerializer.Serialize(new RfSurveyToolPrepDto(DateTime.UtcNow, true, true, true, true, [], []), EngineConfig.JsonOptions()),
                CancellationToken.None);

            var updated = await service.UpdateDraftAsync("rf-test", new RfSurveyDraftUpdateRequest(
                SystemShortName: profile.SystemShortName,
                SiteLabel: profile.SiteLabel,
                SystemShortNames: profile.SystemShortNames,
                SourcePlanSystemShortNames: profile.SourcePlanSystemShortNames,
                CurrentStep: 2), CancellationToken.None);

            Assert.Equal("12345", updated.Profile.RadioReferenceSid);
            Assert.Equal([851775000, 852212500], updated.Profile.ControlChannelsHz);
            Assert.Equal("637862DC2E457DD7", updated.Profile.Sources.Single().Serial);
            Assert.Equal(852506250, updated.Profile.Sources.Single().CenterHz);
            Assert.DoesNotContain(updated.Profile.Warnings, warning => warning.Contains("No TR system was available", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildProfile_SavedDefinitionsAndSourcesOverrideLiveTrFacts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-saved-facts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 856412500, "rate": 2400000, "error": 0, "gain": 28, "device": "rtl=stale-live" }
                  ],
                  "systems": [
                    { "shortName": "hinds-county-simulcast-2-hinds", "control_channels": [856237500] }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);

            var profile = service.BuildProfile(new RfSurveyCreateRequest(
                SystemShortName: "hinds-county-simulcast-2-hinds",
                SiteLabel: "airspyMini-Hinds-Simulcast2",
                SystemShortNames: ["hinds-county-simulcast-2-hinds"],
                SystemDefinitions:
                [
                    new("hinds-county-simulcast-2-hinds", "Hinds County Simulcast 2 Hinds", [851775000, 852212500], [])
                ],
                SdrSources:
                [
                    new(0, "airspy=637862DC2E457DD7", "637862DC2E457DD7", "Airspy", 852506250, 6000000, 0, "15")
                ],
                RadioReferenceSid: "12345"));

            Assert.Equal("12345", profile.RadioReferenceSid);
            Assert.Equal([851775000, 852212500], profile.ControlChannelsHz);
            Assert.Equal("637862DC2E457DD7", profile.Sources.Single().Serial);
            Assert.DoesNotContain(profile.Warnings, warning => warning.Contains("No TR system was available", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ApplyP25DemodOverride_ReplacesConfiguredDemod()
    {
        var method = typeof(RfSurveyService).GetMethod("ApplyP25DemodOverride", BindingFlags.NonPublic | BindingFlags.Static);

        var command = (string)method!.Invoke(null, ["rx.py --args 'airspy=abc' -D cqpsk -f 851775000", "fsk4"])!;

        Assert.Contains("-D fsk4", command);
        Assert.DoesNotContain("-D cqpsk", command);
    }

    [Fact]
    public void ReadP25DemodSequence_DefaultsToFsk4ThenCqpsk()
    {
        var method = typeof(RfSurveyService).GetMethod("ReadP25DemodSequence", BindingFlags.NonPublic | BindingFlags.Static);
        var parameters = JsonSerializer.SerializeToElement(new { });

        var demods = (IReadOnlyList<string>)method!.Invoke(null, [parameters])!;

        Assert.Equal(["fsk4", "cqpsk"], demods);
    }

    [Fact]
    public async Task GetAsync_DoesNotRefreshSavedProfileFromLiveTrConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-readonly-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 856225000, "rate": 2400000, "error": 0, "gain": 28, "device": "rtl=00000002" }
                  ],
                  "systems": [
                    { "shortName": "tacn-chattanooga", "control_channels": [856237500] }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);
            var artifactPath = Path.Combine(root, "rf-raymond");
            Directory.CreateDirectory(artifactPath);
            var savedUpdatedAt = DateTime.UtcNow.AddHours(-1);
            var profile = new RfSurveyProfileDto
            {
                SiteLabel = "airspyMini-Hinds-Simulcast2",
                RadioReferenceSid = "4879",
                SystemShortName = "mswin-etv-raymond",
                SystemShortNames = ["mswin-etv-raymond", "mswin-utica"],
                SourcePlanSystemShortNames = ["mswin-etv-raymond", "mswin-utica"],
                SourcePlanMode = "full",
                Systems =
                [
                    new("mswin-etv-raymond", "ETV Raymond Hinds", [773031250, 773281250], [770081250]),
                    new("mswin-utica", "Utica Hinds", [774281250, 774531250], [769581250])
                ],
                ControlChannelsHz = [773031250, 773281250, 774281250, 774531250],
                VoiceFrequenciesHz = [769581250, 770081250],
                Sources =
                [
                    new(0, "airspy=26A464DC28793293", "26A464DC28793293", "Airspy", 772000000, 3000000, 0, "15")
                ],
                SourceOverride = true,
                SelectedSourceIndexes = [0],
                Warnings = ["No TR system was available. Complete setup/TR config before running Radio Setup."]
            };
            var session = new RfSurveySessionDto
            {
                Id = "rf-raymond",
                Status = "draft",
                SiteLabel = profile.SiteLabel,
                SystemShortName = profile.SystemShortName,
                ArtifactPath = artifactPath,
                CreatedAtUtc = savedUpdatedAt.AddMinutes(-10),
                UpdatedAtUtc = savedUpdatedAt
            };
            var profileJson = JsonSerializer.Serialize(profile, EngineConfig.JsonOptions());
            await database.AddRfSurveySessionAsync(
                session,
                profileJson,
                JsonSerializer.Serialize(new RfSurveyToolPrepDto(DateTime.UtcNow.AddDays(-1), false, false, false, false, [], []), EngineConfig.JsonOptions()),
                CancellationToken.None);

            var detail = await service.GetAsync("rf-raymond", CancellationToken.None);
            var stored = await database.GetRfSurveySessionAsync("rf-raymond", CancellationToken.None);
            var storedProfile = JsonSerializer.Deserialize<RfSurveyProfileDto>(stored!.Value.ProfileJson, EngineConfig.JsonOptions());

            Assert.NotNull(detail);
            Assert.Equal("mswin-etv-raymond", detail!.Profile.SystemShortName);
            Assert.Equal(["mswin-etv-raymond", "mswin-utica"], detail.Profile.SystemShortNames);
            Assert.Contains(detail.Profile.Systems, system => system.SiteLabel == "ETV Raymond Hinds");
            Assert.DoesNotContain(detail.Profile.Systems, system => system.ShortName == "tacn-chattanooga");
            Assert.Equal("4879", detail.Profile.RadioReferenceSid);
            Assert.DoesNotContain(detail.Profile.Warnings, warning => warning.Contains("No TR system was available", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(savedUpdatedAt.ToUniversalTime(), stored.Value.Session.UpdatedAtUtc.ToUniversalTime());
            Assert.Equal("mswin-etv-raymond", storedProfile?.SystemShortName);
            Assert.Contains(storedProfile!.Warnings, warning => warning.Contains("No TR system was available", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(profileJson, stored.Value.ProfileJson);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrimDraftToWorkspaceSystems_KeepsSelectedSystemList()
    {
        var root = JsonNode.Parse("""
            {
              "systems": [
                { "shortName": "raymond" },
                { "shortName": "utica" },
                { "shortName": "jackson" }
              ]
            }
            """)!.AsObject();
        var method = typeof(RfSurveyService).GetMethod("TrimDraftToWorkspaceSystems", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "TrimDraftToWorkspaceSystems");
        var changes = new List<string>();
        var warnings = new List<string>();

        var kept = (IReadOnlyList<string>)method.Invoke(null, [root, new[] { "raymond", "utica" }, changes, warnings])!;

        Assert.Equal(["raymond", "utica"], kept);
        Assert.Equal(2, root["systems"]!.AsArray().Count);
        Assert.Contains(changes, change => change.Contains("kept raymond, utica", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildProfile_UsesRadioReferenceDefinitionNotInLiveTrConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-rr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 772000000, "rate": 3000000, "error": 0, "gain": 15, "device": "airspy=26A464DC28793293" }
                  ],
                  "systems": [
                    { "shortName": "raymond", "modulation": "qpsk", "control_channels": [773031250] }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);

            var profile = service.BuildProfile(new RfSurveyCreateRequest(
                SystemShortName: "utica",
                SystemShortNames: ["raymond", "utica"],
                SystemDefinitions:
                [
                    new("utica", "Utica", [774281250], [])
                ]));

            Assert.Equal(["raymond", "utica"], profile.SystemShortNames);
            Assert.Equal([773031250, 774281250], profile.ControlChannelsHz);
            Assert.Contains(profile.Systems, system => system.ShortName == "utica" && system.ControlChannelsHz.SequenceEqual([774281250]));
            Assert.DoesNotContain(profile.Warnings, warning => warning.Contains("utica", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void VoiceCandidateConfig_EnablesStreamingAndAllowsShortValidationCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rfsurvey-voice-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            var originalJson = """
                {
                  "audioStreaming": false,
                  "sources": [
                    { "center": 856225000, "rate": 2400000, "error": 0, "gain": 28, "device": "rtl=00000002" }
                  ],
                  "systems": [
                    { "shortName": "chattanooga", "modulation": "qpsk", "control_channels": [856237500], "minDuration": 5 }
                  ],
                  "plugins": [
                    { "name": "callstream", "library": "libcallstream.so", "streams": [ { "TGID": 0, "shortName": "chattanooga" } ] }
                  ]
                }
                """;
            File.WriteAllText(trConfigPath, originalJson);
            var config = new EngineConfig
            {
                Ingest = new IngestConfig { CallstreamBind = "127.0.0.1", CallstreamPort = 9123 },
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath },
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root, AppDataRoot = root }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            var calibration = new SetupCalibrationService(config, database, NullLogger<SetupCalibrationService>.Instance);
            var service = new RfSurveyService(config, database, calibration, null!, NullLogger<RfSurveyService>.Instance);
            var profile = new RfSurveyProfileDto
            {
                SystemShortName = "chattanooga",
                Systems =
                [
                    new("north-bradley", "North Bradley", [769606250, 770531250], [])
                ],
                ControlChannelsHz = [769606250, 770531250],
                Sources =
                [
                    new(0, "airspy=26A464DC28793293", "26A464DC28793293", "Airspy", 772000000, 3000000, 0, "15"),
                    new(1, "airspy=637862DC2F5C0CD7", "637862DC2F5C0CD7", "Airspy", 772000000, 3000000, 0, "15")
                ]
            };
            var candidateType = typeof(RfSurveyService).GetNestedType("RfValidationCandidate", BindingFlags.NonPublic)
                ?? throw new MissingMemberException(typeof(RfSurveyService).FullName, "RfValidationCandidate");
            var candidate = Activator.CreateInstance(candidateType, nonPublic: true)!;
            candidateType.GetProperty("Id")!.SetValue(candidate, "s0-cc769606250-g20-e0");
            candidateType.GetProperty("SourceIndex")!.SetValue(candidate, 0);
            candidateType.GetProperty("ControlChannelHz")!.SetValue(candidate, 769606250L);
            candidateType.GetProperty("Gain")!.SetValue(candidate, "20");
            candidateType.GetProperty("ErrorHz")!.SetValue(candidate, 0);
            var method = typeof(RfSurveyService).GetMethod("BuildVoiceCandidateTrConfigJson", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildVoiceCandidateTrConfigJson");

            var json = (string)method.Invoke(service, [originalJson, profile, candidate])!;
            using var document = JsonDocument.Parse(json);
            var rootElement = document.RootElement;
            var system = rootElement.GetProperty("systems")[0];
            var stream = rootElement.GetProperty("plugins")[0].GetProperty("streams")[0];
            var source = rootElement.GetProperty("sources")[0];

            Assert.True(rootElement.GetProperty("audioStreaming").GetBoolean());
            Assert.Equal("north-bradley", system.GetProperty("shortName").GetString());
            Assert.Equal(0, system.GetProperty("minDuration").GetInt32());
            Assert.Equal(0, system.GetProperty("minTransmissionDuration").GetInt32());
            Assert.True(system.GetProperty("callLog").GetBoolean());
            Assert.Equal("north-bradley", stream.GetProperty("shortName").GetString());
            Assert.Equal("airspy=26A464DC28793293,sensitivity=0,linearity=0,bias=0", source.GetProperty("device").GetString());
            Assert.False(source.TryGetProperty("gain", out _));
            Assert.Equal(15, source.GetProperty("lnaGain").GetInt32());
            Assert.Equal(12, source.GetProperty("mixGain").GetInt32());
            Assert.Equal(8, source.GetProperty("ifGain").GetInt32());

            var secondSourceCandidate = Activator.CreateInstance(candidateType, nonPublic: true)!;
            candidateType.GetProperty("Id")!.SetValue(secondSourceCandidate, "s1-cc769606250-g18-e0");
            candidateType.GetProperty("SourceIndex")!.SetValue(secondSourceCandidate, 1);
            candidateType.GetProperty("ControlChannelHz")!.SetValue(secondSourceCandidate, 769606250L);
            candidateType.GetProperty("Gain")!.SetValue(secondSourceCandidate, "18");
            candidateType.GetProperty("ErrorHz")!.SetValue(secondSourceCandidate, 0);

            json = (string)method.Invoke(service, [originalJson, profile, secondSourceCandidate])!;
            using var secondDocument = JsonDocument.Parse(json);
            var secondSources = secondDocument.RootElement.GetProperty("sources");
            var secondSource = secondSources[0];

            Assert.Equal(1, secondSources.GetArrayLength());
            Assert.Equal("airspy=637862DC2F5C0CD7,sensitivity=0,linearity=0,bias=0", secondSource.GetProperty("device").GetString());
            Assert.Equal(3000000, secondSource.GetProperty("rate").GetInt32());
            Assert.Equal(12, secondSource.GetProperty("lnaGain").GetInt32());
            Assert.Equal(10, secondSource.GetProperty("mixGain").GetInt32());
            Assert.Equal(6, secondSource.GetProperty("ifGain").GetInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidationSweep_SelectsPowerSeedsForEverySelectedControlChannel()
    {
        var profile = new RfSurveyProfileDto
        {
            Systems =
            [
                new("alpha", "Alpha", [100, 200], []),
                new("beta", "Beta", [300], [])
            ],
            ControlChannelsHz = [100, 200, 300]
        };
        var candidates = NewValidationCandidateArray(
            NewValidationCandidate(100, 7),
            NewValidationCandidate(100, 6.8, gain: "18"),
            NewValidationCandidate(200, 6),
            NewValidationCandidate(300, 20),
            NewValidationCandidate(300, 18, gain: "18"),
            NewValidationCandidate(400, 19));
        var method = typeof(RfSurveyService).GetMethod("SelectRfValidationPowerCandidates", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "SelectRfValidationPowerCandidates");

        var selected = ((System.Collections.IEnumerable)method.Invoke(null, [profile, candidates, 2])!).Cast<object>().ToList();
        var channels = selected.Select(candidate => (long)candidate.GetType().GetProperty("ControlChannelHz")!.GetValue(candidate)!).ToList();

        Assert.Contains(100, channels);
        Assert.Contains(200, channels);
        Assert.Contains(300, channels);
        Assert.Contains(selected, candidate => (long)candidate.GetType().GetProperty("ControlChannelHz")!.GetValue(candidate)! == 100 &&
                                               (string)candidate.GetType().GetProperty("Gain")!.GetValue(candidate)! == "18");
        Assert.Contains(selected, candidate => (long)candidate.GetType().GetProperty("ControlChannelHz")!.GetValue(candidate)! == 300 &&
                                               (string)candidate.GetType().GetProperty("Gain")!.GetValue(candidate)! == "18");
    }

    [Fact]
    public void ValidationSweep_SelectsP25SeedsForEverySelectedControlChannel()
    {
        var profile = new RfSurveyProfileDto
        {
            Systems =
            [
                new("alpha", "Alpha", [100, 200], []),
                new("beta", "Beta", [300], [])
            ],
            ControlChannelsHz = [100, 200, 300]
        };
        var candidates = NewValidationCandidateArray(
            NewValidationCandidate(100, 7, errorOffsetHz: 0),
            NewValidationCandidate(100, 6.8, gain: "18", errorOffsetHz: 0),
            NewValidationCandidate(100, 7, errorOffsetHz: 300),
            NewValidationCandidate(200, 6, errorOffsetHz: 0),
            NewValidationCandidate(300, 20, errorOffsetHz: 0),
            NewValidationCandidate(300, 18, gain: "18", errorOffsetHz: 0),
            NewValidationCandidate(400, 19, errorOffsetHz: 0));
        var method = typeof(RfSurveyService).GetMethod("SelectRfValidationP25Seeds", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "SelectRfValidationP25Seeds");

        var selected = ((System.Collections.IEnumerable)method.Invoke(null, [profile, candidates, 1])!).Cast<object>().ToList();
        var channels = selected.Select(seed => (long)seed.GetType().GetProperty("ControlChannelHz")!.GetValue(seed)!).ToList();

        Assert.Contains(100, channels);
        Assert.Contains(200, channels);
        Assert.Contains(300, channels);
        Assert.Contains(selected, seed => (long)seed.GetType().GetProperty("ControlChannelHz")!.GetValue(seed)! == 100 &&
                                          (string)seed.GetType().GetProperty("Gain")!.GetValue(seed)! == "18");
        Assert.Contains(selected, seed => (long)seed.GetType().GetProperty("ControlChannelHz")!.GetValue(seed)! == 300 &&
                                          (string)seed.GetType().GetProperty("Gain")!.GetValue(seed)! == "18");
    }

    [Fact]
    public void ValidationSweep_IncludesAirspyDecodeGainForPrimaryControlChannel()
    {
        var profile = new RfSurveyProfileDto
        {
            Systems = [new("hinds", "Hinds", [851775000, 852212500], [])],
            ControlChannelsHz = [851775000, 852212500],
            Sources = [new(0, "airspy=637862DC2E3A19D7", "637862DC2E3A19D7", "Airspy", 852506250, 6000000, 0, "15")],
            SelectedSourceIndexes = [0]
        };
        var candidates = NewValidationCandidateArray(
            NewValidationCandidate(851775000, 8.0, gain: "8"),
            NewValidationCandidate(851775000, 4.5, gain: "20"),
            NewValidationCandidate(852212500, 7.0, gain: "8"));
        var powerMethod = typeof(RfSurveyService).GetMethod("SelectRfValidationPowerCandidates", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "SelectRfValidationPowerCandidates");
        var p25Method = typeof(RfSurveyService).GetMethod("SelectRfValidationP25Seeds", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "SelectRfValidationP25Seeds");

        var powerSelected = ((System.Collections.IEnumerable)powerMethod.Invoke(null, [profile, candidates, 1])!).Cast<object>().ToList();
        var p25Selected = ((System.Collections.IEnumerable)p25Method.Invoke(null, [profile, candidates, 1])!).Cast<object>().ToList();

        Assert.Contains(powerSelected, candidate => (long)candidate.GetType().GetProperty("ControlChannelHz")!.GetValue(candidate)! == 851775000 &&
                                                    (string)candidate.GetType().GetProperty("Gain")!.GetValue(candidate)! == "20");
        Assert.Contains(p25Selected, seed => (long)seed.GetType().GetProperty("ControlChannelHz")!.GetValue(seed)! == 851775000 &&
                                             (string)seed.GetType().GetProperty("Gain")!.GetValue(seed)! == "20");
    }

    [Fact]
    public void ValidationSweep_BuildsSiteReadinessForEverySelectedSite()
    {
        var profile = new RfSurveyProfileDto
        {
            Systems =
            [
                new("alpha", "Alpha", [100, 200], []),
                new("beta", "Beta", [300], [])
            ],
            ControlChannelsHz = [100, 200, 300]
        };
        var candidates = NewValidationCandidateArray(
            NewValidationCandidate(100, 7, systemShortName: "alpha", p25Status: "failed"),
            NewValidationCandidate(200, 8, systemShortName: "alpha", p25Status: "passed", metricsStatus: "passed", voiceStatus: "passed", voiceRealCalls: 2),
            NewValidationCandidate(300, 12, systemShortName: "beta", p25Status: "passed", metricsStatus: "passed", voiceStatus: "passed", voiceRealCalls: 1));
        var method = typeof(RfSurveyService).GetMethod("BuildRfValidationSiteReadiness", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildRfValidationSiteReadiness");

        var readiness = ((System.Collections.IEnumerable)method.Invoke(null, [profile, candidates])!).Cast<object>().ToList();

        Assert.Equal(2, readiness.Count);
        Assert.All(readiness, site => Assert.True((bool)site.GetType().GetProperty("Monitorable")!.GetValue(site)!));
        Assert.Contains(readiness, site => (string)site.GetType().GetProperty("SystemShortName")!.GetValue(site)! == "alpha" &&
                                          (long?)site.GetType().GetProperty("BestControlChannelHz")!.GetValue(site)! == 200);
        Assert.Contains(readiness, site => (string)site.GetType().GetProperty("SystemShortName")!.GetValue(site)! == "beta" &&
                                          (long?)site.GetType().GetProperty("BestControlChannelHz")!.GetValue(site)! == 300);
    }

    [Fact]
    public void ValidationSweep_SiteReadinessAllowsNoTrafficVoiceCaveat()
    {
        var profile = new RfSurveyProfileDto
        {
            Systems =
            [
                new("alpha", "Alpha", [100], []),
                new("beta", "Beta", [300], [])
            ],
            ControlChannelsHz = [100, 300]
        };
        var candidates = NewValidationCandidateArray(
            NewValidationCandidate(100, 7, systemShortName: "alpha", p25Status: "passed", metricsStatus: "passed", voiceStatus: "passed", voiceRealCalls: 1),
            NewValidationCandidate(300, 12, systemShortName: "beta", p25Status: "passed", metricsStatus: "passed", voiceStatus: "failed", voiceSummary: "No real captured calls with audio were found."));
        var method = typeof(RfSurveyService).GetMethod("BuildRfValidationSiteReadiness", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildRfValidationSiteReadiness");

        var readiness = ((System.Collections.IEnumerable)method.Invoke(null, [profile, candidates])!).Cast<object>().ToList();
        var beta = readiness.Single(site => (string)site.GetType().GetProperty("SystemShortName")!.GetValue(site)! == "beta");

        Assert.True((bool)beta.GetType().GetProperty("Monitorable")!.GetValue(beta)!);
        Assert.Equal(300, (long?)beta.GetType().GetProperty("BestControlChannelHz")!.GetValue(beta)!);
        Assert.Equal("voice_inconclusive", beta.GetType().GetProperty("Stage")!.GetValue(beta));
        Assert.Contains("Voice proof is inconclusive", (string)beta.GetType().GetProperty("Issue")!.GetValue(beta)!);
    }

    [Fact]
    public void AppliedSourcePlanSummary_ReportsSystemsAndSourceWindows()
    {
        var root = JsonNode.Parse("""
            {
              "sources": [
                { "device": "airspy=abc", "center": 853131250, "rate": 6000000 }
              ],
              "systems": [
                { "shortName": "chattanooga-simulcast-hamilton-t" },
                { "shortName": "cleveland-bradley-tn" }
              ]
            }
            """);
        var method = typeof(RfSurveyService).GetMethod("BuildAppliedSourcePlanSummary", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildAppliedSourcePlanSummary");

        var summary = (string)method.Invoke(null, [root, new[] { 0 }])!;

        Assert.Equal("Applied 1 SDR source window for 2 systems: chattanooga-simulcast-hamilton-t, cleveland-bradley-tn. Updated source index(es): 0.", summary);
    }

    [Fact]
    public void ObservedCallSpanSeconds_UsesLastStopTime()
    {
        var method = typeof(RfSurveyService).GetMethod("ObservedCallSpanSeconds", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "ObservedCallSpanSeconds");
        var calls = new[]
        {
            new EngineCall { StartTime = 0, StopTime = 42, AudioPath = "first.wav" },
            new EngineCall { StartTime = 114, StopTime = 120, AudioPath = "last.wav" }
        };

        var span = (long)(method.Invoke(null, [calls]) ?? 0L);

        Assert.Equal(120, span);
    }

    private static object NewValidationCandidate(
        long controlChannelHz,
        double snrDb,
        string gain = "20",
        int errorOffsetHz = 0,
        string systemShortName = "",
        string p25Status = "",
        string metricsStatus = "",
        string voiceStatus = "",
        int voiceRealCalls = 0,
        string voiceSummary = "")
    {
        var candidateType = typeof(RfSurveyService).GetNestedType("RfValidationCandidate", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(RfSurveyService).FullName, "RfValidationCandidate");
        var candidate = Activator.CreateInstance(candidateType, nonPublic: true)!;
        candidateType.GetProperty("Id")!.SetValue(candidate, $"s0-cc{controlChannelHz}-g{gain}-e{errorOffsetHz}");
        candidateType.GetProperty("SourceIndex")!.SetValue(candidate, 0);
        candidateType.GetProperty("SystemShortName")!.SetValue(candidate, systemShortName);
        candidateType.GetProperty("ControlChannelHz")!.SetValue(candidate, controlChannelHz);
        candidateType.GetProperty("Gain")!.SetValue(candidate, gain);
        candidateType.GetProperty("ErrorHz")!.SetValue(candidate, errorOffsetHz);
        candidateType.GetProperty("ErrorOffsetHz")!.SetValue(candidate, errorOffsetHz);
        candidateType.GetProperty("RfStatus")!.SetValue(candidate, "measured");
        candidateType.GetProperty("SnrDb")!.SetValue(candidate, snrDb);
        candidateType.GetProperty("P25Status")!.SetValue(candidate, p25Status);
        candidateType.GetProperty("P25Frames")!.SetValue(candidate, string.Equals(p25Status, "passed", StringComparison.OrdinalIgnoreCase));
        candidateType.GetProperty("MetricsStatus")!.SetValue(candidate, metricsStatus);
        candidateType.GetProperty("VoiceStatus")!.SetValue(candidate, voiceStatus);
        candidateType.GetProperty("VoiceRealCalls")!.SetValue(candidate, voiceRealCalls);
        candidateType.GetProperty("VoiceSummary")!.SetValue(candidate, voiceSummary);
        return candidate;
    }

    private static Array NewValidationCandidateArray(params object[] candidates)
    {
        var candidateType = typeof(RfSurveyService).GetNestedType("RfValidationCandidate", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(RfSurveyService).FullName, "RfValidationCandidate");
        var array = Array.CreateInstance(candidateType, candidates.Length);
        for (var i = 0; i < candidates.Length; i++)
            array.SetValue(candidates[i], i);
        return array;
    }
}
