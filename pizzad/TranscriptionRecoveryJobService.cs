using System.Collections.Concurrent;

namespace pizzad;

public sealed class TranscriptionRecoveryJobService : IHostedService
{
    public const string JobType = "transcription_failure_recovery";
    private readonly EngineDatabase _database;
    private readonly EnginePipeline _pipeline;
    private readonly EventStream _events;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<TranscriptionRecoveryJobService> _logger;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _active = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public TranscriptionRecoveryJobService(EngineDatabase database, EnginePipeline pipeline, EventStream events, IHostApplicationLifetime lifetime, ILogger<TranscriptionRecoveryJobService> logger)
    {
        _database = database;
        _pipeline = pipeline;
        _events = events;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.CancelStaleActiveJobsAsync(JobType, TimeSpan.Zero, "Transcription recovery was interrupted by a PizzaWave restart. Start a new recovery job if needed.", cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var worker in _active.Values)
            worker.Cancel();
        return Task.CompletedTask;
    }

    public async Task<JobDto> StartRecoveryAsync(int hours, CancellationToken ct)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        await _startGate.WaitAsync(ct);
        try
        {
            if (await _database.HasActiveJobAsync(JobType, ct))
                throw new InvalidOperationException("A failed-transcription recovery job is already active.");
            var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var start = end - hours * 3600L;
            var total = await _database.CountTranscriptionErrorCallsAsync(start, end, ct);
            if (total == 0)
                throw new InvalidOperationException($"No failed transcriptions with retained audio were found in the last {hours} hour(s).");
            var id = await _database.AddJobAsync(new JobDto
            {
                Type = JobType,
                Status = "queued",
                Total = total,
                Message = $"Queued low-priority recovery for {total:N0} failed transcription(s) from the last {hours} hour(s).",
                CreatedAtUtc = DateTime.UtcNow
            }, ct);
            await _database.AddJobLogAsync(id, "warning", $"Operator requested recovery of {total:N0} failed transcription(s) from the last {hours} hour(s). Recovery yields to live and existing backlog work; one in-flight call may finish after cancellation.", ct);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            _active[id] = cts;
            _ = Task.Run(() => RunAsync(id, start, end, total, cts.Token), CancellationToken.None);
            return await _database.GetJobAsync(id, ct) ?? throw new InvalidOperationException("Recovery job could not be loaded after creation.");
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<JobDto?> CancelAsync(long jobId, CancellationToken ct)
    {
        var job = await _database.GetJobAsync(jobId, ct);
        if (job == null) return null;
        if (!string.Equals(job.Type, JobType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This job is not a transcription recovery job.");
        if (job.Status is not ("queued" or "running" or "canceling")) return job;
        await _database.UpdateJobAsync(jobId, "canceling", null, null, null, "Cancel requested. No additional failed calls will be queued.", false, false, ct);
        await _database.AddJobLogAsync(jobId, "warning", "Operator requested recovery cancellation.", ct);
        if (_active.TryGetValue(jobId, out var cts)) cts.Cancel();
        return await _database.GetJobAsync(jobId, ct);
    }

    private async Task RunAsync(long jobId, long start, long end, int total, CancellationToken ct)
    {
        var completed = 0;
        var failed = 0;
        try
        {
            await _database.UpdateJobAsync(jobId, "running", total, 0, 0, "Recovery is waiting for idle transcription capacity.", true, false, CancellationToken.None);
            var calls = (await _database.ListTranscriptionErrorCallsAsync(total, ct, start, end)).OrderBy(call => call.StartTime).ToList();
            foreach (var call in calls)
            {
                await WaitForIdleCapacityAsync(jobId, completed, failed, total, ct);
                if (!await _pipeline.EnqueueFailedTranscriptionRetryAsync(call.Id, ct))
                {
                    completed++;
                    continue;
                }
                await _database.UpdateJobAsync(jobId, "running", total, completed, failed, $"Recovering call {call.Id}; live work remains prioritized.", false, false, CancellationToken.None);
                var result = await WaitForCallAsync(call.Id, ct);
                if (string.Equals(result?.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase)) failed++; else completed++;
                await _database.UpdateJobAsync(jobId, "running", total, completed, failed, $"Recovered {completed:N0}; {failed:N0} still failed.", false, false, CancellationToken.None);
            }
            var status = failed > 0 ? "failed" : "completed";
            var message = $"Recovery finished: {completed:N0} completed and {failed:N0} failed.";
            await _database.AddJobLogAsync(jobId, failed > 0 ? "warning" : "info", message, CancellationToken.None);
            await _database.UpdateJobAsync(jobId, status, total, completed, failed, message, false, true, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await _database.AddJobLogAsync(jobId, "warning", $"Recovery stopped after {completed:N0} completed and {failed:N0} failed. No additional calls were queued.", CancellationToken.None);
            await _database.UpdateJobAsync(jobId, "canceled", total, completed, failed, "Recovery canceled at a safe call boundary.", false, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transcription recovery job {JobId} failed", jobId);
            await _database.AddJobLogAsync(jobId, "error", ex.Message, CancellationToken.None);
            await _database.UpdateJobAsync(jobId, "failed", total, completed, Math.Max(1, failed), "Recovery failed: " + ex.Message, false, true, CancellationToken.None);
        }
        finally
        {
            if (_active.TryRemove(jobId, out var cts)) cts.Dispose();
            await _events.PublishAsync("job_updated", new { jobId, type = JobType }, CancellationToken.None);
        }
    }

    private async Task WaitForIdleCapacityAsync(long jobId, int completed, int failed, int total, CancellationToken ct)
    {
        var loggedWaiting = false;
        while (_pipeline.LiveQueueDepth > 0 || _pipeline.HasActiveLiveTranscription || _pipeline.BacklogQueueDepth > 0 || _pipeline.RemoteTranscriptionOutageConfirmed)
        {
            if (!loggedWaiting)
            {
                loggedWaiting = true;
                await _database.UpdateJobAsync(jobId, "running", total, completed, failed, "Recovery paused while live, backlog, or endpoint work is unavailable.", false, false, CancellationToken.None);
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<EngineCall?> WaitForCallAsync(long callId, CancellationToken ct)
    {
        while (true)
        {
            var call = await _database.GetCallAsync(callId, ct);
            if (call == null || !string.Equals(call.TranscriptionStatus, "pending", StringComparison.OrdinalIgnoreCase)) return call;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}

public sealed record TranscriptionRecoveryRequest(int Hours = 24);
public sealed record TranscriptionRecoveryAvailability(int Hours, int FailedCalls);
