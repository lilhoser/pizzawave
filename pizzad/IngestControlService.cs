namespace pizzad;

public sealed class IngestControlService
{
    private readonly object _lock = new();
    private readonly ILogger<IngestControlService> _logger;
    private readonly EngineDatabase _database;
    private bool _startupRecoveryComplete;
    private string _stateStatus = "durable";
    private DateTimeOffset _lastDropLogAt = DateTimeOffset.MinValue;

    public IngestControlService(EngineDatabase database, ILogger<IngestControlService> logger)
    {
        _logger = logger;
        _database = database;
        try
        {
            Apply(database.LoadIngestPause());
        }
        catch (Exception ex)
        {
            Paused = true;
            Reason = "Stored ingest pause state is unavailable; review it before explicitly resuming.";
            _stateStatus = "unavailable";
            _logger.LogError("Ingest pause restore failed ({ErrorType}); live ingest remains paused", ex.GetType().Name);
        }
    }

    public bool Paused { get; private set; }
    public bool UntilQueueClear { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public DateTime? PausedAtUtc { get; private set; }
    public long DroppedCalls { get; private set; }
    public long DroppedCallsAtPause { get; private set; }

    // Startup's empty RAM queue is not evidence that persisted work has drained.
    public void CompleteStartupRecovery()
    {
        lock (_lock) _startupRecoveryComplete = true;
    }

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
            var state = new DurableIngestPause(true, untilQueueClear,
                string.IsNullOrWhiteSpace(reason)
                    ? untilQueueClear ? "Paused until transcription queue clears." : "Paused by user."
                    : reason.Trim(), PausedAtUtc ?? DateTime.UtcNow);
            Persist(state); // A successful response means the intent is committed, not merely cached.
            if (!Paused) DroppedCallsAtPause = DroppedCalls;
            Apply(state);
            _logger.LogWarning("Live callstream ingest paused. untilQueueClear={UntilQueueClear}; queueDepth={QueueDepth}; reason={Reason}", UntilQueueClear, queueDepth, Reason);
            return BuildStatus();
        }
    }

    public IngestControlStatusDto Resume(string reason, int queueDepth)
    {
        lock (_lock)
        {
            var wasPaused = Paused;
            Persist(new DurableIngestPause(false, false, string.Empty, null));
            Apply(new DurableIngestPause(false, false, string.Empty, null));
            DroppedCallsAtPause = DroppedCalls;
            if (wasPaused)
                _logger.LogInformation("Live callstream ingest resumed. queueDepth={QueueDepth}; reason={Reason}", queueDepth, reason);
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
            if (!Paused || !UntilQueueClear || !_startupRecoveryComplete || _stateStatus != "durable")
                return;

            try
            {
                Persist(new DurableIngestPause(false, false, string.Empty, null));
                Apply(new DurableIngestPause(false, false, string.Empty, null));
                DroppedCallsAtPause = DroppedCalls;
                _logger.LogInformation("Live callstream ingest auto-resumed because the recovered transcription queue cleared.");
            }
            catch (Exception)
            {
                // A failed durable release must not drop a manual hold or interrupt health reporting.
            }
        }
    }

    private void Persist(DurableIngestPause state)
    {
        try
        {
            _database.SaveIngestPause(state);
            _stateStatus = "durable";
        }
        catch (Exception ex)
        {
            _stateStatus = "unavailable";
            _logger.LogError("Ingest pause write failed ({ErrorType}); no release was applied", ex.GetType().Name);
            throw;
        }
    }

    private void Apply(DurableIngestPause state)
    {
        Paused = state.Paused;
        UntilQueueClear = state.UntilQueueClear;
        Reason = state.Reason;
        PausedAtUtc = state.PausedAtUtc;
    }

    private IngestControlStatusDto BuildStatus()
    {
        var droppedThisPause = Paused ? Math.Max(0, DroppedCalls - DroppedCallsAtPause) : 0;
        return new IngestControlStatusDto(Paused, UntilQueueClear, Reason, PausedAtUtc, DroppedCalls, droppedThisPause) { StateStatus = _stateStatus };
    }
}
