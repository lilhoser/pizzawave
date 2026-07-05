using System.Text.RegularExpressions;

namespace pizzad;

public sealed class SetupTalkgroupService
{
    private readonly TalkgroupCatalogService _catalog;
    private readonly HttpClient _http;

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
            preview = TalkgroupCatalogService.PreviewRadioReferenceHtml(html, request.SystemShortName);
            preview = preview with { Diagnostics = $"Fetched {url}. {preview.Diagnostics}" };
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

        var document = new TalkgroupCatalogDocument { Items = rows };
        var result = await _catalog.SaveAsync(document, generateTrCsv: true, ct);
        var preview = new TalkgroupCatalogPreview(
            rows,
            [],
            rows.GroupBy(r => r.OpsCategory).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count()),
            $"Saved {result.Count:N0} row(s) to the PizzaWave talkgroup catalog and regenerated {result.GeneratedCsvPath}.");
        return ToSetupPreview(preview, includeExcluded: false);
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
}
