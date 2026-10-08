using System.Text.Json;
using System.Text;

namespace pizzad;

public sealed record RadioHealthException(string Id, string Severity, string Capability,
    string Summary, DateTime FirstObservedAt, DateTime ObservedAt, string? CauseRef = null,
    string? Impact = null, string? Evidence = null, string? RecommendedAction = null, string? DetailsUrl = null);
public sealed record RadioHealthCoverage(int Required, int Observed, IReadOnlyList<string> Missing);
public sealed record RadioHealthProtection(string State, string Summary,
    DateTime? ObservedAt, string RecoveryTest);
public sealed record RadioHealthSummary(int SchemaVersion, string AorId, string ReportId,
    DateTime GeneratedAt, DateTime ValidUntil, string Condition, string Confidence,
    string Impact, RadioHealthCoverage Coverage, int ExceptionCount,
    IReadOnlyList<RadioHealthException> Exceptions, RadioHealthProtection Protection,
    string DetailsUrl)
{
    public const string Topic = "whiteoak/health/v1/aor/radio/summary";
    public const string AvailabilityTopic = "whiteoak/health/v1/aor/radio/availability";
    public const int MaxPayloadBytes = 4096;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public byte[] Serialize()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidOperationException("Radio summary exceeds the payload limit.");
        return payload;
    }
}

/// <summary>Projects the existing Recommendations assessment; no new anomaly thresholds.</summary>
public sealed class RadioHealthAssessment
{
    private readonly Dictionary<string, DateTime> _firstObserved = new(StringComparer.Ordinal);
    public const string RecommendationsUrl = "http://192.168.1.173:8080/?page=system&tab=recommendations";

    public RadioHealthSummary Assess(SystemRecommendationsDto source, DateTime now)
    {
        var missing = new List<string> { "sdr-1861" };
        var current = now - source.GeneratedAtUtc <= TimeSpan.FromMinutes(6)
            && source.GeneratedAtUtc - now <= TimeSpan.FromSeconds(30);
        if (!current) missing.Add("ot-pipeline");
        var candidates = current ? source.Items.Where(IsEscalation)
            .OrderByDescending(item => item.Severity == "critical")
            .ThenBy(item => item.Id, StringComparer.Ordinal).ToArray() : [];
        var ids = candidates.Select(item => Identity(item.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var resolved in _firstObserved.Keys.Except(ids).ToArray()) _firstObserved.Remove(resolved);
        foreach (var id in ids) _firstObserved.TryAdd(id, now);
        var exceptions = candidates.Take(3).Select(item =>
        {
            var id = Identity(item.Id);
            var first = _firstObserved[id];
            var link = item.FindingId is > 0 and <= 9007199254740991
                ? RecommendationsUrl + "&finding=" + item.FindingId : RecommendationsUrl;
            return new RadioHealthException(id, item.Severity, "OT analysis pipeline",
                "OT: " + Bounded(item.Title, 120), first, source.GeneratedAtUtc,
                Impact: Consequence(item.Id),
                Evidence: $"Active {item.Severity} System recommendation. {Bounded(item.EvidenceWindow, 72)}.",
                RecommendedAction: "Open the finding in System / Recommendations for its diagnosis and recommended action.",
                DetailsUrl: link);
        }).ToArray();
        return new RadioHealthSummary(1, "radio", Guid.NewGuid().ToString("N"), now,
            now.AddMinutes(15), exceptions.Length > 0 ? "degraded" : "unknown", "partial",
            exceptions.Length > 0 ? exceptions[0].Impact!
                : current ? "No active High or Critical pipeline problems reported on OT. SDR 1861 is not assessed."
                : "OT pipeline assessment is not current; operation cannot be assessed.",
            new RadioHealthCoverage(2, 2 - missing.Count, missing), candidates.Length, exceptions,
            new RadioHealthProtection("unknown", "Backup destination evidence is not connected", null, "unknown"),
            RecommendationsUrl);
    }

    public static bool IsEscalation(SystemRecommendationDto item)
        => item.Kind == "problem" && item.Severity is "high" or "critical"
            && item.ActivityState == "active"
            && item.WorkflowStatus is not ("known_issue" or "resolved" or "dismissed")
            && !item.Id.StartsWith("tr-rf-", StringComparison.Ordinal)
            && item.Target.SubTab != "rf"
            && !(item.Target.TopTab == "tr" && item.Target.SubTab == "metrics");

    private static string Consequence(string id) => id switch
    {
        "ingest-paused" or "tr-live-silent" => "New live calls may not enter the analysis pipeline.",
        "queue-pressure" or "remote-transcription-unavailable" => "Transcripts and downstream incident information may be delayed.",
        "ai-generation-health" => "Incident creation or updates may be incomplete or delayed.",
        "qdrant-unreachable" => "Incident analysis may use weaker matching while retrieval is unavailable.",
        "remote-transcription-bandwidth-critical" => "Transcription traffic may impair the remote link and pipeline dependencies.",
        "tr-resource-pressure" => "Capture resource pressure may interrupt delivery of new calls to analysis.",
        _ => "PizzaWave reports a High or Critical operational problem; review its finding for the effect on analysis."
    };

    private static string Identity(string id) => "radio:recommendation:" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16];

    private static string Bounded(string text, int limit)
    {
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.Trim().EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (bytes + rune.Utf8SequenceLength > limit) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}

public sealed class RadioHealthSummaryService(SystemRecommendationService recommendations)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RadioHealthAssessment _assessment = new();
    private RadioHealthSummary? _cached;

    public async Task<RadioHealthSummary> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached != null && DateTime.UtcNow - _cached.GeneratedAt < TimeSpan.FromSeconds(60)) return _cached;
            var source = await recommendations.BuildAsync(ct);
            _cached = _assessment.Assess(source, DateTime.UtcNow);
            return _cached;
        }
        finally { _gate.Release(); }
    }
}
