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
                IncidentBatchConstructorShadowCandidateLimit = 100
            }
        };

        config.ApplyDefaults();

        Assert.False(config.AiInsights.IncidentV2ShadowEnabled);
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
        Assert.Equal(IncidentBatchContract.MaximumCandidateCount, config.AiInsights.IncidentBatchConstructorShadowCandidateLimit);
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
