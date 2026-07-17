using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class TrHealthTroubleshootServiceTests
{
    [Fact]
    public async Task BuildAsync_MessageRateSampleVolumeAloneIsNotAnIssue()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tr-volume-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio")
                },
                TrunkRecorder = new TrunkRecorderConfig
                {
                    ConfigPath = Path.Combine(root, "missing-config.json"),
                    LogServiceName = "trunk-recorder"
                }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var start = DateTimeOffset.UtcNow.AddMinutes(-50).ToUnixTimeSeconds();
            for (var i = 0; i < 10; i++)
            {
                await database.InsertHealthSampleAsync(new TrHealthSampleDto
                {
                    WindowStartUtc = DateTimeOffset.FromUnixTimeSeconds(start + i * 300).UtcDateTime,
                    WindowEndUtc = DateTimeOffset.FromUnixTimeSeconds(start + (i + 1) * 300).UtcDateTime,
                    Scope = "healthy-system",
                    CcSummaryDecodeLines = 1,
                    CcSummaryDecodeRateTotal = 40,
                    LowDecodeWarningLines = 100,
                    LowDecodeWarningRateTotal = 40 * 100,
                    CallsStarted = 1,
                    CallsConcluded = 1
                }, CancellationToken.None);
            }

            var service = new TrHealthTroubleshootService(
                config,
                database,
                new TrConfigService(config, NullLogger<TrConfigService>.Instance),
                NullLogger<TrHealthTroubleshootService>.Instance);
            var result = await service.BuildAsync(start, start + 3000, bySystem: false, baseline: "7d", CancellationToken.None);

            var sampleVolume = Assert.Single(result.Health.Metrics, row => row.Metric == "CC message-rate samples");
            Assert.False(sampleVolume.IsIssue);
            var system = Assert.Single(result.Health.Systems, row => row.Metric == "healthy-system");
            Assert.False(system.IsIssue);
            var structured = Assert.Single(result.Health.SystemSummaries);
            Assert.Equal("Healthy", structured.Status);
            Assert.Equal(40, structured.CcSummaryAvgDecodeRate);
            Assert.Contains("RF-health window:", result.Health.Window, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_SummaryUsesRequestedWindowInsteadOfUnrelatedRecentRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tr-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = Path.Combine(root, "audio") },
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = Path.Combine(root, "missing-config.json"), LogServiceName = "trunk-recorder" }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var start = DateTimeOffset.UtcNow.AddMinutes(-50).ToUnixTimeSeconds();
            await database.InsertHealthSampleAsync(new TrHealthSampleDto
            {
                WindowStartUtc = DateTimeOffset.FromUnixTimeSeconds(start - 7200).UtcDateTime,
                WindowEndUtc = DateTimeOffset.FromUnixTimeSeconds(start - 6900).UtcDateTime,
                Scope = "windowed-system",
                CcSummaryDecodeLines = 20,
                CcSummaryDecodeZero = 20,
                CallsConcluded = 100
            }, CancellationToken.None);
            for (var i = 0; i < 10; i++)
            {
                await database.InsertHealthSampleAsync(new TrHealthSampleDto
                {
                    WindowStartUtc = DateTimeOffset.FromUnixTimeSeconds(start + i * 300).UtcDateTime,
                    WindowEndUtc = DateTimeOffset.FromUnixTimeSeconds(start + (i + 1) * 300).UtcDateTime,
                    Scope = "windowed-system",
                    CcSummaryDecodeLines = 1,
                    CcSummaryDecodeRateTotal = 40,
                    LowDecodeWarningLines = 100,
                    LowDecodeWarningRateTotal = 4000,
                    CallsConcluded = 1
                }, CancellationToken.None);
            }

            var service = new TrHealthTroubleshootService(config, database, new TrConfigService(config, NullLogger<TrConfigService>.Instance), NullLogger<TrHealthTroubleshootService>.Instance);
            var result = await service.BuildAsync(start, start + 3000, bySystem: false, baseline: "7d", CancellationToken.None);

            var system = Assert.Single(result.Health.SystemSummaries);
            Assert.Equal("Healthy", system.Status);
            Assert.Equal(0, system.CcSummaryDecodeZeroPercent);
            Assert.Equal(40, system.CcSummaryAvgDecodeRate);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_PerSiteAssessmentUsesMatureLocalBaselineInsteadOfBrittleAbsoluteCutoff()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-tr-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = Path.Combine(root, "audio") },
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = Path.Combine(root, "missing-config.json"), LogServiceName = "trunk-recorder" }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var selectedStart = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
            var historyStart = selectedStart - 144 * 300 - 3600;
            for (var i = 0; i < 144; i++)
            {
                await database.InsertHealthSampleAsync(new TrHealthSampleDto
                {
                    WindowStartUtc = DateTimeOffset.FromUnixTimeSeconds(historyStart + i * 300).UtcDateTime,
                    WindowEndUtc = DateTimeOffset.FromUnixTimeSeconds(historyStart + (i + 1) * 300).UtcDateTime,
                    Scope = "locally-stable-system",
                    CcSummaryDecodeLines = 1,
                    CcSummaryDecodeRateTotal = 40,
                    CallsConcluded = 100,
                    NoTxRecorded = 5,
                    Retunes = 3
                }, CancellationToken.None);
            }
            for (var i = 0; i < 12; i++)
            {
                await database.InsertHealthSampleAsync(new TrHealthSampleDto
                {
                    WindowStartUtc = DateTimeOffset.FromUnixTimeSeconds(selectedStart + i * 300).UtcDateTime,
                    WindowEndUtc = DateTimeOffset.FromUnixTimeSeconds(selectedStart + (i + 1) * 300).UtcDateTime,
                    Scope = "locally-stable-system",
                    CcSummaryDecodeLines = 1,
                    CcSummaryDecodeRateTotal = 36,
                    CallsConcluded = 100,
                    NoTxRecorded = 6,
                    Retunes = 3
                }, CancellationToken.None);
            }

            var service = new TrHealthTroubleshootService(config, database, new TrConfigService(config, NullLogger<TrConfigService>.Instance), NullLogger<TrHealthTroubleshootService>.Instance);
            var result = await service.BuildAsync(selectedStart, selectedStart + 3600, bySystem: false, baseline: "7d", CancellationToken.None);

            var system = Assert.Single(result.Health.SystemSummaries);
            Assert.Equal("Healthy", system.Status);
            Assert.Equal("local", system.DecodeAssessment.Basis);
            Assert.Equal("ok", system.DecodeAssessment.Tone);
            Assert.Equal(40, system.DecodeAssessment.BaselineValue);
            Assert.Equal("local", system.NoAudioAssessment.Basis);
            Assert.Equal("ok", system.NoAudioAssessment.Tone);
            Assert.Equal(5, system.NoAudioAssessment.BaselineValue);
            Assert.Equal("ok", system.RetunesAssessment.Tone);
            var sharedAssessments = await service.BuildSystemAssessmentsAsync(selectedStart, selectedStart + 3600, "7d", CancellationToken.None);
            var shared = Assert.Single(sharedAssessments);
            Assert.Equal(system.Status, shared.Status);
            Assert.Equal(system.NoAudioAssessment, shared.NoAudioAssessment);
            Assert.Equal(system.RetunesAssessment, shared.RetunesAssessment);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

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
            var zeroDecode = Assert.Single(result.Health.Charts, chart => chart.Title == "Zero-Decode Samples");
            Assert.All(zeroDecode.Series, series => Assert.Equal("chattanooga-simulcast-hamilton-t", series.Scope));
            Assert.Contains(result.Health.Charts, chart => chart.Title == "Calls Recorded");
            Assert.Contains(result.Health.Charts, chart => chart.Title == "Calls Without Audio");
            Assert.Contains(result.Health.Charts, chart => chart.Title == "Capture Interruptions");
            Assert.DoesNotContain(result.Health.Charts, chart => chart.Title.Contains("Message-Rate", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildAsync_RfChartsKeepThreeDayWindowAndSiteLocalBaselines()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rf-charts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = Path.Combine(root, "audio") },
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = Path.Combine(root, "missing-config.json") }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var end = DateTimeOffset.UtcNow.AddMinutes(-5);
            var start = end.AddHours(-72);
            for (var hours = -72; hours < 72; hours += 3)
            {
                var timestamp = start.AddHours(hours);
                if (timestamp >= end)
                    continue;
                foreach (var (scope, rate) in new[] { ("site-a", 40.0), ("site-b", 10.0) })
                {
                    await database.InsertHealthSampleAsync(new TrHealthSampleDto
                    {
                        WindowStartUtc = timestamp.UtcDateTime,
                        WindowEndUtc = timestamp.AddMinutes(5).UtcDateTime,
                        Scope = scope,
                        CcSummaryDecodeLines = 1,
                        CcSummaryDecodeRateTotal = rate,
                        CallsConcluded = 3
                    }, CancellationToken.None);
                }
            }

            var service = new TrHealthTroubleshootService(config, database, new TrConfigService(config, NullLogger<TrConfigService>.Instance), NullLogger<TrHealthTroubleshootService>.Instance);
            var result = await service.BuildAsync(start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds(), bySystem: true, baseline: "7d", CancellationToken.None);
            var decode = Assert.Single(result.Health.Charts, chart => chart.Title == "Decode Rate");
            Assert.InRange(decode.Labels.Count, 24, 26);
            var first = DateTime.Parse(decode.Labels[0], null, System.Globalization.DateTimeStyles.RoundtripKind);
            var last = DateTime.Parse(decode.Labels[^1], null, System.Globalization.DateTimeStyles.RoundtripKind);
            Assert.True(last - first >= TimeSpan.FromHours(66));
            var siteABaseline = Assert.Single(decode.Series, series => series.Scope == "site-a" && series.IsBaseline);
            var siteBBaseline = Assert.Single(decode.Series, series => series.Scope == "site-b" && series.IsBaseline);
            var siteACurrent = Assert.Single(decode.Series, series => series.Scope == "site-a" && !series.IsBaseline);
            Assert.Contains(siteACurrent.Values, value => value == 40);
            Assert.All(siteABaseline.Values.Where(value => value > 0), value => Assert.Equal(40, value, 3));
            Assert.All(siteBBaseline.Values.Where(value => value > 0), value => Assert.Equal(10, value, 3));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BuildTranscriptionPerformanceAsync_SeparatesOutcomesAndUsesChronologicalBuckets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-transcription-performance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = Path.Combine(root, "audio") },
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = Path.Combine(root, "missing-config.json") }
            };
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var start = DateTimeOffset.UtcNow.AddHours(-23).ToUnixTimeSeconds();
            await database.UpsertCallAsync(Call("baseline", start - 3600, "site-a", 851_000_000) with { AudioPath = "baseline.wav" }, CancellationToken.None);
            await database.UpsertCallAsync(Call("usable", start + 900, "site-a", 851_000_000) with { AudioPath = "usable.wav" }, CancellationToken.None);
            await database.UpsertCallAsync(Call("inaudible", start + 1800, "site-a", 851_000_000) with { AudioPath = "inaudible.wav", TranscriptionStatus = "poor_quality", QualityReason = "inaudible", Transcription = "[inaudible]" }, CancellationToken.None);
            await database.UpsertCallAsync(Call("failed", start + 2700, "site-a", 851_000_000) with { AudioPath = "failed.wav", TranscriptionStatus = "failed", QualityReason = "transcription_error", Transcription = "" }, CancellationToken.None);

            var service = new TrHealthTroubleshootService(config, database, new TrConfigService(config, NullLogger<TrConfigService>.Instance), NullLogger<TrHealthTroubleshootService>.Instance);
            var result = await service.BuildTranscriptionPerformanceAsync(start, DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(), 1, 25, CancellationToken.None);

            Assert.Equal(3, result.TotalCalls);
            Assert.Equal(2, result.CompletedCalls);
            Assert.Equal(1, result.UsableCalls);
            Assert.Equal(1, result.UnusableAudioCalls);
            Assert.Equal(1, result.EngineFailureCalls);
            Assert.Equal(100, result.BaselineUsablePercent);
            Assert.Equal(900, result.BucketSeconds);
            Assert.Equal(2, result.SampleTotal);
            Assert.Contains(result.Reasons, row => row.Reason == "inaudible" && row.Calls == 1);
            Assert.Contains(result.Throughput, row => row.CompletedAudioSeconds > 0);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
