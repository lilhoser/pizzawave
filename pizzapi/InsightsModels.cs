using Newtonsoft.Json;

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
    public string CategoryAccentColor => InsightCategoryPalette.AccentColor(CategoryKey);
    [JsonIgnore]
    public string CategoryTileBackground => InsightCategoryPalette.TileBackground(CategoryKey);
    [JsonIgnore]
    public bool HasAlertMatch { get; set; }
    [JsonIgnore]
    public int MatchedCallCount { get; set; }
    [JsonIgnore]
    public string TimestampDisplay
    {
        get
        {
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
            var endLocal = WindowEnd.ToLocalTime();
            var startPart = GetDayPart(startLocal);
            var endPart = GetDayPart(endLocal.AddMinutes(-1));
            if (!string.Equals(startPart, endPart, StringComparison.Ordinal))
            {
                var totalHours = Math.Max(1, (int)Math.Round((WindowEnd - WindowStart).TotalHours));
                return $"{startLocal:dddd MMMM d, yyyy} - Last {totalHours} hours";
            }

            return $"{startLocal:dddd MMMM d, yyyy} - {startPart}";
        }
    }

    private static string GetDayPart(DateTimeOffset ts)
    {
        var hour = ts.Hour;
        if (hour < 12) return "Morning";
        if (hour < 17) return "Afternoon";
        if (hour < 21) return "Evening";
        return "Night";
    }
}

public class InsightCategorySection
{
    public string CategoryKey { get; set; } = "other";
    public string DisplayName { get; set; } = "Other";
    public string Icon { get; set; } = "•";
    public string AccentColor { get; set; } = "#5a5a5a";
    public List<InsightNotableEvent> Events { get; set; } = new();
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
            "police" => "🚓",
            "fire" => "🚒",
            "ems" => "🚑",
            "traffic" => "🚦",
            "public_works" => "🏗️",
            "utilities" => "⚡",
            _ => "📻"
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
