using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pizzad;

internal static class CandidateReplay
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
        var outputDirectory = Path.GetFullPath(Required("--output"));
        var exhaustiveMode = values.TryGetValue("--exhaustive-mode", out var exhaustiveValue) && bool.Parse(exhaustiveValue);
        var endpoint = exhaustiveMode
            ? string.Empty
            : values.GetValueOrDefault("--endpoint", "http://127.0.0.1:1234/v1").TrimEnd('/');
        var model = exhaustiveMode ? IncidentEventStateMicroBatchExhaustiveCandidates.RetrieverIdentity : Required("--model");
        var timeoutSeconds = Integer("--timeout-seconds", 120);
        var maximumBatches = values.TryGetValue("--max-batches", out var maximumBatchesValue)
            ? int.Parse(maximumBatchesValue)
            : (int?)null;
        var options = new IncidentEventStateMicroBatchReplayOptions(
            Integer("--batch-size", 12),
            Integer("--batch-span-seconds", 60),
            Integer("--context-size", 24),
            Integer("--context-lookback-seconds", 1200));

        Directory.CreateDirectory(outputDirectory);
        var jsonOptions = EngineConfig.JsonOptions();
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        CandidateReplayManifest? priorManifest = null;
        var createdAt = DateTimeOffset.UtcNow;
        if (File.Exists(manifestPath))
        {
            priorManifest = JsonSerializer.Deserialize<CandidateReplayManifest>(
                await File.ReadAllTextAsync(manifestPath), jsonOptions)
                ?? throw new InvalidDataException("existing candidate replay manifest is empty");
            createdAt = priorManifest.Plan.CreatedAtUtc;
        }
        var reader = new IncidentEventStateCorpusSnapshotReader(databasePath);
        var calls = await reader.ListCallsAsync(start, end, CancellationToken.None);
        var bundle = IncidentEventStateCorpusExporter.BuildObservationBundle($"{replayId}:source", createdAt, calls);
        var observationsById = bundle.Observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var plan = IncidentEventStateMicroBatchReplayPlanner.Build(replayId, createdAt, bundle.Observations, options);
        var databaseHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(databasePath)));
        var manifest = new CandidateReplayManifest(
            plan,
            databaseHash,
            start,
            end,
            endpoint,
            model,
            exhaustiveMode
                ? IncidentEventStateMicroBatchExhaustiveCandidates.RetrieverIdentity
                : IncidentEventStateMicroBatchCandidatePrompt.PromptIdentity);
        if (priorManifest is not null)
        {
            if (!string.Equals(priorManifest.Plan.ContentHash, manifest.Plan.ContentHash, StringComparison.Ordinal) ||
                !string.Equals(priorManifest.DatabaseSha256, manifest.DatabaseSha256, StringComparison.Ordinal) ||
                !string.Equals(priorManifest.Endpoint, manifest.Endpoint, StringComparison.Ordinal) ||
                !string.Equals(priorManifest.Model, manifest.Model, StringComparison.Ordinal) ||
                !string.Equals(priorManifest.PromptIdentity, manifest.PromptIdentity, StringComparison.Ordinal))
            {
                throw new InvalidDataException("existing candidate replay manifest does not match this corpus, plan, endpoint, model, and prompt");
            }
        }
        else
        {
            await WriteJsonAtomicAsync(manifestPath, manifest, jsonOptions);
        }
        Console.WriteLine($"planned observations={plan.PlannedObservationCount} batches={plan.Batches.Count} hash={plan.ContentHash}");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        var executed = 0;
        foreach (var batch in plan.Batches)
        {
            var resultPath = Path.Combine(outputDirectory, $"batch-{batch.Sequence:D5}.json");
            if (File.Exists(resultPath))
            {
                var prior = JsonSerializer.Deserialize<CandidateBatchResult>(await File.ReadAllTextAsync(resultPath), jsonOptions);
                if (prior is not null && prior.Success && string.Equals(prior.BatchContentHash, batch.ContentHash, StringComparison.Ordinal))
                    continue;
            }
            if (maximumBatches is not null && executed >= maximumBatches.Value)
                break;

            var prompt = IncidentEventStateMicroBatchCandidatePrompt.Build(batch, observationsById);
            if (exhaustiveMode)
            {
                var candidates = IncidentEventStateMicroBatchExhaustiveCandidates.Build(batch, prompt);
                var deterministicResult = new CandidateBatchResult(
                    batch.Sequence,
                    batch.BatchId,
                    batch.ContentHash,
                    true,
                    DateTimeOffset.UtcNow,
                    0,
                    0,
                    0,
                    0,
                    prompt.ObservationIdsByToken,
                    candidates,
                    [],
                    string.Empty);
                await WriteJsonAtomicAsync(resultPath, deterministicResult, jsonOptions);
                executed++;
                Console.WriteLine($"batch {batch.Sequence}/{plan.Batches.Count}: success=True candidates={candidates.Count} deterministic=True");
                continue;
            }
            var requestBody = new
            {
                model,
                temperature = 0.1,
                max_tokens = 1800,
                response_format = prompt.ResponseFormat,
                messages = new object[]
                {
                    new { role = "system", content = prompt.SystemPrompt },
                    new { role = "user", content = prompt.UserPrompt }
                }
            };
            var timer = Stopwatch.StartNew();
            CandidateBatchResult result;
            try
            {
                var requestJson = JsonSerializer.Serialize(requestBody, jsonOptions);
                using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
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
                var usage = ReadUsage(envelope.RootElement);
                timer.Stop();
                result = new CandidateBatchResult(
                    batch.Sequence,
                    batch.BatchId,
                    batch.ContentHash,
                    validation.IsValid,
                    DateTimeOffset.UtcNow,
                    timer.ElapsedMilliseconds,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    prompt.ObservationIdsByToken,
                    candidates,
                    validation.Errors,
                    validation.IsValid ? string.Empty : "candidate output failed deterministic validation");
            }
            catch (Exception ex)
            {
                timer.Stop();
                result = new CandidateBatchResult(
                    batch.Sequence,
                    batch.BatchId,
                    batch.ContentHash,
                    false,
                    DateTimeOffset.UtcNow,
                    timer.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    prompt.ObservationIdsByToken,
                    [],
                    [],
                    ex.GetBaseException().Message);
            }
            await WriteJsonAtomicAsync(resultPath, result, jsonOptions);
            executed++;
            Console.WriteLine($"batch {batch.Sequence}/{plan.Batches.Count}: success={result.Success} candidates={result.Candidates.Count} ms={result.DurationMilliseconds} tokens={result.TotalTokens} error={result.Error}");
        }

        var results = new List<CandidateBatchResult>();
        foreach (var path in Directory.GetFiles(outputDirectory, "batch-*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var result = JsonSerializer.Deserialize<CandidateBatchResult>(await File.ReadAllTextAsync(path), jsonOptions);
            if (result is not null)
                results.Add(result);
        }
        var successful = results.Where(result => result.Success).ToList();
        var decidedObservations = successful.Sum(result => plan.Batches[result.Sequence - 1].NewObservationIds.Count);
        var report = new CandidateReplayReport(
            replayId,
            plan.ContentHash,
            plan.PlannedObservationCount,
            plan.Batches.Count,
            successful.Count,
            decidedObservations,
            successful.Sum(result => result.Candidates.Count),
            decidedObservations == 0 ? 0 : (double)successful.Sum(result => result.Candidates.Count) / decidedObservations,
            results.Count(result => !result.Success),
            successful.Sum(result => result.TotalTokens),
            successful.Count == 0 ? 0 : successful.Average(result => result.DurationMilliseconds),
            successful.Count == 0 ? 0 : successful.Max(result => result.DurationMilliseconds),
            successful.Count == plan.Batches.Count);
        await WriteJsonAtomicAsync(Path.Combine(outputDirectory, "report.json"), report, jsonOptions);
        Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("candidate replay arguments must be --name value pairs");
            values[args[index]] = args[index + 1];
        }
        return values;
    }

    private static Usage ReadUsage(JsonElement envelope)
    {
        if (!envelope.TryGetProperty("usage", out var usage))
            return new Usage(0, 0, 0);
        var prompt = usage.TryGetProperty("prompt_tokens", out var promptValue) ? promptValue.GetInt32() : 0;
        var completion = usage.TryGetProperty("completion_tokens", out var completionValue) ? completionValue.GetInt32() : 0;
        var total = usage.TryGetProperty("total_tokens", out var totalValue) ? totalValue.GetInt32() : prompt + completion;
        return new Usage(prompt, completion, total);
    }

    private static async Task WriteJsonAtomicAsync(string path, object value, JsonSerializerOptions options)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, options));
        File.Move(temporary, path, true);
    }

    private sealed record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);
    private sealed record CandidateReplayManifest(
        IncidentEventStateMicroBatchReplayPlan Plan,
        string DatabaseSha256,
        long WindowStartUnixSeconds,
        long WindowEndUnixSeconds,
        string Endpoint,
        string Model,
        string PromptIdentity);
    private sealed record CandidateBatchResult(
        int Sequence,
        string BatchId,
        string BatchContentHash,
        bool Success,
        DateTimeOffset CompletedAtUtc,
        long DurationMilliseconds,
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        IReadOnlyDictionary<string, string> ObservationIdsByToken,
        IReadOnlyList<IncidentEventStateMicroBatchCandidate> Candidates,
        IReadOnlyList<string> ValidationErrors,
        string Error);
    private sealed record CandidateReplayReport(
        string ReplayId,
        string PlanContentHash,
        int PlannedObservations,
        int PlannedBatches,
        int SuccessfulBatches,
        int CoveredObservations,
        int CandidatePairs,
        double CandidatesPerObservation,
        int FailedBatches,
        int TotalTokens,
        double AverageBatchMilliseconds,
        long MaximumBatchMilliseconds,
        bool Complete);
}
