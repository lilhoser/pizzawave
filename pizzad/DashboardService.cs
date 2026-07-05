using System.Globalization;
using System.Text.RegularExpressions;
namespace pizzad;

public sealed class DashboardService
{
    private static readonly TimeSpan DashboardCacheTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DashboardBuildTimeout = TimeSpan.FromSeconds(30);
    private readonly EngineDatabase _database;
    private readonly EngineConfig _config;
    private readonly GeocodingService _geocoding;
    private readonly SemaphoreSlim _dashboardCacheGate = new(1, 1);
    private DashboardCacheEntry? _dashboardCache;
    private Task<DashboardDto>? _dashboardBuildTask;
    private string _dashboardBuildKey = string.Empty;

    public DashboardService(
        EngineDatabase database,
        EngineConfig config,
        GeocodingService geocoding)
    {
        _database = database;
        _config = config;
        _geocoding = geocoding;
    }

    public async Task<DashboardDto> BuildDashboardAsync(long start, long end, CancellationToken ct)
    {
        var cacheKey = DashboardCacheKey(start, end);
        var now = DateTimeOffset.UtcNow;
        Task<DashboardDto>? buildTask = null;
        await _dashboardCacheGate.WaitAsync(ct);
        try
        {
            if (_dashboardCache is { } cached &&
                cached.Key == cacheKey &&
                now - cached.CreatedAt <= DashboardCacheTtl)
                return cached.Value;

            if (_dashboardBuildTask is { IsCompleted: false } running && _dashboardBuildKey == cacheKey)
                buildTask = running;
            else
            {
                var normalized = NormalizeDashboardRange(start, end);
                _dashboardBuildKey = cacheKey;
                _dashboardBuildTask = BuildDashboardWithTimeoutAsync(normalized.Start, normalized.End);
                buildTask = _dashboardBuildTask;
            }
        }
        finally
        {
            _dashboardCacheGate.Release();
        }

        var result = await buildTask.WaitAsync(ct);
        await _dashboardCacheGate.WaitAsync(ct);
        try
        {
            if (_dashboardBuildKey == cacheKey)
                _dashboardCache = new DashboardCacheEntry(cacheKey, DateTimeOffset.UtcNow, result);
        }
        finally
        {
            _dashboardCacheGate.Release();
        }
        return result;
    }

    public async Task InvalidateCacheAsync(CancellationToken ct)
    {
        await _dashboardCacheGate.WaitAsync(ct);
        try
        {
            _dashboardCache = null;
            _dashboardBuildTask = null;
            _dashboardBuildKey = string.Empty;
        }
        finally
        {
            _dashboardCacheGate.Release();
        }
    }

    private async Task<DashboardDto> BuildDashboardWithTimeoutAsync(long start, long end)
    {
        using var timeout = new CancellationTokenSource(DashboardBuildTimeout);
        return await BuildDashboardCoreAsync(start, end, timeout.Token);
    }

    private async Task<DashboardDto> BuildDashboardCoreAsync(long start, long end, CancellationToken ct)
    {
        var calls = ApplyProfile(await _database.ListCallsAsync(start, end, null, ct));
        var alerts = (await _database.ListAlertMatchesAsync(start, end, ct)).Where(a => Allows(a.Category, a.Talkgroup)).ToList();
        var allowedCallIds = calls.Select(c => c.Id).ToHashSet();
        var incidents = (await ListIncidentsAsync(start, end, ct)).Where(i => i.Calls.Any(c => allowedCallIds.Contains(c.CallId))).ToList();
        var tokenUsage = await _database.GetTokenUsageAsync(start, end, ct);
        var total = calls.Count;
        var alertRate = total == 0 ? 0 : alerts.Select(a => a.CallId).Distinct().Count() * 100.0 / total;
        var qualityProblems = calls.Count(IsProblemTranscript);
        var problemCalls = calls.Where(IsProblemTranscript).ToList();
        var qualityBreakdown = BuildQualityBreakdown(problemCalls);
        var topProblemSystem = BuildTopProblemSystem(calls);
        var healthRows = await _database.ListHealthSamplesAsync(start, end, ct);
        var decodeKpis = BuildDecodeKpis(healthRows);
        var busiest = calls
            .GroupBy(c => DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().Hour)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        return new DashboardDto
        {
            Kpis =
            [
                new("Total Calls", total.ToString("N0", CultureInfo.CurrentCulture), "Selected range"),
                new("Alert Rate", $"{alertRate:F1}%", "Calls matching alert rules"),
                new("Incidents", incidents.Count.ToString("N0", CultureInfo.CurrentCulture), "Accepted AI/RAG incidents in selected range"),
                new("Token Usage", FormatCompact(tokenUsage.Summary.TotalTokens), $"{tokenUsage.Summary.Requests:N0} LM request(s); ${tokenUsage.Summary.EstimatedStandardCost:F2} std estimate"),
                new("Quality Problems", qualityProblems.ToString("N0", CultureInfo.CurrentCulture), total == 0 ? "No calls in selected range" : $"{qualityProblems * 100.0 / total:F1}% of calls; {qualityBreakdown}"),
                new("Top Problem System", topProblemSystem.Label, topProblemSystem.ValueText),
                decodeKpis.Global,
                decodeKpis.Rate,
                decodeKpis.Systems,
                new("Busiest Hour", busiest == null ? "--" : $"{busiest.Key:00}:00", busiest == null ? "No calls" : $"{busiest.Count():N0} calls"),
                new("Unique Talkgroups", calls.Select(c => c.Talkgroup).Distinct().Count().ToString("N0", CultureInfo.CurrentCulture), "Heard in selected range")
            ],
            VolumeByHourCategory = BuildVolume(calls),
            LocationHeat = await BuildLocationHeatAsync(calls, incidents, start, end, ct),
            QualityByHour = BuildQuality(calls),
            ProblemTalkgroups = BuildProblemTalkgroups(calls),
            InaudibleBySystem = BuildInaudibleBySystem(calls),
            CategoryShare = BuildCategoryShare(calls),
            TopTalkgroups = BuildTopTalkgroups(calls, start, end),
            Alerts = alerts,
            Incidents = incidents,
            TokenUsage = tokenUsage.Summary
        };
    }

    private static (long Start, long End) NormalizeDashboardRange(long start, long end) =>
        (RoundDownMinute(start), RoundDownMinute(end));

    private static string DashboardCacheKey(long start, long end)
    {
        var normalized = NormalizeDashboardRange(start, end);
        return $"{normalized.Start}:{normalized.End}";
    }

    private static long RoundDownMinute(long value) => value - value % 60;

    public async Task<CategoryPageDto> BuildCategoryPageAsync(string category, string groupBy, long start, long end, string searchQuery, CancellationToken ct)
    {
        category = NormalizeCategory(category);
        var groups = (await _database.ListCategoryTalkgroupsAsync(start, end, category, ct))
            .Where(g => Allows(category, g.Talkgroup))
            .ToList();
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var matchingCalls = await _database.ListCategorySearchCallsAsync(start, end, category, searchQuery, ct);
            var callsByTalkgroup = matchingCalls
                .Where(call => Allows(category, call.SystemShortName, call.Talkgroup))
                .GroupBy(call => call.Talkgroup)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(call => call.StartTime).ThenByDescending(call => call.Id).ToList());
            groups = groups
                .Where(group => callsByTalkgroup.ContainsKey(group.Talkgroup) || group.Label.Contains(searchQuery, StringComparison.OrdinalIgnoreCase))
                .Select(group => callsByTalkgroup.TryGetValue(group.Talkgroup, out var calls)
                    ? new CategoryGroupDto(group.Label, calls, group.Talkgroup, group.Count, group.LastHeard)
                    : group)
                .ToList();
        }
        return new CategoryPageDto(category, "talkgroup", groups, [], []);
    }

    public async Task<CategoryGroupDto> BuildCategoryTalkgroupCallsAsync(string category, long talkgroup, long start, long end, int limit, CancellationToken ct)
    {
        category = NormalizeCategory(category);
        if (!Allows(category, talkgroup))
            return new CategoryGroupDto($"TG {talkgroup}", [], talkgroup, 0, 0);

        var calls = await _database.ListCategoryTalkgroupCallsAsync(start, end, category, talkgroup, limit, ct);
        var label = calls.Count > 0 ? GetTalkgroupLabel(calls[0]) : $"TG {talkgroup}";
        return new CategoryGroupDto(label, calls, talkgroup, calls.Count, calls.Select(c => c.StartTime).DefaultIfEmpty(0).Max());
    }

    public Task<List<IncidentDto>> ListIncidentsAsync(long start, long end, CancellationToken ct)
    {
        return _database.ListIncidentsAsync(start, end, ct);
    }

    private List<EngineCall> ApplyProfile(IEnumerable<EngineCall> calls) => calls.Where(c => Allows(c.Category, c.SystemShortName, c.Talkgroup)).ToList();

    private bool Allows(string category, long talkgroup)
        => Allows(category, string.Empty, talkgroup);

    private bool Allows(string category, string? systemShortName, long talkgroup)
    {
        var profile = _config.Profiles.Items.FirstOrDefault(p => p.Id == _config.Profiles.ActiveProfileId);
        if (profile == null) return true;
        var setting = FindSetting(profile, systemShortName, talkgroup);
        if (setting?.Enabled == false) return false;
        if (profile.AllowedTalkgroups.Count > 0 && !profile.AllowedTalkgroups.Contains(talkgroup)) return false;
        var effectiveCategory = string.IsNullOrWhiteSpace(setting?.Category) ? category : setting!.Category;
        return NormalizeCategory(effectiveCategory) switch
        {
            "police" => profile.IncludePolice,
            "fire" => profile.IncludeFire,
            "ems" => profile.IncludeEMS,
            "traffic" => profile.IncludeTraffic,
            _ => profile.IncludeOther
        };
    }

    private static ProfileTalkgroupSetting? FindSetting(ProcessingProfile profile, string? systemShortName, long talkgroup)
    {
        var rows = profile.Talkgroups.Where(t => t.Id == talkgroup).ToList();
        if (rows.Count == 0)
            return null;
        var exactKey = TalkgroupCatalogService.CatalogKey(systemShortName, talkgroup);
        return rows.LastOrDefault(row => string.Equals(TalkgroupCatalogService.SettingKey(row), exactKey, StringComparison.OrdinalIgnoreCase))
            ?? rows.LastOrDefault(row => string.Equals(TalkgroupCatalogService.SettingKey(row), talkgroup.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            ?? rows.LastOrDefault(row => string.IsNullOrWhiteSpace(row.SystemShortName))
            ?? rows[^1];
    }

    private static IReadOnlyList<HourCategoryDto> BuildVolume(List<EngineCall> calls) =>
        calls.GroupBy(c => new { DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().Hour, c.Category })
            .Select(g => new HourCategoryDto(g.Key.Hour, g.Key.Category, g.Count()))
            .OrderBy(r => r.Hour)
            .ThenBy(r => r.Category)
            .ToList();

    private async Task<IReadOnlyList<LocationHeatDto>> BuildLocationHeatAsync(List<EngineCall> calls, List<IncidentDto> incidents, long start, long end, CancellationToken ct)
    {
        if (calls.Count == 0)
            return [];

        var allowedCallIds = calls.Select(c => c.Id).ToHashSet();
        var rows = (await _database.ListCallLocationsAsync(start, end, ct))
            .Where(r => allowedCallIds.Contains(r.CallId))
            .Where(r => !string.IsNullOrWhiteSpace(r.GeocodeProvider))
            .Where(r => !string.Equals(r.GeocodeProvider, "none", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
            return [];

        var incidentCallIds = incidents.SelectMany(i => i.Calls.Select(c => c.CallId)).ToHashSet();
        const int maxMapGroups = 120;
        var grouped = rows
            .GroupBy(r => $"{r.AreaId}|{r.NormalizedKey}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rows = g.ToList();
                var first = rows[0];
                var allCallIds = rows.Select(r => r.CallId).Distinct().ToList();
                var category = rows.GroupBy(r => NormalizeCategory(r.Category))
                    .OrderByDescending(r => r.Count())
                    .ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key;
                return new LocationGroup(
                    first.AreaId,
                    first.AreaLabel,
                    first.AreaSystemShortName,
                    first.LocationText,
                    first.GeocodeQuery,
                    first.GeocodeDisplayName,
                    first.GeocodeProvider,
                    first.GeocodePrecision,
                    first.GeocodeConfidence,
                    first.Latitude,
                    first.Longitude,
                    rows.Select(r => r.CallId).Distinct().Count(),
                    rows.Max(r => r.StartTime),
                    category,
                    allCallIds,
                    allCallIds.OrderByDescending(id => id).Take(8).ToList(),
                    rows
                        .GroupBy(r => r.CallId)
                        .Select(r => r.First())
                        .OrderByDescending(r => r.StartTime)
                        .Take(5)
                        .Select(r => new LocationHeatCallDto(
                            r.CallId,
                            r.StartTime,
                            NormalizeCategory(r.Category),
                            string.IsNullOrWhiteSpace(r.TalkgroupName) ? $"TG {r.Talkgroup}" : r.TalkgroupName,
                            PreviewTranscript(r.Transcription),
                            $"/api/v1/calls/{r.CallId}/audio"))
                        .ToList());
            })
            .Where(r => r.Count > 0)
            .ToList();
        await AddIncidentFallbackLocationGroupsAsync(grouped, incidents, ct);

        grouped = grouped
            .DistinctBy(r => $"{r.AreaId}|{TranscriptLocationService.NormalizeLocationKey(r.Location)}", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(r => r.LinkCallIds.Any(incidentCallIds.Contains))
            .ThenByDescending(r => r.Count)
            .ThenByDescending(r => r.LastHeard)
            .Take(maxMapGroups)
            .ToList();

        var max = Math.Max(1, grouped.Select(g => g.Count).DefaultIfEmpty(0).Max());
        return grouped.Select(group =>
        {
            var callIds = group.LinkCallIds.ToHashSet();
            var incidentLinks = incidents
                .Where(i => i.Calls.Any(c => callIds.Contains(c.CallId)))
                .OrderByDescending(i => i.LastSeen)
                .Select(i => new LocationHeatIncidentDto(i.Id, string.IsNullOrWhiteSpace(i.Title) ? "Radio incident" : i.Title))
                .DistinctBy(i => i.IncidentId)
                .Take(8)
                .ToList();
            var incidentTitles = incidentLinks.Select(i => i.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new LocationHeatDto(
                group.AreaId,
                group.AreaLabel,
                group.AreaSystemShortName,
                group.Location,
                group.GeocodeQuery,
                group.GeocodeDisplayName,
                group.GeocodeProvider,
                group.GeocodePrecision,
                group.GeocodeConfidence,
                group.Latitude,
                group.Longitude,
                group.Count,
                group.Count / (double)max,
                group.LastHeard,
                group.Category,
                group.DisplayCallIds,
                incidentTitles,
                incidentLinks,
                group.SourceCalls);
        }).ToList();
    }

    private async Task AddIncidentFallbackLocationGroupsAsync(List<LocationGroup> groups, List<IncidentDto> incidents, CancellationToken ct)
    {
        var alreadyLinked = groups
            .SelectMany(group => incidents
                .Where(incident => incident.Calls.Any(call => group.LinkCallIds.Contains(call.CallId)))
                .Select(incident => incident.Id))
            .ToHashSet();

        foreach (var incident in incidents.Where(i => !alreadyLinked.Contains(i.Id)).Take(80))
        {
            var system = incident.Calls
                .Select(c => c.SystemShortName)
                .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
            var area = TranscriptLocationService.ResolveArea(system, _config.Locations.MonitoredAreas);
            if (area == null)
                continue;

            var text = $"{incident.Title}. {incident.Detail}";
            foreach (var location in TranscriptLocationService.ExtractLocations(text).Take(2))
            {
                var geocode = await _geocoding.GetCachedAsync(location, area, ct);
                if (geocode == null || string.Equals(geocode.Provider, "none", StringComparison.OrdinalIgnoreCase))
                    continue;

                var callIds = incident.Calls.Select(c => c.CallId).Distinct().ToList();
                var sourceCalls = incident.Calls
                    .OrderByDescending(c => c.RawTimestamp)
                    .Take(5)
                    .Select(c => new LocationHeatCallDto(
                        c.CallId,
                        c.RawTimestamp,
                        NormalizeCategory(c.Category),
                        string.IsNullOrWhiteSpace(c.TalkgroupName) ? "TG" : c.TalkgroupName,
                        PreviewTranscript(c.Transcript),
                        c.AudioUrl))
                    .ToList();

                groups.Add(new LocationGroup(
                    area.AreaId,
                    area.AreaLabel,
                    area.SystemShortName,
                    location,
                    geocode.Query,
                    geocode.DisplayName,
                    geocode.Provider,
                    geocode.Precision,
                    geocode.Confidence,
                    geocode.Latitude,
                    geocode.Longitude,
                    Math.Max(1, callIds.Count),
                    incident.LastSeen,
                    NormalizeCategory(incident.Category),
                    callIds,
                    callIds.OrderByDescending(id => id).Take(8).ToList(),
                    sourceCalls));
                break;
            }
        }
    }

    private static IReadOnlyList<QualityHourDto> BuildQuality(List<EngineCall> calls) =>
        calls.GroupBy(c => DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().Hour)
            .Select(g => new QualityHourDto(
                g.Key,
                g.Count(IsTranscriptEmpty),
                g.Count(IsTranscriptFailureHint),
                g.Count(IsTranscriptInaudibleHint),
                g.Count(IsTranscriptShort)))
            .OrderBy(r => r.Hour)
            .ToList();

    private static IReadOnlyList<BarStatDto> BuildProblemTalkgroups(List<EngineCall> calls)
    {
        var problems = calls.Where(IsProblemTranscript).ToList();
        var max = Math.Max(1, problems.GroupBy(c => c.Talkgroup).Select(g => g.Count()).DefaultIfEmpty(0).Max());
        return problems.GroupBy(c => c.Talkgroup)
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new BarStatDto(GetTalkgroupLabel(g.First()), g.Count(), g.Count() / (double)max, $"{g.Count():N0} problems"))
            .ToList();
    }

    private static IReadOnlyList<BarStatDto> BuildInaudibleBySystem(List<EngineCall> calls)
    {
        var rows = calls.GroupBy(c => string.IsNullOrWhiteSpace(c.SystemShortName) ? "Unknown system" : c.SystemShortName)
            .Select(g =>
            {
                var total = g.Count();
                var inaudible = g.Count(IsTranscriptInaudibleHint);
                return new { System = g.Key, Total = total, Inaudible = inaudible };
            })
            .Where(r => r.Inaudible > 0)
            .OrderByDescending(r => r.Inaudible)
            .ToList();
        var max = Math.Max(1, rows.Select(r => r.Inaudible).DefaultIfEmpty(0).Max());
        return rows.Select(r => new BarStatDto(r.System, r.Inaudible, r.Inaudible / (double)max, $"{r.Inaudible:N0}/{r.Total:N0} ({(r.Inaudible * 100.0 / Math.Max(1, r.Total)):F1}%)"))
            .ToList();
    }

    private static string BuildQualityBreakdown(List<EngineCall> problemCalls)
    {
        if (problemCalls.Count == 0) return "no quality problems";
        var parts = new[]
        {
            (Label: "inaudible", Count: problemCalls.Count(IsTranscriptInaudibleHint)),
            (Label: "short", Count: problemCalls.Count(IsTranscriptShort)),
            (Label: "empty", Count: problemCalls.Count(IsTranscriptEmpty)),
            (Label: "failed", Count: problemCalls.Count(IsTranscriptFailureHint))
        }
        .Where(p => p.Count > 0)
        .Select(p => $"{p.Count:N0} {p.Label}");
        return string.Join(", ", parts);
    }

    private static BarStatDto BuildTopProblemSystem(List<EngineCall> calls)
    {
        var row = calls.GroupBy(c => string.IsNullOrWhiteSpace(c.SystemShortName) ? "Unknown system" : c.SystemShortName)
            .Select(g =>
            {
                var total = g.Count();
                var problems = g.Count(IsProblemTranscript);
                return new { Label = g.Key, Total = total, Problems = problems };
            })
            .Where(r => r.Problems > 0)
            .OrderByDescending(r => r.Problems)
            .ThenByDescending(r => r.Total)
            .FirstOrDefault();
        return row == null
            ? new BarStatDto("--", 0, 0, "No quality problems")
            : new BarStatDto(row.Label, row.Problems, 1, $"{row.Problems:N0}/{row.Total:N0} calls ({row.Problems * 100.0 / Math.Max(1, row.Total):F1}%)");
    }

    private static IReadOnlyList<BarStatDto> BuildCategoryShare(List<EngineCall> calls)
    {
        var total = Math.Max(1, calls.Count);
        return calls.GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new BarStatDto(g.Key, g.Count(), g.Count() / (double)total, $"{g.Count() * 100.0 / total:F1}%"))
            .ToList();
    }

    private static IReadOnlyList<TopTalkgroupDto> BuildTopTalkgroups(List<EngineCall> calls, long start, long end)
    {
        var total = Math.Max(1, calls.Count);
        var trendBins = TrendBinCount(start, end);
        return calls.GroupBy(c => c.Talkgroup)
            .OrderByDescending(g => g.Count())
            .Take(12)
            .Select(g =>
            {
                var trendCounts = BuildTrendCounts(g.ToList(), start, end, trendBins);
                return new TopTalkgroupDto(
                    GetTalkgroupLabel(g.First()),
                    g.Key,
                    g.Count(),
                    g.Count() / (double)total,
                    g.Max(c => c.StartTime),
                    NormalizeTrend(trendCounts),
                    trendCounts,
                    BuildTrendLabels(start, end, trendBins),
                    FormatEndpoint(start),
                    FormatBucket(start, end, trendBins),
                    FormatEndpoint(end));
            })
            .ToList();
    }

    private static int TrendBinCount(long start, long end)
    {
        var hours = Math.Max(1, (int)Math.Ceiling((end - start) / 3600.0));
        return Math.Clamp(hours, 12, 48);
    }

    private static IReadOnlyList<int> BuildTrendCounts(List<EngineCall> calls, long start, long end, int bins)
    {
        var counts = new int[bins];
        var span = Math.Max(1, end - start);
        foreach (var call in calls)
        {
            var index = (int)Math.Floor((call.StartTime - start) / (double)span * bins);
            index = Math.Clamp(index, 0, bins - 1);
            counts[index]++;
        }
        return counts.ToList();
    }

    private static IReadOnlyList<double> NormalizeTrend(IReadOnlyList<int> counts)
    {
        var max = Math.Max(1, counts.Max());
        return counts.Select(c => c / (double)max).ToList();
    }

    private static IReadOnlyList<string> BuildTrendLabels(long start, long end, int bins)
    {
        var labels = new List<string>(bins);
        var span = Math.Max(1, end - start);
        for (var i = 0; i < bins; i++)
        {
            var bucketStart = start + (long)Math.Floor(span * (i / (double)bins));
            labels.Add(DateTimeOffset.FromUnixTimeSeconds(bucketStart).ToLocalTime().ToString("M/d HH:mm", CultureInfo.CurrentCulture));
        }
        return labels;
    }

    public static string NormalizeCategory(string category)
    {
        category = (category ?? string.Empty).Trim().ToLowerInvariant();
        return category is "police" or "fire" or "ems" or "traffic" ? category : "other";
    }

    public static bool IsProblemTranscript(EngineCall call) =>
        TranscriptionQualityClassifier.IsProblem(call);

    public static bool IsTranscriptEmpty(EngineCall call) =>
        string.Equals(call.QualityReason, "empty", StringComparison.OrdinalIgnoreCase);

    public static bool IsTranscriptShort(EngineCall call) =>
        string.Equals(call.QualityReason, "too_short", StringComparison.OrdinalIgnoreCase);

    public static bool IsTranscriptFailureHint(EngineCall call) =>
        string.Equals(call.TranscriptionStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(call.QualityReason, "transcription_error", StringComparison.OrdinalIgnoreCase);

    public static bool IsTranscriptInaudibleHint(EngineCall call) =>
        call.QualityReason is "inaudible" or "blank_audio" or "marker_only";

    private static string GetTalkgroupLabel(EngineCall call) =>
        string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"TG {call.Talkgroup}" : call.TalkgroupName;

    private static string PreviewTranscript(string transcription)
    {
        transcription = Regex.Replace(transcription ?? string.Empty, @"\s+", " ").Trim();
        return transcription.Length <= 180 ? transcription : transcription[..180] + "...";
    }

    private static string FormatEndpoint(long unix) =>
        DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime().ToString("M/d HH:mm", CultureInfo.CurrentCulture);

    private static string FormatCompact(long value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000d:0.#}m";
        if (value >= 1_000) return $"{value / 1_000d:0.#}k";
        return value.ToString("N0", CultureInfo.CurrentCulture);
    }

    private static string FormatBucket(long start, long end, int bins)
    {
        var seconds = Math.Max(1, end - start) / Math.Max(1, bins);
        if (seconds < 3600) return $"{Math.Max(1, seconds / 60)}m each";
        if (seconds < 86400) return $"{Math.Max(1, seconds / 3600)}h each";
        return $"{seconds / 86400.0:F1}d each";
    }

    private static (KpiDto Global, KpiDto Rate, KpiDto Systems) BuildDecodeKpis(List<TrHealthSampleDto> rows)
    {
        var systems = rows
            .Where(r => IsDisplaySystemScope(r.Scope))
            .GroupBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var groupRows = g.ToList();
                var decodeLines = groupRows.Sum(r => r.CcSummaryDecodeLines);
                var decodeZero = groupRows.Sum(r => r.CcSummaryDecodeZero);
                var decodeRateTotal = groupRows.Sum(r => r.CcSummaryDecodeRateTotal);
                if (decodeLines == 0)
                {
                    decodeLines = groupRows.Sum(r => r.DecodeLines);
                    decodeZero = groupRows.Sum(r => r.DecodeZero);
                    decodeRateTotal = groupRows.Sum(r => r.DecodeRateTotal);
                }
                var calls = groupRows.Sum(r => r.CallsConcluded);
                var pct = decodeLines == 0 ? 0 : decodeZero * 100.0 / decodeLines;
                return new DecodeSystemStat(g.Key, decodeLines, decodeZero, decodeRateTotal, calls, pct);
            })
            .Where(s => s.DecodeLines > 0)
            .OrderByDescending(s => s.Calls)
            .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var global = WeightedDecodeZeroPercent(systems, rows);
        var globalZeroKpi = global == null
            ? new KpiDto("TR Decode 0%", "N/A", "No TR decode samples in selected range")
            : new KpiDto("TR Decode 0%", $"{global.Value.ZeroPercent:F1}%", $"Weighted by {global.Value.Weight:N0} concluded calls");

        var globalRateKpi = global == null
            ? new KpiDto("TR CC Rate", "N/A", "No TR CC summary samples in selected range")
            : new KpiDto("TR CC Rate", $"{global.Value.AvgRate:F2}/sec", $"Periodic Control Channel Decode Rates, weighted by {global.Value.Weight:N0} concluded calls");

        var worst = systems.OrderByDescending(s => s.DecodeZeroPercent).FirstOrDefault();
        var systemSummary = string.Join(", ", systems.Take(3).Select(s => $"{s.Label} {s.DecodeZeroPercent:F1}%/{s.Calls:N0} calls"));
        var systemsKpi = worst == null
            ? new KpiDto("TR Worst Decode", "N/A", "No system-scoped decode samples")
            : new KpiDto("TR Worst Decode", $"{worst.Label} {worst.DecodeZeroPercent:F1}%", string.IsNullOrWhiteSpace(systemSummary) ? "Worst system decode-zero rate" : systemSummary);

        return (globalZeroKpi, globalRateKpi, systemsKpi);
    }

    private static (double ZeroPercent, double AvgRate, int Weight)? WeightedDecodeZeroPercent(List<DecodeSystemStat> systems, List<TrHealthSampleDto> fallbackRows)
    {
        var weighted = systems.Where(s => s.Calls > 0).ToList();
        if (weighted.Count > 0)
        {
            var totalCalls = weighted.Sum(s => s.Calls);
            var zeroPercent = weighted.Sum(s => s.DecodeZeroPercent * s.Calls) / Math.Max(1, totalCalls);
            var avgRate = weighted.Sum(s => s.AvgDecodeRate * s.Calls) / Math.Max(1, totalCalls);
            return (zeroPercent, avgRate, totalCalls);
        }

        var globalRows = fallbackRows.Where(r => string.Equals(r.Scope, "global", StringComparison.OrdinalIgnoreCase)).ToList();
        var rows = globalRows.Count > 0 ? globalRows : fallbackRows;
        var lines = rows.Sum(r => r.CcSummaryDecodeLines);
        if (lines == 0)
            lines = rows.Sum(r => r.DecodeLines);
        if (lines == 0)
            return null;
        var zero = rows.Sum(r => r.CcSummaryDecodeLines) > 0 ? rows.Sum(r => r.CcSummaryDecodeZero) : rows.Sum(r => r.DecodeZero);
        var total = rows.Sum(r => r.CcSummaryDecodeLines) > 0 ? rows.Sum(r => r.CcSummaryDecodeRateTotal) : rows.Sum(r => r.DecodeRateTotal);
        return (zero * 100.0 / lines, total / lines, lines);
    }

    private static bool IsDisplaySystemScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
            return false;
        var value = scope.Trim();
        if (string.Equals(value, "global", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.StartsWith("source", StringComparison.OrdinalIgnoreCase))
            return false;
        if (value.All(char.IsDigit))
            return false;
        return value.Any(char.IsLetter);
    }

    private sealed record DecodeSystemStat(string Label, int DecodeLines, int DecodeZero, double DecodeRateTotal, int Calls, double DecodeZeroPercent)
    {
        public double AvgDecodeRate => DecodeLines == 0 ? 0 : DecodeRateTotal / DecodeLines;
    }

    private sealed record DashboardCacheEntry(string Key, DateTimeOffset CreatedAt, DashboardDto Value);

    private sealed record LocationGroup(
        string AreaId,
        string AreaLabel,
        string AreaSystemShortName,
        string Location,
        string GeocodeQuery,
        string GeocodeDisplayName,
        string GeocodeProvider,
        string GeocodePrecision,
        double GeocodeConfidence,
        double Latitude,
        double Longitude,
        int Count,
        long LastHeard,
        string Category,
        List<long> LinkCallIds,
        List<long> DisplayCallIds,
        List<LocationHeatCallDto> SourceCalls);
}
