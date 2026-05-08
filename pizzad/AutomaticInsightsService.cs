using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class AutomaticInsightsService : BackgroundService
{
    private const int DefaultBatchSize = 50;
    private const int MaxPromptChars = 120_000;
    private const double IncidentCallEventThreshold = 0.24;
    private const double IncidentCorroborationPairThreshold = 0.12;
    private const double IncidentPairThreshold = 0.34;
    private static readonly TimeSpan MaxIncidentSpan = TimeSpan.FromMinutes(60);
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

    public int ConfiguredBatchSize => BatchSize();

    public bool IsConfiguredAndEnabled => IsEnabled();

    public async Task<int> GenerateWindowForCallsAsync(List<EngineCall> calls, CancellationToken ct)
    {
        if (!IsEnabled())
            throw new InvalidOperationException("AI insights are disabled or not fully configured.");

        calls = calls
            .Where(c => string.Equals(c.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.QualityReason, "ok", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(c.Transcription))
            .OrderBy(c => c.StartTime)
            .Select(_talkgroups.Enrich)
            .ToList();
        if (calls.Count == 0)
            return 0;

        var start = calls.Min(c => c.StartTime);
        var end = calls.Max(c => Math.Max(c.StartTime, c.StopTime));
        var result = await SummarizeWindowAsync(calls, start, end, ct);
        var windowId = await _database.AddInsightWindowAsync(start, end, result.SummaryText, ct);
        await PersistInsightEventsAsync(windowId, result, calls, ct);
        var incidents = await PersistIncidentsAsync(result, calls, ct);
        _priorSummary = result.SummaryText;
        await _events.PublishAsync("summary_updated", new { windowId, start, end, incidents }, ct);
        _logger.LogInformation("Manual insights generated window {WindowId} with {Incidents} incidents from {Calls} calls", windowId, incidents, calls.Count);
        return incidents;
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
            await PersistInsightEventsAsync(windowId, result, batch, ct);
            var incidents = await PersistIncidentsAsync(result, batch, ct);

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

    private async Task<int> PersistIncidentsAsync(InsightResult result, List<EngineCall> batch, CancellationToken ct)
    {
        var incidents = 0;
        foreach (var ev in result.Events.Where(IsActionableEvent))
        {
            var calls = ResolveEventCalls(ev, batch);
            if (calls.Count < 2)
            {
                _logger.LogInformation(
                    "Skipped AI event '{Title}' because it linked {CallCount} call(s); incidents require at least 2 related calls.",
                    ev.Title,
                    calls.Count);
                continue;
            }

            if (!IsValidIncidentCandidate(ev, calls, out var rejectReason))
            {
                _logger.LogInformation(
                    "Rejected AI incident candidate '{Title}' with {CallCount} call(s): {Reason}. Calls={CallIds}",
                    ev.Title,
                    calls.Count,
                    rejectReason,
                    string.Join(",", calls.Select(c => c.Id)));
                continue;
            }

            await _database.AddIncidentAsync(new IncidentDto
            {
                Title = string.IsNullOrWhiteSpace(ev.Title) ? "Radio incident" : ev.Title.Trim(),
                Detail = string.IsNullOrWhiteSpace(ev.Detail) ? result.SummaryText : ev.Detail.Trim(),
                FirstSeen = calls.Min(c => c.StartTime),
                LastSeen = calls.Max(c => c.StartTime),
                Confidence = Math.Clamp(ev.Confidence, 0, 1),
                Calls = calls.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio")).ToList()
            }, ct);
            incidents++;
        }

        return incidents;
    }

    private async Task PersistInsightEventsAsync(long windowId, InsightResult result, List<EngineCall> batch, CancellationToken ct)
    {
        var events = result.Events
            .Where(IsNotableInsightEvent)
            .Select(ev =>
            {
                var calls = ResolveEventCalls(ev, batch);
                if (calls.Count == 0)
                    return null;

                return new InsightEventRecordDto
                {
                    Title = string.IsNullOrWhiteSpace(ev.Title) ? "Radio insight" : ev.Title.Trim(),
                    Detail = string.IsNullOrWhiteSpace(ev.Detail) ? result.SummaryText : ev.Detail.Trim(),
                    Category = string.IsNullOrWhiteSpace(ev.Category) ? "other" : ev.Category.Trim().ToLowerInvariant(),
                    FirstSeen = calls.Min(c => c.StartTime),
                    LastSeen = calls.Max(c => c.StartTime),
                    Confidence = Math.Clamp(ev.Confidence, 0, 1),
                    Calls = calls.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio")).ToList()
                };
            })
            .Where(e => e != null)
            .Cast<InsightEventRecordDto>()
            .ToList();

        await _database.ReplaceInsightEventsAsync(windowId, events, ct);
        if (events.Count > 0)
            _logger.LogInformation("Persisted {Count} insight event(s) for window {WindowId}", events.Count, windowId);
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
            max_tokens = 8000,
            response_format = InsightResponseFormat(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You summarize radio call transcripts into concise, actionable category insights. Output JSON with fields summary_text and notable_events (list of {title, detail, category, timestamp, confidence, call_ids}). For each notable event: choose exactly one category from [police, fire, ems, traffic, public_works, utilities, other], set timestamp as local HH:mm (24h), and include one or more matching call_ids copied exactly from the provided call lines. Omit routine acknowledgements and 'no incident' findings."
                },
                new { role = "user", content = BuildPrompt(calls, start, end) }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling LM Studio insights endpoint {Endpoint} with model {Model} for {Calls} calls ({PayloadChars} chars)", endpoint, InsightModel(), calls.Count, payload.Length);
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
                _logger.LogInformation("LM Studio insights response received for {Calls} calls ({ResponseChars} chars)", calls.Count, text.Length);
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
        var targetEventCount = calls.Count switch
        {
            >= 300 => 18,
            >= 200 => 16,
            >= 100 => 14,
            >= 50 => 12,
            _ => 8
        };

        sb.AppendLine("/no_think");
        sb.AppendLine("Return only the final JSON object in message.content. Do not place the answer in reasoning_content.");
        sb.AppendLine($"Window: {DateTimeOffset.FromUnixTimeSeconds(start).ToLocalTime()} to {DateTimeOffset.FromUnixTimeSeconds(end).ToLocalTime()}");
        if (!string.IsNullOrWhiteSpace(_priorSummary))
        {
            sb.AppendLine("Prior summary:");
            sb.AppendLine(_priorSummary);
        }

        sb.AppendLine("Category guidance: each notable event must use one of these categories exactly:");
        sb.AppendLine("police, fire, ems, traffic, public_works, utilities, other");
        sb.AppendLine("Timestamp guidance: include timestamp as local HH:mm (24h) using the provided call times.");
        sb.AppendLine("Insight guidance: notable_events may describe a single important call or multiple related calls. Incidents will be derived later from multi-call notable events.");
        sb.AppendLine("Linkage guidance: each notable event must include one or more call_ids copied exactly from input lines. Include every clearly related source call in this window.");
        sb.AppendLine($"Coverage guidance: target up to {targetEventCount} notable_events for this window when evidence supports it. Return an empty notable_events array when there is nothing notable. Keep each detail concise (1 sentence).");

        var prioritizedCalls = calls
            .OrderByDescending(c => c.IsAlertMatch)
            .ThenByDescending(c => c.StopTime - c.StartTime);

        var lines = new List<string>();
        var usedChars = sb.Length;
        foreach (var call in prioritizedCalls)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime();
            var prefix = call.IsAlertMatch ? "[ALERT] " : string.Empty;
            var line = $"- [id:{CallToken(call.Id)}] [{local:h:mm tt}] {call.SystemShortName} | {Label(call)}: {prefix}{call.Transcription}";
            if (usedChars + line.Length + Environment.NewLine.Length > MaxPromptChars)
                break;

            lines.Add(line);
            usedChars += line.Length + Environment.NewLine.Length;
        }

        var omitted = Math.Max(0, calls.Count - lines.Count);
        sb.AppendLine($"Analyzing {lines.Count} calls (alerts prioritized, prompt budget {MaxPromptChars:N0} chars, omitted {omitted}):");
        foreach (var line in lines)
            sb.AppendLine(line);

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

            if (string.IsNullOrWhiteSpace(content) &&
                message.TryGetProperty("reasoning_content", out var reasoningElement) &&
                reasoningElement.ValueKind == JsonValueKind.String)
            {
                content = reasoningElement.GetString() ?? string.Empty;
            }
        }

        content = StripCodeFence(content);
        content = ExtractJsonObject(content);
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

        if (ev.CallIds.Count < 2)
            return false;

        return !Regex.IsMatch(combined,
            @"\b(no|none|not|without|unclear)\b.{0,40}\b(incident|event|actionable|emergency|issue|activity)\b|\b(no event detected|no clear incident|no actionable incident|no notable event|nothing notable|routine traffic only|routine chatter|non.?incident)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsNotableInsightEvent(InsightEvent ev)
    {
        var title = NormalizeEventText(ev.Title);
        var detail = NormalizeEventText(ev.Detail);
        var combined = $"{title} {detail}".Trim();
        if (string.IsNullOrWhiteSpace(combined) || ev.CallIds.Count == 0)
            return false;

        return !Regex.IsMatch(combined,
            @"\b(no|none|not|without|unclear)\b.{0,40}\b(incident|event|actionable|emergency|issue|activity)\b|\b(no event detected|no clear incident|no actionable incident|no notable event|nothing notable|routine traffic only|routine chatter|non.?incident)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsValidIncidentCandidate(InsightEvent ev, List<EngineCall> calls, out string reason)
    {
        if (calls.Count < 2)
        {
            reason = "fewer than 2 resolved calls";
            return false;
        }

        var first = calls.Min(c => c.StartTime);
        var last = calls.Max(c => c.StartTime);
        var span = TimeSpan.FromSeconds(Math.Max(0, last - first));
        if (span > MaxIncidentSpan)
        {
            reason = $"call span {span.TotalMinutes:0.#}m exceeds {MaxIncidentSpan.TotalMinutes:0.#}m";
            return false;
        }

        reason = "multi-call actionable event within incident time window";
        return true;
    }

    private static HashSet<string> ExtractIncidentTokens(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "call","calls","unit","units","officer","officers","dispatch","reported","advised","responding",
            "response","scene","area","update","updates","subject","caller","vehicle","police","fire","ems",
            "medical","traffic","north","south","east","west","street","road","drive","avenue","near","copy",
            "copies","clear","clearance","radio","county","city","sheriff","department","channel","station"
        };
        var cleaned = Regex.Replace((text ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9\s]", " ");
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 4 || stop.Contains(token))
                continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private static double ComputeIncidentSimilarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        var jaccard = union <= 0 ? 0 : intersection / (double)union;
        var containment = intersection / (double)Math.Min(a.Count, b.Count);
        return Math.Max(jaccard, containment * 0.72);
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

    private static string ExtractJsonObject(string content)
    {
        content = content.Trim();
        if (content.StartsWith('{') && content.EndsWith('}'))
            return content;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start
            ? content[start..(end + 1)]
            : content;
    }

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
                                    items = new { type = "string" },
                                    minItems = 1
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
