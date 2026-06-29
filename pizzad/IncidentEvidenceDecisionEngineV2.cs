namespace pizzad;

public sealed record IncidentEvidenceCallContextV2(
    long CallId,
    string Transcript,
    string Category = "",
    string TalkgroupName = "",
    string SystemShortName = "");

public static class IncidentEvidenceDecisionEngineV2
{
    private static readonly HashSet<string> AcceptedDecisions = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept",
        "accepted",
        "retain",
        "retained",
        "supporting"
    };

    private static readonly HashSet<string> NonEventRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "routine",
        "routine_status",
        "unrelated",
        "conflicting",
        "conflict"
    };

    public static PersistenceDecisionV2 Decide(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyDictionary<long, string> transcriptsByCallId,
        string incidentKey = "shadow") =>
        DecideSingle(
            hypothesis,
            transcriptsByCallId.ToDictionary(
                row => row.Key,
                row => new IncidentEvidenceCallContextV2(row.Key, row.Value)),
            incidentKey);

    public static PersistenceDecisionV2 Decide(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyDictionary<long, IncidentEvidenceCallContextV2> callContextsByCallId,
        string incidentKey = "shadow") =>
        DecideSingle(hypothesis, callContextsByCallId, incidentKey);

    public static IReadOnlyList<PersistenceDecisionV2> DecideMany(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyDictionary<long, IncidentEvidenceCallContextV2> callContextsByCallId,
        string incidentKey = "shadow")
    {
        var splitHypotheses = SplitDisconnectedSourceBackedPrimaryEvents(hypothesis, callContextsByCallId, incidentKey);
        if (splitHypotheses.Count <= 1)
            return [DecideSingle(hypothesis, callContextsByCallId, incidentKey)];

        var decisions = splitHypotheses
            .Select(item => DecideSingle(item.Hypothesis, callContextsByCallId, item.IncidentKey))
            .Where(IsActionableShadowDecision)
            .ToList();

        return decisions.Count > 0
            ? DeduplicateEquivalentDecisions(decisions)
            : [DecideSingle(hypothesis, callContextsByCallId, incidentKey)];
    }

    private static PersistenceDecisionV2 DecideSingle(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyDictionary<long, IncidentEvidenceCallContextV2> callContextsByCallId,
        string incidentKey)
    {
        var transcriptsByCallId = callContextsByCallId.ToDictionary(row => row.Key, row => row.Value.Transcript ?? string.Empty);
        var reasons = new List<string>();
        var spanVerification = IncidentEvidenceClaimVerifier.VerifySpans(hypothesis, transcriptsByCallId);
        if (!spanVerification.Accepted)
            reasons.Add($"ignored unsupported optional spans: {spanVerification.Errors.Count}");

        var groundedEventSourceCallIds = hypothesis.Events
            .Where(evidence => IsPrimaryStrength(evidence.Strength))
            .Where(evidence => evidence.Spans.Count > 0)
            .Where(evidence => IncidentEvidenceClaimVerifier.AnySpanGrounded(evidence.Spans, transcriptsByCallId))
            .SelectMany(evidence => evidence.SourceCallIds)
            .ToHashSet();
        var groundedNonConflictSourceCallIds = AllNonConflictHypothesisSpans(hypothesis)
            .Where(span => IncidentEvidenceClaimVerifier.IsSpanGrounded(span, transcriptsByCallId))
            .Select(span => span.CallId)
            .ToHashSet();
        var groundedNarrativeSourceCallIds = hypothesis.Narrative.Facts
            .SelectMany(fact => fact.Spans)
            .Where(span => IncidentEvidenceClaimVerifier.IsSpanGrounded(span, transcriptsByCallId))
            .Select(span => span.CallId)
            .ToHashSet();

        var acceptedCallIds = hypothesis.Membership
            .Where(row => IsAcceptedMembership(row, groundedNarrativeSourceCallIds, groundedNonConflictSourceCallIds))
            .Where(row => (row.Spans.Count == 0 && (IsPrimaryEventRole(row.Role)
                                                     || groundedEventSourceCallIds.Contains(row.CallId)
                                                     || IsContinuationWithCompatibleTranscriptEvent(row, hypothesis, transcriptsByCallId)))
                          || IncidentEvidenceClaimVerifier.AnySpanGrounded(row.Spans, transcriptsByCallId)
                          || groundedEventSourceCallIds.Contains(row.CallId))
            .Select(row => row.CallId)
            .Distinct()
            .Order()
            .ToList();
        acceptedCallIds = RetainServerRecognizedInitialDispatchCalls(hypothesis, acceptedCallIds, transcriptsByCallId, reasons);
        acceptedCallIds = RetainServerRecognizedContinuationCalls(hypothesis, acceptedCallIds, transcriptsByCallId, reasons);
        if (acceptedCallIds.Count == 0)
            acceptedCallIds = RetainServerRecognizedPrimaryEventCalls(hypothesis, callContextsByCallId, reasons);
        if (acceptedCallIds.Count == 0)
        {
            reasons.Add("no accepted event-member calls");
            return Reject(hypothesis, incidentKey, reasons, hypothesis.Conflicts);
        }

        var acceptedSet = acceptedCallIds.ToHashSet();
        var blockingConflicts = hypothesis.Conflicts
            .Where(IsServerRecognizedBlockingConflict)
            .Where(conflict => conflict.CallIds.Distinct().Count() >= 2)
            .Where(conflict => conflict.Spans.Count == 0 || IncidentEvidenceClaimVerifier.AnySpanGrounded(conflict.Spans, transcriptsByCallId))
            .ToList();
        var derivedLocationConflicts = DeriveLocationConflicts(hypothesis, acceptedSet, transcriptsByCallId);
        blockingConflicts.AddRange(derivedLocationConflicts);
        if (derivedLocationConflicts.Count == 0)
            blockingConflicts.AddRange(DeriveTranscriptLocationConflicts(acceptedSet, transcriptsByCallId));
        blockingConflicts.AddRange(DeriveParentEventConflicts(hypothesis, acceptedSet, transcriptsByCallId));
        blockingConflicts.AddRange(DeriveTranscriptParentEventConflicts(acceptedSet, transcriptsByCallId));
        blockingConflicts.AddRange(DeriveVehicleConflicts(hypothesis, acceptedSet, transcriptsByCallId));
        blockingConflicts.AddRange(DerivePersonConflicts(hypothesis, acceptedSet, transcriptsByCallId));
        if (blockingConflicts.Any(conflict => string.Equals(conflict.ConflictType, "parent_event_conflict", StringComparison.OrdinalIgnoreCase)))
        {
            var prunedAcceptedCallIds = RetainDominantParentEventGroup(hypothesis, acceptedSet, transcriptsByCallId, reasons);
            if (prunedAcceptedCallIds.Count > 0 && prunedAcceptedCallIds.Count < acceptedCallIds.Count)
            {
                acceptedCallIds = prunedAcceptedCallIds;
                acceptedSet = acceptedCallIds.ToHashSet();
                blockingConflicts = hypothesis.Conflicts
                    .Where(IsServerRecognizedBlockingConflict)
                    .Where(conflict => conflict.CallIds.Distinct().Count() >= 2)
                    .Where(conflict => conflict.Spans.Count == 0 || IncidentEvidenceClaimVerifier.AnySpanGrounded(conflict.Spans, transcriptsByCallId))
                    .ToList();
                derivedLocationConflicts = DeriveLocationConflicts(hypothesis, acceptedSet, transcriptsByCallId);
                blockingConflicts.AddRange(derivedLocationConflicts);
                if (derivedLocationConflicts.Count == 0)
                    blockingConflicts.AddRange(DeriveTranscriptLocationConflicts(acceptedSet, transcriptsByCallId));
                blockingConflicts.AddRange(DeriveParentEventConflicts(hypothesis, acceptedSet, transcriptsByCallId));
                blockingConflicts.AddRange(DeriveTranscriptParentEventConflicts(acceptedSet, transcriptsByCallId));
                blockingConflicts.AddRange(DeriveVehicleConflicts(hypothesis, acceptedSet, transcriptsByCallId));
                blockingConflicts.AddRange(DerivePersonConflicts(hypothesis, acceptedSet, transcriptsByCallId));
            }
        }
        if (blockingConflicts.Count > 0)
        {
            reasons.Add("hypothesis contains blocking conflicts");
            return Reject(hypothesis, incidentKey, reasons, blockingConflicts);
        }

        acceptedCallIds = RetainConnectedAcceptedPrimaryEventCalls(hypothesis, acceptedCallIds, transcriptsByCallId, reasons);
        acceptedCallIds = DropDisconnectedTerminalMedicalStatusCalls(acceptedCallIds, transcriptsByCallId, reasons);
        acceptedSet = acceptedCallIds.ToHashSet();

        if (IsRoutineStandaloneActivity(acceptedSet, callContextsByCallId))
        {
            reasons.Add("accepted calls contain only routine, transport, administrative, or non-emergency service traffic");
            return Reject(hypothesis, incidentKey, reasons, hypothesis.Conflicts);
        }

        var primaryEvents = hypothesis.Events
            .Where(evidence => evidence.Spans.Count > 0)
            .Where(evidence => IncidentEvidenceClaimVerifier.AnySpanGrounded(evidence.Spans, transcriptsByCallId))
            .Where(evidence => evidence.SourceCallIds.Any(acceptedSet.Contains))
            .Where(evidence => IsPrimaryStrength(evidence.Strength))
            .Where(evidence => !IsRoutineOrAdministrativePrimaryEvent(evidence))
            .ToList();
        var derivedPrimaryEvents = new List<EventEvidenceV2>();
        if (primaryEvents.Count == 0)
        {
            derivedPrimaryEvents.AddRange(DeriveSourceBackedPrimaryEvents(hypothesis, acceptedSet, transcriptsByCallId, reasons));
            primaryEvents.AddRange(derivedPrimaryEvents);
        }
        if (primaryEvents.Count == 0)
        {
            var recoveredAcceptedCallIds = RetainServerRecognizedPrimaryEventCalls(hypothesis, callContextsByCallId, reasons);
            if (recoveredAcceptedCallIds.Count > 0)
            {
                acceptedCallIds = recoveredAcceptedCallIds;
                acceptedSet = acceptedCallIds.ToHashSet();
                derivedPrimaryEvents.AddRange(DeriveSourceBackedPrimaryEvents(hypothesis, acceptedSet, transcriptsByCallId, reasons));
                primaryEvents.AddRange(derivedPrimaryEvents);
            }

            if (primaryEvents.Count == 0)
            {
                reasons.Add("no accepted call has source-backed primary event evidence");
                return Reject(hypothesis, incidentKey, reasons, hypothesis.Conflicts);
            }
        }

        var groundedNarrativeFacts = hypothesis.Narrative.Facts
            .Where(fact => fact.Spans.Count > 0)
            .Where(fact => IncidentEvidenceClaimVerifier.AnySpanGrounded(fact.Spans, transcriptsByCallId))
            .ToList();
        if (groundedNarrativeFacts.Count == 0)
            groundedNarrativeFacts.AddRange(DeriveNarrativeFactsFromPrimaryEvents(primaryEvents, acceptedSet, transcriptsByCallId));
        if (groundedNarrativeFacts.Count == 0)
        {
            reasons.Add("narrative has no source-backed facts");
            return Reject(hypothesis, incidentKey, reasons, hypothesis.Conflicts);
        }

        var readiness = AssessPersistenceReadiness(acceptedCallIds, primaryEvents, callContextsByCallId, transcriptsByCallId);
        if (!readiness.Ready)
        {
            reasons.AddRange(readiness.Reasons);
            var pendingNarrative = SynthesizeNarrative(hypothesis, acceptedSet, primaryEvents, groundedNarrativeFacts, transcriptsByCallId);
            return Pending(
                hypothesis,
                incidentKey,
                acceptedCallIds,
                pendingNarrative.Title,
                pendingNarrative.Detail,
                DeriveCategory(primaryEvents[0].EventClass),
                reasons,
                hypothesis.Conflicts);
        }

        reasons.Add("accepted by v2 shadow guardrails: spans, primary event evidence, conflicts, and narrative facts are grounded");
        var rejectedCallIds = hypothesis.CandidateCallIds
            .Where(id => !acceptedSet.Contains(id))
            .Distinct()
            .Order()
            .ToList();
        var narrative = SynthesizeNarrative(hypothesis, acceptedSet, primaryEvents, groundedNarrativeFacts, transcriptsByCallId);
        return new PersistenceDecisionV2(
            "shadow_accept",
            incidentKey,
            acceptedCallIds,
            rejectedCallIds,
            narrative.Title,
            narrative.Detail,
            DeriveCategory(primaryEvents[0].EventClass),
            reasons,
            []);
    }

    private static bool IsActionableShadowDecision(PersistenceDecisionV2 decision) =>
        string.Equals(decision.Decision, "shadow_accept", StringComparison.OrdinalIgnoreCase)
        || string.Equals(decision.Decision, "shadow_pending", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<PersistenceDecisionV2> DeduplicateEquivalentDecisions(IReadOnlyList<PersistenceDecisionV2> decisions) =>
        decisions
            .GroupBy(DecisionIdentityKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(DecisionTitleQuality).ThenBy(item => item.IncidentKey, StringComparer.OrdinalIgnoreCase).First())
            .ToList();

    private static string DecisionIdentityKey(PersistenceDecisionV2 decision) =>
        string.Join('|',
            decision.Decision,
            string.Join(",", decision.AcceptedCallIds.Distinct().Order()),
            string.Join(",", (decision.PendingCallIds ?? []).Distinct().Order()),
            string.Join(",", decision.RejectedCallIds.Distinct().Order()),
            decision.Category);

    private static int DecisionTitleQuality(PersistenceDecisionV2 decision)
    {
        var title = decision.Title ?? string.Empty;
        var score = title.Length;
        if (title.Contains(" at ", StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (!IsGenericIncidentTitle(title))
            score += 25;
        return score;
    }

    private static PersistenceDecisionV2 Reject(
        IncidentHypothesisV2 hypothesis,
        string incidentKey,
        IReadOnlyList<string> reasons,
        IReadOnlyList<ConflictEvidenceV2> conflicts) =>
        new(
            "shadow_reject",
            incidentKey,
            [],
            hypothesis.CandidateCallIds.Distinct().Order().ToList(),
            string.Empty,
            string.Empty,
            "other",
            reasons,
            conflicts);

    private static PersistenceDecisionV2 Pending(
        IncidentHypothesisV2 hypothesis,
        string incidentKey,
        IReadOnlyList<long> pendingCallIds,
        string title,
        string detail,
        string category,
        IReadOnlyList<string> reasons,
        IReadOnlyList<ConflictEvidenceV2> conflicts)
    {
        var pending = pendingCallIds.Distinct().Order().ToList();
        return new(
            "shadow_pending",
            incidentKey,
            [],
            hypothesis.CandidateCallIds.Where(id => !pending.Contains(id)).Distinct().Order().ToList(),
            title,
            detail,
            category,
            reasons,
            conflicts,
            pending);
    }

    private static IncidentReadiness AssessPersistenceReadiness(
        IReadOnlyList<long> acceptedCallIds,
        IReadOnlyList<EventEvidenceV2> primaryEvents,
        IReadOnlyDictionary<long, IncidentEvidenceCallContextV2> callContextsByCallId,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var profiles = acceptedCallIds
            .Distinct()
            .Order()
            .Select(callId => BuildReadinessProfile(callId, primaryEvents, callContextsByCallId, transcriptsByCallId))
            .ToList();
        if (profiles.Any(profile => profile.ReadyAnchor))
            return new IncidentReadiness(true, []);

        var reasons = new List<string>
        {
            "candidate remains pending because no accepted call is a complete incident anchor yet"
        };
        foreach (var profile in profiles.Where(profile => profile.HasPrimarySignal).OrderBy(profile => profile.CallId))
            reasons.Add($"pending call {profile.CallId}: {profile.PendingReason}");

        return new IncidentReadiness(false, reasons);
    }

    private static CallReadinessProfile BuildReadinessProfile(
        long callId,
        IReadOnlyList<EventEvidenceV2> primaryEvents,
        IReadOnlyDictionary<long, IncidentEvidenceCallContextV2> callContextsByCallId,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var transcript = transcriptsByCallId.TryGetValue(callId, out var transcriptValue) ? transcriptValue : string.Empty;
        var context = callContextsByCallId.TryGetValue(callId, out var contextValue)
            ? contextValue
            : new IncidentEvidenceCallContextV2(callId, transcript);
        var group = TranscriptParentEventGroup(transcript);
        var transcriptSpan = group.Length > 0 ? TryFindPrimarySignalSpan(callId, transcript, group) : null;
        var modelHasGroundedPrimary = primaryEvents
            .Where(evidence => evidence.SourceCallIds.Contains(callId) || evidence.Spans.Any(span => span.CallId == callId))
            .SelectMany(evidence => evidence.Spans)
            .Any(span => span.CallId == callId && IncidentEvidenceClaimVerifier.IsSpanGrounded(span, transcriptsByCallId));
        var hasPrimarySignal = transcriptSpan is not null || modelHasGroundedPrimary;
        if (!hasPrimarySignal)
            return new CallReadinessProfile(callId, false, false, "no source-backed primary event signal");

        var text = $"{context.TalkgroupName} {transcript}";
        var hasConcreteLocation = ExtractTranscriptLocationKeys(transcript).Any();
        var hasDispatchOrRequest = HasIncidentDispatchOrRequestLanguage(text);
        var hasHighInformationEvent = HasHighInformationEventSignal(group, transcript, transcriptSpan?.Text ?? string.Empty);
        var weakSignal = IsLowInformationStandalonePrimarySignal(group, transcript, transcriptSpan?.Text ?? string.Empty);
        var ready = !weakSignal && (hasDispatchOrRequest || hasConcreteLocation || hasHighInformationEvent);
        if (ready)
            return new CallReadinessProfile(callId, true, true, string.Empty);

        var reason = weakSignal
            ? "primary signal is too terse or status-like to create an incident without later corroboration"
            : "primary signal lacks dispatch/request language, concrete location, or high-information emergency detail";
        return new CallReadinessProfile(callId, true, false, reason);
    }

    private static bool HasIncidentDispatchOrRequestLanguage(string text)
    {
        var value = text ?? string.Empty;
        return value.Contains("respond", StringComparison.OrdinalIgnoreCase)
               || value.Contains("copy", StringComparison.OrdinalIgnoreCase)
               || value.Contains("caller reports", StringComparison.OrdinalIgnoreCase)
               || value.Contains("caller advised", StringComparison.OrdinalIgnoreCase)
               || value.Contains("rp is advising", StringComparison.OrdinalIgnoreCase)
               || value.Contains("rp advising", StringComparison.OrdinalIgnoreCase)
               || value.Contains("reports of", StringComparison.OrdinalIgnoreCase)
               || value.Contains("requesting", StringComparison.OrdinalIgnoreCase)
               || value.Contains("we're sending", StringComparison.OrdinalIgnoreCase)
               || value.Contains("sending an", StringComparison.OrdinalIgnoreCase)
               || value.Contains("sending a", StringComparison.OrdinalIgnoreCase)
               || value.Contains("dispatched", StringComparison.OrdinalIgnoreCase)
               || value.Contains("on a 911", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasHighInformationEventSignal(string group, string transcript, string primarySignal)
    {
        var value = transcript ?? string.Empty;
        var signal = primarySignal ?? string.Empty;
        if (string.Equals(group, "medical", StringComparison.OrdinalIgnoreCase))
            return HasMedicalEmergencySignal(value) && !IsBareCode73Signal(value, signal);
        if (string.Equals(group, "traffic", StringComparison.OrdinalIgnoreCase))
            return HasTrafficIncidentSignal(value) || HasRecklessDriverTrafficHazardSignal(value) || HasPedestrianTrafficHazardSignal(value);
        if (string.Equals(group, "fire", StringComparison.OrdinalIgnoreCase))
            return HasFireEventSignal(value);
        if (string.Equals(group, "violent_police", StringComparison.OrdinalIgnoreCase))
            return HasPoliceEmergencySignal(value);
        if (string.Equals(group, "property_police", StringComparison.OrdinalIgnoreCase))
            return HasVehicleFixedObjectCrashSignal(value)
                   || value.Contains("burglary", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("theft", StringComparison.OrdinalIgnoreCase)
                   || value.Contains("stolen", StringComparison.OrdinalIgnoreCase);

        return string.Equals(group, "rescue", StringComparison.OrdinalIgnoreCase)
               || string.Equals(group, "animal", StringComparison.OrdinalIgnoreCase)
               || string.Equals(group, "service_call", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLowInformationStandalonePrimarySignal(string group, string transcript, string primarySignal)
    {
        var value = transcript ?? string.Empty;
        var signal = primarySignal ?? string.Empty;
        if (IsBareCode73Signal(value, signal))
            return true;
        if (string.Equals(group, "violent_police", StringComparison.OrdinalIgnoreCase)
            && string.Equals(signal, "gun", StringComparison.OrdinalIgnoreCase)
            && !HasPoliceEmergencySignal(value))
            return true;

        return false;
    }

    private static bool IsBareCode73Signal(string transcript, string primarySignal)
    {
        if (!primarySignal.Contains("code 73", StringComparison.OrdinalIgnoreCase))
            return false;

        var value = transcript ?? string.Empty;
        return !HasMedicalEmergencySignal(value.Replace("code 73", string.Empty, StringComparison.OrdinalIgnoreCase))
               && !ExtractTranscriptLocationKeys(value).Any()
               && !HasIncidentDispatchOrRequestLanguage(value);
    }

    private static bool HasPoliceEmergencySignal(string text)
    {
        var value = text ?? string.Empty;
        return value.Contains("shots fired", StringComparison.OrdinalIgnoreCase)
               || value.Contains("stabbing", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tabbing", StringComparison.OrdinalIgnoreCase)
               || value.Contains("fight call", StringComparison.OrdinalIgnoreCase)
               || value.Contains("assault", StringComparison.OrdinalIgnoreCase)
               || value.Contains("domestic disorder", StringComparison.OrdinalIgnoreCase)
               || value.Contains("domestic disturbance", StringComparison.OrdinalIgnoreCase)
               || value.Contains("suicid", StringComparison.OrdinalIgnoreCase)
               || value.Contains("kill herself", StringComparison.OrdinalIgnoreCase)
               || value.Contains("kill himself", StringComparison.OrdinalIgnoreCase)
               || value.Contains("possibly armed", StringComparison.OrdinalIgnoreCase)
               || value.Contains("with a gun", StringComparison.OrdinalIgnoreCase)
               || value.Contains("gun in his hand", StringComparison.OrdinalIgnoreCase)
               || value.Contains("gun in her hand", StringComparison.OrdinalIgnoreCase)
               || value.Contains("pointing the gun", StringComparison.OrdinalIgnoreCase)
               || value.Contains("pointed the gun", StringComparison.OrdinalIgnoreCase)
               || HasPoliceBoloSignal(value);
    }

    private static IReadOnlyList<SplitIncidentHypothesis> SplitDisconnectedSourceBackedPrimaryEvents(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyDictionary<long, IncidentEvidenceCallContextV2> callContextsByCallId,
        string incidentKey)
    {
        var transcriptsByCallId = callContextsByCallId.ToDictionary(row => row.Key, row => row.Value.Transcript ?? string.Empty);
        var anchors = hypothesis.CandidateCallIds
            .Distinct()
            .Order()
            .Select(callId => BuildRecognizedPrimaryEvent(callId, transcriptsByCallId.TryGetValue(callId, out var transcript) ? transcript : string.Empty))
            .Where(item => item.Group.Length > 0 && item.Span is not null)
            .Where(item => callContextsByCallId.TryGetValue(item.CallId, out var context) && !IsRoutineOrTransportOnlyContext(context))
            .ToList();
        if (anchors.Count <= 1)
            return [];

        var components = BuildRecognizedPrimaryEventComponents(anchors);
        if (components.Count <= 1)
            return [];

        var anchorIds = anchors.Select(item => item.CallId).ToHashSet();
        var result = new List<SplitIncidentHypothesis>();
        var part = 1;
        foreach (var component in components.OrderBy(component => component.Min(item => item.CallId)))
        {
            var componentAnchorIds = component.Select(item => item.CallId).ToHashSet();
            var scope = componentAnchorIds.ToHashSet();
            foreach (var callId in hypothesis.CandidateCallIds.Distinct().Order())
            {
                if (scope.Contains(callId) || anchorIds.Contains(callId))
                    continue;
                var transcript = transcriptsByCallId.TryGetValue(callId, out var value) ? value : string.Empty;
                if (HasSourceBackedContinuationAnchor(callId, transcript, componentAnchorIds, transcriptsByCallId))
                    scope.Add(callId);
            }

            if (scope.Count == 0)
                continue;

            var scopedHypothesis = ScopeHypothesisToCallSet(hypothesis, scope);
            result.Add(new SplitIncidentHypothesis(scopedHypothesis, $"{incidentKey}:part{part++}"));
        }

        return result.Count > 1 ? result : [];
    }

    private static RecognizedCallEvent BuildRecognizedPrimaryEvent(long callId, string transcript)
    {
        var group = TranscriptParentEventGroup(transcript);
        var span = group.Length > 0 ? TryFindPrimarySignalSpan(callId, transcript, group) : null;
        var locationKeys = ExtractTranscriptLocationKeys(transcript).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new RecognizedCallEvent(callId, group, span, locationKeys, transcript);
    }

    private static List<List<RecognizedCallEvent>> BuildRecognizedPrimaryEventComponents(IReadOnlyList<RecognizedCallEvent> anchors)
    {
        var remaining = anchors.ToDictionary(item => item.CallId);
        var components = new List<List<RecognizedCallEvent>>();
        foreach (var seed in anchors.OrderBy(item => item.CallId))
        {
            if (!remaining.Remove(seed.CallId))
                continue;

            var component = new List<RecognizedCallEvent> { seed };
            var queue = new Queue<RecognizedCallEvent>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var other in remaining.Values.OrderBy(item => item.CallId).ToList())
                {
                    if (!RecognizedPrimaryEventsConnected(current, other))
                        continue;

                    remaining.Remove(other.CallId);
                    component.Add(other);
                    queue.Enqueue(other);
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static bool RecognizedPrimaryEventsConnected(RecognizedCallEvent left, RecognizedCallEvent right)
    {
        if (!ParentEventGroupsCompatible(left.Group, right.Group))
            return false;

        if (left.LocationKeys.Count > 0 && right.LocationKeys.Count > 0)
            return left.LocationKeys.Any(leftKey => right.LocationKeys.Any(rightKey => LocationKeysCompatible(leftKey, rightKey)));

        var leftTokens = IncidentTokenSet(left.Transcript);
        var rightTokens = IncidentTokenSet(right.Transcript);
        if (leftTokens.Count > 0 && leftTokens.Overlaps(rightTokens))
            return true;

        return false;
    }

    private static IncidentHypothesisV2 ScopeHypothesisToCallSet(IncidentHypothesisV2 hypothesis, IReadOnlySet<long> scope)
    {
        var events = hypothesis.Events
            .Select(evidence => evidence with
            {
                SourceCallIds = evidence.SourceCallIds.Where(scope.Contains).Distinct().Order().ToList(),
                Spans = evidence.Spans.Where(span => scope.Contains(span.CallId)).ToList()
            })
            .Where(evidence => evidence.SourceCallIds.Count > 0 || evidence.Spans.Count > 0)
            .ToList();
        var locations = hypothesis.Locations
            .Select(location => location with
            {
                SourceCallIds = location.SourceCallIds.Where(scope.Contains).Distinct().Order().ToList(),
                Spans = location.Spans.Where(span => scope.Contains(span.CallId)).ToList()
            })
            .Where(location => location.SourceCallIds.Count > 0 || location.Spans.Count > 0)
            .ToList();
        var originalMembershipByCallId = hypothesis.Membership
            .GroupBy(row => row.CallId)
            .ToDictionary(group => group.Key, group => group.First());
        var scopedCandidateCallIds = hypothesis.CandidateCallIds
            .Where(scope.Contains)
            .Distinct()
            .Order()
            .ToList();
        var membership = scopedCandidateCallIds
            .Distinct()
            .Order()
            .Select(callId =>
            {
                return originalMembershipByCallId.TryGetValue(callId, out var original)
                    ? original with { Decision = "accept", Role = IsPrimaryEventRole(original.Role) ? original.Role : "continuation" }
                    : new MembershipEvidenceV2(callId, "primary_event", "accept", ["server split source-backed event component"], []);
            })
            .ToList();
        var conflicts = hypothesis.Conflicts
            .Where(conflict => conflict.CallIds.All(scope.Contains))
            .Select(conflict => conflict with { Spans = conflict.Spans.Where(span => scope.Contains(span.CallId)).ToList() })
            .ToList();
        var facts = hypothesis.Narrative.Facts
            .Select(fact => fact with { Spans = fact.Spans.Where(span => scope.Contains(span.CallId)).ToList() })
            .Where(fact => fact.Spans.Count > 0)
            .ToList();

        return hypothesis with
        {
            HypothesisId = $"{hypothesis.HypothesisId}:split:{string.Join("-", scope.Order())}",
            CandidateCallIds = scopedCandidateCallIds,
            Events = events,
            Locations = locations,
            Membership = membership,
            Conflicts = conflicts,
            Narrative = hypothesis.Narrative with { Facts = facts }
        };
    }

    private static bool IsPrimaryStrength(string strength) =>
        strength is not null
        && (string.Equals(strength, "strong", StringComparison.OrdinalIgnoreCase)
            || string.Equals(strength, "primary", StringComparison.OrdinalIgnoreCase)
            || string.Equals(strength, "dispatch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(strength, "confirmed", StringComparison.OrdinalIgnoreCase));

    private static bool IsAcceptedMembership(
        MembershipEvidenceV2 row,
        IReadOnlySet<long> groundedNarrativeSourceCallIds,
        IReadOnlySet<long> groundedHypothesisSourceCallIds)
    {
        if (AcceptedDecisions.Contains(row.Decision) && !NonEventRoles.Contains(row.Role))
            return true;

        if (!groundedNarrativeSourceCallIds.Contains(row.CallId)
            && !groundedHypothesisSourceCallIds.Contains(row.CallId))
            return false;

        if (string.Equals(row.Decision, "reject", StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.Decision, "rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.Decision, "hold", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(row.Role, "unrelated", StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.Equals(row.Role, "routine", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(row.Role, "conflicting", StringComparison.OrdinalIgnoreCase)
               && !string.Equals(row.Role, "conflict", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrimaryEventRole(string role) =>
        string.Equals(role, "primary_event", StringComparison.OrdinalIgnoreCase);

    private static bool IsContinuationWithCompatibleTranscriptEvent(
        MembershipEvidenceV2 row,
        IncidentHypothesisV2 hypothesis,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        if (!string.Equals(row.Role, "continuation", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!transcriptsByCallId.TryGetValue(row.CallId, out var transcript))
            return false;

        var group = TranscriptParentEventGroup(transcript);
        if (group.Length == 0)
            return false;

        var modelGroups = hypothesis.Events
            .Where(evidence => IsPrimaryStrength(evidence.Strength))
            .Select(ParentEventGroup)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return modelGroups.Any(modelGroup => ParentEventGroupsCompatible(modelGroup, group));
    }

    private static bool IsServerRecognizedBlockingConflict(ConflictEvidenceV2 conflict) =>
        string.Equals(conflict.ConflictType, "location_conflict", StringComparison.OrdinalIgnoreCase)
        || string.Equals(conflict.ConflictType, "person_conflict", StringComparison.OrdinalIgnoreCase)
        || string.Equals(conflict.ConflictType, "vehicle_conflict", StringComparison.OrdinalIgnoreCase)
        || string.Equals(conflict.ConflictType, "parent_event_conflict", StringComparison.OrdinalIgnoreCase);

    private static List<long> RetainServerRecognizedInitialDispatchCalls(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyList<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId,
        List<string> reasons)
    {
        if (acceptedCallIds.Count == 0 || !HasAcceptedMedicalEmergencyEvidence(hypothesis, acceptedCallIds, transcriptsByCallId))
            return acceptedCallIds.ToList();

        var accepted = acceptedCallIds.ToHashSet();
        var earliestAccepted = acceptedCallIds.Min();
        var retained = new List<long>();
        foreach (var callId in hypothesis.CandidateCallIds.Distinct().Order())
        {
            if (accepted.Contains(callId) || callId > earliestAccepted)
                continue;
            if (!transcriptsByCallId.TryGetValue(callId, out var transcript))
                continue;
            if (!HasInitialMedicalDispatchSignal(transcript))
                continue;
            if (HasRecognizedBlockingConflictForCall(hypothesis, callId, transcriptsByCallId))
                continue;

            retained.Add(callId);
        }

        if (retained.Count == 0)
            return acceptedCallIds.ToList();

        reasons.Add($"server retained source-backed initial emergency dispatch call(s): {string.Join(",", retained)}");
        return acceptedCallIds.Concat(retained).Distinct().Order().ToList();
    }

    private static List<long> RetainServerRecognizedContinuationCalls(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyList<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId,
        List<string> reasons)
    {
        if (acceptedCallIds.Count == 0)
            return acceptedCallIds.ToList();

        var accepted = acceptedCallIds.ToHashSet();
        var retained = new List<long>();
        foreach (var row in hypothesis.Membership.OrderBy(row => row.CallId))
        {
            if (accepted.Contains(row.CallId) || retained.Contains(row.CallId))
                continue;
            if (!IsContinuationCandidate(row))
                continue;
            if (!transcriptsByCallId.TryGetValue(row.CallId, out var transcript))
                continue;
            if (IsRoutineOrTransportOnlyTranscript(transcript))
                continue;
            if (HasRecognizedBlockingConflictForCall(hypothesis, row.CallId, transcriptsByCallId))
                continue;
            if (!HasSourceBackedContinuationAnchor(row.CallId, transcript, accepted.Concat(retained).ToHashSet(), transcriptsByCallId))
                continue;

            retained.Add(row.CallId);
        }

        if (retained.Count == 0)
            return acceptedCallIds.ToList();

        reasons.Add($"server retained source-backed continuation/update call(s): {string.Join(",", retained)}");
        return acceptedCallIds.Concat(retained).Distinct().Order().ToList();
    }

    private static bool IsContinuationCandidate(MembershipEvidenceV2 row)
    {
        var role = row.Role ?? string.Empty;
        var decision = row.Decision ?? string.Empty;
        if (NonEventRoles.Contains(role))
            return false;
        if (string.Equals(role, "primary_event", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(decision, "hold", StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(role, "continuation", StringComparison.OrdinalIgnoreCase)
               || string.Equals(role, "supporting", StringComparison.OrdinalIgnoreCase)
               || string.Equals(role, "update", StringComparison.OrdinalIgnoreCase)
               || AcceptedDecisions.Contains(decision);
    }

    private static bool HasSourceBackedContinuationAnchor(
        long callId,
        string transcript,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        if (SharesConcreteLocationWithAcceptedCall(callId, transcript, acceptedCallIds, transcriptsByCallId)
            && (HasContinuationLanguage(transcript) || SharesParentEventGroupWithAcceptedCall(transcript, acceptedCallIds, transcriptsByCallId)))
            return true;

        if (HasResponderStatusForAcceptedEmergency(transcript, acceptedCallIds, transcriptsByCallId)
            && !HasConflictingConcreteLocation(callId, transcript, acceptedCallIds, transcriptsByCallId))
            return true;

        return SharesParentEventGroupWithAcceptedCall(transcript, acceptedCallIds, transcriptsByCallId)
               && HasContinuationLanguage(transcript)
               && HasSharedIncidentToken(transcript, acceptedCallIds, transcriptsByCallId)
               && !HasConflictingConcreteLocation(callId, transcript, acceptedCallIds, transcriptsByCallId);
    }

    private static bool SharesConcreteLocationWithAcceptedCall(
        long callId,
        string transcript,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var candidateKeys = ExtractTranscriptLocationKeys(transcript).ToList();
        if (candidateKeys.Count == 0)
            return false;

        foreach (var acceptedCallId in acceptedCallIds.Order())
        {
            if (acceptedCallId == callId || !transcriptsByCallId.TryGetValue(acceptedCallId, out var acceptedTranscript))
                continue;

            var acceptedKeys = ExtractTranscriptLocationKeys(acceptedTranscript);
            if (candidateKeys.Any(candidate => acceptedKeys.Any(accepted => LocationKeysCompatible(candidate, accepted))))
                return true;
        }

        return false;
    }

    private static bool HasConflictingConcreteLocation(
        long callId,
        string transcript,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var candidateKeys = ExtractTranscriptLocationKeys(transcript).ToList();
        if (candidateKeys.Count == 0)
            return false;

        foreach (var acceptedCallId in acceptedCallIds.Order())
        {
            if (acceptedCallId == callId || !transcriptsByCallId.TryGetValue(acceptedCallId, out var acceptedTranscript))
                continue;

            var acceptedKeys = ExtractTranscriptLocationKeys(acceptedTranscript).ToList();
            if (acceptedKeys.Count == 0)
                continue;
            if (!candidateKeys.Any(candidate => acceptedKeys.Any(accepted => LocationKeysCompatible(candidate, accepted))))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ExtractTranscriptLocationKeys(string transcript) =>
        TranscriptLocationService.ExtractLocations(transcript)
            .Select(location => NormalizeLocationConflictKey(TranscriptLocationService.NormalizeLocationKey(location)))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool SharesParentEventGroupWithAcceptedCall(
        string transcript,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var group = TranscriptParentEventGroup(transcript);
        if (group.Length == 0)
            return false;

        return acceptedCallIds
            .Select(callId => transcriptsByCallId.TryGetValue(callId, out var acceptedTranscript)
                ? TranscriptParentEventGroup(acceptedTranscript)
                : string.Empty)
            .Any(acceptedGroup => ParentEventGroupsCompatible(group, acceptedGroup));
    }

    private static bool HasContinuationLanguage(string text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        return value.Contains("responding", StringComparison.OrdinalIgnoreCase)
               || value.Contains("enroute", StringComparison.OrdinalIgnoreCase)
               || value.Contains("en route", StringComparison.OrdinalIgnoreCase)
               || value.Contains("currently", StringComparison.OrdinalIgnoreCase)
               || value.Contains("same address", StringComparison.OrdinalIgnoreCase)
               || value.Contains("caller hung up", StringComparison.OrdinalIgnoreCase)
               || value.Contains("calling back", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tried calling back", StringComparison.OrdinalIgnoreCase)
               || value.Contains("output address", StringComparison.OrdinalIgnoreCase)
               || value.Contains("second one", StringComparison.OrdinalIgnoreCase)
               || value.Contains("gave me", StringComparison.OrdinalIgnoreCase)
               || value.Contains("reference", StringComparison.OrdinalIgnoreCase)
               || value.Contains("serial", StringComparison.OrdinalIgnoreCase)
               || value.Contains("put him on", StringComparison.OrdinalIgnoreCase)
               || value.Contains("put her on", StringComparison.OrdinalIgnoreCase)
               || value.Contains("on this", StringComparison.OrdinalIgnoreCase)
               || value.Contains("card", StringComparison.OrdinalIgnoreCase)
               || value.Contains("still", StringComparison.OrdinalIgnoreCase)
               || value.Contains("update", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasResponderStatusForAcceptedEmergency(
        string transcript,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var value = (transcript ?? string.Empty).ToLowerInvariant();
        var hasResponderStatus = (value.Contains("pd", StringComparison.OrdinalIgnoreCase)
                                  || value.Contains("so", StringComparison.OrdinalIgnoreCase)
                                  || value.Contains("sheriff", StringComparison.OrdinalIgnoreCase)
                                  || value.Contains("police", StringComparison.OrdinalIgnoreCase)
                                  || value.Contains("ems", StringComparison.OrdinalIgnoreCase)
                                  || value.Contains("medic", StringComparison.OrdinalIgnoreCase))
                                 && (value.Contains("still enroute", StringComparison.OrdinalIgnoreCase)
                                     || value.Contains("still en route", StringComparison.OrdinalIgnoreCase)
                                     || value.Contains("enroute", StringComparison.OrdinalIgnoreCase)
                                     || value.Contains("en route", StringComparison.OrdinalIgnoreCase));
        if (!hasResponderStatus)
            return false;

        return acceptedCallIds.Any(callId =>
            transcriptsByCallId.TryGetValue(callId, out var acceptedTranscript)
            && TranscriptParentEventGroup(acceptedTranscript).Equals("medical", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasSharedIncidentToken(
        string transcript,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var candidate = IncidentTokenSet(transcript);
        if (candidate.Count == 0)
            return false;

        return acceptedCallIds
            .Select(callId => transcriptsByCallId.TryGetValue(callId, out var acceptedTranscript) ? IncidentTokenSet(acceptedTranscript) : [])
            .Any(acceptedTokens => candidate.Overlaps(acceptedTokens));
    }

    private static HashSet<string> IncidentTokenSet(string text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (value.Contains("stabbing", StringComparison.OrdinalIgnoreCase) || value.Contains("tabbing", StringComparison.OrdinalIgnoreCase))
            tokens.Add("stabbing");
        if (value.Contains("suicid", StringComparison.OrdinalIgnoreCase) || value.Contains("kill herself", StringComparison.OrdinalIgnoreCase) || value.Contains("kill himself", StringComparison.OrdinalIgnoreCase))
            tokens.Add("suicidal");
        if (ContainsWord(value, "gun") || value.Contains("firearm", StringComparison.OrdinalIgnoreCase) || value.Contains("armed", StringComparison.OrdinalIgnoreCase))
            tokens.Add("weapon");
        if (value.Contains("black car", StringComparison.OrdinalIgnoreCase) || value.Contains("black vehicle", StringComparison.OrdinalIgnoreCase))
            tokens.Add("black_vehicle");
        if (HasTrafficIncidentSignal(value))
            tokens.Add(HasPedestrianTrafficHazardSignal(value) ? "traffic_hazard" : "traffic_collision");
        if (value.Contains("structure fire", StringComparison.OrdinalIgnoreCase) || value.Contains("fire structure", StringComparison.OrdinalIgnoreCase) || value.Contains("on fire", StringComparison.OrdinalIgnoreCase))
            tokens.Add("structure_fire");
        if ((value.Contains("911", StringComparison.OrdinalIgnoreCase) || value.Contains("unknown to one call", StringComparison.OrdinalIgnoreCase) || value.Contains("unknown one call", StringComparison.OrdinalIgnoreCase) || value.Contains("unknown 1 call", StringComparison.OrdinalIgnoreCase))
            && (value.Contains("hang", StringComparison.OrdinalIgnoreCase) || value.Contains("hung up", StringComparison.OrdinalIgnoreCase) || value.Contains("calling back", StringComparison.OrdinalIgnoreCase)))
            tokens.Add("911_hangup");
        if (value.Contains("stroke", StringComparison.OrdinalIgnoreCase)
            || value.Contains("slurred speech", StringComparison.OrdinalIgnoreCase)
            || value.Contains("splurge speech", StringComparison.OrdinalIgnoreCase))
            tokens.Add("stroke");
        if (value.Contains("seizure", StringComparison.OrdinalIgnoreCase))
            tokens.Add("seizure");
        if (value.Contains("difficulty breathing", StringComparison.OrdinalIgnoreCase)
            || value.Contains("shortness of breath", StringComparison.OrdinalIgnoreCase)
            || value.Contains("respiratory distress", StringComparison.OrdinalIgnoreCase)
            || value.Contains("restrode stress", StringComparison.OrdinalIgnoreCase))
            tokens.Add("breathing_distress");
        if (value.Contains("stolen firearm", StringComparison.OrdinalIgnoreCase))
            tokens.Add("stolen_firearm");
        if (value.Contains("dog", StringComparison.OrdinalIgnoreCase) || value.Contains("humane", StringComparison.OrdinalIgnoreCase))
            tokens.Add("animal");
        if (value.Contains("doa", StringComparison.OrdinalIgnoreCase) || value.Contains("doi call", StringComparison.OrdinalIgnoreCase))
            tokens.Add("doa");
        return tokens;
    }

    private static bool IsRoutineOrTransportOnlyTranscript(string text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        return IsNonEmergencyTransportOrHospitalReport(value)
               || IsTransitOperationsTraffic(value)
               || IsRoutineTrafficStop(value);
    }

    private static List<long> RetainServerRecognizedPrimaryEventCalls(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyDictionary<long, IncidentEvidenceCallContextV2> callContextsByCallId,
        List<string> reasons)
    {
        var recognized = hypothesis.CandidateCallIds
            .Distinct()
            .Order()
            .Select(callId =>
            {
                var transcript = callContextsByCallId.TryGetValue(callId, out var value) ? value.Transcript : string.Empty;
                var group = TranscriptParentEventGroup(transcript);
                var span = group.Length > 0 ? TryFindPrimarySignalSpan(callId, transcript, group) : null;
                var locationKeys = ExtractTranscriptLocationKeys(transcript).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
                return new RecognizedCallEvent(callId, group, span, locationKeys, transcript);
            })
            .Where(item => item.Group.Length > 0 && item.Span is not null)
            .Where(item => callContextsByCallId.TryGetValue(item.CallId, out var context) && !IsRoutineOrTransportOnlyContext(context))
            .ToList();
        if (recognized.Count == 0)
            return [];

        var dominantGroup = recognized
            .GroupBy(item => ParentEventCluster(item.Group), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Min(item => item.CallId))
            .First()
            .Key;
        var retainedCandidates = recognized
            .Where(item => ParentEventCluster(item.Group).Equals(dominantGroup, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var coherent = RetainLargestConcretePrimaryEventComponent(retainedCandidates, reasons);
        var retained = coherent
            .Select(item => item.CallId)
            .Distinct()
            .Order()
            .ToList();
        if (retained.Count > 0)
            reasons.Add($"server retained source-backed primary event call(s) after model rejected all membership: {string.Join(",", retained)}");
        return retained;
    }

    private static List<RecognizedCallEvent> RetainLargestConcretePrimaryEventComponent(
        IReadOnlyList<RecognizedCallEvent> candidates,
        List<string> reasons)
    {
        if (candidates.Count <= 1)
            return candidates.ToList();

        var components = BuildConcreteLocationComponents(candidates);
        var locatedComponents = components
            .Where(component => component.Any(item => item.LocationKeys.Count > 0))
            .ToList();
        if (locatedComponents.Count > 0)
        {
            var retained = locatedComponents
                .OrderByDescending(component => component.Count)
                .ThenBy(component => component.Min(item => item.CallId))
                .First()
                .OrderBy(item => item.CallId)
                .ToList();
            retained = IncludeUnlocatedMedicalStatusUpdates(candidates, retained);
            AddDroppedFallbackPrimaryEventReason(candidates, retained, reasons);
            return retained;
        }

        var crashCandidates = candidates
            .Where(item => IsTrafficCrashSignal(item.Span?.Text ?? string.Empty))
            .OrderBy(item => item.CallId)
            .ToList();
        if (crashCandidates.Count > 0)
        {
            AddDroppedFallbackPrimaryEventReason(candidates, crashCandidates, reasons);
            return crashCandidates;
        }

        var earliest = IncludeUnlocatedMedicalStatusUpdates(candidates, candidates.OrderBy(item => item.CallId).Take(1).ToList());
        AddDroppedFallbackPrimaryEventReason(candidates, earliest, reasons);
        return earliest;
    }

    private static List<RecognizedCallEvent> IncludeUnlocatedMedicalStatusUpdates(
        IReadOnlyList<RecognizedCallEvent> candidates,
        IReadOnlyList<RecognizedCallEvent> retained)
    {
        if (!retained.Any(item => ParentEventCluster(item.Group).Equals("medical", StringComparison.OrdinalIgnoreCase)))
            return retained.OrderBy(item => item.CallId).ToList();

        var retainedIds = retained.Select(item => item.CallId).ToHashSet();
        var additions = candidates
            .Where(item => !retainedIds.Contains(item.CallId))
            .Where(item => ParentEventCluster(item.Group).Equals("medical", StringComparison.OrdinalIgnoreCase))
            .Where(item => item.LocationKeys.Count == 0)
            .Where(item => IsUnlocatedMedicalStatusUpdate(item.Transcript, item.Span?.Text ?? string.Empty))
            .OrderBy(item => item.CallId)
            .ToList();
        if (additions.Count == 0)
            return retained.OrderBy(item => item.CallId).ToList();

        return retained.Concat(additions).OrderBy(item => item.CallId).ToList();
    }

    private static bool IsUnlocatedMedicalStatusUpdate(string transcript, string signalText)
    {
        var value = (transcript ?? string.Empty).ToLowerInvariant();
        var signal = (signalText ?? string.Empty).ToLowerInvariant();
        if (!HasMedicalEmergencySignal(signal))
            return false;

        var hasSubject = value.Contains("patient", StringComparison.OrdinalIgnoreCase)
                         || value.Contains("party", StringComparison.OrdinalIgnoreCase)
                         || value.Contains(" male ", StringComparison.OrdinalIgnoreCase)
                         || value.Contains(" female ", StringComparison.OrdinalIgnoreCase)
                         || value.StartsWith("he ", StringComparison.OrdinalIgnoreCase)
                         || value.Contains(" he ", StringComparison.OrdinalIgnoreCase)
                         || value.StartsWith("she ", StringComparison.OrdinalIgnoreCase)
                         || value.Contains(" she ", StringComparison.OrdinalIgnoreCase);
        if (!hasSubject)
            return false;

        return value.Contains("pulse", StringComparison.OrdinalIgnoreCase)
               || value.Contains("breathing", StringComparison.OrdinalIgnoreCase)
               || value.Contains("responsive", StringComparison.OrdinalIgnoreCase)
               || value.Contains("unresponsive", StringComparison.OrdinalIgnoreCase);
    }

    private static List<List<RecognizedCallEvent>> BuildConcreteLocationComponents(IReadOnlyList<RecognizedCallEvent> candidates)
    {
        var remaining = candidates.ToDictionary(item => item.CallId);
        var components = new List<List<RecognizedCallEvent>>();
        foreach (var seed in candidates.OrderBy(item => item.CallId))
        {
            if (!remaining.Remove(seed.CallId))
                continue;

            var component = new List<RecognizedCallEvent> { seed };
            var queue = new Queue<RecognizedCallEvent>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var other in remaining.Values.OrderBy(item => item.CallId).ToList())
                {
                    if (!RecognizedPrimaryEventsShareConcreteLocation(current, other))
                        continue;

                    remaining.Remove(other.CallId);
                    component.Add(other);
                    queue.Enqueue(other);
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static bool RecognizedPrimaryEventsShareConcreteLocation(RecognizedCallEvent left, RecognizedCallEvent right)
    {
        if (left.LocationKeys.Count == 0 || right.LocationKeys.Count == 0)
            return false;

        return left.LocationKeys.Any(leftKey => right.LocationKeys.Any(rightKey =>
            LocationKeysCompatible(leftKey, rightKey) || LocationKeysShareAddressNumberAndStreetToken(leftKey, rightKey)));
    }

    private static bool LocationKeysShareAddressNumberAndStreetToken(string left, string right)
    {
        var leftTokens = ComparableLocationTokens(left).ToList();
        var rightTokens = ComparableLocationTokens(right).ToList();
        var leftNumber = leftTokens.FirstOrDefault(token => token.All(char.IsDigit));
        var rightNumber = rightTokens.FirstOrDefault(token => token.All(char.IsDigit));
        if (string.IsNullOrWhiteSpace(leftNumber) || !string.Equals(leftNumber, rightNumber, StringComparison.OrdinalIgnoreCase))
            return false;

        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "st", "rd", "dr", "ave", "ln", "ct", "cir", "blvd", "hwy", "i",
            "n", "s", "e", "w", "ne", "nw", "se", "sw"
        };
        var leftStreetTokens = leftTokens.Where(token => !token.All(char.IsDigit) && !ignored.Contains(token) && token.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightStreetTokens = rightTokens.Where(token => !token.All(char.IsDigit) && !ignored.Contains(token) && token.Length > 2).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftStreetTokens.Count > 0 && rightStreetTokens.Count > 0 && leftStreetTokens.Overlaps(rightStreetTokens);
    }

    private static IEnumerable<string> ComparableLocationTokens(string key) =>
        (key ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Equals("hickson", StringComparison.OrdinalIgnoreCase) ? "hixson" : token);

    private static void AddDroppedFallbackPrimaryEventReason(
        IReadOnlyList<RecognizedCallEvent> candidates,
        IReadOnlyList<RecognizedCallEvent> retained,
        List<string> reasons)
    {
        var retainedIds = retained.Select(item => item.CallId).ToHashSet();
        var dropped = candidates
            .Where(item => !retainedIds.Contains(item.CallId))
            .Select(item => item.CallId)
            .Order()
            .ToList();
        if (dropped.Count > 0)
            reasons.Add($"server dropped disconnected primary event fallback call(s): {string.Join(",", dropped)}");
    }

    private static List<long> RetainDominantParentEventGroup(
        IncidentHypothesisV2 hypothesis,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId,
        List<string> reasons)
    {
        var eventGroupsByCall = acceptedCallIds
            .Select(callId => new RecognizedCallEvent(callId, ParentEventGroupForAcceptedCall(hypothesis, callId, transcriptsByCallId), null, [], transcriptsByCallId.TryGetValue(callId, out var transcript) ? transcript : string.Empty))
            .Where(item => item.Group.Length > 0)
            .ToList();
        if (eventGroupsByCall.Count == 0)
            return acceptedCallIds.Order().ToList();

        var dominantGroup = eventGroupsByCall
            .GroupBy(item => ParentEventCluster(item.Group), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Min(item => item.CallId))
            .First()
            .Key;
        var retained = acceptedCallIds
            .Where(callId =>
            {
                var group = ParentEventGroupForAcceptedCall(hypothesis, callId, transcriptsByCallId);
                return group.Length == 0 || ParentEventCluster(group).Equals(dominantGroup, StringComparison.OrdinalIgnoreCase);
            })
            .Distinct()
            .Order()
            .ToList();
        if (retained.Count < acceptedCallIds.Count)
        {
            var dropped = acceptedCallIds.Except(retained).Order().ToList();
            reasons.Add($"server dropped parent-event-conflicting neighbor call(s): {string.Join(",", dropped)}");
        }
        return retained;
    }

    private static List<long> RetainConnectedAcceptedPrimaryEventCalls(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyList<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId,
        List<string> reasons)
    {
        if (acceptedCallIds.Count <= 1)
            return acceptedCallIds.ToList();

        var accepted = acceptedCallIds.ToHashSet();
        var acceptedPrimaryCallIds = hypothesis.Membership
            .Where(row => accepted.Contains(row.CallId))
            .Where(row => IsPrimaryEventRole(row.Role))
            .Select(row => row.CallId)
            .Distinct()
            .Order()
            .ToList();
        if (acceptedPrimaryCallIds.Count <= 1)
            return acceptedCallIds.ToList();

        var acceptedEvents = acceptedPrimaryCallIds
            .Select(callId => BuildAcceptedPrimaryEventCall(hypothesis, callId, transcriptsByCallId))
            .Where(item => item.Group.Length > 0)
            .ToList();
        if (acceptedEvents.Count <= 1)
            return acceptedCallIds.ToList();

        var retained = acceptedCallIds.ToHashSet();
        foreach (var cluster in acceptedEvents.GroupBy(item => ParentEventCluster(item.Group), StringComparer.OrdinalIgnoreCase))
        {
            var clusterEvents = cluster.ToList();
            if (clusterEvents.Count <= 1)
                continue;

            var anchors = clusterEvents
                .Where(item => item.HasTranscriptPrimarySignal)
                .Where(item => !item.IsRoutine)
                .ToList();
            if (anchors.Count == 0)
                continue;

            var retainedAnchorIds = anchors.Count == 1
                ? new HashSet<long>([anchors[0].CallId])
                : SelectDominantAcceptedPrimaryComponent(anchors).Select(item => item.CallId).ToHashSet();

            var dropped = new List<long>();
            foreach (var item in clusterEvents.OrderBy(item => item.CallId))
            {
                if (retainedAnchorIds.Contains(item.CallId))
                    continue;

                if (item.IsRoutine
                    || item.HasTranscriptPrimarySignal
                    || !AcceptedPrimaryEventConnectsToAny(item, anchors.Where(anchor => retainedAnchorIds.Contains(anchor.CallId)).ToList()))
                {
                    dropped.Add(item.CallId);
                }
            }

            foreach (var callId in dropped)
                retained.Remove(callId);
        }

        if (retained.Count == acceptedCallIds.Count)
            return acceptedCallIds.ToList();

        var removed = acceptedCallIds.Where(callId => !retained.Contains(callId)).Distinct().Order().ToList();
        reasons.Add($"server dropped disconnected accepted primary event call(s): {string.Join(",", removed)}");
        return acceptedCallIds.Where(retained.Contains).Distinct().Order().ToList();
    }

    private static List<long> DropDisconnectedTerminalMedicalStatusCalls(
        IReadOnlyList<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId,
        List<string> reasons)
    {
        if (acceptedCallIds.Count <= 1)
            return acceptedCallIds.ToList();

        var accepted = acceptedCallIds.ToHashSet();
        var retained = acceptedCallIds.ToHashSet();
        foreach (var callId in acceptedCallIds.Order())
        {
            var transcript = transcriptsByCallId.TryGetValue(callId, out var value) ? value : string.Empty;
            if (!IsTerminalMedicalStatusOnlyTranscript(transcript))
                continue;

            var statusLocations = ExtractTranscriptLocationKeys(transcript)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (statusLocations.Count == 0)
            {
                retained.Remove(callId);
                continue;
            }

            var connected = accepted
                .Where(id => id != callId)
                .Select(id => transcriptsByCallId.TryGetValue(id, out var other) ? other : string.Empty)
                .Select(other => ExtractTranscriptLocationKeys(other).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                .Any(otherLocations => otherLocations.Count > 0
                                       && statusLocations.Any(left => otherLocations.Any(right => LocationKeysCompatible(left, right))));
            if (!connected)
                retained.Remove(callId);
        }

        if (retained.Count == acceptedCallIds.Count)
            return acceptedCallIds.ToList();

        var removed = acceptedCallIds.Where(callId => !retained.Contains(callId)).Distinct().Order().ToList();
        reasons.Add($"server dropped disconnected terminal medical status call(s): {string.Join(",", removed)}");
        return acceptedCallIds.Where(retained.Contains).Distinct().Order().ToList();
    }

    private static bool IsTerminalMedicalStatusOnlyTranscript(string text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        var hasTerminalStatus = value.Contains("patient's loaded", StringComparison.OrdinalIgnoreCase)
                                || value.Contains("patients loaded", StringComparison.OrdinalIgnoreCase)
                                || value.Contains("patient loaded", StringComparison.OrdinalIgnoreCase)
                                || value.Contains("command terminate", StringComparison.OrdinalIgnoreCase)
                                || value.Contains("terminate", StringComparison.OrdinalIgnoreCase)
                                || value.Contains("termination", StringComparison.OrdinalIgnoreCase);
        if (!hasTerminalStatus)
            return false;

        return !HasMedicalEmergencySignal(value)
               && !value.Contains("chest pain", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("difficulty breathing", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("unconscious", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("seizure", StringComparison.OrdinalIgnoreCase)
               && !value.Contains("stroke", StringComparison.OrdinalIgnoreCase);
    }

    private static AcceptedPrimaryEventCall BuildAcceptedPrimaryEventCall(
        IncidentHypothesisV2 hypothesis,
        long callId,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var transcript = transcriptsByCallId.TryGetValue(callId, out var value) ? value : string.Empty;
        var transcriptGroup = TranscriptParentEventGroup(transcript);
        var group = transcriptGroup.Length > 0 ? transcriptGroup : ParentEventGroupForAcceptedCall(hypothesis, callId, transcriptsByCallId);
        var span = group.Length > 0 ? TryFindPrimarySignalSpan(callId, transcript, group) : null;
        var locationKeys = ExtractTranscriptLocationKeys(transcript).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        var incidentTokens = IncidentTokenSet(transcript);
        return new AcceptedPrimaryEventCall(
            callId,
            group,
            transcript,
            transcriptGroup.Length > 0 && span is not null,
            IsRoutineOrTransportOnlyTranscript(transcript),
            IsTrafficCrashSignal(transcript),
            locationKeys,
            incidentTokens);
    }

    private static List<AcceptedPrimaryEventCall> SelectDominantAcceptedPrimaryComponent(IReadOnlyList<AcceptedPrimaryEventCall> anchors)
    {
        var components = BuildAcceptedPrimaryComponents(anchors);
        if (components.Count <= 1)
            return anchors.OrderBy(item => item.CallId).ToList();

        return components
            .OrderByDescending(component => component.Count)
            .ThenByDescending(component => component.Count(item => item.LocationKeys.Count > 0))
            .ThenByDescending(component => component.Count(item => item.HasTrafficCrashSignal))
            .ThenBy(component => component.Min(item => item.CallId))
            .First()
            .OrderBy(item => item.CallId)
            .ToList();
    }

    private static List<List<AcceptedPrimaryEventCall>> BuildAcceptedPrimaryComponents(IReadOnlyList<AcceptedPrimaryEventCall> anchors)
    {
        var remaining = anchors.ToDictionary(item => item.CallId);
        var components = new List<List<AcceptedPrimaryEventCall>>();
        foreach (var seed in anchors.OrderBy(item => item.CallId))
        {
            if (!remaining.Remove(seed.CallId))
                continue;

            var component = new List<AcceptedPrimaryEventCall> { seed };
            var queue = new Queue<AcceptedPrimaryEventCall>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var other in remaining.Values.OrderBy(item => item.CallId).ToList())
                {
                    if (!AcceptedPrimaryEventsConnected(current, other))
                        continue;

                    remaining.Remove(other.CallId);
                    component.Add(other);
                    queue.Enqueue(other);
                }
            }

            components.Add(component);
        }

        return components;
    }

    private static bool AcceptedPrimaryEventConnectsToAny(
        AcceptedPrimaryEventCall candidate,
        IReadOnlyList<AcceptedPrimaryEventCall> retainedAnchors) =>
        retainedAnchors.Any(anchor => AcceptedPrimaryEventsConnected(candidate, anchor));

    private static bool AcceptedPrimaryEventsConnected(AcceptedPrimaryEventCall left, AcceptedPrimaryEventCall right)
    {
        if (!ParentEventGroupsCompatible(left.Group, right.Group))
            return false;

        if (left.LocationKeys.Count > 0 && right.LocationKeys.Count > 0)
            return left.LocationKeys.Any(leftKey => right.LocationKeys.Any(rightKey => LocationKeysCompatible(leftKey, rightKey)));

        if (left.IncidentTokens.Count > 0 && left.IncidentTokens.Overlaps(right.IncidentTokens))
            return true;

        if ((HasContinuationLanguage(left.Transcript) || HasContinuationLanguage(right.Transcript))
            && !AcceptedPrimaryEventsHaveConflictingConcreteLocations(left, right))
            return true;

        return false;
    }

    private static bool AcceptedPrimaryEventsHaveConflictingConcreteLocations(AcceptedPrimaryEventCall left, AcceptedPrimaryEventCall right) =>
        left.LocationKeys.Count > 0
        && right.LocationKeys.Count > 0
        && !left.LocationKeys.Any(leftKey => right.LocationKeys.Any(rightKey => LocationKeysCompatible(leftKey, rightKey)));

    private static string ParentEventGroupForAcceptedCall(
        IncidentHypothesisV2 hypothesis,
        long callId,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var transcriptGroup = transcriptsByCallId.TryGetValue(callId, out var transcript)
            ? TranscriptParentEventGroup(transcript)
            : string.Empty;
        if (transcriptGroup.Length > 0)
            return transcriptGroup;

        var modelGroups = hypothesis.Events
            .Where(evidence => IsPrimaryStrength(evidence.Strength))
            .Where(evidence => evidence.SourceCallIds.Contains(callId))
            .Select(ParentEventGroup)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (modelGroups.Count == 1)
            return modelGroups[0];

        return string.Empty;
    }

    private static bool HasAcceptedMedicalEmergencyEvidence(
        IncidentHypothesisV2 hypothesis,
        IReadOnlyCollection<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var accepted = acceptedCallIds.ToHashSet();
        return hypothesis.Events
            .Where(evidence => evidence.SourceCallIds.Any(accepted.Contains))
            .Where(evidence => evidence.Spans.Count > 0 && IncidentEvidenceClaimVerifier.AnySpanGrounded(evidence.Spans, transcriptsByCallId))
            .Any(evidence => IsMedicalEmergencyEvent(evidence) || evidence.Spans.Any(span => HasMedicalEmergencySignal(span.Text)));
    }

    private static bool IsMedicalEmergencyEvent(EventEvidenceV2 evidence)
    {
        var value = $"{evidence.EventClass} {evidence.EventSubtype}".ToLowerInvariant();
        return value.Contains("medical", StringComparison.OrdinalIgnoreCase)
               || value.Contains("ems", StringComparison.OrdinalIgnoreCase)
               || HasNonNegatedOverdoseSignal(value)
               || value.Contains("diabetic problem", StringComparison.OrdinalIgnoreCase)
               || value.Contains("diabetic problems", StringComparison.OrdinalIgnoreCase)
               || value.Contains("unconscious", StringComparison.OrdinalIgnoreCase)
               || value.Contains("stroke", StringComparison.OrdinalIgnoreCase)
               || value.Contains("cpr", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInitialMedicalDispatchSignal(string text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        return (value.Contains("engine", StringComparison.OrdinalIgnoreCase)
                || value.Contains("medic", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ems", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ambulance", StringComparison.OrdinalIgnoreCase))
               && HasMedicalEmergencySignal(value);
    }

    private static bool HasMedicalEmergencySignal(string text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        return value.Contains("unconscious", StringComparison.OrdinalIgnoreCase)
               || value.Contains("not responsive", StringComparison.OrdinalIgnoreCase)
               || value.Contains("not completely responsive", StringComparison.OrdinalIgnoreCase)
               || value.Contains("doesn't respond", StringComparison.OrdinalIgnoreCase)
               || value.Contains("does not respond", StringComparison.OrdinalIgnoreCase)
               || value.Contains("not breathing", StringComparison.OrdinalIgnoreCase)
               || value.Contains("difficulty breathing", StringComparison.OrdinalIgnoreCase)
               || value.Contains("shortness of breath", StringComparison.OrdinalIgnoreCase)
               || value.Contains("respiratory distress", StringComparison.OrdinalIgnoreCase)
               || value.Contains("restrode stress", StringComparison.OrdinalIgnoreCase)
               || value.Contains("cpr", StringComparison.OrdinalIgnoreCase)
               || value.Contains("heart attack", StringComparison.OrdinalIgnoreCase)
               || value.Contains("bar attack", StringComparison.OrdinalIgnoreCase)
               || value.Contains("another onset", StringComparison.OrdinalIgnoreCase)
               || value.Contains("heart problems", StringComparison.OrdinalIgnoreCase)
               || HasNonNegatedOverdoseSignal(value)
               || value.Contains("diabetic problem", StringComparison.OrdinalIgnoreCase)
               || value.Contains("diabetic problems", StringComparison.OrdinalIgnoreCase)
               || value.Contains("stroke", StringComparison.OrdinalIgnoreCase)
               || value.Contains("slurred speech", StringComparison.OrdinalIgnoreCase)
               || value.Contains("splurge speech", StringComparison.OrdinalIgnoreCase)
               || value.Contains("seizure", StringComparison.OrdinalIgnoreCase)
               || value.Contains("seizures", StringComparison.OrdinalIgnoreCase)
               || value.Contains("chest pain", StringComparison.OrdinalIgnoreCase)
               || value.Contains("severe back pain", StringComparison.OrdinalIgnoreCase)
               || value.Contains("can't move", StringComparison.OrdinalIgnoreCase)
               || value.Contains("cannot move", StringComparison.OrdinalIgnoreCase)
               || value.Contains("not moving", StringComparison.OrdinalIgnoreCase)
               || HasMedicalFallEmergencySignal(value);
    }

    private static bool HasMedicalFallEmergencySignal(string text)
    {
        var hasFall = text.Contains("fall", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("fell", StringComparison.OrdinalIgnoreCase);
        if (!hasFall)
            return false;

        return text.Contains("hit his head", StringComparison.OrdinalIgnoreCase)
               || text.Contains("hit her head", StringComparison.OrdinalIgnoreCase)
               || text.Contains("hit head", StringComparison.OrdinalIgnoreCase)
               || text.Contains("head injury", StringComparison.OrdinalIgnoreCase)
               || text.Contains("bleeding", StringComparison.OrdinalIgnoreCase)
               || text.Contains("altered mental status", StringComparison.OrdinalIgnoreCase)
               || text.Contains("acting strange", StringComparison.OrdinalIgnoreCase)
               || text.Contains("shaking and confused", StringComparison.OrdinalIgnoreCase)
               || text.Contains("unconscious", StringComparison.OrdinalIgnoreCase)
               || text.Contains("unresponsive", StringComparison.OrdinalIgnoreCase)
               || text.Contains("breathing", StringComparison.OrdinalIgnoreCase)
               || text.Contains("injur", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasNonNegatedOverdoseSignal(string text)
    {
        var value = text ?? string.Empty;
        var index = value.IndexOf("overdose", StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            if (!IsNegatedPhraseAt(value, index))
                return true;

            index = value.IndexOf("overdose", index + "overdose".Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsNegatedPhraseAt(string text, int phraseIndex)
    {
        var prefixStart = Math.Max(0, phraseIndex - 16);
        var prefix = text[prefixStart..phraseIndex].ToLowerInvariant();
        return prefix.Contains("not an ", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("not a ", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("not ", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("no ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRecognizedBlockingConflictForCall(
        IncidentHypothesisV2 hypothesis,
        long callId,
        IReadOnlyDictionary<long, string> transcriptsByCallId) =>
        hypothesis.Conflicts
            .Where(IsServerRecognizedBlockingConflict)
            .Where(conflict => conflict.CallIds.Contains(callId))
            .Any(conflict => conflict.Spans.Count == 0 || IncidentEvidenceClaimVerifier.AnySpanGrounded(conflict.Spans, transcriptsByCallId));

    private static IReadOnlyList<ConflictEvidenceV2> DeriveLocationConflicts(
        IncidentHypothesisV2 hypothesis,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var claims = hypothesis.Locations
            .Where(location => IsConcreteLocationKind(location.Kind))
            .Where(location => IsHighConfidenceLocation(location.Confidence))
            .Where(location => location.SourceCallIds.Any(acceptedCallIds.Contains))
            .Where(location => location.Spans.Count > 0)
            .Where(location => IncidentEvidenceClaimVerifier.AnySpanGrounded(location.Spans, transcriptsByCallId))
            .Select(location => new LocationClaim(
                LocationConflictKey(location),
                location.Display,
                location.SourceCallIds.Where(acceptedCallIds.Contains).Distinct().Order().ToList(),
                location.Spans.Where(span => acceptedCallIds.Contains(span.CallId)).ToList()))
            .Where(location => !string.IsNullOrWhiteSpace(location.Key) && location.SourceCallIds.Count > 0)
            .ToList();

        for (var i = 0; i < claims.Count; i++)
        {
            for (var j = i + 1; j < claims.Count; j++)
            {
                var left = claims[i];
                var right = claims[j];
                if (left.SourceCallIds.Intersect(right.SourceCallIds).Any())
                    continue;
                if (LocationKeysCompatible(left.Key, right.Key))
                    continue;

                return
                [
                    new ConflictEvidenceV2(
                        "location_conflict",
                        left.SourceCallIds.Concat(right.SourceCallIds).Distinct().Order().ToList(),
                        $"accepted calls contain incompatible concrete locations: {left.Display} vs {right.Display}",
                        left.Spans.Concat(right.Spans).ToList())
                ];
            }
        }

        return [];
    }

    private static bool IsConcreteLocationKind(string kind) =>
        string.Equals(kind, "address", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "intersection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "highway_mile_marker", StringComparison.OrdinalIgnoreCase);

    private static bool IsHighConfidenceLocation(string confidence) =>
        string.Equals(confidence, "high", StringComparison.OrdinalIgnoreCase)
        || string.Equals(confidence, "strong", StringComparison.OrdinalIgnoreCase)
        || string.Equals(confidence, "confirmed", StringComparison.OrdinalIgnoreCase);

    private static string LocationConflictKey(LocationEvidenceV2 location)
    {
        var value = string.IsNullOrWhiteSpace(location.NormalizedKey) ? location.Display : location.NormalizedKey;
        return NormalizeLocationConflictKey(value);
    }

    private static string NormalizeLocationConflictKey(string value)
    {
        var tokens = new List<string>();
        var buffer = new char[(value ?? string.Empty).Length];
        var index = 0;
        foreach (var ch in value ?? string.Empty)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[index++] = char.ToLowerInvariant(ch);
                continue;
            }

            if (index > 0)
            {
                tokens.Add(NormalizeLocationToken(new string(buffer, 0, index)));
                index = 0;
            }
        }

        if (index > 0)
            tokens.Add(NormalizeLocationToken(new string(buffer, 0, index)));

        return string.Join(' ', tokens.Where(token => token.Length > 0));
    }

    private static string NormalizeLocationToken(string token) =>
        token switch
        {
            "street" => "st",
            "road" => "rd",
            "drive" => "dr",
            "avenue" => "ave",
            "lane" => "ln",
            "court" => "ct",
            "circle" => "cir",
            "boulevard" => "blvd",
            "highway" => "hwy",
            "interstate" => "i",
            "southwest" => "sw",
            "south" => "s",
            "northwest" => "nw",
            "north" => "n",
            "southeast" => "se",
            "east" => "e",
            "northeast" => "ne",
            "west" => "w",
            _ => token
        };

    private static bool LocationKeysCompatible(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || left.Contains(right, StringComparison.OrdinalIgnoreCase)
        || right.Contains(left, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ConflictEvidenceV2> DeriveTranscriptLocationConflicts(
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        if (acceptedCallIds
                .Select(callId => transcriptsByCallId.TryGetValue(callId, out var transcript) ? transcript : string.Empty)
                .Any(transcript => !HasFireEventSignal(transcript)))
            return [];

        var claims = acceptedCallIds
            .Order()
            .SelectMany(callId =>
            {
                if (!transcriptsByCallId.TryGetValue(callId, out var transcript))
                    return [];

                return TranscriptLocationService.ExtractLocations(transcript)
                    .Where(location => location.Any(char.IsDigit))
                    .Select(location => new EntityClaim(callId, NormalizeLocationConflictKey(TranscriptLocationService.NormalizeLocationKey(location)), location));
            })
            .Where(claim => claim.Key.Length > 0)
            .GroupBy(claim => claim.CallId)
            .Select(group => group.First())
            .ToList();

        for (var i = 0; i < claims.Count; i++)
        {
            for (var j = i + 1; j < claims.Count; j++)
            {
                var left = claims[i];
                var right = claims[j];
                if (LocationKeysCompatible(left.Key, right.Key))
                    continue;

                return
                [
                    new ConflictEvidenceV2(
                        "location_conflict",
                        [left.CallId, right.CallId],
                        $"accepted transcripts contain incompatible concrete locations: {left.Display} vs {right.Display}",
                        [])
                ];
            }
        }

        return [];
    }

    private static IReadOnlyList<ConflictEvidenceV2> DeriveParentEventConflicts(
        IncidentHypothesisV2 hypothesis,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var events = hypothesis.Events
            .Where(evidence => evidence.SourceCallIds.Any(acceptedCallIds.Contains))
            .Where(evidence => evidence.Spans.Count > 0 && IncidentEvidenceClaimVerifier.AnySpanGrounded(evidence.Spans, transcriptsByCallId))
            .Where(evidence => IsPrimaryStrength(evidence.Strength))
            .Select(evidence => new EventClaim(
                ParentEventGroup(evidence),
                evidence.SourceCallIds.Where(acceptedCallIds.Contains).Distinct().Order().ToList(),
                evidence.Spans.Where(span => acceptedCallIds.Contains(span.CallId)).ToList()))
            .Where(evidence => evidence.Group.Length > 0 && evidence.SourceCallIds.Count > 0)
            .ToList();

        for (var i = 0; i < events.Count; i++)
        {
            for (var j = i + 1; j < events.Count; j++)
            {
                var left = events[i];
                var right = events[j];
                if (left.SourceCallIds.Intersect(right.SourceCallIds).Any() || ParentEventGroupsCompatible(left.Group, right.Group))
                    continue;

                return
                [
                    new ConflictEvidenceV2(
                        "parent_event_conflict",
                        left.SourceCallIds.Concat(right.SourceCallIds).Distinct().Order().ToList(),
                        $"accepted calls contain incompatible parent event types: {left.Group} vs {right.Group}",
                        left.Spans.Concat(right.Spans).ToList())
                ];
            }
        }

        return [];
    }

    private static IReadOnlyList<ConflictEvidenceV2> DeriveTranscriptParentEventConflicts(
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var claims = acceptedCallIds
            .Order()
            .Select(callId => transcriptsByCallId.TryGetValue(callId, out var transcript)
                ? new EventClaim(TranscriptParentEventGroup(transcript), [callId], [])
                : new EventClaim(string.Empty, [callId], []))
            .Where(claim => claim.Group.Length > 0)
            .ToList();

        for (var i = 0; i < claims.Count; i++)
        {
            for (var j = i + 1; j < claims.Count; j++)
            {
                var left = claims[i];
                var right = claims[j];
                if (ParentEventGroupsCompatible(left.Group, right.Group))
                    continue;

                return
                [
                    new ConflictEvidenceV2(
                        "parent_event_conflict",
                        left.SourceCallIds.Concat(right.SourceCallIds).Distinct().Order().ToList(),
                        $"accepted transcripts contain incompatible parent event signals: {left.Group} vs {right.Group}",
                        [])
                ];
            }
        }

        return [];
    }

    private static string ParentEventGroup(EventEvidenceV2 evidence)
    {
        var value = $"{evidence.EventClass} {evidence.EventSubtype}".ToLowerInvariant();
        if (value.Contains("medical") || value.Contains("ems") || value.Contains("overdose") || value.Contains("stroke") || value.Contains("chest") || value.Contains("unconscious") || value.Contains("cpr"))
            return "medical";
        if (value.Contains("traffic") || value.Contains("crash") || value.Contains("mvc") || value.Contains("mva") || value.Contains("accident") || value.Contains("road_hazard"))
            return "traffic";
        if (value.Contains("shoot") || value.Contains("shot") || value.Contains("stab") || value.Contains("assault") || value.Contains("suicid") || value.Contains("weapon"))
            return "violent_police";
        if (value.Contains("theft") || value.Contains("burglary") || value.Contains("stolen") || value.Contains("vandal"))
            return "property_police";
        if (value.Contains("rescue") || value.Contains("lockout") || value.Contains("locked_vehicle") || value.Contains("locked vehicle"))
            return "rescue";
        if (value.Contains("structure_fire") || value.Contains("fire_alarm") || HasFireEventSignal(value))
            return "fire";
        if (value.Contains("animal") || value.Contains("dog"))
            return "animal";
        if (value.Contains("911") || value.Contains("hang_up") || value.Contains("hangup"))
            return "service_call";
        return string.Empty;
    }

    private static string TranscriptParentEventGroup(string text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        if (HasReportAdministrationSignal(value))
            return string.Empty;
        if (HasAdministrativeWeaponInformationSignal(value))
            return string.Empty;

        if (HasChildLockedVehicleSignal(value))
            return "rescue";
        if (HasTrafficIncidentSignal(value) || HasRecklessDriverTrafficHazardSignal(value) || HasPedestrianTrafficHazardSignal(value))
            return "traffic";
        if (HasMedicalEmergencySignal(value) || value.Contains("difficulty breathing") || value.Contains("patient") || value.Contains("diabetic") || value.Contains("code 73") || value.Contains("doa") || value.Contains("doi call"))
            return "medical";
        if (HasFireEventSignal(value) || value.Contains("carbon monoxide") || value.Contains("oxide detection"))
            return "fire";
        if (HasPoliceBoloSignal(value))
            return "violent_police";
        if (value.Contains("shots fired") || value.Contains("stabbing") || value.Contains("tabbing") || value.Contains("fight call") || value.Contains("assault") || value.Contains("arguing with someone") || value.Contains("ring help") || value.Contains("suicid") || value.Contains("kill herself") || value.Contains("kill himself") || ContainsWord(value, "gun") || value.Contains("armed"))
            return "violent_police";
        if (value.Contains("stolen firearm") || value.Contains("stolen vehicle") || value.Contains("burglary") || value.Contains("theft") || value.Contains("hit the property") || value.Contains("mailbox") || value.Contains("trash can") || value.Contains("property damage") || HasVehicleFixedObjectCrashSignal(value))
            return "property_police";
        if (value.Contains("traffic stop")
            || value.Contains("vehicle stop")
            || HasTrafficIncidentSignal(value))
            return "traffic";
        if ((value.Contains("911") || value.Contains("unknown to one call") || value.Contains("unknown one call") || value.Contains("unknown 1 call"))
            && (value.Contains("hang") || value.Contains("hung up") || value.Contains("calling back")))
            return "service_call";
        if (value.Contains("dog") || value.Contains("humane"))
            return "animal";
        return string.Empty;
    }

    private static bool HasChildLockedVehicleSignal(string value)
    {
        var hasChild = value.Contains("child", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("children", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("kid", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("baby", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("infant", StringComparison.OrdinalIgnoreCase);
        if (!hasChild)
            return false;

        var hasVehicle = value.Contains("vehicle", StringComparison.OrdinalIgnoreCase)
                         || value.Contains("car", StringComparison.OrdinalIgnoreCase)
                         || value.Contains("suv", StringComparison.OrdinalIgnoreCase)
                         || value.Contains("truck", StringComparison.OrdinalIgnoreCase)
                         || value.Contains("van", StringComparison.OrdinalIgnoreCase);
        if (!hasVehicle)
            return false;

        return value.Contains("lock", StringComparison.OrdinalIgnoreCase)
               || value.Contains("no guardian", StringComparison.OrdinalIgnoreCase)
               || value.Contains("not seen guardian", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasTrafficIncidentSignal(string value)
    {
        if (HasNegatedCrashSignal(value))
            return false;

        return ContainsWord(value, "accident")
               || ContainsWord(value, "crash")
               || ContainsWord(value, "wreck")
               || ContainsWord(value, "mvc")
               || ContainsWord(value, "mva")
               || ContainsWord(value, "nvc")
               || ContainsWord(value, "nva")
               || HasPedestrianTrafficHazardSignal(value)
               || value.Contains("entrapment", StringComparison.OrdinalIgnoreCase)
               || value.Contains("entrapped", StringComparison.OrdinalIgnoreCase)
               || value.Contains("partially ejected", StringComparison.OrdinalIgnoreCase)
               || value.Contains("cracked into a tree", StringComparison.OrdinalIgnoreCase)
               || value.Contains("lanes are blocked", StringComparison.OrdinalIgnoreCase)
               || value.Contains("both lanes", StringComparison.OrdinalIgnoreCase)
               || value.Contains("interstate", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRecklessDriverTrafficHazardSignal(string value) =>
        value.Contains("reckless driver", StringComparison.OrdinalIgnoreCase)
        || value.Contains("driven all over the road", StringComparison.OrdinalIgnoreCase)
        || value.Contains("driven all over the roadway", StringComparison.OrdinalIgnoreCase)
        || value.Contains("driving all over the road", StringComparison.OrdinalIgnoreCase)
        || value.Contains("driving all over the roadway", StringComparison.OrdinalIgnoreCase)
        || value.Contains("couldn't maintain speed", StringComparison.OrdinalIgnoreCase)
        || value.Contains("could not maintain speed", StringComparison.OrdinalIgnoreCase);

    private static bool HasPedestrianTrafficHazardSignal(string value)
    {
        var text = value ?? string.Empty;
        if (text.Contains("crossing all lanes of traffic", StringComparison.OrdinalIgnoreCase)
            || text.Contains("crossing lanes of traffic", StringComparison.OrdinalIgnoreCase)
            || text.Contains("walking in traffic", StringComparison.OrdinalIgnoreCase))
            return true;

        var hasPedestrian = text.Contains("pedestrian", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("person", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("male", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("female", StringComparison.OrdinalIgnoreCase)
                            || text.Contains("walker", StringComparison.OrdinalIgnoreCase);
        if (!hasPedestrian)
            return false;

        var hasRoadwayMovement = text.Contains("crossing", StringComparison.OrdinalIgnoreCase)
                                 || text.Contains("walking", StringComparison.OrdinalIgnoreCase)
                                 || text.Contains("in the roadway", StringComparison.OrdinalIgnoreCase)
                                 || text.Contains("in traffic", StringComparison.OrdinalIgnoreCase);
        if (!hasRoadwayMovement)
            return false;

        return text.Contains("lanes of traffic", StringComparison.OrdinalIgnoreCase)
               || text.Contains("lane of traffic", StringComparison.OrdinalIgnoreCase)
               || text.Contains("interstate", StringComparison.OrdinalIgnoreCase)
               || text.Contains("roadway", StringComparison.OrdinalIgnoreCase)
               || text.Contains("northbound", StringComparison.OrdinalIgnoreCase)
               || text.Contains("southbound", StringComparison.OrdinalIgnoreCase)
               || text.Contains("eastbound", StringComparison.OrdinalIgnoreCase)
               || text.Contains("westbound", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrafficCrashSignal(string value) =>
        value.Contains("accident", StringComparison.OrdinalIgnoreCase)
        || value.Contains("crash", StringComparison.OrdinalIgnoreCase)
        || value.Contains("wreck", StringComparison.OrdinalIgnoreCase)
        || value.Contains("mvc", StringComparison.OrdinalIgnoreCase)
        || value.Contains("mva", StringComparison.OrdinalIgnoreCase)
        || value.Contains("nvc", StringComparison.OrdinalIgnoreCase)
        || value.Contains("nva", StringComparison.OrdinalIgnoreCase)
        || value.Contains("entrapment", StringComparison.OrdinalIgnoreCase)
        || value.Contains("entrapped", StringComparison.OrdinalIgnoreCase)
        || value.Contains("partially ejected", StringComparison.OrdinalIgnoreCase)
        || value.Contains("cracked into a tree", StringComparison.OrdinalIgnoreCase);

    private static bool HasNegatedCrashSignal(string value)
    {
        var text = value ?? string.Empty;
        return text.Contains("can't find no crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cant find no crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cannot find no crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("can't find a crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cant find a crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cannot find a crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("can't find any crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cant find any crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cannot find any crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("can't locate a crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cant locate a crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cannot locate a crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("unable to locate a crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("unable to find a crash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("no crash here", StringComparison.OrdinalIgnoreCase)
               || text.Contains("no accident here", StringComparison.OrdinalIgnoreCase)
               || text.Contains("won't be a crash report", StringComparison.OrdinalIgnoreCase)
               || text.Contains("wont be a crash report", StringComparison.OrdinalIgnoreCase)
               || text.Contains("will not be a crash report", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not doing crash reports", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not doing crash report", StringComparison.OrdinalIgnoreCase)
               || text.Contains("weren't doing crash reports", StringComparison.OrdinalIgnoreCase)
               || text.Contains("werent doing crash reports", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not a crash report", StringComparison.OrdinalIgnoreCase)
               || text.Contains("no crash report", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReportAdministrationSignal(string value)
    {
        var text = value ?? string.Empty;
        return HasNegatedCrashSignal(text)
               || text.Contains("crash reports that we're doing", StringComparison.OrdinalIgnoreCase)
               || text.Contains("crash reports that were doing", StringComparison.OrdinalIgnoreCase)
               || text.Contains("property damage reports that we're doing", StringComparison.OrdinalIgnoreCase)
               || text.Contains("property damage reports that were doing", StringComparison.OrdinalIgnoreCase)
               || text.Contains("supposed to be property damage reports", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasPoliceBoloSignal(string value)
    {
        var text = value ?? string.Empty;
        var hasLookoutLanguage = text.Contains("bolo", StringComparison.OrdinalIgnoreCase)
                                 || text.Contains("bowload", StringComparison.OrdinalIgnoreCase)
                                 || text.Contains("bellow", StringComparison.OrdinalIgnoreCase)
                                 || text.Contains("be on the lookout", StringComparison.OrdinalIgnoreCase)
                                 || text.Contains("look out for", StringComparison.OrdinalIgnoreCase)
                                 || text.Contains("stand by for a bow", StringComparison.OrdinalIgnoreCase);
        if (!hasLookoutLanguage)
            return false;

        var hasPoliceContext = text.Contains("domestic disorder", StringComparison.OrdinalIgnoreCase)
                               || text.Contains("domestic disturbance", StringComparison.OrdinalIgnoreCase)
                               || text.Contains("domestic", StringComparison.OrdinalIgnoreCase)
                               || text.Contains("suspect", StringComparison.OrdinalIgnoreCase)
                               || text.Contains("any contact", StringComparison.OrdinalIgnoreCase)
                               || text.Contains("stop, hold and notify", StringComparison.OrdinalIgnoreCase);
        var hasVehicleDescription = text.Contains("vehicle", StringComparison.OrdinalIgnoreCase)
                                    || text.Contains("chevy", StringComparison.OrdinalIgnoreCase)
                                    || text.Contains("impala", StringComparison.OrdinalIgnoreCase)
                                    || text.Contains("tag", StringComparison.OrdinalIgnoreCase);
        return hasPoliceContext && hasVehicleDescription;
    }

    private static bool HasAdministrativeWeaponInformationSignal(string value)
    {
        var text = value ?? string.Empty;
        if (text.Contains("stolen firearm", StringComparison.OrdinalIgnoreCase)
            || text.Contains("stolen gun", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gunshot", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gunshots", StringComparison.OrdinalIgnoreCase)
            || text.Contains("shots fired", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gun in his hand", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gun in her hand", StringComparison.OrdinalIgnoreCase)
            || text.Contains("gun in hand", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pointing the gun", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pointed the gun", StringComparison.OrdinalIgnoreCase)
            || text.Contains("armed"))
            return false;

        return text.Contains("serial number on a firearm", StringComparison.OrdinalIgnoreCase)
               || text.Contains("serial number on the firearm", StringComparison.OrdinalIgnoreCase)
               || text.Contains("zero number on the firearm", StringComparison.OrdinalIgnoreCase)
               || text.Contains("copy the serial number on a firearm", StringComparison.OrdinalIgnoreCase)
               || text.Contains("copy serial number on a firearm", StringComparison.OrdinalIgnoreCase)
               || text.Contains("information is in the gun", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasVehicleFixedObjectCrashSignal(string value)
    {
        var hasHit = value.Contains("hit a ", StringComparison.OrdinalIgnoreCase)
                     || value.Contains("hit an ", StringComparison.OrdinalIgnoreCase)
                     || value.Contains("hit the ", StringComparison.OrdinalIgnoreCase)
                     || value.Contains("hit ", StringComparison.OrdinalIgnoreCase);
        if (!hasHit)
            return false;

        return value.Contains(" pole", StringComparison.OrdinalIgnoreCase)
               || value.Contains(" guardrail", StringComparison.OrdinalIgnoreCase)
               || value.Contains(" barrier", StringComparison.OrdinalIgnoreCase)
               || value.Contains(" tree", StringComparison.OrdinalIgnoreCase)
               || value.Contains(" ditch", StringComparison.OrdinalIgnoreCase)
               || value.Contains(" wall", StringComparison.OrdinalIgnoreCase)
               || value.Contains(" fence", StringComparison.OrdinalIgnoreCase)
               || value.Contains(" building", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFireEventSignal(string value) =>
        value.Contains("structure fire", StringComparison.OrdinalIgnoreCase)
        || value.Contains("fire structure", StringComparison.OrdinalIgnoreCase)
        || value.Contains("commercial structure", StringComparison.OrdinalIgnoreCase)
        || value.Contains("fire alarm", StringComparison.OrdinalIgnoreCase)
        || value.Contains("commercial firearm", StringComparison.OrdinalIgnoreCase)
        || value.Contains("response firearm", StringComparison.OrdinalIgnoreCase)
        || value.Contains("automatic fire", StringComparison.OrdinalIgnoreCase)
        || value.Contains("firewall", StringComparison.OrdinalIgnoreCase)
        || value.Contains("alarm panel", StringComparison.OrdinalIgnoreCase)
        || value.Contains("carbon", StringComparison.OrdinalIgnoreCase)
        || value.Contains("gas leak", StringComparison.OrdinalIgnoreCase)
        || value.Contains("on fire", StringComparison.OrdinalIgnoreCase)
        || value.Contains("alarm", StringComparison.OrdinalIgnoreCase);

    private static bool ParentEventGroupsCompatible(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || (IsPoliceParentEventGroup(left) && IsPoliceParentEventGroup(right));

    private static bool IsPoliceParentEventGroup(string group) =>
        string.Equals(group, "violent_police", StringComparison.OrdinalIgnoreCase)
        || string.Equals(group, "property_police", StringComparison.OrdinalIgnoreCase);

    private static string ParentEventCluster(string group) =>
        IsPoliceParentEventGroup(group) ? "police" : group;

    private static IReadOnlyList<ConflictEvidenceV2> DeriveVehicleConflicts(
        IncidentHypothesisV2 hypothesis,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var vehicles = ExtractCallVehicleClaims(acceptedCallIds, transcriptsByCallId);
        for (var i = 0; i < vehicles.Count; i++)
        {
            for (var j = i + 1; j < vehicles.Count; j++)
            {
                var left = vehicles[i];
                var right = vehicles[j];
                if (VehicleClaimsCompatible(left.Key, right.Key))
                    continue;

                return
                [
                    new ConflictEvidenceV2(
                        "vehicle_conflict",
                        [left.CallId, right.CallId],
                        $"accepted calls contain incompatible vehicle descriptions: {left.Display} vs {right.Display}",
                        [])
                ];
            }
        }

        return [];
    }

    private static List<EntityClaim> ExtractCallVehicleClaims(
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var colors = new[] { "black", "white", "red", "blue", "silver", "gray", "grey", "green", "gold", "tan", "brown" };
        var kinds = new[] { "ford", "chevy", "chevrolet", "toyota", "honda", "nissan", "dodge", "jeep", "subaru", "kia", "hyundai", "truck", "sedan", "suv", "van", "pickup", "wrangler", "silverado", "camry", "civic", "accord" };
        var claims = new List<EntityClaim>();
        foreach (var callId in acceptedCallIds.Order())
        {
            if (!transcriptsByCallId.TryGetValue(callId, out var transcript))
                continue;

            var tokens = TokenSet(transcript);
            var color = colors.FirstOrDefault(tokens.Contains);
            var kind = kinds.FirstOrDefault(tokens.Contains);
            if (color is null || kind is null)
                continue;

            claims.Add(new EntityClaim(callId, $"{color} {NormalizeVehicleKind(kind)}", $"{color} {kind}"));
        }

        return claims;
    }

    private static string NormalizeVehicleKind(string value) =>
        value.Equals("chevrolet", StringComparison.OrdinalIgnoreCase) ? "chevy" :
        value.Equals("grey", StringComparison.OrdinalIgnoreCase) ? "gray" :
        value;

    private static bool VehicleClaimsCompatible(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ConflictEvidenceV2> DerivePersonConflicts(
        IncidentHypothesisV2 hypothesis,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var joinedText = string.Join(' ', acceptedCallIds.Select(id => transcriptsByCallId.TryGetValue(id, out var text) ? text : string.Empty));
        if (HasMultiPatientLanguage(joinedText))
            return [];

        var people = ExtractPersonClaims(acceptedCallIds, transcriptsByCallId);
        for (var i = 0; i < people.Count; i++)
        {
            for (var j = i + 1; j < people.Count; j++)
            {
                var left = people[i];
                var right = people[j];
                if (string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return
                [
                    new ConflictEvidenceV2(
                        "person_conflict",
                        [left.CallId, right.CallId],
                        $"accepted calls contain incompatible patient/person descriptions: {left.Display} vs {right.Display}",
                        [])
                ];
            }
        }

        return [];
    }

    private static List<EntityClaim> ExtractPersonClaims(
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var claims = new List<EntityClaim>();
        foreach (var callId in acceptedCallIds.Order())
        {
            if (!transcriptsByCallId.TryGetValue(callId, out var transcript))
                continue;

            var value = transcript.ToLowerInvariant();
            var gender = value.Contains("female") ? "female" : value.Contains("male") ? "male" : string.Empty;
            var age = ExtractAge(value);
            if (gender.Length == 0 || age is null)
                continue;

            claims.Add(new EntityClaim(callId, $"{age}:{gender}", $"{age}-year-old {gender}"));
        }

        return claims;
    }

    private static int? ExtractAge(string value)
    {
        var marker = "year old";
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        var prefix = value[..index].Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return int.TryParse(prefix, out var age) && age is > 0 and < 120 ? age : null;
    }

    private static bool HasMultiPatientLanguage(string text)
    {
        var value = (text ?? string.Empty).ToLowerInvariant();
        return value.Contains("two patients") || value.Contains("multiple patients") || value.Contains("second patient") || value.Contains("another patient");
    }

    private static ShadowNarrative SynthesizeNarrative(
        IncidentHypothesisV2 hypothesis,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyList<EventEvidenceV2> primaryEvents,
        IReadOnlyList<NarrativeFactV2> groundedFacts,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var eventText = primaryEvents
            .SelectMany(evidence => evidence.Spans)
            .Where(span => acceptedCallIds.Contains(span.CallId) && IncidentEvidenceClaimVerifier.IsSpanGrounded(span, transcriptsByCallId))
            .Select(span => CleanNarrativeText(span.Text))
            .FirstOrDefault(text => text.Length > 0) ?? "Public safety incident";
        var titleFacts = CollectTitleFacts(acceptedCallIds, transcriptsByCallId);
        var modelLocation = hypothesis.Locations
            .Where(location => location.SourceCallIds.Any(acceptedCallIds.Contains))
            .Where(location => location.Spans.Count == 0 || IncidentEvidenceClaimVerifier.AnySpanGrounded(location.Spans, transcriptsByCallId))
            .Select(location => CleanTitleLocation(location.Display, preserveCase: true))
            .FirstOrDefault(text => text.Length > 0);
        var title = primaryEvents
            .Select(evidence => StructuredEventTitle(evidence, acceptedCallIds, transcriptsByCallId))
            .FirstOrDefault(text => text.Length > 0) ?? ToTitleCase(eventText);
        var titleIsGeneric = IsGenericIncidentTitle(title);
        var originalTitle = title;
        if (titleIsGeneric)
        {
            var specificTitle = SpecificTitleFromGroundedEventText(title, eventText);
            if (!string.IsNullOrWhiteSpace(specificTitle))
                title = specificTitle;
        }
        title = RefineTitleWithGroundedFacts(title, titleFacts);
        var location = BestTitleLocation(modelLocation, titleFacts.Location, acceptedCallIds, transcriptsByCallId);
        if (!string.IsNullOrWhiteSpace(location) && !title.Contains(location, StringComparison.OrdinalIgnoreCase) && !LocationAlreadyRepresented(title, location))
            title = $"{title} at {location}";

        var facts = groundedFacts
            .Select(fact => CleanNarrativeText(fact.Text))
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var detail = facts.Count == 0
            ? $"Accepted calls contain source-backed evidence for {eventText}."
            : $"Accepted calls contain source-backed evidence: {string.Join("; ", facts)}.";

        return new ShadowNarrative(TrimNarrative(title, 120), TrimNarrative(detail, 420));
    }

    private static string StructuredEventTitle(
        EventEvidenceV2 evidence,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var tokens = EventIdentifierTokens(evidence);
        if (tokens.Count == 0)
            return string.Empty;

        var groundedSpanProfile = evidence.Spans
            .Where(span => acceptedCallIds.Contains(span.CallId) && IncidentEvidenceClaimVerifier.IsSpanGrounded(span, transcriptsByCallId))
            .Select(span => IncidentEvidenceClassifier.Analyze(span.Text))
            .FirstOrDefault(profile => !string.IsNullOrWhiteSpace(profile.FallbackPhrase));
        var groundedSpanTitle = groundedSpanProfile?.FallbackPhrase ?? string.Empty;
        var groundedSpanTexts = evidence.Spans
            .Where(span => acceptedCallIds.Contains(span.CallId) && IncidentEvidenceClaimVerifier.IsSpanGrounded(span, transcriptsByCallId))
            .Select(span => span.Text)
            .ToList();

        if (HasAny(tokens, "traffic", "crash", "accident", "mvc", "mva", "nvc", "nva", "wreck"))
        {
            if (!HasAny(tokens, "crash", "accident", "mvc", "mva", "nvc", "nva", "wreck")
                && groundedSpanProfile is not null
                && groundedSpanProfile.Concepts.Any(concept => string.Equals(concept, "reckless_driver", StringComparison.OrdinalIgnoreCase) || string.Equals(concept, "road_hazard", StringComparison.OrdinalIgnoreCase)))
                return groundedSpanTitle;
            if (groundedSpanTexts.Any(HasPedestrianTrafficHazardSignal))
                return "Pedestrian traffic hazard";
            if (HasAny(tokens, "entrapment"))
                return "Crash with entrapment";
            if (HasAny(tokens, "injury", "injuries"))
                return "Injury crash";
            return "Traffic crash";
        }

        if (HasAny(tokens, "fire", "structure", "carbon", "monoxide", "oxide", "gas"))
        {
            if (HasAll(tokens, "carbon", "monoxide") || HasAll(tokens, "oxide", "detection"))
                return "Carbon monoxide alarm";
            if (HasAll(tokens, "gas", "leak"))
                return "Gas leak";
            if (HasAny(tokens, "structure", "commercial"))
                return "Structure fire";
            if (HasAny(tokens, "alarm", "automatic", "firewall")
                || string.Equals(groundedSpanTitle, "Fire alarm", StringComparison.OrdinalIgnoreCase)
                || groundedSpanTexts.Any(text =>
                    text.Contains("commercial firearm", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("response firearm", StringComparison.OrdinalIgnoreCase)))
                return "Fire alarm";
            return "Fire incident";
        }

        if (HasAny(tokens, "medical", "ems", "overdose", "unconscious", "stroke", "breathing", "chest", "diabetic", "cpr", "doa"))
        {
            if (groundedSpanTitle.Equals("Diabetic emergency", StringComparison.OrdinalIgnoreCase)
                || groundedSpanTexts.Any(text => text.Contains("diabetic", StringComparison.OrdinalIgnoreCase)))
                return "Diabetic emergency";
            if (HasAny(tokens, "overdose"))
                return "Overdose";
            if (HasAll(tokens, "chest", "pain"))
                return "Chest pain";
            if (HasAny(tokens, "non", "cpr"))
                return "Cardiac arrest";
            if (HasAny(tokens, "breathing"))
                return "Difficulty breathing";
            if (HasAny(tokens, "stroke"))
                return "Stroke symptoms";
            if (HasAny(tokens, "unconscious"))
                return "Unconscious patient";
            if (HasAny(tokens, "diabetic"))
                return "Diabetic emergency";
            if (HasAny(tokens, "doa"))
                return "Deceased person";
            return !string.IsNullOrWhiteSpace(groundedSpanTitle) ? groundedSpanTitle : "Medical emergency";
        }

        if (HasAny(tokens, "rescue", "lockout") || HasAll(tokens, "locked", "vehicle"))
            return "Child locked in vehicle";
        if (HasAny(tokens, "shots"))
            return "Shots fired";
        if (HasAny(tokens, "stabbing"))
            return "Stabbing";
        if (HasAny(tokens, "assault", "fight"))
            return "Assault";
        if (HasAny(tokens, "burglary"))
            return "Burglary";
        if (HasAll(tokens, "home", "invasion"))
            return "Home invasion";
        if (HasAny(tokens, "theft") && HasAny(tokens, "auto", "vehicle"))
            return "Auto theft";
        if (HasAny(tokens, "stolen") && HasAny(tokens, "vehicle", "auto"))
            return "Stolen vehicle";
        if (HasAny(tokens, "stolen") && HasAny(tokens, "firearm", "gun"))
            return "Stolen firearm";
        if (HasAny(tokens, "welfare"))
            return "Welfare check";
        if (HasAll(tokens, "911", "hang") || HasAll(tokens, "911", "up"))
            return "911 hang-up";
        if (HasAny(tokens, "animal", "dog", "dogs"))
            return "Animal complaint";
        if (HasAny(tokens, "property") && HasAny(tokens, "damage"))
            return "Property damage";

        return string.Empty;
    }

    private static HashSet<string> EventIdentifierTokens(EventEvidenceV2 evidence)
    {
        var value = $"{evidence.EventClass} {evidence.EventSubtype}";
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ').ToArray();
        return new HashSet<string>(new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasAny(IReadOnlySet<string> tokens, params string[] values) =>
        values.Any(tokens.Contains);

    private static bool HasAll(IReadOnlySet<string> tokens, params string[] values) =>
        values.All(tokens.Contains);

    private static TitleFacts CollectTitleFacts(
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var acceptedTranscripts = acceptedCallIds
            .Order()
            .Select(callId => transcriptsByCallId.TryGetValue(callId, out var transcript) ? transcript : string.Empty)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        var combined = string.Join(' ', acceptedTranscripts);
        var location = acceptedTranscripts
            .SelectMany(TranscriptLocationService.ExtractLocations)
            .Select(location => CleanTitleLocation(location))
            .FirstOrDefault(text => text.Length > 0) ?? string.Empty;

        return new TitleFacts(
            EventTitleFromAcceptedTranscripts(combined),
            SubjectFromAcceptedTranscripts(combined),
            location,
            QualifierFromAcceptedTranscripts(combined));
    }

    private static string EventTitleFromAcceptedTranscripts(string text)
    {
        var value = text ?? string.Empty;
        if (HasWelfareCheckSignal(value))
            return "Welfare check";
        if (value.Contains("unconscious", StringComparison.OrdinalIgnoreCase)
            || value.Contains("unresponsive", StringComparison.OrdinalIgnoreCase)
            || value.Contains("not responsive", StringComparison.OrdinalIgnoreCase)
            || value.Contains("inresponsive", StringComparison.OrdinalIgnoreCase))
            return "Unconscious";
        if (value.Contains("difficulty breathing", StringComparison.OrdinalIgnoreCase)
            || value.Contains("respiratory distress", StringComparison.OrdinalIgnoreCase)
            || value.Contains("shortness of breath", StringComparison.OrdinalIgnoreCase))
            return "Difficulty breathing";
        if ((value.Contains("mvc", StringComparison.OrdinalIgnoreCase)
             || value.Contains("mva", StringComparison.OrdinalIgnoreCase)
             || value.Contains("nvc", StringComparison.OrdinalIgnoreCase)
             || value.Contains("nva", StringComparison.OrdinalIgnoreCase)
             || value.Contains("traffic crash", StringComparison.OrdinalIgnoreCase)
             || value.Contains("vehicle accident", StringComparison.OrdinalIgnoreCase)
             || value.Contains("motor vehicle accident", StringComparison.OrdinalIgnoreCase))
            && (value.Contains("injury", StringComparison.OrdinalIgnoreCase)
                || value.Contains("injuries", StringComparison.OrdinalIgnoreCase)))
            return "MVC with injuries";
        if (value.Contains("commercial fire alarm", StringComparison.OrdinalIgnoreCase)
            || value.Contains("commercial alarm", StringComparison.OrdinalIgnoreCase))
            return "Commercial fire alarm";
        if (value.Contains("residential fire alarm", StringComparison.OrdinalIgnoreCase)
            || value.Contains("residential alarm", StringComparison.OrdinalIgnoreCase))
            return "Residential fire alarm";
        return string.Empty;
    }

    private static bool HasWelfareCheckSignal(string text)
    {
        var value = text ?? string.Empty;
        return value.Contains("welfare check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("well being check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("wellbeing check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("check welfare", StringComparison.OrdinalIgnoreCase)
               || value.Contains("checking welfare", StringComparison.OrdinalIgnoreCase);
    }

    private static string SubjectFromAcceptedTranscripts(string text)
    {
        var value = text ?? string.Empty;
        if (value.Contains("female", StringComparison.OrdinalIgnoreCase))
            return "female";
        if (value.Contains("male", StringComparison.OrdinalIgnoreCase))
            return "male";
        if (value.Contains("child", StringComparison.OrdinalIgnoreCase))
            return "child";
        return string.Empty;
    }

    private static string QualifierFromAcceptedTranscripts(string text)
    {
        var value = text ?? string.Empty;
        if (value.Contains("dogs in heat", StringComparison.OrdinalIgnoreCase)
            || value.Contains("dog in heat", StringComparison.OrdinalIgnoreCase))
            return "with dogs in heat";
        if (value.Contains("aggressive dogs", StringComparison.OrdinalIgnoreCase)
            || value.Contains("aggressive dog", StringComparison.OrdinalIgnoreCase))
            return "with aggressive dogs";
        if (value.Contains("tractor trailer", StringComparison.OrdinalIgnoreCase)
            || value.Contains("tractor-trailer", StringComparison.OrdinalIgnoreCase))
            return "involving tractor-trailer";
        return string.Empty;
    }

    private static string RefineTitleWithGroundedFacts(string title, TitleFacts facts)
    {
        var refined = title;
        if (facts.EventTitle.Length > 0
            && (IsGenericIncidentTitle(refined)
                || string.Equals(refined, "Animal complaint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(refined, "Fire alarm", StringComparison.OrdinalIgnoreCase)))
            refined = facts.EventTitle;

        if (facts.Subject.Length > 0 && CanAppendSubject(refined, facts.Subject))
            refined = $"{refined} {facts.Subject}";
        if (facts.Qualifier.Length > 0 && !refined.Contains(facts.Qualifier, StringComparison.OrdinalIgnoreCase))
            refined = $"{refined} {facts.Qualifier}";
        return refined;
    }

    private static bool CanAppendSubject(string title, string subject)
    {
        if (title.Contains(subject, StringComparison.OrdinalIgnoreCase))
            return false;

        return title.Equals("Unconscious", StringComparison.OrdinalIgnoreCase)
               || title.Equals("Unconscious person", StringComparison.OrdinalIgnoreCase)
               || title.Equals("Unconscious patient", StringComparison.OrdinalIgnoreCase)
               || title.Equals("Difficulty breathing", StringComparison.OrdinalIgnoreCase)
               || title.Equals("Chest pain", StringComparison.OrdinalIgnoreCase)
               || title.Equals("Stroke symptoms", StringComparison.OrdinalIgnoreCase)
               || title.Equals("Seizure", StringComparison.OrdinalIgnoreCase)
               || title.Equals("Medical emergency", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LocationAlreadyRepresented(string title, string location)
    {
        var locationTokens = TokenSet(location);
        if (locationTokens.Count == 0)
            return false;

        var titleTokens = TokenSet(title);
        return locationTokens.All(titleTokens.Contains);
    }

    private static IEnumerable<string> AcceptedTranscriptLocations(
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        foreach (var callId in acceptedCallIds.Order())
        {
            if (!transcriptsByCallId.TryGetValue(callId, out var transcript) || string.IsNullOrWhiteSpace(transcript))
                continue;

            foreach (var location in TranscriptLocationService.ExtractLocations(transcript))
            {
                var clean = CleanTitleLocation(location);
                if (clean.Length > 0)
                    yield return clean;
            }
        }
    }

    private static string BestTitleLocation(
        string? modelLocation,
        string? titleFactLocation,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId) =>
        new[] { modelLocation, titleFactLocation }
            .Concat(AcceptedTranscriptLocations(acceptedCallIds, transcriptsByCallId))
            .Select(NormalizeTitleLocation)
            .FirstOrDefault(IsSafeTitleLocation) ?? string.Empty;

    private static string CleanTitleLocation(string? value, bool preserveCase = false)
    {
        var clean = preserveCase
            ? CleanNarrativeText(value ?? string.Empty)
            : TranscriptLocationService.CleanLocationText(CleanNarrativeText(value ?? string.Empty));
        clean = TrimNoisyLocationPrefix(NormalizeTitleLocation(clean));
        return clean;
    }

    private static string NormalizeTitleLocation(string? value)
    {
        var clean = CleanNarrativeText(value ?? string.Empty);
        var commaIndex = clean.IndexOf(',');
        if (commaIndex > 0 && clean[..commaIndex].All(char.IsDigit))
            clean = $"{clean[..commaIndex]} {clean[(commaIndex + 1)..].TrimStart()}";
        return CleanNarrativeText(clean);
    }

    private static string TrimNoisyLocationPrefix(string value)
    {
        var clean = CleanNarrativeText(value);
        var tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = tokens.Length - 1; i > 0; i--)
        {
            if (!tokens[i].All(char.IsDigit))
                continue;

            var candidate = string.Join(' ', tokens.Skip(i));
            if (TranscriptLocationService.IsPlausibleLocation(candidate))
                return candidate;
        }

        return clean;
    }

    private static bool IsSafeTitleLocation(string? value)
    {
        var clean = CleanNarrativeText(value ?? string.Empty);
        if (!TranscriptLocationService.IsPlausibleLocation(clean))
            return false;

        var key = TranscriptLocationService.NormalizeLocationKey(clean);
        return !key.Contains(" originally ", StringComparison.OrdinalIgnoreCase)
               && !key.StartsWith("originally ", StringComparison.OrdinalIgnoreCase)
               && !key.StartsWith("was originally ", StringComparison.OrdinalIgnoreCase)
               && !IsIncompleteNumberedHighwayLocation(key);
    }

    private static bool IsIncompleteNumberedHighwayLocation(string key)
    {
        var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 2
               && tokens[0].All(char.IsDigit)
               && (tokens[1].Equals("hwy", StringComparison.OrdinalIgnoreCase)
                   || tokens[1].Equals("highway", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGenericIncidentTitle(string title)
    {
        var clean = CleanNarrativeText(title);
        return clean.Equals("Traffic crash", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("Medical emergency", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("Public safety incident", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("Fire incident", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("Reckless driver", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("Seizure", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("Unconscious person", StringComparison.OrdinalIgnoreCase)
               || clean.Equals("Unconscious patient", StringComparison.OrdinalIgnoreCase);
    }

    private static string SpecificTitleFromGroundedEventText(string currentTitle, string eventText)
    {
        var clean = CleanNarrativeText(eventText);
        if (clean.Length == 0 || clean.Equals("Public safety incident", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (clean.Equals(currentTitle, StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        if (clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            return string.Empty;
        if (!HasSpecificEventDescriptor(clean))
            return string.Empty;

        return ToTitleCase(clean);
    }

    private static bool HasSpecificEventDescriptor(string text)
    {
        var value = text ?? string.Empty;
        return value.Contains("tractor", StringComparison.OrdinalIgnoreCase)
               || value.Contains("trailer", StringComparison.OrdinalIgnoreCase)
               || value.Contains("semi", StringComparison.OrdinalIgnoreCase)
               || value.Contains("box truck", StringComparison.OrdinalIgnoreCase)
               || value.Contains("pedestrian", StringComparison.OrdinalIgnoreCase)
               || value.Contains("blocking roadway", StringComparison.OrdinalIgnoreCase)
               || value.Contains("blocking traffic", StringComparison.OrdinalIgnoreCase)
               || value.Contains("overturned", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanNarrativeText(string value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.', ',', ';', ':');

    private static string ToTitleCase(string value)
    {
        var clean = CleanNarrativeText(value);
        if (clean.Length == 0)
            return "Public safety incident";
        return char.ToUpperInvariant(clean[0]) + clean[1..];
    }

    private static string TrimNarrative(string value, int max)
    {
        var clean = CleanNarrativeText(value);
        return clean.Length <= max ? clean : clean[..Math.Max(0, max - 1)].TrimEnd() + ".";
    }

    private static HashSet<string> TokenSet(string value) =>
        new((value ?? string.Empty).ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length > 0), StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<EvidenceSpanV2> AllHypothesisSpans(IncidentHypothesisV2 hypothesis)
    {
        foreach (var evidence in hypothesis.Events)
        foreach (var span in evidence.Spans)
            yield return span;

        foreach (var evidence in hypothesis.Locations)
        foreach (var span in evidence.Spans)
            yield return span;

        foreach (var evidence in hypothesis.Membership)
        foreach (var span in evidence.Spans)
            yield return span;

        foreach (var evidence in hypothesis.Conflicts)
        foreach (var span in evidence.Spans)
            yield return span;

        foreach (var fact in hypothesis.Narrative.Facts)
        foreach (var span in fact.Spans)
            yield return span;
    }

    private static IEnumerable<EvidenceSpanV2> AllNonConflictHypothesisSpans(IncidentHypothesisV2 hypothesis)
    {
        foreach (var evidence in hypothesis.Events)
        foreach (var span in evidence.Spans)
            yield return span;

        foreach (var evidence in hypothesis.Locations)
        foreach (var span in evidence.Spans)
            yield return span;

        foreach (var evidence in hypothesis.Membership)
        foreach (var span in evidence.Spans)
            yield return span;

        foreach (var fact in hypothesis.Narrative.Facts)
        foreach (var span in fact.Spans)
            yield return span;
    }

    private static bool IsRoutineOrAdministrativePrimaryEvent(EventEvidenceV2 evidence)
    {
        var value = $"{evidence.EventClass} {evidence.EventSubtype}".Trim().ToLowerInvariant();
        if (value.Length == 0)
            return true;
        var sourceText = $"{value} {string.Join(' ', evidence.Spans.Select(span => span.Text))}".ToLowerInvariant();
        if (sourceText.Contains("code 73", StringComparison.OrdinalIgnoreCase)
            || sourceText.Contains("doa", StringComparison.OrdinalIgnoreCase)
            || sourceText.Contains("doi call", StringComparison.OrdinalIgnoreCase))
            return false;

        return sourceText.Contains("routine", StringComparison.OrdinalIgnoreCase)
               || value.Contains("administrative", StringComparison.OrdinalIgnoreCase)
               || value.Contains("logistics", StringComparison.OrdinalIgnoreCase)
               || value.Contains("medical_assist", StringComparison.OrdinalIgnoreCase)
               || value.Contains("ems_assist", StringComparison.OrdinalIgnoreCase)
               || value.Contains("ems assist", StringComparison.OrdinalIgnoreCase)
               || value.Contains("communication_failure", StringComparison.OrdinalIgnoreCase)
               || value.Contains("status", StringComparison.OrdinalIgnoreCase)
               || value.Contains("traffic_stop", StringComparison.OrdinalIgnoreCase)
               || value.Contains("vehicle_stop", StringComparison.OrdinalIgnoreCase)
               || value.Contains("tag_check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("license_check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("warrant_check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("person_check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("subject_check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("subject_location", StringComparison.OrdinalIgnoreCase)
               || value.Contains("subject_identity", StringComparison.OrdinalIgnoreCase)
               || value.Contains("identity_check", StringComparison.OrdinalIgnoreCase)
               || value.Contains("non_emergency", StringComparison.OrdinalIgnoreCase)
               || value.Contains("status_update", StringComparison.OrdinalIgnoreCase)
               || value.Contains("unit_status", StringComparison.OrdinalIgnoreCase)
               || value.Contains("code_status", StringComparison.OrdinalIgnoreCase)
               || IsGenericDispatchOrUpdateWithoutUrgentSignal(value, sourceText)
               || sourceText.Contains("non-emergency", StringComparison.OrdinalIgnoreCase)
               || value.Contains("transport", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericDispatchOrUpdateWithoutUrgentSignal(string eventType, string sourceText) =>
        (eventType.Equals("dispatch dispatch", StringComparison.OrdinalIgnoreCase)
         || eventType.Equals("update update", StringComparison.OrdinalIgnoreCase)
         || eventType.Equals("dispatch update", StringComparison.OrdinalIgnoreCase)
         || eventType.Equals("update dispatch", StringComparison.OrdinalIgnoreCase)
         || eventType.Contains("code", StringComparison.OrdinalIgnoreCase))
        && !ContainsUrgentPublicSafetySignal(sourceText);

    private static bool IsRoutineStandaloneActivity(
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, IncidentEvidenceCallContextV2> callContextsByCallId)
    {
        var contexts = acceptedCallIds
            .Order()
            .Select(id => callContextsByCallId.TryGetValue(id, out var context) ? context : new IncidentEvidenceCallContextV2(id, string.Empty))
            .ToList();
        var text = string.Join(' ', contexts.Select(context => context.Transcript)).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (contexts.All(IsHospitalHandoffOrTransportContext))
            return true;

        if (IsNonEmergencyTransportOrHospitalReport(text)
            || IsTransitOperationsTraffic(text)
            || IsNoiseComplaintWithoutEmergency(text)
            || IsVehicleMeetupWithoutActiveTheftReport(text)
            || IsLiftAssistWithoutParentEmergency(text)
            || IsRoutineTrafficStop(text))
            return true;

        return false;
    }

    private static bool IsRoutineOrTransportOnlyContext(IncidentEvidenceCallContextV2 context) =>
        IsRoutineOrTransportOnlyTranscript(context.Transcript)
        || IsHospitalHandoffOrTransportContext(context);

    private static bool IsHospitalHandoffOrTransportContext(IncidentEvidenceCallContextV2 context)
    {
        var talkgroup = context.TalkgroupName ?? string.Empty;
        var text = context.Transcript ?? string.Empty;
        var contextText = $"{talkgroup} {text}";

        var hospitalOrEncodeTalkgroup =
            talkgroup.Contains("EMS Encode", StringComparison.OrdinalIgnoreCase)
            || talkgroup.Contains("Hospital", StringComparison.OrdinalIgnoreCase)
            || talkgroup.Contains("MedCom", StringComparison.OrdinalIgnoreCase)
            || talkgroup.Contains("MEDCOM", StringComparison.OrdinalIgnoreCase);
        if (!hospitalOrEncodeTalkgroup)
            return false;

        var handoffSignal =
            contextText.Contains("inbound", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("in route", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("en route", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("ETA", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("vital", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("blood pressure", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("questions or orders", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("non-emergent", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("non-emergency", StringComparison.OrdinalIgnoreCase)
            || contextText.Contains("patient", StringComparison.OrdinalIgnoreCase);

        return handoffSignal;
    }

    private static bool IsNonEmergencyTransportOrHospitalReport(string text) =>
        text.Contains("non-emergency", StringComparison.OrdinalIgnoreCase)
        && (text.Contains("facility", StringComparison.OrdinalIgnoreCase)
            || text.Contains("hospital", StringComparison.OrdinalIgnoreCase)
            || text.Contains("transferred", StringComparison.OrdinalIgnoreCase)
            || text.Contains("transfer", StringComparison.OrdinalIgnoreCase)
            || text.Contains("orders", StringComparison.OrdinalIgnoreCase)
            || text.Contains("eta", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ea", StringComparison.OrdinalIgnoreCase));

    private static bool IsTransitOperationsTraffic(string text) =>
        (text.Contains("supervisor", StringComparison.OrdinalIgnoreCase)
         || text.Contains("driver", StringComparison.OrdinalIgnoreCase)
         || text.Contains("schedule", StringComparison.OrdinalIgnoreCase)
         || text.Contains("computer", StringComparison.OrdinalIgnoreCase)
         || text.Contains("same issue", StringComparison.OrdinalIgnoreCase))
        && (text.Contains("not letting me", StringComparison.OrdinalIgnoreCase)
            || text.Contains("switch over", StringComparison.OrdinalIgnoreCase)
            || text.Contains("on time", StringComparison.OrdinalIgnoreCase)
            || text.Contains("same schedule", StringComparison.OrdinalIgnoreCase));

    private static bool IsNoiseComplaintWithoutEmergency(string text) =>
        (text.Contains("music", StringComparison.OrdinalIgnoreCase) || text.Contains("noise", StringComparison.OrdinalIgnoreCase))
        && (text.Contains("refuse", StringComparison.OrdinalIgnoreCase) || text.Contains("information", StringComparison.OrdinalIgnoreCase))
        && !ContainsUrgentPublicSafetySignal(text);

    private static bool IsVehicleMeetupWithoutActiveTheftReport(string text) =>
        (text.Contains("requesting to meet", StringComparison.OrdinalIgnoreCase) || text.Contains("eta", StringComparison.OrdinalIgnoreCase))
        && (text.Contains("stolen", StringComparison.OrdinalIgnoreCase) || text.Contains("taken off", StringComparison.OrdinalIgnoreCase))
        && !text.Contains("just occurred", StringComparison.OrdinalIgnoreCase)
        && !text.Contains("in progress", StringComparison.OrdinalIgnoreCase);

    private static bool IsLiftAssistWithoutParentEmergency(string text) =>
        text.Contains("lift assist", StringComparison.OrdinalIgnoreCase)
        && !HasMedicalEmergencySignal(text)
        && !text.Contains("fall", StringComparison.OrdinalIgnoreCase)
        && !text.Contains("injur", StringComparison.OrdinalIgnoreCase);

    private static bool IsRoutineTrafficStop(string text) =>
        (text.Contains("traffic stop", StringComparison.OrdinalIgnoreCase) || text.Contains("vehicle stop", StringComparison.OrdinalIgnoreCase))
        && !ContainsUrgentPublicSafetySignal(text);

    private static bool ContainsUrgentPublicSafetySignal(string text) =>
        HasMedicalEmergencySignal(text)
        || text.Contains("shots fired", StringComparison.OrdinalIgnoreCase)
        || text.Contains("stabbing", StringComparison.OrdinalIgnoreCase)
        || text.Contains("assault", StringComparison.OrdinalIgnoreCase)
        || text.Contains("crash", StringComparison.OrdinalIgnoreCase)
        || text.Contains("wreck", StringComparison.OrdinalIgnoreCase)
        || text.Contains("fire", StringComparison.OrdinalIgnoreCase)
        || text.Contains("alarm", StringComparison.OrdinalIgnoreCase)
        || text.Contains("weapon", StringComparison.OrdinalIgnoreCase)
        || HasChildLockedVehicleSignal(text);

    private static IReadOnlyList<EventEvidenceV2> DeriveSourceBackedPrimaryEvents(
        IncidentHypothesisV2 hypothesis,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId,
        List<string> reasons)
    {
        var modelPrimaryGroups = hypothesis.Events
            .Where(evidence => IsPrimaryStrength(evidence.Strength))
            .Select(ParentEventGroup)
            .Where(group => group.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (modelPrimaryGroups.Count == 0)
        {
            modelPrimaryGroups = acceptedCallIds
                .Order()
                .Select(callId => transcriptsByCallId.TryGetValue(callId, out var transcript) ? TranscriptParentEventGroup(transcript) : string.Empty)
                .Where(group => group.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        if (modelPrimaryGroups.Count == 0)
            return [];

        var derived = DeriveSourceBackedPrimaryEventsForGroups(modelPrimaryGroups, acceptedCallIds, transcriptsByCallId);
        if (derived.Count == 0)
        {
            var transcriptGroups = acceptedCallIds
                .Order()
                .Select(callId => transcriptsByCallId.TryGetValue(callId, out var transcript) ? TranscriptParentEventGroup(transcript) : string.Empty)
                .Where(group => group.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!modelPrimaryGroups.SequenceEqual(transcriptGroups))
                derived.AddRange(DeriveSourceBackedPrimaryEventsForGroups(transcriptGroups, acceptedCallIds, transcriptsByCallId));
        }

        if (derived.Count > 0)
            reasons.Add("server derived source-backed primary event evidence from accepted transcripts");
        return derived;
    }

    private static List<EventEvidenceV2> DeriveSourceBackedPrimaryEventsForGroups(
        IReadOnlyList<string> groups,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId)
    {
        var derived = new List<EventEvidenceV2>();
        foreach (var group in groups)
        {
            var spans = acceptedCallIds
                .Order()
                .Select(callId => TryFindPrimarySignalSpan(callId, transcriptsByCallId.TryGetValue(callId, out var transcript) ? transcript : string.Empty, group))
                .Where(span => span is not null)
                .Cast<EvidenceSpanV2>()
                .ToList();
            if (spans.Count == 0)
                continue;

            derived.Add(new EventEvidenceV2($"derived_{group}", group, "strong", spans.Select(span => span.CallId).Distinct().Order().ToList(), spans));
        }

        return derived;
    }

    private static EvidenceSpanV2? TryFindPrimarySignalSpan(long callId, string transcript, string group)
    {
        var phrases = group switch
        {
            "medical" => new[] { "heart attack", "bar attack", "another onset", "code 73", "DOA", "DOI call", "possible overdose", "diabetic problem", "diabetic problems", "diabetic emergency", "overdose", "difficulty breathing", "shortness of breath", "respiratory distress", "restrode stress", "possible stroke", "stroke alert", "stroke victim", "slurred speech", "splurge speech", "unresponsive", "unconscious", "not responsive", "not completely responsive", "doesn't respond", "does not respond", "seizure", "seizures", "chest pain", "chest tanks", "severe back pain", "can't move", "cannot move", "not moving", "fell off the bed", "falls to bed", "fell in the floor", "fell into the yard", "hit his head", "hit her head", "bleeding from the nose", "altered mental status" },
            "fire" => new[] { "commercial structure fire", "structure fire", "automatic fire alarm", "fire alarm", "commercial firearm", "response firearm", "firewall", "alarm panel", "on fire", "oxide detection", "carbon monoxide" },
            "rescue" => new[] { "child locked in vehicle", "child locking vehicle", "children in the vehicle", "locked in vehicle", "locking vehicle" },
            "violent_police" => new[] { "shots fired", "shots for our call", "stabbing", "tabbing", "fight call", "possibly armed", "assault", "suicidal", "kill herself", "kill himself", "bolo", "look out", "bellow", "bowload", "domestic disorder", "firearm", "gun" },
            "property_police" => new[] { "stolen firearm", "stolen vehicle", "hit the property", "mailbox", "trash can", "property damage", "hit a pole", "hit the pole", "hit pole", "hit a guardrail", "hit the guardrail", "hit a barrier", "hit a tree", "hit a ditch", "hit a wall", "hit a fence", "hit a building", "stolen", "burglary", "theft" },
            "traffic" => new[] { "motor vehicle accident", "crash", "wreck", "accident", "MVC", "MVA", "NVC", "NVA", "entrapment", "entrapped", "partially ejected", "cracked into a tree", "lanes are blocked", "both lanes", "road hazard", "reckless driver", "driven all over the road", "driven all over the roadway", "couldn't maintain speed", "could not maintain speed", "crossing all lanes of traffic", "crossing lanes of traffic", "walking in traffic", "in the roadway" },
            "animal" => new[] { "aggressive dog", "aggressive dogs", "dogs", "humane" },
            "service_call" => new[] { "911 hang", "911 call", "caller hung up", "hung up" },
            _ => []
        };

        foreach (var phrase in phrases)
        {
            var index = transcript.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;
            if (RequiresWordBoundaries(phrase) && !IsWordBoundedPhraseAt(transcript, index, phrase.Length))
                continue;
            if (string.Equals(phrase, "overdose", StringComparison.OrdinalIgnoreCase) && IsNegatedPhraseAt(transcript, index))
                continue;

            var text = transcript.Substring(index, phrase.Length);
            return new EvidenceSpanV2(callId, index, index + phrase.Length, text);
        }

        return null;
    }

    private static bool RequiresWordBoundaries(string phrase) =>
        string.Equals(phrase, "crash", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phrase, "wreck", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phrase, "accident", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phrase, "MVC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phrase, "MVA", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phrase, "NVC", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phrase, "NVA", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phrase, "gun", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWord(string text, string word)
    {
        var value = text ?? string.Empty;
        var start = 0;
        while (start < value.Length)
        {
            var index = value.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return false;
            if (IsWordBoundedPhraseAt(value, index, word.Length))
                return true;
            start = index + word.Length;
        }

        return false;
    }

    private static bool IsWordBoundedPhraseAt(string text, int index, int length)
    {
        var leftOk = index <= 0 || !char.IsLetterOrDigit(text[index - 1]);
        var right = index + length;
        var rightOk = right >= text.Length || !char.IsLetterOrDigit(text[right]);
        return leftOk && rightOk;
    }

    private static IReadOnlyList<NarrativeFactV2> DeriveNarrativeFactsFromPrimaryEvents(
        IReadOnlyList<EventEvidenceV2> primaryEvents,
        IReadOnlySet<long> acceptedCallIds,
        IReadOnlyDictionary<long, string> transcriptsByCallId) =>
        primaryEvents
            .SelectMany(evidence => evidence.Spans)
            .Where(span => acceptedCallIds.Contains(span.CallId))
            .Where(span => IncidentEvidenceClaimVerifier.IsSpanGrounded(span, transcriptsByCallId))
            .Select(span => new NarrativeFactV2("event", span.Text, [span]))
            .Take(3)
            .ToList();

    private static string DeriveCategory(string eventClass)
    {
        var value = (eventClass ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("medical") || value.Contains("ems"))
            return "ems";
        if (value.Contains("shoot") || value.Contains("shot") || value.Contains("stab") || value.Contains("assault") || value.Contains("weapon") || value.Contains("police"))
            return "police";
        if (value.Contains("traffic") || value.Contains("crash") || value.Contains("road"))
            return "traffic";
        if (value.Contains("rescue") || value.Contains("lockout") || value.Contains("locked_vehicle") || value.Contains("locked vehicle"))
            return "fire";
        if (value.Contains("structure_fire") || value.Contains("fire_alarm") || value.Equals("derived_fire", StringComparison.OrdinalIgnoreCase) || HasFireEventSignal(value) || value.Contains("hazard"))
            return "fire";
        return "other";
    }

    private sealed record LocationClaim(
        string Key,
        string Display,
        IReadOnlyList<long> SourceCallIds,
        IReadOnlyList<EvidenceSpanV2> Spans);

    private sealed record EventClaim(
        string Group,
        IReadOnlyList<long> SourceCallIds,
        IReadOnlyList<EvidenceSpanV2> Spans);

    private sealed record EntityClaim(long CallId, string Key, string Display);

    private sealed record TitleFacts(string EventTitle, string Subject, string Location, string Qualifier);

    private sealed record RecognizedCallEvent(long CallId, string Group, EvidenceSpanV2? Span, IReadOnlyList<string> LocationKeys, string Transcript);

    private sealed record SplitIncidentHypothesis(IncidentHypothesisV2 Hypothesis, string IncidentKey);

    private sealed record IncidentReadiness(bool Ready, IReadOnlyList<string> Reasons);

    private sealed record CallReadinessProfile(long CallId, bool HasPrimarySignal, bool ReadyAnchor, string PendingReason);

    private sealed record AcceptedPrimaryEventCall(
        long CallId,
        string Group,
        string Transcript,
        bool HasTranscriptPrimarySignal,
        bool IsRoutine,
        bool HasTrafficCrashSignal,
        IReadOnlyList<string> LocationKeys,
        HashSet<string> IncidentTokens);

    private sealed record ShadowNarrative(string Title, string Detail);
}
