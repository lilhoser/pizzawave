namespace pizzad;

public static class ParticipantLinkCandidateShadowGroups
{
    public const string SameTalkgroupUpTo60Seconds = "same_talkgroup_up_to_60_seconds";
    public const string SameTalkgroup61To120Seconds = "same_talkgroup_61_to_120_seconds";
    public const string SameTalkgroupOver120Seconds = "same_talkgroup_over_120_seconds";
    public const string DifferentTalkgroup = "different_talkgroup";
}

public sealed record ParticipantLinkCandidateShadowItem(
    long CallId,
    string Group,
    long GapMilliseconds,
    int SharedRadioCount,
    int MostFrequentSharedRadioSegmentCount,
    bool SameTalkgroup,
    bool InvolvesFrequentlyObservedRadio);

public sealed record ParticipantLinkCandidateShadowComparison(
    int BaselineCandidateCount,
    int ParticipantCandidateCount,
    IReadOnlyList<ParticipantLinkCandidateShadowItem> AddedCandidates,
    IReadOnlyList<ParticipantLinkCandidateShadowItem> ExistingCandidatesWithRadioEvidence,
    IReadOnlyList<long> DisplacedBaselineCallIds)
{
    public bool HasDifference =>
        AddedCandidates.Count > 0 ||
        ExistingCandidatesWithRadioEvidence.Count > 0 ||
        DisplacedBaselineCallIds.Count > 0;
}

public static class ParticipantLinkCandidateShadowComparer
{
    public const int FrequentlyObservedRadioCallThreshold = 10;
    public const long MaximumEligibleGapMilliseconds = 60_000;

    public static IReadOnlyList<ConversationSegmentLinkEvidence> SelectEligibleLinks(
        IReadOnlyList<ConversationSegmentLinkEvidence> observedLinks)
    {
        ArgumentNullException.ThrowIfNull(observedLinks);
        return observedLinks
            .Where(link => link.SameTalkgroup)
            .Where(link => link.GapMilliseconds <= MaximumEligibleGapMilliseconds)
            .ToList();
    }

    public static ParticipantLinkCandidateShadowComparison Compare(
        IReadOnlyList<IncidentRagCandidate> baseline,
        IReadOnlyList<IncidentRagCandidate> withParticipantLinks)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(withParticipantLinks);

        var baselineIds = baseline.Select(candidate => candidate.Call.Id).ToHashSet();
        var participantIds = withParticipantLinks.Select(candidate => candidate.Call.Id).ToHashSet();
        var linked = withParticipantLinks
            .Where(candidate => candidate.ParticipantLinked)
            .Select(ToItem)
            .OrderBy(item => item.CallId)
            .ToList();

        return new ParticipantLinkCandidateShadowComparison(
            baseline.Count,
            withParticipantLinks.Count,
            linked.Where(item => !baselineIds.Contains(item.CallId)).ToList(),
            linked.Where(item => baselineIds.Contains(item.CallId)).ToList(),
            baselineIds.Where(callId => !participantIds.Contains(callId)).Order().ToList());
    }

    public static IReadOnlyList<IncidentRagCandidate> SelectProductionCandidates(
        IReadOnlyList<IncidentRagCandidate> baseline,
        IReadOnlyList<IncidentRagCandidate> withParticipantLinks,
        bool participantLinkCandidateEnabled)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(withParticipantLinks);
        return participantLinkCandidateEnabled ? withParticipantLinks : baseline;
    }

    public static string Classify(IncidentRagCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.ParticipantSameTalkgroup)
            return ParticipantLinkCandidateShadowGroups.DifferentTalkgroup;
        if (candidate.ParticipantGapMilliseconds <= 60_000)
            return ParticipantLinkCandidateShadowGroups.SameTalkgroupUpTo60Seconds;
        if (candidate.ParticipantGapMilliseconds <= 120_000)
            return ParticipantLinkCandidateShadowGroups.SameTalkgroup61To120Seconds;
        return ParticipantLinkCandidateShadowGroups.SameTalkgroupOver120Seconds;
    }

    private static ParticipantLinkCandidateShadowItem ToItem(IncidentRagCandidate candidate) =>
        new(
            candidate.Call.Id,
            Classify(candidate),
            candidate.ParticipantGapMilliseconds,
            candidate.SharedRadioCount,
            candidate.SharedRadioNearbySegmentCount,
            candidate.ParticipantSameTalkgroup,
            candidate.SharedRadioNearbySegmentCount >= FrequentlyObservedRadioCallThreshold);
}
