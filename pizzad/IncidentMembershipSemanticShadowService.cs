using System.Text.Json;
using System.Threading.Channels;

namespace pizzad;

public sealed record IncidentMembershipSemanticShadowPackage(
    string SystemShortName,
    IReadOnlyList<EngineCall> BaselineCalls,
    IReadOnlyList<EngineCall> ParticipantCalls,
    IReadOnlyList<long> AddedCallIds,
    ParticipantLinkCandidateShadowComparison CandidateComparison);

public sealed record IncidentMembershipSemanticShadowComparison(
    IReadOnlyList<long> AddedMemberCallIds,
    IReadOnlyList<long> AddedUnresolvedCallIds,
    IReadOnlyList<long> AddedNonIncidentCallIds,
    IReadOnlyList<long> SharedCallsWhoseDispositionChanged);

public sealed record IncidentMembershipSemanticShadowLog(
    string RunId,
    string SystemShortName,
    long RecordedAtUnix,
    bool ProductionParticipantCandidateUse,
    bool ShadowResultPersisted,
    IReadOnlyList<long> BaselineCallIds,
    IReadOnlyList<long> ParticipantCallIds,
    IReadOnlyList<long> AddedCallIds,
    IncidentMembershipAdapterResult Baseline,
    IncidentMembershipAdapterResult Participant,
    IncidentMembershipSemanticShadowComparison Comparison,
    ParticipantLinkCandidateShadowComparison CandidateComparison);

public static class IncidentMembershipSemanticShadowPackageBuilder
{
    public static IncidentMembershipSemanticShadowPackage? Build(
        string systemShortName,
        IReadOnlySet<long> newCallIds,
        IReadOnlyList<IncidentRagCandidate> baseline,
        IReadOnlyList<IncidentRagCandidate> participant,
        ParticipantLinkCandidateShadowComparison comparison,
        int baselineLimit,
        int addedLimit)
    {
        ArgumentNullException.ThrowIfNull(newCallIds);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(comparison);

        var addedIds = comparison.AddedCandidates.Select(item => item.CallId).ToHashSet();
        var added = participant
            .Where(candidate => addedIds.Contains(candidate.Call.Id))
            .Where(HasUsableEvidence)
            .OrderBy(candidate => DistanceToNearestNewCall(candidate.Call, participant, newCallIds))
            .ThenBy(candidate => candidate.Call.StartTime)
            .Take(Math.Clamp(addedLimit, 1, IncidentMembershipOutputLimits.MaximumSources - 1))
            .Select(candidate => candidate.Call)
            .ToList();
        if (added.Count == 0)
            return null;

        var selectedBaseline = baseline
            .Where(HasUsableEvidence)
            .OrderByDescending(candidate => newCallIds.Contains(candidate.Call.Id))
            .ThenBy(candidate => added.Min(item => TimeDistance(candidate.Call, item)))
            .ThenBy(candidate => candidate.Call.StartTime)
            .Take(Math.Clamp(baselineLimit, 1, IncidentMembershipOutputLimits.MaximumSources - added.Count))
            .Select(candidate => candidate.Call)
            .OrderBy(call => call.StartTime)
            .ThenBy(call => call.Id)
            .ToList();
        if (selectedBaseline.Count == 0)
            return null;

        var participantCalls = selectedBaseline
            .Concat(added)
            .DistinctBy(call => call.Id)
            .OrderBy(call => call.StartTime)
            .ThenBy(call => call.Id)
            .ToList();
        if (participantCalls.Count == selectedBaseline.Count)
            return null;

        return new IncidentMembershipSemanticShadowPackage(
            systemShortName,
            selectedBaseline,
            participantCalls,
            added.Select(call => call.Id).ToList(),
            comparison);
    }

    private static bool HasUsableEvidence(IncidentRagCandidate candidate) =>
        !string.IsNullOrWhiteSpace(candidate.Call.Transcription);

    private static long DistanceToNearestNewCall(
        EngineCall call,
        IReadOnlyList<IncidentRagCandidate> candidates,
        IReadOnlySet<long> newCallIds)
    {
        var newCalls = candidates.Where(candidate => newCallIds.Contains(candidate.Call.Id)).Select(candidate => candidate.Call).ToList();
        return newCalls.Count == 0 ? long.MaxValue : newCalls.Min(item => TimeDistance(call, item));
    }

    private static long TimeDistance(EngineCall first, EngineCall second) =>
        Math.Abs(first.StartTime - second.StartTime);
}

public static class IncidentMembershipSemanticShadowComparer
{
    public static IncidentMembershipSemanticShadowComparison Compare(
        IncidentMembershipContractResult baseline,
        IncidentMembershipContractResult participant,
        IReadOnlyList<long> addedCallIds)
    {
        var added = addedCallIds.ToHashSet();
        var baselineCallIds = AllCallIds(baseline).ToHashSet();
        var participantDisposition = Dispositions(participant);
        var baselineSharedDisposition = Dispositions(baseline, baselineCallIds);
        var participantSharedDisposition = Dispositions(participant, baselineCallIds);
        return new IncidentMembershipSemanticShadowComparison(
            added.Where(callId => participantDisposition.TryGetValue(callId, out var value) && value.StartsWith("event:", StringComparison.Ordinal)).Order().ToList(),
            added.Where(callId => participantDisposition.GetValueOrDefault(callId) == "unresolved").Order().ToList(),
            added.Where(callId => participantDisposition.GetValueOrDefault(callId) == "non_incident").Order().ToList(),
            baselineCallIds
                .Where(callId => participantSharedDisposition.TryGetValue(callId, out var value) &&
                                 !string.Equals(value, baselineSharedDisposition[callId], StringComparison.Ordinal))
                .Order()
                .ToList());
    }

    private static Dictionary<long, string> Dispositions(
        IncidentMembershipContractResult result,
        IReadOnlySet<long>? projection = null)
    {
        var map = new Dictionary<long, string>();
        foreach (var hypothesis in result.Hypotheses)
        {
            var projectedSources = hypothesis.Sources
                .Where(source => projection is null || projection.Contains(source.CallId))
                .ToList();
            var signature = "event:" + string.Join(',', projectedSources.Select(source => source.CallId).Order());
            foreach (var source in projectedSources)
                map[source.CallId] = signature;
        }
        foreach (var source in result.UnresolvedSources.Where(source => projection is null || projection.Contains(source.CallId)))
            map[source.CallId] = "unresolved";
        foreach (var source in result.NonIncidentSources.Where(source => projection is null || projection.Contains(source.CallId)))
            map[source.CallId] = "non_incident";
        return map;
    }

    private static IEnumerable<long> AllCallIds(IncidentMembershipContractResult result) =>
        result.Hypotheses.SelectMany(hypothesis => hypothesis.Sources).Select(source => source.CallId)
            .Concat(result.UnresolvedSources.Select(source => source.CallId))
            .Concat(result.NonIncidentSources.Select(source => source.CallId));
}

public sealed class IncidentMembershipSemanticShadowService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly ILogger<IncidentMembershipSemanticShadowService> _logger;
    private readonly Channel<IncidentMembershipSemanticShadowPackage> _queue = Channel.CreateBounded<IncidentMembershipSemanticShadowPackage>(
        new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public IncidentMembershipSemanticShadowService(
        EngineConfig config,
        ILogger<IncidentMembershipSemanticShadowService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public bool TryEnqueue(
        string systemShortName,
        IReadOnlySet<long> newCallIds,
        IReadOnlyList<IncidentRagCandidate> baseline,
        IReadOnlyList<IncidentRagCandidate> participant,
        ParticipantLinkCandidateShadowComparison comparison)
    {
        if (!IsActive())
            return false;
        var package = IncidentMembershipSemanticShadowPackageBuilder.Build(
            systemShortName,
            newCallIds,
            baseline,
            participant,
            comparison,
            _config.AiInsights.IncidentMembershipSemanticShadowBaselineSourceLimit,
            _config.AiInsights.IncidentMembershipSemanticShadowAddedSourceLimit);
        if (package is null)
            return false;
        if (_queue.Writer.TryWrite(package))
            return true;
        _logger.LogWarning(
            "Incident membership semantic shadow queue is full for run {RunId}; skipped one non-production package",
            _config.AiInsights.IncidentMembershipSemanticShadowRunId);
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var package in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!IsActive())
                continue;
            try
            {
                await ProcessAsync(package, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Incident membership semantic shadow failed for run {RunId}; production processing was not affected",
                    _config.AiInsights.IncidentMembershipSemanticShadowRunId);
            }
        }
    }

    private async Task ProcessAsync(IncidentMembershipSemanticShadowPackage package, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var decider = new OpenAiIncidentMembershipCellDecider(
            client,
            ShadowBaseUrl(),
            ShadowApiKey(),
            _config.AiInsights.IncidentMembershipSemanticShadowModel);
        var adapter = new IncidentMembershipConstrainedAdapter(decider, IncidentMembershipOutputLimits.MaximumHypotheses);
        var baseline = await adapter.GenerateAsync(CreateSession("baseline", package.BaselineCalls), ct);
        var participant = await adapter.GenerateAsync(CreateSession("participant", package.ParticipantCalls), ct);
        var comparison = IncidentMembershipSemanticShadowComparer.Compare(
            baseline.Membership,
            participant.Membership,
            package.AddedCallIds);
        var result = new IncidentMembershipSemanticShadowLog(
            _config.AiInsights.IncidentMembershipSemanticShadowRunId,
            package.SystemShortName,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            _config.AiInsights.IncidentParticipantLinkCandidateEnabled,
            false,
            package.BaselineCalls.Select(call => call.Id).ToList(),
            package.ParticipantCalls.Select(call => call.Id).ToList(),
            package.AddedCallIds,
            baseline,
            participant,
            comparison,
            package.CandidateComparison);
        _logger.LogInformation(
            "Incident membership semantic shadow for {System}: {ShadowJson}",
            package.SystemShortName,
            JsonSerializer.Serialize(result, EngineConfig.JsonOptions()));
    }

    private IncidentMembershipContractSession CreateSession(string variant, IReadOnlyList<EngineCall> calls) =>
        new(calls.Select(call => (
            new IncidentMembershipSourceIdentity(call.Id, $"{variant}:call:{call.Id}"),
            new IncidentMembershipModelEvidence(
                DateTimeOffset.FromUnixTimeSeconds(call.StartTime),
                call.Transcription,
                call.SystemShortName,
                call.TalkgroupName,
                call.StopTime >= call.StartTime ? TimeSpan.FromSeconds(call.StopTime - call.StartTime) : null))));

    private bool IsActive()
    {
        var settings = _config.AiInsights;
        return settings.IncidentMembershipSemanticShadowEnabled &&
               !string.IsNullOrWhiteSpace(settings.IncidentMembershipSemanticShadowRunId) &&
               !string.IsNullOrWhiteSpace(settings.IncidentMembershipSemanticShadowModel) &&
               !string.IsNullOrWhiteSpace(ShadowBaseUrl()) &&
               settings.IncidentMembershipSemanticShadowEndUnix > 0 &&
               DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= settings.IncidentMembershipSemanticShadowEndUnix;
    }

    private string ShadowBaseUrl() => string.IsNullOrWhiteSpace(_config.AiInsights.IncidentMembershipSemanticShadowBaseUrl)
        ? _config.AiInsights.OpenAiBaseUrl
        : _config.AiInsights.IncidentMembershipSemanticShadowBaseUrl;

    private string ShadowApiKey() => string.IsNullOrWhiteSpace(_config.AiInsights.IncidentMembershipSemanticShadowApiKey)
        ? _config.AiInsights.OpenAiApiKey
        : _config.AiInsights.IncidentMembershipSemanticShadowApiKey;
}
