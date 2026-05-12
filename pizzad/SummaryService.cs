namespace pizzad;

public sealed class SummaryService
{
    private const long MaxManualSummaryEndSkewSeconds = 2 * 3600;
    private readonly Dictionary<long, CancellationTokenSource> _runningJobs = new();
    private readonly object _jobGate = new();
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EnginePipeline _pipeline;
    private readonly AutomaticInsightsService _insights;
    private readonly EventStream _events;
    private readonly ILogger<SummaryService> _logger;

    public SummaryService(
        EngineConfig config,
        EngineDatabase database,
        EnginePipeline pipeline,
        AutomaticInsightsService insights,
        EventStream events,
        ILogger<SummaryService> logger)
    {
        _config = config;
        _database = database;
        _pipeline = pipeline;
        _insights = insights;
        _events = events;
        _logger = logger;
    }

    public async Task<JobDto> GenerateForRangeAsync(GenerateSummaryRequest request, CancellationToken ct)
    {
        if (!_insights.IsSetupComplete)
            throw new InvalidOperationException("Setup is not complete. Summary and incident generation are disabled in limited mode.");
        if (!_insights.IsConfiguredAndEnabled)
            throw new InvalidOperationException("AI insights are disabled or not fully configured. Enable aiInsights.enabled and configure the AI endpoint/model before generating incidents.");

        var span = request.End - request.Start;
        if (span <= 0)
            throw new InvalidOperationException("Summary generation requires a valid time range.");
        var maxLookbackHours = Math.Max(1, _config.AiInsights.MaxManualLookbackHours);
        if (span > maxLookbackHours * 3600L)
            throw new InvalidOperationException($"AI summary and incident generation is limited to the most recent {maxLookbackHours} hour(s). Historical import backfill is intentionally disabled because LM Studio processing can take days on large ranges.");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (request.End < now - MaxManualSummaryEndSkewSeconds)
            throw new InvalidOperationException("AI summary and incident generation is only available for the current/recent range. Historical backfill is intentionally disabled.");
        if (_config.AiInsights.MaxQueueDepthForManualSummary > 0 && _pipeline.QueueDepth > _config.AiInsights.MaxQueueDepthForManualSummary)
            throw new InvalidOperationException($"AI summary generation is disabled while transcription/import backlog is high. Queue depth is {_pipeline.QueueDepth:N0}; configured limit is {_config.AiInsights.MaxQueueDepthForManualSummary:N0}.");

        var job = new JobDto
        {
            Type = "summary_generation",
            Status = "running",
            Message = "Preparing recent AI summaries and incidents for selected range.",
            CreatedAtUtc = DateTime.UtcNow,
            StartedAtUtc = DateTime.UtcNow
        };
        var jobId = await _database.AddJobAsync(job, ct);
        var cts = new CancellationTokenSource();
        lock (_jobGate)
            _runningJobs[jobId] = cts;
        _ = Task.Run(() => RunGenerateAsync(jobId, request, cts.Token));
        await _events.PublishAsync("job_updated", new { jobId, type = "summary_generation", status = "running" }, ct);
        return job with { Id = jobId };
    }

    public async Task<JobDto?> ControlJobAsync(long jobId, string action, CancellationToken ct)
    {
        if (!string.Equals(action, "cancel", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Summary generation jobs support cancel only.");

        CancellationTokenSource? cts;
        lock (_jobGate)
            _runningJobs.TryGetValue(jobId, out cts);
        if (cts == null)
            return await _database.GetJobAsync(jobId, ct);

        await _database.UpdateJobAsync(jobId, "canceling", null, null, null, "Cancel requested. Waiting for the active LM request to stop.", false, false, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "canceling" }, ct);
        cts.Cancel();
        return await _database.GetJobAsync(jobId, ct);
    }

    public async Task<object> RebuildIncidentsForRangeAsync(GenerateSummaryRequest request, CancellationToken ct)
    {
        var count = await _database.RebuildIncidentsFromInsightEventsAsync(request.Start, request.End, ct);
        await _events.PublishAsync("summary_updated", new { request.Start, request.End, incidents = count, rebuilt = true }, ct);
        return new { incidents = count };
    }

    private async Task RunGenerateAsync(long jobId, GenerateSummaryRequest request, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var allCalls = await _database.ListCallsAsync(request.Start, request.End, null, ct);
            var calls = allCalls
                .Where(c => string.Equals(c.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.QualityReason, "ok", StringComparison.OrdinalIgnoreCase) &&
                            !c.IsImported &&
                            !string.IsNullOrWhiteSpace(c.Transcription))
                .OrderBy(c => c.StartTime)
                .ToList();
            var excluded = allCalls.Count(c => string.Equals(c.TranscriptionStatus, "poor_quality", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(c.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase));
            var importedExcluded = allCalls.Count(c => c.IsImported);
            var batchSize = Math.Max(1, _insights.ConfiguredBatchSize);
            var batches = calls
                .Select((call, index) => new { call, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.call).ToList())
                .ToList();

            var maxCalls = Math.Max(1, _config.AiInsights.MaxManualSummaryCalls);
            var maxWindows = Math.Max(1, _config.AiInsights.MaxManualSummaryWindows);
            if (calls.Count > maxCalls || batches.Count > maxWindows)
                throw new InvalidOperationException($"AI summary generation would process {calls.Count:N0} live calls across {batches.Count:N0} LM window(s), above configured limits of {maxCalls:N0} calls and {maxWindows:N0} windows. Narrow the range or raise the Insights guardrails.");

            var total = batches.Count;
            var completed = 0;
            var incidentCount = 0;
            _logger.LogInformation("Summary generation range {Start}-{End}: {Included:N0} live calls included, {Excluded:N0} poor-quality/failed calls excluded, {ImportedExcluded:N0} imported calls excluded", request.Start, request.End, calls.Count, excluded, importedExcluded);
            await _database.UpdateJobAsync(jobId, "running", total, 0, 0, $"Generating {total:N0} AI summary windows from {calls.Count:N0} live calls; skipped {excluded:N0} poor-quality calls and {importedExcluded:N0} imported calls.", true, false, ct);

            foreach (var batch in batches)
            {
                ct.ThrowIfCancellationRequested();
                var next = completed + 1;
                var waitStarted = DateTime.UtcNow;
                await _database.UpdateJobAsync(
                    jobId,
                    "running",
                    total,
                    completed,
                    0,
                    $"Waiting on LM for AI summary window {next:N0}/{total:N0} since {waitStarted:HH:mm:ss} UTC; {batch.Count:N0} live calls in this request; skipped {excluded:N0} poor-quality calls and {importedExcluded:N0} imported calls.",
                    false,
                    false,
                    ct);
                await _events.PublishAsync("job_updated", new { jobId, completed, total, waitingOnLm = true, window = next }, ct);
                incidentCount += await _insights.GenerateWindowForCallsAsync(batch, ct);
                completed++;
                await _database.UpdateJobAsync(jobId, "running", total, completed, 0, $"Generated {completed:N0}/{total:N0} AI summary windows and {incidentCount:N0} incidents; skipped {excluded:N0} poor-quality calls and {importedExcluded:N0} imported calls.", false, false, ct);
                await _events.PublishAsync("job_updated", new { jobId, completed, total, incidents = incidentCount }, ct);
            }

            await _database.UpdateJobAsync(jobId, "completed", total, completed, 0, $"Generated {completed:N0} AI summary windows and {incidentCount:N0} incidents; skipped {excluded:N0} poor-quality calls and {importedExcluded:N0} imported calls.", false, true, ct);
            await _events.PublishAsync("summary_updated", new { request.Start, request.End, incidents = incidentCount }, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Summary generation job {JobId} canceled", jobId);
            await _database.UpdateJobAsync(jobId, "canceled", null, null, null, "Summary generation canceled.", false, true, CancellationToken.None);
            await _events.PublishAsync("job_updated", new { jobId, status = "canceled" }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summary generation failed");
            await _database.UpdateJobAsync(jobId, "failed", null, null, null, ex.Message, false, true, CancellationToken.None);
            await _events.PublishAsync("job_updated", new { jobId, status = "failed" }, CancellationToken.None);
        }
        finally
        {
            lock (_jobGate)
            {
                if (_runningJobs.Remove(jobId, out var cts))
                    cts.Dispose();
            }
        }
    }
}
