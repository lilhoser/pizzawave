using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class EmbeddingJobRetryTests
{
    [Fact]
    public void RetrievalEvidenceUsesTranscriptContentRatherThanQualityLabels()
    {
        var call = Call("retrieval-evidence") with
        {
            Transcription = "1159 Harrison Pike West, apartment 2208.",
            TranscriptionStatus = "poor_quality",
            QualityReason = "repetitive"
        };

        Assert.True(TranscriptRetrievalEvidence.IsUsable(call));
        Assert.False(TranscriptRetrievalEvidence.IsUsable(call with { Transcription = "10-4" }));
        Assert.False(TranscriptRetrievalEvidence.IsUsable(call with { Transcription = "   " }));
    }

    [Fact]
    public async Task ListPendingEmbeddingJobs_DoesNotImmediatelyRetryFailedJobs()
    {
        using var temp = new TempStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var first = await database.UpsertCallAsync(Call("retry-test-1"), CancellationToken.None);
        var second = await database.UpsertCallAsync(Call("retry-test-2"), CancellationToken.None);
        await database.UpsertEmbeddingJobAsync(first, "pending", string.Empty, CancellationToken.None);
        await database.UpsertEmbeddingJobAsync(second, "pending", string.Empty, CancellationToken.None);
        await database.MarkEmbeddingJobAsync(second, "failed", "temporary outage", CancellationToken.None);

        var jobs = await database.ListPendingEmbeddingJobsAsync(10, CancellationToken.None);

        Assert.Contains(jobs, job => job.CallId == first);
        Assert.DoesNotContain(jobs, job => job.CallId == second);
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
        Transcription = "Test transcript with enough useful words.",
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
