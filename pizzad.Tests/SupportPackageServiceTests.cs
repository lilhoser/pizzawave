using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Text.Json;

namespace pizzad.Tests;

public sealed class SupportPackageServiceTests
{
    [Fact]
    public async Task Create_RedactsSecretsAndExcludesRestorableData()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-support-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new SupportPackageService(config, NullLogger<SupportPackageService>.Instance);

            var result = await service.CreateAsync(new SupportPackageCreateRequestDto(24), CancellationToken.None);

            Assert.True(File.Exists(result.Path));
            Assert.True(result.Manifest.RedactionCount >= 2);
            using var archive = ZipFile.OpenRead(result.Path);
            Assert.NotNull(archive.GetEntry("manifest.json"));
            Assert.NotNull(archive.GetEntry("config/pizzad.redacted.json"));
            Assert.NotNull(archive.GetEntry("evidence/database-summary.json"));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("audio", StringComparison.OrdinalIgnoreCase));

            foreach (var entry in archive.Entries.Where(entry => entry.Length > 0))
            {
                using var reader = new StreamReader(entry.Open());
                var text = await reader.ReadToEndAsync();
                Assert.DoesNotContain("actual-admin-token-value", text, StringComparison.Ordinal);
                Assert.DoesNotContain("actual-qdrant-secret", text, StringComparison.Ordinal);
                Assert.DoesNotContain("sensitive transcript text", text, StringComparison.Ordinal);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Create_PrivateEvidenceRequiresAcknowledgementAndRecordsManifestScope()
    {
        var root = Path.Combine(Path.GetTempPath(), $"pizzawave-support-private-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = await CreateConfigAsync(root);
            var service = new SupportPackageService(config, NullLogger<SupportPackageService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(new(24, false, true, false), CancellationToken.None));
            var result = await service.CreateAsync(new(24, false, true, true), CancellationToken.None);

            Assert.Contains("transcript text", result.Manifest.PrivacyInclusions);
            Assert.DoesNotContain("transcript text", result.Manifest.Exclusions);
            using var archive = ZipFile.OpenRead(result.Path);
            Assert.NotNull(archive.GetEntry("private-opt-in/transcripts.json"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<EngineConfig> CreateConfigAsync(string root)
    {
        var config = new EngineConfig
        {
            Branding = new BrandingConfig { StackName = "Support Test" },
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
                LogServiceName = "trunk-recorder"
            },
            Embeddings = new EmbeddingsConfig { Enabled = true, QdrantApiKey = "actual-qdrant-secret", QdrantStoragePath = Path.Combine(root, "qdrant") }
        };
        config.ConfigPath = Path.Combine(root, "pizzad.json");
        config.ApplyDefaults();
        Directory.CreateDirectory(config.Storage.AppDataRoot);
        Directory.CreateDirectory(config.Storage.AudioRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(config.TrunkRecorder.ConfigPath)!);
        await File.WriteAllTextAsync(config.Auth.TokenFile, "actual-admin-token-value");
        await File.WriteAllTextAsync(config.ConfigPath, JsonSerializer.Serialize(config, EngineConfig.JsonOptions()));
        await File.WriteAllTextAsync(config.TrunkRecorder.ConfigPath, "{\"apiKey\":\"actual-qdrant-secret\",\"systems\":[]}");
        await File.WriteAllTextAsync(Path.Combine(config.Storage.AudioRoot, "call.wav"), "audio bytes");
        await using var connection = new SqliteConnection($"Data Source={config.Storage.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE TABLE calls(id INTEGER PRIMARY KEY, start_time INTEGER, system_short_name TEXT, talkgroup INTEGER, talkgroup_name TEXT, transcription TEXT); INSERT INTO calls(start_time, system_short_name, talkgroup, talkgroup_name, transcription) VALUES ({DateTimeOffset.UtcNow.ToUnixTimeSeconds()}, 'test', 1001, 'Dispatch', 'sensitive transcript text');";
        await command.ExecuteNonQueryAsync();
        return config;
    }
}
