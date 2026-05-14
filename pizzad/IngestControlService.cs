namespace pizzad;

public sealed class IngestControlService
{
    private readonly object _lock = new();
    private readonly ILogger<IngestControlService> _logger;
    private DateTimeOffset _lastDropLogAt = DateTimeOffset.MinValue;

    public IngestControlService(ILogger<IngestControlService> logger)
    {
        _logger = logger;
    }

    public bool Paused { get; private set; }
    public bool UntilQueueClear { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTime? PausedAtUtc { get; private set; }
    public long DroppedCalls { get; private set; }
    public long DroppedCallsAtPause { get; private set; }

    public IngestControlStatusDto GetStatus(int queueDepth)
    {
        MaybeAutoResume(queueDepth);
        lock (_lock)
        {
            return BuildStatus();
        }
    }

    public IngestControlStatusDto Pause(bool untilQueueClear, string? reason, int queueDepth)
    {
        lock (_lock)
        {
            if (!Paused)
                DroppedCallsAtPause = DroppedCalls;
            Paused = true;
            UntilQueueClear = untilQueueClear;
            Reason = string.IsNullOrWhiteSpace(reason)
                ? untilQueueClear ? "Paused until transcription queue clears." : "Paused by user."
                : reason.Trim();
            PausedAtUtc = DateTime.UtcNow;
            _logger.LogWarning("Live callstream ingest paused. untilQueueClear={UntilQueueClear}; queueDepth={QueueDepth}; reason={Reason}", UntilQueueClear, queueDepth, Reason);
            return BuildStatus();
        }
    }

    public IngestControlStatusDto Resume(string reason, int queueDepth)
    {
        lock (_lock)
        {
            if (Paused)
                _logger.LogInformation("Live callstream ingest resumed. queueDepth={QueueDepth}; reason={Reason}", queueDepth, reason);
            Paused = false;
            UntilQueueClear = false;
            Reason = string.Empty;
            PausedAtUtc = null;
            DroppedCallsAtPause = DroppedCalls;
            return BuildStatus();
        }
    }

    public bool ShouldDropLiveCall(int queueDepth)
    {
        MaybeAutoResume(queueDepth);
        lock (_lock)
        {
            if (!Paused)
                return false;

            DroppedCalls++;
            if (DateTimeOffset.UtcNow >= _lastDropLogAt)
            {
                _logger.LogWarning("Dropping live callstream payload while ingest is paused. droppedCalls={DroppedCalls}; queueDepth={QueueDepth}; reason={Reason}", DroppedCalls, queueDepth, Reason);
                _lastDropLogAt = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            return true;
        }
    }

    private void MaybeAutoResume(int queueDepth)
    {
        if (queueDepth > 0)
            return;

        lock (_lock)
        {
            if (!Paused || !UntilQueueClear)
                return;

            _logger.LogInformation("Live callstream ingest auto-resumed because transcription queue cleared.");
            Paused = false;
            UntilQueueClear = false;
            Reason = string.Empty;
            PausedAtUtc = null;
            DroppedCallsAtPause = DroppedCalls;
        }
    }

    private IngestControlStatusDto BuildStatus()
    {
        var droppedThisPause = Paused ? Math.Max(0, DroppedCalls - DroppedCallsAtPause) : 0;
        return new IngestControlStatusDto(Paused, UntilQueueClear, Reason, PausedAtUtc, DroppedCalls, droppedThisPause);
    }
}
