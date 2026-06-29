using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class TrHealthTroubleshootServiceTests
{
    [Fact]
    public async Task BuildAsync_SourcePlanUsesObservedFrequenciesOutsideSelectedWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tr-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var trConfigPath = Path.Combine(root, "tr-config.json");
            File.WriteAllText(trConfigPath, """
                {
                  "sources": [
                    {
                      "device": "airspy=637862DC2E3A19D7",
                      "center": 853131250,
                      "rate": 6000000,
                      "digitalRecorders": 4
                    }
                  ],
                  "systems": [
                    {
                      "shortName": "chattanooga-simulcast-hamilton-t",
                      "modulation": "qpsk",
                      "control_channels": [855212500]
                    }
                  ]
                }
                """);
            var config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio")
                },
                TrunkRecorder = new TrunkRecorderConfig
                {
                    ConfigPath = trConfigPath,
                    LogServiceName = "trunk-recorder"
                }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);

            var start = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
            var end = start + 300;
            await database.InsertHealthSampleAsync(new TrHealthSampleDto
            {
                WindowStartUtc = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime,
                WindowEndUtc = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime,
                Scope = "chattanooga-simulcast-hamilton-t",
                CcSummaryDecodeLines = 1,
                CcSummaryDecodeRateTotal = 39,
                LowDecodeWarningLines = 1,
                LowDecodeWarningRateTotal = 39,
                CallsStarted = 1,
                CallsConcluded = 1
            }, CancellationToken.None);
            await database.UpsertCallAsync(Call("selected-low", start + 10, "chattanooga-simulcast-hamilton-t", 855_987_500), CancellationToken.None);
            await database.UpsertCallAsync(Call("historical-high", start - 86_400, "chattanooga-simulcast-hamilton-t", 858_437_500), CancellationToken.None);

            var service = new TrHealthTroubleshootService(
                config,
                database,
                new TrConfigService(config, NullLogger<TrConfigService>.Instance),
                NullLogger<TrHealthTroubleshootService>.Instance);

            var result = await service.BuildAsync(start, end, bySystem: true, baseline: "7d", CancellationToken.None);

            var plan = Assert.Single(result.Health.SourcePlan);
            Assert.Equal("chattanooga-simulcast-hamilton-t", plan.SystemShortName);
            Assert.Equal(858.4375, plan.HighMhz);
            Assert.Equal(0, plan.AssignedSourceIndex);
            Assert.True(plan.IsIssue);
            Assert.Contains("partially covered", plan.Notes, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static EngineCall Call(string key, long start, string system, double frequency) => new()
    {
        UniqueKey = key,
        StartTime = start,
        StopTime = start + 5,
        Source = 0,
        SystemShortName = system,
        CallstreamCallId = start,
        Talkgroup = 1001,
        TalkgroupName = "Dispatch",
        Frequency = frequency,
        Category = "other",
        AudioPath = "",
        Transcription = "test",
        TranscriptionStatus = "complete",
        QualityReason = "ok",
        RawMetadataJson = "{}"
    };
}
