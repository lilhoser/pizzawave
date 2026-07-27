using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class SupportPackageService
{
    private readonly EngineConfig _config;
    private readonly ILogger<SupportPackageService> _logger;
    private readonly RecoveryOperationCoordinator _recovery;

    public SupportPackageService(EngineConfig config, ILogger<SupportPackageService> logger, RecoveryOperationCoordinator? recovery = null)
    {
        _config = config;
        _logger = logger;
        _recovery = recovery ?? new RecoveryOperationCoordinator();
    }

    public IReadOnlyList<SupportPackageDto> List()
    {
        CleanupExpired();
        var root = Root();
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "pizzawave-support-*.zip")
            .Select(path => new FileInfo(path))
            .Select(info => new SupportPackageDto(info.Name, info.FullName, info.Length, info.CreationTimeUtc, _config.Recovery.SupportPackageCleanupEnabled ? info.CreationTimeUtc.AddDays(_config.Recovery.SupportPackageRetentionDays) : null, ReadManifest(info.FullName)))
            .OrderByDescending(row => row.CreatedUtc)
            .ToList();
    }

    public async Task<SupportPackageCreateResultDto> CreateAsync(SupportPackageCreateRequestDto? request, CancellationToken ct)
    {
        using var recoveryLease = _recovery.Acquire("support package creation");
        request ??= new SupportPackageCreateRequestDto();
        if ((request.IncludeAudio || request.IncludeTranscripts) && !request.PrivacyAcknowledged)
            throw new InvalidOperationException("Audio or transcript inclusion requires explicit privacy acknowledgement.");
        var hours = Math.Clamp(request.Hours, 1, request.IncludeAudio || request.IncludeTranscripts ? 24 : 168);
        var since = DateTime.UtcNow.AddHours(-hours);
        var root = Root();
        Directory.CreateDirectory(root);
        CleanupExpired();
        var name = $"pizzawave-support-{SafeName(_config.Branding.StackName)}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.zip";
        var destination = Path.Combine(root, name);
        var working = Path.Combine(_config.Storage.AppDataRoot, "support-working", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(working);
        var evidence = new List<SupportEvidenceDto>();
        var failures = new List<string>();
        var redactions = 0;

        try
        {
            await WriteRedactedConfigAsync(_config.ConfigPath, Path.Combine(working, "config", "pizzad.redacted.json"), evidence, failures, count => redactions += count, ct);
            await WriteRedactedConfigAsync(_config.TrunkRecorder.ConfigPath, Path.Combine(working, "config", "trunk-recorder.redacted.json"), evidence, failures, count => redactions += count, ct);
            await WriteSystemInfoAsync(Path.Combine(working, "system", "runtime.json"), since, evidence, ct);
            await WriteDatabaseSummaryAsync(Path.Combine(working, "evidence", "database-summary.json"), evidence, failures, ct);
            await WriteOperationalHistoryAsync(Path.Combine(working, "evidence", "operator-and-job-history.json"), since, evidence, failures, ct);
            await CaptureJournalAsync("pizzad", Path.Combine(working, "logs", "pizzad.log"), since, evidence, failures, count => redactions += count, ct);
            await CaptureJournalAsync(_config.TrunkRecorder.LogServiceName, Path.Combine(working, "logs", "trunk-recorder.log"), since, evidence, failures, count => redactions += count, ct);
            if (request.IncludeAudio)
                await CollectAudioAsync(Path.Combine(working, "private-opt-in", "audio"), since, evidence, failures, ct);
            if (request.IncludeTranscripts)
                await CollectTranscriptsAsync(Path.Combine(working, "private-opt-in", "transcripts.json"), since, evidence, failures, ct);

            var unresolved = await ScanForSecretsAsync(working, ct);
            if (unresolved.Count > 0)
                throw new InvalidOperationException("Support package secret scan found unresolved sensitive content: " + string.Join(", ", unresolved));

            var manifest = new SupportPackageManifestDto(
                1,
                "PizzaWave Support Package",
                DateTime.UtcNow,
                since,
                DateTime.UtcNow,
                _config.Branding.StackName,
                evidence,
                new[] { "database files", "Qdrant data", "credentials", "authentication tokens" }
                    .Concat(request.IncludeAudio ? [] : ["call audio"])
                    .Concat(request.IncludeTranscripts ? [] : ["transcript text"])
                    .ToList(),
                new[] { request.IncludeAudio ? "call audio" : null, request.IncludeTranscripts ? "transcript text" : null }.Where(value => value != null).Cast<string>().ToList(),
                redactions,
                failures);
            await WriteJsonAsync(Path.Combine(working, "manifest.json"), manifest, ct);
            ZipFile.CreateFromDirectory(working, destination, CompressionLevel.Fastest, includeBaseDirectory: false);
            return new SupportPackageCreateResultDto(name, destination, new FileInfo(destination).Length, manifest);
        }
        catch
        {
            try { File.Delete(destination); } catch { }
            throw;
        }
        finally
        {
            try { Directory.Delete(working, recursive: true); } catch { }
        }
    }

    public bool Delete(string name)
    {
        var row = List().FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row == null) return false;
        File.Delete(row.Path);
        return true;
    }

    private async Task WriteRedactedConfigAsync(string source, string destination, List<SupportEvidenceDto> evidence, List<string> failures, Action<int> addRedactions, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
        {
            failures.Add($"Configuration source was not found: {source}");
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(source, ct));
            var (value, count) = RedactJson(document.RootElement, null);
            addRedactions(count);
            await WriteJsonAsync(destination, value, ct);
            AddEvidence(evidence, destination, "redacted configuration");
        }
        catch (Exception ex)
        {
            failures.Add($"Could not collect redacted configuration {source}: {ex.Message}");
        }
    }

    private async Task WriteSystemInfoAsync(string destination, DateTime since, List<SupportEvidenceDto> evidence, CancellationToken ct)
    {
        var value = new
        {
            generatedAtUtc = DateTime.UtcNow,
            evidenceStartUtc = since,
            hostname = Environment.MachineName,
            operatingSystem = Environment.OSVersion.ToString(),
            framework = Environment.Version.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            applicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            stackName = _config.Branding.StackName,
            embeddingsEnabled = _config.Embeddings.Enabled,
            transcriptionEnabled = !string.Equals(_config.Transcription.Provider, "none", StringComparison.OrdinalIgnoreCase)
        };
        await WriteJsonAsync(destination, value, ct);
        AddEvidence(evidence, destination, "version and runtime state");
    }

    private async Task WriteDatabaseSummaryAsync(string destination, List<SupportEvidenceDto> evidence, List<string> failures, CancellationToken ct)
    {
        try
        {
            var counts = new SortedDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _config.Storage.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            await using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            var names = new List<string>();
            await using (var reader = await tables.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
            foreach (var table in names)
            {
                await using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\"";
                counts[table] = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            }
            await WriteJsonAsync(destination, new { generatedAtUtc = DateTime.UtcNow, tableCounts = counts }, ct);
            AddEvidence(evidence, destination, "database counts without records or transcript text");
        }
        catch (Exception ex)
        {
            failures.Add("Database summary could not be collected: " + ex.Message);
        }
    }

    private async Task WriteOperationalHistoryAsync(string destination, DateTime since, List<SupportEvidenceDto> evidence, List<string> failures, CancellationToken ct)
    {
        try
        {
            var jobs = new List<object>();
            var setupActions = new List<object>();
            var findingActions = new List<object>();
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _config.Storage.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, type, status, total, completed, failed, message, created_at_utc, updated_at_utc, started_at_utc, finished_at_utc FROM jobs WHERE created_at_utc >= $since ORDER BY id DESC LIMIT 1000";
                command.Parameters.AddWithValue("$since", since.ToString("O"));
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) jobs.Add(new { id = reader.GetInt64(0), type = reader.GetString(1), status = reader.GetString(2), total = reader.GetInt64(3), completed = reader.GetInt64(4), failed = reader.GetInt64(5), message = reader.GetString(6), createdAtUtc = reader.GetString(7), updatedAtUtc = reader.GetString(8), startedAtUtc = reader.IsDBNull(9) ? null : reader.GetString(9), finishedAtUtc = reader.IsDBNull(10) ? null : reader.GetString(10) });
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT timestamp_utc, category, action, summary, monitoring_state, source FROM site_setup_activity WHERE timestamp_utc >= $since ORDER BY id DESC LIMIT 1000";
                command.Parameters.AddWithValue("$since", since.ToString("O"));
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) setupActions.Add(new { timestampUtc = reader.GetString(0), category = reader.GetString(1), action = reader.GetString(2), summary = reader.GetString(3), monitoringState = reader.GetString(4), source = reader.GetString(5) });
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT created_at_utc, event_type, actor, detail FROM recommendation_finding_events WHERE created_at_utc >= $since ORDER BY id DESC LIMIT 1000";
                command.Parameters.AddWithValue("$since", since.ToString("O"));
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) findingActions.Add(new { createdAtUtc = reader.GetString(0), eventType = reader.GetString(1), actor = reader.GetString(2), detail = reader.GetString(3) });
            }
            await WriteJsonAsync(destination, new { generatedAtUtc = DateTime.UtcNow, jobs, setupActions, findingActions }, ct);
            AddEvidence(evidence, destination, "job and operator action history");
        }
        catch (Exception ex)
        {
            failures.Add("Job/operator action history could not be collected: " + ex.Message);
        }
    }

    private async Task CaptureJournalAsync(string service, string destination, DateTime since, List<SupportEvidenceDto> evidence, List<string> failures, Action<int> addRedactions, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(service))
        {
            failures.Add($"Journal evidence for {service} is unavailable on this platform.");
            return;
        }
        try
        {
            var psi = new ProcessStartInfo("journalctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("--unit");
            psi.ArgumentList.Add(service.EndsWith(".service", StringComparison.OrdinalIgnoreCase) ? service : service + ".service");
            psi.ArgumentList.Add("--since");
            psi.ArgumentList.Add(since.ToString("O"));
            psi.ArgumentList.Add("--no-pager");
            psi.ArgumentList.Add("--output=short-iso");
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("journalctl could not be started.");
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
            var (redacted, count) = RedactText(output);
            addRedactions(count);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllTextAsync(destination, redacted, ct);
            AddEvidence(evidence, destination, $"{service} logs");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to collect journal evidence for {Service}", service);
            failures.Add($"Logs for {service} could not be collected: {ex.Message}");
        }
    }

    private async Task CollectAudioAsync(string destination, DateTime since, List<SupportEvidenceDto> evidence, List<string> failures, CancellationToken ct)
    {
        if (!Directory.Exists(_config.Storage.AudioRoot))
        {
            failures.Add("Opted-in call audio directory was not found.");
            return;
        }
        var count = 0;
        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(_config.Storage.AudioRoot, "*", SearchOption.AllDirectories).Select(path => new FileInfo(path)).Where(info => info.LastWriteTimeUtc >= since))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(_config.Storage.AudioRoot, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var output = File.Create(target);
            await input.CopyToAsync(output, ct);
            count++;
            bytes += file.Length;
        }
        evidence.Add(new SupportEvidenceDto("operator-opted-in call audio", $"{count:N0} audio file(s)", bytes));
    }

    private async Task CollectTranscriptsAsync(string destination, DateTime since, List<SupportEvidenceDto> evidence, List<string> failures, CancellationToken ct)
    {
        try
        {
            var rows = new List<object>();
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _config.Storage.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, start_time, system_short_name, talkgroup, talkgroup_name, transcription FROM calls WHERE start_time >= $start AND transcription <> '' ORDER BY start_time DESC LIMIT 5000";
            command.Parameters.AddWithValue("$start", new DateTimeOffset(since).ToUnixTimeSeconds());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(new { id = reader.GetInt64(0), startTime = reader.GetInt64(1), system = reader.GetString(2), talkgroup = reader.GetInt64(3), talkgroupName = reader.GetString(4), transcription = reader.GetString(5) });
            await WriteJsonAsync(destination, new { privacyOptIn = true, boundedTo = 5000, rows }, ct);
            AddEvidence(evidence, destination, "operator-opted-in transcript text");
        }
        catch (Exception ex)
        {
            failures.Add("Opted-in transcripts could not be collected: " + ex.Message);
        }
    }

    private async Task<IReadOnlyList<string>> ScanForSecretsAsync(string root, CancellationToken ct)
    {
        var findings = new List<string>();
        var knownSecrets = new[]
        {
            File.Exists(_config.Auth.TokenFile) ? (await File.ReadAllTextAsync(_config.Auth.TokenFile, ct)).Trim() : string.Empty,
            _config.Embeddings.QdrantApiKey,
            _config.Embeddings.OpenAiApiKey,
            _config.Transcription.OpenAiApiKey,
            _config.AiInsights.OpenAiApiKey,
            _config.Alerts.EmailPassword
        }.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 8).Distinct(StringComparer.Ordinal).ToList();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (Path.GetRelativePath(root, file).StartsWith(Path.Combine("private-opt-in", "audio") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            var text = await File.ReadAllTextAsync(file, ct);
            if (knownSecrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)))
                findings.Add(Path.GetRelativePath(root, file) + " contains a configured secret");
            if (Regex.IsMatch(text, @"(?i)(bearer\s+[a-z0-9._~+/=-]{12,}|api[_-]?key\s*[:=]\s*[a-z0-9._~+/=-]{12,})"))
                findings.Add(Path.GetRelativePath(root, file) + " contains an authentication-shaped value");
        }
        return findings;
    }

    private static (object? Value, int Count) RedactJson(JsonElement element, string? key)
    {
        if (IsSensitiveKey(key)) return ("[REDACTED]", 1);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>();
            var count = 0;
            foreach (var property in element.EnumerateObject())
            {
                var redacted = RedactJson(property.Value, property.Name);
                result[property.Name] = redacted.Value;
                count += redacted.Count;
            }
            return (result, count);
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var values = new List<object?>();
            var count = 0;
            foreach (var item in element.EnumerateArray())
            {
                var redacted = RedactJson(item, key);
                values.Add(redacted.Value);
                count += redacted.Count;
            }
            return (values, count);
        }
        return (element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        }, 0);
    }

    private static bool IsSensitiveKey(string? key) => key != null &&
        (key.Contains("token", StringComparison.OrdinalIgnoreCase) || key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
         key.Contains("password", StringComparison.OrdinalIgnoreCase) || key.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
         key.Contains("apiKey", StringComparison.OrdinalIgnoreCase) || key.Contains("credential", StringComparison.OrdinalIgnoreCase));

    private static (string Text, int Count) RedactText(string text)
    {
        var count = 0;
        var result = Regex.Replace(text, @"(?i)(authorization\s*[:=]\s*bearer\s+|api[_-]?key\s*[:=]\s*|token\s*[:=]\s*)([^\s,;]+)", match =>
        {
            count++;
            return match.Groups[1].Value + "[REDACTED]";
        });
        return (result, count);
    }

    private static async Task WriteJsonAsync(string destination, object? value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(value, EngineConfig.JsonOptions()) + Environment.NewLine, ct);
    }

    private static void AddEvidence(List<SupportEvidenceDto> evidence, string path, string category) =>
        evidence.Add(new SupportEvidenceDto(category, Path.GetFileName(path), new FileInfo(path).Length));

    private void CleanupExpired()
    {
        var root = Root();
        if (!Directory.Exists(root) || !_config.Recovery.SupportPackageCleanupEnabled) return;
        foreach (var path in Directory.EnumerateFiles(root, "pizzawave-support-*.zip"))
        {
            try { if (File.GetCreationTimeUtc(path) < DateTime.UtcNow.AddDays(-_config.Recovery.SupportPackageRetentionDays)) File.Delete(path); }
            catch (Exception ex) { _logger.LogWarning(ex, "Unable to remove expired support package {Path}", path); }
        }
    }

    private string Root() => Path.Combine(_config.Storage.AppDataRoot, "support-packages");
    private static string SafeName(string value) => string.Concat(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
    private static SupportPackageManifestDto? ReadManifest(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("manifest.json");
            if (entry == null) return null;
            using var stream = entry.Open();
            return JsonSerializer.Deserialize<SupportPackageManifestDto>(stream, EngineConfig.JsonOptions());
        }
        catch { return null; }
    }
}

public sealed record SupportPackageCreateRequestDto(int Hours = 24, bool IncludeAudio = false, bool IncludeTranscripts = false, bool PrivacyAcknowledged = false);
public sealed record SupportPackageDto(string Name, string Path, long Bytes, DateTime CreatedUtc, DateTime? ExpiresUtc, SupportPackageManifestDto? Manifest);
public sealed record SupportEvidenceDto(string Category, string Name, long Bytes);
public sealed record SupportPackageManifestDto(int Version, string Product, DateTime CreatedUtc, DateTime WindowStartUtc, DateTime WindowEndUtc, string StackName, IReadOnlyList<SupportEvidenceDto> Evidence, IReadOnlyList<string> Exclusions, IReadOnlyList<string> PrivacyInclusions, int RedactionCount, IReadOnlyList<string> CollectionFailures);
public sealed record SupportPackageCreateResultDto(string Name, string Path, long Bytes, SupportPackageManifestDto Manifest);
