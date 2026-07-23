using System.Globalization;
using System.Text.Json;
using pizzad;

internal static class CombinedCapacityReplay
{
    public static async Task RunAsync(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var tracePaths = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--combined-capacity-plan", StringComparison.Ordinal))
                continue;
            if (!argument.StartsWith("--", StringComparison.Ordinal) || ++index >= args.Length)
                throw new ArgumentException("capacity-plan arguments must be --name value pairs");
            if (string.Equals(argument, "--trace", StringComparison.Ordinal))
                tracePaths.Add(Path.GetFullPath(args[index]));
            else
                values[argument] = args[index];
        }

        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required");
        if (tracePaths.Count < 4)
            throw new ArgumentException("at least four --trace values are required: aligned control and mixed cohorts for two systems");
        var outputPath = Path.GetFullPath(Required("--output"));
        if (File.Exists(outputPath))
            throw new InvalidOperationException($"refusing to overwrite existing capacity report '{outputPath}'");

        var options = EngineConfig.JsonOptions();
        var traces = new List<IncidentCapacityTrace>();
        foreach (var path in tracePaths)
        {
            var trace = JsonSerializer.Deserialize<IncidentCapacityTrace>(await File.ReadAllTextAsync(path), options)
                ?? throw new InvalidDataException($"capacity trace '{path}' is empty");
            traces.Add(trace);
        }
        var headroom = values.TryGetValue("--headroom", out var headroomText)
            ? double.Parse(headroomText, CultureInfo.InvariantCulture)
            : 1.5;
        var report = IncidentCombinedCapacityReplayPlanner.Build(
            Required("--replay-id"),
            DateTimeOffset.UtcNow,
            Required("--control-cohort"),
            Required("--mixed-cohort"),
            Required("--replacement-system"),
            headroom,
            traces);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(report, options));
        File.Move(temporaryPath, outputPath);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            report.ReplayId,
            report.ProtocolIdentity,
            report.ContentHash,
            report.ReplacementTokensPerProcessedObservation,
            report.ReplacementObservationsPerRequest,
            report.Scenarios,
            outputPath
        }, options));
    }
}
