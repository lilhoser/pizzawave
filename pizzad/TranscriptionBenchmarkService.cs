using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace pizzad;

public sealed class TranscriptionBenchmarkService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger<TranscriptionBenchmarkService> _logger;

    public TranscriptionBenchmarkService(EngineConfig config, EngineDatabase database, ILogger<TranscriptionBenchmarkService> logger)
    {
        _config = config;
        _database = database;
        _logger = logger;
    }

    public async Task<TranscriptionBenchmarkResultDto> RunAsync(int sampleCount, int lookbackHours, CancellationToken ct)
    {
        var provider = (_config.Transcription.Provider ?? "none").Trim().ToLowerInvariant();
        if (provider != "whisper")
            throw new InvalidOperationException("Transcription benchmark currently supports the local Whisper provider only.");
        if (string.IsNullOrWhiteSpace(_config.Transcription.WhisperModelFile) || !File.Exists(_config.Transcription.WhisperModelFile))
            throw new InvalidOperationException("Whisper model file is not configured or does not exist.");

        sampleCount = Math.Clamp(sampleCount <= 0 ? 10 : sampleCount, 1, 25);
        lookbackHours = Math.Clamp(lookbackHours <= 0 ? 4 : lookbackHours, 1, 168);

        var total = Stopwatch.StartNew();
        var sampleWatch = Stopwatch.StartNew();
        var samples = await LoadSamplesAsync(sampleCount, lookbackHours, ct);
        sampleWatch.Stop();
        if (samples.Count == 0)
            throw new InvalidOperationException("No recent completed calls with audio were available for benchmark sampling.");

        var initWatch = Stopwatch.StartNew();
        using var transcriber = new WhisperTranscriber(_config.Transcription.WhisperModelFile, _config.Transcription.WhisperThreads, _logger);
        await transcriber.Initialize();
        initWatch.Stop();

        var rows = new List<TranscriptionBenchmarkRowDto>();
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        foreach (var sample in samples)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await RunSampleAsync(sample, transcriber, ct));
        }
        total.Stop();
        process.Refresh();
        var cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;

        var warmRows = rows.Skip(1).ToList();
        var denominator = warmRows.Count > 0 ? warmRows : rows;
        var warmSeconds = denominator.Sum(r => r.TotalSeconds);
        var warmCallsPerMinute = warmSeconds > 0 ? denominator.Count / warmSeconds * 60 : 0;
        var totalAudioSeconds = rows.Sum(r => r.AudioSeconds);
        var totalInferenceSeconds = rows.Sum(r => r.WhisperSeconds);
        var totalSeconds = rows.Sum(r => r.TotalSeconds);

        return new TranscriptionBenchmarkResultDto
        {
            StartedAtUtc = DateTime.UtcNow,
            Provider = provider,
            Model = _config.Transcription.WhisperModelFile,
            WhisperThreads = Math.Max(1, _config.Transcription.WhisperThreads),
            SampleCount = rows.Count,
            LookbackHours = lookbackHours,
            SampleSelectSeconds = sampleWatch.Elapsed.TotalSeconds,
            ModelInitSeconds = initWatch.Elapsed.TotalSeconds,
            TotalSeconds = total.Elapsed.TotalSeconds,
            ProcessCpuSeconds = cpuSeconds,
            WarmCallsPerMinute = warmCallsPerMinute,
            AverageTotalSeconds = rows.Average(r => r.TotalSeconds),
            AverageAudioSeconds = rows.Average(r => r.AudioSeconds),
            AverageRealtimeFactor = totalAudioSeconds > 0 ? totalSeconds / totalAudioSeconds : 0,
            AverageReadSeconds = rows.Average(r => r.ReadSeconds),
            AverageNormalizeSeconds = rows.Average(r => r.NormalizeSeconds),
            AverageWhisperSeconds = rows.Average(r => r.WhisperSeconds),
            AverageQualitySeconds = rows.Average(r => r.QualitySeconds),
            AverageScratchWriteSeconds = rows.Average(r => r.ScratchWriteSeconds),
            WhisperSharePercent = totalSeconds > 0 ? totalInferenceSeconds / totalSeconds * 100 : 0,
            FailureCount = rows.Count(r => !string.IsNullOrWhiteSpace(r.Error)),
            Rows = rows
        };
    }

    private async Task<List<BenchmarkSample>> LoadSamplesAsync(int sampleCount, int lookbackHours, CancellationToken ct)
    {
        var start = DateTimeOffset.UtcNow.AddHours(-lookbackHours).ToUnixTimeSeconds();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, start_time, stop_time, system_short_name, talkgroup, talkgroup_name, category, audio_path
            FROM calls
            WHERE start_time >= $start
              AND length(trim(audio_path)) > 0
              AND transcription_status IN ('complete', 'poor_quality')
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$start", start);
        command.Parameters.AddWithValue("$limit", sampleCount);
        var rows = new List<BenchmarkSample>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var audioPath = reader.GetString(7);
            rows.Add(new BenchmarkSample(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                Path.IsPathRooted(audioPath) ? audioPath : Path.Combine(_config.Storage.AudioRoot, audioPath)));
        }
        return rows;
    }

    private async Task<TranscriptionBenchmarkRowDto> RunSampleAsync(BenchmarkSample sample, ITranscriber transcriber, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var readSeconds = 0d;
        var normalizeSeconds = 0d;
        var whisperSeconds = 0d;
        var qualitySeconds = 0d;
        var scratchWriteSeconds = 0d;
        var text = string.Empty;
        var quality = TranscriptionQualityClassifier.Classify(string.Empty);
        string? error = null;

        try
        {
            var readWatch = Stopwatch.StartNew();
            await using var file = File.OpenRead(sample.AudioPath);
            using var source = new MemoryStream();
            await file.CopyToAsync(source, ct);
            source.Position = 0;
            readWatch.Stop();
            readSeconds = readWatch.Elapsed.TotalSeconds;

            var normalizeWatch = Stopwatch.StartNew();
            using var normalized = await NormalizeForWhisperAsync(source, sample.Id, ct);
            normalizeWatch.Stop();
            normalizeSeconds = normalizeWatch.Elapsed.TotalSeconds;

            var whisperWatch = Stopwatch.StartNew();
            text = await transcriber.TranscribeCall(normalized);
            whisperWatch.Stop();
            whisperSeconds = whisperWatch.Elapsed.TotalSeconds;

            var qualityWatch = Stopwatch.StartNew();
            quality = TranscriptionQualityClassifier.Classify(text);
            qualityWatch.Stop();
            qualitySeconds = qualityWatch.Elapsed.TotalSeconds;

            var writeWatch = Stopwatch.StartNew();
            await ScratchWriteAsync(sample.Id, text, quality.Reason, ct);
            writeWatch.Stop();
            scratchWriteSeconds = writeWatch.Elapsed.TotalSeconds;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "Transcription benchmark failed for call {CallId}", sample.Id);
        }
        finally
        {
            total.Stop();
        }

        return new TranscriptionBenchmarkRowDto(
            sample.Id,
            sample.StartTime,
            sample.StopTime,
            Math.Max(0, sample.StopTime - sample.StartTime),
            sample.SystemShortName,
            sample.Talkgroup,
            sample.TalkgroupName,
            sample.Category,
            total.Elapsed.TotalSeconds,
            readSeconds,
            normalizeSeconds,
            whisperSeconds,
            qualitySeconds,
            scratchWriteSeconds,
            text.Length,
            quality.Reason,
            quality.IncludeInSummaries,
            Preview(text),
            error);
    }

    private async Task ScratchWriteAsync(long callId, string transcription, string qualityReason, CancellationToken ct)
    {
        await using var connection = _database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var create = connection.CreateCommand();
        create.Transaction = transaction as SqliteTransaction;
        create.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS transcription_benchmark_scratch (
                call_id INTEGER NOT NULL,
                text_length INTEGER NOT NULL,
                quality_reason TEXT NOT NULL,
                written_at TEXT NOT NULL
            );
            """;
        await create.ExecuteNonQueryAsync(ct);

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction as SqliteTransaction;
        insert.CommandText = """
            INSERT INTO transcription_benchmark_scratch(call_id, text_length, quality_reason, written_at)
            VALUES ($call_id, $text_length, $quality_reason, $written_at);
            """;
        insert.Parameters.AddWithValue("$call_id", callId);
        insert.Parameters.AddWithValue("$text_length", transcription.Length);
        insert.Parameters.AddWithValue("$quality_reason", qualityReason);
        insert.Parameters.AddWithValue("$written_at", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<MemoryStream> NormalizeForWhisperAsync(MemoryStream wav, long callId, CancellationToken ct)
    {
        wav.Position = 0;
        var info = TryReadWavFormat(wav);
        if (info is { SampleRate: 16000, Channels: 1 })
        {
            wav.Position = 0;
            return wav;
        }

        if (TryNormalizePcm8kMonoTo16kMono(wav, info, out var normalized))
            return normalized;

        var tempDir = Path.Combine(_config.Storage.AppDataRoot, "transcription-benchmark-normalize");
        Directory.CreateDirectory(tempDir);
        var input = Path.Combine(tempDir, $"call-{callId}-{Guid.NewGuid():N}-in.wav");
        var output = Path.Combine(tempDir, $"call-{callId}-{Guid.NewGuid():N}-16k.wav");
        try
        {
            wav.Position = 0;
            await File.WriteAllBytesAsync(input, wav.ToArray(), ct);
            await RunFfmpegAsync(input, output, ct);
            var bytes = await File.ReadAllBytesAsync(output, ct);
            return new MemoryStream(bytes);
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    private static async Task RunFfmpegAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-ar", "16000", "-ac", "1", outputPath })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start ffmpeg for benchmark audio normalization.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg failed to normalize benchmark audio: " + await stderr);
    }

    private static WavFormat? TryReadWavFormat(MemoryStream wav)
    {
        try
        {
            var bytes = wav.ToArray();
            if (bytes.Length < 44 ||
                bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F' ||
                bytes[8] != (byte)'W' || bytes[9] != (byte)'A' || bytes[10] != (byte)'V' || bytes[11] != (byte)'E')
                return null;

            short audioFormat = 0;
            short channels = 0;
            var sampleRate = 0;
            short bitsPerSample = 0;
            var dataOffset = -1;
            var dataSize = 0;
            var offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                var chunkId = BitConverter.ToInt32(bytes, offset);
                var chunkSize = BitConverter.ToInt32(bytes, offset + 4);
                var chunkData = offset + 8;
                if (chunkSize < 0 || chunkData + chunkSize > bytes.Length)
                    break;

                if (chunkId == 0x20746d66 && chunkSize >= 16)
                {
                    audioFormat = BitConverter.ToInt16(bytes, chunkData);
                    channels = BitConverter.ToInt16(bytes, chunkData + 2);
                    sampleRate = BitConverter.ToInt32(bytes, chunkData + 4);
                    bitsPerSample = BitConverter.ToInt16(bytes, chunkData + 14);
                }
                else if (chunkId == 0x61746164)
                {
                    dataOffset = chunkData;
                    dataSize = chunkSize;
                }

                offset = chunkData + chunkSize + (chunkSize % 2);
            }

            return new WavFormat(audioFormat, sampleRate, channels, bitsPerSample, dataOffset, dataSize);
        }
        catch
        {
            return null;
        }
        finally
        {
            wav.Position = 0;
        }
    }

    private static bool TryNormalizePcm8kMonoTo16kMono(MemoryStream wav, WavFormat? info, out MemoryStream normalized)
    {
        normalized = null!;
        if (info is not { AudioFormat: 1, SampleRate: 8000, Channels: 1, BitsPerSample: 16 } ||
            info.DataOffset < 0 ||
            info.DataSize <= 0)
            return false;

        var source = wav.ToArray();
        if (info.DataOffset + info.DataSize > source.Length)
            return false;

        var sampleCount = info.DataSize / 2;
        if (sampleCount <= 0)
            return false;

        var outputDataSize = sampleCount * 4;
        var output = new byte[44 + outputDataSize];
        WriteAscii(output, 0, "RIFF");
        BitConverter.GetBytes(output.Length - 8).CopyTo(output, 4);
        WriteAscii(output, 8, "WAVE");
        WriteAscii(output, 12, "fmt ");
        BitConverter.GetBytes(16).CopyTo(output, 16);
        BitConverter.GetBytes((short)1).CopyTo(output, 20);
        BitConverter.GetBytes((short)1).CopyTo(output, 22);
        BitConverter.GetBytes(16000).CopyTo(output, 24);
        BitConverter.GetBytes(32000).CopyTo(output, 28);
        BitConverter.GetBytes((short)2).CopyTo(output, 32);
        BitConverter.GetBytes((short)16).CopyTo(output, 34);
        WriteAscii(output, 36, "data");
        BitConverter.GetBytes(outputDataSize).CopyTo(output, 40);

        var outOffset = 44;
        var inOffset = info.DataOffset;
        for (var i = 0; i < sampleCount; i++)
        {
            output[outOffset++] = source[inOffset];
            output[outOffset++] = source[inOffset + 1];
            output[outOffset++] = source[inOffset];
            output[outOffset++] = source[inOffset + 1];
            inOffset += 2;
        }

        normalized = new MemoryStream(output);
        return true;
    }

    private static void WriteAscii(byte[] target, int offset, string value)
    {
        for (var i = 0; i < value.Length; i++)
            target[offset + i] = (byte)value[i];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string Preview(string text)
    {
        text = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length <= 180 ? text : text[..180] + "...";
    }

    private sealed record BenchmarkSample(
        long Id,
        long StartTime,
        long StopTime,
        string SystemShortName,
        long Talkgroup,
        string TalkgroupName,
        string Category,
        string AudioPath);

    private sealed record WavFormat(short AudioFormat, int SampleRate, short Channels, short BitsPerSample, int DataOffset, int DataSize);
}

public sealed record TranscriptionBenchmarkRequest(int SampleCount = 10, int LookbackHours = 4);

public sealed record TranscriptionBenchmarkRowDto(
    long CallId,
    long StartTime,
    long StopTime,
    long AudioSeconds,
    string SystemShortName,
    long Talkgroup,
    string TalkgroupName,
    string Category,
    double TotalSeconds,
    double ReadSeconds,
    double NormalizeSeconds,
    double WhisperSeconds,
    double QualitySeconds,
    double ScratchWriteSeconds,
    int TextLength,
    string QualityReason,
    bool IncludeInSummaries,
    string Preview,
    string? Error);

public sealed record TranscriptionBenchmarkResultDto
{
    public DateTime StartedAtUtc { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int WhisperThreads { get; init; }
    public int SampleCount { get; init; }
    public int LookbackHours { get; init; }
    public double SampleSelectSeconds { get; init; }
    public double ModelInitSeconds { get; init; }
    public double TotalSeconds { get; init; }
    public double ProcessCpuSeconds { get; init; }
    public double WarmCallsPerMinute { get; init; }
    public double AverageTotalSeconds { get; init; }
    public double AverageAudioSeconds { get; init; }
    public double AverageRealtimeFactor { get; init; }
    public double AverageReadSeconds { get; init; }
    public double AverageNormalizeSeconds { get; init; }
    public double AverageWhisperSeconds { get; init; }
    public double AverageQualitySeconds { get; init; }
    public double AverageScratchWriteSeconds { get; init; }
    public double WhisperSharePercent { get; init; }
    public int FailureCount { get; init; }
    public IReadOnlyList<TranscriptionBenchmarkRowDto> Rows { get; init; } = [];
}
