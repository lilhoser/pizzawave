using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class BackupJobServiceTests
{
    [Fact]
    public async Task Initialize_UpgradesExistingJobsWithUpdateTimestamp()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            await using (var connection = new SqliteConnection($"Data Source={config.Storage.DatabasePath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE jobs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        type TEXT NOT NULL,
                        status TEXT NOT NULL,
                        total INTEGER NOT NULL DEFAULT 0,
                        completed INTEGER NOT NULL DEFAULT 0,
                        failed INTEGER NOT NULL DEFAULT 0,
                        message TEXT NOT NULL DEFAULT '',
                        created_at_utc TEXT NOT NULL,
                        started_at_utc TEXT NULL,
                        finished_at_utc TEXT NULL,
                        payload_json TEXT NOT NULL DEFAULT '{}'
                    );
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var created = DateTime.UtcNow;
            var jobId = await database.AddJobAsync(new JobDto { Type = "migration_test", Status = "queued", CreatedAtUtc = created }, CancellationToken.None);
            var job = await database.GetJobAsync(jobId, CancellationToken.None);

            Assert.NotNull(job?.UpdatedAtUtc);
            Assert.InRange((job.UpdatedAtUtc.Value.ToUniversalTime() - created).TotalSeconds, -1, 1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsync_RunsBackupAsAuditedBackgroundJob()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var backups = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var service = new BackupJobService(backups, database, new TestApplicationLifetime(), NullLogger<BackupJobService>.Instance);

            var job = await service.StartAsync(new BackupCreateRequestDto("24h"), CancellationToken.None);

            Assert.Equal(BackupJobService.JobType, job.Type);
            Assert.True(job.Id > 0);
            var completed = await WaitForJobAsync(database, job.Id);
            Assert.Equal("completed", completed.Status);
            Assert.Equal(1, completed.Completed);
            Assert.Contains("Created", completed.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(completed.UpdatedAtUtc);
            Assert.True(completed.UpdatedAtUtc >= completed.CreatedAtUtc);
            Assert.Single(backups.ListBackups());
            var logs = await database.ListJobLogsAsync(job.Id, 0, CancellationToken.None);
            Assert.Contains(logs, log => log.Text.Contains("Backup archive started", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(logs, log => log.Text.Contains("Backup archive created", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HostedStart_MarksInterruptedBackupJobsCanceled()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var database = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var jobId = await database.AddJobAsync(new JobDto
            {
                Type = BackupJobService.JobType,
                Status = "running",
                Total = 1,
                Completed = 0,
                Failed = 0,
                Message = "Creating backup archive...",
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-1)
            }, CancellationToken.None);
            var service = new BackupJobService(
                new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance),
                database,
                new TestApplicationLifetime(),
                NullLogger<BackupJobService>.Instance);

            await service.StartAsync(CancellationToken.None);

            var job = await database.GetJobAsync(jobId, CancellationToken.None);
            Assert.NotNull(job);
            Assert.Equal("canceled", job.Status);
            Assert.Contains("interrupted", job.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(job.FinishedAtUtc);
            Assert.NotNull(job.UpdatedAtUtc);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<JobDto> WaitForJobAsync(EngineDatabase database, long jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var job = await database.GetJobAsync(jobId, CancellationToken.None);
            if (job is { Status: "completed" or "failed" or "canceled" })
                return job;
            await Task.Delay(100);
        }

        return await database.GetJobAsync(jobId, CancellationToken.None)
            ?? throw new InvalidOperationException("Backup job disappeared.");
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-backup-job-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task<EngineConfig> CreateConfigAsync(string root)
    {
        var config = new EngineConfig
        {
            Branding = new BrandingConfig { StackName = "Test Rig" },
            Storage = new StorageConfig
            {
                DatabasePath = Path.Combine(root, "pizzad.db"),
                AudioRoot = Path.Combine(root, "audio"),
                AppDataRoot = Path.Combine(root, "appdata")
            },
            Auth = new AuthConfig { TokenFile = Path.Combine(root, "pizzad.token") },
            TrunkRecorder = new TrunkRecorderConfig
            {
                ConfigPath = Path.Combine(root, "trunk-recorder", "config.json"),
                TalkgroupsPath = Path.Combine(root, "trunk-recorder", "talkgroups.csv"),
                TalkgroupCatalogPath = Path.Combine(root, "appdata", "talkgroups.json")
            },
            Embeddings = new EmbeddingsConfig { QdrantStoragePath = Path.Combine(root, "qdrant") },
            Setup = new SetupConfig { Completed = true, CurrentStep = "complete" }
        };
        config.ConfigPath = Path.Combine(root, "pizzad.json");
        config.ApplyDefaults();
        Directory.CreateDirectory(config.Storage.AudioRoot);
        Directory.CreateDirectory(config.Storage.AppDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(config.TrunkRecorder.ConfigPath)!);
        await File.WriteAllTextAsync(config.ConfigPath, System.Text.Json.JsonSerializer.Serialize(config, EngineConfig.JsonOptions()));
        await File.WriteAllTextAsync(config.Auth.TokenFile, "token");
        await File.WriteAllTextAsync(config.TrunkRecorder.ConfigPath, "{}");
        await File.WriteAllTextAsync(config.TrunkRecorder.TalkgroupsPath, "Decimal,Alpha Tag,Category\n1001,Dispatch,police\n");
        return config;
    }

    private sealed class TestApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication()
        {
        }
    }
}
