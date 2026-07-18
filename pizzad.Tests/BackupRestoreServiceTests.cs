using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace pizzad.Tests;

public sealed class BackupRestoreServiceTests
{
    private const string Passphrase = "correct horse battery staple";

    [Fact]
    public async Task CreateBackup_IncludesCoreStateAndManifest()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            await File.WriteAllTextAsync(Path.Combine(config.Storage.AudioRoot, "call.wav"), "audio");
            await File.WriteAllTextAsync(Path.Combine(config.Storage.AppDataRoot, "talkgroups.json"), "{}");

            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);

            var result = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);

            Assert.True(File.Exists(result.Path));
            Assert.True(result.FileCount >= 7);
            var archivePath = await DecryptAsync(result.Path, root);
            using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("database/pizzad.db"));
            Assert.NotNull(archive.GetEntry("audio/call.wav"));
            Assert.NotNull(archive.GetEntry("appdata/talkgroups.json"));
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
            var backup = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);

            await using var stream = File.OpenRead(backup.Path);
            var preview = await service.StageRestoreAsync(stream, backup.Name, Passphrase, CancellationToken.None);

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
            var result = await service.CreateBackupAsync(BackupRequest("all"), CancellationToken.None);

            Assert.DoesNotContain(result.Warnings, warning => warning.Contains("older than", StringComparison.OrdinalIgnoreCase));
            using var archive = System.IO.Compression.ZipFile.OpenRead(await DecryptAsync(result.Path, root));
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
            var result = await service.CreateBackupAsync(BackupRequest("24h"), CancellationToken.None);

            using var archive = System.IO.Compression.ZipFile.OpenRead(await DecryptAsync(result.Path, root));
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
            var result = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);

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
            var backup = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);

            var preview = await service.StageLocalRestoreAsync(backup.Name, Passphrase, CancellationToken.None);

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
            var rawRfCapturePath = Path.Combine(config.Storage.AppDataRoot, "rf-surveys", "rf-test", "rf-power-scans", "20260709000000", "source-0-851775000-gain-21.cs16");
            var rfSummaryPath = Path.Combine(config.Storage.AppDataRoot, "rf-surveys", "rf-test", "survey.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(rawRfCapturePath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(rfSummaryPath)!);
            await File.WriteAllTextAsync(cachePath, "cache");
            await File.WriteAllTextAsync(regularPath, "{}");
            await File.WriteAllTextAsync(rawRfCapturePath, "raw iq");
            await File.WriteAllTextAsync(rfSummaryPath, "{}");

            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var result = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);

            using var archive = System.IO.Compression.ZipFile.OpenRead(await DecryptAsync(result.Path, root));
            Assert.Null(archive.GetEntry("appdata/.cache/huggingface/model.bin"));
            Assert.Null(archive.GetEntry("appdata/rf-surveys/rf-test/rf-power-scans/20260709000000/source-0-851775000-gain-21.cs16"));
            Assert.NotNull(archive.GetEntry("appdata/talkgroups.json"));
            Assert.NotNull(archive.GetEntry("appdata/rf-surveys/rf-test/survey.json"));
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
            var backup = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);
            var preview = await service.StageLocalRestoreAsync(backup.Name, Passphrase, CancellationToken.None);

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

    [Fact]
    public async Task CleanupInterruptedWork_RemovesWorkingDirectoriesAndUnpublishedArchives()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var working = Path.Combine(config.Storage.AppDataRoot, "backup-working", "interrupted");
            var partial = Path.Combine(config.Storage.AppDataRoot, "backups", "interrupted.pwbak.partial");
            Directory.CreateDirectory(working);
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            await File.WriteAllTextAsync(Path.Combine(working, "partial.zip"), "partial");
            await File.WriteAllTextAsync(partial, "partial");
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);

            service.CleanupInterruptedWork();

            Assert.False(Directory.Exists(working));
            Assert.False(File.Exists(partial));
            Assert.Empty(service.ListBackups());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBackup_UsesVerifiedOnlineQdrantSnapshotWhenEmbeddingsEnabled()
    {
        var root = NewTempRoot();
        await using var qdrant = await FakeSnapshotServer.StartAsync();
        try
        {
            var config = await CreateConfigAsync(root);
            config.Embeddings.Enabled = true;
            config.Embeddings.QdrantBaseUrl = qdrant.BaseUrl;
            config.Embeddings.Collection = "calls";
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);

            var result = await service.CreateBackupAsync(BackupRequest("none"), CancellationToken.None);

            using var archive = System.IO.Compression.ZipFile.OpenRead(await DecryptAsync(result.Path, root));
            Assert.NotNull(archive.GetEntry("qdrant/calls-test.snapshot"));
            Assert.Contains(qdrant.Requests, path => path.StartsWith("/collections/calls/snapshots?", StringComparison.Ordinal));
            Assert.Contains(qdrant.Requests, path => path == "/collections/calls/snapshots/calls-test.snapshot");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyPendingRestore_InIsolatedInstallCreatesVerifiedSafetyBackupFirst()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var sourceBackup = await service.CreateBackupAsync(BackupRequest("none"), CancellationToken.None);
            await service.StageLocalRestoreAsync(sourceBackup.Name, Passphrase, CancellationToken.None);

            var result = await service.ApplyPendingRestoreAsync(Passphrase, CancellationToken.None);

            Assert.True(result.Scheduled);
            Assert.Contains("Pre-restore backup", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(service.ListBackups().Count >= 2);
            var durableResults = new RecoveryResultStore(config).List();
            Assert.Contains(durableResults, item => item.Operation == "restore" && item.Status == "completed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBackup_NoAudioPresetExcludesAllAudio()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            await File.WriteAllTextAsync(Path.Combine(config.Storage.AudioRoot, "call.wav"), "audio");
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);

            var result = await service.CreateBackupAsync(BackupRequest("none"), CancellationToken.None);

            using var archive = System.IO.Compression.ZipFile.OpenRead(await DecryptAsync(result.Path, root));
            Assert.Null(archive.GetEntry("audio/call.wav"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreateBackup_RequiresMatchingPassphraseOfAtLeastTwelveCharacters()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBackupAsync(new("all", "too-short", "too-short"), CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateBackupAsync(new("all", Passphrase, Passphrase + "!"), CancellationToken.None));
            Assert.Empty(service.ListBackups());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EncryptedArchive_RoundTripsAcrossMoreThan256Chunks()
    {
        var root = NewTempRoot();
        try
        {
            var source = Path.Combine(root, "large-source.bin");
            var encrypted = Path.Combine(root, "large.pwbak");
            var restored = Path.Combine(root, "large-restored.bin");
            await using (var stream = File.Create(source))
                stream.SetLength(257L * 1024 * 1024 + 17);

            await EncryptedBackupArchive.EncryptFileAsync(source, encrypted, Passphrase, CancellationToken.None);
            await EncryptedBackupArchive.VerifyFileAsync(source, encrypted, Passphrase, CancellationToken.None);
            await EncryptedBackupArchive.DecryptFileAsync(encrypted, restored, Passphrase, CancellationToken.None);

            Assert.Equal(new FileInfo(source).Length, new FileInfo(restored).Length);
            Assert.Equal(await HashFileAsync(source), await HashFileAsync(restored));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageRestore_RejectsWrongPassphraseWithoutLeavingPendingState()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var backup = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);

            await using var stream = File.OpenRead(backup.Path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageRestoreAsync(stream, backup.Name, "this is the wrong passphrase", CancellationToken.None));

            Assert.Equal(string.Empty, config.Setup.PendingRestorePath);
            Assert.Null(service.PendingRestore());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageRestore_RejectsTamperedEncryptedArchive()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var backup = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);
            var bytes = await File.ReadAllBytesAsync(backup.Path);
            bytes[^1] ^= 0x5a;
            await File.WriteAllBytesAsync(backup.Path, bytes);

            await using var stream = File.OpenRead(backup.Path);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.StageRestoreAsync(stream, backup.Name, Passphrase, CancellationToken.None));
            Assert.Null(service.PendingRestore());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageRestore_ContinuesToSupportLegacyZipBackups()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var backup = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);
            var legacyZip = await DecryptAsync(backup.Path, root);

            await using var stream = File.OpenRead(legacyZip);
            var preview = await service.StageRestoreAsync(stream, "legacy.zip", null, CancellationToken.None);

            Assert.False(preview.Encrypted);
            Assert.All(preview.Checks, check => Assert.True(check.Ok, check.Message));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StageRestore_DerivesDestinationsFromCurrentConfigInsteadOfManifestPaths()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var backup = await service.CreateBackupAsync(BackupRequest(), CancellationToken.None);
            var legacyZip = await DecryptAsync(backup.Path, root);
            const string maliciousTarget = "C:\\untrusted\\overwrite.txt";
            using (var archive = System.IO.Compression.ZipFile.Open(legacyZip, System.IO.Compression.ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry("manifest.json")!;
                BackupManifestDto manifest;
                using (var reader = new StreamReader(entry.Open()))
                    manifest = System.Text.Json.JsonSerializer.Deserialize<BackupManifestDto>(await reader.ReadToEndAsync(), EngineConfig.JsonOptions())!;
                entry.Delete();
                var changed = manifest with { Entries = manifest.Entries.Select((item, index) => index == 0 ? item with { Path = maliciousTarget } : item).ToList() };
                var replacement = archive.CreateEntry("manifest.json");
                await using var writer = new StreamWriter(replacement.Open());
                await writer.WriteAsync(System.Text.Json.JsonSerializer.Serialize(changed, EngineConfig.JsonOptions()));
            }

            await using var stream = File.OpenRead(legacyZip);
            var preview = await service.StageRestoreAsync(stream, "legacy.zip", null, CancellationToken.None);
            var plan = System.Text.Json.JsonSerializer.Deserialize<BackupRestorePlanDto>(await File.ReadAllTextAsync(Path.Combine(preview.StagePath, "restore-plan.json")), EngineConfig.JsonOptions())!;

            Assert.DoesNotContain(plan.Entries, item => string.Equals(item.TargetPath, maliciousTarget, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.Entries, item => string.Equals(item.TargetPath, config.Storage.DatabasePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreUpload_ResumesVerifiedChunksAcrossServiceInstances()
    {
        var root = NewTempRoot();
        try
        {
            var config = await CreateConfigAsync(root);
            await File.WriteAllBytesAsync(Path.Combine(config.Storage.AudioRoot, "large.wav"), RandomNumberGenerator.GetBytes(5 * 1024 * 1024));
            var backups = new BackupRestoreService(config, NullLogger<BackupRestoreService>.Instance);
            var backup = await backups.CreateBackupAsync(BackupRequest(), CancellationToken.None);
            var fileBytes = await File.ReadAllBytesAsync(backup.Path);
            var wholeHash = Convert.ToHexString(SHA256.HashData(fileBytes)).ToLowerInvariant();
            var uploads = new RestoreUploadService(config, backups);
            var session = await uploads.CreateAsync(new(backup.Name, fileBytes.Length, wholeHash), CancellationToken.None);

            Assert.True(session.ChunkCount >= 2);
            var second = fileBytes.AsMemory(session.ChunkSize, fileBytes.Length - session.ChunkSize);
            await uploads.PutChunkAsync(session.Id, 1, new MemoryStream(second.ToArray()), Convert.ToHexString(SHA256.HashData(second.Span)).ToLowerInvariant(), CancellationToken.None);

            uploads = new RestoreUploadService(config, backups);
            var resumed = uploads.Get(session.Id);
            Assert.NotNull(resumed);
            Assert.Contains(1, resumed.ReceivedChunks);
            var first = fileBytes.AsMemory(0, session.ChunkSize);
            await uploads.PutChunkAsync(session.Id, 0, new MemoryStream(first.ToArray()), Convert.ToHexString(SHA256.HashData(first.Span)).ToLowerInvariant(), CancellationToken.None);
            var preview = await uploads.CompleteAsync(session.Id, Passphrase, CancellationToken.None);

            Assert.All(preview.Checks, check => Assert.True(check.Ok, check.Message));
            Assert.Null(uploads.Get(session.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static BackupCreateRequestDto BackupRequest(string audioWindow = "all") =>
        new(audioWindow, Passphrase, Passphrase);

    private static async Task<string> DecryptAsync(string encryptedPath, string root)
    {
        var path = Path.Combine(root, $"decrypted-{Guid.NewGuid():N}.zip");
        await EncryptedBackupArchive.DecryptFileAsync(encryptedPath, path, Passphrase, CancellationToken.None);
        return path;
    }

    private static async Task<byte[]> HashFileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
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
            Embeddings = new EmbeddingsConfig { Enabled = false, QdrantStoragePath = Path.Combine(root, "qdrant") },
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

    private sealed class FakeSnapshotServer : IAsyncDisposable
    {
        private static readonly byte[] Snapshot = RandomNumberGenerator.GetBytes(4096);
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private FakeSnapshotServer(int port)
        {
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }
        public List<string> Requests { get; } = [];

        public static async Task<FakeSnapshotServer> StartAsync()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var server = new FakeSnapshotServer(port);
            await Task.Yield();
            return server;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                var path = context.Request.RawUrl ?? string.Empty;
                lock (Requests) Requests.Add(path);
                context.Response.StatusCode = 200;
                byte[] body;
                if (context.Request.HttpMethod == "POST")
                {
                    var checksum = Convert.ToHexString(SHA256.HashData(Snapshot)).ToLowerInvariant();
                    body = Encoding.UTF8.GetBytes($"{{\"result\":{{\"name\":\"calls-test.snapshot\",\"checksum\":\"{checksum}\"}},\"status\":\"ok\"}}");
                    context.Response.ContentType = "application/json";
                }
                else if (context.Request.HttpMethod == "GET")
                {
                    body = Snapshot;
                    context.Response.ContentType = "application/octet-stream";
                }
                else
                {
                    body = Encoding.UTF8.GetBytes("{\"result\":true,\"status\":\"ok\"}");
                    context.Response.ContentType = "application/json";
                }
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { await _loop; } catch { }
            _cts.Dispose();
        }
    }
}
