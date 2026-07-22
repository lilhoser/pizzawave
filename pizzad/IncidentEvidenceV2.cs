using System.Text.Json.Serialization;

namespace pizzad;

public sealed record IncidentHypothesisV2(
    [property: JsonPropertyName("hypothesis_id")]
    string HypothesisId,
    [property: JsonPropertyName("system_short_name")]
    string SystemShortName,
    [property: JsonPropertyName("candidate_call_ids")]
    IReadOnlyList<long> CandidateCallIds,
    [property: JsonPropertyName("model_confidence")]
    double ModelConfidence,
    [property: JsonPropertyName("events")]
    IReadOnlyList<EventEvidenceV2> Events,
    [property: JsonPropertyName("locations")]
    IReadOnlyList<LocationEvidenceV2> Locations,
    [property: JsonPropertyName("membership")]
    IReadOnlyList<MembershipEvidenceV2> Membership,
    [property: JsonPropertyName("conflicts")]
    IReadOnlyList<ConflictEvidenceV2> Conflicts,
    [property: JsonPropertyName("narrative")]
    NarrativeEvidenceV2 Narrative);

public sealed record EventEvidenceV2(
    [property: JsonPropertyName("event_class")]
    string EventClass,
    [property: JsonPropertyName("event_subtype")]
    string EventSubtype,
    [property: JsonPropertyName("strength")]
    string Strength,
    [property: JsonPropertyName("source_call_ids")]
    IReadOnlyList<long> SourceCallIds,
    [property: JsonPropertyName("spans")]
    IReadOnlyList<EvidenceSpanV2> Spans);

public sealed record LocationEvidenceV2(
    [property: JsonPropertyName("kind")]
    string Kind,
    [property: JsonPropertyName("display")]
    string Display,
    [property: JsonPropertyName("normalized_key")]
    string NormalizedKey,
    [property: JsonPropertyName("confidence")]
    string Confidence,
    [property: JsonPropertyName("source_call_ids")]
    IReadOnlyList<long> SourceCallIds,
    [property: JsonPropertyName("spans")]
    IReadOnlyList<EvidenceSpanV2> Spans);

public sealed record MembershipEvidenceV2(
    [property: JsonPropertyName("call_id")]
    long CallId,
    [property: JsonPropertyName("role")]
    string Role,
    [property: JsonPropertyName("decision")]
    string Decision,
    [property: JsonPropertyName("reasons")]
    IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("spans")]
    IReadOnlyList<EvidenceSpanV2> Spans);

public sealed record ConflictEvidenceV2(
    [property: JsonPropertyName("conflict_type")]
    string ConflictType,
    [property: JsonPropertyName("call_ids")]
    IReadOnlyList<long> CallIds,
    [property: JsonPropertyName("reason")]
    string Reason,
    [property: JsonPropertyName("spans")]
    IReadOnlyList<EvidenceSpanV2> Spans);

public sealed record NarrativeEvidenceV2(
    [property: JsonPropertyName("title")]
    string Title,
    [property: JsonPropertyName("detail")]
    string Detail,
    [property: JsonPropertyName("facts")]
    IReadOnlyList<NarrativeFactV2> Facts);

public sealed record NarrativeFactV2(
    [property: JsonPropertyName("kind")]
    string Kind,
    [property: JsonPropertyName("text")]
    string Text,
    [property: JsonPropertyName("spans")]
    IReadOnlyList<EvidenceSpanV2> Spans);

public sealed record EvidenceSpanV2(
    [property: JsonPropertyName("call_id")]
    long CallId,
    [property: JsonPropertyName("start_char")]
    int StartChar,
    [property: JsonPropertyName("end_char")]
    int EndChar,
    [property: JsonPropertyName("text")]
    string Text);

public sealed record PersistenceDecisionV2(
    [property: JsonPropertyName("decision")]
    string Decision,
    [property: JsonPropertyName("incident_key")]
    string IncidentKey,
    [property: JsonPropertyName("accepted_call_ids")]
    IReadOnlyList<long> AcceptedCallIds,
    [property: JsonPropertyName("rejected_call_ids")]
    IReadOnlyList<long> RejectedCallIds,
    [property: JsonPropertyName("title")]
    string Title,
    [property: JsonPropertyName("detail")]
    string Detail,
    [property: JsonPropertyName("category")]
    string Category,
    [property: JsonPropertyName("reasons")]
    IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("conflicts")]
    IReadOnlyList<ConflictEvidenceV2> Conflicts,
    [property: JsonPropertyName("pending_call_ids")]
    IReadOnlyList<long>? PendingCallIds = null,
    [property: JsonPropertyName("candidate_relationship")]
    string CandidateRelationship = "");

public sealed record IncidentEvidenceVerificationResult(
    bool Accepted,
    IReadOnlyList<string> Errors);

public static class IncidentEvidenceClaimVerifier
{
    public static IncidentEvidenceVerificationResult VerifySpans(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var errors = new List<string>();
        foreach (var span in AllSpans(hypothesis))
        {
            if (ContainsEllipsis(span.Text))
            {
                errors.Add($"call {span.CallId}: evidence span contains an ellipsis");
                continue;
            }

            if (!transcriptsByCallId.TryGetValue(span.CallId, out var transcript))
            {
                errors.Add($"call {span.CallId}: transcript unavailable for evidence span");
                continue;
            }

            if (span.StartChar < 0 || span.EndChar < span.StartChar || span.EndChar > transcript.Length)
            {
                if (!CanResolveQuotedText(transcript, span.Text))
                    errors.Add($"call {span.CallId}: evidence span {span.StartChar}-{span.EndChar} is outside transcript bounds");
                continue;
            }

            var actual = transcript[span.StartChar..span.EndChar];
            if (!string.Equals(actual, span.Text, StringComparison.Ordinal) && !CanResolveQuotedText(transcript, span.Text))
                errors.Add($"call {span.CallId}: evidence span text mismatch");
        }

        return new IncidentEvidenceVerificationResult(errors.Count == 0, errors);
    }

    public static bool IsSpanGrounded(
        EvidenceSpanV2 span,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        if (ContainsEllipsis(span.Text))
            return false;

        if (!transcriptsByCallId.TryGetValue(span.CallId, out var transcript))
            return false;

        if (span.StartChar < 0 || span.EndChar < span.StartChar || span.EndChar > transcript.Length)
            return CanResolveQuotedText(transcript, span.Text);

        var actual = transcript[span.StartChar..span.EndChar];
        return string.Equals(actual, span.Text, StringComparison.Ordinal)
            || CanResolveQuotedText(transcript, span.Text);
    }

    public static bool AnySpanGrounded(
        IEnumerable<EvidenceSpanV2> spans,
        IReadOnlyDictionary<long, string> transcriptsByCallId) =>
        spans.Any(span => IsSpanGrounded(span, transcriptsByCallId));

    private static bool ContainsEllipsis(string value) =>
        value.Contains("...", StringComparison.Ordinal) || value.Contains('…');

    private static bool CanResolveQuotedText(string transcript, string quotedText)
    {
        if (string.IsNullOrWhiteSpace(quotedText))
            return false;

        if (transcript.Contains(quotedText, StringComparison.Ordinal))
            return true;

        var normalizedTranscript = NormalizeForEvidenceLookup(transcript);
        var normalizedQuote = NormalizeForEvidenceLookup(quotedText);
        return !string.IsNullOrWhiteSpace(normalizedQuote)
            && normalizedTranscript.Contains(normalizedQuote, StringComparison.Ordinal);
    }

    private static string NormalizeForEvidenceLookup(string value)
    {
        var buffer = new char[value.Length];
        var index = 0;
        var lastWasSpace = true;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = char.ToLowerInvariant(ch);
                lastWasSpace = false;
                continue;
            }

            if (!lastWasSpace)
            {
                buffer[index++] = ' ';
                lastWasSpace = true;
            }
        }

        if (index > 0 && buffer[index - 1] == ' ')
            index--;

        return new string(buffer, 0, index);
    }

    private static IEnumerable<EvidenceSpanV2> AllSpans(IncidentHypothesisV2 hypothesis)
    {
        foreach (var evidence in hypothesis.Events)
        {
            foreach (var span in evidence.Spans)
                yield return span;
        }

        foreach (var location in hypothesis.Locations)
        {
            foreach (var span in location.Spans)
                yield return span;
        }

        foreach (var membership in hypothesis.Membership)
        {
            foreach (var span in membership.Spans)
                yield return span;
        }

        foreach (var conflict in hypothesis.Conflicts)
        {
            foreach (var span in conflict.Spans)
                yield return span;
        }

        foreach (var fact in hypothesis.Narrative.Facts)
        {
            foreach (var span in fact.Spans)
                yield return span;
        }
    }
}
