using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;

namespace pizzad.Tests;

public sealed class RemoteServiceOutagePersistenceTests
{
    [Fact]
    public async Task OpenOutageIsUpdatedAndThenRetainedAsResolvedHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-outage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig { DatabasePath = Path.Combine(root, "pizzad.db"), AudioRoot = Path.Combine(root, "audio") }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var started = DateTime.UtcNow.AddMinutes(-10);

            var first = await database.UpsertRemoteServiceOutageAsync("remote-transcription", "http://paxan:9187/health", "small", "", started, started.AddMinutes(1), "connection refused", 2, CancellationToken.None);
            var updated = await database.UpsertRemoteServiceOutageAsync("remote-transcription", "http://paxan:9187/health", "small", "", started, started.AddMinutes(2), "timed out", 4, CancellationToken.None);
            await database.MarkRemoteServiceOutageEmailSentAsync(updated.Id, CancellationToken.None);

            Assert.Equal(first.Id, updated.Id);
            Assert.Equal(4, updated.FailureCount);
            await database.ResolveRemoteServiceOutageAsync("remote-transcription", "small", DateTime.UtcNow, CancellationToken.None);

            Assert.Null(await database.GetOpenRemoteServiceOutageAsync("remote-transcription", CancellationToken.None));
            var history = await database.ListRemoteServiceOutagesAsync(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 10, CancellationToken.None);
            var outage = Assert.Single(history);
            Assert.NotNull(outage.RecoveredAtUtc);
            Assert.True(outage.AdministrativeEmailSent);
            Assert.Equal("small", outage.ReportedModel);

            var callId = await database.UpsertCallAsync(new EngineCall
            {
                UniqueKey = "durable-incident-call",
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
                StopTime = DateTimeOffset.UtcNow.AddMinutes(-5).AddSeconds(8).ToUnixTimeSeconds(),
                SystemShortName = "site-a",
                AudioPath = "call.wav",
                Transcription = "Structure fire reported.",
                TranscriptionStatus = "complete",
                QualityReason = "ok"
            }, CancellationToken.None);
            await database.QueueIncidentAnalysisAsync(callId, CancellationToken.None);
            Assert.Equal(callId, Assert.Single(await database.ListPendingIncidentAnalysisCallsAsync(10, CancellationToken.None)).Id);
            await database.MarkIncidentAnalysisCompletedAsync([callId], CancellationToken.None);
            Assert.Empty(await database.ListPendingIncidentAnalysisCallsAsync(10, CancellationToken.None));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
