using System.Globalization;
using System.Text.RegularExpressions;
namespace pizzad;

public sealed class DashboardService
{
    private const string DirectionalSuffixPattern = @"(?:\s+(?:n|s|e|w|ne|nw|se|sw|north|south|east|west|northeast|northwest|southeast|southwest))?";
    private static readonly Regex AddressStreetRegex = new(@"\b\d{1,5}\s+(?:[a-z0-9]+\.?\s+){0,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy)" + DirectionalSuffixPattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StreetRegex = new(@"\b(?:[a-z]+\.?\s+){1,4}(?:street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy)" + DirectionalSuffixPattern + @"\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HighwayRegex = new(@"\b(?:i[-\s]?\d{1,3}|interstate\s+\d{1,3}|us\s+\d{1,3}|hwy\s+\d{1,3}|highway\s+\d{1,3}|sr\s+\d{1,3}|state\s+route\s+\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LocalPlaceRegex = new(@"\b(?:fort\s+oglethorpe|east\s+ridge|lookout\s+mountain|soddy[-\s]+daisy|signal\s+mountain|ooltewah|collegedale|hixson|cleveland|chattanooga)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LocationSuffixRegex = new(@"\b(street|st|road|rd|avenue|ave|drive|dr|lane|ln|boulevard|blvd|highway|hwy|pike|place|pl|court|ct|way|circle|cir|terrace|ter|trail|trl|parkway|pkwy)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> LocalHighwayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "i 75", "interstate 75", "i 24", "interstate 24",
        "us 11", "us 27", "us 41", "us 64", "us 74",
        "hwy 11", "highway 11", "hwy 27", "highway 27", "hwy 41", "highway 41", "hwy 64", "highway 64", "hwy 74", "highway 74",
        "sr 2", "state route 2", "sr 58", "state route 58", "sr 60", "state route 60", "sr 153", "state route 153",
        "sr 308", "state route 308", "sr 312", "state route 312", "sr 317", "state route 317"
    };
    private static readonly HashSet<string> LocationStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "radio traffic", "main channel", "control channel", "dispatch road", "signal road", "unknown road",
        "middle of the road", "the middle of the road", "side of the road", "on the road",
        "by the way", "the way", "the road", "county road", "street court"
    };
    private readonly EngineDatabase _database;
    private readonly TalkgroupResolver _talkgroups;
    private readonly EngineConfig _config;
    private readonly GeocodingService _geocoding;

    public DashboardService(EngineDatabase database, TalkgroupResolver talkgroups, EngineConfig config, GeocodingService geocoding)
    {
        _database = database;
        _talkgroups = talkgroups;
        _config = config;
        _geocoding = geocoding;
    }

    public async Task<DashboardDto> BuildDashboardAsync(long start, long end, CancellationToken ct)
    {
        var calls = ApplyProfile(Enrich(await _database.ListCallsAsync(start, end, null, ct)));
        var alerts = (await _database.ListAlertMatchesAsync(start, end, ct)).Where(a => Allows(a.Category, a.Talkgroup)).ToList();
        var incidents = (await ListIncidentsAsync(start, end, ct)).Where(i => i.Calls.Any(c => calls.Any(call => call.Id == c.CallId))).ToList();
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
                new("Incidents", incidents.Count.ToString("N0", CultureInfo.CurrentCulture), "An Incident consists of multiple, related calls"),
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
            LocationHeat = await BuildLocationHeatAsync(calls, incidents, ct),
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

    public async Task<CategoryPageDto> BuildCategoryPageAsync(string category, string groupBy, long start, long end, CancellationToken ct)
    {
        category = NormalizeCategory(category);
        var calls = ApplyProfile(Enrich(await _database.ListCallsAsync(start, end, null, ct)))
            .Where(c => string.Equals(c.Category, category, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var insightEvents = await _database.ListInsightEventsAsync(start, end, ct);
        var insights = BuildCategoryInsights(insightEvents, calls);
        var incidents = BuildCategoryIncidents(await ListIncidentsAsync(start, end, ct), calls);
        if (string.Equals(groupBy, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new CategoryPageDto(category, "none", [new CategoryGroupDto("All calls", calls)], insights, incidents);
        }

        var groups = calls
            .GroupBy(c => string.IsNullOrWhiteSpace(c.TalkgroupName) ? $"TG {c.Talkgroup}" : c.TalkgroupName)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new CategoryGroupDto(g.Key, g.OrderByDescending(c => c.StartTime).ToList()))
            .ToList();

        return new CategoryPageDto(category, "talkgroup", groups, insights, incidents);
    }

    public Task<List<IncidentDto>> ListIncidentsAsync(long start, long end, CancellationToken ct)
    {
        return _database.ListIncidentsAsync(start, end, ct);
    }

    private List<EngineCall> ApplyProfile(IEnumerable<EngineCall> calls) => calls.Where(c => Allows(c.Category, c.Talkgroup)).ToList();

    private bool Allows(string category, long talkgroup)
    {
        var profile = _config.Profiles.Items.FirstOrDefault(p => p.Id == _config.Profiles.ActiveProfileId);
        if (profile == null) return true;
        if (profile.AllowedTalkgroups.Count > 0 && !profile.AllowedTalkgroups.Contains(talkgroup)) return false;
        return NormalizeCategory(category) switch
        {
            "police" => profile.IncludePolice,
            "fire" => profile.IncludeFire,
            "ems" => profile.IncludeEMS,
            "traffic" => profile.IncludeTraffic,
            _ => profile.IncludeOther
        };
    }

    private List<EngineCall> Enrich(List<EngineCall> calls) =>
        calls.Select(_talkgroups.Enrich).ToList();

    private static IReadOnlyList<CategoryInsightDto> BuildCategoryInsights(List<InsightEventRecordDto> insightEvents, List<EngineCall> categoryCalls)
    {
        var categoryCallIds = categoryCalls.Select(c => c.Id).ToHashSet();
        return insightEvents
            .Select(i => new
            {
                Insight = i,
                Calls = i.Calls.Where(c => categoryCallIds.Contains(c.CallId)).ToList()
            })
            .Where(x => x.Calls.Count == 1 && x.Insight.Calls.Count == 1)
            .OrderByDescending(x => x.Insight.LastSeen)
            .ThenByDescending(x => x.Insight.Confidence)
            .Select(x => new CategoryInsightDto(
                x.Insight.Id,
                x.Insight.Title,
                x.Insight.Detail,
                x.Insight.FirstSeen,
                x.Insight.LastSeen,
                x.Insight.Confidence,
                x.Calls.Count,
                x.Calls))
            .Take(100)
            .ToList();
    }

    private static IReadOnlyList<IncidentDto> BuildCategoryIncidents(List<IncidentDto> incidents, List<EngineCall> categoryCalls)
    {
        var categoryCallIds = categoryCalls.Select(c => c.Id).ToHashSet();
        return incidents
            .Where(i => i.Calls.Count >= 2 && i.Calls.Any(c => categoryCallIds.Contains(c.CallId)))
            .OrderByDescending(i => i.LastSeen)
            .ThenByDescending(i => i.Confidence)
            .Take(100)
            .ToList();
    }

    private static IReadOnlyList<HourCategoryDto> BuildVolume(List<EngineCall> calls) =>
        calls.GroupBy(c => new { DateTimeOffset.FromUnixTimeSeconds(c.StartTime).ToLocalTime().Hour, c.Category })
            .Select(g => new HourCategoryDto(g.Key.Hour, g.Key.Category, g.Count()))
            .OrderBy(r => r.Hour)
            .ThenBy(r => r.Category)
            .ToList();

    private async Task<IReadOnlyList<LocationHeatDto>> BuildLocationHeatAsync(List<EngineCall> calls, List<IncidentDto> incidents, CancellationToken ct)
    {
        var areaRows = _config.Locations.MonitoredAreas
            .GroupBy(a => a.AreaId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (areaRows.Count == 0)
            return [];

        var candidates = new List<LocationCandidate>();
        foreach (var call in calls)
        {
            if (string.IsNullOrWhiteSpace(call.Transcription))
                continue;

            var area = ResolveArea(call.SystemShortName, areaRows);
            if (area == null)
                continue;

            foreach (var location in ExtractLocations(call.Transcription))
            {
                candidates.Add(new LocationCandidate(area, call, location, false));
            }
        }

        var callsById = calls.ToDictionary(c => c.Id);
        foreach (var incident in incidents)
        {
            var incidentCalls = incident.Calls
                .Select(c => callsById.TryGetValue(c.CallId, out var call) ? call : null)
                .Where(c => c != null)
                .Cast<EngineCall>()
                .ToList();
            if (incidentCalls.Count == 0)
                continue;

            var text = $"{incident.Title}. {incident.Detail}";
            var area = ResolveIncidentArea(incidentCalls, areaRows, text);
            if (area == null)
                continue;

            foreach (var location in ExtractLocations(text))
            {
                foreach (var call in incidentCalls)
                    candidates.Add(new LocationCandidate(area, call, location, true));
            }
        }

        var grouped = candidates
            .GroupBy(c => $"{c.Area.AreaId}|{NormalizeLocationKey(c.LocationText)}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var rows = g.ToList();
                var first = rows[0];
                var category = rows.GroupBy(r => NormalizeCategory(r.Call.Category))
                    .OrderByDescending(r => r.Count())
                    .ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                    .First().Key;
                return new LocationGroup(
                    first.Area,
                    CanonicalLocation(rows.Select(r => r.LocationText)),
                    rows.Select(r => r.Call.Id).Distinct().Count(),
                    rows.Max(r => r.Call.StartTime),
                    category,
                    rows.Select(r => r.Call.Id).Distinct().OrderByDescending(id => id).Take(8).ToList(),
                    rows.Any(r => r.FromIncident),
                    rows
                        .GroupBy(r => r.Call.Id)
                        .Select(r => r.First().Call)
                        .OrderByDescending(c => c.StartTime)
                        .Take(5)
                        .Select(c => new LocationHeatCallDto(
                            c.Id,
                            c.StartTime,
                            NormalizeCategory(c.Category),
                            GetTalkgroupLabel(c),
                            PreviewTranscript(c.Transcription),
                            $"/api/v1/calls/{c.Id}/audio"))
                        .ToList());
            })
            .Where(r => r.Count > 0)
            .OrderByDescending(r => r.FromIncident)
            .ThenByDescending(r => r.Count)
            .ThenByDescending(r => r.LastHeard)
            .Take(30)
            .ToList();

        var geocoded = new List<(LocationGroup Group, GeocodeCacheDto Geocode)>();
        var liveGeocodeAttempts = 0;
        foreach (var group in grouped)
        {
            var geocode = await _geocoding.GetCachedAsync(group.Location, group.Area, ct);
            if (geocode == null && liveGeocodeAttempts < 6)
            {
                liveGeocodeAttempts++;
                using var geocodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                geocodeCts.CancelAfter(TimeSpan.FromSeconds(2));
                geocode = await _geocoding.ResolveAsync(group.Location, group.Area, geocodeCts.Token);
            }

            if (geocode != null && !string.Equals(geocode.Provider, "none", StringComparison.OrdinalIgnoreCase))
                geocoded.Add((group, geocode));
        }

        var max = Math.Max(1, geocoded.Select(g => g.Group.Count).DefaultIfEmpty(0).Max());
        return geocoded.Select(g =>
        {
            var group = g.Group;
            var callIds = group.CallIds.ToHashSet();
            var incidentLinks = incidents
                .Where(i => i.Calls.Any(c => callIds.Contains(c.CallId)))
                .OrderByDescending(i => i.LastSeen)
                .Select(i => new LocationHeatIncidentDto(i.Id, string.IsNullOrWhiteSpace(i.Title) ? "Radio incident" : i.Title))
                .DistinctBy(i => i.IncidentId)
                .Take(4)
                .ToList();
            var incidentTitles = incidentLinks.Select(i => i.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new LocationHeatDto(
                group.Area.AreaId,
                group.Area.AreaLabel,
                group.Area.SystemShortName,
                group.Location,
                g.Geocode.Query,
                g.Geocode.DisplayName,
                g.Geocode.Provider,
                g.Geocode.Precision,
                g.Geocode.Confidence,
                g.Geocode.Latitude,
                g.Geocode.Longitude,
                group.Count,
                group.Count / (double)max,
                group.LastHeard,
                group.Category,
                group.CallIds,
                incidentTitles,
                incidentLinks,
                group.SourceCalls);
        }).ToList();
    }

    private static MonitoredAreaConfig? ResolveArea(string systemShortName, IReadOnlyList<MonitoredAreaConfig> areas)
    {
        if (string.IsNullOrWhiteSpace(systemShortName))
            return null;

        var system = systemShortName.Trim();
        return areas.FirstOrDefault(area =>
            string.Equals(area.SystemShortName, system, StringComparison.OrdinalIgnoreCase) ||
            area.Aliases.Any(alias => string.Equals(alias, system, StringComparison.OrdinalIgnoreCase))) ??
            areas.FirstOrDefault(area => area.Aliases.Any(alias =>
                !string.IsNullOrWhiteSpace(alias) &&
                system.Contains(alias, StringComparison.OrdinalIgnoreCase)));
    }

    private static MonitoredAreaConfig? ResolveIncidentArea(List<EngineCall> calls, IReadOnlyList<MonitoredAreaConfig> areas, string incidentText)
    {
        var incidentKey = NormalizeLocationKey(incidentText);
        var mentionedArea = areas.FirstOrDefault(area =>
            Mentioned(incidentKey, area.AreaLabel) ||
            Mentioned(incidentKey, area.SystemShortName) ||
            area.Aliases.Any(alias => Mentioned(incidentKey, alias)));
        if (mentionedArea != null)
            return mentionedArea;

        return calls
            .Select(c => ResolveArea(c.SystemShortName, areas))
            .Where(a => a != null)
            .Cast<MonitoredAreaConfig>()
            .GroupBy(a => a.AreaId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.First())
            .FirstOrDefault();
    }

    private static bool Mentioned(string normalizedText, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var normalizedValue = NormalizeLocationKey(value);
        return !string.IsNullOrWhiteSpace(normalizedValue) &&
               Regex.IsMatch(normalizedText, $@"\b{Regex.Escape(normalizedValue)}\b", RegexOptions.IgnoreCase);
    }

    private static IEnumerable<string> ExtractLocations(string transcription)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addressMatches = AddressStreetRegex.Matches(transcription).Cast<Match>()
            .Select(m => CleanLocationText(m.Value))
            .Where(IsPlausibleLocation)
            .ToList();
        foreach (var text in addressMatches
                     .Concat(HighwayRegex.Matches(transcription).Cast<Match>().Select(m => CleanLocationText(m.Value)).Where(IsPlausibleLocation))
                     .Concat(LocalPlaceRegex.Matches(transcription).Cast<Match>().Select(m => CleanLocationText(m.Value)).Where(IsPlausibleLocation))
                     .Concat(StreetRegex.Matches(transcription).Cast<Match>()
                         .Select(m => CleanLocationText(m.Value))
                         .Where(IsPlausibleLocation)
                         .Where(street => !addressMatches.Any(address => address.Contains(street, StringComparison.OrdinalIgnoreCase)))))
        {
            var key = NormalizeLocationKey(text);
            if (seen.Add(key))
                yield return text;
        }
    }

    private static bool IsPlausibleLocation(string text)
    {
        if (text.Length < 4 || LocationStopWords.Contains(text))
            return false;

        var key = NormalizeLocationKey(text);
        if (LocationStopWords.Contains(key))
            return false;
        if (key.Contains(" en route ", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(" in route ", StringComparison.OrdinalIgnoreCase) ||
            key.Contains(" be in route ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ll be ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("we ll ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (key.StartsWith("i ") || key.StartsWith("interstate ") || key.StartsWith("us ") ||
            key.StartsWith("hwy ") || key.StartsWith("highway ") || key.StartsWith("sr ") ||
            key.StartsWith("state route "))
            return LocalHighwayKeys.Contains(key);
        if (LocalPlaceRegex.IsMatch(text))
            return true;

        if (!LocationSuffixRegex.IsMatch(text))
            return false;
        if (key.StartsWith("linux ", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("emergency file ", StringComparison.OrdinalIgnoreCase))
            return false;
        if (key is "st" or "street" or "rd" or "road" or "ave" or "avenue" or "dr" or "drive" or "ln" or "lane" or "way" or "ct" or "court")
            return false;

        return true;
    }

    private static string CleanLocationText(string text)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        text = Regex.Replace(text, @"^[,.;:\-\s]+|[,.;:\-\s]+$", "");
        text = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        text = Regex.Replace(text, @"^(?:(?:And|At|On|Near|By|To|From|The)\s+)+", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bI\s+(\d+)\b", "I-$1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bUs\s+(\d+)\b", "US $1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\bSr\s+(\d+)\b", "SR $1", RegexOptions.IgnoreCase);
        return text;
    }

    private static string NormalizeLocationKey(string text)
    {
        text = text.ToLowerInvariant();
        text = Regex.Replace(text, @"\b(street|st\.)\b", "st");
        text = Regex.Replace(text, @"\b(road|rd\.)\b", "rd");
        text = Regex.Replace(text, @"\b(avenue|ave\.)\b", "ave");
        text = Regex.Replace(text, @"\b(drive|dr\.)\b", "dr");
        text = Regex.Replace(text, @"\b(lane|ln\.)\b", "ln");
        text = Regex.Replace(text, @"\b(boulevard|blvd\.)\b", "blvd");
        text = Regex.Replace(text, @"\b(highway|hwy\.)\b", "hwy");
        text = Regex.Replace(text, @"\b(route|rte\.)\b", "rte");
        text = Regex.Replace(text, @"\b(north\s*east|northeast|ne)\b", "ne");
        text = Regex.Replace(text, @"\b(north\s*west|northwest|nw)\b", "nw");
        text = Regex.Replace(text, @"\b(south\s*east|southeast|se)\b", "se");
        text = Regex.Replace(text, @"\b(south\s*west|southwest|sw)\b", "sw");
        text = Regex.Replace(text, @"\b(north|n)\b", "n");
        text = Regex.Replace(text, @"\b(south|s)\b", "s");
        text = Regex.Replace(text, @"\b(east|e)\b", "e");
        text = Regex.Replace(text, @"\b(west|w)\b", "w");
        text = Regex.Replace(text, @"[^a-z0-9]+", " ").Trim();
        return text;
    }

    private static string CanonicalLocation(IEnumerable<string> values) =>
        values.GroupBy(v => NormalizeLocationKey(v), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.First().Length)
            .First().First();

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var ch in value)
        {
            hash ^= char.ToLowerInvariant(ch);
            hash *= prime;
        }
        return hash;
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
                var decodeLines = groupRows.Sum(r => r.DecodeLines);
                var decodeZero = groupRows.Sum(r => r.DecodeZero);
                var decodeRateTotal = groupRows.Sum(r => r.DecodeRateTotal);
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
            ? new KpiDto("TR Decode Rate", "N/A", "No TR decode samples in selected range")
            : new KpiDto("TR Decode Rate", $"{global.Value.AvgRate:F2}/sec", $"Weighted by {global.Value.Weight:N0} concluded calls");

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
        var lines = rows.Sum(r => r.DecodeLines);
        if (lines == 0)
            return null;
        return (rows.Sum(r => r.DecodeZero) * 100.0 / lines, rows.Sum(r => r.DecodeRateTotal) / lines, lines);
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

    private sealed record LocationCandidate(MonitoredAreaConfig Area, EngineCall Call, string LocationText, bool FromIncident);

    private sealed record LocationGroup(
        MonitoredAreaConfig Area,
        string Location,
        int Count,
        long LastHeard,
        string Category,
        List<long> CallIds,
        bool FromIncident,
        List<LocationHeatCallDto> SourceCalls);
}
