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

public sealed record TranscriptionResult(string Text, TranscriptionMetadata Metadata)
{
    public static TranscriptionResult Empty(TranscriptionMetadata metadata) => new(string.Empty, metadata);
}

public sealed record TranscriptionMetadata(IReadOnlyDictionary<string, object?> Values);

public sealed record AlertMatchDto
{
    public long Id { get; init; }
    public long CallId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public long MatchedAt { get; init; }
    public bool IsImported { get; init; }
    public bool NotificationSuppressed { get; init; }
    public string DismissedAtUtc { get; init; } = string.Empty;
    public bool Active => string.IsNullOrWhiteSpace(DismissedAtUtc);
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

public sealed record CallLocationDashboardRow(
    long CallId,
    long StartTime,
    string SystemShortName,
    long Talkgroup,
    string TalkgroupName,
    string Category,
    string Transcription,
    string AreaId,
    string AreaLabel,
    string AreaSystemShortName,
    string LocationText,
    string NormalizedKey,
    string Source,
    string GeocodeQuery,
    string GeocodeDisplayName,
    string GeocodeProvider,
    string GeocodePrecision,
    double GeocodeConfidence,
    double Latitude,
    double Longitude,
    string AudioPath = "");

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
    string TalkgroupKey,
    string SystemShortName,
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

public sealed record CategoryGroupDto(
    string Label,
    IReadOnlyList<EngineCall> Calls,
    string TalkgroupKey = "",
    string SystemShortName = "",
    long Talkgroup = 0,
    int Count = 0,
    long LastHeard = 0,
    int StrongCount = 0,
    int WeakCount = 0);

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
    public string IncidentKey { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string Category { get; init; } = "other";
    public string Status { get; init; } = "active";
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
    string SystemShortName = "",
    long Talkgroup = 0,
    bool HasAlertMatch = false,
    bool HasActiveAlert = false,
    string AlertRules = "");

public static class CallAudioLinks
{
    public static string ForCall(long callId, string audioPath) =>
        string.IsNullOrWhiteSpace(audioPath) ? string.Empty : $"/api/v1/calls/{callId}/audio";
}

public sealed record IncidentCallOwnerDto(
    long CallId,
    long IncidentId,
    string IncidentKey,
    string Title,
    string Status);

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

public sealed record SetupSdrDeviceDto(
    int Index,
    string Type,
    string Serial,
    string Label,
    string Driver,
    string DeviceArgs,
    string UsbLine,
    IReadOnlyList<int> SampleRateOptions,
    int DefaultSampleRate,
    string GainMode,
    string DefaultGain,
    string Warning);

public sealed record SetupSdrDetectionDto(
    IReadOnlyList<SetupSdrDeviceDto> Devices,
    string RawOutput,
    string Message);

public sealed record SetupTalkgroupParseRequest(string? CsvText = null, string? RadioReferenceSid = null, string? RadioReferenceUrl = null, bool IncludeNormallyExcluded = false, string? SystemShortName = null);

public sealed record SetupTalkgroupSaveRequest(IReadOnlyList<SetupTalkgroupRowDto> Rows);

public sealed record SetupTalkgroupRowDto
{
    public string Key { get; init; } = string.Empty;
    public string SystemShortName { get; init; } = string.Empty;
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
    int SampleRate = 2400000,
    IReadOnlyList<SetupSdrDeviceDto>? SdrDevices = null,
    IReadOnlyList<string>? SiteNameList = null);

public sealed record SetupTrConfigSitesRequest(string? RadioReferenceSid = null, string? HtmlText = null);

public sealed record SetupTrConfigSiteDto(
    string Name,
    string ShortName,
    int FrequencyCount,
    int ControlChannelCount,
    IReadOnlyList<double> ControlChannelsMhz);

public sealed record SetupTrConfigSitesDto(
    string SystemName,
    IReadOnlyList<SetupTrConfigSiteDto> Sites,
    string Diagnostics);

public sealed record SetupTrConfigSourcePlanRequest(
    string? RadioReferenceSid = null,
    string? HtmlText = null,
    string? SiteNames = null,
    string? SdrSerials = null,
    int SampleRate = 2400000,
    IReadOnlyList<SetupSdrDeviceDto>? SdrDevices = null,
    IReadOnlyList<string>? SiteNameList = null);

public sealed record SetupTrConfigSaveRequest(string ConfigJson);

public sealed record SetupTrConfigPatchRequest(bool RestartTr = false, bool DisableCaptureDir = false);

public sealed record SetupTrConfigSourceDto(
    string Label,
    string Serial,
    string Type,
    string Driver,
    string DeviceArgs,
    long CenterFrequency,
    int SampleRate,
    string Gain,
    string GainMode,
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

public sealed record SetupTrConfigSourcePlanDto(
    string SystemName,
    IReadOnlyList<SetupTrConfigSystemDto> Systems,
    IReadOnlyList<SetupTrConfigSourceDto> Sources,
    int RequiredSourceCount,
    int AvailableSourceCount,
    IReadOnlyList<string> Warnings,
    string Diagnostics);

public sealed record SetupAreaBoundaryRequest(string Query);

public sealed record SetupAreaBoundaryCandidateDto(
    string Label,
    string Kind,
    string Source,
    string GeoId,
    double North,
    double South,
    double East,
    double West);

public sealed record SetupAreaBoundaryResponseDto(
    string Query,
    IReadOnlyList<SetupAreaBoundaryCandidateDto> Candidates,
    string Diagnostics);

public sealed record TrConfigEditorSaveRequest(string ConfigJson);
public sealed record TrConfigBackupDto(string Name, string Path, long Bytes, DateTime CreatedAtUtc);
public sealed record TrConfigRestoreRequest(string BackupPath, bool RestartTr = true);
public sealed record TrConfigRestoreResultDto(bool Ok, string Message, string BackupPath, string RestoreBackupPath, string ServiceOutput);

public sealed record TrConfigEditorSourceDto(
    int Index,
    string Device,
    string Serial,
    long CenterFrequency,
    int SampleRate,
    int Error,
    string Gain);

public sealed record TrConfigEditorSystemDto(
    string ShortName,
    string Type,
    string Modulation,
    IReadOnlyList<long> ControlChannelsHz,
    IReadOnlyList<long> VoiceFrequenciesHz,
    string TalkgroupsFile);

public sealed record TrConfigEditorSummaryDto(
    IReadOnlyList<TrConfigEditorSystemDto> Systems,
    IReadOnlyList<TrConfigEditorSourceDto> Sources,
    IReadOnlyList<string> Warnings);

public sealed record TrConfigEditorDto(
    string LivePath,
    string DraftPath,
    string ConfigJson,
    string LiveConfigJson,
    bool HasDraft,
    bool ParseOk,
    string ParseMessage,
    TrConfigEditorSummaryDto Summary);

public sealed record RfSweepInsightRequest(
    string SurveyId,
    string SystemShortName,
    int SourceIndex,
    JsonElement SweepResult,
    JsonElement? SelectedCandidate);

public sealed record RfSweepInsightResponse(
    string Recommendation,
    string Confidence,
    string Rationale,
    IReadOnlyList<string> NextActions,
    string RawText);

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
    int LiveTranscriptionWorkers,
    int WhisperThreadsPerWorker,
    int ThroughputWindowMinutes,
    int DeferredLiveQueueDepth,
    long RecentCallsIngested,
    long RecentCallsTranscribed,
    double RecentIngestPerMinute,
    double RecentTranscribedPerMinute,
    long RecentAudioSecondsIngested,
    long RecentAudioSecondsTranscribed,
    double RecentAudioSecondsIngestedPerMinute,
    double RecentAudioSecondsTranscribedPerMinute,
    long PendingAudioSeconds,
    int RecentTranscriptionSamples,
    double AverageTranscriptionSeconds,
    double AverageAudioSeconds,
    double AverageTranscriptionRealtimeFactor,
    IngestControlStatusDto Ingest,
    LiveTrActivityStatusDto LiveTrActivity,
    string? AiWorkBlockedReason,
    AiCompletionHealthDto AiCompletionHealth,
    EmbeddingPipelineHealthDto EmbeddingHealth,
    string? WorkBlockedReason,
    DateTime ServerTimeUtc);

public sealed record LiveTrActivityStatusDto(
    string Status,
    bool Stale,
    int ThresholdSeconds,
    double AgeSeconds,
    DateTime? LastActivityUtc,
    DateTime? LastLiveCallUtc,
    DateTime? LastTrHealthUtc,
    DateTime StartedAtUtc,
    string Message);

public sealed record TrServiceFaultSnapshotDto(
    DateTime CreatedAtUtc,
    string Unit,
    string ServiceResult,
    string ExitCode,
    string ExitStatus,
    IReadOnlyDictionary<string, string> Systemd,
    IReadOnlyList<string> JournalTail,
    IReadOnlyList<string> Signatures);

public sealed record TrServiceControlStateDto(
    DateTime CreatedAtUtc,
    string Unit,
    string State,
    string Reason);

public sealed record SystemCpuSnapshotDto(
    DateTime GeneratedAtUtc,
    int WindowMinutes,
    int ProcessorCount,
    SystemCpuSampleDto? Latest,
    SystemCpuSampleDto Peaks,
    string Severity,
    string Summary,
    IReadOnlyList<SystemCpuInsightDto> Insights);

public sealed record SystemCpuSampleDto(
    DateTime? WindowEndUtc,
    double TrCpuPercent,
    double TrCpuHostPercent,
    double TrRssMb,
    int TrThreadCount,
    double HostTempC,
    double HostLoad1,
    double HostLoadHostPercent,
    string HostThrottledFlags);

public sealed record SystemCpuInsightDto(
    string Label,
    string Value,
    string Status,
    string Detail);

public sealed record IngestControlStatusDto(
    bool Paused,
    bool UntilQueueClear,
    string Reason,
    DateTime? PausedAtUtc,
    long DroppedCalls,
    long DroppedCallsThisPause);

public sealed record IngestControlRequest(bool Pause, bool UntilQueueClear = false, string? Reason = null);

public sealed record QueueSnapshotDto(
    int QueueDepth,
    int LiveQueueDepth,
    int PriorityLiveQueueDepth,
    int BacklogQueueDepth,
    bool QueueUnderPressure,
    int QueuePressureThreshold,
    long PendingTranscriptions,
    int LiveTranscriptionWorkers,
    int WhisperThreadsPerWorker,
    int ThroughputWindowMinutes,
    int DeferredLiveQueueDepth,
    long RecentCallsIngested,
    long RecentCallsTranscribed,
    double RecentIngestPerMinute,
    double RecentTranscribedPerMinute,
    long RecentAudioSecondsIngested,
    long RecentAudioSecondsTranscribed,
    double RecentAudioSecondsIngestedPerMinute,
    double RecentAudioSecondsTranscribedPerMinute,
    long PendingAudioSeconds,
    int RecentTranscriptionSamples,
    double AverageTranscriptionSeconds,
    double AverageAudioSeconds,
    double AverageTranscriptionRealtimeFactor,
    IngestControlStatusDto Ingest,
    string? AiWorkBlockedReason,
    AiCompletionHealthDto AiCompletionHealth,
    EmbeddingPipelineHealthDto EmbeddingHealth,
    IReadOnlyList<QueuePendingCallDto> PendingCalls,
    IReadOnlyList<QueueTalkgroupLoadDto> TopAudioTalkgroups);

public sealed record AiCompletionHealthDto(
    string Status = "unknown",
    string Message = "No recent AI completion requests recorded.",
    int WindowMinutes = 30,
    int Requests = 0,
    int Failures = 0,
    int TimeoutFailures = 0,
    int NoValidResultFailures = 0,
    int ConsecutiveFailures = 0,
    DateTime? LatestFailureUtc = null,
    string LatestFailureKind = "",
    string LatestFailure = "");

public sealed record QueuePendingCallDto(
    long CallId,
    long StartTime,
    string SystemShortName,
    long Talkgroup,
    string TalkgroupName,
    string Category,
    bool IsImported,
    string AudioPath);

public sealed record QueueTalkgroupLoadDto(
    string SystemShortName,
    long Talkgroup,
    string TalkgroupName,
    string Category,
    long Calls,
    long AudioSeconds,
    double AverageAudioSeconds,
    long PendingCalls,
    long PendingAudioSeconds,
    long WeakCalls = 0,
    long FailedCalls = 0,
    long RepetitiveCalls = 0,
    long IncidentCalls = 0);

public sealed record StatusSummaryDto(int Calls, int Incidents, int HiddenIncidents, int Alerts, long Tokens);

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
    public int CcSummaryDecodeLines { get; init; }
    public int CcSummaryDecodeZero { get; init; }
    public double CcSummaryDecodeRateTotal { get; init; }
    public double CcSummaryAvgDecodeRate => CcSummaryDecodeLines == 0 ? 0 : CcSummaryDecodeRateTotal / CcSummaryDecodeLines;
    public double CcSummaryDecodeZeroPct => CcSummaryDecodeLines == 0 ? 0 : CcSummaryDecodeZero * 100.0 / CcSummaryDecodeLines;
    public int LowDecodeWarningLines { get; init; }
    public int LowDecodeWarningZero { get; init; }
    public double LowDecodeWarningRateTotal { get; init; }
    public double LowDecodeWarningAvgRate => LowDecodeWarningLines == 0 ? 0 : LowDecodeWarningRateTotal / LowDecodeWarningLines;
    public double LowDecodeWarningZeroPct => LowDecodeWarningLines == 0 ? 0 : LowDecodeWarningZero * 100.0 / LowDecodeWarningLines;
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
    public double TrCpuPercent { get; init; }
    public double TrRssMb { get; init; }
    public double TrVszMb { get; init; }
    public int TrThreadCount { get; init; }
    public double HostTempC { get; init; }
    public string HostThrottledFlags { get; init; } = string.Empty;
    public double HostLoad1 { get; init; }
    public double HostLoad5 { get; init; }
    public double HostLoad15 { get; init; }
}

public sealed record TrHealthMetricDto(string Metric, string Value, string Notes, bool IsIssue);

public sealed record TrSourceCoverageDto(
    int Index,
    string Device,
    double CenterMhz,
    double LowMhz,
    double HighMhz,
    int DigitalRecorders,
    int FirstMatchCalls,
    int CoverableCalls,
    int UniqueFrequencies,
    string Notes,
    bool IsIssue);

public sealed record TrSourcePlanDto(
    string SystemShortName,
    double LowMhz,
    double HighMhz,
    double RecommendedCenterMhz,
    int? AssignedSourceIndex,
    string AssignedDevice,
    string Notes,
    bool IsIssue);

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
    public IReadOnlyList<TrSourceCoverageDto> SourceCoverage { get; init; } = [];
    public IReadOnlyList<TrSourcePlanDto> SourcePlan { get; init; } = [];
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

public sealed record TrRfAnalysisDto(
    string System,
    long Start,
    long End,
    string Window,
    bool HasEnoughPostChangeData,
    string Summary,
    IReadOnlyList<TrHealthMetricDto> Metrics,
    IReadOnlyList<TrHealthMetricDto> Comparison,
    IReadOnlyList<TrHealthMetricDto> RetuneTargets,
    IReadOnlyList<TrHealthMetricDto> Recommendations);

public sealed record TokenUsageSummaryDto(
    int Requests = 0,
    int Successes = 0,
    int Failures = 0,
    int Truncated = 0,
    int Canceled = 0,
    int HttpOrOtherErrors = 0,
    long PromptTokens = 0,
    long CompletionTokens = 0,
    long TotalTokens = 0,
    double EstimatedStandardCost = 0,
    int TimeoutFailures = 0,
    int NoValidResultFailures = 0);

public sealed record TokenUsageBucketDto(string Label, long TotalTokens, long PromptTokens, long CompletionTokens, int Requests);

public sealed record TokenUsageFailureBreakdownDto(
    string Kind,
    int Requests,
    long PromptTokens,
    long CompletionTokens,
    long TotalTokens,
    DateTime LatestUtc,
    string Example);

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

public sealed record EvidenceVerifierRunDto(
    long Id,
    DateTime TimestampUtc,
    string SystemShortName,
    string IncidentKey,
    string Title,
    int SelectedCalls,
    int ReviewedCalls,
    int ModelReviewedCalls,
    int TruncatedCalls,
    int AddedCalls,
    int DroppedCalls,
    int RetainedCalls,
    bool Success,
    string Error);

public sealed record IncidentOperationAuditDto(
    long Id,
    DateTime TimestampUtc,
    string SystemShortName,
    string IncidentKey,
    string Operation,
    bool Accepted,
    string Reason,
    double Score,
    string CallIdsJson,
    string MetadataJson);

public sealed record IncidentOperationAuditRowDto(
    long Id,
    DateTime TimestampUtc,
    string SystemShortName,
    string IncidentKey,
    string Operation,
    bool Accepted,
    string Reason,
    double Score,
    IReadOnlyList<long> CallIds,
    string MetadataJson);

public sealed record EmbeddingJobDto(
    long CallId,
    string Status,
    int Attempts,
    string Error,
    DateTime UpdatedAtUtc);

public sealed record EmbeddingPipelineHealthDto(
    bool Enabled,
    bool QdrantOk,
    bool EmbeddingEndpointOk,
    string Status,
    string Collection,
    string Model,
    int VectorSize,
    int QueueDepth,
    long EmbeddedCalls,
    long FailedCalls,
    long PendingCalls,
    DateTime? OldestPendingUtc,
    double LastSearchMs,
    double LastUpsertMs,
    string LastError);

public sealed record VectorSearchMatchDto(
    long CallId,
    double Score,
    string Reason);

public sealed record TokenUsageReportDto(
    string Ledger,
    TokenUsageSummaryDto Summary,
    TokenUsageSummaryDto MonthlySummary,
    TokenUsageSummaryDto AllTimeSummary,
    IReadOnlyList<TokenUsageFailureBreakdownDto> FailuresByKind,
    IReadOnlyList<TokenUsageBucketDto> ByDay,
    IReadOnlyList<TokenUsageBucketDto> ByTrigger,
    IReadOnlyList<TokenUsageEntryDto> Entries);

public sealed record RemoteBandwidthSummaryDto(
    long RequestBytes = 0,
    long ResponseBytes = 0,
    long TotalBytes = 0,
    int Requests = 0,
    int MissingAudioFiles = 0);

public sealed record RemoteBandwidthBucketDto(
    string Label,
    string Activity,
    long RequestBytes,
    long ResponseBytes,
    long TotalBytes,
    int Requests);

public sealed record RemoteBandwidthEntryDto(
    DateTime TimestampUtc,
    string Activity,
    string Endpoint,
    long RequestBytes,
    long ResponseBytes,
    long TotalBytes,
    string Basis,
    bool Estimated);

public sealed record RemoteBandwidthReportDto(
    string Ledger,
    string RemoteHost,
    string TranscriptionEndpoint,
    string AiEndpoint,
    bool TranscriptionIncluded,
    string Notes,
    RemoteBandwidthSummaryDto Summary,
    RemoteBandwidthSummaryDto MonthlySummary,
    RemoteBandwidthSummaryDto AllTimeSummary,
    IReadOnlyList<RemoteBandwidthBucketDto> ByDay,
    IReadOnlyList<RemoteBandwidthBucketDto> ByActivity,
    IReadOnlyList<RemoteBandwidthEntryDto> Entries);

public sealed record RemoteBandwidthUsageSnapshotDto(
    string RemoteHost,
    string TranscriptionEndpoint,
    string AiEndpoint,
    bool TranscriptionIncluded,
    string Notes,
    RemoteBandwidthSummaryDto Summary,
    IReadOnlyList<RemoteBandwidthBucketDto> ByActivity);

public sealed record QualityCheckSnapshotDto(
    DateTime GeneratedAtUtc,
    long Start,
    long End,
    QualityCheckCallSummaryDto Calls,
    IReadOnlyList<QualityCheckCategorySummaryDto> CallsByCategory,
    IReadOnlyList<QualityCheckTranscriptSummaryDto> TranscriptQuality,
    QualityCheckAiSummaryDto Ai,
    QualityCheckEvidenceVerifierSummaryDto EvidenceVerifier,
    QualityCheckIncidentSummaryDto Incidents,
    IReadOnlyList<QualityCheckOperationSummaryDto> IncidentOperations);

public sealed record QualityCheckCallSummaryDto(
    long TotalCalls,
    long AudioSeconds,
    long ShortTranscriptCalls,
    long OldestStartTime,
    long NewestStartTime);

public sealed record QualityCheckCategorySummaryDto(string Category, long Calls, long AudioSeconds);

public sealed record QualityCheckTranscriptSummaryDto(string Status, string QualityReason, long Calls);

public sealed record QualityCheckAiSummaryDto(
    long Requests,
    long Successes,
    long Failures,
    long Truncated,
    long PromptTokens,
    long CompletionTokens,
    DateTime? LatestUtc);

public sealed record QualityCheckEvidenceVerifierSummaryDto(
    long Runs,
    double AverageReviewedCalls,
    double AverageTruncatedCalls,
    long MaxTruncatedCalls,
    long AddedCalls,
    long DroppedCalls,
    long RetentionMismatches = 0);

public sealed record QualityCheckIncidentSummaryDto(
    long Incidents,
    double AverageScore,
    long Creates,
    long Updates,
    long Rejects,
    IReadOnlyList<QualityCheckRecentIncidentDto> Recent);

public sealed record QualityCheckRecentIncidentDto(
    long Id,
    string IncidentKey,
    string Title,
    string Category,
    double Score,
    long FirstSeen,
    long LastSeen);

public sealed record QualityCheckOperationSummaryDto(
    bool Accepted,
    string Reason,
    long Count,
    double AverageScore,
    DateTime? LatestUtc);

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

public sealed record SiteSetupDto(
    SiteSetupConfig Desired,
    SiteSetupAppliedConfigDto Applied,
    SiteSetupStatusDto Status,
    IReadOnlyList<SiteSetupPendingChangeDto> PendingChanges,
    IReadOnlyList<SiteSetupActivityDto> RecentActivity);

public sealed record SiteSetupStatusDto(
    string MonitoringState,
    string Message,
    bool PendingApply,
    long DesiredVersion,
    string AppliedConfigHash,
    DateTime? LastAppliedAtUtc);

public sealed record SiteSetupAppliedConfigDto(
    string ConfigPath,
    bool ConfigExists,
    string ConfigHash,
    DateTime? ConfigUpdatedAtUtc,
    IReadOnlyList<string> SystemShortNames,
    IReadOnlyList<long> ControlChannelsHz,
    IReadOnlyList<SiteSetupAppliedSourceDto> Sources);

public sealed record SiteSetupAppliedSourceDto(
    int Index,
    string Device,
    string Serial,
    long CenterHz,
    int SampleRate,
    int ErrorHz,
    string Gain);

public sealed record SiteSetupPendingChangeDto(string Category, string Summary);

public sealed record SiteSetupUpdateRequest(SiteSetupConfig Desired, string Source = "ui");

public sealed record SiteSetupActivityRequest(
    string Category,
    string Action,
    string Summary,
    JsonElement? Details = null,
    string Source = "ui");

public sealed record SiteSetupMarkAppliedRequest(
    string Summary = "",
    JsonElement? Details = null,
    string Source = "ui");

public sealed record SiteSetupActivityDto(
    long Id,
    DateTime TimestampUtc,
    string Category,
    string Action,
    string Summary,
    string DetailsJson,
    long DesiredVersion,
    string AppliedConfigHash,
    string MonitoringState,
    string Source);

public sealed record RfSurveyListDto(
    IReadOnlyList<RfSurveySessionDto> Sessions,
    string ArtifactRoot);

public sealed record RfSurveySessionDto
{
    public string Id { get; init; } = string.Empty;
    public string Status { get; init; } = "draft";
    public string Mode { get; init; } = "guided";
    public string SiteLabel { get; init; } = string.Empty;
    public string SystemShortName { get; init; } = string.Empty;
    public string Verdict { get; init; } = "not_started";
    public string Stability { get; init; } = "unknown";
    public string SdrSummary { get; init; } = string.Empty;
    public string RfPathSummary { get; init; } = string.Empty;
    public string BestControlChannel { get; init; } = string.Empty;
    public string SourcePlanSummary { get; init; } = string.Empty;
    public string RecommendationState { get; init; } = "none";
    public string ArtifactPath { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; init; }
}

public sealed record RfSurveyDetailDto(
    RfSurveySessionDto Session,
    RfSurveyProfileDto Profile,
    IReadOnlyList<RfSurveyExperimentDto> Experiments,
    IReadOnlyList<RfSurveyNoteDto> Notes,
    RfSurveyToolPrepDto? ToolPrep,
    IReadOnlyList<RfSurveyExperimentPlanDto> NextExperiments);

public sealed record RfSurveyProfileDto
{
    public string SiteLabel { get; init; } = string.Empty;
    public string RadioReferenceSid { get; init; } = string.Empty;
    public string SystemShortName { get; init; } = string.Empty;
    public IReadOnlyList<string> SystemShortNames { get; init; } = [];
    public IReadOnlyList<string> SourcePlanSystemShortNames { get; init; } = [];
    public string SourcePlanMode { get; init; } = "full";
    public IReadOnlyList<RfSurveySystemDto> Systems { get; init; } = [];
    public string Mode { get; init; } = "guided";
    public string GroundTruthSource { get; init; } = "tr-config";
    public IReadOnlyList<long> ControlChannelsHz { get; init; } = [];
    public IReadOnlyList<long> VoiceFrequenciesHz { get; init; } = [];
    public IReadOnlyList<RfSurveySourceDto> Sources { get; init; } = [];
    public IReadOnlyList<RfSurveySdrDeviceDto> Devices { get; init; } = [];
    public bool SourceOverride { get; init; }
    public IReadOnlyList<int> SelectedSourceIndexes { get; init; } = [];
    public RfSurveyPathProfileDto RfPath { get; init; } = new();
    public int CurrentStep { get; init; }
    public string MeasurementMode { get; init; } = "guided";
    public int ProbeDurationSeconds { get; init; } = 45;
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record RfSurveyPathProfileDto
{
    public string Antenna { get; init; } = string.Empty;
    public string AntennaType { get; init; } = "yagi";
    public string AntennaMount { get; init; } = string.Empty;
    public string AntennaPolarization { get; init; } = string.Empty;
    public string AimedAtSite { get; init; } = string.Empty;
    public string PositionNotes { get; init; } = string.Empty;
    public string ConnectorChain { get; init; } = string.Empty;
    public string Coax { get; init; } = string.Empty;
    public string SplitterOrMulticoupler { get; init; } = string.Empty;
    public string Lna { get; init; } = string.Empty;
    public string Filters { get; init; } = string.Empty;
    public string SdrNotes { get; init; } = string.Empty;
    public string Observations { get; init; } = string.Empty;
    public IReadOnlyList<RfSurveyRfChainItemDto> Chain { get; init; } = [];
}

public sealed record RfSurveySystemDto(
    string ShortName,
    string SiteLabel,
    IReadOnlyList<long> ControlChannelsHz,
    IReadOnlyList<long> VoiceFrequenciesHz,
    string RadioReferenceSid = "",
    string TalkgroupSystemShortName = "");

public sealed record RfSurveyRfChainItemDto(
    string Type = "",
    string Label = "",
    string ConnectorIn = "",
    string ConnectorOut = "",
    string Length = "",
    string Loss = "",
    string Power = "",
    string Notes = "",
    string ConnectorInType = "",
    string ConnectorInGender = "",
    string ConnectorOutType = "",
    string ConnectorOutGender = "",
    string GainDb = "",
    string GroundPlane = "",
    string PortCount = "",
    string PowerPass = "",
    string PowerMethod = "",
    string Passband = "");

public sealed record RfSurveySourceDto(
    int Index,
    string Device,
    string Serial,
    string SdrType,
    long CenterHz,
    int SampleRate,
    int ErrorHz,
    string Gain);

public sealed record RfSurveySdrDeviceDto(
    int Index,
    string Serial,
    string Label,
    string SdrType,
    string UsbLine,
    string Warning,
    IReadOnlyList<int>? SampleRateOptions = null,
    int DefaultSampleRate = 0);

public sealed record RfSurveyExperimentDto
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Status { get; init; } = "planned";
    public string Hypothesis { get; init; } = string.Empty;
    public string RequiredSetup { get; init; } = string.Empty;
    public string ResultSummary { get; init; } = string.Empty;
    public string BlockingIssue { get; init; } = string.Empty;
    public string EvidenceJson { get; init; } = "{}";
    public string InterpretationJson { get; init; } = "{}";
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }
}

public sealed record RfSurveyExperimentPlanDto(
    string Type,
    string Label,
    string Purpose,
    bool Enabled,
    string BlockingIssue,
    string RequiredSetup);

public sealed record RfSurveyRunExperimentRequest(
    string Type,
    int DurationSeconds = 30,
    long? ControlChannelHz = null,
    int? SourceIndex = null,
    JsonElement? Parameters = null);

public sealed record RfSurveyCancelExperimentResultDto(
    bool CancelRequested,
    string Message,
    string CleanupOutput = "");

public sealed record RfSurveySweepProgressDto(
    bool Active,
    string Directory,
    IReadOnlyList<RfSurveySweepProgressRowDto> Rows,
    IReadOnlyList<RfSurveySweepCandidateProgressDto>? Candidates = null);

public sealed record RfSurveySweepProgressRowDto(
    int SourceIndex,
    long ControlChannelHz,
    string Gain,
    string Status,
    string Issue,
    double? SnrDb,
    double? PeakOffsetHz,
    bool Overload);

public sealed record RfSurveySweepCandidateProgressDto(
    string Id,
    int SourceIndex,
    long ControlChannelHz,
    string Gain,
    int ErrorHz,
    string P25Status,
    string P25Summary,
    string MetricsStatus,
    string MetricsSummary,
    string VoiceStatus,
    string VoiceSummary,
    int VoiceTotalCalls,
    int VoiceRealCalls);

public sealed record RfSurveyWaterfallStartRequest(
    int? SourceIndex = null,
    long? FrequencyHz = null,
    int? SampleRateHz = null,
    string? Gain = null,
    int BinCount = 160,
    int CaptureMilliseconds = 250,
    int RefreshMilliseconds = 1200);

public sealed record RfSurveyWaterfallStatusDto(
    bool Active,
    string Status,
    string Message,
    int SourceIndex,
    string SdrType,
    long CenterHz,
    int SampleRate,
    string Gain,
    int BinCount,
    DateTime? StartedAtUtc,
    DateTime? UpdatedAtUtc,
    RfSurveyWaterfallFrameDto? Frame,
    bool TrWasActive,
    string TrStopOutput = "",
    string TrRestartOutput = "",
    string TrRestartError = "",
    IReadOnlyList<RfSurveyWaterfallFrameDto>? Frames = null);

public sealed record RfSurveyWaterfallFrameDto(
    int Sequence,
    DateTime CapturedAtUtc,
    long CenterHz,
    int SampleRate,
    double StartHz,
    double BinWidthHz,
    IReadOnlyList<double> PowersDb,
    double MinDb,
    double MaxDb,
    double NoiseFloorDb,
    double PeakDb,
    double PeakFrequencyHz,
    double ClipPct,
    bool Overload,
    long Bytes,
    string Output);

public sealed record RfSurveyP25ProbePreviewDto(
    bool Configured,
    bool Ready,
    string Command,
    string WorkingDirectory,
    string BlockingIssue,
    IReadOnlyList<string> Placeholders);

public sealed record RfSurveyExportPlanDto(
    string SurveyId,
    string ArtifactPath,
    string PlanPath,
    string MarkdownPath,
    string Verdict,
    string Stability,
    IReadOnlyList<string> Recommendations,
    IReadOnlyList<string> Blockers);

public sealed record RfSurveyExportDocumentDto(
    string FileName,
    string Markdown);

public sealed record RfSurveyTrActionRequest(bool Confirmed = false, bool RestartTr = true);

public sealed record RfSurveyTrActionResultDto(
    bool Ok,
    string Action,
    string Message,
    string CandidatePath,
    string BackupPath,
    string RestorePath,
    string ServiceOutput);

public sealed record RfSurveyCandidateRequest(
    string TrialType = "control_channel",
    long? ControlChannelHz = null,
    int? SourceIndex = null,
    long? CenterHz = null,
    string? Gain = null,
    int? SampleRate = null);

public sealed record RfSurveyCandidateDto(
    string TrialType,
    string CandidatePath,
    string DiffPath,
    string Summary,
    IReadOnlyList<string> DiffLines,
    IReadOnlyList<string> Warnings);

public sealed record RfSurveyRunCaptureTrialRequest(
    bool Confirmed = false,
    bool RestartTr = true,
    bool RestoreAfter = true,
    int DurationSeconds = 300);

public sealed record RfSurveyApplySourceDraftRequest(
    string ConfigJson,
    bool RestartTr = true,
    bool PreserveRfValidationEvidence = true);

public sealed record RfSurveyConfigDraftSummaryDto(
    IReadOnlyList<int> SelectedSourceIndexes,
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Warnings,
    string DraftPath);

public sealed record RfSurveyConfigDraftDto(
    string LivePath,
    string DraftPath,
    string ConfigJson,
    string LiveConfigJson,
    RfSurveyConfigDraftSummaryDto Summary);

public sealed record RfSurveyCaptureTrialResultDto(
    RfSurveyTrActionResultDto Apply,
    RfSurveyExperimentDto VoiceCapture,
    RfSurveyTrActionResultDto? Restore,
    int WaitedSeconds);

public sealed record RfSurveyNoteDto(string Text, DateTime CreatedAtUtc);

public sealed record RfSurveyToolPrepDto(
    DateTime GeneratedAtUtc,
    bool ReadyForGuidedSurvey,
    bool ReadyForControlChannelTests,
    bool ReadyForVoiceCapture,
    bool ReadyForTranscriptionGate,
    IReadOnlyList<RfSurveyToolStatusDto> Tools,
    IReadOnlyList<string> Warnings);

public sealed record RfSurveyToolStatusDto(
    string Id,
    string Label,
    string Category,
    bool Required,
    bool Installed,
    string Version,
    string Command,
    string Purpose,
    string InstallHint);

public sealed record RfSurveyCreateRequest(
    string? SystemShortName = null,
    string? SiteLabel = null,
    string Mode = "guided",
    string GroundTruthSource = "tr-config",
    RfSurveyPathProfileDto? RfPath = null,
    IReadOnlyList<int>? SelectedSourceIndexes = null,
    int CurrentStep = 0,
    string MeasurementMode = "guided",
    int ProbeDurationSeconds = 45,
    IReadOnlyList<string>? SystemShortNames = null,
    IReadOnlyList<string>? SourcePlanSystemShortNames = null,
    string? SourcePlanMode = null,
    IReadOnlyList<RfSurveySystemDto>? SystemDefinitions = null,
    IReadOnlyList<RfSurveySourceDto>? SdrSources = null,
    string? RadioReferenceSid = null);

public sealed record RfSurveyDraftUpdateRequest(
    string? SystemShortName = null,
    string? SiteLabel = null,
    string? Mode = null,
    string? GroundTruthSource = null,
    RfSurveyPathProfileDto? RfPath = null,
    IReadOnlyList<int>? SelectedSourceIndexes = null,
    int? CurrentStep = null,
    string? MeasurementMode = null,
    int? ProbeDurationSeconds = null,
    IReadOnlyList<string>? SystemShortNames = null,
    IReadOnlyList<string>? SourcePlanSystemShortNames = null,
    string? SourcePlanMode = null,
    IReadOnlyList<RfSurveySystemDto>? SystemDefinitions = null,
    IReadOnlyList<RfSurveySourceDto>? SdrSources = null,
    string? RadioReferenceSid = null);

public sealed record RfSurveyNoteRequest(string Text);

public sealed record ProfileStateDto(
    Guid ActiveProfileId,
    IReadOnlyList<ProcessingProfile> Profiles,
    bool RestartRecommended = false,
    string GeneratedCsvPath = "",
    string Message = "");

public sealed record SaveProfilesRequest(Guid ActiveProfileId, IReadOnlyList<ProcessingProfile> Profiles);

public sealed record TalkgroupOptionDto(string Key, string SystemShortName, long Talkgroup, string Label, string Category);

public sealed record SseEvent([property: JsonPropertyName("type")] string Type, object Payload, long Id);

public sealed record JobControlRequest(string Action);

public sealed record TroubleshootInsightRequest(long Start, long End, bool BySystem, string Baseline);

public sealed record TroubleshootInsightResponse(string Text);

public sealed record SaveSettingsRequest(JsonElement Values);

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
