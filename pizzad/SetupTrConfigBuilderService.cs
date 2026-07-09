using System.Net;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed partial class SetupTrConfigBuilderService
{
    private const int DefaultSampleRate = 2_400_000;
    private const double TrUsableBandwidthFactor = 0.9375;

    private readonly HttpClient _http;
    private readonly EngineConfig _config;
    private readonly TalkgroupCatalogService _talkgroups;

    public SetupTrConfigBuilderService(HttpClient http, EngineConfig config, TalkgroupCatalogService talkgroups)
    {
        _http = http;
        _config = config;
        _talkgroups = talkgroups;
    }

    public async Task<SetupTrConfigSitesDto> ListSitesAsync(SetupTrConfigSitesRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RadioReferenceSid))
            throw new InvalidOperationException("RadioReference SID is required.");

        var html = await LoadHtmlAsync(new SetupTrConfigDraftRequest(RadioReferenceSid: request.RadioReferenceSid, HtmlText: request.HtmlText), ct);
        var plain = HtmlToText(html);
        var systemName = ExtractTitle(plain, request.RadioReferenceSid);
        var siteNames = InferSiteRows(plain);
        if (siteNames.Count == 0)
            siteNames = InferSiteNames(plain);

        var sites = new List<SetupTrConfigSiteDto>();
        foreach (var siteName in siteNames)
        {
            var parsed = ParseSites(html, request.RadioReferenceSid, [siteName]).FirstOrDefault();
            if (parsed == null)
                continue;
            sites.Add(new SetupTrConfigSiteDto(
                parsed.SiteName,
                parsed.ShortName,
                parsed.FrequenciesMhz.Count,
                parsed.ControlChannelsMhz.Count,
                parsed.ControlChannelsMhz));
        }

        if (sites.Count == 0)
            throw new InvalidOperationException("No RadioReference site rows were found for this SID.");

        var diagnostics = $"Found {sites.Count} RadioReference site(s). Select one or more before continuing.";
        return new SetupTrConfigSitesDto(systemName, sites, diagnostics);
    }

    public async Task<SetupTrConfigDraftDto> DraftAsync(SetupTrConfigDraftRequest request, CancellationToken ct)
    {
        var template = TryLoadTemplateConfig();
        var sampleRate = request.SampleRate > 0 ? request.SampleRate : TemplateSampleRate(template) ?? DefaultSampleRate;
        var html = await LoadHtmlAsync(request, ct);
        var siteFilters = SiteFilters(request.SiteNameList, request.SiteNames);
        var devices = NormalizeDevices(request.SdrDevices, request.SdrSerials, sampleRate);
        var plan = BuildSourcePlan(html, request.RadioReferenceSid, siteFilters, devices, sampleRate);

        var configJson = BuildConfigJson(plan.Systems, plan.Sources, template);
        var diagnostics = $"Drafted {plan.Systems.Count} system/site block(s) from {plan.Systems.Sum(s => s.FrequenciesMhz.Count)} frequency row(s). Source sample rates: {string.Join(", ", plan.Sources.Select(s => $"{s.Label} {s.SampleRate:N0} Hz"))}.";
        return new SetupTrConfigDraftDto(configJson, plan.Systems, plan.Sources, plan.Warnings, diagnostics);
    }

    private static SourcePlan BuildSourcePlan(string html, string? sid, IReadOnlyList<string> siteFilters, IReadOnlyList<SelectedSdrDevice> devices, int requestedSampleRate)
    {
        var systems = ParseSites(html, sid, siteFilters).ToList();
        var warnings = new List<string>();

        if (systems.Count == 0)
            throw new InvalidOperationException("No site frequency rows were found. Verify the SID and selected site names.");
        foreach (var device in devices.Where(device => device.Type.Equals("Airspy", StringComparison.OrdinalIgnoreCase)))
            warnings.Add($"{device.DisplayName} uses Airspy device args '{device.DeviceArgs}'. Verify this with the connected Airspy Mini before the first controlled TR start.");

        if (siteFilters.Count > 0)
        {
            var matched = systems.Select(s => s.SiteName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in siteFilters.Where(filter => !matched.Contains(filter)))
                warnings.Add($"No frequency table matched requested site '{filter}'.");
        }

        var allFrequencies = systems
            .SelectMany(site => site.FrequenciesMhz)
            .Distinct()
            .OrderBy(frequency => frequency)
            .ToList();
        var allControlChannels = systems
            .SelectMany(site => site.ControlChannelsMhz.Count > 0 ? site.ControlChannelsMhz : site.FrequenciesMhz)
            .Distinct()
            .OrderBy(frequency => frequency)
            .ToList();
        var requiredSampleRate = devices.Count > 0 ? devices.Max(device => device.SampleRate) : requestedSampleRate;
        var requiredPlan = PlanCoverage(allControlChannels, allControlChannels, RepeatedDevices(requiredSampleRate, allControlChannels.Count), allControlChannels.Count, allFrequencies);
        var availableSourceCount = Math.Max(1, devices.Count);
        var coveragePlan = PlanCoverage(allControlChannels, allControlChannels, devices, availableSourceCount, allFrequencies);
        var coveredFrequencies = coveragePlan.SelectMany(source => source.Covered).Distinct().ToHashSet();
        var systemDrafts = new List<SetupTrConfigSystemDto>();

        foreach (var site in systems)
        {
            var siteControlChannels = site.ControlChannelsMhz.Count > 0 ? site.ControlChannelsMhz : site.FrequenciesMhz;
            var siteSources = coveragePlan
                .Where(source => site.FrequenciesMhz.Any(source.Covered.Contains) || siteControlChannels.Any(source.Covered.Contains))
                .ToList();
            var omitted = site.FrequenciesMhz.Where(f => !coveredFrequencies.Contains(f)).ToList();
            var omittedControlChannels = siteControlChannels.Where(f => !coveredFrequencies.Contains(f)).ToList();
            var assignedSerials = siteSources.Select(source => source.Device.Serial).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var warningParts = new List<string>();
            if (assignedSerials.Count == 0)
                warningParts.Add("No SDR serial assigned.");
            if (omitted.Count > 0)
                warningParts.Add($"{omitted.Count} frequency/frequencies fall outside the selected SDR window(s).");
            if (omittedControlChannels.Count > 0)
                warningParts.Add($"{omittedControlChannels.Count} control channel(s) are not covered; add SDR bandwidth/serials before using this config.");
            if (site.ControlChannelsMhz.Count == 0)
                warningParts.Add("No explicit control channel marker was detected; all site frequencies were kept as fallback control channels.");

            systemDrafts.Add(new SetupTrConfigSystemDto(
                site.SystemName,
                site.ShortName,
                site.SiteName,
                site.FrequenciesMhz,
                siteControlChannels,
                siteSources.FirstOrDefault(source => siteControlChannels.Any(source.Covered.Contains))?.CenterHz ?? siteSources.FirstOrDefault()?.CenterHz ?? 0,
                string.Join(", ", assignedSerials),
                string.Join(" ", warningParts)));
        }

        var sourceDrafts = coveragePlan.Select((source, index) =>
        {
            var sourceSites = systems
                .Where(site => site.FrequenciesMhz.Any(source.Covered.Contains) || site.ControlChannelsMhz.Any(source.Covered.Contains))
                .Select(site => site.ShortName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var label = sourceSites.Count == 1 ? sourceSites[0] : $"source-{index + 1}";
            return new SetupTrConfigSourceDto(
                label,
                source.Device.Serial,
                source.Device.Type,
                source.Device.Driver,
                source.Device.DeviceArgs,
                source.CenterHz,
                source.Device.SampleRate,
                source.Device.Gain,
                source.Device.GainMode,
                source.Covered,
                allFrequencies.Where(f => !source.Covered.Contains(f)).ToList());
        }).ToList();

        if (requiredPlan.Count > coveragePlan.Count)
        {
            var detected = devices.Count > 0
                ? $"{devices.Count} SDR device{(devices.Count == 1 ? "" : "s")}"
                : "no detected SDR serials";
            warnings.Add($"Selected sites need {requiredPlan.Count} SDR source window(s) at {requiredSampleRate:N0} sps, but the plan has {coveragePlan.Count} window(s) from {detected}.");
        }
        if (devices.Count > requiredPlan.Count)
            warnings.Add($"{devices.Count - requiredPlan.Count} SDR device(s) were not needed by the generated site plan.");
        if (systemDrafts.Any(s => s.Warning.Contains("fall outside", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("The generated config continues with the largest possible covered frequency set. Review omitted frequencies before using it.");
        if (systemDrafts.Any(s => s.Warning.Contains("control channel", StringComparison.OrdinalIgnoreCase) && s.Warning.Contains("not covered", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("One or more selected sites have uncovered control channels. Do not start a baseline until source coverage is corrected.");

        return new SourcePlan(systemDrafts, sourceDrafts, warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private sealed record SourcePlan(
        IReadOnlyList<SetupTrConfigSystemDto> Systems,
        IReadOnlyList<SetupTrConfigSourceDto> Sources,
        IReadOnlyList<string> Warnings);

    public async Task<SetupValidationResult> SaveAsync(SetupTrConfigSaveRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ConfigJson))
            return new SetupValidationResult(false, "No TR config JSON was supplied.");

        using var _ = JsonDocument.Parse(request.ConfigJson);
        var coverage = TrConfigSourceCoverageValidator.Validate(request.ConfigJson);
        if (!coverage.Ok)
            return new SetupValidationResult(false, "TR config cannot start with the selected source plan: " + string.Join(" ", coverage.Blockers), coverage);

        var path = _config.TrunkRecorder.ConfigPath;
        string? backup = null;
        if (NeedsProtectedTrWrite(path))
        {
            backup = await InstallProtectedTrFileAsync(path, request.ConfigJson, ct);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            if (File.Exists(path))
            {
                backup = $"{path}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
                File.Copy(path, backup, overwrite: false);
            }
            await File.WriteAllTextAsync(path, NormalizeText(request.ConfigJson), Encoding.UTF8, ct);
        }
        _config.Setup.TrConfigured = true;
        await _talkgroups.GenerateTrCsvAsync(ct);
        await SaveConfigAsync(ct);
        return new SetupValidationResult(true, $"Saved TR config to {path}. A timestamped backup was created if a file already existed.", new { path, backup });
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
            ?? throw new FileNotFoundException("pizzawave_setup_admin.sh was not found; protected config writes are unavailable.");
    }

    private bool NeedsProtectedTrWrite(string path) =>
        !OperatingSystem.IsWindows() && path.StartsWith("/etc/trunk-recorder/", StringComparison.Ordinal);

    private async Task<string?> InstallProtectedTrFileAsync(string path, string contents, CancellationToken ct)
    {
        var stagingRoot = Path.Combine(_config.Storage.AppDataRoot, "protected-config");
        Directory.CreateDirectory(stagingRoot);
        var candidatePath = Path.Combine(stagingRoot, $"tr-config-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(candidatePath, NormalizeText(contents), Encoding.UTF8, ct);
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
                throw new InvalidOperationException($"Protected TR config helper failed with exit code {process.ExitCode}: {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
            return stdout.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Contains(".bak-", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { File.Delete(candidatePath); } catch { }
        }
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
            if (siteMatches.Count == 0 && siteBuckets.Count == 1 && requestedSites.Count == 0)
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
        var nextSiteRow = RadioReferenceSiteRowRegex().Match(plain[(start + siteName.Length)..]);
        if (nextSiteRow.Success)
            end = Math.Min(end, start + siteName.Length + nextSiteRow.Index);
        return plain[start..end];
    }

    private string BuildConfigJson(IReadOnlyList<SetupTrConfigSystemDto> systems, IReadOnlyList<SetupTrConfigSourceDto> sources, JsonObject? template = null)
    {
        if (template != null)
            return BuildConfigJsonFromTemplate(template, systems, sources);

        var root = new Dictionary<string, object?>
        {
            ["ver"] = 2,
            ["logDir"] = "/var/log/trunk-recorder",
            ["controlWarnRate"] = -1,
            ["audioStreaming"] = true,
            ["sources"] = sources.Select((source, index) => new Dictionary<string, object?>
            {
                ["center"] = source.CenterFrequency,
                ["rate"] = source.SampleRate,
                ["error"] = 0,
                ["gain"] = JsonScalar(source.Gain),
                ["digitalRecorders"] = TrRecorderCapacitySizer.EstimateForSetupSource(source, systems),
                ["driver"] = string.IsNullOrWhiteSpace(source.Driver) ? "osmosdr" : source.Driver,
                ["device"] = string.IsNullOrWhiteSpace(source.DeviceArgs) ? BuildDefaultDeviceArgs(source, index) : source.DeviceArgs
            }).ToList(),
            ["systems"] = systems.Select(system => new Dictionary<string, object?>
            {
                ["type"] = "p25",
                ["shortName"] = system.ShortName,
                ["control_channels"] = system.ControlChannelsMhz.Select(MhzToHz).ToList(),
                ["recordUnknown"] = false,
                ["recordUUVCalls"] = true,
                ["hideEncrypted"] = true,
                ["hideUnknownTalkgroups"] = false,
                ["talkgroupsFile"] = TalkgroupCatalogService.TrCsvPathForSystem(_config.TrunkRecorder.TalkgroupsPath, system.ShortName)
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

    private JsonObject? TryLoadTemplateConfig()
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static int? TemplateSampleRate(JsonObject? root)
    {
        var rate = (root?["sources"] as JsonArray)?.OfType<JsonObject>()
            .Select(source => source["rate"]?.GetValue<int?>())
            .FirstOrDefault(value => value.GetValueOrDefault() > 0);
        return rate.GetValueOrDefault() > 0 ? rate : null;
    }

    private string BuildConfigJsonFromTemplate(JsonObject root, IReadOnlyList<SetupTrConfigSystemDto> systems, IReadOnlyList<SetupTrConfigSourceDto> sources)
    {
        RemoveSftpSettings(root);
        root["controlWarnRate"] = -1;
        root["audioStreaming"] = true;
        PatchSources(root, sources, systems);
        PatchSystems(root, systems, _config.TrunkRecorder.TalkgroupsPath);
        PatchCallstream(root, systems);
        return root.ToJsonString(EngineConfig.JsonOptions());
    }

    private static void PatchSources(JsonObject root, IReadOnlyList<SetupTrConfigSourceDto> sources, IReadOnlyList<SetupTrConfigSystemDto> systems)
    {
        var existingSources = root["sources"] as JsonArray;
        var templateSource = existingSources?.OfType<JsonObject>().FirstOrDefault();
        var patched = new JsonArray();
        for (var i = 0; i < sources.Count; i++)
        {
            var draft = sources[i];
            var source = CloneObject(existingSources?.ElementAtOrDefault(i) as JsonObject ?? templateSource);
            source["center"] = draft.CenterFrequency;
            source["rate"] = draft.SampleRate;
            if (!source.ContainsKey("error"))
                source["error"] = 0;
            if (!source.ContainsKey("gain") || draft.Type.Equals("Airspy", StringComparison.OrdinalIgnoreCase))
                source["gain"] = JsonScalar(draft.Gain);
            TrRecorderCapacitySizer.EnsureJsonSourceDigitalRecorders(
                source,
                TrRecorderCapacitySizer.EstimateForSetupSource(draft, systems),
                $"source {i}");
            if (!source.ContainsKey("driver"))
                source["driver"] = "osmosdr";
            source["device"] = PatchDevice(source["device"]?.GetValue<string>(), draft, i);
            patched.Add(source);
        }
        root["sources"] = patched;
    }

    private static void PatchSystems(JsonObject root, IReadOnlyList<SetupTrConfigSystemDto> systems, string talkgroupsPath)
    {
        var existingSystems = root["systems"] as JsonArray;
        var templateSystem = existingSystems?.OfType<JsonObject>().FirstOrDefault();
        var patched = new JsonArray();
        for (var i = 0; i < systems.Count; i++)
        {
            var draft = systems[i];
            var system = CloneObject(existingSystems?.ElementAtOrDefault(i) as JsonObject ?? templateSystem);
            system["type"] = "p25";
            system["shortName"] = draft.ShortName;
            system["talkgroupsFile"] = TalkgroupCatalogService.TrCsvPathForSystem(talkgroupsPath, draft.ShortName);
            system["control_channels"] = LongArray(draft.ControlChannelsMhz.Select(MhzToHz));
            system["recordUnknown"] = false;
            system["recordUUVCalls"] = true;
            system["hideEncrypted"] = true;
            system["hideUnknownTalkgroups"] = false;
            system.Remove("channels");
            patched.Add(system);
        }
        root["systems"] = patched;
    }

    private static JsonArray LongArray(IEnumerable<long> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);
        return array;
    }

    private void PatchCallstream(JsonObject root, IReadOnlyList<SetupTrConfigSystemDto> systems)
    {
        var plugins = root["plugins"] as JsonArray ?? [];
        root["plugins"] = plugins;
        JsonObject? callstream = null;
        foreach (var plugin in plugins.OfType<JsonObject>())
        {
            if (string.Equals(plugin["name"]?.GetValue<string>(), "callstream", StringComparison.OrdinalIgnoreCase))
            {
                callstream = plugin;
                break;
            }
        }
        if (callstream == null)
        {
            callstream = [];
            plugins.Add(callstream);
        }
        callstream["name"] = "callstream";
        callstream["library"] = callstream["library"]?.GetValue<string>() ?? "libcallstream.so";
        callstream["host"] = _config.Ingest.CallstreamBind;
        callstream["port"] = _config.Ingest.CallstreamPort;
        var clients = new JsonArray();
        JsonObject client = [];
        client["address"] = _config.Ingest.CallstreamBind;
        client["port"] = _config.Ingest.CallstreamPort;
        clients.Add(client);
        callstream["clients"] = clients;
        if (systems.Count > 0)
        {
            callstream["streams"] = new JsonArray(systems.Select(system =>
            {
                JsonObject stream = [];
                stream["TGID"] = 0;
                stream["shortName"] = system.ShortName;
                return (JsonNode?)stream;
            }).ToArray());
        }
    }

    private static JsonObject CloneObject(JsonObject? source)
    {
        if (source == null)
            return [];
        return JsonNode.Parse(source.ToJsonString()) as JsonObject ?? [];
    }

    private static string PatchDevice(string? existing, SetupTrConfigSourceDto draft, int index)
    {
        var target = string.IsNullOrWhiteSpace(draft.Serial) ? index.ToString() : draft.Serial;
        if (draft.Type.Equals("RTL-SDR", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(existing))
                return string.IsNullOrWhiteSpace(draft.DeviceArgs) ? $"rtl={target}" : draft.DeviceArgs;
            return Regex.IsMatch(existing, @"rtl=[^,\s]+", RegexOptions.IgnoreCase)
                ? Regex.Replace(existing, @"rtl=[^,\s]+", $"rtl={target}", RegexOptions.IgnoreCase)
                : (string.IsNullOrWhiteSpace(draft.DeviceArgs) ? $"rtl={target}" : draft.DeviceArgs);
        }
        if (!string.IsNullOrWhiteSpace(draft.DeviceArgs))
            return draft.DeviceArgs;
        if (string.IsNullOrWhiteSpace(existing))
            return BuildDefaultDeviceArgs(draft, index);
        return existing;
    }

    private static JsonNode? JsonScalar(string value) =>
        int.TryParse(value, out var intValue) ? JsonValue.Create(intValue) :
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var doubleValue) ? JsonValue.Create(doubleValue) :
        JsonValue.Create(value);

    private static string BuildDefaultDeviceArgs(SetupTrConfigSourceDto source, int index)
    {
        var selector = string.IsNullOrWhiteSpace(source.Serial) ? index.ToString() : source.Serial;
        return source.Type.Equals("Airspy", StringComparison.OrdinalIgnoreCase)
            ? $"airspy={selector}"
            : $"rtl={selector},buflen=65536";
    }

    private static void RemoveSftpSettings(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    if (key.Contains("sftp", StringComparison.OrdinalIgnoreCase))
                        obj.Remove(key);
                    else
                        RemoveSftpSettings(obj[key]);
                }
                break;
            }
            case JsonArray array:
            {
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is JsonObject plugin &&
                        plugin["name"]?.GetValue<string>().Contains("sftp", StringComparison.OrdinalIgnoreCase) == true)
                        array.RemoveAt(i);
                    else
                        RemoveSftpSettings(array[i]);
                }
                break;
            }
        }
    }

    private static List<CoverageResult> PlanCoverage(IReadOnlyList<double> freqsMhz, IReadOnlyList<double> controlChannelsMhz, IReadOnlyList<SelectedSdrDevice> devices, int? maxSources = null, IReadOnlyList<double>? displayFreqsMhz = null)
    {
        var sorted = freqsMhz.Distinct().OrderBy(f => f).ToList();
        if (sorted.Count == 0)
            return [];
        var displayFreqs = (displayFreqsMhz is { Count: > 0 } ? displayFreqsMhz : freqsMhz)
            .Distinct()
            .OrderBy(f => f)
            .ToList();
        var orderedDevices = devices.Count > 0
            ? devices.OrderByDescending(device => device.SampleRate).ThenBy(device => device.Index).ToList()
            : RepeatedDevices(DefaultSampleRate, 1).ToList();
        var sourceCount = Math.Max(1, Math.Min(maxSources ?? orderedDevices.Count, sorted.Count));
        var remaining = sorted.ToList();
        var remainingControls = controlChannelsMhz.Distinct().OrderBy(f => f).ToList();
        var results = new List<CoverageResult>();
        for (var i = 0; i < sourceCount && remaining.Count > 0 && i < orderedDevices.Count; i++)
        {
            var coverage = BestCoverage(displayFreqs, sorted, remaining, remainingControls, orderedDevices[i]);
            results.Add(coverage);
            remaining = remaining.Where(f => !coverage.Covered.Contains(f)).ToList();
            remainingControls = remainingControls.Where(f => !coverage.Covered.Contains(f)).ToList();
        }
        return results;
    }

    private static CoverageResult BestCoverage(IReadOnlyList<double> displayFreqsMhz, IReadOnlyList<double> candidateFreqsMhz, IReadOnlyList<double> targetFreqsMhz, IReadOnlyList<double> controlChannelsMhz, SelectedSdrDevice device)
    {
        var sorted = targetFreqsMhz.Distinct().OrderBy(f => f).ToList();
        var spanMhz = EffectiveTrSpanMhz(device.SampleRate);
        var bestCoveredTargets = new List<double> { sorted[0] };
        var bestCoveredControls = controlChannelsMhz.Where(f => Math.Abs(f - sorted[0]) <= 0.000001).ToList();
        foreach (var candidate in candidateFreqsMhz.Concat(targetFreqsMhz).Distinct().OrderBy(f => f))
        {
            var candidateCovered = sorted.Where(f => f >= candidate && f <= candidate + spanMhz).ToList();
            if (candidateCovered.Count == 0)
                continue;
            var coveredControls = controlChannelsMhz.Where(f => f >= candidate && f <= candidate + spanMhz).ToList();
            if (coveredControls.Count > bestCoveredControls.Count ||
                (coveredControls.Count == bestCoveredControls.Count && candidateCovered.Count > bestCoveredTargets.Count) ||
                (coveredControls.Count == bestCoveredControls.Count && candidateCovered.Count == bestCoveredTargets.Count && candidateCovered.Last() - candidateCovered.First() < bestCoveredTargets.Last() - bestCoveredTargets.First()))
            {
                bestCoveredTargets = candidateCovered;
                bestCoveredControls = coveredControls;
            }
        }
        var min = bestCoveredTargets.First();
        var max = bestCoveredTargets.Last();
        var center = MhzToHz((min + max) / 2);
        var centerMhz = center / 1_000_000.0;
        var halfSpan = spanMhz / 2.0;
        var covered = displayFreqsMhz.Where(f => f >= centerMhz - halfSpan && f <= centerMhz + halfSpan).Distinct().OrderBy(f => f).ToList();
        return new CoverageResult(center, device, covered);
    }

    private static double EffectiveTrSpanMhz(int sampleRate) =>
        Math.Max(0, sampleRate) / 1_000_000.0 * TrUsableBandwidthFactor;

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

    private static List<string> InferSiteRows(string plain)
    {
        var names = new List<string>();
        foreach (Match row in RadioReferenceFullSiteRowRegex().Matches(plain))
        {
            var tail = row.Groups["tail"].Value.Trim();
            var firstFrequency = FrequencyRegex().Match(tail);
            if (!firstFrequency.Success)
                continue;
            var name = tail[..firstFrequency.Index].Trim();
            name = Regex.Replace(name, @"\s+", " ");
            if (name.Length is <= 2 or >= 100)
                continue;
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }
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

    private static IReadOnlyList<string> SiteFilters(IReadOnlyList<string>? siteNameList, string? siteNames) =>
        siteNameList is { Count: > 0 }
            ? siteNameList
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : SplitList(siteNames);

    private static long MhzToHz(double mhz) => (long)Math.Round(mhz * 1_000_000);

    private static IReadOnlyList<SelectedSdrDevice> NormalizeDevices(IReadOnlyList<SetupSdrDeviceDto>? devices, string? serials, int requestedSampleRate)
    {
        var selected = (devices ?? [])
            .Where(device => !string.IsNullOrWhiteSpace(device.Type) || !string.IsNullOrWhiteSpace(device.Serial) || !string.IsNullOrWhiteSpace(device.DeviceArgs))
            .Select((device, ordinal) => NormalizeDevice(device, ordinal, requestedSampleRate))
            .ToList();
        if (selected.Count > 0)
            return selected;

        var splitSerials = SplitList(serials);
        if (splitSerials.Count == 0)
            return [RtlSelectedDevice(0, string.Empty, requestedSampleRate)];
        return splitSerials.Select((serial, index) => RtlSelectedDevice(index, serial, requestedSampleRate)).ToList();
    }

    private static SelectedSdrDevice NormalizeDevice(SetupSdrDeviceDto device, int ordinal, int requestedSampleRate)
    {
        var type = string.IsNullOrWhiteSpace(device.Type) ? "RTL-SDR" : device.Type.Trim();
        var defaultRate = device.DefaultSampleRate > 0 ? device.DefaultSampleRate : requestedSampleRate;
        var sampleRate = requestedSampleRate > 0
            ? requestedSampleRate
            : type.Equals("Airspy", StringComparison.OrdinalIgnoreCase)
                ? (defaultRate > 0 ? defaultRate : 6_000_000)
                : (defaultRate > 0 ? defaultRate : DefaultSampleRate);
        if (type.Equals("Airspy", StringComparison.OrdinalIgnoreCase))
            sampleRate = AirspyRuntimeSampleRate(sampleRate, device.SampleRateOptions);
        var serial = device.Serial?.Trim() ?? string.Empty;
        var deviceArgs = string.IsNullOrWhiteSpace(device.DeviceArgs)
            ? BuildDeviceArgs(type, serial, device.Index >= 0 ? device.Index : ordinal)
            : device.DeviceArgs.Trim();
        return new SelectedSdrDevice(
            device.Index >= 0 ? device.Index : ordinal,
            type,
            serial,
            string.IsNullOrWhiteSpace(device.Label) ? $"{type} #{ordinal}" : device.Label.Trim(),
            string.IsNullOrWhiteSpace(device.Driver) ? "osmosdr" : device.Driver.Trim(),
            deviceArgs,
            sampleRate,
            string.IsNullOrWhiteSpace(device.GainMode) ? DefaultGainMode(type) : device.GainMode.Trim(),
            string.IsNullOrWhiteSpace(device.DefaultGain) ? DefaultGain(type) : device.DefaultGain.Trim());
    }

    private static IReadOnlyList<SelectedSdrDevice> RepeatedDevices(int sampleRate, int count) =>
        Enumerable.Range(0, Math.Max(1, count)).Select(index => RtlSelectedDevice(index, string.Empty, sampleRate)).ToList();

    private static SelectedSdrDevice RtlSelectedDevice(int index, string serial, int sampleRate) =>
        new(index, "RTL-SDR", serial, string.IsNullOrWhiteSpace(serial) ? $"RTL-SDR #{index}" : $"RTL-SDR {serial}", "osmosdr", BuildDeviceArgs("RTL-SDR", serial, index), sampleRate > 0 ? sampleRate : DefaultSampleRate, "rtl-tuner-gain", "32");

    private static string BuildDeviceArgs(string type, string serial, int index)
    {
        var selector = string.IsNullOrWhiteSpace(serial) ? index.ToString() : serial;
        return type.Equals("Airspy", StringComparison.OrdinalIgnoreCase)
            ? $"airspy={selector}"
            : $"rtl={selector},buflen=65536";
    }

    private static string DefaultGainMode(string type) =>
        type.Equals("Airspy", StringComparison.OrdinalIgnoreCase) ? "airspy-linearity" : "rtl-tuner-gain";

    private static string DefaultGain(string type) =>
        type.Equals("Airspy", StringComparison.OrdinalIgnoreCase) ? "15" : "32";

    private static int AirspyRuntimeSampleRate(int requestedSampleRate, IReadOnlyList<int>? supportedSampleRates)
    {
        var rates = (supportedSampleRates is { Count: > 0 } ? supportedSampleRates : [2_500_000, 3_000_000, 6_000_000, 10_000_000])
            .Where(rate => rate > 0)
            .Distinct()
            .Order()
            .ToList();
        if (requestedSampleRate <= 0)
            return rates.FirstOrDefault();
        if (rates.Contains(requestedSampleRate))
            return requestedSampleRate;
        var nextHigher = rates.FirstOrDefault(rate => rate >= requestedSampleRate);
        if (nextHigher > 0)
            return nextHigher;
        return rates.LastOrDefault(requestedSampleRate);
    }

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";

    [GeneratedRegex(@"(?<freq>\b\d{2,4}\.\d{3,6})(?<marker>[ac])?", RegexOptions.IgnoreCase)]
    private static partial Regex FrequencyRegex();

    [GeneratedRegex(@"(?:Site|RFSS|Simulcast)\s+[:#]?\s*(?<name>[A-Za-z0-9][A-Za-z0-9 ._\-/]{2,60})", RegexOptions.IgnoreCase)]
    private static partial Regex SiteNameRegex();

    [GeneratedRegex(@"\s+\d+\s+\(\d+\)\s+\d+\s+\([0-9A-Fa-f]+\)\s+")]
    private static partial Regex RadioReferenceSiteRowRegex();

    [GeneratedRegex(@"(?:^|\s)\d+\s+\(\d+\)\s+\d+\s+\([0-9A-Fa-f]+\)\s+(?<tail>.*?)(?=\s+\d+\s+\(\d+\)\s+\d+\s+\([0-9A-Fa-f]+\)\s+|$)", RegexOptions.IgnoreCase)]
    private static partial Regex RadioReferenceFullSiteRowRegex();

    private sealed record CoverageResult(long CenterHz, SelectedSdrDevice Device, IReadOnlyList<double> Covered);

    private sealed record SelectedSdrDevice(
        int Index,
        string Type,
        string Serial,
        string DisplayName,
        string Driver,
        string DeviceArgs,
        int SampleRate,
        string GainMode,
        string Gain);

    private sealed record SiteParseResult(string SystemName, string ShortName, string SiteName)
    {
        public List<double> FrequenciesMhz { get; set; } = [];
        public List<double> ControlChannelsMhz { get; set; } = [];
    }
}
