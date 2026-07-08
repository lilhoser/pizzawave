using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed class SetupService
{
    private readonly EngineConfig _config;
    private readonly AuthService _auth;
    private readonly CredentialStore _credentials;
    private readonly TrConfigService _trConfig;
    private readonly SettingsValidationService _settingsValidation;
    private readonly ILogger<SetupService> _logger;
    private readonly SemaphoreSlim _statusCacheLock = new(1, 1);
    private SetupStatusDto? _cachedStatus;
    private DateTime _cachedStatusExpiresUtc = DateTime.MinValue;

    public SetupService(
        EngineConfig config,
        AuthService auth,
        CredentialStore credentials,
        TrConfigService trConfig,
        SettingsValidationService settingsValidation,
        ILogger<SetupService> logger)
    {
        _config = config;
        _auth = auth;
        _credentials = credentials;
        _trConfig = trConfig;
        _settingsValidation = settingsValidation;
        _logger = logger;
    }

    public async Task<SetupStatusDto> GetStatusAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_cachedStatus != null && now < _cachedStatusExpiresUtc)
            return _cachedStatus;

        await _statusCacheLock.WaitAsync(ct);
        try
        {
            now = DateTime.UtcNow;
            if (_cachedStatus != null && now < _cachedStatusExpiresUtc)
                return _cachedStatus;

            var status = await BuildStatusAsync(ct);
            _cachedStatus = status;
            _cachedStatusExpiresUtc = now.AddSeconds(15);
            return status;
        }
        finally
        {
            _statusCacheLock.Release();
        }
    }

    private async Task<SetupStatusDto> BuildStatusAsync(CancellationToken ct)
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
                ingest = _config.Ingest,
                trunkRecorder = _config.TrunkRecorder,
                transcription = _config.Transcription,
                aiInsights = _config.AiInsights,
                embeddings = _config.Embeddings,
                alerts = _credentials.SanitizeAlertsForClient(_config.Alerts),
                locations = _config.Locations,
                profiles = _config.Profiles,
                setup = _config.Setup
            });
    }

    private void InvalidateStatusCache()
    {
        _cachedStatus = null;
        _cachedStatusExpiresUtc = DateTime.MinValue;
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
            talkgroupsExists,
            lmStudio = await DetectLmStudioAsync(ct),
            qdrant = await DetectQdrantAsync(ct)
        };
    }

    private async Task<object> DetectLmStudioAsync(CancellationToken ct)
    {
        const string unit = "lmstudio.service";
        var service = await RunAsync("systemctl", $"is-enabled {unit}", ct);
        var active = await RunAsync("systemctl", $"is-active {unit}", ct);
        var which = await RunAsync("bash", "-lc \"command -v lms || find /home -path '*/.lmstudio/bin/lms' -type f -executable -print -quit 2>/dev/null || true\"", ct);
        var baseUrl = (_config.AiInsights.OpenAiBaseUrl ?? _config.Transcription.OpenAiBaseUrl ?? string.Empty).TrimEnd('/');
        var api = string.IsNullOrWhiteSpace(baseUrl)
            ? (-1, string.Empty, "No LM Studio-compatible base URL is configured.")
            : await RunAsync("bash", $"-lc \"curl -fsS --max-time 2 {ShellQuote(baseUrl + "/models")} >/dev/null 2>&1\"", ct);
        return new
        {
            found = !string.IsNullOrWhiteSpace(which.Stdout) || service.ExitCode == 0,
            binaryPath = which.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty,
            service = unit,
            serviceEnabled = service.ExitCode == 0 && service.Stdout.Contains("enabled", StringComparison.OrdinalIgnoreCase),
            serviceActive = active.ExitCode == 0 && active.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase),
            apiBaseUrl = baseUrl,
            apiReachable = api.Item1 == 0
        };
    }

    private async Task<object> DetectQdrantAsync(CancellationToken ct)
    {
        var unit = UnitName(_config.Embeddings.QdrantServiceName);
        var service = await RunAsync("systemctl", $"is-enabled {unit}", ct);
        var active = await RunAsync("systemctl", $"is-active {unit}", ct);
        var which = await RunAsync("bash", "-lc \"command -v qdrant || true\"", ct);
        var storagePath = _config.Embeddings.QdrantStoragePath;
        return new
        {
            found = !string.IsNullOrWhiteSpace(which.Stdout) || service.ExitCode == 0,
            binaryPath = which.Stdout.Trim(),
            service = unit,
            serviceEnabled = service.ExitCode == 0 && service.Stdout.Contains("enabled", StringComparison.OrdinalIgnoreCase),
            serviceActive = active.ExitCode == 0 && active.Stdout.Contains("active", StringComparison.OrdinalIgnoreCase),
            storagePath,
            storageExists = Directory.Exists(storagePath)
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
        if (root.TryGetProperty("ingest", out var ingest))
            _config.Ingest = ingest.Deserialize<IngestConfig>(EngineConfig.JsonOptions()) ?? _config.Ingest;
        if (root.TryGetProperty("transcription", out var transcription))
            _config.Transcription = transcription.Deserialize<TranscriptionConfig>(EngineConfig.JsonOptions()) ?? _config.Transcription;
        if (root.TryGetProperty("aiInsights", out var ai))
            _config.AiInsights = ai.Deserialize<AiInsightsConfig>(EngineConfig.JsonOptions()) ?? _config.AiInsights;
        if (root.TryGetProperty("embeddings", out var embeddings))
            _config.Embeddings = embeddings.Deserialize<EmbeddingsConfig>(EngineConfig.JsonOptions()) ?? _config.Embeddings;
        if (root.TryGetProperty("alerts", out var alerts))
        {
            var incoming = alerts.Deserialize<AlertConfig>(EngineConfig.JsonOptions()) ?? _config.Alerts;
            _config.Alerts = _credentials.ApplyAlertSecrets(incoming, _config.Alerts, persistSecret: true);
        }
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
                update.EmbeddingsSkippedOrValidated = _config.Setup.EmbeddingsSkippedOrValidated;
                update.AlertsSkippedOrValidated = _config.Setup.AlertsSkippedOrValidated;
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
                await WriteTrFileAsync(_config.TrunkRecorder.TalkgroupsPath, NormalizeText(text), ct);
            }
        }
        if (root.TryGetProperty("trConfigJson", out var trJson) && trJson.ValueKind == JsonValueKind.String)
        {
            var text = trJson.GetString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var _ = JsonDocument.Parse(text);
                var coverage = TrConfigSourceCoverageValidator.Validate(text);
                if (!coverage.Ok)
                    throw new InvalidOperationException("TR config cannot start with the selected source plan: " + string.Join(" ", coverage.Blockers));
                await WriteTrFileAsync(_config.TrunkRecorder.ConfigPath, NormalizeText(text), ct);
            }
        }

        EnsureDefaultProfile();
        await SaveConfigAsync(ct);
        InvalidateStatusCache();
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
            "embeddings" => await ValidateOptionalSettingAsync("embeddings", _config.Embeddings.Enabled, ct),
            "alerts" => await ValidateOptionalSettingAsync("alerts", _config.Alerts.EmailEnabled, ct),
            _ => new SetupValidationResult(false, "Unknown setup section.")
        };

        ApplyValidation(section, result.Ok);
        await SaveConfigAsync(ct);
        InvalidateStatusCache();
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
        _config.Auth.Mode = "token";
        _config.Auth.WriteRequiresAuth = true;
        _config.Auth.ReadRequiresAuth = false;
        _auth.EnsureToken();
        _config.Setup.Completed = true;
        _config.Setup.CompletedAtUtc = DateTime.UtcNow;
        _config.Setup.CurrentStep = "complete";
        _config.Setup.MigrationMode = false;
        _config.Setup.MigrationStartedAtUtc = null;
        _config.Setup.MigrationResetAtUtc = null;
        _config.Setup.MigrationPreviousCompleted = false;
        _config.Setup.MigrationPreviousCurrentStep = string.Empty;
        _config.Setup.RestoreAppliedAtUtc = null;
        await SaveConfigAsync(ct);
        InvalidateStatusCache();
        _logger.LogInformation("PizzaWave setup completed at {CompletedAt}", _config.Setup.CompletedAtUtc);
        return await GetStatusAsync(ct);
    }

    private void RefreshChecksFromDisk(object detection)
    {
        ReconcileMonitoredAreasWithTrSystems();
        _config.Setup.TrDetected = JsonSerializer.Serialize(detection).Contains("\"found\":true", StringComparison.OrdinalIgnoreCase);
        _config.Setup.TrConfigured = ValidateTr().Ok;
        _config.Setup.TalkgroupsValidated = ValidateTalkgroups().Ok;
        _config.Setup.CallstreamValidated = ValidateCallstream().Ok;
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
        new("embeddings", "Vector search/Qdrant skipped or tested", false, _config.Setup.EmbeddingsSkippedOrValidated, "Optional, but recommended when AI incidents are enabled."),
        new("alerts", "Alerts skipped or tested", false, _config.Setup.AlertsSkippedOrValidated, "Optional."),
        new("diagnostic-tools", "Optional RF diagnostic tools skipped or installed", false, _config.Setup.DiagnosticToolsSkippedOrInstalled, "Optional. Installs OP25/P25 and SDR diagnostics used by Setup RF validation."),
        new("calibration", "Setup RF validation handoff acknowledged", false, _config.Setup.CalibrationSkippedOrCompleted, "Optional. First-run hands RF validation to Setup.")
    ];

    private SetupValidationResult ValidateTr()
    {
        var result = _trConfig.Validate();
        var json = JsonSerializer.Serialize(result);
        var ok = json.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase);
        if (ok && File.Exists(_config.TrunkRecorder.ConfigPath))
        {
            try
            {
                var coverage = TrConfigSourceCoverageValidator.Validate(File.ReadAllText(_config.TrunkRecorder.ConfigPath));
                if (!coverage.Ok)
                    return new SetupValidationResult(false, "TR config cannot start with the selected source plan: " + string.Join(" ", coverage.Blockers), coverage);
            }
            catch (Exception ex)
            {
                return new SetupValidationResult(false, "TR config source coverage validation failed: " + ex.Message, result);
            }
        }
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

    private void ReconcileMonitoredAreasWithTrSystems()
    {
        if (_config.Setup.Completed)
            return;
        var systems = ReadTrSystemNames().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var areas = _config.Locations.MonitoredAreas;
        if (systems.Count != 1 || areas.Count != 1)
            return;

        var system = systems[0].Trim();
        var area = areas[0];
        if (AreaMatchesSystem(area, system))
            return;

        var previous = area.SystemShortName?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(previous) && area.Aliases.All(alias => !string.Equals(alias.Trim(), previous, StringComparison.OrdinalIgnoreCase)))
            area.Aliases.Add(previous);
        area.SystemShortName = system;
        if (area.Aliases.All(alias => !string.Equals(alias.Trim(), system, StringComparison.OrdinalIgnoreCase)))
            area.Aliases.Add(system);
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
        var message = TryReadMessage(raw);
        return new SetupValidationResult(ok, ok ? message ?? $"{section} test passed." : message ?? $"{section} test failed.", raw);
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
            case "embeddings": _config.Setup.EmbeddingsSkippedOrValidated = ok; break;
            case "alerts": _config.Setup.AlertsSkippedOrValidated = ok; break;
            case "diagnostic-tools": _config.Setup.DiagnosticToolsSkippedOrInstalled = ok; break;
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

    private async Task WriteTrFileAsync(string path, string contents, CancellationToken ct)
    {
        if (NeedsProtectedTrWrite(path))
        {
            await InstallProtectedTrFileAsync(path, contents, ct);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        await File.WriteAllTextAsync(path, contents, ct);
    }

    private bool NeedsProtectedTrWrite(string path) =>
        !OperatingSystem.IsWindows() && path.StartsWith("/etc/trunk-recorder/", StringComparison.Ordinal);

    private async Task InstallProtectedTrFileAsync(string path, string contents, CancellationToken ct)
    {
        var stagingRoot = Path.Combine(_config.Storage.AppDataRoot, "protected-config");
        Directory.CreateDirectory(stagingRoot);
        var extension = Path.GetExtension(path);
        var candidatePath = Path.Combine(stagingRoot, $"tr-file-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
        await File.WriteAllTextAsync(candidatePath, contents, Encoding.UTF8, ct);
        try
        {
            var helper = FindAdminHelper();
            var result = await RunAdminHelperAsync(helper, "install-tr-file", candidatePath, path, string.Empty, string.Empty, string.Empty, ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Protected TR file helper failed with exit code {result.ExitCode}: {result.Output.Trim()}");
        }
        finally
        {
            try { File.Delete(candidatePath); } catch { }
        }
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
            var helper = FindAdminHelper();
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(helper);
            psi.ArgumentList.Add("install-pizzad-config");
            psi.ArgumentList.Add(candidatePath);
            psi.ArgumentList.Add(_config.ConfigPath);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start sudo helper.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Protected config helper failed with exit code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
        }
        finally
        {
            try { File.Delete(candidatePath); } catch { }
        }
    }

    private static string FindAdminHelper()
    {
        var candidates = new[]
        {
            "/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh",
            "/opt/pizzawave/pizzad/scripts/pizzawave_setup_admin.sh",
            "/opt/pizzawave/scripts/pizzawave_setup_admin.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "pizzawave_setup_admin.sh")
        };
        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("pizzawave_setup_admin.sh was not found; protected config writes are unavailable.");
    }

    private static string UnitName(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "qdrant" : value.Trim();
        return value.EndsWith(".service", StringComparison.OrdinalIgnoreCase) ? value : $"{value}.service";
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static async Task<(int ExitCode, string Output)> RunAdminHelperAsync(string helper, string action, string source, string target, string host, string port, string disableCaptureDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sudo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(helper);
        psi.ArgumentList.Add(action);
        psi.ArgumentList.Add(source);
        psi.ArgumentList.Add(target);
        if (!string.IsNullOrWhiteSpace(host))
            psi.ArgumentList.Add(host);
        if (!string.IsNullOrWhiteSpace(port))
            psi.ArgumentList.Add(port);
        if (!string.IsNullOrWhiteSpace(disableCaptureDir))
            psi.ArgumentList.Add(disableCaptureDir);
        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "Unable to start sudo helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, (await stdout) + (await stderr));
    }

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
