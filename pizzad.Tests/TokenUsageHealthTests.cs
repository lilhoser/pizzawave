using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class TokenUsageHealthTests
{
    [Fact]
    public async Task TokenUsage_ClassifiesCompletionTimeoutsSeparatelyFromGenericErrors()
    {
        using var temp = new TempStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await database.AddLmUsageAsync(Usage(false, "The request was canceled due to the configured HttpClient.Timeout of 600 seconds elapsing."), CancellationToken.None);
        await database.AddLmUsageAsync(Usage(false, "The operation has timed out."), CancellationToken.None);

        var report = await database.GetTokenUsageAsync(
            new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5)).ToUnixTimeSeconds(),
            new DateTimeOffset(DateTime.UtcNow.AddMinutes(5)).ToUnixTimeSeconds(),
            CancellationToken.None);
        var health = await database.GetAiCompletionHealthAsync(30, CancellationToken.None);

        Assert.Equal(2, report.Summary.TimeoutFailures);
        Assert.Equal(0, report.Summary.HttpOrOtherErrors);
        Assert.Contains(report.FailuresByKind, row => row.Kind == "completion-timeout" && row.Requests == 2);
        Assert.Equal("error", health.Status);
        Assert.Equal(2, health.TimeoutFailures);
    }

    [Fact]
    public async Task TokenUsage_ClassifiesFailedZeroTokenResponsesAsNoValidCompletion()
    {
        using var temp = new TempStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await database.AddLmUsageAsync(Usage(false, "No JSON object found in AI response.", promptTokens: 0, completionTokens: 0, totalTokens: 0), CancellationToken.None);
        await database.AddLmUsageAsync(Usage(false, "Response content was empty.", promptTokens: 0, completionTokens: 0, totalTokens: 0), CancellationToken.None);

        var report = await database.GetTokenUsageAsync(
            new DateTimeOffset(DateTime.UtcNow.AddMinutes(-5)).ToUnixTimeSeconds(),
            new DateTimeOffset(DateTime.UtcNow.AddMinutes(5)).ToUnixTimeSeconds(),
            CancellationToken.None);
        var health = await database.GetAiCompletionHealthAsync(30, CancellationToken.None);

        Assert.Equal(2, report.Summary.NoValidResultFailures);
        Assert.Equal(0, report.Summary.HttpOrOtherErrors);
        Assert.Contains(report.FailuresByKind, row => row.Kind == "no-valid-completion" && row.Requests == 2);
        Assert.Equal("error", health.Status);
        Assert.Equal(2, health.NoValidResultFailures);
    }

    [Fact]
    public async Task CompletionHealth_ReturnsOkAfterSuccessfulRecoveryStreak()
    {
        using var temp = new TempStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var now = DateTime.UtcNow;
        await database.AddLmUsageAsync(Usage(false, "Incident extraction request failed with HTTP 400: {\"error\":\"LM Link connection closed\"}", timestampUtc: now.AddMinutes(-10)), CancellationToken.None);
        await database.AddLmUsageAsync(Usage(true, "", promptTokens: 100, completionTokens: 25, totalTokens: 125, timestampUtc: now.AddMinutes(-8)), CancellationToken.None);
        await database.AddLmUsageAsync(Usage(true, "", promptTokens: 100, completionTokens: 25, totalTokens: 125, timestampUtc: now.AddMinutes(-6)), CancellationToken.None);
        await database.AddLmUsageAsync(Usage(true, "", promptTokens: 100, completionTokens: 25, totalTokens: 125, timestampUtc: now.AddMinutes(-4)), CancellationToken.None);

        var health = await database.GetAiCompletionHealthAsync(30, CancellationToken.None);

        Assert.Equal("ok", health.Status);
        Assert.Equal(1, health.Failures);
        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.Contains("recovered", health.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TokenUsageEntryDto Usage(
        bool success,
        string error,
        int promptTokens = 0,
        int completionTokens = 0,
        int totalTokens = 0,
        DateTime? timestampUtc = null) => new(
            0,
            timestampUtc ?? DateTime.UtcNow,
            "automatic insights",
            "chat.completions",
            success,
            error,
            "http://localhost:1234/v1/chat/completions",
            "qwen/qwen3.6-35b-a3b@q8_0",
            string.Empty,
            string.Empty,
            100,
            1000,
            promptTokens,
            completionTokens,
            totalTokens);

    private sealed class TempStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pizzawave-test-" + Guid.NewGuid().ToString("N"));

        public EngineDatabase CreateDatabase()
        {
            Directory.CreateDirectory(_root);
            var config = new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(_root, "pizzad.db"),
                    AudioRoot = Path.Combine(_root, "audio")
                }
            };
            return new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
                // Best effort cleanup for Windows file handles.
            }
        }
    }
}
