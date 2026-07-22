using System.Diagnostics;
using System.Text;
using System.Text.Json;
using pizzad;

internal static class ReviewEvaluation
{
    public static async Task RunAsync(string[] args)
    {
        var values = ParseArguments(args);
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required");
        var packagePath = Path.GetFullPath(Required("--review-package"));
        var reviewPath = Path.GetFullPath(Required("--review"));
        var outputPath = Path.GetFullPath(Required("--output"));
        var endpoint = values.GetValueOrDefault("--endpoint", "http://127.0.0.1:1234/v1").TrimEnd('/');
        var model = Required("--model");
        var timeoutSeconds = values.TryGetValue("--timeout-seconds", out var timeout) ? int.Parse(timeout) : 180;
        var reasoningEffort = values.GetValueOrDefault("--reasoning-effort");
        var reasoningTokens = values.TryGetValue("--reasoning-tokens", out var reasoningTokensValue)
            ? int.Parse(reasoningTokensValue)
            : (int?)null;
        var candidateMode = values.TryGetValue("--candidate-mode", out var candidateValue) && bool.Parse(candidateValue);
        var groupedVerifierMode = values.TryGetValue("--grouped-verifier-mode", out var groupedValue) && bool.Parse(groupedValue);
        var sparseLinkMode = values.TryGetValue("--sparse-link-mode", out var sparseLinkValue) && bool.Parse(sparseLinkValue);
        var selectedCaseId = values.GetValueOrDefault("--case-id", string.Empty);
        if (new[] { candidateMode, groupedVerifierMode, sparseLinkMode }.Count(enabled => enabled) > 1)
            throw new ArgumentException("candidate, grouped verifier, and sparse link modes are mutually exclusive");

        using var package = JsonDocument.Parse(ReadPackageJson(packagePath));
        using var review = JsonDocument.Parse(await File.ReadAllTextAsync(reviewPath));
        var assessments = review.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(
            row => row.GetProperty("case_id").GetString() ?? string.Empty,
            row => row.GetProperty("relationship_assessment").GetString() ?? string.Empty,
            StringComparer.Ordinal);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        var results = new List<ReviewCaseResult>();
        foreach (var fixture in package.RootElement.GetProperty("cases").EnumerateArray())
        {
            var caseId = fixture.GetProperty("case_id").GetString() ?? throw new InvalidDataException("review case id is missing");
            if (!string.IsNullOrWhiteSpace(selectedCaseId) && !string.Equals(caseId, selectedCaseId, StringComparison.Ordinal))
                continue;
            var observations = fixture.GetProperty("observations").EnumerateArray().Select(ParseObservation).ToList();
            if (observations.Count != 2)
                throw new InvalidDataException($"review case '{caseId}' must contain two observations");
            var batch = new IncidentEventStateMicroBatchReplayBatch(
                caseId,
                results.Count + 1,
                observations[0].ObservedAtUnixSeconds,
                observations[1].ObservedAtUnixSeconds,
                observations.Select(observation => observation.ObservationId).ToList(),
                [],
                caseId);
            var lookup = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
            if (groupedVerifierMode)
            {
                results.Add(await EvaluateGroupedVerifierAsync(
                    client,
                    endpoint,
                    model,
                    reasoningEffort,
                    reasoningTokens,
                    caseId,
                    assessments.GetValueOrDefault(caseId, string.Empty),
                    lookup));
                Console.WriteLine($"{caseId}: assessment={results[^1].Assessment} verified={results[^1].AdmittedLink} ms={results[^1].DurationMilliseconds} error={results[^1].Error}");
                continue;
            }
            if (candidateMode)
            {
                results.Add(await EvaluateCandidateAsync(
                    client,
                    endpoint,
                    model,
                    reasoningEffort,
                    reasoningTokens,
                    caseId,
                    assessments.GetValueOrDefault(caseId, string.Empty),
                    batch,
                    lookup));
                Console.WriteLine($"{caseId}: assessment={results[^1].Assessment} candidate={results[^1].AdmittedLink} ms={results[^1].DurationMilliseconds} error={results[^1].Error}");
                continue;
            }
            if (sparseLinkMode)
            {
                results.Add(await EvaluateSparseLinksAsync(
                    client,
                    endpoint,
                    model,
                    reasoningEffort,
                    reasoningTokens,
                    caseId,
                    assessments.GetValueOrDefault(caseId, string.Empty),
                    lookup));
                Console.WriteLine($"{caseId}: assessment={results[^1].Assessment} admitted={results[^1].AdmittedLink} ms={results[^1].DurationMilliseconds} error={results[^1].Error}");
                continue;
            }
            var prompt = IncidentEventStateMicroBatchPrompt.Build(batch, lookup);
            var requestBody = new
            {
                model,
                temperature = 0.1,
                max_tokens = 1800,
                reasoning_effort = reasoningEffort,
                reasoning_tokens = reasoningTokens,
                response_format = prompt.ResponseFormat,
                messages = new object[]
                {
                    new { role = "system", content = prompt.SystemPrompt },
                    new { role = "user", content = prompt.UserPrompt }
                }
            };
            var timer = Stopwatch.StartNew();
            try
            {
                using var content = new StringContent(
                    JsonSerializer.Serialize(requestBody, EngineConfig.JsonOptions()),
                    Encoding.UTF8,
                    "application/json");
                using var response = await client.PostAsync($"{endpoint}/chat/completions", content);
                var responseJson = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {responseJson[..Math.Min(1000, responseJson.Length)]}");
                using var envelope = JsonDocument.Parse(responseJson);
                var responseModel = envelope.RootElement.GetProperty("model").GetString() ?? string.Empty;
                if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                    throw new InvalidDataException($"model identity mismatch: requested '{model}', received '{responseModel}'");
                var responseContent = envelope.RootElement.GetProperty("choices")[0].GetProperty("message")
                    .GetProperty("content").GetString() ?? throw new InvalidDataException("model response content was empty");
                var decisions = ParseDecisions(responseContent);
                var proposal = new IncidentEventStateMicroBatchProposal(
                    responseModel,
                    IncidentEventStateMicroBatchPrompt.PromptIdentity,
                    decisions);
                var validation = IncidentEventStateMicroBatchProposalValidator.Validate(batch, lookup, prompt, proposal);
                var second = decisions.SingleOrDefault(decision => string.Equals(decision.NewObservationToken, "new-2", StringComparison.Ordinal));
                var secondValidation = second is null
                    ? new IncidentEventStateContractValidationResult(false, ["new-2 decision is missing"])
                    : IncidentEventStateMicroBatchProposalValidator.ValidateDecision(batch, lookup, prompt, second);
                timer.Stop();
                results.Add(new ReviewCaseResult(
                    caseId,
                    assessments.GetValueOrDefault(caseId, string.Empty),
                    second?.Decision.ToString() ?? string.Empty,
                    second?.TargetObservationToken ?? string.Empty,
                    second?.RelationshipStatement ?? string.Empty,
                    second is not null && second.Decision == IncidentEventStateMicroBatchDecision.ProposeLink && secondValidation.IsValid,
                    validation.Errors,
                    timer.ElapsedMilliseconds,
                    string.Empty));
            }
            catch (Exception ex)
            {
                timer.Stop();
                results.Add(new ReviewCaseResult(
                    caseId,
                    assessments.GetValueOrDefault(caseId, string.Empty),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    false,
                    [],
                    timer.ElapsedMilliseconds,
                    ex.GetBaseException().Message));
            }
            Console.WriteLine($"{caseId}: assessment={results[^1].Assessment} admitted={results[^1].AdmittedLink} ms={results[^1].DurationMilliseconds} error={results[^1].Error}");
        }

        var scored = results.Where(result => result.Assessment is "same_event" or "not_same_event").ToList();
        var truePositive = scored.Count(result => result.Assessment == "same_event" && result.AdmittedLink);
        var falseNegative = scored.Count(result => result.Assessment == "same_event" && !result.AdmittedLink);
        var falsePositive = scored.Count(result => result.Assessment == "not_same_event" && result.AdmittedLink);
        var trueNegative = scored.Count(result => result.Assessment == "not_same_event" && !result.AdmittedLink);
        var report = new ReviewEvaluationReport(
            model,
            reasoningEffort ?? string.Empty,
            reasoningTokens,
            groupedVerifierMode
                ? "grouped_verification"
                : candidateMode
                    ? "candidate_retrieval"
                    : sparseLinkMode
                        ? "sparse_link_verification"
                        : "final_verification",
            groupedVerifierMode
                ? IncidentEventStateMicroBatchVerificationPrompt.PromptIdentity
                : candidateMode
                    ? IncidentEventStateMicroBatchCandidatePrompt.PromptIdentity
                    : sparseLinkMode
                        ? IncidentEventStateSparseLinkPrompt.PromptIdentity
                        : IncidentEventStateMicroBatchPrompt.PromptIdentity,
            Path.GetFileName(packagePath),
            Path.GetFileName(reviewPath),
            results,
            truePositive,
            falsePositive,
            trueNegative,
            falseNegative,
            truePositive + falsePositive == 0 ? 0 : (double)truePositive / (truePositive + falsePositive),
            truePositive + falseNegative == 0 ? 0 : (double)truePositive / (truePositive + falseNegative));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, EngineConfig.JsonOptions()));
        Console.WriteLine(JsonSerializer.Serialize(report, EngineConfig.JsonOptions()));
    }

    private static async Task<ReviewCaseResult> EvaluateSparseLinksAsync(
        HttpClient client,
        string endpoint,
        string model,
        string? reasoningEffort,
        int? reasoningTokens,
        string caseId,
        string assessment,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> lookup)
    {
        var ordered = lookup.Values
            .OrderBy(observation => observation.ObservedAtUnixSeconds)
            .ThenBy(observation => observation.ObservationId, StringComparer.Ordinal)
            .ToList();
        var plan = IncidentEventStateCandidateBackedMicroBatch.Build(
            caseId,
            1,
            [new IncidentEventStateMicroBatchCandidate("new-2", "new-1", string.Empty)],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["new-1"] = ordered[0].ObservationId,
                ["new-2"] = ordered[1].ObservationId
            },
            lookup);
        var prompt = IncidentEventStateSparseLinkPrompt.Build(plan, lookup);
        var requestBody = new
        {
            model,
            temperature = 0.1,
            max_tokens = 1200,
            reasoning_effort = reasoningEffort,
            reasoning_tokens = reasoningTokens,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        var timer = Stopwatch.StartNew();
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody, EngineConfig.JsonOptions()),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync($"{endpoint}/chat/completions", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {responseJson[..Math.Min(1000, responseJson.Length)]}");
            using var responseEnvelope = JsonDocument.Parse(responseJson);
            var responseModel = responseEnvelope.RootElement.GetProperty("model").GetString() ?? string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"model identity mismatch: requested '{model}', received '{responseModel}'");
            var responseContent = responseEnvelope.RootElement.GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString() ?? throw new InvalidDataException("model response content was empty");
            var sparseEnvelope = ParseSparseLinks(responseModel, responseContent);
            var validation = IncidentEventStateSparseLinkValidator.Validate(plan, lookup, sparseEnvelope);
            var link = sparseEnvelope.Links.FirstOrDefault();
            timer.Stop();
            return new ReviewCaseResult(
                caseId,
                assessment,
                link is null ? IncidentEventStateMicroBatchDecision.Unresolved.ToString() : IncidentEventStateMicroBatchDecision.ProposeLink.ToString(),
                link?.CandidateToken ?? string.Empty,
                link?.RelationshipStatement ?? string.Empty,
                validation.IsValid && link is not null,
                validation.Errors,
                timer.ElapsedMilliseconds,
                string.Empty);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return new ReviewCaseResult(
                caseId,
                assessment,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                [],
                timer.ElapsedMilliseconds,
                ex.GetBaseException().Message);
        }
    }

    private static async Task<ReviewCaseResult> EvaluateGroupedVerifierAsync(
        HttpClient client,
        string endpoint,
        string model,
        string? reasoningEffort,
        int? reasoningTokens,
        string caseId,
        string assessment,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> lookup)
    {
        var ordered = lookup.Values
            .OrderBy(observation => observation.ObservedAtUnixSeconds)
            .ThenBy(observation => observation.ObservationId, StringComparer.Ordinal)
            .ToList();
        var tokenMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["new-1"] = ordered[0].ObservationId,
            ["new-2"] = ordered[1].ObservationId
        };
        var candidate = new IncidentEventStateMicroBatchCandidate("new-2", "new-1", string.Empty);
        var prompt = IncidentEventStateMicroBatchVerificationPrompt.Build([candidate], tokenMap, lookup);
        var requestBody = new
        {
            model,
            temperature = 0.1,
            max_tokens = 1200,
            reasoning_effort = reasoningEffort,
            reasoning_tokens = reasoningTokens,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        var timer = Stopwatch.StartNew();
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody, EngineConfig.JsonOptions()),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync($"{endpoint}/chat/completions", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {responseJson[..Math.Min(1000, responseJson.Length)]}");
            using var envelope = JsonDocument.Parse(responseJson);
            var responseModel = envelope.RootElement.GetProperty("model").GetString() ?? string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"model identity mismatch: requested '{model}', received '{responseModel}'");
            var responseContent = envelope.RootElement.GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString() ?? throw new InvalidDataException("model response content was empty");
            using var decisionJson = JsonDocument.Parse(responseContent);
            var decisions = decisionJson.RootElement.GetProperty("decisions").EnumerateArray().Select(row =>
                new IncidentEventStateMicroBatchVerificationDecision(
                    row.GetProperty("candidate_token").GetString() ?? string.Empty,
                    row.GetProperty("decision").GetString() switch
                    {
                        "verify_link" => IncidentEventStateMicroBatchVerificationDecisionKind.VerifyLink,
                        "reject" => IncidentEventStateMicroBatchVerificationDecisionKind.Reject,
                        var value => throw new InvalidDataException($"unsupported verification decision '{value}'")
                    },
                    row.GetProperty("relationship_statement").GetString() ?? string.Empty,
                    row.GetProperty("uncertainty").GetDouble(),
                    ReadStrings(row.GetProperty("new_evidence_transcript_ids")),
                    ReadStrings(row.GetProperty("target_evidence_transcript_ids")),
                    ReadStrings(row.GetProperty("unresolved_questions")))).ToList();
            var proposal = new IncidentEventStateMicroBatchVerificationProposal(
                responseModel,
                IncidentEventStateMicroBatchVerificationPrompt.PromptIdentity,
                decisions);
            var validation = IncidentEventStateMicroBatchVerificationValidator.Validate(prompt, lookup, proposal);
            var decision = decisions.SingleOrDefault();
            var admitted = validation.IsValid &&
                           decision?.Decision == IncidentEventStateMicroBatchVerificationDecisionKind.VerifyLink;
            timer.Stop();
            return new ReviewCaseResult(
                caseId,
                assessment,
                decision?.Decision.ToString() ?? string.Empty,
                decision?.CandidateToken ?? string.Empty,
                decision?.RelationshipStatement ?? string.Empty,
                admitted,
                validation.Errors,
                timer.ElapsedMilliseconds,
                string.Empty);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return new ReviewCaseResult(
                caseId,
                assessment,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                [],
                timer.ElapsedMilliseconds,
                ex.GetBaseException().Message);
        }
    }

    private static async Task<ReviewCaseResult> EvaluateCandidateAsync(
        HttpClient client,
        string endpoint,
        string model,
        string? reasoningEffort,
        int? reasoningTokens,
        string caseId,
        string assessment,
        IncidentEventStateMicroBatchReplayBatch batch,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> lookup)
    {
        var prompt = IncidentEventStateMicroBatchCandidatePrompt.Build(batch, lookup);
        var requestBody = new
        {
            model,
            temperature = 0.1,
            max_tokens = 1000,
            reasoning_effort = reasoningEffort,
            reasoning_tokens = reasoningTokens,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        var timer = Stopwatch.StartNew();
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody, EngineConfig.JsonOptions()),
                Encoding.UTF8,
                "application/json");
            using var response = await client.PostAsync($"{endpoint}/chat/completions", content);
            var responseJson = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {responseJson[..Math.Min(1000, responseJson.Length)]}");
            using var envelope = JsonDocument.Parse(responseJson);
            var responseModel = envelope.RootElement.GetProperty("model").GetString() ?? string.Empty;
            if (!string.Equals(responseModel, model, StringComparison.Ordinal))
                throw new InvalidDataException($"model identity mismatch: requested '{model}', received '{responseModel}'");
            var responseContent = envelope.RootElement.GetProperty("choices")[0].GetProperty("message")
                .GetProperty("content").GetString() ?? throw new InvalidDataException("model response content was empty");
            using var candidateJson = JsonDocument.Parse(responseContent);
            var candidates = candidateJson.RootElement.GetProperty("candidates").EnumerateArray().Select(row =>
                new IncidentEventStateMicroBatchCandidate(
                    row.GetProperty("new_observation_token").GetString() ?? string.Empty,
                    row.GetProperty("target_observation_token").GetString() ?? string.Empty,
                    row.GetProperty("reason_to_compare").GetString() ?? string.Empty)).ToList();
            var proposal = new IncidentEventStateMicroBatchCandidateProposal(
                responseModel,
                IncidentEventStateMicroBatchCandidatePrompt.PromptIdentity,
                candidates);
            var validation = IncidentEventStateMicroBatchCandidateValidator.Validate(batch, prompt, proposal);
            var candidate = candidates.FirstOrDefault(row =>
                string.Equals(row.NewObservationToken, "new-2", StringComparison.Ordinal) &&
                string.Equals(row.TargetObservationToken, "new-1", StringComparison.Ordinal));
            timer.Stop();
            return new ReviewCaseResult(
                caseId,
                assessment,
                candidate is null ? "NoCandidate" : "Candidate",
                candidate?.TargetObservationToken ?? string.Empty,
                candidate?.ReasonToCompare ?? string.Empty,
                validation.IsValid && candidate is not null,
                validation.Errors,
                timer.ElapsedMilliseconds,
                string.Empty);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return new ReviewCaseResult(
                caseId,
                assessment,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                [],
                timer.ElapsedMilliseconds,
                ex.GetBaseException().Message);
        }
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("review evaluation arguments must be --name value pairs");
            values[args[index]] = args[index + 1];
        }
        return values;
    }

    private static string ReadPackageJson(string path)
    {
        var text = File.ReadAllText(path).Trim();
        var objectStart = text.IndexOf('{');
        var objectEnd = text.LastIndexOf('}');
        if (objectStart < 0 || objectEnd < objectStart)
            throw new InvalidDataException("review package does not contain a JSON object");
        return text[objectStart..(objectEnd + 1)];
    }

    private static IncidentEventStateSourceObservation ParseObservation(JsonElement row)
    {
        var observationId = row.GetProperty("observation_id").GetString() ?? string.Empty;
        var transcripts = row.GetProperty("transcripts").EnumerateArray()
            .Where(transcript => string.Equals(
                transcript.GetProperty("producer").GetString(),
                "pizzad.calls.transcription",
                StringComparison.Ordinal))
            .Select(transcript => new IncidentEventStateTranscriptObservation(
                transcript.GetProperty("transcript_id").GetString() ?? string.Empty,
                transcript.GetProperty("text").GetString() ?? string.Empty,
                transcript.GetProperty("producer").GetString() ?? string.Empty,
                null))
            .ToList();
        return new IncidentEventStateSourceObservation(
            observationId,
            null,
            row.GetProperty("observed_at_unix_seconds").GetInt64(),
            string.Empty,
            null,
            transcripts,
            new Dictionary<string, IncidentEventStateMetadataObservation>(StringComparer.Ordinal));
    }

    private static IReadOnlyList<IncidentEventStateMicroBatchObservationDecision> ParseDecisions(string contentJson)
    {
        using var document = JsonDocument.Parse(contentJson);
        return document.RootElement.GetProperty("decisions").EnumerateArray().Select(row =>
            new IncidentEventStateMicroBatchObservationDecision(
                row.GetProperty("new_observation_token").GetString() ?? string.Empty,
                row.GetProperty("decision").GetString() switch
                {
                    "propose_link" => IncidentEventStateMicroBatchDecision.ProposeLink,
                    "unresolved" => IncidentEventStateMicroBatchDecision.Unresolved,
                    var value => throw new InvalidDataException($"unsupported decision '{value}'")
                },
                row.GetProperty("target_observation_token").GetString() ?? string.Empty,
                row.GetProperty("relationship_statement").GetString() ?? string.Empty,
                row.GetProperty("uncertainty").GetDouble(),
                ReadStrings(row.GetProperty("new_evidence_transcript_ids")),
                ReadStrings(row.GetProperty("target_evidence_transcript_ids")),
                ReadStrings(row.GetProperty("unresolved_questions")))).ToList();
    }

    private static IncidentEventStateSparseLinkEnvelope ParseSparseLinks(string model, string contentJson)
    {
        using var document = JsonDocument.Parse(contentJson);
        var root = document.RootElement;
        var links = root.GetProperty("links").EnumerateArray().Select(row =>
            new IncidentEventStateSparseLinkProposal(
                row.GetProperty("candidate_token").GetString() ?? string.Empty,
                row.GetProperty("relationship_statement").GetString() ?? string.Empty,
                row.GetProperty("uncertainty").GetDouble(),
                ReadStrings(row.GetProperty("new_evidence_transcript_ids")),
                ReadStrings(row.GetProperty("target_evidence_transcript_ids")))).ToList();
        return new IncidentEventStateSparseLinkEnvelope(
            model,
            IncidentEventStateSparseLinkPrompt.PromptIdentity,
            root.GetProperty("completed").GetBoolean(),
            links);
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();

    private sealed record ReviewCaseResult(
        string CaseId,
        string Assessment,
        string Decision,
        string TargetObservationToken,
        string RelationshipStatement,
        bool AdmittedLink,
        IReadOnlyList<string> ValidationErrors,
        long DurationMilliseconds,
        string Error);

    private sealed record ReviewEvaluationReport(
        string Model,
        string ReasoningEffort,
        int? ReasoningTokens,
        string Mode,
        string PromptIdentity,
        string PackageFile,
        string ReviewFile,
        IReadOnlyList<ReviewCaseResult> Cases,
        int TruePositive,
        int FalsePositive,
        int TrueNegative,
        int FalseNegative,
        double Precision,
        double Recall);
}
