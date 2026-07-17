using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public static class TranscriptionCandidateAnalyzer
{
    public const string ScoringVersion = "transcription-candidate-v1";

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex LocationPhraseRegex = new(
        @"\b(?:at|near|on|onto|to|from|by|around|area\s+of|intersection\s+of|cross\s+of|in\s+the\s+area\s+of)\s+(?:the\s+)?(?<place>[a-z0-9']+(?:\s+[a-z0-9']+){0,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StreetSuffixFragmentRegex = new(
        @"\b(?<place>[a-z0-9']+(?:\s+[a-z0-9']+){0,4}\s+(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy|loop))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SymbolEvidenceNameRegex = new(
        "(symbol|symbols|symbolstream|imbe|ambe|dvcf|codec)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (string Label, Regex Pattern, int Weight)[] SeverityPatterns =
    [
        ("entrapment", WordRegex("entrapment|entrapped|trapped|pin(?:ned)?\\s+in|extrication"), 38),
        ("road closure", WordRegex("road\\s+closed|road\\s+closure|shut\\s+down|shutdown|blocked|blockage|all\\s+lanes|traffic\\s+blocked"), 34),
        ("serious wreck", WordRegex("wreck|crash|accident|rollover|overturned|vehicle\\s+fire|head\\s+on"), 28),
        ("injury", WordRegex("injur(?:y|ies|ed)|unconscious|not\\s+breathing|bleeding|trauma|fatal(?:ity)?"), 28),
        ("fire", WordRegex("structure\\s+fire|working\\s+fire|smoke\\s+showing|flames?|fire\\s+alarm|brush\\s+fire"), 26),
        ("hazard", WordRegex("wires?\\s+down|tree\\s+down|gas\\s+leak|hazmat|spill|debris|flood(?:ing)?"), 22),
        ("law emergency", WordRegex("shots?\\s+fired|shooting|stabbing|pursuit|robbery|burglary|assault|domestic"), 22),
        ("dispatch response", WordRegex("respond(?:ing)?|en\\s+route|stage|caller\\s+advises|units?\\s+respond"), 10)
    ];

    private static readonly HashSet<string> FragmentStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "for", "with", "of", "in", "on", "to", "from", "by",
        "this", "that", "there", "here", "you", "me", "us", "unit", "units", "caller", "call",
        "scene", "area", "roadway", "traffic", "reference", "respond", "responding", "route",
        "en route", "be advised", "stand by", "go ahead"
    };

    public static TranscriptionCandidateReportDto BuildReport(
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<IncidentDto> incidents,
        long start,
        long end,
        int limit,
        bool includeIncidentLinked)
    {
        var incidentCallIds = incidents
            .SelectMany(i => i.Calls.Select(c => c.CallId))
            .ToHashSet();

        var candidates = calls
            .Select(call => ScoreCall(call, incidents, incidentCallIds))
            .Where(candidate => candidate != null)
            .Cast<TranscriptionCandidateDto>()
            .Where(candidate => includeIncidentLinked || !candidate.IncidentLinked)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.StartTime)
            .Take(Math.Clamp(limit, 1, 500))
            .ToList();

        return new TranscriptionCandidateReportDto(
            start,
            end,
            calls.Count,
            candidates.Count,
            ScoringVersion,
            candidates);
    }

    private static TranscriptionCandidateDto? ScoreCall(
        EngineCall call,
        IReadOnlyList<IncidentDto> incidents,
        HashSet<long> incidentCallIds)
    {
        var transcript = Normalize(call.Transcription);
        var severitySignals = DetectSeveritySignals(transcript);
        var locationSignals = TranscriptLocationService.ExtractLocations(transcript).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var suspiciousFragments = DetectSuspiciousLocationFragments(transcript, locationSignals);
        var symbolEvidence = ExtractSymbolEvidence(call.RawMetadataJson);
        var incidentLinked = incidentCallIds.Contains(call.Id);
        var nearbyIncidentCount = CountNearbyIncidents(call, incidents);
        var reasons = new List<string>();

        var score = 0;
        if (severitySignals.Count > 0)
        {
            var severityScore = Math.Min(52, severitySignals.Sum(s => s.Weight));
            score += severityScore;
            reasons.Add($"severity language: {string.Join(", ", severitySignals.Select(s => s.Label).Distinct(StringComparer.OrdinalIgnoreCase))}");
        }

        if (locationSignals.Count > 0)
        {
            score += Math.Min(24, 12 + locationSignals.Count * 4);
            reasons.Add($"location extracted: {string.Join(", ", locationSignals.Take(3))}");
        }

        if (suspiciousFragments.Count > 0)
        {
            score += Math.Min(30, 14 + suspiciousFragments.Count * 5);
            reasons.Add($"location-like fragment needs review: {string.Join(", ", suspiciousFragments.Take(3))}");
        }

        if (!string.Equals(call.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
            reasons.Add($"transcription status is {call.TranscriptionStatus}");
        }

        if (!string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase))
        {
            score += 22;
            reasons.Add($"quality reason is {call.QualityReason}");
        }

        if (transcript.Length is > 0 and < 35 && severitySignals.Count > 0)
        {
            score += 14;
            reasons.Add("short transcript still contains incident language");
        }

        if (!incidentLinked && severitySignals.Count > 0)
        {
            score += 22;
            reasons.Add("not linked to an incident");
        }
        else if (incidentLinked && (suspiciousFragments.Count > 0 || !string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase)))
        {
            score += 10;
            reasons.Add("linked incident may have lost a fact");
        }

        if (nearbyIncidentCount > 0 && !incidentLinked)
        {
            score += 10;
            reasons.Add("near another incident but not attached");
        }

        if (symbolEvidence.Count > 0)
        {
            score += 5;
            reasons.Add("symbol/codec evidence metadata present");
        }

        if (string.IsNullOrWhiteSpace(call.AudioPath))
        {
            score -= 10;
            reasons.Add("audio path missing");
        }

        var hasOperationalSignal = severitySignals.Count > 0 ||
            !string.Equals(call.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase);
        var hasLocationQuestion = locationSignals.Count > 0 || suspiciousFragments.Count > 0;
        if (!hasOperationalSignal || (!hasLocationQuestion && severitySignals.Count == 0) || score < 45)
            return null;

        var severity = score >= 90 || severitySignals.Any(s => s.Weight >= 34)
            ? "critical"
            : score >= 72
                ? "high"
                : score >= 55
                    ? "medium"
                    : "low";

        return new TranscriptionCandidateDto(
            call.Id,
            call.StartTime,
            call.StopTime,
            call.SystemShortName,
            call.Talkgroup,
            string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"TG {call.Talkgroup}" : call.TalkgroupName,
            call.Category,
            call.TranscriptionStatus,
            call.QualityReason,
            Math.Max(0, score),
            severity,
            incidentLinked,
            nearbyIncidentCount,
            !string.IsNullOrWhiteSpace(call.AudioPath),
            symbolEvidence.Count > 0,
            $"/api/v1/calls/{call.Id}/audio",
            Preview(transcript),
            reasons,
            severitySignals.Select(s => s.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            locationSignals,
            suspiciousFragments,
            symbolEvidence);
    }

    private static List<(string Label, int Weight)> DetectSeveritySignals(string transcript)
    {
        var matches = new List<(string Label, int Weight)>();
        if (string.IsNullOrWhiteSpace(transcript))
            return matches;

        foreach (var pattern in SeverityPatterns)
        {
            if (pattern.Pattern.IsMatch(transcript))
                matches.Add((pattern.Label, pattern.Weight));
        }
        return matches;
    }

    private static List<string> DetectSuspiciousLocationFragments(string transcript, IReadOnlyList<string> acceptedLocations)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return [];

        var acceptedKeys = acceptedLocations
            .Select(TranscriptLocationService.NormalizeLocationKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return LocationPhraseRegex.Matches(transcript).Cast<Match>()
            .Concat(StreetSuffixFragmentRegex.Matches(transcript).Cast<Match>())
            .Select(match => CleanFragment(match.Groups["place"].Value))
            .Where(fragment => IsUsefulFragment(fragment, acceptedKeys))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static bool IsUsefulFragment(string fragment, HashSet<string> acceptedKeys)
    {
        if (string.IsNullOrWhiteSpace(fragment))
            return false;

        var key = TranscriptLocationService.NormalizeLocationKey(fragment);
        if (key.Length < 4 || acceptedKeys.Contains(key))
            return false;
        if (TranscriptLocationService.IsPlausibleLocation(fragment))
            return false;
        if (FragmentStopWords.Contains(key))
            return false;

        var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens.All(t => FragmentStopWords.Contains(t)))
            return false;
        if (tokens.Any(t => t.Length >= 4 && t.Any(char.IsDigit)))
            return true;
        if (tokens.Any(t => t.Length >= 5 && !FragmentStopWords.Contains(t)))
            return true;
        return tokens.Length >= 2 && tokens.Any(t => !FragmentStopWords.Contains(t));
    }

    private static string CleanFragment(string value)
    {
        value = Normalize(value);
        value = Regex.Replace(value, @"\b(?:for|with|reference|regarding|advis(?:e|es|ing)|caller|units?|respond(?:ing)?|en\s+route)\b.*$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"^[,.;:\-\s]+|[,.;:\-\s]+$", string.Empty);
        return value;
    }

    private static IReadOnlyList<TranscriptionCandidateEvidenceDto> ExtractSymbolEvidence(string rawMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(rawMetadataJson) || rawMetadataJson.Trim() == "{}")
            return [];

        try
        {
            using var doc = JsonDocument.Parse(rawMetadataJson);
            var rows = new List<TranscriptionCandidateEvidenceDto>();
            WalkMetadata(doc.RootElement, string.Empty, rows);
            return rows
                .DistinctBy(row => $"{row.Kind}|{row.Value}", StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static void WalkMetadata(JsonElement element, string path, List<TranscriptionCandidateEvidenceDto> rows)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = string.IsNullOrWhiteSpace(path) ? property.Name : $"{path}.{property.Name}";
                    if (SymbolEvidenceNameRegex.IsMatch(property.Name) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    {
                        var value = property.Value.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                            rows.Add(new TranscriptionCandidateEvidenceDto(property.Name, value, LooksLikeExistingPath(value)));
                    }
                    WalkMetadata(property.Value, childPath, rows);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    WalkMetadata(item, path, rows);
                break;
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                if (SymbolEvidenceNameRegex.IsMatch(path) || SymbolEvidenceNameRegex.IsMatch(text))
                    rows.Add(new TranscriptionCandidateEvidenceDto(path, text, LooksLikeExistingPath(text)));
                break;
        }
    }

    private static bool LooksLikeExistingPath(string value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) && (File.Exists(value) || Directory.Exists(value));
        }
        catch
        {
            return false;
        }
    }

    private static int CountNearbyIncidents(EngineCall call, IReadOnlyList<IncidentDto> incidents)
    {
        var start = call.StartTime - 600;
        var end = Math.Max(call.StopTime, call.StartTime) + 600;
        return incidents.Count(incident =>
            incident.LastSeen >= start &&
            incident.FirstSeen <= end &&
            (string.Equals(incident.Category, call.Category, StringComparison.OrdinalIgnoreCase) ||
             incident.Calls.Any(c => c.TalkgroupName == call.TalkgroupName || c.SystemShortName == call.SystemShortName)));
    }

    private static Regex WordRegex(string pattern) =>
        new(@"\b(?:" + pattern + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string Normalize(string value) => WhitespaceRegex.Replace(value ?? string.Empty, " ").Trim();

    private static string Preview(string value) => value.Length <= 220 ? value : value[..220] + "...";
}
