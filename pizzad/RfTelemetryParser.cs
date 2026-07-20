using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad;

public static class RfTelemetryParser
{
    private const string CallstreamMarker = "PIZZAWAVE_RF ";
    private const string TrMarker = "TR_RF ";
    private static readonly DateTime MinimumTimestampUtc = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan RetuneNarrativeBucket = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<RfTelemetryEventDto> ParseJournal(string journal, out int rejected)
    {
        rejected = 0;
        var events = new List<RfTelemetryEventDto>();
        foreach (var line in journal.Split('\n'))
        {
            var hasMarker = line.Contains(CallstreamMarker, StringComparison.Ordinal)
                || line.Contains(TrMarker, StringComparison.Ordinal);
            if (!hasMarker)
                continue;

            if (TryParseLine(line, out var parsed))
                events.Add(parsed!);
            else
                rejected++;
        }
        return events;
    }

    public static IReadOnlyList<RfTelemetryEventDto> CollapseForPersistence(IReadOnlyList<RfTelemetryEventDto> events)
    {
        var ordinary = events.Where(row => row.EventType != "control_channel_retune");
        var representativeRetunes = events
            .Where(row => row.EventType == "control_channel_retune")
            .GroupBy(row => (
                System: row.SystemShortName.ToUpperInvariant(),
                Reason: row.Reason.ToUpperInvariant(),
                row.PreviousControlChannelHz,
                row.RequestedControlChannelHz,
                row.Success,
                Bucket: row.TimestampUtc.Ticks / RetuneNarrativeBucket.Ticks))
            .Select(group => group.OrderBy(row => row.TimestampUtc).First());
        return ordinary.Concat(representativeRetunes).OrderBy(row => row.TimestampUtc).ToList();
    }

    public static bool TryParseLine(string line, out RfTelemetryEventDto? parsed)
    {
        parsed = null;
        var marker = line.Contains(TrMarker, StringComparison.Ordinal) ? TrMarker : CallstreamMarker;
        var markerIndex = line.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        var rawJson = line[(markerIndex + marker.Length)..].Trim();
        if (rawJson.Length is 0 or > 16384)
            return false;

        try
        {
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || RequiredInt(root, "schemaVersion") is not 1
                || RequiredString(root, "event", 64) is not { } eventType
                || RequiredLong(root, "timestampUnixMs") is not { } timestampUnixMs
                || RequiredString(root, "systemShortName", 200) is not { } systemShortName
                || RequiredString(root, "systemType", 100) is not { } systemType)
                return false;

            var expectedMarker = eventType == "control_channel_retune" ? TrMarker : CallstreamMarker;
            if (!string.Equals(marker, expectedMarker, StringComparison.Ordinal)
                || eventType is not ("rf_sample" or "control_channel_retune" or "control_channel_reacquired"))
                return false;

            DateTime timestampUtc;
            try
            {
                timestampUtc = DateTimeOffset.FromUnixTimeMilliseconds(timestampUnixMs).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            if (timestampUtc < MinimumTimestampUtc || timestampUtc > DateTime.UtcNow.AddMinutes(5))
                return false;

            if (eventType == "control_channel_retune")
            {
                if (RequiredString(root, "reason", 100) is not { } reason
                    || reason is not ("low_decode" or "tdulc")
                    || RequiredNumber(root, "previousControlChannelHz") is null
                    || RequiredNumber(root, "requestedControlChannelHz") is null
                    || RequiredBoolean(root, "success") is null)
                    return false;
            }
            else if (RequiredNumber(root, "controlChannelHz") is null
                || RequiredNumber(root, "decodeRate") is null
                || RequiredInt(root, "sourceIndex") is null)
            {
                return false;
            }

            if (eventType == "rf_sample" && RequiredNumber(root, "sampleWindowSeconds") is null)
                return false;
            if (eventType == "control_channel_reacquired" && RequiredNumber(root, "lowDecodeSeconds") is null)
                return false;

            parsed = new RfTelemetryEventDto
            {
                EventKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))).ToLowerInvariant(),
                SchemaVersion = 1,
                EventType = eventType,
                TimestampUtc = timestampUtc,
                SystemShortName = systemShortName,
                SystemType = systemType,
                ControlChannelHz = OptionalNumber(root, "controlChannelHz"),
                DecodeRate = OptionalNumber(root, "decodeRate"),
                FrequencyErrorHz = OptionalNumber(root, "frequencyErrorHz"),
                LowDecodeSeconds = OptionalNumber(root, "lowDecodeSeconds"),
                SampleWindowSeconds = OptionalNumber(root, "sampleWindowSeconds"),
                SourceIndex = OptionalInt(root, "sourceIndex"),
                SourceCenterHz = OptionalNumber(root, "sourceCenterHz"),
                SourceSampleRate = OptionalNumber(root, "sourceSampleRate"),
                SourceErrorHz = OptionalNumber(root, "sourceErrorHz"),
                SourceDriver = OptionalString(root, "sourceDriver", 200),
                SourceDevice = OptionalString(root, "sourceDevice", 1000),
                Reason = OptionalString(root, "reason", 100),
                PreviousControlChannelHz = OptionalNumber(root, "previousControlChannelHz"),
                RequestedControlChannelHz = OptionalNumber(root, "requestedControlChannelHz"),
                FrequencyErrorBeforeRetuneHz = OptionalNumber(root, "frequencyErrorBeforeRetuneHz"),
                PreviousSourceIndex = OptionalInt(root, "previousSourceIndex"),
                PreviousSourceCenterHz = OptionalNumber(root, "previousSourceCenterHz"),
                SelectedSourceIndex = OptionalInt(root, "selectedSourceIndex"),
                SelectedSourceCenterHz = OptionalNumber(root, "selectedSourceCenterHz"),
                SelectedSourceSampleRate = OptionalNumber(root, "selectedSourceSampleRate"),
                SelectedSourceErrorHz = OptionalNumber(root, "selectedSourceErrorHz"),
                SelectedSourceDriver = OptionalString(root, "selectedSourceDriver", 200),
                SelectedSourceDevice = OptionalString(root, "selectedSourceDevice", 1000),
                Success = OptionalBoolean(root, "success"),
                RawJson = rawJson
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? RequiredString(JsonElement root, string name, int maxLength)
    {
        var value = OptionalString(root, name, maxLength);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string OptionalString(JsonElement root, string name, int maxLength)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return string.Empty;
        var text = (value.GetString() ?? string.Empty).Trim();
        return text.Length <= maxLength ? text : string.Empty;
    }

    private static long? RequiredLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) ? parsed : null;

    private static int? RequiredInt(JsonElement root, string name) => OptionalInt(root, name);

    private static int? OptionalInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;

    private static double? RequiredNumber(JsonElement root, string name) => OptionalNumber(root, name);

    private static double? OptionalNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var parsed)
            || !double.IsFinite(parsed))
            return null;
        return parsed;
    }

    private static bool? RequiredBoolean(JsonElement root, string name) => OptionalBoolean(root, name);

    private static bool? OptionalBoolean(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
