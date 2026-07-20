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
                IncidentEventLinkShadowIntervalSeconds = 1,
                IncidentEventLinkShadowLookbackMinutes = 10,
                IncidentEventLinkShadowCandidateLimit = 100
            }
        };

        config.ApplyDefaults();

        Assert.False(config.AiInsights.IncidentV2ShadowEnabled);
        Assert.Equal(40, config.AiInsights.IncidentV2ShadowCandidateLimit);
        Assert.False(config.AiInsights.IncidentV3FrameShadowEnabled);
        Assert.Equal(40, config.AiInsights.IncidentV3FrameCandidateLimit);
        Assert.False(config.AiInsights.IncidentV3PlanExecutorEnabled);
        Assert.True(config.AiInsights.IncidentV3PlanExecutorDryRun);
        Assert.False(config.AiInsights.IncidentEventLinkShadowEnabled);
        Assert.Equal(60, config.AiInsights.IncidentEventLinkShadowIntervalSeconds);
        Assert.Equal(30, config.AiInsights.IncidentEventLinkShadowLookbackMinutes);
        Assert.Equal(IncidentEventStateLinkContractValidator.MaximumCandidateCount, config.AiInsights.IncidentEventLinkShadowCandidateLimit);
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
