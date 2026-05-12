using System.Text.RegularExpressions;

namespace pizzad;

public sealed record IncidentCandidateCall(
    long CallId,
    long RawTimestamp,
    string Transcript,
    string Category,
    string TalkgroupName,
    string SystemShortName);

public sealed record IncidentCandidateValidationResult(
    bool IsValid,
    string Reason,
    IReadOnlyList<IncidentCandidateCall> Calls);

public static class IncidentCandidateValidator
{
    private static readonly TimeSpan MaxIncidentSpan = TimeSpan.FromMinutes(60);

    private static readonly Regex NonIncidentPattern = new(
        @"\b(no|none|not|without|unclear)\b.{0,40}\b(incident|event|actionable|emergency|issue|activity)\b|\b(no event detected|no clear incident|no actionable incident|no notable event|nothing notable|routine traffic only|routine chatter|non.?incident)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenericBucketPattern = new(
        @"\b(unit status|status updates?|dispatch coordination|dispatch activity|dispatching|administrative|location updates?|call updates?|call assignment|checks?|routine|coordination)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AddressPattern = new(
        @"\b\d{2,6}\s+(?:[a-z0-9]+\s+){0,5}(?:road|rd|street|st|drive|dr|avenue|ave|lane|ln|pike|highway|hwy|place|pl|circle|cir|trail|way|court|ct|parkway|pkwy|boulevard|blvd|terrace|ter|loop)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RoadPattern = new(
        @"\b(?:[a-z0-9]+\s+){1,4}(?:road|rd|street|st|drive|dr|avenue|ave|lane|ln|pike|highway|hwy|place|pl|circle|cir|trail|way|court|ct|parkway|pkwy|boulevard|blvd|terrace|ter|loop)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HighwayPattern = new(
        @"\b(?:i[-\s]?\d{1,3}|interstate\s+\d{1,3}|exit\s+\d{1,3}|mile\s*marker\s*\d{1,3}|mm\s*\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsNotableText(string title, string detail, int callCount)
    {
        var combined = NormalizeText($"{title} {detail}");
        return !string.IsNullOrWhiteSpace(combined)
               && callCount > 0
               && !NonIncidentPattern.IsMatch(combined);
    }

    public static bool IsActionableText(string title, string detail, int callCount) =>
        callCount >= 2 && IsNotableText(title, detail, callCount);

    public static IncidentCandidateValidationResult Validate(string title, string detail, IReadOnlyList<IncidentCandidateCall> calls)
    {
        if (calls.Count < 2)
            return new(false, "fewer than 2 resolved calls", []);

        var first = calls.Min(c => c.RawTimestamp);
        var last = calls.Max(c => c.RawTimestamp);
        var span = TimeSpan.FromSeconds(Math.Max(0, last - first));
        if (span > MaxIncidentSpan)
            return new(false, $"call span {span.TotalMinutes:0.#}m exceeds {MaxIncidentSpan.TotalMinutes:0.#}m", []);

        var eventText = $"{title} {detail}";
        var eventTokens = ExtractTokens(eventText);
        var eventAnchors = ExtractAnchors(eventText);
        var rows = calls.Select(c => new CandidateRow(c, ExtractTokens(CallText(c)), ExtractAnchors(CallText(c)))).ToList();

        if (GenericBucketPattern.IsMatch(title))
            return new(false, "generic routine/status title is not an incident", []);

        var retained = rows
            .Where(row => CallMatchesEvent(row, eventTokens, eventAnchors))
            .ToList();

        if (retained.Count < 2)
            return new(false, $"only {retained.Count} call(s) match the incident title/detail", retained.Select(r => r.Call).ToList());

        retained = retained
            .Where(row => HasCorroboratingCall(row, retained))
            .ToList();

        if (retained.Count < 2)
            return new(false, $"only {retained.Count} call(s) have corroborating related calls", retained.Select(r => r.Call).ToList());

        if (retained.Count == 2)
        {
            var pair = PairScore(retained[0], retained[1]);
            if (pair < 0.20)
                return new(false, $"two-call incident pair similarity {pair:0.00} is below 0.20", []);
        }

        return new(true, "multi-call actionable event with concrete anchors or corroborating text", retained.Select(r => r.Call).ToList());
    }

    private static bool CallMatchesEvent(CandidateRow row, HashSet<string> eventTokens, HashSet<string> eventAnchors)
    {
        var anchorScore = SharedAnchorScore(eventAnchors, row.Anchors);
        if (eventAnchors.Count > 0 && anchorScore > 0)
            return true;

        var textScore = Similarity(eventTokens, row.Tokens);
        if (eventAnchors.Count > 0)
            return textScore >= 0.16;

        return textScore >= 0.24;
    }

    private static bool HasCorroboratingCall(CandidateRow row, IReadOnlyList<CandidateRow> rows)
    {
        foreach (var other in rows)
        {
            if (other.Call.CallId == row.Call.CallId)
                continue;
            if (PairScore(row, other) >= 0.12)
                return true;
        }
        return false;
    }

    private static double PairScore(CandidateRow a, CandidateRow b)
    {
        var anchor = SharedAnchorScore(a.Anchors, b.Anchors);
        if (anchor > 0)
            return Math.Max(0.35, anchor);
        return Similarity(a.Tokens, b.Tokens);
    }

    private static double SharedAnchorScore(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var shared = a.Count(b.Contains);
        return shared == 0 ? 0 : shared / (double)Math.Min(a.Count, b.Count);
    }

    private static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        var jaccard = union <= 0 ? 0 : intersection / (double)union;
        var containment = intersection / (double)Math.Min(a.Count, b.Count);
        return Math.Max(jaccard, containment * 0.72);
    }

    private static HashSet<string> ExtractAnchors(string text)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddMatches(AddressPattern, text, anchors);
        AddMatches(RoadPattern, text, anchors);
        AddMatches(HighwayPattern, text, anchors);
        return anchors;
    }

    private static void AddMatches(Regex regex, string text, HashSet<string> anchors)
    {
        foreach (Match match in regex.Matches(text ?? string.Empty))
        {
            var normalized = NormalizeAnchor(match.Value);
            if (normalized.Length >= 4 && !IsWeakAnchor(normalized))
                anchors.Add(normalized);
        }
    }

    private static bool IsWeakAnchor(string anchor)
    {
        var parts = anchor.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return true;
        if (!char.IsDigit(parts[0][0]) && new[] { "from", "the", "this", "that", "main", "your", "their", "our", "reporting", "nothing", "visible" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            return true;
        if (!char.IsDigit(parts[0][0]) && anchor.EndsWith("from street", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static HashSet<string> ExtractTokens(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "call","calls","unit","units","officer","officers","dispatch","reported","advised","responding",
            "response","scene","area","update","updates","subject","caller","vehicle","vehicles","police","fire",
            "ems","medical","traffic","north","south","east","west","street","road","drive","avenue","lane",
            "near","copy","copies","clear","clearance","radio","county","city","sheriff","department","channel",
            "station","status","administrative","coordination","routine","check","checks"
        };
        var cleaned = Regex.Replace((text ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9\s-]", " ");
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('-');
            if (token.Length < 4 || stop.Contains(token))
                continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private static string CallText(IncidentCandidateCall call) =>
        $"{call.SystemShortName} {call.Category} {call.TalkgroupName} {call.Transcript}";

    private static string NormalizeText(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeAnchor(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\b(rd|st|dr|ave|ln|hwy|pl|cir|ct|pkwy|blvd|ter)\b", m => m.Value switch
        {
            "rd" => "road",
            "st" => "street",
            "dr" => "drive",
            "ave" => "avenue",
            "ln" => "lane",
            "hwy" => "highway",
            "pl" => "place",
            "cir" => "circle",
            "ct" => "court",
            "pkwy" => "parkway",
            "blvd" => "boulevard",
            "ter" => "terrace",
            _ => m.Value
        });
        return Regex.Replace(normalized, @"\s+", " ");
    }

    private sealed record CandidateRow(IncidentCandidateCall Call, HashSet<string> Tokens, HashSet<string> Anchors);
}
