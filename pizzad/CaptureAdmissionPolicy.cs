namespace pizzad;

public sealed record CaptureAdmissionDecision(
    string Disposition,
    bool PersistAudio,
    bool Transcribe,
    bool CanSeedIncident,
    long? ContinuationOfCallId = null);

public static class CaptureAdmissionPolicy
{
    public const int MaximumSuppressibleFragmentMilliseconds = 1000;

    public static bool NeedsStrictContinuationLookup(CallstreamMetadata metadata) =>
        IsSingleShortLateEntry(metadata);

    public static bool CanCreateIncident(IEnumerable<EngineCall> calls) =>
        calls.Any(call => call.CanSeedIncident);

    public static CaptureAdmissionDecision Decide(CallstreamMetadata metadata, long? strictContinuationCallId)
    {
        if (metadata.SchemaVersion < 3 || metadata.ChannelAssignmentStart == "unknown")
            return new("legacy", true, true, true);

        if (metadata.BeginsChannelAssignment)
            return new("complete_assignment_start", true, true, true);

        if (IsSingleShortLateEntry(metadata))
        {
            return strictContinuationCallId.HasValue
                ? new("attached_incomplete_fragment", true, false, false, strictContinuationCallId)
                : new("suppressed_incomplete_fragment", false, false, false);
        }

        // The first transmission may have a missing beginning, but later
        // decoder-delimited transmissions remain useful. This call may support
        // an incident but must not establish a new incident by itself.
        return new("late_entry_with_retained_evidence", true, true, false);
    }

    private static bool IsSingleShortLateEntry(CallstreamMetadata metadata)
    {
        if (metadata.ChannelAssignmentStart != "update" || metadata.Transmissions.Count != 1)
            return false;
        var transmission = metadata.Transmissions[0];
        return transmission.StartStatus == "possibly_incomplete" &&
               transmission.SampleCount * 1000L < metadata.SampleRate * MaximumSuppressibleFragmentMilliseconds;
    }
}
