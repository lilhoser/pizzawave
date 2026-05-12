using pizzalib;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class SetupTalkgroupService
{
    private readonly EngineConfig _config;
    private readonly HttpClient _http;

    public SetupTalkgroupService(EngineConfig config, HttpClient http)
    {
        _config = config;
        _http = http;
    }

    public async Task<SetupTalkgroupPreviewDto> PreviewAsync(SetupTalkgroupParseRequest request, CancellationToken ct)
    {
        var diagnostics = new List<string>();
        List<Talkgroup> rows;
        if (!string.IsNullOrWhiteSpace(request.RadioReferenceSid) || !string.IsNullOrWhiteSpace(request.RadioReferenceUrl))
        {
            var url = BuildRadioReferenceUrl(request);
            var html = await _http.GetStringAsync(url, ct);
            rows = ParseTalkgroupsFromHtml(html, out var diag);
            diagnostics.Add($"Fetched {url}");
            diagnostics.Add(diag);
        }
        else
        {
            rows = ParseCsvText(request.CsvText ?? string.Empty);
            diagnostics.Add($"Parsed CSV text: {rows.Count:N0} row(s).");
        }

        var previewRows = rows
            .Where(r => r.Id > 0)
            .GroupBy(r => r.Id)
            .Select(g => ToPreviewRow(g.First(), request.IncludeNormallyExcluded))
            .OrderBy(r => r.Id)
            .ToList();
        return BuildPreview(previewRows, string.Join(" ", diagnostics));
    }

    public async Task<SetupTalkgroupPreviewDto> SaveAsync(SetupTalkgroupSaveRequest request, CancellationToken ct)
    {
        var rows = request.Rows
            .Where(r => r.Included && r.Id > 0)
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .OrderBy(r => r.Id)
            .ToList();
        if (rows.Count == 0)
            throw new InvalidOperationException("No included talkgroup rows were provided.");

        var path = _config.TrunkRecorder.TalkgroupsPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("trunkRecorder.talkgroupsPath is not configured.");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        if (File.Exists(path))
        {
            var backup = $"{path}.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(path, backup, overwrite: false);
        }

        await using var writer = new StreamWriter(path, append: false, new UTF8Encoding(false));
        await writer.WriteLineAsync("Decimal,Hex,Mode,Alpha Tag,Description,Tag,Category");
        foreach (var row in rows)
        {
            await writer.WriteLineAsync(string.Join(",",
                row.Id.ToString(),
                row.Id.ToString("X"),
                Csv(row.Mode),
                Csv(row.AlphaTag),
                Csv(row.Description),
                Csv(row.Tag),
                Csv(NormalizeOpsCategory(row.OpsCategory))));
        }
        return BuildPreview(rows.Select(r => r with { Included = true, ExclusionReason = string.Empty }).ToList(), $"Saved {rows.Count:N0} row(s) to {path}.");
    }

    private static SetupTalkgroupPreviewDto BuildPreview(List<SetupTalkgroupRowDto> rows, string diagnostics)
    {
        var included = rows.Where(r => r.Included).ToList();
        var byCategory = included
            .GroupBy(r => NormalizeOpsCategory(r.OpsCategory))
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        return new SetupTalkgroupPreviewDto(rows, byCategory, included.Count, rows.Count - included.Count, diagnostics);
    }

    private static SetupTalkgroupRowDto ToPreviewRow(Talkgroup row, bool includeNormallyExcluded)
    {
        var ops = ResolveOpsCategory(row);
        var reason = ExclusionReason(row);
        return new SetupTalkgroupRowDto
        {
            Id = row.Id,
            Mode = row.Mode ?? string.Empty,
            AlphaTag = row.AlphaTag ?? string.Empty,
            Description = row.Description ?? string.Empty,
            Tag = row.Tag ?? string.Empty,
            Category = row.Category ?? string.Empty,
            OpsCategory = ops,
            Included = includeNormallyExcluded || string.IsNullOrWhiteSpace(reason),
            ExclusionReason = reason
        };
    }

    private static List<Talkgroup> ParseCsvText(string text)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"pizzawave-talkgroups-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            return TalkgroupHelper.GetTalkgroupsFromCsv(temp);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static string BuildRadioReferenceUrl(SetupTalkgroupParseRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.RadioReferenceUrl))
            return request.RadioReferenceUrl.Trim();
        var sid = Regex.Replace(request.RadioReferenceSid ?? string.Empty, @"[^\d]", "");
        if (string.IsNullOrWhiteSpace(sid))
            throw new InvalidOperationException("RadioReference SID is required.");
        return $"https://www.radioreference.com/db/sid/{sid}";
    }

    private static List<Talkgroup> ParseTalkgroupsFromHtml(string html, out string diagnostics)
    {
        var rows = new List<Talkgroup>();
        if (string.IsNullOrWhiteSpace(html))
        {
            diagnostics = "empty html";
            return rows;
        }

        var clean = Regex.Replace(html, "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", string.Empty, RegexOptions.IgnoreCase);
        var section = ExtractTalkgroupsSectionHtml(clean);
        var seen = new HashSet<long>();
        foreach (Match table in Regex.Matches(section, "<table[^>]*>[\\s\\S]*?</table>", RegexOptions.IgnoreCase))
            ParseTalkgroupTable(table.Value, seen, rows);
        diagnostics = $"htmlLen={html.Length}; sectionLen={section.Length}; rowsParsed={rows.Count}";
        return rows.OrderBy(r => r.Id).ToList();
    }

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

    private static void ParseTalkgroupTable(string tableHtml, HashSet<long> seen, List<Talkgroup> rows)
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
            rows.Add(new Talkgroup
            {
                Id = id,
                Mode = NullIfEmpty(cells[headers["mode"]]),
                AlphaTag = NullIfEmpty(cells[headers["alpha_tag"]]),
                Description = NullIfEmpty(cells[headers["description"]]),
                Tag = NullIfEmpty(cells[headers["tag"]]),
                Category = NullIfEmpty(cells[headers["tag"]])
            });
        }
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

    private static string ResolveOpsCategory(Talkgroup row)
    {
        var category = NormalizeOpsCategory(row.Category);
        if (category != "other") return category;
        category = NormalizeOpsCategory(row.Tag);
        if (category != "other") return category;
        return NormalizeOpsCategory(string.Join(" ", row.Category, row.Tag, row.AlphaTag, row.Description));
    }

    private static string NormalizeOpsCategory(string? raw)
    {
        var text = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Contains("police") || text.Contains("sheriff") || text.Contains("law") || HasWord(text, "pd") || HasWord(text, "so")) return "police";
        if (text.Contains("fire") || HasWord(text, "fd")) return "fire";
        if (text.Contains("ems") || text.Contains("medical") || text.Contains("medic") || text.Contains("ambulance") || text.Contains("hospital") || text.Contains("rescue")) return "ems";
        if (text.Contains("traffic") || text.Contains("accident") || text.Contains("crash") || text.Contains("road") || text.Contains("highway") || text.Contains("hwy") || text.Contains("transportation") || HasWord(text, "dot")) return "traffic";
        return text is "police" or "fire" or "ems" or "traffic" ? text : "other";
    }

    private static string ExclusionReason(Talkgroup row)
    {
        var text = string.Join(" ", row.Mode, row.AlphaTag, row.Description, row.Tag, row.Category).ToLowerInvariant();
        if (string.Equals(row.Mode?.Trim(), "e", StringComparison.OrdinalIgnoreCase) || text.Contains("encrypted")) return "encrypted";
        if (text.Contains("unknown") || text.Contains("unidentified")) return "unknown";
        if (text.Contains("deprecated") || text.Contains("unused") || text.Contains("unwanted")) return "unwanted";
        return string.Empty;
    }

    private static bool HasWord(string source, string token) => Regex.IsMatch(source, $@"\b{Regex.Escape(token)}\b");
    private static string NormalizeHtmlText(string value) => WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", string.Empty)).Replace("&nbsp;", " ").Trim();
    private static string NormalizeHeaderToken(string value) => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static bool TryParseTalkgroupId(string? raw, out long id)
    {
        id = 0;
        var normalized = (raw ?? string.Empty).Replace(",", string.Empty).Trim();
        return Regex.IsMatch(normalized, @"^\d{1,8}$") && long.TryParse(normalized, out id) && id > 0;
    }
    private static string Csv(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
