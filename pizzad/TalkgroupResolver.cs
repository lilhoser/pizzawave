using pizzalib;

namespace pizzad;

public sealed record ResolvedTalkgroup(string Label, string Category);

public sealed class TalkgroupResolver
{
    private readonly EngineConfig _config;
    private readonly ILogger<TalkgroupResolver> _logger;
    private readonly object _gate = new();
    private Dictionary<long, Talkgroup> _talkgroups = new();
    private DateTime _loadedAtUtc = DateTime.MinValue;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public TalkgroupResolver(EngineConfig config, ILogger<TalkgroupResolver> logger)
    {
        _config = config;
        _logger = logger;
    }

    public EngineCall Enrich(EngineCall call)
    {
        var resolved = Resolve(call.Talkgroup);
        return call with
        {
            TalkgroupName = string.IsNullOrWhiteSpace(call.TalkgroupName) || call.TalkgroupName.StartsWith("TG ", StringComparison.OrdinalIgnoreCase)
                ? resolved.Label
                : call.TalkgroupName,
            Category = resolved.Category
        };
    }

    public ResolvedTalkgroup Resolve(long talkgroup)
    {
        var rows = GetTalkgroups();
        if (!rows.TryGetValue(talkgroup, out var row))
            return new ResolvedTalkgroup($"TG {talkgroup}", "other");

        var label = FirstNonEmpty(row.AlphaTag, row.Description, row.Tag, $"TG {talkgroup}");
        if (!string.IsNullOrWhiteSpace(row.AlphaTag) && !string.IsNullOrWhiteSpace(row.Description) &&
            !string.Equals(row.AlphaTag, row.Description, StringComparison.OrdinalIgnoreCase))
        {
            label = $"{row.AlphaTag} - {row.Description}";
        }

        return new ResolvedTalkgroup(label, NormalizeCategory(row.Category, row.Tag, row.Description, row.AlphaTag));
    }

    private Dictionary<long, Talkgroup> GetTalkgroups()
    {
        var path = _config.TrunkRecorder.TalkgroupsPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return _talkgroups;

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (_talkgroups.Count > 0 && lastWrite == _lastWriteUtc && DateTime.UtcNow - _loadedAtUtc < TimeSpan.FromMinutes(5))
            return _talkgroups;

        lock (_gate)
        {
            lastWrite = File.GetLastWriteTimeUtc(path);
            if (_talkgroups.Count > 0 && lastWrite == _lastWriteUtc && DateTime.UtcNow - _loadedAtUtc < TimeSpan.FromMinutes(5))
                return _talkgroups;

            try
            {
                _talkgroups = TalkgroupHelper.GetTalkgroupsFromCsv(path)
                    .GroupBy(t => t.Id)
                    .ToDictionary(g => g.Key, g => g.First());
                _lastWriteUtc = lastWrite;
                _loadedAtUtc = DateTime.UtcNow;
                _logger.LogInformation("Loaded {Count} talkgroups from {Path}", _talkgroups.Count, path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load talkgroups from {Path}", path);
                _loadedAtUtc = DateTime.UtcNow;
            }
        }

        return _talkgroups;
    }

    private static string NormalizeCategory(params string?[] values)
    {
        var text = string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v))).ToLowerInvariant();
        if (ContainsAny(text, "police", "sheriff", "law", "pd", "so", "dispatch")) return "police";
        if (ContainsAny(text, "fire", "fd")) return "fire";
        if (ContainsAny(text, "ems", "medical", "medic", "ambulance", "rescue")) return "ems";
        if (ContainsAny(text, "traffic", "dot", "road", "streets", "highway")) return "traffic";
        return "other";
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
}
