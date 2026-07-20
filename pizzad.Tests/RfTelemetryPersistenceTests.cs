using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class RfTelemetryPersistenceTests
{
    [Fact]
    public void Parser_AcceptsSupportedEventsAndRejectsBrokenContracts()
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sample = $"prefix PIZZAWAVE_RF {{\"schemaVersion\":1,\"event\":\"rf_sample\",\"timestampUnixMs\":{nowMs},\"systemShortName\":\"raymond\",\"systemType\":\"p25\",\"controlChannelHz\":773031250,\"decodeRate\":40,\"frequencyErrorHz\":595,\"sourceIndex\":0,\"sourceCenterHz\":773500000,\"sourceSampleRate\":2400000,\"sourceErrorHz\":595,\"sourceDriver\":\"osmosdr\",\"sourceDevice\":\"rtl=1\",\"sampleWindowSeconds\":3,\"lowDecodeSeconds\":0}}";
        var reacquired = $"PIZZAWAVE_RF {{\"schemaVersion\":1,\"event\":\"control_channel_reacquired\",\"timestampUnixMs\":{nowMs + 1},\"systemShortName\":\"raymond\",\"systemType\":\"p25\",\"controlChannelHz\":773031250,\"decodeRate\":37,\"frequencyErrorHz\":400,\"sourceIndex\":0,\"sourceCenterHz\":773500000,\"sourceSampleRate\":2400000,\"sourceErrorHz\":400,\"sourceDriver\":\"osmosdr\",\"sourceDevice\":\"rtl=1\",\"lowDecodeSeconds\":18}}";
        var retune = $"TR_RF {{\"schemaVersion\":1,\"event\":\"control_channel_retune\",\"timestampUnixMs\":{nowMs + 2},\"systemShortName\":\"raymond\",\"systemType\":\"p25\",\"reason\":\"low_decode\",\"previousControlChannelHz\":773031250,\"requestedControlChannelHz\":773281250,\"previousSourceIndex\":0,\"previousSourceCenterHz\":773500000,\"selectedSourceIndex\":0,\"selectedSourceCenterHz\":773500000,\"selectedSourceSampleRate\":2400000,\"selectedSourceErrorHz\":500,\"selectedSourceDriver\":\"osmosdr\",\"selectedSourceDevice\":\"rtl=1\",\"decodeRate\":0,\"frequencyErrorBeforeRetuneHz\":595,\"success\":true}}";
        var wrongPrefix = retune.Replace("TR_RF", "PIZZAWAVE_RF", StringComparison.Ordinal);
        var unknownSchema = sample.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal);
        var missingSystem = sample.Replace("\"systemShortName\":\"raymond\",", string.Empty, StringComparison.Ordinal);
        var wrongNumericType = sample.Replace("\"decodeRate\":40", "\"decodeRate\":\"fast\"", StringComparison.Ordinal);

        var parsed = RfTelemetryParser.ParseJournal(string.Join('\n', sample, reacquired, retune, wrongPrefix, unknownSchema, missingSystem, wrongNumericType), out var rejected);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(4, rejected);
        Assert.Equal(["rf_sample", "control_channel_reacquired", "control_channel_retune"], parsed.Select(row => row.EventType).ToArray());
        Assert.Equal(40, parsed[0].DecodeRate);
        Assert.Equal(18, parsed[1].LowDecodeSeconds);
        Assert.True(parsed[2].Success);
        Assert.Equal(595, parsed[2].FrequencyErrorBeforeRetuneHz);
        Assert.All(parsed, row => Assert.Equal(64, row.EventKey.Length));
    }

    [Fact]
    public async Task Database_DeduplicatesFiltersAndPrunesTelemetryByEventClass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-rf-telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio")
                }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);

            var now = DateTime.UtcNow;
            var recentSample = Event("sample-recent", "rf_sample", now, "raymond") with
            {
                DecodeRate = 40,
                FrequencyErrorHz = 595,
                LowDecodeSeconds = 0
            };
            var oldSample = Event("sample-old", "rf_sample", now.AddDays(-9), "raymond");
            var oldRetune = Event("retune-old", "control_channel_retune", now.AddDays(-20), "raymond") with
            {
                Success = true,
                FrequencyErrorBeforeRetuneHz = 595
            };
            var expiredRetune = Event("retune-expired", "control_channel_retune", now.AddDays(-100), "jackson") with { Success = false };

            Assert.Equal(4, await database.UpsertRfTelemetryEventsAsync([recentSample, oldSample, oldRetune, expiredRetune], CancellationToken.None));
            Assert.Equal(0, await database.UpsertRfTelemetryEventsAsync([recentSample], CancellationToken.None));
            Assert.Equal(2, await database.PruneRfTelemetryEventsAsync(now.AddDays(-8), now.AddDays(-90), CancellationToken.None));

            var start = new DateTimeOffset(now.AddDays(-120)).ToUnixTimeSeconds();
            var end = new DateTimeOffset(now.AddMinutes(1)).ToUnixTimeSeconds();
            var all = await database.ListRfTelemetryEventsAsync(start, end, null, null, 100, CancellationToken.None);
            Assert.Equal(2, all.Count);
            Assert.Contains(all, row => row.EventKey == "sample-recent");
            Assert.Contains(all, row => row.EventKey == "retune-old");

            var retunes = await database.ListRfTelemetryEventsAsync(start, end, "RAYMOND", "control_channel_retune", 100, CancellationToken.None);
            var retune = Assert.Single(retunes);
            Assert.True(retune.Success);
            Assert.Equal(595, retune.FrequencyErrorBeforeRetuneHz);

            var summary = await database.BuildRfTelemetrySummaryAsync(start, end, CancellationToken.None);
            var site = Assert.Single(summary.Sites);
            Assert.Equal("raymond", site.SystemShortName);
            Assert.Equal(40, site.AverageDecodeRate);
            Assert.Equal(0, site.ZeroDecodePercent);
            var point = Assert.Single(site.Points);
            Assert.Equal(595, point.AverageAbsoluteFrequencyErrorHz);
            Assert.Single(summary.Transitions);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static RfTelemetryEventDto Event(string key, string type, DateTime timestampUtc, string system) => new()
    {
        EventKey = key,
        SchemaVersion = 1,
        EventType = type,
        TimestampUtc = timestampUtc,
        SystemShortName = system,
        SystemType = "p25",
        RawJson = "{}"
    };
}
