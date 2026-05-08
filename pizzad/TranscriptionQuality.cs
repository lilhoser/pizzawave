using System.Text.RegularExpressions;

namespace pizzad;

public sealed record TranscriptionQuality(string Status, string Reason, bool IncludeInSummaries);

public static class TranscriptionQualityClassifier
{
    private static readonly Regex FailurePattern = new(
        @"\b(failed|error|exception|unable to transcribe)\b",
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
        if (string.Equals(statusHint, "failed", StringComparison.OrdinalIgnoreCase))
            return new("failed", "transcription_error", false);

        var text = transcript ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return new("poor_quality", "empty", false);

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

        return new("complete", "ok", true);
    }

    public static bool IsProblem(EngineCall call) =>
        string.Equals(call.TranscriptionStatus, "poor_quality", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(call.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase);
}
