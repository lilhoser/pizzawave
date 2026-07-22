using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using pizzad;

internal static class BatchConstructorScenarioReplay
{
    public static async Task RunAsync(string[] args)
    {
        var values = ParseArguments(args.Where(arg => !string.Equals(arg, "--batch-constructor-scenario-replay", StringComparison.Ordinal)).ToArray());
        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required");
        var databasePath = Path.GetFullPath(Required("--database"));
        var scenarioPath = Path.GetFullPath(Required("--scenario"));
        var outputDirectory = Path.GetFullPath(Required("--output"));
        var endpoint = Required("--endpoint").TrimEnd('/');
        var model = Required("--model");
        var timeoutSeconds = values.TryGetValue("--timeout-seconds", out var timeout) ? int.Parse(timeout) : 360;
        var jsonOptions = EngineConfig.JsonOptions();
        var scenario = JsonSerializer.Deserialize<Scenario>(await File.ReadAllTextAsync(scenarioPath), jsonOptions)
            ?? throw new InvalidDataException("scenario is empty");
        ValidateScenario(scenario);
        Directory.CreateDirectory(outputDirectory);
        var resultPath = Path.Combine(outputDirectory, "result.json");
        if (File.Exists(resultPath))
            throw new InvalidOperationException($"refusing to overwrite existing replay result '{resultPath}'");

        var reader = new IncidentEventStateCorpusSnapshotReader(databasePath);
        var calls = await reader.ListCallsAsync(scenario.WindowStartUnixSeconds, scenario.WindowEndUnixSeconds, CancellationToken.None);
        var requiredCallIds = scenario.NewCallIds
            .Concat(scenario.Candidates.SelectMany(candidate => candidate.CallIds))
            .Distinct()
            .ToHashSet();
        var selectedCalls = calls.Where(call => requiredCallIds.Contains(call.Id)).OrderBy(call => call.StartTime).ThenBy(call => call.Id).ToList();
        var missing = requiredCallIds.Except(selectedCalls.Select(call => call.Id)).OrderBy(id => id).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"scenario calls are missing from snapshot: {string.Join(", ", missing)}");

        var createdAt = DateTimeOffset.UtcNow;
        var bundle = IncidentEventStateCorpusExporter.BuildObservationBundle($"{scenario.ReplayId}:bundle", createdAt, selectedCalls);
        var newObservationIds = scenario.NewCallIds.Select(ObservationId).ToList();
        var candidates = scenario.Candidates.Select(candidate => new IncidentBatchCandidate(
            candidate.CandidateToken,
            candidate.ProjectionEventId,
            candidate.CallIds.Select(ObservationId).ToList())).ToList();
        var prior = new IncidentBatchProjection(
            scenario.ReplayId,
            $"{scenario.ReplayId}:prior",
            createdAt,
            [],
            candidates.Select(candidate => new IncidentBatchProjectionEvent(
                candidate.ProjectionEventId,
                candidate.ObservationIds,
                candidate.CandidateToken,
                "Frozen replay candidate",
                false,
                true,
                [])).ToList(),
            []);

        var config = new EngineConfig
        {
            Storage = new StorageConfig
            {
                DatabasePath = Path.Combine(outputDirectory, "telemetry.db"),
                AudioRoot = Path.Combine(outputDirectory, "audio")
            }
        };
        config.AiInsights.OpenAiBaseUrl = endpoint;
        config.AiInsights.OpenAiModel = model;
        config.AiInsights.TimeoutMs = checked(timeoutSeconds * 1000);
        var telemetry = new EngineDatabase(config, NullLogger<EngineDatabase>.Instance);
        await telemetry.InitializeAsync(CancellationToken.None);
        var constructor = new OpenAiIncidentBatchProposer(config, telemetry, NullLogger.Instance, scenario.ReplayId);
        var relationship = new OpenAiIncidentBatchRelationshipProposer(config, telemetry, NullLogger.Instance, scenario.ReplayId);
        var verifier = new OpenAiIncidentBatchConfirmationVerifier(config, telemetry, NullLogger.Instance, scenario.ReplayId);
        var coordinator = new IncidentBatchCoordinator(constructor, relationship, verifier, telemetry);
        var configurationIdentity = string.Join(';',
            IncidentBatchPrompt.PromptIdentity,
            IncidentBatchRelationshipPrompt.PromptIdentity,
            IncidentBatchRelationshipContract.ConfigurationToken,
            IncidentBatchConfirmationContract.ConfigurationToken,
            IncidentBatchContract.PerEventAcceptanceConfigurationToken,
            IncidentBatchContract.PerCitationAcceptanceConfigurationToken,
            IncidentBatchContract.EvidenceSummaryProjectionConfigurationToken,
            IncidentBatchContract.CorroboratedVisibilityConfigurationToken,
            IncidentTranscriptCitationResolver.ConfigurationToken,
            IncidentBatchLiveSelection.ConfigurationToken,
            $"replay={scenario.ReplayId}");
        var singletons = scenario.NewCallIds.Select(callId => new IncidentBatchSingletonIdentity(
            ObservationId(callId),
            $"{scenario.ReplayId}:singleton:{callId}")).ToList();
        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                scenario.ReplayId,
                $"{scenario.ReplayId}:ledger:1",
                $"{scenario.ReplayId}:projection:1",
                singletons,
                "offline-replay",
                configurationIdentity),
            bundle,
            prior,
            newObservationIds,
            candidates,
            CancellationToken.None);

        var entry = result.LedgerEntry.Entry;
        var acceptedEvents = IncidentBatchContract.AcceptedEvents(entry);
        var acceptedRelationships = IncidentBatchRelationshipContract.AcceptedRelationships(entry);
        var acceptedConfirmedCandidates = acceptedRelationships
            .Where(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership)
            .Select(item => item.CandidateToken)
            .ToHashSet(StringComparer.Ordinal);
        var acceptedCandidates = acceptedRelationships.Select(item => item.CandidateToken).ToHashSet(StringComparer.Ordinal);
        var acceptedNewCallIds = acceptedEvents
            .SelectMany(item => item.NewObservationIds)
            .Select(ParseCallId)
            .ToHashSet();
        var checks = new ReplayChecks(
            scenario.Expectations.RequiredConfirmedCandidateTokens.All(acceptedConfirmedCandidates.Contains),
            scenario.Expectations.ForbiddenAcceptedCandidateTokens.All(candidate => !acceptedCandidates.Contains(candidate)),
            scenario.Expectations.RequiredCoveredNewCallIds.All(acceptedNewCallIds.Contains));
        var report = new ReplayReport(
            scenario,
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(databasePath))),
            configurationIdentity,
            model,
            endpoint,
            entry,
            acceptedEvents,
            acceptedRelationships,
            checks,
            checks.RequiredConfirmationsPresent && checks.ForbiddenCandidatesAbsent && checks.RequiredNewCallsCovered);
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(report, jsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            scenario.ReplayId,
            acceptedEvents = acceptedEvents.Count,
            acceptedRelationships = acceptedRelationships.Count,
            confirmed = acceptedRelationships.Count(item => item.Disposition == IncidentBatchRelationshipDisposition.ConfirmedMembership),
            provisional = acceptedRelationships.Count(item => item.Disposition == IncidentBatchRelationshipDisposition.ProvisionalAssociation),
            checks,
            report.Passed,
            resultPath
        }, jsonOptions));
        if (!report.Passed)
            Environment.ExitCode = 2;
    }

    private static void ValidateScenario(Scenario scenario)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario.ReplayId);
        if (scenario.WindowStartUnixSeconds < 0 || scenario.WindowEndUnixSeconds <= scenario.WindowStartUnixSeconds)
            throw new ArgumentException("scenario window is invalid");
        if (scenario.NewCallIds.Count is < 1 or > IncidentBatchContract.MaximumNewObservationCount)
            throw new ArgumentException("scenario new-call count is invalid");
        if (scenario.Candidates.Count is < 1 or > IncidentBatchContract.MaximumCandidateCount)
            throw new ArgumentException("scenario candidate count is invalid");
        if (scenario.NewCallIds.Concat(scenario.Candidates.SelectMany(item => item.CallIds)).GroupBy(id => id).Any(group => group.Count() > 1))
            throw new ArgumentException("scenario call ids must belong to exactly one source boundary");
        if (scenario.Candidates.Any(item => string.IsNullOrWhiteSpace(item.CandidateToken) || string.IsNullOrWhiteSpace(item.ProjectionEventId) || item.CallIds.Count == 0))
            throw new ArgumentException("scenario candidates require tokens, projection ids, and calls");
    }

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("scenario replay arguments must be --name value pairs");
            values[args[index]] = args[index + 1];
        }
        return values;
    }

    private static string ObservationId(long callId) => $"call:{callId}";
    private static long ParseCallId(string observationId) => long.Parse(observationId.AsSpan("call:".Length));

    private sealed record Scenario(
        string ReplayId,
        long WindowStartUnixSeconds,
        long WindowEndUnixSeconds,
        IReadOnlyList<long> NewCallIds,
        IReadOnlyList<ScenarioCandidate> Candidates,
        ScenarioExpectations Expectations);
    private sealed record ScenarioCandidate(string CandidateToken, string ProjectionEventId, IReadOnlyList<long> CallIds);
    private sealed record ScenarioExpectations(
        IReadOnlyList<string> RequiredConfirmedCandidateTokens,
        IReadOnlyList<string> ForbiddenAcceptedCandidateTokens,
        IReadOnlyList<long> RequiredCoveredNewCallIds);
    private sealed record ReplayChecks(bool RequiredConfirmationsPresent, bool ForbiddenCandidatesAbsent, bool RequiredNewCallsCovered);
    private sealed record ReplayReport(
        Scenario Scenario,
        string DatabaseSha256,
        string ConfigurationIdentity,
        string Model,
        string Endpoint,
        IncidentBatchLedgerEntry LedgerEntry,
        IReadOnlyList<IncidentBatchEventProposal> AcceptedEvents,
        IReadOnlyList<IncidentBatchRelationship> AcceptedRelationships,
        ReplayChecks Checks,
        bool Passed);
}
