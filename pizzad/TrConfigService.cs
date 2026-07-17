using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace pizzad;

public sealed class TrConfigService
{
    private const long MaxViewerArtifactBytes = 4L * 1024L * 1024L;
    private readonly EngineConfig _config;
    private readonly ILogger<TrConfigService> _logger;
    private readonly TrConfigArtifactProvenanceStore? _provenance;

    public TrConfigService(EngineConfig config, ILogger<TrConfigService> logger, TrConfigArtifactProvenanceStore? provenance = null)
    {
        _config = config;
        _logger = logger;
        _provenance = provenance;
    }

    public async Task<TrConfigEditorDto> GetEditorAsync(CancellationToken ct)
    {
        var livePath = _config.TrunkRecorder.ConfigPath;
        var liveJson = File.Exists(livePath) ? await File.ReadAllTextAsync(livePath, ct) : string.Empty;
        var draftPath = EditorDraftPath();
        var hasDraft = File.Exists(draftPath);
        var configJson = hasDraft ? await File.ReadAllTextAsync(draftPath, ct) : liveJson;
        return BuildEditorDto(livePath, draftPath, configJson, liveJson, hasDraft);
    }

    public async Task<TrConfigViewerDto> GetViewerAsync(IReadOnlyList<RfSurveySessionDto> surveySessions, string? selectedId, CancellationToken ct)
    {
        var artifacts = BuildViewerCatalog(surveySessions);
        var active = artifacts.FirstOrDefault(row => row.IsActive);
        var selected = artifacts.FirstOrDefault(row => string.Equals(row.Id, selectedId, StringComparison.Ordinal)) ?? active ?? artifacts.FirstOrDefault();
        var activeRead = active == null ? (Content: string.Empty, Error: string.Empty) : await ReadViewerArtifactAsync(active, ct);
        var activeJson = activeRead.Content;
        TrConfigArtifactDetailDto? detail = null;
        if (selected != null)
        {
            var selectedRead = selected.IsActive ? activeRead : await ReadViewerArtifactAsync(selected, ct);
            detail = string.IsNullOrWhiteSpace(selectedRead.Error)
                ? BuildViewerDetail(selected, selectedRead.Content)
                : new TrConfigArtifactDetailDto(selected, selectedRead.Content, false, selectedRead.Error, EmptySummary());
        }

        return new TrConfigViewerDto(
            active?.Id ?? string.Empty,
            selected?.Id ?? string.Empty,
            artifacts,
            detail,
            activeJson);
    }

    public async Task<TrConfigEditorDto> SaveEditorDraftAsync(TrConfigEditorSaveRequest request, CancellationToken ct)
    {
        var draftPath = EditorDraftPath();
        Directory.CreateDirectory(Path.GetDirectoryName(draftPath) ?? ".");
        await File.WriteAllTextAsync(draftPath, NormalizeText(request.ConfigJson ?? string.Empty), ct);
        return await GetEditorAsync(ct);
    }

    public Task ClearEditorDraftAsync(CancellationToken ct)
    {
        var path = EditorDraftPath();
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }

    public IReadOnlyList<TrConfigBackupDto> ListConfigBackups()
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path))
            return [];
        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return [];
        return Directory.EnumerateFiles(directory, $"{name}.bak-*")
            .Select(file => new FileInfo(file))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Select(file => new TrConfigBackupDto(file.Name, file.FullName, file.Length, file.LastWriteTimeUtc))
            .ToList();
    }

    public async Task<TrConfigRestoreResultDto> RestoreConfigBackupAsync(TrConfigRestoreRequest request, CancellationToken ct)
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path))
            return new TrConfigRestoreResultDto(false, "TR config path is not configured.", request.BackupPath, "", "");
        if (string.IsNullOrWhiteSpace(request.BackupPath) || !File.Exists(request.BackupPath))
            return new TrConfigRestoreResultDto(false, "Selected TR config backup was not found.", request.BackupPath, "", "");

        var fullConfigPath = Path.GetFullPath(path);
        var configDirectory = Path.GetDirectoryName(fullConfigPath) ?? ".";
        var fullBackupPath = Path.GetFullPath(request.BackupPath);
        if (!fullBackupPath.StartsWith(configDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(fullBackupPath).StartsWith(Path.GetFileName(fullConfigPath) + ".bak-", StringComparison.OrdinalIgnoreCase))
            return new TrConfigRestoreResultDto(false, "Selected file is not a recognized TR config backup.", request.BackupPath, "", "");

        var helper = FindAdminHelper();
        string restoreBackup;
        if (string.IsNullOrWhiteSpace(helper))
        {
            Directory.CreateDirectory(configDirectory);
            restoreBackup = string.Empty;
            if (File.Exists(fullConfigPath))
            {
                restoreBackup = $"{fullConfigPath}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(fullConfigPath, restoreBackup, overwrite: false);
            }
            File.Copy(fullBackupPath, fullConfigPath, overwrite: true);
        }
        else
        {
            var result = await RunAdminHelperAsync(helper, "install-tr-file", fullBackupPath, fullConfigPath, string.Empty, string.Empty, string.Empty, ct);
            if (result.ExitCode != 0)
                return new TrConfigRestoreResultDto(false, "TR config restore failed: " + result.Output.Trim(), fullBackupPath, "", result.Output);
            restoreBackup = result.Output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Contains(".bak-", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        if (_provenance != null && !string.IsNullOrWhiteSpace(restoreBackup))
            await _provenance.RecordAsync(restoreBackup, "TR configuration restore", "Safety backup created before restoring another Trunk Recorder configuration.", Path.GetFileName(fullBackupPath), ct);

        var serviceOutput = request.RestartTr ? await RestartTrAsync(ct) : "Restart TR when you are ready for the restored config to take effect.";
        return new TrConfigRestoreResultDto(true, $"Restored TR config backup {Path.GetFileName(fullBackupPath)}.", fullBackupPath, restoreBackup, serviceOutput);
    }

    public async Task<string> GetEditorConfigForApplyAsync(string? configJson, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configJson))
            return configJson;
        var draftPath = EditorDraftPath();
        if (File.Exists(draftPath))
            return await File.ReadAllTextAsync(draftPath, ct);
        var livePath = _config.TrunkRecorder.ConfigPath;
        return File.Exists(livePath) ? await File.ReadAllTextAsync(livePath, ct) : string.Empty;
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

            var coverage = TrConfigSourceCoverageValidator.Validate(root.GetRawText());
            return new
            {
                ok = coverage.Ok,
                path,
                captureDir,
                logDir,
                systems,
                callstreamConfigured,
                callstreamTarget = $"{_config.Ingest.CallstreamBind}:{_config.Ingest.CallstreamPort}",
                sourceCoverage = coverage,
                error = coverage.Ok ? "" : "TR source coverage failed: " + string.Join(" ", coverage.Blockers)
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, path, error = ex.Message };
        }
    }

    private TrConfigEditorDto BuildEditorDto(string livePath, string draftPath, string configJson, string liveJson, bool hasDraft)
    {
        var summary = EmptySummary();
        var parseOk = false;
        var parseMessage = string.Empty;
        try
        {
            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson) as JsonObject
                ?? throw new JsonException("TR config root must be a JSON object.");
            summary = Summarize(root);
            parseOk = true;
            parseMessage = "Valid JSON.";
        }
        catch (Exception ex)
        {
            parseMessage = ex.Message;
        }

        return new TrConfigEditorDto(livePath, draftPath, configJson, liveJson, hasDraft, parseOk, parseMessage, summary);
    }

    private IReadOnlyList<TrConfigArtifactCatalogDto> BuildViewerCatalog(IReadOnlyList<RfSurveySessionDto> surveySessions)
    {
        var rows = new List<TrConfigArtifactCatalogDto>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var livePath = _config.TrunkRecorder.ConfigPath;
        AddViewerArtifact(rows, seen, livePath, "active", "Active", "Current active configuration", "Trunk Recorder", "Installed configuration currently read by Trunk Recorder.", "", true, true);

        var draftPath = EditorDraftPath();
        AddViewerArtifact(rows, seen, draftPath, "draft", "Draft", "Unapplied configuration draft", "Configuration editor", "Saved draft that has not been applied to Trunk Recorder.", "", true, false);

        if (!string.IsNullOrWhiteSpace(livePath))
        {
            var directory = Path.GetDirectoryName(livePath);
            var liveName = Path.GetFileName(livePath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory, liveName + ".*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(path);
                    AddViewerArtifact(
                        rows,
                        seen,
                        path,
                        "backup",
                        "Backup",
                        FriendlyBackupName(name, liveName),
                        "Legacy or automatic safety backup",
                        "Previous configuration retained before an operator or workflow change. The exact originating workflow was not recorded.",
                        "",
                        false,
                        false);
                }
            }
        }

        foreach (var session in surveySessions)
        {
            if (string.IsNullOrWhiteSpace(session.ArtifactPath) || !Directory.Exists(session.ArtifactPath))
                continue;
            var activity = string.IsNullOrWhiteSpace(session.SiteLabel) ? session.Id : $"{session.SiteLabel} · {session.Id}";
            AddSurveyArtifact(rows, seen, session, "tr-config-before.json", "backup", "Backup", "Configuration before RF workflow", "Configuration captured before the RF workflow changed Trunk Recorder.", activity);
            AddSurveyArtifact(rows, seen, session, "config-draft.json", "experiment", "Experimental", "RF workflow configuration draft", "Draft produced by RF planning; it is not the active configuration.", activity);
            AddSurveyArtifact(rows, seen, session, "tr-config-candidate.json", "experiment", "Experimental", "RF workflow candidate", "Candidate configuration produced for RF validation.", activity);
            AddSurveyArtifacts(rows, seen, session, "tr-config-selected-rf-validation-*.json", "Selected RF validation candidate", "Candidate selected by the RF validation workflow.", activity);
            AddSurveyArtifacts(rows, seen, session, "tr-config-source-apply*.json", "RF source-plan artifact", "Configuration produced while applying an RF source plan.", activity);
        }

        var origins = _provenance?.ReadAll() ?? new Dictionary<string, TrConfigArtifactOrigin>(StringComparer.OrdinalIgnoreCase);
        return rows
            .Select(row => origins.TryGetValue(Path.GetFullPath(row.Path), out var origin)
                ? row with
                {
                    CreatedAtUtc = origin.CreatedAtUtc,
                    Workflow = origin.Workflow,
                    Reason = origin.Reason,
                    RelatedActivity = origin.RelatedActivity,
                    HasRecordedOrigin = true
                }
                : row)
            .OrderBy(row => ViewerKindOrder(row.Kind))
            .ThenByDescending(row => row.CreatedAtUtc)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void AddSurveyArtifact(List<TrConfigArtifactCatalogDto> rows, HashSet<string> seen, RfSurveySessionDto session, string fileName, string kind, string state, string name, string reason, string activity)
    {
        AddViewerArtifact(rows, seen, Path.Combine(session.ArtifactPath, fileName), kind, state, name, $"RF workflow: {session.Status}", reason, activity, true, false);
    }

    private void AddSurveyArtifacts(List<TrConfigArtifactCatalogDto> rows, HashSet<string> seen, RfSurveySessionDto session, string pattern, string name, string reason, string activity)
    {
        foreach (var path in Directory.EnumerateFiles(session.ArtifactPath, pattern, SearchOption.TopDirectoryOnly))
            AddViewerArtifact(rows, seen, path, "experiment", "Experimental", name, $"RF workflow: {session.Status}", reason, activity, true, false);
    }

    private static void AddViewerArtifact(List<TrConfigArtifactCatalogDto> rows, HashSet<string> seen, string path, string kind, string state, string name, string workflow, string reason, string activity, bool hasOrigin, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        var file = new FileInfo(path);
        if (file.Length <= 0 || file.Length > MaxViewerArtifactBytes)
            return;
        var fullPath = file.FullName;
        if (!seen.Add(fullPath))
            return;
        rows.Add(new TrConfigArtifactCatalogDto(
            ViewerArtifactId(fullPath),
            kind,
            state,
            name,
            fullPath,
            file.LastWriteTimeUtc,
            file.Length,
            workflow,
            reason,
            activity,
            hasOrigin,
            isActive));
    }

    private async Task<(string Content, string Error)> ReadViewerArtifactAsync(TrConfigArtifactCatalogDto artifact, CancellationToken ct)
    {
        var file = new FileInfo(artifact.Path);
        if (!file.Exists || file.Length <= 0 || file.Length > MaxViewerArtifactBytes)
            return (string.Empty, "Configuration artifact is missing or outside the viewer size limit.");
        try
        {
            return (await File.ReadAllTextAsync(file.FullName, ct), string.Empty);
        }
        catch (UnauthorizedAccessException) when (!OperatingSystem.IsWindows() && artifact.Path.StartsWith("/etc/trunk-recorder/config.json", StringComparison.Ordinal))
        {
            var helper = FindAdminHelper();
            if (string.IsNullOrWhiteSpace(helper))
                return (string.Empty, "Configuration artifact exists but PizzaWave does not have permission to read it.");
            var result = await RunAdminHelperAsync(helper, "read-tr-config-artifact", artifact.Path, string.Empty, string.Empty, string.Empty, string.Empty, ct);
            return result.ExitCode == 0
                ? (result.Output, string.Empty)
                : (string.Empty, "Configuration artifact could not be read safely: " + result.Output.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (string.Empty, "Configuration artifact could not be read: " + ex.Message);
        }
    }

    private static TrConfigArtifactDetailDto BuildViewerDetail(TrConfigArtifactCatalogDto artifact, string configJson)
    {
        try
        {
            var root = JsonNode.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson) as JsonObject
                ?? throw new JsonException("TR config root must be a JSON object.");
            return new TrConfigArtifactDetailDto(artifact, configJson, true, "Valid JSON.", Summarize(root));
        }
        catch (Exception ex)
        {
            return new TrConfigArtifactDetailDto(artifact, configJson, false, ex.Message, EmptySummary());
        }
    }

    private static string ViewerArtifactId(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return Convert.ToHexString(hash[..12]).ToLowerInvariant();
    }

    private static string FriendlyBackupName(string fileName, string liveName)
    {
        var suffix = fileName.StartsWith(liveName + ".", StringComparison.OrdinalIgnoreCase)
            ? fileName[(liveName.Length + 1)..]
            : fileName;
        suffix = suffix.Replace(".bak", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ').Replace('-', ' ').Trim();
        return string.IsNullOrWhiteSpace(suffix) ? "Configuration backup" : suffix;
    }

    private static int ViewerKindOrder(string kind) => kind switch
    {
        "active" => 0,
        "draft" => 1,
        "backup" => 2,
        "experiment" => 3,
        _ => 4
    };

    private string EditorDraftPath() =>
        Path.Combine(_config.Storage.AppDataRoot, "tr-config-editor-draft.json");

    private static TrConfigEditorSummaryDto EmptySummary() => new([], [], []);

    private static TrConfigEditorSummaryDto Summarize(JsonObject root)
    {
        var systems = new List<TrConfigEditorSystemDto>();
        if (root["systems"] is JsonArray systemArray)
        {
            foreach (var node in systemArray.OfType<JsonObject>())
            {
                systems.Add(new TrConfigEditorSystemDto(
                    ReadString(node, "shortName"),
                    ReadString(node, "type"),
                    ReadString(node, "modulation"),
                    ReadLongArray(node, "control_channels"),
                    ReadLongArray(node, "channels"),
                    ReadString(node, "talkgroupsFile")));
            }
        }

        var sources = new List<TrConfigEditorSourceDto>();
        if (root["sources"] is JsonArray sourceArray)
        {
            var index = 0;
            foreach (var node in sourceArray.OfType<JsonObject>())
            {
                sources.Add(new TrConfigEditorSourceDto(
                    index++,
                    ReadString(node, "device"),
                    ReadString(node, "digitalRecorders"),
                    ReadLong(node, "center"),
                    (int)ReadLong(node, "rate"),
                    (int)ReadLong(node, "error"),
                    ReadRawScalar(node, "gain")));
            }
        }

        var warnings = new List<string>();
        if (systems.Count == 0)
            warnings.Add("No systems are defined.");
        if (sources.Count == 0)
            warnings.Add("No SDR sources are defined.");
        foreach (var system in systems.Where(s => s.ControlChannelsHz.Count == 0))
            warnings.Add($"{system.ShortName}: no control_channels are defined.");

        return new TrConfigEditorSummaryDto(systems, sources, warnings);
    }

    private static string ReadString(JsonObject obj, string name) =>
        obj.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;

    private static string ReadRawScalar(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node == null)
            return string.Empty;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return text;
        return node.ToJsonString();
    }

    private static long ReadLong(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node == null)
            return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
                return longValue;
            if (value.TryGetValue<double>(out var doubleValue))
                return (long)Math.Round(doubleValue);
            if (value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed))
                return parsed;
        }
        return 0;
    }

    private static IReadOnlyList<long> ReadLongArray(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node is not JsonArray array)
            return [];
        return array.Select(item =>
            item is JsonValue value && value.TryGetValue<long>(out var longValue)
                ? longValue
                : item is JsonValue stringValue && stringValue.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed)
                    ? parsed
                    : 0)
            .Where(value => value > 0)
            .ToList();
    }

    private static string NormalizeJson(JsonNode node) =>
        node.ToJsonString(EngineConfig.JsonOptions()).Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";

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

    private async Task SaveConfigAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() || !_config.ConfigPath.StartsWith("/etc/", StringComparison.Ordinal))
        {
            _config.Save();
            return;
        }

        var stagingRoot = Path.Combine(_config.Storage.AppDataRoot, "protected-config");
        Directory.CreateDirectory(stagingRoot);
        var candidatePath = Path.Combine(stagingRoot, $"pizzad-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(candidatePath, JsonSerializer.Serialize(_config, EngineConfig.JsonOptions()) + Environment.NewLine, ct);
        try
        {
            var helper = FindAdminHelper() ?? throw new FileNotFoundException("pizzawave_setup_admin.sh was not found; protected config writes are unavailable.");
            var result = await RunAdminHelperAsync(helper, "install-pizzad-config", candidatePath, _config.ConfigPath, string.Empty, string.Empty, string.Empty, ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Protected config helper failed with exit code {result.ExitCode}: {result.Output.Trim()}");
        }
        finally
        {
            try { File.Delete(candidatePath); } catch { }
        }
    }

    private static async Task<(int ExitCode, string Output)> RunAdminHelperAsync(string helper, string action, string path, string host, string port, string disableCaptureDir, string unit, CancellationToken ct)
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
        psi.ArgumentList.Add(disableCaptureDir);
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
