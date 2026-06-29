using System.Globalization;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed record IncidentFrameCurrentIncidentV3(
    string IncidentId,
    string Status,
    string Title,
    string Category,
    IReadOnlyList<string> CallIds,
    IReadOnlyList<string> RawCallIds,
    IReadOnlyList<string> DroppedCallIds);

public sealed record IncidentFrameV3(
    string FrameId,
    string FrameKey,
    string Maturity,
    string Lifecycle,
    string Title,
    string Category,
    string LocationLabel,
    IReadOnlyList<long> CandidateCallIds,
    IReadOnlyList<long> ExcludedCallIds,
    IReadOnlyList<string> Anchors,
    string MatchedCurrentIncidentId,
    string MatchedCurrentTitle,
    string MatchedCurrentStatus,
    string Reason)
{
    public IReadOnlyList<long> PromotionAmbiguousCallIds { get; init; } = [];
    public bool LocationIsHighConfidenceGeocode { get; init; }
    public double LocationGeocodeConfidence { get; init; }
    public string LocationGeocodeProvider { get; init; } = string.Empty;
    public string LocationGeocodePrecision { get; init; } = string.Empty;
    public string LocationSource { get; init; } = string.Empty;
    public string LocationGeocodeQuery { get; init; } = string.Empty;
    public string LocationGeocodeDisplayName { get; init; } = string.Empty;
    public double LocationLatitude { get; init; }
    public double LocationLongitude { get; init; }
    public string MatchedCurrentCategory { get; init; } = string.Empty;
    public IReadOnlyList<long> MatchedCurrentCallIds { get; init; } = [];
    public bool HasHospitalHandoffOrTransportMember { get; init; }
    public bool HasConflictingMemberLocations { get; init; }
}

public sealed record IncidentFramePromotionDecisionV3(
    string FrameId,
    string FrameKey,
    string Action,
    string Lifecycle,
    string Title,
    string Category,
    string LocationLabel,
    IReadOnlyList<long> CandidateCallIds,
    IReadOnlyList<long> PromotedCallIds,
    IReadOnlyList<long> AmbiguousCallIds,
    string MatchedCurrentIncidentId,
    string MatchedCurrentTitle,
    string Reason);

public sealed record IncidentFrameResolverScoreV3(
    string FrameId,
    string FrameKey,
    int Score,
    IReadOnlyList<string> Evidence);

public sealed record IncidentFrameResolverDecisionV3(
    long CallId,
    IReadOnlyList<string> CandidateFrameIds,
    IReadOnlyList<IncidentFrameResolverScoreV3> ScoresByFrame,
    string WinningFrameId,
    int ScoreMargin,
    string Decision,
    string DecisionReason,
    long? AmbiguousUntil,
    long? CutoffAt,
    bool WouldCreateIncident,
    string WouldAttachCurrentIncidentId,
    bool WouldDetachCreate,
    string DroppedBecause,
    IReadOnlyList<string> MembershipEvidence);

public sealed record IncidentPlanDecisionV3(
    string Action,
    string TargetIncidentId,
    string TargetIncidentTitle,
    string FrameId,
    string Title,
    string FrameTitle,
    string Category,
    string LocationLabel,
    IReadOnlyList<long> CallIds,
    string Reason);

public sealed class IncidentFrameBuilderV3
{
    private const long FrameWindowSeconds = 3600;
    private const long StrongCategoryWindowSeconds = 900;
    private const int ResolverMinimumWinningScore = 50;
    private const int ResolverMinimumWinningMargin = 20;
    private const long ResolverWeakPendingWindowSeconds = 600;
    private const long ResolverStrongMissingLocationWindowSeconds = 300;
    private const long ResolverAmbiguityWindowSeconds = 600;
    private static readonly HashSet<string> GenericEventLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fire response",
        "Medical response",
        "Police response",
        "Public safety incident"
    };
    private static readonly Regex NumberedAddressPattern = new(@"\b\d{1,6}\s+(?:[a-z0-9]+\s+){0,5}(?:rd|road|st|street|ave|avenue|dr|drive|ln|lane|cir|circle|ct|court|blvd|boulevard|pike|hwy|highway|way|pl|place)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NamedRoadPattern = new(@"\b(?:[a-z0-9]+\s+){1,5}(?:rd|road|st|street|ave|avenue|dr|drive|ln|lane|cir|circle|ct|court|blvd|boulevard|pike|hwy|highway|way|pl|place)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HighwayPattern = new(@"\b(?:i|interstate|hwy|highway|route|sr|us)\s*-?\s*\d{1,4}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BrokenDownVehicleHazardPattern = new(@"\b(?:broken down vehicle|(?:vehicle|car|truck|tractor trailer|trailer)\b.{0,45}\bbroken down|broken down\b.{0,80}\b(?:tow|tour|bad spot|road|roadway|hill|county line))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BrokenDownHighwayMileMarkerPattern = new(@"\b(?<marker>\d{1,3}(?:\.\d+)?)\s+(?:of|at|on)\s+(?:i[-\s]?|interstate\s+)?(?<route>\d{1,3})\s*(?<direction>westbound|eastbound|northbound|southbound|wb|eb|nb|sb)?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NonRoadwayHintPhrasePattern = new(@"\b(?:tow\s+truck|tour\s+gets?|on\s+the\s+way|way\s+to\s+(?:them|him|her|it)|waiting\s+on)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WeakLocationPhrasePattern = new(@"\b(?:cleared|coverage|management|station|hour down|going to|point and|on scene|en route|responding|available|switch|radio|channel|ve cleared|this to|with hospitals|wrong way|coming|show to|live circle|on dog)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HospitalHandoffOrTransportPattern = new(@"\b(?:ems\s+encode|hospital|medcom|non[-\s]?emergency\s+traffic|inbound\s+(?:non[-\s]?emergency\s+)?(?:traffic\s+)?to\s+your\s+facility|to\s+your\s+facility|coming\s+in\s+(?:now\s+)?(?:to|for)\s+(?:evaluation|your\s+facility)|transport(?:ing)?\s+to)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StreetSuffixTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "rd", "road", "st", "street", "ave", "avenue", "dr", "drive", "ln", "lane", "cir", "circle", "ct", "court", "blvd", "boulevard", "pike", "hwy", "highway", "way", "pl", "place"
    };
    private static readonly HashSet<string> DisallowedLocationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "there", "here", "wrong", "hospitals", "hospital", "coming", "available", "responding"
    };

    public IReadOnlyList<IncidentFrameV3> Build(
        string systemShortName,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentRagCandidate> candidates,
        IReadOnlyList<IncidentFrameCurrentIncidentV3> currentIncidents,
        int candidateLimit)
    {
        if (candidates.Count == 0)
            return [];

        var pool = candidates
            .OrderByDescending(row => row.Score)
            .ThenByDescending(row => row.GeoScore)
            .ThenByDescending(row => row.TimeScore)
            .Take(Math.Clamp(candidateLimit <= 0 ? 18 : candidateLimit, 6, 40))
            .Select(FrameRow.FromCandidate)
            .OrderBy(row => row.Call.StartTime)
            .ToList();

        var currentCallIds = currentIncidents
            .SelectMany(current => ParseCallIds(current.CallIds.Concat(current.RawCallIds)))
            .ToHashSet();
        var proposals = new List<FrameProposal>();
        foreach (var seed in pool.Where(row => IsFrameSeed(row, currentCallIds)))
        {
            var members = pool
                .Where(row => IsFrameMember(seed, row))
                .OrderBy(row => row.Call.StartTime)
                .ToList();
            if (members.Count == 0)
                continue;

            proposals.Add(BuildFrameProposal(seed, members));
        }

        var frames = proposals
            .GroupBy(proposal => proposal.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildFrame(systemShortName, group.ToList(), pool, activeIncidents, currentIncidents))
            .Where(ShouldEmitFrame)
            .ToList();

        return ApplyAmbiguousLifecycle(NormalizeCurrentMatchLocationConflicts(CollapseCurrentMatchedFrames(systemShortName, frames)))
            .OrderBy(frame => frame.CandidateCallIds.Min())
            .ThenBy(frame => frame.FrameKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<IncidentFramePromotionDecisionV3> BuildPromotionDecisions(IReadOnlyList<IncidentFrameV3> frames) =>
        BuildPromotionDecisions(frames, AmbiguousPromotionCallIdsByFrame(frames));

    public IReadOnlyList<IncidentFrameResolverDecisionV3> BuildResolverDecisions(
        IReadOnlyList<IncidentFrameV3> frames,
        IReadOnlyList<IncidentRagCandidate> candidates)
    {
        if (frames.Count == 0)
            return [];

        var candidateByCallId = candidates
            .GroupBy(candidate => candidate.Call.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var evaluationTime = candidateByCallId.Count == 0
            ? 0
            : candidateByCallId.Values.Max(candidate => Math.Max(candidate.Call.StartTime, candidate.Call.StopTime));
        var frameStartById = frames.ToDictionary(
            frame => frame.FrameId,
            frame => FrameStartTime(frame, candidateByCallId),
            StringComparer.OrdinalIgnoreCase);

        return frames
            .SelectMany(frame => frame.CandidateCallIds)
            .Distinct()
            .Order()
            .Select(callId =>
            {
                var candidateFrames = frames
                    .Where(frame => frame.CandidateCallIds.Contains(callId))
                    .OrderBy(frame => frame.FrameKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return BuildResolverDecision(callId, candidateFrames, frameStartById, evaluationTime);
            })
            .ToList();
    }

    public IReadOnlyList<IncidentPlanDecisionV3> BuildIncidentPlanDecisions(
        IReadOnlyList<IncidentFrameV3> frames,
        IReadOnlyList<IncidentFrameResolverDecisionV3> resolverDecisions)
    {
        var frameById = frames.ToDictionary(frame => frame.FrameId, StringComparer.OrdinalIgnoreCase);
        var resolverClaimedCallIds = resolverDecisions
            .Select(decision => decision.CallId)
            .ToHashSet();
        return resolverDecisions
            .GroupBy(decision => PlanKey(decision, frameById), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildIncidentPlanDecision(group.Key, group.ToList(), frameById, resolverClaimedCallIds))
            .OrderBy(plan => plan.CallIds.Count == 0 ? long.MaxValue : plan.CallIds.Min())
            .ThenBy(plan => plan.Action, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plan => plan.FrameId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string PlanKey(
        IncidentFrameResolverDecisionV3 decision,
        IReadOnlyDictionary<string, IncidentFrameV3> frameById) =>
        decision.Decision switch
        {
            "attach_current" => $"attach_current:{CurrentPlanTargetKey(decision, frameById)}",
            "attach_new" => $"attach_new:{NormalizeKey(decision.WinningFrameId)}",
            "detach_create" => $"detach_create:{NormalizeKey(decision.WinningFrameId)}",
            "hold_pending" => $"hold_pending:{NormalizeKey(decision.WinningFrameId)}",
            "ambiguous_hold" => $"ambiguous_hold:{NormalizeKey(decision.WinningFrameId)}",
            "ambiguous_drop" => $"ambiguous_drop:{NormalizeKey(decision.WinningFrameId)}",
            "suppress_stale" => $"suppress_stale:{NormalizeKey(decision.WinningFrameId)}",
            _ => $"{NormalizeKey(decision.Decision)}:{NormalizeKey(decision.WinningFrameId)}"
        };

    private static string CurrentPlanTargetKey(
        IncidentFrameResolverDecisionV3 decision,
        IReadOnlyDictionary<string, IncidentFrameV3> frameById)
    {
        var incidentId = NormalizeKey(decision.WouldAttachCurrentIncidentId);
        if (!IsPlaceholderCurrentIncidentId(decision.WouldAttachCurrentIncidentId))
            return incidentId;

        var title = frameById.TryGetValue(decision.WinningFrameId, out var frame)
            ? frame.MatchedCurrentTitle
            : string.Empty;
        return $"{incidentId}:{NormalizeKey(title)}";
    }

    private static bool IsPlaceholderCurrentIncidentId(string? incidentId)
    {
        if (string.IsNullOrWhiteSpace(incidentId))
            return true;
        var normalized = incidentId.Trim();
        return string.Equals(normalized, "new", StringComparison.OrdinalIgnoreCase);
    }

    private static IncidentPlanDecisionV3 BuildIncidentPlanDecision(
        string planKey,
        IReadOnlyList<IncidentFrameResolverDecisionV3> decisions,
        IReadOnlyDictionary<string, IncidentFrameV3> frameById,
        IReadOnlySet<long> resolverClaimedCallIds)
    {
        var first = decisions
            .OrderBy(decision => decision.CallId)
            .First();
        var frame = frameById.TryGetValue(first.WinningFrameId, out var matchedFrame)
            ? matchedFrame
            : null;
        var action = PlanAction(first.Decision);
        var requestedTargetIncidentId = string.Equals(action, "update_current", StringComparison.OrdinalIgnoreCase)
            ? first.WouldAttachCurrentIncidentId ?? string.Empty
            : string.Empty;
        var requestedTargetIncidentTitle = string.Equals(action, "update_current", StringComparison.OrdinalIgnoreCase)
            ? frame?.MatchedCurrentTitle ?? string.Empty
            : string.Empty;
        var usesCurrentMetadata = string.Equals(action, "update_current", StringComparison.OrdinalIgnoreCase);
        var requestedTargetIsNewPlaceholder = usesCurrentMetadata &&
                                              string.Equals(requestedTargetIncidentId.Trim(), "new", StringComparison.OrdinalIgnoreCase);
        string targetIncidentId = usesCurrentMetadata && IsActiveIncidentTarget(requestedTargetIncidentId)
            ? requestedTargetIncidentId
            : string.Empty;
        var targetIncidentTitle = !string.IsNullOrWhiteSpace(targetIncidentId)
            ? requestedTargetIncidentTitle
            : string.Empty;
        var title = !string.IsNullOrWhiteSpace(targetIncidentTitle)
            ? targetIncidentTitle
            : !string.IsNullOrWhiteSpace(requestedTargetIncidentTitle)
                ? requestedTargetIncidentTitle
            : frame?.Title ?? string.Empty;
        var category = usesCurrentMetadata &&
                       !string.IsNullOrWhiteSpace(frame?.MatchedCurrentCategory)
            ? frame.MatchedCurrentCategory
            : frame?.Category ?? string.Empty;
        var planDroppedBecause = string.Empty;
        if (usesCurrentMetadata &&
            string.IsNullOrWhiteSpace(targetIncidentId))
        {
            action = "hold_pending";
            planDroppedBecause = "non_active_current_target";
        }
        var createIneligibilityReason = string.Equals(action, "create_new", StringComparison.OrdinalIgnoreCase) && !usesCurrentMetadata
            ? CreateIneligibilityReason(frame)
            : string.Empty;
        if (string.Equals(action, "create_new", StringComparison.OrdinalIgnoreCase) &&
            !usesCurrentMetadata &&
            !string.IsNullOrWhiteSpace(createIneligibilityReason))
        {
            action = "hold_pending";
            planDroppedBecause = createIneligibilityReason;
        }
        if (string.Equals(action, "detach_create", StringComparison.OrdinalIgnoreCase) &&
            IsGenericTitle(title))
        {
            action = "hold_pending";
            planDroppedBecause = "generic_title";
        }
        var resolverCallIds = decisions
            .Select(decision => decision.CallId)
            .Distinct()
            .Order()
            .ToList();
        var callIds = PlanCallIds(action, resolverCallIds, frame, resolverClaimedCallIds);
        var currentCoverageCallIds = callIds
            .Except(resolverCallIds)
            .Order()
            .ToList();
        var reasons = decisions
            .Select(decision => decision.DecisionReason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        var reasonParts = new List<string>
        {
            $"resolverAction={first.Decision}",
            $"calls={string.Join(",", callIds)}"
        };
        if (currentCoverageCallIds.Count > 0)
            reasonParts.Add($"currentCoverageCallIds={string.Join(",", currentCoverageCallIds)}");
        if (!string.IsNullOrWhiteSpace(targetIncidentId))
            reasonParts.Add($"current={targetIncidentId}");
        else if (!string.IsNullOrWhiteSpace(requestedTargetIncidentId))
            reasonParts.Add($"current={requestedTargetIncidentId}");
        if (!string.IsNullOrWhiteSpace(frame?.Title) && !string.Equals(frame.Title, title, StringComparison.OrdinalIgnoreCase))
            reasonParts.Add($"frameTitle={frame.Title}");
        if (!string.IsNullOrWhiteSpace(first.DroppedBecause))
            reasonParts.Add($"droppedBecause={first.DroppedBecause}");
        if (!string.IsNullOrWhiteSpace(planDroppedBecause))
            reasonParts.Add($"planDroppedBecause={planDroppedBecause}");
        reasonParts.AddRange(reasons.Select(reason => $"reason={reason}"));

        return new IncidentPlanDecisionV3(
            action,
            targetIncidentId,
            targetIncidentTitle,
            frame?.FrameId ?? first.WinningFrameId,
            title,
            frame?.Title ?? string.Empty,
            category,
            frame?.LocationLabel ?? string.Empty,
            callIds,
            string.Join("; ", reasonParts));
    }

    private static List<long> PlanCallIds(
        string action,
        IReadOnlyList<long> resolverCallIds,
        IncidentFrameV3? frame,
        IReadOnlySet<long> resolverClaimedCallIds)
    {
        if (!string.Equals(action, "update_current", StringComparison.OrdinalIgnoreCase) ||
            frame is null ||
            frame.MatchedCurrentCallIds.Count == 0)
        {
            return resolverCallIds.ToList();
        }

        return resolverCallIds
            .Concat(frame.MatchedCurrentCallIds.Where(callId => !resolverClaimedCallIds.Contains(callId)))
            .Distinct()
            .Order()
            .ToList();
    }

    private static bool IsCreateEligibleUnmatchedFrame(IncidentFrameV3? frame)
    {
        if (frame is null)
            return false;
        if (frame.HasHospitalHandoffOrTransportMember)
            return false;
        if (frame.HasConflictingMemberLocations)
            return false;
        if (HasTrustedResolverLocation(frame))
            return true;
        if (IsSpecificRoadwayHazardFrame(frame))
            return true;
        return frame.Anchors.Any(IsStrongCreatePlanAnchor);
    }

    private static string CreateIneligibilityReason(IncidentFrameV3? frame)
    {
        if (frame is null)
            return "weak_create_evidence";
        if (frame.HasHospitalHandoffOrTransportMember)
            return "hospital_handoff_or_transport_member";
        if (frame.HasConflictingMemberLocations)
            return "conflicting_member_locations";
        return IsCreateEligibleUnmatchedFrame(frame) ? string.Empty : "weak_create_evidence";
    }

    private static bool IsStrongCreatePlanAnchor(string anchor)
    {
        if (anchor.StartsWith("address:", StringComparison.OrdinalIgnoreCase))
            return IsTrustedLocation(AnchorValue(anchor), geocoded: true);
        if (anchor.StartsWith("location:", StringComparison.OrdinalIgnoreCase))
            return IsTrustedLocation(AnchorValue(anchor), geocoded: true);
        if (anchor.StartsWith("intersection:", StringComparison.OrdinalIgnoreCase))
        {
            var value = AnchorValue(anchor);
            var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 2 && parts.All(IsCleanIntersectionRoad);
        }
        if (!anchor.StartsWith("highway_mile_marker:", StringComparison.OrdinalIgnoreCase))
            return false;

        var highwayValue = AnchorValue(anchor);
        var marker = Regex.Match(highwayValue, @"(?:mm|mile\s*marker)\s*:?\s*(?<marker>\d{1,3}(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return marker.Success &&
               double.TryParse(marker.Groups["marker"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mileMarker) &&
               mileMarker > 0;
    }

    private static string PlanAction(string resolverDecision) =>
        resolverDecision switch
        {
            "attach_current" => "update_current",
            "attach_new" => "create_new",
            "detach_create" => "detach_create",
            "hold_pending" => "hold_pending",
            "ambiguous_hold" => "hold_ambiguous",
            "ambiguous_drop" => "drop_ambiguous",
            "suppress_stale" => "suppress_stale",
            _ => "hold_pending"
        };

    private static bool IsActiveIncidentTarget(string? targetIncidentId)
    {
        var value = (targetIncidentId ?? string.Empty).Trim();
        if (!value.StartsWith("active:", StringComparison.OrdinalIgnoreCase))
            return false;
        return long.TryParse(value["active:".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
    }

    private static IncidentFrameResolverDecisionV3 BuildResolverDecision(
        long callId,
        IReadOnlyList<IncidentFrameV3> candidateFrames,
        IReadOnlyDictionary<string, long> frameStartById,
        long evaluationTime)
    {
        var scores = candidateFrames
            .Select(frame => ResolverScore(frame, callId))
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.FrameKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var top = scores.FirstOrDefault();
        var second = scores.Skip(1).FirstOrDefault();
        var winningFrame = top is null
            ? null
            : candidateFrames.First(frame => string.Equals(frame.FrameId, top.FrameId, StringComparison.OrdinalIgnoreCase));
        var scoreMargin = top is null
            ? 0
            : top.Score - (second?.Score ?? 0);
        long? cutoffAt = candidateFrames.Count == 0
            ? null
            : candidateFrames.Max(frame => frameStartById.TryGetValue(frame.FrameId, out var start)
                ? start + ResolverWindowSeconds(frame)
                : ResolverAmbiguityWindowSeconds);
        var ambiguousUntil = cutoffAt;
        var topIsAmbiguous = winningFrame?.PromotionAmbiguousCallIds.Contains(callId) == true;
        if (candidateFrames.Count == 1 &&
            winningFrame is not null &&
            string.IsNullOrWhiteSpace(winningFrame.MatchedCurrentIncidentId) &&
            IsGenericTitle(winningFrame.Title))
        {
            var genericDecision = ResolverDecisionForWinner(winningFrame);
            return new IncidentFrameResolverDecisionV3(
                callId,
                candidateFrames.Select(frame => frame.FrameId).ToList(),
                scores,
                top?.FrameId ?? string.Empty,
                scoreMargin,
                genericDecision.Action,
                genericDecision.Reason,
                null,
                cutoffAt,
                genericDecision.WouldCreateIncident,
                genericDecision.WouldAttachCurrentIncidentId,
                genericDecision.WouldDetachCreate,
                genericDecision.DroppedBecause,
                top?.Evidence ?? []);
        }

        var isAmbiguous = top is null ||
                          top.Score < ResolverMinimumWinningScore ||
                          (candidateFrames.Count > 1 && scoreMargin < ResolverMinimumWinningMargin) ||
                          topIsAmbiguous;
        if (isAmbiguous)
        {
            var waitOpen = ambiguousUntil is not null && evaluationTime > 0 && evaluationTime < ambiguousUntil.Value;
            var reason = top is null
                ? "no_candidate_frame"
                : topIsAmbiguous
                    ? "promotion_ambiguous"
                    : top.Score < ResolverMinimumWinningScore
                        ? "winner_below_minimum_score"
                        : "winner_margin_too_small";
            return new IncidentFrameResolverDecisionV3(
                callId,
                candidateFrames.Select(frame => frame.FrameId).ToList(),
                scores,
                top?.FrameId ?? string.Empty,
                scoreMargin,
                waitOpen ? "ambiguous_hold" : "ambiguous_drop",
                reason,
                ambiguousUntil,
                cutoffAt,
                false,
                string.Empty,
                false,
                waitOpen ? string.Empty : reason,
                top?.Evidence ?? []);
        }

        if (IsBorderlineSingleCallNewCandidate(winningFrame!, top!))
        {
            return new IncidentFrameResolverDecisionV3(
                callId,
                candidateFrames.Select(frame => frame.FrameId).ToList(),
                scores,
                top!.FrameId,
                scoreMargin,
                "hold_pending",
                "borderline_single_call_new_candidate",
                null,
                cutoffAt,
                false,
                string.Empty,
                false,
                string.Empty,
                top.Evidence);
        }

        var decision = ResolverDecisionForWinner(winningFrame!);
        return new IncidentFrameResolverDecisionV3(
            callId,
            candidateFrames.Select(frame => frame.FrameId).ToList(),
            scores,
            top!.FrameId,
            scoreMargin,
            decision.Action,
            decision.Reason,
            null,
            cutoffAt,
            decision.WouldCreateIncident,
            decision.WouldAttachCurrentIncidentId,
            decision.WouldDetachCreate,
            decision.DroppedBecause,
            top.Evidence);
    }

    private static bool IsBorderlineSingleCallNewCandidate(
        IncidentFrameV3 frame,
        IncidentFrameResolverScoreV3 score)
    {
        if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId))
            return false;
        if (!string.Equals(frame.Lifecycle, "mature", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(frame.Maturity, "multi_call", StringComparison.OrdinalIgnoreCase) ||
            frame.CandidateCallIds.Count > 1)
        {
            return false;
        }
        if (HasTrustedResolverLocation(frame))
            return false;
        return score.Score <= ResolverMinimumWinningScore;
    }

    private static IncidentFrameResolverScoreV3 ResolverScore(IncidentFrameV3 frame, long callId)
    {
        var score = 0;
        var evidence = new List<string>();

        if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId) &&
            HasCurrentOverlapForCall(frame, callId))
        {
            score += 100;
            evidence.Add("current_overlap=100");
        }

        if (HasTrustedResolverLocation(frame))
        {
            score += 70;
            evidence.Add("trusted_location=70");
        }

        if (IsSpecificRoadwayHazardFrame(frame))
        {
            score += 25;
            evidence.Add("specific_roadway_hazard=25");
        }

        if (string.Equals(frame.Lifecycle, "mature", StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
            evidence.Add("mature=30");
        }
        else if (string.Equals(frame.Lifecycle, "pending", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
            evidence.Add("pending=10");
        }

        if (string.Equals(frame.Maturity, "multi_call", StringComparison.OrdinalIgnoreCase) ||
            frame.CandidateCallIds.Count > 1)
        {
            score += 25;
            evidence.Add("multi_call=25");
        }

        if (!IsGenericTitle(frame.Title))
        {
            score += 20;
            evidence.Add("specific_title=20");
        }

        if (frame.PromotionAmbiguousCallIds.Contains(callId))
        {
            score -= 60;
            evidence.Add("promotion_ambiguous=-60");
        }

        if (string.Equals(frame.Lifecycle, "conflicted", StringComparison.OrdinalIgnoreCase))
        {
            score -= 20;
            evidence.Add("conflicted=-20");
        }

        return new IncidentFrameResolverScoreV3(frame.FrameId, frame.FrameKey, score, evidence);
    }

    private static bool HasCurrentOverlapForCall(IncidentFrameV3 frame, long callId)
    {
        return frame.MatchedCurrentCallIds.Contains(callId);
    }

    private static (string Action, string Reason, bool WouldCreateIncident, string WouldAttachCurrentIncidentId, bool WouldDetachCreate, string DroppedBecause) ResolverDecisionForWinner(IncidentFrameV3 frame)
    {
        if (string.Equals(frame.Lifecycle, "stale", StringComparison.OrdinalIgnoreCase))
            return ("suppress_stale", "stale_frame_no_new_create", false, string.Empty, false, "stale_frame");

        if (string.Equals(frame.Lifecycle, "pending", StringComparison.OrdinalIgnoreCase))
            return ("hold_pending", "pending_frame_waiting_for_evidence", false, string.Empty, false, string.Empty);

        if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId))
        {
            if (string.Equals(frame.Lifecycle, "conflicted", StringComparison.OrdinalIgnoreCase) &&
                HasStrongFrameCurrentConflictLocation(frame))
            {
                return ("detach_create", "strong_location_conflicts_with_current", true, string.Empty, true, string.Empty);
            }

            return ("attach_current", "highest_scored_current_match", false, frame.MatchedCurrentIncidentId, false, string.Empty);
        }

        if (IsGenericTitle(frame.Title))
            return ("hold_pending", "generic_title_blocks_create", false, string.Empty, false, "generic_title");

        if (string.Equals(frame.Lifecycle, "mature", StringComparison.OrdinalIgnoreCase))
            return ("attach_new", "highest_scored_new_incident_candidate", true, string.Empty, false, string.Empty);

        return ("hold_pending", "default_hold_pending", false, string.Empty, false, string.Empty);
    }

    private static long FrameStartTime(
        IncidentFrameV3 frame,
        IReadOnlyDictionary<long, IncidentRagCandidate> candidateByCallId)
    {
        var starts = frame.CandidateCallIds
            .Where(candidateByCallId.ContainsKey)
            .Select(callId => candidateByCallId[callId].Call.StartTime)
            .ToList();
        return starts.Count == 0 ? 0 : starts.Min();
    }

    private static long ResolverWindowSeconds(IncidentFrameV3 frame)
    {
        if (string.Equals(frame.Lifecycle, "pending", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(frame.Lifecycle, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return ResolverWeakPendingWindowSeconds;
        }

        if (string.Equals(frame.Lifecycle, "mature", StringComparison.OrdinalIgnoreCase) &&
            frame.CandidateCallIds.Count == 1 &&
            IsEmptyLocation(frame.LocationLabel))
        {
            return ResolverStrongMissingLocationWindowSeconds;
        }

        return ResolverAmbiguityWindowSeconds;
    }

    private static IReadOnlyList<IncidentFramePromotionDecisionV3> BuildPromotionDecisions(
        IReadOnlyList<IncidentFrameV3> frames,
        IReadOnlyDictionary<string, HashSet<long>> ambiguousCallIdsByFrame) =>
        frames
            .OrderBy(frame => frame.CandidateCallIds.Count == 0 ? long.MaxValue : frame.CandidateCallIds.Min())
            .ThenBy(frame => frame.FrameKey, StringComparer.OrdinalIgnoreCase)
            .Select(frame => BuildPromotionDecision(
                frame,
                ambiguousCallIdsByFrame.TryGetValue(frame.FrameId, out var ambiguousCallIds)
                    ? ambiguousCallIds
                    : []))
            .ToList();

    private static IncidentFramePromotionDecisionV3 BuildPromotionDecision(
        IncidentFrameV3 frame,
        IReadOnlySet<long> ambiguousCallIds)
    {
        var frameAmbiguousCallIds = frame.CandidateCallIds
            .Where(ambiguousCallIds.Contains)
            .Distinct()
            .Order()
            .ToList();
        var promotedCallIds = frame.CandidateCallIds
            .Where(id => !frameAmbiguousCallIds.Contains(id))
            .Distinct()
            .Order()
            .ToList();
        var action = PromotionAction(frame, frameAmbiguousCallIds);
        return new IncidentFramePromotionDecisionV3(
            frame.FrameId,
            frame.FrameKey,
            action,
            frame.Lifecycle,
            frame.Title,
            frame.Category,
            frame.LocationLabel,
            frame.CandidateCallIds,
            promotedCallIds,
            frameAmbiguousCallIds,
            frame.MatchedCurrentIncidentId,
            frame.MatchedCurrentTitle,
            PromotionReason(frame, action, promotedCallIds, frameAmbiguousCallIds));
    }

    private static Dictionary<string, HashSet<long>> AmbiguousPromotionCallIdsByFrame(IReadOnlyList<IncidentFrameV3> frames)
    {
        var ambiguousCallIds = frames
            .Where(frame => string.Equals(frame.Lifecycle, "ambiguous", StringComparison.OrdinalIgnoreCase))
            .SelectMany(frame => frame.CandidateCallIds)
            .ToHashSet();
        var ambiguousByFrame = frames.ToDictionary(
            frame => frame.FrameId,
            frame => frame.CandidateCallIds
                .Where(ambiguousCallIds.Contains)
                .Concat(frame.PromotionAmbiguousCallIds)
                .ToHashSet(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var overlap in CurrentMatchOverlaps(frames))
        {
            var winner = overlap.Frames
                .OrderByDescending(frame => CurrentMatchPromotionScore(frame))
                .ThenBy(frame => frame.CandidateCallIds.Count)
                .ThenBy(frame => frame.FrameKey, StringComparer.OrdinalIgnoreCase)
                .First();

            foreach (var frame in overlap.Frames.Where(frame => !string.Equals(frame.FrameId, winner.FrameId, StringComparison.OrdinalIgnoreCase)))
                ambiguousByFrame[frame.FrameId].Add(overlap.CallId);
        }

        return ambiguousByFrame;
    }

    private static IEnumerable<(long CallId, IReadOnlyList<IncidentFrameV3> Frames)> CurrentMatchOverlaps(IReadOnlyList<IncidentFrameV3> frames) =>
        frames
            .Where(frame => !string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId))
            .SelectMany(frame => frame.CandidateCallIds.Distinct().Select(callId => new { callId, frame }))
            .GroupBy(item => item.callId)
            .Select(group => (
                CallId: group.Key,
                Frames: (IReadOnlyList<IncidentFrameV3>)group
                    .Select(item => item.frame)
                    .DistinctBy(frame => frame.FrameId)
                    .ToList()))
            .Where(overlap => overlap.Frames
                .Select(CurrentMatchCollapseKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1);

    private static int CurrentMatchPromotionScore(IncidentFrameV3 frame)
    {
        var score = CurrentTitleAnchorScore(frame, frame.MatchedCurrentTitle) * 10;
        score += FrameTitleQuality(frame);
        if (!IsEmptyLocation(frame.LocationLabel))
            score += 1;
        return score;
    }

    private static string PromotionAction(IncidentFrameV3 frame, IReadOnlyList<long> ambiguousCallIds)
    {
        if (string.Equals(frame.Lifecycle, "conflicted", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId))
            {
                if (ambiguousCallIds.Count > 0)
                    return "match_current_location_disagreement_with_ambiguous_members";
                return "match_current_location_disagreement";
            }
            return "flag_conflict";
        }
        if (string.Equals(frame.Lifecycle, "ambiguous", StringComparison.OrdinalIgnoreCase))
            return "mark_ambiguous";
        if (string.Equals(frame.Lifecycle, "stale", StringComparison.OrdinalIgnoreCase))
            return "hold_stale";
        if (string.Equals(frame.Lifecycle, "pending", StringComparison.OrdinalIgnoreCase))
            return "hold_pending";
        if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId))
        {
            if (ambiguousCallIds.Count > 0)
                return "match_current_with_ambiguous_members";
            return "match_current";
        }
        if (string.Equals(frame.Lifecycle, "mature", StringComparison.OrdinalIgnoreCase))
            return "create";
        return "hold_pending";
    }

    private static string PromotionReason(
        IncidentFrameV3 frame,
        string action,
        IReadOnlyList<long> promotedCallIds,
        IReadOnlyList<long> ambiguousCallIds)
    {
        var traits = new List<string>
        {
            $"action={action}",
            $"lifecycle={frame.Lifecycle}",
            $"maturity={frame.Maturity}",
            $"calls={string.Join(",", frame.CandidateCallIds)}",
            $"promotedCalls={string.Join(",", promotedCallIds)}"
        };
        if (ambiguousCallIds.Count > 0)
            traits.Add($"ambiguousCalls={string.Join(",", ambiguousCallIds)}");
        if (frame.LocationIsHighConfidenceGeocode && !IsEmptyLocation(frame.LocationLabel))
            traits.Add($"location={frame.LocationLabel}");
        if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId))
            traits.Add($"current={frame.MatchedCurrentIncidentId}");
        if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentTitle))
            traits.Add($"currentTitle={frame.MatchedCurrentTitle}");
        return string.Join("; ", traits);
    }

    private static IncidentFrameV3 BuildFrame(
        string systemShortName,
        IReadOnlyList<FrameProposal> proposals,
        IReadOnlyList<FrameRow> pool,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentFrameCurrentIncidentV3> currentIncidents)
    {
        var seed = proposals
            .Select(proposal => proposal.Seed)
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Call.StartTime)
            .First();
        var members = proposals
            .SelectMany(proposal => proposal.Members)
            .DistinctBy(row => row.Call.Id)
            .OrderBy(row => row.Call.StartTime)
            .ToList();
        var memberIds = members.Select(row => row.Call.Id).Distinct().Order().ToList();
        var anchors = members
                .SelectMany(row => row.Anchors)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        var excludedIds = pool
            .Where(row => !memberIds.Contains(row.Call.Id))
            .Where(row => Math.Abs(row.Call.StartTime - seed.Call.StartTime) <= FrameWindowSeconds)
            .Select(row => row.Call.Id)
            .Distinct()
            .Order()
            .Take(12)
            .ToList();
        var match = MatchCurrentIncident(seed, members, anchors, activeIncidents, currentIncidents);
        var category = NormalizeCategory(seed.Call.Category);
        var location = proposals
            .Select(proposal => proposal.CanonicalLocation)
            .FirstOrDefault(value => !IsEmptyLocation(value)) ?? string.Empty;
        var locationEvidence = SelectLocationEvidence(members, location);
        var title = BuildTitle(seed, location, anchors);
        var maturity = !string.IsNullOrWhiteSpace(match.IncidentId)
            ? "current_matched"
            : members.Count > 1 ? "multi_call" : "single_call";
        var lifecycle = ClassifyLifecycle(seed, members, pool, match, location, anchors);
        var reason = $"{BuildReason(seed, members, match, location)}; lifecycle={lifecycle}";
        var frameKey = proposals[0].CanonicalKey;

        return new IncidentFrameV3(
            BuildFrameId(systemShortName, seed, frameKey),
            frameKey,
            maturity,
            lifecycle,
            title,
            category,
            location,
            memberIds,
            excludedIds,
            anchors,
            match.IncidentId,
            match.Title,
            match.Status,
            reason)
        {
            MatchedCurrentCategory = match.Category,
            LocationIsHighConfidenceGeocode = locationEvidence?.LocationIsHighConfidenceGeocode ?? false,
            LocationGeocodeConfidence = locationEvidence?.LocationGeocodeConfidence ?? 0,
            LocationGeocodeProvider = locationEvidence?.LocationGeocodeProvider ?? string.Empty,
            LocationGeocodePrecision = locationEvidence?.LocationGeocodePrecision ?? string.Empty,
            LocationSource = locationEvidence?.LocationSource ?? string.Empty,
            LocationGeocodeQuery = locationEvidence?.LocationGeocodeQuery ?? string.Empty,
            LocationGeocodeDisplayName = locationEvidence?.LocationGeocodeDisplayName ?? string.Empty,
            LocationLatitude = locationEvidence?.LocationLatitude ?? 0,
            LocationLongitude = locationEvidence?.LocationLongitude ?? 0,
            MatchedCurrentCallIds = match.CallIds.Order().ToList(),
            HasHospitalHandoffOrTransportMember = members.Any(IsHospitalHandoffOrTransportRow),
            HasConflictingMemberLocations = HasConflictingMemberLocations(members)
        };
    }

    private static bool IsHospitalHandoffOrTransportRow(FrameRow row)
    {
        var text = $"{row.Call.TalkgroupName} {row.Call.Transcription}";
        return HospitalHandoffOrTransportPattern.IsMatch(text);
    }

    private static bool IsFrameSeed(FrameRow row, IReadOnlySet<long> currentCallIds) =>
        currentCallIds.Contains(row.Call.Id) || row.Anchors.Count > 0 || row.StrongIncidentSignal;

    private static IReadOnlyList<IncidentFrameV3> CollapseCurrentMatchedFrames(
        string systemShortName,
        IReadOnlyList<IncidentFrameV3> frames)
    {
        var collapsed = new List<IncidentFrameV3>();
        collapsed.AddRange(frames.Where(frame => CurrentMatchCollapseKey(frame).Length == 0));
        collapsed.AddRange(frames
            .Where(frame => CurrentMatchCollapseKey(frame).Length > 0)
            .GroupBy(CurrentMatchCollapseKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count() == 1
                ? group.First()
                : MergeCurrentMatchedFrames(systemShortName, group.Key, group.ToList())));
        return collapsed;
    }

    private static string CurrentMatchCollapseKey(IncidentFrameV3 frame)
    {
        if (string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId) ||
            string.IsNullOrWhiteSpace(frame.MatchedCurrentTitle))
        {
            return string.Empty;
        }

        return $"{NormalizeKey(frame.MatchedCurrentIncidentId)}:{NormalizeKey(frame.MatchedCurrentTitle)}";
    }

    private static IncidentFrameV3 MergeCurrentMatchedFrames(
        string systemShortName,
        string collapseKey,
        IReadOnlyList<IncidentFrameV3> frames)
    {
        var currentTitle = frames
            .Select(frame => frame.MatchedCurrentTitle)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var representative = frames
            .OrderByDescending(frame => CurrentTitleAnchorScore(frame, currentTitle))
            .ThenBy(frame => string.Equals(frame.Lifecycle, "conflicted", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(FrameTitleQuality)
            .ThenBy(frame => frame.CandidateCallIds.Min())
            .First();
        var collapseAmbiguousIds = CurrentMatchLocationAmbiguousCallIds(frames, currentTitle);
        var anchorSourceFrames = frames
            .Where(frame => !frame.CandidateCallIds.Any(collapseAmbiguousIds.Contains))
            .ToList();
        if (anchorSourceFrames.Count == 0)
            anchorSourceFrames = frames.ToList();
        var candidateIds = frames
            .SelectMany(frame => frame.CandidateCallIds)
            .Distinct()
            .Order()
            .ToList();
        var excludedIds = frames
            .SelectMany(frame => frame.ExcludedCallIds)
            .Where(id => !candidateIds.Contains(id))
            .Distinct()
            .Order()
            .Take(12)
            .ToList();
        var anchors = anchorSourceFrames
            .SelectMany(frame => frame.Anchors)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var location = anchorSourceFrames
            .OrderByDescending(frame => CurrentTitleAnchorScore(frame, currentTitle))
            .ThenBy(frame => string.Equals(frame.Lifecycle, "conflicted", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .Select(frame => frame.LocationLabel)
            .FirstOrDefault(value => !IsEmptyLocation(value)) ?? string.Empty;
        var locationEvidence = SelectLocationEvidence(anchorSourceFrames, location);
        var reasons = string.Join(" | ", frames.Select(frame => frame.Reason));
        var lifecycle = anchorSourceFrames.Any(frame => string.Equals(frame.Lifecycle, "conflicted", StringComparison.OrdinalIgnoreCase)) ||
                        HasInternalLocationConflict(anchorSourceFrames)
            ? "conflicted"
            : "mature";
        var reason = $"merged current match; frames={frames.Count}; lifecycle={lifecycle}; {reasons}";
        if (collapseAmbiguousIds.Count > 0)
            reason = $"{reason}; collapseAmbiguousCallIds={string.Join(",", collapseAmbiguousIds)}";
        var currentCategory = frames
            .Select(frame => frame.MatchedCurrentCategory)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
        var matchedCurrentCallIds = frames
            .SelectMany(frame => frame.MatchedCurrentCallIds)
            .Distinct()
            .Order()
            .ToList();

        return representative with
        {
            FrameId = $"v3:{NormalizeKey(systemShortName)}:current:{collapseKey}",
            FrameKey = $"current:{collapseKey}",
            Maturity = "current_matched",
            Lifecycle = lifecycle,
            LocationLabel = location,
            LocationIsHighConfidenceGeocode = locationEvidence?.LocationIsHighConfidenceGeocode ?? false,
            LocationGeocodeConfidence = locationEvidence?.LocationGeocodeConfidence ?? 0,
            LocationGeocodeProvider = locationEvidence?.LocationGeocodeProvider ?? string.Empty,
            LocationGeocodePrecision = locationEvidence?.LocationGeocodePrecision ?? string.Empty,
            LocationSource = locationEvidence?.LocationSource ?? string.Empty,
            LocationGeocodeQuery = locationEvidence?.LocationGeocodeQuery ?? string.Empty,
            LocationGeocodeDisplayName = locationEvidence?.LocationGeocodeDisplayName ?? string.Empty,
            LocationLatitude = locationEvidence?.LocationLatitude ?? 0,
            LocationLongitude = locationEvidence?.LocationLongitude ?? 0,
            CandidateCallIds = candidateIds,
            ExcludedCallIds = excludedIds,
            Anchors = anchors,
            Reason = reason,
            MatchedCurrentCategory = currentCategory,
            MatchedCurrentCallIds = matchedCurrentCallIds,
            PromotionAmbiguousCallIds = collapseAmbiguousIds
        };
    }

    private static IReadOnlyList<long> CurrentMatchLocationAmbiguousCallIds(
        IReadOnlyList<IncidentFrameV3> frames,
        string currentTitle)
    {
        var currentAnchors = TextLocationConflictAnchors(currentTitle);
        if (currentAnchors.Count == 0)
            return [];

        var frameLocations = frames
            .Select(frame => new { Frame = frame, Anchors = StrongFrameCurrentConflictAnchors(frame) })
            .Where(item => item.Anchors.Count > 0)
            .ToList();
        if (frameLocations.Count < 2 ||
            !frameLocations.Any(item => LocationAnchorSetsCompatible(item.Anchors, currentAnchors)))
        {
            return [];
        }

        return frameLocations
            .Where(item => !LocationAnchorSetsCompatible(item.Anchors, currentAnchors))
            .SelectMany(item => item.Frame.CandidateCallIds)
            .Distinct()
            .Order()
            .ToList();
    }

    private static IReadOnlyList<IncidentFrameV3> NormalizeCurrentMatchLocationConflicts(IReadOnlyList<IncidentFrameV3> frames) =>
        frames
            .Select(frame =>
            {
                if (string.Equals(frame.Lifecycle, "conflicted", StringComparison.OrdinalIgnoreCase) ||
                    !HasFrameCurrentLocationConflict(frame))
                {
                    return frame;
                }

                return frame with
                {
                    Lifecycle = "conflicted",
                    Reason = $"{frame.Reason}; lifecycle=conflicted; currentLocationConflict=post_frame"
                };
            })
            .ToList();

    private static bool HasFrameCurrentLocationConflict(IncidentFrameV3 frame)
    {
        if (string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId))
            return false;

        var currentAnchors = TextLocationConflictAnchors(frame.MatchedCurrentTitle);
        if (currentAnchors.Count == 0)
            return false;

        var frameAnchors = StrongFrameCurrentConflictAnchors(frame);
        if (frameAnchors.Count == 0)
            return false;

        return !LocationAnchorSetsCompatible(frameAnchors, currentAnchors);
    }

    private static int CurrentTitleAnchorScore(IncidentFrameV3 frame, string currentTitle)
    {
        var currentAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(currentTitle);
        if (currentAnchors.Count == 0)
            return 0;

        var frameAnchors = frame.Anchors.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (frame.LocationIsHighConfidenceGeocode && !IsEmptyLocation(frame.LocationLabel))
            frameAnchors.Add($"location:{frame.LocationLabel}");

        return IncidentCandidateValidator.SharedConcreteAnchorCount(frameAnchors, currentAnchors);
    }

    private static bool HasInternalLocationConflict(IReadOnlyList<IncidentFrameV3> frames)
    {
        var locationSets = frames
            .Select(FrameLocationAnchors)
            .Where(anchors => anchors.Count > 0)
            .ToList();
        for (var i = 0; i < locationSets.Count; i++)
        {
            for (var j = i + 1; j < locationSets.Count; j++)
            {
                if (!LocationAnchorSetsCompatible(locationSets[i], locationSets[j]))
                    return true;
            }
        }

        return false;
    }

    private static bool HasConflictingMemberLocations(IReadOnlyList<FrameRow> members)
    {
        var locatedMembers = members
            .Select(row => StrictMemberLocationAnchors(row))
            .Where(anchors => anchors.Count > 0)
            .ToList();
        for (var i = 0; i < locatedMembers.Count; i++)
        {
            for (var j = i + 1; j < locatedMembers.Count; j++)
            {
                if (!StrictMemberLocationAnchorsCompatible(locatedMembers[i], locatedMembers[j]))
                    return true;
            }
        }

        return false;
    }

    private static HashSet<string> StrictMemberLocationAnchors(FrameRow row)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in row.Anchors)
        {
            if (anchor.StartsWith("location:", StringComparison.OrdinalIgnoreCase) ||
                anchor.StartsWith("address:", StringComparison.OrdinalIgnoreCase) ||
                anchor.StartsWith("intersection:", StringComparison.OrdinalIgnoreCase) ||
                anchor.StartsWith("highway_mile_marker:", StringComparison.OrdinalIgnoreCase))
            {
                anchors.Add(NormalizeStrictMemberLocationAnchor(anchor));
            }
        }

        return anchors;
    }

    private static bool StrictMemberLocationAnchorsCompatible(HashSet<string> left, HashSet<string> right) =>
        left.Count == 0 ||
        right.Count == 0 ||
        left.Any(right.Contains);

    private static string NormalizeStrictMemberLocationAnchor(string anchor)
    {
        var prefixIndex = anchor.IndexOf(':');
        var kind = prefixIndex > 0 ? anchor[..prefixIndex].ToLowerInvariant() : string.Empty;
        var value = prefixIndex >= 0 && prefixIndex + 1 < anchor.Length ? anchor[(prefixIndex + 1)..] : anchor;
        value = NormalizeTrustedLocationText(value.Replace("|", " ", StringComparison.Ordinal));
        value = Regex.Replace(value, @"\b(rd|road)\b", "road", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b(st|street)\b", "street", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b(ave|avenue)\b", "avenue", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b(dr|drive)\b", "drive", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b(ln|lane)\b", "lane", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b(ct|court)\b", "court", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\b(blvd|boulevard)\b", "boulevard", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return $"{kind}:{value}";
    }

    private static HashSet<string> FrameLocationAnchors(IncidentFrameV3 frame)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (frame.LocationIsHighConfidenceGeocode && !IsEmptyLocation(frame.LocationLabel))
            anchors.Add($"location:{frame.LocationLabel}");
        return anchors;
    }

    private static HashSet<string> StrongFrameCurrentConflictAnchors(IncidentFrameV3 frame)
    {
        var anchors = StrongCurrentConflictAnchors(frame.LocationLabel, frame.LocationIsHighConfidenceGeocode, frame.Anchors);
        anchors.UnionWith(TextLocationConflictAnchors(frame.Title));
        return anchors;
    }

    private static bool HasStrongFrameCurrentConflictLocation(IncidentFrameV3 frame) =>
        StrongFrameCurrentConflictAnchors(frame).Count > 0;

    private static bool HasTrustedResolverLocation(IncidentFrameV3 frame) =>
        frame.LocationIsHighConfidenceGeocode && IsTrustedLocation(frame.LocationLabel, geocoded: true);

    private static bool IsSpecificRoadwayHazardFrame(IncidentFrameV3 frame) =>
        frame.Title.StartsWith("Broken down vehicle on ", StringComparison.OrdinalIgnoreCase) &&
        frame.Anchors.Any(IsRoadwayTitleAnchor);

    private static HashSet<string> StrongCurrentConflictAnchors(
        string location,
        bool locationIsHighConfidenceGeocode,
        IEnumerable<string> rawAnchors)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (locationIsHighConfidenceGeocode && !IsEmptyLocation(location) && IsTrustedLocation(location, geocoded: true))
            anchors.Add($"location:{location}");
        foreach (var anchor in rawAnchors.Where(IsStrongCurrentConflictAnchor))
            anchors.Add(anchor);
        return anchors;
    }

    private static bool IsStrongCurrentConflictAnchor(string anchor)
    {
        if (anchor.StartsWith("highway_mile_marker:", StringComparison.OrdinalIgnoreCase))
            return true;

        if (anchor.StartsWith("address:", StringComparison.OrdinalIgnoreCase))
            return IsTrustedLocation(AnchorValue(anchor), geocoded: true);

        if (anchor.StartsWith("location:", StringComparison.OrdinalIgnoreCase))
            return IsTrustedLocation(AnchorValue(anchor), geocoded: true);

        if (!anchor.StartsWith("intersection:", StringComparison.OrdinalIgnoreCase))
            return false;

        var value = AnchorValue(anchor);
        var parts = value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts.All(IsCleanIntersectionRoad);
    }

    private static bool IsCleanIntersectionRoad(string value)
    {
        if (IsEmptyLocation(value))
            return false;

        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", " ");
        if (WeakLocationPhrasePattern.IsMatch(normalized))
            return false;

        var tokens = LocationTokens(normalized);
        if (tokens.Any(DisallowedLocationTokens.Contains))
            return false;

        var suffixCount = tokens.Count(StreetSuffixTokens.Contains);
        if (suffixCount != 1)
            return false;

        var nameTokens = tokens
            .Where(token => !StreetSuffixTokens.Contains(token) && !token.All(char.IsDigit))
            .ToList();
        if (nameTokens.Count is < 1 or > 2)
            return false;

        return nameTokens.All(token => token.Length >= 3);
    }

    private static HashSet<string> TextLocationConflictAnchors(string text)
    {
        var anchors = IncidentCandidateValidator.ExtractConcreteAnchors(text)
            .Where(IsLocationConflictAnchor)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AddLocationConflictPatternAnchors(text, NumberedAddressPattern, anchors);
        AddLocationConflictPatternAnchors(text, NamedRoadPattern, anchors);
        AddLocationConflictPatternAnchors(text, HighwayPattern, anchors);
        return anchors;
    }

    private static void AddLocationConflictPatternAnchors(string text, Regex pattern, HashSet<string> anchors)
    {
        foreach (Match match in pattern.Matches(text ?? string.Empty))
        {
            var location = NormalizeLocationConflictMatch(match.Value);
            if (IsTrustedLocation(location, geocoded: true))
                anchors.Add($"location:{location}");
        }
    }

    private static string NormalizeLocationConflictMatch(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        foreach (var marker in new[] { " at ", " near ", " on ", " to ", " by ", " around " })
        {
            var index = normalized.LastIndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && index + marker.Length < normalized.Length)
            {
                var suffix = normalized[(index + marker.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(suffix))
                    return suffix;
            }
        }

        return normalized;
    }

    private static bool IsLocationConflictAnchor(string anchor) =>
        anchor.StartsWith("address:", StringComparison.OrdinalIgnoreCase) ||
        anchor.StartsWith("intersection:", StringComparison.OrdinalIgnoreCase) ||
        anchor.StartsWith("highway_mile_marker:", StringComparison.OrdinalIgnoreCase) ||
        anchor.StartsWith("location:", StringComparison.OrdinalIgnoreCase);

    private static bool LocationAnchorSetsCompatible(HashSet<string> left, HashSet<string> right) =>
        IncidentCandidateValidator.SharedConcreteAnchorCount(left, right) > 0 ||
        HighwayKeys(left).Overlaps(HighwayKeys(right)) ||
        LocationPhrasesCompatible(left, right);

    private static bool LocationPhrasesCompatible(IEnumerable<string> left, IEnumerable<string> right)
    {
        var leftPhrases = CanonicalLocationPhrases(left);
        var rightPhrases = CanonicalLocationPhrases(right);
        return leftPhrases.Any(leftPhrase =>
            rightPhrases.Any(rightPhrase =>
                string.Equals(leftPhrase, rightPhrase, StringComparison.OrdinalIgnoreCase) ||
                IsSpecificLocationPhraseMatch(leftPhrase, rightPhrase)));
    }

    private static HashSet<string> CanonicalLocationPhrases(IEnumerable<string> anchors) =>
        anchors
            .Select(CanonicalLocationPhrase)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string CanonicalLocationPhrase(string anchor)
    {
        var value = anchor ?? string.Empty;
        var separatorIndex = value.IndexOf(':');
        if (separatorIndex >= 0 && separatorIndex + 1 < value.Length)
            value = value[(separatorIndex + 1)..];

        var normalized = Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\b(rd|st|dr|ave|ln|hwy|pl|cir|ct|pkwy|blvd|ter)\b", match => match.Value switch
        {
            "rd" => "road",
            "st" => "street",
            "dr" => "drive",
            "ave" => "avenue",
            "ln" => "lane",
            "hwy" => "highway",
            "pl" => "place",
            "cir" => "circle",
            "ct" => "court",
            "pkwy" => "parkway",
            "blvd" => "boulevard",
            "ter" => "terrace",
            _ => match.Value
        });
        normalized = Regex.Replace(normalized, @"^(\d{1,6})\s+and\s+", "$1 ");
        normalized = Regex.Replace(normalized, @"\b(and|at|near|on|to|by|around)\b", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static bool IsSpecificLocationPhraseMatch(string left, string right)
    {
        if (left.Length < 10 || right.Length < 10)
            return false;

        return left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
               right.Contains(left, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> HighwayKeys(IEnumerable<string> anchors) =>
        anchors
            .Select(HighwayKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? HighwayKey(string anchor)
    {
        var value = AnchorValue(anchor);
        var match = HighwayPattern.Match(value);
        if (!match.Success)
            return null;

        var key = match.Value.ToLowerInvariant();
        key = Regex.Replace(key, @"\binterstate\b", "i", RegexOptions.CultureInvariant);
        key = Regex.Replace(key, @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        return NormalizeKey(key);
    }

    private static string AnchorValue(string anchor)
    {
        var value = anchor;
        var index = value.IndexOf(':');
        if (index >= 0 && index + 1 < value.Length)
            value = value[(index + 1)..];
        var separator = value.IndexOf('|');
        if (separator >= 0)
            value = value[..separator];
        return value;
    }

    private static IReadOnlyList<IncidentFrameV3> ApplyAmbiguousLifecycle(IReadOnlyList<IncidentFrameV3> frames)
    {
        var ambiguousCallIds = frames
            .SelectMany(frame => frame.CandidateCallIds.Select(id => new { id, frame.FrameId }))
            .GroupBy(item => item.id)
            .Where(group => group.Select(item => item.FrameId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        if (ambiguousCallIds.Count == 0)
            return frames;

        return frames
            .Select(frame =>
            {
                if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId) ||
                    string.Equals(frame.Lifecycle, "conflicted", StringComparison.OrdinalIgnoreCase))
                {
                    return frame;
                }

                var frameAmbiguousIds = frame.CandidateCallIds
                    .Where(ambiguousCallIds.Contains)
                    .Order()
                    .ToList();
                if (frameAmbiguousIds.Count == 0)
                    return frame;

                return frame with
                {
                    Lifecycle = "ambiguous",
                    Reason = $"{frame.Reason}; lifecycle=ambiguous; ambiguousCallIds={string.Join(",", frameAmbiguousIds)}"
                };
            })
            .ToList();
    }

    private static string ClassifyLifecycle(
        FrameRow seed,
        IReadOnlyList<FrameRow> members,
        IReadOnlyList<FrameRow> pool,
        CurrentMatch match,
        string location,
        IReadOnlyList<string> anchors)
    {
        if (HasCurrentMatchLocationConflict(match, location, anchors))
            return "conflicted";
        if (!string.IsNullOrWhiteSpace(match.IncidentId))
            return "mature";
        if (members.Count > 1 && (members.Any(row => row.StrongIncidentSignal) || !IsEmptyLocation(location) || anchors.Count > 0))
            return "mature";
        if (seed.StrongIncidentSignal && IsSpecificIncidentLabel(EventLabel(seed)))
            return "mature";
        if (IsStaleFrame(members, pool, location))
            return "stale";
        return "pending";
    }

    private static bool HasCurrentMatchLocationConflict(CurrentMatch match, string location, IReadOnlyList<string> anchors)
    {
        if (string.IsNullOrWhiteSpace(match.IncidentId))
            return false;
        var currentAnchors = TextLocationConflictAnchors(match.Title);
        if (currentAnchors.Count == 0)
            return false;

        var frameAnchors = StrongCurrentConflictAnchors(location, anchors.Any(anchor =>
            anchor.StartsWith("location:", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(AnchorValue(anchor), location, StringComparison.OrdinalIgnoreCase)), anchors);
        if (frameAnchors.Count == 0)
            return false;

        return !LocationAnchorSetsCompatible(frameAnchors, currentAnchors);
    }

    private static bool IsSpecificIncidentLabel(string label) =>
        !GenericEventLabels.Contains(label) &&
        !string.Equals(label, "Traffic incident", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(label, "Public safety incident", StringComparison.OrdinalIgnoreCase);

    private static bool IsGenericTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return true;

        var normalized = Regex.Replace(title.Trim(), @"\s+", " ");
        if (GenericEventLabels.Any(label => string.Equals(normalized, label, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (Regex.IsMatch(normalized, @"^(?:fire|medical|ems|police) response (?:at|near|on|to) .+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;

        return string.Equals(normalized, "Traffic incident", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Medical incident", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Police incident", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Fire incident", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Public safety incident", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Assistance call", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Unit response", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "Unknown problem", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStaleFrame(IReadOnlyList<FrameRow> members, IReadOnlyList<FrameRow> pool, string location)
    {
        if (members.Count != 1 || !IsEmptyLocation(location) || members.Any(row => row.StrongIncidentSignal))
            return false;
        var latestPoolStart = pool.Max(row => row.Call.StartTime);
        var latestMemberStart = members.Max(row => row.Call.StartTime);
        return latestPoolStart - latestMemberStart > StrongCategoryWindowSeconds;
    }

    private static int FrameTitleQuality(IncidentFrameV3 frame)
    {
        var score = 0;
        if (!GenericEventLabels.Contains(frame.Title))
            score += 2;
        if (!IsEmptyLocation(frame.LocationLabel))
            score += 1;
        if (frame.CandidateCallIds.Count > 1)
            score += 1;
        return score;
    }

    private static bool IsFrameMember(FrameRow seed, FrameRow row)
    {
        if (seed.Call.Id == row.Call.Id)
            return true;
        var seconds = Math.Abs(row.Call.StartTime - seed.Call.StartTime);
        if (seconds > FrameWindowSeconds)
            return false;
        var seedLocation = TrustedLocationOrEmpty(seed);
        var rowLocation = TrustedLocationOrEmpty(row);
        if (!IsEmptyLocation(seedLocation) &&
            !IsEmptyLocation(rowLocation) &&
            !string.Equals(seedLocation, rowLocation, StringComparison.OrdinalIgnoreCase))
            return false;
        if (seed.LocationIsHighConfidenceGeocode &&
            row.LocationIsHighConfidenceGeocode &&
            IsTrustedLocation(seed.LocationLabel, geocoded: true) &&
            string.Equals(seed.LocationLabel, row.LocationLabel, StringComparison.OrdinalIgnoreCase))
            return true;
        if (seed.LocationIsHighConfidenceGeocode &&
            IsTrustedLocation(seed.LocationLabel, geocoded: true) &&
            MentionsLocation(row.Call.Transcription, seed.LocationLabel))
            return true;
        if (row.LocationIsHighConfidenceGeocode &&
            IsTrustedLocation(row.LocationLabel, geocoded: true) &&
            MentionsLocation(seed.Call.Transcription, row.LocationLabel))
            return true;
        var seedConflictAnchors = StrongCurrentConflictAnchors(TrustedLocationOrEmpty(seed), seed.LocationIsHighConfidenceGeocode, seed.Anchors);
        var rowConflictAnchors = StrongCurrentConflictAnchors(TrustedLocationOrEmpty(row), row.LocationIsHighConfidenceGeocode, row.Anchors);
        if (seedConflictAnchors.Count > 0 &&
            rowConflictAnchors.Count > 0 &&
            !LocationAnchorSetsCompatible(seedConflictAnchors, rowConflictAnchors))
        {
            return false;
        }
        if (((seed.LocationIsHighConfidenceGeocode && IsTrustedLocation(seed.LocationLabel, geocoded: true)) ||
             (row.LocationIsHighConfidenceGeocode && IsTrustedLocation(row.LocationLabel, geocoded: true))) &&
            seconds <= StrongCategoryWindowSeconds &&
            SameEventFamily(seed, row) &&
            (seed.StrongIncidentSignal || row.StrongIncidentSignal || !GenericEventLabels.Contains(EventLabel(seed)) || !GenericEventLabels.Contains(EventLabel(row))))
            return true;
        if (IncidentCandidateValidator.SharedConcreteAnchorCount(seed.Anchors, row.Anchors) > 0)
            return true;
        return seconds <= StrongCategoryWindowSeconds
               && seed.StrongIncidentSignal
               && row.StrongIncidentSignal
               && string.Equals(NormalizeCategory(seed.Call.Category), NormalizeCategory(row.Call.Category), StringComparison.OrdinalIgnoreCase)
               && string.Equals(EventLabel(seed), EventLabel(row), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameEventFamily(FrameRow left, FrameRow right)
    {
        var leftLabel = EventLabel(left);
        var rightLabel = EventLabel(right);
        if (string.Equals(leftLabel, rightLabel, StringComparison.OrdinalIgnoreCase))
            return true;
        if ((leftLabel is "Chest pain" or "Difficulty breathing" or "Unconscious person" or "Overdose" or "Seizure" or "Fall" or "Medical response") &&
            (rightLabel is "Chest pain" or "Difficulty breathing" or "Unconscious person" or "Overdose" or "Seizure" or "Fall" or "Medical response"))
            return true;
        if ((leftLabel is "Bolo" or "Welfare check" or "Suspicious vehicle" or "Police response") &&
            (rightLabel is "Bolo" or "Welfare check" or "Suspicious vehicle" or "Police response"))
            return true;
        return false;
    }

    private static CurrentMatch MatchCurrentIncident(
        FrameRow seed,
        IReadOnlyList<FrameRow> members,
        IReadOnlyList<string> frameAnchors,
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<IncidentFrameCurrentIncidentV3> currentIncidents)
    {
        var memberIds = members.Select(row => row.Call.Id).ToHashSet();
        foreach (var current in currentIncidents)
        {
            var currentCallIds = ParseCallIds(current.CallIds.Concat(current.RawCallIds));
            if (currentCallIds.Overlaps(memberIds))
            {
                if (!IsActiveIncidentTarget(current.IncidentId) &&
                    TryMatchActiveIncidentByCallOverlap(activeIncidents, memberIds.Concat(currentCallIds).ToHashSet(), out var activeMatch))
                {
                    return activeMatch;
                }
                if (!IsActiveIncidentTarget(current.IncidentId) &&
                    TryMatchActiveIncidentByIncidentKey(activeIncidents, current.IncidentId, out activeMatch))
                {
                    return activeMatch;
                }
                if (!IsActiveIncidentTarget(current.IncidentId) &&
                    TryMatchActiveIncidentByAnchorOverlap(activeIncidents, frameAnchors, current.Title, out activeMatch))
                {
                    return activeMatch;
                }

                return new CurrentMatch(current.IncidentId, current.Title, current.Status, current.Category, currentCallIds, "call overlap");
            }
        }

        foreach (var active in activeIncidents)
        {
            var activeCallIds = active.Calls.Select(call => call.CallId).ToHashSet();
            if (activeCallIds.Overlaps(memberIds))
                return new CurrentMatch($"active:{active.Id}", active.Title, active.Status, active.Category, activeCallIds, "active call overlap");

            var activeAnchors = IncidentCandidateValidator.ExtractConcreteAnchors($"{active.Title} {active.Detail}");
            if (IncidentCandidateValidator.SharedConcreteAnchorCount(frameAnchors.ToHashSet(StringComparer.OrdinalIgnoreCase), activeAnchors) > 0)
                return new CurrentMatch($"active:{active.Id}", active.Title, active.Status, active.Category, activeCallIds, "active anchor overlap");
        }

        foreach (var current in currentIncidents)
        {
            if (!string.Equals(NormalizeCategory(seed.Call.Category), NormalizeCategory(current.Category), StringComparison.OrdinalIgnoreCase))
                continue;
            var currentAnchors = IncidentCandidateValidator.ExtractConcreteAnchors(current.Title);
            if (IncidentCandidateValidator.SharedConcreteAnchorCount(frameAnchors.ToHashSet(StringComparer.OrdinalIgnoreCase), currentAnchors) > 0)
                return new CurrentMatch(current.IncidentId, current.Title, current.Status, current.Category, ParseCallIds(current.CallIds.Concat(current.RawCallIds)), "title anchor overlap");
        }

        return new CurrentMatch(string.Empty, string.Empty, string.Empty, string.Empty, new HashSet<long>(), "no current match");
    }

    private static bool TryMatchActiveIncidentByCallOverlap(
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlySet<long> memberIds,
        out CurrentMatch match)
    {
        foreach (var active in activeIncidents)
        {
            var activeCallIds = active.Calls.Select(call => call.CallId).ToHashSet();
            if (!activeCallIds.Overlaps(memberIds))
                continue;

            match = new CurrentMatch($"active:{active.Id}", active.Title, active.Status, active.Category, activeCallIds, "active call overlap");
            return true;
        }

        match = new CurrentMatch(string.Empty, string.Empty, string.Empty, string.Empty, new HashSet<long>(), "no active call overlap");
        return false;
    }

    private static bool TryMatchActiveIncidentByIncidentKey(
        IReadOnlyList<IncidentDto> activeIncidents,
        string currentIncidentId,
        out CurrentMatch match)
    {
        if (!string.IsNullOrWhiteSpace(currentIncidentId) &&
            !IsPlaceholderCurrentIncidentId(currentIncidentId))
        {
            foreach (var active in activeIncidents)
            {
                if (!string.Equals(active.IncidentKey, currentIncidentId, StringComparison.OrdinalIgnoreCase))
                    continue;

                match = new CurrentMatch(
                    $"active:{active.Id}",
                    active.Title,
                    active.Status,
                    active.Category,
                    active.Calls.Select(call => call.CallId).ToHashSet(),
                    "active incident key match");
                return true;
            }
        }

        match = new CurrentMatch(string.Empty, string.Empty, string.Empty, string.Empty, new HashSet<long>(), "no active incident key match");
        return false;
    }

    private static bool TryMatchActiveIncidentByAnchorOverlap(
        IReadOnlyList<IncidentDto> activeIncidents,
        IReadOnlyList<string> frameAnchors,
        string currentTitle,
        out CurrentMatch match)
    {
        var candidateAnchors = frameAnchors
            .Concat(IncidentCandidateValidator.ExtractConcreteAnchors(currentTitle))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidateAnchors.Count > 0)
        {
            foreach (var active in activeIncidents)
            {
                var activeAnchors = IncidentCandidateValidator.ExtractConcreteAnchors($"{active.Title} {active.Detail}");
                if (IncidentCandidateValidator.SharedConcreteAnchorCount(candidateAnchors, activeAnchors) == 0)
                    continue;

                match = new CurrentMatch(
                    $"active:{active.Id}",
                    active.Title,
                    active.Status,
                    active.Category,
                    active.Calls.Select(call => call.CallId).ToHashSet(),
                    "active anchor overlap");
                return true;
            }
        }

        match = new CurrentMatch(string.Empty, string.Empty, string.Empty, string.Empty, new HashSet<long>(), "no active anchor overlap");
        return false;
    }

    private static HashSet<long> ParseCallIds(IEnumerable<string> values)
    {
        var result = new HashSet<long>();
        foreach (var value in values)
        {
            if (TryParseCallId(value, out var id))
                result.Add(id);
        }
        return result;
    }

    private static bool TryParseCallId(string? value, out long id)
    {
        id = 0;
        var text = (value ?? string.Empty).Trim();
        if (long.TryParse(text, out id))
            return true;

        if (text.StartsWith('C') && text.Length > 1)
            text = text[1..];

        if (!Regex.IsMatch(text, @"\A[0-9a-fA-F]{6,}\z", RegexOptions.CultureInvariant))
            return false;

        return long.TryParse(
            text,
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture,
            out id);
    }

    private static string BuildReason(FrameRow seed, IReadOnlyList<FrameRow> members, CurrentMatch match, string location)
    {
        var traits = new List<string>
        {
            $"seed={seed.Call.Id}",
            $"event={EventLabel(seed)}",
            $"members={members.Count}",
            $"match={match.Reason}"
        };
        if (!IsEmptyLocation(location))
            traits.Add($"location={location}");
        if (seed.StrongIncidentSignal)
            traits.Add("strong_signal=true");
        return string.Join("; ", traits);
    }

    private static string BuildTitle(FrameRow seed, string location, IReadOnlyList<string> anchors)
    {
        var label = EventLabel(seed);
        if (!IsEmptyLocation(location))
            return $"{label} at {TitleCase(location)}";
        if (string.Equals(label, "Broken down vehicle", StringComparison.OrdinalIgnoreCase))
        {
            var roadway = BrokenDownVehicleRoadwayTitle(seed, anchors);
            if (!string.IsNullOrWhiteSpace(roadway))
            {
                var title = $"{label} on {roadway}";
                if (!title.Contains("county line", StringComparison.OrdinalIgnoreCase) &&
                    Regex.IsMatch(seed.Call.Transcription ?? string.Empty, @"\bhamilton county line\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    title = $"{title} near Hamilton County line";
                return title;
            }
        }
        var anchor = anchors.FirstOrDefault(IsDisplayableTitleAnchor);
        var displayAnchor = string.IsNullOrWhiteSpace(anchor) ? string.Empty : DisplayAnchor(anchor);
        return string.IsNullOrWhiteSpace(displayAnchor)
            ? label
            : $"{label} near {displayAnchor}";
    }

    private static string EventLabel(FrameRow row)
    {
        var transcript = (row.Call.Transcription ?? string.Empty).ToLowerInvariant();
        if (Regex.IsMatch(transcript, @"\b(?:chest pain)\b", RegexOptions.CultureInvariant))
            return "Chest pain";
        if (Regex.IsMatch(transcript, @"\b(?:difficulty breathing|trouble breathing|shortness of breath|short of breath)\b", RegexOptions.CultureInvariant))
            return "Difficulty breathing";
        if (Regex.IsMatch(transcript, @"\b(?:unconscious|not conscious|not breathing|unresponsive)\b", RegexOptions.CultureInvariant))
            return "Unconscious person";
        if (Regex.IsMatch(transcript, @"\b(?:seizure)\b", RegexOptions.CultureInvariant))
            return "Seizure";
        if (Regex.IsMatch(transcript, @"\b(?:overdose)\b", RegexOptions.CultureInvariant))
            return "Overdose";
        if (Regex.IsMatch(transcript, @"\b(?:fall|fallen)\b", RegexOptions.CultureInvariant))
            return "Fall";
        if (Regex.IsMatch(transcript, @"\b(?:bolo|be on the lookout)\b", RegexOptions.CultureInvariant))
            return "Bolo";
        if (Regex.IsMatch(transcript, @"\b(?:welfare check|well-being check|check welfare)\b", RegexOptions.CultureInvariant))
            return "Welfare check";
        if (Regex.IsMatch(transcript, @"\b(?:suspicious vehicle|suspicious person)\b", RegexOptions.CultureInvariant))
            return "Suspicious vehicle";
        if (Regex.IsMatch(transcript, @"\b(?:mvc|mva|crash|accident|collision|wreck|rollover|vehicle fire)\b", RegexOptions.CultureInvariant))
            return "Traffic crash";
        if (BrokenDownVehicleHazardPattern.IsMatch(transcript))
            return "Broken down vehicle";
        if (Regex.IsMatch(transcript, @"\b(?:tree down|wire down|road blocked|disabled vehicle|hazard)\b", RegexOptions.CultureInvariant))
            return "Road hazard";
        if (Regex.IsMatch(transcript, @"\b(?:fire|smoke|flames|structure|brush fire|alarm)\b", RegexOptions.CultureInvariant))
            return "Fire response";
        if (Regex.IsMatch(transcript, @"\b(?:ems|medic|patient|injur|unconscious|breathing|chest pain|seizure|overdose)\b", RegexOptions.CultureInvariant))
            return "Medical response";
        if (Regex.IsMatch(transcript, @"\b(?:shots|disturbance|domestic|theft|burglary|assault|pursuit|welfare)\b", RegexOptions.CultureInvariant))
            return "Police response";
        return NormalizeCategory(row.Call.Category) switch
        {
            "fire" => "Fire response",
            "ems" => "Medical response",
            "law" or "police" => "Police response",
            "traffic" => "Traffic incident",
            _ => "Public safety incident"
        };
    }

    private static FrameProposal BuildFrameProposal(FrameRow seed, IReadOnlyList<FrameRow> members)
    {
        var location = !IsEmptyLocation(TrustedLocationOrEmpty(seed))
            ? TrustedLocationOrEmpty(seed)
            : SelectFrameLocation(members);
        var anchor = !IsEmptyLocation(location)
            ? $"location:{NormalizeKey(location)}"
            : SelectSharedAnchor(members) ?? $"calls:{string.Join("-", members.Select(row => row.Call.Id).Order())}";
        var key = $"{FrameIdentityFamily(EventLabel(seed))}:{NormalizeKey(anchor)}";
        return new FrameProposal(seed, members, key, location);
    }

    private static string FrameIdentityFamily(string eventLabel) =>
        eventLabel switch
        {
            "Chest pain" or "Difficulty breathing" or "Unconscious person" or "Overdose" or "Seizure" or "Fall" or "Medical response" => "medical",
            "Traffic crash" or "Road hazard" or "Broken down vehicle" => "traffic",
            "Bolo" or "Welfare check" or "Suspicious vehicle" or "Police response" => "police",
            "Fire response" => "fire",
            _ => NormalizeKey(eventLabel)
        };

    private static string RoadwayTitleAnchor(IEnumerable<string> anchors) =>
        anchors
            .Where(IsRoadwayTitleAnchor)
            .Select(AnchorValue)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string BrokenDownVehicleRoadwayTitle(FrameRow seed, IEnumerable<string> anchors)
    {
        var highway = BrokenDownVehicleHighwayTitle(seed.Call.Transcription);
        if (!string.IsNullOrWhiteSpace(highway))
            return highway;

        var roadway = RoadwayTitleAnchor(anchors);
        return string.IsNullOrWhiteSpace(roadway) ? string.Empty : TitleCase(roadway);
    }

    private static string BrokenDownVehicleHighwayTitle(string? transcript)
    {
        var match = BrokenDownHighwayMileMarkerPattern.Match(transcript ?? string.Empty);
        if (!match.Success)
            return string.Empty;

        var marker = match.Groups["marker"].Value;
        var route = match.Groups["route"].Value;
        if (string.IsNullOrWhiteSpace(marker) || string.IsNullOrWhiteSpace(route))
            return string.Empty;

        var direction = NormalizeDirection(match.Groups["direction"].Value);
        var highway = $"I-{route}";
        if (!string.IsNullOrWhiteSpace(direction))
            highway = $"{highway} {direction}";
        return $"{highway} near mile marker {marker}";
    }

    private static string BrokenDownVehicleHighwayAnchor(string? transcript)
    {
        var match = BrokenDownHighwayMileMarkerPattern.Match(transcript ?? string.Empty);
        if (!match.Success)
            return string.Empty;

        var marker = match.Groups["marker"].Value;
        var route = match.Groups["route"].Value;
        if (string.IsNullOrWhiteSpace(marker) || string.IsNullOrWhiteSpace(route))
            return string.Empty;

        var direction = NormalizeDirection(match.Groups["direction"].Value).ToLowerInvariant();
        return string.IsNullOrWhiteSpace(direction)
            ? $"i-{route} mm {marker}"
            : $"i-{route} {direction} mm {marker}";
    }

    private static string NormalizeDirection(string value) =>
        value.ToLowerInvariant() switch
        {
            "westbound" or "wb" => "Westbound",
            "eastbound" or "eb" => "Eastbound",
            "northbound" or "nb" => "Northbound",
            "southbound" or "sb" => "Southbound",
            _ => string.Empty
        };

    private static bool IsRoadwayTitleAnchor(string anchor) =>
        anchor.StartsWith("road_hint:", StringComparison.OrdinalIgnoreCase) ||
        anchor.StartsWith("address:", StringComparison.OrdinalIgnoreCase) ||
        anchor.StartsWith("highway_mile_marker:", StringComparison.OrdinalIgnoreCase) ||
        anchor.StartsWith("intersection:", StringComparison.OrdinalIgnoreCase);

    private static string BuildFrameId(string systemShortName, FrameRow seed, string frameKey) =>
        $"v3:{NormalizeKey(systemShortName)}:{seed.Call.Id}:{NormalizeKey(frameKey)}";

    private static string? SelectSharedAnchor(IReadOnlyList<FrameRow> members)
    {
        if (members.Count <= 1)
            return null;
        return members
            .SelectMany(row => row.Anchors.Where(anchor => !anchor.StartsWith("location:", StringComparison.OrdinalIgnoreCase)))
            .GroupBy(anchor => anchor, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
    }

    private static string SelectFrameLocation(IReadOnlyList<FrameRow> members)
    {
        var grouped = members
            .Where(row => row.LocationIsHighConfidenceGeocode && IsTrustedLocation(row.LocationLabel, geocoded: true))
            .GroupBy(row => row.LocationLabel, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Max(row => row.GeoScore))
            .ThenByDescending(group => group.Max(row => row.Score))
            .FirstOrDefault();
        return grouped?.Key ?? string.Empty;
    }

    private static FrameRow? SelectLocationEvidence(IEnumerable<FrameRow> rows, string location) =>
        rows
            .Where(row => row.LocationIsHighConfidenceGeocode)
            .Where(row => string.Equals(row.LocationLabel, location, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.LocationGeocodeConfidence)
            .FirstOrDefault();

    private static IncidentFrameV3? SelectLocationEvidence(IEnumerable<IncidentFrameV3> frames, string location) =>
        frames
            .Where(frame => frame.LocationIsHighConfidenceGeocode)
            .Where(frame => string.Equals(frame.LocationLabel, location, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(frame => frame.LocationGeocodeConfidence)
            .FirstOrDefault();

    private static string TrustedLocationOrEmpty(FrameRow row) =>
        row.LocationIsHighConfidenceGeocode && IsTrustedLocation(row.LocationLabel, geocoded: true) ? row.LocationLabel : string.Empty;

    private static string DisplayAnchor(string anchor)
    {
        var value = anchor;
        var index = value.IndexOf(':');
        if (index >= 0 && index + 1 < value.Length)
            value = value[(index + 1)..];
        value = value.Replace("|", " and ", StringComparison.Ordinal);
        return TitleCase(value);
    }

    private static bool IsDisplayableTitleAnchor(string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor))
            return false;

        if (anchor.StartsWith("location:", StringComparison.OrdinalIgnoreCase))
            return IsTrustedLocation(AnchorValue(anchor), geocoded: true);

        if (anchor.StartsWith("address:", StringComparison.OrdinalIgnoreCase))
            return IsDisplayableAddress(AnchorValue(anchor));

        if (anchor.StartsWith("intersection:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = AnchorValue(anchor).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 2 && parts.All(IsCleanIntersectionRoad);
        }

        if (anchor.StartsWith("road_hint:", StringComparison.OrdinalIgnoreCase))
            return IsCleanRoadwayLocationHint(AnchorValue(anchor));

        return false;
    }

    private static bool IsDisplayableAddress(string value)
    {
        if (IsEmptyLocation(value))
            return false;
        var normalized = NormalizeTrustedLocationText(value);
        if (WeakLocationPhrasePattern.IsMatch(normalized))
            return false;
        if (LocationTokens(normalized).Any(DisallowedLocationTokens.Contains))
            return false;
        if (HasSuspiciousAddressConjunction(normalized))
            return false;
        return NumberedAddressPattern.IsMatch(normalized) && HasStreetNameToken(normalized);
    }

    private static string TitleCase(string value)
    {
        value = Regex.Replace((value ?? string.Empty).Trim(), @"[_\-]+", " ");
        value = Regex.Replace(value, @"\s+", " ");
        if (value.Length == 0)
            return string.Empty;
        return string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    }

    private static bool IsEmptyLocation(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedLocation(string? value, bool geocoded)
    {
        if (IsEmptyLocation(value))
            return false;
        var normalized = NormalizeTrustedLocationText(value!);
        if (WeakLocationPhrasePattern.IsMatch(normalized))
            return false;
        if (LocationTokens(normalized).Any(DisallowedLocationTokens.Contains))
            return false;
        if (HasSuspiciousAddressConjunction(normalized))
            return false;
        if (NumberedAddressPattern.IsMatch(normalized))
            return geocoded && HasStreetNameToken(normalized);
        if (HighwayPattern.IsMatch(normalized))
            return geocoded;
        return geocoded && NamedRoadPattern.IsMatch(normalized) && HasStreetNameToken(normalized);
    }

    private static string NormalizeTrustedLocationText(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        normalized = Regex.Replace(normalized, @"^(\d{1,6})\s+and\s+", "$1 ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static bool HasSuspiciousAddressConjunction(string normalized)
    {
        if (!NumberedAddressPattern.IsMatch(normalized))
            return false;

        return LocationTokens(normalized).Contains("and");
    }

    private static bool HasStreetNameToken(string value)
    {
        var tokens = LocationTokens(value).ToList();
        var firstStreetToken = tokens
            .SkipWhile(token => token.All(char.IsDigit))
            .FirstOrDefault(token => !StreetSuffixTokens.Contains(token));
        return firstStreetToken is not null;
    }

    private static bool MentionsLocation(string? transcript, string? location)
    {
        if (IsEmptyLocation(transcript) || IsEmptyLocation(location))
            return false;
        var transcriptTokens = LocationTokens(transcript!);
        var locationTokens = LocationTokens(location!);
        if (locationTokens.Count == 0)
            return false;
        if (HighwayPattern.IsMatch(location!))
            return locationTokens.All(transcriptTokens.Contains);

        var streetTokens = locationTokens
            .Where(token => !token.All(char.IsDigit))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (streetTokens.Count > 0 && streetTokens.All(transcriptTokens.Contains))
            return true;

        return locationTokens.All(transcriptTokens.Contains);
    }

    private static HashSet<string> LocationTokens(string value) =>
        Regex.Matches((value ?? string.Empty).ToLowerInvariant(), @"[a-z0-9]+")
            .Select(match => match.Value)
            .Where(token => token.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ShouldEmitFrame(IncidentFrameV3 frame)
    {
        if (!IsEmptyLocation(frame.LocationLabel))
            return true;
        if (!string.IsNullOrWhiteSpace(frame.MatchedCurrentIncidentId))
            return true;
        var genericTitle = GenericEventLabels.Any(label =>
            string.Equals(frame.Title, label, StringComparison.OrdinalIgnoreCase) ||
            frame.Title.StartsWith($"{label} near ", StringComparison.OrdinalIgnoreCase));
        if (genericTitle)
            return false;
        return frame.CandidateCallIds.Count > 0;
    }

    private static string NormalizeCategory(string? value)
    {
        var category = (value ?? string.Empty).Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(category) ? "other" : category;
    }

    private static string NormalizeKey(string? value) =>
        Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');

    private sealed record CurrentMatch(string IncidentId, string Title, string Status, string Category, IReadOnlySet<long> CallIds, string Reason);

    private sealed record FrameProposal(FrameRow Seed, IReadOnlyList<FrameRow> Members, string CanonicalKey, string CanonicalLocation);

    private sealed record FrameRow(
        EngineCall Call,
        double Score,
        double GeoScore,
        string Reason,
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
        HashSet<string> Anchors,
        bool StrongIncidentSignal)
    {
        public static FrameRow FromCandidate(IncidentRagCandidate candidate)
        {
            var callText = $"{candidate.Call.Category} {candidate.Call.TalkgroupName} {candidate.Call.Transcription}";
            var anchors = IncidentCandidateValidator.ExtractConcreteAnchors(callText);
            var highwayAnchor = BrokenDownVehicleHighwayAnchor(candidate.Call.Transcription);
            if (!string.IsNullOrWhiteSpace(highwayAnchor))
                anchors.Add($"highway_mile_marker:{highwayAnchor}");
            if (candidate.LocationIsHighConfidenceGeocode &&
                IsTrustedLocation(candidate.LocationLabel, geocoded: true))
                anchors.Add($"location:{candidate.LocationLabel}");
            else if (IsCleanRoadwayLocationHint(candidate.LocationLabel))
                anchors.Add($"road_hint:{NormalizeTrustedLocationText(candidate.LocationLabel)}");
            return new FrameRow(
                candidate.Call,
                candidate.Score,
                candidate.GeoScore,
                candidate.Reason,
                candidate.LocationLabel,
                candidate.LocationIsHighConfidenceGeocode,
                candidate.LocationGeocodeConfidence,
                candidate.LocationGeocodeProvider,
                candidate.LocationGeocodePrecision,
                candidate.LocationSource,
                candidate.LocationGeocodeQuery,
                candidate.LocationGeocodeDisplayName,
                candidate.LocationLatitude,
                candidate.LocationLongitude,
                anchors,
                IncidentCandidateValidator.HasStrongIncidentSignal(callText));
        }
    }

    private static bool IsCleanRoadwayLocationHint(string? value)
    {
        if (IsEmptyLocation(value))
            return false;
        var normalized = NormalizeTrustedLocationText(value!);
        if (WeakLocationPhrasePattern.IsMatch(normalized))
            return false;
        if (NonRoadwayHintPhrasePattern.IsMatch(normalized))
            return false;
        if (LocationTokens(normalized).Any(DisallowedLocationTokens.Contains))
            return false;
        return (NamedRoadPattern.IsMatch(normalized) || HighwayPattern.IsMatch(normalized)) && HasStreetNameToken(normalized);
    }
}
