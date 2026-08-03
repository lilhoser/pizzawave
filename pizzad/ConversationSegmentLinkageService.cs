namespace pizzad;

public sealed record ConversationSegmentParticipantRecord(
    long CallId,
    string SystemShortName,
    long SourceId,
    long FirstObservedAtMs,
    long LastObservedAtMs,
    int TransmissionCount);

public sealed record ConversationSegmentParticipantSummary(
    long CallId,
    string SystemShortName,
    int IdentifiedRadioCount,
    int IdentifiedTransmissionCount);

public sealed record ConversationSegmentLinkEvidence(
    long EarlierCallId,
    long LaterCallId,
    int SharedRadioCount,
    long GapMilliseconds,
    int MostFrequentSharedRadioSegmentCount,
    bool SameTalkgroup);

public sealed record ConversationSegmentLinkageContext(
    IReadOnlyDictionary<long, ConversationSegmentParticipantSummary> ParticipantsByCallId,
    IReadOnlyList<ConversationSegmentLinkEvidence> Links)
{
    public static readonly ConversationSegmentLinkageContext Empty = new(
        new Dictionary<long, ConversationSegmentParticipantSummary>(), []);
}

public sealed class ConversationSegmentLinkageService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;

    public ConversationSegmentLinkageService(EngineConfig config, EngineDatabase database)
    {
        _config = config;
        _database = database;
    }

    public async Task<ConversationSegmentLinkageContext> BuildContextAsync(IReadOnlyList<EngineCall> calls, CancellationToken ct)
    {
        if (calls.Count == 0) return ConversationSegmentLinkageContext.Empty;
        var start = calls.Min(call => call.StartTime);
        var end = calls.Max(call => Math.Max(call.StartTime, call.StopTime));
        var participants = await _database.ListConversationSegmentParticipantsAsync(start, end, ct);
        return BuildContext(calls, participants, _config.AiInsights.IncidentParticipantLinkWindowSeconds);
    }

    public static ConversationSegmentLinkageContext BuildContext(
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<ConversationSegmentParticipantRecord> participants,
        int maximumGapSeconds)
    {
        if (calls.Count == 0 || participants.Count == 0)
            return ConversationSegmentLinkageContext.Empty;

        var callsById = calls.DistinctBy(call => call.Id).ToDictionary(call => call.Id);
        var relevant = participants.Where(row => callsById.ContainsKey(row.CallId)).ToList();
        var summaries = relevant
            .GroupBy(row => row.CallId)
            .ToDictionary(
                group => group.Key,
                group => new ConversationSegmentParticipantSummary(
                    group.Key,
                    callsById[group.Key].SystemShortName,
                    group.Select(row => row.SourceId).Distinct().Count(),
                    group.Sum(row => row.TransmissionCount)));
        var maximumGapMilliseconds = Math.Max(0, maximumGapSeconds) * 1000L;
        var links = new Dictionary<(long Earlier, long Later), MutableLink>();

        // Keep observations from calls outside the eligible candidate set in the
        // ordering. Otherwise two eligible calls could appear adjacent merely
        // because an intervening call was filtered out before incident analysis.
        foreach (var sourceGroup in participants.GroupBy(row => (System: NormalizeSystem(row.SystemShortName), row.SourceId)))
        {
            var ordered = sourceGroup
                .DistinctBy(row => row.CallId)
                .OrderBy(row => row.FirstObservedAtMs)
                .ThenBy(row => row.CallId)
                .ToList();
            var sourceSegmentCount = ordered.Count;
            for (var index = 1; index < ordered.Count; index++)
            {
                var earlier = ordered[index - 1];
                var later = ordered[index];
                if (!callsById.TryGetValue(earlier.CallId, out var earlierCall) ||
                    !callsById.TryGetValue(later.CallId, out var laterCall))
                    continue;
                var gap = Math.Max(0, later.FirstObservedAtMs - earlier.LastObservedAtMs);
                if (gap > maximumGapMilliseconds) continue;

                var key = (earlier.CallId, later.CallId);
                if (!links.TryGetValue(key, out var link))
                {
                    link = new MutableLink(
                        earlier.CallId,
                        later.CallId,
                        gap,
                        string.Equals(earlierCall.SystemShortName, laterCall.SystemShortName, StringComparison.OrdinalIgnoreCase) &&
                        earlierCall.Talkgroup == laterCall.Talkgroup);
                    links[key] = link;
                }
                link.SharedSourceIds.Add(sourceGroup.Key.SourceId);
                link.GapMilliseconds = Math.Min(link.GapMilliseconds, gap);
                link.MostFrequentSharedRadioSegmentCount = Math.Max(link.MostFrequentSharedRadioSegmentCount, sourceSegmentCount);
            }
        }

        return new(
            summaries,
            links.Values
                .Select(link => new ConversationSegmentLinkEvidence(
                    link.EarlierCallId,
                    link.LaterCallId,
                    link.SharedSourceIds.Count,
                    link.GapMilliseconds,
                    link.MostFrequentSharedRadioSegmentCount,
                    link.SameTalkgroup))
                .OrderBy(link => callsById[link.EarlierCallId].StartTime)
                .ThenBy(link => link.EarlierCallId)
                .ThenBy(link => link.LaterCallId)
                .ToList());
    }

    private static string NormalizeSystem(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private sealed class MutableLink(long earlierCallId, long laterCallId, long gapMilliseconds, bool sameTalkgroup)
    {
        public long EarlierCallId { get; } = earlierCallId;
        public long LaterCallId { get; } = laterCallId;
        public HashSet<long> SharedSourceIds { get; } = [];
        public long GapMilliseconds { get; set; } = gapMilliseconds;
        public int MostFrequentSharedRadioSegmentCount { get; set; }
        public bool SameTalkgroup { get; } = sameTalkgroup;
    }
}
