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
    public IReadOnlyList<LocationHeatDto> LocationHeat { get; init; } = [];
    public IReadOnlyList<QualityHourDto> QualityByHour { get; init; } = [];
    public IReadOnlyList<BarStatDto> ProblemTalkgroups { get; init; } = [];
    public IReadOnlyList<BarStatDto> InaudibleBySystem { get; init; } = [];
    public IReadOnlyList<BarStatDto> CategoryShare { get; init; } = [];
    public IReadOnlyList<TopTalkgroupDto> TopTalkgroups { get; init; } = [];
    public IReadOnlyList<AlertMatchDto> Alerts { get; init; } = [];
    public IReadOnlyList<IncidentDto> Incidents { get; init; } = [];
    public TokenUsageSummaryDto TokenUsage { get; init; } = new();
}

public sealed record KpiDto(string Label, string Value, string Subtext);

public sealed record HourCategoryDto(int Hour, string Category, int Count);

public sealed record LocationHeatDto(
    string AreaId,
    string AreaLabel,
    string SystemShortName,
    string LocationText,
    string GeocodeQuery,
    string GeocodeDisplayName,
    string GeocodeProvider,
    string GeocodePrecision,
    double GeocodeConfidence,
    double Latitude,
    double Longitude,
    int Count,
    double Intensity,
    long LastHeard,
    string Category,
    IReadOnlyList<long> CallIds,
    IReadOnlyList<string> IncidentTitles,
    IReadOnlyList<LocationHeatIncidentDto> IncidentLinks,
    IReadOnlyList<LocationHeatCallDto> SourceCalls);

public sealed record LocationHeatIncidentDto(long IncidentId, string Title);

public sealed record LocationHeatCallDto(
    long CallId,
    long RawTimestamp,
    string Category,
    string TalkgroupName,
    string Transcript,
    string AudioUrl);

public sealed record GeocodeCacheDto
{
    public string CacheKey { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Query { get; init; } = string.Empty;
    public string AreaId { get; init; } = string.Empty;
    public string LocationText { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Precision { get; init; } = "unknown";
    public double Confidence { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

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
    public string Category { get; init; } = "other";
    public long FirstSeen { get; init; }
    public long LastSeen { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<IncidentCallDto> Calls { get; init; } = [];
}

public sealed record IncidentCallDto(
    long CallId,
    long RawTimestamp,
    string Transcript,
    string AudioUrl,
    string Category = "other",
    string TalkgroupName = "",
    string SystemShortName = "");

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

public sealed record JobLogDto(
    long Id,
    long JobId,
    DateTime TimestampUtc,
    string Stream,
    string Text);

public sealed record SetupJobRequest(string Action, bool Confirmed = false, JsonElement? Parameters = null);

public sealed record SetupArtifactDto(string Path, bool Exists, string Notes);

public sealed record SetupArtifactReportDto(
    bool HasBlockingArtifacts,
    IReadOnlyList<SetupArtifactDto> Artifacts,
    IReadOnlyList<string> ManualCommands);

public sealed record SetupSdrDeviceDto(int Index, string Serial, string Label, string UsbLine, string Warning);

public sealed record SetupSdrDetectionDto(
    IReadOnlyList<SetupSdrDeviceDto> Devices,
    string RawOutput,
    string Message);

public sealed record SetupTalkgroupParseRequest(string? CsvText = null, string? RadioReferenceSid = null, string? RadioReferenceUrl = null, bool IncludeNormallyExcluded = false);

public sealed record SetupTalkgroupSaveRequest(IReadOnlyList<SetupTalkgroupRowDto> Rows);

public sealed record SetupTalkgroupRowDto
{
    public long Id { get; init; }
    public string Mode { get; init; } = string.Empty;
    public string AlphaTag { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string OpsCategory { get; init; } = "other";
    public bool Included { get; init; } = true;
    public string ExclusionReason { get; init; } = string.Empty;
}

public sealed record SetupTalkgroupPreviewDto(
    IReadOnlyList<SetupTalkgroupRowDto> Rows,
    IReadOnlyDictionary<string, int> IncludedByCategory,
    int IncludedCount,
    int ExcludedCount,
    string Diagnostics);

public sealed record SetupTrConfigDraftRequest(
    string? RadioReferenceSid = null,
    string? RadioReferenceUrl = null,
    string? HtmlText = null,
    string? SiteNames = null,
    string? SdrSerials = null,
    int SampleRate = 2400000);

public sealed record SetupTrConfigSaveRequest(string ConfigJson);

public sealed record SetupTrConfigPatchRequest(bool RestartTr = false);

public sealed record SetupTrConfigSourceDto(
    string Label,
    string Serial,
    long CenterFrequency,
    int SampleRate,
    IReadOnlyList<double> CoveredFrequenciesMhz,
    IReadOnlyList<double> OmittedFrequenciesMhz);

public sealed record SetupTrConfigSystemDto(
    string SystemName,
    string ShortName,
    string SiteName,
    IReadOnlyList<double> FrequenciesMhz,
    IReadOnlyList<double> ControlChannelsMhz,
    long CenterFrequency,
    string AssignedSerial,
    string Warning);

public sealed record SetupTrConfigDraftDto(
    string ConfigJson,
    IReadOnlyList<SetupTrConfigSystemDto> Systems,
    IReadOnlyList<SetupTrConfigSourceDto> Sources,
    IReadOnlyList<string> Warnings,
    string Diagnostics);

public sealed record HealthDto(
    string Status,
    string Version,
    string StackName,
    string DatabasePath,
    string AudioRoot,
    int QueueDepth,
    int LiveQueueDepth,
    int PriorityLiveQueueDepth,
    int BacklogQueueDepth,
    bool QueueUnderPressure,
    int QueuePressureThreshold,
    long PendingTranscriptions,
    string? WorkBlockedReason,
    DateTime ServerTimeUtc);

public sealed record StatusSummaryDto(int Calls, int Incidents, int Alerts, long Tokens);

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
    public int RecorderExhausted { get; init; }
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
    IReadOnlyList<TrHealthSeriesDto> Series,
    string BaselineNote);

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

public sealed record TokenUsageSummaryDto(
    int Requests = 0,
    int Successes = 0,
    int Failures = 0,
    long PromptTokens = 0,
    long CompletionTokens = 0,
    long TotalTokens = 0,
    double EstimatedStandardCost = 0);

public sealed record TokenUsageBucketDto(string Label, long TotalTokens, long PromptTokens, long CompletionTokens, int Requests);

public sealed record TokenUsageEntryDto(
    long Id,
    DateTime TimestampUtc,
    string TriggerActivity,
    string RequestKind,
    bool Success,
    string Error,
    string Endpoint,
    string RequestModel,
    string ResponseModel,
    string FinishReason,
    int InputChars,
    int PayloadChars,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

public sealed record TokenUsageReportDto(
    string Ledger,
    TokenUsageSummaryDto Summary,
    IReadOnlyList<TokenUsageBucketDto> ByDay,
    IReadOnlyList<TokenUsageBucketDto> ByTrigger,
    IReadOnlyList<TokenUsageEntryDto> Entries);

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

public sealed record LocalImportEstimateRequest(DateTime StartLocal, DateTime EndLocal);

public sealed record LocalImportEstimateResponse(int CandidateCount, long CandidateBytes, bool ExceedsQuickImportWindow, string Message);

public sealed record LocalArchiveAvailabilityResponse(
    bool Available,
    string CaptureDir,
    DateTime? EarliestLocal,
    DateTime? LatestLocal,
    int FileCount,
    long TotalBytes,
    int ScannedDirectories,
    int SkippedDirectories,
    string Message);

public sealed record LocalImportRequest(DateTime StartLocal, DateTime EndLocal, bool ConfirmLargeImport, int? CallCap, long? ByteCap);

public sealed record SettingsSectionDto(string Section, object Values);

public sealed record SetupStatusDto(
    bool Completed,
    string CurrentStep,
    IReadOnlyList<SetupCheckDto> Checks,
    object Detection,
    object Values);

public sealed record SetupCheckDto(string Id, string Label, bool Required, bool Ok, string Message);

public sealed record SetupSaveRequest(JsonElement Values);

public sealed record SetupValidationResult(bool Ok, string Message, object? Detail = null);

public sealed record ProfileStateDto(Guid ActiveProfileId, IReadOnlyList<ProcessingProfile> Profiles);

public sealed record SaveProfilesRequest(Guid ActiveProfileId, IReadOnlyList<ProcessingProfile> Profiles);

public sealed record TalkgroupOptionDto(long Talkgroup, string Label, string Category);

public sealed record SseEvent([property: JsonPropertyName("type")] string Type, object Payload, long Id);

public sealed record JobControlRequest(string Action);

public sealed record GenerateSummaryRequest(long Start, long End, bool ConfirmLargeRange);

public sealed record TroubleshootInsightRequest(long Start, long End, bool BySystem, string Baseline);

public sealed record TroubleshootInsightResponse(string Text);

public sealed record RetryTranscriptionErrorsRequest(int Limit = 100);

public sealed record SaveSettingsRequest(JsonElement Values);

public sealed record DiagnosticToolRequest(
    IReadOnlyList<long>? CallIds,
    long? Start,
    long? End,
    int? SampleCount,
    IReadOnlyList<string>? Models,
    IReadOnlyList<DiagnosticCustomModelRequest>? CustomModels);

public sealed record DiagnosticCustomModelRequest(
    string Engine,
    string Label,
    string BaseUrl,
    string Model,
    string ApiKey);

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
    public BrandingConfig? Branding { get; set; }
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
