using System.Text.RegularExpressions;

namespace pizzad;

public sealed record TranscriptionQuality(string Status, string Reason, bool IncludeInSummaries);

public static class TranscriptionQualityClassifier
{
    private static readonly Regex FailurePattern = new(
        @"\b(transcription failed|transcribe failed|ffmpeg failed|unhandled exception|system\.exception|unable to transcribe|unable to normalize whisper audio)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InaudiblePattern = new(
        @"\[(?:\s*)inaudible(?:\s*)\]|\binaudible\b|\bunintelligible\b|\bunclear audio\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BlankSilencePattern = new(
        @"\[(?:\s*)(blank_audio|blank audio|silence|no audio)(?:\s*)\]|\bblank audio\b|\bno audio\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex MarkerPattern = new(
        @"\[(?:\s*)(inaudible|blank_audio|blank audio|silence|no audio|beeping|beep|music|pause|static|background noise|birds chirping|crickets chirping|phone ringing)(?:\s*)\]|\binaudible\b|\bunintelligible\b|\bunclear audio\b|\bblank audio\b|\bno audio\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WordPattern = new(
        @"[A-Za-z0-9]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static TranscriptionQuality Classify(string? transcript, string? statusHint = null, double? audioSeconds = null)
    {
        var text = transcript ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            if (string.Equals(statusHint, "failed", StringComparison.OrdinalIgnoreCase))
                return new("failed", "transcription_error", false);
            return new("poor_quality", "empty", false);
        }

        if (FailurePattern.IsMatch(text))
            return new("failed", "transcription_error", false);

        var withoutMarkers = MarkerPattern.Replace(text, " ");
        var words = WordPattern.Matches(withoutMarkers).Count;
        if (words == 0)
        {
            if (InaudiblePattern.IsMatch(text))
                return new("poor_quality", "inaudible", false);
            if (BlankSilencePattern.IsMatch(text))
                return new("poor_quality", "blank_audio", false);
            return new("poor_quality", "marker_only", false);
        }

        if (words < 3)
            return new("poor_quality", "too_short", false);

        var tokens = Tokenize(withoutMarkers);

        if (LooksNumericOnly(tokens))
            return new("poor_quality", "numeric_noise", false);

        if (LooksRepetitive(tokens))
            return new("poor_quality", "repetitive", false);

        if (LooksLowInformation(tokens))
            return new("poor_quality", "low_information", false);

        if (LooksOverExpanded(withoutMarkers, tokens, audioSeconds))
            return new("poor_quality", "overexpanded", false);

        return new("complete", "ok", true);
    }

    private static List<string> Tokenize(string text) =>
        WordPattern.Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .ToList();

    private static bool LooksNumericOnly(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 5)
            return false;

        return tokens.All(token => token.All(char.IsDigit));
    }

    private static bool LooksRepetitive(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 8)
            return false;

        var mostCommon = tokens.GroupBy(t => t).Max(g => g.Count());
        if (mostCommon >= 10 && mostCommon >= tokens.Count * 0.4)
            return true;

        var run = 1;
        for (var i = 1; i < tokens.Count; i++)
        {
            run = tokens[i] == tokens[i - 1] ? run + 1 : 1;
            if (run >= 8)
                return true;
        }

        for (var size = 2; size <= 4; size++)
        {
            var phraseRun = 1;
            string? previous = null;
            for (var i = 0; i <= tokens.Count - size; i += size)
            {
                var phrase = string.Join(' ', tokens.Skip(i).Take(size));
                phraseRun = phrase == previous ? phraseRun + 1 : 1;
                previous = phrase;
                if (phraseRun >= 4)
                    return true;
            }
        }

        if (tokens.Count >= 18)
        {
            for (var size = 3; size <= 8; size++)
            {
                var repeatedTokenCoverage = RepeatedNgramTokenCoverage(tokens, size);
                if (repeatedTokenCoverage >= tokens.Count * 0.45)
                    return true;
            }
        }

        return false;
    }

    private static int RepeatedNgramTokenCoverage(IReadOnlyList<string> tokens, int size)
    {
        if (tokens.Count < size * 2)
            return 0;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i <= tokens.Count - size; i++)
        {
            var key = string.Join('\u001f', tokens.Skip(i).Take(size));
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        return counts.Values
            .Where(count => count >= 2)
            .Sum(count => count * size);
    }

    private static bool LooksLowInformation(IReadOnlyList<string> tokens)
    {
        if (tokens.Count < 25)
            return false;

        var uniqueRatio = tokens.Distinct(StringComparer.Ordinal).Count() / (double)tokens.Count;
        if (uniqueRatio <= 0.24)
            return true;

        var shortTokenRatio = tokens.Count(t => t.Length <= 2) / (double)tokens.Count;
        return uniqueRatio <= 0.32 && shortTokenRatio >= 0.55;
    }

    private static bool LooksOverExpanded(string text, IReadOnlyList<string> tokens, double? audioSeconds)
    {
        if (audioSeconds is null or <= 0 || tokens.Count < 18)
            return false;

        var seconds = Math.Max(1, audioSeconds.Value);
        var wordsPerSecond = tokens.Count / seconds;
        var charsPerSecond = text.Length / seconds;
        return (wordsPerSecond >= 5.5 && tokens.Count >= 35) ||
               (charsPerSecond >= 32 && text.Length >= 220);
    }

    public static bool IsProblem(EngineCall call) =>
        string.Equals(call.TranscriptionStatus, "poor_quality", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(call.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase);
}
