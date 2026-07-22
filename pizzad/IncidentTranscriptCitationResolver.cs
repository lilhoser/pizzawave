namespace pizzad;

public static class IncidentTranscriptCitationResolver
{
    public const string ConfigurationToken = "citations=source-punctuation-v1";

    public static string Resolve(string transcript, string proposedQuote)
    {
        if (string.IsNullOrEmpty(transcript) || string.IsNullOrEmpty(proposedQuote))
            return proposedQuote;

        if (transcript.Contains(proposedQuote, StringComparison.Ordinal))
            return proposedQuote;

        var normalizedTranscript = NormalizeTypography(transcript);
        var normalizedQuote = NormalizeTypography(proposedQuote);
        var sourceIndex = normalizedTranscript.IndexOf(normalizedQuote, StringComparison.Ordinal);
        return sourceIndex < 0
            ? proposedQuote
            : transcript.Substring(sourceIndex, proposedQuote.Length);
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
