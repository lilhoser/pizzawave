using System.Text.Json;
using System.Threading.Channels;

namespace pizzad;

public sealed record IncidentTargetMembershipShadowPackage(
    string SystemShortName,
    long IncidentId,
    IReadOnlyList<EngineCall> EstablishedCalls,
    EngineCall DirectlyLinkedCall,
    EngineCall Candidate,
    ConversationSegmentLinkEvidence SourceLink)
{
    public string Key => $"{Candidate.Id}:{IncidentId}";
}

public sealed record IncidentTargetMembershipShadowLog(
    string RunId,
    long RecordedAtUnix,
    string SystemShortName,
    long IncidentId,
    IReadOnlyList<long> EstablishedCallIds,
    long DirectlyLinkedCallId,
    long CandidateCallId,
    long GapMilliseconds,
    int SharedRadioCount,
    int RadioSegmentCount,
    bool ProductionParticipantCandidateUse,
    bool ResultPersisted,
    IncidentAnalysisQueueHealthDto ProductionWorkAtStart,
    EvidencePurityResult ExistingIncident,
    EvidencePurityResult CandidateConversationSegment,
    IncidentMembershipPurityGateResult Screening,
    IncidentTargetMembershipResult? Membership);

public static class IncidentTargetMembershipShadowPackageBuilder
{
    public static IReadOnlyList<IncidentTargetMembershipShadowPackage> Build(
        string systemShortName,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentRagCandidate> participantCandidates,
        IReadOnlyList<ConversationSegmentLinkEvidence> eligibleLinks,
        ParticipantLinkCandidateShadowComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(recentCalls);
        ArgumentNullException.ThrowIfNull(activeIncidents);
        ArgumentNullException.ThrowIfNull(participantCandidates);
        ArgumentNullException.ThrowIfNull(eligibleLinks);
        ArgumentNullException.ThrowIfNull(comparison);

        var addedIds = comparison.AddedCandidates.Select(item => item.CallId).ToHashSet();
        if (addedIds.Count == 0)
            return [];

        var recentById = recentCalls.DistinctBy(call => call.Id).ToDictionary(call => call.Id);
        var participantById = participantCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Call.Transcription))
            .DistinctBy(candidate => candidate.Call.Id)
            .ToDictionary(candidate => candidate.Call.Id, candidate => candidate.Call);
        var packages = new Dictionary<string, IncidentTargetMembershipShadowPackage>(StringComparer.Ordinal);

        foreach (var link in eligibleLinks
                     .Where(link => addedIds.Contains(link.LaterCallId))
                     .OrderBy(link => link.GapMilliseconds)
                     .ThenBy(link => link.LaterCallId))
        {
            if (!participantById.TryGetValue(link.LaterCallId, out var candidate) ||
                !recentById.TryGetValue(link.EarlierCallId, out var directlyLinkedCall) ||
                !HasUsableTranscript(candidate) ||
                !HasUsableTranscript(directlyLinkedCall) ||
                !link.SameTalkgroup ||
                link.GapMilliseconds is < 0 or > ParticipantLinkCandidateShadowComparer.MaximumEligibleGapMilliseconds ||
                !string.Equals(candidate.SystemShortName, directlyLinkedCall.SystemShortName, StringComparison.OrdinalIgnoreCase) ||
                candidate.Talkgroup != directlyLinkedCall.Talkgroup ||
                candidate.StartTime < directlyLinkedCall.StartTime)
            {
                continue;
            }

            foreach (var incident in activeIncidents
                         .Where(incident => incident.MergedIntoIncidentId == 0)
                         .Where(incident => incident.Calls.Any(call => call.CallId == directlyLinkedCall.Id))
                         .Where(incident => incident.Calls.All(call => call.CallId != candidate.Id))
                         .Where(incident => incident.Calls.Count is >= 1 and <= IncidentTargetMembershipContext.MaximumEstablishedCalls))
            {
                var established = new List<EngineCall>(incident.Calls.Count);
                foreach (var member in incident.Calls.OrderBy(call => call.RawTimestamp).ThenBy(call => call.CallId))
                {
                    if (!recentById.TryGetValue(member.CallId, out var call) || !HasUsableTranscript(call))
                    {
                        established.Clear();
                        break;
                    }
                    established.Add(call);
                }
                if (established.Count != incident.Calls.Count || established.All(call => call.Id != directlyLinkedCall.Id))
                    continue;

                var package = new IncidentTargetMembershipShadowPackage(
                    systemShortName,
                    incident.Id,
                    established,
                    directlyLinkedCall,
                    candidate,
                    link);
                packages.TryAdd(package.Key, package);
            }
        }

        return packages.Values
            .OrderBy(package => package.Candidate.StartTime)
            .ThenBy(package => package.Candidate.Id)
            .ThenBy(package => package.IncidentId)
            .ToList();
    }

    private static bool HasUsableTranscript(EngineCall call) =>
        string.Equals(call.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(call.QualityReason, "ok", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(call.Transcription);
}

public static class IncidentTargetMembershipShadowWorkPolicy
{
    public static bool CanRun(
        IncidentAnalysisQueueHealthDto health,
        int maximumPendingCalls,
        int maximumCompletedAgeMinutes,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (!string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"incident analysis status is {health.Status}";
            return false;
        }
        if (health.PendingCalls > Math.Max(1, maximumPendingCalls))
        {
            reason = $"pending incident calls {health.PendingCalls} exceed limit {Math.Max(1, maximumPendingCalls)}";
            return false;
        }
        if (health.LatestCompletedAgeMinutes > Math.Max(1, maximumCompletedAgeMinutes))
        {
            reason = $"latest completed incident call is {health.LatestCompletedAgeMinutes:0.0} minutes old";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

public sealed class IncidentTargetMembershipShadowService : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly ILogger<IncidentTargetMembershipShadowService> _logger;
    private readonly Channel<IncidentTargetMembershipShadowPackage> _queue = Channel.CreateBounded<IncidentTargetMembershipShadowPackage>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly object _stateGate = new();
    private readonly HashSet<string> _acceptedKeys = new(StringComparer.Ordinal);
    private DateTimeOffset _nextEligibleAt = DateTimeOffset.MinValue;
    private int _acceptedCount;

    public IncidentTargetMembershipShadowService(
        EngineConfig config,
        EngineDatabase database,
        ILogger<IncidentTargetMembershipShadowService> logger)
    {
        _config = config;
        _database = database;
        _logger = logger;
    }

    public async Task<bool> TryEnqueueAsync(
        string systemShortName,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentRagCandidate> participantCandidates,
        IReadOnlyList<ConversationSegmentLinkEvidence> eligibleLinks,
        ParticipantLinkCandidateShadowComparison comparison,
        CancellationToken ct)
    {
        if (!IsActive())
            return false;

        var packages = IncidentTargetMembershipShadowPackageBuilder.Build(
            systemShortName,
            recentCalls,
            activeIncidents,
            participantCandidates,
            eligibleLinks,
            comparison);
        if (packages.Count == 0)
            return false;

        var now = DateTimeOffset.UtcNow;
        IncidentTargetMembershipShadowPackage? package;
        lock (_stateGate)
        {
            if (_acceptedCount >= _config.AiInsights.IncidentTargetMembershipShadowMaximumPackages || now < _nextEligibleAt)
                return false;
            package = packages.FirstOrDefault(item => !_acceptedKeys.Contains(item.Key));
        }
        if (package is null)
            return false;

        var health = await _database.GetIncidentAnalysisQueueHealthAsync(
            _config.AiInsights.IncidentAnalysisMaximumAgeMinutes,
            ct);
        if (!CanRun(health, out var reason))
        {
            _logger.LogInformation(
                "Incident target membership observation paused for run {RunId}: {Reason}",
                _config.AiInsights.IncidentTargetMembershipShadowRunId,
                reason);
            return false;
        }

        lock (_stateGate)
        {
            now = DateTimeOffset.UtcNow;
            if (_acceptedCount >= _config.AiInsights.IncidentTargetMembershipShadowMaximumPackages ||
                now < _nextEligibleAt ||
                !_acceptedKeys.Add(package.Key))
            {
                return false;
            }
            if (!_queue.Writer.TryWrite(package))
            {
                _acceptedKeys.Remove(package.Key);
                return false;
            }
            _acceptedCount++;
            _nextEligibleAt = now.AddSeconds(_config.AiInsights.IncidentTargetMembershipShadowMinimumIntervalSeconds);
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var package in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!IsActive())
                continue;
            try
            {
                var delay = TimeSpan.FromSeconds(_config.AiInsights.IncidentTargetMembershipShadowDelaySeconds);
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, stoppingToken);

                var health = await _database.GetIncidentAnalysisQueueHealthAsync(
                    _config.AiInsights.IncidentAnalysisMaximumAgeMinutes,
                    stoppingToken);
                if (!CanRun(health, out var reason))
                {
                    _logger.LogInformation(
                        "Incident target membership observation skipped for run {RunId}, candidate {CandidateCallId}: {Reason}",
                        _config.AiInsights.IncidentTargetMembershipShadowRunId,
                        package.Candidate.Id,
                        reason);
                    continue;
                }
                await ProcessAsync(package, health, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Incident target membership observation failed for run {RunId}, candidate {CandidateCallId}; production processing was not affected",
                    _config.AiInsights.IncidentTargetMembershipShadowRunId,
                    package.Candidate.Id);
            }
        }
    }

    private async Task ProcessAsync(
        IncidentTargetMembershipShadowPackage package,
        IncidentAnalysisQueueHealthDto health,
        CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var purityAdapter = new EvidencePurityAdapter(new OpenAiEvidencePurityDecider(
            client,
            _config.AiInsights.OpenAiBaseUrl,
            _config.AiInsights.OpenAiApiKey,
            _config.AiInsights.OpenAiModel));
        var incident = await purityAdapter.DecideAsync(new EvidencePurityContext(
            new EvidencePurityOwnerIdentity("incident", package.IncidentId, $"shadow:incident:{package.IncidentId}"),
            EvidencePurityScope.ExistingIncident,
            package.EstablishedCalls.Select(call => ToSource("incident", call))), ct);
        var candidate = await purityAdapter.DecideAsync(new EvidencePurityContext(
            new EvidencePurityOwnerIdentity("candidate", package.Candidate.Id, $"shadow:candidate:{package.Candidate.Id}"),
            EvidencePurityScope.CandidateConversationSegment,
            [ToSource("candidate", package.Candidate)]), ct);
        var screening = IncidentMembershipPurityGate.Evaluate(incident, candidate);
        IncidentTargetMembershipResult? membership = null;
        if (screening.MayEvaluateMembership)
        {
            var targetContext = new IncidentTargetMembershipContext(
                new IncidentTargetIdentity(package.IncidentId, $"shadow:incident:{package.IncidentId}"),
                package.EstablishedCalls.Select(call => ToSource("incident", call)),
                ToSource("incident", package.DirectlyLinkedCall).Identity,
                ToSource("candidate", package.Candidate));
            membership = await new IncidentTargetMembershipAdapter(new OpenAiIncidentTargetMembershipDecider(
                    client,
                    _config.AiInsights.OpenAiBaseUrl,
                    _config.AiInsights.OpenAiApiKey,
                    _config.AiInsights.OpenAiModel))
                .DecideAsync(targetContext, ct);
        }

        var result = new IncidentTargetMembershipShadowLog(
            _config.AiInsights.IncidentTargetMembershipShadowRunId,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            package.SystemShortName,
            package.IncidentId,
            package.EstablishedCalls.Select(call => call.Id).ToList(),
            package.DirectlyLinkedCall.Id,
            package.Candidate.Id,
            package.SourceLink.GapMilliseconds,
            package.SourceLink.SharedRadioCount,
            package.SourceLink.MostFrequentSharedRadioSegmentCount,
            _config.AiInsights.IncidentParticipantLinkCandidateEnabled,
            false,
            health,
            incident,
            candidate,
            screening,
            membership);
        _logger.LogInformation(
            "Incident target membership observation for {System}: {ObservationJson}",
            package.SystemShortName,
            JsonSerializer.Serialize(result, EngineConfig.JsonOptions()));
    }

    private static (IncidentMembershipSourceIdentity Identity, IncidentMembershipModelEvidence Evidence) ToSource(
        string owner,
        EngineCall call) =>
        (new IncidentMembershipSourceIdentity(call.Id, $"shadow:{owner}:call:{call.Id}"),
            new IncidentMembershipModelEvidence(
                DateTimeOffset.FromUnixTimeSeconds(call.StartTime),
                call.Transcription,
                call.SystemShortName,
                call.TalkgroupName,
                call.StopTime >= call.StartTime ? TimeSpan.FromSeconds(call.StopTime - call.StartTime) : null));

    private bool CanRun(IncidentAnalysisQueueHealthDto health, out string reason) =>
        IncidentTargetMembershipShadowWorkPolicy.CanRun(
            health,
            _config.AiInsights.IncidentTargetMembershipShadowMaximumPendingCalls,
            _config.AiInsights.IncidentTargetMembershipShadowMaximumCompletedAgeMinutes,
            out reason);

    private bool IsActive()
    {
        var settings = _config.AiInsights;
        return settings.IncidentTargetMembershipShadowEnabled &&
               !string.IsNullOrWhiteSpace(settings.IncidentTargetMembershipShadowRunId) &&
               settings.IncidentTargetMembershipShadowEndUnix > 0 &&
               DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= settings.IncidentTargetMembershipShadowEndUnix &&
               !string.IsNullOrWhiteSpace(settings.OpenAiBaseUrl) &&
               !string.IsNullOrWhiteSpace(settings.OpenAiModel);
    }
}
