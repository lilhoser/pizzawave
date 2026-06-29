namespace pizzad.Tests;

public sealed class IncidentPlanExecutorV3Tests
{
    [Fact]
    public void BuildExecutionPlan_DisabledBuildsAuditableOperationsWithoutMutation()
    {
        var executor = new IncidentPlanExecutorV3();
        var result = executor.BuildExecutionPlan(
            [
                Plan("update_current", [101, 102], targetIncidentId: "incident-1", title: "Traffic crash at Main St"),
                Plan("hold_pending", [103], title: "Medical response")
            ],
            new IncidentPlanExecutionOptionsV3(Enabled: false, DryRun: true));

        Assert.Equal("disabled", result.Mode);
        Assert.False(result.CanMutate);
        Assert.Equal(2, result.OperationCount);
        Assert.Equal(1, result.MutatingOperationCount);
        Assert.Empty(result.BlockReasons);
        Assert.Contains(result.Operations, operation =>
            operation.ExecutionAction == "upsert_current_incident" &&
            operation.WouldMutate &&
            !operation.Blocked);
    }

    [Fact]
    public void BuildExecutionPlan_DryRunNeverAllowsMutation()
    {
        var executor = new IncidentPlanExecutorV3();
        var result = executor.BuildExecutionPlan(
            [Plan("create_new", [201], title: "Chest pain at 100 Main St")],
            new IncidentPlanExecutionOptionsV3(Enabled: true, DryRun: true));

        Assert.Equal("dry_run", result.Mode);
        Assert.False(result.CanMutate);
        var operation = Assert.Single(result.Operations);
        Assert.Equal("create_incident", operation.ExecutionAction);
        Assert.True(operation.WouldMutate);
        Assert.True(operation.Blocked);
        Assert.Contains("live_create_new_not_implemented", result.BlockReasons);
    }

    [Fact]
    public void BuildExecutionPlan_DryRunExposesUnsupportedUpdateCurrentTargets()
    {
        var executor = new IncidentPlanExecutorV3();
        var result = executor.BuildExecutionPlan(
            [Plan("update_current", [251], targetIncidentId: "new", title: "Traffic crash at Main St")],
            new IncidentPlanExecutionOptionsV3(Enabled: true, DryRun: true));

        Assert.Equal("dry_run", result.Mode);
        Assert.False(result.CanMutate);
        Assert.Equal(1, result.BlockedOperationCount);
        Assert.Contains("unsupported_target_incident_id", result.BlockReasons);
        Assert.True(Assert.Single(result.Operations).Blocked);
    }

    [Fact]
    public void BuildExecutionPlan_BlocksUnsafeLiveWriteSet()
    {
        var executor = new IncidentPlanExecutorV3();
        var result = executor.BuildExecutionPlan(
            [
                Plan("update_current", [301], targetIncidentId: "active:1", title: "Traffic crash at Main St"),
                Plan("create_new", [301], title: "Medical response at Broad St"),
                Plan("detach_create", [302], title: "Police response")
            ],
            new IncidentPlanExecutionOptionsV3(Enabled: true, DryRun: false));

        Assert.Equal("blocked", result.Mode);
        Assert.False(result.CanMutate);
        Assert.Contains(result.BlockReasons, reason => reason.Contains("duplicate_write_call_ids:301", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.BlockReasons, reason => reason.Contains("generic_title", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, result.BlockedOperationCount);
    }

    [Fact]
    public void BuildExecutionPlan_LiveModeAllowsOnlyUpdateCurrentAgainstActiveTargets()
    {
        var executor = new IncidentPlanExecutorV3();
        var result = executor.BuildExecutionPlan(
            [
                Plan("update_current", [401, 402], targetIncidentId: "active:42", title: "Traffic crash at Main St"),
                Plan("hold_pending", [403], title: "Chest pain")
            ],
            new IncidentPlanExecutionOptionsV3(Enabled: true, DryRun: false));

        Assert.Equal("live_update_current", result.Mode);
        Assert.True(result.CanMutate);
        Assert.Equal(2, result.OperationCount);
        Assert.Equal(1, result.MutatingOperationCount);
        Assert.Equal(0, result.BlockedOperationCount);
        Assert.Empty(result.BlockReasons);
        Assert.Contains(result.Operations, operation =>
            operation.PlanAction == "update_current" &&
            operation.WouldMutate &&
            !operation.Blocked);
    }

    [Fact]
    public void BuildExecutionPlan_LiveModeAllowsUpdateCurrentWhileCreateRemainsBlocked()
    {
        var executor = new IncidentPlanExecutorV3();
        var result = executor.BuildExecutionPlan(
            [
                Plan("update_current", [451, 452], targetIncidentId: "active:42", title: "Traffic crash at Main St"),
                Plan("create_new", [453], title: "Chest pain at 100 Main St")
            ],
            new IncidentPlanExecutionOptionsV3(Enabled: true, DryRun: false));

        Assert.Equal("live_update_current_partial", result.Mode);
        Assert.True(result.CanMutate);
        Assert.Equal(2, result.MutatingOperationCount);
        Assert.Equal(1, result.BlockedOperationCount);
        Assert.Contains("live_create_new_not_implemented", result.BlockReasons);
        Assert.Contains(result.Operations, operation =>
            operation.PlanAction == "update_current" &&
            operation.WouldMutate &&
            !operation.Blocked);
        Assert.Contains(result.Operations, operation =>
            operation.PlanAction == "create_new" &&
            operation.WouldMutate &&
            operation.Blocked &&
            operation.BlockedBecause.Contains("live_create_new_not_implemented", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("new")]
    [InlineData("incident-1")]
    public void BuildExecutionPlan_BlocksLiveUpdateCurrentWithoutActiveTarget(string targetIncidentId)
    {
        var executor = new IncidentPlanExecutorV3();
        var result = executor.BuildExecutionPlan(
            [Plan("update_current", [501], targetIncidentId: targetIncidentId, title: "Traffic crash at Main St")],
            new IncidentPlanExecutionOptionsV3(Enabled: true, DryRun: false));

        Assert.Equal("blocked", result.Mode);
        Assert.False(result.CanMutate);
        Assert.Contains(result.BlockReasons, reason => reason.Contains("unsupported_target_incident_id", StringComparison.OrdinalIgnoreCase) ||
                                                       reason.Contains("missing_target_incident_id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildExecutionPlan_BlocksLiveCreatesUntilExecutionIsImplemented()
    {
        var executor = new IncidentPlanExecutorV3();
        var result = executor.BuildExecutionPlan(
            [Plan("create_new", [601, 602], title: "Chest pain at 100 Main St")],
            new IncidentPlanExecutionOptionsV3(Enabled: true, DryRun: false));

        Assert.Equal("blocked", result.Mode);
        Assert.False(result.CanMutate);
        Assert.Contains("live_create_new_not_implemented", result.BlockReasons);
    }

    private static IncidentPlanDecisionV3 Plan(
        string action,
        IReadOnlyList<long> callIds,
        string targetIncidentId = "",
        string title = "Traffic crash at Main St") =>
        new(
            action,
            targetIncidentId,
            targetIncidentId,
            "frame-1",
            title,
            title,
            "traffic",
            "Main St",
            callIds,
            "test");
}
