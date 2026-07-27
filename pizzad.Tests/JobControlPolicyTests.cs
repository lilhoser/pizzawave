namespace pizzad.Tests;

public sealed class JobControlPolicyTests
{
    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("canceling")]
    public void TranscriptionRecoveryCanBeCanceled(string status)
    {
        var described = JobControlPolicy.Describe(new JobDto { Type = TranscriptionRecoveryJobService.JobType, Status = status });

        Assert.Contains("cancel", described.SupportedOperations);
    }
    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("paused")]
    public void BackupJobOffersCancelOnlyWhileItCanBeStopped(string status)
    {
        var described = JobControlPolicy.Describe(new JobDto { Type = BackupJobService.JobType, Status = status });

        Assert.Equal(["cancel"], described.SupportedOperations);
        Assert.True(JobControlPolicy.Supports(described, "cancel"));
        Assert.False(JobControlPolicy.Supports(described, "pause"));
        Assert.False(JobControlPolicy.Supports(described, "resume"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("canceled")]
    [InlineData("canceling")]
    public void FinishedOrStoppingBackupJobOffersNoControls(string status)
    {
        var described = JobControlPolicy.Describe(new JobDto { Type = BackupJobService.JobType, Status = status });

        Assert.Empty(described.SupportedOperations);
    }

    [Fact]
    public void SetupJobOffersDashboardCancellationWhileActive()
    {
        var described = JobControlPolicy.Describe(new JobDto { Type = "setup_tr_calibration_sweep", Status = "running" });

        Assert.Equal(["cancel"], described.SupportedOperations);
        Assert.True(JobControlPolicy.Supports(described, "cancel"));
        Assert.False(JobControlPolicy.Supports(described, "pause"));
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("canceled")]
    public void FinishedSetupJobOffersNoControls(string status)
    {
        var described = JobControlPolicy.Describe(new JobDto { Type = "setup_tr_source_build", Status = status });

        Assert.Empty(described.SupportedOperations);
    }
}
