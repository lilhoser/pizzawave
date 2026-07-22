namespace pizzad;

public enum IncidentAssociationReviewAction
{
    ConfirmMembership,
    RejectMembership,
    Defer
}

public sealed record IncidentAssociationReviewLedgerEntry(
    string ReviewEntryId,
    DateTimeOffset RecordedAtUtc,
    string ProposalKey,
    string RunId,
    string ProjectionEventId,
    IncidentAssociationReviewAction Action,
    long AnchorIncidentId,
    IReadOnlyList<long> CallIds,
    string Actor,
    string Note);

public sealed record IncidentAssociationStoredReviewLedgerEntry(
    long Sequence,
    string ContentHash,
    IncidentAssociationReviewLedgerEntry Entry);

public sealed record IncidentAssociationReviewRequest(
    string ProposalKey,
    string RunId,
    string ProjectionEventId,
    string Action,
    long AnchorIncidentId,
    IReadOnlyList<long> CallIds,
    string Note = "");

public sealed record IncidentAssociationReviewCallDto(
    long CallId,
    long Timestamp,
    string Transcript,
    string AudioUrl,
    string SystemShortName,
    long Talkgroup,
    string TalkgroupName,
    long CurrentIncidentId,
    string CurrentIncidentTitle,
    string CurrentIncidentStatus,
    string ReviewState);

public sealed record IncidentAssociationReviewEvidenceDto(
    long NewCallId,
    IReadOnlyList<long> CandidateCallIds,
    string RelationshipStatement,
    double Uncertainty,
    IReadOnlyList<string> NewObservationQuotes,
    IReadOnlyList<string> CandidateQuotes,
    IReadOnlyList<string> UnresolvedQuestions);

public sealed record IncidentAssociationReviewGroupDto(
    string ProposalKey,
    string RunId,
    string ProjectionEventId,
    string Placement,
    string Status,
    DateTime LatestProposalUtc,
    IReadOnlyList<long> AnchorIncidentIds,
    IReadOnlyList<IncidentAssociationReviewCallDto> Calls,
    IReadOnlyList<IncidentAssociationReviewEvidenceDto> Evidence,
    int PendingCallCount);

public sealed record IncidentAssociationReviewReportDto(
    bool Enabled,
    string RunId,
    int PendingGroupCount,
    int StandalonePendingGroupCount,
    IReadOnlyList<IncidentAssociationReviewGroupDto> Groups);

public static class IncidentAssociationReviewContract
{
    public static string ProposalKey(string runId, string projectionEventId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionEventId);
        return $"{runId.Length}:{runId}{projectionEventId}";
    }

    public static IReadOnlyList<string> Validate(IncidentAssociationReviewLedgerEntry entry)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(entry.ReviewEntryId)) errors.Add("review entry id is required");
        if (string.IsNullOrWhiteSpace(entry.ProposalKey)) errors.Add("proposal key is required");
        if (string.IsNullOrWhiteSpace(entry.RunId)) errors.Add("run id is required");
        if (string.IsNullOrWhiteSpace(entry.ProjectionEventId)) errors.Add("projection event id is required");
        if (entry.RecordedAtUtc == default) errors.Add("recorded timestamp is required");
        if (entry.AnchorIncidentId < 0) errors.Add("anchor incident id cannot be negative");
        if (entry.CallIds.Count == 0) errors.Add("at least one call id is required");
        if (entry.CallIds.Any(callId => callId <= 0)) errors.Add("call ids must be positive");
        if (entry.CallIds.Count != entry.CallIds.Distinct().Count()) errors.Add("call ids must be unique");
        if (entry.CallIds.Count > 100) errors.Add("a review entry cannot contain more than 100 calls");
        if (!string.IsNullOrWhiteSpace(entry.RunId) &&
            !string.IsNullOrWhiteSpace(entry.ProjectionEventId) &&
            !string.Equals(entry.ProposalKey, ProposalKey(entry.RunId, entry.ProjectionEventId), StringComparison.Ordinal))
            errors.Add("proposal key does not match the source run and projection event");
        if (entry.Action == IncidentAssociationReviewAction.ConfirmMembership && entry.AnchorIncidentId == 0 && entry.CallIds.Count < 2)
            errors.Add("confirming an unanchored association requires at least two calls");
        if (entry.Note.Length > 2_000) errors.Add("review note cannot exceed 2000 characters");
        return errors;
    }

    public static bool TryParseAction(string value, out IncidentAssociationReviewAction action) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out action) &&
        Enum.IsDefined(action);
}
