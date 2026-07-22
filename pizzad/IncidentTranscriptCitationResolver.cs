namespace pizzad;

public static class IncidentTranscriptCitationResolver
{
    public const string ConfigurationToken = "citations=source-segments-v3";

    public static string Resolve(string transcript, string proposedQuote)
    {
        if (string.IsNullOrEmpty(transcript) || string.IsNullOrEmpty(proposedQuote))
            return proposedQuote;

        if (transcript.Contains(proposedQuote, StringComparison.Ordinal))
            return proposedQuote;

        var normalizedTranscript = NormalizeTypography(transcript);
        var normalizedQuote = NormalizeTypography(proposedQuote);
        var sourceIndex = normalizedTranscript.IndexOf(normalizedQuote, StringComparison.Ordinal);
        if (sourceIndex >= 0)
            return transcript.Substring(sourceIndex, proposedQuote.Length);

        sourceIndex = normalizedTranscript.IndexOf(normalizedQuote, StringComparison.OrdinalIgnoreCase);
        if (sourceIndex < 0)
            return proposedQuote;
        var duplicateIndex = normalizedTranscript.IndexOf(
            normalizedQuote,
            sourceIndex + 1,
            StringComparison.OrdinalIgnoreCase);
        return duplicateIndex >= 0
            ? proposedQuote
            : transcript.Substring(sourceIndex, proposedQuote.Length);
    }

    public static IReadOnlyList<string> ResolveSegments(string transcript, string proposedQuote)
    {
        var resolved = Resolve(transcript, proposedQuote);
        if (transcript.Contains(resolved, StringComparison.Ordinal))
            return [resolved];

        var proposedSegments = proposedQuote
            .Replace("…", "...", StringComparison.Ordinal)
            .Split("...", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (proposedSegments.Length < 2)
            return [proposedQuote];

        var sourceSegments = new List<string>(proposedSegments.Length);
        var searchStart = 0;
        foreach (var proposedSegment in proposedSegments)
        {
            var sourceSegment = Resolve(transcript[searchStart..], proposedSegment);
            var relativeIndex = transcript.IndexOf(sourceSegment, searchStart, StringComparison.Ordinal);
            if (relativeIndex < 0)
                return [proposedQuote];

            sourceSegments.Add(sourceSegment);
            searchStart = relativeIndex + sourceSegment.Length;
        }

        return sourceSegments;
    }

    private static string NormalizeTypography(string value)
    {
        var characters = value.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = characters[index] switch
            {
                '\u2018' or '\u2019' or '\u201b' or '\u02bc' => '\'',
                '\u201c' or '\u201d' => '"',
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' => '-',
                _ => characters[index]
            };
        }

        return new string(characters);
    }
}
