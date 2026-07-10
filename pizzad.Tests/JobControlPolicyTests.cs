namespace pizzad.Tests;

public sealed class JobControlPolicyTests
{
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
    public void WorkflowOwnedSetupJobOffersNoGenericControls()
    {
        var described = JobControlPolicy.Describe(new JobDto { Type = "setup_tr_calibration_sweep", Status = "running" });

        Assert.Empty(described.SupportedOperations);
        Assert.False(JobControlPolicy.Supports(described, "cancel"));
    }
}
