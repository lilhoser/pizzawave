namespace pizzad.Tests;

public sealed class IncidentEvidenceDecisionEngineV2Tests
{
    [Fact]
    public void Decide_AcceptsSourceBackedPrimaryEventWithoutConflicts()
    {
        const string transcript = "Engine 3 responding CPR in progress at 804 Main Street.";
        var span = Span(802453, transcript, "CPR in progress");
        var hypothesis = Hypothesis(
            [802453],
            [new EventEvidenceV2("medical", "non_breathing", "strong", [802453], [span])],
            [new MembershipEvidenceV2(802453, "primary_event", "accept", ["source-backed event evidence"], [span])],
            [],
            new NarrativeEvidenceV2("Non-breathing patient", "CPR in progress.", [new NarrativeFactV2("event", "CPR in progress", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [802453] = transcript },
            "llm:ot:802453:event");

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([802453], decision.AcceptedCallIds);
        Assert.Empty(decision.RejectedCallIds);
        Assert.Equal("ems", decision.Category);
    }

    [Fact]
    public void Decide_RejectsUngroundedSpans()
    {
        const string transcript = "Engine 3 responding CPR in progress at 804 Main Street.";
        var start = transcript.IndexOf("CPR in progress", StringComparison.Ordinal);
        var badSpan = new EvidenceSpanV2(802453, start, start + "CPR in progress".Length, "vehicle crash");
        var hypothesis = Hypothesis(
            [802453],
            [new EventEvidenceV2("medical", "non_breathing", "strong", [802453], [badSpan])],
            [new MembershipEvidenceV2(802453, "primary_event", "accept", ["source-backed event evidence"], [badSpan])],
            [],
            new NarrativeEvidenceV2("Non-breathing patient", "CPR in progress.", [new NarrativeFactV2("event", "CPR in progress", [badSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [802453] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("ignored unsupported optional spans", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted event-member calls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsExplicitConflicts()
    {
        const string leftTranscript = "Medic responding chest pain at 523 Callaway Court.";
        const string rightTranscript = "Medic responding chest pain at 7301 East Brainerd Road.";
        var leftSpan = Span(797519, leftTranscript, "chest pain");
        var rightSpan = Span(797833, rightTranscript, "chest pain");
        var conflictSpan = Span(797833, rightTranscript, "7301 East Brainerd Road");
        var hypothesis = Hypothesis(
            [797519, 797833],
            [new EventEvidenceV2("medical", "chest_pain", "strong", [797519, 797833], [leftSpan, rightSpan])],
            [
                new MembershipEvidenceV2(797519, "primary_event", "accept", ["source-backed event evidence"], [leftSpan]),
                new MembershipEvidenceV2(797833, "primary_event", "accept", ["source-backed event evidence"], [rightSpan])
            ],
            [new ConflictEvidenceV2("location_conflict", [797519, 797833], "same symptom but incompatible concrete locations", [conflictSpan])],
            new NarrativeEvidenceV2("Chest pain", "Two calls report chest pain.", [new NarrativeFactV2("event", "chest pain", [leftSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [797519] = leftTranscript,
                [797833] = rightTranscript
            });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Conflicts, conflict => conflict.ConflictType == "location_conflict");
    }

    [Fact]
    public void Decide_RejectsServerDerivedLocationConflict()
    {
        const string leftTranscript = "Medic responding chest pain at 523 Callaway Court.";
        const string rightTranscript = "Medic responding chest pain at 7301 East Brainerd Road.";
        var leftEventSpan = Span(797519, leftTranscript, "chest pain");
        var rightEventSpan = Span(797833, rightTranscript, "chest pain");
        var leftLocationSpan = Span(797519, leftTranscript, "523 Callaway Court");
        var rightLocationSpan = Span(797833, rightTranscript, "7301 East Brainerd Road");
        var hypothesis = Hypothesis(
            [797519, 797833],
            [new EventEvidenceV2("medical", "chest_pain", "strong", [797519, 797833], [leftEventSpan, rightEventSpan])],
            [
                new LocationEvidenceV2("address", "523 Callaway Court", "523 Callaway Court", "high", [797519], [leftLocationSpan]),
                new LocationEvidenceV2("address", "7301 East Brainerd Road", "7301 East Brainerd Road", "high", [797833], [rightLocationSpan])
            ],
            [
                new MembershipEvidenceV2(797519, "primary_event", "accept", ["source-backed event evidence"], [leftEventSpan]),
                new MembershipEvidenceV2(797833, "primary_event", "accept", ["source-backed event evidence"], [rightEventSpan])
            ],
            [],
            new NarrativeEvidenceV2("Chest pain", "Two calls report chest pain.", [new NarrativeFactV2("event", "chest pain", [leftEventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [797519] = leftTranscript,
                [797833] = rightTranscript
            });

        Assert.Equal("shadow_reject", decision.Decision);
        var conflict = Assert.Single(decision.Conflicts);
        Assert.Equal("location_conflict", conflict.ConflictType);
        Assert.Equal([797519, 797833], conflict.CallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("blocking conflicts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_AllowsEquivalentLocationKeyVariants()
    {
        const string leftTranscript = "Medic responding chest pain at 523 Callaway Court.";
        const string rightTranscript = "Engine responding to 523 Callaway Ct for chest pain.";
        var leftEventSpan = Span(797519, leftTranscript, "chest pain");
        var rightEventSpan = Span(797833, rightTranscript, "chest pain");
        var leftLocationSpan = Span(797519, leftTranscript, "523 Callaway Court");
        var rightLocationSpan = Span(797833, rightTranscript, "523 Callaway Ct");
        var hypothesis = Hypothesis(
            [797519, 797833],
            [new EventEvidenceV2("medical", "chest_pain", "strong", [797519, 797833], [leftEventSpan, rightEventSpan])],
            [
                new LocationEvidenceV2("address", "523 Callaway Court", "523 Callaway Court", "high", [797519], [leftLocationSpan]),
                new LocationEvidenceV2("address", "523 Callaway Ct", "523 Callaway Ct", "high", [797833], [rightLocationSpan])
            ],
            [
                new MembershipEvidenceV2(797519, "primary_event", "accept", ["source-backed event evidence"], [leftEventSpan]),
                new MembershipEvidenceV2(797833, "supporting", "accept", ["same address"], [rightEventSpan])
            ],
            [],
            new NarrativeEvidenceV2("Chest pain at 523 Callaway Court", "Calls report chest pain at 523 Callaway Court.", [new NarrativeFactV2("event", "chest pain", [leftEventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [797519] = leftTranscript,
                [797833] = rightTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Empty(decision.Conflicts);
        Assert.Equal([797519, 797833], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_RejectsTranscriptDerivedLocationConflict()
    {
        const string leftTranscript = "Engine 8 responding to 2309 Hickory Valley Road for automatic fire alarm.";
        const string rightTranscript = "Engine 4 responding to 406 Broad Street for commercial fire alarm.";
        var leftSpan = Span(1, leftTranscript, "automatic fire alarm");
        var rightSpan = Span(2, rightTranscript, "commercial fire alarm");
        var hypothesis = Hypothesis(
            [1, 2],
            [
                new EventEvidenceV2("fire", "fire_alarm", "strong", [1], [leftSpan]),
                new EventEvidenceV2("fire", "fire_alarm", "strong", [2], [rightSpan])
            ],
            [
                new MembershipEvidenceV2(1, "primary_event", "accept", ["source-backed event"], [leftSpan]),
                new MembershipEvidenceV2(2, "primary_event", "accept", ["source-backed event"], [rightSpan])
            ],
            [],
            new NarrativeEvidenceV2("Fire alarms", "Mixed fire alarms.", [new NarrativeFactV2("event", "automatic fire alarm", [leftSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [1] = leftTranscript, [2] = rightTranscript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Conflicts, conflict => conflict.ConflictType == "location_conflict");
    }

    [Fact]
    public void Decide_DropsServerDerivedParentEventConflictNeighbor()
    {
        const string leftTranscript = "Engine responding to a fire alarm at 2309 Hickory Valley Road.";
        const string rightTranscript = "Medic responding to chest pain at 523 Callaway Court.";
        var leftSpan = Span(1, leftTranscript, "fire alarm");
        var rightSpan = Span(2, rightTranscript, "chest pain");
        var hypothesis = Hypothesis(
            [1, 2],
            [
                new EventEvidenceV2("fire", "fire_alarm", "strong", [1], [leftSpan]),
                new EventEvidenceV2("medical", "chest_pain", "strong", [2], [rightSpan])
            ],
            [
                new MembershipEvidenceV2(1, "primary_event", "accept", ["source-backed event"], [leftSpan]),
                new MembershipEvidenceV2(2, "primary_event", "accept", ["source-backed event"], [rightSpan])
            ],
            [],
            new NarrativeEvidenceV2("Fire alarm and chest pain", "Mixed events.", [new NarrativeFactV2("event", "fire alarm", [leftSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [1] = leftTranscript, [2] = rightTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([1], decision.AcceptedCallIds);
        Assert.Equal([2], decision.RejectedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("parent-event-conflicting neighbor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsServerDerivedVehicleConflict()
    {
        const string leftTranscript = "Caller reports a black Ford truck hit a mailbox.";
        const string rightTranscript = "Caller reports a red Toyota sedan hit a fence.";
        var leftSpan = Span(1, leftTranscript, "hit a mailbox");
        var rightSpan = Span(2, rightTranscript, "hit a fence");
        var hypothesis = Hypothesis(
            [1, 2],
            [new EventEvidenceV2("property_damage", "vehicle_damage", "strong", [1, 2], [leftSpan, rightSpan])],
            [
                new MembershipEvidenceV2(1, "primary_event", "accept", ["source-backed event"], [leftSpan]),
                new MembershipEvidenceV2(2, "continuation", "accept", ["source-backed event"], [rightSpan])
            ],
            [],
            new NarrativeEvidenceV2("Vehicle property damage", "Vehicle damage.", [new NarrativeFactV2("event", "vehicle damage", [leftSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [1] = leftTranscript, [2] = rightTranscript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Conflicts, conflict => conflict.ConflictType == "vehicle_conflict");
    }

    [Fact]
    public void Decide_RejectsServerDerivedPersonConflict()
    {
        const string leftTranscript = "Medic responding for a 70 year old male possible stroke.";
        const string rightTranscript = "Medic responding for a 25 year old male unconscious.";
        var leftSpan = Span(1, leftTranscript, "possible stroke");
        var rightSpan = Span(2, rightTranscript, "unconscious");
        var hypothesis = Hypothesis(
            [1, 2],
            [new EventEvidenceV2("medical", "unconscious_or_stroke", "strong", [1, 2], [leftSpan, rightSpan])],
            [
                new MembershipEvidenceV2(1, "primary_event", "accept", ["source-backed event"], [leftSpan]),
                new MembershipEvidenceV2(2, "continuation", "accept", ["source-backed event"], [rightSpan])
            ],
            [],
            new NarrativeEvidenceV2("Medical incident", "Mixed medical calls.", [new NarrativeFactV2("event", "possible stroke", [leftSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [1] = leftTranscript, [2] = rightTranscript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Conflicts, conflict => conflict.ConflictType == "person_conflict");
    }

    [Fact]
    public void Decide_SynthesizesNarrativeFromAcceptedGroundedFacts()
    {
        const string transcript = "Engine 8 respond to 2309 Hickory Valley Road for automatic fire alarm.";
        var eventSpan = Span(1, transcript, "automatic fire alarm");
        var locationSpan = Span(1, transcript, "2309 Hickory Valley Road");
        var hypothesis = Hypothesis(
            [1],
            [new EventEvidenceV2("fire", "fire_alarm", "strong", [1], [eventSpan])],
            [new LocationEvidenceV2("address", "2309 Hickory Valley Road", "2309 Hickory Valley Road", "high", [1], [locationSpan])],
            [new MembershipEvidenceV2(1, "primary_event", "accept", ["source-backed event"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("Unsupported model title", "Unsupported detail.", [new NarrativeFactV2("event", "automatic fire alarm", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [1] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Fire alarm at 2309 Hickory Valley Road", decision.Title);
        Assert.Contains("automatic fire alarm", decision.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_RecoversFireAlarmWhenDispatchIsTranscribedAsFirewall()
    {
        const string dispatchTranscript = "Engine 2, engine 4, 210, Old Tesla Road Northeast, Firewall.";
        const string alarmPanelTranscript = "Full CASO command. We've made access through the Knox box. We're going to be going to the alarm panel to investigate.";
        var hypothesis = Hypothesis(
            [846703, 846745],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(846703, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(846745, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [846703] = dispatchTranscript,
                [846745] = alarmPanelTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([846703], decision.AcceptedCallIds);
        Assert.Equal("fire", decision.Category);
        Assert.Equal("Fire alarm at 210 Old Tesla Road Northeast", decision.Title);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversFireAlarmWhenDispatchIsTranscribedAsCommercialFirearm()
    {
        const string dispatchTranscript = "Engine 8 Ladder 21, Engine 21, Commercial Firearm 6425 Lee Highway, response firearm one.";
        var hypothesis = Hypothesis(
            [848081],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(848081, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [848081] = dispatchTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([848081], decision.AcceptedCallIds);
        Assert.Equal("fire", decision.Category);
        Assert.Equal("Fire alarm at 6425 Lee Highway", decision.Title);
    }

    [Fact]
    public void Decide_ComposesFireAlarmTitleWithAcceptedCallLocationAndBuildingType()
    {
        const string transcript = "Engine 4 respond to a residential fire alarm at 3621 Doris Street.";
        var eventSpan = Span(852031, transcript, "residential fire alarm");
        var hypothesis = Hypothesis(
            [852031],
            [new EventEvidenceV2("fire", "fire_alarm", "strong", [852031], [eventSpan])],
            [new MembershipEvidenceV2(852031, "primary_event", "accept", ["source-backed fire alarm"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("Fire alarm", "Fire alarm.", [new NarrativeFactV2("event", "residential fire alarm", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [852031] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Residential fire alarm at 3621 Doris Street", decision.Title);
    }

    [Fact]
    public void Decide_ComposesOverdoseTitleWithAcceptedCallTranscriptLocation()
    {
        const string transcript = "Medic 3 respond for an overdose at 6554 Union Road Northeast.";
        var eventSpan = Span(853861, transcript, "overdose");
        var hypothesis = Hypothesis(
            [853861],
            [new EventEvidenceV2("medical", "overdose", "strong", [853861], [eventSpan])],
            [new MembershipEvidenceV2(853861, "primary_event", "accept", ["source-backed overdose"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("Overdose", "Overdose.", [new NarrativeFactV2("event", "overdose", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [853861] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Overdose at 6554 Union Road Northeast", decision.Title);
    }

    [Fact]
    public void Decide_DoesNotRecoverTrafficCrashFromShipwrecksRollCall()
    {
        const string transcript = "This includes morning roll call. Stand by for shipwrecks.";
        var hypothesis = Hypothesis(
            [848111],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(848111, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [848111] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Empty(decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted event-member calls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_ComposesTrafficTitleFromStructuredEventTypeInsteadOfRawAbbreviation()
    {
        const string transcript = "Engine 1 responding to NVA at I-65 mile marker 29.";
        var eventSpan = Span(814121, transcript, "NVA");
        var locationSpan = Span(814121, transcript, "I-65 mile marker 29");
        var hypothesis = Hypothesis(
            [814121],
            [new EventEvidenceV2("traffic", "traffic_crash", "strong", [814121], [eventSpan])],
            [new LocationEvidenceV2("route", "I-65 mile marker 29", "i65-mm29", "high", [814121], [locationSpan])],
            [new MembershipEvidenceV2(814121, "primary_event", "accept", ["source-backed traffic crash"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("NVA", "NVA.", [new NarrativeFactV2("event", "NVA", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [814121] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Traffic crash at I-65 mile marker 29", decision.Title);
    }

    [Fact]
    public void Decide_ComposesMedicalTitleFromStructuredSubtype()
    {
        const string transcript = "Medic 4 responding to 1200 Market Street for shortness of breath.";
        var eventSpan = Span(814775, transcript, "shortness of breath");
        var locationSpan = Span(814775, transcript, "1200 Market Street");
        var hypothesis = Hypothesis(
            [814775],
            [new EventEvidenceV2("medical", "difficulty_breathing", "strong", [814775], [eventSpan])],
            [new LocationEvidenceV2("address", "1200 Market Street", "1200 Market Street", "high", [814775], [locationSpan])],
            [new MembershipEvidenceV2(814775, "primary_event", "accept", ["source-backed medical event"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("Medical", "Shortness of breath.", [new NarrativeFactV2("event", "shortness of breath", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [814775] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Difficulty breathing at 1200 Market Street", decision.Title);
    }

    [Fact]
    public void Decide_ComposesMedicalTitleFromGroundedSpanWhenSubtypeIsBroad()
    {
        const string transcript = "Medic 14 responding to 4510 Highway 58 for a 77-year-old female diabetic emergency.";
        var eventSpan = Span(814773, transcript, "diabetic emergency");
        var hypothesis = Hypothesis(
            [814773],
            [new EventEvidenceV2("medical", "medical_emergency", "strong", [814773], [eventSpan])],
            [new MembershipEvidenceV2(814773, "primary_event", "accept", ["source-backed medical event"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("Medical emergency", "Medical emergency.", [new NarrativeFactV2("event", "diabetic emergency", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [814773] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Diabetic emergency at 4510 Highway 58", decision.Title);
    }

    [Fact]
    public void Decide_ComposesMedicalTitleAndLocationFromGroundedSpanWhenSubtypeIsBroad()
    {
        const string transcript = "Engine 3, difficulty breathing, 3743 Coming Highway at the Circle K.";
        var eventSpan = Span(846679, transcript, "difficulty breathing");
        var locationSpan = Span(846679, transcript, "3743 Coming Highway at the Circle K");
        var hypothesis = Hypothesis(
            [846679],
            [new EventEvidenceV2("medical", "medical_emergency", "strong", [846679], [eventSpan])],
            [new LocationEvidenceV2("address", "3743 Coming Highway at the Circle K", "3743 Coming Highway", "high", [846679], [locationSpan])],
            [new MembershipEvidenceV2(846679, "primary_event", "accept", ["source-backed medical event"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("Medical emergency", "Medical emergency.", [new NarrativeFactV2("event", "difficulty breathing", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [846679] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Difficulty breathing at 3743 Coming Highway at the Circle K", decision.Title);
    }

    [Fact]
    public void Decide_IgnoresOneSidedModelConflict()
    {
        const string transcript = "503 probably going to be an overdose.";
        var eventSpan = Span(802511, transcript, "overdose");
        var hypothesis = Hypothesis(
            [802511],
            [new EventEvidenceV2("medical_incident", "overdose", "strong", [802511], [eventSpan])],
            [new MembershipEvidenceV2(802511, "primary_event", "accept", ["source-backed event evidence"], [eventSpan])],
            [new ConflictEvidenceV2("unrelated_location", [802511], "one-sided model conflict", [eventSpan])],
            new NarrativeEvidenceV2("Overdose", "Overdose.", [new NarrativeFactV2("event", "overdose", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [802511] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Empty(decision.Conflicts);
    }

    [Fact]
    public void Decide_IgnoresNonAuthoritativeModelConflictTypes()
    {
        const string dispatchTranscript = "Engine 3 unconscious, 17th Street, South East.";
        const string updateTranscript = "503 probably going to be an overdose.";
        var dispatchSpan = Span(802437, dispatchTranscript, "Engine 3 unconscious");
        var eventSpan = Span(802511, updateTranscript, "overdose");
        var hypothesis = Hypothesis(
            [802437, 802511],
            [new EventEvidenceV2("medical_incident", "overdose", "strong", [802511], [eventSpan])],
            [
                new MembershipEvidenceV2(802437, "unrelated", "reject", ["model called it unrelated"], [dispatchSpan]),
                new MembershipEvidenceV2(802511, "primary_event", "accept", ["source-backed event evidence"], [eventSpan])
            ],
            [new ConflictEvidenceV2("unrelated_location", [802437, 802511], "model inferred unrelated location", [dispatchSpan, eventSpan])],
            new NarrativeEvidenceV2(
                "Overdose",
                "Unconscious dispatch followed by overdose update.",
                [
                    new NarrativeFactV2("dispatch", "Engine 3 unconscious", [dispatchSpan]),
                    new NarrativeFactV2("event", "overdose", [eventSpan])
                ]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [802437] = dispatchTranscript,
                [802511] = updateTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([802437, 802511], decision.AcceptedCallIds);
        Assert.Empty(decision.Conflicts);
    }

    [Fact]
    public void Decide_RetainsNarrativeGroundedCallWhenModelMembershipSaysUnrelated()
    {
        const string dispatchTranscript = "Engine 3 unconscious, 17th Street, South East.";
        const string updateTranscript = "503 probably going to be an overdose.";
        var dispatchSpan = Span(802437, dispatchTranscript, "Engine 3 unconscious");
        var eventSpan = Span(802511, updateTranscript, "overdose");
        var hypothesis = Hypothesis(
            [802437, 802511],
            [new EventEvidenceV2("medical_incident", "overdose", "strong", [802511], [eventSpan])],
            [
                new MembershipEvidenceV2(802437, "unrelated", "reject", ["model called it unrelated"], [dispatchSpan]),
                new MembershipEvidenceV2(802511, "primary_event", "accept", ["source-backed event evidence"], [eventSpan])
            ],
            [],
            new NarrativeEvidenceV2(
                "Overdose",
                "Unconscious dispatch followed by overdose update.",
                [
                    new NarrativeFactV2("dispatch", "Engine 3 unconscious", [dispatchSpan]),
                    new NarrativeFactV2("event", "overdose", [eventSpan])
                ]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [802437] = dispatchTranscript,
                [802511] = updateTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([802437, 802511], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_RetainsSourceBackedInitialDispatchWhenModelDropsIt()
    {
        const string dispatchTranscript = "Engine 3 unconscious, 17th Street, South East.";
        const string patientTranscript = "503, there's two patients. I need another truck, emergency traffic.";
        const string overdoseTranscript = "503 probably going to be an overdose. No CPR. But both are agonizing.";
        var patientSpan = Span(802497, patientTranscript, "there's two patients");
        var overdoseSpan = Span(802511, overdoseTranscript, "overdose");
        var hypothesis = Hypothesis(
            [802437, 802497, 802511],
            [new EventEvidenceV2("medical_emergency", "overdose", "strong", [802497, 802511], [patientSpan, overdoseSpan])],
            [
                new MembershipEvidenceV2(802437, "unrelated", "reject", ["model called it unrelated"], []),
                new MembershipEvidenceV2(802497, "primary_event", "accept", ["source-backed event evidence"], [patientSpan]),
                new MembershipEvidenceV2(802511, "continuation", "accept", ["source-backed event evidence"], [overdoseSpan])
            ],
            [],
            new NarrativeEvidenceV2(
                "Overdose",
                "Two patients with possible overdose.",
                [
                    new NarrativeFactV2("patient_count", "there's two patients", [patientSpan]),
                    new NarrativeFactV2("event", "overdose", [overdoseSpan])
                ]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [802437] = dispatchTranscript,
                [802497] = patientTranscript,
                [802511] = overdoseTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([802437, 802497, 802511], decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("initial emergency dispatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_SynthesizesNarrativeWhenModelFactsAreMissing()
    {
        const string transcript = "Engine 3 responding CPR in progress at 804 Main Street.";
        var span = Span(802453, transcript, "CPR in progress");
        var hypothesis = Hypothesis(
            [802453],
            [new EventEvidenceV2("medical", "non_breathing", "strong", [802453], [span])],
            [new MembershipEvidenceV2(802453, "primary_event", "accept", ["source-backed event evidence"], [span])],
            [],
            new NarrativeEvidenceV2("Non-breathing patient", "CPR in progress.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [802453] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([802453], decision.AcceptedCallIds);
        Assert.Contains("CPR in progress", decision.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_RejectsRoutineTrafficStopAsPrimaryEvent()
    {
        const string transcript = "Traffic stop at 3333 St. Elmo Avenue, red Dodge Challenger, non-emergency.";
        var span = Span(802597, transcript, "Traffic stop");
        var hypothesis = Hypothesis(
            [802597],
            [new EventEvidenceV2("police_activity", "traffic_stop", "strong", [802597], [span])],
            [new MembershipEvidenceV2(802597, "primary_event", "accept", ["model classified as traffic stop"], [span])],
            [],
            new NarrativeEvidenceV2("Traffic stop at 3333 St. Elmo Avenue", "Traffic stop.", [new NarrativeFactV2("event", "Traffic stop", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [802597] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("routine, transport", StringComparison.OrdinalIgnoreCase)
                                                    || reason.Contains("no accepted call has source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsWarrantOrSubjectCheckAsPrimaryEvent()
    {
        const string transcript = "Check 29s on Matthew Wilkie. The one that I am showing warrants for is showing a birthday.";
        var span = Span(802905, transcript, "Check 29s on Matthew Wilkie");
        var hypothesis = Hypothesis(
            [802905],
            [new EventEvidenceV2("police", "warrant_check", "strong", [802905], [span])],
            [new MembershipEvidenceV2(802905, "primary_event", "accept", ["model classified as warrant check"], [span])],
            [],
            new NarrativeEvidenceV2("Subject warrant check", "Warrant check.", [new NarrativeFactV2("event", "Check 29s on Matthew Wilkie", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [802905] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted call has source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsSubjectIdentityAsPrimaryEvent()
    {
        const string transcript = "Drop me out with a subject at 3598. Check 29s on Matthew Wilkie.";
        var span = Span(802905, transcript, "subject at 3598");
        var hypothesis = Hypothesis(
            [802905],
            [new EventEvidenceV2("subject_identity", "subject_identity", "strong", [802905], [span])],
            [new MembershipEvidenceV2(802905, "primary_event", "accept", ["model classified as subject identity"], [span])],
            [],
            new NarrativeEvidenceV2("Subject identity check", "Subject identity check.", [new NarrativeFactV2("event", "subject at 3598", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [802905] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted call has source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsGenericDispatchOrStatusUpdateAsPrimaryEvent()
    {
        const string transcript = "Roadway's going to be clear. Let me show me code 1.";
        var span = Span(803143, transcript, "Roadway's going to be clear");
        var hypothesis = Hypothesis(
            [803143],
            [new EventEvidenceV2("status_update", "status_update", "strong", [803143], [span])],
            [new MembershipEvidenceV2(803143, "primary_event", "accept", ["model classified as status update"], [span])],
            [],
            new NarrativeEvidenceV2("Roadway clear", "Roadway clear.", [new NarrativeFactV2("event", "Roadway's going to be clear", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [803143] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("routine, transport", StringComparison.OrdinalIgnoreCase)
                                                    || reason.Contains("no accepted call has source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsCodeStatusDispatchUpdateAsPrimaryEvent()
    {
        const string firstTranscript = "2258, get on with 98 code 1.";
        const string secondTranscript = "Roadway's going to be clear. Let me show me code 1, code 12.";
        var firstSpan = Span(803031, firstTranscript, "98 code 1");
        var secondSpan = Span(803143, secondTranscript, "code 1, code 12");
        var hypothesis = Hypothesis(
            [803031, 803143],
            [
                new EventEvidenceV2("dispatch", "dispatch", "strong", [803031], [firstSpan]),
                new EventEvidenceV2("update", "update", "strong", [803143], [secondSpan])
            ],
            [
                new MembershipEvidenceV2(803031, "primary_event", "accept", ["code status"], [firstSpan]),
                new MembershipEvidenceV2(803143, "continuation", "accept", ["code status"], [secondSpan])
            ],
            [],
            new NarrativeEvidenceV2("Code status", "Code status update.", [new NarrativeFactV2("status", "code 1, code 12", [secondSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [803031] = firstTranscript, [803143] = secondTranscript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted call has source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsStandaloneEmsAssistWithoutParentEmergency()
    {
        const string transcript = "Station at 500. I've got an EMS assist on Bellingham Drive. Patient wait is 122.";
        var span = Span(801357, transcript, "EMS assist on Bellingham Drive");
        var hypothesis = Hypothesis(
            [801357],
            [new EventEvidenceV2("medical_assist", "ems_assist", "strong", [801357], [span])],
            [new MembershipEvidenceV2(801357, "primary_event", "accept", ["model classified as EMS assist"], [span])],
            [],
            new NarrativeEvidenceV2("EMS assist on Bellingham Drive", "EMS assist.", [new NarrativeFactV2("event", "EMS assist on Bellingham Drive", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [801357] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("routine, transport", StringComparison.OrdinalIgnoreCase)
                                                    || reason.Contains("no accepted call has source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsStandaloneLiftAssistWithoutParentEmergency()
    {
        const string transcript = "Engine 58 respond to 8837 River Cove Drive for lift assist.";
        var span = Span(811287, transcript, "lift assist");
        var hypothesis = Hypothesis(
            [811287],
            [new EventEvidenceV2("lift_assist", "lift_assist", "confirmed", [811287], [span])],
            [new MembershipEvidenceV2(811287, "primary_event", "accept", ["standalone lift assist"], [span])],
            [],
            new NarrativeEvidenceV2("Lift assist", "Lift assist.", [new NarrativeFactV2("event", "lift assist", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [811287] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("routine, transport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DoesNotRetainRejectedCallFromConflictSpanOnly()
    {
        const string leftTranscript = "Dispatch identified a possible stolen firearm at 2275 Spring Place Road.";
        const string rightTranscript = "2561 Sweetway Circle, oxide detection alarm.";
        var eventSpan = Span(812887, leftTranscript, "possible stolen firearm");
        var conflictSpan = Span(812997, rightTranscript, "2561 Sweetway Circle");
        var hypothesis = Hypothesis(
            [812887, 812997],
            [new EventEvidenceV2("stolen_firearm_investigation", "stolen_firearm", "strong", [812887], [eventSpan])],
            [
                new MembershipEvidenceV2(812887, "primary_event", "accept", ["source-backed event"], [eventSpan]),
                new MembershipEvidenceV2(812997, "unrelated", "reject", ["different alarm call"], [])
            ],
            [new ConflictEvidenceV2("unrelated_location", [812997], "different location", [conflictSpan])],
            new NarrativeEvidenceV2("Stolen firearm", "Possible stolen firearm.", [new NarrativeFactV2("event", "possible stolen firearm", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [812887] = leftTranscript, [812997] = rightTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([812887], decision.AcceptedCallIds);
        Assert.Equal([812997], decision.RejectedCallIds);
    }

    [Fact]
    public void Decide_DoesNotRetainRejectedCallFromNarrativeSpanOnly()
    {
        const string leftTranscript = "Dispatch identified a possible stolen firearm at 2275 Spring Place Road.";
        const string rightTranscript = "2561 Sweetway Circle, oxide detection alarm.";
        var eventSpan = Span(812887, leftTranscript, "possible stolen firearm");
        var unrelatedSpan = Span(812997, rightTranscript, "oxide detection alarm");
        var hypothesis = Hypothesis(
            [812887, 812997],
            [new EventEvidenceV2("stolen_firearm_investigation", "stolen_firearm", "strong", [812887], [eventSpan])],
            [
                new MembershipEvidenceV2(812887, "primary_event", "accept", ["source-backed event"], [eventSpan]),
                new MembershipEvidenceV2(812997, "unrelated", "reject", ["different alarm call"], [])
            ],
            [],
            new NarrativeEvidenceV2(
                "Stolen firearm",
                "Possible stolen firearm with unrelated alarm neighbor.",
                [
                    new NarrativeFactV2("event", "possible stolen firearm", [eventSpan]),
                    new NarrativeFactV2("unrelated", "oxide detection alarm", [unrelatedSpan])
                ]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [812887] = leftTranscript, [812997] = rightTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([812887], decision.AcceptedCallIds);
        Assert.Equal([812997], decision.RejectedCallIds);
    }

    [Fact]
    public void Decide_RejectsNonEmergencyHospitalTransferTraffic()
    {
        const string transcript = "This is Memorial, unit 13 en route to facility, non-emergency, 70-year-old male, ETA ten minutes. Any other questions or orders?";
        var span = Span(810359, transcript, "non-emergency");
        var hypothesis = Hypothesis(
            [810359],
            [new EventEvidenceV2("medical", "medical", "strong", [810359], [span])],
            [new MembershipEvidenceV2(810359, "primary_event", "accept", ["hospital report"], [span])],
            [],
            new NarrativeEvidenceV2("Non-emergency transfer", "Hospital report.", [new NarrativeFactV2("transport", "non-emergency", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [810359] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("routine, transport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RejectsStandaloneHospitalHandoffEvenWhenMarkedEmergency()
    {
        const string transcript = "This is Memorial, go ahead. Hamilton County Medics 10, we're inbound on emergency with the 87-year-old male, complaining of a productive cough and chest pain when he coughs. Vitals are stable. ETA about five. Any questions or orders?";
        var span = Span(842142, transcript, "chest pain");
        var hypothesis = Hypothesis(
            [842142],
            [new EventEvidenceV2("medical", "chest_pain", "strong", [842142], [span])],
            [new MembershipEvidenceV2(842142, "primary_event", "accept", ["hospital handoff"], [span])],
            [],
            new NarrativeEvidenceV2("Medical emergency", "Hospital handoff.", [new NarrativeFactV2("event", "chest pain", [span])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, IncidentEvidenceCallContextV2>
            {
                [842142] = new(842142, transcript, "ems", "MEMORIAL - EMS Encode: Memorial Hospital (Hamilton County)", "whiteoakmt-hamilton")
            });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Contains(decision.Reasons, reason => reason.Contains("routine, transport", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_AllowsHospitalHandoffWhenParentDispatchIsAccepted()
    {
        const string dispatchTranscript = "Walker Fire District 9 Memorial Medical 15 3782 Kensington Road for severe difficulty breathing.";
        const string handoffTranscript = "Memorial Medics 15 inbound with the 58-year-old male from 3782 Kensington Road, difficulty breathing, ETA about five.";
        var dispatchSpan = Span(842257, dispatchTranscript, "severe difficulty breathing");
        var handoffSpan = Span(842293, handoffTranscript, "difficulty breathing");
        var hypothesis = Hypothesis(
            [842257, 842293],
            [new EventEvidenceV2("medical", "difficulty_breathing", "strong", [842257], [dispatchSpan])],
            [
                new MembershipEvidenceV2(842257, "primary_event", "accept", ["dispatch"], [dispatchSpan]),
                new MembershipEvidenceV2(842293, "logistics", "accept", ["hospital handoff for same patient"], [handoffSpan])
            ],
            [],
            new NarrativeEvidenceV2("Difficulty breathing", "Dispatch with hospital handoff.", [new NarrativeFactV2("event", "severe difficulty breathing", [dispatchSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, IncidentEvidenceCallContextV2>
            {
                [842257] = new(842257, dispatchTranscript, "fire", "WCFR-DISP - Fire/EMS: Dispatch (Countywide)", "whiteoakmt-hamilton"),
                [842293] = new(842293, handoffTranscript, "ems", "MEMORIAL - EMS Encode: Memorial Hospital (Hamilton County)", "whiteoakmt-hamilton")
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([842257, 842293], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_DerivesSourceBackedPrimaryEventWhenModelOmitsEventSpans()
    {
        const string firstTranscript = "Engine 1 respond to 310 Industrial Drive for a commercial structure fire.";
        const string secondTranscript = "Command reports one of the machines is on fire.";
        var factSpan = Span(810003, firstTranscript, "commercial structure fire");
        var hypothesis = Hypothesis(
            [810003, 810063],
            [new EventEvidenceV2("structure_fire", "structure_fire", "confirmed", [], [])],
            [
                new MembershipEvidenceV2(810003, "primary_event", "accept", ["dispatch"], []),
                new MembershipEvidenceV2(810063, "continuation", "accept", ["command update"], [])
            ],
            [],
            new NarrativeEvidenceV2("Structure fire", "Commercial structure fire.", [new NarrativeFactV2("event", "commercial structure fire", [factSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [810003] = firstTranscript, [810063] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([810003, 810063], decision.AcceptedCallIds);
        Assert.Equal("fire", decision.Category);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server derived source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RetainsSameAddressContinuationWhenModelDropsIt()
    {
        const string firstTranscript = "Caller reports two aggressive dogs running at large at 3910 University Drive.";
        const string secondTranscript = "Humane Education advises the dogs are currently on the porch at 3910 University Drive with no contact.";
        var eventSpan = Span(813053, firstTranscript, "two aggressive dogs running at large");
        var hypothesis = Hypothesis(
            [813053, 813061],
            [new EventEvidenceV2("animal_complaint", "aggressive_dogs", "strong", [813053], [eventSpan])],
            [
                new MembershipEvidenceV2(813053, "primary_event", "accept", ["source-backed animal complaint"], [eventSpan]),
                new MembershipEvidenceV2(813061, "continuation", "reject", ["model dropped same-address update"], [])
            ],
            [],
            new NarrativeEvidenceV2("Aggressive dogs", "Aggressive dogs running at large.", [new NarrativeFactV2("event", "two aggressive dogs running at large", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [813053] = firstTranscript, [813061] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([813053, 813061], decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed continuation/update call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_ComposesWelfareCheckTitleBeforeAnimalQualifier()
    {
        const string transcript = "Police welfare check at 335 Castile Road for a female with dogs in heat.";
        var eventSpan = Span(852375, transcript, "welfare check");
        var hypothesis = Hypothesis(
            [852375],
            [new EventEvidenceV2("animal_complaint", "dogs", "strong", [852375], [eventSpan])],
            [new MembershipEvidenceV2(852375, "primary_event", "accept", ["dispatch"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("Animal complaint", "Dogs in heat.", [new NarrativeFactV2("event", "welfare check", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [852375] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Welfare check with dogs in heat at 335 Castile Road", decision.Title);
    }

    [Fact]
    public void Decide_ComposesUnconsciousTitleWithSubjectAndLocation()
    {
        const string transcript = "Medic 7 responding unconscious female at 705 Henderson Street.";
        var hypothesis = Hypothesis(
            [852023],
            [new EventEvidenceV2("medical", "unconscious", "strong", [], [])],
            [new MembershipEvidenceV2(852023, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [852023] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Unconscious female at 705 Henderson Street", decision.Title);
    }

    [Fact]
    public void Decide_DoesNotBorrowTitleLocationFromRejectedNeighbor()
    {
        const string acceptedTranscript = "Medic 7 responding unconscious female.";
        const string rejectedTranscript = "Engine responding to 705 Henderson Street for fire alarm.";
        var acceptedSpan = Span(852023, acceptedTranscript, "unconscious female");
        var rejectedLocationSpan = Span(852071, rejectedTranscript, "705 Henderson Street");
        var hypothesis = Hypothesis(
            [852023, 852071],
            [new EventEvidenceV2("medical", "unconscious", "strong", [852023], [acceptedSpan])],
            [new LocationEvidenceV2("address", "705 Henderson Street", "705 henderson street", "0.9", [852071], [rejectedLocationSpan])],
            [
                new MembershipEvidenceV2(852023, "primary_event", "accept", ["dispatch"], [acceptedSpan]),
                new MembershipEvidenceV2(852071, "unrelated", "reject", ["different event"], [])
            ],
            [],
            new NarrativeEvidenceV2("Unconscious", "Unconscious female.", [new NarrativeFactV2("event", "unconscious female", [acceptedSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [852023] = acceptedTranscript, [852071] = rejectedTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("Unconscious female", decision.Title);
        Assert.DoesNotContain("705 Henderson", decision.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_RetainsSameIncidentAssignmentUpdateWhenModelDropsIt()
    {
        const string firstTranscript = "Units are on scene for a stabbing at 334 Market Street.";
        const string secondTranscript = "Make sure he was on this tabbing at 334 Market Street so I can put him on the car.";
        var eventSpan = Span(808637, firstTranscript, "stabbing");
        var hypothesis = Hypothesis(
            [808637, 809057],
            [new EventEvidenceV2("violent_police", "stabbing", "strong", [808637], [eventSpan])],
            [
                new MembershipEvidenceV2(808637, "primary_event", "accept", ["source-backed stabbing"], [eventSpan]),
                new MembershipEvidenceV2(809057, "continuation", "reject", ["model treated assignment as non-event"], [])
            ],
            [],
            new NarrativeEvidenceV2("Stabbing", "Stabbing incident.", [new NarrativeFactV2("event", "stabbing", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [808637] = firstTranscript, [809057] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([808637, 809057], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_DoesNotRetainContinuationWithDifferentConcreteLocation()
    {
        const string firstTranscript = "Engine 1 responding to 100 Main Street for an accident with injuries.";
        const string secondTranscript = "Engine 2 responding to 500 Pine Street for an accident with injuries.";
        var eventSpan = Span(811621, firstTranscript, "accident with injuries");
        var hypothesis = Hypothesis(
            [811621, 811647],
            [new EventEvidenceV2("traffic", "injury_accident", "strong", [811621], [eventSpan])],
            [
                new MembershipEvidenceV2(811621, "primary_event", "accept", ["source-backed accident"], [eventSpan]),
                new MembershipEvidenceV2(811647, "continuation", "reject", ["different address"], [])
            ],
            [],
            new NarrativeEvidenceV2("Injury accident", "Accident with injuries.", [new NarrativeFactV2("event", "accident with injuries", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [811621] = firstTranscript, [811647] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([811621], decision.AcceptedCallIds);
        Assert.Equal([811647], decision.RejectedCallIds);
    }

    [Fact]
    public void Decide_RetainsNoisy911HangupContinuation()
    {
        const string firstTranscript = "Adam, I'm copy on a 911 hang up 1110 mark 1110 mark is street. Caller hung up and tried calling back a couple times.";
        const string secondTranscript = "Unknown to one call at 1110 Market Street. Caller hung up, they answer, try calling back multiple times.";
        var eventSpan = Span(812731, firstTranscript, "911 hang up");
        var hypothesis = Hypothesis(
            [812731, 812739],
            [new EventEvidenceV2("911_hang_up", "911_hang_up", "strong", [812731], [eventSpan])],
            [
                new MembershipEvidenceV2(812731, "primary_event", "accept", ["source-backed 911 hangup"], [eventSpan]),
                new MembershipEvidenceV2(812739, "continuation", "accept", ["same hangup update"], [])
            ],
            [],
            new NarrativeEvidenceV2("911 hangup", "911 hangup.", [new NarrativeFactV2("event", "911 hang up", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [812731] = firstTranscript, [812739] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([812731, 812739], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_RetainsFireStructureDispatchWithNoisyAddress()
    {
        const string firstTranscript = "We have commercial structure fire, 310 industrial drive, South West.";
        const string secondTranscript = "503 Fire Structure Commercial 310 Southwest 310 Industrial Drive Southwest";
        var eventSpan = Span(810045, firstTranscript, "commercial structure fire");
        var hypothesis = Hypothesis(
            [810041, 810045],
            [new EventEvidenceV2("structure_fire", "structure_fire", "strong", [810045], [eventSpan])],
            [
                new MembershipEvidenceV2(810045, "primary_event", "accept", ["source-backed structure fire"], [eventSpan]),
                new MembershipEvidenceV2(810041, "continuation", "accept", ["EMS dispatch to same fire"], [])
            ],
            [],
            new NarrativeEvidenceV2("Structure fire", "Commercial structure fire.", [new NarrativeFactV2("event", "commercial structure fire", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [810045] = firstTranscript, [810041] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([810041, 810045], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_RetainsStolenFirearmReferenceUpdate()
    {
        const string firstTranscript = "Deputy is checking on a hit on a possible stolen firearm.";
        const string secondTranscript = "That's second one you gave me, 3280162. That's going to be the stolen firearm.";
        var eventSpan = Span(812887, firstTranscript, "possible stolen firearm");
        var hypothesis = Hypothesis(
            [812887, 813049],
            [new EventEvidenceV2("stolen_firearm_check", "stolen_firearm_check", "strong", [812887], [eventSpan])],
            [
                new MembershipEvidenceV2(812887, "primary_event", "accept", ["source-backed stolen firearm"], [eventSpan]),
                new MembershipEvidenceV2(813049, "continuation", "accept", ["serial/reference update"], [])
            ],
            [],
            new NarrativeEvidenceV2("Stolen firearm", "Possible stolen firearm.", [new NarrativeFactV2("event", "possible stolen firearm", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [812887] = firstTranscript, [813049] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([812887, 813049], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_RetainsResponderStatusForAcceptedMedicalEmergency()
    {
        const string firstTranscript = "It's probably going to be a DOA, refused CPR. We're still trying to get some more information.";
        const string secondTranscript = "500 PD or SO still enroute but call 5-0-5 500 feet, AR";
        var eventSpan = Span(809265, firstTranscript, "DOA");
        var hypothesis = Hypothesis(
            [809265, 809275],
            [new EventEvidenceV2("medical_emergency", "medical_emergency", "strong", [809265], [eventSpan])],
            [
                new MembershipEvidenceV2(809265, "primary_event", "accept", ["source-backed medical emergency"], [eventSpan]),
                new MembershipEvidenceV2(809275, "continuation", "accept", ["responder status"], [])
            ],
            [],
            new NarrativeEvidenceV2("DOA", "Probable DOA.", [new NarrativeFactV2("event", "DOA", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [809265] = firstTranscript, [809275] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([809265, 809275], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_DoesNotSplitTrafficAccidentBecausePatientDetailsArePresent()
    {
        const string fireTranscript = "Highway 58 reports of an accident with entrapment, 6500 Highway 58 at Hunter Road.";
        const string emsTranscript = "Medic 211 is responding to an accident with entrapment, one patient with a head injury.";
        var eventSpan = Span(816071, fireTranscript, "accident with entrapment");
        var hypothesis = Hypothesis(
            [816071, 816073],
            [new EventEvidenceV2("traffic", "entrapment_accident", "strong", [816071], [eventSpan])],
            [
                new MembershipEvidenceV2(816071, "primary_event", "accept", ["source-backed accident"], [eventSpan]),
                new MembershipEvidenceV2(816073, "continuation", "accept", ["EMS response"], [])
            ],
            [],
            new NarrativeEvidenceV2("Accident with entrapment", "Accident with entrapment.", [new NarrativeFactV2("event", "accident with entrapment", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [816071] = fireTranscript, [816073] = emsTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([816071, 816073], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_TreatsViolentAndPropertyPoliceSignalsAsCompatibleParentEvent()
    {
        const string firstTranscript = "Female advised someone beat this male and he may have a weapon.";
        const string secondTranscript = "All units, possible auto theft in progress, male is bleeding and refusing to stay.";
        var eventSpan = Span(814811, firstTranscript, "someone beat this male");
        var hypothesis = Hypothesis(
            [814811, 814861],
            [new EventEvidenceV2("violent_police", "assault_weapon", "strong", [814811], [eventSpan])],
            [
                new MembershipEvidenceV2(814811, "primary_event", "accept", ["assault with possible weapon"], [eventSpan]),
                new MembershipEvidenceV2(814861, "continuation", "accept", ["same police event update"], [])
            ],
            [],
            new NarrativeEvidenceV2("Assault with possible weapon", "Assault with possible weapon.", [new NarrativeFactV2("event", "someone beat this male", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [814811] = firstTranscript, [814861] = secondTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([814811, 814861], decision.AcceptedCallIds);
    }

    [Fact]
    public void Decide_TreatsNoisyNvcDispatchAsTrafficIncident()
    {
        const string transcript = "Station tone for NVC with another injury.";
        var eventSpan = Span(814121, transcript, "NVC with another injury");
        var hypothesis = Hypothesis(
            [814121],
            [new EventEvidenceV2("traffic", "injury_crash", "strong", [814121], [eventSpan])],
            [new MembershipEvidenceV2(814121, "primary_event", "accept", ["noisy MVC dispatch"], [eventSpan])],
            [],
            new NarrativeEvidenceV2("Injury crash", "NVC with another injury.", [new NarrativeFactV2("event", "NVC with another injury", [eventSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [814121] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([814121], decision.AcceptedCallIds);
        Assert.Equal("traffic", decision.Category);
    }

    [Fact]
    public void Decide_RecoversServerRecognizedPrimaryEventWhenModelRejectsAllMembership()
    {
        const string transcript = "Tune station on Puget 228 for NVC with another injury.";
        var hypothesis = Hypothesis(
            [814121],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(814121, "unrelated", "reject", ["model missed noisy MVC wording"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [814121] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([814121], decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DoesNotJoinDisconnectedPrimaryEventsWhenModelRejectsAllMembership()
    {
        const string broadFireTranscript = "Engine 12 responding to an automatic fire alarm at 1351 Broad Street, suite 111.";
        const string emsTranscript = "Medic 5 responding for respiratory distress on fire ground 5.";
        const string standingOakFireTranscript = "Engine 9 responding to an automatic fire alarm at 512 Standing Oak Lane.";
        var hypothesis = Hypothesis(
            [835645, 835723, 835891],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(835645, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(835723, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(835891, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [835645] = broadFireTranscript,
                [835723] = emsTranscript,
                [835891] = standingOakFireTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([835645], decision.AcceptedCallIds);
        Assert.Equal([835723, 835891], decision.RejectedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server dropped disconnected primary event fallback call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversInjuryFallWhenModelRejectsAllMembershipWithoutJoiningSeparateFall()
    {
        const string pediatricPoliceDispatch = "EMS and first responders are outside 240 County Road 442. I have a seven month old that falls to bed and hit his head. He's bleeding from the nose. He's breathing and responding.";
        const string pediatricEmsDispatch = "Attention 901, 901, 240 County Road 442, have a seven month old that fell off the bed and hit his head. He is bleeding from his nose.";
        const string adultFallDispatch = "903, 903, 708 Howard Street, have a 69-year-old male fell in the floor, unknown if injured, does have altered mental status.";
        const string adultFallUpdate = "Responding to 708 Howard Street, 69-year-old male fell in the floor, altered mental status, shaking and confused.";
        var hypothesis = Hypothesis(
            [837951, 837967, 838063, 838079],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(837951, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(837967, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(838063, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(838079, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [837951] = pediatricPoliceDispatch,
                [837967] = pediatricEmsDispatch,
                [838063] = adultFallDispatch,
                [838079] = adultFallUpdate
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([837951, 837967], decision.AcceptedCallIds);
        Assert.Equal([838063, 838079], decision.RejectedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(decision.Reasons, reason => reason.Contains("server dropped disconnected primary event fallback call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DropsParentEventConflictingNeighborInsteadOfRejectingWholeHypothesis()
    {
        const string policeTranscript = "Female advised someone beat this male and he may have a weapon.";
        const string updateTranscript = "Possible auto theft in progress, male is bleeding and refusing to stay.";
        const string crashTranscript = "Two vehicle accidents near Hickory Valley Tiner Road with one party bleeding.";
        var policeSpan = Span(814811, policeTranscript, "someone beat this male");
        var updateSpan = Span(814861, updateTranscript, "male is bleeding");
        var crashSpan = Span(815321, crashTranscript, "Two vehicle accidents");
        var hypothesis = Hypothesis(
            [814811, 814861, 815321],
            [
                new EventEvidenceV2("violent_police", "assault_weapon", "strong", [814811, 814861], [policeSpan, updateSpan]),
                new EventEvidenceV2("traffic", "traffic_crash", "strong", [815321], [crashSpan])
            ],
            [
                new MembershipEvidenceV2(814811, "primary_event", "accept", ["police event"], [policeSpan]),
                new MembershipEvidenceV2(814861, "continuation", "accept", ["police update"], [updateSpan]),
                new MembershipEvidenceV2(815321, "primary_event", "accept", ["unrelated crash neighbor"], [crashSpan])
            ],
            [],
            new NarrativeEvidenceV2("Police event", "Assault with possible weapon.", [new NarrativeFactV2("event", "someone beat this male", [policeSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [814811] = policeTranscript,
                [814861] = updateTranscript,
                [815321] = crashTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([814811, 814861], decision.AcceptedCallIds);
        Assert.Equal([815321], decision.RejectedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("parent-event-conflicting neighbor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_UsesTranscriptGroupWhenModelSourceListMixesMedicalAndPoliceEvents()
    {
        const string medicalTranscript = "Medic 14 responding to 4510 Highway 58 for a 77-year-old female diabetic emergency.";
        const string policeTranscript = "Female advised someone beat this male and he may have a weapon.";
        const string updateTranscript = "Possible auto theft in progress, male is bleeding and refusing to stay.";
        var medicalSpan = Span(814773, medicalTranscript, "diabetic emergency");
        var policeSpan = Span(814811, policeTranscript, "someone beat this male");
        var updateSpan = Span(814861, updateTranscript, "male is bleeding");
        var hypothesis = Hypothesis(
            [814773, 814811, 814861],
            [new EventEvidenceV2("assault", "assault", "strong", [814773, 814811, 814861], [medicalSpan, policeSpan, updateSpan])],
            [
                new MembershipEvidenceV2(814773, "primary_event", "accept", ["model incorrectly mixed medical call into police event"], []),
                new MembershipEvidenceV2(814811, "continuation", "accept", ["police event"], []),
                new MembershipEvidenceV2(814861, "continuation", "accept", ["police update"], [])
            ],
            [],
            new NarrativeEvidenceV2("Assault", "Assault with possible weapon.", [new NarrativeFactV2("event", "someone beat this male", [policeSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [814773] = medicalTranscript,
                [814811] = policeTranscript,
                [814861] = updateTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([814811, 814861], decision.AcceptedCallIds);
        Assert.Equal([814773], decision.RejectedCallIds);
    }

    [Fact]
    public void Decide_DropsDisconnectedAcceptedMedicalTransportNeighbor()
    {
        const string transportTranscript = "10 Nougat Macon, this is Memorial Medical 14. Memorial 14, go ahead. I'm not on emergency, Memorial Glenwood, 55 year old female overdose. Channel's open, go ahead and switch over.";
        const string breathingTranscript = "Welcome to your United Team of Moislemake, their number 100 Hamilton Drive, 100 Hamilton Drive with a friendship road. We've reached the 40 years of age male extreme difficulty breathing here in Pueblo, pressure and sleep apnea. Base hand lords, the time of the patrol. Out.";
        var transportSpan = Span(839995, transportTranscript, "overdose");
        var breathingSpan = Span(840097, breathingTranscript, "difficulty breathing");
        var hypothesis = Hypothesis(
            [839995, 840097],
            [new EventEvidenceV2("medical", "medical_emergency", "strong", [839995, 840097], [transportSpan, breathingSpan])],
            [
                new MembershipEvidenceV2(839995, "primary_event", "accept", ["model joined non-emergency transport with medical dispatch"], [transportSpan]),
                new MembershipEvidenceV2(840097, "primary_event", "accept", ["medical dispatch"], [breathingSpan])
            ],
            [],
            new NarrativeEvidenceV2("Medical emergency", "Medical emergency.", [new NarrativeFactV2("event", "difficulty breathing", [breathingSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [839995] = transportTranscript,
                [840097] = breathingTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([840097], decision.AcceptedCallIds);
        Assert.Equal([839995], decision.RejectedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("disconnected accepted primary event", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DropsDisconnectedTerminalMedicalStatusNeighbor()
    {
        const string terminalStatusTranscript = "Here on command, patient's loaded. The MS command terminate 12. I copy. Termination 149.";
        const string chestPainDispatchTranscript = "508 Chest Pain, 4045 Old Three Wheel Road, Northwest.";
        const string chestPainResponseTranscript = "Engine 5, all three will run Northwest chest pains.";
        var terminalSpan = Span(845275, terminalStatusTranscript, "patient's loaded");
        var dispatchSpan = Span(845301, chestPainDispatchTranscript, "Chest Pain, 4045 Old Three Wheel Road");
        var responseSpan = Span(845303, chestPainResponseTranscript, "all three will run Northwest chest pains");
        var hypothesis = Hypothesis(
            [845275, 845301, 845303],
            [new EventEvidenceV2("medical", "chest_pain", "strong", [845275, 845301, 845303], [terminalSpan, dispatchSpan, responseSpan])],
            [
                new MembershipEvidenceV2(845275, "continuation", "accept", ["model joined terminal status with new medical call"], [terminalSpan]),
                new MembershipEvidenceV2(845301, "primary_event", "accept", ["chest pain dispatch"], [dispatchSpan]),
                new MembershipEvidenceV2(845303, "continuation", "accept", ["same chest pain response"], [responseSpan])
            ],
            [],
            new NarrativeEvidenceV2("Medical emergency", "Chest pain call.", [new NarrativeFactV2("event", "Chest Pain, 4045 Old Three Wheel Road", [dispatchSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [845275] = terminalStatusTranscript,
                [845301] = chestPainDispatchTranscript,
                [845303] = chestPainResponseTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([845301, 845303], decision.AcceptedCallIds);
        Assert.Equal([845275], decision.RejectedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("disconnected terminal medical status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DropsDisconnectedAcceptedTrafficNeighbor()
    {
        const string recklessDriverTranscript = "Cloning Simifer, reckless driver bullet. The last contact was outbound on AP 840 from 64Bullet for a white FedEx delivery van just all over the roadway. I called her past him and advised that the driver looked like his eyes were closed. Any contact with the server's top time is 1967.";
        const string crashTranscript = "I'm sorry, I was a queen. Make a queen, David, 9-0-5-9. Thank you. Three-two-one. Eighteen-good. This case be available for that crash on the interstate. I can call.";
        var recklessSpan = Span(840057, recklessDriverTranscript, "reckless driver");
        var crashSpan = Span(840081, crashTranscript, "crash");
        var hypothesis = Hypothesis(
            [840057, 840081],
            [new EventEvidenceV2("traffic", "traffic_crash", "strong", [840057, 840081], [recklessSpan, crashSpan])],
            [
                new MembershipEvidenceV2(840057, "primary_event", "accept", ["model joined reckless driver BOLO with crash"], [recklessSpan]),
                new MembershipEvidenceV2(840081, "primary_event", "accept", ["crash dispatch"], [crashSpan])
            ],
            [],
            new NarrativeEvidenceV2("Traffic crash", "Traffic crash.", [new NarrativeFactV2("event", "crash", [crashSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [840057] = recklessDriverTranscript,
                [840081] = crashTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([840081], decision.AcceptedCallIds);
        Assert.Equal([840057], decision.RejectedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("disconnected accepted primary event", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversEntrapmentCrashOverUnrelatedMedicalCallWhenModelRejectsAllMembership()
    {
        const string entrapmentDispatchTranscript = "Squad rescue one, you're responding to 513 Lee Pike, 513 Lee Pike off of Nesee Drive and Pendergrass Road motor vehicle accident with entrapment.";
        const string entrapmentUpdateTranscript = "cracked into a tree, one partially ejected, one entrapped, updated from medic 2. There is one code 73 and one patient with agnobreathing.";
        const string medicalTranscript = "East Ridge Station 1, you're responding to 1607 East Ridge Avenue, difficulty breathing and chest pain.";
        var hypothesis = Hypothesis(
            [845915, 845917, 845975],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(845915, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(845917, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(845975, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [845915] = entrapmentDispatchTranscript,
                [845917] = entrapmentUpdateTranscript,
                [845975] = medicalTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([845915], decision.AcceptedCallIds);
        Assert.Equal([845917, 845975], decision.RejectedCallIds);
        Assert.Equal("traffic", decision.Category);
        Assert.Contains("Crash", decision.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_RecoversChildLockedVehicleWhenModelRejectsAllMembership()
    {
        const string civilTranscript = "2513 Judson Lane between North Chamberlain Avenue and the dead end. The reporting party advised someone threw olive oil on her car.";
        const string serviceTranscript = "One 8 at the service center. That's correct.";
        const string hangupTranscript = "800 Van Dyl drive was across the McIntyre road, number one hang up. May contact back with a female advisor and she heard something in her garage.";
        const string childVehicleTranscript = "Water 21, you're responding. 2220 Hamilton Place Boulevard at the Alta. It's going to be off of Pine Grove, Child Locking Vehicle, White Kia SUV, two children in the vehicle under the age of four. Advice, I've not seen guardian or a driver for the vehicle.";
        var hypothesis = Hypothesis(
            [840717, 840971, 840987, 841029],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(840717, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(840971, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(840987, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(841029, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [840717] = civilTranscript,
                [840971] = serviceTranscript,
                [840987] = hangupTranscript,
                [841029] = childVehicleTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([841029], decision.AcceptedCallIds);
        Assert.Equal("fire", decision.Category);
        Assert.Contains("Child locked in vehicle", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversPrimaryEventWhenModelAcceptsOnlyNonEventCalls()
    {
        const string routineMedicalTranscript = "I'm at 15. I'm around on emergency. 67 is a year old female. She started feeling dizzy and anxious. Vitals unremarkable.";
        const string vehicleFireTranscript = "Four and five, they're advising a Mercedes SUV, one of them is currently on fire, advising the driver is out of the vehicle.";
        const string trappedTranscript = "Units en route to the accident be advised. There is one person trapped in a vehicle trying to figure out which one.";
        var routineSpan = Span(841109, routineMedicalTranscript, "dizzy and anxious");
        var hypothesis = Hypothesis(
            [841109, 841315, 841337],
            [new EventEvidenceV2("medical", "non_emergency", "strong", [841109], [routineSpan])],
            [
                new MembershipEvidenceV2(841109, "primary_event", "accept", ["model accepted non-event medical traffic"], [routineSpan]),
                new MembershipEvidenceV2(841315, "unrelated", "reject", ["model missed vehicle fire"], []),
                new MembershipEvidenceV2(841337, "unrelated", "reject", ["model missed trapped person update"], [])
            ],
            [],
            new NarrativeEvidenceV2("Medical traffic", "Model picked a non-event.", [new NarrativeFactV2("event", "dizzy and anxious", [routineSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [841109] = routineMedicalTranscript,
                [841315] = vehicleFireTranscript,
                [841337] = trappedTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([841315], decision.AcceptedCallIds);
        Assert.Equal("fire", decision.Category);
        Assert.Contains("Fire", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversSeizureWhenModelRejectsAllMembership()
    {
        const string seizureTranscript = "16. Pigeon Route to 100 Tally, Department 240, 100 Tally, hip hill, avenue and sherry zone on a 29 year old female seizure. Clear and route momentarily.";
        var hypothesis = Hypothesis(
            [841143],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(841143, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [841143] = seizureTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([841143], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DerivesPrimaryEvidenceWhenModelAcceptsRecoverableCallWithoutUsableEventSpan()
    {
        const string seizureTranscript = "16. Pigeon Route to 100 Tally, Department 240, 100 Tally, hip hill, avenue and sherry zone on a 29 year old female seizure. Clear and route momentarily.";
        var unsupportedSpan = new EvidenceSpanV2(841143, 999, 1019, "respiratory distress");
        var hypothesis = Hypothesis(
            [841143],
            [new EventEvidenceV2("medical", "seizure", "strong", [841143], [unsupportedSpan])],
            [new MembershipEvidenceV2(841143, "primary_event", "accept", ["model accepted call but gave unusable span"], [unsupportedSpan])],
            [],
            new NarrativeEvidenceV2("Seizure", "Model accepted call.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [841143] = seizureTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([841143], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server derived source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversVehicleHitPoleWhenModelRejectsAllMembership()
    {
        const string transcript = "We're going to be the off ramp from 24 eastbound to 4th Avenue. Our RP is advising a vehicle fly off the grass and hit a pole. RP no longer on scene. Advising that it looked like the vehicle lights were on.";
        var hypothesis = Hypothesis(
            [843309],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(843309, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [843309] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([843309], decision.AcceptedCallIds);
        Assert.Equal("police", decision.Category);
        Assert.Contains("Hit a pole", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversRecklessDriverTrafficHazardWhenModelRejectsAllMembership()
    {
        const string transcript = "We need area 75 north from the 37, white BMW, driven all over the road, couldn't maintain speed, moved down 37.";
        var hypothesis = Hypothesis(
            [846783],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(846783, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [846783] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([846783], decision.AcceptedCallIds);
        Assert.Equal("traffic", decision.Category);
        Assert.Contains("Reckless driver", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DoesNotRecoverNegatedCrashReport()
    {
        const string transcript = "2345 We can't find no crash here, but I'll be here doing traffic control getting these cars out of here.";
        var hypothesis = Hypothesis(
            [844071],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(844071, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [844071] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Empty(decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted event-member calls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DoesNotRecoverPrivatePropertyOffenseReportAsCrash()
    {
        const string noCrashTranscript = "2345 We can't find no crash here, but I'll be here doing traffic control getting these cars out of here.";
        const string offenseReportTranscript = "Ma'am, if that's the incident happened on private property at that racetrack, we will do an offense report with its private property, there won't be a crash report.";
        var hypothesis = Hypothesis(
            [844071, 844209],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(844071, "primary_event", "accept", ["model accepted negated crash"], []),
                new MembershipEvidenceV2(844209, "primary_event", "accept", ["model accepted offense report as crash"], [])
            ],
            [],
            new NarrativeEvidenceV2("Traffic crash", "Model accepted a non-crash report.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [844071] = noCrashTranscript,
                [844209] = offenseReportTranscript
            });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Empty(decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted call has source-backed primary event evidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DoesNotRecoverCrashReportAdministrationAsCrash()
    {
        const string reportTranscript = "Also, believe T3's that involved in that they was not doing crash reports. I believe it's supposed to be property damage reports that we're doing. 10 for her.";
        var hypothesis = Hypothesis(
            [844659],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(844659, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [844659] = reportTranscript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Empty(decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted event-member calls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DoesNotRecoverAdministrativeGunInformationAsWeaponEvent()
    {
        const string transcript = "4348, 47 star regional, any mileage 80561 and his information is in the gun. Thank you 139.";
        var hypothesis = Hypothesis(
            [845195],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(845195, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [845195] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Empty(decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted event-member calls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DoesNotRecoverStandaloneFirearmSerialNumberAsWeaponEvent()
    {
        const string transcript = "Traffic 20 on info. Copy the serial number on a firearm, please. Tango 6429-21. It's a black Stoker STR-9.";
        var hypothesis = Hypothesis(
            [844867],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(844867, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [844867] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Empty(decision.AcceptedCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted event-member calls", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversPoliceBoloWhenModelRejectsAllMembership()
    {
        const string routineTranscript = "Two Baker Fives. Two Fives. Yes, are we clear to contact 57? Two Fives, can you go with his number?";
        const string boloTranscript = "All units, stand by for a bow. If you'll look out for a 2014 Blue Chevy Impala, Tennessee Tag 499 Bravo Clean Frank Bravo. It should be driven by a Cory Bird Party involved in a domestic disorder from the Segal Select Only Highway. Any contacts stop, hold and notify Main for Charlie Channel.";
        var hypothesis = Hypothesis(
            [845127, 845239],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(845127, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(845239, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [845127] = routineTranscript,
                [845239] = boloTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([845239], decision.AcceptedCallIds);
        Assert.Equal("police", decision.Category);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversPrimaryEventFromSyntheticCandidateOnlyHypothesis()
    {
        const string breathingTranscript = "Walker Fire District 9 Memorial Medical 15 3782 Kensington Road. Earlier caller called back and advised sudden onset requesting EMS for difficulty breathing, severe difficulty breathing.";
        var hypothesis = Hypothesis(
            [842257],
            [],
            [],
            [],
            new NarrativeEvidenceV2("Rejected", "Model returned no hypothesis.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [842257] = breathingTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([842257], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.Contains("Difficulty breathing", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversPossibleOverdoseWhenModelRejectsAllMembership()
    {
        const string overdoseTranscript = "62728. 62728. You have 47 interrupt me as well. Possible overdose. 10-4.";
        var hypothesis = Hypothesis(
            [846451],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(846451, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [846451] = overdoseTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([846451], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.Contains("Overdose", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RetainsSameAddressOverdoseFollowUpWhenLocationTranscriptDiffers()
    {
        const string dispatchTranscript = "Palace Base Station 1, you're responding to 1940, Hixon Marina Road for drug overdose. Go Fireground 7, Fireground 7, 440.";
        const string followUpTranscript = "responding to 1940 Hickson Marina Road, 1940 Hickson River Road between Inland Ove Drive and Shelter Cove Drive, drug overdose of a 36-year-old male, patient inresponsive, going through alcohol withdrawal, breathing in slight labor, does have a history of seizures, will be at the end of the road, or is your med response?";
        var hypothesis = Hypothesis(
            [846739, 846755],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(846739, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(846755, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [846739] = dispatchTranscript,
                [846755] = followUpTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([846739, 846755], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.DoesNotContain(decision.Reasons, reason => reason.Contains("dropped disconnected primary event fallback", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversSevereBackPainWhenModelRejectsAllMembership()
    {
        const string backPainTranscript = "Walk Central Memorial 12, number 1400 Pine Road, 1400 Pine Road, B Rock City. Having 17 years of HML, complex severe back pain, provided someone did something heavy, and now she cannot move requesting an EMS check her out.";
        var hypothesis = Hypothesis(
            [841247],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(841247, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [841247] = backPainTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([841247], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversUnconsciousPatientFromNotResponsiveUpdate()
    {
        const string policeDispatchTranscript = "Taylor at Milny. RP says black male on his back, looks like he's breathing, has gashes on his head, halfway in the street.";
        const string fireDispatchTranscript = "Engine 4 for an unconscious Taylor Street at Millner.";
        const string updateTranscript = "He has a pulse and he is breathing. But he's not responsive. Exactly.";
        var hypothesis = Hypothesis(
            [847155, 847167, 847173],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(847155, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(847167, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(847173, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [847155] = policeDispatchTranscript,
                [847167] = fireDispatchTranscript,
                [847173] = updateTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([847167, 847173], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.Contains("Unconscious", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversStrokeVictimWhenModelRejectsAllMembership()
    {
        const string routineTranscript = "Any unit that can advise on all those, 1100 Carter Street at the Marriott.";
        const string strokeTranscript = "133, we're sending an EMS to 137 Bennett Drive. That's going to be Arrowhead. We've got an 80-year-old male, possible stroke victim, Cabin 19.";
        const string vandalismTranscript = "7 copy vandalism 311 Chestnut Street. There are multiple vehicles in the parking lot with their glass broken.";
        var hypothesis = Hypothesis(
            [847161, 847191, 847231],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(847161, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(847191, "unrelated", "reject", ["model rejected all membership"], []),
                new MembershipEvidenceV2(847231, "unrelated", "reject", ["model rejected all membership"], [])
            ],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string>
            {
                [847161] = routineTranscript,
                [847191] = strokeTranscript,
                [847231] = vandalismTranscript
            });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([847191], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.Contains("Stroke", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_RecoversDiabeticProblemWithoutOverdoseTitleWhenOverdoseIsNegated()
    {
        const string diabeticTranscript = "71 Central. This is a diabetic problem, not an overdose. Clearly diabetic problems, I'm going to take.";
        var hypothesis = Hypothesis(
            [847461],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(847461, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [847461] = diabeticTranscript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([847461], decision.AcceptedCallIds);
        Assert.Equal("ems", decision.Category);
        Assert.Contains("Diabetic", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Overdose", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(decision.Reasons, reason => reason.Contains("server retained source-backed primary event call", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DecideMany_SplitsDisconnectedMedicalEventsWithoutRequiringEveryEventToHaveLocation()
    {
        const string breathingDispatch = "Sale Creek, 2412 Leggett Road, have a 74 year old male respiratory distress.";
        const string breathingUpdate = "Showing responding to 2412 Leggett Road for respiratory distress.";
        const string strokeDispatch = "Squad 7 on a possible stroke alert.";
        const string strokeUpdate = "Medic responding to 4821 Sylvia Circle for slurred speech, may be having a seizure.";
        var hypothesis = Hypothesis(
            [848205, 848249, 848253, 848257],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [
                new MembershipEvidenceV2(848205, "primary_event", "accept", ["model accepted mixed medical window"], []),
                new MembershipEvidenceV2(848249, "primary_event", "accept", ["model accepted mixed medical window"], []),
                new MembershipEvidenceV2(848253, "continuation", "accept", ["model accepted mixed medical window"], []),
                new MembershipEvidenceV2(848257, "continuation", "accept", ["model accepted mixed medical window"], [])
            ],
            [],
            new NarrativeEvidenceV2("Medical", "Mixed medical window.", []));

        var decisions = IncidentEvidenceDecisionEngineV2.DecideMany(
            hypothesis,
            new Dictionary<long, IncidentEvidenceCallContextV2>
            {
                [848205] = new(848205, breathingDispatch),
                [848249] = new(848249, strokeDispatch),
                [848253] = new(848253, breathingUpdate),
                [848257] = new(848257, strokeUpdate)
            });

        Assert.Equal(2, decisions.Count);
        Assert.Contains(decisions, decision => decision.AcceptedCallIds.SequenceEqual([848205, 848253]));
        Assert.Contains(decisions, decision => decision.AcceptedCallIds.SequenceEqual([848249, 848257]));
        Assert.All(decisions, decision => Assert.Empty(decision.RejectedCallIds));
    }

    [Fact]
    public void Decide_UsesTranscriptLocationInRecoveredTitle()
    {
        const string transcript = "Medic 4 responding for a seizure at 1934 Circle Drive Southwest.";
        var hypothesis = Hypothesis(
            [848601],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(848601, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [848601] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([848601], decision.AcceptedCallIds);
        Assert.Contains("Seizure", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1934 Circle Drive Southwest", decision.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decide_UsesSpecificGroundedTrafficTitleInsteadOfGenericCrash()
    {
        const string transcript = "Dispatch reports a tractor trailer blocking roadway at 1200 Hixson Pike.";
        var eventSpan = Span(848603, transcript, "tractor trailer blocking roadway");
        var factSpan = Span(848603, transcript, "tractor trailer blocking roadway");
        var hypothesis = Hypothesis(
            [848603],
            [new EventEvidenceV2("traffic_crash", "traffic_crash", "strong", [848603], [eventSpan])],
            [new MembershipEvidenceV2(848603, "primary_event", "accept", ["dispatch"], [])],
            [],
            new NarrativeEvidenceV2("Traffic", "Traffic hazard.", [new NarrativeFactV2("event", "tractor trailer blocking roadway", [factSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [848603] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Contains("Tractor trailer blocking roadway", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1200 Hixson Pike", decision.Title, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("Traffic crash", decision.Title);
    }

    [Fact]
    public void Decide_PendsLowInformationMedicalCodeInsteadOfCreatingIncident()
    {
        const string transcript = "Okay, let's count for code 73. Six, code 73, error 915.";
        var hypothesis = Hypothesis(
            [848589],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(848589, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [848589] = transcript });

        Assert.Equal("shadow_pending", decision.Decision);
        Assert.Empty(decision.AcceptedCallIds);
        Assert.Equal([848589], decision.PendingCallIds);
        Assert.Contains(decision.Reasons, reason => reason.Contains("no accepted call is a complete incident anchor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Decide_DoesNotRecoverWeaponEventFromBurgundyVehicleDescription()
    {
        const string transcript = "So I'm looking for a black or burgundy Mazda. Hey Tim, we're working here to stop. I'm not sure if she's actually on that road or if she's still driving around the neighborhood trying to find her daughter.";
        var hypothesis = Hypothesis(
            [848587],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(848587, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [848587] = transcript });

        Assert.Equal("shadow_reject", decision.Decision);
        Assert.Empty(decision.AcceptedCallIds);
        Assert.Null(decision.PendingCallIds);
    }

    [Fact]
    public void Decide_RecoversPedestrianTrafficHazardWhenModelRejectsAllMembership()
    {
        const string transcript = "I have a white male with a white t-shirt on, blue jeans. He was originally on Northbound St. Ivan, on the Southbound side. And he's crossing all lanes of traffic.";
        var hypothesis = Hypothesis(
            [848629],
            [new EventEvidenceV2("unknown", "unknown", "none", [], [])],
            [new MembershipEvidenceV2(848629, "unrelated", "reject", ["model rejected all membership"], [])],
            [],
            new NarrativeEvidenceV2("Rejected", "Model rejected.", []));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [848629] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal([848629], decision.AcceptedCallIds);
        Assert.Equal("traffic", decision.Category);
        Assert.Equal("Pedestrian traffic hazard", decision.Title);
    }

    [Fact]
    public void Decide_CategorizesShotsFiredAsPolice()
    {
        const string transcript = "Unit has a shots fired call at 1634 Rossville Avenue.";
        var factSpan = Span(809307, transcript, "shots fired");
        var hypothesis = Hypothesis(
            [809307],
            [new EventEvidenceV2("shots_fired", "shots_fired", "strong", [], [])],
            [new MembershipEvidenceV2(809307, "primary_event", "accept", ["dispatch"], [])],
            [],
            new NarrativeEvidenceV2("Shots fired", "Shots fired call.", [new NarrativeFactV2("event", "shots fired", [factSpan])]));

        var decision = IncidentEvidenceDecisionEngineV2.Decide(
            hypothesis,
            new Dictionary<long, string> { [809307] = transcript });

        Assert.Equal("shadow_accept", decision.Decision);
        Assert.Equal("police", decision.Category);
    }

    private static IncidentHypothesisV2 Hypothesis(
        IReadOnlyList<long> callIds,
        IReadOnlyList<EventEvidenceV2> events,
        IReadOnlyList<LocationEvidenceV2> locations,
        IReadOnlyList<MembershipEvidenceV2> membership,
        IReadOnlyList<ConflictEvidenceV2> conflicts,
        NarrativeEvidenceV2 narrative) => new(
        "hypothesis-1",
        "ot",
        callIds,
        0.9,
        events,
        locations,
        membership,
        conflicts,
        narrative);

    private static IncidentHypothesisV2 Hypothesis(
        IReadOnlyList<long> callIds,
        IReadOnlyList<EventEvidenceV2> events,
        IReadOnlyList<MembershipEvidenceV2> membership,
        IReadOnlyList<ConflictEvidenceV2> conflicts,
        NarrativeEvidenceV2 narrative) =>
        Hypothesis(callIds, events, [], membership, conflicts, narrative);

    private static EvidenceSpanV2 Span(long callId, string transcript, string text)
    {
        var start = transcript.IndexOf(text, StringComparison.Ordinal);
        Assert.True(start >= 0);
        return new EvidenceSpanV2(callId, start, start + text.Length, text);
    }
}
