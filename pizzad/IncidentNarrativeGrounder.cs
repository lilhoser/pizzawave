using System.Text.RegularExpressions;

namespace pizzad;

public sealed record GroundedIncidentNarrative(string Title, string Detail, bool WasRewritten, string Reason, bool HasSpecificEvidence);

public static class IncidentNarrativeGrounder
{
    private const int TitleLimit = 120;
    private const int DetailLimit = 420;

    private static readonly Regex GenericTrafficControlTitlePattern = new(
        @"^\s*(?:traffic control|traffic incident|traffic assistance|traffic detail)(?:\s+(?:on|at|near)\s+(?:street|road|scene))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> StopTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "that", "this", "with", "from", "near", "into", "onto", "over",
        "under", "unit", "units", "respond", "responding", "response", "scene", "call", "calls",
        "dispatch", "dispatched", "reported", "reports", "reporting", "person", "party", "male",
        "female", "patient", "north", "south", "east", "west", "northeast", "northwest", "southeast",
        "southwest", "road", "rd", "street", "st", "drive", "dr", "avenue", "ave", "lane", "ln",
        "pike", "highway", "hwy", "place", "pl", "circle", "cir", "trail", "way", "court", "ct",
        "parkway", "pkwy", "boulevard", "blvd", "terrace", "ter"
    };

    public static GroundedIncidentNarrative Ground(
        string title,
        string detail,
        string category,
        IReadOnlyList<IncidentCandidateCall> calls)
    {
        var cleanTitle = Clean(title);
        var cleanDetail = Clean(detail);
        var evidence = Clean(string.Join(' ', calls.Select(c => c.Transcript)));
        var forceSpecificFallback = GenericTrafficControlTitlePattern.IsMatch(cleanTitle)
                                    && !string.IsNullOrWhiteSpace(SupportedFallbackPhrase(evidence));
        if (!forceSpecificFallback && IsNarrativeSupported(cleanTitle, cleanDetail, calls))
            return new(cleanTitle, cleanDetail, false, string.Empty, true);

        var location = ExtractRetainedCallLocation(calls);
        var phrase = SupportedFallbackPhrase(evidence);
        var hasSpecificEvidence = !string.IsNullOrWhiteSpace(phrase);
        if (string.IsNullOrWhiteSpace(phrase))
            phrase = $"{CategoryLabel(category, calls)} call";

        var fallbackTitle = AppendLocation(phrase, location);
        var fallbackDetail = BuildFallbackDetail(evidence, calls);
        return new(
            Trim(fallbackTitle, TitleLimit),
            Trim(fallbackDetail, DetailLimit),
            true,
            "rewrote unsupported model narrative from retained transcript evidence",
            hasSpecificEvidence);
    }

    public static bool IsNarrativeSupported(string title, string detail, IReadOnlyList<IncidentCandidateCall> calls)
    {
        if (calls.Count == 0)
            return false;

        var titleConcepts = ExtractConcepts(EventLead(title));
        if (titleConcepts.Count == 0)
            return true;

        var evidenceText = string.Join(' ', calls.Select(c => c.Transcript));
        if (ClaimsPositiveInjury(title, detail) && HasNegatedInjury(evidenceText) && !HasPositiveInjury(evidenceText))
            return false;
        if (!NarrativeLocationsSupported(title, detail, evidenceText))
            return false;

        var titleEventConcepts = ExtractEventConcepts(EventLead(title));
        var evidenceEventConcepts = ExtractEventConcepts(evidenceText);
        if (titleEventConcepts.Count > 0 && !titleEventConcepts.All(evidenceEventConcepts.Contains))
            return false;

        var evidenceConcepts = ExtractConcepts(evidenceText);
        var supported = titleConcepts.Count(concept => evidenceConcepts.Contains(concept));
        if (supported == titleConcepts.Count)
            return true;

        var ratio = supported / (double)titleConcepts.Count;
        if (ratio >= 0.50 && supported >= 1)
            return true;

        var detailConcepts = ExtractConcepts(EventLead(detail));
        return detailConcepts.Count > 0 && detailConcepts.All(concept => evidenceConcepts.Contains(concept));
    }

    private static HashSet<string> ExtractConcepts(string text)
    {
        var normalized = Clean(text).ToLowerInvariant();
        var concepts = ExtractEventConcepts(normalized);

        foreach (Match match in Regex.Matches(normalized, @"[a-z0-9]{3,}"))
        {
            var token = NormalizeToken(match.Value);
            if (token.Length == 0 || StopTokens.Contains(token))
                continue;
            concepts.Add(token);
        }

        return concepts;
    }

    private static HashSet<string> ExtractEventConcepts(string text)
    {
        var normalized = Clean(text).ToLowerInvariant();
        return IncidentEvidenceClassifier
            .Analyze(normalized)
            .Concepts
            .Where(concept => !string.Equals(concept, "injury", StringComparison.OrdinalIgnoreCase) || HasPositiveInjury(normalized))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string EventLead(string text)
    {
        var value = Clean(text);
        var split = Regex.Match(value, @"^(?<lead>.+?)\s+(?:at|near|on)\s+\d|^(?<lead>.+?)\s+(?:at|near|on)\s+[a-z0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (split.Success)
            return split.Groups["lead"].Value;

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(Math.Min(words.Length, 9)));
    }

    private static string SupportedFallbackPhrase(string evidence)
    {
        return IncidentEvidenceClassifier.Analyze(evidence).FallbackPhrase;
    }

    private static string BuildFallbackDetail(string evidence, IReadOnlyList<IncidentCandidateCall> calls)
    {
        var snippet = Clean(evidence);
        if (snippet.Length == 0)
            return "Retained calls describe a public-safety response.";

        var firstSentence = Regex.Split(snippet, @"(?<=[.!?])\s+")
            .Select(Clean)
            .FirstOrDefault(s => s.Length >= 20) ?? snippet;
        var label = CategoryLabel(calls.Select(c => c.Category).FirstOrDefault() ?? string.Empty, calls);
        return $"{label} dispatch audio reports: {Trim(firstSentence, 240)}";
    }

    private static bool NarrativeLocationsSupported(string title, string detail, string evidenceText)
    {
        foreach (var location in new[] { ExtractNarrativeLocation(title), ExtractNarrativeLocation(detail) }.Where(value => value.Length > 0))
        {
            if (IsGenericRoadwayPositionLocation(location))
                return false;
            if (Regex.IsMatch(location, @"^\d{1,6}\b", RegexOptions.CultureInvariant) &&
                !TranscriptLocationService.IsPlausibleLocation(location))
                return false;

            if (!RequiresNarrativeLocationSupport(location))
                continue;

            var tokens = LocationSupportTokens(location);
            if (tokens.Count == 0)
                continue;

            var evidence = NormalizeLocationSupportText(evidenceText);
            if (!tokens.Any(token => Regex.IsMatch(evidence, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                return false;
        }

        return true;
    }

    private static bool RequiresNarrativeLocationSupport(string location)
    {
        location ??= string.Empty;
        if (location.Contains('/'))
            return true;
        var normalized = NormalizeLocationSupportText(location);
        var suffixCount = Regex.Matches(
                normalized,
                @"\b(?:street|st|road|rd|drive|dr|avenue|ave|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|circle|cir|court|ct|way|trail|trl|parkway|pkwy|terrace|ter)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Count;
        return suffixCount >= 2;
    }

    private static string ExtractNarrativeLocation(string source)
    {
        var match = Regex.Match(source ?? string.Empty, @"\b(?:at|near|on)\s+(?<location>[A-Z0-9][A-Za-z0-9'./ -]{4,90})", RegexOptions.CultureInvariant);
        return match.Success ? CleanLocation(match.Groups["location"].Value) : string.Empty;
    }

    private static string ExtractRetainedCallLocation(IReadOnlyList<IncidentCandidateCall> calls)
    {
        foreach (var location in calls.SelectMany(c => TranscriptLocationService.ExtractLocations(c.Transcript)))
        {
            var clean = CleanLocation(location);
            if (IsGenericRoadwayPositionLocation(clean))
                continue;
            if (clean.Length > 0)
                return clean;
        }

        return string.Empty;
    }

    private static bool IsGenericRoadwayPositionLocation(string location)
    {
        var normalized = NormalizeLocationSupportText(location);
        if (Regex.IsMatch(normalized, @"\b(?:interstate|highway|hwy|state route|sr|us)\s+\d{1,3}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        return Regex.IsMatch(
            normalized,
            @"\b(?:left|right|middle|center)(?:\s+hand)?\s+(?:side\s+of\s+(?:the\s+)?road|lane|line|shoulder)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static HashSet<string> LocationSupportTokens(string location)
    {
        var normalized = NormalizeLocationSupportText(location);
        var stop = new HashSet<string>(StopTokens, StringComparer.OrdinalIgnoreCase)
        {
            "apartment", "apt", "unit", "block", "mile", "marker", "mm", "exit", "interstate",
            "highway", "hwy", "route", "northbound", "southbound", "eastbound", "westbound"
        };
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(normalized, @"[a-z0-9]{2,}"))
        {
            var token = NormalizeToken(match.Value);
            if (stop.Contains(token))
                continue;
            if (token.All(char.IsDigit) && token.Length < 2)
                continue;
            if (!token.All(char.IsDigit) && token.Length < 4)
                continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private static string NormalizeLocationSupportText(string text)
    {
        var value = Clean(text).ToLowerInvariant();
        value = Regex.Replace(value, @"\b(i|interstate)\s*[- ]?\s*(\d{1,3})\b", "interstate $2");
        value = Regex.Replace(value, @"\b(hwy|highway)\s+(\d{1,3})\b", "highway $2");
        value = Regex.Replace(value, @"\bln\b", "lane");
        value = Regex.Replace(value, @"\brd\b", "road");
        value = Regex.Replace(value, @"\bst\b", "street");
        return Regex.Replace(value, @"[^a-z0-9]+", " ");
    }

    private static string AppendLocation(string phrase, string location) =>
        string.IsNullOrWhiteSpace(location) ? phrase : $"{phrase} at {location}";

    private static string CategoryLabel(string category, IReadOnlyList<IncidentCandidateCall> calls)
    {
        var value = string.IsNullOrWhiteSpace(category)
            ? calls.Select(c => c.Category).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) ?? string.Empty
            : category;

        return value.Trim().ToLowerInvariant() switch
        {
            "ems" => "EMS",
            "fire" => "Fire",
            "police" => "Police",
            "traffic" => "Traffic",
            "public_works" => "Public works",
            "utilities" => "Utilities",
            _ => "Public safety"
        };
    }

    private static string NormalizeToken(string token)
    {
        var value = token.Trim().ToLowerInvariant();
        if (value.EndsWith("ies", StringComparison.Ordinal) && value.Length > 4)
            return value[..^3] + "y";
        if (value.EndsWith("ing", StringComparison.Ordinal) && value.Length > 5)
            return value[..^3];
        if (value.EndsWith("ed", StringComparison.Ordinal) && value.Length > 4)
            return value[..^2];
        if (value.EndsWith("s", StringComparison.Ordinal) && value.Length > 4 && !value.EndsWith("ous", StringComparison.Ordinal) && !value.EndsWith("ss", StringComparison.Ordinal))
            return value[..^1];
        return value;
    }

    private static bool ClaimsPositiveInjury(string title, string detail) =>
        HasPositiveInjury($"{EventLead(title)} {EventLead(detail)}");

    private static bool HasPositiveInjury(string text)
    {
        var value = Clean(text).ToLowerInvariant();
        foreach (Match match in Regex.Matches(value, @"\b(?:injury|injuries|injured|bleeding|laceration)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            if (match.Value.Contains("bleed", StringComparison.OrdinalIgnoreCase) || match.Value.Contains("laceration", StringComparison.OrdinalIgnoreCase))
                return true;

            var start = Math.Max(0, match.Index - 24);
            var prefix = value[start..match.Index];
            if (!Regex.IsMatch(prefix, @"\b(?:no|none|negative|unknown|without|obvious)\b\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                return true;
        }

        return false;
    }

    private static bool HasNegatedInjury(string text) =>
        Regex.IsMatch(text ?? string.Empty, @"\b(?:no|none|negative|without|unknown|no obvious)\s+(?:injury|injuries|injured)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string CleanLocation(string value)
    {
        var clean = Clean(value).Trim(' ', '.', ',', ';', ':');
        clean = Regex.Replace(clean, @"\b(?:for|with|where|caller|rp|time|timeout)\b.*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Trim();
        return Trim(clean, 90);
    }

    private static string Clean(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string Trim(string value, int max)
    {
        var clean = Clean(value);
        if (clean.Length <= max)
            return clean;
        return clean[..max].Trim().TrimEnd(',', ';', ':') + ".";
    }
}
