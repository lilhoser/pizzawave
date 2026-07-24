using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public sealed record IncidentBatchStandaloneVerificationDecision(
    string SourceProposalToken,
    IncidentBatchConfirmationDecisionKind Decision,
    bool ConcreteIncidentSupported,
    string DisplayTitle,
    string VerificationStatement,
    IReadOnlyList<IncidentEventStateTranscriptCitation> Evidence,
    IReadOnlyList<string> CounterEvidence,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentBatchStandaloneVerificationProposal(
    string ProposalId,
    DateTimeOffset GeneratedAtUtc,
    string ModelIdentity,
    string PromptIdentity,
    IncidentBatchStandaloneVerificationDecision Decision);

public interface IIncidentBatchStandaloneVerifier
{
    Task<IncidentBatchStandaloneVerificationProposal> VerifyAsync(
        IncidentEventStateObservationBundle bundle,
        IncidentBatchEventProposal proposedEvent,
        CancellationToken ct);
}

public static class IncidentBatchStandaloneVerificationContract
{
    public const string ConfigurationToken = "standalone-verification=independent-grounded-title-v1";

    public static bool IsEnabled(string configurationIdentity) =>
        configurationIdentity.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(ConfigurationToken, StringComparer.Ordinal);

    public static IncidentEventStateContractValidationResult ValidateProposal(
        IncidentEventStateObservationBundle bundle,
        IncidentBatchEventProposal proposedEvent,
        IncidentBatchStandaloneVerificationProposal proposal)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(proposal.ProposalId))
            errors.Add("standalone verification proposal id is required");
        if (string.IsNullOrWhiteSpace(proposal.ModelIdentity))
            errors.Add("standalone verification model identity is required");
        if (!string.Equals(proposal.PromptIdentity, IncidentBatchStandaloneVerificationPrompt.PromptIdentity, StringComparison.Ordinal))
            errors.Add("standalone verification prompt identity does not match the contract");
        if (proposal.GeneratedAtUtc == default)
            errors.Add("standalone verification timestamp is required");

        var decision = proposal.Decision;
        if (!string.Equals(decision.SourceProposalToken, proposedEvent.ProposalToken, StringComparison.Ordinal))
            errors.Add("standalone verification decision references an unknown source proposal");
        if (!Enum.IsDefined(decision.Decision))
            errors.Add("standalone verification decision is invalid");
        if (string.IsNullOrWhiteSpace(decision.VerificationStatement))
            errors.Add("standalone verification statement is required");
        if (decision.VerificationStatement?.Length > IncidentBatchRelationshipContract.MaximumTextLength)
            errors.Add($"standalone verification statement exceeds {IncidentBatchRelationshipContract.MaximumTextLength} characters");
        if (decision.DisplayTitle?.Length > IncidentBatchConfirmationContract.MaximumDisplayTitleLength)
            errors.Add($"standalone display title exceeds {IncidentBatchConfirmationContract.MaximumDisplayTitleLength} characters");
        if (decision.Decision is IncidentBatchConfirmationDecisionKind.Verify or IncidentBatchConfirmationDecisionKind.Review &&
            string.IsNullOrWhiteSpace(decision.DisplayTitle))
        {
            errors.Add("verified or review standalone event requires a grounded display title");
        }
        if (decision.Decision is IncidentBatchConfirmationDecisionKind.Verify or IncidentBatchConfirmationDecisionKind.Review &&
            !decision.ConcreteIncidentSupported)
        {
            errors.Add("verified or review standalone event must be supported as a concrete incident");
        }
        if (decision.Decision == IncidentBatchConfirmationDecisionKind.Verify &&
            (decision.CounterEvidence.Count > 0 || decision.UnresolvedQuestions.Count > 0))
        {
            errors.Add("verified standalone event cannot retain counterevidence or unresolved questions");
        }
        if (decision.Decision == IncidentBatchConfirmationDecisionKind.Review &&
            decision.CounterEvidence.Count == 0 &&
            decision.UnresolvedQuestions.Count == 0)
        {
            errors.Add("review standalone event must explain its remaining uncertainty");
        }
        if (decision.Evidence.Count == 0)
            errors.Add("standalone verification requires at least one exact transcript citation");
        if (decision.Evidence.Count > IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide)
            errors.Add($"standalone verification contains more than {IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide} evidence spans");
        if (decision.CounterEvidence.Count > IncidentBatchRelationshipContract.MaximumAlternatives)
            errors.Add($"standalone verification contains more than {IncidentBatchRelationshipContract.MaximumAlternatives} counterevidence items");
        if (decision.UnresolvedQuestions.Count > IncidentBatchRelationshipContract.MaximumUnresolvedQuestions)
            errors.Add($"standalone verification contains more than {IncidentBatchRelationshipContract.MaximumUnresolvedQuestions} unresolved questions");

        ValidateCitations(bundle, proposedEvent.NewObservationIds, decision.Evidence, errors);
        ValidateStrings(decision.CounterEvidence, "standalone counterevidence", errors);
        ValidateStrings(decision.UnresolvedQuestions, "standalone unresolved question", errors);
        return new IncidentEventStateContractValidationResult(errors.Count == 0, errors);
    }

    public static IncidentBatchStandaloneVerificationDecision? AcceptedDecision(
        IncidentEventStateObservationBundle bundle,
        IncidentBatchEventProposal proposedEvent,
        IncidentBatchStandaloneVerificationProposal proposal) =>
        ValidateProposal(bundle, proposedEvent, proposal).IsValid
            ? proposal.Decision
            : null;

    private static void ValidateCitations(
        IncidentEventStateObservationBundle bundle,
        IReadOnlyList<string> allowedObservationIds,
        IReadOnlyList<IncidentEventStateTranscriptCitation> citations,
        List<string> errors)
    {
        var transcripts = bundle.Observations
            .Where(item => allowedObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
            .SelectMany(item => item.Transcripts)
            .GroupBy(item => item.TranscriptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var citation in citations)
        {
            if (string.IsNullOrWhiteSpace(citation.TranscriptId) || string.IsNullOrWhiteSpace(citation.ExactQuote))
                errors.Add("standalone evidence requires transcript id and exact quote");
            if (!seen.Add($"{citation.TranscriptId}\u001f{citation.ExactQuote}"))
                errors.Add("duplicate standalone transcript citation");
            if (!transcripts.TryGetValue(citation.TranscriptId, out var matches) || matches.Count != 1)
                errors.Add("standalone evidence cites a transcript outside its source boundary");
            else if (!matches[0].Text.Contains(citation.ExactQuote, StringComparison.Ordinal))
                errors.Add($"standalone evidence quote does not occur exactly in transcript '{citation.TranscriptId}'");
        }
    }

    private static void ValidateStrings(IReadOnlyList<string> values, string owner, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"{owner} is required");
            if (value?.Length > IncidentBatchRelationshipContract.MaximumTextLength)
                errors.Add($"{owner} exceeds {IncidentBatchRelationshipContract.MaximumTextLength} characters");
            if (!seen.Add(value ?? string.Empty))
                errors.Add($"duplicate {owner}");
        }
    }
}

public sealed record IncidentBatchStandaloneVerificationPromptPayload(
    string SystemPrompt,
    string UserPrompt,
    object ResponseFormat);

public static class IncidentBatchStandaloneVerificationPrompt
{
    public const string PromptIdentity = "incident-batch-standalone-verifier-v1-grounded-title";

    public static IncidentBatchStandaloneVerificationPromptPayload Build(
        IncidentEventStateObservationBundle bundle,
        IncidentBatchEventProposal proposedEvent)
    {
        var observations = bundle.Observations
            .Where(item => proposedEvent.NewObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
            .ToList();
        if (observations.Count == 0)
            throw new ArgumentException("standalone verification requires source observations", nameof(proposedEvent));
        var transcriptIds = observations
            .SelectMany(item => item.Transcripts)
            .Select(item => item.TranscriptId)
            .ToHashSet(StringComparer.Ordinal);
        var evidenceCatalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle)
            .Where(item => transcriptIds.Contains(item.TranscriptId))
            .ToList();
        var spansByTranscript = evidenceCatalog
            .GroupBy(item => item.TranscriptId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var evidenceIds = evidenceCatalog.Select(item => item.EvidenceId).ToArray();
        var input = new
        {
            source_proposal_token = proposedEvent.ProposalToken,
            untrusted_constructor_statement = proposedEvent.RelationshipStatement,
            observations = observations.Select(item => new
            {
                observation_id = item.ObservationId,
                observed_at_unix_seconds = item.ObservedAtUnixSeconds,
                transcripts = item.Transcripts.Select(transcript => new
                {
                    transcript_id = transcript.TranscriptId,
                    evidence_spans = spansByTranscript[transcript.TranscriptId]
                        .Select(span => new { evidence_id = span.EvidenceId, exact_quote = span.ExactQuote })
                        .ToList()
                }).ToList()
            }).ToList()
        };
        var user = new StringBuilder();
        user.AppendLine("/no_think");
        user.AppendLine("Return only JSON matching the supplied schema.");
        user.AppendLine("Independently decide whether the source evidence describes a concrete real-world incident that should appear in an operator incident list. The constructor is untrusted.");
        user.AppendLine("Verify only when the transcript itself establishes a specific active or recently reported situation, response, hazard, crime, medical need, fire, missing person, traffic event, or other concrete occurrence. This list is illustrative, not a fixed taxonomy.");
        user.AppendLine("Reject routine identifiers, acknowledgements, channel logistics, generic status chatter, unintelligible fragments, and statements that do not establish a concrete occurrence.");
        user.AppendLine("Use review only when a concrete occurrence is evidenced but a material ambiguity prevents safe publication as a normal incident. Review never persists an incident.");
        user.AppendLine("For verify, provide a concise operator-facing display_title based only on selected evidence. Omit radio preambles, unit chatter, and unsupported details.");
        user.AppendLine($"display_title must be at most {IncidentBatchConfirmationContract.MaximumDisplayTitleLength} characters. For reject return an empty display_title.");
        user.AppendLine("Select one or more application-owned evidence_id values. Never generate, edit, or paraphrase quote text.");
        user.AppendLine("For verify, counter_evidence and unresolved_questions must be empty. For review, include the concrete remaining issue.");
        user.AppendLine();
        user.AppendLine("Proposed standalone event:");
        user.AppendLine(JsonSerializer.Serialize(input, EngineConfig.JsonOptions()));

        object Strings() => new
        {
            type = "array",
            maxItems = IncidentBatchRelationshipContract.MaximumAlternatives,
            items = new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength }
        };
        var responseFormat = new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "pizzawave_incident_batch_standalone_verifier_v1",
                strict = true,
                schema = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        source_proposal_token = new { type = "string", @enum = new[] { proposedEvent.ProposalToken } },
                        concrete_incident_supported = new { type = "boolean" },
                        decision = new { type = "string", @enum = new[] { "verify", "review", "reject" } },
                        display_title = new { type = "string", maxLength = IncidentBatchConfirmationContract.MaximumDisplayTitleLength },
                        verification_statement = new { type = "string", maxLength = IncidentBatchRelationshipContract.MaximumTextLength },
                        evidence_ids = new
                        {
                            type = "array",
                            minItems = 1,
                            maxItems = IncidentBatchRelationshipContract.MaximumEvidenceSpansPerSide,
                            items = new { type = "string", @enum = evidenceIds }
                        },
                        counter_evidence = Strings(),
                        unresolved_questions = Strings()
                    },
                    required = new[]
                    {
                        "source_proposal_token", "concrete_incident_supported", "decision", "display_title",
                        "verification_statement", "evidence_ids", "counter_evidence", "unresolved_questions"
                    }
                }
            }
        };
        return new IncidentBatchStandaloneVerificationPromptPayload(
            "You are an independent evidence-bounded verifier for standalone public-safety incident proposals. Application code owns evidence spans, state transitions, and persistence. You may supply presentation text, but it cannot influence membership or bypass validation.",
            user.ToString(),
            responseFormat);
    }
}

public sealed class OpenAiIncidentBatchStandaloneVerifier : IIncidentBatchStandaloneVerifier
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger _logger;
    private readonly string _runId;

    public OpenAiIncidentBatchStandaloneVerifier(
        EngineConfig config,
        EngineDatabase database,
        ILogger logger,
        string runId)
    {
        _config = config;
        _database = database;
        _logger = logger;
        _runId = runId;
    }

    public async Task<IncidentBatchStandaloneVerificationProposal> VerifyAsync(
        IncidentEventStateObservationBundle bundle,
        IncidentBatchEventProposal proposedEvent,
        CancellationToken ct)
    {
        var prompt = IncidentBatchStandaloneVerificationPrompt.Build(bundle, proposedEvent);
        var model = _config.AiInsights.OpenAiModel;
        var body = new
        {
            model,
            temperature = 0.1,
            max_tokens = 900,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(Math.Max(1000, _config.AiInsights.TimeoutMs))
        };
        if (!string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.AiInsights.OpenAiApiKey);
        var endpoint = $"{_config.AiInsights.OpenAiBaseUrl.TrimEnd('/')}/chat/completions";
        var payload = JsonSerializer.Serialize(body, EngineConfig.JsonOptions());
        var responseText = string.Empty;
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, ct);
            responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Batch standalone verifier returned HTTP {(int)response.StatusCode}: {Trim(responseText, 500)}");
            using var envelope = JsonDocument.Parse(responseText);
            var responseModel = envelope.RootElement.TryGetProperty("model", out var modelElement)
                ? modelElement.GetString() ?? string.Empty
                : string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Batch standalone model identity mismatch: requested '{model}', received '{responseModel}'.");
            var json = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
                       ?? throw new InvalidDataException("Batch standalone response content was empty.");
            var parsed = JsonSerializer.Deserialize<StandaloneResponse>(json, EngineConfig.JsonOptions())
                         ?? throw new InvalidDataException("Batch standalone JSON was empty.");
            var evidenceCatalog = IncidentBatchConfirmationEvidenceCatalog.Build(bundle);
            var proposedDecision = parsed.Decision switch
            {
                "verify" => IncidentBatchConfirmationDecisionKind.Verify,
                "review" => IncidentBatchConfirmationDecisionKind.Review,
                "reject" => IncidentBatchConfirmationDecisionKind.Reject,
                _ => throw new InvalidDataException($"Unsupported standalone decision '{parsed.Decision}'.")
            };
            var decision = new IncidentBatchStandaloneVerificationDecision(
                parsed.SourceProposalToken,
                parsed.ConcreteIncidentSupported
                    ? proposedDecision
                    : IncidentBatchConfirmationDecisionKind.Reject,
                parsed.ConcreteIncidentSupported,
                parsed.ConcreteIncidentSupported ? parsed.DisplayTitle : string.Empty,
                parsed.VerificationStatement,
                IncidentBatchConfirmationEvidenceCatalog.Resolve(parsed.EvidenceIds, evidenceCatalog),
                parsed.CounterEvidence,
                parsed.UnresolvedQuestions);
            await RecordUsageAsync(responseText, endpoint, model, payload.Length, true, string.Empty, ct);
            return new IncidentBatchStandaloneVerificationProposal(
                $"model:incident-batch-standalone-verification:{Guid.NewGuid():N}",
                DateTimeOffset.UtcNow,
                responseModel,
                IncidentBatchStandaloneVerificationPrompt.PromptIdentity,
                decision);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await RecordUsageAsync(
                responseText,
                endpoint,
                model,
                payload.Length,
                false,
                ex.GetBaseException().Message,
                CancellationToken.None);
            throw;
        }
    }

    private async Task RecordUsageAsync(
        string responseText,
        string endpoint,
        string requestedModel,
        int payloadChars,
        bool success,
        string error,
        CancellationToken ct)
    {
        var usage = ReadUsage(responseText);
        try
        {
            await _database.AddLmUsageAsync(new TokenUsageEntryDto(
                0,
                DateTime.UtcNow,
                $"incident batch standalone verification:{_runId}",
                "chat.completions",
                success,
                error,
                endpoint,
                requestedModel,
                usage.ResponseModel,
                usage.FinishReason,
                payloadChars,
                payloadChars,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not record incident batch standalone verifier usage");
        }
    }

    private static (int PromptTokens, int CompletionTokens, int TotalTokens, string ResponseModel, string FinishReason)
        ReadUsage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (0, 0, 0, string.Empty, string.Empty);
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
            var prompt = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var p)
                ? p.GetInt32()
                : 0;
            var completion = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var c)
                ? c.GetInt32()
                : 0;
            var total = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("total_tokens", out var t)
                ? t.GetInt32()
                : 0;
            var responseModel = root.TryGetProperty("model", out var m)
                ? m.GetString() ?? string.Empty
                : string.Empty;
            var finishReason = root.TryGetProperty("choices", out var choices) &&
                               choices.GetArrayLength() > 0 &&
                               choices[0].TryGetProperty("finish_reason", out var f)
                ? f.GetString() ?? string.Empty
                : string.Empty;
            return (prompt, completion, total, responseModel, finishReason);
        }
        catch
        {
            return (0, 0, 0, string.Empty, string.Empty);
        }
    }

    private static string Trim(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private sealed record StandaloneResponse(
        [property: JsonPropertyName("source_proposal_token")] string SourceProposalToken,
        [property: JsonPropertyName("concrete_incident_supported")] bool ConcreteIncidentSupported,
        [property: JsonPropertyName("decision")] string Decision,
        [property: JsonPropertyName("display_title")] string DisplayTitle,
        [property: JsonPropertyName("verification_statement")] string VerificationStatement,
        [property: JsonPropertyName("evidence_ids")] IReadOnlyList<string> EvidenceIds,
        [property: JsonPropertyName("counter_evidence")] IReadOnlyList<string> CounterEvidence,
        [property: JsonPropertyName("unresolved_questions")] IReadOnlyList<string> UnresolvedQuestions);
}
