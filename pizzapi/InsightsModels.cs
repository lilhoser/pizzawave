using Newtonsoft.Json;
using pizzalib;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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

    [JsonIgnore]
    public bool ShowDateDivider { get; set; }

    [JsonIgnore]
    public string DateDividerText { get; set; } = string.Empty;

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
    public string AccentColor { get; set; } = "#8a63d2";
    public List<InsightNotableEvent> Events { get; set; } = new();
    public bool IsCollapsed { get; set; }
    public bool ShowAll { get; set; }
    public int PreviewCount { get; set; } = 100;

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
            _ => "#8a63d2"
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
            _ => "#271f33"
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

public class InsightIncident
{
    [JsonProperty("incident_id")]
    public string IncidentId { get; set; } = string.Empty;
    [JsonProperty("title")]
    public string Title { get; set; } = string.Empty;
    [JsonProperty("narrative_summary")]
    public string NarrativeSummary { get; set; } = string.Empty;
    [JsonProperty("interestingness_score")]
    public double InterestingnessScore { get; set; }
    [JsonProperty("severity")]
    public string Severity { get; set; } = "medium";
    [JsonProperty("status")]
    public string Status { get; set; } = "active";
    [JsonProperty("first_seen")]
    public DateTimeOffset FirstSeen { get; set; }
    [JsonProperty("last_seen")]
    public DateTimeOffset LastSeen { get; set; }
    [JsonProperty("category")]
    public string Category { get; set; } = "other";
    [JsonProperty("call_ids")]
    public List<string> CallIds { get; set; } = new();
    [JsonProperty("call_hashes")]
    public List<string> CallHashes { get; set; } = new();
    [JsonProperty("event_count")]
    public int EventCount { get; set; }
    [JsonProperty("source_window_count")]
    public int SourceWindowCount { get; set; }
    [JsonProperty("participating_categories")]
    public List<string> ParticipatingCategories { get; set; } = new();
    [JsonProperty("novelty_score")]
    public double NoveltyScore { get; set; }
    [JsonProperty("recency_score")]
    public double RecencyScore { get; set; }
    [JsonProperty("rank_score")]
    public double RankScore { get; set; }
    [JsonProperty("correlation_confidence")]
    public double CorrelationConfidence { get; set; }

    [JsonIgnore]
    public List<InsightNotableEvent> LinkedEvents { get; set; } = new();
    [JsonIgnore]
    public InsightNotableEvent? PrimaryEvent { get; set; }
    [JsonIgnore]
    public string CategoryKey => InsightCategoryPalette.Normalize(Category);
    [JsonIgnore]
    public string CategoryDisplay => InsightCategoryPalette.DisplayName(CategoryKey);
    [JsonIgnore]
    public string CategoryAccentColor => InsightCategoryPalette.AccentColor(CategoryKey);
    [JsonIgnore]
    public string CategoryTileBackground => InsightCategoryPalette.TileBackground(CategoryKey);
    [JsonIgnore]
    public string SeverityDisplay => (Severity ?? string.Empty).Trim().ToUpperInvariant();
    [JsonIgnore]
    public string ConfidencePercent => $"{Math.Clamp((int)Math.Round(InterestingnessScore * 100.0), 0, 100)}%";
    [JsonIgnore]
    public string LastSeenDisplay => LastSeen.ToLocalTime().ToString("MMM d, h:mm tt");
    [JsonIgnore]
    public int DistinctCallCount => CallIds.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    [JsonIgnore]
    public string AgenciesDisplay => ParticipatingCategories.Count == 0
        ? CategoryDisplay
        : string.Join(" + ", ParticipatingCategories
            .Select(InsightCategoryPalette.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    [JsonIgnore]
    public string EventsDefinitionText => $"Events: {EventCount}";
    [JsonIgnore]
    public string CallsDefinitionText => $"Calls: {DistinctCallCount}";
    [JsonIgnore]
    public string NoveltyDefinitionText => $"Novelty: {Math.Clamp((int)Math.Round(NoveltyScore * 100.0), 0, 100)}%";
    [JsonIgnore]
    public string CorrelationDefinitionText => $"Correlation: {Math.Clamp((int)Math.Round(CorrelationConfidence * 100.0), 0, 100)}%";
}

public class DashboardIncidentBucket : INotifyPropertyChanged
{
    private bool _isExpanded;

    public string BucketName { get; set; } = string.Empty;
    public int MatchCount { get; set; }
    public DateTimeOffset LatestSeen { get; set; }
    public string PreviewText { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public List<DashboardIncidentDetail> EventDetails { get; set; } = new();
    public string LatestSeenText => LatestSeen.ToLocalTime().ToString("MMM d, h:mm tt");
    public string TimeRangeText
    {
        get
        {
            var times = EventDetails
                .Where(d => d.OccurredAt.HasValue)
                .Select(d => d.OccurredAt!.Value.ToLocalTime())
                .OrderBy(t => t)
                .ToList();
            if (times.Count == 0)
                return LatestSeenText;

            var first = times.First();
            var last = times.Last();
            return first.Date == last.Date
                ? $"{first:MMM d h:mm tt}-{last:h:mm tt}"
                : $"{first:MMM d h:mm tt}-{last:MMM d h:mm tt}";
        }
    }
    public bool HasEventDetails => EventDetails.Count > 0;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDetailVisible));
            OnPropertyChanged(nameof(ExpandLabel));
        }
    }
    public bool IsDetailVisible => HasEventDetails && IsExpanded;
    public string ExpandLabel => IsExpanded ? "Collapse" : "Expand";
    public string UpdateCountText => $"{MatchCount} update{(MatchCount == 1 ? string.Empty : "s")}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class DashboardIncidentDetail
{
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string MetaText { get; set; } = string.Empty;
    public DateTimeOffset? OccurredAt { get; set; }
    public string SourceCallId { get; set; } = string.Empty;
    [JsonIgnore]
    public TranscribedCall? SourceCall { get; set; }
    [JsonIgnore]
    public bool HasSourceCall => SourceCall != null;
}

public class DashboardTalkgroupTrendRow
{
    public string Talkgroup { get; set; } = string.Empty;
    public int TotalCalls { get; set; }
    public string ShareText { get; set; } = string.Empty;
    public string LastHeardText { get; set; } = string.Empty;
    public string TrendStartLabel { get; set; } = string.Empty;
    public string TrendEndLabel { get; set; } = string.Empty;
    public string TrendBucketLabel { get; set; } = string.Empty;
    public double CountRatio { get; set; }
    public List<DashboardMiniBar> Bars { get; set; } = new();
}

public class DashboardMiniBar
{
    public double Ratio { get; set; }
    public double DisplayOpacity => Ratio <= 0 ? 0.16 : Math.Clamp(0.30 + (Ratio * 0.70), 0.30, 1.0);
}

public class DashboardQualityHourStackRow
{
    public string HourLabel { get; set; } = string.Empty;
    public string HourTickLabel { get; set; } = string.Empty;
    public double EmptyHeight { get; set; }
    public double FailureHeight { get; set; }
    public double InaudibleHeight { get; set; }
    public double ShortHeight { get; set; }
    public string TotalText { get; set; } = string.Empty;
    public string RateText { get; set; } = string.Empty;
}

public class DashboardSimpleStat
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string SubValue { get; set; } = string.Empty;
}

public class DashboardBarStatRow
{
    public string Label { get; set; } = string.Empty;
    public double Ratio { get; set; }
    public string ValueText { get; set; } = string.Empty;
}

public class DashboardScatterPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Color { get; set; } = "#6fb7ff";
    public string Tooltip { get; set; } = string.Empty;
}
