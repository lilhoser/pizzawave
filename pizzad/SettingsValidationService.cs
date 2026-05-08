using Renci.SshNet;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed class SettingsValidationService
{
    private readonly EngineConfig _config;

    private static readonly Dictionary<string, string> WhisperModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiny"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
        ["base"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        ["small"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        ["medium"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin"
    };

    public SettingsValidationService(EngineConfig config)
    {
        _config = config;
    }

    public async Task<object> TestAsync(string section, CancellationToken ct)
    {
        try
        {
            return section.Trim().ToLowerInvariant() switch
            {
                "transcription" => await TestTranscriptionAsync(ct),
                "ai-insights" => await TestAiInsightsAsync(ct),
                "alerts" => TestAlerts(),
                "sftp" => TestSftp(),
                "auth" => new { ok = true, message = "Security settings are valid." },
                _ => new { ok = false, message = "No test is available for this settings section." }
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, message = ex.Message };
        }
    }

    public object ListWhisperModels()
    {
        var dir = WhisperModelDirectory();
        Directory.CreateDirectory(dir);
        var rows = WhisperModels.Keys.Select(name =>
        {
            var path = WhisperModelPath(name);
            var file = new FileInfo(path);
            return new
            {
                id = name,
                label = $"Whisper {name}",
                path,
                installed = file.Exists,
                bytes = file.Exists ? file.Length : 0
            };
        });
        return rows;
    }

    public async Task<object> DownloadWhisperModelAsync(string model, CancellationToken ct)
    {
        if (!WhisperModels.TryGetValue(model, out var url))
            return new { ok = false, message = "Unknown Whisper model." };

        Directory.CreateDirectory(WhisperModelDirectory());
        var path = WhisperModelPath(model);
        var tmp = $"{path}.download";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        await using (var input = await client.GetStreamAsync(url, ct))
        await using (var output = File.Open(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, ct);
        }
        File.Move(tmp, path, overwrite: true);
        return new { ok = true, message = $"Downloaded Whisper {model}.", path };
    }

    public object DeleteWhisperModel(string model)
    {
        if (!WhisperModels.ContainsKey(model))
            return new { ok = false, message = "Unknown Whisper model." };
        var path = WhisperModelPath(model);
        if (File.Exists(path))
            File.Delete(path);
        return new { ok = true, message = $"Removed Whisper {model}.", path };
    }

    private Task<object> TestTranscriptionAsync(CancellationToken ct)
    {
        var provider = (_config.Transcription.Provider ?? "none").Trim().ToLowerInvariant();
        if (provider == "none")
            return Task.FromResult<object>(new { ok = true, message = "Transcription is disabled." });
        if (provider == "whisper")
        {
            var path = ResolveWhisperPath();
            return Task.FromResult<object>(new { ok = File.Exists(path), message = File.Exists(path) ? $"Whisper model found: {path}" : $"Whisper model not found: {path}" });
        }
        if (provider == "vosk")
        {
            var path = _config.Transcription.VoskModelPath;
            return Task.FromResult<object>(new { ok = Directory.Exists(path), message = Directory.Exists(path) ? $"Vosk model directory found: {path}" : $"Vosk model directory not found: {path}" });
        }
        if (provider is "lmstudio" or "openai")
            return TestOpenAiEndpointAsync(_config.Transcription.OpenAiBaseUrl, _config.Transcription.OpenAiApiKey, _config.Transcription.OpenAiModel, audio: true, ct);
        return Task.FromResult<object>(new { ok = false, message = $"Unknown transcription provider: {provider}" });
    }

    private async Task<object> TestAiInsightsAsync(CancellationToken ct)
    {
        if (!_config.AiInsights.Enabled)
            return new { ok = false, message = "AI usage is disabled." };
        return await TestOpenAiEndpointAsync(InsightBaseUrl(), InsightApiKey(), InsightModel(), audio: false, ct);
    }

    private object TestAlerts()
    {
        if (!_config.Alerts.EmailEnabled)
            return new { ok = true, message = "Email alerts are disabled." };
        if (string.IsNullOrWhiteSpace(_config.Alerts.EmailUser) || string.IsNullOrWhiteSpace(_config.Alerts.EmailPassword))
            return new { ok = false, message = "Email address and app password are required." };
        return new { ok = true, message = $"Email settings are present for {_config.Alerts.EmailProvider}." };
    }

    private object TestSftp()
    {
        if (!_config.SftpImport.Enabled)
            return new { ok = true, message = "SFTP import is disabled." };
        using var client = CreateSftpClient();
        client.Connect();
        var exists = client.Exists(NormalizeRemoteRoot(_config.SftpImport.RemoteRoot));
        client.Disconnect();
        return new { ok = exists, message = exists ? "SFTP connection succeeded." : "SFTP connected, but remote root was not found." };
    }

    private async Task<object> TestOpenAiEndpointAsync(string baseUrl, string apiKey, string model, bool audio, CancellationToken ct)
    {
        baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new { ok = false, message = "Base URL is required." };
        if (audio)
            return new { ok = true, message = "Endpoint format is configured. Full audio transcription testing requires a sample call." };
        if (string.IsNullOrWhiteSpace(model))
            return new { ok = false, message = "Model is required." };

        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var body = JsonSerializer.Serialize(new
        {
            model,
            max_tokens = 8,
            messages = new[] { new { role = "user", content = "Reply with OK." } }
        }, EngineConfig.JsonOptions());
        using var response = await client.PostAsync($"{baseUrl}/chat/completions", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return new { ok = response.IsSuccessStatusCode, message = response.IsSuccessStatusCode ? "AI endpoint responded." : $"HTTP {(int)response.StatusCode}: {Trim(text, 300)}" };
    }

    private SftpClient CreateSftpClient()
    {
        var cfg = _config.SftpImport;
        if (string.Equals(cfg.AuthMode, "privateKey", StringComparison.OrdinalIgnoreCase))
        {
            var key = string.IsNullOrWhiteSpace(cfg.PrivateKeyPassphrase)
                ? new PrivateKeyFile(cfg.PrivateKeyPath)
                : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.PrivateKeyPassphrase);
            return new SftpClient(new Renci.SshNet.ConnectionInfo(cfg.Host, cfg.Port, cfg.Username, new PrivateKeyAuthenticationMethod(cfg.Username, key)));
        }
        return new SftpClient(cfg.Host, cfg.Port, cfg.Username, cfg.Password);
    }

    private string WhisperModelDirectory() => Path.Combine(_config.Storage.AppDataRoot, "pizzawave", "model");
    private string WhisperModelPath(string model) => Path.Combine(WhisperModelDirectory(), $"ggml-{model}.bin");
    private string ResolveWhisperPath() => string.IsNullOrWhiteSpace(_config.Transcription.WhisperModelFile) ? WhisperModelPath("base") : _config.Transcription.WhisperModelFile;
    private string InsightBaseUrl() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl) ? _config.Transcription.OpenAiBaseUrl : _config.AiInsights.OpenAiBaseUrl;
    private string InsightApiKey() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiApiKey) ? _config.Transcription.OpenAiApiKey : _config.AiInsights.OpenAiApiKey;
    private string InsightModel() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel) ? _config.Transcription.OpenAiModel : _config.AiInsights.OpenAiModel;
    private static string NormalizeRemoteRoot(string value) => string.IsNullOrWhiteSpace(value) ? "/" : value.Replace('\\', '/');
    private static string Trim(string text, int max) => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];
}
