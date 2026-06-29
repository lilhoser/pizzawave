using System.Globalization;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class CallAnchorExtractionService
{
    private static readonly Regex AddressRegex = new(
        @"\b\d{2,6}\s*,?\s+(?:[a-z0-9]+\.?\s+){0,5}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)(?:\s*,?\s+(?:n|s|e|w|ne|nw|se|sw|north|south|east|west|northeast|northwest|southeast|southwest))?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SpokenSingleDigitAddressRegex = new(
        @"\b(?<number>one|two|three|four|five|six|seven|eight|nine)\s+(?<street>(?:[a-z0-9]+\.?\s+){1,5}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop)(?:\s+(?:n|s|e|w|ne|nw|se|sw|north|south|east|west|northeast|northwest|southeast|southwest))?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HighwayMileMarkerRegex = new(
        @"\b(?:(?<highway>i[-\s]?\d{1,3}|interstate\s+\d{1,3}|us\s+\d{1,3}|hwy\s+\d{1,3}|highway\s+\d{1,3}|sr\s+\d{1,3}|state\s+route\s+\d{1,3})\D{0,45}(?:(?:mile\s*marker|mm|exit)\s*(?<marker>\d{1,3}(?:\.\d{1,2})?)|(?<marker2>\d{1,3}(?:\.\d{1,2})?)\s*mile\s*marker|(?:at|near|around|by|before|past|south\s+of|north\s+of|right\s+before)\s+(?:the\s+)?(?<marker4>\d{1,3}(?:\.\d{1,2})?))|(?:(?:mile\s*marker|mm|exit)\s*(?<marker3>\d{1,3}(?:\.\d{1,2})?)\D{0,45}(?<highway2>i[-\s]?\d{1,3}|interstate\s+\d{1,3}|us\s+\d{1,3}|hwy\s+\d{1,3}|highway\s+\d{1,3}|sr\s+\d{1,3}|state\s+route\s+\d{1,3})))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MarkerBeforeHighwayRegex = new(
        @"\b(?<marker>\d{1,3}(?:\.\d{1,2})?)\s+(?<highway>i[-\s]?\d{1,3}|interstate\s+\d{1,3}|us\s+\d{1,3}|hwy\s+\d{1,3}|highway\s+\d{1,3}|sr\s+\d{1,3}|state\s+route\s+\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BareMarkerHighwayRegex = new(
        @"\b(?:near|at|around|to|the)\s+(?<marker>\d{1,3}(?:\.\d{1,2})?)\s*,?\s+(?<highway>\d{1,3})\s*(?:e(?:ast)?bound|w(?:est)?bound|n(?:orth)?bound|s(?:outh)?bound|eb|wb|nb|sb)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LandmarkRegex = new(
        @"\b(?:near|area\s+of|at|by|past|into|over\s+to|around)\s+(?:the\s+)?(?<name>[a-z][a-z0-9']*(?:\s+[a-z][a-z0-9']*){0,4}\s+(?:nursery|home\s+store|store|market|church|school|hospital|clinic|mall|restaurant|station|airport|bridge|park|cemetery|apartments?))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex IntersectionRegex = new(
        @"\b(?<left>(?:[a-z0-9]+\.?\s+){1,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop))\s+(?:and|&|at|/)\s+(?<right>(?:[a-z0-9]+\.?\s+){1,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        @"\b(?:tag|plate|registration|license)\s+(?:number\s+)?(?<tag>[a-z0-9]{2,8}(?:[-\s][a-z0-9]{1,8}){0,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyList<CallAnchorRecord> Extract(EngineCall call, IReadOnlyList<CallLocationRecord> locations)
    {
        var anchors = new List<CallAnchorRecord>();
        AddTranscriptAnchors(call.Id, call.Transcription, anchors);
        AddLocationAnchors(call.Id, locations, anchors);
        return anchors
            .Where(a => !string.IsNullOrWhiteSpace(a.Value))
            .DistinctBy(a => $"{a.Kind}|{a.Value}|{a.Source}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<CallAnchorRecord> ExtractTranscriptAnchors(long callId, string transcript)
    {
        var anchors = new List<CallAnchorRecord>();
        AddTranscriptAnchors(callId, transcript, anchors);
        return anchors
            .DistinctBy(a => $"{a.Kind}|{a.Value}|{a.Source}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddTranscriptAnchors(long callId, string transcript, List<CallAnchorRecord> anchors)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return;

        AddHighwayMarkerMatches(callId, transcript, HighwayMileMarkerRegex.Matches(transcript), anchors, requireInterstateContext: false);
        AddHighwayMarkerMatches(callId, transcript, MarkerBeforeHighwayRegex.Matches(transcript), anchors, requireInterstateContext: false);
        AddHighwayMarkerMatches(callId, transcript, BareMarkerHighwayRegex.Matches(transcript), anchors, requireInterstateContext: true);

        foreach (Match match in LandmarkRegex.Matches(transcript))
        {
            var display = CleanLandmark(match.Groups["name"].Value);
            var key = TranscriptLocationService.NormalizeLocationKey(display);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            anchors.Add(new CallAnchorRecord(
                callId,
                "business_or_landmark",
                key,
                display,
                "deterministic",
                0.82,
                "{}"));
        }

        foreach (Match match in IntersectionRegex.Matches(transcript))
        {
            var left = CleanAnchorLocation(match.Groups["left"].Value);
            var right = CleanAnchorLocation(match.Groups["right"].Value);
            var leftKey = TranscriptLocationService.NormalizeLocationKey(left);
            var rightKey = TranscriptLocationService.NormalizeLocationKey(right);
            if (string.IsNullOrWhiteSpace(leftKey) || string.IsNullOrWhiteSpace(rightKey) || leftKey == rightKey)
                continue;

            var ordered = new[] { leftKey, rightKey }.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            anchors.Add(new CallAnchorRecord(
                callId,
                "intersection",
                $"{ordered[0]}|{ordered[1]}",
                $"{left} / {right}",
                "deterministic_weak",
                0.62,
                """{"trust":"weak_hint","reason":"regex intersection from noisy transcript"}"""));
        }

        foreach (Match match in AddressRegex.Matches(transcript))
        {
            var display = CleanAddress(match.Value);
            var key = TranscriptLocationService.NormalizeLocationKey(display);
            if (string.IsNullOrWhiteSpace(key) || !TranscriptLocationService.IsPlausibleLocation(display))
                continue;

            anchors.Add(new CallAnchorRecord(
                callId,
                "address",
                key,
                display,
                "deterministic_weak",
                0.58,
                """{"trust":"weak_hint","reason":"regex address from noisy transcript"}"""));
        }

        foreach (Match match in SpokenSingleDigitAddressRegex.Matches(transcript))
        {
            var number = SpokenSingleDigitValue(match.Groups["number"].Value);
            if (number <= 0)
                continue;

            var display = CleanAddress($"{number} {match.Groups["street"].Value}");
            var key = TranscriptLocationService.NormalizeLocationKey(display);
            if (string.IsNullOrWhiteSpace(key) || !TranscriptLocationService.IsPlausibleLocation(display))
                continue;

            anchors.Add(new CallAnchorRecord(
                callId,
                "address",
                key,
                display,
                "deterministic_weak",
                0.58,
                """{"trust":"weak_hint","reason":"spoken single-digit address from transcript"}"""));
        }

        foreach (Match match in TagRegex.Matches(transcript))
        {
            var normalized = NormalizeTag(match.Groups["tag"].Value);
            if (normalized.Length < 3)
                continue;

            anchors.Add(new CallAnchorRecord(
                callId,
                "vehicle_tag",
                normalized,
                normalized.ToUpperInvariant(),
                "deterministic",
                0.85,
                "{}"));
        }
    }

    private static void AddLocationAnchors(long callId, IReadOnlyList<CallLocationRecord> locations, List<CallAnchorRecord> anchors)
    {
        foreach (var location in locations)
        {
            if (string.IsNullOrWhiteSpace(location.NormalizedKey))
                continue;

            anchors.Add(new CallAnchorRecord(
                callId,
                "location",
                $"{location.AreaId}|{location.NormalizedKey}",
                location.LocationText,
                "location_hint",
                0.55,
                """{"trust":"weak_hint","reason":"transcript-derived location candidate"}"""));
        }
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

    private static string CleanAddress(string value)
    {
        value = Regex.Replace(value ?? string.Empty, @"\s+(?:at|and|&|/)\s+.*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return CleanAnchorLocation(value);
    }

    private static int SpokenSingleDigitValue(string value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            _ => 0
        };

    private static string CleanAnchorLocation(string value)
    {
        value = Regex.Replace(value ?? string.Empty, @"^(?:respond(?:ing)?\s+to|start(?:ing)?\s+to|send(?:ing)?\s+to|go(?:ing)?\s+to|units?\s+(?:to|at)|engine\s+to|deputy\s+to)\s+", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var firstDigit = value.IndexOfAny("0123456789".ToCharArray());
        if (firstDigit > 0)
            value = value[firstDigit..];
        return TranscriptLocationService.CleanLocationText(value);
    }

    private static string CleanLandmark(string value)
    {
        value = Regex.Replace(value ?? string.Empty, @".*\b(?:near|area\s+of|at|by|past|into|over\s+to|around)\s+(?:the\s+)?", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return TranscriptLocationService.CleanLocationText(value);
    }

    private static void AddHighwayMarkerMatches(long callId, string transcript, MatchCollection matches, List<CallAnchorRecord> anchors, bool requireInterstateContext)
    {
        foreach (Match match in matches)
        {
            if (requireInterstateContext && !HasInterstateContext(transcript, match))
                continue;

            var highway = FirstGroup(match, "highway", "highway2");
            var marker = FirstGroup(match, "marker", "marker2", "marker3", "marker4");
            var normalizedHighway = NormalizeHighway(highway);
            var normalizedMarker = NormalizeMarker(marker);
            if (string.IsNullOrWhiteSpace(normalizedHighway) || string.IsNullOrWhiteSpace(normalizedMarker))
                continue;

            AddHighwayMarkerAnchors(callId, normalizedHighway, normalizedMarker, anchors);
        }
    }

    private static bool HasInterstateContext(string transcript, Match match)
    {
        var start = Math.Max(0, match.Index - 80);
        var length = Math.Min(transcript.Length - start, match.Length + 160);
        var context = transcript.Substring(start, length);
        return Regex.IsMatch(context, @"\b(?:interstate|i[-\s]?\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void AddHighwayMarkerAnchors(long callId, string normalizedHighway, string normalizedMarker, List<CallAnchorRecord> anchors)
    {
        anchors.Add(new CallAnchorRecord(
            callId,
            "highway_mile_marker",
            $"{normalizedHighway}|mm:{normalizedMarker}",
            $"{DisplayHighway(normalizedHighway)} MM {normalizedMarker}",
            "deterministic",
            0.98,
            "{}"));

        var rounded = RoundedMarker(normalizedMarker);
        if (!string.IsNullOrWhiteSpace(rounded) && !string.Equals(rounded, normalizedMarker, StringComparison.OrdinalIgnoreCase))
        {
            anchors.Add(new CallAnchorRecord(
                callId,
                "highway_mile_marker",
                $"{normalizedHighway}|mm:{rounded}",
                $"{DisplayHighway(normalizedHighway)} MM {rounded}",
                "deterministic",
                0.94,
                """{"derived":"rounded_marker"}"""));
        }
    }

    private static string NormalizeHighway(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\binterstate\s+(\d{1,3})\b", "i $1");
        normalized = Regex.Replace(normalized, @"\bi\s+(\d{1,3})\b", "i-$1");
        if (Regex.IsMatch(normalized, @"^\d{1,3}$", RegexOptions.CultureInvariant))
            normalized = $"i-{normalized}";
        normalized = Regex.Replace(normalized, @"\bhwy\s+(\d{1,3})\b", "highway $1");
        normalized = Regex.Replace(normalized, @"\bsr\s+(\d{1,3})\b", "state route $1");
        return Regex.Replace(normalized, @"\s+", " ");
    }

    private static string DisplayHighway(string normalized) =>
        normalized.StartsWith("i-", StringComparison.OrdinalIgnoreCase)
            ? normalized.ToUpperInvariant()
            : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized);

    private static string NormalizeMarker(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var marker))
            return string.Empty;
        return marker % 1 == 0
            ? ((int)marker).ToString(CultureInfo.InvariantCulture)
            : marker.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static string RoundedMarker(string value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var marker))
            return string.Empty;
        return Math.Truncate(marker).ToString("0", CultureInfo.InvariantCulture);
    }

    private static string NormalizeTag(string value) =>
        Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
}

public sealed record CallAnchorRecord(
    long CallId,
    string Kind,
    string Value,
    string DisplayText,
    string Source,
    double Confidence,
    string DetailsJson);
