using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IncidentAnalysisQueueTests
{
    [Fact]
    public async Task StaleJobsAreRecordedAsSkippedAndCannotStarveCurrentCalls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-incident-queue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var staleId = await AddCallAsync(database, "stale", now.AddHours(-3));
            var currentId = await AddCallAsync(database, "current", now.AddMinutes(-2));
            await database.QueueIncidentAnalysisAsync(staleId, CancellationToken.None);
            await database.QueueIncidentAnalysisAsync(currentId, CancellationToken.None);

            var degraded = await database.GetIncidentAnalysisQueueHealthAsync(60, CancellationToken.None);
            Assert.Equal("degraded", degraded.Status);
            Assert.Equal(2, degraded.PendingCalls);
            Assert.Equal(1, degraded.StalePendingCalls);

            var cutoff = now.AddMinutes(-60).ToUnixTimeSeconds();
            var skipped = await database.SkipStaleIncidentAnalysisJobsAsync(cutoff, "expired from live window", CancellationToken.None);
            Assert.Equal(1, skipped);
            var pending = await database.ListPendingIncidentAnalysisCallsAsync(10, cutoff, CancellationToken.None);
            Assert.Equal(currentId, Assert.Single(pending).Id);

            var healthy = await database.GetIncidentAnalysisQueueHealthAsync(60, CancellationToken.None);
            Assert.Equal("ok", healthy.Status);
            Assert.Equal(1, healthy.PendingCalls);
            Assert.Equal(0, healthy.StalePendingCalls);
            Assert.Equal(1, healthy.SkippedStaleCalls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DurableQueueReturnsNewestCurrentCallsFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-incident-queue-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = root }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var oldest = await AddCallAsync(database, "oldest", now.AddMinutes(-20));
            var middle = await AddCallAsync(database, "middle", now.AddMinutes(-10));
            var newest = await AddCallAsync(database, "newest", now.AddMinutes(-1));
            foreach (var callId in new[] { oldest, middle, newest })
                await database.QueueIncidentAnalysisAsync(callId, CancellationToken.None);

            var rows = await database.ListPendingIncidentAnalysisCallsAsync(2, now.AddHours(-1).ToUnixTimeSeconds(), CancellationToken.None);
            Assert.Equal([newest, middle], rows.Select(call => call.Id).ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void LiveBatchIsBoundedToNewestCallsAndFairAcrossSystems()
    {
        var pending = new[]
        {
            Call(1, "system-a", 100),
            Call(2, "system-a", 200),
            Call(3, "system-a", 300),
            Call(4, "system-b", 150),
            Call(5, "system-b", 250),
            Call(6, "system-c", 175)
        };

        var selected = AutomaticInsightsService.SelectCurrentIncidentBatch(pending, 4);

        Assert.Equal(4, selected.Count);
        Assert.Contains(selected, call => call.Id == 3);
        Assert.Contains(selected, call => call.Id == 5);
        Assert.Contains(selected, call => call.Id == 6);
        Assert.DoesNotContain(selected, call => call.Id == 1);
        Assert.Equal(selected.OrderBy(call => call.StartTime).Select(call => call.Id), selected.Select(call => call.Id));
    }

    private static async Task<long> AddCallAsync(EngineDatabase database, string key, DateTimeOffset observedAt) =>
        await database.UpsertCallAsync(new EngineCall
        {
            UniqueKey = key,
            StartTime = observedAt.ToUnixTimeSeconds(),
            StopTime = observedAt.AddSeconds(5).ToUnixTimeSeconds(),
            SystemShortName = "system-a",
            AudioPath = $"{key}.wav",
            Transcription = $"Transcript {key}",
            TranscriptionStatus = "complete",
            QualityReason = "ok"
        }, CancellationToken.None);

    private static EngineCall Call(long id, string system, long startTime) => new()
    {
        Id = id,
        SystemShortName = system,
        StartTime = startTime,
        Transcription = $"call {id}",
        TranscriptionStatus = "complete",
        QualityReason = "ok"
    };
}
