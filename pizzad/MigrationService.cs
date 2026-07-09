using System.Diagnostics;
using System.Text.Json;

namespace pizzad;

public sealed class MigrationService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EmbeddingService _embeddings;
    private readonly IngestControlService _ingest;
    private readonly CredentialStore _credentials;
    private readonly AuthService _auth;
    private readonly BackupRestoreService _backups;
    private readonly ILogger<MigrationService> _logger;

    public MigrationService(EngineConfig config, EngineDatabase database, EmbeddingService embeddings, IngestControlService ingest, CredentialStore credentials, AuthService auth, BackupRestoreService backups, ILogger<MigrationService> logger)
    {
        _config = config;
        _database = database;
        _embeddings = embeddings;
        _ingest = ingest;
        _credentials = credentials;
        _auth = auth;
        _backups = backups;
        _logger = logger;
    }

    public async Task<MigrationActionResultDto> BeginAsync(CancellationToken ct)
    {
        if (!_config.Setup.MigrationMode)
        {
            _config.Setup.MigrationPreviousCompleted = _config.Setup.Completed;
            _config.Setup.MigrationPreviousCurrentStep = string.IsNullOrWhiteSpace(_config.Setup.CurrentStep)
                ? (_config.Setup.Completed ? "complete" : "stack")
                : _config.Setup.CurrentStep;
        }
        _config.Setup.Completed = false;
        _config.Setup.CurrentStep = "migration";
        _config.Setup.MigrationMode = true;
        _config.Setup.MigrationStartedAtUtc = DateTime.UtcNow;
        _config.Setup.MigrationResetAtUtc = null;
        _config.Setup.PendingRestorePath = string.Empty;
        _config.Setup.PendingRestoreManifestJson = string.Empty;
        _ingest.Pause(false, "Migration mode started from System > Backup.", 0);
        await SaveConfigAsync(ct);
        return new MigrationActionResultDto(true, "Migration mode started. PizzaWave will open the setup wizard for migration choices.");
    }

    public async Task<MigrationActionResultDto> CancelAsync(CancellationToken ct)
    {
        var resetAlreadyApplied = _config.Setup.MigrationResetAtUtc.HasValue;
        if (resetAlreadyApplied)
            throw new InvalidOperationException("Migration reset has already been applied and cannot be canceled. Restore from a backup or continue setup for the new site.");

        _config.Setup.MigrationMode = false;
        _config.Setup.MigrationStartedAtUtc = null;
        _config.Setup.MigrationResetAtUtc = null;
        _config.Setup.Completed = _config.Setup.MigrationPreviousCompleted;
        _config.Setup.CurrentStep = string.IsNullOrWhiteSpace(_config.Setup.MigrationPreviousCurrentStep)
            ? (_config.Setup.Completed ? "complete" : "stack")
            : _config.Setup.MigrationPreviousCurrentStep;
        _config.Setup.MigrationPreviousCompleted = false;
        _config.Setup.MigrationPreviousCurrentStep = string.Empty;
        if (_config.Setup.Completed)
            _ingest.Resume("Migration mode canceled before reset.", 0);
        await SaveConfigAsync(ct);
        return new MigrationActionResultDto(true, "Migration mode canceled. No site data was cleared.");
    }

    public MigrationProfileDto ExportProfile()
    {
        return new MigrationProfileDto(
            1,
            "PizzaWave Migration Profile",
            DateTime.UtcNow,
            _config.Branding,
            _config.Server,
            _config.Ingest,
            _config.Storage,
            _config.Transcription,
            _config.AiInsights,
            _config.Embeddings,
            _credentials.SanitizeAlertsForExport(_config.Alerts),
            _config.Auth);
    }

    public async Task<MigrationActionResultDto> ImportProfileAsync(MigrationProfileDto profile, CancellationToken ct)
    {
        if (!string.Equals(profile.Product, "PizzaWave Migration Profile", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Migration profile is not a supported PizzaWave migration profile.");

        _config.Branding = profile.Branding;
        _config.Server = profile.Server;
        _config.Ingest = profile.Ingest;
        _config.Storage = profile.Storage;
        _config.Transcription = profile.Transcription;
        _config.AiInsights = profile.AiInsights;
        _config.Embeddings = profile.Embeddings;
        _config.Alerts = _credentials.ApplyAlertSecrets(profile.Alerts, _config.Alerts, persistSecret: true);
        _config.Auth = profile.Auth;
        _config.ApplyDefaults();
        await SaveConfigAsync(ct);
        return new MigrationActionResultDto(true, "Migration profile imported. Review site-specific setup steps before finishing.");
    }

    public async Task<MigrationResetResultDto> ResetForNewSiteAsync(MigrationResetRequestDto? request, CancellationToken ct)
    {
        request ??= new MigrationResetRequestDto();
        if (!_config.Setup.MigrationMode || _config.Setup.MigrationStartedAtUtc == null)
            throw new InvalidOperationException("Begin migration mode before resetting this rig for a new site.");
        if (!string.IsNullOrWhiteSpace(_config.Setup.PendingRestorePath))
            throw new InvalidOperationException("Cancel or apply the pending restore before starting migration reset.");

        var current = _config.Clone();
        var warnings = new List<string>();
        BackupCreateResultDto? backup = null;
        _ingest.Pause(false, "Migration reset for new site.", 0);
        PreflightProtectedSiteFilesForReset();

        if (request.CreateBackup)
            backup = await _backups.CreateBackupAsync(new BackupCreateRequestDto(request.BackupAudioWindow), ct);

        await _database.ClearSiteDataForMigrationAsync(ct);
        DeleteDirectoryContents(_config.Storage.AudioRoot, warnings);

        var qdrant = await _embeddings.DeleteCollectionAsync(ct);
        if (!qdrant.Ok)
            warnings.Add(qdrant.Message);

        DeleteFileIfExists(_config.TrunkRecorder.TalkgroupCatalogPath, warnings);
        await ResetProtectedSiteFilesAsync(ct);

        ApplyResetConfig(current, request);
        _auth.RegenerateToken();
        _config.ApplyDefaults();
        await SaveConfigAsync(ct);

        var message = backup == null
            ? "Site-specific data was cleared. Continue setup with the new trunk-recorder site/frequency/talkgroup configuration."
            : $"Created backup {backup.Name}. Site-specific data was cleared. Continue setup with the new trunk-recorder site/frequency/talkgroup configuration.";
        return new MigrationResetResultDto(true, message, warnings, backup);
    }

    public async Task<SystemResetResultDto> ResetAsync(SystemResetRequestDto? request, CancellationToken ct)
    {
        request ??= new SystemResetRequestDto();
        var presets = request.Presets
            .Select(preset => (preset ?? string.Empty).Trim().ToLowerInvariant())
            .Where(preset => !string.IsNullOrWhiteSpace(preset))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (presets.Count == 0)
            throw new InvalidOperationException("Choose at least one reset preset.");
        if (!string.IsNullOrWhiteSpace(_config.Setup.PendingRestorePath))
            throw new InvalidOperationException("Cancel or apply the pending restore before starting reset.");

        var dataOnly = presets.Contains("data-only");
        var siteReset = presets.Contains("site-reset");
        var fullReset = presets.Contains("full-reset");
        var customReset = presets.Contains("custom");
        var clearOperationalData = dataOnly || siteReset || fullReset || customReset;
        var clearSiteState = siteReset || fullReset;

        var current = _config.Clone();
        var warnings = new List<string>();
        BackupCreateResultDto? backup = null;

        _ingest.Pause(false, $"System reset started: {string.Join(", ", presets)}.", 0);
        if (clearSiteState)
            PreflightProtectedSiteFilesForReset();

        if (request.CreateBackup)
            backup = await _backups.CreateBackupAsync(new BackupCreateRequestDto(request.BackupAudioWindow), ct);

        if (clearOperationalData)
        {
            await _database.ClearOperationalDataAsync(request.PreserveAuditHistory && !clearSiteState, ct);
            DeleteDirectoryContents(_config.Storage.AudioRoot, warnings);
            var qdrant = await _embeddings.DeleteCollectionAsync(ct);
            if (!qdrant.Ok)
                warnings.Add(qdrant.Message);
        }

        if (clearSiteState)
        {
            DeleteFileIfExists(_config.TrunkRecorder.TalkgroupCatalogPath, warnings);
            await ResetProtectedSiteFilesAsync(ct);
            ApplyResetConfig(current, request.ToMigrationResetRequest(fullReset));
            _config.Setup.MigrationMode = false;
            _config.Setup.MigrationStartedAtUtc = null;
            _config.Setup.MigrationResetAtUtc = null;
            _config.Setup.MigrationPreviousCompleted = false;
            _config.Setup.MigrationPreviousCurrentStep = string.Empty;
            _config.Setup.RestoreAppliedAtUtc = null;
            _config.Setup.PendingRestorePath = string.Empty;
            _config.Setup.PendingRestoreManifestJson = string.Empty;
            _config.Setup.CurrentStep = fullReset ? "tr" : "complete";
            _config.Setup.Completed = !fullReset;
            _config.Setup.CompletedAtUtc = fullReset ? null : DateTime.UtcNow;
        }

        if (fullReset)
            _auth.RegenerateToken();

        _config.ApplyDefaults();
        await SaveConfigAsync(ct);

        var message = fullReset
            ? "Full reset complete. First-run prerequisites must be completed before returning to normal operation."
            : clearSiteState
                ? "Site reset complete. Open Setup to choose location, systems, talkgroups, RF path, and TR source planning."
                : "Data reset complete. Current site/configuration was preserved.";
        return new SystemResetResultDto(true, message, warnings, backup, fullReset ? "first-run" : clearSiteState ? "setup" : "backup");
    }

    private void ApplyResetConfig(EngineConfig current, MigrationResetRequestDto request)
    {
        _config.Branding = request.PreserveBranding ? CloneSection(current.Branding) : new BrandingConfig();
        _config.Server = CloneSection(current.Server);
        _config.Auth = CloneSection(current.Auth);
        _config.Storage = CloneSection(current.Storage);
        _config.Ingest = CloneSection(current.Ingest);
        _config.Transcription = request.PreserveTranscription ? CloneSection(current.Transcription) : new TranscriptionConfig();
        _config.Transcription.DeferredTalkgroups.Clear();
        _config.AiInsights = request.PreserveAiInsights ? CloneSection(current.AiInsights) : new AiInsightsConfig();
        _config.Embeddings = request.PreserveEmbeddings ? CloneSection(current.Embeddings) : new EmbeddingsConfig();
        _config.TrunkRecorder = new TrunkRecorderConfig
        {
            ConfigPath = current.TrunkRecorder.ConfigPath,
            TalkgroupsPath = current.TrunkRecorder.TalkgroupsPath,
            TalkgroupCatalogPath = current.TrunkRecorder.TalkgroupCatalogPath,
            LogServiceName = current.TrunkRecorder.LogServiceName,
            HealthWindowMinutes = current.TrunkRecorder.HealthWindowMinutes
        };
        _config.RfSurvey = request.PreserveRfDiagnostics && request.PreserveRfSurvey ? CloneSection(current.RfSurvey) : new RfSurveyConfig();
        _config.Alerts = request.PreserveAlerts ? CloneSection(current.Alerts) : new AlertConfig();
        _config.Alerts.Rules.Clear();
        _config.Profiles = new ProfileConfig();
        _config.Locations = new LocationConfig();
        _config.Setup = new SetupConfig
        {
            Completed = false,
            CompletedAtUtc = null,
            CurrentStep = "tr",
            InstallMode = "freshTr",
            TrConfigMode = "radioReference",
            MigrationMode = true,
            MigrationStartedAtUtc = current.Setup.MigrationStartedAtUtc,
            MigrationResetAtUtc = DateTime.UtcNow,
            RestoreAppliedAtUtc = null,
            TrDetected = false,
            TrConfigured = false,
            TalkgroupsValidated = false,
            CallstreamValidated = false,
            TranscriptionValidated = false,
            MonitoredAreasValidated = false,
            HealthValidated = false,
            AiInsightsSkippedOrValidated = !_config.AiInsights.Enabled,
            EmbeddingsSkippedOrValidated = !_config.Embeddings.Enabled,
            AlertsSkippedOrValidated = !_config.Alerts.EmailEnabled,
            DiagnosticToolsSkippedOrInstalled = true,
            CalibrationSkippedOrCompleted = true
        };
    }

    private static T CloneSection<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, EngineConfig.JsonOptions()), EngineConfig.JsonOptions())
        ?? throw new InvalidOperationException($"Unable to clone {typeof(T).Name}.");

    private async Task ResetProtectedSiteFilesAsync(CancellationToken ct)
    {
        if (!NeedsProtectedTrFileReset())
        {
            DeleteFileIfExists(_config.TrunkRecorder.ConfigPath, []);
            DeleteFileIfExists(_config.TrunkRecorder.TalkgroupsPath, []);
            return;
        }

        var helper = FindAdminHelper();
        var psi = new ProcessStartInfo("sudo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(helper);
        psi.ArgumentList.Add("reset-migration-site-files");
        psi.ArgumentList.Add(_config.TrunkRecorder.ConfigPath);
        psi.ArgumentList.Add(_config.TrunkRecorder.TalkgroupsPath);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start sudo helper.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Migration helper failed with exit code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
        _logger.LogInformation("Migration site file reset completed: {Output}", stdout.Trim());
    }

    private void PreflightProtectedSiteFilesForReset()
    {
        if (!NeedsProtectedTrFileReset())
            return;

        _ = FindAdminHelper();
        ValidateProtectedTrPath(_config.TrunkRecorder.ConfigPath);
        ValidateProtectedTrPath(_config.TrunkRecorder.TalkgroupsPath);
    }

    private bool NeedsProtectedTrFileReset() =>
        !OperatingSystem.IsWindows() &&
        (_config.TrunkRecorder.ConfigPath.StartsWith("/etc/", StringComparison.Ordinal) ||
         _config.TrunkRecorder.TalkgroupsPath.StartsWith("/etc/", StringComparison.Ordinal));

    private static void ValidateProtectedTrPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!path.StartsWith("/etc/trunk-recorder/", StringComparison.Ordinal))
            throw new InvalidOperationException($"Migration reset refuses protected TR file outside /etc/trunk-recorder: {path}");
    }

    private static void DeleteDirectoryContents(string path, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.Delete(file); }
            catch (Exception ex) { warnings.Add($"Unable to delete audio file {file}: {ex.Message}"); }
        }
        foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { }
        }
    }

    private static void DeleteFileIfExists(string path, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        try { File.Delete(path); }
        catch (Exception ex) { warnings.Add($"Unable to delete {path}: {ex.Message}"); }
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
            var psi = new ProcessStartInfo("sudo")
            {
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
            ?? throw new FileNotFoundException("pizzawave_setup_admin.sh was not found; protected migration actions are unavailable.");
    }
}

public sealed record MigrationActionResultDto(bool Ok, string Message);
public sealed class MigrationResetRequestDto
{
    public bool CreateBackup { get; set; } = true;
    public string BackupAudioWindow { get; set; } = "all";
    public bool PreserveBranding { get; set; } = true;
    public bool PreserveTranscription { get; set; } = true;
    public bool PreserveAiInsights { get; set; } = true;
    public bool PreserveEmbeddings { get; set; } = true;
    public bool PreserveAlerts { get; set; } = true;
    public bool PreserveRfDiagnostics { get; set; } = true;
    public bool PreserveRfSurvey { get; set; } = true;
}
public sealed record MigrationResetResultDto(bool Ok, string Message, IReadOnlyList<string> Warnings, BackupCreateResultDto? Backup);
public sealed class SystemResetRequestDto
{
    public List<string> Presets { get; set; } = new();
    public bool CreateBackup { get; set; } = true;
    public string BackupAudioWindow { get; set; } = "all";
    public bool PreserveAuditHistory { get; set; } = true;
    public bool PreserveBranding { get; set; } = true;
    public bool PreserveTranscription { get; set; } = true;
    public bool PreserveAiInsights { get; set; } = true;
    public bool PreserveEmbeddings { get; set; } = true;
    public bool PreserveAlerts { get; set; } = true;
    public bool PreserveRfDiagnostics { get; set; } = true;
    public bool PreserveRfSurvey { get; set; } = true;

    public MigrationResetRequestDto ToMigrationResetRequest(bool fullReset) => new()
    {
        CreateBackup = false,
        BackupAudioWindow = BackupAudioWindow,
        PreserveBranding = !fullReset && PreserveBranding,
        PreserveTranscription = !fullReset && PreserveTranscription,
        PreserveAiInsights = !fullReset && PreserveAiInsights,
        PreserveEmbeddings = !fullReset && PreserveEmbeddings,
        PreserveAlerts = !fullReset && PreserveAlerts,
        PreserveRfDiagnostics = !fullReset && PreserveRfDiagnostics,
        PreserveRfSurvey = !fullReset && PreserveRfSurvey
    };
}
public sealed record SystemResetResultDto(bool Ok, string Message, IReadOnlyList<string> Warnings, BackupCreateResultDto? Backup, string NextPage);
public sealed record MigrationProfileDto(
    int Version,
    string Product,
    DateTime CreatedUtc,
    BrandingConfig Branding,
    ServerConfig Server,
    IngestConfig Ingest,
    StorageConfig Storage,
    TranscriptionConfig Transcription,
    AiInsightsConfig AiInsights,
    EmbeddingsConfig Embeddings,
    AlertConfig Alerts,
    AuthConfig Auth);
