namespace pizzad;

public sealed record IncidentBatchVerificationShadowTotalsDto(
    int Enqueued,
    int Pending,
    int Verified,
    int Review,
    int Rejected,
    int Invalid,
    int CanaryPersisted,
    int CanaryConflicts,
    double AverageVerifierMilliseconds,
    long MaximumVerifierMilliseconds);

public sealed record IncidentBatchVerificationShadowItemDto(
    long RequestSequence,
    string RequestId,
    DateTime EnqueuedAtUtc,
    string SourceLedgerEntryId,
    string SourceProposalToken,
    string CandidateToken,
    string ProposedDisposition,
    string Outcome,
    DateTime? CompletedAtUtc,
    long VerifierMilliseconds,
    IReadOnlyList<string> ValidationErrors,
    string CanaryOutcome,
    long CanaryIncidentId,
    string CanaryReason);

public sealed record IncidentBatchVerificationShadowReportDto(
    bool Enabled,
    string RunId,
    IncidentBatchVerificationShadowTotalsDto Totals,
    IReadOnlyList<IncidentBatchVerificationShadowItemDto> Items);

public sealed partial class EngineDatabase
{
    public async Task<IncidentBatchVerificationShadowReportDto> GetIncidentBatchVerificationShadowReportAsync(
        bool enabled,
        string runId,
        int limit,
        CancellationToken ct)
    {
        runId = runId.Trim();
        if (string.IsNullOrWhiteSpace(runId))
            return new IncidentBatchVerificationShadowReportDto(enabled, string.Empty, new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), []);
        var requests = await ListIncidentBatchVerificationRequestsAsync(runId, 1000, ct);
        var results = await ListIncidentBatchVerificationResultsAsync(runId, 1000, ct);
        var commits = await ListIncidentBatchCanaryCommitsAsync(runId, 1000, ct);
        var resultsByRequest = results.ToDictionary(item => item.Result.RequestId, StringComparer.Ordinal);
        var commitsByRequest = commits.ToDictionary(item => item.Commit.RequestId, StringComparer.Ordinal);
        var completed = results.Select(item => item.Result).ToList();
        var totals = new IncidentBatchVerificationShadowTotalsDto(
            requests.Count,
            requests.Count(item => !resultsByRequest.ContainsKey(item.Request.RequestId)),
            completed.Count(item => item.Outcome == IncidentBatchVerificationOutcome.Verified),
            completed.Count(item => item.Outcome == IncidentBatchVerificationOutcome.Review),
            completed.Count(item => item.Outcome == IncidentBatchVerificationOutcome.Rejected),
            completed.Count(item => item.Outcome == IncidentBatchVerificationOutcome.Invalid),
            commits.Count(item => item.Commit.Outcome == IncidentBatchCanaryCommitOutcome.Persisted),
            commits.Count(item => item.Commit.Outcome == IncidentBatchCanaryCommitOutcome.Conflict),
            completed.Count == 0 ? 0 : completed.Average(item => item.Execution.VerifierDurationMilliseconds),
            completed.Count == 0 ? 0 : completed.Max(item => item.Execution.VerifierDurationMilliseconds));
        var items = requests
            .OrderByDescending(item => item.Sequence)
            .Take(Math.Clamp(limit, 1, 500))
            .Select(item =>
            {
                resultsByRequest.TryGetValue(item.Request.RequestId, out var storedResult);
                commitsByRequest.TryGetValue(item.Request.RequestId, out var storedCommit);
                return new IncidentBatchVerificationShadowItemDto(
                    item.Sequence,
                    item.Request.RequestId,
                    item.Request.EnqueuedAtUtc.UtcDateTime,
                    item.Request.SourceLedgerEntryId,
                    item.Request.SourceProposalToken,
                    item.Request.CandidateToken,
                    item.Request.ProposedDisposition.ToString(),
                    storedResult?.Result.Outcome.ToString() ?? "Pending",
                    storedResult?.Result.RecordedAtUtc.UtcDateTime,
                    storedResult?.Result.Execution.VerifierDurationMilliseconds ?? 0,
                    storedResult?.Result.ValidationErrors ?? [],
                    storedCommit?.Commit.Outcome.ToString() ?? string.Empty,
                    storedCommit?.Commit.IncidentId ?? 0,
                    storedCommit?.Commit.Reason ?? string.Empty);
            })
            .ToList();
        return new IncidentBatchVerificationShadowReportDto(enabled, runId, totals, items);
    }
}
