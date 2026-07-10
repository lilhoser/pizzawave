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
    public void P25ProbeSampleRate_AirspyPrefersSixMsps()
    {
        var source = AirspySource("airspy=26A464DC28793293", "26A464DC28793293") with { SampleRate = 3_000_000 };
        var profile = new RfSurveyProfileDto
        {
            Sources = [source],
            Devices =
            [
                new(0, source.Serial, "Airspy Mini", "Airspy", source.Device, "", [3_000_000, 6_000_000], 3_000_000)
            ]
        };

        var sampleRate = InvokeP25ProbeSampleRate(profile, source);

        Assert.Equal(6_000_000, sampleRate);
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
    public void BuildRfValidationPowerParameters_PreservesSourceBoundMeasurements()
    {
        using var doc = JsonDocument.Parse("""{"sourceMeasurements":[{"sourceIndex":0,"sourceSerial":"A0","controlChannelHz":773000000,"gain":"15","sampleRateHz":6000000,"measuredSignalOffsetHz":1250},{"sourceIndex":1,"sourceSerial":"B1","controlChannelHz":769000000,"gain":"12","sampleRateHz":3000000,"measuredSignalOffsetHz":-500}]}""");

        var parameters = InvokeBuildRfValidationPowerParameters(doc.RootElement);

        Assert.Equal(2, parameters.GetProperty("sourceMeasurements").GetArrayLength());
        Assert.Equal("B1", parameters.GetProperty("sourceMeasurements")[1].GetProperty("sourceSerial").GetString());
        Assert.Equal(-500, parameters.GetProperty("sourceMeasurements")[1].GetProperty("measuredSignalOffsetHz").GetInt32());
    }

    [Fact]
    public void ReadPowerScanSourceMeasurements_KeepsSameFrequencyForDifferentSdrs()
    {
        using var doc = JsonDocument.Parse("""{"sourceMeasurements":[{"sourceIndex":0,"sourceSerial":"A0","controlChannelHz":773000000,"gain":"15","sampleRateHz":6000000,"measuredSignalOffsetHz":1250},{"sourceIndex":1,"sourceSerial":"B1","controlChannelHz":773000000,"gain":"12","sampleRateHz":3000000,"measuredSignalOffsetHz":-500}]}""");

        var measurements = InvokeReadPowerScanSourceMeasurements(doc.RootElement);

        Assert.Equal(2, measurements.Count);
        Assert.Equal([0, 1], measurements.Select(row => (int)GetProperty(row, "SourceIndex")).ToArray());
        Assert.Equal([1250, -500], measurements.Select(row => (int?)GetProperty(row, "MeasuredSignalOffsetHz")).ToArray());
        Assert.Equal([6_000_000, 3_000_000], measurements.Select(row => (int?)GetProperty(row, "SampleRateHz")).ToArray());
    }

    [Fact]
    public void ReconcilePowerScanSourceMeasurements_UsesSavedCrystalCorrectionForWeakOffsets()
    {
        using var doc = JsonDocument.Parse("""{"sourceMeasurements":[{"sourceIndex":1,"sourceSerial":"B1","controlChannelHz":855587500,"gain":"21","sampleRateHz":6000000,"measuredSignalOffsetHz":91,"snrDb":7.5,"confidence":0.89},{"sourceIndex":1,"sourceSerial":"B1","controlChannelHz":857837500,"gain":"21","sampleRateHz":6000000,"measuredSignalOffsetHz":-3344,"snrDb":7.1,"confidence":0.88}]}""");
        var profile = new RfSurveyProfileDto
        {
            Sources = [new RfSurveySourceDto(1, "airspy=B1", "B1", "Airspy", 854_062_500, 6_000_000, 4_155, "21")]
        };

        var plan = InvokeReconcilePowerScanSourceMeasurements(profile, doc.RootElement);
        var measurements = ((System.Collections.IEnumerable)GetProperty(plan, "Measurements")).Cast<object>().ToList();
        var issues = ((System.Collections.IEnumerable)GetProperty(plan, "Issues")).Cast<string>().ToList();

        Assert.Empty(issues);
        Assert.Equal([4162, 4173], measurements.Select(row => (int?)GetProperty(row, "ErrorHz")).ToArray());
    }

    [Fact]
    public void ReconcilePowerScanSourceMeasurements_RejectsConflictingStrongCrystalOffsets()
    {
        using var doc = JsonDocument.Parse("""{"sourceMeasurements":[{"sourceIndex":0,"controlChannelHz":770000000,"gain":"15","sampleRateHz":6000000,"measuredSignalOffsetHz":4500,"snrDb":20,"confidence":1},{"sourceIndex":0,"controlChannelHz":775000000,"gain":"15","sampleRateHz":6000000,"measuredSignalOffsetHz":-2500,"snrDb":18,"confidence":0.95}]}""");
        var profile = new RfSurveyProfileDto
        {
            Sources = [new RfSurveySourceDto(0, "airspy=A0", "A0", "Airspy", 772_000_000, 6_000_000, 3_800, "15")]
        };

        var plan = InvokeReconcilePowerScanSourceMeasurements(profile, doc.RootElement);
        var issues = ((System.Collections.IEnumerable)GetProperty(plan, "Issues")).Cast<string>().ToList();

        Assert.Single(issues);
        Assert.Contains("one SDR crystal", issues[0]);
    }

    [Fact]
    public void AnalyzeIqFile_UsesMedianAcrossCaptureAndCarrierLocalWindow()
    {
        const int sampleRate = 6_000_000;
        const int samplesPerWindow = 4096;
        var path = Path.Combine(Path.GetTempPath(), $"pizzawave-rf-{Guid.NewGuid():N}.cs16");
        try
        {
            var bytes = new List<byte>();
            for (var window = 0; window < 9; window++)
            {
                var offsetHz = window == 0 ? 23_437.5 : 4_394.53125;
                bytes.AddRange(BuildComplexTone(samplesPerWindow, sampleRate, offsetHz, 12_000));
            }
            File.WriteAllBytes(path, bytes.ToArray());

            var analysis = InvokeAnalyzeIqFile(path, sampleRate, true);

            Assert.InRange((double)GetProperty(analysis, "PeakOffsetHz"), 4_000, 4_800);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void FindP25FrameEvidence_ReturnsAuditableMarkerAndRejectsDiscardedFrames()
    {
        Assert.Contains("tsbk", InvokeFindP25FrameEvidence("12:00 tsbk opcode=0x3a"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, InvokeFindP25FrameEvidence("p25_framer error check failed, frame discarded"));
    }

    [Fact]
    public void FindTrControlChannelReadinessLine_RequiresScopedDecodeMeasurement()
    {
        var log = "[other-site] freq: 773.781250 MHz Control Channel Message Decode Rate: 20/sec\n" +
                  "[etv-raymond-hinds] Started with Control Channel: 773.781250 MHz\n" +
                  "[etv-raymond-hinds] freq: 773.781250 MHz Control Channel Message Decode Rate: 0/sec, count: 1";

        var line = InvokeFindTrControlChannelReadinessLine(log, "etv-raymond-hinds", "773.781250");

        Assert.Contains("Decode Rate: 0/sec", line);
    }

    [Fact]
    public void NormalizeRfSurveySources_DerivesAirspySerialFromDevice()
    {
        var normalized = InvokeNormalizeRfSurveySources([new RfSurveySourceDto(0, "airspy=26A464DC28793293", "", "airspy", 773_000_000, 6_000_000, 4_000, "15")]);

        Assert.Equal("26A464DC28793293", normalized[0].Serial);
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

    private static int InvokeP25ProbeSampleRate(RfSurveyProfileDto profile, RfSurveySourceDto source)
    {
        var method = typeof(RfSurveyService).GetMethod("P25ProbeSampleRate", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "P25ProbeSampleRate");
        return (int)(method.Invoke(null, [profile, source])
            ?? throw new InvalidOperationException("P25ProbeSampleRate returned null."));
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

    private static IReadOnlyList<object> InvokeReadPowerScanSourceMeasurements(JsonElement parameters)
    {
        var method = typeof(RfSurveyService).GetMethod("ReadPowerScanSourceMeasurements", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "ReadPowerScanSourceMeasurements");
        var values = method.Invoke(null, [parameters]) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("ReadPowerScanSourceMeasurements returned null.");
        return values.Cast<object>().ToList();
    }

    private static object InvokeReconcilePowerScanSourceMeasurements(RfSurveyProfileDto profile, JsonElement parameters)
    {
        var readMethod = typeof(RfSurveyService).GetMethod("ReadPowerScanSourceMeasurements", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "ReadPowerScanSourceMeasurements");
        var measurements = readMethod.Invoke(null, [parameters])
            ?? throw new InvalidOperationException("ReadPowerScanSourceMeasurements returned null.");
        var method = typeof(RfSurveyService).GetMethod("ReconcilePowerScanSourceMeasurements", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "ReconcilePowerScanSourceMeasurements");
        return method.Invoke(null, [profile, measurements])
            ?? throw new InvalidOperationException("ReconcilePowerScanSourceMeasurements returned null.");
    }

    private static object InvokeAnalyzeIqFile(string path, int sampleRate, bool isAirspy)
    {
        var method = typeof(RfSurveyService).GetMethod("AnalyzeIqFile", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "AnalyzeIqFile");
        return method.Invoke(null, [path, sampleRate, isAirspy])
            ?? throw new InvalidOperationException("AnalyzeIqFile returned null.");
    }

    private static string InvokeFindP25FrameEvidence(string output)
    {
        var method = typeof(RfSurveyService).GetMethod("FindP25FrameEvidence", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "FindP25FrameEvidence");
        return (string)(method.Invoke(null, [output]) ?? string.Empty);
    }

    private static string InvokeFindTrControlChannelReadinessLine(string log, string systemShortName, string frequencyText)
    {
        var method = typeof(RfSurveyService).GetMethod("FindTrControlChannelReadinessLine", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "FindTrControlChannelReadinessLine");
        return (string)(method.Invoke(null, [log, systemShortName, frequencyText]) ?? string.Empty);
    }

    private static IReadOnlyList<RfSurveySourceDto> InvokeNormalizeRfSurveySources(IReadOnlyList<RfSurveySourceDto> sources)
    {
        var method = typeof(RfSurveyService).GetMethod("NormalizeRfSurveySources", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(RfSurveyService).FullName, "NormalizeRfSurveySources");
        return (IReadOnlyList<RfSurveySourceDto>)(method.Invoke(null, [sources])
            ?? throw new InvalidOperationException("NormalizeRfSurveySources returned null."));
    }

    private static byte[] BuildComplexTone(int sampleCount, int sampleRate, double offsetHz, double amplitude)
    {
        var bytes = new byte[sampleCount * 4];
        for (var index = 0; index < sampleCount; index++)
        {
            var phase = 2 * Math.PI * offsetHz * index / sampleRate;
            var i = (short)Math.Round(amplitude * Math.Cos(phase));
            var q = (short)Math.Round(amplitude * Math.Sin(phase));
            BitConverter.GetBytes(i).CopyTo(bytes, index * 4);
            BitConverter.GetBytes(q).CopyTo(bytes, index * 4 + 2);
        }
        return bytes;
    }

    private static object GetProperty(object value, string name) =>
        value.GetType().GetProperty(name)?.GetValue(value)
        ?? throw new MissingMemberException(value.GetType().FullName, name);

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
