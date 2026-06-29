namespace pizzad.Tests;

using System.Reflection;
using System.Text.Json;

public sealed class RfSurveyAirspyCommandTests
{
    [Fact]
    public void BuildRfPowerScanCommand_AirspyUsesMhzAndSerialSelector()
    {
        var source = new RfSurveySourceDto(
            0,
            "airspy=26A464DC28793293",
            "26A464DC28793293",
            "Airspy",
            856_225_000,
            3_000_000,
            0,
            "15");

        var command = InvokeBuildCommand(source, 856_237_500, 3_000_000, 120_000, "/tmp/pizzawave airspy.cs16", true, "");

        Assert.Contains("airspy_rx", command);
        Assert.Contains("-s", command);
        Assert.Contains("0x26A464DC28793293", command);
        Assert.Contains("-f 856.2375", command);
        Assert.DoesNotContain("-f 856237500", command);
        Assert.Contains("-a 3000000", command);
        Assert.Contains("-g 15", command);
    }

    [Fact]
    public void NormalizeP25ProbeDeviceArgs_AirspyAddsLinearityMode()
    {
        var source = AirspySource("airspy=26A464DC28793293", "26A464DC28793293");

        var deviceArgs = InvokeNormalizeP25ProbeDeviceArgs(source);

        Assert.Equal("airspy=26A464DC28793293,linearity,bias=0", deviceArgs);
    }

    [Fact]
    public void NormalizeP25ProbeDeviceArgs_AirspyPreservesExistingModeAndBias()
    {
        var source = AirspySource("airspy=26A464DC28793293,sensitivity,bias=1", "26A464DC28793293");

        var deviceArgs = InvokeNormalizeP25ProbeDeviceArgs(source);

        Assert.Equal("airspy=26A464DC28793293,sensitivity,bias=1", deviceArgs);
    }

    [Fact]
    public void NormalizeP25ProbeDeviceArgs_AirspyCanUseSerialWhenDeviceIsMissing()
    {
        var source = AirspySource("", "26A464DC28793293");

        var deviceArgs = InvokeNormalizeP25ProbeDeviceArgs(source);

        Assert.Equal("airspy=26A464DC28793293,linearity,bias=0", deviceArgs);
    }

    [Fact]
    public void NormalizeP25ProbeDeviceArgs_AirspyNamedGainsDisableOptimizedModes()
    {
        var source = AirspySource("airspy=26A464DC28793293,linearity,bias=1", "26A464DC28793293");

        var deviceArgs = InvokeNormalizeP25ProbeDeviceArgs(source, useNamedAirspyStageGains: true);

        Assert.Equal("airspy=26A464DC28793293,bias=1,sensitivity=0,linearity=0", deviceArgs);
    }

    [Fact]
    public void NormalizeP25ProbeDeviceArgs_RtlSdrIsUnchanged()
    {
        var source = new RfSurveySourceDto(
            0,
            "rtl=00000001",
            "00000001",
            "RTL-SDR",
            856_225_000,
            2_400_000,
            0,
            "32");

        var deviceArgs = InvokeNormalizeP25ProbeDeviceArgs(source);

        Assert.Equal("rtl=00000001", deviceArgs);
    }

    [Fact]
    public void BuildP25ProbeGainArgs_AirspyUsesNamedStageGains()
    {
        var source = AirspySource("airspy=26A464DC28793293", "26A464DC28793293") with { Gain = "20" };

        var gainArgs = InvokeBuildP25ProbeGainArgs(source);

        Assert.Contains("-N", gainArgs);
        Assert.Contains("LNA:15,MIX:12,IF:8", gainArgs);
    }

    [Fact]
    public void BuildP25ProbeGainArgs_RtlUsesGenericGain()
    {
        var source = new RfSurveySourceDto(
            0,
            "rtl=00000001",
            "00000001",
            "RTL-SDR",
            856_225_000,
            2_400_000,
            0,
            "32");

        var gainArgs = InvokeBuildP25ProbeGainArgs(source);

        Assert.Contains("-g", gainArgs);
        Assert.Contains("32", gainArgs);
    }

    [Fact]
    public void BuildRfValidationPowerParameters_PreservesExplicitSingleChannelScan()
    {
        using var doc = JsonDocument.Parse("""{"scanAllControlChannels":false,"gainSequence":["20"]}""");

        var parameters = InvokeBuildRfValidationPowerParameters(doc.RootElement);

        Assert.False(parameters.GetProperty("scanAllControlChannels").GetBoolean());
        Assert.Equal("20", parameters.GetProperty("gainSequence")[0].GetString());
    }

    [Fact]
    public void BuildRfValidationPowerParameters_DefaultsToAllControlChannels()
    {
        using var doc = JsonDocument.Parse("""{"gainSequence":["20"]}""");

        var parameters = InvokeBuildRfValidationPowerParameters(doc.RootElement);

        Assert.True(parameters.GetProperty("scanAllControlChannels").GetBoolean());
    }

    [Fact]
    public void ReadPowerScanControlChannels_UsesRequestedChannelWhenScanAllIsFalse()
    {
        using var doc = JsonDocument.Parse("""{"scanAllControlChannels":false}""");

        var channels = InvokeReadPowerScanControlChannels(doc.RootElement, 770_531_250, [769_606_250, 770_531_250]);

        Assert.Equal([770_531_250], channels);
    }

    private static string InvokeBuildCommand(RfSurveySourceDto source, long frequencyHz, int sampleRate, int sampleCount, string rawPath, bool isAirspy, string rtlDeviceArg)
    {
        var method = typeof(RfSurveyService).GetMethod("BuildRfPowerScanCommand", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildRfPowerScanCommand");
        return (string)(method.Invoke(null, [source, frequencyHz, sampleRate, sampleCount, rawPath, isAirspy, rtlDeviceArg])
            ?? throw new InvalidOperationException("BuildRfPowerScanCommand returned null."));
    }

    private static string InvokeNormalizeP25ProbeDeviceArgs(RfSurveySourceDto source, bool useNamedAirspyStageGains = false)
    {
        var method = typeof(RfSurveyService).GetMethod("NormalizeP25ProbeDeviceArgs", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "NormalizeP25ProbeDeviceArgs");
        return (string)(method.Invoke(null, [source, useNamedAirspyStageGains])
            ?? throw new InvalidOperationException("NormalizeP25ProbeDeviceArgs returned null."));
    }

    private static string InvokeBuildP25ProbeGainArgs(RfSurveySourceDto source)
    {
        var method = typeof(RfSurveyService).GetMethod("BuildP25ProbeGainArgs", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildP25ProbeGainArgs");
        return (string)(method.Invoke(null, [source])
            ?? throw new InvalidOperationException("BuildP25ProbeGainArgs returned null."));
    }

    private static JsonElement InvokeBuildRfValidationPowerParameters(JsonElement parameters)
    {
        var method = typeof(RfSurveyService).GetMethod("BuildRfValidationPowerParameters", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "BuildRfValidationPowerParameters");
        return (JsonElement)(method.Invoke(null, [parameters])
            ?? throw new InvalidOperationException("BuildRfValidationPowerParameters returned null."));
    }

    private static IReadOnlyList<long> InvokeReadPowerScanControlChannels(JsonElement parameters, long requestedControlChannel, IReadOnlyList<long> profileControlChannels)
    {
        var method = typeof(RfSurveyService).GetMethod("ReadPowerScanControlChannels", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "ReadPowerScanControlChannels");
        return (IReadOnlyList<long>)(method.Invoke(null, [parameters, requestedControlChannel, profileControlChannels])
            ?? throw new InvalidOperationException("ReadPowerScanControlChannels returned null."));
    }

    private static RfSurveySourceDto AirspySource(string device, string serial) =>
        new(
            0,
            device,
            serial,
            "Airspy",
            856_225_000,
            3_000_000,
            0,
            "15");
}
