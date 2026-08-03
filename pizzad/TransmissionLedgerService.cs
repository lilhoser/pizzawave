namespace pizzad;

public sealed class TransmissionLedgerService
{
    private readonly EngineDatabase _database;

    public TransmissionLedgerService(EngineDatabase database)
    {
        _database = database;
    }

    public async Task<CallTransmissionSessionDto?> GetSessionAsync(long callId, CancellationToken ct)
    {
        var call = await _database.GetCallAsync(callId, ct);
        if (call == null) return null;
        var rows = await _database.GetCallTransmissionsAsync(callId, ct);
        return BuildSession(call, rows);
    }

    private static CallTransmissionSessionDto BuildSession(EngineCall call, IReadOnlyList<CallTransmissionRecord> rows)
    {
        if (rows.Count == 0)
            return new()
            {
                CallId = call.Id,
                SystemShortName = call.SystemShortName,
                Talkgroup = call.Talkgroup,
                TalkgroupName = call.TalkgroupName,
                ChannelAssignmentStart = call.ChannelAssignmentStart,
                BeginsChannelAssignment = call.BeginsChannelAssignment,
                CaptureDisposition = call.CaptureDisposition,
                FullAudioUrl = string.IsNullOrWhiteSpace(call.AudioPath) ? "" : $"/api/v1/calls/{call.Id}/audio",
                Message = "Individual transmissions are not available for this recording. It was received before Callstream version 2, or version 2 could not preserve its transmission ledger."
            };

        var firstStart = rows.Min(row => row.StartTimeMs);
        var mappingStates = rows.Select(row => row.AudioMappingStatus).Distinct(StringComparer.Ordinal).ToList();
        var mapping = mappingStates.Count == 1 ? mappingStates[0] : "unavailable";
        var transmissions = rows.Select(row => new CallTransmissionDto
        {
            Sequence = row.Sequence,
            SourceId = row.SourceId,
            SourceIdProvenance = row.SourceIdProvenance,
            Talkgroup = row.Talkgroup,
            StartTimeMs = row.StartTimeMs,
            StopTimeMs = row.StopTimeMs,
            OffsetMs = Math.Max(0, row.StartTimeMs - firstStart),
            DurationMs = Math.Max(0, row.StopTimeMs - row.StartTimeMs),
            StartSample = row.StartSample,
            SampleCount = row.SampleCount,
            Frequency = row.Frequency,
            TdmaSlot = row.TdmaSlot,
            ErrorCount = row.ErrorCount,
            SpikeCount = row.SpikeCount,
            StartStatus = row.StartStatus
        }).ToList();
        return new()
        {
            CallId = call.Id,
            SystemShortName = call.SystemShortName,
            Talkgroup = call.Talkgroup,
            TalkgroupName = call.TalkgroupName,
            Available = true,
            AudioMappingStatus = mapping,
            ChannelAssignmentStart = call.ChannelAssignmentStart,
            BeginsChannelAssignment = call.BeginsChannelAssignment,
            CaptureDisposition = call.CaptureDisposition,
            TransmissionCount = transmissions.Count,
            IdentifiedRadioCount = transmissions.Where(row => row.SourceId.HasValue).Select(row => row.SourceId).Distinct().Count(),
            UnknownSourceCount = transmissions.Count(row => !row.SourceId.HasValue),
            StartTimeMs = transmissions.Min(row => row.StartTimeMs),
            StopTimeMs = transmissions.Max(row => row.StopTimeMs),
            FullAudioUrl = string.IsNullOrWhiteSpace(call.AudioPath) ? "" : $"/api/v1/calls/{call.Id}/audio",
            Message = "P25 supplies transmitting-radio identifiers but does not identify dispatcher and responder roles.",
            Transmissions = transmissions
        };
    }

}
