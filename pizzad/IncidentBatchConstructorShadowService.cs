using System.Globalization;
using System.Reflection;

namespace pizzad;

public sealed class IncidentBatchConstructorShadowService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EmbeddingService _embeddings;
    private readonly ILogger<IncidentBatchConstructorShadowService> _logger;
    private long? _lastSampledCallId;

    public IncidentBatchConstructorShadowService(
        EngineConfig config,
        EngineDatabase database,
        EmbeddingService embeddings,
        ILogger<IncidentBatchConstructorShadowService> logger)
    {
        _config = config;
        _database = database;
        _embeddings = embeddings;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!IsEnabled())
            {
                await DelayAsync(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Incident batch constructor shadow failed; production incident state was not changed");
            }
            await DelayAsync(TimeSpan.FromSeconds(_config.AiInsights.IncidentBatchConstructorShadowIntervalSeconds), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var runId = _config.AiInsights.IncidentBatchConstructorShadowRunId.Trim();
        var now = DateTimeOffset.UtcNow;
        var start = now.AddMinutes(-_config.AiInsights.IncidentBatchConstructorShadowLookbackMinutes).ToUnixTimeSeconds();
        var calls = (await _database.ListCallsAsync(start, now.ToUnixTimeSeconds(), null, ct))
            .Where(IncidentAssociationLiveSelection.IsEligibleSourceObservation)
            .OrderBy(call => call.Id)
            .ToList();
        if (_lastSampledCallId is null)
        {
            var latest = await _database.GetLatestIncidentBatchLedgerEntryAsync(runId, ct);
            _lastSampledCallId = latest?.Entry.Bundle.Observations
                .Where(item => latest.Entry.NewObservationIds.Contains(item.ObservationId, StringComparer.Ordinal))
                .Max(item => item.CallId) ?? calls.LastOrDefault()?.Id ?? 0;
            _logger.LogInformation(
                "Incident batch constructor shadow run {RunId} initialized after call {CallId}; historical calls will not be backfilled",
                runId,
                _lastSampledCallId);
            return;
        }

        var queueHealth = await _database.GetIncidentAnalysisQueueHealthAsync(_config.AiInsights.IncidentAnalysisMaximumAgeMinutes, ct);
        if (!string.Equals(queueHealth.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Incident batch constructor shadow paused because production incident analysis is not current: {Reason}", queueHealth.Message);
            return;
        }

        var batchSize = _config.AiInsights.IncidentBatchConstructorShadowBatchSize;
        var newCalls = calls
            .Where(call => call.Id > _lastSampledCallId.Value)
            .OrderByDescending(call => call.Id)
            .Take(batchSize)
            .OrderBy(call => call.Id)
            .ToList();
        if (newCalls.Count == 0)
            return;
        var priorStored = await _database.GetLatestIncidentBatchProjectionAsync(runId, ct);
        var prior = priorStored?.Projection;
        var matches = new List<VectorSearchMatchDto>();
        if (prior is not null)
        {
            foreach (var call in newCalls)
            {
                matches.AddRange(await _embeddings.SearchSimilarAsync(
                    call.Transcription,
                    call.SystemShortName,
                    start,
                    now.ToUnixTimeSeconds(),
                    12,
                    ct));
            }
        }
        var selection = IncidentBatchLiveSelection.Build(
            newCalls,
            calls,
            matches,
            prior,
            _config.AiInsights.IncidentBatchConstructorShadowCandidateLimit,
            now);
        var batchIdentity = $"{newCalls.First().Id.ToString(CultureInfo.InvariantCulture)}-{newCalls.Last().Id.ToString(CultureInfo.InvariantCulture)}";
        var singletons = newCalls.Select(call => new IncidentBatchSingletonIdentity(
            $"call:{call.Id.ToString(CultureInfo.InvariantCulture)}",
            $"batch-live:{runId}:event:call:{call.Id.ToString(CultureInfo.InvariantCulture)}")).ToList();
        var proposer = new OpenAiIncidentBatchProposer(_config, _database, _logger, runId);
        var coordinator = new IncidentBatchCoordinator(proposer, _database);
        var result = await coordinator.RunAsync(
            new IncidentBatchRunRequest(
                runId,
                $"batch-live:{runId}:ledger:{batchIdentity}",
                $"batch-live:{runId}:projection:{batchIdentity}",
                singletons,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                ConfigurationIdentity()),
            selection.Bundle,
            prior,
            selection.NewObservationIds,
            selection.Candidates,
            ct);
        _lastSampledCallId = newCalls.Max(call => call.Id);
        var validEvents = IncidentBatchContract.AcceptedEvents(result.LedgerEntry.Entry);
        _logger.LogInformation(
            "Incident batch constructor shadow run {RunId} processed {CallCount} calls through {LastCallId}: new={NewCount}, confirmed={ConfirmedCount}, provisional={ProvisionalCount}, unresolved={UnresolvedCount}, candidates={CandidateCount}, proposerMs={DurationMs}, invalid={Invalid}, proposerError={HasError}; production incident state unchanged",
            runId,
            newCalls.Count,
            _lastSampledCallId,
            validEvents.Count(item => item.Disposition == IncidentBatchEventDisposition.NewEvent),
            validEvents.Count(item => item.Disposition == IncidentBatchEventDisposition.ConfirmedMembership),
            validEvents.Count(item => item.Disposition == IncidentBatchEventDisposition.ProvisionalAssociation),
            newCalls.Count - validEvents.SelectMany(item => item.NewObservationIds).Distinct(StringComparer.Ordinal).Count(),
            selection.Candidates.Count,
            result.LedgerEntry.Entry.Execution.ProposerDurationMilliseconds,
            result.LedgerEntry.Entry.ProposalValidationErrors.Count > 0,
            !string.IsNullOrWhiteSpace(result.LedgerEntry.Entry.Execution.ProposerError));
    }

    private bool IsEnabled() =>
        _config.Setup.Completed
        && _config.AiInsights.Enabled
        && _config.AiInsights.IncidentBatchConstructorShadowEnabled
        && !string.IsNullOrWhiteSpace(_config.AiInsights.IncidentBatchConstructorShadowRunId)
        && !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiBaseUrl)
        && !string.IsNullOrWhiteSpace(_config.AiInsights.OpenAiModel);

    private string ConfigurationIdentity() =>
        $"{IncidentBatchPrompt.PromptIdentity};{IncidentBatchContract.PerEventAcceptanceConfigurationToken};run={_config.AiInsights.IncidentBatchConstructorShadowRunId.Trim()};interval={_config.AiInsights.IncidentBatchConstructorShadowIntervalSeconds};lookback={_config.AiInsights.IncidentBatchConstructorShadowLookbackMinutes};batch={_config.AiInsights.IncidentBatchConstructorShadowBatchSize};candidates={_config.AiInsights.IncidentBatchConstructorShadowCandidateLimit}";

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

public sealed record IncidentBatchLiveSelection(
    IncidentEventStateObservationBundle Bundle,
    IReadOnlyList<string> NewObservationIds,
    IReadOnlyList<IncidentBatchCandidate> Candidates)
{
    public static IncidentBatchLiveSelection Build(
        IReadOnlyList<EngineCall> newCalls,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyList<VectorSearchMatchDto> matches,
        IncidentBatchProjection? priorProjection,
        int candidateLimit,
        DateTimeOffset createdAtUtc)
    {
        var newIds = newCalls.Select(call => ObservationId(call.Id)).ToHashSet(StringComparer.Ordinal);
        var callsByObservation = recentCalls.ToDictionary(call => ObservationId(call.Id), StringComparer.Ordinal);
        var scores = matches
            .Where(match => !newIds.Contains(ObservationId(match.CallId)))
            .GroupBy(match => ObservationId(match.CallId), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Score), StringComparer.Ordinal);
        var groups = (priorProjection?.Events ?? [])
            .Select(projectedEvent => new
            {
                Event = projectedEvent,
                SourceCalls = projectedEvent.ObservationIds
                    .Where(callsByObservation.ContainsKey)
                    .Select(observationId => new { ObservationId = observationId, Call = callsByObservation[observationId], Score = scores.GetValueOrDefault(observationId, double.NegativeInfinity) })
                    .ToList()
            })
            .Where(item => item.SourceCalls.Count > 0 && (item.Event.OperatorVisible || item.SourceCalls.Any(source => double.IsFinite(source.Score))))
            .OrderByDescending(item => item.Event.OperatorVisible)
            .ThenByDescending(item => item.SourceCalls.Any(source => double.IsFinite(source.Score)))
            .ThenByDescending(item => item.SourceCalls.Max(source => source.Score))
            .ThenByDescending(item => item.SourceCalls.Max(source => source.Call.StartTime))
            .ThenBy(item => item.Event.ProjectionEventId, StringComparer.Ordinal)
            .Take(Math.Clamp(candidateLimit, 1, IncidentBatchContract.MaximumCandidateCount))
            .ToList();
        var sourceCalls = newCalls.ToList();
        var candidates = new List<IncidentBatchCandidate>();
        for (var index = 0; index < groups.Count; index++)
        {
            var selected = groups[index].SourceCalls
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Call.StartTime)
                .Take(3)
                .ToList();
            sourceCalls.AddRange(selected.Select(item => item.Call));
            candidates.Add(new IncidentBatchCandidate($"candidate-{index + 1}", groups[index].Event.ProjectionEventId, selected.Select(item => item.ObservationId).ToList()));
        }
        var raw = IncidentEventStateCorpusExporter.BuildObservationBundle(
            $"batch-live:bundle:{newCalls.First().Id.ToString(CultureInfo.InvariantCulture)}-{newCalls.Last().Id.ToString(CultureInfo.InvariantCulture)}",
            createdAtUtc,
            sourceCalls.DistinctBy(call => call.Id));
        var bundle = raw with
        {
            Observations = raw.Observations.Select(observation => observation with
            {
                AudioReference = string.Empty,
                Metadata = new Dictionary<string, IncidentEventStateMetadataObservation>(StringComparer.Ordinal)
            }).ToList()
        };
        return new IncidentBatchLiveSelection(bundle, newCalls.Select(call => ObservationId(call.Id)).ToList(), candidates);
    }

    private static string ObservationId(long callId) => $"call:{callId.ToString(CultureInfo.InvariantCulture)}";
}
