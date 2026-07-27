namespace pizzad.Tests;

using System.Reflection;

public sealed class SetupSdrDetectionTests
{
    [Fact]
    public void ParseRtlAndAirspyOutputs_ReturnTypedDevices()
    {
        const string rtlOutput = """
            Found 1 device(s):
              0:  Realtek, RTL2838UHIDIR, SN: 00000002
            """;
        const string airspyOutput = """
            airspy_lib_version: 1.0.11
            Found AirSpy board 1
            Board ID Number: 0 (AIRSPY MINI)
            Serial Number: 0x26A464DC28793293
            Supported sample rates:
                10.000000 MSPS
                6.000000 MSPS
                3.000000 MSPS
            """;

        var rtlDevices = InvokeParser("ParseRtlDevices", rtlOutput);
        var airspyDevices = InvokeParser("ParseAirspyDevices", airspyOutput, rtlDevices.Count);

        var rtl = Assert.Single(rtlDevices);
        Assert.Equal("RTL-SDR", rtl.Type);
        Assert.Equal("00000002", rtl.Serial);
        Assert.Equal("rtl=00000002,buflen=65536", rtl.DeviceArgs);
        Assert.Equal(2_400_000, rtl.DefaultSampleRate);

        var airspy = Assert.Single(airspyDevices);
        Assert.Equal("Airspy", airspy.Type);
        Assert.Equal("26A464DC28793293", airspy.Serial);
        Assert.Equal("airspy=26A464DC28793293", airspy.DeviceArgs);
        Assert.Equal(6_000_000, airspy.DefaultSampleRate);
        Assert.Contains(6_000_000, airspy.SampleRateOptions);
    }

    [Fact]
    public void ParseAirspyOutput_NotFound_ReturnsNoDevices()
    {
        var devices = InvokeParser("ParseAirspyDevices", """
            airspy_open() board 1 failed: AIRSPY_ERROR_NOT_FOUND (-5)
            airspy_lib_version: 1.0.11
            """, 0);

        Assert.Empty(devices);
    }

    private static IReadOnlyList<SetupSdrDeviceDto> InvokeParser(string methodName, string output, int startIndex = 0)
    {
        var method = typeof(SetupJobService).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SetupJobService).FullName, methodName);
        var args = method.GetParameters().Length == 1
            ? new object[] { output }
            : new object[] { output, startIndex };
        return (IReadOnlyList<SetupSdrDeviceDto>)(method.Invoke(null, args)
            ?? throw new InvalidOperationException($"{methodName} returned null."));
    }
}
