using System.Text.RegularExpressions;

namespace pizzad;

public sealed record IncidentCandidateCall(
    long CallId,
    long RawTimestamp,
    string Transcript,
    string Category,
    string TalkgroupName,
    string SystemShortName,
    IReadOnlyList<CallAnchorRecord>? Anchors = null,
    bool IncidentEligible = true);

public sealed record IncidentCandidateValidationResult(
    bool IsValid,
    string Reason,
    IReadOnlyList<IncidentCandidateCall> Calls);

public sealed record IncidentEventValidationInput(
    string EventClass,
    bool LogisticsOnly,
    IReadOnlyList<string> ParentEventEvidence,
    string EventLocationText,
    string EventLocationEvidenceText);

public static class IncidentCandidateValidator
{
    private static readonly TimeSpan MaxIncidentSpan = TimeSpan.FromMinutes(60);
    private static readonly HashSet<string> ConcreteLocationAnchorKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "address",
        "intersection",
        "highway_mile_marker",
        "location"
    };

    private static readonly HashSet<string> StrongSingleCallAnchorKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "highway_mile_marker",
        "vehicle_tag"
    };

    private static readonly Regex NonIncidentPattern = new(
        @"\b(no|none|not|without|unclear)\b.{0,40}\b(incident|event|actionable|emergency|issue|activity)\b|\b(no event detected|no clear incident|no actionable incident|no notable event|nothing notable|routine traffic only|routine chatter|non.?incident)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenericBucketPattern = new(
        @"\b(unit status|status updates?|dispatch coordination|dispatch activity|dispatching|administrative|location updates?|call updates?|call assignment|routine checks?|status checks?|routine|coordination)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AddressPattern = new(
        @"\b\d{2,6}\s+(?:[a-z0-9]+\s+){0,5}(?:road|rd|street|st|drive|dr|avenue|ave|lane|ln|pike|highway|hwy|place|pl|circle|cir|trail|way|court|ct|parkway|pkwy|boulevard|blvd|terrace|ter|loop)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RoadPattern = new(
        @"\b(?:[a-z0-9]+\s+){1,4}(?:road|rd|street|st|drive|dr|avenue|ave|lane|ln|pike|highway|hwy|place|pl|circle|cir|trail|way|court|ct|parkway|pkwy|boulevard|blvd|terrace|ter|loop)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HighwayPattern = new(
        @"\b(?:i[-\s]?\d{1,3}|interstate\s+\d{1,3}|exit\s+\d{1,3}|mile\s*marker\s*\d{1,3}|mm\s*\d{1,3})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NamedLandmarkPattern = new(
        @"\b(?<name>(?:[a-z][a-z0-9'-]+\s+){1,4}(?:nursery|home\s+store|store|market|church|school|hospital|clinic|mall|restaurant|station|airport|bridge|park|cemetery|apartments?))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RoutineStatusPattern = new(
        @"\b(?:in compliance|compliant|contact made|made contact|no contact|unable to contact|check(?:ing)? (?:in|out)|10[- ]4|copy|clear|available|en route|on scene|arrived|transporting|at station|back in service|unit status|status check|go ahead)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenericTrafficControlPattern = new(
        @"\b(?:traffic control|traffic detail|traffic assistance|take (?:it|that) on the street|on the street)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NoisyEvidencePattern = new(
        @"\[(?:inaudible|unintelligible)\]|\b(?:no further|thank you no further|disregard|delete the loan|go today|tennessee flight)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex OperationalChatterPattern = new(
        @"\b(?:maintenance|engineering|facilit(?:y|ies)|parking|valet|shuttle|administrat(?:ion|ive)|housekeeping|routine service|work order|repair|inspection)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StrongIncidentAnchorPattern = new(
        @"\b(?:crash|accident|mvc|mva|collision|rear[- ]?end(?:ed|ing)?|injur(?:y|ies|ed)|unconscious|unresponsive|not\s+(?:completely\s+)?responsive|bleeding|burns?|cpr|stroke|chest pains?|difficulty breathing|shortness of breath|respiratory distress|diabetic|choking|clammy|weakness|(?:feels?|feeling|felt)\s+(?:\w+\s+){0,2}weak|real weak|heart (?:issues?|problems?|(?:rate\s+)?(?:is\s+)?(?:racing|fast|high|elevated))|(?:fast|high|elevated)\s+heart\s+rate|palpitations?|(?:feels?|feeling|felt)\s+like\s+(?:it(?:'s| is)|his|her|their|my)?\s*(?:heart\s+)?(?:is\s+)?racing|altered mental status|diaphoretic|fire|smoke|carbon monoxide|c\.?o\.?\s+alarm|rescue|assault|batter(?:y|ed|ing)|beat(?:en|ing)|fight(?:ing)?|harass(?:ment|ing)?|threats?|shooting|shots?\s+fired|shot\s+(?:out|at)|stab(?:bing|bed)?|weapon|gun|domestic(?:\s+(?:unknown|disturbance|dispute|violence))?|home invasion|burglary|robbery|vehicle (?:was )?burglarized|stole(?:n)?|theft|pursuit|missing child|welfare check|well[- ]?being check|check welfare|cardiac|overdose|fentanyl|seizure|fall|pinned|trapped|entrapment|hazmat|road(?:way)? hazard|disabled vehicle|tree down|blocking (?:the )?(?:road(?:way)?|lane))\b|\b(?:vehicle|car|truck|tractor trailer|tanker|trailer)\b.{0,45}\b(?:park(?:ed|ing)|stopped|disabled)\b.{0,45}\b(?:middle of (?:the )?road|in (?:the )?road(?:way)?|blocking)\b|\b(?:vehicle|car|truck|tractor trailer|tanker|trailer)\b.{0,30}\b(?:versus|vs\.?)\b.{0,30}\b(?:guardrail|barrier|pole|tree|ditch|wall|fence)\b|\bhit\s+(?:(?:a|an|the|their|his|her|our|your|my|its)\s+)?(?:pole|tree|guardrail|barrier|building|wall|fence|ditch|windshield)\b|10[- ](?:32|33|49|50|51|52|97)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BrokenDownVehicleHazardPattern = new(
        @"\b(?:broken down vehicle|(?:vehicle|car|truck|tractor trailer|tanker|trailer)\b.{0,45}\bbroken down|broken down\b.{0,80}\b(?:tow|tour|bad spot|road|roadway|hill|county line))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CorroboratedEmergencySignalPattern = new(
        @"\b(?:crash|accident|mvc|mva|collision|rear[- ]?end(?:ed|ing)?|injur(?:y|ies|ed)|unconscious|unresponsive|not\s+(?:completely\s+)?responsive|bleeding|burns?|cpr|stroke|chest pains?|difficulty breathing|shortness of breath|respiratory distress|diabetic|choking|clammy|weakness|(?:feels?|feeling|felt)\s+(?:\w+\s+){0,2}weak|real weak|heart (?:issues?|problems?|(?:rate\s+)?(?:is\s+)?(?:racing|fast|high|elevated))|(?:fast|high|elevated)\s+heart\s+rate|palpitations?|(?:feels?|feeling|felt)\s+like\s+(?:it(?:'s| is)|his|her|their|my)?\s*(?:heart\s+)?(?:is\s+)?racing|altered mental status|diaphoretic|(?:brush|structure|house|apartment|vehicle|car|truck|large)\s+fire|fire alarm|carbon monoxide|c\.?o\.?\s+alarm|smoke|flames?|rescue|assault|batter(?:y|ed|ing)|beat(?:en|ing)|fight(?:ing)?|harass(?:ment|ing)?|threats?|shooting|shots?\s+fired|shot\s+(?:out|at)|stab(?:bing|bed)?|weapon|gun|domestic(?:\s+(?:unknown|disturbance|dispute|violence))?|home invasion|burglary|robbery|vehicle (?:was )?burglarized|stole(?:n)?|theft|pursuit|missing child|welfare check|well[- ]?being check|check welfare|cardiac|overdose|fentanyl|seizure|fall|pinned|trapped|entrapment|hazmat|road(?:way)? hazard|disabled vehicle|tree down|blocking (?:the )?(?:road(?:way)?|lane))\b|\b(?:vehicle|car|truck|tractor trailer|tanker|trailer)\b.{0,45}\b(?:park(?:ed|ing)|stopped|disabled)\b.{0,45}\b(?:middle of (?:the )?road|in (?:the )?road(?:way)?|blocking)\b|\b(?:vehicle|car|truck|tractor trailer|tanker|trailer)\b.{0,30}\b(?:versus|vs\.?)\b.{0,30}\b(?:guardrail|barrier|pole|tree|ditch|wall|fence)\b|\bhit\s+(?:(?:a|an|the|their|his|her|our|your|my|its)\s+)?(?:pole|tree|guardrail|barrier|building|wall|fence|ditch|windshield)\b|10[- ](?:32|33|49|50|51|52|97)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RadioTestPattern = new(
        @"\b(?:radio|tone|pager|station|dispatch|fire)?\s*test(?:ing)?\b|\bconducting a test\b|\bthis is (?:a )?test\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StandaloneTransportPattern = new(
        @"\b(?:transport(?:ing|ed)?|inbound|en route|non[- ]?emergency|eta|patient report|vital signs?|bp|blood pressure|pulse|oxygen|hospital|medical center|emergency room|er)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TransportParentEventReferencePattern = new(
        @"\b(?:from|at|reference|regarding|related to)\b.{0,80}\b(?:crash|accident|mvc|mva|collision|fire|overdose|stabb(?:ing|ed)|shooting|fall|seizure|stroke|cardiac|unconscious|unresponsive|welfare check)\b|\b(?:crash|accident|mvc|mva|collision|fire|overdose|stabb(?:ing|ed)|shooting|fall|seizure|stroke|cardiac|unconscious|unresponsive|welfare check)\b.{0,80}\b(?:transport(?:ing|ed)?|inbound|patient report|hospital|medical center|emergency room|er)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ResponseContinuationPattern = new(
        @"\b(?:shut(?:ting)?(?:\s+\w+){0,4}\s+down|traffic|divert(?:ing)?|mutual aid|on scene|incoming units?|respond(?:ing)?|en route|command|tac|fire ?ground|transport(?:ing)?|patient|burns?|thp|highway patrol|eod)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InjuryContinuationPattern = new(
        @"\b(?:patient|injur(?:y|ies|ed)|pain|bleeding|bleed(?:ing)?|laceration|leg|hand|arm|head|neck|back|checked|evaluat(?:e|ed|ion)|ems|medic|ambulance)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex RoadContextPattern = new(
        @"\b(?:i[-\s]?\d{1,3}|interstate\s+\d{1,3}|interstate|highway\s+\d{1,3}|hwy\s+\d{1,3}|us\s+highway\s+\d{1,3}|exit\s+\d{1,3}|lee\s+highway|northbound|southbound)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BoloPattern = new(
        @"\b(?:bolo|be on (?:the )?lookout|look ?out|attempt to locate|atl|wanted|suspect vehicle)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex StandbyOnlyPattern = new(
        @"\b(?:standby|stand by|cover station|station coverage)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsNotableText(string title, string detail, int callCount)
    {
        var combined = NormalizeText($"{title} {detail}");
        return !string.IsNullOrWhiteSpace(combined)
               && callCount > 0
               && !NonIncidentPattern.IsMatch(combined);
    }

    public static bool IsActionableText(string title, string detail, int callCount) =>
        callCount > 0 && IsNotableText(title, detail, callCount);

    public static IncidentCandidateValidationResult ValidateEvent(IncidentEventValidationInput input, IReadOnlyList<IncidentCandidateCall> calls)
    {
        if (calls.Count == 0)
            return new(false, "fewer than 1 resolved call", []);

        var first = calls.Min(c => c.RawTimestamp);
        var last = calls.Max(c => c.RawTimestamp);
        var span = TimeSpan.FromSeconds(Math.Max(0, last - first));
        if (span > MaxIncidentSpan)
            return new(false, $"call span {span.TotalMinutes:0.#}m exceeds {MaxIncidentSpan.TotalMinutes:0.#}m", []);

        var eventText = BuildEvidenceEventText(input, calls);
        var eventLocations = ExtractConcreteLocationAnchors($"{input.EventLocationText} {input.EventLocationEvidenceText}");
        var rows = calls
            .Select(c => new CandidateRow(
                c,
                ExtractTokens(CallText(c)),
                ExtractAnchors(c.Transcript, c.Anchors),
                ExtractConcreteLocationAnchors(c.Transcript, c.Anchors)))
            .ToList();

        if (LooksLikeExplicitRadioTest(calls))
            return new(false, "explicit radio/test traffic is not an incident", []);

        if (IsLogisticsOrRoutineOnly(input) && !HasParentEmergencyEvidence(input) && !HasStrongIncidentAnchor(eventText, calls))
            return new(false, $"{NormalizeStructuredToken(input.EventClass)} lacks parent emergency/event evidence", []);

        if (calls.Any(c => !c.IncidentEligible) && !HasStrongIncidentAnchor(eventText, calls))
            return new(false, "non-incident operational talkgroup lacks a strong emergency/event anchor", []);

        if (LooksLikeOperationalChatter(eventText, calls) && !HasStrongIncidentAnchor(eventText, calls))
            return new(false, "operational chatter lacks a strong emergency/event anchor", []);

        if (LooksLikeStandaloneTransport(eventText, calls) && !HasStrongIncidentAnchor(eventText, calls))
            return new(false, "standalone transport/hospital handoff lacks a parent emergency/event anchor", []);

        if (LooksLikeStandbyOnlyIncident(eventText, calls))
            return new(false, "standby-only dispatch is not an incident", []);

        if (LooksLikeGenericTrafficControlWithoutEventAnchor(eventText, rows))
            return new(false, "generic traffic-control/status chatter lacks a concrete event anchor", []);

        if (calls.Count == 1)
            return ValidateSingleCallIncident(eventText, rows[0]);

        var transportRecoveredSingle = TryRecoverFromUnanchoredTransportHandoffCandidate(
            rows,
            row => BuildEvidenceEventText(input, [row.Call]),
            "transport/hospital handoff cluster lacks a shared parent emergency/event anchor");
        if (transportRecoveredSingle != null)
            return transportRecoveredSingle;

        if (IsRoutineStatusRollup(eventText, rows))
        {
            var recoveredSingle = TryRecoverSingleCallIncidentAfterPruning(
                rows,
                row => BuildEvidenceEventText(input, [row.Call]),
                "routine status/compliance rollup lacks a shared concrete event anchor");
            if (recoveredSingle != null)
                return recoveredSingle;
            return new(false, "routine status/compliance rollup lacks a shared concrete event anchor", []);
        }

        var multi = ValidateRetainedMultiCallEvent(eventText, eventLocations, rows);
        if (multi.IsValid)
            return multi;

        var singles = rows
            .Select(row => ValidateSingleCallIncident(BuildEvidenceEventText(input, [row.Call]), row))
            .Where(result => result.IsValid)
            .SelectMany(result => result.Calls)
            .DistinctBy(call => call.CallId)
            .ToList();
        if (singles.Count == 1)
            return new(true, $"single-call actionable event after pruning unrelated candidate calls ({multi.Reason})", singles);

        return multi;
    }

    public static bool IsLowConfidenceEventAcceptable(IncidentEventValidationInput input, IReadOnlyList<IncidentCandidateCall> calls, double confidence)
    {
        if (confidence >= 0.65)
            return true;
        return ValidateEvent(input, calls).IsValid;
    }

    public static IncidentCandidateValidationResult Validate(string title, string detail, IReadOnlyList<IncidentCandidateCall> calls)
    {
        if (calls.Count == 0)
            return new(false, "fewer than 1 resolved call", []);

        var first = calls.Min(c => c.RawTimestamp);
        var last = calls.Max(c => c.RawTimestamp);
        var span = TimeSpan.FromSeconds(Math.Max(0, last - first));
        if (span > MaxIncidentSpan)
            return new(false, $"call span {span.TotalMinutes:0.#}m exceeds {MaxIncidentSpan.TotalMinutes:0.#}m", []);

        var eventText = $"{title} {detail}";
        var eventTokens = ExtractTokens(eventText);
        var eventAnchors = ExtractAnchors(eventText);
        var eventLocations = ExtractConcreteLocationAnchors(eventText);
        var rows = calls
            .Select(c => new CandidateRow(
                c,
                ExtractTokens(CallText(c)),
                ExtractAnchors(c.Transcript, c.Anchors),
                ExtractConcreteLocationAnchors(c.Transcript, c.Anchors)))
            .ToList();

        if (calls.Any(c => !c.IncidentEligible) && !HasStrongIncidentAnchor(eventText, calls))
            return new(false, "non-incident operational talkgroup lacks a strong emergency/event anchor", []);

        if (LooksLikeOperationalChatter(eventText, calls) && !HasStrongIncidentAnchor(eventText, calls))
            return new(false, "operational chatter lacks a strong emergency/event anchor", []);

        if (LooksLikeStandaloneTransport(eventText, calls) && !HasStrongIncidentAnchor(eventText, calls))
            return new(false, "standalone transport/hospital handoff lacks a parent emergency/event anchor", []);

        if (GenericBucketPattern.IsMatch(title))
            return new(false, "generic routine/status title is not an incident", []);

        if (TitleIntroducesUnsupportedStrongSignal(eventText, calls))
            return new(false, "incident title/detail introduces unsupported emergency/event signal", []);

        if (LooksLikeStandbyOnlyIncident(eventText, calls))
            return new(false, "standby-only dispatch is not an incident", []);

        if (LooksLikeGenericTrafficControlWithoutEventAnchor(eventText, rows))
            return new(false, "generic traffic-control/status chatter lacks a concrete event anchor", []);

        if (calls.Count == 1)
            return ValidateSingleCallIncident(eventText, rows[0]);

        var transportRecoveredSingle = TryRecoverFromUnanchoredTransportHandoffCandidate(
            rows,
            row => $"{title} {detail} {CallText(row.Call)}",
            "transport/hospital handoff cluster lacks a shared parent emergency/event anchor");
        if (transportRecoveredSingle != null)
            return transportRecoveredSingle;

        if (IsRoutineStatusRollup(eventText, rows))
        {
            var recoveredSingle = TryRecoverSingleCallIncidentAfterPruning(
                rows,
                row => $"{title} {detail} {CallText(row.Call)}",
                "routine status/compliance rollup lacks a shared concrete event anchor");
            if (recoveredSingle != null)
                return recoveredSingle;
            return new(false, "routine status/compliance rollup lacks a shared concrete event anchor", []);
        }

        if (eventAnchors.Count > 0)
        {
            var anchoredRows = rows.Where(row => SharedAnchorScore(eventAnchors, row.Anchors) > 0).ToList();
            if (anchoredRows.Count == 1)
            {
                var expandedAnchoredRows = RetainResponseContinuationEvidence(eventText, rows, anchoredRows, allowSingleSeed: true);
                if (expandedAnchoredRows.Count >= 2)
                    return ValidateRetainedMultiCallEvent(eventText, eventLocations, expandedAnchoredRows);

                return ValidateSingleCandidateAfterPruning(eventText, anchoredRows[0], "only 1 call supports the incident title/detail anchor");
            }
        }

        var retained = rows
            .Where(row => CallMatchesEvent(row, eventTokens, eventAnchors))
            .ToList();

        if (LooksLikeBolo(eventText))
            retained = RetainConcreteBoloEvidence(retained, eventAnchors);

        retained = RetainResponseContinuationEvidence(eventText, rows, retained);

        if (retained.Count < 2)
        {
            if (retained.Count == 1)
                return ValidateSingleCandidateAfterPruning(eventText, retained[0], "only 1 call(s) match the incident title/detail");
            return new(false, $"only {retained.Count} call(s) match the incident title/detail", retained.Select(r => r.Call).ToList());
        }

        return ValidateRetainedMultiCallEvent(eventText, eventLocations, retained);
    }

    private static IncidentCandidateValidationResult ValidateRetainedMultiCallEvent(string eventText, HashSet<string> eventLocations, List<CandidateRow> retained)
    {
        retained = retained
            .Where(row => HasCorroboratingCall(row, retained))
            .ToList();

        if (retained.Count < 2)
            return new(false, $"only {retained.Count} call(s) have corroborating related calls", retained.Select(r => r.Call).ToList());

        var locationResolution = ResolveConcreteLocationConflicts(eventLocations, retained);
        if (!locationResolution.IsValid)
            return new(false, locationResolution.Reason, locationResolution.Rows.Select(r => r.Call).ToList());
        retained = locationResolution.Rows;

        if (retained.Count < 2)
            return new(false, "concrete location conflict left fewer than 2 related calls", retained.Select(r => r.Call).ToList());

        if (retained.Count == 2)
        {
            var pair = PairScore(retained[0], retained[1]);
            if (pair < 0.20 && !HasCloseSameTalkgroupEmergencyContext(retained[0], retained[1]))
                return new(false, $"two-call incident pair similarity {pair:0.00} is below 0.20", []);
        }

        return new(true, "multi-call actionable event with concrete anchors or corroborating text", retained.Select(r => r.Call).ToList());
    }

    private static IncidentCandidateValidationResult ValidateSingleCandidateAfterPruning(string eventText, CandidateRow row, string originalReason)
    {
        var single = ValidateSingleCallIncident(eventText, row);
        return single.IsValid
            ? new(true, $"single-call actionable event after pruning unrelated candidate calls ({originalReason})", single.Calls)
            : new(false, originalReason, [row.Call]);
    }

    private static IncidentCandidateValidationResult ValidateSingleCallIncident(string eventText, CandidateRow row)
    {
        var hasStrongIncidentAnchor = HasStrongIncidentAnchor(eventText, [row.Call]);
        var hasConcreteAnchorOrLocationHint = HasStrongSingleCallAnchor(eventText, row)
                                            || HasWeakLocationHint(row)
                                            || HasNamedLandmarkHint(row)
                                            || HasTrafficCrashRoadwayLocationHint(eventText, row.Call.Transcript);

        if (IsNoisyEvidenceText(row.Call.Transcript))
            return new(false, "single-call incident source is noisy or routine", []);

        if (IsRoutineEvidenceTextWithoutAnchors(row.Call.Transcript) && !(hasStrongIncidentAnchor && hasConcreteAnchorOrLocationHint))
            return new(false, "single-call incident source is noisy or routine", []);

        if (!hasStrongIncidentAnchor)
            return new(false, "single-call incident lacks a strong emergency/event signal", []);

        if (!hasConcreteAnchorOrLocationHint)
            return new(false, "single-call incident lacks a concrete structured anchor or location hint", []);

        if (LooksLikeStandaloneTransport(eventText, [row.Call]))
            return new(false, "single-call transport/hospital handoff lacks a parent emergency/event anchor", []);

        return new(true, "single-call actionable event with concrete anchor", [row.Call]);
    }

    private static bool HasStrongSingleCallAnchor(string eventText, CandidateRow row)
    {
        if (row.Call.Anchors?.Any(IsStrongSingleCallAnchor) == true)
            return true;

        var textAnchors = CallAnchorExtractionService.ExtractTranscriptAnchors(0, $"{eventText} {row.Call.Transcript}");
        return textAnchors.Any(IsStrongSingleCallAnchor);
    }

    private static bool HasWeakLocationHint(CandidateRow row)
    {
        if (row.ConcreteLocationAnchors.Count > 0)
            return true;
        return CallAnchorExtractionService.ExtractTranscriptAnchors(0, row.Call.Transcript)
            .Any(IsConcreteLocationAnchor);
    }

    private static bool HasNamedLandmarkHint(CandidateRow row) =>
        ExtractNamedLandmarkKeys(CallText(row.Call), row.Anchors).Count > 0;

    private static bool HasTrafficCrashRoadwayLocationHint(string eventText, string transcript)
    {
        var combined = $"{eventText} {transcript}";
        if (!Regex.IsMatch(
                combined,
                @"\b(?:mvc|mva|motor vehicle accident|vehicle accident|crash|collision|rear[- ]?end(?:ed|ing)?|accident\s+with\s+injur(?:y|ies)|possible\s+injur(?:y|ies))\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        var normalized = Regex.Replace(combined.ToLowerInvariant(), @"[^a-z0-9]+", " ");
        if (Regex.IsMatch(normalized, @"\b(?:i|interstate|highway|hwy|sr|state route|us)\s+\d{1,3}\b", RegexOptions.CultureInvariant))
            return true;

        if (Regex.IsMatch(normalized, @"\b\d{1,3}\s+(?:northbound|southbound|eastbound|westbound|nb|sb|eb|wb)\b", RegexOptions.CultureInvariant))
            return true;

        return Regex.IsMatch(normalized, @"\b\d{1,3}\s+(?:north|south|east|west)\b", RegexOptions.CultureInvariant)
               && Regex.IsMatch(normalized, @"\b(?:exit|ramp|mile|marker|lane|shoulder|median|interstate|highway|hwy|vehicle|motor vehicle)\b", RegexOptions.CultureInvariant);
    }

    private static bool IsStrongSingleCallAnchor(CallAnchorRecord anchor) =>
        StrongSingleCallAnchorKinds.Contains(anchor.Kind)
        && !string.IsNullOrWhiteSpace(anchor.Value)
        && !string.Equals(anchor.Source, "deterministic_weak", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(anchor.Source, "location_hint", StringComparison.OrdinalIgnoreCase);

    private static string BuildEvidenceEventText(IncidentEventValidationInput input, IReadOnlyList<IncidentCandidateCall> calls)
    {
        var parts = new List<string>
        {
            input.EventClass,
            input.EventLocationText,
            input.EventLocationEvidenceText
        };
        parts.AddRange(input.ParentEventEvidence ?? []);
        parts.AddRange(calls.Select(CallText));
        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool IsLogisticsOrRoutineOnly(IncidentEventValidationInput input)
    {
        var eventClass = NormalizeStructuredToken(input.EventClass);
        return input.LogisticsOnly || eventClass is "medical_transport_context" or "administrative_or_logistics" or "routine_status";
    }

    private static bool HasParentEmergencyEvidence(IncidentEventValidationInput input) =>
        input.ParentEventEvidence?.Any(value => !string.IsNullOrWhiteSpace(value)) == true;

    private static bool LooksLikeExplicitRadioTest(IReadOnlyList<IncidentCandidateCall> calls)
    {
        if (calls.Count == 0)
            return false;
        var testCalls = calls.Count(call => RadioTestPattern.IsMatch(call.Transcript ?? string.Empty));
        if (testCalls < Math.Max(1, calls.Count / 2))
            return false;
        return !calls.Any(call => HasStrongIncidentAnchorText(call.Transcript));
    }

    private static string NormalizeStructuredToken(string value) =>
        Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');

    private static LocationConflictResult ResolveConcreteLocationConflicts(HashSet<string> eventLocations, List<CandidateRow> rows)
    {
        var located = rows.Where(row => row.ConcreteLocationAnchors.Count > 0).ToList();
        if (located.Count < 2)
            return new(true, string.Empty, rows);

        if (eventLocations.Count > 0)
        {
            var matchingEventLocation = located
                .Where(row => SharesConcreteLocation(eventLocations, row.ConcreteLocationAnchors))
                .ToList();
            var conflictingEventLocation = located
                .Where(row => !SharesConcreteLocation(eventLocations, row.ConcreteLocationAnchors) && !IsResponseManagementLocation(eventLocations, row))
                .ToList();

            var bridgeableEventLocationConflicts = conflictingEventLocation
                .Where(conflict => matchingEventLocation.Any(match => CanBridgeNoisyConcreteLocationConflict(match, conflict)))
                .ToList();
            var hardEventLocationConflicts = conflictingEventLocation
                .Where(conflict => !bridgeableEventLocationConflicts.Any(bridgeable => bridgeable.Call.CallId == conflict.Call.CallId))
                .ToList();

            if (matchingEventLocation.Count >= 2 && hardEventLocationConflicts.Count > 0)
            {
                var retained = rows
                    .Where(row => row.ConcreteLocationAnchors.Count == 0
                                  || SharesConcreteLocation(eventLocations, row.ConcreteLocationAnchors)
                                  || IsResponseManagementLocation(eventLocations, row)
                                  || bridgeableEventLocationConflicts.Any(bridgeable => bridgeable.Call.CallId == row.Call.CallId))
                    .ToList();
                return new(retained.Count >= 2, "concrete incident location conflicts with one or more source-call locations", retained);
            }

            if (matchingEventLocation.Count == 1 && hardEventLocationConflicts.Count == 0 && bridgeableEventLocationConflicts.Count > 0)
                return new(true, "concrete incident location conflict bridged by shared landmark and event evidence", rows);

            if (matchingEventLocation.Count == 1 && hardEventLocationConflicts.Count > 0)
                return new(false, "only 1 call supports the concrete incident location while other calls cite different locations", matchingEventLocation);

            located = located
                .Where(row => !IsResponseManagementLocation(eventLocations, row))
                .ToList();
            if (located.Count < 2)
                return new(true, string.Empty, rows);
        }

        var bestLocationGroup = located
            .SelectMany(row => row.ConcreteLocationAnchors.Select(anchor => new { Anchor = anchor, Row = row }))
            .GroupBy(item => item.Anchor, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Anchor = group.Key,
                Rows = group.Select(item => item.Row).DistinctBy(row => row.Call.CallId).ToList()
            })
            .OrderByDescending(group => group.Rows.Count)
            .FirstOrDefault();

        if (bestLocationGroup is { Rows.Count: >= 2 })
        {
            var retained = rows
                .Where(row => row.ConcreteLocationAnchors.Count == 0 || row.ConcreteLocationAnchors.Contains(bestLocationGroup.Anchor))
                .ToList();
            return new(retained.Count >= 2, "conflicting concrete source-call locations have no shared event anchor", retained);
        }

        for (var i = 0; i < located.Count; i++)
        {
            for (var j = i + 1; j < located.Count; j++)
            {
                if (!SharesConcreteLocation(located[i].ConcreteLocationAnchors, located[j].ConcreteLocationAnchors)
                    && !CanBridgeNoisyConcreteLocationConflict(located[i], located[j]))
                    return new(false, "source calls cite conflicting concrete locations without a shared location anchor", []);
            }
        }

        return new(true, string.Empty, rows);
    }

    private static bool IsResponseManagementLocation(HashSet<string> eventLocations, CandidateRow row)
    {
        if (!eventLocations.Any(location => location.StartsWith("highway_mile_marker:", StringComparison.OrdinalIgnoreCase)))
            return false;
        var text = CallText(row.Call);
        return ResponseContinuationPattern.IsMatch(text)
               && Regex.IsMatch(text, @"\b(?:traffic|divert(?:ing)?|exit|ramp|lee\s+highway|mutual aid|shut(?:ting)? down)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool CanBridgeNoisyConcreteLocationConflict(CandidateRow left, CandidateRow right) =>
        ShareNamedLandmark(left, right) && ShareSpecificEventConcept(left, right);

    private static bool ShareNamedLandmark(CandidateRow left, CandidateRow right)
    {
        var leftLandmarks = ExtractNamedLandmarkKeys(CallText(left.Call), left.Anchors);
        if (leftLandmarks.Count == 0)
            return false;
        var rightLandmarks = ExtractNamedLandmarkKeys(CallText(right.Call), right.Anchors);
        return rightLandmarks.Count > 0 && leftLandmarks.Overlaps(rightLandmarks);
    }

    private static HashSet<string> ExtractNamedLandmarkKeys(string text, HashSet<string> anchors)
    {
        var landmarks = anchors
            .Where(anchor => anchor.StartsWith("business_or_landmark:", StringComparison.OrdinalIgnoreCase))
            .Select(anchor => anchor["business_or_landmark:".Length..])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in NamedLandmarkPattern.Matches(text ?? string.Empty))
        {
            var key = TranscriptLocationService.NormalizeLocationKey(match.Groups["name"].Value);
            if (IsSpecificNamedLandmark(key))
                landmarks.Add(key);
        }

        return landmarks;
    }

    private static bool IsSpecificNamedLandmark(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var tokens = Regex.Split(key, @"[^a-z0-9]+").Where(token => token.Length > 0).ToArray();
        if (tokens.Length < 2)
            return false;

        return tokens[0] is not ("a" or "an" or "the" or "this" or "that" or "local" or "county" or "city");
    }

    private static bool ShareSpecificEventConcept(CandidateRow left, CandidateRow right)
    {
        var leftConcepts = SpecificEventConcepts(CallText(left.Call));
        if (leftConcepts.Count == 0)
            return false;
        var rightConcepts = SpecificEventConcepts(CallText(right.Call));
        return rightConcepts.Count > 0 && leftConcepts.Overlaps(rightConcepts);
    }

    private static HashSet<string> SpecificEventConcepts(string text)
    {
        return new HashSet<string>(
            IncidentEvidenceClassifier.Analyze(text).Concepts,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IncidentCandidateValidationResult? TryRecoverSingleCallIncidentAfterPruning(
        IReadOnlyList<CandidateRow> rows,
        Func<CandidateRow, string> eventTextForRow,
        string originalReason)
    {
        var singles = rows
            .Select(row => ValidateSingleCallIncident(eventTextForRow(row), row))
            .Where(result => result.IsValid)
            .SelectMany(result => result.Calls)
            .DistinctBy(call => call.CallId)
            .ToList();

        return singles.Count == 1
            ? new IncidentCandidateValidationResult(true, $"single-call actionable event after pruning routine/status calls ({originalReason})", singles)
            : null;
    }

    private static bool CallMatchesEvent(CandidateRow row, HashSet<string> eventTokens, HashSet<string> eventAnchors)
    {
        var anchorScore = SharedAnchorScore(eventAnchors, row.Anchors);
        if (eventAnchors.Count > 0 && anchorScore > 0)
            return true;

        var textScore = Similarity(eventTokens, row.Tokens);
        if (eventAnchors.Count > 0)
            return textScore >= 0.16;

        return textScore >= 0.24;
    }

    private static bool HasCorroboratingCall(CandidateRow row, IReadOnlyList<CandidateRow> rows)
    {
        foreach (var other in rows)
        {
            if (other.Call.CallId == row.Call.CallId)
                continue;
            if (PairScore(row, other) >= 0.12)
                return true;
            if (HasCloseSameTalkgroupEmergencyContext(row, other))
                return true;
        }
        return false;
    }

    private static bool HasCloseSameTalkgroupEmergencyContext(CandidateRow a, CandidateRow b)
    {
        if (!string.Equals(a.Call.SystemShortName, b.Call.SystemShortName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(a.Call.TalkgroupName, b.Call.TalkgroupName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Math.Abs(a.Call.RawTimestamp - b.Call.RawTimestamp) > 10 * 60)
            return false;

        var aText = CallText(a.Call);
        var bText = CallText(b.Call);
        var oneHasEvent = HasStrongIncidentAnchorText(aText) || HasStrongIncidentAnchorText(bText);
        if (!oneHasEvent)
            return false;

        return HasPatientHospitalContext(aText) && HasPatientHospitalContext(bText);
    }

    private static bool HasPatientHospitalContext(string text) =>
        Regex.IsMatch(
            text ?? string.Empty,
            @"\b(?:patient|subject|victim)\b.{0,90}\b(?:er|emergency room|hospital|medical center|common spirit|erlanger|memorial|bed\s+\d+)\b|\b(?:er|emergency room|hospital|medical center|common spirit|erlanger|memorial|bed\s+\d+)\b.{0,90}\b(?:patient|subject|victim)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static List<CandidateRow> RetainResponseContinuationEvidence(string eventText, IReadOnlyList<CandidateRow> rows, List<CandidateRow> retained, bool allowSingleSeed = false)
    {
        if ((!allowSingleSeed && retained.Count < 2) || retained.Count == 0 || !HasStrongIncidentAnchor(eventText, retained.Select(row => row.Call).ToList()))
            return retained;

        var retainedById = retained.Select(row => row.Call.CallId).ToHashSet();
        var expanded = retained.ToDictionary(row => row.Call.CallId);
        foreach (var row in rows)
        {
            if (retainedById.Contains(row.Call.CallId))
                continue;
            var linked = expanded.Values.Any(existing => HasResponseContinuationLink(eventText, row, existing));
            if (linked && (ResponseContinuationPattern.IsMatch(CallText(row.Call)) || IsDispatchLocationOnlyContinuation(row, expanded.Values) || IsInjuryOutcomeContinuation(eventText, row)))
                expanded[row.Call.CallId] = row;
        }

        return expanded.Values.OrderBy(row => row.Call.RawTimestamp).ToList();
    }

    private static bool HasResponseContinuationLink(string eventText, CandidateRow row, CandidateRow existing)
    {
        var minutes = Math.Abs(row.Call.RawTimestamp - existing.Call.RawTimestamp) / 60d;
        if (minutes > 45)
            return false;

        if (PairScore(row, existing) >= 0.12)
            return true;

        var rowText = CallText(row.Call);
        var existingText = CallText(existing.Call);
        var rowRoad = RoadContextTokens(rowText);
        var existingRoad = RoadContextTokens(existingText);
        if (rowRoad.Count > 0 && existingRoad.Count > 0 && rowRoad.Overlaps(existingRoad))
            return true;

        if (rowRoad.Contains("interstate") && existingRoad.Contains("interstate"))
            return true;

        var eventValue = eventText ?? string.Empty;
        return Regex.IsMatch(eventValue, @"\b(?:fire|explosion|burns?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
               && Regex.IsMatch(rowText, @"\b(?:transport(?:ing)?|patient|burns?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
               && HasStrongIncidentAnchorText(rowText);
    }

    private static bool IsDispatchLocationOnlyContinuation(CandidateRow row, IEnumerable<CandidateRow> retained)
    {
        var rowText = CallText(row.Call);
        if (HasStrongIncidentAnchorText(rowText))
            return false;
        var rowRoad = RoadContextTokens(rowText);
        return rowRoad.Count > 0
               && retained.Any(existing =>
                   Math.Abs(row.Call.RawTimestamp - existing.Call.RawTimestamp) <= 15 * 60
                   && rowRoad.Overlaps(RoadContextTokens(CallText(existing.Call))));
    }

    private static bool IsInjuryOutcomeContinuation(string eventText, CandidateRow row)
    {
        var eventValue = eventText ?? string.Empty;
        if (!Regex.IsMatch(eventValue, @"\b(?:mvc|mva|crash|accident|collision|wreck|rear[- ]?end(?:ed|ing)?|injur(?:y|ies|ed))\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;
        var rowText = CallText(row.Call);
        return InjuryContinuationPattern.IsMatch(rowText) && !LooksLikeStandaloneTransport(rowText, [row.Call]);
    }

    private static HashSet<string> RoadContextTokens(string text)
    {
        var tokens = RoadContextPattern.Matches(text ?? string.Empty)
            .Select(match => NormalizeRoadToken(match.Value))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (tokens.Any(value => value.StartsWith("i-", StringComparison.OrdinalIgnoreCase) || value.StartsWith("interstate-", StringComparison.OrdinalIgnoreCase)))
            tokens.Add("interstate");
        return tokens;
    }

    private static string NormalizeRoadToken(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).Trim().ToLowerInvariant(), @"\s+", " ");
        normalized = normalized
            .Replace("interstate ", "interstate-", StringComparison.OrdinalIgnoreCase)
            .Replace("i ", "i-", StringComparison.OrdinalIgnoreCase)
            .Replace("i-", "interstate-", StringComparison.OrdinalIgnoreCase)
            .Replace("hwy ", "highway-", StringComparison.OrdinalIgnoreCase)
            .Replace("highway ", "highway-", StringComparison.OrdinalIgnoreCase)
            .Replace("us highway ", "highway-", StringComparison.OrdinalIgnoreCase)
            .Replace("exit ", "exit-", StringComparison.OrdinalIgnoreCase);
        return normalized;
    }

    public static bool IsRoutineStatusText(string text) =>
        RoutineStatusPattern.IsMatch(text ?? string.Empty);

    public static bool HasStrongIncidentSignal(string text) =>
        HasStrongIncidentAnchorText(text);

    private static bool HasStrongIncidentAnchorText(string? text)
    {
        var value = text ?? string.Empty;
        return StrongIncidentAnchorPattern.IsMatch(value) ||
               BrokenDownVehicleHazardPattern.IsMatch(value);
    }

    public static HashSet<string> ExtractConcreteAnchors(string text) => ExtractAnchors(text);

    public static int SharedConcreteAnchorCount(IReadOnlySet<string> a, IReadOnlySet<string> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : a.Count(left => b.Any(right => AnchorsMatch(left, right)));

    public static bool IsLowConfidenceIncidentAcceptable(string title, string detail, IReadOnlyList<IncidentCandidateCall> calls, double confidence)
    {
        if (confidence >= 0.65)
            return true;
        var eventAnchors = ExtractAnchors($"{title} {detail}");
        if (eventAnchors.Count == 0)
            return false;
        if (calls.Count == 1)
        {
            var row = new CandidateRow(
                calls[0],
                ExtractTokens(CallText(calls[0])),
                ExtractAnchors(calls[0].Transcript, calls[0].Anchors),
                ExtractConcreteLocationAnchors(calls[0].Transcript, calls[0].Anchors));
            return ValidateSingleCallIncident($"{title} {detail}", row).IsValid;
        }
        var validation = Validate(title, detail, calls);
        if (validation.IsValid)
            return true;
        var strongAnchoredCalls = calls
            .Where(call => !IsNoisyEvidenceText(call.Transcript) && !IsRoutineEvidenceTextWithoutAnchors(call.Transcript))
            .Count(call => SharedAnchorScore(eventAnchors, ExtractAnchors(call.Transcript, call.Anchors)) > 0);
        return strongAnchoredCalls >= 2;
    }

    private static bool IsRoutineStatusRollup(string eventText, IReadOnlyList<CandidateRow> rows)
    {
        if (rows.Count < 2 || !RoutineStatusPattern.IsMatch(eventText))
            return false;
        if (HasCorroboratedEmergencySignal(rows))
            return false;
        var routineCalls = rows.Count(row => RoutineStatusPattern.IsMatch(CallText(row.Call)));
        if (routineCalls < Math.Max(2, rows.Count / 2))
            return false;
        var anchoredRows = rows.Where(row => row.Anchors.Count > 0).ToList();
        if (anchoredRows.Count < 2)
            return true;
        for (var i = 0; i < anchoredRows.Count; i++)
        {
            for (var j = i + 1; j < anchoredRows.Count; j++)
            {
                if (SharedConcreteAnchorCount(anchoredRows[i].Anchors, anchoredRows[j].Anchors) > 0)
                    return false;
            }
        }
        return true;
    }

    private static bool HasCorroboratedEmergencySignal(IReadOnlyList<CandidateRow> rows)
    {
        var strongRows = rows
            .Where(row => CorroboratedEmergencySignalPattern.IsMatch(CallText(row.Call)))
            .ToList();
        if (strongRows.Count < 2)
            return false;

        for (var i = 0; i < strongRows.Count; i++)
        {
            for (var j = i + 1; j < strongRows.Count; j++)
            {
                if (PairScore(strongRows[i], strongRows[j]) >= 0.12)
                    return true;
            }
        }

        return false;
    }

    private static bool IsNoisyEvidenceText(string text)
    {
        var value = text ?? string.Empty;
        var normalized = NormalizeText(value);
        if (normalized.Length < 45)
            return true;
        return NoisyEvidencePattern.IsMatch(normalized);
    }

    private static bool IsRoutineEvidenceTextWithoutAnchors(string text)
    {
        var value = text ?? string.Empty;
        var normalized = NormalizeText(value);
        return RoutineStatusPattern.IsMatch(normalized) && ExtractAnchors(normalized).Count == 0;
    }

    private static bool LooksLikeOperationalChatter(string eventText, IReadOnlyList<IncidentCandidateCall> calls)
    {
        if (HasStrongIncidentAnchorText(eventText))
            return false;
        var combinedLabels = string.Join(' ', calls.Select(c => c.TalkgroupName));
        var combinedTexts = string.Join(' ', calls.Select(c => c.Transcript));
        var labelHits = calls.Count(c => OperationalChatterPattern.IsMatch(c.TalkgroupName ?? string.Empty));
        return OperationalChatterPattern.IsMatch(eventText ?? string.Empty)
               || labelHits >= Math.Max(1, calls.Count / 2)
               || (OperationalChatterPattern.IsMatch(combinedLabels) && OperationalChatterPattern.IsMatch(combinedTexts));
    }

    private static bool HasStrongIncidentAnchor(string eventText, IReadOnlyList<IncidentCandidateCall> calls) =>
        HasStrongIncidentAnchorText(eventText)
        || calls.Any(call => HasStrongIncidentAnchorText(call.Transcript));

    private static bool TitleIntroducesUnsupportedStrongSignal(string eventText, IReadOnlyList<IncidentCandidateCall> calls) =>
        HasStrongIncidentAnchorText(eventText)
        && !calls.Any(call => HasStrongIncidentAnchorText(CallText(call)));

    private static bool LooksLikeStandaloneTransport(string eventText, IReadOnlyList<IncidentCandidateCall> calls)
    {
        var eventValue = eventText ?? string.Empty;
        if (!StandaloneTransportPattern.IsMatch(eventValue))
            return false;
        var transportCalls = calls.Count(call =>
            StandaloneTransportPattern.IsMatch(call.Transcript ?? string.Empty)
            || StandaloneTransportPattern.IsMatch(call.TalkgroupName ?? string.Empty));
        return transportCalls >= Math.Max(1, calls.Count / 2);
    }

    private static IncidentCandidateValidationResult? TryRecoverFromUnanchoredTransportHandoffCandidate(
        List<CandidateRow> rows,
        Func<CandidateRow, string> singleEventText,
        string reason)
    {
        if (!LooksLikeUnanchoredTransportHandoffCluster(rows))
            return null;

        var nonTransportRows = rows
            .Where(row => !IsTransportOrHospitalHandoffRow(row))
            .ToList();
        return TryRecoverSingleCallIncidentAfterPruning(nonTransportRows, singleEventText, reason)
               ?? new IncidentCandidateValidationResult(false, reason, []);
    }

    private static bool LooksLikeUnanchoredTransportHandoffCluster(IReadOnlyList<CandidateRow> rows)
    {
        var transportRows = rows.Where(IsTransportOrHospitalHandoffRow).ToList();
        if (transportRows.Count < 2 || transportRows.Count < Math.Max(1, rows.Count / 2))
            return false;

        var nonTransportRows = rows.Where(row => !IsTransportOrHospitalHandoffRow(row)).ToList();
        var nonTransportAnchors = nonTransportRows
            .SelectMany(row => row.ConcreteLocationAnchors)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (nonTransportAnchors.Count > 0
            && transportRows.Any(row => SharedAnchorScore(nonTransportAnchors, row.Anchors) > 0))
            return false;

        return !transportRows.Any(row => TransportParentEventReferencePattern.IsMatch(CallText(row.Call)));
    }

    private static bool IsTransportOrHospitalHandoffRow(CandidateRow row)
    {
        var text = $"{row.Call.TalkgroupName} {row.Call.Transcript}";
        return StandaloneTransportPattern.IsMatch(text);
    }

    private static bool LooksLikeBolo(string eventText) => BoloPattern.IsMatch(eventText ?? string.Empty);

    private static bool LooksLikeStandbyOnlyIncident(string eventText, IReadOnlyList<IncidentCandidateCall> calls)
    {
        if (!StandbyOnlyPattern.IsMatch(eventText ?? string.Empty) && !calls.Any(call => StandbyOnlyPattern.IsMatch(CallText(call))))
            return false;

        var eventAnchors = ExtractAnchors(eventText ?? string.Empty);
        var callAnchors = calls.SelectMany(call => ExtractAnchors(CallText(call), call.Anchors)).ToHashSet(StringComparer.Ordinal);
        return eventAnchors.Count == 0 && callAnchors.Count == 0;
    }

    private static bool LooksLikeGenericTrafficControlWithoutEventAnchor(string eventText, IReadOnlyList<CandidateRow> rows)
    {
        if (HasStrongIncidentAnchor(eventText, rows.Select(row => row.Call).ToList()))
            return false;

        var combined = $"{eventText} {string.Join(' ', rows.Select(row => row.Call.Transcript))}";
        if (!GenericTrafficControlPattern.IsMatch(combined))
            return false;

        if (rows.Any(row => row.ConcreteLocationAnchors.Count > 0 || HasNamedLandmarkHint(row)))
            return false;

        return true;
    }

    private static List<CandidateRow> RetainConcreteBoloEvidence(List<CandidateRow> rows, HashSet<string> eventAnchors)
    {
        if (rows.Count < 3)
            return rows;

        if (eventAnchors.Count > 0)
        {
            var anchored = rows.Where(row => SharedAnchorScore(eventAnchors, row.Anchors) > 0).ToList();
            if (anchored.Count >= 2)
                return anchored;
        }

        var bestGroup = rows
            .Select(row => new
            {
                Row = row,
                Related = rows
                    .Where(other => other.Call.CallId != row.Call.CallId)
                    .Count(other => PairScore(row, other) >= 0.35)
            })
            .OrderByDescending(row => row.Related)
            .FirstOrDefault();
        if (bestGroup == null || bestGroup.Related == 0)
            return rows;

        return rows
            .Where(row => row.Call.CallId == bestGroup.Row.Call.CallId || PairScore(bestGroup.Row, row) >= 0.35)
            .ToList();
    }

    private static double PairScore(CandidateRow a, CandidateRow b)
    {
        var anchor = SharedAnchorScore(a.Anchors, b.Anchors);
        if (anchor > 0)
            return Math.Max(0.35, anchor);
        return Similarity(a.Tokens, b.Tokens);
    }

    private static double SharedAnchorScore(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0)
            return 0;
        var shared = a.Count(left => b.Any(right => AnchorsMatch(left, right)));
        return shared == 0 ? 0 : shared / (double)Math.Min(a.Count, b.Count);
    }

    private static bool SharesConcreteLocation(HashSet<string> a, HashSet<string> b) =>
        a.Count > 0 && b.Count > 0 && a.Any(left => b.Any(right => ConcreteLocationsCompatible(left, right)));

    private static bool ConcreteLocationsCompatible(string left, string right)
    {
        if (AnchorsMatch(left, right))
            return true;

        var leftParsed = ParseConcreteLocation(left);
        var rightParsed = ParseConcreteLocation(right);
        if (leftParsed.Kind.Length == 0 || rightParsed.Kind.Length == 0)
            return false;
        if (string.Equals(leftParsed.Kind, "highway_mile_marker", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rightParsed.Kind, "highway_mile_marker", StringComparison.OrdinalIgnoreCase))
            return false;

        var leftParts = ConcreteLocationParts(leftParsed.Value);
        var rightParts = ConcreteLocationParts(rightParsed.Value);
        return leftParts.Any(leftPart => rightParts.Any(rightPart => ConcreteLocationPartsCompatible(leftPart, rightPart)));
    }

    private static (string Kind, string Value) ParseConcreteLocation(string anchor)
    {
        anchor ??= string.Empty;
        var index = anchor.IndexOf(':');
        if (index <= 0 || index >= anchor.Length - 1)
            return (string.Empty, anchor);
        return (anchor[..index], anchor[(index + 1)..]);
    }

    private static IReadOnlyList<string> ConcreteLocationParts(string value)
    {
        return (value ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ConcreteLocationComparablePart)
            .Select(part => Regex.Replace(part, @"\s+", " ").Trim())
            .Where(part => part.Length >= 8)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ConcreteLocationComparablePart(string part)
    {
        var value = Regex.Replace(part ?? string.Empty, @"\bblock\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"^\s*\d{1,6}\s+", " ", RegexOptions.CultureInvariant);
        return value;
    }

    private static bool ConcreteLocationTokenOverlap(string left, string right)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "road", "street", "drive", "avenue", "lane", "pike", "highway", "place", "circle",
            "court", "way", "terrace", "trail", "parkway", "boulevard", "block", "of", "the"
        };
        var leftTokens = Regex.Split(left.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(token => token.Length >= 4 && !stop.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rightTokens = Regex.Split(right.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(token => token.Length >= 4 && !stop.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return leftTokens.Count > 0
               && rightTokens.Count > 0
               && leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static bool ConcreteLocationPartsCompatible(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || left.Contains(right, StringComparison.OrdinalIgnoreCase)
            || right.Contains(left, StringComparison.OrdinalIgnoreCase)
            || ConcreteLocationTokenOverlap(left, right))
            return true;

        var leftCompact = CompactConcreteLocationPart(left);
        var rightCompact = CompactConcreteLocationPart(right);
        return leftCompact.Length >= 10
               && rightCompact.Length >= 10
               && (string.Equals(leftCompact, rightCompact, StringComparison.OrdinalIgnoreCase)
                   || leftCompact.Contains(rightCompact, StringComparison.OrdinalIgnoreCase)
                   || rightCompact.Contains(leftCompact, StringComparison.OrdinalIgnoreCase));
    }

    private static string CompactConcreteLocationPart(string value) =>
        Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", string.Empty);

    private static bool AnchorsMatch(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;
        return left.Length >= 8
               && right.Length >= 8
               && (left.Contains(right, StringComparison.OrdinalIgnoreCase)
                   || right.Contains(left, StringComparison.OrdinalIgnoreCase));
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

    private static HashSet<string> ExtractAnchors(string text, IReadOnlyList<CallAnchorRecord>? storedAnchors = null)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        text ??= string.Empty;
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(0, text))
        {
            if (!string.IsNullOrWhiteSpace(anchor.Value))
                anchors.Add(AnchorKey(anchor));
        }
        if (storedAnchors != null)
        {
            foreach (var anchor in storedAnchors)
            {
                if (!string.IsNullOrWhiteSpace(anchor.Value))
                    anchors.Add(AnchorKey(anchor));
            }
        }
        return anchors;
    }

    private static HashSet<string> ExtractConcreteLocationAnchors(string text, IReadOnlyList<CallAnchorRecord>? storedAnchors = null)
    {
        var anchors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in CallAnchorExtractionService.ExtractTranscriptAnchors(0, text ?? string.Empty))
            AddConcreteLocationAnchor(anchors, anchor);

        if (storedAnchors != null)
        {
            foreach (var anchor in storedAnchors)
                AddConcreteLocationAnchor(anchors, anchor);
        }

        return anchors;
    }

    private static void AddConcreteLocationAnchor(HashSet<string> anchors, CallAnchorRecord anchor)
    {
        if (!IsConcreteLocationAnchor(anchor))
            return;
        anchors.Add(AnchorKey(anchor));
    }

    private static bool IsConcreteLocationAnchor(CallAnchorRecord anchor) =>
        ConcreteLocationAnchorKinds.Contains(anchor.Kind)
        && !string.IsNullOrWhiteSpace(anchor.Value)
        && !TranscriptLocationService.LooksLikeTrafficLanePosition(anchor.Value)
        && (!string.Equals(anchor.Kind, "location", StringComparison.OrdinalIgnoreCase) || IsStrongLocationAnchor(anchor));

    private static bool IsStrongLocationAnchor(CallAnchorRecord anchor) =>
        anchor.Confidence >= 0.72
        && !string.Equals(anchor.Source, "deterministic_weak", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(anchor.Source, "location_hint", StringComparison.OrdinalIgnoreCase);

    private static string AnchorKey(CallAnchorRecord anchor) =>
        $"{anchor.Kind}:{anchor.Value}";

    private static void AddMatches(Regex regex, string text, HashSet<string> anchors)
    {
        foreach (Match match in regex.Matches(text ?? string.Empty))
        {
            var normalized = NormalizeAnchor(match.Value);
            if (normalized.Length >= 4 && !IsWeakAnchor(normalized))
                anchors.Add(normalized);
        }
    }

    private static bool IsWeakAnchor(string anchor)
    {
        var parts = anchor.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return true;
        if (!char.IsDigit(parts[0][0]) && new[] { "from", "the", "this", "that", "main", "your", "their", "our", "reporting", "nothing", "visible" }.Contains(parts[0], StringComparer.OrdinalIgnoreCase))
            return true;
        if (!char.IsDigit(parts[0][0]) && anchor.EndsWith("from street", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static HashSet<string> ExtractTokens(string text)
    {
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "call","calls","unit","units","officer","officers","dispatch","reported","advised","responding",
            "response","scene","area","update","updates","subject","caller","vehicle","vehicles","police","fire",
            "ems","medical","traffic","north","south","east","west","street","road","drive","avenue","lane",
            "near","copy","copies","clear","clearance","radio","county","city","sheriff","department","channel",
            "station","status","administrative","coordination","routine","check","checks"
        };
        var cleaned = Regex.Replace((text ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9\s-]", " ");
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim('-');
            if (token.Length < 4 || stop.Contains(token))
                continue;
            tokens.Add(token);
        }
        return tokens;
    }

    private static string CallText(IncidentCandidateCall call) =>
        $"{call.SystemShortName} {call.Category} {call.TalkgroupName} {call.Transcript}";

    private static string NormalizeText(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

    private static string NormalizeAnchor(string value)
    {
        var normalized = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\b(rd|st|dr|ave|ln|hwy|pl|cir|ct|pkwy|blvd|ter)\b", m => m.Value switch
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
            _ => m.Value
        });
        return Regex.Replace(normalized, @"\s+", " ");
    }

    private sealed record CandidateRow(
        IncidentCandidateCall Call,
        HashSet<string> Tokens,
        HashSet<string> Anchors,
        HashSet<string> ConcreteLocationAnchors);

    private sealed record LocationConflictResult(bool IsValid, string Reason, List<CandidateRow> Rows);
}
