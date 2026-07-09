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
    private readonly ILogger<SetupService> _logger;
    private readonly SemaphoreSlim _statusCacheLock = new(1, 1);
    private SetupStatusDto? _cachedStatus;
    private DateTime _cachedStatusExpiresUtc = DateTime.MinValue;

    public SetupService(
        EngineConfig config,
        AuthService auth,
        CredentialStore credentials,
        TrConfigService trConfig,
        ILogger<SetupService> logger)
    {
        _config = config;
        _auth = auth;
        _credentials = credentials;
        _trConfig = trConfig;
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

    public async Task<SetupValidationResult> ValidateRequiredAsync(CancellationToken ct)
    {
        var result = await ValidateTrPrerequisiteAsync(ct);
        _config.Setup.TrConfigured = result.Ok;
        await SaveConfigAsync(ct);
        InvalidateStatusCache();
        return result.Ok
            ? new SetupValidationResult(true, "Required first-run prerequisite checks passed.", result.Detail)
            : new SetupValidationResult(false, "Required first-run prerequisite validation failed: " + result.Message, result.Detail);
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
        _config.Setup.RestoreAppliedAtUtc = null;
        await SaveConfigAsync(ct);
        InvalidateStatusCache();
        _logger.LogInformation("PizzaWave setup completed at {CompletedAt}", _config.Setup.CompletedAtUtc);
        return await GetStatusAsync(ct);
    }

    private void RefreshChecksFromDisk(object detection)
    {
        _config.Setup.TrDetected = JsonSerializer.Serialize(detection).Contains("\"found\":true", StringComparison.OrdinalIgnoreCase);
        _config.Setup.TrConfigured = ValidateTrConfig().Ok;
    }

    private IReadOnlyList<SetupCheckDto> BuildChecks() =>
    [
        new("tr", "Trunk Recorder installed or reusable", true, _config.Setup.TrDetected, "First-run only needs a trunk-recorder binary, service, or reusable config to continue."),
        new("lm-link", "LM Link prepared", false, true, "Optional. AI Insights can be configured later from Settings."),
        new("qdrant", "Qdrant prepared", false, true, "Optional. Embeddings can be configured later from Settings.")
    ];

    private async Task<SetupValidationResult> ValidateTrPrerequisiteAsync(CancellationToken ct)
    {
        var detection = await DetectTrAsync(ct);
        RefreshChecksFromDisk(detection);
        return _config.Setup.TrDetected
            ? new SetupValidationResult(true, "Trunk Recorder prerequisite is available.", detection)
            : new SetupValidationResult(false, "Trunk Recorder was not detected. Reuse an existing install or build/install TR before finishing first-run.", detection);
    }

    private SetupValidationResult ValidateTrConfig()
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
