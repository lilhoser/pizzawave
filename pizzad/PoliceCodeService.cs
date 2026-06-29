using System.Text.RegularExpressions;

namespace pizzad;

public sealed class PoliceCodeService
{
    private static readonly Dictionary<string, string> GenericMeanings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10-4"] = "acknowledged",
        ["10-7"] = "out of service",
        ["10-8"] = "in service",
        ["10-9"] = "repeat",
        ["10-20"] = "location",
        ["10-21"] = "call by telephone",
        ["10-23"] = "arrived at scene",
        ["10-28"] = "registration check",
        ["10-29"] = "wanted check",
        ["10-32"] = "person with gun",
        ["10-33"] = "emergency traffic",
        ["10-36"] = "correct time",
        ["10-41"] = "beginning tour of duty",
        ["10-42"] = "ending tour of duty",
        ["10-49"] = "traffic crash",
        ["10-50"] = "traffic collision",
        ["10-51"] = "wrecker needed",
        ["10-52"] = "ambulance needed",
        ["10-76"] = "en route",
        ["10-97"] = "arrived",
        ["10-98"] = "assignment complete"
    };

    private static readonly Regex NumericTenCodeRegex = new(@"\b10[-\s]?(?<num>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CodeRegex = new(@"\b(?:code|signal)\s+(?<num>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<TranscriptAnnotation> Detect(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return [];

        var rows = new List<TranscriptAnnotation>();
        foreach (Match match in NumericTenCodeRegex.Matches(transcript))
        {
            var number = match.Groups["num"].Value.TrimStart('0');
            if (string.IsNullOrWhiteSpace(number))
                number = "0";
            rows.Add(Build($"10-{number}", match.Value));
        }

        foreach (Match match in CodeRegex.Matches(transcript))
        {
            var number = match.Groups["num"].Value.TrimStart('0');
            if (string.IsNullOrWhiteSpace(number))
                number = "0";
            var normalized = match.Value.StartsWith("signal", StringComparison.OrdinalIgnoreCase) ? $"signal-{number}" : $"code-{number}";
            rows.Add(Build(normalized, match.Value));
        }

        return rows;
    }

    private static TranscriptAnnotation Build(string normalizedCode, string matchedText)
    {
        GenericMeanings.TryGetValue(normalizedCode, out var meaning);
        return new TranscriptAnnotation(
            "police_code",
            normalizedCode,
            normalizedCode,
            matchedText,
            meaning ?? string.Empty,
            string.IsNullOrWhiteSpace(meaning) ? 0.55 : 0.7,
            string.IsNullOrWhiteSpace(meaning) ? "detected" : "generic-unverified",
            "{}");
    }
}

public sealed record TranscriptAnnotation(
    string Kind,
    string Code,
    string NormalizedCode,
    string MatchedText,
    string Meaning,
    double Confidence,
    string Source,
    string DetailsJson);
