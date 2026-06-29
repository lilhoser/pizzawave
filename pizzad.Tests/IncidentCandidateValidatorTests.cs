namespace pizzad.Tests;

public sealed class IncidentCandidateValidatorTests
{
    [Fact]
    public void Validate_RejectsRoutineComplianceRollupAcrossDifferentAddresses()
    {
        var calls = new[]
        {
            Call(1, 1000, "TBI command go ahead. Donald Ray Davis is in compliance at 25th Annis Road. 10-4."),
            Call(2, 1040, "TBI command go ahead. John Fields at 884 Millwood Road is in compliance. 10-4."),
            Call(3, 1080, "TBI command, 18 at Mount Calma Road. Contact made and compliant.")
        };

        var result = IncidentCandidateValidator.Validate(
            "TBI Command compliance updates",
            "Law mutual aid units report contact made and subjects in compliance at multiple addresses.",
            calls);

        Assert.False(result.IsValid);
        Assert.Contains("routine status/compliance rollup", result.Reason);
    }

    [Fact]
    public void ValidateEvent_RejectsGenericTrafficControlChatterWithoutEventAnchor()
    {
        var calls = new[]
        {
            Call(801161, 1781815585, "63, you can cancel 75. I'm going to take that on the street."),
            Call(801169, 1781815609, "35. Yeah. A few whole 6-7s traffic. 6-8 will take care of that one. She leaves at 8-8.")
        };

        var result = IncidentCandidateValidator.ValidateEvent(
            new IncidentEventValidationInput(
                "traffic_event",
                false,
                [],
                "street",
                "take that on the street"),
            calls);

        Assert.False(result.IsValid);
        Assert.Contains("generic traffic-control/status", result.Reason);
    }

    [Fact]
    public void ValidateEvent_AllowsTrafficControlWhenItSupportsConcreteCrash()
    {
        var calls = new[]
        {
            Call(1, 1000, "Engine responding to a crash with injuries at 7800 Causeway Road."),
            Call(2, 1030, "Deputy shutting down traffic at 7800 Causeway Road for the crash.")
        };

        var result = IncidentCandidateValidator.ValidateEvent(
            new IncidentEventValidationInput(
                "traffic_event",
                false,
                ["crash with injuries"],
                "7800 Causeway Road",
                "traffic shutdown for crash at 7800 Causeway Road"),
            calls);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void ValidateEvent_AllowsConcreteVehicleParkedInRoadHazard()
    {
        var result = IncidentCandidateValidator.ValidateEvent(
            new IncidentEventValidationInput(
                "traffic_event",
                false,
                [],
                "1720 Newcastle Drive Northeast",
                "vehicle parked in the middle of the road and a hazard"),
            [Call(801557, 1781817362, "Can you be in route 1720, Newcastle Drive, northeast? There's a vehicle there parking in the middle of the road. It's also a hazard.")]);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void ValidateEvent_AllowsVehicleVersusGuardrailCrashWithoutCrashWord()
    {
        var result = IncidentCandidateValidator.ValidateEvent(
            new IncidentEventValidationInput(
                "traffic_event",
                false,
                [],
                "I-24 east of the 163",
                "tractor trailer tanker versus guardrail blocking lane one"),
            [Call(801113, 1781815406, "I-24 east of the 163, partial 152.6. Now 45 is a tractor trailer tanker versus guardrail, blocking lane one. The 83 is nearby the duly with the trailer.")]);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void ValidateEvent_AllowsVehicleBurglaryAtConcreteAddress()
    {
        var result = IncidentCandidateValidator.ValidateEvent(
            new IncidentEventValidationInput(
                "public_safety_event",
                false,
                [],
                "3115 Dodson Avenue",
                "vehicle was burglarized and a walker was stolen"),
            [Call(801565, 1781819455, "3115 Dodson Avenue in reference to a vehicle that was burglarized at Dollar General. They stole her blue and black walker.")]);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void ValidateEvent_AllowsWindshieldDamageWithHighwayContext()
    {
        var result = IncidentCandidateValidator.ValidateEvent(
            new IncidentEventValidationInput(
                "traffic_event",
                false,
                [],
                "Highway 27 southbound near mile marker 19",
                "party threw something out of a truck and hit his windshield"),
            [Call(801471, 1781819450, "Highway 27 southbound around the 19 or 20 marker. A truck threw something out of their window and it hit his windshield and busted it.")]);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void ValidateEvent_AllowsTreeDownTrafficControlWithIntersectionContext()
    {
        var result = IncidentCandidateValidator.ValidateEvent(
            new IncidentEventValidationInput(
                "traffic_event",
                false,
                [],
                "Walnut Street and Daisy Dallas Road",
                "traffic control in reference to a tree down"),
            [Call(801263, 1781819440, "Highway department requesting assistance with traffic control at Walnut Street and Daisy Dallas Road in reference to this tree down.")]);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_AllowsSameAddressDispatchContinuation()
    {
        var calls = new[]
        {
            Call(1, 1000, "Engine responding to a crash with injuries at 7800 Causeway Road."),
            Call(2, 1040, "Deputy start to Causeway Road and Gold Point Circle for the e-bike crash."),
            Call(3, 1080, "Squad responding to 7800 block of Causeway Road for patient with facial injuries.")
        };

        var result = IncidentCandidateValidator.Validate(
            "E-bike crash with injuries at Causeway Road",
            "EMS, fire, and law responding to a crash at 7800 Causeway Road and Gold Point Circle.",
            calls);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_RejectsTitleAnchorSupportedByOnlyOneNoisyCall()
    {
        var calls = new[]
        {
            Call(1, 1000, "One, four, ten. [inaudible] [inaudible] go in there."),
            Call(2, 1300, "Okay, I wanted to check I show for you to service them. Thank you no further."),
            Call(3, 1600, "Maple Street. I think four of them in the corner. Tennessee Flight 601.")
        };

        var result = IncidentCandidateValidator.Validate(
            "Police Response at Maple Street",
            "Units responding around Maple Street.",
            calls);

        Assert.False(result.IsValid);
        Assert.True(
            result.Reason.Contains("only 1 call supports", StringComparison.OrdinalIgnoreCase)
            || result.Reason.Contains("only 1 call(s) match", StringComparison.OrdinalIgnoreCase),
            result.Reason);
    }

    [Fact]
    public void Validate_AllowsStrongSingleCallAfterPruningUnrelatedCandidateCalls()
    {
        var calls = new[]
        {
            Call(1, 1000, "Medic 14 respond to 5100 Highway 153 for an MVC with injuries, party possibly seizing."),
            Call(2, 1020, "Unit clear from routine patrol, no further."),
            Call(3, 1040, "Show me available after checking the station.")
        };

        var result = IncidentCandidateValidator.Validate(
            "MVC with injuries at 5100 Highway 153",
            "Single dispatch reports an MVC with injuries at 5100 Highway 153.",
            calls);

        Assert.True(result.IsValid, result.Reason);
        Assert.Contains("single-call actionable event after pruning", result.Reason);
        Assert.Equal([1L], result.Calls.Select(c => c.CallId).ToArray());
    }

    [Fact]
    public void IsLowConfidenceIncidentAcceptable_AllowsStrongSingleCallEmergencyWithConcreteAnchor()
    {
        var calls = new[]
        {
            Call(1, 1000, "Medic 14 respond to 5100 Highway 153 for an MVC with injuries, party possibly seizing.")
        };

        Assert.True(IncidentCandidateValidator.IsLowConfidenceIncidentAcceptable(
            "MVC with injuries at 5100 Highway 153",
            "Single dispatch reports an MVC with injuries at 5100 Highway 153.",
            calls,
            0.58));
    }

    [Fact]
    public void IsLowConfidenceIncidentAcceptable_AllowsTwoStrongAnchoredCalls()
    {
        var calls = new[]
        {
            Call(1, 1000, "Engine responding to a crash with injuries at 7800 Causeway Road."),
            Call(2, 1040, "Deputy start to 7800 Causeway Road for the e-bike crash.")
        };

        Assert.True(IncidentCandidateValidator.IsLowConfidenceIncidentAcceptable(
            "Crash at 7800 Causeway Road",
            "Two units responding to the same crash.",
            calls,
            0.58));
    }

    [Fact]
    public void IsLowConfidenceIncidentAcceptable_RejectsSingleAnchoredNoisyRollup()
    {
        var calls = new[]
        {
            Call(1, 1000, "One, four, ten. [inaudible] [inaudible] go in there."),
            Call(2, 1300, "Okay, thank you no further."),
            Call(3, 1600, "Maple Street. I think four of them in the corner. Tennessee Flight 601.")
        };

        Assert.False(IncidentCandidateValidator.IsLowConfidenceIncidentAcceptable(
            "Police Response at Maple Street",
            "Units around Maple Street.",
            calls,
            0.50));
    }

    [Fact]
    public void Validate_UsesStoredHighwayMileMarkerAnchorForVehicleDescriptionDrift()
    {
        var anchors = new[]
        {
            new CallAnchorRecord(1, "highway_mile_marker", "i-75|mm:28", "I-75 MM 28", "deterministic", 0.98, "{}"),
            new CallAnchorRecord(2, "highway_mile_marker", "i-75|mm:28", "I-75 MM 28", "deterministic", 0.98, "{}")
        };
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "Engine responding to I-75 for an Odyssey MVA with injury.", "fire", "Fire Dispatch", "test", [anchors[0]]),
            new IncidentCandidateCall(2, 1030, "Deputy checking the same crash involving a van at the 28.", "police", "Law Dispatch", "test", [anchors[1]])
        };

        var result = IncidentCandidateValidator.Validate(
            "I-75 Honda Odyssey MVA",
            "Units are handling a crash at I-75 mile marker 28.",
            calls);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_RetainsCrossAgencyTrafficAndTransportForLargeVehicleFire()
    {
        var calls = new[]
        {
            Call(583823, 1780787110, "Looks like we've got a fireworks trailer on fire shooting off fireworks 75 north bound. Got two different locations, one saying the 13 saying the 11."),
            Call(583813, 1780787113, "Squad 7, vehicle fire. Interstate 75 North, set to 14."),
            Call(583821, 1780787134, "Multiple calls in reference to a truck with fireworks on it. 75 northbound, around 13.5 at this time."),
            Call(583831, 1780787185, "I'm going to run vehicle fire to help shut the interstate down."),
            Call(583839, 1780787203, "Will you start into this vehicle fire at the 13.5 75 northbound?"),
            Call(583869, 1780787246, "Vehicle fire, Interstate 75 Northbound at the 14. Possible fireworks vehicle. Driver has been removed. PD is also in route."),
            Call(583885, 1780787321, "Is it a crash or is it a vehicle fire? It's a vehicle fire. Fireworks in the back of the truck are exploding."),
            Call(583907, 1780787370, "Tango 21 vehicle fire interstate 75 North at the 14 mile marker, original fire ground 4."),
            Call(584025, 1780787657, "Traffic is shut down 75 north at the 14.4, a vehicle fire."),
            Call(584085, 1780787828, "Do y'all need any city units out there to help divert? Put them on exit 11 so people do not get on the interstate."),
            Call(584109, 1780787897, "Any unit on the vehicle fire 75 go to mutual aid one."),
            Call(584139, 1780787973, "I got two city units with traffic diverting off exit 11 now on the Lee Highway."),
            Call(584779, 1780790184, "Life Force is outbound with a male patient with burns to legs and hand, second degree, going to the ER.")
        };

        var result = IncidentCandidateValidator.Validate(
            "Vehicle Fire/Explosion on I-75 North at Mile 14",
            "Fire and police responding to a vehicle fire with fireworks explosions on Interstate 75 Northbound near mile marker 14.",
            calls);

        Assert.True(result.IsValid, result.Reason);
        Assert.Contains(583831, result.Calls.Select(c => c.CallId));
        Assert.Contains(584085, result.Calls.Select(c => c.CallId));
        Assert.Contains(584139, result.Calls.Select(c => c.CallId));
        Assert.Contains(584779, result.Calls.Select(c => c.CallId));
    }

    [Fact]
    public void Validate_RetainsLocationDispatchAndPatientDetailsForHighwayMvc()
    {
        var anchors = new[]
        {
            new CallAnchorRecord(637049, "highway_mile_marker", "i-75|mm:3", "I-75 MM 3", "deterministic", 0.98, "{}")
        };
        var calls = new[]
        {
            new IncidentCandidateCall(
                637003,
                1781053094,
                "2125, the three mile marker, Interstate 75, Southbound, three mile marker, 75 Southbound, between 153 and Eastwood Road.",
                "police",
                "HC CPD C",
                "test"),
            new IncidentCandidateCall(
                637049,
                1781053313,
                "N21 is going to be a mile marker 3, I-75 southbound, mile marker 3, interstate southbound between exit 2 and exit 3. Perfect, so with injuries, with at least one person that is requesting the EMS to be checked on them.",
                "fire",
                "HC CFD DISP",
                "test",
                [anchors[0]]),
            new IncidentCandidateCall(
                637117,
                1781053607,
                "Two-pops, two-pops, one-year-old female, left leg pain and a 24-year-old male with left hand pain that's lightly bleeding.",
                "police",
                "HC CPD C",
                "test")
        };

        var result = IncidentCandidateValidator.Validate(
            "MVC with injuries on I-75 SB at MM3, exit 2-3",
            "Units are responding to an MVC with injuries on I-75 southbound at mile marker 3.",
            calls);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(new long[] { 637003, 637049, 637117 }, result.Calls.Select(c => c.CallId).OrderBy(id => id));
    }

    [Fact]
    public void Validate_RejectsOperationalChatterWithoutStrongEmergencyAnchor()
    {
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "Reference 18, room 3106 says the AC is too high.", "other", "Facilities Ops", "test", IncidentEligible: false),
            new IncidentCandidateCall(2, 1040, "Maintenance copy, checking that work order.", "other", "Facilities Ops", "test", IncidentEligible: false)
        };

        var result = IncidentCandidateValidator.Validate(
            "Facilities maintenance issue",
            "Units discuss a maintenance work order.",
            calls);

        Assert.False(result.IsValid);
        Assert.Contains("operational", result.Reason);
    }

    [Fact]
    public void Validate_AllowsOperationalTalkgroupWhenStrongEmergencyAnchorExists()
    {
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "Maintenance reports smoke and fire in the mechanical room at 123 Main Street.", "other", "Facilities Ops", "test", IncidentEligible: false),
            new IncidentCandidateCall(2, 1030, "Security confirms smoke at 123 Main Street and requests fire response.", "other", "Security Ops", "test", IncidentEligible: false)
        };

        var result = IncidentCandidateValidator.Validate(
            "Fire response at 123 Main Street",
            "Facilities and security report smoke and fire in the same mechanical room.",
            calls);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_RejectsValetOperationalChatterWithoutStrongEmergencyAnchor()
    {
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "608 dropped off in the garage and is waiting on the elevator.", "ems", "Hospital Valet Parking", "test"),
            new IncidentCandidateCall(2, 1040, "Can we get a wheelchair for a patient waiting outside please?", "ems", "Hospital Valet Parking", "test")
        };

        var result = IncidentCandidateValidator.Validate(
            "EMS transport to hospital via valet park",
            "Valet units discuss garage drop-offs and wheelchair movement.",
            calls);

        Assert.False(result.IsValid);
        Assert.Contains("operational", result.Reason);
    }

    [Fact]
    public void Validate_AllowsValetTalkgroupWhenStrongEmergencyAnchorExists()
    {
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "Valet reports smoke and fire in the garage stairwell.", "other", "Hospital Valet Parking", "test"),
            new IncidentCandidateCall(2, 1030, "Fire response requested for smoke in the garage stairwell.", "fire", "Fire Dispatch", "test")
        };

        var result = IncidentCandidateValidator.Validate(
            "Fire response for smoke in garage stairwell",
            "Valet and fire dispatch report smoke in the same garage stairwell.",
            calls);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_RejectsStandaloneHospitalTransport()
    {
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "Medic 12 is inbound non-emergency to the hospital with an 84 year old female and an ETA of ten minutes.", "ems", "MedCom", "test"),
            new IncidentCandidateCall(2, 1030, "Patient report, vital signs are stable, blood pressure 145 over 80, oxygen 98 percent.", "ems", "Hospital Encode", "test")
        };

        var result = IncidentCandidateValidator.Validate(
            "EMS transport to hospital",
            "Ambulance is inbound with a patient report and ETA.",
            calls);

        Assert.False(result.IsValid);
        Assert.Contains("transport", result.Reason);
    }

    [Fact]
    public void Validate_AllowsTransportCallsWhenParentEventHasStrongAnchor()
    {
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "Units are on scene of a crash with injuries at mile marker 12.", "traffic", "Police Dispatch", "test"),
            new IncidentCandidateCall(2, 1030, "Medic 12 is transporting one patient from the crash at mile marker 12 to the hospital.", "ems", "MedCom", "test")
        };

        var result = IncidentCandidateValidator.Validate(
            "Crash with injuries at mile marker 12",
            "Police and EMS are handling the same crash with patient transport.",
            calls);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_RejectsStrongIncidentTitleWhenSourceCallsOnlyConfirmAddress()
    {
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "10-4 15912 Fort Campbell Boulevard.", "other", "Interop", "test"),
            new IncidentCandidateCall(2, 1030, "Confirm 15912 Fort Campbell Boulevard at the tobacco store.", "other", "Interop", "test")
        };

        var result = IncidentCandidateValidator.Validate(
            "MVC with entrapment at 15912 Fort Campbell Blvd",
            "EMS responding to an MVC with entrapment involving a white BMW.",
            calls);

        Assert.False(result.IsValid);
        Assert.Contains("unsupported emergency/event signal", result.Reason);
    }

    [Fact]
    public void Validate_RejectsStandbyOnlyMvaDispatchWithoutLocation()
    {
        var calls = new[]
        {
            new IncidentCandidateCall(1, 1000, "Station one, stand by for a motor vehicle accident with injuries.", "fire", "County EMS", "test"),
            new IncidentCandidateCall(2, 1030, "Station one, stand by for a motor vehicle accident with injuries.", "fire", "County Fire", "test")
        };

        var result = IncidentCandidateValidator.Validate(
            "MVA with injuries standby request at Station 1",
            "Dispatch requests station coverage for a motor vehicle accident.",
            calls);

        Assert.False(result.IsValid);
        Assert.Contains("standby-only", result.Reason);
    }

    [Fact]
    public void Validate_PrunesSourceCallsWithConflictingConcreteLocations()
    {
        var calls = new[]
        {
            Call(1, 1000, "Medic 1 responding to a seizure at 3938 Juniper Street."),
            Call(2, 1030, "Engine 4 on scene at 3938 Juniper Street for the seizure call."),
            Call(3, 1060, "Medic 2 responding to seizure at 5695 Middle Valley Road.")
        };

        var result = IncidentCandidateValidator.Validate(
            "Seizure at 3938 Juniper Street",
            "EMS and fire are responding to a seizure at 3938 Juniper Street.",
            calls);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal(new long[] { 1, 2 }, result.Calls.Select(c => c.CallId).OrderBy(id => id));
    }

    [Fact]
    public void Validate_RejectsTwoCallSameSymptomDifferentAddressJoin()
    {
        var calls = new[]
        {
            Call(1, 1000, "Medic 1 responding to a seizure at 3938 Juniper Street."),
            Call(2, 1030, "Medic 2 responding to seizure at 5695 Middle Valley Road.")
        };

        var result = IncidentCandidateValidator.Validate(
            "Seizure response",
            "EMS responding to seizure calls.",
            calls);

        Assert.False(result.IsValid);
        Assert.Contains("conflicting concrete", result.Reason);
    }

    [Fact]
    public void Validate_AllowsTransportContinuationWhenSharedMvcAnchorExists()
    {
        var calls = new[]
        {
            Call(1, 1000, "Deputy is on scene with an MVC with injury at I-75 mile marker 28."),
            Call(2, 1040, "Medic 4 is transporting one patient from the crash at I-75 mile marker 28 to Erlanger.")
        };

        var result = IncidentCandidateValidator.Validate(
            "MVC with injury at I-75 mile marker 28",
            "Deputy and EMS are handling the same crash with patient transport.",
            calls);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_AllowsSingleCallMvcWithConcreteAnchor()
    {
        var result = IncidentCandidateValidator.Validate(
            "MVC with injuries at Highway 153",
            "Single dispatch reports an MVC with injuries at 5100 Highway 153.",
            [Call(1, 1000, "Medic 14 respond to 5100 Highway 153 for an MVC with injuries, party possibly seizing.")]);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal("single-call actionable event with concrete anchor", result.Reason);
        Assert.Equal(1, result.Calls.Single().CallId);
    }

    [Fact]
    public void Validate_AllowsSingleCallHarassmentComplaintWithConcreteAnchor()
    {
        var result = IncidentCandidateValidator.Validate(
            "Harassment complaint at 2045 Hampton Drive",
            "Caller requests in-person support for online harassment and threats at 2045 Hampton Drive.",
            [Call(1, 1000, "Caller at 2045 Hampton Drive is requesting in-person support about a person harassing her on Facebook.")]);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal("single-call actionable event with concrete anchor", result.Reason);
    }

    [Fact]
    public void Validate_AllowsSingleCallBoloWithVehicleTag()
    {
        var result = IncidentCandidateValidator.Validate(
            "BOLO for suspect vehicle",
            "Attempt to locate a red Toyota with license tag ABC123.",
            [Call(1, 1000, "All units BOLO for a red Toyota, license tag ABC123, wanted in reference to a robbery.")]);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_AllowsSingleCallWelfareCheckWithConcretePersonAndAddress()
    {
        var result = IncidentCandidateValidator.Validate(
            "Welfare check at 1246 Lewis Street Northeast",
            "Deputy is dispatched to check on Norma Waters at 1246 Lewis Street Northeast.",
            [Call(1, 1000, "Can you take a welfare check? 1246 Lewis Street Northeast, checking on Norma Waters. She is not making sense and may be stuck somewhere.")]);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal("single-call actionable event with concrete anchor", result.Reason);
    }

    [Fact]
    public void Validate_AllowsSingleCallVehicleHitPoleWithConcreteAddress()
    {
        var result = IncidentCandidateValidator.Validate(
            "Vehicle hit pole at 1928 Central Avenue",
            "Dispatch reports a black Wrangler hit a pole at 1928 Central Avenue.",
            [Call(1, 1000, "1928 Central Avenue off East 17th Street. It is for a black Wrangler that hit a pole.")]);

        Assert.True(result.IsValid, result.Reason);
        Assert.Equal("single-call actionable event with concrete anchor", result.Reason);
    }

    [Fact]
    public void HasStrongIncidentSignal_DetectsBrokenDownVehicleRoadwayHazard()
    {
        const string transcript = "Charlie 501 called his friend is broken down there, almost at the Hamilton County line, but at the top of the hill in a bad spot until the tour gets there.";

        Assert.True(IncidentCandidateValidator.HasStrongIncidentSignal(transcript));
    }

    [Fact]
    public void Validate_RejectsSingleCallWhenOnlyAnchorIsWeakLocationHint()
    {
        var anchors = new[]
        {
            new CallAnchorRecord(1, "location", "test|is there a lane", "Is There A Lane", "location_hint", 0.55, "{}")
        };
        var result = IncidentCandidateValidator.Validate(
            "Vehicle fire reported",
            "Caller reports a tractor trailer on fire on the shoulder.",
            [new IncidentCandidateCall(1, 1000, "Caller advises tractor trailer on fire on the right hand shoulder.", "fire", "Fire Dispatch", "test", anchors)]);

        Assert.False(result.IsValid);
        Assert.Contains("concrete", result.Reason);
    }

    [Fact]
    public void Validate_AllowsSingleCallWithModelEventHighwayMileMarkerAnchor()
    {
        var anchors = new[]
        {
            new CallAnchorRecord(1, "highway_mile_marker", "i-75|mm:350", "I-75 MM 350 NB", "model_event_location", 0.86, "{}")
        };
        var result = IncidentCandidateValidator.Validate(
            "Vehicle fire reported",
            "Caller reports a tractor trailer on fire on the shoulder.",
            [new IncidentCandidateCall(1, 1000, "Caller advises tractor trailer on fire on the right hand shoulder.", "fire", "Fire Dispatch", "test", anchors)]);

        Assert.True(result.IsValid, result.Reason);
    }

    [Fact]
    public void Validate_RejectsSingleCallTransportOnlyIncident()
    {
        var result = IncidentCandidateValidator.Validate(
            "Transport to Erlanger",
            "Medic is inbound to Erlanger with patient report.",
            [new IncidentCandidateCall(1, 1000, "Medic 12 inbound to Erlanger with stable vitals and ETA ten minutes.", "ems", "MedCom", "test")]);

        Assert.False(result.IsValid);
        Assert.Contains("transport", result.Reason);
    }

    [Fact]
    public void Validate_RejectsSingleCallStrongSignalWithoutConcreteAnchor()
    {
        var result = IncidentCandidateValidator.Validate(
            "MVC with injuries",
            "Single dispatch reports a crash.",
            [Call(1, 1000, "Caller reports an MVC with injuries somewhere in the county and requests response.")]);

        Assert.False(result.IsValid);
        Assert.Contains("concrete", result.Reason);
    }

    [Fact]
    public void Validate_RejectsPatientAgeMisreadAsInterstateLocation()
    {
        var result = IncidentCandidateValidator.Validate(
            "EMS Stroke I-57 Rhea EMS",
            "Rhea EMS responding to I-57 for stroke symptoms.",
            [Call(1, 1000, "Ninety-five central. It's going to be 165, I-57 year old male showing signs of stroke, slurred speech.")]);

        Assert.False(result.IsValid);
        Assert.Contains("concrete", result.Reason);
    }

    [Fact]
    public void TranscriptLocationExtractor_DoesNotTreatPatientAgeAsInterstate()
    {
        var locations = TranscriptLocationService.ExtractLocations(
            "Ninety-five central. It's going to be 165, I-57 year old male showing signs of stroke.").ToArray();

        Assert.DoesNotContain(locations, location => location.Equals("I-57", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TranscriptLocationExtractor_DoesNotTreatCommandWordsAsStreetAddress()
    {
        var locations = TranscriptLocationService.ExtractLocations(
            "Engine 3, you are showing out 804, continue the street southeast. CPR in progress.").ToArray();

        Assert.DoesNotContain(locations, location => location.Contains("continue", StringComparison.OrdinalIgnoreCase));
    }

    private static IncidentCandidateCall Call(long id, long timestamp, string transcript) =>
        new(id, timestamp, transcript, "police", "LAW MA 01", "test");
}
