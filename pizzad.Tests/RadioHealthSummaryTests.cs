using System.Text.Json;

namespace pizzad.Tests;

public sealed class RadioHealthSummaryTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    private static HealthDto Healthy()
        => JsonSerializer.Deserialize<HealthDto>(JsonSerializer.Serialize(new
        {
            status = "ok", serverTimeUtc = Now, databasePath = "private-path", audioRoot = "private-audio",
            liveTrActivity = new { status = "ok", stale = false, lastActivityUtc = Now, lastTrHealthUtc = Now },
            incidentAnalysisQueueHealth = new { status = "ok" },
            aiCompletionHealth = new { status = "ok" },
            embeddingHealth = new { enabled = true, status = "ok", qdrantOk = true, embeddingEndpointOk = true }
        }), EngineConfig.JsonOptions())!;

    private static LiveRfStatusDto Rf(string tone = "ok") => new(Now, 120, 300, tone, "assessed",
        [new("OT", tone, "assessed", 40, 0, 5, 0, 0, Now, 0, "existing-domain-assessment", "detail")]);

    [Fact]
    public void MissingRemoteCoverageCannotBecomeWholeAreaHealthy()
    {
        var report = new RadioHealthAssessment().Assess(Healthy(), Rf(), Now);
        Assert.Equal("unknown", report.Condition);
        Assert.Equal("partial", report.Confidence);
        Assert.Equal(3, report.Coverage.Observed);
        Assert.Equal("sdr-1861", Assert.Single(report.Coverage.Missing));
        Assert.Equal("unknown", report.Protection.State);
    }

    [Fact]
    public void QueuePressureOverridesTopLevelOk()
    {
        var report = new RadioHealthAssessment().Assess(Healthy() with { QueueUnderPressure = true }, Rf(), Now);
        Assert.Equal("degraded", report.Condition);
        Assert.Equal("radio:queue-pressure", Assert.Single(report.Exceptions).Id);
    }

    [Fact]
    public void DomainCaptureFaultRemainsVisibleEvenWhenTheDomainMarksItStale()
    {
        var health = Healthy();
        var report = new RadioHealthAssessment().Assess(health with
        { LiveTrActivity = health.LiveTrActivity with { Status = "fault", Stale = true } }, Rf(), Now);
        Assert.Equal("degraded", report.Condition);
        Assert.Contains(report.Exceptions, item => item.Id == "radio:capture");
    }

    [Fact]
    public void IntentionalCaptureStopIsReportedWithoutAnUnexpectedOutageAlarm()
    {
        var health = Healthy();
        var report = new RadioHealthAssessment().Assess(health with
        { LiveTrActivity = health.LiveTrActivity with { Status = "stopped", Stale = false } }, Rf("error"), Now);
        Assert.Equal("unknown", report.Condition);
        Assert.Empty(report.Exceptions);
        Assert.Contains("intentionally stopped", report.Impact);
        Assert.Equal(2, report.Coverage.Required);
    }

    [Theory]
    [InlineData(-181)]
    [InlineData(31)]
    public void FreshReportDoesNotFreshenInvalidSourceTime(int offset)
    {
        var report = new RadioHealthAssessment().Assess(Healthy() with
        { ServerTimeUtc = Now.AddSeconds(offset) }, Rf(), Now);
        Assert.Equal("unknown", report.Condition);
        Assert.Contains("ot-processing", report.Coverage.Missing);
        Assert.Contains("ot-receiver", report.Coverage.Missing);
    }

    [Fact]
    public void StaleRfSnapshotCannotReassertItsOldFault()
    {
        var report = new RadioHealthAssessment().Assess(Healthy(),
            Rf("error") with { GeneratedAtUtc = Now.AddMinutes(-4) }, Now);
        Assert.Contains("ot-rf", report.Coverage.Missing);
        Assert.Empty(report.Exceptions);
    }

    [Fact]
    public void DomainFaultsAreBoundedAndDoNotExposeSourcePaths()
    {
        var health = Healthy() with { QueueUnderPressure = true, AiWorkBlockedReason = "sensitive detail" };
        health = health with
        {
            IncidentAnalysisQueueHealth = health.IncidentAnalysisQueueHealth with { Status = "degraded" },
            AiCompletionHealth = health.AiCompletionHealth with { Status = "degraded" },
            EmbeddingHealth = health.EmbeddingHealth with { Status = "degraded" }
        };
        var report = new RadioHealthAssessment().Assess(health, Rf("warning"), Now);
        Assert.True(report.ExceptionCount > 3);
        Assert.Equal(3, report.Exceptions.Count);
        var bytes = report.Serialize();
        Assert.True(bytes.Length <= RadioHealthSummary.MaxPayloadBytes);
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("private-path", json);
        Assert.DoesNotContain("private-audio", json);
        Assert.DoesNotContain("sensitive detail", json);
        using var parsed = JsonDocument.Parse(bytes);
        Assert.Equal("radio", parsed.RootElement.GetProperty("aor_id").GetString());
        Assert.True(parsed.RootElement.TryGetProperty("generated_at", out _));
    }

    [Fact]
    public void FirstObservedPersistsUntilRecoveryAndResetsForANewIncident()
    {
        var assessor = new RadioHealthAssessment();
        var health = Healthy() with { QueueUnderPressure = true };
        var first = assessor.Assess(health, Rf(), Now);
        var second = assessor.Assess(health, Rf(), Now.AddMinutes(1));
        Assert.Equal(first.Exceptions[0].FirstObservedAt, second.Exceptions[0].FirstObservedAt);
        assessor.Assess(Healthy(), Rf(), Now.AddMinutes(1));
        var recurrence = assessor.Assess(health, Rf(), Now.AddMinutes(2));
        Assert.Equal(Now.AddMinutes(2), recurrence.Exceptions[0].FirstObservedAt);
    }

    [Fact]
    public void ScheduleSendsChangesAndHeartbeatButNotTimestampOnlyChanges()
    {
        var assessor = new RadioHealthAssessment();
        var first = assessor.Assess(Healthy(), Rf(), Now);
        var schedule = new RadioHealthPublishSchedule();
        Assert.True(schedule.IsDue(first, Now));
        schedule.Published(first, Now);
        var refresh = first with { GeneratedAt = Now.AddMinutes(1), ReportId = "another" };
        Assert.False(schedule.IsDue(refresh, Now.AddMinutes(1)));
        Assert.True(schedule.IsDue(refresh, Now.AddMinutes(5)));
        Assert.True(schedule.IsDue(refresh with { Condition = "degraded" }, Now.AddMinutes(1)));
    }

    [Fact]
    public void FailedPublishDoesNotAdvanceSchedule()
    {
        var report = new RadioHealthAssessment().Assess(Healthy(), Rf(), Now);
        var schedule = new RadioHealthPublishSchedule();
        Assert.True(schedule.IsDue(report, Now));
        Assert.True(schedule.IsDue(report, Now.AddMinutes(1)));
    }

    [Fact]
    public void CriticalReceptionLeadsWithSiteReasonAndHonestImpact()
    {
        var site = Rf("error").Sites[0] with { SystemShortName = "North Bradley",
            DecodeAssessment = new("error", "critical", null, "Control-channel decoding is effectively unavailable.") };
        var report = new RadioHealthAssessment().Assess(Healthy() with { QueueUnderPressure = true },
            Rf("error") with { Sites = [site] }, Now);
        var issue = report.Exceptions[0];
        Assert.Contains("North Bradley", issue.Summary);
        Assert.Contains("cannot be decoded", issue.Summary);
        Assert.Contains("not measured", report.Impact);
        Assert.Contains("effectively unavailable", issue.Evidence);
        Assert.Equal("http://192.168.1.173:8080/api/v1/system/rf/live", issue.DetailsUrl);
    }

    [Fact]
    public void RetuneWarningDoesNotAssertLostCalls()
    {
        var site = Rf("warning").Sites[0] with {
            DecodeAssessment = new("ok", "local", 40, "Decoding available."),
            RetunesAssessment = new("warning", "local", 10, "Retunes are elevated, but decoding remains available.") };
        var report = new RadioHealthAssessment().Assess(Healthy(), Rf("warning") with { Sites = [site] }, Now);
        Assert.Contains("repeatedly changes", report.Exceptions[0].Summary);
        Assert.Contains("not been established", report.Impact);
    }

    [Fact]
    public void RecoveryHoldDoesNotRepeatThePreviousFaultAsCurrentEvidence()
    {
        var site = Rf("warning").Sites[0] with { Status = "Recovering",
            DecodeAssessment = new("ok", "local", 40, "Healthy now.") };
        var report = new RadioHealthAssessment().Assess(Healthy(), Rf("warning") with { Sites = [site] }, Now);
        Assert.Contains("recovering", report.Exceptions[0].Summary);
        Assert.Contains("Current RF readings are healthy", report.Impact);
    }

    [Fact]
    public void MultipleSitesStayBoundedWithoutExportingCombinedDiagnosticText()
    {
        var sites = Enumerable.Range(0, 20).Select(i => Rf("error").Sites[0] with {
            SystemShortName = i + new string('\u754c',100), Detail = "private full diagnostic text",
            DecodeAssessment = new("error", "critical", null, new string('\u754c',1000)) }).ToArray();
        var report = new RadioHealthAssessment().Assess(Healthy(), Rf("error") with { Sites = sites }, Now);
        Assert.Equal(20, report.ExceptionCount);
        Assert.Equal(3, report.Exceptions.Count);
        Assert.True(report.Serialize().Length <= RadioHealthSummary.MaxPayloadBytes);
        Assert.DoesNotContain("private full diagnostic", System.Text.Encoding.UTF8.GetString(report.Serialize()));
    }

    [Fact]
    public void MaterialEvidenceChangesArePublishedWithoutWaitingForHeartbeat()
    {
        var report = new RadioHealthAssessment().Assess(Healthy(), Rf("warning"), Now);
        var schedule = new RadioHealthPublishSchedule();
        schedule.Published(report, Now);
        var changed = report with { Exceptions = [report.Exceptions[0] with { Evidence = "Changed operational finding" }] };
        Assert.True(schedule.IsDue(changed, Now.AddMinutes(1)));
    }

    [Fact]
    public void PublisherDefaultsDisabledAndRequiresAnExplicitProtectedCredentialPath()
    {
        var options = new RadioHealthPublisherOptions();
        options.Validate();
        Assert.False(options.Enabled);
        options.Enabled = true;
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.BrokerHost = "broker.local";
        options.Username = "radio";
        options.PasswordFile = "relative-password";
        Assert.Throws<InvalidOperationException>(options.Validate);
    }
}
