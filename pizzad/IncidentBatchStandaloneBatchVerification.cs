using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace pizzad;

public static class IncidentBatchStandaloneBatchVerification
{
    public const string ConfigurationToken = "standalone-verification=batched-independent-v1";
    public const string PromptIdentity = "incident-batch-standalone-batch-verifier-v1";

    public static bool IsEnabled(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(ConfigurationToken, StringComparer.Ordinal);

    public static IncidentBatchPromptPayload BuildPrompt(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchEventProposal> events)
    {
        var catalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle);
        var inputs = events.Select((item, index) => new
        {
            row_number = index + 1,
            source_proposal_token = item.ProposalToken,
            observations = item.NewObservationIds,
            evidence_spans = catalog
                .Where(span => item.NewObservationIds.Contains(span.ObservationId, StringComparer.Ordinal))
                .Select((span, spanIndex) => new { span_index = spanIndex, exact_quote = span.ExactQuote })
                .ToList()
        }).ToList();
        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Independently verify every source proposal. Do not compare rows and do not infer relationships between calls.");
        user.AppendLine("Use v only when that row's transcript establishes a concrete real-world occurrence affecting people, property, or public safety. Communication, documentation, identification, enforcement contact, unit status, responder presence, and coordination are not incidents by themselves.");
        user.AppendLine("Use r only when a concrete occurrence is supported but a materially disputed core fact prevents normal publication. Ordinary ASR noise, missing location, or missing final status alone does not require review. Use x when no concrete incident is established.");
        user.AppendLine("Missing a valid incident is costly. A row may be a mid-incident update rather than an initial dispatch; verify it when intelligible words explicitly establish the ongoing underlying condition. Garbled surrounding words do not erase a concrete occurrence that is directly stated. Reject only when the row's own selected evidence cannot state a concrete operator-relevant situation.");
        user.AppendLine("For v or r, return a concise title based only on selected evidence from that row. Preserve negation and modality exactly. Never repair garbled ASR or infer a name, location, agency, medication, diagnosis, condition, or status.");
        user.AppendLine("Return rows in input order as [row_number, decision, display_title, evidence_span_indices, review_reason]. Use an empty title for x and a reason only for r.");
        user.AppendLine();
        user.AppendLine("Source proposals:");
        user.AppendLine(JsonSerializer.Serialize(inputs, EngineConfig.JsonOptions()));

        object RowSchema(IncidentBatchEventProposal item, int index)
        {
            var spanCount = catalog.Count(span => item.NewObservationIds.Contains(span.ObservationId, StringComparer.Ordinal));
            return new
            {
                type = "array",
                minItems = 5,
                maxItems = 5,
                prefixItems = new object[]
                {
                    new { type = "integer", @const = index + 1 },
                    new { type = "string", @enum = new[] { "v", "r", "x" } },
                    new { type = "string", maxLength = IncidentBatchConfirmationContract.MaximumDisplayTitleLength },
                    new
                    {
                        type = "array",
                        minItems = 1,
                        maxItems = IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide,
                        uniqueItems = true,
                        items = new { type = "integer", minimum = 0, maximum = Math.Max(0, spanCount - 1) }
                    },
                    new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength }
                }
            };
        }
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_batch_standalone_batch_verifier_v1",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        decisions = new
                        {
                            type = "array",
                            minItems = events.Count,
                            maxItems = events.Count,
                            prefixItems = events.Select(RowSchema).ToArray()
                        }
                    },
                    required = new[] { "decisions" }
                }
            }
        };
        return new IncidentBatchPromptPayload(
            "You are PizzaWave's independent evidence-bounded verifier for standalone incident proposals. Application code owns row identity, evidence spans, validation, and persistence.",
            user.ToString(),
            responseFormat);
    }

    public static IReadOnlyList<IncidentBatchStandaloneVerificationProposal> Parse(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchEventProposal> events,
        string json,
        string model,
        DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.GetProperty("decisions");
        if (rows.GetArrayLength() != events.Count)
            throw new InvalidDataException("Batched standalone verifier did not return complete row coverage.");
        var catalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle);
        var results = new List<IncidentBatchStandaloneVerificationProposal>();
        for (var index = 0; index < events.Count; index++)
        {
            var row = rows[index];
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 5 || row[0].GetInt32() != index + 1)
                throw new InvalidDataException($"Batched standalone verifier row {index + 1} has an invalid identity or shape.");
            var source = events[index];
            var localSpans = catalog.Where(span => source.NewObservationIds.Contains(span.ObservationId, StringComparer.Ordinal)).ToList();
            var evidence = row[3].EnumerateArray()
                .Select(item => item.GetInt32())
                .Distinct()
                .Select(spanIndex => spanIndex >= 0 && spanIndex < localSpans.Count
                    ? localSpans[spanIndex]
                    : throw new InvalidDataException($"Unknown standalone evidence span index '{spanIndex}'."))
                .Select(span => new IncidentEventStateTranscriptCitation(span.TranscriptId, span.ExactQuote))
                .ToList();
            var decision = row[1].GetString() switch
            {
                "v" => IncidentBatchConfirmationDecisionKind.Verify,
                "r" => IncidentBatchConfirmationDecisionKind.Review,
                "x" => IncidentBatchConfirmationDecisionKind.Reject,
                var value => throw new InvalidDataException($"Unsupported standalone batch decision '{value}'.")
            };
            var reviewReason = row[4].GetString() ?? string.Empty;
            results.Add(new IncidentBatchStandaloneVerificationProposal(
                $"model:incident-batch-standalone-batch:{Guid.NewGuid():N}:{index + 1}",
                now,
                model,
                PromptIdentity,
                new IncidentBatchStandaloneVerificationDecision(
                    source.ProposalToken,
                    decision,
                    decision != IncidentBatchConfirmationDecisionKind.Reject,
                    decision == IncidentBatchConfirmationDecisionKind.Reject ? string.Empty : IncidentTitlePresentation.Normalize(row[2].GetString()),
                    decision switch
                    {
                        IncidentBatchConfirmationDecisionKind.Verify => "Batched verifier established a concrete standalone incident.",
                        IncidentBatchConfirmationDecisionKind.Review => "Batched verifier retained a concrete incident for operator review.",
                        _ => "Batched verifier did not establish a concrete standalone incident."
                    },
                    evidence,
                    decision == IncidentBatchConfirmationDecisionKind.Review ? [reviewReason] : [],
                    [])));
        }
        return results;
    }
}

public sealed class OpenAiIncidentBatchStandaloneBatchVerifier(
    EngineConfig config,
    EngineDatabase database,
    ILogger logger,
    string runId)
{
    public async Task<IReadOnlyList<IncidentBatchStandaloneVerificationProposal>> VerifyAsync(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<IncidentBatchEventProposal> events,
        CancellationToken ct)
    {
        var prompt = IncidentBatchStandaloneBatchVerification.BuildPrompt(bundle, events);
        var model = config.AiInsights.OpenAiModel;
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 3000,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, config.AiInsights.TimeoutMs)) };
        if (!string.IsNullOrWhiteSpace(config.AiInsights.OpenAiApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.AiInsights.OpenAiApiKey);
        var endpoint = $"{config.AiInsights.OpenAiBaseUrl.TrimEnd('/')}/chat/completions";
        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        var responseText = string.Empty;
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, ct);
            responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Batched standalone verifier returned HTTP {(int)response.StatusCode}.");
            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.GetProperty("model").GetString() ?? string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"Batched standalone model identity mismatch: requested '{model}', received '{responseModel}'.");
            var json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Batched standalone response content was empty.");
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, Elapsed(started), ct);
            return IncidentBatchStandaloneBatchVerification.Parse(bundle, events, json, responseModel, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, false, ex.GetBaseException().Message, Elapsed(started), CancellationToken.None);
            throw;
        }
    }

    private async Task RecordUsageAsync(string responseText, string endpoint, string model, int payloadChars, bool success, string error, long duration, CancellationToken ct)
    {
        var promptTokens = 0;
        var completionTokens = 0;
        var totalTokens = 0;
        var responseModel = string.Empty;
        var finishReason = string.Empty;
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            try
            {
                using var document = JsonDocument.Parse(responseText);
                var root = document.RootElement;
                if (root.TryGetProperty("usage", out var usage))
                {
                    promptTokens = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : 0;
                    completionTokens = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : 0;
                    totalTokens = usage.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : 0;
                }
                responseModel = root.TryGetProperty("model", out var m) ? m.GetString() ?? string.Empty : string.Empty;
                finishReason = root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    ? choices[0].GetProperty("finish_reason").GetString() ?? string.Empty
                    : string.Empty;
            }
            catch { }
        }
        try
        {
            await database.AddLmUsageAsync(new TokenUsageEntryDto(
                0, DateTime.UtcNow, $"incident batch standalone batch verification:{runId}", "chat.completions",
                success, error, endpoint, model, responseModel, finishReason, payloadChars, payloadChars,
                promptTokens, completionTokens, totalTokens, duration), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not record batched standalone verifier usage");
        }
    }

    private static long Elapsed(long started) =>
        Math.Max(0, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
