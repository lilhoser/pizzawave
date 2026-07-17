namespace pizzad;

public sealed class TranscriptionCandidateService
{
    private readonly EngineDatabase _database;

    public TranscriptionCandidateService(EngineDatabase database)
    {
        _database = database;
    }

    public async Task<TranscriptionCandidateReportDto> BuildReportAsync(
        long start,
        long end,
        int limit,
        bool includeIncidentLinked,
        CancellationToken ct)
    {
        var calls = await _database.ListCallsAsync(start, end, null, ct);
        var incidents = await _database.ListIncidentsAsync(start, end, ct);
        return TranscriptionCandidateAnalyzer.BuildReport(
            calls,
            incidents,
            start,
            end,
            limit,
            includeIncidentLinked);
    }
}
