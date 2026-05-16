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
    private const int IncidentPromptCharLimit = 24_000;
    private const int IncidentTranscriptCharLimit = 320;
    private const int IncidentTitleCharLimit = 90;
    private const int IncidentDetailCharLimit = 260;
    private const int IncidentMaxCandidateCalls = 120;
    private const int IncidentMaxOutputTokens = 2_800;
    private const int EvidenceVerifierPromptCharLimit = 20_000;
    private const int EvidenceVerifierTranscriptCharLimit = 260;
    private const int EvidenceVerifierMaxNearbyCalls = 45;
    private const int EvidenceVerifierMaxCalls = 60;
    private const int EvidenceVerifierMaxOutputTokens = 2_400;
    private const int NormalMaxOutputTokens = 2_000;
    private const int CompactMaxOutputTokens = 1_200;
    private const int CompactMaxEvents = 4;
    private static readonly TimeSpan MaxIncidentSpan = TimeSpan.FromMinutes(60);
    private readonly ConcurrentQueue<EngineCall> _queue = new();
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly ILogger<AutomaticInsightsService> _logger;
    private readonly List<EngineCall> _pending = new();
    private readonly object _gate = new();
    private string? _priorSummary;
    private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextIncidentRunAt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextQueueGateLogAt = DateTimeOffset.MinValue;
    private int _failureStreak;

    public AutomaticInsightsService(
        EngineConfig config,
        EngineDatabase database,
        EventStream events,
        ILogger<AutomaticInsightsService> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _logger = logger;
    }

    public void Enqueue(EngineCall call)
    {
        if (!_config.Setup.Completed || !IsEnabled())
            return;
        _queue.Enqueue(call);
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
            .ToList();
        if (calls.Count == 0)
            return 0;

        var start = calls.Min(c => c.StartTime);
        var end = calls.Max(c => Math.Max(c.StartTime, c.StopTime));
        var result = await SummarizeWindowAsync(calls, start, end, InsightPromptMode.CompactManual, ct);
        var windowId = await _database.AddInsightWindowAsync(start, end, result.SummaryText, ct);
        await PersistInsightEventsAsync(windowId, result, calls, ct);
        _priorSummary = result.SummaryText;
        await _events.PublishAsync("summary_updated", new { windowId, start, end, incidents = 0 }, ct);
        _logger.LogInformation("Manual insights generated window {WindowId} with insight events only from {Calls} calls", windowId, calls.Count);
        return 0;
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

        if (DateTimeOffset.UtcNow < _nextIncidentRunAt)
            return;

        List<EngineCall> batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
                return;

            batch = _pending.ToList();
        }

        if (batch.Count == 0)
            return;

        var start = DateTimeOffset.UtcNow.Add(MaxIncidentSpan.Negate()).ToUnixTimeSeconds();
        var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            var incidents = 0;
            var stale = await _database.ConcludeStaleManagedIncidentsAsync(start, ct);
            foreach (var system in batch.Select(c => c.SystemShortName).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var newCallIds = batch
                    .Where(c => string.Equals(c.SystemShortName, system, StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Id)
                    .ToHashSet();
                incidents += await ExtractIncidentsForSystemAsync(system, start, end, newCallIds, ct);
            }

            lock (_gate)
            {
                var ids = batch.Select(c => c.Id).ToHashSet();
                _pending.RemoveAll(c => ids.Contains(c.Id));
            }
            _failureStreak = 0;
            _nextAttemptAt = DateTimeOffset.UtcNow;
            _nextIncidentRunAt = DateTimeOffset.UtcNow.AddMinutes(5);
            await _events.PublishAsync("summary_updated", new { windowId = 0, start, end, incidents }, ct);
            _logger.LogInformation("Automatic incident extraction updated {Incidents} incident(s), concluded {StaleIncidents} stale incident(s), from {Calls} new call(s)", incidents, stale, batch.Count);
        }
        catch (Exception ex)
        {
            _failureStreak++;
            var cooldownSeconds = Math.Min(300, 5 * (1 << Math.Min(_failureStreak, 5)));
            _nextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);
            _nextIncidentRunAt = DateTimeOffset.UtcNow.AddSeconds(cooldownSeconds);
            RotateFailedBatch(batch);
            _logger.LogWarning(ex, "Automatic incident extraction failed for {Count} calls; backing off {Seconds}s", batch.Count, cooldownSeconds);
        }
    }

    private async Task<int> ExtractIncidentsForSystemAsync(string systemShortName, long start, long end, HashSet<long> newCallIds, CancellationToken ct)
    {
        var recent = (await _database.ListCallsAsync(start, end, null, ct))
            .Where(c => string.Equals(c.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase))
            .Where(IsIncidentEligibleCall)
            .OrderBy(c => c.StartTime)
            .ToList();
        if (recent.Count == 0)
            return 0;

        var activeIncidents = (await _database.ListIncidentsAsync(start, end, ct))
            .Where(i => !string.Equals(i.Status, "concluded", StringComparison.OrdinalIgnoreCase))
            .Where(i => i.Calls.Any(c => string.Equals(c.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var activeCallIds = activeIncidents.SelectMany(i => i.Calls.Select(c => c.CallId)).ToHashSet();
        var configuredCandidateLimit = _config.AiInsights.MaxManualSummaryCalls <= 0 ? IncidentMaxCandidateCalls : _config.AiInsights.MaxManualSummaryCalls;
        var candidateLimit = Math.Clamp(configuredCandidateLimit, 20, IncidentMaxCandidateCalls);
        var newCandidateCalls = recent
            .Where(c => newCallIds.Contains(c.Id))
            .Where(c => !activeCallIds.Contains(c.Id))
            .OrderBy(c => c.StartTime)
            .Take(candidateLimit)
            .ToList();
        var selectedIds = newCandidateCalls.Select(c => c.Id).ToHashSet();
        var carryoverLimit = Math.Max(0, candidateLimit - newCandidateCalls.Count);
        var carryoverCalls = recent
            .Where(c => !selectedIds.Contains(c.Id))
            .Where(c => !activeCallIds.Contains(c.Id))
            .OrderByDescending(c => c.StartTime)
            .Take(carryoverLimit)
            .OrderBy(c => c.StartTime)
            .ToList();
        var candidateCalls = carryoverCalls
            .Concat(newCandidateCalls)
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.StartTime)
            .ToList();
        if (candidateCalls.Count == 0 && activeIncidents.Count == 0)
            return 0;

        _logger.LogInformation(
            "Prepared incident extraction candidates for {System}: {NewCandidates} new, {CarryoverCandidates} carryover, {CandidateCalls} total, {RecentEligibleCalls} eligible calls in rolling state window, {ActiveIncidents} active incident(s)",
            systemShortName,
            newCandidateCalls.Count,
            carryoverCalls.Count,
            candidateCalls.Count,
            recent.Count,
            activeIncidents.Count);

        var result = await ExtractIncidentStateAsync(systemShortName, activeIncidents, candidateCalls, start, end, ct);
        return await PersistIncidentStateAsync(systemShortName, result, activeIncidents, recent, ct);
    }

    private async Task<IncidentExtractionResult> ExtractIncidentStateAsync(string systemShortName, List<IncidentDto> activeIncidents, List<EngineCall> candidateCalls, long start, long end, CancellationToken ct)
    {
        var baseUrl = InsightBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var prompt = BuildIncidentExtractionPrompt(systemShortName, activeIncidents, candidateCalls, start, end);
        var body = new
        {
            model = InsightModel(),
            temperature = 0.1,
            max_tokens = IncidentMaxOutputTokens,
            response_format = IncidentExtractionResponseFormat(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You maintain an incremental incident state from public safety radio transcripts. Output JSON only. Incidents are site-local real-world events supported by concrete transcript evidence. Do not group by category; a single incident may span police, fire, EMS, traffic, utilities, or other talkgroups. Do not create incidents for routine acknowledgements, generic status/admin traffic, or weak topical buckets."
                },
                new { role = "user", content = prompt }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling incident extraction endpoint {Endpoint} with model {Model} for {System} ({ActiveIncidents} active, {CandidateCalls} candidate calls, {PayloadChars} chars)", endpoint, InsightModel(), systemShortName, activeIncidents.Count, candidateCalls.Count, payload.Length);
        Exception? last = null;
        for (var attempt = 0; attempt <= Math.Max(0, _config.AiInsights.MaxRetries); attempt++)
        {
            try
            {
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                using var response = await client.PostAsync(endpoint, content, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Incident extraction request failed with HTTP {(int)response.StatusCode}: {Trim(text, 1000)}");
                var usage = ExtractUsage(text);
                if (string.Equals(usage.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    var message = $"LM incident extraction response was truncated at max_tokens={IncidentMaxOutputTokens}.";
                    await RecordUsageAsync(text, endpoint, payload.Length, candidateCalls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, message, ct);
                    throw new InsightResponseTruncatedException(message);
                }

                var parsed = ParseIncidentExtractionResponse(text);
                await RecordUsageAsync(text, endpoint, payload.Length, candidateCalls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, true, string.Empty, ct);
                return parsed;
            }
            catch (InsightResponseTruncatedException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < Math.Max(0, _config.AiInsights.MaxRetries))
            {
                last = ex;
                await RecordUsageAsync(string.Empty, endpoint, payload.Length, candidateCalls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, ex.Message, CancellationToken.None);
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                last = ex;
                await RecordUsageAsync(string.Empty, endpoint, payload.Length, candidateCalls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, ex.Message, CancellationToken.None);
                break;
            }
        }

        throw new InvalidOperationException(last?.Message ?? "Incident extraction request failed.", last);
    }

    private async Task<int> PersistIncidentStateAsync(string systemShortName, IncidentExtractionResult result, List<IncidentDto> activeIncidents, List<EngineCall> recentCalls, CancellationToken ct)
    {
        var byToken = new Dictionary<string, EngineCall>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in recentCalls)
        {
            byToken[CallToken(call.Id)] = call;
            byToken[call.Id.ToString(CultureInfo.InvariantCulture)] = call;
            byToken[call.Id.ToString("X12", CultureInfo.InvariantCulture)] = call;
        }

        var existingByKey = activeIncidents
            .Where(i => !string.IsNullOrWhiteSpace(i.IncidentKey))
            .ToDictionary(i => i.IncidentKey, StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        var claimed = new HashSet<long>();
        foreach (var item in result.Incidents.Take(40))
        {
            var callIds = item.CallIds
                .Select(id => byToken.TryGetValue(id.Trim(), out var call) ? call : null)
                .Where(c => c != null)
                .DistinctBy(c => c!.Id)
                .Cast<EngineCall>()
                .Where(c => !claimed.Contains(c.Id))
                .ToList();
            if (callIds.Count < 2)
                continue;

            var status = string.Equals(item.Status, "concluded", StringComparison.OrdinalIgnoreCase) ? "concluded" : "active";
            var key = ResolveIncidentKey(systemShortName, item, callIds, existingByKey);
            var title = TrimIncidentText(item.Title ?? string.Empty, IncidentTitleCharLimit);
            var detail = TrimIncidentText(item.Detail ?? string.Empty, IncidentDetailCharLimit);
            if (existingByKey.TryGetValue(key, out var existingIncident))
            {
                var existingCalls = existingIncident.Calls
                    .Select(c => byToken.TryGetValue(CallToken(c.CallId), out var call) ? call : null)
                    .Where(c => c != null)
                    .Cast<EngineCall>()
                    .ToList();
                callIds = callIds
                    .Concat(existingCalls)
                    .DistinctBy(c => c.Id)
                    .Where(c => !claimed.Contains(c.Id))
                    .OrderBy(c => c.StartTime)
                    .ToList();
            }

            if (!IsIncidentNarrativeAcceptable(title, detail, callIds))
            {
                _logger.LogInformation(
                    "Rejected incident state item '{Title}' for {System}: detail is too long or transcript-like. DetailLength={DetailLength}; Calls={CallIds}",
                    item.Title,
                    systemShortName,
                    item.Detail?.Length ?? 0,
                    string.Join(",", callIds.Select(c => c.Id)));
                continue;
            }

            callIds = await VerifyIncidentEvidenceAsync(systemShortName, title, detail, key, callIds, existingIncident, recentCalls, claimed, ct);
            if (callIds.Count < 2)
            {
                _logger.LogInformation("Rejected incident state item '{Title}' for {System}: evidence verifier retained fewer than 2 call(s)", title, systemShortName);
                continue;
            }

            var validation = IncidentCandidateValidator.Validate(title, detail, callIds.Select(ToIncidentCandidateCall).ToList());
            if (!validation.IsValid)
            {
                _logger.LogInformation("Rejected incident state item '{Title}' for {System}: {Reason}. Calls={CallIds}", title, systemShortName, validation.Reason, string.Join(",", callIds.Select(c => c.Id)));
                continue;
            }

            var retained = validation.Calls.Select(c => c.CallId).ToHashSet();
            callIds = callIds.Where(c => retained.Contains(c.Id)).ToList();
            var id = await _database.UpsertManagedIncidentAsync(new IncidentDto
            {
                IncidentKey = key,
                Title = string.IsNullOrWhiteSpace(title) ? "Radio incident" : title,
                Detail = string.IsNullOrWhiteSpace(detail) ? "Multiple related calls describe the same incident." : detail,
                Category = NormalizeCategory(item.Category, callIds),
                Status = status,
                FirstSeen = callIds.Min(c => c.StartTime),
                LastSeen = callIds.Max(c => c.StartTime),
                Confidence = Math.Clamp(item.Confidence, 0, 1),
                Calls = callIds.Select(c => new IncidentCallDto(c.Id, c.StartTime, c.Transcription, $"/api/v1/calls/{c.Id}/audio", c.Category, c.TalkgroupName, c.SystemShortName)).ToList()
            }, ct);
            if (id <= 0)
                continue;

            foreach (var call in callIds)
                claimed.Add(call.Id);
            updated++;
        }

        return updated;
    }

    private async Task<List<EngineCall>> VerifyIncidentEvidenceAsync(
        string systemShortName,
        string title,
        string detail,
        string incidentKey,
        List<EngineCall> selectedCalls,
        IncidentDto? existingIncident,
        List<EngineCall> recentCalls,
        HashSet<long> claimed,
        CancellationToken ct)
    {
        var selectedIds = selectedCalls.Select(c => c.Id).ToHashSet();
        var existingIds = existingIncident?.Calls.Select(c => c.CallId).ToHashSet() ?? [];
        var first = selectedCalls.Min(c => c.StartTime);
        var last = selectedCalls.Max(c => c.StartTime);
        var reviewStart = first - 600;
        var reviewEnd = last + 600;
        var midpoint = first + Math.Max(0, last - first) / 2;
        var nearby = recentCalls
            .Where(c => c.StartTime >= reviewStart && c.StartTime <= reviewEnd)
            .Where(c => !selectedIds.Contains(c.Id))
            .Where(c => !claimed.Contains(c.Id))
            .OrderBy(c => Math.Abs(c.StartTime - midpoint))
            .ThenBy(c => c.StartTime)
            .ToList();
        var truncatedNearby = Math.Max(0, nearby.Count - EvidenceVerifierMaxNearbyCalls);
        nearby = nearby.Take(EvidenceVerifierMaxNearbyCalls).OrderBy(c => c.StartTime).ToList();
        var reviewCalls = selectedCalls
            .Concat(nearby)
            .DistinctBy(c => c.Id)
            .OrderBy(c => c.StartTime)
            .Take(EvidenceVerifierMaxCalls)
            .ToList();
        var truncatedTotal = selectedCalls.Count + nearby.Count - reviewCalls.Count;
        var truncatedCalls = truncatedNearby + Math.Max(0, truncatedTotal);

        if (reviewCalls.Count == selectedCalls.Count && truncatedNearby == 0)
            return selectedCalls;

        EvidenceVerificationResult result;
        try
        {
            result = await VerifyEvidenceWithModelAsync(systemShortName, title, detail, incidentKey, selectedIds, existingIds, reviewCalls, truncatedCalls, ct);
        }
        catch (Exception ex)
        {
            await _database.AddEvidenceVerifierRunAsync(new EvidenceVerifierRunDto(
                0,
                DateTime.UtcNow,
                systemShortName,
                incidentKey,
                title,
                selectedCalls.Count,
                reviewCalls.Count,
                reviewCalls.Count,
                truncatedCalls,
                0,
                0,
                0,
                false,
                ex.Message), CancellationToken.None);
            _logger.LogWarning(ex, "Evidence verifier failed for incident '{Title}' on {System}; keeping extractor-selected call set", title, systemShortName);
            return selectedCalls;
        }

        var decisions = result.Calls
            .Where(c => !string.IsNullOrWhiteSpace(c.CallId))
            .GroupBy(c => c.CallId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.Confidence).First(), StringComparer.OrdinalIgnoreCase);
        var selectedById = selectedCalls.ToDictionary(c => c.Id);
        var reviewByToken = reviewCalls.ToDictionary(c => CallToken(c.Id), StringComparer.OrdinalIgnoreCase);
        var retained = new List<EngineCall>();
        var added = new List<long>();
        var dropped = new List<long>();

        foreach (var call in selectedCalls)
        {
            var token = CallToken(call.Id);
            decisions.TryGetValue(token, out var decision);
            var label = NormalizeEvidenceLabel(decision?.Label);
            var confidence = decision?.Confidence ?? 0;
            var isExisting = existingIds.Contains(call.Id);
            if (isExisting && !(label == "contradicts" && confidence >= 0.85))
            {
                retained.Add(call);
                continue;
            }

            if (label is "" or "supporting" or "related_context" || confidence < 0.80)
            {
                retained.Add(call);
                continue;
            }

            dropped.Add(call.Id);
        }

        foreach (var call in reviewCalls)
        {
            if (selectedById.ContainsKey(call.Id))
                continue;
            if (!decisions.TryGetValue(CallToken(call.Id), out var decision))
                continue;
            var label = NormalizeEvidenceLabel(decision.Label);
            var confidence = decision.Confidence;
            if (label == "supporting" && confidence >= 0.62)
            {
                retained.Add(call);
                added.Add(call.Id);
            }
            else if (label == "related_context" && confidence >= 0.82)
            {
                retained.Add(call);
                added.Add(call.Id);
            }
        }

        retained = retained.DistinctBy(c => c.Id).OrderBy(c => c.StartTime).ToList();
        await _database.AddEvidenceVerifierRunAsync(new EvidenceVerifierRunDto(
            0,
            DateTime.UtcNow,
            systemShortName,
            incidentKey,
            title,
            selectedCalls.Count,
            reviewCalls.Count,
            result.ReviewedCalls,
            truncatedCalls + result.PromptOmittedCalls,
            added.Count,
            dropped.Count,
            retained.Count,
            true,
            string.Empty), ct);
        _logger.LogInformation(
            "Evidence verifier reconciled incident '{Title}' on {System}: selected={SelectedCalls}, reviewed={ReviewedCalls}, added={AddedCalls}, dropped={DroppedCalls}, retained={RetainedCalls}, truncated={TruncatedCalls}",
            title,
            systemShortName,
            selectedCalls.Count,
            reviewCalls.Count,
            added.Count == 0 ? "-" : string.Join(",", added),
            dropped.Count == 0 ? "-" : string.Join(",", dropped),
            retained.Count,
            truncatedCalls + result.PromptOmittedCalls);
        return retained;
    }

    private async Task<EvidenceVerificationResult> VerifyEvidenceWithModelAsync(
        string systemShortName,
        string title,
        string detail,
        string incidentKey,
        HashSet<long> selectedIds,
        HashSet<long> existingIds,
        List<EngineCall> reviewCalls,
        int truncatedCalls,
        CancellationToken ct)
    {
        var baseUrl = InsightBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/chat/completions";
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var prompt = BuildEvidenceVerificationPrompt(systemShortName, title, detail, incidentKey, selectedIds, existingIds, reviewCalls, truncatedCalls);
        var body = new
        {
            model = InsightModel(),
            temperature = 0,
            max_tokens = EvidenceVerifierMaxOutputTokens,
            response_format = EvidenceVerificationResponseFormat(),
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You verify evidence for one public-safety radio incident. Classify each call independently. Output compact JSON only. Favor retaining concrete dispatch, location, continuation, and outcome calls, even across talkgroups/categories, when they support the same site-local real-world event."
                },
                new { role = "user", content = prompt.Text }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling evidence verifier endpoint {Endpoint} with model {Model} for {System} incident '{Title}' ({ReviewCalls} call(s), {PayloadChars} chars, {TruncatedCalls} truncated)", endpoint, InsightModel(), systemShortName, title, prompt.IncludedCalls, payload.Length, truncatedCalls + prompt.OmittedCalls);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(endpoint, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            await RecordUsageAsync(text, endpoint, payload.Length, reviewCalls.Take(prompt.IncludedCalls).Sum(c => c.Transcription?.Length ?? 0), 1, false, $"Evidence verifier HTTP {(int)response.StatusCode}: {Trim(text, 500)}", ct);
            throw new InvalidOperationException($"Evidence verifier request failed with HTTP {(int)response.StatusCode}: {Trim(text, 1000)}");
        }

        var usage = ExtractUsage(text);
        if (string.Equals(usage.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            var message = $"LM evidence verifier response was truncated at max_tokens={EvidenceVerifierMaxOutputTokens}.";
            await RecordUsageAsync(text, endpoint, payload.Length, reviewCalls.Take(prompt.IncludedCalls).Sum(c => c.Transcription?.Length ?? 0), 1, false, message, ct);
            throw new InsightResponseTruncatedException(message);
        }

        var parsed = ParseEvidenceVerificationResponse(text);
        await RecordUsageAsync(text, endpoint, payload.Length, reviewCalls.Take(prompt.IncludedCalls).Sum(c => c.Transcription?.Length ?? 0), 1, true, string.Empty, ct);
        return parsed with { ReviewedCalls = prompt.IncludedCalls, PromptOmittedCalls = prompt.OmittedCalls };
    }

    private EvidenceVerificationPrompt BuildEvidenceVerificationPrompt(
        string systemShortName,
        string title,
        string detail,
        string incidentKey,
        HashSet<long> selectedIds,
        HashSet<long> existingIds,
        List<EngineCall> reviewCalls,
        int truncatedCalls)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/no_think");
        sb.AppendLine("Return only JSON in message.content.");
        sb.AppendLine($"Site/system boundary: {systemShortName}.");
        sb.AppendLine($"Incident id: {incidentKey}");
        sb.AppendLine($"Incident title: {title}");
        sb.AppendLine($"Incident detail: {detail}");
        sb.AppendLine("Classify every call below as one of: supporting, related_context, unrelated, contradicts.");
        sb.AppendLine("supporting means the call directly reports, dispatches, locates, continues, or gives outcome/status for this same incident. related_context means useful nearby context but not direct evidence. unrelated means a different event/routine traffic. contradicts means it says the incident did not happen or belongs elsewhere.");
        sb.AppendLine("Do not drop initial dispatch/location calls. Do not require the same category or talkgroup. Use call ids exactly as given.");
        sb.AppendLine("Return every included call with only call_id, label, and confidence. Do not include explanations.");
        if (truncatedCalls > 0)
            sb.AppendLine($"Context note: {truncatedCalls} nearby call(s) were omitted by guardrails before this verifier prompt.");
        sb.AppendLine();
        sb.AppendLine("Calls:");
        var used = sb.Length;
        var included = 0;
        foreach (var call in reviewCalls.OrderBy(c => c.StartTime))
        {
            var flags = new List<string>();
            if (selectedIds.Contains(call.Id)) flags.Add("extractor_selected");
            if (existingIds.Contains(call.Id)) flags.Add("existing_incident_evidence");
            if (flags.Count == 0) flags.Add("nearby_candidate");
            var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime();
            var line = $"- [id:{CallToken(call.Id)}] [{local:HH:mm:ss}] flags={string.Join("|", flags)} | {call.SystemShortName} | {Label(call)} | category={call.Category}: {Trim(call.Transcription, EvidenceVerifierTranscriptCharLimit)}";
            if (used + line.Length + Environment.NewLine.Length > EvidenceVerifierPromptCharLimit)
                break;
            sb.AppendLine(line);
            used += line.Length + Environment.NewLine.Length;
            included++;
        }
        var omitted = Math.Max(0, reviewCalls.Count - included);
        sb.AppendLine($"Included {included}/{reviewCalls.Count} calls under verifier prompt budget {EvidenceVerifierPromptCharLimit:N0} chars.");
        return new EvidenceVerificationPrompt(sb.ToString(), included, omitted);
    }

    private string BuildIncidentExtractionPrompt(string systemShortName, List<IncidentDto> activeIncidents, List<EngineCall> candidateCalls, long start, long end)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/no_think");
        sb.AppendLine("Return only the final JSON object in message.content. Do not place the answer in reasoning_content.");
        sb.AppendLine($"Site/system boundary: {systemShortName}. Do not merge across sites unless an input call explicitly says this is the same cross-site response.");
        sb.AppendLine($"State window: {DateTimeOffset.FromUnixTimeSeconds(start).ToLocalTime()} to {DateTimeOffset.FromUnixTimeSeconds(end).ToLocalTime()}.");
        sb.AppendLine("Task: update the incident state using active incidents plus candidate calls. Assign useful related calls to an existing incident or a new incident. Leave routine/unrelated calls unassigned or held.");
        sb.AppendLine("Rules: do not use category as a grouping qualifier; require a concrete shared anchor such as address, road, landmark, patient/person, vehicle/plate, unit handoff, or explicit same-call reference; every incident must include all source_call_ids it should retain; every call_id must be copied exactly from input.");
        sb.AppendLine($"Narrative rule: title <= {IncidentTitleCharLimit} characters. detail <= {IncidentDetailCharLimit} characters and must be a concise operator summary, not a transcript. Do not concatenate call transcripts, quote radio dialogue, preserve filler, or include speaker turns. Summarize only the event, location/anchor, responders/status, and known outcome.");
        sb.AppendLine("Status: use active for developing/ongoing incidents and concluded when the event appears complete or stale. Do not return concluded incidents that have no supporting calls in this state window.");
        sb.AppendLine("Categories are labels only: choose one from police, fire, ems, traffic, public_works, utilities, other after deciding the incident.");
        sb.AppendLine();
        sb.AppendLine("Active incidents:");
        foreach (var incident in activeIncidents.OrderBy(i => i.LastSeen).Take(25))
        {
            var key = string.IsNullOrWhiteSpace(incident.IncidentKey) ? $"legacy-{incident.Id}" : incident.IncidentKey;
            sb.AppendLine($"- [incident_id:{key}] status={incident.Status}; last={DateTimeOffset.FromUnixTimeSeconds(incident.LastSeen).ToLocalTime():HH:mm}; title={Trim(incident.Title, 120)}; detail={Trim(incident.Detail, 220)}; source_call_ids=[{string.Join(",", incident.Calls.Select(c => CallToken(c.CallId)))}]");
        }

        sb.AppendLine();
        sb.AppendLine("Candidate calls:");
        var used = sb.Length;
        var included = 0;
        foreach (var call in candidateCalls.OrderBy(c => c.StartTime))
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime();
            var text = Trim(call.Transcription, IncidentTranscriptCharLimit);
            var line = $"- [id:{CallToken(call.Id)}] [{local:HH:mm:ss}] {call.SystemShortName} | {Label(call)} | category={call.Category}: {text}";
            if (used + line.Length + Environment.NewLine.Length > IncidentPromptCharLimit)
                break;
            sb.AppendLine(line);
            used += line.Length + Environment.NewLine.Length;
            included++;
        }

        sb.AppendLine($"Included {included}/{candidateCalls.Count} candidate calls under prompt budget {IncidentPromptCharLimit:N0} chars.");
        return sb.ToString();
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
                var usage = ExtractUsage(text);
                if (string.Equals(usage.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    var message = $"LM response was truncated at max_tokens={budget.MaxOutputTokens}; retry with a smaller summary window.";
                    await RecordUsageAsync(text, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, false, message, ct);
                    throw new InsightResponseTruncatedException(message);
                }

                var parsed = ParseResponse(text);
                await RecordUsageAsync(text, endpoint, payload.Length, calls.Sum(c => c.Transcription?.Length ?? 0), attempt + 1, true, string.Empty, ct);
                return parsed;
            }
            catch (InsightResponseTruncatedException)
            {
                throw;
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
        var targetEventCount = budget.MaxEvents > 0 ? budget.MaxEvents : calls.Count switch
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

    private static IncidentExtractionResult ParseIncidentExtractionResponse(string text)
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
                content = reasoningElement.GetString() ?? string.Empty;
        }

        content = StripCodeFence(content);
        content = ExtractJsonObject(content);
        using var doc = JsonDocument.Parse(content);
        var incidents = new List<IncidentStateItem>();
        if (doc.RootElement.TryGetProperty("incidents", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(80))
            {
                var ids = new List<string>();
                if (item.TryGetProperty("call_ids", out var callIds) && callIds.ValueKind == JsonValueKind.Array)
                    ids.AddRange(callIds.EnumerateArray().Select(v => v.GetString()).Where(v => !string.IsNullOrWhiteSpace(v))!);

                incidents.Add(new IncidentStateItem(
                    GetString(item, "incident_id"),
                    GetString(item, "status"),
                    GetString(item, "title"),
                    GetString(item, "detail"),
                    GetString(item, "category"),
                    item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                    ids,
                    ReadEvidence(item)));
            }
        }
        return new IncidentExtractionResult(incidents);
    }

    private static EvidenceVerificationResult ParseEvidenceVerificationResponse(string text)
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
                content = reasoningElement.GetString() ?? string.Empty;
        }

        content = StripCodeFence(content);
        content = ExtractJsonObject(content);
        using var doc = JsonDocument.Parse(content);
        var calls = new List<EvidenceVerificationCall>();
        if (doc.RootElement.TryGetProperty("calls", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray().Take(EvidenceVerifierMaxCalls))
            {
                calls.Add(new EvidenceVerificationCall(
                    GetString(item, "call_id"),
                    GetString(item, "label"),
                    item.TryGetProperty("confidence", out var confidence) && confidence.TryGetDouble(out var score) ? score : 0,
                    GetString(item, "reason")));
            }
        }
        return new EvidenceVerificationResult(calls, 0, 0);
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

    private static string NormalizeEvidenceLabel(string? label)
    {
        var normalized = Regex.Replace((label ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z_]+", "_").Trim('_');
        return normalized switch
        {
            "support" or "supported" or "direct_support" or "directly_supporting" => "supporting",
            "context" or "related" or "related_contextual" => "related_context",
            "unrelated_call" or "not_related" => "unrelated",
            "contradictory" or "contradiction" => "contradicts",
            _ => normalized
        };
    }

    private static bool IsIncidentEligibleCall(EngineCall call)
    {
        if (!string.Equals(call.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase))
            return false;
        var text = call.Transcription?.Trim() ?? string.Empty;
        return !call.IsImported && text.Length >= 12;
    }

    private static string ResolveIncidentKey(string systemShortName, IncidentStateItem item, List<EngineCall> calls, Dictionary<string, IncidentDto> existingByKey)
    {
        var supplied = (item.IncidentId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(supplied) && existingByKey.ContainsKey(supplied))
            return supplied;
        if (!string.IsNullOrWhiteSpace(supplied) && !supplied.StartsWith("new", StringComparison.OrdinalIgnoreCase))
            return supplied;
        var slug = Regex.Replace((item.Title ?? "incident").ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 40)
            slug = slug[..40].Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
            slug = "incident";
        return $"llm:{systemShortName}:{calls.Min(c => c.Id)}:{slug}";
    }

    private static string NormalizeCategory(string category, List<EngineCall> calls)
    {
        var normalized = (category ?? string.Empty).Trim().ToLowerInvariant();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "police", "fire", "ems", "traffic", "public_works", "utilities", "other" };
        if (allowed.Contains(normalized))
            return normalized;
        return calls.GroupBy(c => string.IsNullOrWhiteSpace(c.Category) ? "other" : c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Key)
            .FirstOrDefault() ?? "other";
    }

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

    private static string TrimIncidentText(string value, int max)
    {
        var text = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        if (text.Length <= max)
            return text;

        var sentenceEnd = text.LastIndexOfAny(['.', '!', '?'], Math.Min(text.Length - 1, max - 1));
        if (sentenceEnd >= Math.Max(80, max / 2))
            return text[..(sentenceEnd + 1)].Trim();

        return text[..max].Trim().TrimEnd(',', ';', ':') + ".";
    }

    private static bool IsIncidentNarrativeAcceptable(string title, string detail, List<EngineCall> calls)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(detail))
            return false;
        if (title.Length > IncidentTitleCharLimit || detail.Length > IncidentDetailCharLimit)
            return false;

        var normalized = NormalizeEventText(detail).ToLowerInvariant();
        if (normalized.Contains(">>") ||
            normalized.Contains("[beeping]") ||
            normalized.Contains("you're welcome") ||
            normalized.Contains("thank you") ||
            normalized.Contains(" i copy") ||
            normalized.Contains(" copy ") ||
            normalized.Contains(" i'm ") ||
            normalized.Contains(" we've ") ||
            normalized.Contains(" we'll "))
            return false;

        var detailTokens = MeaningfulTokens(normalized).ToList();
        if (detailTokens.Count < 5)
            return false;

        var transcript = NormalizeEventText(string.Join(' ', calls.Select(c => c.Transcription))).ToLowerInvariant();
        var transcriptTokens = MeaningfulTokens(transcript).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (transcriptTokens.Count == 0)
            return false;

        var overlap = detailTokens.Count(token => transcriptTokens.Contains(token)) / (double)detailTokens.Count;
        return overlap < 0.86 || detailTokens.Count <= 18;
    }

    private static IEnumerable<string> MeaningfulTokens(string text)
    {
        foreach (Match match in Regex.Matches(text, @"[a-z0-9]{3,}"))
        {
            var token = match.Value;
            if (token is "the" or "and" or "for" or "that" or "this" or "with" or "you" or "your" or "are" or "was" or "were" or "have" or "has" or "had" or "they" or "them" or "from" or "will" or "all" or "out" or "now")
                continue;
            yield return token;
        }
    }

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

    private static object IncidentExtractionResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "pizzawave_incident_state",
            strict = false,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    incidents = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                incident_id = new { type = "string" },
                                status = new { type = "string", @enum = new[] { "active", "concluded" } },
                                title = new { type = "string", maxLength = IncidentTitleCharLimit },
                                detail = new { type = "string", maxLength = IncidentDetailCharLimit },
                                category = new { type = "string" },
                                confidence = new { type = "number" },
                                call_ids = new
                                {
                                    type = "array",
                                    items = new { type = "string" },
                                    minItems = 2
                                },
                                call_evidence = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        additionalProperties = false,
                                        properties = new
                                        {
                                            call_id = new { type = "string" },
                                            evidence = new { type = "string" }
                                        },
                                        required = new[] { "call_id", "evidence" }
                                    }
                                }
                            },
                            required = new[] { "incident_id", "status", "title", "detail", "category", "confidence", "call_ids" }
                        }
                    }
                },
                required = new[] { "incidents" }
            }
        }
    };

    private static object EvidenceVerificationResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "pizzawave_evidence_verification",
            strict = false,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    calls = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            properties = new
                            {
                                call_id = new { type = "string" },
                                label = new { type = "string", @enum = new[] { "supporting", "related_context", "unrelated", "contradicts" } },
                                confidence = new { type = "number" }
                            },
                            required = new[] { "call_id", "label", "confidence" }
                        }
                    }
                },
                required = new[] { "calls" }
            }
        }
    };

    private sealed record InsightResult(string SummaryText, List<InsightEvent> Events);
    private sealed record InsightEvent(string Title, string Detail, string Category, string Timestamp, double Confidence, List<string> CallIds, List<CallEvidence> CallEvidence);
    private sealed record IncidentExtractionResult(List<IncidentStateItem> Incidents);
    private sealed record IncidentStateItem(string IncidentId, string Status, string Title, string Detail, string Category, double Confidence, List<string> CallIds, List<CallEvidence> CallEvidence);
    private sealed record EvidenceVerificationPrompt(string Text, int IncludedCalls, int OmittedCalls)
    {
        public override string ToString() => Text;
    }
    private sealed record EvidenceVerificationResult(List<EvidenceVerificationCall> Calls, int ReviewedCalls, int PromptOmittedCalls);
    private sealed record EvidenceVerificationCall(string CallId, string Label, double Confidence, string Reason);
    private sealed record CallEvidence(string CallId, string Evidence);
    private enum InsightPromptMode { NormalLive, CompactManual }
    private sealed record PromptBudget(int PromptCharLimit, int TranscriptCharLimit, int MaxOutputTokens, int MaxEvents = 0)
    {
        public static PromptBudget For(InsightPromptMode mode) => mode switch
        {
            InsightPromptMode.CompactManual => new PromptBudget(CompactPromptCharLimit, CompactTranscriptCharLimit, CompactMaxOutputTokens, CompactMaxEvents),
            _ => new PromptBudget(NormalPromptCharLimit, NormalTranscriptCharLimit, NormalMaxOutputTokens)
        };
    }
}

public sealed class InsightResponseTruncatedException(string message) : InvalidOperationException(message);
