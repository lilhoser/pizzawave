using Newtonsoft.Json.Linq;
using pizzalib;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace pizzad;

public sealed class EnginePipeline
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly EngineAlertService _alerts;
    private readonly TalkgroupResolver _talkgroups;
    private readonly ILogger<EnginePipeline> _logger;
    private readonly ConcurrentQueue<(long CallId, RawCallData Raw, bool Imported)> _transcriptionQueue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly SemaphoreSlim _transcriptionLock = new(1, 1);
    private readonly Settings _pizzalibSettings;
    private ITranscriber? _transcriber;

    public int QueueDepth => _transcriptionQueue.Count;

    public EnginePipeline(
        EngineConfig config,
        EngineDatabase database,
        EventStream events,
        EngineAlertService alerts,
        TalkgroupResolver talkgroups,
        ILogger<EnginePipeline> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _alerts = alerts;
        _talkgroups = talkgroups;
        _logger = logger;
        _pizzalibSettings = BuildPizzalibSettings(config);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var provider = (_config.Transcription.Provider ?? "none").Trim().ToLowerInvariant();
        if (provider is "whisper" or "vosk")
        {
            _transcriber = provider == "vosk"
                ? new VoskTranscriber(_pizzalibSettings)
                : new pizzalib.Whisper(_pizzalibSettings);
            await _transcriber.Initialize();
            _logger.LogInformation("Transcription provider initialized: {Provider}", provider);
        }

        _ = Task.Run(() => TranscriptionLoopAsync(ct), ct);
    }

    public async Task<long> IngestRawCallAsync(RawCallData raw, bool imported, CancellationToken ct)
    {
        var metadata = raw.GetJsonObject();
        var call = BuildPendingCall(metadata, raw.GetJsonString(), imported);
        var callId = await _database.UpsertCallAsync(call, ct);

        var audioPath = await PersistAudioAsync(raw, call, callId, ct);
        call = call with { Id = callId, AudioPath = audioPath };
        await _database.UpsertCallAsync(call, ct);

        await _events.PublishAsync("call_ingested", new { callId, imported }, ct);

        _transcriptionQueue.Enqueue((callId, raw, imported));
        _queueSignal.Release();
        await _events.PublishAsync("job_updated", new { type = "transcription", queueDepth = QueueDepth }, ct);
        return callId;
    }

    private async Task TranscriptionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _queueSignal.WaitAsync(ct);
            if (!_transcriptionQueue.TryDequeue(out var item))
                continue;

            try
            {
                var transcription = await TranscribeAsync(item.Raw, ct);
                var call = await _database.GetCallAsync(item.CallId, ct);
                if (call == null)
                    continue;

                var alert = _alerts.Evaluate(call, transcription, item.Imported);
                await _database.UpdateCallTranscriptionAsync(item.CallId, transcription, "complete", alert.IsMatch, ct);

                if (alert.IsMatch)
                {
                    await _database.AddAlertMatchAsync(new AlertMatchDto
                    {
                        CallId = item.CallId,
                        RuleName = alert.RuleName,
                        Detail = $"{alert.Type}:{alert.Detail}",
                        MatchedAt = call.StartTime,
                        IsImported = item.Imported,
                        NotificationSuppressed = item.Imported
                    }, ct);
                    await _events.PublishAsync("alert_matched", new { callId = item.CallId, imported = item.Imported }, ct);
                }

                await _events.PublishAsync("call_transcribed", new { callId = item.CallId, imported = item.Imported }, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Transcription failed for call {CallId}", item.CallId);
                await _database.UpdateCallTranscriptionAsync(item.CallId, string.Empty, "failed", false, ct);
            }
            finally
            {
                item.Raw.Dispose();
            }
        }
    }

    private async Task<string> TranscribeAsync(RawCallData raw, CancellationToken ct)
    {
        var provider = (_config.Transcription.Provider ?? "none").Trim().ToLowerInvariant();
        if (provider == "none")
            return string.Empty;

        if (provider is "whisper" or "vosk")
        {
            if (_transcriber == null)
                return string.Empty;

            await _transcriptionLock.WaitAsync(ct);
            try
            {
                using var wav = await raw.GetAudioStreamForWhisperAsync();
                return await _transcriber.TranscribeCall(wav);
            }
            finally
            {
                _transcriptionLock.Release();
            }
        }

        if (provider is "lmstudio" or "openai")
        {
            using var wav = await raw.GetAudioStreamForWhisperAsync();
            return await TranscribeOpenAiCompatibleAsync(wav, ct);
        }

        return string.Empty;
    }

    private async Task<string> TranscribeOpenAiCompatibleAsync(MemoryStream wav, CancellationToken ct)
    {
        var baseUrl = (_config.Transcription.OpenAiBaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return string.Empty;

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(_config.Transcription.OpenAiApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Transcription.OpenAiApiKey);

        using var content = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav.ToArray());
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audio, "file", "call.wav");
        content.Add(new StringContent(string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel) ? "whisper-1" : _config.Transcription.OpenAiModel), "model");

        using var response = await client.PostAsync($"{baseUrl}/audio/transcriptions", content, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("text", out var text))
            return text.GetString() ?? string.Empty;
        return json;
    }

    private async Task<string> PersistAudioAsync(RawCallData raw, EngineCall call, long callId, CancellationToken ct)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).UtcDateTime;
        var relativeDir = Path.Combine(date.Year.ToString("0000"), date.Month.ToString("00"), date.Day.ToString("00"));
        var absoluteDir = Path.Combine(_config.Storage.AudioRoot, relativeDir);
        Directory.CreateDirectory(absoluteDir);

        var fileName = $"{call.StartTime}-{call.SystemShortName}-{call.Talkgroup}-{call.CallstreamCallId}-{callId}.wav";
        var absolutePath = Path.Combine(absoluteDir, SanitizeFileName(fileName));
        using var wav = await raw.GetAudioStreamAsync(OutputFileFormat.Wav);
        await File.WriteAllBytesAsync(absolutePath, wav.ToArray(), ct);
        return Path.GetRelativePath(_config.Storage.AudioRoot, absolutePath).Replace('\\', '/');
    }

    private EngineCall BuildPendingCall(JObject json, string rawJson, bool imported)
    {
        var start = json.Value<long?>("StartTime") ?? 0;
        var stop = json.Value<long?>("StopTime") ?? start;
        var system = json.Value<string>("SystemShortName")?.Trim() ?? "unknown";
        var talkgroup = json.Value<long?>("Talkgroup") ?? 0;
        var callstreamCallId = json.Value<long?>("CallId") ?? 0;
        var source = json.Value<int?>("Source") ?? -1;
        var frequency = json.Value<double?>("Frequency") ?? 0;
        var resolved = _talkgroups.Resolve(talkgroup);
        var unique = $"{system}|{talkgroup}|{start}|{stop}|{callstreamCallId}|{frequency}";
        return new EngineCall
        {
            UniqueKey = unique,
            StartTime = start,
            StopTime = stop,
            Source = source,
            SystemShortName = system,
            CallstreamCallId = callstreamCallId,
            Talkgroup = talkgroup,
            TalkgroupName = resolved.Label,
            Frequency = frequency,
            Category = resolved.Category,
            TranscriptionStatus = "pending",
            IsImported = imported,
            RawMetadataJson = rawJson
        };
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '-');
        return fileName;
    }

    private static Settings BuildPizzalibSettings(EngineConfig config)
    {
        var settings = new Settings
        {
            listenPort = config.Ingest.CallstreamPort,
            analogSamplingRate = config.Transcription.AnalogSampleRate,
            transcriptionEngine = config.Transcription.Provider is "vosk" ? "vosk" : "whisper",
            whisperModelFile = config.Transcription.WhisperModelFile,
            voskModelPath = config.Transcription.VoskModelPath,
            EmailProvider = config.Alerts.EmailProvider,
            emailUser = config.Alerts.EmailUser,
            emailPassword = config.Alerts.EmailPassword
        };
        return settings;
    }
}
