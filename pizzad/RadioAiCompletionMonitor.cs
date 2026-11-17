using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace pizzad;

public sealed record RadioAiCompletionEvidence(DateTime? LastSuccessAt, DateTime? ProbeAt, bool? ProbeOk);

public sealed class RadioAiCompletionMonitor(EngineConfig config, EngineDatabase database, HttpClient client)
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(5);
    private (string Endpoint, string Model) _route;
    private DateTime? _lastReadAt;
    private RadioAiCompletionEvidence _evidence = new(null, null, null);

    // RadioHealthSummaryService serializes access for the API and publisher.
    public async Task<RadioAiCompletionEvidence> GetAsync(DateTime now, bool allowProbe, CancellationToken ct)
    {
        var ai = config.AiInsights;
        if (!ai.Enabled || ai.ExecutionMode != "lmlink")
            return new(null, null, null);

        var baseUrl = string.IsNullOrWhiteSpace(ai.OpenAiBaseUrl)
            ? config.Transcription.OpenAiBaseUrl : ai.OpenAiBaseUrl;
        var model = string.IsNullOrWhiteSpace(ai.OpenAiModel)
            ? config.Transcription.OpenAiModel : ai.OpenAiModel;
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(model))
            return new(null, null, null);

        var route = (Endpoint: baseUrl.TrimEnd('/') + "/chat/completions", Model: model);
        if (_route != route)
        {
            _route = route;
            _lastReadAt = null;
            _evidence = new(null, null, null);
        }

        var lastSuccess = await database.GetLatestAutomaticCompletionAsync(
            route.Endpoint, route.Model, _lastReadAt?.Subtract(Freshness), now, ct);
        _lastReadAt = now;
        if (lastSuccess > _evidence.LastSuccessAt || _evidence.LastSuccessAt is null)
            _evidence = _evidence with { LastSuccessAt = lastSuccess };

        if (!allowProbe || _evidence.LastSuccessAt >= now - Freshness || _evidence.ProbeAt >= now - Freshness)
            return _evidence;

        var apiKey = string.IsNullOrWhiteSpace(ai.OpenAiApiKey)
            ? config.Transcription.OpenAiApiKey : ai.OpenAiApiKey;
        _evidence = _evidence with { ProbeAt = now, ProbeOk = null };
        var ok = await ProbeAsync(endpoint, model, apiKey, ct);
        _evidence = _evidence with { ProbeOk = ok };
        return _evidence;
    }

    private async Task<bool> ProbeAsync(Uri endpoint, string model, string apiKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new
            {
                model,
                temperature = 0,
                max_tokens = 8,
                messages = new[] { new { role = "user", content = "Reply with OK only." } }
            });
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return false;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var choice = json.RootElement.GetProperty("choices")[0];
            return choice.GetProperty("finish_reason").GetString() == "stop"
                && string.Equals(choice.GetProperty("message").GetProperty("content").GetString()?.Trim(),
                    "OK", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
            or KeyNotFoundException or IndexOutOfRangeException or FormatException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
