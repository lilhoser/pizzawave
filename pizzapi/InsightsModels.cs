using Newtonsoft.Json;
using System.Linq;
namespace pizzapi;

public class InsightNotableEvent
{
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;
    [JsonProperty("detail")]
    public string Detail { get; set; } = string.Empty;
    [JsonProperty("confidence")]
    public double Confidence { get; set; } = 0.0;
    [JsonProperty("category")]
    public string Category { get; set; } = "other";
    [JsonProperty("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
    [JsonProperty("call_ids")]
    public List<string> CallIds { get; set; } = new();
    [JsonProperty("call_hashes")]
    public List<string> CallHashes { get; set; } = new();

    [JsonIgnore]
    public InsightSummaryWindow? ParentSummary { get; set; }

    [JsonIgnore]
    public string CategoryKey => InsightCategoryPalette.Normalize(Category);
    [JsonIgnore]
    public string CategoryDisplay => InsightCategoryPalette.DisplayName(CategoryKey);
    [JsonIgnore]
    public string CategoryIcon => InsightCategoryPalette.Icon(CategoryKey);
    [JsonIgnore]
    public string DisplayTitle => NormalizeDisplayTitle(Title, CategoryKey);
    [JsonIgnore]
    public string CategoryAccentColor => InsightCategoryPalette.AccentColor(CategoryKey);
    [JsonIgnore]
    public string CategoryTileBackground => InsightCategoryPalette.TileBackground(CategoryKey);
    [JsonIgnore]
    public bool HasAlertMatch { get; set; }
    [JsonProperty("has_alert_match")]
    public bool HasAlertMatchPersisted
    {
        get => HasAlertMatch;
        set => HasAlertMatch = value;
    }
    [JsonIgnore]
    public int MatchedCallCount { get; set; }
    [JsonIgnore]
    public string ConfidencePercent => $"{Math.Clamp((int)Math.Round(Confidence * 100.0), 0, 100)}%";
    [JsonIgnore]
    public bool IsErrorEvent =>
        string.Equals(Title?.Trim(), "Insights Error", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(ParentSummary?.Error));
    [JsonIgnore]
    public bool CanOpenSources => !IsErrorEvent;
    [JsonIgnore]
    public string TimestampDisplay
    {
        get
        {
            if (IsErrorEvent)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(Timestamp))
            {
                var raw = Timestamp.Trim();
                if (DateTime.TryParseExact(raw, "HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed24))
                {
                    return parsed24.ToString("h:mm tt");
                }
                return raw;
            }

            return ParentSummary != null ? ParentSummary.WindowStart.ToLocalTime().ToString("h:mm tt") : string.Empty;
        }
    }

    private static string NormalizeDisplayTitle(string? rawTitle, string categoryKey)
    {
        var title = (rawTitle ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        foreach (var token in GetCategoryPrefixTokens(categoryKey))
        {
            if (string.IsNullOrWhiteSpace(token))
                continue;

            var normalizedToken = token.Trim();
            if (title.StartsWith($"[{normalizedToken}]", StringComparison.OrdinalIgnoreCase))
                title = title.Substring(normalizedToken.Length + 2).TrimStart(' ', ':', '-', '|');
            else if (title.StartsWith($"{normalizedToken}:", StringComparison.OrdinalIgnoreCase) ||
                     title.StartsWith($"{normalizedToken}-", StringComparison.OrdinalIgnoreCase) ||
                     title.StartsWith($"{normalizedToken}|", StringComparison.OrdinalIgnoreCase))
                title = title.Substring(normalizedToken.Length + 1).TrimStart();
            else if (title.StartsWith($"{normalizedToken} ", StringComparison.OrdinalIgnoreCase))
                title = title.Substring(normalizedToken.Length + 1).TrimStart();
        }

        // Final cleanup for residual separators after prefix stripping.
        title = title.TrimStart(' ', '-', ':', '|', '•', '[', ']');
        return title.TrimStart();
    }

    private static IEnumerable<string> GetCategoryPrefixTokens(string categoryKey)
    {
        yield return categoryKey;
        yield return InsightCategoryPalette.DisplayName(categoryKey);

        foreach (var token in categoryKey switch
        {
            "police" => new[] { "POL", "PD", "POLICE" },
            "fire" => new[] { "FIRE", "FD" },
            "ems" => new[] { "EMS", "MED", "MEDICAL" },
            "traffic" => new[] { "TRAFFIC", "TRAF" },
            "public_works" => new[] { "PUBLIC WORKS", "PW" },
            "utilities" => new[] { "UTILITIES", "UTIL" },
            _ => new[] { "OTHER" }
        })
        {
            yield return token;
        }
    }
}

public class InsightSummaryWindow
{
    [JsonProperty("window_start")]
    public DateTimeOffset WindowStart { get; set; }
    [JsonProperty("window_end")]
    public DateTimeOffset WindowEnd { get; set; }
    [JsonProperty("summary_text")]
    public string SummaryText { get; set; } = string.Empty;
    [JsonProperty("notable_events")]
    public List<InsightNotableEvent> NotableEvents { get; set; } = new();
    
    [JsonIgnore]
    public IEnumerable<InsightNotableEvent> SortedNotableEvents => 
        NotableEvents.OrderByDescending(e => e.Confidence);

    [JsonIgnore]
    public IEnumerable<InsightCategorySection> NotableSections =>
        SortedNotableEvents
            .GroupBy(e => e.CategoryKey)
            .OrderBy(g => InsightCategoryPalette.Order(g.Key))
            .Select(g => new InsightCategorySection
            {
                CategoryKey = g.Key,
                DisplayName = InsightCategoryPalette.DisplayName(g.Key),
                Icon = InsightCategoryPalette.Icon(g.Key),
                AccentColor = InsightCategoryPalette.AccentColor(g.Key),
                Events = g.ToList()
            });

    [JsonProperty("source_counts")]
    public Dictionary<string, object> SourceCounts { get; set; } = new();
    [JsonProperty("source_hashes")]
    public List<string> SourceHashes { get; set; } = new();
    [JsonProperty("source_call_ids")]
    public List<string> SourceCallIds { get; set; } = new();
    [JsonProperty("model")]
    public string Model { get; set; } = string.Empty;
    [JsonProperty("prompt_version")]
    public string PromptVersion { get; set; } = "insights_v2";
    [JsonProperty("error")]
    public string? Error { get; set; }

    [JsonIgnore]
    public string FriendlyWindowLabel
    {
        get
        {
            var startLocal = WindowStart.ToLocalTime();
            var midpoint = WindowStart + TimeSpan.FromTicks((WindowEnd - WindowStart).Ticks / 2);
            var part = GetDayPart(midpoint.ToLocalTime());
            return $"{startLocal:dddd MMMM d, yyyy} - {part}";
        }
    }

    private static string GetDayPart(DateTimeOffset ts)
    {
        var hour = ts.Hour + (ts.Minute / 60.0);
        if (hour < 4) return "Overnight";
        if (hour < 7) return "Early morning";
        if (hour < 10) return "Morning";
        if (hour < 11.5) return "Late morning";
        if (hour < 13.5) return "Around noon";
        if (hour < 16) return "Afternoon";
        if (hour < 18) return "Late afternoon";
        if (hour < 21) return "Evening";
        return "Late night";
    }
}

public class InsightCategorySection
{
    public string CategoryKey { get; set; } = "other";
    public string DisplayName { get; set; } = "Other";
    public string Icon { get; set; } = "*";
    public string AccentColor { get; set; } = "#5a5a5a";
    public List<InsightNotableEvent> Events { get; set; } = new();
    public bool IsCollapsed { get; set; }
    public bool ShowAll { get; set; }
    public int PreviewCount { get; set; } = 3;

    public int TotalCount => Events.Count;
    public int HiddenCount => Math.Max(0, TotalCount - PreviewCount);
    public bool HasHiddenEvents => !IsCollapsed && !ShowAll && HiddenCount > 0;
    public bool CanShowFewer => !IsCollapsed && ShowAll && TotalCount > PreviewCount;
    public List<InsightNotableEvent> VisibleEvents =>
        IsCollapsed
            ? new List<InsightNotableEvent>()
            : (ShowAll ? Events.ToList() : Events.Take(PreviewCount).ToList());
}

public class InsightsNarrativeSection
{
    public string Title { get; set; } = string.Empty;
    public List<InsightsNarrativeBullet> Bullets { get; set; } = new();
}

public class InsightsNarrativeBullet
{
    public string Lead { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string DisplayText => string.IsNullOrWhiteSpace(Lead) ? Text : $"{Lead}: {Text}";
}

internal static class InsightCategoryPalette
{
    public static string Normalize(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
        {
            "police" or "law" or "law_enforcement" => "police",
            "fire" => "fire",
            "ems" or "medical" => "ems",
            "traffic" or "transport" => "traffic",
            "public_works" or "publicworks" or "public works" => "public_works",
            "utilities" or "utility" => "utilities",
            _ => "other"
        };
    }

    public static int Order(string categoryKey)
    {
        return Normalize(categoryKey) switch
        {
            "police" => 0,
            "fire" => 1,
            "ems" => 2,
            "traffic" => 3,
            "public_works" => 4,
            "utilities" => 5,
            _ => 6
        };
    }

    public static string DisplayName(string categoryKey)
    {
        return Normalize(categoryKey) switch
        {
            "police" => "Police",
            "fire" => "Fire",
            "ems" => "EMS",
            "traffic" => "Traffic",
            "public_works" => "Public Works",
            "utilities" => "Utilities",
            _ => "Other"
        };
    }

    public static string Icon(string categoryKey)
    {
        return Normalize(categoryKey) switch
        {
            "police" => "POL",
            "fire" => "FIR",
            "ems" => "EMS",
            "traffic" => "TRF",
            "public_works" => "PWK",
            "utilities" => "UTL",
            _ => "OTH"
        };
    }

    public static string AccentColor(string categoryKey)
    {
        return Normalize(categoryKey) switch
        {
            "police" => "#2b74e4",
            "fire" => "#cf4b3c",
            "ems" => "#2ea44f",
            "traffic" => "#d79a28",
            "public_works" => "#6b7f95",
            "utilities" => "#8a63d2",
            _ => "#5a5a5a"
        };
    }

    public static string TileBackground(string categoryKey)
    {
        return Normalize(categoryKey) switch
        {
            "police" => "#1b2435",
            "fire" => "#34201d",
            "ems" => "#1c2c22",
            "traffic" => "#312916",
            "public_works" => "#222a31",
            "utilities" => "#272133",
            _ => "#2a2a2a"
        };
    }
}

public class InsightIndexEntry
{
    [JsonProperty("start")]
    public DateTimeOffset Start { get; set; }
    [JsonProperty("end")]
    public DateTimeOffset End { get; set; }
    [JsonProperty("tags")]
    public List<string> Tags { get; set; } = new();
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;
}
