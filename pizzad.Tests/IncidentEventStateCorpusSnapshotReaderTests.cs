using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IncidentEventStateCorpusSnapshotReaderTests
{
    [Fact]
    public async Task ReadsHalfOpenWindowInSourceOrder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-corpus-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "snapshot.db");
            var database = new EngineDatabase(
                new EngineConfig
                {
                    Storage = new StorageConfig
                    {
                        DatabasePath = databasePath,
                        AudioRoot = Path.Combine(root, "audio")
                    }
                },
                NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            await database.UpsertCallAsync(Call("before", 99), CancellationToken.None);
            await database.UpsertCallAsync(Call("second", 150), CancellationToken.None);
            await database.UpsertCallAsync(Call("first", 100), CancellationToken.None);
            await database.UpsertCallAsync(Call("after", 200), CancellationToken.None);

            var reader = new IncidentEventStateCorpusSnapshotReader(databasePath);
            var calls = await reader.ListCallsAsync(100, 200, CancellationToken.None);

            Assert.Equal([100L, 150L], calls.Select(call => call.StartTime));
            Assert.Equal(["first", "second"], calls.Select(call => call.UniqueKey));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task MissingSnapshotIsRejectedWithoutCreatingAFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-corpus-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "missing.db");
        try
        {
            var reader = new IncidentEventStateCorpusSnapshotReader(databasePath);

            await Assert.ThrowsAsync<SqliteException>(() =>
                reader.ListCallsAsync(100, 200, CancellationToken.None));

            Assert.False(File.Exists(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static EngineCall Call(string key, long startTime) =>
        new()
        {
            UniqueKey = key,
            StartTime = startTime,
            StopTime = startTime + 5,
            Source = 1,
            SystemShortName = "source-system",
            CallstreamCallId = startTime,
            Talkgroup = 1,
            Frequency = 851_000_000,
            AudioPath = $"{key}.wav"
        };
}
