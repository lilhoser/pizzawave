namespace pizzad;

public sealed record WorkspaceDto
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Status { get; init; } = "ready";
    public string SourceLabel { get; init; } = string.Empty;
    public string SourceIdentity { get; init; } = string.Empty;
    public string RootPath { get; init; } = string.Empty;
    public string ManifestJson { get; init; } = "{}";
    public long SourceBytes { get; init; }
    public long ExtractedBytes { get; init; }
    public long DerivedBytes { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed record WorkspaceProcessingRunDto
{
    public long Id { get; init; }
    public string WorkspaceId { get; init; } = string.Empty;
    public long? JobId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Status { get; init; } = "queued";
    public string ProfileJson { get; init; } = "{}";
    public string RequestedStagesJson { get; init; } = "[]";
    public string EstimateJson { get; init; } = "{}";
    public string ActualSummaryJson { get; init; } = "{}";
    public DateTime QueuedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed record ProcessingStageAttemptDto
{
    public long Id { get; init; }
    public string Scope { get; init; } = "workspace";
    public string WorkspaceId { get; init; } = string.Empty;
    public long? RunId { get; init; }
    public long? CallId { get; init; }
    public long? JobId { get; init; }
    public string Stage { get; init; } = string.Empty;
    public int Attempt { get; init; } = 1;
    public string Status { get; init; } = "queued";
    public DateTime QueuedAtUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public long QueueDurationMs { get; init; }
    public long ActiveDurationMs { get; init; }
    public long PausedDurationMs { get; init; }
    public long WallDurationMs { get; init; }
    public long EndpointDurationMs { get; init; }
    public long ItemCount { get; init; }
    public double AudioSeconds { get; init; }
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    public int RetryCount { get; init; }
    public int FailureCount { get; init; }
    public string PricingVersion { get; init; } = string.Empty;
    public decimal EstimatedCost { get; init; }
    public decimal ActualCost { get; init; }
    public string Message { get; init; } = string.Empty;
    public string DetailsJson { get; init; } = "{}";
    public DateTime? ActiveStartedAtUtc { get; init; }
    public DateTime? PauseStartedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
}

public sealed record ProcessingStageMetricsDelta(
    long EndpointDurationMs = 0,
    long ItemCount = 0,
    double AudioSeconds = 0,
    long PromptTokens = 0,
    long CompletionTokens = 0,
    int RetryCount = 0,
    int FailureCount = 0,
    decimal ActualCost = 0);
