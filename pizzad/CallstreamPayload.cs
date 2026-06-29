using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record CallstreamMetadata(
    long StartTime,
    long StopTime,
    string SystemShortName,
    long CallId,
    long Talkgroup,
    int Source,
    double Frequency);

public sealed class CallstreamPayload
{
    private const int PizzaMagic = 0x415A5A50; // pzza
    private const long MaxJsonLength = 4096 * 2;
    private const int MaxSampleCount = 0xfffffe;

    public CallstreamPayload(CallstreamMetadata metadata, string rawMetadataJson, byte[] pcmS16Le, int sampleRate)
    {
        Metadata = metadata;
        RawMetadataJson = rawMetadataJson;
        PcmS16Le = pcmS16Le;
        SampleRate = sampleRate;
    }

    public CallstreamMetadata Metadata { get; }
    public string RawMetadataJson { get; }
    public byte[] PcmS16Le { get; }
    public int SampleRate { get; }

    public static async Task<CallstreamPayload> ReadAsync(Stream stream, int sampleRate, CancellationToken ct)
    {
        var buffer4 = new byte[4];
        var buffer8 = new byte[8];

        await stream.ReadExactlyAsync(buffer4, ct);
        if (BitConverter.ToInt32(buffer4, 0) != PizzaMagic)
            throw new InvalidDataException("Bad callstream magic header.");

        await stream.ReadExactlyAsync(buffer8, ct);
        var jsonLength = BitConverter.ToInt64(buffer8, 0);
        if (jsonLength <= 0 || jsonLength > MaxJsonLength)
            throw new InvalidDataException($"Invalid callstream metadata length: {jsonLength}.");

        await stream.ReadExactlyAsync(buffer4, ct);
        var sampleCount = BitConverter.ToInt32(buffer4, 0);
        if (sampleCount <= 0 || sampleCount > MaxSampleCount)
            throw new InvalidDataException($"Invalid callstream sample count: {sampleCount}.");

        var jsonBuffer = new byte[jsonLength];
        await stream.ReadExactlyAsync(jsonBuffer, ct);
        var rawJson = Encoding.UTF8.GetString(jsonBuffer);
        var metadata = ParseMetadata(rawJson);

        var pcm = new byte[sampleCount * 2];
        await stream.ReadExactlyAsync(pcm, ct);
        return new CallstreamPayload(metadata, rawJson, pcm, sampleRate);
    }

    public static CallstreamMetadata ParseMetadata(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var start = RequiredInt64(root, "StartTime");
        var stop = RequiredInt64(root, "StopTime");
        var system = RequiredString(root, "SystemShortName");
        var callId = RequiredInt64(root, "CallId");
        var talkgroup = RequiredInt64(root, "Talkgroup");
        var source = OptionalInt32(root, "Source", -1);
        var frequency = OptionalDouble(root, "Frequency", 0);
        return new CallstreamMetadata(start, stop, system, callId, talkgroup, source, frequency);
    }

    private static long RequiredInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !TryReadInt64(value, out var parsed))
            throw new InvalidDataException($"Callstream metadata is missing required numeric field '{name}'.");
        return parsed;
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new InvalidDataException($"Callstream metadata is missing required text field '{name}'.");
        var text = value.GetString()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException($"Callstream metadata field '{name}' is empty.");
        return text;
    }

    private static int OptionalInt32(JsonElement root, string name, int fallback)
    {
        if (!root.TryGetProperty(name, out var value) || !TryReadInt64(value, out var parsed))
            return fallback;
        return parsed is < int.MinValue or > int.MaxValue ? fallback : (int)parsed;
    }

    private static double OptionalDouble(JsonElement root, string name, double fallback)
    {
        if (!root.TryGetProperty(name, out var value))
            return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var parsed) => parsed,
            JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static bool TryReadInt64(JsonElement value, out long parsed)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.TryGetInt64(out parsed);
        if (value.ValueKind == JsonValueKind.String)
            return long.TryParse(value.GetString(), out parsed);
        parsed = 0;
        return false;
    }
}
