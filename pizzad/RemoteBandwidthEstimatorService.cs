using System.Text;

namespace pizzad;

public sealed class RemoteBandwidthEstimatorService
{
    private const int TranscriptionMultipartOverheadBytes = 768;
    private const int TranscriptionJsonResponseOverheadBytes = 128;

    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;

    public RemoteBandwidthEstimatorService(EngineConfig config, EngineDatabase database, ILogger<RemoteBandwidthEstimatorService> logger)
    {
        _config = config;
        _database = database;
    }

    public async Task<RemoteBandwidthReportDto> BuildReportAsync(long start, long end, CancellationToken ct)
    {
        var startUtc = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
        var endUtc = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime;
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var summary = await _database.SummarizeRemoteBandwidthAsync(startUtc, endUtc, ct);
        var monthlySummary = await _database.SummarizeRemoteBandwidthAsync(monthStart, DateTime.UtcNow, ct);
        var allTimeSummary = await _database.SummarizeRemoteBandwidthAsync(null, null, ct);
        var byDay = await _database.ListRemoteBandwidthDayBucketsAsync(startUtc, endUtc, ct);
        var byActivity = await _database.ListRemoteBandwidthActivityBucketsAsync(startUtc, endUtc, ct);
        var entries = await _database.ListRemoteBandwidthEntriesAsync(startUtc, endUtc, 500, ct);
        var missingAudio = summary.MissingAudioFiles;
        var notes = Notes(missingAudio);

        return new RemoteBandwidthReportDto(
            "sqlite:remote_bandwidth_usage",
            RemoteHost(),
            TranscriptionEndpoint(),
            AiEndpoint(),
            ShouldIncludeRemoteTranscription(),
            notes,
            summary,
            monthlySummary,
            allTimeSummary,
            byDay,
            byActivity,
            entries);
    }

    public async Task<RemoteBandwidthUsageSnapshotDto> BuildUsageSnapshotAsync(long start, long end, CancellationToken ct)
    {
        var startUtc = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
        var endUtc = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime;
        var summary = await _database.SummarizeRemoteBandwidthAsync(startUtc, endUtc, ct);
        var byActivity = await _database.ListRemoteBandwidthActivityBucketsAsync(startUtc, endUtc, ct);
        return new RemoteBandwidthUsageSnapshotDto(
            RemoteHost(),
            TranscriptionEndpoint(),
            AiEndpoint(),
            ShouldIncludeRemoteTranscription(),
            Notes(summary.MissingAudioFiles),
            summary,
            byActivity);
    }

    public async Task RecordTranscriptionAsync(EngineCall call, CancellationToken ct)
    {
        if (!ShouldIncludeRemoteTranscription())
            return;

        var endpoint = $"{TranscriptionEndpoint().TrimEnd('/')}/audio/transcriptions";
        var audioPath = Path.IsPathRooted(call.AudioPath) ? call.AudioPath : Path.Combine(_config.Storage.AudioRoot, call.AudioPath);
        var timestampUtc = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).UtcDateTime;
        var responseBytes = EstimateTranscriptionResponseBytes(call);
        RemoteBandwidthUsageRecordDto usage;
        if (!File.Exists(audioPath))
        {
            usage = new RemoteBandwidthUsageRecordDto(
                $"transcription-call:{call.Id}",
                timestampUtc,
                "remote transcription",
                endpoint,
                0,
                responseBytes,
                responseBytes,
                $"missing audio file: {call.AudioPath}",
                true,
                true);
        }
        else
        {
            var uploadBytes = EstimateRemoteWhisperAudioBytes(audioPath) + TranscriptionMultipartOverheadBytes + Encoding.UTF8.GetByteCount(ResolveTranscriptionModel());
            usage = new RemoteBandwidthUsageRecordDto(
                $"transcription-call:{call.Id}",
                timestampUtc,
                "remote transcription",
                endpoint,
                uploadBytes,
                responseBytes,
                uploadBytes + responseBytes,
                "audio file size plus OpenAI-compatible multipart/model overhead",
                true,
                false);
        }

        await _database.UpsertRemoteBandwidthUsageAsync(usage, ct);
    }

    private long EstimateRemoteWhisperAudioBytes(string path)
    {
        var length = new FileInfo(path).Length;
        var format = TryReadWavFormat(path);
        if (format is { AudioFormat: 1, SampleRate: 8000, Channels: 1, BitsPerSample: 16, DataSize: > 0 })
            return 44L + format.DataSize * 2L;
        return length;
    }

    private static WavFormat? TryReadWavFormat(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (stream.Length < 44 || ReadAscii(reader, 4) != "RIFF")
                return null;
            stream.Position = 8;
            if (ReadAscii(reader, 4) != "WAVE")
                return null;

            short audioFormat = 0;
            short channels = 0;
            var sampleRate = 0;
            short bitsPerSample = 0;
            var dataOffset = -1;
            var dataSize = 0;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunkId = ReadAscii(reader, 4);
                var chunkSize = reader.ReadInt32();
                var chunkData = stream.Position;
                if (chunkSize < 0 || chunkData + chunkSize > stream.Length)
                    break;

                if (chunkId == "fmt " && chunkSize >= 16)
                {
                    audioFormat = reader.ReadInt16();
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    stream.Position += 6;
                    bitsPerSample = reader.ReadInt16();
                }
                else if (chunkId == "data")
                {
                    dataOffset = (int)chunkData;
                    dataSize = chunkSize;
                }

                stream.Position = chunkData + chunkSize + (chunkSize % 2);
            }

            return new WavFormat(audioFormat, sampleRate, channels, bitsPerSample, dataOffset, dataSize);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadAscii(BinaryReader reader, int count) => Encoding.ASCII.GetString(reader.ReadBytes(count));

    private long EstimateTranscriptionResponseBytes(EngineCall call) =>
        Encoding.UTF8.GetByteCount(call.Transcription ?? string.Empty) + TranscriptionJsonResponseOverheadBytes;

    private bool ShouldIncludeRemoteTranscription() =>
        string.Equals(_config.Transcription.Provider, "remote-faster-whisper", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(TranscriptionEndpoint()) &&
        !IsLocalHost(Host(TranscriptionEndpoint()));

    private string RemoteHost()
    {
        var aiHost = Host(AiEndpoint());
        if (!string.IsNullOrWhiteSpace(aiHost) && !IsLocalHost(aiHost))
            return aiHost;
        if (_config.AiInsights.ExecutionMode is "remote" or "lmlink")
            return _config.AiInsights.ExecutionMode;
        var transcriptionHost = Host(TranscriptionEndpoint());
        return IsLocalHost(transcriptionHost) ? string.Empty : transcriptionHost;
    }

    private string AiEndpoint() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl)
        ? _config.Transcription.OpenAiBaseUrl
        : _config.AiInsights.OpenAiBaseUrl;

    private string TranscriptionEndpoint() => _config.Transcription.OpenAiBaseUrl ?? string.Empty;

    private string ResolveTranscriptionModel() => string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel)
        ? "whisper-1"
        : _config.Transcription.OpenAiModel;

    private static string Host(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return uri.Host;
        return string.Empty;
    }

    private static bool IsLocalHost(string host) =>
        string.IsNullOrWhiteSpace(host) ||
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("::1", StringComparison.OrdinalIgnoreCase);

    private string Notes(int missingAudio)
    {
        var notes = new List<string>
        {
            "Derived from the remote_bandwidth_usage ledger recorded during transcription and AI usage writes; HTTP headers/TLS/TCP framing are not included.",
            "Requests that happened before this ledger existed are not reconstructed on report load.",
            "AI response bytes are estimated from completion tokens because response bodies are not stored."
        };
        if (ShouldIncludeRemoteTranscription())
            notes.Add("Remote transcription bytes assume the current remote-faster-whisper endpoint and estimate 8 kHz PCM uploads after PizzaWave's 16 kHz normalization.");
        else
            notes.Add("Remote transcription is not included because the current transcription provider is not remote-faster-whisper with a non-local endpoint.");
        if (IsLocalHost(Host(AiEndpoint())) && _config.AiInsights.ExecutionMode is not ("remote" or "lmlink"))
            notes.Add("AI insight rows are not included because the current AI endpoint is local/loopback, not the remote RTX host.");
        else if (_config.AiInsights.ExecutionMode is "remote" or "lmlink")
            notes.Add("AI insight rows are included because AI execution mode is remote/lmlink, even if the local endpoint is a relay.");
        if (missingAudio > 0)
            notes.Add($"{missingAudio:N0} selected-range transcription call(s) had missing audio files.");
        return string.Join(" ", notes);
    }
}
