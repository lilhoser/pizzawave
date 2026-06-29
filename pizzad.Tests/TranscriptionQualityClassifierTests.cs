namespace pizzad.Tests;

public sealed class TranscriptionQualityClassifierTests
{
    [Fact]
    public void Classify_RejectsRepeatedPhraseExpansion()
    {
        var transcript = string.Join(' ', Enumerable.Repeat("Medic three E four from our liner where do you like us", 6));

        var quality = TranscriptionQualityClassifier.Classify(transcript, audioSeconds: 18);

        Assert.Equal("poor_quality", quality.Status);
        Assert.Equal("repetitive", quality.Reason);
        Assert.False(quality.IncludeInSummaries);
    }

    [Fact]
    public void Classify_RejectsLowInformationTokenChurn()
    {
        var transcript = "10 4 10 4 10 4 10 4 10 4 10 4 go ahead go ahead go ahead go ahead go ahead go ahead";

        var quality = TranscriptionQualityClassifier.Classify(transcript, audioSeconds: 20);

        Assert.Equal("poor_quality", quality.Status);
        Assert.Equal("repetitive", quality.Reason);
    }

    [Fact]
    public void Classify_RejectsPhysicallyImplausibleTextDensity()
    {
        var transcript = string.Join(' ', Enumerable.Range(1, 48).Select(i => $"word{i}"));

        var quality = TranscriptionQualityClassifier.Classify(transcript, audioSeconds: 5);

        Assert.Equal("poor_quality", quality.Status);
        Assert.Equal("overexpanded", quality.Reason);
    }

    [Fact]
    public void Classify_AllowsNormalAnchoredDispatch()
    {
        var transcript = "Engine 7 respond to 101 Park City Road for a technical rescue. Caller advises one person trapped near the rear entrance.";

        var quality = TranscriptionQualityClassifier.Classify(transcript, audioSeconds: 18);

        Assert.Equal("complete", quality.Status);
        Assert.Equal("ok", quality.Reason);
        Assert.True(quality.IncludeInSummaries);
    }
}
