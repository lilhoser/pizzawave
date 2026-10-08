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
                    "OT transcription is falling behind", health.ServerTimeUtc,
                    "New transcripts may arrive late.",
                    $"{health.PendingTranscriptions} calls await transcription; {health.LiveQueueDepth} live calls are queued.",
                    "Review the transcription queue and worker health.");
            if (health.IncidentAnalysisQueueHealth.Status is not ("ok" or "unknown" or "disabled"))
                Add("radio:incident-analysis", "high", "OT incident analysis",
                    "OT incident analysis is delayed or blocked", health.ServerTimeUtc,
                    "Incident information may lag behind received calls.",
                    $"{health.IncidentAnalysisQueueHealth.PendingCalls} calls pending; oldest is {health.IncidentAnalysisQueueHealth.OldestPendingAgeMinutes:F0} minutes old.",
                    "Review incident analysis and its processing dependencies.");
            if (health.AiCompletionHealth.Status is not ("ok" or "unknown" or "disabled"))
                Add("radio:ai-completion", "high", "OT AI processing",
                    "OT AI processing is failing", health.ServerTimeUtc,
                    "AI-generated incident information may be incomplete or delayed.",
                    $"{health.AiCompletionHealth.Failures} failed requests in {health.AiCompletionHealth.WindowMinutes} minutes.",
                    "Review AI completion health and recent failures.");
            if (health.EmbeddingHealth.Enabled && health.EmbeddingHealth.Status != "ok")
                Add("radio:embeddings", "high", "OT retrieval",
                    "OT search indexing is impaired", health.ServerTimeUtc,
                    "New information may be missing from search and retrieval.",
                    "The existing embedding or retrieval assessment is not healthy.",
                    "Review embedding and retrieval service health.");
            if (!string.IsNullOrWhiteSpace(health.AiWorkBlockedReason))
                Add("radio:ai-blocked", "medium", "OT AI processing",
                    "OT AI work is paused by the existing queue policy", health.ServerTimeUtc,
                    "Some AI work is waiting; recording and transcription are assessed separately.",
                    "The existing processing policy reports paused AI work.",
                    "Review the processing queue and dependency health.");
            if (health.Status == "degraded" && exceptions.Count == 0)
                Add("radio:processing-degraded", "high", "OT processing",
                    "OT processing reports degraded operation", health.ServerTimeUtc,
                    "Operational impact has not been established.",
                    "The source reports a processing fault without a more specific assessment.",
                    "Review OT health diagnostics.");
        }

        var activity = health.LiveTrActivity;
        var intentionallyStopped = fresh && activity.Status == "stopped";
        if (!fresh)
            missing.Add("ot-receiver");
        else if (intentionallyStopped)
            required--; // Explicit domain control state: not an unexpected outage.
        else if (activity.Status is "fault" or "failed")
            Add("radio:capture", "critical", "OT live capture",
                "OT live capture reports a fault", health.ServerTimeUtc,
                "New calls may not be recorded on OT.",
                "The receiver reports a fault or failed state.",
                "Review capture service diagnostics before restarting equipment.");
        else if (activity.Stale || activity.Status != "ok")
            missing.Add("ot-receiver");

        var rfFresh = now - rf.GeneratedAtUtc <= TimeSpan.FromMinutes(3)
            && rf.GeneratedAtUtc - now <= TimeSpan.FromSeconds(30);
        if (intentionallyStopped)
            required--;
        else if (!rfFresh || rf.Sites.Count == 0 || rf.Sites.Any(site => site.Tone is "unknown" or "stale"))
            missing.Add("ot-rf");
        if (!intentionallyStopped && rfFresh)
        {
            foreach (var site in rf.Sites.Where(site => site.Tone is "error" or "warning")
                .OrderBy(site => site.SystemShortName, StringComparer.Ordinal))
            {
                var name = Bounded(site.SystemShortName, 48);
                var assessments = new[] { (Kind: "decode", Value: site.DecodeAssessment),
                    (Kind: "gaps", Value: site.ZeroDecodeAssessment), (Kind: "retunes", Value: site.RetunesAssessment) };
                var problem = assessments.Where(item => item.Value?.Tone is "error" or "warning")
                    .OrderByDescending(item => item.Value!.Tone == "error").FirstOrDefault();
                var recovering = site.Status == "Recovering";
                var explanation = recovering ? "reception recovering"
                    : problem.Kind == "decode" ? (problem.Value!.Tone == "error"
                        ? "radio traffic cannot be decoded reliably" : "reception below its expected level")
                    : problem.Kind == "gaps" ? "gaps in radio reception"
                    : problem.Kind == "retunes" ? "receiver repeatedly changes channels"
                    : "reception needs review";
                var impact = recovering ? "Current RF readings are healthy; awaiting stable recovery confirmation."
                    : site.Tone == "error" ? "Calls from this site may be missed; missed-call count is not measured."
                    : "Reception has a warning; loss of calls has not been established.";
                var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(site.SystemShortName)))[..16];
                Add("radio:rf:" + identity, site.Tone == "error" ? "critical" : "medium", "OT reception",
                    $"OT / {name}: {explanation}", rf.GeneratedAtUtc, impact,
                    recovering ? "The RF service is holding its prior warning until recovery is stable."
                        : problem.Value is not null ? Bounded(problem.Value.Detail, 160)
                        : "The RF service reports a warning without a specific component assessment.",
                    "Review this site's reception assessment before changing receiver settings.",
                    "http://192.168.1.173:8080/api/v1/system/rf/live");
            }
        }

        foreach (var resolved in _firstObserved.Keys.Except(exceptions.Select(item => item.Id)).ToArray())
            _firstObserved.Remove(resolved);
        var selected = exceptions.OrderByDescending(item => item.Severity == "critical" ? 3 : item.Severity == "high" ? 2 : 1)
            .ThenBy(item => item.Id, StringComparer.Ordinal).Take(3).ToArray();
        return new RadioHealthSummary(1, "radio", Guid.NewGuid().ToString("N"), now,
            now.AddMinutes(15), exceptions.Count > 0 ? "degraded" : "unknown", "partial",
            selected.Length > 0 ? selected[0].Impact!
                : intentionallyStopped ? "OT capture is intentionally stopped; Radio coverage is incomplete"
                : "No reported fault in observed OT assessments; Radio coverage is incomplete",
            new RadioHealthCoverage(required, required - missing.Count, missing), exceptions.Count, selected,
            new RadioHealthProtection("unknown", "Backup destination evidence is not connected",
                null, "unknown"), "/dashboard-home/pizzawave");

        void Add(string id, string severity, string capability, string summary, DateTime observed,
            string? impact = null, string? evidence = null, string? action = null, string? detailsUrl = null)
        {
            if (!_firstObserved.TryGetValue(id, out var first))
                _firstObserved[id] = first = now;
            exceptions.Add(new RadioHealthException(id, severity, capability, summary, first, observed,
                Impact: impact ?? "Operational impact has not been established.", Evidence: evidence,
                RecommendedAction: action, DetailsUrl: detailsUrl ?? "http://192.168.1.173:8080/api/v1/health"));
        }
    }

    private static string Bounded(string text, int limit)
    {
        var result = new System.Text.StringBuilder();
        var bytes = 0;
        foreach (var rune in text.Trim().EnumerateRunes())
        {
            if (System.Text.Rune.IsControl(rune)) continue;
            if (bytes + rune.Utf8SequenceLength > limit) break;
            result.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
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
