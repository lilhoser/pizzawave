using System.Text.RegularExpressions;

namespace pizzad;

public sealed record IncidentRagCandidate(
    EngineCall Call,
    double Score,
    double TextScore,
    double GeoScore,
    double TimeScore,
    double ContextScore,
    string Reason,
    string LocationLabel,
    bool LocationIsHighConfidenceGeocode = false,
    double LocationGeocodeConfidence = 0,
    string LocationGeocodeProvider = "",
    string LocationGeocodePrecision = "",
    string LocationSource = "",
    string LocationGeocodeQuery = "",
    string LocationGeocodeDisplayName = "",
    double LocationLatitude = 0,
    double LocationLongitude = 0);

public sealed class IncidentRagMatcher
{
    private const double HighConfidenceGeocodeThreshold = 0.75;
    private static readonly HashSet<string> TrustedGeocodePrecisions = new(StringComparer.OrdinalIgnoreCase)
    {
        "address",
        "intersection",
        "mile_marker",
        "road",
        "route",
        "street",
        "highway"
    };
    private const int MaxNewSeedCalls = 12;
    private const int MaxCarryoverCalls = 12;
    private const int MaxMatchesPerSeed = 4;
    private const int MaxMatchesPerIncident = 8;

    public IReadOnlyList<IncidentRagCandidate> SelectCandidates(
        string systemShortName,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<EngineCall> recentCalls,
        IReadOnlyCollection<long> newCallIds,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, VectorSearchMatchDto>? vectorMatches = null)
    {
        vectorMatches ??= new Dictionary<long, VectorSearchMatchDto>();
        var locations = locationRows
            .Where(l => string.Equals(l.SystemShortName, systemShortName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(l => l.CallId)
            .ToDictionary(g => g.Key, g => BestLocation(g), EqualityComparer<long>.Default);
        var activeCallIds = activeIncidents.SelectMany(i => i.Calls.Select(c => c.CallId)).ToHashSet();
        var candidates = new Dictionary<long, IncidentRagCandidate>();
        var rows = recentCalls
            .Where(c => !activeCallIds.Contains(c.Id))
            .Select(c => new CallSemanticRow(
                c,
                Tokens(CallText(c)),
                IncidentCandidateValidator.ExtractConcreteAnchors(CallText(c)),
                LocationKey(c.Id, locations),
                LocationLabel(c.Id, locations),
                IsHighConfidenceGeocode(c.Id, locations),
                LocationGeocodeConfidence(c.Id, locations),
                LocationGeocodeProvider(c.Id, locations),
                LocationGeocodePrecision(c.Id, locations),
                LocationSource(c.Id, locations),
                LocationGeocodeQuery(c.Id, locations),
                LocationGeocodeDisplayName(c.Id, locations),
                LocationLatitude(c.Id, locations),
                LocationLongitude(c.Id, locations),
                vectorMatches.ContainsKey(c.Id)))
            .ToList();

        foreach (var incident in activeIncidents.OrderByDescending(i => i.LastSeen).Take(20))
        {
            var incidentText = $"{incident.Title} {incident.Detail} {string.Join(' ', incident.Calls.Select(c => c.Transcript))}";
            var incidentTokens = Tokens(incidentText);
            var incidentLocation = incident.Calls
                .Select(c => LocationKey(c.CallId, locations))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
            foreach (var match in rows
                .Select(row => Score(row, incidentTokens, incident.LastSeen, incidentLocation, "active incident", vectorMatches))
                .Where(row => row.Score >= 0.22)
                .OrderByDescending(row => row.Score)
                .ThenByDescending(row => row.Call.StartTime)
                .Take(MaxMatchesPerIncident))
            {
                AddBest(candidates, match);
            }
        }

        var newRows = rows
            .Where(r => newCallIds.Contains(r.Call.Id))
            .Where(IsNewIncidentSeed)
            .OrderBy(r => r.Call.StartTime)
            .Take(MaxNewSeedCalls)
            .ToList();
        foreach (var seed in newRows)
        {
            AddBest(candidates, new IncidentRagCandidate(
                seed.Call,
                0.15,
                1,
                0,
                0,
                0.15,
                "new call seed",
                seed.LocationLabel,
                seed.LocationIsHighConfidenceGeocode,
                seed.LocationGeocodeConfidence,
                seed.LocationGeocodeProvider,
                seed.LocationGeocodePrecision,
                seed.LocationSource,
                seed.LocationGeocodeQuery,
                seed.LocationGeocodeDisplayName,
                seed.LocationLatitude,
                seed.LocationLongitude));
            foreach (var match in rows
                .Where(r => r.Call.Id != seed.Call.Id)
                .Where(r => Math.Abs(r.Call.StartTime - seed.Call.StartTime) <= 3600)
                .Where(row => IsConcreteNewMatch(seed, row))
                .Select(row => Score(row, seed.Tokens, seed.Call.StartTime, seed.LocationKey, "new similarity set", vectorMatches))
                .Where(row => row.Score >= 0.24)
                .OrderByDescending(row => row.Score)
                .ThenBy(row => Math.Abs(row.Call.StartTime - seed.Call.StartTime))
                .Take(MaxMatchesPerSeed))
            {
                AddBest(candidates, match);
            }
        }

        var selectedNew = candidates.Values
            .Where(c => newCallIds.Contains(c.Call.Id))
            .OrderBy(c => c.Call.StartTime)
            .Take(MaxNewSeedCalls)
            .ToList();
        var selectedIds = selectedNew.Select(c => c.Call.Id).ToHashSet();
        var selectedCarryover = candidates.Values
            .Where(c => !selectedIds.Contains(c.Call.Id))
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Call.StartTime)
            .Take(MaxCarryoverCalls)
            .ToList();

        return selectedNew
            .Concat(selectedCarryover)
            .DistinctBy(c => c.Call.Id)
            .OrderBy(c => c.Call.StartTime)
            .ToList();
    }

    private static bool IsNewIncidentSeed(CallSemanticRow row) =>
        row.HasVectorMatch ||
        row.Anchors.Count > 0 ||
        IncidentCandidateValidator.HasStrongIncidentSignal(CallText(row.Call));

    private static bool IsConcreteNewMatch(CallSemanticRow seed, CallSemanticRow row)
    {
        if (!string.IsNullOrWhiteSpace(seed.LocationKey) &&
            string.Equals(seed.LocationKey, row.LocationKey, StringComparison.OrdinalIgnoreCase))
            return true;

        if (IncidentCandidateValidator.SharedConcreteAnchorCount(seed.Anchors, row.Anchors) > 0)
            return true;

        var minutes = Math.Abs(row.Call.StartTime - seed.Call.StartTime) / 60d;
        if (seed.HasVectorMatch || row.HasVectorMatch)
            return minutes <= 30;

        return IncidentCandidateValidator.HasStrongIncidentSignal(CallText(seed.Call)) &&
               IncidentCandidateValidator.HasStrongIncidentSignal(CallText(row.Call)) &&
               Similarity(seed.Tokens, row.Tokens) >= 0.34 &&
               minutes <= 20;
    }

    private static IncidentRagCandidate Score(CallSemanticRow row, HashSet<string> queryTokens, long queryTime, string queryLocation, string context, IReadOnlyDictionary<long, VectorSearchMatchDto> vectorMatches)
    {
        var lexical = Similarity(queryTokens, row.Tokens);
        var vector = vectorMatches.TryGetValue(row.Call.Id, out var vectorMatch) ? Math.Clamp(vectorMatch.Score, 0, 1) : 0;
        var text = vector > 0 ? Math.Max(vector, lexical * 0.85) : lexical;
        var geo = !string.IsNullOrWhiteSpace(queryLocation) && !string.IsNullOrWhiteSpace(row.LocationKey)
            ? string.Equals(queryLocation, row.LocationKey, StringComparison.OrdinalIgnoreCase) ? 1 : 0
            : 0;
        var minutes = Math.Abs(row.Call.StartTime - queryTime) / 60d;
        var time = Math.Max(0, 1 - minutes / 60d);
        var contextScore = context == "active incident" ? 0.12 : 0.08;
        var score = text * 0.58 + geo * 0.24 + time * 0.10 + contextScore;
        var reason = $"{context}; semantic={text:0.00}; vector={vector:0.00}; lexical={lexical:0.00}; geo={geo:0.00}; time={time:0.00}";
        return new IncidentRagCandidate(
            row.Call,
            Math.Clamp(score, 0, 1),
            text,
            geo,
            time,
            contextScore,
            reason,
            row.LocationLabel,
            row.LocationIsHighConfidenceGeocode,
            row.LocationGeocodeConfidence,
            row.LocationGeocodeProvider,
            row.LocationGeocodePrecision,
            row.LocationSource,
            row.LocationGeocodeQuery,
            row.LocationGeocodeDisplayName,
            row.LocationLatitude,
            row.LocationLongitude);
    }

    private static void AddBest(Dictionary<long, IncidentRagCandidate> candidates, IncidentRagCandidate candidate)
    {
        if (!candidates.TryGetValue(candidate.Call.Id, out var existing) || candidate.Score > existing.Score)
            candidates[candidate.Call.Id] = candidate;
    }

    private static CandidateLocation BestLocation(IEnumerable<CallLocationDashboardRow> rows)
    {
        var best = rows
            .OrderByDescending(r => r.GeocodeConfidence)
            .ThenByDescending(r => r.LocationText.Length)
            .FirstOrDefault();
        if (best is null)
            return CandidateLocation.Empty;
        var label = string.IsNullOrWhiteSpace(best.NormalizedKey)
            ? NormalizeLocation(best.LocationText)
            : best.NormalizedKey;
        var highConfidence = IsTrustworthyGeocode(best);
        return new CandidateLocation(
            label,
            highConfidence,
            best.GeocodeConfidence,
            best.GeocodeProvider,
            best.GeocodePrecision,
            best.Source,
            best.GeocodeQuery,
            best.GeocodeDisplayName,
            best.Latitude,
            best.Longitude);
    }

    private static string LocationKey(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.Label : string.Empty;

    private static string LocationLabel(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) && !string.IsNullOrWhiteSpace(value.Label) ? value.Label : "none";

    private static bool IsHighConfidenceGeocode(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) && value.IsHighConfidenceGeocode;

    private static double LocationGeocodeConfidence(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.GeocodeConfidence : 0;

    private static string LocationGeocodeProvider(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.GeocodeProvider : string.Empty;

    private static string LocationGeocodePrecision(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.GeocodePrecision : string.Empty;

    private static string LocationSource(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.Source : string.Empty;

    private static string LocationGeocodeQuery(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.GeocodeQuery : string.Empty;

    private static string LocationGeocodeDisplayName(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.GeocodeDisplayName : string.Empty;

    private static double LocationLatitude(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.Latitude : 0;

    private static double LocationLongitude(long callId, IReadOnlyDictionary<long, CandidateLocation> locations) =>
        locations.TryGetValue(callId, out var value) ? value.Longitude : 0;

    private static string CallText(EngineCall call) =>
        $"{call.SystemShortName} {call.Category} {call.TalkgroupName} {call.Transcription}";

    private static HashSet<string> Tokens(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "call", "calls", "unit", "units", "copy", "clear", "radio", "county", "dispatch",
            "scene", "status", "routine", "traffic", "responding", "advised", "reported"
        };
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches((text ?? string.Empty).ToLowerInvariant(), @"[a-z0-9]{3,}"))
        {
            var token = match.Value;
            if (token.Length < 4 || stop.Contains(token))
                continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private static double Similarity(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        var jaccard = union <= 0 ? 0 : intersection / (double)union;
        var containment = intersection / (double)Math.Min(a.Count, b.Count);
        return Math.Max(jaccard, containment * 0.72);
    }

    private static string NormalizeLocation(string value) =>
        Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    private static bool IsTrustworthyGeocode(CallLocationDashboardRow row) =>
        !string.IsNullOrWhiteSpace(row.GeocodeProvider) &&
        !string.Equals(row.GeocodeProvider, "none", StringComparison.OrdinalIgnoreCase) &&
        row.GeocodeConfidence >= HighConfidenceGeocodeThreshold &&
        TrustedGeocodePrecisions.Contains(row.GeocodePrecision) &&
        IsValidCoordinate(row.Latitude, row.Longitude);

    private static bool IsValidCoordinate(double latitude, double longitude) =>
        Math.Abs(latitude) > 0.0001 &&
        Math.Abs(longitude) > 0.0001 &&
        latitude is >= -90 and <= 90 &&
        longitude is >= -180 and <= 180;

    private sealed record CandidateLocation(
        string Label,
        bool IsHighConfidenceGeocode,
        double GeocodeConfidence,
        string GeocodeProvider,
        string GeocodePrecision,
        string Source,
        string GeocodeQuery,
        string GeocodeDisplayName,
        double Latitude,
        double Longitude)
    {
        public static readonly CandidateLocation Empty = new(string.Empty, false, 0, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, 0, 0);
    }

    private sealed record CallSemanticRow(
        EngineCall Call,
        HashSet<string> Tokens,
        HashSet<string> Anchors,
        string LocationKey,
        string LocationLabel,
        bool LocationIsHighConfidenceGeocode,
        double LocationGeocodeConfidence,
        string LocationGeocodeProvider,
        string LocationGeocodePrecision,
        string LocationSource,
        string LocationGeocodeQuery,
        string LocationGeocodeDisplayName,
        double LocationLatitude,
        double LocationLongitude,
        bool HasVectorMatch);
}
