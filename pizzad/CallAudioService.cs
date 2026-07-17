using System.Diagnostics;
using System.Text;

namespace pizzad;

public sealed class CallAudioService
{
    private readonly EngineConfig _config;
    private readonly ILogger<CallAudioService> _logger;

    public CallAudioService(EngineConfig config, ILogger<CallAudioService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public MemoryStream CreateWavStream(byte[] pcmS16Le, int sampleRate)
    {
        const int headerSize = 44;
        var dataSize = pcmS16Le.Length;
        var wav = new MemoryStream(headerSize + dataSize);
        using var writer = new BinaryWriter(wav, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        writer.Write(pcmS16Le);
        wav.Position = 0;
        return wav;
    }

    public async Task<MemoryStream> CreateTranscriptionWavAsync(CallstreamPayload payload, long callId, CancellationToken ct)
    {
        using var wav = CreateWavStream(payload.PcmS16Le, payload.SampleRate);
        return await Ensure16kMonoPcmAsync(wav, callId, ct);
    }

    public async Task<MemoryStream> Ensure16kMonoPcmAsync(MemoryStream wav, long callId, CancellationToken ct)
    {
        wav.Position = 0;
        var info = TryReadWavFormat(wav);
        if (info is { SampleRate: 16000, Channels: 1 })
        {
            wav.Position = 0;
            return new MemoryStream(wav.ToArray());
        }

        if (TryNormalizePcm8kMonoTo16kMono(wav, info, out var normalized))
        {
            _logger.LogDebug("Normalized call {CallId} audio in-process from 8 kHz mono PCM to 16 kHz mono", callId);
            return normalized;
        }

        var tempDir = Path.Combine(_config.Storage.AppDataRoot, "transcription-normalize");
        Directory.CreateDirectory(tempDir);
        var input = Path.Combine(tempDir, $"call-{callId}-{Guid.NewGuid():N}-in.wav");
        var output = Path.Combine(tempDir, $"call-{callId}-{Guid.NewGuid():N}-16k.wav");
        try
        {
            wav.Position = 0;
            await File.WriteAllBytesAsync(input, wav.ToArray(), ct);
            await RunFfmpegAsync(input, output, ct);
            var bytes = await File.ReadAllBytesAsync(output, ct);
            _logger.LogDebug("Normalized call {CallId} audio with ffmpeg from {SampleRate} Hz/{Channels} channel(s) to 16 kHz mono", callId, info?.SampleRate, info?.Channels);
            return new MemoryStream(bytes);
        }
        finally
        {
            TryDelete(input);
            TryDelete(output);
        }
    }

    public static WavFormat? TryReadWavFormat(MemoryStream wav)
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

    public static double? TryReadWavDurationSeconds(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (stream.Length < 12 || new string(reader.ReadChars(4)) != "RIFF") return null;
            reader.ReadInt32();
            if (new string(reader.ReadChars(4)) != "WAVE") return null;
            var byteRate = 0;
            long dataSize = -1;
            while (stream.Position + 8 <= stream.Length)
            {
                var chunk = new string(reader.ReadChars(4));
                var size = reader.ReadInt32();
                if (size < 0 || stream.Position + size > stream.Length) break;
                if (chunk == "fmt " && size >= 12)
                {
                    reader.ReadInt16();
                    reader.ReadInt16();
                    reader.ReadInt32();
                    byteRate = reader.ReadInt32();
                    stream.Position += size - 12;
                }
                else if (chunk == "data")
                {
                    dataSize = size;
                    break;
                }
                else stream.Position += size;
                if ((size & 1) != 0 && stream.Position < stream.Length) stream.Position++;
            }
            return byteRate > 0 && dataSize >= 0 ? dataSize / (double)byteRate : null;
        }
        catch { return null; }
    }

    public static bool TryNormalizePcm8kMonoTo16kMono(MemoryStream wav, WavFormat? info, out MemoryStream normalized)
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

    private static async Task RunFfmpegAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-ar", "16000", "-ac", "1", outputPath })
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start ffmpeg for audio normalization.");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg failed to normalize audio: " + await stderr);
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
        catch
        {
        }
    }
}

public sealed record WavFormat(short AudioFormat, int SampleRate, short Channels, short BitsPerSample, int DataOffset, int DataSize);
