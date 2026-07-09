using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace pizzad;

public sealed class BackupRestoreService
{
    private const int ManifestVersion = 1;
    private readonly EngineConfig _config;
    private readonly ILogger<BackupRestoreService> _logger;

    public BackupRestoreService(EngineConfig config, ILogger<BackupRestoreService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public IReadOnlyList<BackupArchiveDto> ListBackups()
    {
        var root = BackupRoot();
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, "*.zip")
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new BackupArchiveDto(
                    info.Name,
                    path,
                    info.Length,
                    info.CreationTimeUtc,
                    info.LastWriteTimeUtc);
            })
            .OrderByDescending(row => row.CreatedUtc)
            .ToList();
    }

    public BackupEstimateDto EstimateBackup(BackupCreateRequestDto? request = null)
    {
        var totals = new Dictionary<string, BackupEstimateKindAccumulator>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var options = BackupCreateOptions.From(request);

        AddEstimateFile(totals, warnings, "database", _config.Storage.DatabasePath);
        AddEstimateFile(totals, warnings, "config", _config.ConfigPath);
        AddEstimateFile(totals, warnings, "config", _config.Auth.TokenFile);
        AddEstimateFile(totals, warnings, "tr-config", _config.TrunkRecorder.ConfigPath);
        AddEstimateFile(totals, warnings, "tr-talkgroups", _config.TrunkRecorder.TalkgroupsPath);
        AddEstimateAudioDirectory(totals, warnings, options);
        AddEstimateDirectory(totals, warnings, "appdata", _config.Storage.AppDataRoot, ExcludeAppDataPath);
        AddEstimateDirectory(totals, warnings, "qdrant", _config.Embeddings.QdrantStoragePath);

        var kinds = totals
            .OrderBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .Select(row => new BackupEstimateKindDto(row.Key, row.Value.Bytes, row.Value.FileCount))
            .ToList();
        return new BackupEstimateDto(kinds.Sum(row => row.Bytes), kinds.Sum(row => row.FileCount), kinds, warnings);
    }

    public async Task<BackupCreateResultDto> CreateBackupAsync(BackupCreateRequestDto? request, CancellationToken ct)
    {
        var options = BackupCreateOptions.From(request);
        var backupRoot = BackupRoot();
        Directory.CreateDirectory(backupRoot);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var fileName = $"pizzawave-backup-{SafeName(_config.Branding.StackName)}-{stamp}.zip";
        var path = Path.Combine(backupRoot, fileName);
        var entries = new List<BackupManifestEntryDto>();
        var warnings = new List<string>();
        var tempRoot = Path.Combine(_config.Storage.AppDataRoot, "backup-working", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            await using var file = File.Create(path);
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);

            await AddSnapshotDatabaseAsync(archive, entries, tempRoot, warnings, ct);
            await AddFileIfExistsAsync(archive, entries, "config", _config.ConfigPath, "config/pizzad.json", warnings, ct);
            await AddFileIfExistsAsync(archive, entries, "config", _config.Auth.TokenFile, "config/pizzad.token", warnings, ct);
            await AddFileIfExistsAsync(archive, entries, "tr-config", _config.TrunkRecorder.ConfigPath, "config/trunk-recorder/config.json", warnings, ct);
            await AddFileIfExistsAsync(archive, entries, "tr-talkgroups", _config.TrunkRecorder.TalkgroupsPath, "config/trunk-recorder/talkgroups.csv", warnings, ct);

            await AddAudioDirectoryAsync(archive, entries, options, warnings, ct);
            await AddDirectoryAsync(archive, entries, "appdata", _config.Storage.AppDataRoot, "appdata", warnings, ct, ExcludeAppDataPath);
            await AddDirectoryAsync(archive, entries, "qdrant", _config.Embeddings.QdrantStoragePath, "qdrant", warnings, ct);

            var manifest = new BackupManifestDto(
                ManifestVersion,
                "PizzaWave",
                DateTime.UtcNow,
                Environment.MachineName,
                _config.Branding.StackName,
                _config.ConfigPath,
                _config.Storage.DatabasePath,
                _config.Storage.AudioRoot,
                _config.Storage.AppDataRoot,
                _config.TrunkRecorder.ConfigPath,
                _config.TrunkRecorder.TalkgroupsPath,
                _config.Embeddings.QdrantStoragePath,
                options.AudioStartUtc,
                options.AudioEndUtc,
                entries,
                warnings);
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using (var stream = manifestEntry.Open())
                await JsonSerializer.SerializeAsync(stream, manifest, EngineConfig.JsonOptions(), ct);

            return new BackupCreateResultDto(fileName, path, new FileInfo(path).Length, entries.Count, warnings);
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    public async Task<BackupRestorePreviewDto> StageRestoreAsync(Stream source, string fileName, CancellationToken ct)
    {
        var stageRoot = Path.Combine(_config.Storage.AppDataRoot, "restore-staging", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(stageRoot);
        var archivePath = Path.Combine(stageRoot, string.IsNullOrWhiteSpace(fileName) ? "restore.zip" : Path.GetFileName(fileName));
        await using (var output = File.Create(archivePath))
            await source.CopyToAsync(output, ct);

        var extractRoot = Path.Combine(stageRoot, "extract");
        Directory.CreateDirectory(extractRoot);
        ZipFile.ExtractToDirectory(archivePath, extractRoot);
        var manifestPath = Path.Combine(extractRoot, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("Backup archive does not contain manifest.json.");

        var manifest = JsonSerializer.Deserialize<BackupManifestDto>(await File.ReadAllTextAsync(manifestPath, ct), EngineConfig.JsonOptions())
            ?? throw new InvalidOperationException("Backup manifest could not be read.");
        if (manifest.ManifestVersion != ManifestVersion || !string.Equals(manifest.Product, "PizzaWave", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup archive is not a supported PizzaWave backup.");

        var checks = VerifyManifest(extractRoot, manifest);
        var plan = BuildRestorePlan(extractRoot, manifest);
        var planPath = Path.Combine(stageRoot, "restore-plan.json");
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, EngineConfig.JsonOptions()) + Environment.NewLine, ct);

        _config.Setup.PendingRestorePath = stageRoot;
        _config.Setup.PendingRestoreManifestJson = JsonSerializer.Serialize(manifest, EngineConfig.JsonOptions());
        await SaveConfigAsync(ct);

        return new BackupRestorePreviewDto(stageRoot, manifest, checks);
    }

    public async Task<BackupRestorePreviewDto?> StageLocalRestoreAsync(string name, CancellationToken ct)
    {
        var row = ListBackups().FirstOrDefault(backup => string.Equals(backup.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row == null)
            return null;

        await using var stream = File.Open(row.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await StageRestoreAsync(stream, row.Name, ct);
    }

    public BackupRestorePreviewDto? PendingRestore()
    {
        if (string.IsNullOrWhiteSpace(_config.Setup.PendingRestorePath) || string.IsNullOrWhiteSpace(_config.Setup.PendingRestoreManifestJson))
            return null;
        var manifest = JsonSerializer.Deserialize<BackupManifestDto>(_config.Setup.PendingRestoreManifestJson, EngineConfig.JsonOptions());
        if (manifest == null)
            return null;
        var extractRoot = Path.Combine(_config.Setup.PendingRestorePath, "extract");
        return new BackupRestorePreviewDto(_config.Setup.PendingRestorePath, manifest, Directory.Exists(extractRoot) ? VerifyManifest(extractRoot, manifest) : [new("archive", false, "Restore staging directory is missing.")]);
    }

    public async Task<BackupRestoreCancelResultDto> CancelPendingRestoreAsync(CancellationToken ct)
    {
        var stageRoot = _config.Setup.PendingRestorePath;
        _config.Setup.PendingRestorePath = string.Empty;
        _config.Setup.PendingRestoreManifestJson = string.Empty;
        await SaveConfigAsync(ct);

        if (!string.IsNullOrWhiteSpace(stageRoot) && IsRestoreStagePath(stageRoot) && Directory.Exists(stageRoot))
        {
            try { Directory.Delete(stageRoot, recursive: true); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to delete staged restore directory {StageRoot}", stageRoot);
                return new BackupRestoreCancelResultDto(true, $"Restore was canceled, but staged files could not be deleted: {stageRoot}");
            }
        }

        return new BackupRestoreCancelResultDto(true, "Restore was canceled. No live files were changed.");
    }

    public async Task<BackupRestoreApplyResultDto> ApplyPendingRestoreAsync(CancellationToken ct)
    {
        var stageRoot = _config.Setup.PendingRestorePath;
        if (string.IsNullOrWhiteSpace(stageRoot))
            throw new InvalidOperationException("No backup restore is staged.");
        var planPath = Path.Combine(stageRoot, "restore-plan.json");
        if (!File.Exists(planPath))
            throw new InvalidOperationException("Staged restore plan is missing.");

        if (!OperatingSystem.IsWindows())
        {
            var helper = FindAdminHelper();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sudo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(helper);
            psi.ArgumentList.Add("apply-staged-restore");
            psi.ArgumentList.Add(planPath);
            using var process = System.Diagnostics.Process.Start(psi) ?? throw new InvalidOperationException("Unable to start restore helper.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Restore helper failed with exit code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
            return new BackupRestoreApplyResultDto(true, stdout.Trim());
        }

        var plan = JsonSerializer.Deserialize<BackupRestorePlanDto>(await File.ReadAllTextAsync(planPath, ct), EngineConfig.JsonOptions())
            ?? throw new InvalidOperationException("Restore plan could not be read.");
        foreach (var entry in plan.Entries)
            CopyPlanEntry(entry);
        MarkRestoredConfigApplied(_config.ConfigPath);
        return new BackupRestoreApplyResultDto(true, "Restore files were copied. Restart pizzad before using the restored data.");
    }

    public bool DeleteBackup(string name)
    {
        var row = ListBackups().FirstOrDefault(backup => string.Equals(backup.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row == null)
            return false;
        File.Delete(row.Path);
        return true;
    }

    private async Task AddSnapshotDatabaseAsync(ZipArchive archive, List<BackupManifestEntryDto> entries, string tempRoot, List<string> warnings, CancellationToken ct)
    {
        if (!File.Exists(_config.Storage.DatabasePath))
        {
            warnings.Add($"PizzaWave database was not found: {_config.Storage.DatabasePath}");
            return;
        }
        var snapshot = Path.Combine(tempRoot, "pizzad.db");
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _config.Storage.DatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            var escaped = snapshot.Replace("'", "''");
            await using var command = connection.CreateCommand();
            command.CommandText = $"VACUUM INTO '{escaped}'";
            await command.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to create SQLite snapshot; falling back to direct database copy.");
            warnings.Add("SQLite snapshot failed; backup used a direct database file copy.");
            File.Copy(_config.Storage.DatabasePath, snapshot, overwrite: true);
        }
        await AddFileAsync(archive, entries, "database", snapshot, "database/pizzad.db", _config.Storage.DatabasePath, ct);
    }

    private async Task AddAudioDirectoryAsync(ZipArchive archive, List<BackupManifestEntryDto> entries, BackupCreateOptions options, List<string> warnings, CancellationToken ct)
    {
        var root = _config.Storage.AudioRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            warnings.Add($"audio directory was not found: {root}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists && options.IncludesAudioFile(info)))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file.FullName);
            await AddFileAsync(archive, entries, "audio", file.FullName, Path.Combine("audio", relative).Replace('\\', '/'), Path.Combine(root, relative), ct);
        }
    }

    private async Task AddFileIfExistsAsync(ZipArchive archive, List<BackupManifestEntryDto> entries, string kind, string path, string archivePath, List<string> warnings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            warnings.Add($"{kind} file was not found: {path}");
            return;
        }
        await AddFileAsync(archive, entries, kind, path, archivePath, path, ct);
    }

    private static async Task AddFileAsync(ZipArchive archive, List<BackupManifestEntryDto> entries, string kind, string sourcePath, string archivePath, string targetPath, CancellationToken ct)
    {
        var entry = archive.CreateEntry(archivePath.Replace('\\', '/'), CompressionLevel.Fastest);
        await using (var input = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        await using (var output = entry.Open())
            await input.CopyToAsync(output, ct);
        var info = new FileInfo(sourcePath);
        entries.Add(new BackupManifestEntryDto(kind, targetPath, archivePath.Replace('\\', '/'), info.Length, await Sha256Async(sourcePath, ct)));
    }

    private async Task AddDirectoryAsync(ZipArchive archive, List<BackupManifestEntryDto> entries, string kind, string root, string archiveRoot, List<string> warnings, CancellationToken ct, Func<string, bool>? exclude = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            warnings.Add($"{kind} directory was not found: {root}");
            return;
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (exclude?.Invoke(file) == true)
                continue;
            if (IsSymlink(file))
                continue;
            var relative = Path.GetRelativePath(root, file);
            var archivePath = Path.Combine(archiveRoot, relative).Replace('\\', '/');
            var target = Path.Combine(root, relative);
            await AddFileAsync(archive, entries, kind, file, archivePath, target, ct);
        }
    }

    private void AddEstimateFile(Dictionary<string, BackupEstimateKindAccumulator> totals, List<string> warnings, string kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            warnings.Add($"{kind} file was not found: {path}");
            return;
        }
        AddEstimate(totals, kind, new FileInfo(path).Length);
    }

    private void AddEstimateDirectory(Dictionary<string, BackupEstimateKindAccumulator> totals, List<string> warnings, string kind, string root, Func<string, bool>? exclude = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            warnings.Add($"{kind} directory was not found: {root}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (exclude?.Invoke(file) == true)
                continue;
            if (IsSymlink(file))
                continue;
            var info = new FileInfo(file);
            if (info.Exists)
                AddEstimate(totals, kind, info.Length);
        }
    }

    private void AddEstimateAudioDirectory(Dictionary<string, BackupEstimateKindAccumulator> totals, List<string> warnings, BackupCreateOptions options)
    {
        var root = _config.Storage.AudioRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            warnings.Add($"audio directory was not found: {root}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.Exists && options.IncludesAudioFile(info))
                AddEstimate(totals, "audio", info.Length);
        }
    }

    private static void AddEstimate(Dictionary<string, BackupEstimateKindAccumulator> totals, string kind, long bytes)
    {
        if (!totals.TryGetValue(kind, out var total))
        {
            total = new BackupEstimateKindAccumulator();
            totals[kind] = total;
        }
        total.Bytes += bytes;
        total.FileCount++;
    }

    private bool ExcludeAppDataPath(string path)
    {
        var relative = Path.GetRelativePath(_config.Storage.AppDataRoot, path);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).FirstOrDefault() ?? string.Empty;
        return first.Equals("backups", StringComparison.OrdinalIgnoreCase) ||
               first.Equals("backup-working", StringComparison.OrdinalIgnoreCase) ||
               first.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
               first.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
               first.Equals("restore-staging", StringComparison.OrdinalIgnoreCase) ||
               first.Equals("protected-config", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName);
        }
        catch
        {
            return false;
        }
    }

    private bool IsRestoreStagePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(Path.Combine(_config.Storage.AppDataRoot, "restore-staging"));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<BackupRestoreCheckDto> VerifyManifest(string extractRoot, BackupManifestDto manifest)
    {
        var checks = new List<BackupRestoreCheckDto>();
        foreach (var entry in manifest.Entries)
        {
            var path = SafeExtractPath(extractRoot, entry.ArchivePath);
            if (!File.Exists(path))
            {
                checks.Add(new(entry.ArchivePath, false, "File missing from archive extraction."));
                continue;
            }
            var sizeOk = new FileInfo(path).Length == entry.Bytes;
            var hashOk = string.Equals(Sha256(path), entry.Sha256, StringComparison.OrdinalIgnoreCase);
            checks.Add(new(entry.ArchivePath, sizeOk && hashOk, sizeOk && hashOk ? "OK" : "Size or checksum mismatch."));
        }
        return checks;
    }

    private static BackupRestorePlanDto BuildRestorePlan(string extractRoot, BackupManifestDto manifest)
    {
        var entries = manifest.Entries
            .Select(entry => new BackupRestorePlanEntryDto(
                SafeExtractPath(extractRoot, entry.ArchivePath),
                entry.Path,
                entry.Kind,
                entry.Bytes,
                entry.Sha256))
            .ToList();
        return new BackupRestorePlanDto(DateTime.UtcNow, entries);
    }

    private static void CopyPlanEntry(BackupRestorePlanEntryDto entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(entry.TargetPath) ?? ".");
        if (entry.TargetPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            DeleteSqliteSidecars(entry.TargetPath);
        File.Copy(entry.SourcePath, entry.TargetPath, overwrite: true);
        if (entry.TargetPath.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            DeleteSqliteSidecars(entry.TargetPath);
    }

    private static void DeleteSqliteSidecars(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            try { File.Delete(databasePath + suffix); } catch { }
        }
    }

    private static void MarkRestoredConfigApplied(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            return;
        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = JsonSerializer.Deserialize<Dictionary<string, object?>>(document.RootElement.GetRawText(), EngineConfig.JsonOptions()) ?? new();
        var setup = root.TryGetValue("setup", out var existing)
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(existing, EngineConfig.JsonOptions()), EngineConfig.JsonOptions()) ?? new()
            : new Dictionary<string, object?>();
        setup["restoreAppliedAtUtc"] = DateTime.UtcNow;
        setup["pendingRestorePath"] = string.Empty;
        setup["pendingRestoreManifestJson"] = string.Empty;
        root["setup"] = setup;
        File.WriteAllText(configPath, JsonSerializer.Serialize(root, EngineConfig.JsonOptions()) + Environment.NewLine);
    }

    private async Task SaveConfigAsync(CancellationToken ct)
    {
        _config.ApplyDefaults();
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
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(FindAdminHelper());
            psi.ArgumentList.Add("install-pizzad-config");
            psi.ArgumentList.Add(candidatePath);
            psi.ArgumentList.Add(_config.ConfigPath);

            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start protected config helper.");
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

    private string BackupRoot() => Path.Combine(_config.Storage.AppDataRoot, "backups");

    private static string SafeName(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var safe = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(safe) ? "pizzawave" : safe;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static string SafeExtractPath(string root, string archivePath)
    {
        var full = Path.GetFullPath(Path.Combine(root, archivePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup archive contains an unsafe path.");
        return full;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sha256(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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
        return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("PizzaWave admin helper was not found.");
    }
}

internal sealed class BackupEstimateKindAccumulator
{
    public long Bytes { get; set; }
    public int FileCount { get; set; }
}

public sealed class BackupCreateOptions
{
    public string AudioWindow { get; init; } = "all";
    public DateTime? AudioStartUtc { get; init; }
    public DateTime? AudioEndUtc { get; init; }

    public static BackupCreateOptions From(BackupCreateRequestDto? request)
    {
        var window = NormalizeAudioWindow(request?.AudioWindow);
        var end = DateTime.UtcNow;
        DateTime? start = window switch
        {
            "24h" => end.AddHours(-24),
            "7d" => end.AddDays(-7),
            "30d" => end.AddDays(-30),
            "60d" => end.AddDays(-60),
            _ => null
        };
        return new BackupCreateOptions
        {
            AudioWindow = window,
            AudioStartUtc = start,
            AudioEndUtc = start.HasValue ? end : null
        };
    }

    public bool IncludesAudioFile(FileInfo file)
    {
        if (AudioStartUtc.HasValue && file.LastWriteTimeUtc < AudioStartUtc.Value)
            return false;
        if (AudioEndUtc.HasValue && file.LastWriteTimeUtc > AudioEndUtc.Value)
            return false;
        return true;
    }

    private static string NormalizeAudioWindow(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized is "24h" or "7d" or "30d" or "60d" or "all" ? normalized : "all";
    }
}

public sealed record BackupArchiveDto(string Name, string Path, long Bytes, DateTime CreatedUtc, DateTime ModifiedUtc);
public sealed record BackupCreateRequestDto(string? AudioWindow);
public sealed record BackupEstimateDto(long Bytes, int FileCount, IReadOnlyList<BackupEstimateKindDto> Kinds, IReadOnlyList<string> Warnings);
public sealed record BackupEstimateKindDto(string Kind, long Bytes, int FileCount);
public sealed record BackupCreateResultDto(string Name, string Path, long Bytes, int FileCount, IReadOnlyList<string> Warnings);
public sealed record BackupManifestDto(
    int ManifestVersion,
    string Product,
    DateTime CreatedUtc,
    string Hostname,
    string StackName,
    string PizzadConfigPath,
    string DatabasePath,
    string AudioRoot,
    string AppDataRoot,
    string TrConfigPath,
    string TrTalkgroupsPath,
    string QdrantStoragePath,
    DateTime? AudioStartUtc,
    DateTime? AudioEndUtc,
    IReadOnlyList<BackupManifestEntryDto> Entries,
    IReadOnlyList<string> Warnings);
public sealed record BackupManifestEntryDto(string Kind, string Path, string ArchivePath, long Bytes, string Sha256);
public sealed record BackupRestorePreviewDto(string StagePath, BackupManifestDto Manifest, IReadOnlyList<BackupRestoreCheckDto> Checks);
public sealed record BackupRestoreCheckDto(string Name, bool Ok, string Message);
public sealed record BackupRestorePlanDto(DateTime CreatedUtc, IReadOnlyList<BackupRestorePlanEntryDto> Entries);
public sealed record BackupRestorePlanEntryDto(string SourcePath, string TargetPath, string Kind, long Bytes, string Sha256);
public sealed record BackupRestoreApplyResultDto(bool Scheduled, string Message);
public sealed record BackupRestoreCancelResultDto(bool Canceled, string Message);
