namespace pizzad;

public sealed class LiveTrActivityMonitor
{
    private static readonly TimeSpan DefaultStaleThreshold = TimeSpan.FromSeconds(60);
    private readonly object _sync = new();
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private DateTime? _lastLiveCallUtc;
    private DateTime? _lastTrHealthUtc;

    public void MarkLiveCall(DateTime utcNow)
    {
        lock (_sync)
        {
            _lastLiveCallUtc = utcNow.ToUniversalTime();
        }
    }

    public void MarkTrHealth(DateTime utcNow)
    {
        lock (_sync)
        {
            _lastTrHealthUtc = utcNow.ToUniversalTime();
        }
    }

    public LiveTrActivityStatusDto GetStatus(DateTime utcNow) => GetStatus(utcNow, DefaultStaleThreshold);

    public LiveTrActivityStatusDto GetStatus(DateTime utcNow, TrServiceFaultSnapshotDto? fault) =>
        ApplyFault(GetStatus(utcNow, DefaultStaleThreshold), fault, utcNow);

    public LiveTrActivityStatusDto GetStatus(DateTime utcNow, TrServiceFaultSnapshotDto? fault, TrServiceControlStateDto? controlState)
    {
        var status = ApplyFault(GetStatus(utcNow, DefaultStaleThreshold), fault, utcNow);
        if (fault != null && controlState != null && controlState.CreatedAtUtc.ToUniversalTime() < fault.CreatedAtUtc.ToUniversalTime())
            return status;
        return ApplyControlState(status, controlState, utcNow);
    }

    public LiveTrActivityStatusDto GetStatus(DateTime utcNow, TimeSpan staleThreshold)
    {
        utcNow = utcNow.ToUniversalTime();
        DateTime? lastLiveCall;
        DateTime? lastTrHealth;
        lock (_sync)
        {
            lastLiveCall = _lastLiveCallUtc;
            lastTrHealth = _lastTrHealthUtc;
        }

        var lastActivity = Max(lastLiveCall, lastTrHealth);
        var age = lastActivity.HasValue ? utcNow - lastActivity.Value : utcNow - _startedAtUtc;
        var stale = age > staleThreshold;
        var status = stale ? "stale" : lastActivity.HasValue ? "ok" : "warming";
        var message = status switch
        {
            "ok" => $"Live TR data was received {FormatAge(age)} ago.",
            "warming" => $"Waiting for live TR data; PizzaWave started {FormatAge(age)} ago.",
            _ => lastActivity.HasValue
                ? $"No live TR callstream or health data received for {FormatAge(age)}."
                : $"No live TR callstream or health data received since PizzaWave started {FormatAge(age)} ago."
        };

        return new LiveTrActivityStatusDto(
            status,
            stale,
            (int)Math.Round(staleThreshold.TotalSeconds),
            Math.Round(age.TotalSeconds, 1),
            lastActivity,
            lastLiveCall,
            lastTrHealth,
            _startedAtUtc,
            message);
    }

    private static DateTime? Max(DateTime? left, DateTime? right)
    {
        if (!left.HasValue) return right;
        if (!right.HasValue) return left;
        return left.Value >= right.Value ? left : right;
    }

    private static LiveTrActivityStatusDto ApplyFault(LiveTrActivityStatusDto status, TrServiceFaultSnapshotDto? fault, DateTime utcNow)
    {
        if (fault == null)
            return status;

        var faultUtc = fault.CreatedAtUtc.ToUniversalTime();
        if (status.LastActivityUtc.HasValue && faultUtc < status.LastActivityUtc.Value.ToUniversalTime())
            return status;

        var age = utcNow.ToUniversalTime() - faultUtc;
        var signatures = fault.Signatures.Count > 0 ? $" Signatures: {string.Join(", ", fault.Signatures.Take(6))}." : string.Empty;
        return status with
        {
            Status = "fault",
            Stale = true,
            AgeSeconds = Math.Round(Math.Max(0, age.TotalSeconds), 1),
            Message = $"trunk-recorder faulted {FormatAge(age)} ago: result={fault.ServiceResult}, exit={fault.ExitCode}/{fault.ExitStatus}.{signatures}"
        };
    }

    private static LiveTrActivityStatusDto ApplyControlState(LiveTrActivityStatusDto status, TrServiceControlStateDto? controlState, DateTime utcNow)
    {
        if (controlState == null || !string.Equals(controlState.State, "stopped", StringComparison.OrdinalIgnoreCase))
            return status;

        var stoppedUtc = controlState.CreatedAtUtc.ToUniversalTime();
        if (status.LastActivityUtc.HasValue && stoppedUtc < status.LastActivityUtc.Value.ToUniversalTime())
            return status;

        var age = utcNow.ToUniversalTime() - stoppedUtc;
        return status with
        {
            Status = "stopped",
            Stale = false,
            AgeSeconds = Math.Round(Math.Max(0, age.TotalSeconds), 1),
            Message = $"trunk-recorder was intentionally stopped {FormatAge(age)} ago. Live capture will remain stopped until TR is started or restarted."
        };
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalSeconds < 90) return $"{Math.Max(0, Math.Round(age.TotalSeconds)):N0}s";
        if (age.TotalMinutes < 90) return $"{age.TotalMinutes:N1}m";
        return $"{age.TotalHours:N1}h";
    }
}
