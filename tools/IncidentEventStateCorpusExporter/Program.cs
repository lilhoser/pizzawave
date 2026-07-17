using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using pizzad;

var arguments = ParseArguments(args);
var databasePath = Require(arguments, "database");
var planPath = Require(arguments, "plan");
var outputRoot = Require(arguments, "output");
var planJson = await File.ReadAllTextAsync(planPath);
var plan = JsonSerializer.Deserialize<CorpusExportPlan>(planJson, EngineConfig.JsonOptions())
           ?? throw new InvalidDataException("The corpus export plan is empty.");
ValidatePlan(plan);

Directory.CreateDirectory(outputRoot);
var reader = new IncidentEventStateCorpusSnapshotReader(databasePath);
foreach (var split in plan.Splits)
{
    var bundles = new List<IncidentEventStateObservationBundle>();
    foreach (var window in split.Windows.OrderBy(window => window.StartUtc))
    {
        var calls = await reader.ListCallsAsync(
            window.StartUtc.ToUnixTimeSeconds(),
            window.EndUtc.ToUnixTimeSeconds(),
            CancellationToken.None);
        bundles.Add(IncidentEventStateCorpusExporter.BuildObservationBundle(
            window.BundleId,
            plan.CreatedAtUtc,
            calls));
    }

    var manifest = new IncidentEventStateCorpusManifest(
        $"{plan.CorpusId}-{split.Name}",
        plan.CorpusVersion,
        plan.CreatedAtUtc,
        split.Windows.Min(window => window.StartUtc.ToUnixTimeSeconds()),
        split.Windows.Max(window => window.EndUtc.ToUnixTimeSeconds()),
        plan.SelectionProtocolIdentity,
        plan.SoftwareVersion);
    var corpus = IncidentEventStateCorpusExporter.Freeze(manifest, bundles);
    var splitRoot = Path.Combine(outputRoot, split.Name);
    Directory.CreateDirectory(splitRoot);
    await File.WriteAllTextAsync(Path.Combine(splitRoot, "corpus.json"), corpus.Json);
    await File.WriteAllTextAsync(Path.Combine(splitRoot, "corpus.sha256"), corpus.ContentHash + Environment.NewLine);

    var audioManifest = bundles
        .SelectMany(bundle => bundle.Observations.Select(observation => new CorpusAudioReference(
            bundle.BundleId,
            observation.ObservationId,
            observation.CallId,
            observation.AudioReference)))
        .ToList();
    var audioJson = JsonSerializer.Serialize(audioManifest, EngineConfig.JsonOptions());
    await File.WriteAllTextAsync(Path.Combine(splitRoot, "audio-manifest.json"), audioJson);
    var audioHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(audioJson)));
    await File.WriteAllTextAsync(Path.Combine(splitRoot, "audio-manifest.sha256"), audioHash + Environment.NewLine);
    Console.WriteLine($"{split.Name}: {bundles.Count} blocks, {audioManifest.Count} calls, corpus {corpus.ContentHash}");
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Arguments must be provided as --name value pairs.");
        parsed[values[index][2..]] = values[index + 1];
    }
    return parsed;
}

static string Require(IReadOnlyDictionary<string, string> arguments, string name) =>
    arguments.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException($"--{name} is required.");

static void ValidatePlan(CorpusExportPlan plan)
{
    if (string.IsNullOrWhiteSpace(plan.CorpusId) ||
        string.IsNullOrWhiteSpace(plan.CorpusVersion) ||
        string.IsNullOrWhiteSpace(plan.SelectionProtocolIdentity) ||
        string.IsNullOrWhiteSpace(plan.SoftwareVersion))
    {
        throw new InvalidDataException("Corpus plan identities are required.");
    }
    if (plan.CreatedAtUtc == default || plan.Splits.Count == 0)
        throw new InvalidDataException("Corpus plan creation time and splits are required.");
    if (plan.Splits.Select(split => split.Name).Distinct(StringComparer.Ordinal).Count() != plan.Splits.Count)
        throw new InvalidDataException("Corpus split names must be unique.");
    foreach (var split in plan.Splits)
    {
        if (string.IsNullOrWhiteSpace(split.Name) || split.Windows.Count == 0)
            throw new InvalidDataException("Every corpus split requires a name and at least one window.");
        if (split.Windows.Select(window => window.BundleId).Distinct(StringComparer.Ordinal).Count() != split.Windows.Count)
            throw new InvalidDataException($"Corpus split '{split.Name}' has duplicate bundle ids.");
        if (split.Windows.Any(window =>
                string.IsNullOrWhiteSpace(window.BundleId) ||
                window.StartUtc == default ||
                window.EndUtc <= window.StartUtc))
        {
            throw new InvalidDataException($"Corpus split '{split.Name}' contains an invalid window.");
        }
    }
}

public sealed record CorpusExportPlan(
    string CorpusId,
    string CorpusVersion,
    DateTimeOffset CreatedAtUtc,
    string SelectionProtocolIdentity,
    string SoftwareVersion,
    IReadOnlyList<CorpusExportSplit> Splits);

public sealed record CorpusExportSplit(
    string Name,
    IReadOnlyList<CorpusExportWindow> Windows);

public sealed record CorpusExportWindow(
    string BundleId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc);

public sealed record CorpusAudioReference(
    string BundleId,
    string ObservationId,
    long? CallId,
    string AudioReference);
