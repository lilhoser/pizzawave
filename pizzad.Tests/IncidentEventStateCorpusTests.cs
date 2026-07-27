namespace pizzad.Tests;

public sealed class IncidentEventStateCorpusTests
{
    [Fact]
    public void BundlePreservesEveryCallAndTreatsExistingLabelsAsMetadata()
    {
        var calls = new[]
        {
            Call(2, 200, "existing-category-b", 22, "existing-name-b", "second transcript"),
            Call(1, 100, "existing-category-a", 11, "existing-name-a", "first transcript")
        };

        var bundle = IncidentEventStateCorpusExporter.BuildObservationBundle(
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T15:00:00Z"),
            calls);

        Assert.Equal(["call:1", "call:2"], bundle.Observations.Select(row => row.ObservationId));
        Assert.Equal("existing-category-a", bundle.Observations[0].Metadata["category"].Value);
        Assert.Equal(IncidentEventStateMetadataOrigin.ApplicationDerived, bundle.Observations[0].Metadata["category"].Origin);
        Assert.Equal("11", bundle.Observations[0].Metadata["talkgroup"].Value);
        Assert.Equal(IncidentEventStateMetadataOrigin.SourceRecord, bundle.Observations[0].Metadata["talkgroup"].Origin);
        Assert.Equal("existing-name-a", bundle.Observations[0].Metadata["talkgroupName"].Value);
        Assert.Equal("first transcript", Assert.Single(bundle.Observations[0].Transcripts).Text);
        Assert.Null(bundle.Observations[0].Transcripts[0].CreatedAtUtc);
    }

    [Fact]
    public void FreezeIsStableAndMakesSelectionProtocolPartOfCorpusIdentity()
    {
        var bundle = IncidentEventStateCorpusExporter.BuildObservationBundle(
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T15:00:00Z"),
            [Call(1, 100, "source-category", 11, "source-name", "transcript")]);
        var manifest = Manifest("protocol-before-model-output-v1");

        var first = IncidentEventStateCorpusExporter.Freeze(manifest, [bundle]);
        var second = IncidentEventStateCorpusExporter.Freeze(manifest, [bundle]);
        var changedProtocol = IncidentEventStateCorpusExporter.Freeze(
            Manifest("different-protocol-v1"),
            [bundle]);

        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.Json, second.Json);
        Assert.NotEqual(first.ContentHash, changedProtocol.ContentHash);
        Assert.Equal(64, first.ContentHash.Length);
    }

    [Fact]
    public void FreezeRejectsObservationOutsideDeclaredWindow()
    {
        var bundle = IncidentEventStateCorpusExporter.BuildObservationBundle(
            "bundle-1",
            DateTimeOffset.Parse("2026-07-17T15:00:00Z"),
            [Call(1, 301, "source-category", 11, "source-name", "transcript")]);

        var error = Assert.Throws<ArgumentException>(() =>
            IncidentEventStateCorpusExporter.Freeze(Manifest("protocol-v1"), [bundle]));

        Assert.Contains("outside the frozen corpus window", error.Message, StringComparison.Ordinal);
    }

    private static IncidentEventStateCorpusManifest Manifest(string protocolIdentity) =>
        new(
            "ordinary-traffic-corpus",
            "v1",
            DateTimeOffset.Parse("2026-07-17T15:05:00Z"),
            100,
            300,
            protocolIdentity,
            "test-version");

    private static EngineCall Call(
        long id,
        long startTime,
        string category,
        long talkgroup,
        string talkgroupName,
        string transcription) =>
        new()
        {
            Id = id,
            UniqueKey = $"key-{id}",
            StartTime = startTime,
            StopTime = startTime + 5,
            Source = 3,
            SystemShortName = "source-system",
            CallstreamCallId = id + 1000,
            Talkgroup = talkgroup,
            TalkgroupName = talkgroupName,
            Frequency = 851_012_500,
            Category = category,
            AudioPath = $"audio/{id}.wav",
            Transcription = transcription,
            TranscriptionStatus = "source-status",
            QualityReason = "source-quality",
            RawMetadataJson = $"{{\"call\":{id}}}"
        };
}
