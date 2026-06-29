using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace pizzad.Tests;

public sealed class SetupCalibrationServiceTests
{
    [Fact]
    public void BuildPlan_PreservesSourceErrorAsHzOffset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-calibration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 851000000, "rate": 2400000, "error": 1200, "gain": 32, "device": "rtl=00000001" },
                    { "center": 853000000, "rate": 2400000, "error": -1600, "gain": 32, "device": "rtl=00000002" },
                    { "center": 855000000, "rate": 2400000, "error": "300", "gain": 32, "device": "rtl=00000003" }
                  ],
                  "systems": [
                    { "shortName": "test", "modulation": "qpsk", "control_channels": [851012500] }
                  ]
                }
                """);

            var service = new SetupCalibrationService(
                new EngineConfig { TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath }, Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db") } },
                new EngineDatabase(new EngineConfig { Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root } }, NullLogger<EngineDatabase>.Instance),
                NullLogger<SetupCalibrationService>.Instance);

            var plan = service.BuildPlan();

            Assert.Equal(1200, plan.Sources[0].ErrorHz);
            Assert.Equal(-1600, plan.Sources[1].ErrorHz);
            Assert.Equal(300, plan.Sources[2].ErrorHz);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildPlan_UsesAllSourcesForSingleSystemWhenVoiceChannelsAreMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-calibration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 772718750, "rate": 2400000, "error": 0, "gain": 0, "device": "rtl=00000001" },
                    { "center": 770718750, "rate": 2400000, "error": 0, "gain": 0, "device": "rtl=00000002" }
                  ],
                  "systems": [
                    { "shortName": "etv-raymond", "modulation": "qpsk", "control_channels": [773031250, 773281250, 773531250, 773781250] }
                  ]
                }
                """);

            var service = new SetupCalibrationService(
                new EngineConfig { TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath }, Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db") } },
                new EngineDatabase(new EngineConfig { Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root } }, NullLogger<EngineDatabase>.Instance),
                NullLogger<SetupCalibrationService>.Instance);

            var plan = service.BuildPlan();

            var system = Assert.Single(plan.Systems);
            Assert.Equal(2, system.RequiredSdrCount);
            Assert.Equal(new[] { 0, 1 }, system.ProposedSourceIndexes);
            Assert.Contains(system.Warnings, warning => warning.Contains("using all configured SDR source windows", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(["etv-raymond"], plan.Sources[0].CoveredSystems);
            Assert.Equal(["etv-raymond"], plan.Sources[1].CoveredSystems);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildPlan_ExtractsAirspySerialFromSourceDevice()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-calibration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    { "center": 856225000, "rate": 3000000, "error": 0, "gain": 15, "device": "airspy=26A464DC28793293" }
                  ],
                  "systems": [
                    { "shortName": "test", "modulation": "qpsk", "control_channels": [856237500] }
                  ]
                }
                """);

            var service = new SetupCalibrationService(
                new EngineConfig { TrunkRecorder = new TrunkRecorderConfig { ConfigPath = trConfigPath }, Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db") } },
                new EngineDatabase(new EngineConfig { Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root } }, NullLogger<EngineDatabase>.Instance),
                NullLogger<SetupCalibrationService>.Instance);

            var plan = service.BuildPlan();

            var source = Assert.Single(plan.Sources);
            Assert.Equal("26A464DC28793293", source.Serial);
            Assert.Equal("airspy=26A464DC28793293", source.Device);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
