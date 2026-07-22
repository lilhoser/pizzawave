using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pizzad;

internal static class PairwiseAdjudicationReplay
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
        var proposalDirectory = Path.GetFullPath(Required("--proposal-directory"));
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

        Directory.CreateDirectory(outputDirectory);
        var jsonOptions = EngineConfig.JsonOptions();
        var observations = await new IncidentEventStateCorpusSnapshotReader(databasePath)
            .ListCallsAsync(start, end, CancellationToken.None);
        var sourceBundle = IncidentEventStateCorpusExporter.BuildObservationBundle(
            $"{replayId}:source",
            DateTimeOffset.UtcNow,
            observations);
        var observationsById = sourceBundle.Observations.ToDictionary(
            observation => observation.ObservationId,
            StringComparer.Ordinal);

        var candidateManifestPath = RequiredManifest(candidateDirectory, "candidate");
        var proposalManifestPath = RequiredManifest(proposalDirectory, "proposal");
        var databaseSha256 = HashFile(databasePath);
        var candidateManifestSha256 = HashFile(candidateManifestPath);
        var sourceProposalManifest = JsonSerializer.Deserialize<SourceProposalManifest>(
            await File.ReadAllTextAsync(proposalManifestPath),
            jsonOptions) ?? throw new InvalidDataException("source proposal manifest is empty");
        if (!string.Equals(sourceProposalManifest.DatabaseSha256, databaseSha256, StringComparison.Ordinal) ||
            !string.Equals(sourceProposalManifest.CandidateManifestSha256, candidateManifestSha256, StringComparison.Ordinal) ||
            !string.Equals(sourceProposalManifest.PromptIdentity, IncidentEventStateSparseLinkPrompt.PromptIdentity, StringComparison.Ordinal))
        {
            throw new InvalidDataException("source proposals do not match the database, candidate manifest, and sparse-link prompt");
        }
        var manifest = new ReplayManifest(
            replayId,
            databaseSha256,
            candidateManifestSha256,
            HashFile(proposalManifestPath),
            endpoint,
            model,
            reasoningEffort ?? string.Empty,
            reasoningTokens,
            $"{IncidentEventStateSparseLinkPrompt.PromptIdentity}:pairwise-adjudication-v1");
        var manifestPath = Path.Combine(outputDirectory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            var prior = JsonSerializer.Deserialize<ReplayManifest>(await File.ReadAllTextAsync(manifestPath), jsonOptions)
                ?? throw new InvalidDataException("existing adjudication manifest is empty");
            if (prior != manifest)
                throw new InvalidDataException("existing adjudication manifest does not match its sources, endpoint, model, reasoning settings, and prompt");
        }
        else
        {
            await WriteJsonAtomicAsync(manifestPath, manifest, jsonOptions);
        }

        var work = await LoadWorkAsync(candidateDirectory, proposalDirectory, jsonOptions);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        var issuedRequests = 0;
        foreach (var item in work)
        {
            var resultPath = Path.Combine(outputDirectory, $"adjudication-{item.Ordinal:D5}.json");
            if (File.Exists(resultPath))
            {
                var prior = JsonSerializer.Deserialize<AdjudicationResult>(await File.ReadAllTextAsync(resultPath), jsonOptions);
                if (prior is not null && string.Equals(prior.SourceContentHash, item.SourceContentHash, StringComparison.Ordinal))
                    continue;
            }
            if (maximumRequests is not null && issuedRequests >= maximumRequests.Value)
                break;

            var result = await AdjudicateAsync(
                client,
                endpoint,
                model,
                reasoningEffort,
                reasoningTokens,
                replayId,
                item,
                observationsById,
                jsonOptions);
            await WriteJsonAtomicAsync(resultPath, result, jsonOptions);
            issuedRequests++;
            Console.WriteLine(
                $"adjudication {item.Ordinal}: batch={item.Proposal.Sequence} source={item.SourceCandidateToken} " +
                $"admitted={result.Admitted} ms={result.DurationMilliseconds} success={result.Success}");
        }

        var results = Directory.GetFiles(outputDirectory, "adjudication-*.json")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => JsonSerializer.Deserialize<AdjudicationResult>(File.ReadAllText(path), jsonOptions))
            .Where(result => result is not null)
            .Cast<AdjudicationResult>()
            .ToList();
        var modelResults = results.Where(result => result.DurationMilliseconds > 0).ToList();
        var report = new ReplayReport(
            replayId,
            work.Count,
            results.Count,
            modelResults.Count,
            results.Count(result => result.Admitted),
            results.Count(result => result.Success && !result.Admitted),
            results.Count(result => result.ValidationErrors.Count > 0),
            results.Count(result => !result.Success),
            results.Sum(result => result.TotalTokens),
            modelResults.Count == 0 ? 0 : modelResults.Average(result => result.DurationMilliseconds),
            modelResults.Count == 0 ? 0 : modelResults.Max(result => result.DurationMilliseconds),
            results.Count == work.Count);
        await WriteJsonAtomicAsync(Path.Combine(outputDirectory, "report.json"), report, jsonOptions);
        Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
    }

    private static async Task<AdjudicationResult> AdjudicateAsync(
        HttpClient client,
        string endpoint,
        string model,
        string? reasoningEffort,
        int? reasoningTokens,
        string replayId,
        WorkItem item,
        IReadOnlyDictionary<string, IncidentEventStateSourceObservation> observationsById,
        JsonSerializerOptions jsonOptions)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            if (!item.Proposal.Success)
                throw new InvalidDataException("source proposal batch did not complete successfully");
            if (!string.Equals(item.Proposal.CandidateBatchContentHash, item.Candidates.BatchContentHash, StringComparison.Ordinal))
                throw new InvalidDataException("proposal and candidate batch content hashes do not match");
            var plan = IncidentEventStatePairwiseAdjudication.Build(
                $"{replayId}:batch:{item.Proposal.Sequence:D5}",
                item.Proposal.Sequence,
                item.SourceCandidateToken,
                item.Candidates.Candidates,
                item.Candidates.ObservationIdsByToken,
                observationsById);
            var prompt = IncidentEventStateSparseLinkPrompt.Build(plan.PairwisePrompt, observationsById);
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
            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody, jsonOptions),
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
            var validation = IncidentEventStateSparseLinkValidator.Validate(
                plan.PairwisePrompt,
                observationsById,
                sparseEnvelope);
            var admitted = validation.IsValid && sparseEnvelope.Links.Count == 1;
            var usage = ReadUsage(responseEnvelope.RootElement);
            timer.Stop();
            return new AdjudicationResult(
                item.Ordinal,
                item.Proposal.Sequence,
                item.SourceCandidateToken,
                item.SourceContentHash,
                plan.NewObservationId,
                plan.TargetObservationId,
                true,
                admitted,
                DateTimeOffset.UtcNow,
                timer.ElapsedMilliseconds,
                usage.PromptTokens,
                usage.CompletionTokens,
                usage.TotalTokens,
                sparseEnvelope.Links.FirstOrDefault()?.RelationshipStatement ?? string.Empty,
                validation.Errors,
                string.Empty);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return new AdjudicationResult(
                item.Ordinal,
                item.Proposal.Sequence,
                item.SourceCandidateToken,
                item.SourceContentHash,
                string.Empty,
                string.Empty,
                false,
                false,
                DateTimeOffset.UtcNow,
                timer.ElapsedMilliseconds,
                0,
                0,
                0,
                string.Empty,
                [],
                ex.GetBaseException().Message);
        }
    }

    private static async Task<List<WorkItem>> LoadWorkAsync(
        string candidateDirectory,
        string proposalDirectory,
        JsonSerializerOptions jsonOptions)
    {
        var work = new List<WorkItem>();
        var ordinal = 0;
        foreach (var proposalPath in Directory.GetFiles(proposalDirectory, "batch-*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            var proposalBytes = await File.ReadAllBytesAsync(proposalPath);
            var proposal = JsonSerializer.Deserialize<ProposalBatchSource>(proposalBytes, jsonOptions)
                ?? throw new InvalidDataException($"proposal batch is empty: {proposalPath}");
            if (!proposal.Success || proposal.AdmittedCandidateTokens.Count == 0)
                continue;
            if (proposal.AdmittedCandidateTokens.Distinct(StringComparer.Ordinal).Count() != proposal.AdmittedCandidateTokens.Count)
                throw new InvalidDataException($"proposal batch contains duplicate admitted candidate tokens: {proposalPath}");
            var candidatePath = Path.Combine(candidateDirectory, $"batch-{proposal.Sequence:D5}.json");
            if (!File.Exists(candidatePath))
                throw new FileNotFoundException("candidate batch for proposal is missing", candidatePath);
            var candidateBytes = await File.ReadAllBytesAsync(candidatePath);
            var candidates = JsonSerializer.Deserialize<CandidateBatchSource>(candidateBytes, jsonOptions)
                ?? throw new InvalidDataException($"candidate batch is empty: {candidatePath}");
            foreach (var candidateToken in proposal.AdmittedCandidateTokens)
            {
                ordinal++;
                var identity = $"{Convert.ToHexString(SHA256.HashData(proposalBytes))}:" +
                               $"{Convert.ToHexString(SHA256.HashData(candidateBytes))}:{candidateToken}";
                work.Add(new WorkItem(
                    ordinal,
                    candidateToken,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))),
                    proposal,
                    candidates));
            }
        }
        return work;
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
                throw new ArgumentException("pairwise adjudication arguments must be --name value pairs");
            values[args[index]] = args[index + 1];
        }
        return values;
    }

    private static string RequiredManifest(string directory, string sourceName)
    {
        var path = Path.Combine(directory, "manifest.json");
        return File.Exists(path) ? path : throw new FileNotFoundException($"{sourceName} replay manifest is missing", path);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static async Task WriteJsonAtomicAsync(string path, object value, JsonSerializerOptions options)
    {
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, options));
        File.Move(temporary, path, true);
    }

    private sealed record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);
    private sealed record WorkItem(
        int Ordinal,
        string SourceCandidateToken,
        string SourceContentHash,
        ProposalBatchSource Proposal,
        CandidateBatchSource Candidates);
    private sealed record ReplayManifest(
        string ReplayId,
        string DatabaseSha256,
        string CandidateManifestSha256,
        string ProposalManifestSha256,
        string Endpoint,
        string Model,
        string ReasoningEffort,
        int? ReasoningTokens,
        string PromptIdentity);
    private sealed record SourceProposalManifest(
        string DatabaseSha256,
        string CandidateManifestSha256,
        string PromptIdentity);
    private sealed record ProposalBatchSource(
        int Sequence,
        string CandidateBatchContentHash,
        bool Success,
        IReadOnlyList<string> AdmittedCandidateTokens);
    private sealed record CandidateBatchSource(
        int Sequence,
        string BatchContentHash,
        bool Success,
        IReadOnlyDictionary<string, string> ObservationIdsByToken,
        IReadOnlyList<IncidentEventStateMicroBatchCandidate> Candidates);
    private sealed record AdjudicationResult(
        int Ordinal,
        int SourceBatchSequence,
        string SourceCandidateToken,
        string SourceContentHash,
        string NewObservationId,
        string TargetObservationId,
        bool Success,
        bool Admitted,
        DateTimeOffset CompletedAtUtc,
        long DurationMilliseconds,
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        string RelationshipStatement,
        IReadOnlyList<string> ValidationErrors,
        string Error);
    private sealed record ReplayReport(
        string ReplayId,
        int AvailableProposals,
        int ProcessedProposals,
        int ModelRequests,
        int AdmittedLinks,
        int RejectedLinks,
        int InvalidResponses,
        int FailedRequests,
        int TotalTokens,
        double AverageRequestMilliseconds,
        long MaximumRequestMilliseconds,
        bool Complete);
}
