namespace pizzad;

public sealed class SummaryService
{
    private readonly EngineDatabase _database;
    private readonly AutomaticInsightsService _insights;
    private readonly EventStream _events;
    private readonly ILogger<SummaryService> _logger;

    public SummaryService(
        EngineDatabase database,
        AutomaticInsightsService insights,
        EventStream events,
        ILogger<SummaryService> logger)
    {
        _database = database;
        _insights = insights;
        _events = events;
        _logger = logger;
    }

    public async Task<JobDto> GenerateForRangeAsync(GenerateSummaryRequest request, CancellationToken ct)
    {
        var span = request.End - request.Start;
        if (span > 7 * 24 * 3600 && !request.ConfirmLargeRange)
            throw new InvalidOperationException("Summary generation for ranges larger than one week requires confirmation.");

        var job = new JobDto
        {
            Type = "summary_generation",
            Status = "running",
            Message = "Generating AI summaries and incidents for selected range.",
            CreatedAtUtc = DateTime.UtcNow,
            StartedAtUtc = DateTime.UtcNow
        };
        var jobId = await _database.AddJobAsync(job, ct);
        _ = Task.Run(() => RunGenerateAsync(jobId, request, CancellationToken.None));
        await _events.PublishAsync("job_updated", new { jobId, type = "summary_generation", status = "running" }, ct);
        return job with { Id = jobId };
    }

    private async Task RunGenerateAsync(long jobId, GenerateSummaryRequest request, CancellationToken ct)
    {
        try
        {
            var allCalls = await _database.ListCallsAsync(request.Start, request.End, null, ct);
            var calls = allCalls
                .Where(c => string.Equals(c.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(c.QualityReason, "ok", StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(c.Transcription))
                .OrderBy(c => c.StartTime)
                .ToList();
            var excluded = allCalls.Count(c => string.Equals(c.TranscriptionStatus, "poor_quality", StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals(c.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase));
            var batchSize = Math.Max(1, _insights.ConfiguredBatchSize);
            var batches = calls
                .Select((call, index) => new { call, index })
                .GroupBy(x => x.index / batchSize)
                .Select(g => g.Select(x => x.call).ToList())
                .ToList();

            var total = batches.Count;
            var completed = 0;
            var incidentCount = 0;
            _logger.LogInformation("Summary generation range {Start}-{End}: {Included:N0} calls included, {Excluded:N0} poor-quality/failed calls excluded", request.Start, request.End, calls.Count, excluded);
            await _database.UpdateJobAsync(jobId, "running", total, 0, 0, $"Generating {total:N0} AI summary windows from {calls.Count:N0} calls; skipped {excluded:N0} poor-quality calls.", true, false, ct);

            foreach (var batch in batches)
            {
                incidentCount += await _insights.GenerateWindowForCallsAsync(batch, ct);
                completed++;
                await _database.UpdateJobAsync(jobId, "running", total, completed, 0, $"Generated {completed:N0}/{total:N0} AI summary windows and {incidentCount:N0} incidents; skipped {excluded:N0} poor-quality calls.", false, false, ct);
                await _events.PublishAsync("job_updated", new { jobId, completed, total, incidents = incidentCount }, ct);
            }

            await _database.UpdateJobAsync(jobId, "completed", total, completed, 0, $"Generated {completed:N0} AI summary windows and {incidentCount:N0} incidents; skipped {excluded:N0} poor-quality calls.", false, true, ct);
            await _events.PublishAsync("summary_updated", new { request.Start, request.End, incidents = incidentCount }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summary generation failed");
            await _database.UpdateJobAsync(jobId, "failed", null, null, null, ex.Message, false, true, CancellationToken.None);
            await _events.PublishAsync("job_updated", new { jobId, status = "failed" }, CancellationToken.None);
        }
    }
}
