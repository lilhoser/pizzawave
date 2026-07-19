namespace pizzad.Tests;

using System.Reflection;
using System.Text.Json;

public sealed class SiteSetupRfSelectionTests
{
    [Fact]
    public void BuildSiteSetupRequest_PutsSelectedChannelFirstWithoutDiscardingAlternates()
    {
        var desired = new SiteSetupConfig
        {
            Systems =
            [
                new RfSurveySystemDto(
                    "etv-raymond-hinds",
                    "ETV Raymond Hinds",
                    [773_031_250, 773_281_250, 773_531_250, 773_781_250],
                    [])
            ],
            RfSelections = [new SiteSetupRfSelection { FrequencyHz = 773_781_250, SourceIndex = 0 }]
        };

        var method = typeof(RfSurveyService).GetMethod("BuildSiteSetupRequest", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildSiteSetupRequest");
        var request = (RfSurveyCreateRequest)(method.Invoke(null, [desired])
            ?? throw new InvalidOperationException("BuildSiteSetupRequest returned null."));

        var system = Assert.Single(request.SystemDefinitions!);
        Assert.Equal([773_781_250, 773_031_250, 773_281_250, 773_531_250], system.ControlChannelsHz);
    }

    [Fact]
    public void BuildSourcePlanSystems_PrefersProvenChannelsAndRetainsAuthoritativeAlternates()
    {
        var profile = new RfSurveyProfileDto
        {
            Systems =
            [
                new RfSurveySystemDto(
                    "etv-raymond-hinds",
                    "ETV Raymond Hinds",
                    [773_031_250, 773_281_250, 773_531_250, 773_781_250],
                    [])
            ]
        };
        var experiment = new RfSurveyExperimentDto
        {
            Type = "rf_validation_sweep",
            CreatedAtUtc = DateTime.UtcNow,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                candidates = new object[]
                {
                    new { systemShortName = "etv-raymond-hinds", controlChannelHz = 773_781_250L, rfStatus = "measured", metricsStatus = "passed", snrDb = 18.0 },
                    new { systemShortName = "etv-raymond-hinds", controlChannelHz = 773_281_250L, rfStatus = "measured", metricsStatus = "passed", snrDb = 12.0 },
                    new { systemShortName = "etv-raymond-hinds", controlChannelHz = 773_031_250L, rfStatus = "measured", metricsStatus = "failed", snrDb = 20.0 }
                }
            }, EngineConfig.JsonOptions())
        };
        var warnings = new List<string>();
        var method = typeof(RfSurveyService).GetMethod("BuildSourcePlanSystems", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildSourcePlanSystems");

        var result = (IReadOnlyList<RfSurveySystemDto>)(method.Invoke(null, [profile, new[] { "etv-raymond-hinds" }, new[] { experiment }, warnings])
            ?? throw new InvalidOperationException("BuildSourcePlanSystems returned null."));

        var system = Assert.Single(result);
        Assert.Equal([773_781_250, 773_281_250, 773_031_250, 773_531_250], system.ControlChannelsHz);
        Assert.Contains(warnings, warning => warning.Contains("retained 2 authoritative alternate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildPendingChanges_DetectsPerSiteChannelDriftEvenWhenAggregateMatches()
    {
        var desired = new SiteSetupConfig
        {
            Systems =
            [
                new RfSurveySystemDto("site-a", "Site A", [100, 200], []),
                new RfSurveySystemDto("site-b", "Site B", [300], [])
            ],
            SourcePlanSystemShortNames = ["site-a", "site-b"]
        };
        var applied = new SiteSetupAppliedConfigDto(
            "config.json",
            true,
            "hash",
            DateTime.UtcNow,
            ["site-a", "site-b"],
            [100, 200, 300],
            [],
            [
                new SiteSetupAppliedSystemDto("site-a", [100]),
                new SiteSetupAppliedSystemDto("site-b", [200, 300])
            ]);
        var method = typeof(SiteSetupService).GetMethod("BuildControlChannelPendingChanges", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SiteSetupService).FullName, "BuildControlChannelPendingChanges");

        var changes = (IReadOnlyList<SiteSetupPendingChangeDto>)(method.Invoke(null, [desired, applied, new[] { "site-a", "site-b" }])
            ?? throw new InvalidOperationException("BuildControlChannelPendingChanges returned null."));

        Assert.Equal(2, changes.Count(change => change.Category == "Control Channels"));
        Assert.Contains(changes, change => change.Summary.StartsWith("Site A:", StringComparison.Ordinal));
        Assert.Contains(changes, change => change.Summary.StartsWith("Site B:", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizeRfSelections_KeepsMeasurementsBoundToDifferentSources()
    {
        var values = new[]
        {
            new SiteSetupRfSelection { FrequencyHz = 773_000_000, SourceIndex = 0, SourceSerial = "A0", Gain = "15", SampleRateHz = 6_000_000, ErrorHz = 1_250 },
            new SiteSetupRfSelection { FrequencyHz = 773_000_000, SourceIndex = 1, SourceSerial = "B1", Gain = "12", SampleRateHz = 3_000_000, ErrorHz = -500 },
            new SiteSetupRfSelection { FrequencyHz = 773_000_000, SourceIndex = 0, SourceSerial = "a0", Gain = "14", SampleRateHz = 6_000_000, ErrorHz = 1_100 }
        };

        var normalized = InvokeNormalize(values, []);

        Assert.Equal(2, normalized.Count);
        Assert.Collection(normalized,
            first =>
            {
                Assert.Equal(0, first.SourceIndex);
                Assert.Equal("a0", first.SourceSerial);
                Assert.Equal("14", first.Gain);
                Assert.Equal(1_100, first.MeasuredSignalOffsetHz);
                Assert.Null(first.ErrorHz);
            },
            second =>
            {
                Assert.Equal(1, second.SourceIndex);
                Assert.Equal("B1", second.SourceSerial);
                Assert.Equal("12", second.Gain);
                Assert.Equal(-500, second.MeasuredSignalOffsetHz);
                Assert.Null(second.ErrorHz);
            });
    }

    [Fact]
    public void NormalizeSources_DerivesConfiguredHardwareSerial()
    {
        var method = typeof(SiteSetupService).GetMethod("NormalizeSources", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SiteSetupService).FullName, "NormalizeSources");
        var sources = new[] { new RfSurveySourceDto(0, "airspy=637862DC2E3A19D7", "", "airspy", 773_000_000, 6_000_000, 3_600, "21") };

        var normalized = (IReadOnlyList<RfSurveySourceDto>)(method.Invoke(null, [sources])
            ?? throw new InvalidOperationException("NormalizeSources returned null."));

        Assert.Equal("637862DC2E3A19D7", normalized[0].Serial);
    }

    [Fact]
    public void Guidance_AcceptsImplicitAssignmentsFromSelectedServerPlan()
    {
        var method = typeof(SiteSetupService).GetMethod("BuildGuidance", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SiteSetupService).FullName, "BuildGuidance");
        var desired = new SiteSetupConfig
        {
            Systems = [new RfSurveySystemDto("hinds", "Hinds County", [851_100_000], [], "1", "mswin")],
            Sources = [new RfSurveySourceDto(0, "airspy=ABC", "ABC", "airspy", 851_100_000, 6_000_000, 0, "21")],
            SourcePlanSystemShortNames = ["hinds"],
            SelectedSourceIndexes = [0],
            SourceAssignments = new Dictionary<string, int>(),
            RfSelections = [new SiteSetupRfSelection { FrequencyHz = 851_100_000, SourceIndex = 0, SourceSerial = "ABC" }]
        };
        var applied = new SiteSetupAppliedConfigDto("config.json", true, "hash", DateTime.UtcNow, ["hinds"], [851_100_000], []);

        var guidance = (SiteSetupGuidanceDto)(method.Invoke(null, [desired, applied, Array.Empty<SiteSetupPendingChangeDto>(), "active", "Monitoring active."])
            ?? throw new InvalidOperationException("BuildGuidance returned null."));

        Assert.Equal("RF choices recorded", guidance.Validation.Value);
        Assert.Equal("ok", guidance.Validation.State);
    }

    private static IReadOnlyList<SiteSetupRfSelection> InvokeNormalize(IEnumerable<SiteSetupRfSelection> values, IEnumerable<RfSurveySourceDto> sources)
    {
        var method = typeof(SiteSetupService).GetMethod("NormalizeRfSelections", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SiteSetupService).FullName, "NormalizeRfSelections");
        return (IReadOnlyList<SiteSetupRfSelection>)(method.Invoke(null, [values, sources])
            ?? throw new InvalidOperationException("NormalizeRfSelections returned null."));
    }
}
