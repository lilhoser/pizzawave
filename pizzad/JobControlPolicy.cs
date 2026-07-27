namespace pizzad;

public static class JobControlPolicy
{
    public static JobDto Describe(JobDto job) => job with
    {
        SupportedOperations = SupportedOperations(job)
    };

    public static bool Supports(JobDto job, string? operation) =>
        SupportedOperations(job).Contains((operation ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> SupportedOperations(JobDto job)
    {
        if (job.Type == BackupJobService.JobType && job.Status is "queued" or "running" or "paused")
            return ["cancel"];

        if (job.Type == TranscriptionRecoveryJobService.JobType && job.Status is "queued" or "running" or "canceling")
            return ["cancel"];

        if (SetupJobService.IsManagedJobType(job.Type) && job.Status is "queued" or "running" or "paused" or "canceling")
            return ["cancel"];

        return [];
    }
}
