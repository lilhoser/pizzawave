using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed record CallstreamMetadata(
    int SchemaVersion,
    long StartTime,
    long StopTime,
    long StartTimeMs,
    long StopTimeMs,
    string SystemShortName,
    long CallId,
    long Talkgroup,
    int SystemNumber,
    double Frequency,
    int SampleRate,
    string AudioMappingStatus,
    IReadOnlyList<long> PatchedTalkgroups,
    IReadOnlyList<CallstreamTransmission> Transmissions,
    string ChannelAssignmentStart = "unknown",
    bool BeginsChannelAssignment = false,
    long? PossiblyIncompleteTransmissionStartTimeMs = null);

public sealed record CallstreamTransmission(
    long? SourceId,
    string SourceIdProvenance,
    long Talkgroup,
    long StartTimeMs,
    long StopTimeMs,
    int? StartSample,
    int SampleCount,
    double Frequency,
    int TdmaSlot,
    long ErrorCount,
    long SpikeCount,
    string StartStatus = "unknown");

public sealed class CallstreamPayload
{
    private const int PizzaMagic = 0x415A5A50; // pzza
    private const long MaxJsonLength = 1024 * 1024;
    private const int MaxSampleCount = 0xfffffe;
    private const int MaxTransmissionCount = 4096;

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
        var metadata = ParseMetadata(rawJson, sampleCount, sampleRate);

        var pcm = new byte[sampleCount * 2];
        await stream.ReadExactlyAsync(pcm, ct);
        return new CallstreamPayload(metadata, rawJson, pcm, metadata.SampleRate);
    }

    public static CallstreamMetadata ParseMetadata(string rawJson, int pcmSampleCount = 0, int configuredSampleRate = 8000)
    {
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;
        var schemaVersion = OptionalInt32(root, "SchemaVersion", 1);
        if (schemaVersion is < 1 or > 3)
            throw new InvalidDataException($"Unsupported callstream schema version: {schemaVersion}.");
        var start = RequiredInt64(root, "StartTime");
        var stop = RequiredInt64(root, "StopTime");
        var system = RequiredString(root, "SystemShortName");
        var callId = RequiredInt64(root, "CallId");
        var talkgroup = RequiredInt64(root, "Talkgroup");
        var systemNumber = schemaVersion >= 2
            ? RequiredInt32(root, "SystemNumber")
            : OptionalInt32(root, "Source", -1);
        var frequency = OptionalDouble(root, "Frequency", 0);
        var startMs = OptionalInt64(root, "StartTimeMs", checked(start * 1000));
        var stopMs = OptionalInt64(root, "StopTimeMs", checked(stop * 1000));
        var sampleRate = OptionalInt32(root, "SampleRate", configuredSampleRate);
        if (sampleRate <= 0)
            throw new InvalidDataException("Callstream metadata has an invalid sample rate.");
        var patchedTalkgroups = ReadInt64Array(root, "PatchedTalkgroups");
        var mappingStatus = schemaVersion >= 2
            ? RequiredString(root, "AudioMappingStatus")
            : "legacy_unavailable";
        var channelAssignmentStart = schemaVersion >= 3
            ? RequiredString(root, "ChannelAssignmentStart")
            : "unknown";
        var beginsChannelAssignment = schemaVersion >= 3 && RequiredBoolean(root, "BeginsChannelAssignment");
        var possiblyIncompleteStartTimeMs = schemaVersion >= 3
            ? RequiredNullablePositiveInt64(root, "PossiblyIncompleteTransmissionStartTimeMs")
            : null;
        var transmissions = schemaVersion >= 2
            ? ReadTransmissions(root, schemaVersion)
            : [];
        if (schemaVersion >= 2)
            ValidateTransmissionContract(schemaVersion, transmissions, mappingStatus, channelAssignmentStart,
                beginsChannelAssignment, possiblyIncompleteStartTimeMs, pcmSampleCount);
        return new CallstreamMetadata(schemaVersion, start, stop, startMs, stopMs, system, callId, talkgroup,
            systemNumber, frequency, sampleRate, mappingStatus, patchedTalkgroups, transmissions,
            channelAssignmentStart, beginsChannelAssignment, possiblyIncompleteStartTimeMs);
    }

    private static IReadOnlyList<CallstreamTransmission> ReadTransmissions(JsonElement root, int schemaVersion)
    {
        if (!root.TryGetProperty("Transmissions", out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Callstream version 2 metadata is missing the Transmissions array.");
        if (value.GetArrayLength() > MaxTransmissionCount)
            throw new InvalidDataException($"Callstream metadata contains too many transmissions: {value.GetArrayLength()}.");

        var rows = new List<CallstreamTransmission>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Callstream transmission metadata contains a non-object value.");
            var sourceId = OptionalPositiveInt64(item, "SourceId");
            var provenance = RequiredString(item, "SourceIdProvenance");
            var startStatus = schemaVersion >= 3 ? RequiredString(item, "StartStatus") : "unknown";
            var talkgroup = RequiredInt64(item, "Talkgroup");
            var startMs = RequiredInt64(item, "StartTimeMs");
            var stopMs = RequiredInt64(item, "StopTimeMs");
            var startSample = OptionalNullableInt32(item, "StartSample");
            var sampleCount = RequiredInt32(item, "SampleCount");
            var frequency = OptionalDouble(item, "Frequency", 0);
            var tdmaSlot = OptionalInt32(item, "TdmaSlot", 0);
            var errorCount = OptionalInt64(item, "ErrorCount", 0);
            var spikeCount = OptionalInt64(item, "SpikeCount", 0);
            if (stopMs < startMs || sampleCount <= 0 || errorCount < 0 || spikeCount < 0)
                throw new InvalidDataException("Callstream transmission metadata contains an invalid time, sample, or decoder-quality value.");
            rows.Add(new(sourceId, provenance, talkgroup, startMs, stopMs, startSample, sampleCount,
                frequency, tdmaSlot, errorCount, spikeCount, startStatus));
        }
        return rows;
    }

    private static void ValidateTransmissionContract(
        int schemaVersion,
        IReadOnlyList<CallstreamTransmission> transmissions,
        string mappingStatus,
        string channelAssignmentStart,
        bool beginsChannelAssignment,
        long? possiblyIncompleteStartTimeMs,
        int pcmSampleCount)
    {
        if (transmissions.Count == 0)
            throw new InvalidDataException("Callstream version 2 metadata contains no transmissions.");
        if (schemaVersion >= 3)
        {
            if (channelAssignmentStart is not ("grant" or "update"))
                throw new InvalidDataException($"Callstream metadata has an unknown channel-assignment start: {channelAssignmentStart}.");
            if (beginsChannelAssignment != (channelAssignmentStart == "grant"))
                throw new InvalidDataException("Callstream channel-assignment start fields disagree.");
            if (channelAssignmentStart == "grant" && possiblyIncompleteStartTimeMs.HasValue)
                throw new InvalidDataException("A grant-started call cannot declare a possibly incomplete transmission.");
            if (channelAssignmentStart == "update" && !possiblyIncompleteStartTimeMs.HasValue)
                throw new InvalidDataException("An update-started call must identify its possibly incomplete original transmission.");
            for (var index = 0; index < transmissions.Count; index++)
            {
                var expected = possiblyIncompleteStartTimeMs == transmissions[index].StartTimeMs
                    ? "possibly_incomplete"
                    : "observed_boundary";
                if (!string.Equals(transmissions[index].StartStatus, expected, StringComparison.Ordinal))
                    throw new InvalidDataException($"Callstream transmission {index} has start status '{transmissions[index].StartStatus}', expected '{expected}'.");
            }
        }
        var exact = mappingStatus is "exact_live" or "exact_reconstructed";
        if (!exact)
        {
            if (!string.Equals(mappingStatus, "unavailable", StringComparison.Ordinal))
                throw new InvalidDataException($"Callstream version 2 metadata has an unknown audio mapping status: {mappingStatus}.");
            if (transmissions.Any(row => row.StartSample.HasValue))
                throw new InvalidDataException("Callstream unavailable audio mapping must not contain sample offsets.");
            return;
        }

        long expectedStart = 0;
        foreach (var row in transmissions)
        {
            if (row.StartSample != expectedStart)
                throw new InvalidDataException("Callstream transmission sample ranges are not contiguous and ordered.");
            expectedStart += row.SampleCount;
            if (expectedStart > MaxSampleCount)
                throw new InvalidDataException("Callstream transmission sample ranges exceed the payload limit.");
        }
        if (pcmSampleCount > 0 && expectedStart != pcmSampleCount)
            throw new InvalidDataException($"Callstream transmission sample coverage is {expectedStart}, but the payload contains {pcmSampleCount} samples.");
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

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Callstream metadata is missing required boolean field '{name}'.");
        return value.GetBoolean();
    }

    private static int RequiredInt32(JsonElement root, string name)
    {
        var value = RequiredInt64(root, name);
        if (value is < int.MinValue or > int.MaxValue)
            throw new InvalidDataException($"Callstream metadata numeric field '{name}' is outside the supported range.");
        return (int)value;
    }

    private static long OptionalInt64(JsonElement root, string name, long fallback) =>
        root.TryGetProperty(name, out var value) && TryReadInt64(value, out var parsed) ? parsed : fallback;

    private static long? OptionalNullableInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (!TryReadInt64(value, out var parsed))
            throw new InvalidDataException($"Callstream metadata field '{name}' is not a valid integer or null.");
        return parsed;
    }

    private static long? OptionalPositiveInt64(JsonElement root, string name)
    {
        var value = OptionalNullableInt64(root, name);
        return value > 0 ? value : null;
    }

    private static long? RequiredNullablePositiveInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            throw new InvalidDataException($"Callstream metadata is missing required field '{name}'.");
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        if (!TryReadInt64(value, out var parsed) || parsed <= 0)
            throw new InvalidDataException($"Callstream metadata field '{name}' is not a positive integer or null.");
        return parsed;
    }

    private static int? OptionalNullableInt32(JsonElement root, string name)
    {
        var value = OptionalNullableInt64(root, name);
        if (!value.HasValue)
            return null;
        if (value.Value is < int.MinValue or > int.MaxValue)
            throw new InvalidDataException($"Callstream metadata field '{name}' is outside the supported range.");
        return (int)value.Value;
    }

    private static IReadOnlyList<long> ReadInt64Array(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return [];
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Callstream metadata field '{name}' is not an array.");
        var rows = new List<long>(value.GetArrayLength());
        foreach (var item in value.EnumerateArray())
        {
            if (!TryReadInt64(item, out var parsed))
                throw new InvalidDataException($"Callstream metadata field '{name}' contains a non-integer value.");
            rows.Add(parsed);
        }
        return rows;
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
