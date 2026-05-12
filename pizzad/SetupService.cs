using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed class SetupService
{
    private readonly EngineConfig _config;
    private readonly TrConfigService _trConfig;
    private readonly SettingsValidationService _settingsValidation;
    private readonly ILogger<SetupService> _logger;

    public SetupService(
        EngineConfig config,
        TrConfigService trConfig,
        SettingsValidationService settingsValidation,
        ILogger<SetupService> logger)
    {
        _config = config;
        _trConfig = trConfig;
        _settingsValidation = settingsValidation;
        _logger = logger;
    }

    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var detection = await DetectTrAsync(ct);
        RefreshChecksFromDisk(detection);
        return new SetupStatusDto(
            _config.Setup.Completed,
            _config.Setup.CurrentStep,
            BuildChecks(),
            detection,
            new
            {
                branding = _config.Branding,
                auth = _config.Auth,
                trunkRecorder = _config.TrunkRecorder,
                transcription = _config.Transcription,
                aiInsights = _config.AiInsights,
                alerts = _config.Alerts,
                sftpImport = _config.SftpImport,
                locations = _config.Locations,
                profiles = _config.Profiles
            });
    }

    public async Task<object> DetectTrAsync(CancellationToken ct)
    {
        var service = await RunAsync("systemctl", "is-enabled trunk-recorder.service", ct);
        var active = await RunAsync("systemctl", "is-active trunk-recorder.service", ct);
        var which = await RunAsync("bash", "-lc \"command -v trunk-recorder || true\"", ct);
        var configExists = File.Exists(_config.TrunkRecorder.ConfigPath);
        var talkgroupsExists = File.Exists(_config.TrunkRecorder.TalkgroupsPath);
        return new
        {
            found = configExists || service.Stdout.Contains("enabled", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(which.Stdout),
            binaryPath = which.Stdout.Trim(),
            service = "trunk-recorder.service",
            serviceEnabled = service.ExitCode == 0 && service.Stdout.Contains("enabled", StringComparison.OrdinalIgnoreCase),
            serviceActive = active.ExitCode == 0 && active.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase),
            configPath = _config.TrunkRecorder.ConfigPath,
            configExists,
            talkgroupsPath = _config.TrunkRecorder.TalkgroupsPath,
            talkgroupsExists
        };
    }

    public async Task<SetupStatusDto> SaveAsync(SetupSaveRequest request, CancellationToken ct)
    {
        var json = request.Values.GetRawText();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("branding", out var branding))
            _config.Branding = branding.Deserialize<BrandingConfig>(EngineConfig.JsonOptions()) ?? _config.Branding;
        if (root.TryGetProperty("trunkRecorder", out var tr))
            _config.TrunkRecorder = tr.Deserialize<TrunkRecorderConfig>(EngineConfig.JsonOptions()) ?? _config.TrunkRecorder;
        if (root.TryGetProperty("transcription", out var transcription))
            _config.Transcription = transcription.Deserialize<TranscriptionConfig>(EngineConfig.JsonOptions()) ?? _config.Transcription;
        if (root.TryGetProperty("aiInsights", out var ai))
            _config.AiInsights = ai.Deserialize<AiInsightsConfig>(EngineConfig.JsonOptions()) ?? _config.AiInsights;
        if (root.TryGetProperty("alerts", out var alerts))
            _config.Alerts = alerts.Deserialize<AlertConfig>(EngineConfig.JsonOptions()) ?? _config.Alerts;
        if (root.TryGetProperty("sftpImport", out var sftp))
            _config.SftpImport = sftp.Deserialize<SftpImportConfig>(EngineConfig.JsonOptions()) ?? _config.SftpImport;
        if (root.TryGetProperty("locations", out var locations))
            _config.Locations = locations.Deserialize<LocationConfig>(EngineConfig.JsonOptions()) ?? _config.Locations;
        if (root.TryGetProperty("setup", out var setup))
        {
            var update = setup.Deserialize<SetupConfig>(EngineConfig.JsonOptions());
            if (update != null)
            {
                update.TrDetected = _config.Setup.TrDetected;
                update.TrConfigured = _config.Setup.TrConfigured;
                update.TalkgroupsValidated = _config.Setup.TalkgroupsValidated;
                update.CallstreamValidated = _config.Setup.CallstreamValidated;
                update.TranscriptionValidated = _config.Setup.TranscriptionValidated;
                update.MonitoredAreasValidated = _config.Setup.MonitoredAreasValidated;
                update.HealthValidated = _config.Setup.HealthValidated;
                update.AiInsightsSkippedOrValidated = _config.Setup.AiInsightsSkippedOrValidated;
                update.AlertsSkippedOrValidated = _config.Setup.AlertsSkippedOrValidated;
                update.SftpSkippedOrValidated = _config.Setup.SftpSkippedOrValidated;
                update.CalibrationSkippedOrCompleted = _config.Setup.CalibrationSkippedOrCompleted;
                update.Completed = _config.Setup.Completed;
                update.CompletedAtUtc = _config.Setup.CompletedAtUtc;
                _config.Setup = update;
            }
        }
        if (root.TryGetProperty("talkgroupsCsv", out var tgCsv) && tgCsv.ValueKind == JsonValueKind.String)
        {
            var text = tgCsv.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_config.TrunkRecorder.TalkgroupsPath) ?? ".");
                await File.WriteAllTextAsync(_config.TrunkRecorder.TalkgroupsPath, NormalizeText(text), ct);
            }
        }
        if (root.TryGetProperty("trConfigJson", out var trJson) && trJson.ValueKind == JsonValueKind.String)
        {
            var text = trJson.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var _ = JsonDocument.Parse(text);
                Directory.CreateDirectory(Path.GetDirectoryName(_config.TrunkRecorder.ConfigPath) ?? ".");
                await File.WriteAllTextAsync(_config.TrunkRecorder.ConfigPath, NormalizeText(text), ct);
            }
        }

        EnsureDefaultProfile();
        _config.Save();
        return await GetStatusAsync(ct);
    }

    public async Task<SetupValidationResult> ValidateAsync(string section, CancellationToken ct)
    {
        section = section.Trim().ToLowerInvariant();
        var result = section switch
        {
            "tr" => ValidateTr(),
            "talkgroups" => ValidateTalkgroups(),
            "callstream" => ValidateCallstream(),
            "transcription" => await ValidateTranscriptionAsync(ct),
            "locations" => ValidateLocations(),
            "health" => await ValidateHealthAsync(ct),
            "ai-insights" => await ValidateOptionalSettingAsync("ai-insights", _config.AiInsights.Enabled, ct),
            "alerts" => await ValidateOptionalSettingAsync("alerts", _config.Alerts.EmailEnabled, ct),
            "sftp" => await ValidateOptionalSettingAsync("sftp", _config.SftpImport.Enabled, ct),
            _ => new SetupValidationResult(false, "Unknown setup section.")
        };

        ApplyValidation(section, result.Ok);
        _config.Save();
        return result;
    }

    public async Task<SetupValidationResult> ValidateRequiredAsync(CancellationToken ct)
    {
        var required = new[] { "tr", "talkgroups", "callstream", "transcription", "locations", "health" };
        var failures = new List<string>();
        foreach (var section in required)
        {
            var result = await ValidateAsync(section, ct);
            if (!result.Ok)
                failures.Add($"{section}: {result.Message}");
        }
        return failures.Count == 0
            ? new SetupValidationResult(true, "All required setup sections passed after restart.")
            : new SetupValidationResult(false, "Required setup validation failed: " + string.Join("; ", failures));
    }

    public async Task<SetupStatusDto> CompleteAsync(CancellationToken ct)
    {
        var status = await GetStatusAsync(ct);
        var blocking = status.Checks.Where(c => c.Required && !c.Ok).ToList();
        if (blocking.Count > 0)
            throw new InvalidOperationException("Setup cannot complete: " + string.Join("; ", blocking.Select(b => b.Message)));
        EnsureDefaultProfile();
        _config.Setup.Completed = true;
        _config.Setup.CompletedAtUtc = DateTime.UtcNow;
        _config.Setup.CurrentStep = "complete";
        _config.Save();
        _logger.LogInformation("PizzaWave setup completed at {CompletedAt}", _config.Setup.CompletedAtUtc);
        return await GetStatusAsync(ct);
    }

    private void RefreshChecksFromDisk(object detection)
    {
        _config.Setup.TrDetected = JsonSerializer.Serialize(detection).Contains("\"found\":true", StringComparison.OrdinalIgnoreCase);
        _config.Setup.TrConfigured = ValidateTr().Ok;
        _config.Setup.TalkgroupsValidated = ValidateTalkgroups().Ok;
        _config.Setup.MonitoredAreasValidated = ValidateLocations().Ok;
        _config.Setup.HealthValidated = File.Exists(_config.TrunkRecorder.ConfigPath);
    }

    private IReadOnlyList<SetupCheckDto> BuildChecks() =>
    [
        new("tr", "Trunk Recorder detected/configured", true, _config.Setup.TrDetected && _config.Setup.TrConfigured, "TR must be installed or detected and config must validate."),
        new("talkgroups", "Talkgroup CSV valid", true, _config.Setup.TalkgroupsValidated, "A valid talkgroup CSV is required."),
        new("callstream", "callstream configured", true, _config.Setup.CallstreamValidated, "TR config must load callstream and target pizzad's localhost listener."),
        new("transcription", "Transcription provider tested", true, _config.Setup.TranscriptionValidated, "A transcription provider must pass a test."),
        new("locations", "Monitored areas configured", true, _config.Setup.MonitoredAreasValidated, "Each TR system needs a monitored geographic area."),
        new("health", "TR health access validated", true, _config.Setup.HealthValidated, "pizzad must be able to inspect TR config/logs."),
        new("ai-insights", "AI insights skipped or tested", false, _config.Setup.AiInsightsSkippedOrValidated, "Optional."),
        new("alerts", "Alerts skipped or tested", false, _config.Setup.AlertsSkippedOrValidated, "Optional."),
        new("sftp", "SFTP skipped or tested", false, _config.Setup.SftpSkippedOrValidated, "Optional."),
        new("calibration", "Calibration skipped or completed", false, _config.Setup.CalibrationSkippedOrCompleted, "Optional.")
    ];

    private SetupValidationResult ValidateTr()
    {
        var result = _trConfig.Validate();
        var json = JsonSerializer.Serialize(result);
        var ok = json.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase);
        return new SetupValidationResult(ok, ok ? "TR config validates." : "TR config validation failed.", result);
    }

    private SetupValidationResult ValidateTalkgroups()
    {
        var path = _config.TrunkRecorder.TalkgroupsPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new SetupValidationResult(false, "Talkgroup CSV file was not found.", new { path });

        var lines = File.ReadLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).Take(5000).ToList();
        if (lines.Count < 2)
            return new SetupValidationResult(false, "Talkgroup CSV needs a header and at least one row.", new { path });

        var header = SplitCsvLine(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        var idIndex = FindColumn(header, "decimal", "talkgroup", "tgid", "id");
        var nameIndex = FindColumn(header, "alpha tag", "alpha", "tag", "name", "description");
        var categoryIndex = FindColumn(header, "category", "group", "type");
        if (idIndex < 0 || nameIndex < 0)
            return new SetupValidationResult(false, "CSV must include a talkgroup ID and friendly name column.", new { path, header });

        var valid = 0;
        var missingCategory = 0;
        var duplicates = new HashSet<string>();
        var seen = new HashSet<string>();
        foreach (var line in lines.Skip(1))
        {
            var cols = SplitCsvLine(line);
            if (idIndex >= cols.Count || !long.TryParse(cols[idIndex].Trim(), out _))
                continue;
            if (nameIndex >= cols.Count || string.IsNullOrWhiteSpace(cols[nameIndex]))
                continue;
            valid++;
            if (categoryIndex < 0 || categoryIndex >= cols.Count || string.IsNullOrWhiteSpace(cols[categoryIndex]))
                missingCategory++;
            if (!seen.Add(cols[idIndex].Trim()))
                duplicates.Add(cols[idIndex].Trim());
        }

        if (valid == 0)
            return new SetupValidationResult(false, "No valid talkgroup rows were found.", new { path });
        if (missingCategory > 0)
            return new SetupValidationResult(false, $"{missingCategory} talkgroup row(s) are missing categories.", new { path, valid, missingCategory });
        if (duplicates.Count > 0)
            return new SetupValidationResult(false, $"Duplicate talkgroup IDs found: {string.Join(", ", duplicates.Take(8))}", new { path, valid, duplicates = duplicates.Take(20) });
        return new SetupValidationResult(true, $"Validated {valid:N0} talkgroup row(s).", new { path, valid });
    }

    private SetupValidationResult ValidateCallstream()
    {
        var tr = ValidateTr();
        if (!tr.Ok)
            return tr;
        var json = JsonSerializer.Serialize(tr.Detail);
        var configured = json.Contains("callstreamConfigured", StringComparison.OrdinalIgnoreCase) && json.Contains("true", StringComparison.OrdinalIgnoreCase);
        return configured
            ? new SetupValidationResult(true, "callstream is configured for pizzad ingest.", tr.Detail)
            : new SetupValidationResult(false, "callstream is not configured in TR config.", tr.Detail);
    }

    private async Task<SetupValidationResult> ValidateTranscriptionAsync(CancellationToken ct)
    {
        var raw = await _settingsValidation.TestAsync("transcription", ct);
        var json = JsonSerializer.Serialize(raw);
        var providerDisabled = string.Equals((_config.Transcription.Provider ?? "none").Trim(), "none", StringComparison.OrdinalIgnoreCase);
        var ok = json.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase) && !providerDisabled;
        var message = TryReadMessage(raw) ?? "Transcription test failed.";
        if (providerDisabled)
            message = "Choose a transcription engine before testing.";
        return new SetupValidationResult(ok, ok ? "Transcription test passed." : message, raw);
    }

    private SetupValidationResult ValidateLocations()
    {
        var systems = ReadTrSystemNames().ToList();
        if (systems.Count == 0)
            return new SetupValidationResult(false, "No TR systems were found to map.");
        var missing = systems.Where(system => _config.Locations.MonitoredAreas.All(area => !AreaMatchesSystem(area, system))).ToList();
        var invalid = _config.Locations.MonitoredAreas.Where(a => a.North <= a.South || a.East <= a.West).ToList();
        if (missing.Count > 0)
            return new SetupValidationResult(false, "Missing monitored area mappings for: " + string.Join(", ", missing), new { systems, missing });
        if (invalid.Count > 0)
            return new SetupValidationResult(false, "One or more monitored areas have invalid bounds.", new { invalid = invalid.Select(a => a.AreaLabel) });
        return new SetupValidationResult(true, $"Validated {systems.Count} monitored system area(s).", new { systems, areas = _config.Locations.MonitoredAreas });
    }

    private async Task<SetupValidationResult> ValidateHealthAsync(CancellationToken ct)
    {
        var result = await RunAsync("journalctl", $"-u {_config.TrunkRecorder.LogServiceName} -n 5 --no-pager", ct);
        var ok = result.ExitCode == 0 || File.Exists(_config.TrunkRecorder.ConfigPath);
        return new SetupValidationResult(ok, ok ? "TR health access check completed." : "Unable to read TR service logs.", result);
    }

    private async Task<SetupValidationResult> ValidateOptionalSettingAsync(string section, bool enabled, CancellationToken ct)
    {
        if (!enabled)
            return new SetupValidationResult(true, $"{section} skipped.");
        var raw = await _settingsValidation.TestAsync(section, ct);
        var json = JsonSerializer.Serialize(raw);
        var ok = json.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase);
        return new SetupValidationResult(ok, ok ? $"{section} test passed." : $"{section} test failed.", raw);
    }

    private static string? TryReadMessage(object value)
    {
        try
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
            return document.RootElement.TryGetProperty("message", out var message) ? message.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyValidation(string section, bool ok)
    {
        switch (section)
        {
            case "tr": _config.Setup.TrConfigured = ok; break;
            case "talkgroups": _config.Setup.TalkgroupsValidated = ok; break;
            case "callstream": _config.Setup.CallstreamValidated = ok; break;
            case "transcription": _config.Setup.TranscriptionValidated = ok; break;
            case "locations": _config.Setup.MonitoredAreasValidated = ok; break;
            case "health": _config.Setup.HealthValidated = ok; break;
            case "ai-insights": _config.Setup.AiInsightsSkippedOrValidated = ok; break;
            case "alerts": _config.Setup.AlertsSkippedOrValidated = ok; break;
            case "sftp": _config.Setup.SftpSkippedOrValidated = ok; break;
            case "calibration": _config.Setup.CalibrationSkippedOrCompleted = ok; break;
        }
    }

    private static bool AreaMatchesSystem(MonitoredAreaConfig area, string system)
    {
        var normalizedSystem = NormalizeSystemName(system);
        if (NormalizeSystemName(area.SystemShortName) == normalizedSystem)
            return true;
        return area.Aliases.Any(alias => NormalizeSystemName(alias) == normalizedSystem);
    }

    private static string NormalizeSystemName(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        value = value.Replace("whiteoakmt", "whiteoak", StringComparison.OrdinalIgnoreCase);
        return new string(value.Where(char.IsLetterOrDigit).ToArray());
    }

    private IEnumerable<string> ReadTrSystemNames()
    {
        if (!File.Exists(_config.TrunkRecorder.ConfigPath))
            yield break;
        using var doc = JsonDocument.Parse(File.ReadAllText(_config.TrunkRecorder.ConfigPath));
        if (!doc.RootElement.TryGetProperty("systems", out var systems) || systems.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var system in systems.EnumerateArray())
            if (system.TryGetProperty("shortName", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
                yield return name.GetString()!;
    }

    private void EnsureDefaultProfile()
    {
        if (_config.Profiles.Items.Count == 0)
            _config.Profiles.Items.Add(new ProcessingProfile { Name = "Default" });
        var profile = _config.Profiles.Items[0];
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Default" : profile.Name;
        profile.IncludePolice = profile.IncludeFire = profile.IncludeEMS = profile.IncludeTraffic = profile.IncludeOther = true;
        if (_config.Profiles.ActiveProfileId == Guid.Empty)
            _config.Profiles.ActiveProfileId = profile.Id;
    }

    private static int FindColumn(IReadOnlyList<string> header, params string[] names)
    {
        foreach (var name in names)
        {
            for (var i = 0; i < header.Count; i++)
            {
                var normalized = header[i].Replace("_", " ").Replace("-", " ");
                if (header[i] == name || normalized == name)
                    return i;
            }
        }
        return -1;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cols = new List<string>();
        var sb = new StringBuilder();
        var quote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quote && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quote = !quote;
            }
            else if (c == ',' && !quote)
            {
                cols.Add(sb.ToString());
                sb.Clear();
            }
            else sb.Append(c);
        }
        cols.Add(sb.ToString());
        return cols;
    }

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(psi);
            if (process == null) return (-1, string.Empty, "failed to start process");
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
