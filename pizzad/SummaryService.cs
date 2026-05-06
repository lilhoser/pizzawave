namespace pizzad;

public sealed class SummaryService
{
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly ILogger<SummaryService> _logger;

    public SummaryService(EngineDatabase database, EventStream events, ILogger<SummaryService> logger)
    {
        _database = database;
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
            Message = "Generating summaries and incidents for selected range.",
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
            var calls = await _database.ListCallsAsync(request.Start, request.End, null, ct);
            var candidateGroups = calls
                .Where(c => !string.IsNullOrWhiteSpace(c.Transcription))
                .GroupBy(c => new
                {
                    c.SystemShortName,
                    c.Talkgroup,
                    Bucket = c.StartTime / 3600
                })
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Min(c => c.StartTime))
                .ToList();

            var total = candidateGroups.Count;
            var completed = 0;
            await _database.UpdateJobAsync(jobId, "running", total, 0, 0, $"Generating {total:N0} incident candidates.", true, false, ct);

            foreach (var group in candidateGroups)
            {
                var ordered = group.OrderBy(c => c.StartTime).ToList();
                var title = $"{Label(ordered[0])} activity";
                var detail = BuildDetail(ordered);
                var incident = new IncidentDto
                {
                    Title = title,
                    Detail = detail,
                    FirstSeen = ordered.Min(c => c.StartTime),
                    LastSeen = ordered.Max(c => c.StartTime),
                    Calls = ordered.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio")).ToList()
                };
                await _database.AddIncidentAsync(incident, ct);
                completed++;
                await _database.UpdateJobAsync(jobId, "running", total, completed, 0, $"Generated {completed:N0}/{total:N0} incidents.", false, false, ct);
                await _events.PublishAsync("job_updated", new { jobId, completed, total }, ct);
            }

            await _database.UpdateJobAsync(jobId, "completed", total, completed, 0, $"Generated {completed:N0} incidents.", false, true, ct);
            await _events.PublishAsync("summary_updated", new { request.Start, request.End, incidents = completed }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Summary generation failed");
            await _database.UpdateJobAsync(jobId, "failed", null, null, null, ex.Message, false, true, CancellationToken.None);
            await _events.PublishAsync("job_updated", new { jobId, status = "failed" }, CancellationToken.None);
        }
    }

    private static string Label(EngineCall call) =>
        string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"{call.SystemShortName} TG {call.Talkgroup}" : call.TalkgroupName;

    private static string BuildDetail(IReadOnlyList<EngineCall> calls)
    {
        var first = DateTimeOffset.FromUnixTimeSeconds(calls.Min(c => c.StartTime)).ToLocalTime();
        var last = DateTimeOffset.FromUnixTimeSeconds(calls.Max(c => c.StartTime)).ToLocalTime();
        return $"{calls.Count} related calls from {first:g} to {last:g}. {calls[0].Transcription}";
    }
}
