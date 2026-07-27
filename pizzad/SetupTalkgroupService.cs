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

            var now = DateTime.UtcNow;
            var batches = new List<TalkgroupCatalogImportBatch>();

            foreach (var source in pending)
            {
                var url = BuildRadioReferenceUrl(source.RadioReferenceSid);
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
                batches.Add(new TalkgroupCatalogImportBatch(source.RadioReferenceSid, systemShortName, incoming, now));
            }

            var merge = await _catalog.MergeRadioReferenceImportsAsync(batches, ct);
            return BuildSyncResult(merge.Document, batches.Count, merge.AddedRows, merge.RefreshedRows,
                $"Loaded {batches.Count:N0} RadioReference system(s): {merge.AddedRows:N0} talkgroups added and {merge.RefreshedRows:N0} refreshed.");
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

    private static string BuildRadioReferenceUrl(string radioReferenceSid)
    {
        var sid = Regex.Replace(radioReferenceSid ?? string.Empty, @"[^\d]", "");
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
