namespace pizzad;

public sealed record IncidentReconciliationCandidate(
    int Index,
    string IncidentId,
    string Title,
    string Detail,
    string Category,
    double Confidence,
    IReadOnlyList<EngineCall> Calls);

public sealed record IncidentReconciliationDecision(
    int PrimaryIndex,
    string IncidentId,
    string Title,
    string Detail,
    string Category,
    double Confidence,
    IReadOnlyList<long> CallIds,
    string Reason,
    double Score,
    IReadOnlyList<int> MergedCandidateIndexes);

public sealed record IncidentReconciliationAudit(
    int CandidateIndex,
    string Operation,
    string Reason,
    double Score,
    IReadOnlyList<long> CallIds);

public sealed record IncidentReconciliationResult(
    IReadOnlyList<IncidentReconciliationDecision> Decisions,
    IReadOnlyList<IncidentReconciliationAudit> AuditRows);

public sealed class IncidentReconciliationService
{
    private const double CandidateMergeThreshold = 0.78;

    public IncidentReconciliationResult Reconcile(
        IReadOnlyList<IncidentReconciliationCandidate> candidates,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<CallLocationDashboardRow> locationRows,
        IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
        IReadOnlyDictionary<long, IncidentRagCandidate> ragByCall)
    {
        if (candidates.Count == 0)
            return new IncidentReconciliationResult([], []);

        _ = activeIncidents;

        var locationsByCall = locationRows
            .GroupBy(row => row.CallId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(row => row.GeocodeConfidence).First());

        var states = candidates
            .Select(candidate => CandidateState.FromCandidate(candidate, locationsByCall, anchorsByCall, ragByCall))
            .ToList();
        var audits = new List<IncidentReconciliationAudit>();

        for (var i = 0; i < states.Count; i++)
        {
            if (states[i].Consumed)
                continue;

            for (var j = i + 1; j < states.Count; j++)
            {
                if (states[j].Consumed)
                    continue;

                var match = Score(states[i], states[j]);
                if (match.Score < CandidateMergeThreshold)
                    continue;

                states[i] = states[i].Merge(states[j], $"merged sibling candidate {states[j].Index}: {match.Reason}", match.Score);
                states[j] = states[j] with { Consumed = true };
                audits.Add(new IncidentReconciliationAudit(
                    states[j].Index,
                    "merge_sibling_candidate",
                    $"merged into candidate {states[i].Index}: {match.Reason}",
                    match.Score,
                    states[j].CallIds.Order().ToList()));
            }
        }

        var decisions = new List<IncidentReconciliationDecision>();
        foreach (var state in states.Where(s => !s.Consumed))
        {
            decisions.Add(new IncidentReconciliationDecision(
                state.Index,
                state.IncidentId,
                state.Title,
                state.Detail,
                state.Category,
                state.Confidence,
                state.CallIds.Order().ToList(),
                state.Reason,
                state.Score,
                state.MergedIndexes.Order().ToList()));
        }

        return new IncidentReconciliationResult(decisions, audits);
    }

    private static SimilarityScore Score(CandidateState left, CandidateState right)
    {
        var score = 0d;
        var reasons = new List<string>();
        var minutes = GapMinutes(left, right);

        if (left.CallIds.Overlaps(right.CallIds))
        {
            score += 0.35;
            reasons.Add("shared call id");
        }

        var strongAnchorOverlap = left.StrongAnchorKeys.Intersect(right.StrongAnchorKeys, StringComparer.OrdinalIgnoreCase).Count();
        if (strongAnchorOverlap > 0)
        {
            score += 0.45;
            reasons.Add($"shared strong anchor x{strongAnchorOverlap}");
            if (minutes <= 60)
                score += 0.22;
        }

        var locationOverlap = left.LocationKeys.Intersect(right.LocationKeys, StringComparer.OrdinalIgnoreCase).Count();
        if (locationOverlap > 0)
        {
            score += 0.42;
            reasons.Add($"shared geocoded location x{locationOverlap}");
            if (minutes <= 30)
                score += 0.22;
        }
        else if (ClosestMiles(left, right) is { } miles && miles <= 0.15)
        {
            score += 0.38;
            reasons.Add($"nearby geocode {miles:0.00}mi");
        }

        var timeScore = Math.Max(0, 1 - minutes / 60d);
        if (timeScore > 0)
        {
            score += timeScore * 0.18;
            reasons.Add($"time proximity {timeScore:0.00}");
        }

        if (left.Categories.Overlaps(right.Categories))
        {
            score += 0.06;
            reasons.Add("compatible category");
        }

        var ragScore = Math.Min(left.AverageRagScore, right.AverageRagScore);
        if (ragScore >= 0.60)
        {
            score += ragScore * 0.15;
            reasons.Add($"rag support {ragScore:0.00}");
        }

        if (minutes > 75 && strongAnchorOverlap == 0 && locationOverlap == 0)
            score = Math.Min(score, 0.60);

        var closest = ClosestMiles(left, right);
        if (closest is > 5 && strongAnchorOverlap == 0)
            score = Math.Min(score, 0.55);

        return new SimilarityScore(Math.Clamp(score, 0, 1), reasons.Count == 0 ? "no structured similarity" : string.Join("; ", reasons));
    }

    private static double GapMinutes(CandidateState left, CandidateState right)
    {
        if (left.End <= right.Start)
            return (right.Start - left.End) / 60d;
        if (right.End <= left.Start)
            return (left.Start - right.End) / 60d;
        return 0;
    }

    private static double? ClosestMiles(CandidateState left, CandidateState right)
    {
        double? best = null;
        foreach (var a in left.Points)
        {
            foreach (var b in right.Points)
            {
                var miles = HaversineMiles(a.Latitude, a.Longitude, b.Latitude, b.Longitude);
                best = best is null ? miles : Math.Min(best.Value, miles);
            }
        }
        return best;
    }

    private static double HaversineMiles(double lat1, double lon1, double lat2, double lon2)
    {
        const double radiusMiles = 3958.7613;
        var dLat = DegreesToRadians(lat2 - lat1);
        var dLon = DegreesToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(DegreesToRadians(lat1)) * Math.Cos(DegreesToRadians(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return radiusMiles * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;

    private sealed record SimilarityScore(double Score, string Reason);

    private sealed record CandidateState(
        int Index,
        string IncidentId,
        string Title,
        string Detail,
        string Category,
        double Confidence,
        long Start,
        long End,
        HashSet<long> CallIds,
        HashSet<string> Categories,
        HashSet<string> LocationKeys,
        HashSet<string> StrongAnchorKeys,
        IReadOnlyList<GeoPoint> Points,
        double AverageRagScore,
        string Reason,
        double Score,
        HashSet<int> MergedIndexes,
        bool Consumed)
    {
        public static CandidateState FromCandidate(
            IncidentReconciliationCandidate candidate,
            IReadOnlyDictionary<long, CallLocationDashboardRow> locationsByCall,
            IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall,
            IReadOnlyDictionary<long, IncidentRagCandidate> ragByCall)
        {
            var callIds = candidate.Calls.Select(c => c.Id).ToHashSet();
            return new CandidateState(
                candidate.Index,
                candidate.IncidentId,
                candidate.Title,
                candidate.Detail,
                candidate.Category,
                candidate.Confidence,
                candidate.Calls.Min(c => c.StartTime),
                candidate.Calls.Max(c => c.StartTime),
                callIds,
                candidate.Calls.Select(c => c.Category).Where(v => !string.IsNullOrWhiteSpace(v)).ToHashSet(StringComparer.OrdinalIgnoreCase),
                BuildLocationKeys(callIds, locationsByCall),
                BuildStrongAnchorKeys(callIds, anchorsByCall),
                GeoPoints(callIds, locationsByCall),
                AverageRag(callIds, ragByCall),
                string.Empty,
                0,
                [candidate.Index],
                false);
        }

        public CandidateState Merge(CandidateState other, string reason, double score)
        {
            var mergedIds = CallIds.Concat(other.CallIds).ToHashSet();
            var useOtherNarrative = other.Confidence > Confidence && other.CallIds.Count >= CallIds.Count;
            return this with
            {
                IncidentId = PreferIncidentId(IncidentId, other.IncidentId),
                Title = useOtherNarrative ? other.Title : Title,
                Detail = useOtherNarrative ? other.Detail : Detail,
                Category = useOtherNarrative ? other.Category : Category,
                Confidence = Math.Max(Confidence, other.Confidence),
                Start = Math.Min(Start, other.Start),
                End = Math.Max(End, other.End),
                CallIds = mergedIds,
                Categories = Categories.Concat(other.Categories).ToHashSet(StringComparer.OrdinalIgnoreCase),
                LocationKeys = LocationKeys.Concat(other.LocationKeys).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StrongAnchorKeys = StrongAnchorKeys.Concat(other.StrongAnchorKeys).ToHashSet(StringComparer.OrdinalIgnoreCase),
                Points = Points.Concat(other.Points).ToList(),
                AverageRagScore = Math.Max(AverageRagScore, other.AverageRagScore),
                Reason = string.IsNullOrWhiteSpace(Reason) ? reason : $"{Reason}; {reason}",
                Score = Math.Max(Score, score),
                MergedIndexes = MergedIndexes.Concat(other.MergedIndexes).ToHashSet()
            };
        }

        public CandidateState RedirectToIncident(string incidentId, string reason, double score) =>
            this with
            {
                IncidentId = incidentId,
                Reason = string.IsNullOrWhiteSpace(Reason) ? reason : $"{Reason}; {reason}",
                Score = Math.Max(Score, score)
            };

        private static string PreferIncidentId(string left, string right)
        {
            if (!string.IsNullOrWhiteSpace(left) && !left.StartsWith("new", StringComparison.OrdinalIgnoreCase))
                return left;
            return string.IsNullOrWhiteSpace(right) ? left : right;
        }

        private static HashSet<string> BuildLocationKeys(IReadOnlySet<long> callIds, IReadOnlyDictionary<long, CallLocationDashboardRow> locationsByCall) =>
            callIds
                .Where(locationsByCall.ContainsKey)
                .Select(id => locationsByCall[id])
                .Where(row => row.GeocodeConfidence >= 0.55)
                .Select(row => string.IsNullOrWhiteSpace(row.NormalizedKey) ? row.GeocodeDisplayName : row.NormalizedKey)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static IReadOnlyList<GeoPoint> GeoPoints(IReadOnlySet<long> callIds, IReadOnlyDictionary<long, CallLocationDashboardRow> locationsByCall) =>
            callIds
                .Where(locationsByCall.ContainsKey)
                .Select(id => locationsByCall[id])
                .Where(row => row.GeocodeConfidence >= 0.55 && Math.Abs(row.Latitude) > 0.0001 && Math.Abs(row.Longitude) > 0.0001)
                .Select(row => new GeoPoint(row.Latitude, row.Longitude))
                .ToList();

        private static HashSet<string> BuildStrongAnchorKeys(IReadOnlySet<long> callIds, IReadOnlyDictionary<long, IReadOnlyList<CallAnchorRecord>> anchorsByCall) =>
            callIds
                .Where(anchorsByCall.ContainsKey)
                .SelectMany(id => anchorsByCall[id])
                .Where(anchor => anchor.Confidence >= 0.72)
                .Where(anchor => anchor.Kind is "vehicle_tag" or "highway_mile_marker" or "business_or_landmark")
                .Select(anchor => $"{anchor.Kind}:{anchor.Value}".ToLowerInvariant())
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static double AverageRag(IReadOnlySet<long> callIds, IReadOnlyDictionary<long, IncidentRagCandidate> ragByCall)
        {
            var scores = callIds.Where(ragByCall.ContainsKey).Select(id => ragByCall[id].Score).ToList();
            return scores.Count == 0 ? 0 : scores.Average();
        }
    }

    private sealed record GeoPoint(double Latitude, double Longitude);
}
