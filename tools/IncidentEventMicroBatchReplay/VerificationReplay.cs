using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pizzad;

internal static class VerificationReplay
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
        var timeoutSeconds = Integer("--timeout-seconds", 180);
        var maximumRequests = values.TryGetValue("--max-requests", out var maximumRequestsValue)
            ? int.Parse(maximumRequestsValue)
            : (int?)null;

        Directory.CreateDirectory(outputDirectory);
        var jsonOptions = EngineConfig.JsonOptions();
        var reader = new IncidentEventStateCorpusSnapshotReader(databasePath);
        var calls = await reader.ListCallsAsync(start, end, CancellationToken.None);
        var bundle = IncidentEventStateCorpusExporter.BuildObservationBundle($"{replayId}:source", DateTimeOffset.UtcNow, calls);
        var observationsById = bundle.Observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
        var candidateManifestPath = Path.Combine(candidateDirectory, "manifest.json");
        if (!File.Exists(candidateManifestPath))
            throw new FileNotFoundException("candidate replay manifest is missing", candidateManifestPath);
        var candidateManifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(candidateManifestPath)));
        var databaseHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(databasePath)));
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        var manifest = new VerificationReplayManifest(
            replayId,
            databaseHash,
            candidateManifestHash,
            endpoint,
            model,
            IncidentEventStateMicroBatchVerificationPrompt.PromptIdentity);
        if (File.Exists(manifestPath))
        {
            var prior = JsonSerializer.Deserialize<VerificationReplayManifest>(await File.ReadAllTextAsync(manifestPath), jsonOptions)
                ?? throw new InvalidDataException("existing verification manifest is empty");
            if (prior != manifest)
                throw new InvalidDataException("existing verification manifest does not match the snapshot, candidate run, endpoint, model, and prompt");
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
                var prior = JsonSerializer.Deserialize<VerificationBatchResult>(await File.ReadAllTextAsync(resultPath), jsonOptions);
                if (prior is not null && prior.Success && string.Equals(prior.CandidateBatchContentHash, source.BatchContentHash, StringComparison.Ordinal))
                    continue;
            }
            if (source.Candidates.Count == 0)
            {
                await WriteJsonAtomicAsync(resultPath, new VerificationBatchResult(
                    source.Sequence,
                    source.BatchContentHash,
                    true,
                    DateTimeOffset.UtcNow,
                    0,
                    0,
                    0,
                    0,
                    source.ObservationIdsByToken,
                    [],
                    [],
                    [],
                    [],
                    string.Empty), jsonOptions);
                continue;
            }
            if (maximumRequests is not null && requests >= maximumRequests.Value)
                break;

            var prompt = IncidentEventStateMicroBatchVerificationPrompt.Build(
                source.Candidates,
                source.ObservationIdsByToken,
                observationsById);
            var requestBody = new
            {
                model,
                temperature = 0.1,
                max_tokens = 2400,
                response_format = prompt.ResponseFormat,
                messages = new object[]
                {
                    new { role = "system", content = prompt.SystemPrompt },
                    new { role = "user", content = prompt.UserPrompt }
                }
            };
            var timer = Stopwatch.StartNew();
            VerificationBatchResult result;
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
                var decisions = ParseDecisions(responseContent);
                var proposal = new IncidentEventStateMicroBatchVerificationProposal(
                    responseModel,
                    IncidentEventStateMicroBatchVerificationPrompt.PromptIdentity,
                    decisions);
                var validation = IncidentEventStateMicroBatchVerificationValidator.Validate(prompt, observationsById, proposal);
                var expectedTokens = prompt.Pairs.Select(pair => pair.CandidateToken).ToList();
                var shapeIsValid = decisions.Select(decision => decision.CandidateToken)
                    .SequenceEqual(expectedTokens, StringComparer.Ordinal);
                var decisionValidations = decisions.Select(decision => new
                {
                    Decision = decision,
                    Validation = IncidentEventStateMicroBatchVerificationValidator.ValidateDecision(
                        prompt,
                        observationsById,
                        decision)
                }).ToList();
                var verified = shapeIsValid
                    ? decisionValidations
                        .Where(item =>
                            item.Decision.Decision == IncidentEventStateMicroBatchVerificationDecisionKind.VerifyLink &&
                            item.Validation.IsValid)
                        .Select(item => item.Decision.CandidateToken)
                        .ToList()
                    : [];
                var invalid = decisionValidations
                    .Where(item => !item.Validation.IsValid)
                    .Select(item => item.Decision.CandidateToken)
                    .ToList();
                var usage = ReadUsage(envelope.RootElement);
                timer.Stop();
                result = new VerificationBatchResult(
                    source.Sequence,
                    source.BatchContentHash,
                    shapeIsValid,
                    DateTimeOffset.UtcNow,
                    timer.ElapsedMilliseconds,
                    usage.PromptTokens,
                    usage.CompletionTokens,
                    usage.TotalTokens,
                    source.ObservationIdsByToken,
                    decisions,
                    verified,
                    invalid,
                    validation.Errors,
                    shapeIsValid ? string.Empty : "verifier output did not account for every candidate exactly once");
            }
            catch (Exception ex)
            {
                timer.Stop();
                result = new VerificationBatchResult(
                    source.Sequence,
                    source.BatchContentHash,
                    false,
                    DateTimeOffset.UtcNow,
                    timer.ElapsedMilliseconds,
                    0,
                    0,
                    0,
                    source.ObservationIdsByToken,
                    [],
                    [],
                    [],
                    [],
                    ex.GetBaseException().Message);
            }
            await WriteJsonAtomicAsync(resultPath, result, jsonOptions);
            requests++;
            Console.WriteLine($"batch {source.Sequence}: candidates={source.Candidates.Count} verified={result.VerifiedCandidateTokens.Count} success={result.Success} ms={result.DurationMilliseconds} tokens={result.TotalTokens} error={result.Error}");
        }

        var results = new List<VerificationBatchResult>();
        foreach (var path in Directory.GetFiles(outputDirectory, "batch-*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var result = JsonSerializer.Deserialize<VerificationBatchResult>(await File.ReadAllTextAsync(path), jsonOptions);
            if (result is not null)
                results.Add(result);
        }
        var successful = results.Where(result => result.Success).ToList();
        var modelResults = successful.Where(result => result.DurationMilliseconds > 0).ToList();
        var report = new VerificationReplayReport(
            replayId,
            candidateFiles.Count,
            results.Count,
            modelResults.Count,
            successful.Sum(result => result.Decisions.Count),
            successful.Sum(result => result.VerifiedCandidateTokens.Count),
            successful.Sum(result => result.InvalidCandidateTokens.Count),
            results.Count(result => !result.Success),
            successful.Sum(result => result.TotalTokens),
            modelResults.Count == 0 ? 0 : modelResults.Average(result => result.DurationMilliseconds),
            modelResults.Count == 0 ? 0 : modelResults.Max(result => result.DurationMilliseconds),
            results.Count == candidateFiles.Count);
        await WriteJsonAtomicAsync(Path.Combine(outputDirectory, "report.json"), report, jsonOptions);
        Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
    }

    private static IReadOnlyList<IncidentEventStateMicroBatchVerificationDecision> ParseDecisions(string responseContent)
    {
        using var document = JsonDocument.Parse(responseContent);
        return document.RootElement.GetProperty("decisions").EnumerateArray().Select(row =>
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
                throw new ArgumentException("verification replay arguments must be --name value pairs");
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
    private sealed record VerificationReplayManifest(
        string ReplayId,
        string DatabaseSha256,
        string CandidateManifestSha256,
        string Endpoint,
        string Model,
        string PromptIdentity);
    private sealed record CandidateBatchSource(
        int Sequence,
        string BatchContentHash,
        bool Success,
        IReadOnlyDictionary<string, string> ObservationIdsByToken,
        IReadOnlyList<IncidentEventStateMicroBatchCandidate> Candidates);
    private sealed record VerificationBatchResult(
        int Sequence,
        string CandidateBatchContentHash,
        bool Success,
        DateTimeOffset CompletedAtUtc,
        long DurationMilliseconds,
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        IReadOnlyDictionary<string, string> ObservationIdsByToken,
        IReadOnlyList<IncidentEventStateMicroBatchVerificationDecision> Decisions,
        IReadOnlyList<string> VerifiedCandidateTokens,
        IReadOnlyList<string> InvalidCandidateTokens,
        IReadOnlyList<string> ValidationErrors,
        string Error);
    private sealed record VerificationReplayReport(
        string ReplayId,
        int AvailableCandidateBatches,
        int ProcessedBatches,
        int ModelRequests,
        int CandidatePairs,
        int VerifiedLinks,
        int InvalidDecisions,
        int FailedBatches,
        int TotalTokens,
        double AverageRequestMilliseconds,
        long MaximumRequestMilliseconds,
        bool Complete);
}
