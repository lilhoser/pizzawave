using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class TranscriptLocationService
{
    private const string DirectionalSuffixPattern = @"(?:\s+(?:n|s|e|w|ne|nw|se|sw|north|south|east|west|northeast|northwest|southeast|southwest))?";
    private static readonly Regex HighwayAddressRegex = new(@"\b\d{1,5}\s*,?\s+(?:[a-z0-9]+\.?\s+){0,4}(?:highway|hwy)\s+\d{1,3}" + DirectionalSuffixPattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AddressStreetRegex = new(@"\b\d{1,5}\s*,?\s+(?:[a-z0-9]+\.?\s+){0,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)(?:\s*,?\s+(?:n|s|e|w|ne|nw|se|sw|north|south|east|west|northeast|northwest|southeast|southwest))?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StreetRegex = new(@"\b(?:[a-z]+\.?\s+){1,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)" + DirectionalSuffixPattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HighwayRegex = new(@"\b(?:i[-\s]?\d{1,3}|interstate\s+\d{1,3}|us\s+\d{1,3}|hwy\s+\d{1,3}|highway\s+\d{1,3}|sr\s+\d{1,3}|state\s+route\s+\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HighwayMarkerRegex = new(@"\b(?:(?<marker>\d{1,3}(?:\.\d{1,2})?)\s+(?<highway>i[-\s]?\d{1,3}|interstate\s+\d{1,3})|(?<highway2>i[-\s]?\d{1,3}|interstate\s+\d{1,3})\D{0,45}(?:(?:mile\s*marker|mm|exit)\s*(?<marker2>\d{1,3}(?:\.\d{1,2})?)|(?:at|near|around|by|before|past|south\s+of|north\s+of|right\s+before)\s+(?:the\s+)?(?<marker5>\d{1,3}(?:\.\d{1,2})?))|(?:mile\s*marker|mm|exit)\s*(?<marker3>\d{1,3}(?:\.\d{1,2})?)\D{0,45}(?<highway3>i[-\s]?\d{1,3}|interstate\s+\d{1,3})|(?:near|at|around|to|the)\s+(?<marker4>\d{1,3}(?:\.\d{1,2})?)\s*,?\s+(?<highway4>\d{1,3})\s*(?:e(?:ast)?bound|w(?:est)?bound|n(?:orth)?bound|s(?:outh)?bound|eb|wb|nb|sb))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LandmarkRegex = new(@"\b(?:near|area\s+of|at|by|past|into|over\s+to|around)\s+(?:the\s+)?(?<name>[a-z][a-z0-9']*(?:\s+[a-z][a-z0-9']*){0,4}\s+(?:nursery|home\s+store|store|market|church|school|hospital|clinic|mall|restaurant|station|airport|bridge|park|cemetery|apartments?))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LocationSuffixRegex = new(@"\b(street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LandmarkSuffixRegex = new(@"\b(nursery|home\s+store|store|market|church|school|hospital|clinic|mall|restaurant|station|airport|bridge|park|cemetery|apartments?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> LocationStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "radio traffic", "main channel", "control channel", "dispatch road", "signal road", "unknown road",
        "middle of the road", "the middle of the road", "side of the road", "on the road",
        "by the way", "the way", "the road", "county road", "street court"
    };
    private readonly EngineConfig _config;
    private readonly TalkgroupCatalogService _talkgroups;
    private readonly ConcurrentDictionary<string, MonitoredAreaConfig> _derivedAreas = new(StringComparer.OrdinalIgnoreCase);

    public TranscriptLocationService(EngineConfig config, TalkgroupCatalogService talkgroups)
    {
        _config = config;
        _talkgroups = talkgroups;
    }

    public IReadOnlyList<CallLocationRecord> ExtractCallLocations(EngineCall call)
    {
        if (string.IsNullOrWhiteSpace(call.Transcription))
            return [];

        var area = ResolveCallArea(call);
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
        return areas.Where(area => area.IsOverride).FirstOrDefault(area =>
            string.Equals(area.SystemShortName, system, StringComparison.OrdinalIgnoreCase) ||
            area.Aliases.Any(alias => string.Equals(alias, system, StringComparison.OrdinalIgnoreCase))) ??
            areas.Where(area => area.IsOverride).FirstOrDefault(area => area.Aliases.Any(alias =>
                !string.IsNullOrWhiteSpace(alias) &&
                system.Contains(alias, StringComparison.OrdinalIgnoreCase))) ??
            areas.FirstOrDefault(area => HasUsableBounds(area) && AreaMatchesSystem(area, system, system));
    }

    private MonitoredAreaConfig? ResolveCallArea(EngineCall call)
    {
        var catalog = _talkgroups.Resolve(call.SystemShortName, call.Talkgroup);
        var system = (_config.SiteSetup.Systems ?? []).FirstOrDefault(value =>
            string.Equals(value.ShortName, call.SystemShortName, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(catalog.SystemShortName) && string.Equals(value.TalkgroupSystemShortName, catalog.SystemShortName, StringComparison.OrdinalIgnoreCase)));
        var catalogSystem = string.IsNullOrWhiteSpace(catalog.SystemShortName)
            ? string.IsNullOrWhiteSpace(system?.TalkgroupSystemShortName) ? call.SystemShortName : system.TalkgroupSystemShortName
            : catalog.SystemShortName;
        var label = !string.IsNullOrWhiteSpace(catalog.Jurisdiction)
            ? catalog.Jurisdiction.Trim()
            : !string.IsNullOrWhiteSpace(system?.SiteLabel) ? system.SiteLabel.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(label))
            return ResolveArea(call.SystemShortName, _config.Locations.MonitoredAreas);
        var contextKey = !string.IsNullOrWhiteSpace(catalog.Jurisdiction)
            ? $"rr:{catalogSystem}:{label}"
            : $"site:{system?.ShortName ?? call.SystemShortName}:{label}";
        var explicitOverride = _config.Locations.MonitoredAreas.FirstOrDefault(area =>
            area.IsOverride && string.Equals(area.ContextKey, contextKey, StringComparison.OrdinalIgnoreCase))
            ?? _config.Locations.MonitoredAreas.FirstOrDefault(area =>
                area.IsOverride && string.IsNullOrWhiteSpace(area.ContextKey) && AreaMatchesSystem(area, call.SystemShortName, catalogSystem));
        if (explicitOverride != null)
            return explicitOverride;

        var compatibleConfiguredArea = _config.Locations.MonitoredAreas.FirstOrDefault(area =>
            !area.IsOverride &&
            HasUsableBounds(area) &&
            AreaMatchesSystem(area, call.SystemShortName, catalogSystem) &&
            AreaLabelMatchesContext(area.AreaLabel, label));
        if (compatibleConfiguredArea != null)
            return compatibleConfiguredArea;

        var id = DerivedAreaId(contextKey);
        return _derivedAreas.GetOrAdd(id, _ => new MonitoredAreaConfig
        {
            AreaId = id,
            AreaLabel = label,
            SystemShortName = call.SystemShortName,
            North = 85,
            South = -85,
            West = -180,
            East = 180,
            Aliases = new[] { call.SystemShortName, catalogSystem }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ContextKey = contextKey,
            IsOverride = false
        });
    }

    private static bool AreaMatchesSystem(MonitoredAreaConfig area, string siteSystem, string catalogSystem) =>
        string.Equals(area.SystemShortName, siteSystem, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(area.SystemShortName, catalogSystem, StringComparison.OrdinalIgnoreCase) ||
        area.Aliases.Any(alias => string.Equals(alias, siteSystem, StringComparison.OrdinalIgnoreCase) || string.Equals(alias, catalogSystem, StringComparison.OrdinalIgnoreCase));

    private static bool HasUsableBounds(MonitoredAreaConfig area) =>
        area.North is > -90 and <= 90 &&
        area.South is >= -90 and < 90 &&
        area.West is >= -180 and < 180 &&
        area.East is > -180 and <= 180 &&
        area.North > area.South &&
        area.East > area.West;

    private static bool AreaLabelMatchesContext(string areaLabel, string contextLabel)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "county", "city", "site", "simulcast", "mississippi", "ms", "tennessee", "tn"
        };
        var areaTokens = LabelTokens(areaLabel).Where(token => !ignored.Contains(token)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextTokens = LabelTokens(contextLabel).Where(token => !ignored.Contains(token)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return areaTokens.Overlaps(contextTokens);
    }

    private static IEnumerable<string> LabelTokens(string value) =>
        Regex.Matches(value ?? string.Empty, @"[a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Select(match => match.Value)
            .Where(token => token.Length > 1);

    private static string DerivedAreaId(string contextKey)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contextKey.ToLowerInvariant()))).ToLowerInvariant();
        return $"rr-{hash[..16]}";
    }

    public MonitoredAreaConfig? ResolveAreaById(string areaId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
            return null;

        return _config.Locations.MonitoredAreas.FirstOrDefault(area =>
                   string.Equals(area.AreaId, areaId, StringComparison.OrdinalIgnoreCase))
               ?? (_derivedAreas.TryGetValue(areaId, out var derived) ? derived : null);
    }

    public static IEnumerable<string> ExtractLocations(string? transcription)
    {
        if (string.IsNullOrWhiteSpace(transcription))
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addressMatches = HighwayAddressRegex.Matches(transcription).Cast<Match>()
            .Concat(AddressStreetRegex.Matches(transcription).Cast<Match>())
            .Select(m => CleanLocationText(m.Value))
            .Where(IsPlausibleLocation)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var highwayMatches = HighwayRegex.Matches(transcription).Cast<Match>()
            .Where(match => !LooksLikePatientAgeHighwayFalsePositive(transcription, match))
            .Select(m => CleanLocationText(m.Value))
            .Where(IsPlausibleLocation);
        var highwayMarkerMatches = HighwayMarkerRegex.Matches(transcription).Cast<Match>()
            .Where(match => HasInterstateContext(transcription, match))
            .Select(HighwayMarkerLocation)
            .Where(IsPlausibleLocation);
        var landmarkMatches = LandmarkRegex.Matches(transcription).Cast<Match>()
            .Select(m => CleanLandmarkText(m.Groups["name"].Value))
            .Where(IsPlausibleLocation);
        foreach (var text in addressMatches
                     .Concat(highwayMarkerMatches)
                     .Concat(landmarkMatches)
                     .Concat(highwayMatches)
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

    private static bool LooksLikePatientAgeHighwayFalsePositive(string transcription, Match match)
    {
        var afterStart = Math.Min(transcription.Length, match.Index + match.Length);
        var afterLength = Math.Min(40, transcription.Length - afterStart);
        var after = afterLength <= 0 ? string.Empty : transcription.Substring(afterStart, afterLength);
        return Regex.IsMatch(after, @"^\s*(?:-|,)?\s*(?:year\s*old|yo|y/o|male|female|patient)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
        if (LooksLikeTrafficLanePosition(key))
            return false;

        if (key.Contains(" en route ", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(" in route ", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(" be in route ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ll be ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("we ll ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (LooksLikeDispatchCommandAddress(key))
            return false;
        if (key.StartsWith("i ") || key.StartsWith("interstate ") || key.StartsWith("us ") ||
            key.StartsWith("hwy ") || key.StartsWith("highway ") || key.StartsWith("sr ") ||
            key.StartsWith("state route "))
            return key.Any(char.IsDigit);

        if (!LocationSuffixRegex.IsMatch(text) && !LandmarkSuffixRegex.IsMatch(text))
            return false;
        if (key.StartsWith("linux ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("emergency file ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsBareStreetType(key))
            return false;

        return true;
    }

    private static bool LooksLikeDispatchCommandAddress(string key) =>
        Regex.IsMatch(
            key,
            @"^\d{1,6}\s+(?:continue|continued|copy|clear|cancel|route|respond(?:ing)?|show(?:ing)?)\s+(?:the\s+)?(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
        Regex.IsMatch(
            key,
            @"^(?:continue|continued|copy|clear|cancel|route|respond(?:ing)?|show(?:ing)?)\s+(?:the\s+)?(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool LooksLikeTrafficLanePosition(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = NormalizeLocationKey(part);
            if (Regex.IsMatch(
                    key,
                    @"^\d{1,6}\s+(?:(?:on|in|at)\s+(?:the\s+)?)?(?:left|right|middle|center)\s+(?:lane|ln|shoulder)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
            if (Regex.IsMatch(
                    key,
                    @"^\d{1,6}\s+(?:now\s+on|off)\s+(?:the\s+)?[a-z0-9 ]+\s+(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
            if (Regex.IsMatch(
                    key,
                    @"^\d{1,6}\s+(?:between|near|around|by|before|after|past|close\s+to|next\s+to|across\s+from)\s+(?:the\s+)?[a-z0-9 ]+\s+(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }

    private static bool HasInterstateContext(string transcription, Match match)
    {
        var start = Math.Max(0, match.Index - 80);
        var length = Math.Min(transcription.Length - start, match.Length + 160);
        var context = transcription.Substring(start, length);
        return Regex.IsMatch(context, @"\b(?:interstate|i[-\s]?\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string HighwayMarkerLocation(Match match)
    {
        var highway = FirstGroup(match, "highway", "highway2", "highway3", "highway4");
        var marker = FirstGroup(match, "marker", "marker2", "marker3", "marker4", "marker5");
        highway = NormalizeHighwayDisplay(highway);
        marker = NormalizeMarker(marker);
        return string.IsNullOrWhiteSpace(highway) || string.IsNullOrWhiteSpace(marker)
            ? string.Empty
            : $"{highway} MM {marker}";
    }

    private static string FirstGroup(Match match, params string[] names)
    {
        foreach (var name in names)
        {
            var group = match.Groups[name];
            if (group.Success && !string.IsNullOrWhiteSpace(group.Value))
                return group.Value;
        }
        return string.Empty;
    }

    private static string NormalizeHighwayDisplay(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\binterstate\s+(\d{1,3})\b", "i $1");
        normalized = Regex.Replace(normalized, @"\bi\s+(\d{1,3})\b", "i-$1");
        if (Regex.IsMatch(normalized, @"^\d{1,3}$", RegexOptions.CultureInvariant))
            normalized = $"i-{normalized}";
        return normalized.StartsWith("i-", StringComparison.OrdinalIgnoreCase)
            ? normalized.ToUpperInvariant()
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized);
    }

    private static string NormalizeMarker(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var marker))
            return string.Empty;
        return marker % 1 == 0
            ? ((int)marker).ToString(CultureInfo.InvariantCulture)
            : marker.ToString("0.##", CultureInfo.InvariantCulture);
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
        return TrimLeadingAddressNoise(text);
    }

    private static string TrimLeadingAddressNoise(string text)
    {
        var clean = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        var tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = tokens.Length - 1; i > 0; i--)
        {
            if (!tokens[i].All(char.IsDigit))
                continue;

            var candidate = string.Join(' ', tokens.Skip(i));
            if (LocationSuffixRegex.IsMatch(candidate) && IsPlausibleLocation(candidate))
                return candidate;
        }

        return clean;
    }

    private static string CleanLandmarkText(string? text)
    {
        var value = Regex.Replace(text ?? string.Empty, @".*\b(?:near|area\s+of|at|by|past|into|over\s+to|around)\s+(?:the\s+)?", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return CleanLocationText(value);
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
            "ter" or "terrace" or "trl" or "trail" or "pkwy" or "parkway" or "loop";

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
