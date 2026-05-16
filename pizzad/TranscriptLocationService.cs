using System.Globalization;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class TranscriptLocationService
{
    private const string DirectionalSuffixPattern = @"(?:\s+(?:n|s|e|w|ne|nw|se|sw|north|south|east|west|northeast|northwest|southeast|southwest))?";
    private static readonly Regex AddressStreetRegex = new(@"\b\d{1,5}\s+(?:[a-z0-9]+\.?\s+){0,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy)" + DirectionalSuffixPattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StreetRegex = new(@"\b(?:[a-z]+\.?\s+){1,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy)" + DirectionalSuffixPattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HighwayRegex = new(@"\b(?:i[-\s]?\d{1,3}|interstate\s+\d{1,3}|us\s+\d{1,3}|hwy\s+\d{1,3}|highway\s+\d{1,3}|sr\s+\d{1,3}|state\s+route\s+\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LocationSuffixRegex = new(@"\b(street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> LocationStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "radio traffic", "main channel", "control channel", "dispatch road", "signal road", "unknown road",
        "middle of the road", "the middle of the road", "side of the road", "on the road",
        "by the way", "the way", "the road", "county road", "street court"
    };
    private readonly EngineConfig _config;

    public TranscriptLocationService(EngineConfig config)
    {
        _config = config;
    }

    public IReadOnlyList<CallLocationRecord> ExtractCallLocations(EngineCall call)
    {
        if (string.IsNullOrWhiteSpace(call.Transcription))
            return [];

        var area = ResolveArea(call.SystemShortName, _config.Locations.MonitoredAreas);
        if (area == null)
            return [];

        return ExtractLocations(call.Transcription)
            .Select(location => new CallLocationRecord(
                call.Id,
                area.AreaId,
                area.AreaLabel,
                area.SystemShortName,
                location,
                NormalizeLocationKey(location),
                GeocodingService.CacheKey(area.AreaId, location),
                "transcription"))
            .Where(r => !string.IsNullOrWhiteSpace(r.NormalizedKey))
            .DistinctBy(r => $"{r.AreaId}|{r.NormalizedKey}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static MonitoredAreaConfig? ResolveArea(string systemShortName, IReadOnlyList<MonitoredAreaConfig> areas)
    {
        if (string.IsNullOrWhiteSpace(systemShortName))
            return null;

        var system = systemShortName.Trim();
        return areas.FirstOrDefault(area =>
            string.Equals(area.SystemShortName, system, StringComparison.OrdinalIgnoreCase) ||
            area.Aliases.Any(alias => string.Equals(alias, system, StringComparison.OrdinalIgnoreCase))) ??
            areas.FirstOrDefault(area => area.Aliases.Any(alias =>
                !string.IsNullOrWhiteSpace(alias) &&
                system.Contains(alias, StringComparison.OrdinalIgnoreCase)));
    }

    public MonitoredAreaConfig? ResolveAreaById(string areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
            return null;

        return _config.Locations.MonitoredAreas.FirstOrDefault(area =>
            string.Equals(area.AreaId, areaId, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<string> ExtractLocations(string? transcription)
    {
        if (string.IsNullOrWhiteSpace(transcription))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addressMatches = AddressStreetRegex.Matches(transcription).Cast<Match>()
            .Select(m => CleanLocationText(m.Value))
            .Where(IsPlausibleLocation)
            .ToList();
        foreach (var text in addressMatches
                     .Concat(HighwayRegex.Matches(transcription).Cast<Match>().Select(m => CleanLocationText(m.Value)).Where(IsPlausibleLocation))
                     .Concat(StreetRegex.Matches(transcription).Cast<Match>()
                         .Select(m => CleanLocationText(m.Value))
                         .Where(IsPlausibleLocation)
                         .Where(street => !addressMatches.Any(address => address.Contains(street, StringComparison.OrdinalIgnoreCase)))))
        {
            var key = NormalizeLocationKey(text);
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                yield return text;
        }
    }

    public static bool IsPlausibleLocation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (text.Length < 4 || LocationStopWords.Contains(text))
            return false;

        var key = NormalizeLocationKey(text);
        if (LocationStopWords.Contains(key))
            return false;
        if (key.Contains(" en route ", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(" in route ", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(" be in route ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ll be ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("we ll ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (key.StartsWith("i ") || key.StartsWith("interstate ") || key.StartsWith("us ") ||
            key.StartsWith("hwy ") || key.StartsWith("highway ") || key.StartsWith("sr ") ||
            key.StartsWith("state route "))
            return key.Any(char.IsDigit);

        if (!LocationSuffixRegex.IsMatch(text))
            return false;
        if (key.StartsWith("linux ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("emergency file ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsBareStreetType(key))
            return false;

        return true;
    }

    public static string CleanLocationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        text = Regex.Replace(text.Trim(), @"\s+", " ");
        text = Regex.Replace(text, @"^[,.;:\-\s]+|[,.;:\-\s]+$", "");
        text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        text = Regex.Replace(text, @"^(?:(?:And|At|On|Near|By|To|From|The)\s+)+", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bI\s+(\d+)\b", "I-$1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bUs\s+(\d+)\b", "US $1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bSr\s+(\d+)\b", "SR $1", RegexOptions.IgnoreCase);
        return text;
    }

    public static string NormalizeLocationKey(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
            return string.Empty;

        try
        {
            value = value.ToLowerInvariant();
            value = Regex.Replace(value, @"\b(street|st\.)\b", "st");
            value = Regex.Replace(value, @"\b(road|rd\.)\b", "rd");
            value = Regex.Replace(value, @"\b(avenue|ave\.)\b", "ave");
            value = Regex.Replace(value, @"\b(drive|dr\.)\b", "dr");
            value = Regex.Replace(value, @"\b(lane|ln\.)\b", "ln");
            value = Regex.Replace(value, @"\b(boulevard|blvd\.)\b", "blvd");
            value = Regex.Replace(value, @"\b(highway|hwy\.)\b", "hwy");
            value = Regex.Replace(value, @"\b(route|rte\.)\b", "rte");
            value = Regex.Replace(value, @"\b(north\s*east|northeast|ne)\b", "ne");
            value = Regex.Replace(value, @"\b(north\s*west|northwest|nw)\b", "nw");
            value = Regex.Replace(value, @"\b(south\s*east|southeast|se)\b", "se");
            value = Regex.Replace(value, @"\b(south\s*west|southwest|sw)\b", "sw");
            value = Regex.Replace(value, @"\b(north|n)\b", "n");
            value = Regex.Replace(value, @"\b(south|s)\b", "s");
            value = Regex.Replace(value, @"\b(east|e)\b", "e");
            value = Regex.Replace(value, @"\b(west|w)\b", "w");
            return Regex.Replace(value, @"[^a-z0-9]+", " ").Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsBareStreetType(string key)
    {
        var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return true;

        return tokens.All(token => IsStreetSuffixToken(token) || IsDirectionToken(token));
    }

    private static bool IsStreetSuffixToken(string token) =>
        token is "st" or "street" or "rd" or "road" or "ave" or "avenue" or "dr" or "drive" or
            "ln" or "lane" or "blvd" or "boulevard" or "hwy" or "highway" or "pike" or
            "pl" or "place" or "ct" or "court" or "way" or "cir" or "circle" or
            "ter" or "terrace" or "trl" or "trail" or "pkwy" or "parkway";

    private static bool IsDirectionToken(string token) =>
        token is "n" or "s" or "e" or "w" or "ne" or "nw" or "se" or "sw" or
            "north" or "south" or "east" or "west" or "northeast" or "northwest" or
            "southeast" or "southwest";
}

public sealed record CallLocationRecord(
    long CallId,
    string AreaId,
    string AreaLabel,
    string SystemShortName,
    string LocationText,
    string NormalizedKey,
    string GeocodeCacheKey,
    string Source);
