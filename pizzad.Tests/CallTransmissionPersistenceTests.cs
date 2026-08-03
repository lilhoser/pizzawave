using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class CallTransmissionPersistenceTests
{
    [Fact]
    public async Task ReplaceCallTransmissions_PreservesOrderUnknownSourceAndMapping()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-transmission-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio")
                }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var callId = await database.UpsertCallAsync(new EngineCall
            {
                UniqueKey = "transmission-parent",
                StartTime = 10,
                StopTime = 15,
                Source = 7,
                SystemShortName = "ham",
                CallstreamCallId = 99,
                Talkgroup = 123,
                Frequency = 851.1,
                AudioPath = "test.wav"
            }, CancellationToken.None);
            var metadata = new CallstreamMetadata(
                2, 10, 15, 10000, 15000, "ham", 99, 123, 7, 851.1, 8000, "exact_live", [124],
                [
                    new(2010241, "unknown", 123, 10000, 10100, 0, 2, 851.1, 0, 1, 2),
                    new(null, "unknown", 123, 10200, 10300, 2, 2, 851.1, 0, 0, 0)
                ]);

            await database.ReplaceCallTransmissionsAsync(callId, metadata, CancellationToken.None);
            await database.ReplaceCallTransmissionsAsync(callId, metadata, CancellationToken.None);
            await database.ReplaceCallTransmissionsAsync(callId, metadata with
            {
                SchemaVersion = 1,
                AudioMappingStatus = "legacy_unavailable",
                Transmissions = []
            }, CancellationToken.None);
            var rows = await database.GetCallTransmissionsAsync(callId, CancellationToken.None);

            Assert.Equal(2, rows.Count);
            Assert.Equal(2010241, rows[0].SourceId);
            Assert.Null(rows[1].SourceId);
            Assert.Equal(2, rows[1].StartSample);
            Assert.All(rows, row => Assert.Equal("exact_live", row.AudioMappingStatus));

            var service = new TransmissionLedgerService(database);
            var session = await service.GetSessionAsync(callId, CancellationToken.None);
            Assert.NotNull(session);
            Assert.True(session.Available);
            Assert.Equal("ham", session.SystemShortName);
            Assert.Equal(2, session.TransmissionCount);
            Assert.Equal(1, session.IdentifiedRadioCount);
            Assert.Equal(1, session.UnknownSourceCount);
            Assert.Equal(200, session.Transmissions[1].OffsetMs);
            var participants = await database.ListConversationSegmentParticipantsAsync(0, 20, CancellationToken.None);
            var participant = Assert.Single(participants);
            Assert.Equal(callId, participant.CallId);
            Assert.Equal(2010241, participant.SourceId);
            Assert.Equal(1, participant.TransmissionCount);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task StrictContinuation_RequiresMatchingCompleteRadioEvidenceAndExcludesIncompleteFragmentFromLinkage()
    {
        var root = Path.Combine(Path.GetTempPath(), "pizzawave-continuation-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new EngineDatabase(new EngineConfig
            {
                Storage = new StorageConfig
                {
                    DatabasePath = Path.Combine(root, "pizzad.db"),
                    AudioRoot = Path.Combine(root, "audio")
                }
            }, NullLogger<EngineDatabase>.Instance);
            await database.InitializeAsync(CancellationToken.None);
            var parentId = await database.UpsertCallAsync(new EngineCall
            {
                UniqueKey = "parent",
                StartTime = 10,
                StopTime = 11,
                Source = 1,
                SystemShortName = "ham",
                CallstreamCallId = 1,
                Talkgroup = 123,
                AudioPath = "parent.wav",
                ChannelAssignmentStart = "grant",
                BeginsChannelAssignment = true,
                CanSeedIncident = true,
                CaptureDisposition = "complete_assignment_start"
            }, CancellationToken.None);
            var parentMetadata = Metadata(1, "grant", true,
                new(42, "unknown", 123, 10000, 10500, 0, 4000, 851.1, 0, 0, 0, "observed_boundary"));
            await database.ReplaceCallTransmissionsAsync(parentId, parentMetadata, CancellationToken.None);

            var fragmentMetadata = Metadata(2, "update", false,
                new(42, "unknown", 123, 11000, 11400, 0, 3200, 851.1, 0, 0, 0, "possibly_incomplete"));
            var continuation = await database.FindStrictContinuationCallAsync(fragmentMetadata, 3000, CancellationToken.None);
            Assert.Equal(parentId, continuation);

            var fragmentId = await database.UpsertCallAsync(new EngineCall
            {
                UniqueKey = "fragment",
                StartTime = 11,
                StopTime = 12,
                Source = 1,
                SystemShortName = "ham",
                CallstreamCallId = 2,
                Talkgroup = 123,
                AudioPath = "fragment.wav",
                ChannelAssignmentStart = "update",
                BeginsChannelAssignment = false,
                CanSeedIncident = false,
                CaptureDisposition = "attached_incomplete_fragment",
                ContinuationOfCallId = parentId
            }, CancellationToken.None);
            await database.ReplaceCallTransmissionsAsync(fragmentId, fragmentMetadata, CancellationToken.None);

            var participants = await database.ListConversationSegmentParticipantsAsync(0, 20, CancellationToken.None);
            var participant = Assert.Single(participants);
            Assert.Equal(parentId, participant.CallId);
            Assert.Equal(42, participant.SourceId);

            await database.ReplaceCallTransmissionsAsync(fragmentId, fragmentMetadata, CancellationToken.None, audioPersisted: false);
            var suppressedRows = await database.GetCallTransmissionsAsync(fragmentId, CancellationToken.None);
            var suppressedRow = Assert.Single(suppressedRows);
            Assert.Null(suppressedRow.StartSample);
            Assert.Equal("unavailable", suppressedRow.AudioMappingStatus);

            var wrongTalkgroup = fragmentMetadata with { Talkgroup = 999 };
            Assert.Null(await database.FindStrictContinuationCallAsync(wrongTalkgroup, 3000, CancellationToken.None));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static CallstreamMetadata Metadata(long callId, string start, bool begins, CallstreamTransmission transmission) =>
        new(3, 10, 15, transmission.StartTimeMs, transmission.StopTimeMs, "ham", callId, 123, 1, 851.1, 8000,
            "exact_live", [], [transmission], start, begins,
            start == "update" ? transmission.StartTimeMs : null);
}
