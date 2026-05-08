using System.Text.Json.Serialization;
using System.Text.Json;

namespace pizzad;

public sealed record EngineCall
{
    public long Id { get; init; }
    public string UniqueKey { get; init; } = string.Empty;
    public long StartTime { get; init; }
    public long StopTime { get; init; }
    public int Source { get; init; }
    public string SystemShortName { get; init; } = string.Empty;
    public long CallstreamCallId { get; init; }
    public long Talkgroup { get; init; }
    public string TalkgroupName { get; init; } = string.Empty;
    public double Frequency { get; init; }
    public string Category { get; init; } = "other";
    public string AudioPath { get; init; } = string.Empty;
    public string Transcription { get; init; } = string.Empty;
    public string TranscriptionStatus { get; init; } = "pending";
    public string QualityReason { get; init; } = "ok";
    public bool IsImported { get; init; }
    public bool IsAlertMatch { get; init; }
    public string RawMetadataJson { get; init; } = "{}";
}

public sealed record AlertMatchDto
{
    public long Id { get; init; }
    public long CallId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public long MatchedAt { get; init; }
    public bool IsImported { get; init; }
    public bool NotificationSuppressed { get; init; }
    public string SystemShortName { get; init; } = string.Empty;
    public long Talkgroup { get; init; }
    public string TalkgroupName { get; init; } = string.Empty;
    public string Category { get; init; } = "other";
    public string Transcription { get; init; } = string.Empty;
    public string TranscriptionStatus { get; init; } = string.Empty;
    public string QualityReason { get; init; } = string.Empty;
    public string AudioUrl { get; init; } = string.Empty;
}

public sealed record DashboardDto
{
    public IReadOnlyList<KpiDto> Kpis { get; init; } = [];
    public IReadOnlyList<HourCategoryDto> VolumeByHourCategory { get; init; } = [];
    public IReadOnlyList<QualityHourDto> QualityByHour { get; init; } = [];
    public IReadOnlyList<BarStatDto> ProblemTalkgroups { get; init; } = [];
    public IReadOnlyList<BarStatDto> InaudibleBySystem { get; init; } = [];
    public IReadOnlyList<BarStatDto> CategoryShare { get; init; } = [];
    public IReadOnlyList<TopTalkgroupDto> TopTalkgroups { get; init; } = [];
    public IReadOnlyList<AlertMatchDto> Alerts { get; init; } = [];
    public IReadOnlyList<IncidentDto> Incidents { get; init; } = [];
}

public sealed record KpiDto(string Label, string Value, string Subtext);

public sealed record HourCategoryDto(int Hour, string Category, int Count);

public sealed record QualityHourDto(int Hour, int Empty, int Failure, int Inaudible, int Short);

public sealed record BarStatDto(string Label, int Value, double Ratio, string ValueText);

public sealed record TopTalkgroupDto(
    string Label,
    long Talkgroup,
    int Count,
    double Share,
    long LastHeard,
    IReadOnlyList<double> Trend,
    IReadOnlyList<int> TrendCounts,
    IReadOnlyList<string> TrendLabels,
    string TrendStartLabel,
    string TrendBucketLabel,
    string TrendEndLabel);

public sealed record CategoryGroupDto(string Label, IReadOnlyList<EngineCall> Calls);

public sealed record CategoryInsightDto(
    long Id,
    string Title,
    string Detail,
    long FirstSeen,
    long LastSeen,
    double Score,
    int CallCount,
    IReadOnlyList<IncidentCallDto> Calls);

public sealed record InsightEventRecordDto
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Category { get; init; } = "other";
    public long FirstSeen { get; init; }
    public long LastSeen { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<IncidentCallDto> Calls { get; init; } = [];
}

public sealed record CategoryPageDto(
    string Category,
    string GroupBy,
    IReadOnlyList<CategoryGroupDto> Groups,
    IReadOnlyList<CategoryInsightDto> Insights,
    IReadOnlyList<IncidentDto> Incidents);

public sealed record IncidentDto
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public long FirstSeen { get; init; }
    public long LastSeen { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<IncidentCallDto> Calls { get; init; } = [];
}

public sealed record IncidentCallDto(long CallId, long RawTimestamp, string Transcript, string AudioUrl);

public sealed record JobDto
{
    public long Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Status { get; init; } = "queued";
    public int Total { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
}

public sealed record HealthDto(
    string Status,
    string Version,
    string DatabasePath,
    string AudioRoot,
    int QueueDepth,
    DateTime ServerTimeUtc);

public sealed record AuthInitDto(string Mode, bool ReadRequiresAuth, bool WriteRequiresAuth);

public sealed record TrHealthSampleDto
{
    public long Id { get; init; }
    public DateTime WindowStartUtc { get; init; }
    public DateTime WindowEndUtc { get; init; }
    public string Scope { get; init; } = "global";
    public int DecodeLines { get; init; }
    public int DecodeZero { get; init; }
    public double DecodeZeroPct { get; init; }
    public double DecodeRateTotal { get; init; }
    public double AvgDecodeRate => DecodeLines == 0 ? 0 : DecodeRateTotal / DecodeLines;
    public int Retunes { get; init; }
    public int CallsStarted { get; init; }
    public int CallsConcluded { get; init; }
    public int UpdateNotGrant { get; init; }
    public int NoTxRecorded { get; init; }
    public int SampleStops { get; init; }
    public int UnableSource { get; init; }
    public int TuningErrSamples { get; init; }
    public double TuningErrTotalAbsHz { get; init; }
    public double TuningErrMaxAbsHz { get; init; }
}

public sealed record TrHealthMetricDto(string Metric, string Value, string Notes, bool IsIssue);

public sealed record TrHealthSeriesDto(string Label, IReadOnlyList<double> Values, bool IsBaseline = false);

public sealed record TrHealthChartDto(
    string Title,
    string YAxisLabel,
    string ValueFormat,
    IReadOnlyList<string> Labels,
    IReadOnlyList<TrHealthSeriesDto> Series);

public sealed record TrHealthSummaryDto
{
    public string Title { get; init; } = "TR health summary";
    public string Window { get; init; } = string.Empty;
    public string LastWindow { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string SummaryText { get; init; } = string.Empty;
    public IReadOnlyList<TrHealthMetricDto> Metrics { get; init; } = [];
    public IReadOnlyList<TrHealthMetricDto> Systems { get; init; } = [];
    public IReadOnlyList<TrHealthMetricDto> Remedies { get; init; } = [];
    public IReadOnlyList<TrHealthChartDto> Charts { get; init; } = [];
    public IReadOnlyList<TrHealthSampleDto> Samples { get; init; } = [];
}

public sealed record TrTroubleshootDto(
    TrHealthSummaryDto Health,
    QualityAuditDto QualityAudit,
    object Config,
    string LogOutput,
    string Diagnostics,
    string InsightsText);

public sealed record QualityAuditDto(
    int TotalCalls,
    int ProblemCalls,
    int InaudibleCalls,
    double ProblemPercent,
    double InaudiblePercent,
    IReadOnlyList<QualityAuditGroupDto> ByReason,
    IReadOnlyList<QualityAuditGroupDto> BySystem,
    IReadOnlyList<QualityAuditGroupDto> ByTalkgroup,
    IReadOnlyList<QualityAuditHourDto> ByHour,
    IReadOnlyList<QualityAuditSampleDto> Samples);

public sealed record QualityAuditGroupDto(
    string Label,
    int TotalCalls,
    int ProblemCalls,
    int InaudibleCalls,
    double ProblemPercent,
    double InaudiblePercent);

public sealed record QualityAuditHourDto(int Hour, int TotalCalls, int ProblemCalls, int InaudibleCalls);

public sealed record QualityAuditSampleDto(
    long CallId,
    long StartTime,
    string SystemShortName,
    int Source,
    long Talkgroup,
    string TalkgroupName,
    string Category,
    double DurationSeconds,
    string TranscriptionStatus,
    string QualityReason,
    string Transcription,
    string AudioUrl);

public sealed record TimeRangeQuery(long? Start, long? End)
{
    public (long Start, long End) Resolve()
    {
        var end = End ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var start = Start ?? DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
        return (start, end);
    }
}

public sealed record SftpEstimateRequest(DateTime StartLocal, DateTime EndLocal);

public sealed record SftpEstimateResponse(int CandidateCount, long CandidateBytes, bool ExceedsQuickImportWindow, string Message);

public sealed record SftpAvailabilityResponse(
    bool Enabled,
    bool Available,
    string Host,
    string RemoteRoot,
    DateTime? EarliestLocal,
    DateTime? LatestLocal,
    int FileCount,
    long TotalBytes,
    int ScannedDirectories,
    int SkippedDirectories,
    string Message);

public sealed record SftpImportRequest(DateTime StartLocal, DateTime EndLocal, bool ConfirmLargeImport, int? CallCap, long? ByteCap);

public sealed record SettingsSectionDto(string Section, object Values);

public sealed record SseEvent([property: JsonPropertyName("type")] string Type, object Payload, long Id);

public sealed record JobControlRequest(string Action);

public sealed record GenerateSummaryRequest(long Start, long End, bool ConfirmLargeRange);

public sealed record TroubleshootInsightRequest(long Start, long End, bool BySystem, string Baseline);

public sealed record TroubleshootInsightResponse(string Text);

public sealed record SaveSettingsRequest(JsonElement Values);

public sealed record DiagnosticToolRequest(
    IReadOnlyList<long>? CallIds,
    long? Start,
    long? End,
    int? SampleCount,
    IReadOnlyList<string>? Models);

public sealed record DiagnosticModelDto(
    string Id,
    string Label,
    string Engine,
    bool Available,
    string Detail);

public sealed record DiagnosticToolResultDto(
    long JobId,
    string Tool,
    DateTime CreatedAtUtc,
    IReadOnlyList<DiagnosticToolRowDto> Rows);

public sealed record DiagnosticToolRowDto(
    long CallId,
    string Variant,
    string Model,
    string Status,
    int Score,
    double DurationMs,
    string Transcript,
    string AudioUrl,
    string Notes);

public sealed class EngineSectionUpdate
{
    public ServerConfig? Server { get; set; }
    public StorageConfig? Storage { get; set; }
    public IngestConfig? Ingest { get; set; }
}

public sealed record EngineAlertMatchResult(
    bool IsMatch,
    Guid? RuleId,
    string RuleName,
    string Type,
    string Detail,
    bool EmailSent,
    string Error);
