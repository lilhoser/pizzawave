using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class WorkspaceProcessingTelemetryTests
{
    [Fact]
    public async Task StageAttempt_PersistsQueueActivePausedWallAndEndpointDurationsSeparately()
    {
        using var temp = new TempStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var queued = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        await database.AddWorkspaceAsync(new WorkspaceDto
        {
            Id = "support-raymond",
            Name = "Raymond support",
            Kind = "support_package",
            RootPath = Path.Combine(temp.Root, "workspace"),
            CreatedAtUtc = queued
        }, CancellationToken.None);
        var runId = await database.AddWorkspaceProcessingRunAsync(new WorkspaceProcessingRunDto
        {
            WorkspaceId = "support-raymond",
            Name = "Transcription comparison",
            ProfileJson = "{\"model\":\"tiny\"}",
            RequestedStagesJson = "[\"transcription\"]",
            QueuedAtUtc = queued
        }, CancellationToken.None);
        var attemptId = await database.AddProcessingStageAttemptAsync(new ProcessingStageAttemptDto
        {
            Scope = "workspace",
            WorkspaceId = "support-raymond",
            RunId = runId,
            Stage = "transcription",
            QueuedAtUtc = queued,
            PricingVersion = "test-v1",
            EstimatedCost = 1.25m
        }, CancellationToken.None);

        await database.TransitionProcessingStageAttemptAsync(attemptId, "running", queued.AddSeconds(10), null, null, null, CancellationToken.None);
        await database.TransitionProcessingStageAttemptAsync(attemptId, "paused", queued.AddSeconds(30),
            new ProcessingStageMetricsDelta(EndpointDurationMs: 3_000, ItemCount: 2, AudioSeconds: 18.5),
            "Yielding to live work.", null, CancellationToken.None);
        await database.TransitionProcessingStageAttemptAsync(attemptId, "running", queued.AddSeconds(50), null, null, null, CancellationToken.None);
        var completed = await database.TransitionProcessingStageAttemptAsync(attemptId, "completed", queued.AddSeconds(90),
            new ProcessingStageMetricsDelta(EndpointDurationMs: 4_000, ItemCount: 3, AudioSeconds: 21.5, PromptTokens: 100, CompletionTokens: 25, ActualCost: .75m),
            "Completed.", "{\"result\":\"ok\"}", CancellationToken.None);

        Assert.Equal(10_000, completed.QueueDurationMs);
        Assert.Equal(60_000, completed.ActiveDurationMs);
        Assert.Equal(20_000, completed.PausedDurationMs);
        Assert.Equal(90_000, completed.WallDurationMs);
        Assert.Equal(7_000, completed.EndpointDurationMs);
        Assert.Equal(5, completed.ItemCount);
        Assert.Equal(40, completed.AudioSeconds);
        Assert.Equal(100, completed.PromptTokens);
        Assert.Equal(25, completed.CompletionTokens);
        Assert.Equal(.75m, completed.ActualCost);
        Assert.Equal("Completed.", completed.Message);
        Assert.Equal("{\"result\":\"ok\"}", completed.DetailsJson);
        Assert.Null(completed.ActiveStartedAtUtc);
        Assert.Null(completed.PauseStartedAtUtc);
    }

    [Fact]
    public async Task StageAttempt_RejectsInvalidTerminalRestart()
    {
        using var temp = new TempStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var queued = DateTime.UtcNow;
        var attemptId = await database.AddProcessingStageAttemptAsync(new ProcessingStageAttemptDto
        {
            Scope = "live",
            Stage = "transcription",
            QueuedAtUtc = queued
        }, CancellationToken.None);
        await database.TransitionProcessingStageAttemptAsync(attemptId, "cancelled", queued.AddSeconds(1), null, null, null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            database.TransitionProcessingStageAttemptAsync(attemptId, "running", queued.AddSeconds(2), null, null, null, CancellationToken.None));
    }

    private sealed class TempStore : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pizzawave-workspace-test-" + Guid.NewGuid().ToString("N"));

        public EngineDatabase CreateDatabase()
        {
            Directory.CreateDirectory(Root);
            return new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(Root, "pizzad.db"),
                    AudioRoot = Path.Combine(Root, "audio")
                }
            }, NullLogger<EngineDatabase>.Instance);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch { }
        }
    }
}
