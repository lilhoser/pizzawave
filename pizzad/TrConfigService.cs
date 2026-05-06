using System.Text.Json;

namespace pizzad;

public sealed class TrConfigService
{
    private readonly EngineConfig _config;

    public TrConfigService(EngineConfig config)
    {
        _config = config;
    }

    public object Validate()
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new { ok = false, path, error = "trunk-recorder config file not found." };

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var captureDir = root.TryGetProperty("captureDir", out var cap) ? cap.GetString() ?? string.Empty : string.Empty;
            var logDir = root.TryGetProperty("logDir", out var log) ? log.GetString() ?? string.Empty : string.Empty;
            var systems = root.TryGetProperty("systems", out var sys) && sys.ValueKind == JsonValueKind.Array
                ? sys.EnumerateArray().Select(s => new
                {
                    shortName = s.TryGetProperty("shortName", out var sn) ? sn.GetString() : null,
                    type = s.TryGetProperty("type", out var type) ? type.GetString() : null,
                    talkgroupsFile = s.TryGetProperty("talkgroupsFile", out var tg) ? tg.GetString() : null
                }).ToList()
                : [];

            var callstreamConfigured = false;
            if (root.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
            {
                foreach (var plugin in plugins.EnumerateArray())
                {
                    if (plugin.TryGetProperty("name", out var name) &&
                        string.Equals(name.GetString(), "callstream", StringComparison.OrdinalIgnoreCase))
                    {
                        callstreamConfigured = true;
                        break;
                    }
                }
            }

            return new
            {
                ok = true,
                path,
                captureDir,
                logDir,
                systems,
                callstreamConfigured,
                callstreamTarget = $"{_config.Ingest.CallstreamBind}:{_config.Ingest.CallstreamPort}"
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, path, error = ex.Message };
        }
    }
}
