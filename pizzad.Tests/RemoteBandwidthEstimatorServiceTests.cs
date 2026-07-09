using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class RemoteBandwidthEstimatorServiceTests
{
    [Fact]
    public async Task Report_UsesPersistedTranscriptionLedger()
    {
        using var temp = new TempStore();
        var config = temp.CreateConfig(remoteTranscription: true);
        var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        var service = new RemoteBandwidthEstimatorService(config, database, NullLogger<RemoteBandwidthEstimatorService>.Instance);
        var wavPath = Path.Combine(config.Storage.AudioRoot, "call.wav");
        Directory.CreateDirectory(config.Storage.AudioRoot);
        WritePcmWav(wavPath, dataBytes: 1600, sampleRate: 8000);

        await service.RecordTranscriptionAsync(new EngineCall
        {
            Id = 123,
            UniqueKey = "call-123",
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            StopTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            AudioPath = "call.wav",
            Transcription = "unit test transcription",
            TranscriptionStatus = "ok"
        }, CancellationToken.None);

        var report = await service.BuildReportAsync(
            DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            CancellationToken.None);

        Assert.Equal("sqlite:remote_bandwidth_usage", report.Ledger);
        Assert.Equal(1, report.Summary.Requests);
        Assert.True(report.Summary.RequestBytes > 0);
        Assert.True(report.Summary.ResponseBytes > 0);
        Assert.Contains(report.ByActivity, row => row.Activity == "remote transcription");
        Assert.Contains(report.Entries, row => row.Activity == "remote transcription" && row.Endpoint.Contains("/audio/transcriptions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LmUsage_WriteAddsRemoteAiBandwidthLedgerRow()
    {
        using var temp = new TempStore();
        var config = temp.CreateConfig(remoteTranscription: false);
        var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        await database.InitializeAsync(CancellationToken.None);
        await database.AddLmUsageAsync(new TokenUsageEntryDto(
            0,
            DateTime.UtcNow,
            "automatic insights",
            "chat.completions",
            true,
            string.Empty,
            "http://10.0.0.200:1234/v1/chat/completions",
            "test-model",
            "test-model",
            "stop",
            200,
            1200,
            300,
            80,
            380), CancellationToken.None);
        var service = new RemoteBandwidthEstimatorService(config, database, NullLogger<RemoteBandwidthEstimatorService>.Instance);

        var report = await service.BuildReportAsync(
            DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(),
            DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            CancellationToken.None);

        Assert.Equal(1, report.Summary.Requests);
        Assert.Equal(1200, report.Summary.RequestBytes);
        Assert.Contains(report.ByActivity, row => row.Activity == "AI insights");
        Assert.Contains(report.Entries, row => row.Activity == "AI insights" && row.Endpoint.Contains("10.0.0.200", StringComparison.Ordinal));
    }

    private static void WritePcmWav(string path, int dataBytes, int sampleRate)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }

    private sealed class TempStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pizzawave-test-" + Guid.NewGuid().ToString("N"));

        public EngineConfig CreateConfig(bool remoteTranscription)
        {
            Directory.CreateDirectory(_root);
            return new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(_root, "pizzad.db"),
                    AudioRoot = Path.Combine(_root, "audio")
                },
                Transcription = new TranscriptionConfig
                {
                    Provider = remoteTranscription ? "remote-faster-whisper" : "none",
                    OpenAiBaseUrl = remoteTranscription ? "http://10.0.0.200:8000/v1" : "http://localhost:8000/v1",
                    OpenAiModel = "whisper-1"
                },
                AiInsights = new AiInsightsConfig
                {
                    OpenAiBaseUrl = "http://10.0.0.200:1234/v1"
                }
            };
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
