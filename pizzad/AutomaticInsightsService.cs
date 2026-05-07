using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class AutomaticInsightsService : BackgroundService
{
    private const int DefaultBatchSize = 20;
    private readonly ConcurrentQueue<EngineCall> _queue = new();
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly TalkgroupResolver _talkgroups;
    private readonly ILogger<AutomaticInsightsService> _logger;
    private readonly List<EngineCall> _pending = new();
    private readonly object _gate = new();
    private string? _priorSummary;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private int _failureStreak;
    private bool _backlogSeeded;

    public AutomaticInsightsService(
        EngineConfig config,
        EngineDatabase database,
        EventStream events,
        TalkgroupResolver talkgroups,
        ILogger<AutomaticInsightsService> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _talkgroups = talkgroups;
        _logger = logger;
    }

    public void Enqueue(EngineCall call)
    {
        if (!IsEnabled())
            return;
        _queue.Enqueue(_talkgroups.Enrich(call));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (IsEnabled())
                {
                    await SeedBacklogIfNeededAsync(stoppingToken);
                    DrainQueue();
                    await PumpAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic insights loop failed");
            }

            await Task.Delay(1000, stoppingToken).ContinueWith(_ => { }, CancellationToken.None);
        }
    }

    private async Task SeedBacklogIfNeededAsync(CancellationToken ct)
    {
        if (_backlogSeeded)
            return;

        _backlogSeeded = true;
        var watermark = await _database.GetLatestInsightWindowEndAsync(ct);
        var batchLimit = Math.Max(BatchSize(), 1) * 3;
        var backlog = await _database.ListCompletedCallsAfterAsync(watermark, batchLimit, ct);
        foreach (var call in backlog)
            Enqueue(call);
        if (backlog.Count > 0)
            _logger.LogInformation("Seeded {Count} calls into automatic insights backlog after {Watermark}", backlog.Count, watermark);
    }

    private void DrainQueue()
    {
        lock (_gate)
        {
            while (_queue.TryDequeue(out var call))
            {
                if (_pending.Any(c => c.Id == call.Id))
                    continue;
                _pending.Add(call);
            }

            var max = Math.Max(_config.AiInsights.MaxPendingCalls, BatchSize());
            if (_pending.Count > max)
                _pending.RemoveRange(0, _pending.Count - max);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _nextAttemptAt)
            return;

        List<EngineCall> batch;
        lock (_gate)
        {
            if (_pending.Count < BatchSize())
                return;

            var take = Math.Min(ComputeAdaptiveBatchSize(_pending.Count), _pending.Count);
            batch = _pending.Take(take).ToList();
        }

        if (batch.Count == 0)
            return;

        var start = batch.Min(c => c.StartTime);
        var end = batch.Max(c => Math.Max(c.StartTime, c.StopTime));
        try
        {
            var result = await SummarizeWindowAsync(batch, start, end, ct);
            var windowId = await _database.AddInsightWindowAsync(start, end, result.SummaryText, ct);
            var incidents = 0;
            foreach (var ev in result.Events.Where(IsActionableEvent))
            {
                var calls = ResolveEventCalls(ev, batch);
                if (calls.Count == 0)
                    continue;

                await _database.AddIncidentAsync(new IncidentDto
                {
                    Title = string.IsNullOrWhiteSpace(ev.Title) ? "Radio incident" : ev.Title.Trim(),
                    Detail = string.IsNullOrWhiteSpace(ev.Detail) ? result.SummaryText : ev.Detail.Trim(),
                    FirstSeen = calls.Min(c => c.StartTime),
                    LastSeen = calls.Max(c => c.StartTime),
                    Calls = calls.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio")).ToList()
                }, ct);
                incidents++;
            }

            _priorSummary = result.SummaryText;
            lock (_gate)
            {
                var ids = batch.Select(c => c.Id).ToHashSet();
                _pending.RemoveAll(c => ids.Contains(c.Id));
            }
            _failureStreak = 0;
            _nextAttemptAt = DateTimeOffset.UtcNow;
            await _events.PublishAsync("summary_updated", new { windowId, start, end, incidents }, ct);
            _logger.LogInformation("Automatic insights generated window {WindowId} with {Incidents} incidents from {Calls} calls", windowId, incidents, batch.Count);
        }
        catch (Exception ex)
        {
            _failureStreak++;
            var cooldownSeconds = Math.Min(300, 5 * (1 << Math.Min(_failureStreak, 5)));
            _nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);
            RotateFailedBatch(batch);
            _logger.LogWarning(ex, "Automatic insights failed for {Count} calls; backing off {Seconds}s", batch.Count, cooldownSeconds);
        }
    }

    private async Task<InsightResult> SummarizeWindowAsync(List<EngineCall> calls, long start, long end, CancellationToken ct)
    {
        var baseUrl = InsightBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new
        {
            model = InsightModel(),
            temperature = 0.1,
            max_tokens = 1200,
            response_format = InsightResponseFormat(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You summarize radio call transcripts into concise actionable incidents. Output JSON only with fields summary_text and notable_events. notable_events is an array of objects with title, detail, category, timestamp, confidence, and call_ids. Choose category from police, fire, ems, traffic, public_works, utilities, other. Each event must include 1-3 call_ids copied exactly from input lines, such as C000001234ABC. Do not create an event if you cannot cite at least one exact call_id from the input."
                },
                new { role = "user", content = BuildPrompt(calls, start, end) }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        Exception? last = null;
        for (var attempt = 0; attempt <= Math.Max(0, _config.AiInsights.MaxRetries); attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(endpoint, content, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Automatic insights request failed with HTTP {(int)response.StatusCode}: {Trim(text, 1000)}");
                return ParseResponse(text);
            }
            catch (Exception ex) when (attempt < Math.Max(0, _config.AiInsights.MaxRetries))
            {
                last = ex;
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        throw new InvalidOperationException(last?.Message ?? "Automatic insights request failed.", last);
    }

    private string BuildPrompt(List<EngineCall> calls, long start, long end)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Window: {DateTimeOffset.FromUnixTimeSeconds(start).ToLocalTime():yyyy-MM-dd HH:mm} to {DateTimeOffset.FromUnixTimeSeconds(end).ToLocalTime():yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(_priorSummary))
        {
            sb.AppendLine("Prior summary context:");
            sb.AppendLine(_priorSummary);
            sb.AppendLine();
        }

        var target = Math.Clamp(calls.Count / 4, 1, 8);
        sb.AppendLine($"Return zero notable_events when there is no clear incident. Otherwise, target up to {target} notable_events when evidence supports it. Ignore greetings, empty/inaudible/static-only calls, routine plate/status checks without incident content, and duplicated adjacent radio fragments.");
        sb.AppendLine("Calls:");
        foreach (var call in calls.OrderBy(c => c.StartTime))
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime();
            var line = $"{CallToken(call.Id)} | {local:HH:mm:ss} | {call.SystemShortName} | {Label(call)} | {Trim(call.Transcription, 220)}";
            if (sb.Length + line.Length > 12_000)
                break;
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static InsightResult ParseResponse(string text)
    {
        using var root = JsonDocument.Parse(text);
        var content = text;
        if (root.RootElement.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentElement))
        {
            content = contentElement.ValueKind == JsonValueKind.String
                ? contentElement.GetString() ?? string.Empty
                : contentElement.GetRawText();
        }

        content = StripCodeFence(content);
        using var doc = JsonDocument.Parse(content);
        var summary = doc.RootElement.TryGetProperty("summary_text", out var summaryElement)
            ? summaryElement.GetString() ?? string.Empty
            : string.Empty;
        var events = new List<InsightEvent>();
        if (doc.RootElement.TryGetProperty("notable_events", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(80))
            {
                var ids = new List<string>();
                if (item.TryGetProperty("call_ids", out var callIds) && callIds.ValueKind == JsonValueKind.Array)
                    ids.AddRange(callIds.EnumerateArray().Select(v => v.GetString()).Where(v => !string.IsNullOrWhiteSpace(v))!);

                events.Add(new InsightEvent(
                    GetString(item, "title"),
                    GetString(item, "detail"),
                    GetString(item, "category"),
                    GetString(item, "timestamp"),
                    item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                    ids));
            }
        }
        return new InsightResult(summary, events);
    }

    private List<EngineCall> ResolveEventCalls(InsightEvent ev, List<EngineCall> batch)
    {
        var byToken = new Dictionary<string, EngineCall>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in batch)
        {
            byToken[CallToken(call.Id)] = call;
            byToken[call.Id.ToString(CultureInfo.InvariantCulture)] = call;
            byToken[call.Id.ToString("X12", CultureInfo.InvariantCulture)] = call;
        }

        var resolved = ev.CallIds
            .Select(id => byToken.TryGetValue(id.Trim(), out var call) ? call : null)
            .Where(c => c != null)
            .DistinctBy(c => c!.Id)
            .Cast<EngineCall>()
            .ToList();

        if (resolved.Count > 0)
            return resolved;

        if (DateTimeOffset.TryParse(ev.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp))
        {
            var eventTime = timestamp.ToUnixTimeSeconds();
            return batch
                .Where(c => Math.Abs(c.StartTime - eventTime) <= 90)
                .OrderBy(c => Math.Abs(c.StartTime - eventTime))
                .Take(2)
                .ToList();
        }

        return resolved;
    }

    private static bool IsActionableEvent(InsightEvent ev)
    {
        var title = NormalizeEventText(ev.Title);
        var detail = NormalizeEventText(ev.Detail);
        var combined = $"{title} {detail}".Trim();
        if (string.IsNullOrWhiteSpace(combined))
            return false;

        if (ev.CallIds.Count == 0)
            return false;

        return !Regex.IsMatch(combined,
            @"\b(no|none|not|without|unclear)\b.{0,40}\b(incident|event|actionable|emergency|issue|activity)\b|\b(no event detected|no clear incident|no actionable incident|no notable event|nothing notable|routine traffic only|routine chatter|non.?incident)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeEventText(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private bool IsEnabled() =>
        _config.AiInsights.Enabled &&
        !string.IsNullOrWhiteSpace(InsightBaseUrl()) &&
        !string.IsNullOrWhiteSpace(InsightModel());

    private int BatchSize() => Math.Max(1, _config.AiInsights.BatchSize <= 0 ? DefaultBatchSize : _config.AiInsights.BatchSize);

    private int ComputeAdaptiveBatchSize(int pendingCount)
    {
        if (_failureStreak <= 0)
            return BatchSize();
        return Math.Max(5, BatchSize() / (1 << Math.Min(_failureStreak, 3)));
    }

    private void RotateFailedBatch(List<EngineCall> failedBatch)
    {
        lock (_gate)
        {
            var ids = failedBatch.Select(c => c.Id).ToHashSet();
            var moved = _pending.Where(c => ids.Contains(c.Id)).ToList();
            _pending.RemoveAll(c => ids.Contains(c.Id));
            _pending.AddRange(moved);
        }
    }

    private string InsightBaseUrl() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl)
        ? _config.Transcription.OpenAiBaseUrl
        : _config.AiInsights.OpenAiBaseUrl;

    private string InsightApiKey() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiApiKey)
        ? _config.Transcription.OpenAiApiKey
        : _config.AiInsights.OpenAiApiKey;

    private string InsightModel() => string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel)
        ? _config.Transcription.OpenAiModel
        : _config.AiInsights.OpenAiModel;

    private static string Label(EngineCall call) => string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"TG {call.Talkgroup}" : call.TalkgroupName;

    private static string CallToken(long id) => $"C{id:X12}";

    private static string Trim(string value, int max) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : value.Length <= max ? value : value[..max];

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static string StripCodeFence(string content) =>
        Regex.Replace(content.Trim(), "^```(?:json)?\\s*|\\s*```$", string.Empty, RegexOptions.IgnoreCase);

    private static object InsightResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "pizzawave_insight_result",
            strict = false,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    summary_text = new { type = "string" },
                    notable_events = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                title = new { type = "string" },
                                detail = new { type = "string" },
                                category = new { type = "string" },
                                timestamp = new { type = "string" },
                                confidence = new { type = "number" },
                                call_ids = new
                                {
                                    type = "array",
                                    items = new { type = "string" }
                                }
                            },
                            required = new[] { "title", "detail", "category", "timestamp", "confidence", "call_ids" }
                        }
                    }
                },
                required = new[] { "summary_text", "notable_events" }
            }
        }
    };

    private sealed record InsightResult(string SummaryText, List<InsightEvent> Events);
    private sealed record InsightEvent(string Title, string Detail, string Category, string Timestamp, double Confidence, List<string> CallIds);
}
