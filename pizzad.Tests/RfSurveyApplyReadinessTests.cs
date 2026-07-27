namespace pizzad.Tests;

public sealed class RfSurveyApplyReadinessTests
{
    [Fact]
    public void SelectDraftSourceCenter_PreservesConfiguredCenterThatCoversPlan()
    {
        var center = RfSurveyService.SelectDraftSourceCenter(855_287_500, 6_000_000, 852_475_000, 858_100_000, 856_300_000);

        Assert.Equal(855_287_500, center);
    }

    [Fact]
    public void SelectDraftSourceCenter_RecentersWhenConfiguredWindowCannotCoverPlan()
    {
        var center = RfSurveyService.SelectDraftSourceCenter(855_287_500, 6_000_000, 851_000_000, 858_100_000, 854_550_000);

        Assert.Equal(854_550_000, center);
    }

    [Fact]
    public void EnsureCallAndTranscriptionProof_RejectsMissingProof()
    {
        var detail = Detail([]);

        var error = Assert.Throws<InvalidOperationException>(() => RfSurveyService.EnsureCallAndTranscriptionProof(detail));

        Assert.Contains("Call capture proof must pass", error.Message);
        Assert.Contains("Transcription proof must pass", error.Message);
    }

    [Fact]
    public void EnsureCallAndTranscriptionProof_UsesLatestResultForEachGate()
    {
        var now = DateTime.UtcNow;
        var detail = Detail([
            Experiment("voice_capture_trial", "failed", now.AddMinutes(-2)),
            Experiment("voice_capture_trial", "passed", now),
            Experiment("transcription_gate", "passed", now)
        ]);

        RfSurveyService.EnsureCallAndTranscriptionProof(detail);
    }

    [Fact]
    public void EnsureCallAndTranscriptionProof_RejectsLatestFailedResult()
    {
        var now = DateTime.UtcNow;
        var detail = Detail([
            Experiment("voice_capture_trial", "passed", now),
            Experiment("transcription_gate", "passed", now.AddMinutes(-2)),
            Experiment("transcription_gate", "failed", now)
        ]);

        var error = Assert.Throws<InvalidOperationException>(() => RfSurveyService.EnsureCallAndTranscriptionProof(detail));

        Assert.DoesNotContain("Call capture proof must pass", error.Message);
        Assert.Contains("Transcription proof must pass", error.Message);
    }

    private static RfSurveyDetailDto Detail(IReadOnlyList<RfSurveyExperimentDto> experiments) => new(
        new RfSurveySessionDto(),
        new RfSurveyProfileDto(),
        experiments,
        [],
        null,
        []);

    private static RfSurveyExperimentDto Experiment(string type, string status, DateTime createdAtUtc) => new()
    {
        Type = type,
        Status = status,
        CreatedAtUtc = createdAtUtc
    };
}
