using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace pizzad.Tests;

public sealed class SystemResetServiceTests
{
    [Fact]
    public async Task SiteReset_ClearsQdrantEvenWhenSetupIncomplete()
    {
        using var qdrant = await FakeQdrantServer.StartAsync();
        using var temp = new TempMigrationStore();
        temp.Config.Setup.Completed = false;
        temp.Config.Embeddings.Enabled = true;
        temp.Config.Embeddings.OpenAiBaseUrl = "http://embedding.invalid/v1";
        temp.Config.Embeddings.OpenAiModel = "nomic-embed-text";
        temp.Config.Embeddings.QdrantBaseUrl = qdrant.BaseUrl;
        temp.Config.Embeddings.Collection = "reset_test";
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        await temp.SeedCallAsync(database);
        var service = temp.CreateService(database);

        var result = await service.ResetAsync(new SystemResetRequestDto
        {
            Presets = ["site-reset"],
            CreateBackup = true
        }, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(result.Backup);
        Assert.Contains("/collections/reset_test", qdrant.Requests);
        Assert.False(File.Exists(temp.AudioFile));
        Assert.False(File.Exists(temp.TrConfigPath));
        Assert.False(File.Exists(temp.TrTalkgroupsPath));
        Assert.False(File.Exists(temp.TalkgroupCatalogPath));
        Assert.Equal("freshTr", temp.Config.Setup.InstallMode);
        Assert.True(temp.Config.Embeddings.Enabled);
        Assert.Equal("reset_test", temp.Config.Embeddings.Collection);
        Assert.True(temp.Config.Setup.Completed);
    }

    [Fact]
    public async Task SiteReset_OnlyPreservesSelectedSettings()
    {
        using var temp = new TempMigrationStore();
        temp.Config.Branding.StackName = "Old Site";
        temp.Config.Transcription.Provider = "remote-faster-whisper";
        temp.Config.Transcription.OpenAiBaseUrl = "http://paxan:9187/v1";
        temp.Config.Transcription.DeferredTalkgroups = [1001];
        temp.Config.AiInsights.Enabled = true;
        temp.Config.AiInsights.OpenAiModel = "old-model";
        temp.Config.Embeddings.Enabled = true;
        temp.Config.Embeddings.Collection = "old_vectors";
        temp.Config.Alerts.EmailEnabled = true;
        temp.Config.Alerts.Playback.Enabled = true;
        temp.Config.RfSurvey.P25ProbeDurationSeconds = 75;
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var service = temp.CreateService(database);

        await service.ResetAsync(new SystemResetRequestDto
        {
            Presets = ["site-reset"],
            CreateBackup = false,
            PreserveBranding = false,
            PreserveTranscription = true,
            PreserveAiInsights = true,
            PreserveEmbeddings = false,
            PreserveAlerts = false,
            PreserveRfSurvey = false
        }, CancellationToken.None);

        Assert.Equal("PizzaWave", temp.Config.Branding.StackName);
        Assert.Equal("remote-faster-whisper", temp.Config.Transcription.Provider);
        Assert.Equal("http://paxan:9187/v1", temp.Config.Transcription.OpenAiBaseUrl);
        Assert.Empty(temp.Config.Transcription.DeferredTalkgroups);
        Assert.True(temp.Config.AiInsights.Enabled);
        Assert.Equal("old-model", temp.Config.AiInsights.OpenAiModel);
        Assert.False(temp.Config.Embeddings.Enabled);
        Assert.False(temp.Config.Alerts.EmailEnabled);
        Assert.False(temp.Config.Alerts.Playback.Enabled);
        Assert.Equal(45, temp.Config.RfSurvey.P25ProbeDurationSeconds);
        Assert.Empty(temp.Config.Alerts.Rules);
    }

    [Fact]
    public async Task FailedSafetyBackup_DoesNotPauseLiveIngest()
    {
        using var temp = new TempMigrationStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var ingest = new IngestControlService(NullLogger<IngestControlService>.Instance);
        var service = temp.CreateService(database, ingest);

        Directory.Delete(temp.AppDataRoot, recursive: true);
        await File.WriteAllTextAsync(temp.AppDataRoot, "blocks backup directory creation");

        await Assert.ThrowsAnyAsync<IOException>(() => service.ResetAsync(new SystemResetRequestDto
        {
            Presets = ["data-only"],
            CreateBackup = true
        }, CancellationToken.None));

        Assert.False(ingest.Paused);
    }

    [Theory]
    [InlineData("custom")]
    [InlineData("unknown")]
    public async Task UnsupportedResetScope_IsRejectedWithoutPausingIngest(string scope)
    {
        using var temp = new TempMigrationStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var ingest = new IngestControlService(NullLogger<IngestControlService>.Instance);
        var service = temp.CreateService(database, ingest);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetAsync(new SystemResetRequestDto
        {
            Presets = [scope],
            CreateBackup = false
        }, CancellationToken.None));

        Assert.Contains("Unsupported reset scope", error.Message);
        Assert.False(ingest.Paused);
    }

    [Fact]
    public async Task MultipleResetScopes_AreRejectedWithoutPausingIngest()
    {
        using var temp = new TempMigrationStore();
        var database = temp.CreateDatabase();
        await database.InitializeAsync(CancellationToken.None);
        var ingest = new IngestControlService(NullLogger<IngestControlService>.Instance);
        var service = temp.CreateService(database, ingest);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResetAsync(new SystemResetRequestDto
        {
            Presets = ["data-only", "full-reset"],
            CreateBackup = false
        }, CancellationToken.None));

        Assert.Contains("exactly one reset scope", error.Message);
        Assert.False(ingest.Paused);
    }

    private sealed class TempMigrationStore : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "pizzawave-migration-test-" + Guid.NewGuid().ToString("N"));

        public TempMigrationStore()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(AudioRoot);
            Directory.CreateDirectory(AppDataRoot);
            Directory.CreateDirectory(TrRoot);
            File.WriteAllText(AudioFile, "audio");
            File.WriteAllText(TrConfigPath, "{}");
            File.WriteAllText(TrTalkgroupsPath, "Decimal,Alpha Tag,Category\n1001,Dispatch,police\n");
            File.WriteAllText(TalkgroupCatalogPath, "{}");
            Config = new EngineConfig
            {
                Branding = new BrandingConfig { StackName = "Migration Test" },
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(_root, "pizzad.db"),
                    AudioRoot = AudioRoot,
                    AppDataRoot = AppDataRoot
                },
                Auth = new AuthConfig { TokenFile = Path.Combine(_root, "pizzad.token") },
                TrunkRecorder = new TrunkRecorderConfig
                {
                    ConfigPath = TrConfigPath,
                    TalkgroupsPath = TrTalkgroupsPath,
                    TalkgroupCatalogPath = TalkgroupCatalogPath
                },
                AiInsights = new AiInsightsConfig { Enabled = false },
                Embeddings = new EmbeddingsConfig { Enabled = false, QdrantStoragePath = Path.Combine(_root, "qdrant") },
                Alerts = new AlertConfig
                {
                    Rules = [new EngineAlertRule { Name = "Old alert", Enabled = true, Keywords = "old" }]
                },
                Profiles = new ProfileConfig
                {
                    Items = [new ProcessingProfile { Name = "Old profile", Talkgroups = [new ProfileTalkgroupSetting { SystemShortName = "old", Id = 1001, Enabled = false }] }]
                },
                Locations = new LocationConfig
                {
                    MonitoredAreas = [new MonitoredAreaConfig { AreaId = "old", AreaLabel = "Old", SystemShortName = "old" }]
                },
                Setup = new SetupConfig { Completed = true, CurrentStep = "complete" }
            };
            Config.ConfigPath = Path.Combine(_root, "pizzad.json");
            Config.ApplyDefaults();
        }

        public EngineConfig Config { get; }
        public string AudioRoot => Path.Combine(_root, "audio");
        public string AppDataRoot => Path.Combine(_root, "appdata");
        public string TrRoot => Path.Combine(_root, "trunk-recorder");
        public string AudioFile => Path.Combine(AudioRoot, "sample.wav");
        public string TrConfigPath => Path.Combine(TrRoot, "config.json");
        public string TrTalkgroupsPath => Path.Combine(TrRoot, "talkgroups.csv");
        public string TalkgroupCatalogPath => Path.Combine(AppDataRoot, "talkgroups.json");

        public EngineDatabase CreateDatabase() => new(Config, NullLogger<EngineDatabase>.Instance);

        public SystemResetService CreateService(EngineDatabase database, IngestControlService? ingest = null)
        {
            var events = new EventStream();
            var catalog = new TalkgroupCatalogService(Config, NullLogger<TalkgroupCatalogService>.Instance);
            var embeddings = new EmbeddingService(Config, database, events, catalog, NullLogger<EmbeddingService>.Instance);
            ingest ??= new IngestControlService(NullLogger<IngestControlService>.Instance);
            var auth = new AuthService(Config, NullLogger<AuthService>.Instance);
            var backups = new BackupRestoreService(Config, NullLogger<BackupRestoreService>.Instance);
            return new SystemResetService(Config, database, embeddings, ingest, auth, backups, NullLogger<SystemResetService>.Instance);
        }

        public async Task<long> SeedCallAsync(EngineDatabase database) =>
            await database.UpsertCallAsync(new EngineCall
            {
                UniqueKey = "migration-test-call",
                StartTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                StopTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5,
                Source = 1,
                SystemShortName = "old",
                CallstreamCallId = 1,
                Talkgroup = 1001,
                TalkgroupName = "Dispatch",
                Frequency = 851.0125,
                Category = "police",
                AudioPath = AudioFile,
                Transcription = "Test transcript.",
                TranscriptionStatus = "complete",
                QualityReason = "ok",
                RawMetadataJson = "{}"
            }, CancellationToken.None);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private sealed class FakeQdrantServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private FakeQdrantServer(int port)
        {
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }
        public List<string> Requests { get; } = new();

        public static async Task<FakeQdrantServer> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var server = new FakeQdrantServer(port);
            await Task.Yield();
            return server;
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch when (_cts.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    return;
                }

                lock (Requests)
                    Requests.Add(context.Request.RawUrl ?? string.Empty);
                context.Response.StatusCode = 200;
                var body = Encoding.UTF8.GetBytes("{\"result\":true}");
                await context.Response.OutputStream.WriteAsync(body);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
