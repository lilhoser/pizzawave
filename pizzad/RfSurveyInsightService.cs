using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace pizzad;

public sealed class RfSurveyInsightService
{
    private readonly EngineConfig _config;
    private readonly ILogger<RfSurveyInsightService> _logger;

    public RfSurveyInsightService(EngineConfig config, ILogger<RfSurveyInsightService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<RfSweepInsightResponse> AnalyzeSweepAsync(RfSweepInsightRequest request, CancellationToken ct)
    {
        if (!_config.AiInsights.Enabled || string.IsNullOrWhiteSpace(InsightBaseUrl()) || string.IsNullOrWhiteSpace(InsightModel()))
            throw new InvalidOperationException("AI Insights are disabled or incomplete. Enable AI Insights and choose a chat model first.");

        var endpoint = $"{InsightBaseUrl().TrimEnd('/')}/chat/completions";
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs)) };
        var apiKey = InsightApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var body = new
        {
            model = InsightModel(),
            temperature = 0.1,
            max_tokens = 900,
            response_format = InsightResponseFormat(),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You interpret measured RF calibration sweep results for trunk-recorder/P25 SDR systems. Use only the provided measurements. Do not invent RF facts. Output JSON with fields recommendation, confidence, rationale, next_actions. recommendation must be one concise sentence. confidence is high, medium, low, or inconclusive. next_actions is an array of short operator actions."
                },
                new
                {
                    role = "user",
                    content = JsonSerializer.Serialize(new
                    {
                        request.SurveyId,
                        request.SystemShortName,
                        request.SourceIndex,
                        selectedCandidate = request.SelectedCandidate,
                        sweepResult = request.SweepResult,
                        instruction = "Assess whether the selected/best error candidate is credible. Consider zero-decode percentage, average/max decode rate, retunes, calls started/concluded, no-transmission count, flat/noisy candidate spread, and whether a narrow follow-up sweep is warranted."
                    }, EngineConfig.JsonOptions())
                }
            }
        };

        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        _logger.LogInformation("Calling RF sweep insight endpoint {Endpoint} with model {Model} for survey {SurveyId} source {SourceIndex}", endpoint, InsightModel(), request.SurveyId, request.SourceIndex);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(endpoint, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"RF sweep insight request failed with HTTP {(int)response.StatusCode}: {Trim(text, 1000)}");

        var contentText = ExtractAssistantContent(text);
        return ParseInsightContent(contentText);
    }

    private static RfSweepInsightResponse ParseInsightContent(string content)
    {
        try
        {
            var node = JsonNode.Parse(content)?.AsObject();
            if (node != null)
            {
                var actions = node["next_actions"] is JsonArray arr
                    ? arr.Select(item => item?.GetValue<string>() ?? string.Empty).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                    : [];
                return new RfSweepInsightResponse(
                    node["recommendation"]?.GetValue<string>() ?? "Review the sweep measurements before changing config.",
                    node["confidence"]?.GetValue<string>() ?? "inconclusive",
                    node["rationale"]?.GetValue<string>() ?? content,
                    actions,
                    content);
            }
        }
        catch
        {
            // Fall through to raw response.
        }
        return new RfSweepInsightResponse("Review the sweep measurements before changing config.", "inconclusive", content, [], content);
    }

    private static string ExtractAssistantContent(string responseText)
    {
        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var content))
                return content.GetString() ?? string.Empty;
        }
        return responseText;
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

    private static object InsightResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "rf_sweep_insight",
            strict = false,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    recommendation = new { type = "string" },
                    confidence = new { type = "string", @enum = new[] { "high", "medium", "low", "inconclusive" } },
                    rationale = new { type = "string" },
                    next_actions = new
                    {
                        type = "array",
                        items = new { type = "string" }
                    }
                },
                required = new[] { "recommendation", "confidence", "rationale", "next_actions" }
            }
        }
    };

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "...";
}
