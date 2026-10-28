using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class IngestControlPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pizzawave-ingest-hold-" + Guid.NewGuid().ToString("N"));
    private readonly EngineDatabase _database;

    public IngestControlPersistenceTests()
    {
        var config = new EngineConfig();
        config.Storage.DatabasePath = Path.Combine(_root, "engine.db");
        config.Storage.AudioRoot = Path.Combine(_root, "audio");
        _database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        _database.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private IngestControlService Restart() => new(_database, NullLogger<IngestControlService>.Instance);

    [Fact]
    public void ManualPauseSurvivesNewServiceWithOriginalTimeAndReasonUntilExplicitResume()
    {
        var first = Restart();
        var paused = first.Pause(false, "Operator hold", 12);
        var next = Restart(); next.CompleteStartupRecovery();
        var restored = next.GetStatus(0);
        Assert.True(restored.Paused);
        Assert.False(restored.UntilQueueClear);
        Assert.Equal(paused.PausedAtUtc, restored.PausedAtUtc);
        Assert.Equal("Operator hold", restored.Reason);
        Assert.True(next.ShouldDropLiveCall(0));
        next.Resume("operator release", 0);
        Assert.False(Restart().GetStatus(0).Paused);
    }

    [Fact]
    public void QueueClearPauseCannotReleaseBeforeRecoveryOrWhileRecoveredWorkRemains()
    {
        var first = Restart(); var paused = first.Pause(true, "Drain backlog", 12);
        var next = Restart();
        Assert.True(next.GetStatus(0).Paused);
        Assert.True(next.ShouldDropLiveCall(0));
        next.CompleteStartupRecovery();
        Assert.True(next.GetStatus(4).Paused);
        Assert.Equal(paused.PausedAtUtc, next.GetStatus(4).PausedAtUtc);
        Assert.False(next.GetStatus(0).Paused);
        Assert.False(Restart().GetStatus(0).Paused);
    }

    [Fact]
    public void CorruptStoredHoldFailsPausedAndExplicitResumeRepairsIt()
    {
        var first = Restart(); first.Pause(false, "hold", 1);
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE ingest_control_state SET paused_at_utc='not a timestamp';";
            command.ExecuteNonQuery();
        }
        var restored = Restart(); restored.CompleteStartupRecovery();
        Assert.True(restored.ShouldDropLiveCall(0));
        Assert.Equal("unavailable", restored.GetStatus(0).StateStatus);
        restored.Resume("reviewed and released", 0);
        Assert.False(Restart().GetStatus(0).Paused);
    }

    [Fact]
    public async Task MissingSavedRowIsNotTreatedAsANewUnpausedInstallation()
    {
        var first = Restart(); first.Pause(false, "hold", 0);
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM ingest_control_state;";
            command.ExecuteNonQuery();
        }
        await _database.InitializeAsync(CancellationToken.None);
        var recovered = Restart().GetStatus(0);
        Assert.True(recovered.Paused);
        Assert.Equal("unavailable", recovered.StateStatus);
    }

    [Fact]
    public void LegacyApiTimestampPrecisionIsRestoredWithoutChangingTheSourceTime()
    {
        using (var connection = _database.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT OR REPLACE INTO ingest_control_state VALUES (1,1,1,0,'upgraded hold','2026-10-28T18:05:15.123456Z');";
            command.ExecuteNonQuery();
        }
        var state = Restart().GetStatus(0);
        Assert.True(state.Paused);
        Assert.Equal(new DateTime(2026,10,28,18,5,15,DateTimeKind.Utc).AddTicks(1234560), state.PausedAtUtc);
    }

    [Fact]
    public void FailedWriteDoesNotAcknowledgePauseOrClearAnExistingHold()
    {
        var service = Restart(); service.Pause(false, "hold", 0);
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_hold_write BEFORE UPDATE ON ingest_control_state BEGIN SELECT RAISE(FAIL, 'test write failure'); END;";
        command.ExecuteNonQuery();
        Assert.ThrowsAny<Exception>(() => service.Resume("release", 0));
        Assert.True(service.GetStatus(0).Paused);
        Assert.Equal("unavailable", service.GetStatus(0).StateStatus);
        Assert.ThrowsAny<Exception>(() => service.Pause(false, "changed reason", 0));
        Assert.Equal("hold", Restart().GetStatus(0).Reason);
    }

    [Fact]
    public void FirstInstallationDoesNotInventAPauseAndReassertionKeepsOriginalTime()
    {
        var service = Restart(); Assert.False(service.GetStatus(0).Paused);
        var first = service.Pause(false, "first", 0);
        Assert.Equal(first.PausedAtUtc, service.Pause(false, "reasserted", 0).PausedAtUtc);
        Assert.Equal(first.PausedAtUtc, Restart().GetStatus(0).PausedAtUtc);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
