using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed partial class SetupCalibrationService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger<SetupCalibrationService> _logger;

    public SetupCalibrationService(EngineConfig config, EngineDatabase database, ILogger<SetupCalibrationService> logger)
    {
        _config = config;
        _database = database;
        _logger = logger;
    }

    public SetupCalibrationPlanDto BuildPlan()
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new SetupCalibrationPlanDto([], [], ["TR config file was not found. Complete the TR config step first."], "No calibration plan available.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var sampleSources = ReadSources(root).ToList();
        var systems = ReadSystems(root).ToList();
        var warnings = new List<string>();

        if (sampleSources.Count == 0)
            warnings.Add("No SDR sources were found in the TR config.");
        if (systems.Count == 0)
            warnings.Add("No systems were found in the TR config.");

        var plans = new List<SetupCalibrationSystemPlanDto>();
        foreach (var system in systems)
        {
            var voiceFrequencies = system.VoiceFrequenciesHz.Count > 0
                ? system.VoiceFrequenciesHz
                : ReadObservedVoiceFrequencies(system.ShortName);
            var frequencies = voiceFrequencies.Count > 0
                ? system.ControlChannelsHz.Concat(voiceFrequencies).Distinct().Order().ToList()
                : system.ControlChannelsHz.Distinct().Order().ToList();
            var ranges = CalculateRequiredRanges(frequencies, sampleSources.FirstOrDefault()?.SampleRate ?? 2_400_000);
            var proposed = ranges.Select(range => BestSourceForRange(range, sampleSources)).ToList();
            var systemWarnings = new List<string>();
            if (system.VoiceFrequenciesHz.Count == 0 && voiceFrequencies.Count > 0)
                systemWarnings.Add($"TR config has no voice channel list for this system; calibration is using {voiceFrequencies.Count} observed call frequenc{(voiceFrequencies.Count == 1 ? "y" : "ies")} from PizzaWave history.");
            if (voiceFrequencies.Count == 0 && systems.Count == 1 && sampleSources.Count > proposed.Where(p => p != null).DistinctBy(p => p!.Index).Count())
            {
                ranges = sampleSources.Select(SourceWindowRange).ToList();
                proposed = sampleSources.Cast<TrSource?>().ToList();
                systemWarnings.Add("No voice channel list was found in the TR system config, so calibration is using all configured SDR source windows for this single system.");
            }
            if (frequencies.Count == 0)
                systemWarnings.Add("No control channels were found for this system. Add control_channels to the TR config before calibration.");
            if (proposed.Any(p => p == null))
            {
                var missing = ranges.Where((_, i) => proposed[i] == null).Select(FormatRange).ToList();
                systemWarnings.Add($"Coverage missing: no configured SDR source covers {string.Join(", ", missing)}. Add another SDR source or adjust source center/rate before tuning this system.");
            }
            if (ranges.Count > sampleSources.Count)
                systemWarnings.Add($"This system appears to need {ranges.Count} SDR tuner(s), but only {sampleSources.Count} source(s) are configured. Add SDR hardware or reduce selected site coverage.");

            plans.Add(new SetupCalibrationSystemPlanDto(
                system.ShortName,
                system.Modulation,
                system.ControlChannelsHz,
                voiceFrequencies,
                ranges.Select(r => new SetupCalibrationFrequencyRangeDto(r.LowHz, r.HighHz, r.CenterHz)).ToList(),
                ranges.Count,
                proposed.Where(p => p != null).Select(p => p!).DistinctBy(p => p.Index).Select(p => p.Index).ToList(),
                systemWarnings));
        }

        var sourceDtos = sampleSources.Select(source =>
        {
            var coveredSystems = plans
                .Where(plan => plan.ProposedSourceIndexes.Contains(source.Index))
                .Select(plan => plan.ShortName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new SetupCalibrationSourcePlanDto(
                source.Index,
                source.Serial,
                source.Device,
                source.CenterHz,
                source.SampleRate,
                source.ErrorHz,
                source.Gain,
                coveredSystems);
        }).ToList();

        var diagnostics = $"Built calibration plan for {plans.Count} system(s) and {sourceDtos.Count} configured SDR source(s).";
        return new SetupCalibrationPlanDto(plans, sourceDtos, warnings, diagnostics);
    }

    private static IEnumerable<TrSource> ReadSources(JsonElement root)
    {
        if (!root.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Array)
            yield break;

        var index = 0;
        foreach (var source in sources.EnumerateArray())
        {
            var center = ReadLong(source, "center");
            var rate = (int)ReadLong(source, "rate");
            var error = (int)ReadHzOffset(source, "error");
            var gain = ReadStringOrNumber(source, "gain");
            var device = source.TryGetProperty("device", out var deviceElement) ? deviceElement.GetString() ?? string.Empty : string.Empty;
            var serial = ExtractSdrSerial(device);
            if (center > 0 && rate > 0)
                yield return new TrSource(index, serial, device, center, rate, error, gain);
            index++;
        }
    }

    private static IEnumerable<TrSystem> ReadSystems(JsonElement root)
    {
        if (!root.TryGetProperty("systems", out var systems) || systems.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var system in systems.EnumerateArray())
        {
            var shortName = system.TryGetProperty("shortName", out var shortNameElement) ? shortNameElement.GetString() ?? "system" : "system";
            var modulation = system.TryGetProperty("modulation", out var modulationElement) ? modulationElement.GetString() ?? "qpsk" : "qpsk";
            var controls = ReadFrequencyArray(system, "control_channels");
            var voice = ReadFrequencyArray(system, "channels")
                .Concat(ReadFrequencyArray(system, "frequencies"))
                .Distinct()
                .Order()
                .ToList();
            yield return new TrSystem(shortName, modulation, controls, voice);
        }
    }

    private IReadOnlyList<long> ReadObservedVoiceFrequencies(string systemShortName)
    {
        if (string.IsNullOrWhiteSpace(systemShortName))
            return [];
        try
        {
            using var connection = _database.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT frequency
                FROM calls
                WHERE system_short_name = $system
                  AND frequency > 0
                ORDER BY frequency;
                """;
            command.Parameters.AddWithValue("$system", systemShortName);
            using var reader = command.ExecuteReader();
            var frequencies = new List<long>();
            while (reader.Read())
            {
                var value = reader.GetDouble(0);
                if (value > 0)
                    frequencies.Add(NormalizeFrequency((long)Math.Round(value)));
            }
            return frequencies.Distinct().Order().ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read observed call frequencies for {SystemShortName}", systemShortName);
            return [];
        }
    }

    private static List<FrequencyRange> CalculateRequiredRanges(IReadOnlyList<long> frequencies, int sampleRate)
    {
        if (frequencies.Count == 0)
            return [];
        var usableWidth = Math.Max(100_000, (long)(sampleRate * 0.90));
        var sorted = frequencies.Order().ToList();
        var ranges = new List<FrequencyRange>();
        var current = new List<long>();
        foreach (var frequency in sorted)
        {
            if (current.Count == 0 || frequency - current[0] <= usableWidth)
            {
                current.Add(frequency);
                continue;
            }
            ranges.Add(ToRange(current));
            current = [frequency];
        }
        if (current.Count > 0)
            ranges.Add(ToRange(current));
        return ranges;
    }

    private static FrequencyRange ToRange(IReadOnlyList<long> values)
    {
        var low = values.Min();
        var high = values.Max();
        return new FrequencyRange(low, high, (low + high) / 2);
    }

    private static TrSource? BestSourceForRange(FrequencyRange range, IReadOnlyList<TrSource> sources)
    {
        return sources
            .Select(source => new { Source = source, Score = SourceRangeScore(source, range) })
            .OrderByDescending(row => row.Score)
            .ThenBy(row => Math.Abs(row.Source.CenterHz - range.CenterHz))
            .FirstOrDefault(row => row.Score > 0)?.Source;
    }

    private static long SourceRangeScore(TrSource source, FrequencyRange range)
    {
        var sourceRange = SourceWindowRange(source);
        var low = sourceRange.LowHz;
        var high = sourceRange.HighHz;
        var overlap = Math.Min(high, range.HighHz) - Math.Max(low, range.LowHz);
        return overlap >= 0 ? overlap + 1 : 0;
    }

    private static FrequencyRange SourceWindowRange(TrSource source)
    {
        var half = source.SampleRate / 2;
        return new FrequencyRange(source.CenterHz - half, source.CenterHz + half, source.CenterHz);
    }

    private static List<long> ReadFrequencyArray(JsonElement system, string property)
    {
        if (!system.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array)
            return [];
        return values.EnumerateArray()
            .Select(ReadFrequency)
            .Where(v => v > 0)
            .Distinct()
            .Order()
            .ToList();
    }

    private static long ReadFrequency(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var longValue))
                return NormalizeFrequency(longValue);
            if (value.TryGetDouble(out var doubleValue))
                return NormalizeFrequency((long)Math.Round(doubleValue));
        }
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed))
            return NormalizeFrequency((long)Math.Round(parsed));
        return 0;
    }

    private static long NormalizeFrequency(long value) => value > 0 && value < 10_000 ? value * 1_000_000 : value;

    private static long ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        return ReadFrequency(value);
    }

    private static long ReadHzOffset(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt64(out var longValue))
                return longValue;
            if (value.TryGetDouble(out var doubleValue))
                return (long)Math.Round(doubleValue);
        }

        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? (long)Math.Round(parsed)
            : 0;
    }

    private static string ReadStringOrNumber(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            _ => string.Empty
        };
    }

    private static string ExtractSdrSerial(string device)
    {
        var match = SdrDeviceRegex().Match(device ?? string.Empty);
        return match.Success ? match.Groups["serial"].Value : string.Empty;
    }

    private static string FormatHz(long value)
    {
        if (value >= 1_000_000)
            return $"{value / 1_000_000d:0.######} MHz";
        if (value >= 1_000)
            return $"{value / 1_000d:0.#} kHz";
        return $"{value} Hz";
    }

    private static string FormatRange(FrequencyRange range) =>
        range.LowHz == range.HighHz ? FormatHz(range.LowHz) : $"{FormatHz(range.LowHz)} to {FormatHz(range.HighHz)}";

    [GeneratedRegex(@"(?:rtl|airspy)[=:](?<serial>[^,\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SdrDeviceRegex();

    private sealed record TrSource(int Index, string Serial, string Device, long CenterHz, int SampleRate, int ErrorHz, string Gain);
    private sealed record TrSystem(string ShortName, string Modulation, IReadOnlyList<long> ControlChannelsHz, IReadOnlyList<long> VoiceFrequenciesHz);
    private sealed record FrequencyRange(long LowHz, long HighHz, long CenterHz);
}

public sealed record SetupCalibrationPlanDto(
    IReadOnlyList<SetupCalibrationSystemPlanDto> Systems,
    IReadOnlyList<SetupCalibrationSourcePlanDto> Sources,
    IReadOnlyList<string> Warnings,
    string Diagnostics);

public sealed record SetupCalibrationSystemPlanDto(
    string ShortName,
    string Modulation,
    IReadOnlyList<long> ControlChannelsHz,
    IReadOnlyList<long> VoiceFrequenciesHz,
    IReadOnlyList<SetupCalibrationFrequencyRangeDto> RequiredRanges,
    int RequiredSdrCount,
    IReadOnlyList<int> ProposedSourceIndexes,
    IReadOnlyList<string> Warnings);

public sealed record SetupCalibrationFrequencyRangeDto(long LowHz, long HighHz, long CenterHz);

public sealed record SetupCalibrationSourcePlanDto(
    int Index,
    string Serial,
    string Device,
    long CenterFrequency,
    int SampleRate,
    int ErrorHz,
    string Gain,
    IReadOnlyList<string> CoveredSystems);

