using System.Text.Json;

namespace pizzad;

public sealed record TrConfigArtifactOrigin(
    string Path,
    DateTime CreatedAtUtc,
    string Workflow,
    string Reason,
    string RelatedActivity);

public sealed class TrConfigArtifactProvenanceStore
{
    private readonly EngineConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrConfigArtifactProvenanceStore(EngineConfig config)
    {
        _config = config;
    }

    public IReadOnlyDictionary<string, TrConfigArtifactOrigin> ReadAll()
    {
        var path = StorePath();
        if (!File.Exists(path))
            return new Dictionary<string, TrConfigArtifactOrigin>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = JsonSerializer.Deserialize<List<TrConfigArtifactOrigin>>(File.ReadAllText(path), EngineConfig.JsonOptions()) ?? [];
            return rows
                .Where(row => !string.IsNullOrWhiteSpace(row.Path))
                .GroupBy(row => NormalizePath(row.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.CreatedAtUtc).First(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, TrConfigArtifactOrigin>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task RecordAsync(string? path, string workflow, string reason, string relatedActivity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        await _gate.WaitAsync(ct);
        try
        {
            var rows = ReadAll().Values.ToList();
            var normalized = NormalizePath(path);
            rows.RemoveAll(row => string.Equals(NormalizePath(row.Path), normalized, StringComparison.OrdinalIgnoreCase));
            rows.Add(new TrConfigArtifactOrigin(normalized, DateTime.UtcNow, workflow, reason, relatedActivity));
            var storePath = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(storePath) ?? ".");
            var tempPath = storePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(rows.OrderByDescending(row => row.CreatedAtUtc), EngineConfig.JsonOptions()) + Environment.NewLine, ct);
            File.Move(tempPath, storePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string StorePath() => Path.Combine(_config.Storage.AppDataRoot, "tr-config-artifact-provenance.json");

    private static string NormalizePath(string path) => Path.GetFullPath(path);
}
