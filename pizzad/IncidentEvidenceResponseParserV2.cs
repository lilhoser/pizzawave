using System.Text.Json;

namespace pizzad;

public sealed record IncidentHypothesisResponseV2(IReadOnlyList<IncidentHypothesisV2> Hypotheses);

public static class IncidentEvidenceResponseParserV2
{
    public static IncidentHypothesisResponseV2 ParseOpenAiChatCompletion(string responseText, string systemShortName)
    {
        var content = ExtractAssistantContent(responseText);
        content = StripCodeFence(content);
        content = ExtractJsonObject(content);
        return ParseContent(content, systemShortName);
    }

    public static IncidentHypothesisResponseV2 ParseContent(string content, string systemShortName)
    {
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("hypotheses", out var hypothesesElement) ||
            hypothesesElement.ValueKind != JsonValueKind.Array)
            return new IncidentHypothesisResponseV2([]);

        var hypotheses = new List<IncidentHypothesisV2>();
        var index = 0;
        foreach (var item in hypothesesElement.EnumerateArray())
        {
            index++;
            hypotheses.Add(ReadHypothesis(item, systemShortName, index));
        }

        return new IncidentHypothesisResponseV2(hypotheses);
    }

    private static IncidentHypothesisV2 ReadHypothesis(JsonElement item, string systemShortName, int index)
    {
        var membership = ReadArray(item, "membership", ReadMembership);
        var candidateCallIds = ReadLongArray(item, "candidate_call_ids");
        if (candidateCallIds.Count == 0)
            candidateCallIds = membership.Select(row => row.CallId).Where(id => id > 0).Distinct().Order().ToList();

        return new IncidentHypothesisV2(
            DefaultString(ReadString(item, "hypothesis_id"), $"hypothesis-{index}"),
            DefaultString(ReadString(item, "system_short_name"), systemShortName),
            candidateCallIds,
            Math.Clamp(ReadDouble(item, "model_confidence"), 0, 1),
            ReadArray(item, "events", ReadEvent),
            ReadArray(item, "locations", ReadLocation),
            membership,
            ReadArray(item, "conflicts", ReadConflict),
            ReadNarrative(ReadObject(item, "narrative")));
    }

    private static EventEvidenceV2 ReadEvent(JsonElement item) => new(
        ReadString(item, "event_class"),
        ReadString(item, "event_subtype"),
        ReadString(item, "strength"),
        ReadLongArray(item, "source_call_ids"),
        ReadArray(item, "spans", ReadSpan));

    private static LocationEvidenceV2 ReadLocation(JsonElement item) => new(
        ReadString(item, "kind"),
        ReadString(item, "display"),
        ReadString(item, "normalized_key"),
        ReadString(item, "confidence"),
        ReadLongArray(item, "source_call_ids"),
        ReadArray(item, "spans", ReadSpan));

    private static MembershipEvidenceV2 ReadMembership(JsonElement item) => new(
        ReadLong(item, "call_id"),
        ReadString(item, "role"),
        ReadString(item, "decision"),
        ReadStringArray(item, "reasons"),
        ReadArray(item, "spans", ReadSpan));

    private static ConflictEvidenceV2 ReadConflict(JsonElement item) => new(
        ReadString(item, "conflict_type"),
        ReadLongArray(item, "call_ids"),
        ReadString(item, "reason"),
        ReadArray(item, "spans", ReadSpan));

    private static NarrativeEvidenceV2 ReadNarrative(JsonElement item) => item.ValueKind == JsonValueKind.Object
        ? new NarrativeEvidenceV2(
            ReadString(item, "title"),
            ReadString(item, "detail"),
            ReadArray(item, "facts", ReadNarrativeFact))
        : new NarrativeEvidenceV2(string.Empty, string.Empty, []);

    private static NarrativeFactV2 ReadNarrativeFact(JsonElement item) => new(
        ReadString(item, "kind"),
        ReadString(item, "text"),
        ReadArray(item, "spans", ReadSpan));

    private static EvidenceSpanV2 ReadSpan(JsonElement item) => new(
        ReadLong(item, "call_id"),
        Math.Max(0, (int)ReadLong(item, "start_char")),
        Math.Max(0, (int)ReadLong(item, "end_char")),
        ReadString(item, "text"));

    private static List<T> ReadArray<T>(JsonElement item, string propertyName, Func<JsonElement, T> read)
    {
        if (!item.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<T>();
        foreach (var child in array.EnumerateArray())
        {
            if (child.ValueKind == JsonValueKind.Object)
                values.Add(read(child));
        }

        return values;
    }

    private static List<long> ReadLongArray(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Select(ReadLongValue)
            .Where(id => id > 0)
            .Distinct()
            .Order()
            .ToList();
    }

    private static List<string> ReadStringArray(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Select(ReadStringValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static JsonElement ReadObject(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string ReadString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) ? ReadStringValue(value) : string.Empty;

    private static string ReadStringValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => string.Empty
    };

    private static long ReadLong(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) ? ReadLongValue(value) : 0;

    private static long ReadLongValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;

        return 0;
    }

    private static double ReadDouble(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value))
            return 0;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out number))
            return number;

        return 0;
    }

    private static string DefaultString(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string ExtractAssistantContent(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
            return string.Empty;

        try
        {
            using var root = JsonDocument.Parse(responseText);
            if (root.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentElement))
            {
                var content = contentElement.ValueKind == JsonValueKind.String
                    ? contentElement.GetString() ?? string.Empty
                    : contentElement.GetRawText();
                if (string.IsNullOrWhiteSpace(content) &&
                    message.TryGetProperty("reasoning_content", out var reasoningElement) &&
                    reasoningElement.ValueKind == JsonValueKind.String)
                    content = reasoningElement.GetString() ?? string.Empty;
                return content;
            }
        }
        catch (JsonException)
        {
            return responseText;
        }

        return responseText;
    }

    private static string StripCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
            return trimmed.Trim('`').Trim();

        var body = trimmed[(firstLineEnd + 1)..].Trim();
        return body.EndsWith("```", StringComparison.Ordinal)
            ? body[..^3].Trim()
            : body;
    }

    private static string ExtractJsonObject(string content)
    {
        content = content.Trim();
        if (content.StartsWith('{') && content.EndsWith('}'))
            return content;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start
            ? content[start..(end + 1)]
            : content;
    }
}
