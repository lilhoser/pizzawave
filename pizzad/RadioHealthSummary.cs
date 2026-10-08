using System.Text.Json;

namespace pizzad;

public sealed record RadioHealthException(string Id, string Severity, string Capability,
    string Summary, DateTime FirstObservedAt, DateTime ObservedAt, string? CauseRef = null);
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

/// <summary>Maps existing domain assessments; it performs no network or device checks.</summary>
public sealed class RadioHealthAssessment
{
    private readonly Dictionary<string, DateTime> _firstObserved = new(StringComparer.Ordinal);

    public RadioHealthSummary Assess(HealthDto health, LiveRfStatusDto rf, DateTime now)
    {
        var missing = new List<string> { "sdr-1861" };
        var required = 4;
        var exceptions = new List<RadioHealthException>();
        var fresh = now - health.ServerTimeUtc <= TimeSpan.FromMinutes(3)
            && health.ServerTimeUtc - now <= TimeSpan.FromSeconds(30);
        if (!fresh || health.Status is not ("ok" or "degraded"))
            missing.Add("ot-processing");
        else
        {
            if (health.QueueUnderPressure)
                Add("radio:queue-pressure", "high", "OT transcription",
                    "OT transcription is under pressure", health.ServerTimeUtc);
            if (health.IncidentAnalysisQueueHealth.Status is not ("ok" or "unknown" or "disabled"))
                Add("radio:incident-analysis", "high", "OT incident analysis",
                    "OT incident analysis is delayed or blocked", health.ServerTimeUtc);
            if (health.AiCompletionHealth.Status is not ("ok" or "unknown" or "disabled"))
                Add("radio:ai-completion", "high", "OT AI processing",
                    "OT AI completion reports degraded operation", health.ServerTimeUtc);
            if (health.EmbeddingHealth.Enabled && health.EmbeddingHealth.Status != "ok")
                Add("radio:embeddings", "high", "OT retrieval",
                    "OT embedding or retrieval service is degraded", health.ServerTimeUtc);
            if (!string.IsNullOrWhiteSpace(health.AiWorkBlockedReason))
                Add("radio:ai-blocked", "medium", "OT AI processing",
                    "OT AI work is paused by the existing queue policy", health.ServerTimeUtc);
            if (health.Status == "degraded" && exceptions.Count == 0)
                Add("radio:processing-degraded", "high", "OT processing",
                    "OT processing reports degraded operation", health.ServerTimeUtc);
        }

        var activity = health.LiveTrActivity;
        var intentionallyStopped = fresh && activity.Status == "stopped";
        if (!fresh)
            missing.Add("ot-receiver");
        else if (intentionallyStopped)
            required--; // Explicit domain control state: not an unexpected outage.
        else if (activity.Status is "fault" or "failed")
            Add("radio:capture", "high", "OT live capture",
                "OT live capture reports a fault", health.ServerTimeUtc);
        else if (activity.Stale || activity.Status != "ok")
            missing.Add("ot-receiver");

        var rfFresh = now - rf.GeneratedAtUtc <= TimeSpan.FromMinutes(3)
            && rf.GeneratedAtUtc - now <= TimeSpan.FromSeconds(30);
        if (intentionallyStopped)
            required--;
        else if (!rfFresh || rf.Sites.Count == 0 || rf.Sites.Any(site => site.Tone is "unknown" or "stale"))
            missing.Add("ot-rf");
        if (!intentionallyStopped && rfFresh && rf.Sites.Any(site => site.Tone is "error" or "warning"))
            Add("radio:rf", "high", "OT reception",
                "OT RF assessment reports degraded reception", rf.GeneratedAtUtc);

        foreach (var resolved in _firstObserved.Keys.Except(exceptions.Select(item => item.Id)).ToArray())
            _firstObserved.Remove(resolved);
        var selected = exceptions.OrderByDescending(item => item.Severity == "high")
            .ThenBy(item => item.Id, StringComparer.Ordinal).Take(3).ToArray();
        return new RadioHealthSummary(1, "radio", Guid.NewGuid().ToString("N"), now,
            now.AddMinutes(15), exceptions.Count > 0 ? "degraded" : "unknown", "partial",
            selected.Length > 0 ? selected[0].Summary
                : intentionallyStopped ? "OT capture is intentionally stopped; Radio coverage is incomplete"
                : "No reported fault in observed OT assessments; Radio coverage is incomplete",
            new RadioHealthCoverage(required, required - missing.Count, missing), exceptions.Count, selected,
            new RadioHealthProtection("unknown", "Backup destination evidence is not connected",
                null, "unknown"), "/dashboard-home/pizzawave");

        void Add(string id, string severity, string capability, string summary, DateTime observed)
        {
            if (!_firstObserved.TryGetValue(id, out var first))
                _firstObserved[id] = first = now;
            exceptions.Add(new RadioHealthException(id, severity, capability, summary, first, observed));
        }
    }
}

public sealed class RadioHealthSummaryService(HealthStatusService health, LiveRfStatusService rf)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RadioHealthAssessment _assessment = new();
    private RadioHealthSummary? _cached;

    public async Task<RadioHealthSummary> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            if (_cached != null && now - _cached.GeneratedAt < TimeSpan.FromSeconds(60))
                return _cached;
            _cached = _assessment.Assess(await health.GetAsync(ct), rf.GetSnapshot(), now);
            return _cached;
        }
        finally { _gate.Release(); }
    }
}
