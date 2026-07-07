using CsvHelper;
using CsvHelper.Configuration;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class TalkgroupCatalogService
{
    public static readonly string[] Categories = ["police", "fire", "ems", "traffic", "other"];
    private readonly EngineConfig _config;
    private readonly ILogger<TalkgroupCatalogService> _logger;
    private readonly object _gate = new();
    private TalkgroupCatalogDocument? _cached;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public TalkgroupCatalogService(EngineConfig config, ILogger<TalkgroupCatalogService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public TalkgroupCatalogDocument Load()
    {
        var path = CatalogPath();
        if (!File.Exists(path))
            return new TalkgroupCatalogDocument();

        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (_cached != null && lastWrite == _lastWriteUtc)
            return _cached;

        lock (_gate)
        {
            lastWrite = File.GetLastWriteTimeUtc(path);
            if (_cached != null && lastWrite == _lastWriteUtc)
                return _cached;

            try
            {
                var document = JsonSerializer.Deserialize<TalkgroupCatalogDocument>(
                    File.ReadAllText(path),
                    EngineConfig.JsonOptions()) ?? new TalkgroupCatalogDocument();
                _cached = NormalizeDocument(document);
                _lastWriteUtc = lastWrite;
                return _cached;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load talkgroup catalog from {Path}", path);
                return _cached ?? new TalkgroupCatalogDocument();
            }
        }
    }

    public async Task<TalkgroupCatalogSaveResult> SaveAsync(TalkgroupCatalogDocument document, bool generateTrCsv, CancellationToken ct)
    {
        var normalized = NormalizeDocument(document);
        normalized = normalized with { UpdatedAtUtc = DateTime.UtcNow };
        var path = CatalogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string? backupPath = null;
        if (File.Exists(path))
        {
            backupPath = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(path, backupPath, overwrite: false);
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(normalized, EngineConfig.JsonOptions()) + Environment.NewLine, new UTF8Encoding(false), ct);
        lock (_gate)
        {
            _cached = normalized;
            _lastWriteUtc = File.GetLastWriteTimeUtc(path);
        }

        TalkgroupTrCsvResult? csv = null;
        if (generateTrCsv)
            csv = await GenerateTrCsvAsync(normalized, ct);

        return new TalkgroupCatalogSaveResult(normalized.Items.Count, backupPath, csv?.Path ?? string.Empty, csv?.BackupPath, csv != null);
    }

    public async Task<TalkgroupTrCsvResult> GenerateTrCsvAsync(CancellationToken ct) => await GenerateTrCsvAsync(Load(), ct);

    public async Task<TalkgroupTrCsvResult> GenerateTrCsvAsync(TalkgroupCatalogDocument document, CancellationToken ct)
    {
        var normalized = NormalizeDocument(document);
        var path = _config.TrunkRecorder.TalkgroupsPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("trunkRecorder.talkgroupsPath is not configured.");
        var enabledRows = normalized.Items.Where(i => i.Enabled).ToList();
        var backupPath = await WriteTalkgroupCsvAsync(path, enabledRows.OrderBy(i => i.Id).ToList(), ct);

        foreach (var group in enabledRows
            .Where(row => !string.IsNullOrWhiteSpace(row.SystemShortName))
            .GroupBy(row => NormalizeSystemShortName(row.SystemShortName), StringComparer.OrdinalIgnoreCase))
        {
            await WriteTalkgroupCsvAsync(
                TrCsvPathForSystem(path, group.Key),
                group.OrderBy(row => row.Id).ToList(),
                ct);
        }

        return new TalkgroupTrCsvResult(path, backupPath, enabledRows.Count);
    }

    public static string TrCsvPathForSystem(string basePath, string? systemShortName)
    {
        var system = NormalizeSystemShortName(systemShortName);
        if (string.IsNullOrWhiteSpace(system))
            return basePath;
        var directory = Path.GetDirectoryName(basePath);
        var fileName = Path.GetFileNameWithoutExtension(basePath);
        var extension = Path.GetExtension(basePath);
        var scopedName = $"{(string.IsNullOrWhiteSpace(fileName) ? "talkgroups" : fileName)}.{SanitizePathToken(system)}{(string.IsNullOrWhiteSpace(extension) ? ".csv" : extension)}";
        return string.IsNullOrWhiteSpace(directory) ? scopedName : Path.Combine(directory, scopedName);
    }

    private async Task<string?> WriteTalkgroupCsvAsync(string path, IReadOnlyList<TalkgroupCatalogItem> rows, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var protectedWrite = NeedsProtectedTrWrite(path);
        string? backupPath = null;
        if (!protectedWrite && File.Exists(path))
        {
            backupPath = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(path, backupPath, overwrite: false);
        }

        var builder = new StringBuilder();
        builder.AppendLine("Decimal,Hex,Mode,Alpha Tag,Description,Tag,Category");
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",",
                row.Id.ToString(CultureInfo.InvariantCulture),
                row.Id.ToString("X", CultureInfo.InvariantCulture),
                Csv(row.Mode),
                Csv(row.AlphaTag),
                Csv(row.Description),
                Csv(row.Tag),
                Csv(row.OpsCategory)));
        }

        if (protectedWrite)
        {
            backupPath = await InstallProtectedTrFileAsync(path, builder.ToString(), ct);
        }
        else
        {
            await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false), ct);
        }

        return backupPath;
    }

    private bool NeedsProtectedTrWrite(string path) =>
        !OperatingSystem.IsWindows() && path.StartsWith("/etc/trunk-recorder/", StringComparison.Ordinal);

    private async Task<string?> InstallProtectedTrFileAsync(string path, string contents, CancellationToken ct)
    {
        var stagingRoot = Path.Combine(_config.Storage.AppDataRoot, "protected-config");
        Directory.CreateDirectory(stagingRoot);
        var candidatePath = Path.Combine(stagingRoot, $"tr-talkgroups-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(candidatePath, contents, new UTF8Encoding(false), ct);
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
            psi.ArgumentList.Add("install-tr-file");
            psi.ArgumentList.Add(candidatePath);
            psi.ArgumentList.Add(path);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start sudo helper.");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Protected talkgroup helper failed with exit code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
            return stdout.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Contains(".bak-", StringComparison.OrdinalIgnoreCase));
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
            ?? throw new FileNotFoundException("pizzawave_setup_admin.sh was not found; protected TR writes are unavailable.");
    }

    public ResolvedCatalogTalkgroup Resolve(long id) => Resolve(string.Empty, id);

    public ResolvedCatalogTalkgroup Resolve(string? systemShortName, long id)
    {
        var row = FindBestMatch(Load().Items.Where(i => i.Enabled), systemShortName, id);
        return row == null
            ? new ResolvedCatalogTalkgroup(id, $"TG {id}", "other", false)
            : new ResolvedCatalogTalkgroup(id, BuildLabel(row), row.OpsCategory, true, row.IncidentEligible);
    }

    public bool IsIncidentEligible(long id) => IsIncidentEligible(string.Empty, id);

    public bool IsIncidentEligible(string? systemShortName, long id)
    {
        var row = FindBestMatch(Load().Items.Where(i => i.Enabled), systemShortName, id);
        if (row == null)
            return true;

        var profile = _config.Profiles.Items.FirstOrDefault(p => p.Id == _config.Profiles.ActiveProfileId);
        var setting = FindBestMatch(profile?.Talkgroups ?? [], systemShortName, id);
        return setting?.IncidentEligible ?? row.IncidentEligible;
    }

    public IReadOnlyList<TalkgroupOptionDto> ListEnabledOptions() =>
        EffectiveItemsForActiveProfile(Load())
            .Where(i => i.Enabled)
            .OrderBy(i => BuildLabel(i), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Id)
            .Select(i => new TalkgroupOptionDto(ItemKey(i), i.SystemShortName, i.Id, BuildLabel(i), i.OpsCategory))
            .ToList();

    public bool IsGloballyEnabled(string? systemShortName, long id)
    {
        var row = FindBestMatch(Load().Items, systemShortName, id);
        return row?.Enabled ?? true;
    }

    public IReadOnlyList<TalkgroupCatalogItem> EffectiveItemsForActiveProfile(TalkgroupCatalogDocument document)
    {
        var profile = _config.Profiles.Items.FirstOrDefault(p => p.Id == _config.Profiles.ActiveProfileId);
        return EffectiveItemsForProfile(document, profile).ToList();
    }

    public static IEnumerable<TalkgroupCatalogItem> EffectiveItemsForProfile(TalkgroupCatalogDocument document, ProcessingProfile? profile)
    {
        var overrides = (profile?.Talkgroups ?? [])
            .Where(t => t.Id > 0)
            .GroupBy(SettingKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last());
        foreach (var row in document.Items)
        {
            var setting = FirstSettingMatch(overrides, row);
            var enabled = row.Enabled && (setting?.Enabled ?? true);
            var category = NormalizeCategoryValue(string.IsNullOrWhiteSpace(setting?.Category) ? row.OpsCategory : setting!.Category);
            var label = setting?.Label?.Trim() ?? string.Empty;
            yield return row with
            {
                Enabled = enabled,
                AlphaTag = string.IsNullOrWhiteSpace(label) ? row.AlphaTag : label,
                OpsCategory = category,
                IncidentEligible = setting?.IncidentEligible ?? row.IncidentEligible
            };
        }
    }

    public static TalkgroupCatalogPreview PreviewCsv(string text, string? systemShortName = null)
    {
        var rows = ParseCsv(text, systemShortName);
        return BuildPreview(rows, "csv");
    }

    public static TalkgroupCatalogPreview PreviewRadioReferenceHtml(string html, string? systemShortName = null)
    {
        var rows = ParseTalkgroupsFromHtml(html, systemShortName);
        return BuildPreview(rows, "radioreference");
    }

    public static string NormalizeSystemShortName(string? value) =>
        Regex.Replace((value ?? string.Empty).Trim(), @"\s+", "-").ToLowerInvariant();

    public static string CatalogKey(string? systemShortName, long id)
    {
        var system = NormalizeSystemShortName(systemShortName);
        return string.IsNullOrWhiteSpace(system)
            ? id.ToString(CultureInfo.InvariantCulture)
            : $"{system}:{id.ToString(CultureInfo.InvariantCulture)}";
    }

    public static string SystemFromKeyOrValue(string? key, string? systemShortName, long id)
    {
        var system = NormalizeSystemShortName(systemShortName);
        if (!string.IsNullOrWhiteSpace(system))
            return system;
        var normalizedKey = (key ?? string.Empty).Trim().ToLowerInvariant();
        var suffix = $":{id.ToString(CultureInfo.InvariantCulture)}";
        return normalizedKey.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? normalizedKey[..^suffix.Length]
            : string.Empty;
    }

    public static string ItemKey(TalkgroupCatalogItem item) =>
        !string.IsNullOrWhiteSpace(item.Key) ? item.Key.Trim().ToLowerInvariant() : CatalogKey(item.SystemShortName, item.Id);

    public static string SettingKey(ProfileTalkgroupSetting setting) =>
        !string.IsNullOrWhiteSpace(setting.Key) ? setting.Key.Trim().ToLowerInvariant() : CatalogKey(setting.SystemShortName, setting.Id);

    public static string NormalizeOpsCategory(params string?[] values)
    {
        var text = string.Join(" ", values.Where(v => !string.IsNullOrWhiteSpace(v))).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return "other";
        if (text.Contains("fire", StringComparison.OrdinalIgnoreCase) || HasWord(text, "fd"))
            return "fire";
        if (text.Contains("ems", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("medical", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("medic", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ambulance", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("rescue", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("hospital", StringComparison.OrdinalIgnoreCase))
            return "ems";
        if (text.Contains("police", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("sheriff", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("law", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("corrections", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("jail", StringComparison.OrdinalIgnoreCase) ||
            HasWord(text, "pd") ||
            HasWord(text, "so"))
            return "police";
        if (text.Contains("traffic", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("transportation", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("road", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("highway", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("service patrol", StringComparison.OrdinalIgnoreCase) ||
            HasWord(text, "dot") ||
            HasWord(text, "tdot") ||
            HasWord(text, "hwy"))
            return "traffic";
        return Categories.Contains(text) ? text : "other";
    }

    public static string BuildLabel(TalkgroupCatalogItem row)
    {
        var alpha = row.AlphaTag?.Trim() ?? string.Empty;
        var description = row.Description?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(alpha) && !string.IsNullOrWhiteSpace(description) &&
            !string.Equals(alpha, description, StringComparison.OrdinalIgnoreCase))
            return $"{alpha} - {description}";
        return FirstNonEmpty(alpha, description, row.Tag, $"TG {row.Id}");
    }

    private static TalkgroupCatalogPreview BuildPreview(List<TalkgroupCatalogItem> rows, string source)
    {
        var diagnostics = new List<string>();
        var duplicateCount = rows.Count - rows.Select(ItemKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var normalized = rows
            .Where(r => r.Id > 0)
            .GroupBy(ItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Select(r => r with
            {
                OpsCategory = NormalizeCategoryValue(string.IsNullOrWhiteSpace(r.OpsCategory)
                    ? NormalizeOpsCategory(r.SourceCategory, r.Tag, r.AlphaTag, r.Description)
                    : r.OpsCategory),
                Source = string.IsNullOrWhiteSpace(r.Source) ? source : r.Source
            })
            .Select(NormalizeItem)
            .Select(r => r with { IncidentEligible = r.IncidentEligible && DefaultIncidentEligible(r) })
            .OrderBy(r => r.Id)
            .ToList();

        var included = normalized.Where(r => !ShouldExcludeImported(r)).ToList();
        var excluded = normalized.Count - included.Count;
        if (duplicateCount > 0)
            diagnostics.Add($"{duplicateCount:N0} duplicate row(s) collapsed by catalog key.");
        if (excluded > 0)
            diagnostics.Add($"{excluded:N0} encrypted, unknown, deprecated, unused, or unwanted row(s) excluded.");

        var byCategory = included
            .GroupBy(r => r.OpsCategory)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        return new TalkgroupCatalogPreview(
            included,
            normalized.Where(ShouldExcludeImported).ToList(),
            byCategory,
            string.Join(" ", diagnostics.DefaultIfEmpty($"Parsed {included.Count:N0} talkgroup row(s).")));
    }

    private static List<TalkgroupCatalogItem> ParseCsv(string text, string? defaultSystemShortName)
    {
        using var reader = new StringReader(text ?? string.Empty);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            MissingFieldFound = null,
            HeaderValidated = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim
        };
        using var csv = new CsvReader(reader, config);
        if (!csv.Read() || !csv.ReadHeader())
            return [];

        var rows = new List<TalkgroupCatalogItem>();
        while (csv.Read())
        {
            var idRaw = GetField(csv, "decimal", "dec", "id", "tgid");
            var normalizedId = (idRaw ?? string.Empty).Replace(",", string.Empty).Trim();
            if (!long.TryParse(normalizedId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                continue;

            rows.Add(new TalkgroupCatalogItem
            {
                SystemShortName = NullIfEmpty(GetField(csv, "systemshortname", "system short name", "system", "rrsystem", "rr system")) ?? NormalizeSystemShortName(defaultSystemShortName),
                Id = id,
                Mode = NullIfEmpty(GetField(csv, "mode")) ?? "D",
                AlphaTag = NullIfEmpty(GetField(csv, "alphatag", "alpha", "alpha tag")) ?? string.Empty,
                Description = NullIfEmpty(GetField(csv, "description", "desc")) ?? string.Empty,
                Tag = NullIfEmpty(GetField(csv, "tag")) ?? string.Empty,
                SourceCategory = NullIfEmpty(GetField(csv, "sourcecategory", "source category")) ?? NullIfEmpty(GetField(csv, "category", "group", "type")) ?? string.Empty,
                OpsCategory = GetField(csv, "opscategory", "ops category", "pizzawavecategory", "pizza wave category") ?? string.Empty,
                Enabled = ParseEnabled(GetField(csv, "enabled")),
                Source = "csv"
            });
        }
        return rows;
    }

    private static List<TalkgroupCatalogItem> ParseTalkgroupsFromHtml(string html, string? systemShortName)
    {
        var rows = new List<TalkgroupCatalogItem>();
        if (string.IsNullOrWhiteSpace(html))
            return rows;

        var clean = Regex.Replace(html, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", string.Empty, RegexOptions.IgnoreCase);
        var section = ExtractTalkgroupsSectionHtml(clean);
        var seen = new HashSet<long>();
        foreach (Match table in Regex.Matches(section, "<table[^>]*>[\\s\\S]*?</table>", RegexOptions.IgnoreCase))
            ParseTalkgroupTable(table.Value, seen, rows, systemShortName);
        return rows.OrderBy(r => r.Id).ToList();
    }

    private static void ParseTalkgroupTable(string tableHtml, HashSet<long> seen, List<TalkgroupCatalogItem> rows, string? systemShortName)
    {
        var trMatches = Regex.Matches(tableHtml, "<tr[^>]*>(?<row>[\\s\\S]*?)</tr>", RegexOptions.IgnoreCase);
        var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headerIndex = -1;
        for (var i = 0; i < trMatches.Count; i++)
        {
            var th = Regex.Matches(trMatches[i].Groups["row"].Value, "<th\\b(?:\"[^\"]*\"|'[^']*'|[^'\">])*>(?<cell>[\\s\\S]*?)</th>", RegexOptions.IgnoreCase);
            if (th.Count == 0) continue;
            var normalized = th.Cast<Match>().Select(m => NormalizeHeaderToken(NormalizeHtmlText(m.Groups["cell"].Value))).ToList();
            if (!TryGetHeaderIndexes(normalized, headers)) continue;
            headerIndex = i;
            break;
        }
        if (headerIndex < 0) return;

        for (var i = headerIndex + 1; i < trMatches.Count; i++)
        {
            var td = Regex.Matches(trMatches[i].Groups["row"].Value, "<td\\b(?:\"[^\"]*\"|'[^']*'|[^'\">])*>(?<cell>[\\s\\S]*?)</td>", RegexOptions.IgnoreCase);
            if (td.Count == 0) continue;
            var cells = td.Cast<Match>().Select(m => NormalizeHtmlText(m.Groups["cell"].Value)).ToList();
            if (!headers.Values.All(index => index >= 0 && index < cells.Count)) continue;
            if (!TryParseTalkgroupId(cells[headers["dec"]], out var id) || !seen.Add(id)) continue;
            var tag = cells[headers["tag"]];
            rows.Add(new TalkgroupCatalogItem
            {
                SystemShortName = NormalizeSystemShortName(systemShortName),
                Id = id,
                Mode = cells[headers["mode"]],
                AlphaTag = cells[headers["alpha_tag"]],
                Description = cells[headers["description"]],
                Tag = tag,
                SourceCategory = tag,
                OpsCategory = NormalizeOpsCategory(tag, cells[headers["alpha_tag"]], cells[headers["description"]]),
                Source = "radioreference"
            });
        }
    }

    private static TalkgroupCatalogDocument NormalizeDocument(TalkgroupCatalogDocument document)
    {
        var rows = document.Items
            .Where(i => i.Id > 0)
            .GroupBy(ItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => NormalizeItem(g.First()))
            .OrderBy(i => i.SystemShortName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Id)
            .ToList();
        return document with
        {
            SchemaVersion = document.SchemaVersion <= 0 ? 1 : document.SchemaVersion,
            UpdatedAtUtc = document.UpdatedAtUtc == default ? DateTime.UtcNow : document.UpdatedAtUtc,
            Items = rows
        };
    }

    private static TalkgroupCatalogItem NormalizeItem(TalkgroupCatalogItem item)
    {
        var system = SystemFromKeyOrValue(item.Key, item.SystemShortName, item.Id);
        return item with
        {
            SystemShortName = system,
            Key = CatalogKey(system, item.Id),
            Mode = string.IsNullOrWhiteSpace(item.Mode) ? "D" : item.Mode.Trim(),
            AlphaTag = item.AlphaTag?.Trim() ?? string.Empty,
            Description = item.Description?.Trim() ?? string.Empty,
            Tag = item.Tag?.Trim() ?? string.Empty,
            SourceCategory = item.SourceCategory?.Trim() ?? string.Empty,
            OpsCategory = NormalizeCategoryValue(item.OpsCategory),
            Source = item.Source?.Trim() ?? string.Empty,
            IncidentEligible = item.IncidentEligible && DefaultIncidentEligible(item),
            Notes = item.Notes?.Trim() ?? string.Empty,
            UpdatedAtUtc = item.UpdatedAtUtc == default ? DateTime.UtcNow : item.UpdatedAtUtc
        };
    }

    private static TalkgroupCatalogItem? FindBestMatch(IEnumerable<TalkgroupCatalogItem> items, string? systemShortName, long id)
    {
        var rows = items.Where(i => i.Id == id).ToList();
        if (rows.Count == 0)
            return null;
        var exactKey = CatalogKey(systemShortName, id);
        return rows.FirstOrDefault(row => string.Equals(ItemKey(row), exactKey, StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(row => string.Equals(ItemKey(row), id.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            ?? rows.FirstOrDefault(row => string.IsNullOrWhiteSpace(row.SystemShortName))
            ?? rows[0];
    }

    private static ProfileTalkgroupSetting? FindBestMatch(IEnumerable<ProfileTalkgroupSetting> items, string? systemShortName, long id)
    {
        var rows = items.Where(i => i.Id == id).ToList();
        if (rows.Count == 0)
            return null;
        var exactKey = CatalogKey(systemShortName, id);
        return rows.LastOrDefault(row => string.Equals(SettingKey(row), exactKey, StringComparison.OrdinalIgnoreCase))
            ?? rows.LastOrDefault(row => string.Equals(SettingKey(row), id.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            ?? rows.LastOrDefault(row => string.IsNullOrWhiteSpace(row.SystemShortName))
            ?? rows[^1];
    }

    private static ProfileTalkgroupSetting? FirstSettingMatch(IReadOnlyDictionary<string, ProfileTalkgroupSetting> overrides, TalkgroupCatalogItem row)
    {
        if (overrides.TryGetValue(ItemKey(row), out var exact))
            return exact;
        if (overrides.TryGetValue(row.Id.ToString(CultureInfo.InvariantCulture), out var legacy))
            return legacy;
        return null;
    }

    private string CatalogPath() =>
        string.IsNullOrWhiteSpace(_config.TrunkRecorder.TalkgroupCatalogPath)
            ? Path.Combine(_config.Storage.AppDataRoot, "talkgroups.json")
            : _config.TrunkRecorder.TalkgroupCatalogPath;

    private static bool ShouldExcludeImported(TalkgroupCatalogItem row)
    {
        var text = string.Join(" ", row.Mode, row.AlphaTag, row.Description, row.Tag, row.SourceCategory).ToLowerInvariant();
        return string.Equals(row.Mode?.Trim(), "e", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("encrypted", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unidentified", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("deprecated", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unused", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unwanted", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCategoryValue(string? category)
    {
        category = (category ?? string.Empty).Trim().ToLowerInvariant();
        return Categories.Contains(category) ? category : "other";
    }

    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    private static string SanitizePathToken(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in NormalizeSystemShortName(value))
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        var text = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(text) ? "system" : text;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool HasWord(string source, string token) => Regex.IsMatch(source, $@"\b{Regex.Escape(token)}\b", RegexOptions.IgnoreCase);
    private static string NormalizeHtmlText(string value) => WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", string.Empty)).Replace("&nbsp;", " ").Trim();
    private static string NormalizeHeaderToken(string value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string ExtractTalkgroupsSectionHtml(string html)
    {
        var headings = Regex.Matches(html, "<h(?<level>[1-6])[^>]*>(?<title>[\\s\\S]*?)</h\\k<level>>", RegexOptions.IgnoreCase);
        var heading = headings.Cast<Match>().FirstOrDefault(h => NormalizeHtmlText(h.Groups["title"].Value).Contains("Talkgroups", StringComparison.OrdinalIgnoreCase));
        if (heading == null)
            return html;
        var level = int.TryParse(heading.Groups["level"].Value, out var parsed) ? parsed : 2;
        var start = heading.Index + heading.Length;
        var end = html.Length;
        foreach (Match next in headings)
        {
            if (next.Index <= heading.Index) continue;
            if (int.TryParse(next.Groups["level"].Value, out var nextLevel) && nextLevel <= level)
            {
                end = next.Index;
                break;
            }
        }
        return start < html.Length && end > start ? html[start..end] : html;
    }

    private static bool TryGetHeaderIndexes(List<string> normalizedHeaders, Dictionary<string, int> indexes)
    {
        indexes.Clear();
        for (var i = 0; i < normalizedHeaders.Count; i++)
        {
            var h = normalizedHeaders[i];
            if (h == "dec") indexes["dec"] = i;
            else if (h == "mode") indexes["mode"] = i;
            else if (h == "alphatag" || h == "alpha") indexes["alpha_tag"] = i;
            else if (h == "description" || h == "desc") indexes["description"] = i;
            else if (h == "tag") indexes["tag"] = i;
        }
        return indexes.ContainsKey("dec") && indexes.ContainsKey("mode") && indexes.ContainsKey("alpha_tag") && indexes.ContainsKey("description") && indexes.ContainsKey("tag");
    }

    private static bool TryParseTalkgroupId(string? raw, out long id)
    {
        id = 0;
        var normalized = (raw ?? string.Empty).Replace(",", string.Empty).Trim();
        return Regex.IsMatch(normalized, @"^\d{1,8}$") && long.TryParse(normalized, out id) && id > 0;
    }

    private static string? GetField(CsvReader csv, params string[] names)
    {
        foreach (var name in names)
        {
            if (csv.TryGetField(name, out string? value))
                return value;
            var compact = new string(name.Where(char.IsLetterOrDigit).ToArray());
            var header = csv.HeaderRecord?.FirstOrDefault(h => string.Equals(new string(h.Where(char.IsLetterOrDigit).ToArray()), compact, StringComparison.OrdinalIgnoreCase));
            if (header != null && csv.TryGetField(header, out value))
                return value;
        }
        return null;
    }

    private static bool ParseEnabled(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("1", StringComparison.OrdinalIgnoreCase);

    private static bool DefaultIncidentEligible(TalkgroupCatalogItem row)
    {
        var text = string.Join(" ", row.AlphaTag, row.Description, row.Tag, row.SourceCategory).ToLowerInvariant();
        if (HasEmergencyDispatchWord(text))
            return true;
        if (HasOperationalNonIncidentWord(text))
            return false;
        return true;
    }

    private static bool HasOperationalNonIncidentWord(string text) =>
        HasWord(text, "maintenance") ||
        HasWord(text, "engineering") ||
        HasWord(text, "facilities") ||
        HasWord(text, "facility") ||
        HasWord(text, "parking") ||
        HasWord(text, "valet") ||
        HasWord(text, "security") ||
        HasWord(text, "shuttle") ||
        HasWord(text, "administration") ||
        HasWord(text, "administrative") ||
        HasWord(text, "housekeeping");

    private static bool HasEmergencyDispatchWord(string text) =>
        HasWord(text, "dispatch") ||
        HasWord(text, "tac") ||
        HasWord(text, "rescue") ||
        HasWord(text, "fire") ||
        HasWord(text, "sheriff") ||
        HasWord(text, "police") ||
        HasWord(text, "ems");
}

public sealed record TalkgroupCatalogDocument
{
    public int SchemaVersion { get; init; } = 1;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
    public List<TalkgroupCatalogItem> Items { get; init; } = [];
}

public sealed record TalkgroupCatalogItem
{
    public string Key { get; init; } = string.Empty;
    public string SystemShortName { get; init; } = string.Empty;
    public long Id { get; init; }
    public string Mode { get; init; } = "D";
    public string AlphaTag { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Tag { get; init; } = string.Empty;
    public string SourceCategory { get; init; } = string.Empty;
    public string OpsCategory { get; init; } = "other";
    public bool Enabled { get; init; } = true;
    public bool IncidentEligible { get; init; } = true;
    public string Source { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
    public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record TalkgroupCatalogPreview(
    IReadOnlyList<TalkgroupCatalogItem> Included,
    IReadOnlyList<TalkgroupCatalogItem> Excluded,
    IReadOnlyDictionary<string, int> IncludedByCategory,
    string Diagnostics);

public sealed record ResolvedCatalogTalkgroup(long Id, string Label, string Category, bool Found, bool IncidentEligible = true);
public sealed record TalkgroupCatalogSaveResult(int Count, string? BackupPath, string GeneratedCsvPath, string? GeneratedCsvBackupPath, bool TrRestartRecommended);
public sealed record TalkgroupTrCsvResult(string Path, string? BackupPath, int EnabledCount);
