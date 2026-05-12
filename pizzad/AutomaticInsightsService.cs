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
    private const int NormalPromptCharLimit = 24_000;
    private const int CompactPromptCharLimit = 12_000;
    private const int NormalTranscriptCharLimit = 500;
    private const int CompactTranscriptCharLimit = 220;
    private const int NormalMaxOutputTokens = 2_000;
    private const int CompactMaxOutputTokens = 1_200;
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
    private DateTimeOffset _nextQueueGateLogAt = DateTimeOffset.MinValue;
    private int _failureStreak;

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
        if (!_config.Setup.Completed || !IsEnabled())
            return;
        _queue.Enqueue(_talkgroups.Enrich(call));
    }

    public int ConfiguredBatchSize => BatchSize();

    public bool IsConfiguredAndEnabled => IsEnabled();

    public bool IsSetupComplete => _config.Setup.Completed;

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
        var result = await SummarizeWindowAsync(calls, start, end, InsightPromptMode.CompactManual, ct);
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
                if (_config.Setup.Completed && IsEnabled())
                {
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

        var maxQueueDepth = _config.AiInsights.MaxQueueDepthForManualSummary;
        if (maxQueueDepth > 0)
        {
            var pendingTranscriptions = await _database.CountPendingTranscriptionCallsAsync(ct);
            if (pendingTranscriptions > maxQueueDepth)
            {
                if (DateTimeOffset.UtcNow >= _nextQueueGateLogAt)
                {
                    _logger.LogInformation(
                        "Automatic AI insights paused while transcription backlog is high: {Pending:N0} pending call(s), configured limit {Limit:N0}",
                        pendingTranscriptions,
                        maxQueueDepth);
                    _nextQueueGateLogAt = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                return;
            }
        }

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
            var result = await SummarizeWindowAsync(batch, start, end, InsightPromptMode.NormalLive, ct);
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
        var claimedCallIds = new HashSet<long>();
        foreach (var ev in result.Events.Where(IsActionableEvent))
        {
            var calls = ResolveEventCalls(ev, batch)
                .Where(c => !claimedCallIds.Contains(c.Id))
                .ToList();
            if (calls.Count < 2)
            {
                _logger.LogInformation(
                    "Skipped AI event '{Title}' because it linked {CallCount} unclaimed call(s); incidents require at least 2 related calls.",
                    ev.Title,
                    calls.Count);
                continue;
            }

            var validation = IncidentCandidateValidator.Validate(ev.Title, ev.Detail, calls.Select(ToIncidentCandidateCall).ToList());
            if (!validation.IsValid)
            {
                _logger.LogInformation(
                    "Rejected AI incident candidate '{Title}' with {CallCount} call(s): {Reason}. Calls={CallIds}",
                    ev.Title,
                    calls.Count,
                    validation.Reason,
                    string.Join(",", calls.Select(c => c.Id)));
                continue;
            }
            var validatedCallIds = validation.Calls.Select(c => c.CallId).ToHashSet();
            calls = calls.Where(c => validatedCallIds.Contains(c.Id)).ToList();

            var incidentId = await _database.AddIncidentAsync(new IncidentDto
            {
                Title = string.IsNullOrWhiteSpace(ev.Title) ? "Radio incident" : ev.Title.Trim(),
                Detail = string.IsNullOrWhiteSpace(ev.Detail) ? result.SummaryText : ev.Detail.Trim(),
                FirstSeen = calls.Min(c => c.StartTime),
                LastSeen = calls.Max(c => c.StartTime),
                Confidence = Math.Clamp(ev.Confidence, 0, 1),
                Calls = calls.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio")).ToList()
            }, ct);
            if (incidentId <= 0)
            {
                _logger.LogInformation(
                    "Skipped AI incident candidate '{Title}' because one or more calls are already linked to an existing incident. Calls={CallIds}",
                    ev.Title,
                    string.Join(",", calls.Select(c => c.Id)));
                continue;
            }

            foreach (var call in calls)
                claimedCallIds.Add(call.Id);
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
        else
            _logger.LogInformation("Persisted 0 insight events for window {WindowId}; summary was: {Summary}", windowId, Trim(result.SummaryText, 300));
    }

    private async Task<InsightResult> SummarizeWindowAsync(List<EngineCall> calls, long start, long end, InsightPromptMode mode, CancellationToken ct)
    {
        var budget = PromptBudget.For(mode);
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
            max_tokens = budget.MaxOutputTokens,
            response_format = InsightResponseFormat(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You summarize radio call transcripts into concise, actionable category insights. Output JSON with fields summary_text and notable_events (list of {title, detail, category, timestamp, confidence, call_ids}). For each notable event: choose exactly one category from [police, fire, ems, traffic, public_works, utilities, other], set timestamp as local HH:mm (24h), and include one or more matching call_ids copied exactly from the provided call lines. Extract useful intelligence from radio shorthand when supported by context, including common 10-codes, status codes, disposition codes, and spoken numeric codes such as 10-7, 10-49, 1049, code 16, signal codes, unit status, locations, hazards, vehicles, people, and outcomes. Preserve the original code in detail text when its meaning is uncertain; do not invent local code meanings. Omit routine acknowledgements and 'no incident' findings."
                },
                new { role = "user", content = BuildPrompt(calls, start, end, budget) }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling LM Studio insights endpoint {Endpoint} with model {Model} for {Calls} calls ({PayloadChars} chars, mode={Mode}, promptLimit={PromptLimit}, transcriptLimit={TranscriptLimit}, maxTokens={MaxTokens})", endpoint, InsightModel(), calls.Count, payload.Length, mode, budget.PromptCharLimit, budget.TranscriptCharLimit, budget.MaxOutputTokens);
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
                await RecordUsageAsync(text, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, true, string.Empty, ct);
                return ParseResponse(text);
            }
            catch (Exception ex) when (attempt < Math.Max(0, _config.AiInsights.MaxRetries))
            {
                last = ex;
                await RecordUsageAsync(string.Empty, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, ex.Message, CancellationToken.None);
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                last = ex;
                await RecordUsageAsync(string.Empty, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, ex.Message, CancellationToken.None);
                break;
            }
        }

        throw new InvalidOperationException(last?.Message ?? "Automatic insights request failed.", last);
    }

    private async Task RecordUsageAsync(string responseText, string endpoint, int payloadChars, int inputChars, int attempt, bool success, string error, CancellationToken ct)
    {
        var usage = ExtractUsage(responseText);
        await _database.AddLmUsageAsync(new TokenUsageEntryDto(
            0,
            DateTime.UtcNow,
            "automatic insights",
            "chat.completions",
            success,
            error,
            endpoint,
            InsightModel(),
            usage.ResponseModel,
            usage.FinishReason,
            inputChars,
            payloadChars,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens), ct);
    }

    private static (int PromptTokens, int CompletionTokens, int TotalTokens, string ResponseModel, string FinishReason) ExtractUsage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (0, 0, 0, string.Empty, string.Empty);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var prompt = ReadInt(root, "usage", "prompt_tokens", "promptTokens");
            var completion = ReadInt(root, "usage", "completion_tokens", "completionTokens");
            var total = ReadInt(root, "usage", "total_tokens", "totalTokens");
            var model = root.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty;
            var finish = string.Empty;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("finish_reason", out var f))
                finish = f.GetString() ?? string.Empty;
            return (prompt, completion, total, model, finish);
        }
        catch
        {
            return (0, 0, 0, string.Empty, string.Empty);
        }
    }

    private static int ReadInt(JsonElement root, string parent, string snake, string camel)
    {
        if (!root.TryGetProperty(parent, out var obj)) return 0;
        if (obj.TryGetProperty(snake, out var a) && a.TryGetInt32(out var av)) return av;
        if (obj.TryGetProperty(camel, out var b) && b.TryGetInt32(out var bv)) return bv;
        return 0;
    }

    private string BuildPrompt(List<EngineCall> calls, long start, long end, PromptBudget budget)
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
        sb.AppendLine("Category guidance: each notable event must use one of these categories exactly:");
        sb.AppendLine("police, fire, ems, traffic, public_works, utilities, other");
        sb.AppendLine("Timestamp guidance: include timestamp as local HH:mm (24h) using the provided call times.");
        sb.AppendLine("Insight guidance: notable_events are AI summary cards for category pages. They may describe one useful source call or multiple clearly related calls. Incidents are derived later only from notable_events that contain 2 or more strongly related calls.");
        sb.AppendLine("Single-call guidance: include a one-call notable_event when a call contains actionable or situationally useful information, such as a dispatched complaint, medical transport, fire response, BOLO/vehicle/person description, road hazard, pursuit, crash, alarm, disturbance, arrest/custody, welfare check, address/location, agency handoff, or meaningful radio-code/status detail.");
        sb.AppendLine("Incident grouping guidance: a multi-call notable_event must be one real-world event, not a topic bucket. Do not group calls merely because they are close in time, share a category, share an agency, share a talkgroup, or are routine unit/admin/status traffic.");
        sb.AppendLine("Concrete anchor guidance: include multiple call_ids only when every included call shares concrete evidence such as the same address/street/intersection, landmark, patient, person/name, vehicle/plate, unit continuation, radio channel handoff, or an explicit reference to the same call. If a call lacks a concrete anchor to the event, omit it.");
        sb.AppendLine("Routine exclusion guidance: do not create notable_events for pure acknowledgements, availability only, radio checks, administrative chatter, generic dispatch coordination with no event detail, or isolated unclear/inaudible calls. Do not treat the whole window as routine if some individual calls contain useful dispatch intelligence.");
        sb.AppendLine("Linkage guidance: each notable event must include one or more call_ids copied exactly from input lines. Include every clearly related source call in this window, but do not pad an event with weakly related calls. Each call_id may belong to at most one notable_event.");
        sb.AppendLine("Evidence guidance: for every included call_id, add a call_evidence entry {call_id, evidence}. The evidence must be a short anchor phrase explaining why that call belongs to this event, such as an address, road, unit handoff, patient/vehicle detail, or quoted shared phrase.");
        sb.AppendLine("Radio-code guidance: police/fire/EMS traffic often uses compact codes. Treat patterns like 10-7, 10-49/1049, code 16, signal codes, unit status, and disposition codes as important evidence. Include the code and any context-supported meaning in notable event details; if the meaning is ambiguous, keep the code verbatim instead of guessing.");
        sb.AppendLine($"Coverage guidance: target up to {targetEventCount} notable_events for this window when evidence supports it. Prefer several precise single-call insights over one generic 'routine traffic' summary. Return an empty notable_events array only when every call is truly routine or unusable. Keep each detail concise (1 sentence).");

        var prioritizedCalls = calls
            .OrderByDescending(c => c.IsAlertMatch)
            .ThenByDescending(c => c.StopTime - c.StartTime);

        var lines = new List<string>();
        var usedChars = sb.Length;
        foreach (var call in prioritizedCalls)
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime();
            var prefix = call.IsAlertMatch ? "[ALERT] " : string.Empty;
            var line = $"- [id:{CallToken(call.Id)}] [{local:h:mm tt}] {call.SystemShortName} | {Label(call)}: {prefix}{Trim(call.Transcription, budget.TranscriptCharLimit)}";
            if (usedChars + line.Length + Environment.NewLine.Length > budget.PromptCharLimit)
                break;

            lines.Add(line);
            usedChars += line.Length + Environment.NewLine.Length;
        }

        var omitted = Math.Max(0, calls.Count - lines.Count);
        sb.AppendLine($"Analyzing {lines.Count} calls (alerts prioritized, prompt budget {budget.PromptCharLimit:N0} chars, transcript trim {budget.TranscriptCharLimit:N0} chars, omitted {omitted}):");
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
                    ids,
                    ReadEvidence(item)));
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

        return IncidentCandidateValidator.IsActionableText(title, detail, ev.CallIds.Count);
    }

    private static bool IsNotableInsightEvent(InsightEvent ev)
    {
        var title = NormalizeEventText(ev.Title);
        var detail = NormalizeEventText(ev.Detail);
        var combined = $"{title} {detail}".Trim();
        if (string.IsNullOrWhiteSpace(combined) || ev.CallIds.Count == 0)
            return false;

        return IncidentCandidateValidator.IsNotableText(title, detail, ev.CallIds.Count);
    }

    private static string NormalizeEventText(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private bool IsEnabled() =>
        _config.Setup.Completed &&
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

    private static List<CallEvidence> ReadEvidence(JsonElement item)
    {
        var evidence = new List<CallEvidence>();
        if (!item.TryGetProperty("call_evidence", out var array) || array.ValueKind != JsonValueKind.Array)
            return evidence;
        foreach (var row in array.EnumerateArray())
        {
            evidence.Add(new CallEvidence(GetString(row, "call_id"), GetString(row, "evidence")));
        }
        return evidence;
    }

    private static IncidentCandidateCall ToIncidentCandidateCall(EngineCall call) =>
        new(call.Id, call.StartTime, call.Transcription, call.Category, call.TalkgroupName, call.SystemShortName);

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
                                },
                                call_evidence = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            call_id = new { type = "string" },
                                            evidence = new { type = "string" }
                                        },
                                        required = new[] { "call_id", "evidence" }
                                    }
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
    private sealed record InsightEvent(string Title, string Detail, string Category, string Timestamp, double Confidence, List<string> CallIds, List<CallEvidence> CallEvidence);
    private sealed record CallEvidence(string CallId, string Evidence);
    private enum InsightPromptMode { NormalLive, CompactManual }
    private sealed record PromptBudget(int PromptCharLimit, int TranscriptCharLimit, int MaxOutputTokens)
    {
        public static PromptBudget For(InsightPromptMode mode) => mode switch
        {
            InsightPromptMode.CompactManual => new PromptBudget(CompactPromptCharLimit, CompactTranscriptCharLimit, CompactMaxOutputTokens),
            _ => new PromptBudget(NormalPromptCharLimit, NormalTranscriptCharLimit, NormalMaxOutputTokens)
        };
    }
}
