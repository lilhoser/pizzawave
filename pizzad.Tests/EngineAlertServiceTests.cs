using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class EngineAlertServiceTests
{
    [Fact]
    public void ApplyDefaults_RejectsUnscopedTalkgroupsInsteadOfBroadeningRule()
    {
        var config = new EngineConfig
        {
            Alerts = new AlertConfig
            {
                Rules =
                [
                    new EngineAlertRule
                    {
                        Name = "Invalid",
                        Keywords = "outage",
                        Talkgroups = [new AlertTalkgroupRef { Id = 76 }]
                    }
                ]
            }
        };

        var error = Assert.Throws<InvalidOperationException>(config.ApplyDefaults);
        Assert.Contains("unsupported unscoped talkgroup", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ScopesTalkgroupBySystemAndId()
    {
        var config = Config(new EngineAlertRule
        {
            Name = "Entergy outage",
            MatchType = AlertRulePolicy.Keyword,
            Keywords = "power line down",
            Talkgroups = [new AlertTalkgroupRef { SystemShortName = "entergy", Id = 76 }]
        });
        var service = Create(config);

        var wrongSystem = service.Evaluate(Call("mswin", 76), "Power line down on Highway 18.", imported: false);
        var exactSystem = service.Evaluate(Call("entergy", 76), "Power line down on Highway 18.", imported: false);

        Assert.False(wrongSystem.IsMatch);
        Assert.True(exactSystem.IsMatch);
        Assert.Equal("power line down", exactSystem.Detail);
    }

    [Theory]
    [InlineData("police-code", "10-50 reported at Main and First", true, "police_code")]
    [InlineData("keyword_or_police_code", "10-50 reported at Main and First", true, "police_code")]
    [InlineData("keyword_or_police_code", "Vehicle rollover at Main and First", true, "keyword")]
    [InlineData("keyword", "10-50 reported at Main and First", false, "")]
    public void Evaluate_UsesCanonicalMatchTypeBehavior(string matchType, string transcript, bool expected, string expectedType)
    {
        var config = Config(new EngineAlertRule
        {
            Name = "Crash",
            MatchType = matchType,
            Keywords = "rollover",
            PoliceCodes = "10-50"
        });
        var result = Create(config).Evaluate(Call("mswin", 1001), transcript, imported: false);

        Assert.Equal(expected, result.IsMatch);
        Assert.Equal(expectedType, result.Type);
    }

    private static EngineConfig Config(EngineAlertRule rule)
    {
        var config = new EngineConfig
        {
            Setup = new SetupConfig { Completed = true },
            Alerts = new AlertConfig { Rules = [rule] }
        };
        config.ApplyDefaults();
        return config;
    }

    private static EngineAlertService Create(EngineConfig config) =>
        new(
            config,
            new CredentialStore(config, NullLogger<CredentialStore>.Instance),
            new PoliceCodeService(),
            NullLogger<EngineAlertService>.Instance);

    private static EngineCall Call(string system, long talkgroup) => new()
    {
        Id = 1,
        UniqueKey = $"{system}-{talkgroup}",
        SystemShortName = system,
        Talkgroup = talkgroup,
        TalkgroupName = $"TG {talkgroup}",
        Category = "police"
    };
}
