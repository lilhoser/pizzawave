using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed partial class SetupTrConfigBuilderService
{
    private readonly HttpClient _http;
    private readonly EngineConfig _config;

    public SetupTrConfigBuilderService(HttpClient http, EngineConfig config)
    {
        _http = http;
        _config = config;
    }

    public async Task<SetupTrConfigDraftDto> DraftAsync(SetupTrConfigDraftRequest request, CancellationToken ct)
    {
        var sampleRate = request.SampleRate > 0 ? request.SampleRate : 2_400_000;
        var html = await LoadHtmlAsync(request, ct);
        var siteFilters = SplitList(request.SiteNames);
        var serials = SplitList(request.SdrSerials);
        var systems = ParseSites(html, request.RadioReferenceSid, siteFilters).ToList();
        var warnings = new List<string>();

        if (systems.Count == 0)
            throw new InvalidOperationException("No site frequency rows were found. Paste the RadioReference page source or verify the SID/site names.");

        if (siteFilters.Count > 0)
        {
            var matched = systems.Select(s => s.SiteName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in siteFilters.Where(filter => !matched.Contains(filter)))
                warnings.Add($"No frequency table matched requested site '{filter}'.");
        }

        var sourceDrafts = new List<SetupTrConfigSourceDto>();
        var systemDrafts = new List<SetupTrConfigSystemDto>();
        for (var i = 0; i < systems.Count; i++)
        {
            var site = systems[i];
            var coverage = BestCoverage(site.FrequenciesMhz, sampleRate);
            var serial = i < serials.Count ? serials[i] : string.Empty;
            var warningParts = new List<string>();
            if (string.IsNullOrWhiteSpace(serial))
                warningParts.Add("No SDR serial assigned.");
            if (coverage.Omitted.Count > 0)
                warningParts.Add($"{coverage.Omitted.Count} frequency/frequencies fall outside the selected SDR window.");
            if (site.ControlChannelsMhz.Count == 0)
                warningParts.Add("No explicit control channel marker was detected; all site frequencies were kept as fallback control channels.");

            systemDrafts.Add(new SetupTrConfigSystemDto(
                site.SystemName,
                site.ShortName,
                site.SiteName,
                site.FrequenciesMhz,
                site.ControlChannelsMhz.Count > 0 ? site.ControlChannelsMhz : site.FrequenciesMhz,
                coverage.CenterHz,
                serial,
                string.Join(" ", warningParts)));

            sourceDrafts.Add(new SetupTrConfigSourceDto(
                site.ShortName,
                serial,
                coverage.CenterHz,
                sampleRate,
                coverage.Covered,
                coverage.Omitted));
        }

        if (serials.Count > 0 && serials.Count < systems.Count)
            warnings.Add($"Only {serials.Count} SDR serial(s) were supplied for {systems.Count} site(s).");
        if (systems.Any(s => s.FrequenciesMhz.Count > 0 && BestCoverage(s.FrequenciesMhz, sampleRate).Omitted.Count > 0))
            warnings.Add("The generated config continues with the largest possible covered frequency set. Review omitted frequencies before using it.");

        var configJson = BuildConfigJson(systemDrafts, sourceDrafts);
        var diagnostics = $"Drafted {systemDrafts.Count} system/site block(s) from {systems.Sum(s => s.FrequenciesMhz.Count)} frequency row(s). Sample rate {sampleRate:N0} Hz.";
        return new SetupTrConfigDraftDto(configJson, systemDrafts, sourceDrafts, warnings, diagnostics);
    }

    public async Task<SetupValidationResult> SaveAsync(SetupTrConfigSaveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ConfigJson))
            return new SetupValidationResult(false, "No TR config JSON was supplied.");

        using var _ = JsonDocument.Parse(request.ConfigJson);
        var path = _config.TrunkRecorder.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        if (File.Exists(path))
        {
            var backup = $"{path}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(path, backup, overwrite: false);
        }
        await File.WriteAllTextAsync(path, NormalizeText(request.ConfigJson), Encoding.UTF8, ct);
        _config.Setup.TrConfigured = true;
        _config.Save();
        return new SetupValidationResult(true, $"Saved TR config to {path}. A timestamped backup was created if a file already existed.", new { path });
    }

    private async Task<string> LoadHtmlAsync(SetupTrConfigDraftRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(request.HtmlText))
            return request.HtmlText;
        var url = !string.IsNullOrWhiteSpace(request.RadioReferenceUrl)
            ? request.RadioReferenceUrl.Trim()
            : !string.IsNullOrWhiteSpace(request.RadioReferenceSid)
                ? $"https://www.radioreference.com/db/sid/{request.RadioReferenceSid.Trim()}"
                : string.Empty;
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Provide a RadioReference SID, URL, or pasted page HTML.");
        return await _http.GetStringAsync(url, ct);
    }

    private static IEnumerable<SiteParseResult> ParseSites(string html, string? sid, IReadOnlyList<string> requestedSites)
    {
        var plain = HtmlToText(html);
        var systemName = ExtractTitle(plain, sid);
        if (!FrequencyRegex().IsMatch(plain))
            yield break;

        var siteBuckets = requestedSites.Count > 0
            ? requestedSites.Select(site => new SiteParseResult(systemName, ShortName(site), site)).ToList()
            : InferSiteNames(plain).Select(site => new SiteParseResult(systemName, ShortName(site), site)).ToList();
        if (siteBuckets.Count == 0)
            siteBuckets.Add(new SiteParseResult(systemName, ShortName(systemName), systemName));

        foreach (var site in siteBuckets)
        {
            var section = ExtractSiteSection(plain, site.SiteName, siteBuckets.Select(s => s.SiteName));
            var siteMatches = FrequencyRegex().Matches(section).Cast<Match>().ToList();
            if (siteMatches.Count == 0 && siteBuckets.Count == 1)
                siteMatches = FrequencyRegex().Matches(plain).Cast<Match>().ToList();
            if (siteMatches.Count == 0)
                continue;

            foreach (var match in siteMatches)
            {
                if (!double.TryParse(match.Groups["freq"].Value, out var mhz))
                    continue;
                if (mhz < 20 || mhz > 1300)
                    continue;
                site.FrequenciesMhz.Add(Math.Round(mhz, 6));
                var marker = match.Groups["marker"].Value;
                var contextStart = Math.Max(0, match.Index - 80);
                var contextLength = Math.Min(160, section.Length - contextStart);
                var context = section.Substring(contextStart, contextLength);
                if (marker.Equals("c", StringComparison.OrdinalIgnoreCase) ||
                    marker.Equals("a", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains("control", StringComparison.OrdinalIgnoreCase))
                    site.ControlChannelsMhz.Add(Math.Round(mhz, 6));
            }

            site.FrequenciesMhz = site.FrequenciesMhz.Distinct().OrderBy(f => f).ToList();
            site.ControlChannelsMhz = site.ControlChannelsMhz.Distinct().OrderBy(f => f).ToList();
            if (site.FrequenciesMhz.Count > 0)
                yield return site;
        }
    }

    private static string ExtractSiteSection(string plain, string siteName, IEnumerable<string> allSiteNames)
    {
        var startMatch = Regex.Match(plain, Regex.Escape(siteName), RegexOptions.IgnoreCase);
        if (!startMatch.Success)
            return string.Empty;
        var start = startMatch.Index;
        var end = plain.Length;
        foreach (var other in allSiteNames.Where(s => !string.Equals(s, siteName, StringComparison.OrdinalIgnoreCase)))
        {
            var otherMatch = Regex.Match(plain[(start + siteName.Length)..], Regex.Escape(other), RegexOptions.IgnoreCase);
            if (otherMatch.Success)
                end = Math.Min(end, start + siteName.Length + otherMatch.Index);
        }
        return plain[start..end];
    }

    private string BuildConfigJson(IReadOnlyList<SetupTrConfigSystemDto> systems, IReadOnlyList<SetupTrConfigSourceDto> sources)
    {
        var root = new Dictionary<string, object?>
        {
            ["ver"] = 2,
            ["logDir"] = "/var/log/trunk-recorder",
            ["sources"] = sources.Select((source, index) => new Dictionary<string, object?>
            {
                ["center"] = source.CenterFrequency,
                ["rate"] = source.SampleRate,
                ["error"] = 0,
                ["gain"] = 0,
                ["digitalRecorders"] = 4,
                ["driver"] = "osmosdr",
                ["device"] = string.IsNullOrWhiteSpace(source.Serial) ? $"rtl={index}" : $"rtl={source.Serial}"
            }).ToList(),
            ["systems"] = systems.Select(system => new Dictionary<string, object?>
            {
                ["type"] = "p25",
                ["shortName"] = system.ShortName,
                ["control_channels"] = system.ControlChannelsMhz.Select(MhzToHz).ToList(),
                ["talkgroupsFile"] = _config.TrunkRecorder.TalkgroupsPath
            }).ToList(),
            ["plugins"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = "callstream",
                    ["library"] = "libcallstream.so",
                    ["host"] = _config.Ingest.CallstreamBind,
                    ["port"] = _config.Ingest.CallstreamPort
                }
            }
        };
        return JsonSerializer.Serialize(root, EngineConfig.JsonOptions());
    }

    private static CoverageResult BestCoverage(IReadOnlyList<double> freqsMhz, int sampleRate)
    {
        var sorted = freqsMhz.Distinct().OrderBy(f => f).ToList();
        if (sorted.Count == 0)
            return new CoverageResult(0, [], []);
        var spanMhz = sampleRate / 1_000_000.0;
        var bestStart = sorted[0];
        var bestCovered = new List<double> { sorted[0] };
        foreach (var candidate in sorted)
        {
            var covered = sorted.Where(f => f >= candidate && f <= candidate + spanMhz).ToList();
            if (covered.Count > bestCovered.Count ||
                (covered.Count == bestCovered.Count && covered.Last() - covered.First() < bestCovered.Last() - bestCovered.First()))
            {
                bestStart = candidate;
                bestCovered = covered;
            }
        }
        var min = bestCovered.First();
        var max = bestCovered.Last();
        var center = MhzToHz((min + max) / 2);
        var omitted = sorted.Except(bestCovered).ToList();
        _ = bestStart;
        return new CoverageResult(center, bestCovered, omitted);
    }

    private static List<string> InferSiteNames(string plain)
    {
        var names = SiteNameRegex().Matches(plain).Cast<Match>()
            .Select(m => WebUtility.HtmlDecode(m.Groups["name"].Value).Trim())
            .Where(s => s.Length is > 2 and < 80)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        return names;
    }

    private static string HtmlToText(string html)
    {
        var text = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, "\\s+", " ").Trim();
    }

    private static string ExtractTitle(string plain, string? sid)
    {
        if (!string.IsNullOrWhiteSpace(sid))
            return $"RadioReference SID {sid.Trim()}";
        var title = plain.Split("System", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(title) ? "Trunk Recorder System" : title[..Math.Min(title.Length, 60)];
    }

    private static string ShortName(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "system" : normalized[..Math.Min(normalized.Length, 32)];
    }

    private static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static long MhzToHz(double mhz) => (long)Math.Round(mhz * 1_000_000);

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";

    [GeneratedRegex(@"(?<freq>\b\d{2,4}\.\d{3,6})(?<marker>[ac])?", RegexOptions.IgnoreCase)]
    private static partial Regex FrequencyRegex();

    [GeneratedRegex(@"(?:Site|RFSS|Simulcast)\s+[:#]?\s*(?<name>[A-Za-z0-9][A-Za-z0-9 ._\-/]{2,60})", RegexOptions.IgnoreCase)]
    private static partial Regex SiteNameRegex();

    private sealed record CoverageResult(long CenterHz, IReadOnlyList<double> Covered, IReadOnlyList<double> Omitted);

    private sealed record SiteParseResult(string SystemName, string ShortName, string SiteName)
    {
        public List<double> FrequenciesMhz { get; set; } = [];
        public List<double> ControlChannelsMhz { get; set; } = [];
    }
}
