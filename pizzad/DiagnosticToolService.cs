using pizzalib;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class DiagnosticToolService
{
    private static readonly Regex BadTranscriptPattern = new(@"\[(?:\s*)(?:inaudible|blank_audio|blank audio|silence|no audio)(?:\s*)\]|\binaudible\b|^\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly ILogger<DiagnosticToolService> _logger;

    public DiagnosticToolService(EngineConfig config, EngineDatabase database, EventStream events, ILogger<DiagnosticToolService> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _logger = logger;
    }

    public async Task<JobDto> StartAudioExperimentAsync(DiagnosticToolRequest request, CancellationToken ct)
    {
        var calls = await ResolveCallsAsync(request, ct);
        var jobId = await CreateJobAsync("diagnostic_audio_experiment", calls.Count, "Audio cleanup experiment queued.", ct);
        _ = Task.Run(() => RunAudioExperimentAsync(jobId, calls, CancellationToken.None));
        await _events.PublishAsync("job_updated", new { jobId, type = "diagnostic_audio_experiment", status = "queued" }, ct);
        return (await _database.GetJobAsync(jobId, ct))!;
    }

    public async Task<JobDto> StartTranscriptionBakeoffAsync(DiagnosticToolRequest request, CancellationToken ct)
    {
        var calls = await ResolveCallsAsync(request, ct);
        var models = NormalizeModels(request.Models);
        var jobId = await CreateJobAsync("diagnostic_transcription_bakeoff", calls.Count * models.Count, "Transcription model bakeoff queued.", ct);
        _ = Task.Run(() => RunBakeoffAsync(jobId, calls, models, CancellationToken.None));
        await _events.PublishAsync("job_updated", new { jobId, type = "diagnostic_transcription_bakeoff", status = "queued" }, ct);
        return (await _database.GetJobAsync(jobId, ct))!;
    }

    public IReadOnlyList<DiagnosticModelDto> ListDiagnosticModels()
    {
        return DiscoverModels()
            .Select(m => new DiagnosticModelDto(m.Id, m.Label, m.Engine, true, m.Detail))
            .ToList();
    }

    public async Task<JobDto> StartUnifiedTranscriptionExperimentAsync(DiagnosticToolRequest request, CancellationToken ct)
    {
        var calls = await ResolveCallsAsync(request with { SampleCount = request.SampleCount ?? 50 }, ct);
        var models = DiscoverModels();
        if (models.Count == 0)
            throw new InvalidOperationException("No downloaded local transcription models or configured LM/OpenAI-compatible transcription model were found.");
        var variantsPerCall = 4;
        var total = calls.Count * models.Count * variantsPerCall;
        var jobId = await CreateJobAsync("diagnostic_transcription_experiment", total, $"Transcription experiment queued for {calls.Count:N0} calls, {models.Count:N0} model(s), and {variantsPerCall:N0} audio variant(s).", ct);
        _ = Task.Run(() => RunUnifiedTranscriptionExperimentAsync(jobId, calls, models, total, CancellationToken.None));
        await _events.PublishAsync("job_updated", new { jobId, type = "diagnostic_transcription_experiment", status = "queued" }, ct);
        return (await _database.GetJobAsync(jobId, ct))!;
    }

    public Task<DiagnosticToolResultDto?> GetResultAsync(long jobId, CancellationToken ct) =>
        _database.GetDiagnosticResultAsync(jobId, ct);

    private async Task<long> CreateJobAsync(string type, int total, string message, CancellationToken ct)
    {
        return await _database.AddJobAsync(new JobDto
        {
            Type = type,
            Status = "queued",
            Total = total,
            Message = message,
            CreatedAtUtc = DateTime.UtcNow
        }, ct);
    }

    private async Task<List<EngineCall>> ResolveCallsAsync(DiagnosticToolRequest request, CancellationToken ct)
    {
        var max = Math.Clamp(request.SampleCount ?? 50, 1, 50);
        if (request.CallIds is { Count: > 0 })
        {
            var selected = new List<EngineCall>();
            foreach (var id in request.CallIds.Distinct().Take(50))
            {
                var call = await _database.GetCallAsync(id, ct);
                if (call != null && !string.IsNullOrWhiteSpace(call.AudioPath))
                    selected.Add(call);
            }
            if (selected.Count == 0)
                throw new InvalidOperationException("No matching calls with audio were found.");
            return selected;
        }

        var range = new TimeRangeQuery(request.Start, request.End).Resolve();
        var calls = await _database.ListCallsAsync(range.Start, range.End, null, ct);
        var problem = calls
            .Where(c => !string.IsNullOrWhiteSpace(c.AudioPath) && TranscriptionQualityClassifier.IsProblem(c))
            .OrderByDescending(c => c.QualityReason is "inaudible" or "blank_audio" or "marker_only")
            .ThenByDescending(c => c.StopTime - c.StartTime)
            .ToList();
        var fallback = calls
            .Where(c => !string.IsNullOrWhiteSpace(c.AudioPath) && !problem.Any(p => p.Id == c.Id))
            .OrderByDescending(c => c.StartTime)
            .ToList();
        return problem.Concat(fallback)
            .Where(c => File.Exists(InputPath(c)))
            .Take(max)
            .ToList();
    }

    private async Task RunUnifiedTranscriptionExperimentAsync(long jobId, List<EngineCall> calls, List<DiagnosticTranscriptionModel> models, int total, CancellationToken ct)
    {
        var rows = new List<DiagnosticToolRowDto>();
        var completed = 0;
        try
        {
            await _database.UpdateJobAsync(jobId, "running", total, 0, 0, "Running transcription experiment across models and audio cleanup variants...", true, false, ct);
            foreach (var call in calls)
            {
                var variants = await BuildAudioVariantsAsync(jobId, call, ct);
                foreach (var variant in variants)
                {
                    foreach (var model in models)
                    {
                        rows.Add(await TranscribeWithModelAsync(call.Id, variant.Name, model, variant.Path, variant.AudioUrl, ct));
                        completed++;
                        await _database.UpdateJobAsync(jobId, "running", total, completed, 0, $"Processed {completed:N0}/{total:N0} transcription runs.", false, false, ct);
                        if (completed % 10 == 0)
                            await _events.PublishAsync("job_updated", new { jobId, completed, total }, ct);
                    }
                }
            }

            await SaveCompleteAsync(jobId, "transcription_experiment", rows, $"Transcription experiment finished: {UsefulCount(rows):N0} potentially useful transcription(s).", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic transcription experiment {JobId} failed", jobId);
            await _database.UpdateJobAsync(jobId, "failed", null, completed, 1, ex.Message, false, true, CancellationToken.None);
        }
    }

    private async Task RunAudioExperimentAsync(long jobId, List<EngineCall> calls, CancellationToken ct)
    {
        var rows = new List<DiagnosticToolRowDto>();
        var completed = 0;
        try
        {
            await _database.UpdateJobAsync(jobId, "running", calls.Count, 0, 0, "Running audio cleanup variants...", true, false, ct);
            foreach (var call in calls)
            {
                var variants = await BuildAudioVariantsAsync(jobId, call, ct);
                foreach (var variant in variants)
                {
                    rows.Add(await TranscribeLocalAsync(call.Id, variant.Name, "local-whisper-current", variant.Path, variant.AudioUrl, ct));
                }
                completed++;
                await _database.UpdateJobAsync(jobId, "running", calls.Count, completed, 0, $"Processed {completed:N0}/{calls.Count:N0} calls.", false, false, ct);
                await _events.PublishAsync("job_updated", new { jobId, completed, total = calls.Count }, ct);
            }

            await SaveCompleteAsync(jobId, "audio_experiment", rows, $"Audio experiment finished: {UsefulCount(rows):N0} potentially useful transcription(s).", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic audio experiment {JobId} failed", jobId);
            await _database.UpdateJobAsync(jobId, "failed", null, completed, 1, ex.Message, false, true, CancellationToken.None);
        }
    }

    private async Task RunBakeoffAsync(long jobId, List<EngineCall> calls, List<string> models, CancellationToken ct)
    {
        var rows = new List<DiagnosticToolRowDto>();
        var total = calls.Count * models.Count;
        var completed = 0;
        try
        {
            await _database.UpdateJobAsync(jobId, "running", total, 0, 0, "Running transcription model bakeoff...", true, false, ct);
            foreach (var call in calls)
            {
                var baseline = await CreateBaseline16kAsync(jobId, call, ct);
                foreach (var model in models)
                {
                    rows.Add(model.StartsWith("openai:", StringComparison.OrdinalIgnoreCase)
                        ? await TranscribeOpenAiCompatibleAsync(call.Id, model, baseline.Path, ct)
                        : await TranscribeLocalAsync(call.Id, "baseline_16k", model, baseline.Path, baseline.AudioUrl, ct, model));
                    completed++;
                    await _database.UpdateJobAsync(jobId, "running", total, completed, 0, $"Processed {completed:N0}/{total:N0} model runs.", false, false, ct);
                }
            }

            await SaveCompleteAsync(jobId, "transcription_bakeoff", rows, $"Bakeoff finished: {UsefulCount(rows):N0} potentially useful transcription(s).", ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic transcription bakeoff {JobId} failed", jobId);
            await _database.UpdateJobAsync(jobId, "failed", null, completed, 1, ex.Message, false, true, CancellationToken.None);
        }
    }

    private async Task SaveCompleteAsync(long jobId, string tool, List<DiagnosticToolRowDto> rows, string message, CancellationToken ct)
    {
        await _database.SaveDiagnosticResultAsync(new DiagnosticToolResultDto(jobId, tool, DateTime.UtcNow, rows), ct);
        await _database.UpdateJobAsync(jobId, "completed", rows.Count, rows.Count, 0, message, false, true, ct);
        await _events.PublishAsync("job_updated", new { jobId, status = "completed" }, ct);
    }

    private async Task<List<(string Name, string Path, string AudioUrl)>> BuildAudioVariantsAsync(long jobId, EngineCall call, CancellationToken ct)
    {
        var baseline = await CreateBaseline16kAsync(jobId, call, ct);
        var rows = new List<(string Name, string Path, string AudioUrl)> { baseline };
        var filters = new Dictionary<string, string>
        {
            ["bandpass_norm"] = "highpass=f=180,lowpass=f=3600,loudnorm=I=-18:TP=-1.5:LRA=9",
            ["mild_denoise"] = "highpass=f=150,lowpass=f=3800,afftdn=nf=-20,loudnorm=I=-18:TP=-1.5:LRA=9",
            ["declick_norm"] = "adeclick,highpass=f=150,lowpass=f=3800,loudnorm=I=-18:TP=-1.5:LRA=9"
        };
        foreach (var filter in filters)
        {
            var output = DiagnosticPath(jobId, call.Id, $"{filter.Key}.wav");
            await RunFfmpegAsync(InputPath(call), output, ["-af", filter.Value, "-ar", "16000", "-ac", "1"], ct);
            rows.Add((filter.Key, output, DiagnosticAudioUrl(jobId, call.Id, $"{filter.Key}.wav")));
        }
        return rows;
    }

    private async Task<(string Name, string Path, string AudioUrl)> CreateBaseline16kAsync(long jobId, EngineCall call, CancellationToken ct)
    {
        var output = DiagnosticPath(jobId, call.Id, "baseline_16k.wav");
        if (!File.Exists(output))
            await RunFfmpegAsync(InputPath(call), output, ["-ar", "16000", "-ac", "1"], ct);
        return ("baseline_16k", output, DiagnosticAudioUrl(jobId, call.Id, "baseline_16k.wav"));
    }

    private async Task<DiagnosticToolRowDto> TranscribeLocalAsync(long callId, string variant, string modelLabel, string audioPath, string audioUrl, CancellationToken ct, string? modelPathOrLabel = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var settings = new Settings
            {
                TranscriptionEngine = "whisper",
                WhisperModelFile = ResolveWhisperModelPath(modelPathOrLabel),
                TranscriptionModelPreset = string.Empty
            };
            using var whisper = new pizzalib.Whisper(settings);
            await whisper.Initialize();
            await using var file = File.OpenRead(audioPath);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            ms.Position = 0;
            var text = (await whisper.TranscribeCall(ms)).Trim();
            sw.Stop();
            return Row(callId, variant, modelLabel, text, sw.Elapsed.TotalMilliseconds, audioUrl);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticToolRowDto(callId, variant, modelLabel, "error", 0, sw.Elapsed.TotalMilliseconds, string.Empty, audioUrl, ex.Message);
        }
    }

    private Task<DiagnosticToolRowDto> TranscribeWithModelAsync(long callId, string variant, DiagnosticTranscriptionModel model, string audioPath, string audioUrl, CancellationToken ct)
    {
        return model.Engine switch
        {
            "whisper" => TranscribeWhisperAsync(callId, variant, model, audioPath, audioUrl, ct),
            "vosk" => TranscribeVoskAsync(callId, variant, model, audioPath, audioUrl, ct),
            "openai" => TranscribeOpenAiCompatibleAsync(callId, variant, model, audioPath, audioUrl, ct),
            _ => Task.FromResult(new DiagnosticToolRowDto(callId, variant, model.Label, "error", 0, 0, string.Empty, audioUrl, $"Unknown model engine: {model.Engine}"))
        };
    }

    private async Task<DiagnosticToolRowDto> TranscribeWhisperAsync(long callId, string variant, DiagnosticTranscriptionModel model, string audioPath, string audioUrl, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var settings = new Settings
            {
                TranscriptionEngine = "whisper",
                WhisperModelFile = model.Value,
                TranscriptionModelPreset = string.Empty
            };
            using var whisper = new pizzalib.Whisper(settings);
            await whisper.Initialize();
            await using var file = File.OpenRead(audioPath);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            ms.Position = 0;
            var text = (await whisper.TranscribeCall(ms)).Trim();
            sw.Stop();
            return Row(callId, variant, model.Label, text, sw.Elapsed.TotalMilliseconds, audioUrl);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticToolRowDto(callId, variant, model.Label, "error", 0, sw.Elapsed.TotalMilliseconds, string.Empty, audioUrl, ex.Message);
        }
    }

    private async Task<DiagnosticToolRowDto> TranscribeVoskAsync(long callId, string variant, DiagnosticTranscriptionModel model, string audioPath, string audioUrl, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var settings = new Settings
            {
                TranscriptionEngine = "vosk",
                VoskModelPath = model.Value
            };
            using var vosk = new pizzalib.VoskTranscriber(settings);
            await vosk.Initialize();
            await using var file = File.OpenRead(audioPath);
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            ms.Position = 0;
            var text = (await vosk.TranscribeCall(ms)).Trim();
            sw.Stop();
            return Row(callId, variant, model.Label, text, sw.Elapsed.TotalMilliseconds, audioUrl);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticToolRowDto(callId, variant, model.Label, "error", 0, sw.Elapsed.TotalMilliseconds, string.Empty, audioUrl, ex.Message);
        }
    }

    private async Task<DiagnosticToolRowDto> TranscribeOpenAiCompatibleAsync(long callId, string modelSpec, string audioPath, CancellationToken ct)
    {
        var model = modelSpec["openai:".Length..].Trim();
        if (string.IsNullOrWhiteSpace(model))
            model = string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel) ? "whisper-1" : _config.Transcription.OpenAiModel;
        var sw = Stopwatch.StartNew();
        try
        {
            var baseUrl = (_config.Transcription.OpenAiBaseUrl ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("OpenAI-compatible transcription base URL is not configured.");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            if (!string.IsNullOrWhiteSpace(_config.Transcription.OpenAiApiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Transcription.OpenAiApiKey);
            using var content = new MultipartFormDataContent();
            var audio = new ByteArrayContent(await File.ReadAllBytesAsync(audioPath, ct));
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audio, "file", "call.wav");
            content.Add(new StringContent(model), "model");
            using var response = await client.PostAsync($"{baseUrl}/audio/transcriptions", content, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.TryGetProperty("text", out var prop) ? prop.GetString() ?? string.Empty : json;
            sw.Stop();
            return Row(callId, "baseline_16k", $"openai:{model}", text, sw.Elapsed.TotalMilliseconds, string.Empty);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticToolRowDto(callId, "baseline_16k", $"openai:{model}", "error", 0, sw.Elapsed.TotalMilliseconds, string.Empty, string.Empty, ex.Message);
        }
    }

    private async Task<DiagnosticToolRowDto> TranscribeOpenAiCompatibleAsync(long callId, string variant, DiagnosticTranscriptionModel modelSpec, string audioPath, string audioUrl, CancellationToken ct)
    {
        var model = modelSpec.Value;
        var sw = Stopwatch.StartNew();
        try
        {
            var baseUrl = (modelSpec.Detail ?? string.Empty).TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException("OpenAI-compatible transcription base URL is not configured.");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            var apiKey = string.Equals(baseUrl, (_config.AiInsights.OpenAiBaseUrl ?? string.Empty).TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? _config.AiInsights.OpenAiApiKey
                : _config.Transcription.OpenAiApiKey;
            if (!string.IsNullOrWhiteSpace(apiKey))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var content = new MultipartFormDataContent();
            var audio = new ByteArrayContent(await File.ReadAllBytesAsync(audioPath, ct));
            audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(audio, "file", "call.wav");
            content.Add(new StringContent(model), "model");
            using var response = await client.PostAsync($"{baseUrl}/audio/transcriptions", content, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement.TryGetProperty("text", out var prop) ? prop.GetString() ?? string.Empty : json;
            sw.Stop();
            return Row(callId, variant, modelSpec.Label, text, sw.Elapsed.TotalMilliseconds, audioUrl);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new DiagnosticToolRowDto(callId, variant, modelSpec.Label, "error", 0, sw.Elapsed.TotalMilliseconds, string.Empty, audioUrl, ex.Message);
        }
    }

    private DiagnosticToolRowDto Row(long callId, string variant, string model, string text, double durationMs, string audioUrl)
    {
        var score = Score(text);
        var status = score > 0 ? "potentially_useful" : "not_useful";
        return new DiagnosticToolRowDto(callId, variant, model, status, score, durationMs, text, audioUrl, score > 0 ? string.Empty : "Empty, inaudible, or likely hallucinated.");
    }

    private static int Score(string text)
    {
        if (BadTranscriptPattern.IsMatch(text))
            return 0;
        return Regex.Matches(text, @"[A-Za-z0-9']+").Count;
    }

    private static int UsefulCount(IEnumerable<DiagnosticToolRowDto> rows) =>
        rows.Count(r => r.Score > 0);

    private static List<string> NormalizeModels(IReadOnlyList<string>? models)
    {
        var rows = (models ?? ["local-current"]).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        return rows.Count == 0 ? ["local-current"] : rows;
    }

    private List<DiagnosticTranscriptionModel> DiscoverModels()
    {
        var rows = new List<DiagnosticTranscriptionModel>();
        var modelRoot = Path.Combine(_config.Storage.AppDataRoot, "pizzawave", "model");
        if (Directory.Exists(modelRoot))
        {
            foreach (var file in Directory.EnumerateFiles(modelRoot, "ggml-*.bin").OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileNameWithoutExtension(file).Replace("ggml-", "", StringComparison.OrdinalIgnoreCase);
                rows.Add(new DiagnosticTranscriptionModel($"whisper:{name}", $"Whisper {name}", "whisper", file, file));
            }

            foreach (var dir in Directory.EnumerateDirectories(modelRoot, "vosk-model*").OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileName(dir);
                rows.Add(new DiagnosticTranscriptionModel($"vosk:{name}", $"Vosk {name}", "vosk", dir, dir));
            }
        }

        if (!string.IsNullOrWhiteSpace(_config.Transcription.OpenAiBaseUrl) && !string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel))
        {
            var model = _config.Transcription.OpenAiModel.Trim();
            rows.Add(new DiagnosticTranscriptionModel($"openai:{model}", $"LM/OpenAI {model}", "openai", model, _config.Transcription.OpenAiBaseUrl.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl) && !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel))
        {
            var model = _config.AiInsights.OpenAiModel.Trim();
            rows.Add(new DiagnosticTranscriptionModel($"lmlink:{model}", $"LM Link {model}", "openai", model, _config.AiInsights.OpenAiBaseUrl.Trim()));
        }

        return rows
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private string ResolveWhisperModelPath(string? model)
    {
        if (!string.IsNullOrWhiteSpace(model) && !string.Equals(model, "local-current", StringComparison.OrdinalIgnoreCase))
            return model;
        if (!string.IsNullOrWhiteSpace(_config.Transcription.WhisperModelFile))
            return _config.Transcription.WhisperModelFile;
        return Path.Combine(_config.Storage.AppDataRoot, "pizzawave", "model", "ggml-base.bin");
    }

    private string InputPath(EngineCall call) =>
        Path.GetFullPath(Path.Combine(_config.Storage.AudioRoot, call.AudioPath));

    private string DiagnosticPath(long jobId, long callId, string fileName)
    {
        var dir = Path.Combine(_config.Storage.AppDataRoot, "diagnostics", jobId.ToString(), callId.ToString());
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    private static string DiagnosticAudioUrl(long jobId, long callId, string fileName) =>
        $"/api/v1/troubleshoot/tools/results/{jobId}/audio/{callId}/{Uri.EscapeDataString(fileName)}";

    private async Task RunFfmpegAsync(string input, string output, IReadOnlyList<string> args, CancellationToken ct)
    {
        var allArgs = new List<string> { "-y", "-hide_banner", "-loglevel", "error", "-i", input };
        allArgs.AddRange(args);
        allArgs.Add(output);
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in allArgs)
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start ffmpeg.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(await process.StandardError.ReadToEndAsync(ct));
    }

    public string GetDiagnosticAudioPath(long jobId, long callId, string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(_config.Storage.AppDataRoot, "diagnostics", jobId.ToString()));
        var path = Path.GetFullPath(Path.Combine(root, callId.ToString(), fileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return string.Empty;
        return path;
    }

    private sealed record DiagnosticTranscriptionModel(string Id, string Label, string Engine, string Value, string Detail);
}
