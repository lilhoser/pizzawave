using System.Text.RegularExpressions;
using System.Net;

namespace pizzad;

public sealed class SetupTalkgroupService
{
    private readonly TalkgroupCatalogService _catalog;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public SetupTalkgroupService(TalkgroupCatalogService catalog, HttpClient http)
    {
        _catalog = catalog;
        _http = http;
    }

    public async Task<SetupTalkgroupPreviewDto> PreviewAsync(SetupTalkgroupParseRequest request, CancellationToken ct)
    {
        TalkgroupCatalogPreview preview;
        if (!string.IsNullOrWhiteSpace(request.RadioReferenceSid) || !string.IsNullOrWhiteSpace(request.RadioReferenceUrl))
        {
            var url = BuildRadioReferenceUrl(request);
            var html = await _http.GetStringAsync(url, ct);
            var systemShortName = EffectiveRadioReferenceSystemShortName(html, request.SystemShortName, request.RadioReferenceSid);
            preview = TalkgroupCatalogService.PreviewRadioReferenceHtml(html, systemShortName);
            preview = preview with { Diagnostics = $"Fetched {url} for {systemShortName}. {preview.Diagnostics}" };
        }
        else
        {
            preview = TalkgroupCatalogService.PreviewCsv(request.CsvText ?? string.Empty, request.SystemShortName);
        }

        return ToSetupPreview(preview, request.IncludeNormallyExcluded);
    }

    public async Task<SetupTalkgroupPreviewDto> SaveAsync(SetupTalkgroupSaveRequest request, CancellationToken ct)
    {
        var rows = request.Rows
            .Where(r => r.Included && r.Id > 0)
            .Select(r => new TalkgroupCatalogItem
            {
                Key = string.IsNullOrWhiteSpace(r.Key) ? TalkgroupCatalogService.CatalogKey(r.SystemShortName, r.Id) : r.Key.Trim(),
                SystemShortName = TalkgroupCatalogService.NormalizeSystemShortName(r.SystemShortName),
                Id = r.Id,
                Mode = string.IsNullOrWhiteSpace(r.Mode) ? "D" : r.Mode,
                AlphaTag = r.AlphaTag,
                Description = r.Description,
                Tag = r.Tag,
                SourceCategory = r.Category,
                OpsCategory = string.IsNullOrWhiteSpace(r.OpsCategory)
                    ? TalkgroupCatalogService.NormalizeOpsCategory(r.Category, r.Tag, r.AlphaTag, r.Description)
                    : r.OpsCategory,
                Enabled = true,
                Source = "setup"
            })
            .GroupBy(TalkgroupCatalogService.ItemKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        rows = rows
            .OrderBy(r => r.Id)
            .ToList();
        if (rows.Count == 0)
            throw new InvalidOperationException("No included talkgroup rows were provided.");

        var sync = MergeImportedRowsWithExistingPolicy(rows);
        var document = sync.Document;
        var result = await _catalog.SaveAsync(document, generateTrCsv: true, ct);
        var preview = new TalkgroupCatalogPreview(
            rows,
            [],
            rows.GroupBy(r => r.OpsCategory).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            $"Loaded {rows.Count:N0} RadioReference talkgroup row(s) into the PizzaWave talkgroup catalog: {sync.Added:N0} added, {sync.Refreshed:N0} refreshed. Replaced unrelated catalog rows, preserved matching operator policy, and regenerated {result.GeneratedCsvPath}.");
        return ToSetupPreview(preview, includeExcluded: false);
    }

    public async Task<SetupTalkgroupSyncResult> SyncAsync(SetupTalkgroupSyncRequest request, CancellationToken ct)
    {
        var sources = (request.Sources ?? [])
            .Select(source => new SetupTalkgroupImportSourceRequest(
                Regex.Replace(source.RadioReferenceSid ?? string.Empty, @"[^\d]", string.Empty),
                TalkgroupCatalogService.NormalizeSystemShortName(source.SystemShortName)))
            .Where(source => !string.IsNullOrWhiteSpace(source.RadioReferenceSid))
            .GroupBy(source => source.RadioReferenceSid, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(source => !IsGenericRadioReferenceName(source.SystemShortName ?? string.Empty, source.RadioReferenceSid)) ?? group.First())
            .ToList();
        if (sources.Count == 0)
            throw new InvalidOperationException("At least one RadioReference system is required.");

        var forceSid = Regex.Replace(request.ForceRadioReferenceSid ?? string.Empty, @"[^\d]", string.Empty);
        await _syncGate.WaitAsync(ct);
        try
        {
            var document = _catalog.Load();
            var existingImports = (document.Imports ?? []).ToDictionary(row => row.RadioReferenceSid, StringComparer.OrdinalIgnoreCase);
            var pending = sources
                .Where(source => string.Equals(source.RadioReferenceSid, forceSid, StringComparison.OrdinalIgnoreCase) || !existingImports.ContainsKey(source.RadioReferenceSid))
                .ToList();
            if (pending.Count == 0)
                return BuildSyncResult(document, 0, 0, 0, "RadioReference talkgroups are already loaded for the selected systems.");

            var items = document.Items.ToDictionary(TalkgroupCatalogService.ItemKey, StringComparer.OrdinalIgnoreCase);
            var imports = (document.Imports ?? []).ToDictionary(row => row.RadioReferenceSid, StringComparer.OrdinalIgnoreCase);
            var importedSystems = 0;
            var addedRows = 0;
            var refreshedRows = 0;
            var now = DateTime.UtcNow;

            foreach (var source in pending)
            {
                var url = BuildRadioReferenceUrl(new SetupTalkgroupParseRequest(RadioReferenceSid: source.RadioReferenceSid));
                var html = await _http.GetStringAsync(url, ct);
                var systemShortName = EffectiveRadioReferenceSystemShortName(html, source.SystemShortName, source.RadioReferenceSid);
                var preview = TalkgroupCatalogService.PreviewRadioReferenceHtml(html, systemShortName);
                var incoming = preview.Included
                    .Select(row => row with
                    {
                        SystemShortName = systemShortName,
                        Key = TalkgroupCatalogService.CatalogKey(systemShortName, row.Id),
                        Source = "radioreference",
                        RadioReferenceSid = source.RadioReferenceSid,
                        UpdatedAtUtc = now
                    })
                    .ToList();
                if (incoming.Count == 0)
                    throw new InvalidOperationException($"No talkgroups were found for RadioReference system {source.RadioReferenceSid}.");

                foreach (var row in incoming)
                {
                    var key = TalkgroupCatalogService.ItemKey(row);
                    if (!items.TryGetValue(key, out var existing))
                    {
                        items[key] = row;
                        addedRows++;
                        continue;
                    }

                    var preserveManualFields = string.Equals(existing.Source, "manual", StringComparison.OrdinalIgnoreCase);
                    items[key] = row with
                    {
                        Mode = preserveManualFields ? existing.Mode : row.Mode,
                        AlphaTag = preserveManualFields ? existing.AlphaTag : row.AlphaTag,
                        Description = preserveManualFields ? existing.Description : row.Description,
                        Tag = preserveManualFields ? existing.Tag : row.Tag,
                        SourceCategory = preserveManualFields ? existing.SourceCategory : row.SourceCategory,
                        Enabled = existing.Enabled,
                        OpsCategory = existing.OpsCategory,
                        IncidentEligible = existing.IncidentEligible,
                        Source = preserveManualFields ? existing.Source : row.Source,
                        Notes = existing.Notes
                    };
                    refreshedRows++;
                }

                imports[source.RadioReferenceSid] = new TalkgroupCatalogImport
                {
                    RadioReferenceSid = source.RadioReferenceSid,
                    SystemShortName = systemShortName,
                    RowCount = incoming.Count,
                    ImportedAtUtc = now
                };
                importedSystems++;
            }

            var next = document with
            {
                SchemaVersion = 2,
                UpdatedAtUtc = now,
                Imports = imports.Values.OrderBy(row => row.SystemShortName, StringComparer.OrdinalIgnoreCase).ToList(),
                Items = items.Values.OrderBy(row => row.SystemShortName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Id).ToList()
            };
            await _catalog.SaveAsync(next, generateTrCsv: true, ct);
            return BuildSyncResult(next, importedSystems, addedRows, refreshedRows,
                $"Loaded {importedSystems:N0} RadioReference system(s): {addedRows:N0} talkgroups added and {refreshedRows:N0} refreshed.");
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private static SetupTalkgroupSyncResult BuildSyncResult(
        TalkgroupCatalogDocument document,
        int importedSystems,
        int addedRows,
        int refreshedRows,
        string message) => new(
            importedSystems,
            addedRows,
            refreshedRows,
            (document.Imports ?? [])
                .OrderBy(row => row.SystemShortName, StringComparer.OrdinalIgnoreCase)
                .Select(row => new SetupTalkgroupImportDto(row.RadioReferenceSid, row.SystemShortName, row.RowCount, row.ImportedAtUtc))
                .ToList(),
            message);

    private (TalkgroupCatalogDocument Document, int Added, int Refreshed) MergeImportedRowsWithExistingPolicy(List<TalkgroupCatalogItem> rows)
    {
        var existing = _catalog.Load();
        var added = 0;
        var refreshed = 0;
        var synced = rows.Select(row =>
        {
            var key = TalkgroupCatalogService.ItemKey(row);
            var existingRow = existing.Items.LastOrDefault(item =>
                string.Equals(TalkgroupCatalogService.ItemKey(item), key, StringComparison.OrdinalIgnoreCase));
            if (existingRow == null)
            {
                added++;
                return row;
            }

            refreshed++;
            var manualCatalogRow = string.Equals(existingRow.Source, "manual", StringComparison.OrdinalIgnoreCase);
            return row with
            {
                AlphaTag = manualCatalogRow ? existingRow.AlphaTag : row.AlphaTag,
                Description = manualCatalogRow ? existingRow.Description : row.Description,
                Tag = manualCatalogRow ? existingRow.Tag : row.Tag,
                Enabled = existingRow.Enabled,
                OpsCategory = existingRow.OpsCategory,
                IncidentEligible = existingRow.IncidentEligible,
                Notes = existingRow.Notes,
                UpdatedAtUtc = DateTime.UtcNow
            };
        });
        return (existing with { Items = synced.ToList() }, added, refreshed);
    }

    private static SetupTalkgroupPreviewDto ToSetupPreview(TalkgroupCatalogPreview preview, bool includeExcluded)
    {
        var includedRows = preview.Included.Select(row => ToPreviewRow(row, included: true, string.Empty));
        var excludedRows = includeExcluded
            ? preview.Excluded.Select(row => ToPreviewRow(row, included: false, "excluded by import policy"))
            : [];
        var rows = includedRows.Concat(excludedRows)
            .OrderBy(r => r.SystemShortName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Id)
            .ToList();
        return new SetupTalkgroupPreviewDto(rows, preview.IncludedByCategory, preview.Included.Count, preview.Excluded.Count, preview.Diagnostics);
    }

    private static SetupTalkgroupRowDto ToPreviewRow(TalkgroupCatalogItem row, bool included, string reason) => new()
    {
        Key = TalkgroupCatalogService.ItemKey(row),
        SystemShortName = row.SystemShortName,
        Id = row.Id,
        Mode = row.Mode,
        AlphaTag = row.AlphaTag,
        Description = row.Description,
        Tag = row.Tag,
        Category = row.SourceCategory,
        OpsCategory = row.OpsCategory,
        Included = included,
        ExclusionReason = reason
    };

    private static string BuildRadioReferenceUrl(SetupTalkgroupParseRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RadioReferenceUrl))
            return request.RadioReferenceUrl.Trim();
        var sid = Regex.Replace(request.RadioReferenceSid ?? string.Empty, @"[^\d]", "");
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("RadioReference SID is required.");
        return $"https://www.radioreference.com/db/sid/{sid}";
    }

    private static string EffectiveRadioReferenceSystemShortName(string html, string? requestedSystemShortName, string? sid)
    {
        var requested = TalkgroupCatalogService.NormalizeSystemShortName(requestedSystemShortName);
        if (!IsGenericRadioReferenceName(requested, sid))
            return requested;

        var name = ExtractRadioReferenceSystemName(html);
        var acronym = ExtractParentheticalAcronym(name);
        return TalkgroupCatalogService.NormalizeSystemShortName(acronym ?? name ?? requested);
    }

    private static bool IsGenericRadioReferenceName(string value, string? sid)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (Regex.IsMatch(value, @"^radioreference-sid-\d+$", RegexOptions.IgnoreCase))
            return true;
        var digits = Regex.Replace(sid ?? string.Empty, @"[^\d]", "");
        return !string.IsNullOrWhiteSpace(digits) && string.Equals(value, $"rr-{digits}", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractRadioReferenceSystemName(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var plain = HtmlToText(html);
        var systemName = Regex.Match(plain, @"System\s+Name:\s*(?<name>.*?)(?:\s+Location:|\s+County:|\s+System\s+Type:)", RegexOptions.IgnoreCase);
        if (systemName.Success)
            return CleanName(systemName.Groups["name"].Value);

        var title = Regex.Match(html, @"<title[^>]*>(?<title>[\s\S]*?)</title>", RegexOptions.IgnoreCase);
        if (title.Success)
        {
            var titleText = HtmlToText(title.Groups["title"].Value);
            titleText = Regex.Replace(titleText, @"\s+Trunking\s+System[\s\S]*$", string.Empty, RegexOptions.IgnoreCase);
            var firstSegment = titleText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            var cleaned = CleanName(firstSegment ?? titleText);
            if (!string.IsNullOrWhiteSpace(cleaned))
                return cleaned;
        }

        return null;
    }

    private static string? ExtractParentheticalAcronym(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var match = Regex.Match(name, @"\((?<acronym>[A-Z0-9]{2,12})\)");
        return match.Success ? match.Groups["acronym"].Value : null;
    }

    private static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static string? CleanName(string? value)
    {
        var text = Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
