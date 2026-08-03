using Xunit;

namespace pizzad.Tests;

public sealed class CallAudioSliceTests
{
    [Fact]
    public void CreatesExactPcmSliceWithOriginalSampleRate()
    {
        var sourcePcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var source = CreateWav(sourcePcm, 8000);

        var ok = CallAudioService.TryCreatePcm16MonoSlice(source, 1, 2, out var slice);

        Assert.True(ok);
        using (slice)
        {
            var info = CallAudioService.TryReadWavFormat(slice);
            Assert.NotNull(info);
            Assert.Equal(8000, info.SampleRate);
            Assert.Equal(4, info.DataSize);
            Assert.Equal(new byte[] { 3, 4, 5, 6 }, slice.ToArray().Skip(info.DataOffset).Take(info.DataSize));
        }
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 0)]
    [InlineData(3, 2)]
    public void RejectsInvalidOrOutOfRangeSlice(int startSample, int sampleCount)
    {
        using var source = CreateWav(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 8000);

        Assert.False(CallAudioService.TryCreatePcm16MonoSlice(source, startSample, sampleCount, out _));
    }

    [Fact]
    public void RejectsStereoAudioBecauseOffsetsAreMonoSampleOffsets()
    {
        using var source = CreateWav(new byte[] { 1, 2, 3, 4 }, 8000);
        var bytes = source.ToArray();
        BitConverter.GetBytes((short)2).CopyTo(bytes, 22);
        using var stereo = new MemoryStream(bytes);

        Assert.False(CallAudioService.TryCreatePcm16MonoSlice(stereo, 0, 1, out _));
    }

    private static MemoryStream CreateWav(byte[] pcm, int sampleRate)
    {
        var bytes = new byte[44 + pcm.Length];
        Write(bytes, 0, "RIFF");
        BitConverter.GetBytes(bytes.Length - 8).CopyTo(bytes, 4);
        Write(bytes, 8, "WAVE");
        Write(bytes, 12, "fmt ");
        BitConverter.GetBytes(16).CopyTo(bytes, 16);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 20);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(bytes, 24);
        BitConverter.GetBytes(sampleRate * 2).CopyTo(bytes, 28);
        BitConverter.GetBytes((short)2).CopyTo(bytes, 32);
        BitConverter.GetBytes((short)16).CopyTo(bytes, 34);
        Write(bytes, 36, "data");
        BitConverter.GetBytes(pcm.Length).CopyTo(bytes, 40);
        pcm.CopyTo(bytes, 44);
        return new MemoryStream(bytes);
    }

    private static void Write(byte[] bytes, int offset, string value)
    {
        for (var index = 0; index < value.Length; index++) bytes[offset + index] = (byte)value[index];
    }
}
