using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace pizzad;

public static class TranscriptRetrievalEvidence
{
    public const int MinimumCharacters = 12;

    public static bool IsUsable(EngineCall call) =>
        !string.IsNullOrWhiteSpace(call.Transcription) &&
        call.Transcription.Trim().Length >= MinimumCharacters;
}

public sealed class EmbeddingService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly TalkgroupCatalogService _catalog;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly ConcurrentQueue<long> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private volatile bool _collectionReady;
    private string _collectionFingerprint = string.Empty;
    private double _lastSearchMs;
    private double _lastUpsertMs;
    private string _lastError = string.Empty;
    private readonly SemaphoreSlim _healthProbeGate = new(1, 1);
    private DateTime _lastHealthProbeUtc = DateTime.MinValue;
    private bool _lastQdrantOk;
    private bool _lastEmbeddingEndpointOk;
    private static readonly TimeSpan HealthProbeInterval = TimeSpan.FromMinutes(1);

    public EmbeddingService(EngineConfig config, EngineDatabase database, EventStream events, TalkgroupCatalogService catalog, ILogger<EmbeddingService> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _catalog = catalog;
        _logger = logger;
    }

    public int QueueDepth => _queue.Count;

    public async Task<(bool Ok, string Message)> DeleteCollectionAsync(CancellationToken ct)
    {
        if (!IsConfigured())
            return (true, "Embeddings are disabled; no Qdrant collection was cleared.");
        try
        {
            using var client = CreateQdrantClient();
            using var response = await client.DeleteAsync($"{QdrantBaseUrl()}/collections/{Uri.EscapeDataString(Collection())}", ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _collectionReady = false;
                _collectionFingerprint = string.Empty;
                return (true, $"Cleared Qdrant collection '{Collection()}'.");
            }
            return (false, $"Qdrant collection delete failed with HTTP {(int)response.StatusCode}: {Trim(text, 500)}");
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "Qdrant collection delete failed during system reset");
            return (false, $"Qdrant collection delete failed: {ex.Message}");
        }
    }

    public async Task EnqueueAsync(EngineCall call, CancellationToken ct)
    {
        if (!ShouldEmbed(call))
            return;
        await _database.UpsertEmbeddingJobAsync(call.Id, "pending", string.Empty, ct);
        _queue.Enqueue(call.Id);
        _signal.Release();
        await _events.PublishAsync("job_updated", new { type = "embeddings", queueDepth = QueueDepth }, ct);
    }

    public Task<IReadOnlyList<VectorSearchMatchDto>> SearchSimilarAsync(string queryText, string systemShortName, long start, long end, int limit, CancellationToken ct)
        => SearchSimilarAsync(queryText, systemShortName, requireOkQuality: true, start, end, limit, ct);

    public Task<IReadOnlyList<VectorSearchMatchDto>> SearchSimilarAcrossSystemsAsync(string queryText, long start, long end, int limit, CancellationToken ct)
        => SearchSimilarAsync(queryText, systemShortName: null, requireOkQuality: false, start, end, limit, ct);

    public async Task<IReadOnlyList<IReadOnlyList<VectorSearchMatchDto>>> SearchSimilarAcrossSystemsBatchAsync(
        IReadOnlyList<string> queryTexts,
        long start,
        long end,
        int limit,
        CancellationToken ct)
    {
        if (queryTexts.Count == 0)
            return [];
        var empty = queryTexts.Select(_ => (IReadOnlyList<VectorSearchMatchDto>)[]).ToList();
        if (!IsEnabled() || queryTexts.Any(string.IsNullOrWhiteSpace))
            return empty;
        try
        {
            await EnsureCollectionAsync(ct);
            var vectors = await CreateEmbeddingsAsync(queryTexts, ct);
            var must = new object[]
            {
                new { key = "startTime", range = new { gte = start, lte = end } }
            };
            var searches = vectors.Select(vector => new
            {
                vector,
                limit = Math.Clamp(limit, 1, Math.Max(1, _config.Embeddings.SearchLimit)),
                with_payload = true,
                filter = new { must }
            }).ToList();
            var sw = Stopwatch.StartNew();
            using var client = CreateQdrantClient();
            using var content = JsonContent(new { searches });
            using var response = await client.PostAsync(
                $"{QdrantBaseUrl()}/collections/{Uri.EscapeDataString(Collection())}/points/search/batch",
                content,
                ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Qdrant batch search failed with HTTP {(int)response.StatusCode}: {Trim(text, 500)}");
            sw.Stop();
            _lastSearchMs = sw.Elapsed.TotalMilliseconds;
            var results = EmbeddingSearchResponseParser.ParseBatch(text);
            if (results.Count != queryTexts.Count)
                throw new InvalidDataException($"Qdrant batch search returned {results.Count} result sets for {queryTexts.Count} queries.");
            return results;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "Qdrant vector batch search failed");
            return empty;
        }
    }

    private async Task<IReadOnlyList<VectorSearchMatchDto>> SearchSimilarAsync(
        string queryText,
        string? systemShortName,
        bool requireOkQuality,
        long start,
        long end,
        int limit,
        CancellationToken ct)
    {
        if (!IsEnabled() || string.IsNullOrWhiteSpace(queryText))
            return [];
        try
        {
            await EnsureCollectionAsync(ct);
            var vector = await CreateEmbeddingAsync(queryText, ct);
            var sw = Stopwatch.StartNew();
            using var client = CreateQdrantClient();
            var must = new List<object>();
            if (!string.IsNullOrWhiteSpace(systemShortName))
                must.Add(new { key = "systemShortName", match = new { value = systemShortName } });
            if (requireOkQuality)
                must.Add(new { key = "qualityReason", match = new { value = "ok" } });
            must.Add(new { key = "startTime", range = new { gte = start, lte = end } });
            var body = new
            {
                vector,
                limit = Math.Clamp(limit, 1, Math.Max(1, _config.Embeddings.SearchLimit)),
                with_payload = true,
                filter = new { must }
            };
            using var content = JsonContent(body);
            using var response = await client.PostAsync($"{QdrantBaseUrl()}/collections/{Uri.EscapeDataString(Collection())}/points/search", content, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Qdrant search failed with HTTP {(int)response.StatusCode}: {Trim(text, 500)}");
            sw.Stop();
            _lastSearchMs = sw.Elapsed.TotalMilliseconds;
            return ParseSearch(text);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "Qdrant vector search failed");
            return [];
        }
    }

    public async Task<EmbeddingPipelineHealthDto> GetHealthAsync(CancellationToken ct)
    {
        var stats = await _database.GetEmbeddingJobStatsAsync(ct);
        var qdrantOk = false;
        var embeddingOk = false;
        if (IsEnabled())
        {
            await RefreshHealthProbeIfDueAsync(ct);
            qdrantOk = _lastQdrantOk;
            embeddingOk = _lastEmbeddingEndpointOk;
        }
        var status = !IsEnabled() ? "disabled" : qdrantOk && embeddingOk ? "ok" : "degraded";
        return new EmbeddingPipelineHealthDto(
            IsEnabled(),
            qdrantOk,
            embeddingOk,
            status,
            Collection(),
            _config.Embeddings.OpenAiModel,
            _config.Embeddings.VectorSize,
            QueueDepth,
            stats.Embedded,
            stats.Failed,
            stats.Pending,
            stats.OldestPending,
            _lastSearchMs,
            _lastUpsertMs,
            _lastError);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!IsEnabled())
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                    continue;
                }
                await RecoverPendingAsync(stoppingToken);
                await _signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
                while (_queue.TryDequeue(out var callId))
                    await ProcessAsync(callId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _logger.LogWarning(ex, "Embedding worker loop failed");
                await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
        }
    }

    private async Task RecoverPendingAsync(CancellationToken ct)
    {
        if (_queue.Count > 0)
            return;
        var pending = await _database.ListPendingEmbeddingJobsAsync(500, ct);
        var retrying = pending.Count(j => string.Equals(j.Status, "failed", StringComparison.OrdinalIgnoreCase));
        if (retrying > 0)
            _logger.LogInformation("Retrying {Count} failed embedding job(s) after backoff", retrying);
        foreach (var job in pending)
            _queue.Enqueue(job.CallId);
        if (pending.Count > 0)
            _signal.Release();
    }

    private async Task ProcessAsync(long callId, CancellationToken ct)
    {
        if (_config.Embeddings.MaxQueueDepthWhenTranscriptionBusy > 0)
        {
            var pendingTranscriptions = await _database.CountPendingTranscriptionCallsAsync(ct);
            if (pendingTranscriptions > _config.Embeddings.MaxQueueDepthWhenTranscriptionBusy)
            {
                _queue.Enqueue(callId);
                _signal.Release();
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return;
            }
        }
        var call = await _database.GetCallAsync(callId, ct);
        if (call == null || !ShouldEmbed(call))
        {
            await _database.MarkEmbeddingJobAsync(callId, "failed", "call missing or no longer eligible", ct);
            return;
        }
        try
        {
            await EnsureCollectionAsync(ct);
            var locationRows = await _database.ListCallLocationsAsync(call.StartTime - 60, call.StartTime + 60, ct);
            var callLocations = locationRows.Where(l => l.CallId == call.Id).ToList();
            var embeddingText = BuildEmbeddingText(call, callLocations);
            var vector = await CreateEmbeddingAsync(embeddingText, ct);
            await UpsertPointAsync(call, vector, callLocations, ct);
            _lastEmbeddingEndpointOk = true;
            _lastQdrantOk = true;
            _lastError = string.Empty;
            await _database.MarkEmbeddingJobAsync(callId, "embedded", string.Empty, ct);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "Embedding failed for call {CallId}", callId);
            await _database.MarkEmbeddingJobAsync(callId, "failed", ex.Message, CancellationToken.None);
        }
    }

    private async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var fingerprint = CollectionFingerprint();
        if (_collectionReady && string.Equals(_collectionFingerprint, fingerprint, StringComparison.Ordinal))
            return;
        using var client = CreateQdrantClient();
        var get = await client.GetAsync($"{QdrantBaseUrl()}/collections/{Uri.EscapeDataString(Collection())}", ct);
        if (get.IsSuccessStatusCode)
        {
            var collectionJson = await get.Content.ReadAsStringAsync(ct);
            ValidateCollectionVectorSize(collectionJson);
            _collectionReady = true;
            _collectionFingerprint = fingerprint;
            return;
        }

        var body = new
        {
            vectors = new
            {
                size = _config.Embeddings.VectorSize,
                distance = "Cosine"
            }
        };
        using var content = JsonContent(body);
        using var response = await client.PutAsync($"{QdrantBaseUrl()}/collections/{Uri.EscapeDataString(Collection())}", content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qdrant collection create failed with HTTP {(int)response.StatusCode}: {Trim(text, 500)}");
        _collectionReady = true;
        _collectionFingerprint = fingerprint;
    }

    private void ValidateCollectionVectorSize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("result");
            var vectors = result.GetProperty("config").GetProperty("params").GetProperty("vectors");
            if (vectors.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt32(out var size) && size != _config.Embeddings.VectorSize)
                throw new InvalidOperationException($"Qdrant collection '{Collection()}' vector size is {size}, but PizzaWave is configured for {_config.Embeddings.VectorSize}. Create a new collection or correct the embedding settings.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            // Older/newer Qdrant shapes should not block startup if the collection exists.
        }
    }

    private async Task UpsertPointAsync(EngineCall call, float[] vector, IReadOnlyList<CallLocationDashboardRow> locations, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var bestLocation = locations.OrderByDescending(l => l.GeocodeConfidence).FirstOrDefault();
        var body = new
        {
            points = new[]
            {
                new
                {
                    id = call.Id,
                    vector,
                    payload = new
                    {
                        callId = call.Id,
                        systemShortName = call.SystemShortName,
                        startTime = call.StartTime,
                        category = call.Category,
                        talkgroup = call.Talkgroup,
                        talkgroupName = call.TalkgroupName,
                        qualityReason = call.QualityReason,
                        locationKey = bestLocation?.NormalizedKey ?? string.Empty,
                        latitude = bestLocation?.Latitude ?? 0,
                        longitude = bestLocation?.Longitude ?? 0
                    }
                }
            }
        };
        using var client = CreateQdrantClient();
        using var content = JsonContent(body);
        using var response = await client.PutAsync($"{QdrantBaseUrl()}/collections/{Uri.EscapeDataString(Collection())}/points?wait=true", content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qdrant upsert failed with HTTP {(int)response.StatusCode}: {Trim(text, 500)}");
        sw.Stop();
        _lastUpsertMs = sw.Elapsed.TotalMilliseconds;
    }

    private async Task<float[]> CreateEmbeddingAsync(string text, CancellationToken ct)
        => (await CreateEmbeddingsAsync([text], ct))[0];

    private async Task<IReadOnlyList<float[]>> CreateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0)
            return [];
        using var client = CreateEmbeddingClient();
        var body = new { model = _config.Embeddings.OpenAiModel, input = texts };
        using var content = JsonContent(body);
        using var response = await client.PostAsync($"{_config.Embeddings.OpenAiBaseUrl.TrimEnd('/')}/embeddings", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Embedding endpoint failed with HTTP {(int)response.StatusCode}: {Trim(json, 500)}");
        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(item => new
            {
                Index = item.GetProperty("index").GetInt32(),
                Values = item.GetProperty("embedding").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray()
            })
            .OrderBy(item => item.Index)
            .ToList();
        if (rows.Count != texts.Count || rows.Select(item => item.Index).Where((index, position) => index != position).Any())
            throw new InvalidDataException($"Embedding endpoint returned indexes that do not cover all {texts.Count} inputs exactly once.");
        foreach (var row in rows)
        {
            if (row.Values.Length != _config.Embeddings.VectorSize)
                throw new InvalidOperationException($"Embedding vector size {row.Values.Length} does not match configured qdrant size {_config.Embeddings.VectorSize}.");
        }
        return rows.Select(item => item.Values).ToList();
    }

    private async Task<bool> CheckQdrantAsync(CancellationToken ct)
    {
        try
        {
            using var client = CreateQdrantClient();
            using var response = await client.GetAsync($"{QdrantBaseUrl()}/collections/{Uri.EscapeDataString(Collection())}", ct);
            var ok = response.IsSuccessStatusCode;
            _lastQdrantOk = ok;
            if (!ok)
                _lastError = $"Qdrant health check failed with HTTP {(int)response.StatusCode}.";
            return ok;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _lastQdrantOk = false;
            _lastError = ex.Message;
            return false;
        }
    }

    private async Task<bool> CheckEmbeddingEndpointAsync(CancellationToken ct)
    {
        try
        {
            var vector = await CreateEmbeddingAsync("health check", ct);
            var ok = vector.Length == _config.Embeddings.VectorSize;
            _lastEmbeddingEndpointOk = ok;
            if (!ok)
                _lastError = $"Embedding health check returned vector size {vector.Length}; expected {_config.Embeddings.VectorSize}.";
            return ok;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _lastEmbeddingEndpointOk = false;
            _lastError = ex.Message;
            return false;
        }
    }

    private async Task RefreshHealthProbeIfDueAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _lastHealthProbeUtc < HealthProbeInterval)
            return;
        if (!await _healthProbeGate.WaitAsync(0, ct))
            return;
        try
        {
            now = DateTime.UtcNow;
            if (now - _lastHealthProbeUtc < HealthProbeInterval)
                return;
            var qdrantOk = await CheckQdrantAsync(ct);
            var embeddingOk = await CheckEmbeddingEndpointAsync(ct);
            if (qdrantOk && embeddingOk)
                _lastError = string.Empty;
            _lastHealthProbeUtc = DateTime.UtcNow;
        }
        finally
        {
            _healthProbeGate.Release();
        }
    }

    private bool ShouldEmbed(EngineCall call) =>
        IsEnabled() &&
        !call.IsImported &&
        DownstreamProfilePolicy.Allows(_config, _catalog, call) &&
        TranscriptRetrievalEvidence.IsUsable(call);

    private bool IsEnabled() =>
        _config.Setup.Completed &&
        IsConfigured();

    private bool IsConfigured() =>
        _config.Embeddings.Enabled &&
        !string.IsNullOrWhiteSpace(_config.Embeddings.OpenAiBaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.Embeddings.OpenAiModel) &&
        !string.IsNullOrWhiteSpace(_config.Embeddings.QdrantBaseUrl);

    private HttpClient CreateEmbeddingClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!string.IsNullOrWhiteSpace(_config.Embeddings.OpenAiApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Embeddings.OpenAiApiKey);
        return client;
    }

    private HttpClient CreateQdrantClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrWhiteSpace(_config.Embeddings.QdrantApiKey))
            client.DefaultRequestHeaders.Add("api-key", _config.Embeddings.QdrantApiKey);
        return client;
    }

    private static StringContent JsonContent(object body) =>
        new(JsonSerializer.Serialize(body, EngineConfig.JsonOptions()), Encoding.UTF8, "application/json");

    private static IReadOnlyList<VectorSearchMatchDto> ParseSearch(string text)
        => EmbeddingSearchResponseParser.ParseSingle(text);

    private static string BuildEmbeddingText(EngineCall call, IReadOnlyList<CallLocationDashboardRow> locations)
    {
        var location = locations.OrderByDescending(l => l.GeocodeConfidence).FirstOrDefault();
        return string.Join('\n', new[]
        {
            $"system: {call.SystemShortName}",
            $"category: {call.Category}",
            $"talkgroup: {call.TalkgroupName}",
            $"location: {location?.LocationText ?? string.Empty}",
            $"transcript: {call.Transcription}"
        });
    }

    private string QdrantBaseUrl() => _config.Embeddings.QdrantBaseUrl.TrimEnd('/');

    private string Collection() => _config.Embeddings.Collection;

    private string CollectionFingerprint() =>
        string.Join("|", QdrantBaseUrl(), Collection(), _config.Embeddings.VectorSize, _config.Embeddings.OpenAiBaseUrl.TrimEnd('/'), _config.Embeddings.OpenAiModel);

    private static string Trim(string value, int max) =>
        string.IsNullOrWhiteSpace(value) || value.Length <= max ? value : value[..max];
}

public static class EmbeddingSearchResponseParser
{
    public static IReadOnlyList<VectorSearchMatchDto> ParseSingle(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return [];
        return ParseResultSet(result);
    }

    public static IReadOnlyList<IReadOnlyList<VectorSearchMatchDto>> ParseBatch(string text)
    {
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return [];
        return result.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Array
                ? ParseResultSet(item)
                : (IReadOnlyList<VectorSearchMatchDto>)[])
            .ToList();
    }

    private static IReadOnlyList<VectorSearchMatchDto> ParseResultSet(JsonElement result)
    {
        var rows = new List<VectorSearchMatchDto>();
        foreach (var item in result.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idElement)
                     && idElement.ValueKind == JsonValueKind.Number
                     && idElement.TryGetInt64(out var parsedId)
                ? parsedId
                : 0;
            var score = item.TryGetProperty("score", out var scoreElement)
                        && scoreElement.ValueKind == JsonValueKind.Number
                        && scoreElement.TryGetDouble(out var parsedScore)
                ? parsedScore
                : 0;
            if (id > 0)
                rows.Add(new VectorSearchMatchDto(id, score, "qdrant"));
        }
        return rows;
    }
}
