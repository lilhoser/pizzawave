using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace pizzad.Tests;

[Trait("Category", "Feature")]
public sealed class ApiFeatureTests : IClassFixture<PizzadApiFactory>
{
    private readonly PizzadApiFactory _factory;

    public ApiFeatureTests(PizzadApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HealthAndReadEndpointsWorkWithoutExternalServices()
    {
        using var client = _factory.CreateClient();

        using var health = await client.GetAsync("/api/v1/health");
        using var authInit = await client.GetAsync("/api/v1/auth-init");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, authInit.StatusCode);
        var json = await authInit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("writeRequiresAuth").GetBoolean());
    }

    [Fact]
    public async Task UnknownApiRouteReturnsNotFoundInsteadOfSpaShell()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task QualityCheckEndpointUsesReadOnlyLocalTelemetry()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/system/quality-check");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("start").GetInt64() > 0);
        Assert.True(json.GetProperty("end").GetInt64() >= json.GetProperty("start").GetInt64());
        Assert.True(json.TryGetProperty("calls", out _));
        Assert.True(json.TryGetProperty("ai", out _));
        Assert.True(json.TryGetProperty("incidentOperations", out _));
    }

    [Fact]
    public async Task QualityCheckEndpointHonorsHoursQuery()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/system/quality-check?hours=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var seconds = json.GetProperty("end").GetInt64() - json.GetProperty("start").GetInt64();
        Assert.Equal(7200, seconds);
    }

    [Fact]
    public async Task WriteEndpointsRequireTokenAndRejectDisabledTranscription()
    {
        using var client = _factory.CreateClient();

        using var unauthenticated = await client.PostAsJsonAsync("/api/v1/settings/transcription", new
        {
            values = new { provider = "none" }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var token = await ReadTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var invalid = await client.PostAsJsonAsync("/api/v1/settings/transcription", new
        {
            values = new { provider = "none" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("Transcription is required", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AlertsSavePreservesExistingRulesWhenLegacyPayloadOmitsOrDefaultsThem()
    {
        using var client = _factory.CreateClient();
        var token = await ReadTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var initial = await client.PostAsJsonAsync("/api/v1/settings/alerts", new
        {
            values = new
            {
                emailEnabled = false,
                emailProvider = "gmail",
                rules = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        name = "Mahan Gap",
                        enabled = true,
                        matchType = "keyword",
                        keywords = "mahan gap, harrison pike",
                        policeCodes = "",
                        email = "",
                        frequency = "realtime",
                        autoplay = true,
                        talkgroups = Array.Empty<long>()
                    }
                },
                _rulesExplicit = true
            }
        });
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

        using var legacyMissingRules = await client.PostAsJsonAsync("/api/v1/settings/alerts", new
        {
            values = new
            {
                emailEnabled = false,
                emailProvider = "gmail",
                playback = new { enabled = true }
            }
        });
        Assert.Equal(HttpStatusCode.OK, legacyMissingRules.StatusCode);

        using var legacyDefaultedRules = await client.PostAsJsonAsync("/api/v1/settings/alerts", new
        {
            values = new
            {
                emailEnabled = false,
                emailProvider = "gmail",
                rules = Array.Empty<object>(),
                playback = new { enabled = true }
            }
        });
        Assert.Equal(HttpStatusCode.OK, legacyDefaultedRules.StatusCode);

        using var response = await client.GetAsync("/api/v1/settings/alerts");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rules = json.GetProperty("values").GetProperty("rules");
        Assert.Equal(1, rules.GetArrayLength());
        Assert.Equal("Mahan Gap", rules[0].GetProperty("name").GetString());
        Assert.True(json.GetProperty("values").GetProperty("playback").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task AlertsSaveCanExplicitlyClearRulesFromCurrentUi()
    {
        using var client = _factory.CreateClient();
        var token = await ReadTokenAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var initial = await client.PostAsJsonAsync("/api/v1/settings/alerts", new
        {
            values = new
            {
                emailEnabled = false,
                emailProvider = "gmail",
                rules = new[]
                {
                    new
                    {
                        id = Guid.NewGuid(),
                        name = "Temporary",
                        enabled = true,
                        matchType = "keyword",
                        keywords = "temporary",
                        policeCodes = "",
                        email = "",
                        frequency = "realtime",
                        autoplay = false,
                        talkgroups = Array.Empty<long>()
                    }
                },
                _rulesExplicit = true
            }
        });
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);

        using var clear = await client.PostAsJsonAsync("/api/v1/settings/alerts", new
        {
            values = new
            {
                emailEnabled = false,
                emailProvider = "gmail",
                rules = Array.Empty<object>(),
                _rulesExplicit = true
            }
        });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        using var response = await client.GetAsync("/api/v1/settings/alerts");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, json.GetProperty("values").GetProperty("rules").GetArrayLength());
    }

    private static async Task<string> ReadTokenAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/settings/auth/token");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetString() ?? throw new InvalidOperationException("Test host did not expose a token.");
    }
}

public sealed class PizzadApiFactory : WebApplicationFactory<Program>, IDisposable
{
    private readonly string _root;
    private readonly string? _previousConfig;

    public PizzadApiFactory()
    {
        _root = Path.Combine(Path.GetTempPath(), $"pizzawave-api-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        var configPath = Path.Combine(_root, "pizzad.json");
        var config = new EngineConfig
        {
            Branding = new BrandingConfig { StackName = "PizzaWave Test" },
            Server = new ServerConfig { HttpBind = "127.0.0.1", HttpPort = 18080 },
            Storage = new StorageConfig
            {
                AppDataRoot = Path.Combine(_root, "appdata"),
                AudioRoot = Path.Combine(_root, "audio"),
                DatabasePath = Path.Combine(_root, "pizzad.db")
            },
            Auth = new AuthConfig
            {
                Mode = "token",
                ReadRequiresAuth = false,
                WriteRequiresAuth = true,
                TokenFile = Path.Combine(_root, "pizzad.token")
            },
            Ingest = new IngestConfig { CallstreamBind = "127.0.0.1", CallstreamPort = 19123 },
            Transcription = new TranscriptionConfig { Provider = "none" },
            AiInsights = new AiInsightsConfig { Enabled = false },
            Embeddings = new EmbeddingsConfig { Enabled = false },
            TrunkRecorder = new TrunkRecorderConfig
            {
                ConfigPath = Path.Combine(_root, "trunk-recorder.json"),
                TalkgroupCatalogPath = Path.Combine(_root, "talkgroups.json"),
                TalkgroupsPath = Path.Combine(_root, "talkgroups.csv"),
                LogServiceName = "trunk-recorder"
            },
            Setup = new SetupConfig { Completed = false }
        };
        Directory.CreateDirectory(config.Storage.AppDataRoot);
        Directory.CreateDirectory(config.Storage.AudioRoot);
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, EngineConfig.JsonOptions()) + Environment.NewLine, Encoding.UTF8);

        _previousConfig = Environment.GetEnvironmentVariable("PIZZAD_CONFIG");
        Environment.SetEnvironmentVariable("PIZZAD_CONFIG", configPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("PIZZAD_CONFIG", _previousConfig);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
