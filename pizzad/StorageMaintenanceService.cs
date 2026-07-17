namespace pizzad;

public sealed class StorageMaintenanceService : BackgroundService
{
    public const string JobType = "system_storage_maintenance";
    private static readonly TimeSpan NormalInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly ILogger<StorageMaintenanceService> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public StorageMaintenanceService(EngineDatabase database, EventStream events, ILogger<StorageMaintenanceService> logger)
    {
        _database = database;
        _events = events;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var canceled = await _database.CancelStaleActiveJobsAsync(
            JobType,
            TimeSpan.Zero,
            "Automatic storage maintenance was interrupted by a PizzaWave restart.",
            stoppingToken);
        if (canceled > 0)
            _logger.LogWarning("Marked {Count} interrupted storage maintenance job(s) canceled", canceled);

        await Task.Delay(StartupDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunIfDueAsync(DateTime.UtcNow, false, stoppingToken);
            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    public async Task<JobDto?> RunIfDueAsync(DateTime nowUtc, bool force, CancellationToken ct)
    {
        if (!await _runGate.WaitAsync(0, ct))
            return null;

        try
        {
            var latest = await _database.GetLatestJobByTypeAsync(JobType, ct);
            if (!force && latest != null)
            {
                if (latest.Status is "queued" or "running" or "paused" or "canceling")
                    return null;
                var lastActivity = latest.FinishedAtUtc ?? latest.UpdatedAtUtc ?? latest.CreatedAtUtc;
                var interval = latest.Status == "completed" ? NormalInterval : FailureRetryInterval;
                if (nowUtc.ToUniversalTime() - lastActivity.ToUniversalTime() < interval)
                    return null;
            }

            var jobId = await _database.AddJobAsync(new JobDto
            {
                Type = JobType,
                Status = "queued",
                Total = 2,
                Message = "Automatic storage maintenance queued.",
                CreatedAtUtc = nowUtc.ToUniversalTime()
            }, ct);
            await PublishAsync(jobId, ct);

            try
            {
                await _database.UpdateJobAsync(jobId, "running", 2, 0, 0, "Optimizing SQLite query planning...", true, false, ct);
                await _database.AddJobLogAsync(jobId, "info", "Started automatic storage maintenance. Vacuum is not run.", ct);
                await _database.OptimizeAsync(ct);
                await _database.AddJobLogAsync(jobId, "info", "SQLite PRAGMA optimize completed.", ct);
                await _database.UpdateJobAsync(jobId, "running", 2, 1, 0, "Pruning completed job history older than 30 days...", false, false, ct);

                var pruned = await _database.PruneJobsOlderThanAsync(nowUtc.ToUniversalTime().AddDays(-30), ct);
                var message = $"Optimized SQLite and pruned {pruned.JobsRemoved:N0} old job(s) with {pruned.JobLogsRemoved:N0} log row(s).";
                await _database.AddJobLogAsync(jobId, "info", message, ct);
                await _database.UpdateJobAsync(jobId, "completed", 2, 2, 0, message, false, true, ct);
            }
            catch (OperationCanceledException)
            {
                await _database.AddJobLogAsync(jobId, "warning", "Automatic storage maintenance was canceled during shutdown.", CancellationToken.None);
                await _database.UpdateJobAsync(jobId, "canceled", 2, null, null, "Automatic storage maintenance was interrupted by shutdown.", false, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic storage maintenance job {JobId} failed", jobId);
                await _database.AddJobLogAsync(jobId, "error", ex.Message, CancellationToken.None);
                await _database.UpdateJobAsync(jobId, "failed", 2, null, 1, "Automatic storage maintenance failed: " + ex.Message, false, true, CancellationToken.None);
            }

            await PublishAsync(jobId, CancellationToken.None);
            return await _database.GetJobAsync(jobId, CancellationToken.None);
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task PublishAsync(long jobId, CancellationToken ct)
    {
        var job = await _database.GetJobAsync(jobId, ct);
        if (job != null)
            await _events.PublishAsync("job_updated", JobControlPolicy.Describe(job), ct);
    }
}
