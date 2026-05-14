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

    public static TranscriptionQuality Classify(string? transcript, string? statusHint = null)
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

        if (LooksNumericOnly(withoutMarkers))
            return new("poor_quality", "numeric_noise", false);

        if (LooksRepetitive(withoutMarkers))
            return new("poor_quality", "repetitive", false);

        return new("complete", "ok", true);
    }

    private static bool LooksNumericOnly(string text)
    {
        var tokens = WordPattern.Matches(text).Select(m => m.Value).ToList();
        if (tokens.Count < 5)
            return false;

        return tokens.All(token => token.All(char.IsDigit));
    }

    private static bool LooksRepetitive(string text)
    {
        var tokens = WordPattern.Matches(text)
            .Select(m => m.Value.ToLowerInvariant())
            .ToList();
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

        return false;
    }

    public static bool IsProblem(EngineCall call) =>
        string.Equals(call.TranscriptionStatus, "poor_quality", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(call.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase);
}
