using Renci.SshNet;
using pizzalib;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.IO.Compression;

namespace pizzad;

public sealed class SettingsValidationService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private static readonly SemaphoreSlim ModelDownloadLock = new(1, 1);

    private static readonly Dictionary<string, string> WhisperModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tiny"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
        ["base"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
        ["small"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
        ["medium"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin"
    };

    private static readonly Dictionary<string, string> VoskModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["small-en-us"] = "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"
    };

    public SettingsValidationService(EngineConfig config, EngineDatabase database)
    {
        _config = config;
        _database = database;
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
        var whisper = WhisperModels.Keys.Select(name => ModelRow("whisper", name, $"Whisper {name}", WhisperModelPath(name), File.Exists(WhisperModelPath(name)) ? new FileInfo(WhisperModelPath(name)).Length : 0));
        var vosk = VoskModels.Keys.Select(name => ModelRow("vosk", name, $"Vosk {name}", VoskModelPath(name), Directory.Exists(VoskModelPath(name)) ? DirectorySize(VoskModelPath(name)) : 0));
        return whisper.Concat(vosk);
    }

    public async Task<object> DownloadWhisperModelAsync(string model, CancellationToken ct)
    {
        if (model.StartsWith("vosk-", StringComparison.OrdinalIgnoreCase))
            return await DownloadVoskModelAsync(model["vosk-".Length..], ct);
        if (model.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase))
            model = model["whisper-".Length..];
        if (!WhisperModels.TryGetValue(model, out var url))
            return new { ok = false, message = "Unknown model." };

        Directory.CreateDirectory(WhisperModelDirectory());
        var path = WhisperModelPath(model);
        var tmp = $"{path}.download";
        if (File.Exists(path))
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
            return new { ok = true, message = $"Whisper {model} is already downloaded.", path };
        }

        if (!await ModelDownloadLock.WaitAsync(0, ct))
            return new { ok = false, message = "Another model download is already running. Wait for it to finish before starting another." };

        try
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Open(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                await input.CopyToAsync(output, ct);
            }
            File.Move(tmp, path, overwrite: true);
            return new { ok = true, message = $"Downloaded Whisper {model}.", path };
        }
        catch
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
            throw;
        }
        finally
        {
            ModelDownloadLock.Release();
        }
    }

    public object DeleteWhisperModel(string model)
    {
        if (model.StartsWith("vosk-", StringComparison.OrdinalIgnoreCase))
            return DeleteVoskModel(model["vosk-".Length..]);
        if (model.StartsWith("whisper-", StringComparison.OrdinalIgnoreCase))
            model = model["whisper-".Length..];
        if (!WhisperModels.ContainsKey(model))
            return new { ok = false, message = "Unknown model." };
        var path = WhisperModelPath(model);
        if (File.Exists(path))
            File.Delete(path);
        return new { ok = true, message = $"Removed Whisper {model}.", path };
    }

    public async Task<object> ListAiInsightModelsAsync(CancellationToken ct)
    {
        var baseUrl = InsightBaseUrl().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new { ok = false, message = "LM Link/OpenAI-compatible base URL is required.", models = Array.Empty<string>() };
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await client.GetAsync($"{baseUrl}/models", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return new { ok = false, message = $"HTTP {(int)response.StatusCode}: {Trim(text, 300)}", models = Array.Empty<string>() };
        using var doc = JsonDocument.Parse(text);
        var models = new List<string>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray())
                if (row.TryGetProperty("id", out var id) && !string.IsNullOrWhiteSpace(id.GetString()))
                    models.Add(id.GetString()!);
        }
        return new { ok = true, message = models.Count == 0 ? "Endpoint responded, but no models were listed." : $"Found {models.Count} model(s).", models };
    }

    private async Task<object> TestTranscriptionAsync(CancellationToken ct)
    {
        var provider = (_config.Transcription.Provider ?? "none").Trim().ToLowerInvariant();
        if (provider == "none")
            return new { ok = true, message = "Transcription is disabled." };

        if (provider == "whisper")
        {
            var path = ResolveWhisperPath();
            if (!File.Exists(path))
                return new { ok = false, message = $"Whisper model not found: {path}" };
            var call = await FindRecentAudioCallAsync(ct);
            if (call == null)
                return new { ok = true, message = $"Whisper is configured with {Path.GetFileName(path)}. No calls are available yet, so a live transcription sample was skipped." };
            var testAudioPath = await PrepareTestAudioAsync(call, ct);
            var started = DateTime.UtcNow;
            using var transcriber = new pizzalib.Whisper(LocalSettings("whisper"));
            await transcriber.Initialize();
            var text = await transcriber.TranscribeCall(await LoadAudioAsync(testAudioPath, ct));
            return TranscriptionResult(call, text, started);
        }
        if (provider == "vosk")
        {
            var path = _config.Transcription.VoskModelPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return new { ok = false, message = $"Vosk model directory not found: {path}" };
            var call = await FindRecentAudioCallAsync(ct);
            if (call == null)
                return new { ok = true, message = $"Vosk is configured with {path}. No calls are available yet, so a live transcription sample was skipped." };
            var testAudioPath = await PrepareTestAudioAsync(call, ct);
            var started = DateTime.UtcNow;
            using var transcriber = new pizzalib.VoskTranscriber(LocalSettings("vosk"));
            await transcriber.Initialize();
            var text = await transcriber.TranscribeCall(await LoadAudioAsync(testAudioPath, ct));
            return TranscriptionResult(call, text, started);
        }
        if (provider is "lmstudio" or "openai")
        {
            var call = await FindRecentAudioCallAsync(ct);
            if (call == null)
            {
                var endpoint = await TestOpenAiEndpointAsync(_config.Transcription.OpenAiBaseUrl, _config.Transcription.OpenAiApiKey, string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel) ? "whisper-1" : _config.Transcription.OpenAiModel, audio: true, ct);
                return endpoint;
            }
            var testAudioPath = await PrepareTestAudioAsync(call, ct);
            return await TestOpenAiTranscriptionAsync(call, testAudioPath, ct);
        }
        return new { ok = false, message = $"Unknown transcription provider: {provider}" };
    }

    private async Task<string> PrepareTestAudioAsync(EngineCall call, CancellationToken ct)
    {
        var audioPath = Path.GetFullPath(Path.Combine(_config.Storage.AudioRoot, call.AudioPath));
        if (!File.Exists(audioPath))
            throw new FileNotFoundException($"Recent call audio file was not found: {audioPath}");
        return await CreateTranscriptionTestAudioAsync(call, audioPath, ct);
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
            return new { ok = true, message = "Endpoint format is configured." };
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
    private string VoskModelDirectory() => Path.Combine(_config.Storage.AppDataRoot, "pizzawave", "model");
    private string VoskModelPath(string model) => Path.Combine(VoskModelDirectory(), model == "small-en-us" ? "vosk-model-small-en-us-0.15" : model);
    private string ResolveWhisperPath()
    {
        if (!string.IsNullOrWhiteSpace(_config.Transcription.WhisperModelFile))
            return _config.Transcription.WhisperModelFile;

        foreach (var model in new[] { "base", "small", "tiny", "medium" })
        {
            var path = WhisperModelPath(model);
            if (File.Exists(path))
                return path;
        }

        return WhisperModelPath("base");
    }
    private string InsightBaseUrl() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl) ? _config.Transcription.OpenAiBaseUrl : _config.AiInsights.OpenAiBaseUrl;
    private string InsightApiKey() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiApiKey) ? _config.Transcription.OpenAiApiKey : _config.AiInsights.OpenAiApiKey;
    private string InsightModel() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel) ? _config.Transcription.OpenAiModel : _config.AiInsights.OpenAiModel;
    private static string NormalizeRemoteRoot(string value) => string.IsNullOrWhiteSpace(value) ? "/" : value.Replace('\\', '/');
    private static string Trim(string text, int max) => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

    private static object ModelRow(string engine, string id, string label, string path, long bytes)
    {
        var downloading = engine == "whisper" && File.Exists($"{path}.download");
        var installed = engine == "whisper" ? File.Exists(path) : Directory.Exists(path);
        var displayBytes = installed
            ? bytes
            : downloading
                ? new FileInfo($"{path}.download").Length
                : 0;
        return new
        {
            id = $"{engine}-{id}",
            engine,
            label,
            path,
            installed,
            downloading,
            bytes = displayBytes
        };
    }

    private async Task<object> DownloadVoskModelAsync(string model, CancellationToken ct)
    {
        if (!VoskModels.TryGetValue(model, out var url))
            return new { ok = false, message = "Unknown Vosk model." };
        Directory.CreateDirectory(VoskModelDirectory());
        var zip = Path.Combine(VoskModelDirectory(), $"{model}.zip.download");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        await using (var input = await client.GetStreamAsync(url, ct))
        await using (var output = File.Open(zip, FileMode.Create, FileAccess.Write, FileShare.None))
            await input.CopyToAsync(output, ct);
        var path = VoskModelPath(model);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        ZipFile.ExtractToDirectory(zip, VoskModelDirectory(), overwriteFiles: true);
        File.Delete(zip);
        return new { ok = true, message = $"Downloaded Vosk {model}.", path };
    }

    private object DeleteVoskModel(string model)
    {
        if (!VoskModels.ContainsKey(model))
            return new { ok = false, message = "Unknown Vosk model." };
        var path = VoskModelPath(model);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        return new { ok = true, message = $"Removed Vosk {model}.", path };
    }

    private async Task<EngineCall?> FindRecentAudioCallAsync(CancellationToken ct)
    {
        var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var days in new[] { 1, 7, 30 })
        {
            var calls = await _database.ListCallsAsync(end - days * 86400L, end, null, ct);
            var candidates = calls
                .Where(c => !string.IsNullOrWhiteSpace(c.AudioPath) && File.Exists(Path.Combine(_config.Storage.AudioRoot, c.AudioPath)))
                .ToList();
            var call = candidates
                .Select(c => new { Call = c, Score = TranscriptionTestSampleScore(c) })
                .Where(row => row.Score > 0)
                .OrderByDescending(row => row.Score)
                .ThenByDescending(row => row.Call.StartTime)
                .Select(row => row.Call)
                .FirstOrDefault() ?? candidates.FirstOrDefault();
            if (call != null)
                return call;
        }
        return null;
    }

    private static int TranscriptionTestSampleScore(EngineCall call)
    {
        var text = (call.Transcription ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var lower = text.ToLowerInvariant();
        if (lower.Contains("[inaudible]") || lower.Contains("[blank_audio]") || lower.Contains("[silence]") || lower.Contains("no speech"))
            return 0;

        var letters = text.Count(char.IsLetter);
        var digits = text.Count(char.IsDigit);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(word => word.Count(char.IsLetter) >= 3);
        var significant = Math.Max(1, letters + digits);
        var alphaRatio = letters / (double)significant;
        var digitRatio = digits / (double)significant;
        if (letters < 20 || words < 4 || alphaRatio < 0.55 || digitRatio > 0.30)
            return 0;

        var duration = Math.Max(0, call.StopTime - call.StartTime);
        var score = letters + words * 12;
        if (string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase))
            score += 1000;
        if (string.Equals(call.TranscriptionStatus, "done", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(call.TranscriptionStatus, "completed", StringComparison.OrdinalIgnoreCase))
            score += 100;
        if (duration is >= 3 and <= 60)
            score += 75;
        return score;
    }

    private async Task<string> CreateTranscriptionTestAudioAsync(EngineCall call, string inputPath, CancellationToken ct)
    {
        var dir = Path.Combine(_config.Storage.AppDataRoot, "settings-tests");
        Directory.CreateDirectory(dir);
        var outputPath = Path.Combine(dir, $"call-{call.Id}-16k.wav");
        if (!File.Exists(outputPath) || File.GetLastWriteTimeUtc(outputPath) < File.GetLastWriteTimeUtc(inputPath))
            await RunFfmpegAsync(inputPath, outputPath, ["-ar", "16000", "-ac", "1"], ct);
        return outputPath;
    }

    private static async Task RunFfmpegAsync(string inputPath, string outputPath, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath })
            psi.ArgumentList.Add(arg);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start ffmpeg.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(ct));
    }

    private Settings LocalSettings(string engine) => new()
    {
        TranscriptionEngine = engine,
        WhisperModelFile = ResolveWhisperPath(),
        VoskModelPath = _config.Transcription.VoskModelPath
    };

    private static async Task<MemoryStream> LoadAudioAsync(string path, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return new MemoryStream(bytes);
    }

    private object TranscriptionResult(EngineCall call, string text, DateTime started)
    {
        text = (text ?? string.Empty).Trim();
        var elapsed = DateTime.UtcNow - started;
        return new
        {
            ok = !string.IsNullOrWhiteSpace(text),
            message = string.IsNullOrWhiteSpace(text)
                ? $"Test call {FormatCallContext(call)} produced an empty transcription in {elapsed.TotalSeconds:0.0}s."
                : $"Test call {FormatCallContext(call)} transcribed in {elapsed.TotalSeconds:0.0}s: {Trim(text, 160)}"
        };
    }

    private static string FormatCallContext(EngineCall call)
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");
        var tg = string.IsNullOrWhiteSpace(call.TalkgroupName) ? call.Talkgroup.ToString() : call.TalkgroupName;
        return $"{call.Id} ({when}, {call.Category}, {tg})";
    }

    private async Task<object> TestOpenAiTranscriptionAsync(EngineCall call, string audioPath, CancellationToken ct)
    {
        var baseUrl = (_config.Transcription.OpenAiBaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return new { ok = false, message = "Base URL is required." };
        var model = string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel) ? "whisper-1" : _config.Transcription.OpenAiModel;
        var started = DateTime.UtcNow;
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(_config.Transcription.OpenAiApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Transcription.OpenAiApiKey);
        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(await File.ReadAllBytesAsync(audioPath, ct));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", Path.GetFileName(audioPath));
        content.Add(new StringContent(model), "model");
        using var response = await client.PostAsync($"{baseUrl}/audio/transcriptions", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return new { ok = false, message = $"HTTP {(int)response.StatusCode}: {Trim(json, 300)}" };
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.TryGetProperty("text", out var prop) ? prop.GetString() ?? string.Empty : json;
        return TranscriptionResult(call, text, started);
    }

    private static long DirectorySize(string path) =>
        Directory.Exists(path) ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) : 0;
}
