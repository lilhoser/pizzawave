using System.Collections.Concurrent;
using System.Text;
using System.Net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using pizzalib;
using System.Diagnostics;
using static pizzapi.TraceLogger;

namespace pizzapi;

public class InsightsService : IDisposable
{
    private const int UnsummarizedBatchSize = 50;
    private const int MaxPendingLiveCalls = 1000;
    private readonly ConcurrentQueue<TranscribedCall> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly InsightsStorage _storage;
    private Settings _settings;
    private readonly object _pendingLiveCallsLock = new();
    private readonly List<TranscribedCall> _pendingLiveCalls = new();
    private readonly object _summaryWorkerLock = new();
    private Task? _activeSummaryTask;
    private DateTimeOffset _nextSummaryAttemptAt = DateTimeOffset.MinValue;
    private int _consecutiveSummaryFailures;
    private string? _priorSummary;
    private DateTimeOffset _lastRetentionRun = DateTimeOffset.MinValue;
    private DateOnly _lastDigestSentForDate = DateOnly.MinValue;
    public string? LastFailureReason { get; private set; }

    public event Action<InsightSummaryWindow>? WindowSaved;

    public InsightsService(Settings settings)
    {
        _settings = settings;
        _storage = new InsightsStorage();
        _storage.EnsureDirectories();
        _worker = Task.Run(ProcessQueueAsync);
        
        Trace(TraceLoggerType.Insights, TraceEventType.Information, "InsightsService initialized");
        }

    public void UpdateSettings(Settings settings)
    {
        _settings = settings;
        }

    public void IngestCall(TranscribedCall call)
    {
        _queue.Enqueue(call);
        }

    public string GetNextSummaryStatusText()
    {
        int pendingCount;
        lock (_pendingLiveCallsLock)
        {
            pendingCount = _pendingLiveCalls.Count;
        }
        pendingCount += _queue.Count;

        bool isBusy;
        DateTimeOffset nextAttempt;
        lock (_summaryWorkerLock)
        {
            isBusy = _activeSummaryTask != null && !_activeSummaryTask.IsCompleted;
            nextAttempt = _nextSummaryAttemptAt;
        }

        var remaining = Math.Max(0, UnsummarizedBatchSize - pendingCount);
        if (DateTimeOffset.UtcNow < nextAttempt)
        {
            var delay = Math.Max(1, (int)Math.Ceiling((nextAttempt - DateTimeOffset.UtcNow).TotalSeconds));
            return remaining == 0
                ? $"Next insight delayed {delay}s (backoff)"
                : $"Next insight in {remaining} calls (backoff {delay}s)";
        }

        if (isBusy)
        {
            return remaining == 0
                ? "Next insight due (processing...)"
                : $"Next insight in {remaining} calls (processing...)";
        }

        if (remaining == 0)
            return "Next insight due";

        return $"Next insight in {remaining} calls";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _worker.Wait(2000); } catch { }
        try { _activeSummaryTask?.Wait(2000); } catch { }
        }

    private async Task ProcessQueueAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            while (_queue.TryDequeue(out var call))
            {
                AddPendingLiveCall(call);
            }

            try
            {
                PumpHeuristicSummaryWorker();
                await RunRetentionIfNeeded();
                await RunDailyDigestIfNeeded();
            }
            catch
            {
                // Swallow processing errors to keep service alive
            }

            await Task.Delay(500, _cts.Token).ContinueWith(_ => { });
        }
        }

    private void AddPendingLiveCall(TranscribedCall call)
    {
        lock (_pendingLiveCallsLock)
        {
            _pendingLiveCalls.Add(call);

            if (_pendingLiveCalls.Count > MaxPendingLiveCalls)
            {
                var overflow = _pendingLiveCalls.Count - MaxPendingLiveCalls;
                for (var i = 0; i < overflow; i++)
                {
                    var dropIndex = _pendingLiveCalls.FindIndex(c => !c.IsAlertMatch);
                    if (dropIndex < 0)
                        dropIndex = 0;
                    _pendingLiveCalls.RemoveAt(dropIndex);
                }
                Trace(TraceLoggerType.Insights, TraceEventType.Warning,
                    $"Dropped {overflow} pending live calls (preferring non-alert) to cap memory at {MaxPendingLiveCalls}.");
            }
        }
    }

    private void PumpHeuristicSummaryWorker()
    {
        if (!_settings.LmLinkEnabled ||
            string.IsNullOrWhiteSpace(_settings.LmLinkBaseUrl) ||
            string.IsNullOrWhiteSpace(_settings.LmLinkModel))
            return;

        lock (_summaryWorkerLock)
        {
            if (_activeSummaryTask != null && !_activeSummaryTask.IsCompleted)
                return;

            if (_activeSummaryTask != null && _activeSummaryTask.IsCompleted)
                _activeSummaryTask = null;

            if (DateTimeOffset.UtcNow < _nextSummaryAttemptAt)
                return;

            int pendingCount;
            lock (_pendingLiveCallsLock)
            {
                pendingCount = _pendingLiveCalls.Count;
            }

            if (pendingCount < UnsummarizedBatchSize)
                return;

            var batchSize = ComputeAdaptiveBatchSize(pendingCount, _consecutiveSummaryFailures);
            _activeSummaryTask = Task.Run(() => RunHeuristicSummaryBatchAsync(batchSize));
        }
    }

    private async Task RunHeuristicSummaryBatchAsync(int batchSize)
    {
        try
        {
            List<TranscribedCall> batchCalls;
            lock (_pendingLiveCallsLock)
            {
                if (_pendingLiveCalls.Count < UnsummarizedBatchSize)
                    return;
                var take = Math.Min(batchSize, _pendingLiveCalls.Count);
                batchCalls = _pendingLiveCalls.Take(take).ToList();
            }

            if (batchCalls.Count == 0)
                return;

            var windowStartUnix = batchCalls.Min(c => c.StartTime);
            var windowEndUnix = batchCalls.Max(c => Math.Max(c.StartTime, c.StopTime));
            var windowStart = DateTimeOffset.FromUnixTimeSeconds(windowStartUnix).ToLocalTime();
            var windowEnd = DateTimeOffset.FromUnixTimeSeconds(windowEndUnix).ToLocalTime();

            Trace(TraceLoggerType.Insights, TraceEventType.Information,
                $"Generating heuristic insights summary for {batchCalls.Count} pending calls: {windowStart} to {windowEnd}");

            var success = await FinalizeWindowCoreAsync(windowStart, windowEnd, batchCalls, _settings);
            if (success)
            {
                lock (_pendingLiveCallsLock)
                {
                    var removeCount = Math.Min(batchCalls.Count, _pendingLiveCalls.Count);
                    _pendingLiveCalls.RemoveRange(0, removeCount);
                }

                lock (_summaryWorkerLock)
                {
                    _consecutiveSummaryFailures = 0;
                    _nextSummaryAttemptAt = DateTimeOffset.UtcNow;
                }
                return;
            }

            int failureStreak;
            int cooldownSeconds;
            lock (_summaryWorkerLock)
            {
                _consecutiveSummaryFailures++;
                failureStreak = _consecutiveSummaryFailures;
                cooldownSeconds = Math.Min(300, 5 * (1 << Math.Min(failureStreak, 5)));
                _nextSummaryAttemptAt = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);
            }
            RotateFailedBatchToTail(batchCalls);

            Trace(TraceLoggerType.Insights, TraceEventType.Warning,
                $"Heuristic insights summary failed for batch size {batchCalls.Count}. Rotated failed batch to queue tail and backing off for {cooldownSeconds}s (failure streak {failureStreak}).");
        }
        catch (Exception ex)
        {
            lock (_summaryWorkerLock)
            {
                _consecutiveSummaryFailures++;
                var cooldownSeconds = Math.Min(300, 5 * (1 << Math.Min(_consecutiveSummaryFailures, 5)));
                _nextSummaryAttemptAt = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);
            }
            Trace(TraceLoggerType.Insights, TraceEventType.Error, $"Heuristic summary worker crashed: {ex.Message}");
        }
    }

    private static int ComputeAdaptiveBatchSize(int pendingCount, int failureStreak)
    {
        // Recovery mode: after any failure, return to baseline batch size.
        // This avoids repeatedly issuing oversized LM requests while backlog is high.
        if (failureStreak > 0)
            return UnsummarizedBatchSize;

        if (pendingCount >= 500)
            return 200;
        if (pendingCount >= 250)
            return 150;
        if (pendingCount >= 120)
            return 100;
        return UnsummarizedBatchSize;
    }

    private void RotateFailedBatchToTail(List<TranscribedCall> failedBatch)
    {
        if (failedBatch.Count == 0)
            return;

        lock (_pendingLiveCallsLock)
        {
            var moveCount = Math.Min(failedBatch.Count, _pendingLiveCalls.Count);
            if (moveCount <= 0)
                return;

            var moved = _pendingLiveCalls.Take(moveCount).ToList();
            _pendingLiveCalls.RemoveRange(0, moveCount);
            _pendingLiveCalls.AddRange(moved);
        }
    }

    internal async Task<bool> FinalizeWindowAsync(DateTimeOffset start, DateTimeOffset end, List<TranscribedCall> calls)
    {
        return await FinalizeWindowCoreAsync(start, end, calls, _settings);
        }

    internal async Task<bool> FinalizeWindowAsync(DateTimeOffset start, DateTimeOffset end, List<TranscribedCall> calls, Settings settings)
    {
        return await FinalizeWindowCoreAsync(start, end, calls, settings);
    }

    private async Task<bool> FinalizeWindowCoreAsync(DateTimeOffset start, DateTimeOffset end, List<TranscribedCall> calls, Settings settings)
    {
        LastFailureReason = null;
        if (calls.Count == 0)
        {
            LastFailureReason = "No calls found in selected range.";
            return false;
        }

        var summary = new InsightSummaryWindow
        {
            WindowStart = start,
            WindowEnd = end,
            Model = settings.LmLinkModel ?? string.Empty
        };

        summary.SourceCounts["total_calls"] = calls.Count;
        summary.SourceCounts["alert_matches"] = calls.Count(c => c.IsAlertMatch);
        // Keep persisted summary compact; per-event call_ids carry deterministic linkage.
        summary.SourceHashes = new List<string>();
        summary.SourceCallIds = new List<string>();

        Trace(TraceLoggerType.Insights, TraceEventType.Information, 
            $"Finalizing window {start} to {end}, {calls.Count} calls");

        if (settings.LmLinkEnabled && !string.IsNullOrWhiteSpace(settings.LmLinkBaseUrl))
        {
            try
            {
                Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                    $"Calling LM Link for window {start} to {end}");
                var lmResult = await LmLinkClient.SummarizeWindowAsync(settings, start, end, calls, _priorSummary);
                summary.SummaryText = lmResult.SummaryText;
                summary.NotableEvents = lmResult.NotableEvents;
                if (calls.Count >= 50 && summary.NotableEvents.Count == 0)
                {
                    summary.Error = "LM Link returned zero notable events for a high-volume window.";
                    summary.SummaryText = "Insights unavailable (empty LM result).";
                    LastFailureReason = summary.Error;
                }
                var alertByCallId = calls
                    .GroupBy(CallHash.ComputeCallId)
                    .ToDictionary(g => g.Key, g => g.Any(c => c.IsAlertMatch), StringComparer.OrdinalIgnoreCase);
                foreach (var ev in summary.NotableEvents)
                {
                    ev.HasAlertMatch = ev.CallIds.Any(id =>
                        alertByCallId.TryGetValue(id, out var isAlert) && isAlert);
                }
                _priorSummary = lmResult.SummaryText;
                Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                    $"LM Link returned summary with {lmResult.NotableEvents.Count} notable events");
            }
            catch (Exception ex)
            {
                summary.SummaryText = "Insights unavailable (LM Link error).";
                summary.Error = ex.Message;
                LastFailureReason = ex.Message;
                Trace(TraceLoggerType.Insights, TraceEventType.Error, 
                    $"LM Link error for window {start}: {ex.Message}");
            }
        }
        else
        {
            summary.SummaryText = "Insights disabled.";
            summary.Error = "LM Link disabled";
        }

        if (!string.IsNullOrWhiteSpace(summary.Error))
        {
            LastFailureReason ??= summary.Error;
            Trace(TraceLoggerType.Insights, TraceEventType.Warning,
                $"Skipping persistence for failed summary window {start} to {end}: {summary.Error}");
            return false;
        }

        _storage.SaveWindowSummary(summary);
        WindowSaved?.Invoke(summary);
        return true;
    }

    private async Task RunDailyDigestIfNeeded()
    {
        if (!IsDailyDigestConfigured(_settings))
            return;

        var nowLocal = DateTimeOffset.Now;
        if (nowLocal.TimeOfDay < TimeSpan.FromMinutes(10))
            return;

        var digestDate = DateOnly.FromDateTime(nowLocal.Date.AddDays(-1));
        if (digestDate <= _lastDigestSentForDate)
            return;

        try
        {
            var sent = await Task.Run(() => TrySendDailyDigest(digestDate));
            if (sent)
                _lastDigestSentForDate = digestDate;
        }
        catch (Exception ex)
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Warning,
                $"Daily insights digest failed for {digestDate:yyyy-MM-dd}: {ex.Message}");
        }
    }

    private bool TrySendDailyDigest(DateOnly digestDate)
    {
        var dayStart = new DateTimeOffset(digestDate.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(digestDate.ToDateTime(TimeOnly.MinValue)));
        var dayEnd = dayStart.AddDays(1);
        var summaries = _storage.LoadDaily(dayStart, dayEnd);
        if (summaries.Count == 0)
            return false;

        var grouped = summaries
            .SelectMany(s => s.NotableEvents, (summary, ev) => new { Summary = summary, Event = ev })
            .GroupBy(x => x.Event.CategoryKey)
            .OrderBy(g => InsightCategoryPalette.Order(g.Key))
            .ToList();

        if (grouped.Count == 0)
            return false;

        var body = new StringBuilder();
        body.Append("<html><body style=\"font-family:Segoe UI,Arial,sans-serif;\">");
        body.Append($"<h2>PizzaWave Daily Insights Digest - {digestDate:MMMM d, yyyy}</h2>");

        foreach (var categoryGroup in grouped)
        {
            var category = InsightCategoryPalette.DisplayName(categoryGroup.Key);
            var icon = InsightCategoryPalette.Icon(categoryGroup.Key);
            body.Append($"<h3>{icon} {WebUtility.HtmlEncode(category)}</h3><ol>");

            foreach (var item in categoryGroup
                         .OrderByDescending(x => (x.Event.Confidence) + (x.Event.HasAlertMatch ? 1.0 : 0.0))
                         .ThenByDescending(x => x.Summary.WindowEnd)
                         .Take(10))
            {
                var score = item.Event.Confidence + (item.Event.HasAlertMatch ? 1.0 : 0.0);
                var alertBadge = item.Event.HasAlertMatch ? " [ALERT]" : string.Empty;
                var detail = string.IsNullOrWhiteSpace(item.Event.Detail) ? item.Event.Title : item.Event.Detail;
                var timeText = !string.IsNullOrWhiteSpace(item.Event.TimestampDisplay)
                    ? item.Event.TimestampDisplay
                    : item.Summary.WindowEnd.ToLocalTime().ToString("h:mm tt");
                body.Append("<li>");
                body.Append($"<b>{WebUtility.HtmlEncode(item.Event.Title)}</b>{WebUtility.HtmlEncode(alertBadge)}");
                body.Append($"<br/><span>{WebUtility.HtmlEncode(detail)}</span>");
                body.Append($"<br/><small>Time: {WebUtility.HtmlEncode(timeText)} | Confidence: {item.Event.Confidence:0.00} | Rank score: {score:0.00}</small>");
                body.Append("</li>");
            }

            body.Append("</ol>");
        }

        body.Append("</body></html>");

        EmailSender.SendHtml(
            _settings,
            "pizzawave insights",
            _settings.EmailUser!,
            $"PizzaWave daily insights digest ({digestDate:yyyy-MM-dd})",
            body.ToString());
        Trace(TraceLoggerType.Insights, TraceEventType.Information,
            $"Sent daily insights digest for {digestDate:yyyy-MM-dd} with {grouped.Count} category sections.");
        return true;
    }

    private static bool IsDailyDigestConfigured(Settings settings)
    {
        return settings.DailyInsightsDigestEnabled
               && settings.LmLinkEnabled
               && !string.IsNullOrWhiteSpace(settings.LmLinkBaseUrl)
               && !string.IsNullOrWhiteSpace(settings.LmLinkModel)
               && !string.IsNullOrWhiteSpace(settings.EmailUser)
               && !string.IsNullOrWhiteSpace(settings.EmailPassword);
    }

    private async Task RunRetentionIfNeeded()
    {
        if (DateTimeOffset.UtcNow - _lastRetentionRun < TimeSpan.FromHours(24))
            return;
        _lastRetentionRun = DateTimeOffset.UtcNow;
        await _storage.RunRetentionAsync();
        }
}

internal static class LmLinkClient
{
    private const int MaxPromptChars = 120_000;
    private const int MaxNotableEventsFromLm = 80;
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient
        {
            // Use Settings.LmLinkTimeoutMs via per-request CTS; avoid HttpClient's 100s default timeout.
            Timeout = Timeout.InfiniteTimeSpan
        };
        return client;
    }

    public static async Task<(string SummaryText, List<InsightNotableEvent> NotableEvents)> SummarizeWindowAsync(
        Settings settings,
        DateTimeOffset start,
        DateTimeOffset end,
        List<TranscribedCall> calls,
        string? priorSummary)
    {
        var endpoint = BuildEndpoint(settings);
        Trace(TraceLoggerType.Insights, TraceEventType.Information, 
            $"LM Link endpoint: {endpoint}");

        var prompt = BuildPrompt(start, end, calls, priorSummary);

        Trace(TraceLoggerType.Insights, TraceEventType.Information, 
            $"Context size: {prompt.Length} characters, {calls.Count} calls");

        var body = new
        {
            model = settings.LmLinkModel,
            temperature = 0.1, // Lower for more deterministic JSON output
            max_tokens = 8000,
            response_format = new 
            {
                type = "json_schema",
                json_schema = new 
                {
                    name = "insights_summary",
                    strict = true,
                    schema = new 
                    {
                        type = "object",
                        properties = new 
                        {
                            summary_text = new { type = "string" },
                            notable_events = new 
                            {
                                type = "array",
                                maxItems = 20,
                                items = new 
                                {
                                    type = "object",
                                     properties = new 
                                     {
                                         title = new { type = "string", maxLength = 120 },
                                         detail = new { type = "string", maxLength = 240 },
                                         category = new { type = "string" },
                                         timestamp = new { type = "string" },
                                        confidence = new { type = "number" },
                                        call_ids = new
                                        {
                                            type = "array",
                                            minItems = 1,
                                            maxItems = 3,
                                            items = new
                                            {
                                                type = "string",
                                                pattern = "^C[0-9A-F]{12}$"
                                            }
                                        }
                                     },
                                     required = new string[] { "title", "detail", "category", "timestamp", "confidence", "call_ids" }
                                 }
                             }
                        },
                        required = new string[] { "summary_text", "notable_events" }
                    }
                }
            },
            messages = new[]
            {
                new { role = "system", content = "You summarize radio call transcripts into concise, actionable insights. Output JSON with fields summary_text and notable_events (list of {title, detail, category, timestamp, confidence, call_ids}). For each notable event: choose exactly one category from [police, fire, ems, traffic, public_works, utilities, other], set timestamp as local HH:mm (24h), and include 1-3 matching call_ids copied exactly from the provided call lines. Do not collapse many distinct incidents into only a few bullets; include broad coverage across distinct incidents with strong evidence." },
                new { role = "user", content = prompt }
            }
        };

        var payload = JsonConvert.SerializeObject(body);
        var retries = Math.Max(0, settings.LmLinkMaxRetries);
        Exception? lastError = null;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                    $"LM Link request attempt {attempt + 1} to {endpoint}");
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(settings.LmLinkApiKey))
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.LmLinkApiKey);
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(settings.LmLinkTimeoutMs));
                using var resp = await SharedHttpClient.SendAsync(request, cts.Token);
                var text = await resp.Content.ReadAsStringAsync(cts.Token);
                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"LM Link HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {TrimForLog(text)}");
                
Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                    $"LM Link response received from attempt {attempt + 1}");

                var parsedResponse = DeserializeChatCompletion(text);
                var finishReason = parsedResponse?.Choices?.FirstOrDefault()?.FinishReason;
                if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("LM Link output was truncated (finish_reason=length). Reduce requested event volume or increase completion token limit.");
                }
                if (parsedResponse?.Usage != null)
                {
                    var promptTokens = parsedResponse.Usage.PromptTokens ?? parsedResponse.Usage.PromptTokensSnake ?? 0;
                    var completionTokens = parsedResponse.Usage.CompletionTokens ?? parsedResponse.Usage.CompletionTokensSnake ?? 0;
                    var totalTokens = parsedResponse.Usage.TotalTokens ?? 0;
                    Trace(TraceLoggerType.Insights, TraceEventType.Information,
                        $"LM Link usage: prompt_tokens={promptTokens}, completion_tokens={completionTokens}, total_tokens={totalTokens}");
                }
                if (!string.IsNullOrWhiteSpace(parsedResponse?.Model))
                {
                    Trace(TraceLoggerType.Insights, TraceEventType.Information,
                        $"LM Link model: {parsedResponse!.Model}");
                }

                var content = ExtractMessageContent(text);
                var result = ParseSummary(content, MaxNotableEventsFromLm);

                Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                    $"LM Link parsed window summary with {result.NotableEvents.Count} notable events");
                return result;
            }
            catch (TaskCanceledException ex)
            {
                var timedOut = ex.CancellationToken.IsCancellationRequested;
                var message = timedOut
                    ? $"LM Link request timed out after {settings.LmLinkTimeoutMs} ms (server likely still processing when client canceled)."
                    : "LM Link request was canceled/disconnected before response completed.";
                throw new Exception(message, ex);
            }
            catch (Exception ex) when (attempt < retries)
            {
                lastError = ex;
                Trace(TraceLoggerType.Insights, TraceEventType.Warning, 
                    $"LM Link window summary attempt {attempt + 1} failed: {ex.Message}");
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                lastError = ex;
                Trace(TraceLoggerType.Insights, TraceEventType.Error, 
                    $"LM Link window summary request failed after all attempts: {ex.Message}");
                break;
            }
        }

        throw new Exception($"LM Link window summary request failed: {lastError?.Message ?? "unknown error"}");
        }

    private static string BuildPrompt(DateTimeOffset start, DateTimeOffset end, List<TranscribedCall> calls, string? priorSummary)
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
        sb.AppendLine($"Window: {start} to {end}");
        if (!string.IsNullOrWhiteSpace(priorSummary))
        {
            sb.AppendLine("Prior summary:");
            sb.AppendLine(priorSummary);
        }
        sb.AppendLine("Category guidance: each notable event must use one of these categories exactly:");
        sb.AppendLine("police, fire, ems, traffic, public_works, utilities, other");
        sb.AppendLine("Timestamp guidance: include timestamp as local HH:mm (24h) using the provided call times.");
        sb.AppendLine("Linkage guidance: each notable event must include 1-3 call_ids copied exactly from input lines.");
        sb.AppendLine($"Coverage guidance: target about {targetEventCount} notable_events for this window when evidence supports it (never fewer than 6 unless there are truly fewer distinct incidents). Keep each detail concise (1 sentence).");
        
        // Prioritize alert calls, then include as many as fit prompt budget.
        var prioritizedCalls = calls
            .OrderByDescending(c => c.IsAlertMatch)
            .ThenByDescending(c => c.StopTime - c.StartTime);

        var lines = new List<string>();
        var usedChars = sb.Length;
        foreach (var call in prioritizedCalls)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime().ToString("h:mm tt");
            var prefix = call.IsAlertMatch ? "[ALERT] " : "";
            var callId = CallHash.ComputeCallId(call);
            var line = $"- [id:{callId}] [{time}] TG {call.Talkgroup}: {prefix}{call.Transcription}";
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

    internal static string ExtractMessageContent(string responseJson)
    {
        try
        {
            var root = JsonConvert.DeserializeObject<JObject>(responseJson);
            var choice = root?["choices"]?.FirstOrDefault();
            var contentToken = choice?["message"]?["content"];
            var content = ExtractContentFromToken(contentToken);
            if (string.IsNullOrWhiteSpace(content))
                content = choice?["text"]?.ToString() ?? string.Empty;
            
            Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                $"ExtractMessageContent: extracted {content.Length} chars");
            
            return content;
        }
        catch (Exception ex)
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Warning, 
                $"ExtractMessageContent failed to deserialize, returning raw response. Error: {ex.Message}");
            return responseJson;
        }
        }

    private static string ExtractContentFromToken(JToken? token)
    {
        if (token == null)
            return string.Empty;

        if (token.Type == JTokenType.String)
            return token.ToString();

        if (token.Type == JTokenType.Array)
        {
            var parts = token.Children()
                .Select(t => t?["text"]?.ToString() ?? t?.ToString() ?? string.Empty)
                .Where(s => !string.IsNullOrWhiteSpace(s));
            return string.Join(Environment.NewLine, parts);
        }

        if (token.Type == JTokenType.Object)
            return token["text"]?.ToString() ?? token.ToString();

        return token.ToString();
    }

    internal static Uri BuildEndpoint(Settings settings)
    {
        var baseUrlRaw = settings.LmLinkBaseUrl?.Trim();
        if (string.IsNullOrWhiteSpace(baseUrlRaw))
            throw new Exception("LM Link base URL is empty.");

        if (!baseUrlRaw.Contains("://", StringComparison.Ordinal))
            baseUrlRaw = "http://" + baseUrlRaw;

        if (!Uri.TryCreate(baseUrlRaw, UriKind.Absolute, out var baseUri))
            throw new Exception($"Invalid LM Link base URL: '{settings.LmLinkBaseUrl}'. Expected http(s)://host:port");

        var builder = new UriBuilder(baseUri);
        var path = builder.Path.EndsWith("/") 
            ? builder.Path.TrimEnd('/') + "/v1/chat/completions" 
            : builder.Path + "/v1/chat/completions";
        builder.Path = path;
        return builder.Uri;
        }

    internal static string TrimForLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        text = text.Trim();
        return text.Length > 300 ? text.Substring(0, 300) + "..." : text;
        }

    internal static (string SummaryText, List<InsightNotableEvent> NotableEvents) ParseSummary(string content, int maxNotableEvents = MaxNotableEventsFromLm)
    {
        try
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                $"ParseSummary called. Content length: {content.Length} chars");
            
            var cleanContent = StripThinkingBlocks(content);
            Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                $"After stripping thinking blocks: {cleanContent.Length} chars");
            
            if (string.IsNullOrWhiteSpace(cleanContent))
            {
                Trace(TraceLoggerType.Insights, TraceEventType.Warning, "Clean content is empty after stripping blocks");
                return (content, new List<InsightNotableEvent>());
            }

            LmSummaryPayload? obj = null;
            try
            {
                obj = JsonConvert.DeserializeObject<LmSummaryPayload>(cleanContent);
            }
            catch (JsonReaderException ex)
            {
                Trace(TraceLoggerType.Insights, TraceEventType.Error, 
                    $"JSON parse error: {ex.Message}");
                var start = Math.Max(0, ex.LinePosition - 1);
                var end = Math.Min(cleanContent.Length, start + 200);
                Trace(TraceLoggerType.Insights, TraceEventType.Error, 
                    $"Context around error: '{TrimForLog(cleanContent.Substring(start, end - start))}'");
                return (content, new List<InsightNotableEvent>());
            }
            
            if (obj == null)
            {
                Trace(TraceLoggerType.Insights, TraceEventType.Warning, 
                    $"JSON deserialization returned null. First 200 chars: {TrimForLog(cleanContent)}");
                return (content, new List<InsightNotableEvent>());
            }

            Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                $"Deserialized successfully. Has summary_text? {(!string.IsNullOrWhiteSpace(obj.SummaryText))}, Has notable_events? {(obj.NotableEvents != null)}");

            if (!string.IsNullOrWhiteSpace(obj.SummaryText))
            {
                var summary = obj.SummaryText!;
                Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                    $"Summary text length: {summary.Length} chars");
                
                var events = new List<InsightNotableEvent>();
                try
                {
                    if (obj.NotableEvents != null)
                    {
                        int eventCount = 0;
                        foreach (var ev in obj.NotableEvents)
                        {
                            eventCount++;
                            var title = ev.Title ?? string.Empty;
                            var detail = ev.Detail ?? string.Empty;
                            var category = ev.Category ?? "other";
                            var timestamp = ev.Timestamp ?? string.Empty;
                            var confidence = ev.Confidence;
                            var callIds = (ev.CallIds ?? new List<string>())
                                .Where(IsValidCallId)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                            var callHashes = (ev.CallHashes ?? new List<string>())
                                .Where(h => !string.IsNullOrWhiteSpace(h))
                                .ToList()!;
                            
                            Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                                $"Event {eventCount}: Title='{title}', Confidence={confidence}");
                            
                            events.Add(new InsightNotableEvent
                            {
                                Title = title ?? string.Empty,
                                Detail = detail ?? string.Empty,
                                Category = category ?? "other",
                                Timestamp = timestamp ?? string.Empty,
                                Confidence = confidence,
                                CallIds = callIds,
                                CallHashes = callHashes
                            });
                        }
                        Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                            $"Parsed {eventCount} notable events from response");
                    }
                    else
                    {
                        Trace(TraceLoggerType.Insights, TraceEventType.Warning, "notable_events field is null in parsed JSON");
                    }
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.Insights, TraceEventType.Error, 
                        $"Failed to iterate notable_events: {ex.Message}");
                }

                var sortedEvents = events.OrderByDescending(e => e.Confidence).Take(maxNotableEvents).ToList();
                Trace(TraceLoggerType.Insights, TraceEventType.Information, 
                    $"Returning {sortedEvents.Count} notable events (max={maxNotableEvents})");
                
                return (summary, sortedEvents);
            }
            else
            {
                Trace(TraceLoggerType.Insights, TraceEventType.Warning, "summary_text field is null in parsed JSON");
            }
        }
        catch (Exception ex)
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Error, 
                $"ParseSummary exception: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Trace(TraceLoggerType.Insights, TraceEventType.Error, 
                    $"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
        }

        return (content, new List<InsightNotableEvent>());
        }

    private static LmChatCompletionResponse? DeserializeChatCompletion(string responseJson)
    {
        try
        {
            return JsonConvert.DeserializeObject<LmChatCompletionResponse>(responseJson);
        }
        catch (Exception ex)
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Warning,
                $"Failed to parse LM Link response envelope: {ex.Message}");
            return null;
        }
    }

    private static string StripThinkingBlocks(string content)
    {
        var result = content;
        Trace(TraceLoggerType.Insights, TraceEventType.Information, 
            $"StripThinkingBlocks called. Input length: {result.Length} chars");
        
        // Remove <thinking>...</thinking> blocks (case insensitive)
        var pattern1 = @"<thinking>.*?</thinking>";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern1, "", 
            System.Text.RegularExpressions.RegexOptions.Singleline | 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove ```think ... ``` blocks
        var pattern2 = @"```think\s*\n?([\s\S]*?)\n?```";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern2, "", 
            System.Text.RegularExpressions.RegexOptions.Singleline | 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove ```json ... ``` blocks (in case JSON is wrapped) - extract inner content
        var pattern3 = @"```json\s*\n?([\s\S]*?)\n?```";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern3, "$1", 
            System.Text.RegularExpressions.RegexOptions.Singleline | 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove plain markdown code blocks that aren't JSON - extract inner content
        var pattern4 = @"```\n?([\s\S]*?)\n?```";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern4, "$1", 
            System.Text.RegularExpressions.RegexOptions.Singleline | 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
// Remove LLM thinking process markers like "Thinking Process:" or "Analysis:" - only at start of content
        var pattern5 = @"^[^\{]*?(?:[Cc]onsidering|[Tt]hinking\s+[Pp]rocess|[Aa]nalysis|Analyzing|Extracting|Filtering)[\s\S]*?(?=\{)";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern5, "", 
            System.Text.RegularExpressions.RegexOptions.Singleline | 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove standalone "</think>" text markers (common in some LLM outputs)
        var pattern6 = @"</think>";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern6, "", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove any remaining thinking process lines that start at beginning of content
        var pattern7 = @"^[^\{]*?Thinking Process:[\s\S]*?(?=\{)";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern7, "", 
            System.Text.RegularExpressions.RegexOptions.Singleline | 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
// Remove "Thinking" marker followed by space and text until JSON starts - only at start of content (handles "Thinking Process:")
        var pattern8 = @"^[^\{]*?Thinking\s+Process:[\s\S]*?(?=\{)";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern8, "", 
            System.Text.RegularExpressions.RegexOptions.Singleline | 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Remove "Thinking Process:" at start of content followed by text until JSON starts  
        var pattern9 = @"^[^\{]*?Thinking Process:[\s\S]*?(?=\{)";
        result = System.Text.RegularExpressions.Regex.Replace(result, pattern9, "", 
            System.Text.RegularExpressions.RegexOptions.Singleline | 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        // Validate that result is valid JSON after stripping - if not, return original content
        try
        {
            JsonConvert.DeserializeObject<dynamic>(result.Trim());
        }
        catch (JsonReaderException)
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Warning, 
                $"Stripping corrupted JSON. Returning original content.");
            return content; // Return unmodified if stripping broke it
        }
        
        Trace(TraceLoggerType.Insights, TraceEventType.Information, 
            $"After stripping: {result.Length} chars. First 200 chars:\n{TrimForLog(result)}");
        
        return result.Trim();
        }

    private sealed class LmChatCompletionResponse
    {
        [JsonProperty("model")]
        public string? Model { get; set; }
        [JsonProperty("usage")]
        public LmUsage? Usage { get; set; }
        [JsonProperty("choices")]
        public List<LmChoice>? Choices { get; set; }
    }

    private sealed class LmUsage
    {
        [JsonProperty("promptTokens")]
        public int? PromptTokens { get; set; }
        [JsonProperty("completionTokens")]
        public int? CompletionTokens { get; set; }
        [JsonProperty("prompt_tokens")]
        public int? PromptTokensSnake { get; set; }
        [JsonProperty("completion_tokens")]
        public int? CompletionTokensSnake { get; set; }
        [JsonProperty("totalTokens")]
        public int? TotalTokens { get; set; }
        [JsonProperty("total_tokens")]
        public int? TotalTokensSnake
        {
            set => TotalTokens = value;
        }
    }

    private sealed class LmChoice
    {
        [JsonProperty("message")]
        public LmMessage? Message { get; set; }
        [JsonProperty("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class LmMessage
    {
        [JsonProperty("content")]
        public string? Content { get; set; }
    }

    private sealed class LmSummaryPayload
    {
        [JsonProperty("summary_text")]
        public string? SummaryText { get; set; }
        [JsonProperty("notable_events")]
        public List<LmNotableEvent>? NotableEvents { get; set; }
    }

    private sealed class LmNotableEvent
    {
        [JsonProperty("title")]
        public string? Title { get; set; }
        [JsonProperty("detail")]
        public string? Detail { get; set; }
        [JsonProperty("category")]
        public string? Category { get; set; }
        [JsonProperty("timestamp")]
        public string? Timestamp { get; set; }
        [JsonProperty("confidence")]
        public double Confidence { get; set; }
        [JsonProperty("call_ids")]
        public List<string>? CallIds { get; set; }
        [JsonProperty("call_hashes")]
        public List<string>? CallHashes { get; set; }
    }

    private static bool IsValidCallId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var s = value.Trim().ToUpperInvariant();
        if (s.Length != 13 || s[0] != 'C')
            return false;

        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }
        return true;
    }
}

internal class InsightsStorage
{
    private readonly string _basePath = Path.Combine(Settings.DefaultWorkingDirectory, "insights");
    private static readonly object SharedCacheLock = new();
    private static Dictionary<string, InsightSummaryWindow>? SharedSummaryCache;

    private string GetWindowPath(DateTimeOffset timestamp)
    {
        return Path.Combine(Settings.DefaultWorkingDirectory, "insights",
            timestamp.ToString("yyyy"), timestamp.ToString("MM"), timestamp.ToString("dd"), $"{timestamp:HHmm}.json");
    }

public void EnsureDirectories()
    {
        Directory.CreateDirectory(_basePath);
    }

    public void SaveWindowSummary(InsightSummaryWindow summary)
    {
        var windowPath = GetWindowPath(summary.WindowEnd);
        Directory.CreateDirectory(Path.GetDirectoryName(windowPath)!);
        File.WriteAllText(windowPath, JsonConvert.SerializeObject(summary, Formatting.Indented));
        lock (SharedCacheLock)
        {
            SharedSummaryCache ??= new Dictionary<string, InsightSummaryWindow>(StringComparer.OrdinalIgnoreCase);
            SharedSummaryCache[windowPath] = summary;
        }
    }

    private InsightSummaryWindow? ReadSummary(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<InsightSummaryWindow>(json);
        }
        catch (Exception ex)
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Warning,
                $"Failed to read insights summary '{path}': {ex.Message}");
            return null;
        }
    }

    public bool HasRecentSummaryFiles(TimeSpan lookback)
    {
        if (!Directory.Exists(_basePath))
            return false;

        var cutoff = DateTimeOffset.Now - lookback;
        foreach (var file in Directory.EnumerateFiles(_basePath, "*.json", SearchOption.AllDirectories))
        {
            if (!IsSummaryPath(file))
                continue;

            try
            {
                var write = File.GetLastWriteTime(file);
                if (write >= cutoff.LocalDateTime)
                    return true;
            }
            catch
            {
                // Ignore IO errors while checking recency.
            }
        }

        return false;
    }

public async Task RunRetentionAsync()
    {
        await Task.Run(() =>
        {
            DeleteOlderThan(_basePath, TimeSpan.FromDays(30));
        });
        InvalidateCache();
    }

    private void DeleteOlderThan(string root, TimeSpan age)
    {
        if (!Directory.Exists(root)) return;
        var cutoff = DateTimeOffset.Now - age;
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                    info.Delete();
            }
            catch { }
        }
    }

    public List<InsightSummaryWindow> LoadDaily(DateTimeOffset start, DateTimeOffset end)
    {
        var snapshot = GetSummarySnapshot();
        return snapshot
            .Where(summary => summary.WindowEnd >= start && summary.WindowStart <= end)
            .OrderByDescending(r => r.WindowStart)
            .ToList();
    }

    private List<InsightSummaryWindow> GetSummarySnapshot()
    {
        lock (SharedCacheLock)
        {
            if (SharedSummaryCache == null)
            {
                var built = new Dictionary<string, InsightSummaryWindow>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(_basePath))
                {
                    foreach (var file in Directory.EnumerateFiles(_basePath, "*.json", SearchOption.AllDirectories))
                    {
                        if (!IsSummaryPath(file)) continue;
                        var summary = ReadSummary(file);
                        if (summary != null)
                            built[file] = summary;
                    }
                }
                SharedSummaryCache = built;
            }

            return SharedSummaryCache.Values.ToList();
        }
    }

    private void InvalidateCache()
    {
        lock (SharedCacheLock)
        {
            SharedSummaryCache = null;
        }
    }

    private bool IsSummaryPath(string file)
    {
        var relative = Path.GetRelativePath(_basePath, file);
        var parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        return parts[0].Length == 4 && parts[1].Length == 2 && parts[2].Length == 2;
    }
}

