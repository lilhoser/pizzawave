using System.Collections.Concurrent;

namespace pizzad;

public sealed class BackupJobService : IHostedService
{
    public const string JobType = "system_backup";
    private static readonly TimeSpan StaleBackupJobAge = TimeSpan.FromHours(12);
    private readonly BackupRestoreService _backups;
    private readonly EngineDatabase _database;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BackupJobService> _logger;
    private readonly RecoveryOperationCoordinator _recovery;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public BackupJobService(
        BackupRestoreService backups,
        EngineDatabase database,
        IHostApplicationLifetime lifetime,
        ILogger<BackupJobService> logger,
        RecoveryOperationCoordinator? recovery = null)
    {
        _backups = backups;
        _database = database;
        _lifetime = lifetime;
        _logger = logger;
        _recovery = recovery ?? new RecoveryOperationCoordinator();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _backups.CleanupInterruptedWork();
        var canceled = await _database.CancelStaleActiveJobsAsync(
            JobType,
            TimeSpan.Zero,
            "Backup job was interrupted by a PizzaWave service restart. Start a new backup if needed.",
            cancellationToken);
        if (canceled > 0)
            _logger.LogWarning("Marked {Count} interrupted backup job(s) canceled on startup", canceled);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var worker in _active.Values)
            worker.Cancel();
        return Task.CompletedTask;
    }

    public async Task<JobDto> StartAsync(BackupCreateRequestDto? request, CancellationToken ct)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            await _database.CancelStaleActiveJobsAsync(
                JobType,
                StaleBackupJobAge,
                "Backup job was abandoned by a service restart or shutdown.",
                ct);

            foreach (var type in new[] { JobType, RecoveryJobService.SupportPackageJobType, RecoveryJobService.RestoreApplyJobType, RecoveryJobService.ResetJobType })
                if (await _database.HasActiveJobAsync(type, ct))
                    throw new InvalidOperationException("Another backup, restore, reset, or support-package job is already active.");

            var jobId = await _database.AddJobAsync(new JobDto
            {
                Type = JobType,
                Status = "queued",
                Total = 1,
                Completed = 0,
                Failed = 0,
                Message = "Backup job queued.",
                CreatedAtUtc = DateTime.UtcNow
            }, ct);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            _active[jobId] = cts;
            _ = Task.Run(() => RunAsync(jobId, request, cts.Token), CancellationToken.None);
            return await _database.GetJobAsync(jobId, ct) ?? new JobDto { Id = jobId, Type = JobType, Status = "queued", Total = 1, Message = "Backup job queued.", CreatedAtUtc = DateTime.UtcNow };
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<JobDto?> CancelAsync(long jobId, CancellationToken ct)
    {
        var job = await _database.GetJobAsync(jobId, ct);
        if (job == null)
            return null;
        if (!string.Equals(job.Type, JobType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This job is not a backup job.");
        if (job.Status is not ("queued" or "running" or "paused" or "canceling"))
            return job;

        await _database.UpdateJobAsync(jobId, "canceling", null, null, null, "Cancel requested. Stopping backup archive creation...", false, false, ct);
        await _database.AddJobLogAsync(jobId, "warning", "Operator requested backup cancellation.", ct);
        if (_active.TryGetValue(jobId, out var cts))
        {
            cts.Cancel();
        }
        else
        {
            await _database.UpdateJobAsync(jobId, "canceled", 1, 0, 0, "Backup job was not active in this process and was marked canceled.", false, true, ct);
            await _database.AddJobLogAsync(jobId, "warning", "No active backup worker was found for this job; marked canceled.", ct);
        }

        return await _database.GetJobAsync(jobId, ct);
    }

    private async Task RunAsync(long jobId, BackupCreateRequestDto? request, CancellationToken ct)
    {
        try
        {
            using var recoveryLease = _recovery.Acquire("backup creation");
            await _database.UpdateJobAsync(jobId, "running", 1, 0, 0, "Creating backup archive...", true, false, CancellationToken.None);
            await _database.AddJobLogAsync(jobId, "info", $"Backup archive started. Audio window: {BackupCreateOptions.From(request).AudioWindow}.", CancellationToken.None);
            var result = await _backups.CreateBackupAsync(request, ct);
            var warningText = result.Warnings.Count > 0 ? " Warnings: " + string.Join(" ", result.Warnings) : string.Empty;
            var message = $"Created {result.Name}: {FormatBytes(result.Bytes)} across {result.FileCount:N0} file(s).{warningText}";
            await _database.AddJobLogAsync(jobId, "info", $"Backup archive created at {result.Path}.", CancellationToken.None);
            foreach (var warning in result.Warnings)
                await _database.AddJobLogAsync(jobId, "warning", warning, CancellationToken.None);
            await _database.UpdateJobAsync(jobId, "completed", 1, 1, 0, message, false, true, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await _database.AddJobLogAsync(jobId, "warning", "Backup archive creation was canceled.", CancellationToken.None);
            await _database.UpdateJobAsync(jobId, "canceled", 1, 0, 0, "Backup canceled. Partial working files may be cleaned up by the backup service.", false, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup job {JobId} failed", jobId);
            await _database.AddJobLogAsync(jobId, "error", ex.Message, CancellationToken.None);
            await _database.UpdateJobAsync(jobId, "failed", 1, 0, 1, "Backup failed: " + ex.Message, false, true, CancellationToken.None);
        }
        finally
        {
            if (_active.TryRemove(jobId, out var cts))
                cts.Dispose();
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {units[unit]}";
    }
}
