namespace pizzad.Tests;

public sealed class EngineConfigAiInsightsTests
{
    [Fact]
    public void ApplyDefaults_KeepsIncidentV2ShadowDisabledUnlessConfigured()
    {
        var config = new EngineConfig
        {
            AiInsights = new AiInsightsConfig
            {
                IncidentV2ShadowCandidateLimit = 100,
                IncidentV3FrameCandidateLimit = 100,
                IncidentAnalysisMaximumAgeMinutes = 1,
                IncidentEventLinkShadowIntervalSeconds = 1,
                IncidentEventLinkShadowLookbackMinutes = 10,
                IncidentEventLinkShadowCandidateLimit = 100,
                IncidentAssociationShadowIntervalSeconds = 1,
                IncidentAssociationShadowLookbackMinutes = 10,
                IncidentAssociationShadowCandidateLimit = 100,
                IncidentBatchConstructorShadowIntervalSeconds = 1,
                IncidentBatchConstructorShadowLookbackMinutes = 10,
                IncidentBatchConstructorShadowBatchSize = 100,
                IncidentBatchConstructorShadowMinimumBatchSize = 100,
                IncidentBatchConstructorShadowMaximumWaitSeconds = 1,
                IncidentBatchConstructorShadowCandidateLimit = 100,
                IncidentBatchVerificationShadowIntervalSeconds = 1
            }
        };

        config.ApplyDefaults();

        Assert.False(config.AiInsights.IncidentV2ShadowEnabled);
        Assert.True(config.AiInsights.IncidentAnalysisExecutionEnabled);
        Assert.Equal(40, config.AiInsights.IncidentV2ShadowCandidateLimit);
        Assert.False(config.AiInsights.IncidentV3FrameShadowEnabled);
        Assert.Equal(40, config.AiInsights.IncidentV3FrameCandidateLimit);
        Assert.Equal(15, config.AiInsights.IncidentAnalysisMaximumAgeMinutes);
        Assert.False(config.AiInsights.IncidentV3PlanExecutorEnabled);
        Assert.True(config.AiInsights.IncidentV3PlanExecutorDryRun);
        Assert.False(config.AiInsights.IncidentEventLinkShadowEnabled);
        Assert.Equal(60, config.AiInsights.IncidentEventLinkShadowIntervalSeconds);
        Assert.Equal(30, config.AiInsights.IncidentEventLinkShadowLookbackMinutes);
        Assert.Equal(IncidentEventStateLinkContractValidator.MaximumCandidateCount, config.AiInsights.IncidentEventLinkShadowCandidateLimit);
        Assert.False(config.AiInsights.IncidentAssociationShadowEnabled);
        Assert.Equal(60, config.AiInsights.IncidentAssociationShadowIntervalSeconds);
        Assert.Equal(30, config.AiInsights.IncidentAssociationShadowLookbackMinutes);
        Assert.Equal(IncidentAssociationContract.MaximumCandidateCount, config.AiInsights.IncidentAssociationShadowCandidateLimit);
        Assert.False(config.AiInsights.IncidentBatchConstructorShadowEnabled);
        Assert.Equal(300, config.AiInsights.IncidentBatchConstructorShadowIntervalSeconds);
        Assert.Equal(30, config.AiInsights.IncidentBatchConstructorShadowLookbackMinutes);
        Assert.Equal(IncidentBatchContract.MaximumNewObservationCount, config.AiInsights.IncidentBatchConstructorShadowBatchSize);
        Assert.Equal(IncidentBatchContract.MaximumNewObservationCount, config.AiInsights.IncidentBatchConstructorShadowMinimumBatchSize);
        Assert.Equal(5, config.AiInsights.IncidentBatchConstructorShadowMaximumWaitSeconds);
        Assert.Equal(IncidentBatchContract.MaximumCandidateCount, config.AiInsights.IncidentBatchConstructorShadowCandidateLimit);
        Assert.False(config.AiInsights.IncidentBatchConstructorShadowSourceIsolated);
        Assert.False(config.AiInsights.IncidentBatchConstructorShadowObservationIsolated);
        Assert.False(config.AiInsights.IncidentBatchRelationshipShadowEnabled);
        Assert.False(config.AiInsights.IncidentBatchConstructorShadowExclusiveInferenceWindow);
        Assert.False(config.AiInsights.IncidentBatchConstructorShadowContinuous);
        Assert.Equal(0, config.AiInsights.IncidentBatchConstructorShadowStartAfterCallId);
        Assert.False(config.AiInsights.IncidentBatchVerificationShadowEnabled);
        Assert.Equal(5, config.AiInsights.IncidentBatchVerificationShadowIntervalSeconds);
    }

    [Fact]
    public void BatchShadowCadence_CapacityModeRunsBackToBackWithoutChangingNormalCadence()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), IncidentBatchShadowCadence.NextDelay(true, 300, TimeSpan.FromSeconds(200)));
        Assert.Equal(TimeSpan.FromSeconds(100), IncidentBatchShadowCadence.NextDelay(false, 300, TimeSpan.FromSeconds(200)));
        Assert.Equal(TimeSpan.FromSeconds(30), IncidentBatchShadowCadence.NextDelay(false, 300, TimeSpan.FromSeconds(400)));
    }

    [Fact]
    public void ApplyDefaults_ObservationIsolationAlsoForcesCandidateFreeSourceContext()
    {
        var config = new EngineConfig
        {
            AiInsights = new AiInsightsConfig
            {
                IncidentBatchConstructorShadowObservationIsolated = true,
                IncidentBatchConstructorShadowSourceIsolated = false
            }
        };

        config.ApplyDefaults();

        Assert.True(config.AiInsights.IncidentBatchConstructorShadowObservationIsolated);
        Assert.True(config.AiInsights.IncidentBatchConstructorShadowSourceIsolated);
    }

    [Fact]
    public void ApplyDefaults_RelationshipStageForcesObservationIsolatedSourceOwnership()
    {
        var config = new EngineConfig
        {
            AiInsights = new AiInsightsConfig
            {
                IncidentBatchRelationshipShadowEnabled = true,
                IncidentBatchConstructorShadowObservationIsolated = false,
                IncidentBatchConstructorShadowSourceIsolated = false
            }
        };

        config.ApplyDefaults();

        Assert.True(config.AiInsights.IncidentBatchRelationshipShadowEnabled);
        Assert.True(config.AiInsights.IncidentBatchConstructorShadowObservationIsolated);
        Assert.True(config.AiInsights.IncidentBatchConstructorShadowSourceIsolated);
        Assert.False(config.AiInsights.IncidentBatchConstructorShadowExclusiveInferenceWindow);
        Assert.True(config.AiInsights.IncidentAnalysisExecutionEnabled);
    }

    [Fact]
    public void BatchAdmissionPolicy_CombinesUsefulBatchesWithoutUnboundedDelay()
    {
        Assert.False(IncidentBatchAdmissionPolicy.ShouldProcess(true, 0, 24, 12, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2)));
        Assert.False(IncidentBatchAdmissionPolicy.ShouldProcess(true, 11, 24, 12, TimeSpan.FromSeconds(119), TimeSpan.FromMinutes(2)));
        Assert.True(IncidentBatchAdmissionPolicy.ShouldProcess(true, 12, 24, 12, TimeSpan.Zero, TimeSpan.FromMinutes(2)));
        Assert.True(IncidentBatchAdmissionPolicy.ShouldProcess(true, 24, 24, 12, TimeSpan.Zero, TimeSpan.FromMinutes(2)));
        Assert.True(IncidentBatchAdmissionPolicy.ShouldProcess(true, 1, 24, 12, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2)));
        Assert.True(IncidentBatchAdmissionPolicy.ShouldProcess(false, 1, 24, 12, TimeSpan.Zero, TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void ExclusiveExperimentWorkRequiresAnExplicitWindowAndPausedProductionExecutor()
    {
        Assert.False(IncidentBatchExperimentWindow.AllowsExclusiveReplacementWork(false, false));
        Assert.False(IncidentBatchExperimentWindow.AllowsExclusiveReplacementWork(true, true));
        Assert.True(IncidentBatchExperimentWindow.AllowsExclusiveReplacementWork(true, false));
    }

    [Fact]
    public void ApplyDefaults_ForcesRetiredIncidentV3ExecutorToDryRun()
    {
        var config = new EngineConfig
        {
            AiInsights = new AiInsightsConfig
            {
                IncidentV3FrameShadowEnabled = true,
                IncidentV3PlanExecutorEnabled = true,
                IncidentV3PlanExecutorDryRun = false
            }
        };

        config.ApplyDefaults();

        Assert.True(config.AiInsights.IncidentV3FrameShadowEnabled);
        Assert.True(config.AiInsights.IncidentV3PlanExecutorEnabled);
        Assert.True(config.AiInsights.IncidentV3PlanExecutorDryRun);
    }
}
