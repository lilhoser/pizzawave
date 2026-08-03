using System.Text;

namespace pizzad.Tests;

public sealed class CallstreamPayloadTests
{
    [Fact]
    public async Task ReadAsync_ParsesValidPayload()
    {
        var stream = BuildPayload("""{"StartTime":10,"StopTime":15,"SystemShortName":"ham","CallId":99,"Talkgroup":123,"Source":2,"Frequency":851.1}""", [1, 0, 2, 0]);

        var payload = await CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None);

        Assert.Equal(10, payload.Metadata.StartTime);
        Assert.Equal("ham", payload.Metadata.SystemShortName);
        Assert.Equal(123, payload.Metadata.Talkgroup);
        Assert.Equal(1, payload.Metadata.SchemaVersion);
        Assert.Equal(2, payload.Metadata.SystemNumber);
        Assert.Empty(payload.Metadata.Transmissions);
        Assert.Equal(2, payload.PcmS16Le.Length / 2);
    }

    [Fact]
    public async Task ReadAsync_ParsesVersion2WithCompleteTransmissionCoverage()
    {
        var stream = BuildPayload("""
            {
              "SchemaVersion":2,
              "SystemNumber":7,
              "StartTime":10,
              "StopTime":15,
              "StartTimeMs":10123,
              "StopTimeMs":15456,
              "SystemShortName":"ham",
              "CallId":99,
              "Talkgroup":123,
              "PatchedTalkgroups":[124,125],
              "Frequency":851.1,
              "SampleRate":8000,
              "AudioMappingStatus":"exact_live",
              "Transmissions":[
                {"SourceId":2010241,"SourceIdProvenance":"unknown","Talkgroup":123,"StartTimeMs":10123,"StopTimeMs":10223,"StartSample":0,"SampleCount":2,"Frequency":851.1,"TdmaSlot":0,"ErrorCount":1,"SpikeCount":2},
                {"SourceId":2010027,"SourceIdProvenance":"unknown","Talkgroup":123,"StartTimeMs":10300,"StopTimeMs":10400,"StartSample":2,"SampleCount":2,"Frequency":851.1,"TdmaSlot":0,"ErrorCount":0,"SpikeCount":0}
              ]
            }
            """, [1, 0, 2, 0, 3, 0, 4, 0]);

        var payload = await CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None);

        Assert.Equal(2, payload.Metadata.SchemaVersion);
        Assert.Equal(7, payload.Metadata.SystemNumber);
        Assert.Equal(10123, payload.Metadata.StartTimeMs);
        Assert.Equal([124L, 125L], payload.Metadata.PatchedTalkgroups);
        Assert.Equal(2, payload.Metadata.Transmissions.Count);
        Assert.Equal(2010241, payload.Metadata.Transmissions[0].SourceId);
        Assert.Equal(2, payload.Metadata.Transmissions[1].StartSample);
    }

    [Fact]
    public async Task ReadAsync_PreservesUnknownSourcesWhenAudioMappingIsUnavailable()
    {
        var stream = BuildPayload("""
            {
              "SchemaVersion":2,"SystemNumber":1,"StartTime":10,"StopTime":15,
              "SystemShortName":"ham","CallId":99,"Talkgroup":123,"SampleRate":8000,
              "AudioMappingStatus":"unavailable","Transmissions":[
                {"SourceId":null,"SourceIdProvenance":"unknown","Talkgroup":123,
                 "StartTimeMs":10000,"StopTimeMs":10100,"StartSample":null,"SampleCount":2}
              ]
            }
            """, [1, 0]);

        var payload = await CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None);

        Assert.Null(payload.Metadata.Transmissions.Single().SourceId);
        Assert.Null(payload.Metadata.Transmissions.Single().StartSample);
    }

    [Fact]
    public async Task ReadAsync_ParsesVersion3CaptureCompletenessWithoutMarkingLaterTransmissionsIncomplete()
    {
        var stream = BuildPayload("""
            {
              "SchemaVersion":3,"SystemNumber":1,"StartTime":10,"StopTime":15,
              "StartTimeMs":10000,"StopTimeMs":15000,"SystemShortName":"ham","CallId":99,
              "Talkgroup":123,"SampleRate":8000,"AudioMappingStatus":"exact_live",
              "ChannelAssignmentStart":"update","BeginsChannelAssignment":false,
              "PossiblyIncompleteTransmissionStartTimeMs":10000,
              "Transmissions":[
                {"SourceId":1,"SourceIdProvenance":"unknown","StartStatus":"possibly_incomplete","Talkgroup":123,
                 "StartTimeMs":10000,"StopTimeMs":10500,"StartSample":0,"SampleCount":2},
                {"SourceId":2,"SourceIdProvenance":"unknown","StartStatus":"observed_boundary","Talkgroup":123,
                 "StartTimeMs":11000,"StopTimeMs":11500,"StartSample":2,"SampleCount":2}
              ]
            }
            """, [1, 0, 2, 0, 3, 0, 4, 0]);

        var payload = await CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None);

        Assert.Equal(3, payload.Metadata.SchemaVersion);
        Assert.Equal("update", payload.Metadata.ChannelAssignmentStart);
        Assert.False(payload.Metadata.BeginsChannelAssignment);
        Assert.Equal("possibly_incomplete", payload.Metadata.Transmissions[0].StartStatus);
        Assert.Equal("observed_boundary", payload.Metadata.Transmissions[1].StartStatus);
    }

    [Fact]
    public async Task ReadAsync_RejectsVersion3ThatMarksALaterTransmissionIncomplete()
    {
        var stream = BuildPayload("""
            {
              "SchemaVersion":3,"SystemNumber":1,"StartTime":10,"StopTime":15,
              "SystemShortName":"ham","CallId":99,"Talkgroup":123,"SampleRate":8000,
              "AudioMappingStatus":"exact_live","ChannelAssignmentStart":"update","BeginsChannelAssignment":false,
              "PossiblyIncompleteTransmissionStartTimeMs":10000,
              "Transmissions":[
                {"SourceId":1,"SourceIdProvenance":"unknown","StartStatus":"possibly_incomplete","Talkgroup":123,
                 "StartTimeMs":10000,"StopTimeMs":10100,"StartSample":0,"SampleCount":1},
                {"SourceId":2,"SourceIdProvenance":"unknown","StartStatus":"possibly_incomplete","Talkgroup":123,
                 "StartTimeMs":10200,"StopTimeMs":10300,"StartSample":1,"SampleCount":1}
              ]
            }
            """, [1, 0, 2, 0]);

        await Assert.ThrowsAsync<InvalidDataException>(() => CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_DoesNotShiftIncompleteStatusWhenOriginalFirstTransmissionWasOmitted()
    {
        var stream = BuildPayload("""
            {
              "SchemaVersion":3,"SystemNumber":1,"StartTime":10,"StopTime":15,
              "SystemShortName":"ham","CallId":99,"Talkgroup":123,"SampleRate":8000,
              "AudioMappingStatus":"exact_live","ChannelAssignmentStart":"update","BeginsChannelAssignment":false,
              "PossiblyIncompleteTransmissionStartTimeMs":9000,
              "Transmissions":[
                {"SourceId":2,"SourceIdProvenance":"unknown","StartStatus":"observed_boundary","Talkgroup":123,
                 "StartTimeMs":10000,"StopTimeMs":10100,"StartSample":0,"SampleCount":2}
              ]
            }
            """, [1, 0, 2, 0]);

        var payload = await CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None);

        Assert.Equal("observed_boundary", payload.Metadata.Transmissions.Single().StartStatus);
        Assert.Equal(9000, payload.Metadata.PossiblyIncompleteTransmissionStartTimeMs);
    }

    [Fact]
    public async Task ReadAsync_RejectsIncompleteVersion2AudioCoverage()
    {
        var stream = BuildPayload("""
            {
              "SchemaVersion":2,"SystemNumber":1,"StartTime":10,"StopTime":15,
              "SystemShortName":"ham","CallId":99,"Talkgroup":123,"SampleRate":8000,
              "AudioMappingStatus":"exact_live","Transmissions":[
                {"SourceId":1,"SourceIdProvenance":"unknown","Talkgroup":123,
                 "StartTimeMs":10000,"StopTimeMs":10100,"StartSample":0,"SampleCount":1}
              ]
            }
            """, [1, 0, 2, 0]);

        await Assert.ThrowsAsync<InvalidDataException>(() => CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_RejectsGapBetweenTransmissionRanges()
    {
        var stream = BuildPayload("""
            {
              "SchemaVersion":2,"SystemNumber":1,"StartTime":10,"StopTime":15,
              "SystemShortName":"ham","CallId":99,"Talkgroup":123,"SampleRate":8000,
              "AudioMappingStatus":"exact_reconstructed","Transmissions":[
                {"SourceId":1,"SourceIdProvenance":"unknown","Talkgroup":123,"StartTimeMs":10000,"StopTimeMs":10100,"StartSample":0,"SampleCount":1},
                {"SourceId":2,"SourceIdProvenance":"unknown","Talkgroup":123,"StartTimeMs":10200,"StopTimeMs":10300,"StartSample":2,"SampleCount":1}
              ]
            }
            """, [1, 0, 2, 0]);

        await Assert.ThrowsAsync<InvalidDataException>(() => CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_RejectsMissingRequiredMetadata()
    {
        var stream = BuildPayload("""{"StartTime":10,"StopTime":15,"SystemShortName":"ham","CallId":99}""", [1, 0]);

        await Assert.ThrowsAsync<InvalidDataException>(() => CallstreamPayload.ReadAsync(stream, 8000, CancellationToken.None));
    }

    private static MemoryStream BuildPayload(string json, byte[] pcm)
    {
        var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(0x415A5A50));
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        stream.Write(BitConverter.GetBytes((long)jsonBytes.Length));
        stream.Write(BitConverter.GetBytes(pcm.Length / 2));
        stream.Write(jsonBytes);
        stream.Write(pcm);
        stream.Position = 0;
        return stream;
    }
}
