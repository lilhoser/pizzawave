using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace pizzad;

public static class TrRecorderCapacitySizer
{
    public const int MinimumDigitalRecorders = 4;
    public const int MaximumDigitalRecorders = 24;

    private const double TrUsableHalfBandwidthFactor = 0.46875;

    public static int EstimateDigitalRecorders(int coveredVoiceFrequencyCount, int coveredSystemCount)
    {
        if (coveredVoiceFrequencyCount <= 0)
            return MinimumDigitalRecorders;

        var burstHeadroom = Math.Max(1, coveredSystemCount) * 2;
        return Math.Clamp(RoundUpEven(coveredVoiceFrequencyCount + burstHeadroom), MinimumDigitalRecorders, MaximumDigitalRecorders);
    }

    public static int EstimateForSetupSource(SetupTrConfigSourceDto source, IReadOnlyList<SetupTrConfigSystemDto> systems)
    {
        var controlChannels = systems
            .SelectMany(system => system.ControlChannelsMhz)
            .Select(MhzToHz)
            .ToHashSet();
        var covered = source.CoveredFrequenciesMhz
            .Select(MhzToHz)
            .Where(value => value > 0)
            .Distinct()
            .ToList();
        var coveredVoiceCount = covered.Count(value => !controlChannels.Contains(value));
        var coveredSystemCount = systems.Count(system =>
            system.FrequenciesMhz.Any(frequency => covered.Contains(MhzToHz(frequency))) ||
            system.ControlChannelsMhz.Any(frequency => covered.Contains(MhzToHz(frequency))));
        return EstimateDigitalRecorders(coveredVoiceCount, coveredSystemCount);
    }

    public static void EnsureJsonConfigRecorderCapacity(JsonObject root, List<string> changes)
    {
        if (root["sources"] is not JsonArray sources)
            return;
        var systems = (root["systems"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

        for (var index = 0; index < sources.Count; index++)
        {
            if (sources[index] is not JsonObject source)
                continue;

            var sourceInfo = ReadSource(source);
            var coveredVoice = new HashSet<long>();
            var coveredSystems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var system in systems)
            {
                var shortName = ReadString(system, "shortName");
                if (string.IsNullOrWhiteSpace(shortName))
                    shortName = $"system-{coveredSystems.Count + 1}";

                var systemCovered = false;
                foreach (var voice in ReadFrequencyArray(system, "channels", "voiceFrequencies", "voiceFrequenciesHz"))
                {
                    if (!Covers(sourceInfo, voice))
                        continue;
                    coveredVoice.Add(voice);
                    systemCovered = true;
                }

                if (!systemCovered)
                    systemCovered = ReadFrequencyArray(system, "control_channels", "controlChannels", "controlChannelsHz")
                        .Any(controlChannel => Covers(sourceInfo, controlChannel));

                if (systemCovered)
                    coveredSystems.Add(shortName);
            }

            var target = EstimateDigitalRecorders(coveredVoice.Count, coveredSystems.Count);
            EnsureJsonSourceDigitalRecorders(source, target, $"source {index}", changes);
        }
    }

    public static void EnsureJsonSourceDigitalRecorders(JsonObject source, int target, string scope, List<string>? changes = null)
    {
        target = Math.Clamp(target, MinimumDigitalRecorders, MaximumDigitalRecorders);
        var current = ReadInt(source, "digitalRecorders");
        if (current >= target)
            return;
        source["digitalRecorders"] = target;
        changes?.Add($"{scope} digitalRecorders: {(current > 0 ? current.ToString() : "null")} -> {target}");
    }

    private static int RoundUpEven(int value) =>
        value % 2 == 0 ? value : value + 1;

    private static SourceInfo ReadSource(JsonObject source) =>
        new(ReadFrequencyHz(source, "center"), ReadInt(source, "rate"));

    private static bool Covers(SourceInfo source, long frequencyHz) =>
        source.SampleRate > 0 && frequencyHz >= source.LowHz && frequencyHz <= source.HighHz;

    private static long TrUsableHalfBandwidthHz(int sampleRate) =>
        (long)Math.Floor(Math.Max(0, sampleRate) * TrUsableHalfBandwidthFactor);

    private static IEnumerable<long> ReadFrequencyArray(JsonObject root, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (root[propertyName] is not JsonArray values)
                continue;
            foreach (var value in values)
            {
                var frequency = ReadFrequencyHz(value);
                if (frequency > 0)
                    yield return frequency;
            }
        }
    }

    private static string ReadString(JsonObject root, string propertyName) =>
        root[propertyName] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;

    private static int ReadInt(JsonObject root, string propertyName) =>
        root[propertyName] is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : 0;

    private static long ReadFrequencyHz(JsonObject root, string propertyName) =>
        ReadFrequencyHz(root[propertyName]);

    private static long ReadFrequencyHz(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
                return ToHz(longValue);
            if (value.TryGetValue<double>(out var doubleValue))
                return ToHz(doubleValue);
            if (value.TryGetValue<string>(out var textValue) && double.TryParse(textValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return ToHz(parsed);
        }
        return 0;
    }

    private static long MhzToHz(double value) =>
        (long)Math.Round(value * 1_000_000d);

    private static long ToHz(double value) =>
        value > 0 && value < 1_000_000 ? (long)Math.Round(value * 1_000_000) : (long)Math.Round(value);

    private sealed record SourceInfo(long CenterHz, int SampleRate)
    {
        public long LowHz => CenterHz - TrUsableHalfBandwidthHz(SampleRate);
        public long HighHz => CenterHz + TrUsableHalfBandwidthHz(SampleRate);
    }
}
