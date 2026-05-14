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
        Assert.Equal(2, payload.PcmS16Le.Length / 2);
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
