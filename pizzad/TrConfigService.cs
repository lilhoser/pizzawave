using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace pizzad;

public sealed class TrConfigService
{
    private readonly EngineConfig _config;
    private readonly ILogger<TrConfigService> _logger;

    public TrConfigService(EngineConfig config, ILogger<TrConfigService> logger)
    {
        _config = config;
        _logger = logger;
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

    public async Task<SetupValidationResult> PatchCallstreamAsync(SetupTrConfigPatchRequest request, CancellationToken ct)
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new SetupValidationResult(false, "TR config file was not found.", new { path });

        JsonNode root;
        try
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) ?? throw new JsonException("empty JSON document");
        }
        catch (Exception ex)
        {
            return new SetupValidationResult(false, "TR config could not be parsed: " + ex.Message, new { path });
        }

        if (root is not JsonObject obj)
            return new SetupValidationResult(false, "TR config root must be a JSON object.", new { path });

        var plugins = obj["plugins"] as JsonArray;
        if (plugins == null)
        {
            plugins = [];
            obj["plugins"] = plugins;
        }

        JsonObject? callstream = null;
        for (var i = 0; i < plugins.Count; i++)
        {
            if (plugins[i] is JsonObject plugin &&
                string.Equals(plugin["name"]?.GetValue<string>(), "callstream", StringComparison.OrdinalIgnoreCase))
            {
                callstream = plugin;
                break;
            }
        }

        if (callstream == null)
        {
            callstream = [];
            plugins.Add(callstream);
        }

        callstream["name"] = "callstream";
        callstream["library"] = callstream["library"]?.GetValue<string>() ?? "libcallstream.so";
        callstream["host"] = _config.Ingest.CallstreamBind;
        callstream["port"] = _config.Ingest.CallstreamPort;

        var helper = FindAdminHelper();
        string backup;
        string restartMessage;
        if (string.IsNullOrWhiteSpace(helper))
        {
            backup = $"{path}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.Copy(path, backup, overwrite: false);
            await File.WriteAllTextAsync(path, NormalizeJson(obj), ct);
            restartMessage = request.RestartTr ? await RestartTrAsync(ct) : "Restart TR when you are ready for the change to take effect.";
        }
        else
        {
            var result = await RunAdminHelperAsync(
                helper,
                "patch-callstream",
                path,
                _config.Ingest.CallstreamBind,
                _config.Ingest.CallstreamPort.ToString(),
                request.RestartTr ? TrUnitName() : string.Empty,
                ct);
            if (result.ExitCode != 0)
                return new SetupValidationResult(false, "Callstream patch failed: " + result.Output.Trim(), new { path });
            backup = result.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(".bak-", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            restartMessage = request.RestartTr ? $"Restarted {TrUnitName()}." : "Restart TR when you are ready for the change to take effect.";
        }
        _config.Setup.CallstreamValidated = true;
        _config.Save();
        _logger.LogInformation("Patched callstream in TR config {Path}; backup {Backup}", path, backup);

        var message = $"Patched callstream config. {restartMessage}";
        return new SetupValidationResult(true, message, new { path, backup, target = $"{_config.Ingest.CallstreamBind}:{_config.Ingest.CallstreamPort}", restarted = request.RestartTr });
    }

    private static string NormalizeJson(JsonNode node) =>
        node.ToJsonString(EngineConfig.JsonOptions()).Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";

    private async Task<string> RestartTrAsync(CancellationToken ct)
    {
        var unit = TrUnitName();
        try
        {
            var helper = FindAdminHelper();
            var psi = string.IsNullOrWhiteSpace(helper)
                ? new ProcessStartInfo("systemctl")
                : new ProcessStartInfo("sudo");
            if (string.IsNullOrWhiteSpace(helper))
            {
                psi.ArgumentList.Add("restart");
                psi.ArgumentList.Add(unit);
            }
            else
            {
                psi.ArgumentList.Add(helper);
                psi.ArgumentList.Add("restart-tr");
                psi.ArgumentList.Add(unit);
            }
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return $"Unable to start systemctl for {unit}; restart TR manually.";
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode == 0)
                return $"Restarted {unit}.";
            return $"systemctl restart {unit} failed: {await stderr}";
        }
        catch (Exception ex)
        {
            return $"systemctl restart {unit} failed: {ex.Message}";
        }
    }

    private string TrUnitName()
    {
        var service = string.IsNullOrWhiteSpace(_config.TrunkRecorder.LogServiceName)
            ? "trunk-recorder"
            : _config.TrunkRecorder.LogServiceName.Trim();
        return service.EndsWith(".service", StringComparison.OrdinalIgnoreCase)
            ? service
            : service + ".service";
    }

    private static string? FindAdminHelper()
    {
        var candidates = new[]
        {
            "/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh",
            "/opt/pizzawave/scripts/pizzawave_setup_admin.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "pizzawave_setup_admin.sh"),
            Path.Combine(AppContext.BaseDirectory, "pizzawave_setup_admin.sh")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string Output)> RunAdminHelperAsync(string helper, string action, string path, string host, string port, string unit, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sudo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(helper);
        psi.ArgumentList.Add(action);
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add(port);
        if (!string.IsNullOrWhiteSpace(unit))
            psi.ArgumentList.Add(unit);
        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "Unable to start sudo helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, (await stdout) + (await stderr));
    }
}
