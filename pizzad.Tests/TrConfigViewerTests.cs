using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class TrConfigViewerTests
{
    [Fact]
    public async Task ViewerCatalogGroupsActiveDraftNamedBackupsAndMeaningfulRfArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-tr-viewer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var trRoot = Path.Combine(root, "tr");
            var appRoot = Path.Combine(root, "app");
            var surveyRoot = Path.Combine(appRoot, "rf-surveys", "survey-1");
            Directory.CreateDirectory(trRoot);
            Directory.CreateDirectory(appRoot);
            Directory.CreateDirectory(surveyRoot);
            var livePath = Path.Combine(trRoot, "config.json");
            await File.WriteAllTextAsync(livePath, ConfigJson("live", 851_012_500));
            var recordedBackup = Path.Combine(trRoot, "config.json.bak-20260715");
            await File.WriteAllTextAsync(recordedBackup, ConfigJson("backup", 852_012_500));
            await File.WriteAllTextAsync(Path.Combine(trRoot, "config.json.pre-gain-test-20260714.bak"), ConfigJson("pre-gain", 853_012_500));
            await File.WriteAllTextAsync(Path.Combine(appRoot, "tr-config-editor-draft.json"), ConfigJson("draft", 854_012_500));
            await File.WriteAllTextAsync(Path.Combine(surveyRoot, "tr-config-before.json"), ConfigJson("before", 855_012_500));
            await File.WriteAllTextAsync(Path.Combine(surveyRoot, "tr-config-candidate.json"), ConfigJson("candidate", 856_012_500));
            await File.WriteAllTextAsync(Path.Combine(surveyRoot, "tr-config-selected-rf-validation-best.json"), ConfigJson("selected", 857_012_500));
            await File.WriteAllTextAsync(Path.Combine(surveyRoot, "tr-config-metrics-candidate-noisy.json"), ConfigJson("temporary", 858_012_500));

            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = appRoot },
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = livePath }
            };
            var provenance = new TrConfigArtifactProvenanceStore(config);
            await provenance.RecordAsync(recordedBackup, "Setup", "Safety backup before a source change.", "Raymond source plan", CancellationToken.None);
            var service = new TrConfigService(config, NullLogger<TrConfigService>.Instance, provenance);
            var sessions = new[]
            {
                new RfSurveySessionDto { Id = "survey-1", SiteLabel = "Raymond", Status = "completed", ArtifactPath = surveyRoot }
            };

            var viewer = await service.GetViewerAsync(sessions, null, CancellationToken.None);

            Assert.Equal("active", viewer.Selected!.Artifact.Kind);
            Assert.Equal(2, viewer.Selected.Summary.Systems.Count + viewer.Selected.Summary.Sources.Count);
            Assert.Equal(2, viewer.Artifacts.Count(row => row.Kind == "backup" && row.Path.StartsWith(trRoot, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(viewer.Artifacts, row => row.Path == recordedBackup && row.HasRecordedOrigin && row.Workflow == "Setup" && row.RelatedActivity == "Raymond source plan");
            Assert.Contains(viewer.Artifacts, row => row.Kind == "draft");
            Assert.Contains(viewer.Artifacts, row => row.Name == "RF workflow candidate" && row.RelatedActivity.Contains("Raymond", StringComparison.Ordinal));
            Assert.Contains(viewer.Artifacts, row => row.Name == "Selected RF validation candidate");
            Assert.DoesNotContain(viewer.Artifacts, row => row.Path.Contains("metrics-candidate", StringComparison.OrdinalIgnoreCase));

            var candidate = viewer.Artifacts.Single(row => row.Name == "RF workflow candidate");
            var selected = await service.GetViewerAsync(sessions, candidate.Id, CancellationToken.None);
            Assert.Equal(candidate.Id, selected.SelectedArtifactId);
            Assert.Contains("candidate", selected.Selected!.ConfigJson, StringComparison.Ordinal);
            Assert.True(selected.Selected.ParseOk);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnknownViewerArtifactIdFallsBackToActiveWithoutReadingArbitraryPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-tr-viewer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var livePath = Path.Combine(root, "config.json");
            await File.WriteAllTextAsync(livePath, ConfigJson("live", 851_012_500));
            var config = new EngineConfig
            {
                Storage = new StorageConfig { AppDataRoot = root },
                TrunkRecorder = new TrunkRecorderConfig { ConfigPath = livePath }
            };
            var service = new TrConfigService(config, NullLogger<TrConfigService>.Instance);

            var viewer = await service.GetViewerAsync([], "../../untrusted", CancellationToken.None);

            Assert.Equal(viewer.ActiveArtifactId, viewer.SelectedArtifactId);
            Assert.True(viewer.Selected!.Artifact.IsActive);
            Assert.Contains("live", viewer.Selected.ConfigJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string ConfigJson(string shortName, long center) => $$"""
    {
      "systems": [{ "shortName": "{{shortName}}", "type": "p25", "control_channels": [851012500] }],
      "sources": [{ "device": "airspy", "center": {{center}}, "rate": 6000000 }]
    }
    """;
}
