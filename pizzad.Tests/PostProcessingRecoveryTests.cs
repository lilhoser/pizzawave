using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class PostProcessingRecoveryTests
{
    [Fact]
    public async Task MissingPostProcessingList_ExcludesCallsAfterMarker()
    {
        using var temp = new TempStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var id = await database.UpsertCallAsync(Call("post-processing-marker"), CancellationToken.None);

        var before = await database.ListCallsMissingPostProcessingAsync(10, CancellationToken.None);
        Assert.Contains(before, call => call.Id == id);

        await database.MarkCallPostProcessedAsync(id, CancellationToken.None);

        var after = await database.ListCallsMissingPostProcessingAsync(10, CancellationToken.None);
        Assert.DoesNotContain(after, call => call.Id == id);
    }

    private static EngineCall Call(string key) => new()
    {
        UniqueKey = key,
        StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        StopTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5,
        SystemShortName = "test",
        CallstreamCallId = Math.Abs(key.GetHashCode()),
        Talkgroup = 100,
        TalkgroupName = "Test Dispatch",
        Frequency = 851.0125,
        Category = "other",
        AudioPath = "test.wav",
        Transcription = "Respond to 123 Main Street for a 10-50.",
        TranscriptionStatus = "complete",
        QualityReason = "ok",
        RawMetadataJson = "{}"
    };

    private sealed class TempStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pizzawave-test-" + Guid.NewGuid().ToString("N"));

        public EngineDatabase CreateDatabase()
        {
            Directory.CreateDirectory(_root);
            var config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(_root, "pizzad.db"),
                    AudioRoot = Path.Combine(_root, "audio")
                }
            };
            return new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
                // Best effort cleanup for Windows file handles.
            }
        }
    }
}
