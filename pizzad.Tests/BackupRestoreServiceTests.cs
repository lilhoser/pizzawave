using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class BackupRestoreServiceTests
{
    [Fact]
    public async Task CreateBackup_IncludesCoreStateAndManifest()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            await File.WriteAllTextAsync(Path.Combine(config.Storage.AudioRoot, "call.wav"), "audio");
            await File.WriteAllTextAsync(Path.Combine(config.Storage.AppDataRoot, "talkgroups.json"), "{}");
            Directory.CreateDirectory(config.Embeddings.QdrantStoragePath);
            await File.WriteAllTextAsync(Path.Combine(config.Embeddings.QdrantStoragePath, "collection.bin"), "qdrant");

            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);

            var result = await service.CreateBackupAsync(null, CancellationToken.None);

            Assert.True(File.Exists(result.Path));
            Assert.True(result.FileCount >= 7);
            using var archive = System.IO.Compression.ZipFile.OpenRead(result.Path);
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("database/pizzad.db"));
            Assert.NotNull(archive.GetEntry("audio/call.wav"));
            Assert.NotNull(archive.GetEntry("appdata/talkgroups.json"));
            Assert.NotNull(archive.GetEntry("qdrant/collection.bin"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageRestore_ValidatesArchiveWithoutLaunchingSetupMode()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var backup = await service.CreateBackupAsync(null, CancellationToken.None);

            await using var stream = File.OpenRead(backup.Path);
            var preview = await service.StageRestoreAsync(stream, backup.Name, CancellationToken.None);

            Assert.NotEmpty(preview.Manifest.Entries);
            Assert.All(preview.Checks, check => Assert.True(check.Ok, check.Message));
            Assert.True(config.Setup.Completed);
            Assert.Equal("complete", config.Setup.CurrentStep);
            Assert.True(Directory.Exists(config.Setup.PendingRestorePath));
            Assert.False(string.IsNullOrWhiteSpace(config.Setup.PendingRestoreManifestJson));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBackup_IncludesOldAudioWithoutAgeGuardrail()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var oldAudio = Path.Combine(config.Storage.AudioRoot, "old.wav");
            var recentAudio = Path.Combine(config.Storage.AudioRoot, "recent.wav");
            await File.WriteAllTextAsync(oldAudio, "old");
            await File.WriteAllTextAsync(recentAudio, "recent");
            File.SetLastWriteTimeUtc(oldAudio, DateTime.UtcNow.AddDays(-40));

            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var result = await service.CreateBackupAsync(new BackupCreateRequestDto("all"), CancellationToken.None);

            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("older than", StringComparison.OrdinalIgnoreCase));
            using var archive = System.IO.Compression.ZipFile.OpenRead(result.Path);
            Assert.NotNull(archive.GetEntry("audio/old.wav"));
            Assert.NotNull(archive.GetEntry("audio/recent.wav"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EstimateBackup_ReportsSourceSizeByKind()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            await File.WriteAllTextAsync(Path.Combine(config.Storage.AudioRoot, "call.wav"), "audio");
            await File.WriteAllTextAsync(Path.Combine(config.Storage.AppDataRoot, "talkgroups.json"), "{}");

            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var estimate = service.EstimateBackup();

            Assert.True(estimate.Bytes > 0);
            Assert.True(estimate.FileCount >= 6);
            Assert.Contains(estimate.Kinds, kind => kind.Kind == "database" && kind.Bytes > 0 && kind.FileCount == 1);
            Assert.Contains(estimate.Kinds, kind => kind.Kind == "audio" && kind.Bytes > 0 && kind.FileCount == 1);
            Assert.Contains(estimate.Kinds, kind => kind.Kind == "appdata" && kind.Bytes > 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBackup_FiltersAudioByPresetWindow()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var oldAudio = Path.Combine(config.Storage.AudioRoot, "old.wav");
            var recentAudio = Path.Combine(config.Storage.AudioRoot, "recent.wav");
            await File.WriteAllTextAsync(oldAudio, "old");
            await File.WriteAllTextAsync(recentAudio, "recent");
            File.SetLastWriteTimeUtc(oldAudio, DateTime.UtcNow.AddDays(-3));

            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var result = await service.CreateBackupAsync(new BackupCreateRequestDto("24h"), CancellationToken.None);

            using var archive = System.IO.Compression.ZipFile.OpenRead(result.Path);
            Assert.Null(archive.GetEntry("audio/old.wav"));
            Assert.NotNull(archive.GetEntry("audio/recent.wav"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteBackup_RemovesNamedArchive()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var result = await service.CreateBackupAsync(null, CancellationToken.None);

            Assert.True(service.DeleteBackup(result.Name));
            Assert.False(File.Exists(result.Path));
            Assert.False(service.DeleteBackup(result.Name));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageLocalRestore_UsesExistingBackupArchive()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var backup = await service.CreateBackupAsync(null, CancellationToken.None);

            var preview = await service.StageLocalRestoreAsync(backup.Name, CancellationToken.None);

            Assert.NotNull(preview);
            Assert.NotEmpty(preview.Manifest.Entries);
            Assert.All(preview.Checks, check => Assert.True(check.Ok, check.Message));
            Assert.Equal("complete", config.Setup.CurrentStep);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBackup_ExcludesAppDataCaches()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var cachePath = Path.Combine(config.Storage.AppDataRoot, ".cache", "huggingface", "model.bin");
            var regularPath = Path.Combine(config.Storage.AppDataRoot, "talkgroups.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllTextAsync(cachePath, "cache");
            await File.WriteAllTextAsync(regularPath, "{}");

            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var result = await service.CreateBackupAsync(null, CancellationToken.None);

            using var archive = System.IO.Compression.ZipFile.OpenRead(result.Path);
            Assert.Null(archive.GetEntry("appdata/.cache/huggingface/model.bin"));
            Assert.NotNull(archive.GetEntry("appdata/talkgroups.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelPendingRestore_ClearsStateAndDeletesStage()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var backup = await service.CreateBackupAsync(null, CancellationToken.None);
            var preview = await service.StageLocalRestoreAsync(backup.Name, CancellationToken.None);

            Assert.NotNull(preview);
            Assert.True(Directory.Exists(preview.StagePath));

            var result = await service.CancelPendingRestoreAsync(CancellationToken.None);

            Assert.True(result.Canceled);
            Assert.Equal(string.Empty, config.Setup.PendingRestorePath);
            Assert.Equal(string.Empty, config.Setup.PendingRestoreManifestJson);
            Assert.False(Directory.Exists(preview.StagePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-backup-tests-{Guid.NewGuid():N}");
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
        Directory.CreateDirectory(Path.GetDirectoryName(config.Storage.DatabasePath)!);
        await File.WriteAllTextAsync(config.ConfigPath, System.Text.Json.JsonSerializer.Serialize(config, EngineConfig.JsonOptions()));
        await File.WriteAllTextAsync(config.Auth.TokenFile, "token");
        await File.WriteAllTextAsync(config.TrunkRecorder.ConfigPath, "{}");
        await File.WriteAllTextAsync(config.TrunkRecorder.TalkgroupsPath, "Decimal,Alpha Tag,Category\n1001,Dispatch,police\n");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = config.Storage.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE calls(id INTEGER PRIMARY KEY, transcription TEXT); INSERT INTO calls(transcription) VALUES ('test');";
        await command.ExecuteNonQueryAsync();
        return config;
    }
}
