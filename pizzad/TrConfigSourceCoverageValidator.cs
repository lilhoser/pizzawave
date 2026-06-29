using System.Text.Json;

namespace pizzad;

public static class TrConfigSourceCoverageValidator
{
    private const double TrUsableHalfBandwidthFactor = 0.46875;

    public static TrConfigSourceCoverageValidation Validate(string configJson)
    {
        using var doc = JsonDocument.Parse(configJson);
        var root = doc.RootElement;
        var sources = ReadSources(root).ToList();
        var systems = ReadSystems(root).ToList();
        var blockers = new List<string>();

        if (sources.Count == 0)
            blockers.Add("TR config has no sources.");
        if (systems.Count == 0)
            blockers.Add("TR config has no systems.");

        foreach (var source in sources)
        {
            if (source.SampleRate <= 0)
                blockers.Add($"Source {source.Index} has no positive sample rate.");
        }

        foreach (var system in systems)
        {
            if (system.ControlChannelsHz.Count == 0)
            {
                blockers.Add($"System {system.ShortName} has no control channels.");
                continue;
            }

            foreach (var controlChannel in system.ControlChannelsHz)
            {
                if (sources.Any(source => Covers(source, controlChannel)))
                    continue;

                var nearest = sources
                    .OrderBy(source => DistanceToWindow(source, controlChannel))
                    .FirstOrDefault();
                blockers.Add(nearest == null
                    ? $"System {system.ShortName} control channel {FormatHz(controlChannel)} is not covered because no source is configured."
                    : $"System {system.ShortName} control channel {FormatHz(controlChannel)} is outside all source windows. Nearest source {nearest.Index} usable TR window is {FormatHz(LowHz(nearest))}-{FormatHz(HighHz(nearest))}.");
            }
        }

        return new TrConfigSourceCoverageValidation(
            blockers.Count == 0,
            blockers,
            sources.Select(source => new TrConfigSourceCoverageSource(
                source.Index,
                source.Device,
                source.Driver,
                source.CenterHz,
                source.SampleRate,
                LowHz(source),
                HighHz(source))).ToList(),
            systems);
    }

    private static IEnumerable<TrConfigSourceCoverageSourceRaw> ReadSources(JsonElement root)
    {
        if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            yield break;

        var index = 0;
        foreach (var source in sources.EnumerateArray())
        {
            yield return new TrConfigSourceCoverageSourceRaw(
                index++,
                ReadString(source, "device"),
                ReadString(source, "driver"),
                ReadFrequencyHz(source, "center"),
                ReadInt(source, "rate"));
        }
    }

    private static IEnumerable<TrConfigSourceCoverageSystem> ReadSystems(JsonElement root)
    {
        if (!root.TryGetProperty("systems", out var systems) || systems.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var system in systems.EnumerateArray())
        {
            var shortName = ReadString(system, "shortName");
            if (string.IsNullOrWhiteSpace(shortName))
                shortName = ReadString(system, "name");
            var controlChannels = new List<long>();
            if (system.TryGetProperty("control_channels", out var channels) && channels.ValueKind == JsonValueKind.Array)
            {
                foreach (var channel in channels.EnumerateArray())
                {
                    var hz = ReadFrequencyHz(channel);
                    if (hz > 0)
                        controlChannels.Add(hz);
                }
            }
            yield return new TrConfigSourceCoverageSystem(string.IsNullOrWhiteSpace(shortName) ? "<unnamed>" : shortName, controlChannels.Distinct().ToList());
        }
    }

    private static bool Covers(TrConfigSourceCoverageSourceRaw source, long frequencyHz) =>
        source.SampleRate > 0 && frequencyHz >= LowHz(source) && frequencyHz <= HighHz(source);

    private static long LowHz(TrConfigSourceCoverageSourceRaw source) =>
        source.CenterHz - TrUsableHalfBandwidthHz(source.SampleRate);

    private static long HighHz(TrConfigSourceCoverageSourceRaw source) =>
        source.CenterHz + TrUsableHalfBandwidthHz(source.SampleRate);

    private static long TrUsableHalfBandwidthHz(int sampleRate) =>
        (long)Math.Floor(Math.Max(0, sampleRate) * TrUsableHalfBandwidthFactor);

    private static long DistanceToWindow(TrConfigSourceCoverageSourceRaw source, long frequencyHz)
    {
        var low = LowHz(source);
        var high = HighHz(source);
        if (frequencyHz < low)
            return low - frequencyHz;
        if (frequencyHz > high)
            return frequencyHz - high;
        return 0;
    }

    private static string ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static long ReadFrequencyHz(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? ReadFrequencyHz(value) : 0;

    private static long ReadFrequencyHz(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value))
            return ToHz(value);
        if (element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out var textValue))
            return ToHz(textValue);
        return 0;
    }

    private static long ToHz(double value) =>
        value > 0 && value < 1_000_000 ? (long)Math.Round(value * 1_000_000) : (long)Math.Round(value);

    private static string FormatHz(long value) =>
        value >= 1_000_000 ? $"{value / 1_000_000d:F6} MHz" : $"{value:N0} Hz";

    private sealed record TrConfigSourceCoverageSourceRaw(
        int Index,
        string Device,
        string Driver,
        long CenterHz,
        int SampleRate);
}

public sealed record TrConfigSourceCoverageValidation(
    bool Ok,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<TrConfigSourceCoverageSource> Sources,
    IReadOnlyList<TrConfigSourceCoverageSystem> Systems);

public sealed record TrConfigSourceCoverageSource(
    int Index,
    string Device,
    string Driver,
    long CenterHz,
    int SampleRate,
    long LowHz,
    long HighHz);

public sealed record TrConfigSourceCoverageSystem(
    string ShortName,
    IReadOnlyList<long> ControlChannelsHz);
