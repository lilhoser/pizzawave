using Newtonsoft.Json;

namespace pizzapi;

public sealed class TalkgroupMapping
{
    public long TalkgroupId { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string AlphaTag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool EnabledInTr { get; set; } = true;
    public bool EnabledInPp { get; set; } = true;
    public string OpsCategory { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class TalkgroupMappingStore
{
    private readonly string _path;

    public TalkgroupMappingStore(string? path = null)
    {
        _path = path ?? Path.Combine(pizzalib.Settings.DefaultWorkingDirectory, "talkgroup-mappings.json");
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        // Cleanup legacy draft file from older versions.
        try
        {
            var legacyDraftPath = Path.Combine(
                Path.GetDirectoryName(_path) ?? pizzalib.Settings.DefaultWorkingDirectory,
                "talkgroup-mappings.draft.json");
            if (File.Exists(legacyDraftPath))
                File.Delete(legacyDraftPath);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public List<TalkgroupMapping> LoadAll()
    {
        try
        {
            if (!File.Exists(_path))
                return new List<TalkgroupMapping>();

            var json = File.ReadAllText(_path);
            var rows = JsonConvert.DeserializeObject<List<TalkgroupMapping>>(json);
            return rows ?? new List<TalkgroupMapping>();
        }
        catch
        {
            return new List<TalkgroupMapping>();
        }
    }

    public void SaveAll(List<TalkgroupMapping> rows)
    {
        var payload = JsonConvert.SerializeObject(rows, Formatting.Indented);
        File.WriteAllText(_path, payload);
    }
}
