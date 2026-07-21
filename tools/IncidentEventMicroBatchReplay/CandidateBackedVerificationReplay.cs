using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pizzad;

internal static class CandidateBackedVerificationReplay
{
    public static async Task RunAsync(string[] args)
    {
        var values = ParseArguments(args);
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required");
        int Integer(string name, int fallback) => values.TryGetValue(name, out var value) ? int.Parse(value) : fallback;
        var databasePath = Path.GetFullPath(Required("--database"));
        var start = long.Parse(Required("--start"));
        var end = long.Parse(Required("--end"));
        var replayId = Required("--replay-id");
        var candidateDirectory = Path.GetFullPath(Required("--candidate-directory"));
        var outputDirectory = Path.GetFullPath(Required("--output"));
        var endpoint = values.GetValueOrDefault("--endpoint", "http://127.0.0.1:1234/v1").TrimEnd('/');
        var model = Required("--model");
        var reasoningEffort = values.GetValueOrDefault("--reasoning-effort");
        var reasoningTokens = values.TryGetValue("--reasoning-tokens", out var reasoningTokensValue)
            ? int.Parse(reasoningTokensValue)
            : (int?)null;
        var timeoutSeconds = Integer("--timeout-seconds", 180);
        var maximumRequests = values.TryGetValue("--max-requests", out var maximumRequestsValue)
            ? int.Parse(maximumRequestsValue)
            : (int?)null;
        var sparseLinkMode = values.TryGetValue("--sparse-link-mode", out var sparseLinkValue) && bool.Parse(sparseLinkValue);

        Directory.CreateDirectory(outputDirectory);
        var jsonOptions = EngineConfig.JsonOptions();
        var reader = new IncidentEventStateCorpusSnapshotReader(databasePath);
        var calls = await reader.ListCallsAsync(start, end, CancellationToken.None);
        var bundle = IncidentEventStateCorpusExporter.BuildObservationBundle($"{replayId}:source", DateTimeOffset.UtcNow, calls);
        var observationsById = bundle.Observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var candidateManifestPath = Path.Combine(candidateDirectory, "manifest.json");
        if (!File.Exists(candidateManifestPath))
            throw new FileNotFoundException("candidate replay manifest is missing", candidateManifestPath);
        var manifest = new ReplayManifest(
            replayId,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(databasePath))),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(candidateManifestPath))),
            endpoint,
            model,
            reasoningEffort ?? string.Empty,
            reasoningTokens,
            sparseLinkMode
                ? IncidentEventStateSparseLinkPrompt.PromptIdentity
                : IncidentEventStateMicroBatchPrompt.PromptIdentity);
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var prior = JsonSerializer.Deserialize<ReplayManifest>(await File.ReadAllTextAsync(manifestPath), jsonOptions)
                ?? throw new InvalidDataException("existing verification manifest is empty");
            if (prior != manifest)
                throw new InvalidDataException("existing verification manifest does not match the snapshot, candidate run, endpoint, model, reasoning settings, and prompt");
        }
        else
        {
            await WriteJsonAtomicAsync(manifestPath, manifest, jsonOptions);
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        var requests = 0;
        var candidateFiles = Directory.GetFiles(candidateDirectory, "batch-*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        foreach (var candidatePath in candidateFiles)
        {
            var source = JsonSerializer.Deserialize<CandidateBatchSource>(await File.ReadAllTextAsync(candidatePath), jsonOptions)
                ?? throw new InvalidDataException($"candidate batch is empty: {candidatePath}");
            if (!source.Success)
                continue;
            var resultPath = Path.Combine(outputDirectory, $"batch-{source.Sequence:D5}.json");
            if (File.Exists(resultPath))
            {
                var prior = JsonSerializer.Deserialize<BatchResult>(await File.ReadAllTextAsync(resultPath), jsonOptions);
                if (prior is not null && prior.Success && string.Equals(prior.CandidateBatchContentHash, source.BatchContentHash, StringComparison.Ordinal))
                    continue;
            }
            if (source.Candidates.Count == 0)
            {
                await WriteJsonAtomicAsync(resultPath, BatchResult.Empty(source.Sequence, source.BatchContentHash), jsonOptions);
                continue;
            }
            if (maximumRequests is not null && requests >= maximumRequests.Value)
                break;

            var plan = IncidentEventStateCandidateBackedMicroBatch.Build(
                $"{replayId}:batch:{source.Sequence:D5}",
                source.Sequence,
                source.Candidates,
                source.ObservationIdsByToken,
                observationsById);
            var sparsePrompt = sparseLinkMode
                ? IncidentEventStateSparseLinkPrompt.Build(plan, observationsById)
                : null;
            var requestBody = new
            {
                model,
                temperature = 0.1,
                max_tokens = sparseLinkMode ? 1200 : 2400,
                reasoning_effort = reasoningEffort,
                reasoning_tokens = reasoningTokens,
                response_format = sparsePrompt?.ResponseFormat ?? plan.Prompt.ResponseFormat,
                messages = new object[]
                {
                    new { role = "system", content = sparsePrompt?.SystemPrompt ?? plan.Prompt.SystemPrompt },
                    new { role = "user", content = sparsePrompt?.UserPrompt ?? plan.Prompt.UserPrompt }
                }
            };
            var timer = Stopwatch.StartNew();
            BatchResult result;
            try
            {
                using var content = new StringContent(JsonSerializer.Serialize(requestBody, jsonOptions), Encoding.UTF8, "application/json");
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
                IReadOnlyList<IncidentEventStateMicroBatchObservationDecision> decisions;
                IReadOnlyList<string> admitted;
                IReadOnlyList<string> invalid;
                IReadOnlyList<string> validationErrors;
                if (sparseLinkMode)
                {
                    var sparseEnvelope = ParseSparseLinks(responseModel, responseContent);
                    var sparseValidation = IncidentEventStateSparseLinkValidator.Validate(plan, observationsById, sparseEnvelope);
                    decisions = sparseEnvelope.Links
                        .Select(link => ToDecision(plan, link))
                        .Where(decision => decision is not null)
                        .Cast<IncidentEventStateMicroBatchObservationDecision>()
                        .ToList();
                    admitted = sparseValidation.IsValid
                        ? sparseEnvelope.Links.Select(link => link.CandidateToken).ToList()
                        : [];
                    invalid = sparseValidation.IsValid
                        ? []
                        : sparseEnvelope.Links.Select(link => link.CandidateToken).Distinct(StringComparer.Ordinal).ToList();
                    validationErrors = sparseValidation.Errors;
                }
                else
                {
                    decisions = ParseDecisions(responseContent);
                    var proposal = new IncidentEventStateMicroBatchProposal(responseModel, IncidentEventStateMicroBatchPrompt.PromptIdentity, decisions);
                    var baseValidation = IncidentEventStateMicroBatchProposalValidator.Validate(plan.Batch, observationsById, plan.Prompt, proposal);
                    var expectedTokens = plan.Batch.NewObservationIds
                        .Select(id => plan.Prompt.ObservationIdsByToken.Single(item => item.Value == id).Key)
                        .ToList();
                    var shapeIsValid = decisions.Select(decision => decision.NewObservationToken)
                        .SequenceEqual(expectedTokens, StringComparer.Ordinal);
                    var checkedDecisions = decisions.Select(decision => new
                    {
                        Decision = decision,
                        Validation = IncidentEventStateCandidateBackedMicroBatch.ValidateDecision(plan, observationsById, decision)
                    }).ToList();
                    admitted = shapeIsValid
                        ? checkedDecisions
                            .Where(item => item.Decision.Decision == IncidentEventStateMicroBatchDecision.ProposeLink && item.Validation.IsValid)
                            .Select(item => IncidentEventStateCandidateBackedMicroBatch.CandidateTokenFor(plan, item.Decision))
                            .Where(token => token is not null)
                            .Cast<string>()
                            .ToList()
                        : [];
                    invalid = checkedDecisions
                        .Where(item => !item.Validation.IsValid)
                        .Select(item => item.Decision.NewObservationToken)
                        .ToList();
                    validationErrors = baseValidation.Errors
                        .Concat(checkedDecisions.SelectMany(item => item.Validation.Errors))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                }
                var usage = ReadUsage(envelope.RootElement);
                timer.Stop();
                result = new BatchResult(
                    source.Sequence,
                    source.BatchContentHash,
                    true,
                    DateTimeOffset.UtcNow,
                    timer.ElapsedMilliseconds,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    plan.Prompt.ObservationIdsByToken,
                    decisions,
                    admitted,
                    invalid,
                    validationErrors,
                    string.Empty);
            }
            catch (Exception ex)
            {
                timer.Stop();
                result = new BatchResult(
                    source.Sequence,
                    source.BatchContentHash,
                    false,
                    DateTimeOffset.UtcNow,
                    timer.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    plan.Prompt.ObservationIdsByToken,
                    [],
                    [],
                    [],
                    [],
                    ex.GetBaseException().Message);
            }
            await WriteJsonAtomicAsync(resultPath, result, jsonOptions);
            requests++;
            Console.WriteLine($"batch {source.Sequence}: candidates={source.Candidates.Count} admitted={result.AdmittedCandidateTokens.Count} ms={result.DurationMilliseconds} success={result.Success}");
        }

        var results = Directory.GetFiles(outputDirectory, "batch-*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<BatchResult>(File.ReadAllText(path), jsonOptions))
            .Where(result => result is not null)
            .Cast<BatchResult>()
            .ToList();
        var successful = results.Where(result => result.Success).ToList();
        var modelResults = successful.Where(result => result.DurationMilliseconds > 0).ToList();
        var report = new ReplayReport(
            replayId,
            candidateFiles.Count,
            results.Count,
            modelResults.Count,
            successful.Sum(result => result.Decisions.Count),
            successful.Sum(result => result.AdmittedCandidateTokens.Count),
            successful.Sum(result => result.InvalidNewObservationTokens.Count),
            results.Count(result => !result.Success),
            successful.Sum(result => result.TotalTokens),
            modelResults.Count == 0 ? 0 : modelResults.Average(result => result.DurationMilliseconds),
            modelResults.Count == 0 ? 0 : modelResults.Max(result => result.DurationMilliseconds),
            results.Count == candidateFiles.Count);
        await WriteJsonAtomicAsync(Path.Combine(outputDirectory, "report.json"), report, jsonOptions);
        Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
    }

    private static IReadOnlyList<IncidentEventStateMicroBatchObservationDecision> ParseDecisions(string responseContent)
    {
        using var document = JsonDocument.Parse(responseContent);
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

    private static IncidentEventStateSparseLinkEnvelope ParseSparseLinks(string model, string responseContent)
    {
        using var document = JsonDocument.Parse(responseContent);
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

    private static IncidentEventStateMicroBatchObservationDecision? ToDecision(
        IncidentEventStateCandidateBackedMicroBatchPrompt plan,
        IncidentEventStateSparseLinkProposal proposal)
    {
        var candidate = plan.CandidateLinks.SingleOrDefault(link =>
            string.Equals(link.CandidateToken, proposal.CandidateToken, StringComparison.Ordinal));
        if (candidate is null)
            return null;
        var tokenById = plan.Prompt.ObservationIdsByToken.ToDictionary(item => item.Value, item => item.Key, StringComparer.Ordinal);
        return new IncidentEventStateMicroBatchObservationDecision(
            tokenById[candidate.NewObservationId],
            IncidentEventStateMicroBatchDecision.ProposeLink,
            tokenById[candidate.TargetObservationId],
            proposal.RelationshipStatement,
            proposal.Uncertainty,
            proposal.NewEvidenceTranscriptIds,
            proposal.TargetEvidenceTranscriptIds,
            []);
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement array) =>
        array.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();

    private static Usage ReadUsage(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("usage", out var usage))
            return new Usage(0, 0, 0);
        var prompt = usage.TryGetProperty("prompt_tokens", out var promptValue) ? promptValue.GetInt32() : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var completionValue) ? completionValue.GetInt32() : 0;
        var total = usage.TryGetProperty("total_tokens", out var totalValue) ? totalValue.GetInt32() : prompt + completion;
        return new Usage(prompt, completion, total);
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("candidate-backed verification arguments must be --name value pairs");
            values[args[index]] = args[index + 1];
        }
        return values;
    }

    private static async Task WriteJsonAtomicAsync(string path, object value, JsonSerializerOptions options)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, options));
        File.Move(temporary, path, true);
    }

    private sealed record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);
    private sealed record ReplayManifest(
        string ReplayId,
        string DatabaseSha256,
        string CandidateManifestSha256,
        string Endpoint,
        string Model,
        string ReasoningEffort,
        int? ReasoningTokens,
        string PromptIdentity);
    private sealed record CandidateBatchSource(
        int Sequence,
        string BatchContentHash,
        bool Success,
        IReadOnlyDictionary<string, string> ObservationIdsByToken,
        IReadOnlyList<IncidentEventStateMicroBatchCandidate> Candidates);
    private sealed record BatchResult(
        int Sequence,
        string CandidateBatchContentHash,
        bool Success,
        DateTimeOffset CompletedAtUtc,
        long DurationMilliseconds,
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        IReadOnlyDictionary<string, string> ObservationIdsByToken,
        IReadOnlyList<IncidentEventStateMicroBatchObservationDecision> Decisions,
        IReadOnlyList<string> AdmittedCandidateTokens,
        IReadOnlyList<string> InvalidNewObservationTokens,
        IReadOnlyList<string> ValidationErrors,
        string Error)
    {
        public static BatchResult Empty(int sequence, string contentHash) => new(
            sequence, contentHash, true, DateTimeOffset.UtcNow, 0, 0, 0, 0,
            new Dictionary<string, string>(StringComparer.Ordinal), [], [], [], [], string.Empty);
    }
    private sealed record ReplayReport(
        string ReplayId,
        int AvailableCandidateBatches,
        int ProcessedBatches,
        int ModelRequests,
        int Decisions,
        int AdmittedLinks,
        int InvalidDecisions,
        int FailedBatches,
        int TotalTokens,
        double AverageRequestMilliseconds,
        long MaximumRequestMilliseconds,
        bool Complete);
}
