using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pizzad;

if (args.Contains("--combined-capacity-plan", StringComparer.Ordinal))
{
    await CombinedCapacityReplay.RunAsync(args);
    return;
}

if (args.Contains("--batch-constructor-scenario-replay", StringComparer.Ordinal))
{
    await BatchConstructorScenarioReplay.RunAsync(args);
    return;
}

if (args.Contains("--pairwise-adjudication-replay", StringComparer.Ordinal))
{
    await PairwiseAdjudicationReplay.RunAsync(args);
    return;
}

if (args.Contains("--candidate-backed-verification-replay", StringComparer.Ordinal))
{
    await CandidateBackedVerificationReplay.RunAsync(args);
    return;
}

if (args.Contains("--verification-replay", StringComparer.Ordinal))
{
    await VerificationReplay.RunAsync(args);
    return;
}

if (args.Contains("--candidate-replay", StringComparer.Ordinal))
{
    await CandidateReplay.RunAsync(args);
    return;
}

if (args.Contains("--review-package", StringComparer.Ordinal))
{
    await ReviewEvaluation.RunAsync(args);
    return;
}

var arguments = Arguments.Parse(args);
var jsonOptions = EngineConfig.JsonOptions();
Directory.CreateDirectory(arguments.OutputDirectory);
var manifestPath = Path.Combine(arguments.OutputDirectory, "manifest.json");
ReplayManifest? existingManifest = null;
if (File.Exists(manifestPath))
{
    existingManifest = JsonSerializer.Deserialize<ReplayManifest>(await File.ReadAllTextAsync(manifestPath), jsonOptions)
        ?? throw new InvalidDataException("existing replay manifest is empty");
    arguments = arguments with { CreatedAtUtc = existingManifest.Plan.CreatedAtUtc };
}
var reader = new IncidentEventStateCorpusSnapshotReader(arguments.DatabasePath);
var calls = await reader.ListCallsAsync(arguments.StartUnixSeconds, arguments.EndUnixSeconds, CancellationToken.None);
var sourceBundle = IncidentEventStateCorpusExporter.BuildObservationBundle(
    $"{arguments.ReplayId}:source",
    arguments.CreatedAtUtc,
    calls);
var observations = sourceBundle.Observations.ToList();
var observationsById = observations.ToDictionary(observation => observation.ObservationId, StringComparer.Ordinal);
var plan = IncidentEventStateMicroBatchReplayPlanner.Build(
    arguments.ReplayId,
    arguments.CreatedAtUtc,
    observations,
    arguments.Options);

var snapshotHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(arguments.DatabasePath)));
var manifest = new ReplayManifest(
    plan,
    Path.GetFullPath(arguments.DatabasePath),
    snapshotHash,
    arguments.StartUnixSeconds,
    arguments.EndUnixSeconds,
    arguments.Endpoint,
    arguments.Model,
    arguments.TimeoutSeconds,
    arguments.MaximumCompletionTokens);
if (existingManifest is not null)
{
    if (!string.Equals(existingManifest.Plan.ContentHash, manifest.Plan.ContentHash, StringComparison.Ordinal) ||
        !string.Equals(existingManifest.DatabaseSha256, manifest.DatabaseSha256, StringComparison.Ordinal) ||
        !string.Equals(existingManifest.Model, manifest.Model, StringComparison.Ordinal) ||
        !string.Equals(existingManifest.Endpoint, manifest.Endpoint, StringComparison.Ordinal))
    {
        throw new InvalidDataException("existing replay manifest does not match the requested corpus, plan, endpoint, and model");
    }
}
else
{
    await WriteJsonAtomicAsync(manifestPath, manifest, jsonOptions);
}
Console.WriteLine($"planned observations={plan.PlannedObservationCount} batches={plan.Batches.Count} hash={plan.ContentHash}");
if (arguments.DryRun)
    return;

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(arguments.TimeoutSeconds) };
if (!string.IsNullOrWhiteSpace(arguments.ApiKey))
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", arguments.ApiKey);
var executed = 0;
foreach (var batch in plan.Batches)
{
    var resultPath = Path.Combine(arguments.OutputDirectory, $"batch-{batch.Sequence:D5}.json");
    if (File.Exists(resultPath))
    {
        var prior = JsonSerializer.Deserialize<ReplayBatchResult>(await File.ReadAllTextAsync(resultPath), jsonOptions);
        if (prior is not null && prior.Success && string.Equals(prior.BatchContentHash, batch.ContentHash, StringComparison.Ordinal))
        {
            Console.WriteLine($"batch {batch.Sequence}/{plan.Batches.Count}: already complete");
            continue;
        }
    }
    if (arguments.MaximumBatches is not null && executed >= arguments.MaximumBatches.Value)
        break;

    var prompt = IncidentEventStateMicroBatchPrompt.Build(batch, observationsById);
    var timer = Stopwatch.StartNew();
    ReplayBatchResult result;
    try
    {
        var requestBody = new
        {
            model = arguments.Model,
            temperature = 0.1,
            max_tokens = arguments.MaximumCompletionTokens,
            response_format = prompt.ResponseFormat,
            messages = new object[]
            {
                new { role = "system", content = prompt.SystemPrompt },
                new { role = "user", content = prompt.UserPrompt }
            }
        };
        var requestJson = JsonSerializer.Serialize(requestBody, jsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync($"{arguments.Endpoint.TrimEnd('/')}/chat/completions", content);
        var responseJson = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Trim(responseJson, 1000)}");

        using var envelope = JsonDocument.Parse(responseJson);
        var responseModel = envelope.RootElement.TryGetProperty("model", out var responseModelElement)
            ? responseModelElement.GetString() ?? string.Empty
            : string.Empty;
        if (!string.Equals(responseModel, arguments.Model, StringComparison.Ordinal))
            throw new InvalidDataException($"model identity mismatch: requested '{arguments.Model}', received '{responseModel}'");
        var contentJson = envelope.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("content").GetString() ?? throw new InvalidDataException("model response content was empty");
        var proposal = ParseProposal(responseModel, contentJson);
        var validation = IncidentEventStateMicroBatchProposalValidator.Validate(batch, observationsById, prompt, proposal);
        var expectedTokens = batch.NewObservationIds
            .Select(id => prompt.ObservationIdsByToken.Single(item => item.Value == id).Key)
            .ToList();
        var shapeIsValid = proposal.Decisions.Select(decision => decision.NewObservationToken)
            .SequenceEqual(expectedTokens, StringComparer.Ordinal);
        var decisionValidations = proposal.Decisions.ToDictionary(
            decision => decision.NewObservationToken,
            decision => IncidentEventStateMicroBatchProposalValidator.ValidateDecision(
                batch,
                observationsById,
                prompt,
                decision),
            StringComparer.Ordinal);
        var admittedLinkTokens = proposal.Decisions
            .Where(decision =>
                decision.Decision == IncidentEventStateMicroBatchDecision.ProposeLink &&
                decisionValidations[decision.NewObservationToken].IsValid)
            .Select(decision => decision.NewObservationToken)
            .ToList();
        var invalidDecisionTokens = decisionValidations
            .Where(item => !item.Value.IsValid)
            .Select(item => item.Key)
            .ToList();
        var usage = ReadUsage(envelope.RootElement);
        timer.Stop();
        result = new ReplayBatchResult(
            batch.Sequence,
            batch.BatchId,
            batch.ContentHash,
            shapeIsValid,
            DateTimeOffset.UtcNow,
            timer.ElapsedMilliseconds,
            requestJson.Length,
            usage.PromptTokens,
            usage.CompletionTokens,
            usage.TotalTokens,
            responseModel,
            prompt.ObservationIdsByToken,
            proposal.Decisions,
            admittedLinkTokens,
            invalidDecisionTokens,
            validation.Errors,
            shapeIsValid ? string.Empty : "model output did not account for every new observation exactly once");
    }
    catch (Exception ex)
    {
        timer.Stop();
        result = new ReplayBatchResult(
            batch.Sequence,
            batch.BatchId,
            batch.ContentHash,
            false,
            DateTimeOffset.UtcNow,
            timer.ElapsedMilliseconds,
            0,
            0,
            0,
            0,
            arguments.Model,
            prompt.ObservationIdsByToken,
            [],
            [],
            [],
            [],
            ex.GetBaseException().Message);
    }
    await WriteJsonAtomicAsync(resultPath, result, jsonOptions);
    executed++;
    Console.WriteLine(
        $"batch {batch.Sequence}/{plan.Batches.Count}: success={result.Success} proposed={result.Decisions.Count(decision => decision.Decision == IncidentEventStateMicroBatchDecision.ProposeLink)} admitted={result.AdmittedLinkObservationTokens.Count} invalid={result.InvalidDecisionObservationTokens.Count} ms={result.DurationMilliseconds} tokens={result.TotalTokens} error={Trim(result.Error, 160)}");
}

var resultFiles = Directory.GetFiles(arguments.OutputDirectory, "batch-*.json").OrderBy(path => path, StringComparer.Ordinal).ToList();
var results = new List<ReplayBatchResult>();
foreach (var path in resultFiles)
{
    var result = JsonSerializer.Deserialize<ReplayBatchResult>(await File.ReadAllTextAsync(path), jsonOptions);
    if (result is not null)
        results.Add(result);
}
var completed = results.Where(result => result.Success).ToList();
var report = new ReplayRunReport(
    arguments.ReplayId,
    plan.ContentHash,
    plan.PlannedObservationCount,
    plan.Batches.Count,
    completed.Count,
    completed.Sum(result => result.Decisions.Count),
    completed.Sum(result => result.Decisions.Count(decision => decision.Decision == IncidentEventStateMicroBatchDecision.ProposeLink)),
    completed.Sum(result => result.AdmittedLinkObservationTokens.Count),
    completed.Sum(result => result.InvalidDecisionObservationTokens.Count),
    results.Count(result => !result.Success),
    completed.Sum(result => result.PromptTokens),
    completed.Sum(result => result.CompletionTokens),
    completed.Sum(result => result.TotalTokens),
    completed.Count == 0 ? 0 : completed.Average(result => result.DurationMilliseconds),
    completed.Count == 0 ? 0 : completed.Max(result => result.DurationMilliseconds),
    completed.Count == plan.Batches.Count);
await WriteJsonAtomicAsync(Path.Combine(arguments.OutputDirectory, "report.json"), report, jsonOptions);
Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));

static IncidentEventStateMicroBatchProposal ParseProposal(string model, string contentJson)
{
    using var document = JsonDocument.Parse(contentJson);
    var decisions = new List<IncidentEventStateMicroBatchObservationDecision>();
    foreach (var row in document.RootElement.GetProperty("decisions").EnumerateArray())
    {
        var decisionText = row.GetProperty("decision").GetString();
        var decision = decisionText switch
        {
            "propose_link" => IncidentEventStateMicroBatchDecision.ProposeLink,
            "unresolved" => IncidentEventStateMicroBatchDecision.Unresolved,
            _ => throw new InvalidDataException($"unsupported decision '{decisionText}'")
        };
        decisions.Add(new IncidentEventStateMicroBatchObservationDecision(
            row.GetProperty("new_observation_token").GetString() ?? string.Empty,
            decision,
            row.GetProperty("target_observation_token").GetString() ?? string.Empty,
            row.GetProperty("relationship_statement").GetString() ?? string.Empty,
            row.GetProperty("uncertainty").GetDouble(),
            ReadStrings(row.GetProperty("new_evidence_transcript_ids")),
            ReadStrings(row.GetProperty("target_evidence_transcript_ids")),
            ReadStrings(row.GetProperty("unresolved_questions"))));
    }
    return new IncidentEventStateMicroBatchProposal(model, IncidentEventStateMicroBatchPrompt.PromptIdentity, decisions);
}

static IReadOnlyList<string> ReadStrings(JsonElement array) =>
    array.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToList();

static Usage ReadUsage(JsonElement envelope)
{
    if (!envelope.TryGetProperty("usage", out var usage))
        return new Usage(0, 0, 0);
    var prompt = usage.TryGetProperty("prompt_tokens", out var promptElement) ? promptElement.GetInt32() : 0;
    var completion = usage.TryGetProperty("completion_tokens", out var completionElement) ? completionElement.GetInt32() : 0;
    var total = usage.TryGetProperty("total_tokens", out var totalElement) ? totalElement.GetInt32() : prompt + completion;
    return new Usage(prompt, completion, total);
}

static async Task WriteJsonAtomicAsync(string path, object value, JsonSerializerOptions options)
{
    var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, options));
    File.Move(temporary, path, true);
}

static string Trim(string value, int limit) => value.Length <= limit ? value : value[..limit];

sealed record Usage(int PromptTokens, int CompletionTokens, int TotalTokens);

sealed record ReplayManifest(
    IncidentEventStateMicroBatchReplayPlan Plan,
    string DatabasePath,
    string DatabaseSha256,
    long WindowStartUnixSeconds,
    long WindowEndUnixSeconds,
    string Endpoint,
    string Model,
    int TimeoutSeconds,
    int MaximumCompletionTokens);

sealed record ReplayBatchResult(
    int Sequence,
    string BatchId,
    string BatchContentHash,
    bool Success,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int RequestCharacters,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    string ModelIdentity,
    IReadOnlyDictionary<string, string> ObservationIdsByToken,
    IReadOnlyList<IncidentEventStateMicroBatchObservationDecision> Decisions,
    IReadOnlyList<string> AdmittedLinkObservationTokens,
    IReadOnlyList<string> InvalidDecisionObservationTokens,
    IReadOnlyList<string> ValidationErrors,
    string Error);

sealed record ReplayRunReport(
    string ReplayId,
    string PlanContentHash,
    int PlannedObservations,
    int PlannedBatches,
    int SuccessfulBatches,
    int DecidedObservations,
    int ProposedLinks,
    int AdmittedLinks,
    int InvalidDecisions,
    int FailedBatches,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    double AverageBatchMilliseconds,
    long MaximumBatchMilliseconds,
    bool Complete);

sealed record Arguments(
    string DatabasePath,
    long StartUnixSeconds,
    long EndUnixSeconds,
    string ReplayId,
    DateTimeOffset CreatedAtUtc,
    IncidentEventStateMicroBatchReplayOptions Options,
    string OutputDirectory,
    string Endpoint,
    string Model,
    string ApiKey,
    int TimeoutSeconds,
    int MaximumCompletionTokens,
    int? MaximumBatches,
    bool DryRun)
{
    public static Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"unexpected argument '{args[index]}'");
            if (string.Equals(args[index], "--dry-run", StringComparison.Ordinal))
            {
                flags.Add(args[index]);
                continue;
            }
            if (++index >= args.Length)
                throw new ArgumentException($"missing value for '{args[index - 1]}'");
            values[args[index - 1]] = args[index];
        }
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required");
        int Integer(string name, int fallback) => values.TryGetValue(name, out var value) ? int.Parse(value) : fallback;

        var createdAt = DateTimeOffset.UtcNow;
        return new Arguments(
            Path.GetFullPath(Required("--database")),
            long.Parse(Required("--start")),
            long.Parse(Required("--end")),
            values.GetValueOrDefault("--replay-id", $"microbatch-{createdAt:yyyyMMddTHHmmssZ}"),
            createdAt,
            new IncidentEventStateMicroBatchReplayOptions(
                Integer("--batch-size", 12),
                Integer("--batch-span-seconds", 60),
                Integer("--context-size", 24),
                Integer("--context-lookback-seconds", 1200)),
            Path.GetFullPath(Required("--output")),
            values.GetValueOrDefault("--endpoint", "http://127.0.0.1:1234/v1"),
            Required("--model"),
            values.GetValueOrDefault("--api-key", string.Empty),
            Integer("--timeout-seconds", 180),
            Integer("--max-completion-tokens", 4000),
            values.TryGetValue("--max-batches", out var maximumBatches) ? int.Parse(maximumBatches) : null,
            flags.Contains("--dry-run"));
    }
}
