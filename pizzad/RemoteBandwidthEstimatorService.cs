using System.Text;

namespace pizzad;

public sealed class RemoteBandwidthEstimatorService
{
    private const int TranscriptionMultipartOverheadBytes = 768;
    private const int TranscriptionJsonResponseOverheadBytes = 128;
    private const int AiJsonResponseOverheadBytes = 512;

    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger<RemoteBandwidthEstimatorService> _logger;

    public RemoteBandwidthEstimatorService(EngineConfig config, EngineDatabase database, ILogger<RemoteBandwidthEstimatorService> logger)
    {
        _config = config;
        _database = database;
        _logger = logger;
    }

    public async Task<RemoteBandwidthReportDto> BuildReportAsync(long start, long end, CancellationToken ct)
    {
        var startUtc = DateTimeOffset.FromUnixTimeSeconds(start).UtcDateTime;
        var endUtc = DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime;
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        var selectedEntries = await BuildEntriesAsync(start, end, ct);
        var monthlyEntries = await BuildEntriesAsync(new DateTimeOffset(monthStart).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ct);
        var allTimeEntries = await BuildEntriesAsync(null, null, ct);
        var missingAudio = selectedEntries.Count(e => e.Basis.Contains("missing audio", StringComparison.OrdinalIgnoreCase));
        var notes = Notes(missingAudio);

        return new RemoteBandwidthReportDto(
            "derived:calls+lm_usage+audio-files",
            RemoteHost(),
            TranscriptionEndpoint(),
            AiEndpoint(),
            ShouldIncludeRemoteTranscription(),
            notes,
            Summary(selectedEntries),
            Summary(monthlyEntries),
            Summary(allTimeEntries),
            BucketsByDay(selectedEntries),
            BucketsByActivity(selectedEntries),
            selectedEntries
                .Where(e => e.TotalBytes > 0 || e.Basis.Contains("missing audio", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.TimestampUtc)
                .Take(500)
                .ToList());
    }

    private async Task<List<RemoteBandwidthEntryDto>> BuildEntriesAsync(long? start, long? end, CancellationToken ct)
    {
        var entries = new List<RemoteBandwidthEntryDto>();
        if (ShouldIncludeRemoteTranscription())
        {
            var transcriptionStart = start ?? 0;
            var transcriptionEnd = end ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var calls = await _database.ListCallsAsync(transcriptionStart, transcriptionEnd, null, ct);
            foreach (var call in calls.Where(c => !string.Equals(c.TranscriptionStatus, "pending", StringComparison.OrdinalIgnoreCase)))
                entries.Add(EstimateTranscription(call));
        }

        DateTime? startUtc = start.HasValue ? DateTimeOffset.FromUnixTimeSeconds(start.Value).UtcDateTime : null;
        DateTime? endUtc = end.HasValue ? DateTimeOffset.FromUnixTimeSeconds(end.Value).UtcDateTime : null;
        var aiHost = Host(AiEndpoint());
        if (!string.IsNullOrWhiteSpace(aiHost) && !IsLocalHost(aiHost))
        {
            var usageRows = await _database.ListLmUsageEntriesAsync(startUtc, endUtc, ct);
            foreach (var row in usageRows.Where(row => Host(row.Endpoint).Equals(aiHost, StringComparison.OrdinalIgnoreCase)))
                entries.Add(EstimateAi(row));
        }

        return entries;
    }

    private RemoteBandwidthEntryDto EstimateTranscription(EngineCall call)
    {
        var endpoint = $"{TranscriptionEndpoint().TrimEnd('/')}/audio/transcriptions";
        var audioPath = Path.IsPathRooted(call.AudioPath) ? call.AudioPath : Path.Combine(_config.Storage.AudioRoot, call.AudioPath);
        if (!File.Exists(audioPath))
        {
            return new RemoteBandwidthEntryDto(
                DateTimeOffset.FromUnixTimeSeconds(call.StartTime).UtcDateTime,
                "remote transcription",
                endpoint,
                0,
                EstimateTranscriptionResponseBytes(call),
                EstimateTranscriptionResponseBytes(call),
                $"missing audio file: {call.AudioPath}",
                true);
        }

        var uploadBytes = EstimateRemoteWhisperAudioBytes(audioPath) + TranscriptionMultipartOverheadBytes + Encoding.UTF8.GetByteCount(ResolveTranscriptionModel());
        var responseBytes = EstimateTranscriptionResponseBytes(call);
        return new RemoteBandwidthEntryDto(
            DateTimeOffset.FromUnixTimeSeconds(call.StartTime).UtcDateTime,
            "remote transcription",
            endpoint,
            uploadBytes,
            responseBytes,
            uploadBytes + responseBytes,
            "audio file size plus OpenAI-compatible multipart/model overhead",
            true);
    }

    private RemoteBandwidthEntryDto EstimateAi(TokenUsageEntryDto row)
    {
        var requestBytes = Math.Max(0, row.PayloadChars);
        var responseBytes = Math.Max(0, row.CompletionTokens * 4 + AiJsonResponseOverheadBytes);
        if (!row.Success && row.CompletionTokens == 0)
            responseBytes = 0;

        return new RemoteBandwidthEntryDto(
            row.TimestampUtc,
            "AI insights",
            row.Endpoint,
            requestBytes,
            responseBytes,
            requestBytes + responseBytes,
            "lm_usage payload_chars plus completion-token response estimate",
            true);
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
            "Derived from stored calls, audio files, and the lm_usage table; HTTP headers/TLS/TCP framing are not included.",
            "AI response bytes are estimated from completion tokens because response bodies are not stored."
        };
        if (ShouldIncludeRemoteTranscription())
            notes.Add("Remote transcription bytes assume the current remote-faster-whisper endpoint and estimate 8 kHz PCM uploads after PizzaWave's 16 kHz normalization.");
        else
            notes.Add("Remote transcription is not included because the current transcription provider is not remote-faster-whisper with a non-local endpoint.");
        if (IsLocalHost(Host(AiEndpoint())))
            notes.Add("AI insight rows are not included because the current AI endpoint is local/loopback, not the remote RTX host.");
        if (missingAudio > 0)
            notes.Add($"{missingAudio:N0} selected-range transcription call(s) had missing audio files.");
        return string.Join(" ", notes);
    }

    private static RemoteBandwidthSummaryDto Summary(IReadOnlyList<RemoteBandwidthEntryDto> entries)
    {
        var request = entries.Sum(e => e.RequestBytes);
        var response = entries.Sum(e => e.ResponseBytes);
        return new RemoteBandwidthSummaryDto(
            request,
            response,
            request + response,
            entries.Count(e => e.TotalBytes > 0),
            entries.Count(e => e.Basis.Contains("missing audio", StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<RemoteBandwidthBucketDto> BucketsByDay(IReadOnlyList<RemoteBandwidthEntryDto> entries) =>
        entries
            .GroupBy(e => e.TimestampUtc.ToLocalTime().ToString("MM/dd"))
            .Select(g => Bucket(g.Key, "all", g))
            .OrderBy(b => b.Label)
            .ToList();

    private static IReadOnlyList<RemoteBandwidthBucketDto> BucketsByActivity(IReadOnlyList<RemoteBandwidthEntryDto> entries) =>
        entries
            .GroupBy(e => e.Activity)
            .Select(g => Bucket(g.Key, g.Key, g))
            .OrderByDescending(b => b.TotalBytes)
            .ToList();

    private static RemoteBandwidthBucketDto Bucket(string label, string activity, IEnumerable<RemoteBandwidthEntryDto> rows)
    {
        var list = rows.ToList();
        var request = list.Sum(r => r.RequestBytes);
        var response = list.Sum(r => r.ResponseBytes);
        return new RemoteBandwidthBucketDto(label, activity, request, response, request + response, list.Count);
    }
}
