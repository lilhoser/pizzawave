using System.Text.Json;
namespace pizzad.Tests;

public sealed class RadioHealthSummaryTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static SystemRecommendationDto Finding(string id = "queue-pressure", string severity = "high")
        => new(id, "pizzad", severity, "Transcription queue is growing", "private endpoint/error detail",
            "private raw action", new("pizzad", "jobs", ""), []) { FindingId = 12, EvidenceWindow = "Last 10 minutes" };
    private static SystemRecommendationsDto Source(params SystemRecommendationDto[] items)
        => new(0, 0, 0, 0, 0, 0, items, [], [], []) { GeneratedAtUtc = Now };

    [Fact]
    public void NoPipelineEscalationDoesNotClaimUnobservedReceiverHealthy()
    {
        var report = new RadioHealthAssessment().Assess(Source(), Now);
        Assert.Equal("unknown", report.Condition);
        Assert.Equal("partial", report.Confidence);
        Assert.Contains("No active High or Critical pipeline",report.Impact);
        Assert.Equal(1, report.Coverage.Observed);
        Assert.Equal("sdr-1861", Assert.Single(report.Coverage.Missing));
        Assert.Equal("unknown", report.Protection.State);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("critical")]
    public void ActivePipelineRecommendationsEscalate(string severity)
    {
        var report = new RadioHealthAssessment().Assess(Source(Finding(severity: severity)), Now);
        Assert.Equal("degraded", report.Condition);
        Assert.Equal(severity, Assert.Single(report.Exceptions).Severity);
        Assert.Contains("Transcripts", report.Impact);
    }

    [Theory]
    [InlineData("medium")]
    [InlineData("low")]
    public void LowerSeveritiesDoNotBecomeEstateAlarms(string severity)
        => Assert.Empty(new RadioHealthAssessment().Assess(Source(Finding(severity: severity)), Now).Exceptions);

    [Fact]
    public void ExpectedOrRecurringRfSitesNeverBecomePipelineEscalations()
    {
        var source = Source(Finding("tr-rf-temporal-v2:whiteoakmt-nbradley", "critical"),
            Finding("future-rf-finding", "high") with { Target = new("metrics", "rf", "") },
            Finding("legacy-rf", "high") with { Target = new("tr", "metrics", "") });
        Assert.Empty(new RadioHealthAssessment().Assess(source, Now).Exceptions);
    }

    [Theory]
    [InlineData("known_issue")]
    [InlineData("resolved")]
    [InlineData("dismissed")]
    public void AcceptedOrClosedFindingsDoNotEscalate(string workflow)
        => Assert.Empty(new RadioHealthAssessment().Assess(Source(Finding() with { WorkflowStatus = workflow }), Now).Exceptions);

    [Theory]
    [InlineData("investigating")]
    [InlineData("unresolved")]
    [InlineData("monitoring")]
    public void OpenOperationalProblemsRemainVisible(string workflow)
        => Assert.Single(new RadioHealthAssessment().Assess(Source(Finding() with { WorkflowStatus = workflow }), Now).Exceptions);

    [Fact]
    public void QuietFindingsAndImprovementsAreNotPipelineFaults()
    {
        var source = Source(Finding() with { ActivityState = "quiet" }, Finding("optimization", "critical") with { Kind = "improvement" });
        Assert.Empty(new RadioHealthAssessment().Assess(source, Now).Exceptions);
    }

    [Fact]
    public void KnownAndHistoricalCollectionsNeverBecomeCurrentAlarms()
    {
        var source = Source() with { KnownIssues = [Finding()], RecentlyResolved = [Finding()], History = [Finding()] };
        Assert.Empty(new RadioHealthAssessment().Assess(source, Now).Exceptions);
    }

    [Theory]
    [InlineData(-361)]
    [InlineData(31)]
    public void FreshReportCannotFreshenStaleOrFutureRecommendations(int seconds)
    {
        var report = new RadioHealthAssessment().Assess(Source(Finding()) with { GeneratedAtUtc = Now.AddSeconds(seconds) }, Now);
        Assert.Empty(report.Exceptions);
        Assert.Equal(0, report.Coverage.Observed);
        Assert.Contains("ot-pipeline", report.Coverage.Missing);
        Assert.Contains("not current",report.Impact);
    }

    [Fact]
    public void FiveMinuteSharedRecommendationCacheRemainsCurrent()
        => Assert.Single(new RadioHealthAssessment().Assess(Source(Finding()) with { GeneratedAtUtc = Now.AddMinutes(-5) }, Now).Exceptions);

    [Fact]
    public void CriticalFindingsLeadAndProjectionDoesNotExportRawDiagnostics()
    {
        var report = new RadioHealthAssessment().Assess(Source(Finding(), Finding("ai-generation-health", "critical")), Now);
        Assert.Equal("critical", report.Exceptions[0].Severity);
        Assert.Contains("Incident creation",report.Impact);
        var text = System.Text.Encoding.UTF8.GetString(report.Serialize());
        Assert.DoesNotContain("private endpoint",text);
        Assert.DoesNotContain("private raw action",text);
        Assert.Contains("?page=system",text);
        Assert.DoesNotContain("/api/",text);
    }

    [Fact]
    public void SpecificFindingLinksUseTheWebUiAndSafeIds()
    {
        var issue = Assert.Single(new RadioHealthAssessment().Assess(Source(Finding()), Now).Exceptions);
        Assert.EndsWith("&finding=12",issue.DetailsUrl);
        issue = Assert.Single(new RadioHealthAssessment().Assess(Source(Finding() with { FindingId = long.MaxValue }), Now).Exceptions);
        Assert.Equal(RadioHealthAssessment.RecommendationsUrl,issue.DetailsUrl);
    }

    [Fact]
    public void MultiFindingUnicodePayloadRemainsBounded()
    {
        var items = Enumerable.Range(0,20).Select(i => Finding(i.ToString()) with { Title = new string('\u754c',1000), EvidenceWindow = new string('\u754c',1000) }).ToArray();
        var report = new RadioHealthAssessment().Assess(Source(items), Now);
        Assert.Equal(20,report.ExceptionCount);
        Assert.Equal(3,report.Exceptions.Count);
        Assert.True(report.Serialize().Length <= RadioHealthSummary.MaxPayloadBytes);
    }

    [Fact]
    public void FirstObservationSurvivesRefreshAndResetsAfterRecovery()
    {
        var assessor = new RadioHealthAssessment();
        var first = assessor.Assess(Source(Finding()), Now);
        var refresh = assessor.Assess(Source(Finding()), Now.AddMinutes(1));
        Assert.Equal(first.Exceptions[0].FirstObservedAt,refresh.Exceptions[0].FirstObservedAt);
        assessor.Assess(Source(), Now.AddMinutes(1));
        Assert.Equal(Now.AddMinutes(2),assessor.Assess(Source(Finding()), Now.AddMinutes(2)).Exceptions[0].FirstObservedAt);
    }

    [Fact]
    public void APreviouslyUnexportedFindingKeepsItsFirstActiveObservation()
    {
        var assessor = new RadioHealthAssessment();
        assessor.Assess(Source(Finding("a"),Finding("b"),Finding("c"),Finding("d")),Now);
        var promoted = assessor.Assess(Source(Finding("d")),Now.AddMinutes(1));
        Assert.Equal(Now,Assert.Single(promoted.Exceptions).FirstObservedAt);
    }

    [Fact]
    public void ScheduleSendsChangedFindingsAndHeartbeatButNotReportTimeOnly()
    {
        var report = new RadioHealthAssessment().Assess(Source(Finding()), Now);
        var schedule = new RadioHealthPublishSchedule();
        Assert.True(schedule.IsDue(report,Now));
        schedule.Published(report,Now);
        Assert.False(schedule.IsDue(report with { ReportId = "another", GeneratedAt = Now.AddMinutes(1) },Now.AddMinutes(1)));
        Assert.True(schedule.IsDue(report,Now.AddMinutes(5)));
        Assert.True(schedule.IsDue(report with { Exceptions = [report.Exceptions[0] with { Summary = "Another active problem" }] },Now.AddMinutes(1)));
    }

    [Fact]
    public void FailedPublishDoesNotAdvanceSchedule()
    {
        var schedule = new RadioHealthPublishSchedule();
        var report = new RadioHealthAssessment().Assess(Source(),Now);
        Assert.True(schedule.IsDue(report,Now));
        Assert.True(schedule.IsDue(report,Now.AddMinutes(1)));
    }

    [Fact]
    public void PublisherDefaultsDisabledAndRequiresProtectedCredentialPath()
    {
        var options = new RadioHealthPublisherOptions(); options.Validate(); Assert.False(options.Enabled);
        options.Enabled = true; Assert.Throws<InvalidOperationException>(options.Validate);
        options.BrokerHost = "broker.local"; options.Username = "radio"; options.PasswordFile = "relative-password";
        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
