using System.Collections.Concurrent;

namespace pizzad;

public sealed class RecoveryJobService : IHostedService
{
    public const string SupportPackageJobType = "support_package";
    public const string RestoreApplyJobType = "restore_apply";
    public const string ResetJobType = "system_reset";
    private static readonly string[] JobTypes = [BackupJobService.JobType, SupportPackageJobType, RestoreApplyJobType, ResetJobType];
    private readonly EngineDatabase _database;
    private readonly SupportPackageService _supportPackages;
    private readonly BackupRestoreService _backups;
    private readonly SystemResetService _reset;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RecoveryJobService> _logger;
    private readonly RecoveryResultStore _results;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();

    public RecoveryJobService(EngineDatabase database, SupportPackageService supportPackages, BackupRestoreService backups, SystemResetService reset, IHostApplicationLifetime lifetime, ILogger<RecoveryJobService> logger, RecoveryResultStore results)
    {
        _database = database;
        _supportPackages = supportPackages;
        _backups = backups;
        _reset = reset;
        _lifetime = lifetime;
        _logger = logger;
        _results = results;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var type in new[] { SupportPackageJobType, RestoreApplyJobType, ResetJobType })
            await _database.CancelStaleActiveJobsAsync(type, TimeSpan.Zero, "Recovery job was interrupted by a PizzaWave service restart. Passphrases are never persisted, so the job cannot resume.", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var cts in _active.Values) cts.Cancel();
        return Task.CompletedTask;
    }

    public Task<JobDto> StartSupportPackageAsync(SupportPackageCreateRequestDto request, CancellationToken ct) =>
        StartAsync(SupportPackageJobType, "Support package queued.", token => RunSupportPackageAsync(request, token), ct);

    public Task<JobDto> StartRestoreApplyAsync(string? passphrase, CancellationToken ct) =>
        StartAsync(RestoreApplyJobType, "Restore apply queued.", token => RunRestoreApplyAsync(passphrase, token), ct);

    public Task<JobDto> StartResetAsync(SystemResetRequestDto request, CancellationToken ct) =>
        StartAsync(ResetJobType, "System reset queued.", token => RunResetAsync(request, token), ct);

    public async Task<JobDto?> CancelSupportPackageAsync(long jobId, CancellationToken ct)
    {
        var job = await _database.GetJobAsync(jobId, ct);
        if (job == null) return null;
        if (!string.Equals(job.Type, SupportPackageJobType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only support-package recovery jobs can be safely canceled through this action.");
        if (_active.TryGetValue(jobId, out var cts))
        {
            await _database.UpdateJobAsync(jobId, "canceling", null, null, null, "Cancel requested. Discarding the unfinished support package...", false, false, ct);
            cts.Cancel();
        }
        return await _database.GetJobAsync(jobId, ct);
    }

    private async Task<JobDto> StartAsync(string type, string queuedMessage, Func<CancellationToken, Task<string>> action, CancellationToken ct)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            foreach (var jobType in JobTypes)
                if (await _database.HasActiveJobAsync(jobType, ct))
                    throw new InvalidOperationException("Another backup, restore, reset, or support-package job is already active.");
            var id = await _database.AddJobAsync(new JobDto { Type = type, Status = "queued", Total = 1, Message = queuedMessage, CreatedAtUtc = DateTime.UtcNow }, ct);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            _active[id] = cts;
            _ = Task.Run(() => RunAsync(id, type, action, cts.Token), CancellationToken.None);
            return await _database.GetJobAsync(id, ct) ?? new JobDto { Id = id, Type = type, Status = "queued", Total = 1, Message = queuedMessage, CreatedAtUtc = DateTime.UtcNow };
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task RunAsync(long id, string type, Func<CancellationToken, Task<string>> action, CancellationToken ct)
    {
        try
        {
            var operation = OperationName(type);
            await _results.StartAsync(operation, id, $"{type} job #{id} started.", CancellationToken.None);
            await _database.UpdateJobAsync(id, "running", 1, 0, 0, "Recovery operation in progress...", true, false, CancellationToken.None);
            await _database.AddJobLogAsync(id, "info", $"{type} started. Browser connection is not required.", CancellationToken.None);
            var message = await action(ct);
            await _results.AppendAsync(operation, "finished", "completed", message, true, CancellationToken.None);
            await _database.AddJobLogAsync(id, "info", message, CancellationToken.None);
            await _database.UpdateJobAsync(id, "completed", 1, 1, 0, message, false, true, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await _results.AppendAsync(OperationName(type), "interrupted", "canceled", "Operation was interrupted or canceled; no passphrase was persisted.", true, CancellationToken.None);
            await _database.AddJobLogAsync(id, "warning", "Recovery operation was interrupted or canceled.", CancellationToken.None);
            await _database.UpdateJobAsync(id, "canceled", 1, 0, 0, "Recovery operation was interrupted or canceled. No passphrase was persisted.", false, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _results.AppendAsync(OperationName(type), "failed", "failed", ex.Message, true, CancellationToken.None);
            _logger.LogError(ex, "Recovery job {JobId} ({Type}) failed", id, type);
            await _database.AddJobLogAsync(id, "error", ex.Message, CancellationToken.None);
            await _database.UpdateJobAsync(id, "failed", 1, 0, 1, "Recovery operation failed: " + ex.Message, false, true, CancellationToken.None);
        }
        finally
        {
            if (_active.TryRemove(id, out var cts)) cts.Dispose();
        }
    }

    private static string OperationName(string type) => type switch
    {
        SupportPackageJobType => "support-package",
        RestoreApplyJobType => "restore",
        ResetJobType => "reset",
        _ => type
    };

    private async Task<string> RunSupportPackageAsync(SupportPackageCreateRequestDto request, CancellationToken ct)
    {
        var result = await _supportPackages.CreateAsync(request, ct);
        return $"Created {result.Name}: {result.Bytes:N0} bytes, {result.Manifest.RedactionCount:N0} redaction(s), {result.Manifest.CollectionFailures.Count:N0} unavailable evidence source(s).";
    }

    private async Task<string> RunRestoreApplyAsync(string? passphrase, CancellationToken ct)
    {
        var result = await _backups.ApplyPendingRestoreAsync(passphrase, ct);
        return result.Message;
    }

    private async Task<string> RunResetAsync(SystemResetRequestDto request, CancellationToken ct)
    {
        var result = await _reset.ResetAsync(request, ct);
        return result.Message + (result.Backup == null ? string.Empty : $" Safety backup: {result.Backup.Name}.");
    }
}
