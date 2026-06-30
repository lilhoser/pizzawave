namespace pizzad.Tests;

public sealed class IncidentFrameBuilderV3Tests
{
    [Fact]
    public void Build_CreatesSingleCallFrameWithoutLocation()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(10, 1000, "Reported crash with possible injuries, units responding.");
        var candidates = new[]
        {
            Candidate(call, 0.6, "none")
        };

        var frames = builder.Build("test", [], candidates, [], 18);

        var frame = Assert.Single(frames);
        Assert.Equal("single_call", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal([10], frame.CandidateCallIds);
        Assert.Equal(string.Empty, frame.LocationLabel);
        Assert.Contains("Traffic crash", frame.Title);
    }

    [Fact]
    public void Build_AllowsOneCallToBelongToMultipleTentativeFrames()
    {
        var builder = new IncidentFrameBuilderV3();
        var call1 = Call(1, 1000, "Crash at Mahan Gap Road, engine responding.");
        var call2 = Call(2, 1040, "Crash reported at Mahan Gap Road and Highway 58.");
        var call3 = Call(3, 1080, "Disabled vehicle blocking Highway 58, request law enforcement.");
        var candidates = new[]
        {
            Candidate(call1, 0.8, "mahan gap road"),
            Candidate(call2, 0.7, "none"),
            Candidate(call3, 0.8, "highway 58")
        };

        var frames = builder.Build("test", [], candidates, [], 18);

        var summary = string.Join(Environment.NewLine, frames.Select(frame => $"{frame.FrameKey}: {string.Join(",", frame.CandidateCallIds)}"));
        Assert.True(frames.Count >= 2, summary);
        Assert.True(frames.Count(frame => frame.CandidateCallIds.Contains(2)) >= 2, summary);
        Assert.All(frames.Where(frame => frame.CandidateCallIds.Contains(2)), frame => Assert.Equal("ambiguous", frame.Lifecycle));
    }

    [Fact]
    public void Build_CollapsesDuplicateSeedsForSameLocatedEvent()
    {
        var builder = new IncidentFrameBuilderV3();
        var calls = new[]
        {
            Call(20, 1000, "Fire alarm sounding at 910 Magnolia Street."),
            Call(21, 1030, "Engine checking the alarm at 910 Magnolia Street."),
            Call(22, 1060, "Ladder responding to 910 Magnolia Street for the fire alarm.")
        };
        var candidates = calls.Select(call => Candidate(call, 0.8, "910 magnolia st")).ToList();

        var frames = builder.Build("test", [], candidates, [], 18);

        var frame = Assert.Single(frames);
        Assert.Equal("Fire response at 910 Magnolia St", frame.Title);
        Assert.Equal([20, 21, 22], frame.CandidateCallIds);
    }

    [Theory]
    [InlineData("hour down the gravel rd")]
    [InlineData("ve cleared the rd")]
    [InlineData("coverage management station")]
    [InlineData("2955 this to dr")]
    [InlineData("40 on dog pike")]
    [InlineData("900 coming rd")]
    [InlineData("80 rd")]
    public void Build_DoesNotUseWeakLocationPhraseAsTitleLocation(string weakLocation)
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(30, 1000, "Police checking a suspicious vehicle.");
        var candidates = new[]
        {
            Candidate(call, 0.8, weakLocation)
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal(string.Empty, frame.LocationLabel);
        Assert.DoesNotContain(TitleCaseForTest(weakLocation), frame.Title);
    }

    [Fact]
    public void Build_DoesNotTrustNumberedCommandPhraseLocation()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(40, 1000, "Patient having chest pain, medic responding.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "211 going to the live circle")
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("Chest pain", frame.Title);
        Assert.Equal(string.Empty, frame.LocationLabel);
    }

    [Fact]
    public void Build_UsesUngeocodedAddressAsDisplayTitleButNotTrustedLocation()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(41, 1000, "Patient having chest pain at 123 Main Street.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "123 main st", highConfidenceGeocode: false)
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("Chest pain near 123 Main St", frame.Title);
        Assert.Equal(string.Empty, frame.LocationLabel);
        Assert.False(frame.LocationIsHighConfidenceGeocode);
        Assert.DoesNotContain("trusted_location", string.Join(' ', builder.BuildResolverDecisions([frame], candidates).SelectMany(decision => decision.MembershipEvidence)));
    }

    [Fact]
    public void Build_UsesTranscriptAddressAnchorAsDisplayTitle()
    {
        var builder = new IncidentFrameBuilderV3();
        var calls = new[]
        {
            Call(44, 1000, "Respond to 172 Depot Street for chest pain and difficulty breathing."),
            Call(45, 1040, "Medic responding to 172 Depot Street, patient is pale.")
        };
        var candidates = calls.Select(call => Candidate(call, 0.8, "none")).ToList();

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("Chest pain near 172 Depot St", frame.Title);
        Assert.Equal(string.Empty, frame.LocationLabel);
        Assert.False(frame.LocationIsHighConfidenceGeocode);
    }

    [Fact]
    public void Build_DoesNotUseUngeocodedHighwayAsTrustedLocation()
    {
        var builder = new IncidentFrameBuilderV3();
        var calls = new[]
        {
            Call(42, 1000, "Welfare check near I 952."),
            Call(43, 1030, "Another unit checking the same welfare call.")
        };
        var candidates = calls
            .Select(call => Candidate(call, 0.8, "i 952", highConfidenceGeocode: false))
            .ToList();

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("Welfare check", frame.Title);
        Assert.Equal(string.Empty, frame.LocationLabel);
        Assert.False(frame.LocationIsHighConfidenceGeocode);
    }

    [Fact]
    public void Build_CreatesBrokenDownVehicleFrameFromUngeocodedRoadHint()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(
            935863,
            1000,
            "It's going to be here's some pike and old, I'm sorry, Owl Hollow Road southwest. Charlie 501 called his friend is broken down there, almost at the Hamilton County line, but at the top of the hill in a bad spot and it was just like some weird concept until the tour gets there.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "owl hollow road southwest", highConfidenceGeocode: false)
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("Broken down vehicle on Owl Hollow Road Southwest near Hamilton County line", frame.Title);
        Assert.Equal("single_call", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal(string.Empty, frame.LocationLabel);
        Assert.False(frame.LocationIsHighConfidenceGeocode);
        Assert.Contains(frame.Anchors, anchor => anchor == "road_hint:owl hollow road southwest");

        var resolver = Assert.Single(builder.BuildResolverDecisions([frame], candidates));
        Assert.Equal("attach_new", resolver.Decision);
        Assert.Contains("specific_roadway_hazard=25", resolver.MembershipEvidence);
        Assert.DoesNotContain("trusted_location=70", resolver.MembershipEvidence);

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([frame], [resolver]));
        Assert.Equal("create_new", plan.Action);
        Assert.Equal("Broken down vehicle on Owl Hollow Road Southwest near Hamilton County line", plan.Title);
        Assert.Equal([935863], plan.CallIds);
    }

    [Fact]
    public void Build_CreatesBrokenDownVehicleFrameFromTranscriptHighwayWhenLocationHintIsLogistics()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(
            942773,
            1000,
            "2011 vehicle broken down the roadway. The 178.8 of 24 Westbound, 178.8, 27, excuse me, 24 Westbound. On the shoulder is a vehicle broken down partially in the roadway. They have a tow truck on the way to them. Still waiting on a description of the vehicle. Use 53766, 53766.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "tow truck on the way", highConfidenceGeocode: false)
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("Broken down vehicle on I-24 Westbound near mile marker 178.8", frame.Title);
        Assert.Equal(string.Empty, frame.LocationLabel);
        Assert.False(frame.LocationIsHighConfidenceGeocode);
        Assert.DoesNotContain(frame.Anchors, anchor => anchor == "road_hint:tow truck on the way");
        Assert.Contains(frame.Anchors, anchor => anchor == "highway_mile_marker:i-24 westbound mm 178.8");

        var resolver = Assert.Single(builder.BuildResolverDecisions([frame], candidates));
        Assert.Equal("attach_new", resolver.Decision);
        Assert.Contains("specific_roadway_hazard=25", resolver.MembershipEvidence);
        Assert.DoesNotContain("trusted_location=70", resolver.MembershipEvidence);

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([frame], [resolver]));
        Assert.Equal("create_new", plan.Action);
        Assert.Equal("Broken down vehicle on I-24 Westbound near mile marker 178.8", plan.Title);
        Assert.Equal([942773], plan.CallIds);
    }

    [Fact]
    public void Build_MergesUnlocatedContinuationIntoTrustedLocationFrame()
    {
        var builder = new IncidentFrameBuilderV3();
        var dispatch = Call(50, 1000, "Difficulty breathing female at 1201 Hoffman Avenue.");
        var update = Call(51, 1040, "Medic update for 1 Hoffman Ave, patient still short of breath.");
        var candidates = new[]
        {
            Candidate(dispatch, 0.8, "1201 hoffman ave"),
            Candidate(update, 0.6, "none")
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("Difficulty breathing at 1201 Hoffman Ave", frame.Title);
        Assert.Equal([50, 51], frame.CandidateCallIds);
    }

    [Fact]
    public void Build_SuppressesGenericUnlocatedSingleton()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(60, 1000, "Engine responding to a fire alarm.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "none")
        };

        var frames = builder.Build("test", [], candidates, [], 18);

        Assert.Empty(frames);
    }

    [Fact]
    public void Build_SuppressesGenericUnlocatedMultiCallFrame()
    {
        var builder = new IncidentFrameBuilderV3();
        var first = Call(61, 1000, "Engine responding to a fire alarm.");
        var second = Call(62, 1040, "Second engine also responding for the alarm.");
        var candidates = new[]
        {
            Candidate(first, 0.8, "none"),
            Candidate(second, 0.8, "none")
        };

        var frames = builder.Build("test", [], candidates, [], 18);

        Assert.Empty(frames);
    }

    [Fact]
    public void Build_DoesNotMergeDifferentTrustedLocations()
    {
        var builder = new IncidentFrameBuilderV3();
        var first = Call(70, 1000, "Difficulty breathing female at 145 Chandler Road.");
        var second = Call(71, 1040, "Difficulty breathing male at 725 Glenwood Drive.");
        var candidates = new[]
        {
            Candidate(first, 0.8, "145 chandler rd"),
            Candidate(second, 0.8, "725 glenwood dr")
        };

        var frames = builder.Build("test", [], candidates, [], 18);

        Assert.Contains(frames, frame => frame.LocationLabel == "145 chandler rd" && frame.CandidateCallIds.SequenceEqual([70]));
        Assert.Contains(frames, frame => frame.LocationLabel == "725 glenwood dr" && frame.CandidateCallIds.SequenceEqual([71]));
        Assert.DoesNotContain(frames, frame => frame.CandidateCallIds.Count > 1);
    }

    [Fact]
    public void Build_UsesCanonicalLocationForTitle()
    {
        var builder = new IncidentFrameBuilderV3();
        var seed = Call(80, 1000, "Fall injury at 7 Dolls Avenue.");
        var related = Call(81, 1040, "Fall update, patient is stable.");
        var unrelatedLocation = Call(82, 1080, "Medical response at 201 East Street.");
        var candidates = new[]
        {
            Candidate(seed, 0.8, "7 dolls ave"),
            Candidate(related, 0.7, "none"),
            Candidate(unrelatedLocation, 0.6, "201 e st")
        };

        var frames = builder.Build("test", [], candidates, [], 18);

        var fall = Assert.Single(frames, frame => frame.FrameKey.Contains("7-dolls-ave", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("7 dolls ave", fall.LocationLabel);
        Assert.Equal("Fall at 7 Dolls Ave", fall.Title);
        Assert.Equal([80, 81], fall.CandidateCallIds);
    }

    [Fact]
    public void Build_CollapsesSameLocationMedicalFamilyFrames()
    {
        var builder = new IncidentFrameBuilderV3();
        var fall = Call(83, 1000, "Fall with injury at 1922 Eureka Drive.");
        var medical = Call(84, 1030, "Medic responding to the patient at 1922 Eureka Drive.");
        var fireMedical = Call(85, 1060, "Engine assisting EMS at 1922 Eureka Drive.");
        var candidates = new[]
        {
            Candidate(fall, 0.8, "1922 eureka dr"),
            Candidate(medical, 0.8, "1922 eureka dr"),
            Candidate(fireMedical, 0.8, "1922 eureka dr")
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("1922 eureka dr", frame.LocationLabel);
        Assert.Contains("medical:location-1922-eureka-dr", frame.FrameKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([83, 84, 85], frame.CandidateCallIds);
    }

    [Fact]
    public void Build_SuppressesGenericNearMalformedAnchorSingleton()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(90, 1000, "Police checking 3 with hospitals on the wrong way.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "none")
        };

        var frames = builder.Build("test", [], candidates, [], 18);

        Assert.Empty(frames);
    }

    [Fact]
    public void Build_MatchesCurrentIncidentWithEncodedCallId()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(877127, 1000, "Debris and pothole hazard on I-24 west near mile marker 178.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "none")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:877127:event",
                "active",
                "I-24 West debris and pothole hazard at mile markers 178 and 20",
                "traffic",
                ["C0000000D6247"],
                ["C0000000D6247"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal("llm:test:877127:event", frame.MatchedCurrentIncidentId);
    }

    [Fact]
    public void Build_EmitsCurrentMatchedFrameForWeakEncodedCallId()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(877457, 1000, "Unit twelve copy, checking.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "none")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:877457:event",
                "active",
                "Vehicle off roadway at I-75 MM 3",
                "police",
                ["C0000000D6391"],
                ["C0000000D6391"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal("llm:test:877457:event", frame.MatchedCurrentIncidentId);
        Assert.Equal([877457], frame.CandidateCallIds);
    }

    [Fact]
    public void Build_CollapsesFramesSharingCurrentIncidentMatch()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(101, 1000, "Unit one copies the detail."), 0.8, "none"),
            Candidate(Call(102, 1030, "Another unit checking that call."), 0.8, "none"),
            Candidate(Call(103, 1060, "Crash update, gray vehicle involved."), 0.8, "none")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "MVC I-75 SB near mile marker 10 involving gray GMC and green vehicle",
                "police",
                ["C000000000065", "C000000000066", "C000000000067"],
                ["C000000000065", "C000000000066", "C000000000067"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal("new", frame.MatchedCurrentIncidentId);
        Assert.Equal([101, 102, 103], frame.CandidateCallIds);
        Assert.StartsWith("current:new:mvc-i-75-sb-near-mile-marker-10", frame.FrameKey);
        Assert.Contains("merged current match", frame.Reason);
    }

    [Fact]
    public void Build_UsesActiveIncidentTargetWhenPlaceholderCurrentOverlapsManagedIncident()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(201, 1000, "Gravel on Dalton Pike blocking part of the roadway."), 0.8, "none"),
            Candidate(Call(202, 1030, "More gravel on Dalton Pike near the county line."), 0.8, "none")
        };
        var activeIncidents = new[]
        {
            new IncidentDto
            {
                Id = 4566,
                IncidentKey = "llm:test:201:event",
                Status = "active",
                Title = "Gravel on roadway Dalton Pike",
                Category = "public_works",
                Calls =
                [
                    IncidentCall(201, 1000, "Gravel on Dalton Pike blocking part of the roadway."),
                    IncidentCall(202, 1030, "More gravel on Dalton Pike near the county line.")
                ]
            }
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Gravel on roadway Dalton Pike",
                "public_works",
                ["C0000000000C9", "C0000000000CA"],
                ["C0000000000C9", "C0000000000CA"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", activeIncidents, candidates, current, 18));
        var resolverDecisions = builder.BuildResolverDecisions([frame], candidates);
        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([frame], resolverDecisions));

        Assert.Equal("active:4566", frame.MatchedCurrentIncidentId);
        Assert.All(resolverDecisions, decision => Assert.Equal("active:4566", decision.WouldAttachCurrentIncidentId));
        Assert.Equal("active:4566", plan.TargetIncidentId);
        Assert.Equal("update_current", plan.Action);
    }

    [Fact]
    public void Build_UsesActiveIncidentTargetWhenModelCurrentOverlapsManagedIncident()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(211, 1000, "Residential fire at 6906 Lake Breeze Drive."), 0.8, "6906 lake breeze dr"),
            Candidate(Call(212, 1030, "Engine reports garage involvement at 6906 Lake Breeze Drive."), 0.8, "6906 lake breeze dr")
        };
        var activeIncidents = new[]
        {
            new IncidentDto
            {
                Id = 4567,
                IncidentKey = "llm:test:211:event",
                Status = "active",
                Title = "Residential fire at 6906 Lake Breeze Dr",
                Category = "fire",
                Calls =
                [
                    IncidentCall(211, 1000, "Residential fire at 6906 Lake Breeze Drive."),
                    IncidentCall(212, 1030, "Engine reports garage involvement at 6906 Lake Breeze Drive.")
                ]
            }
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:211:event",
                "active",
                "Residential fire at 6906 Lake Breeze Dr",
                "fire",
                ["211", "212"],
                ["211", "212"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", activeIncidents, candidates, current, 18));
        var resolverDecisions = builder.BuildResolverDecisions([frame], candidates);
        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([frame], resolverDecisions));

        Assert.Equal("active:4567", frame.MatchedCurrentIncidentId);
        Assert.All(resolverDecisions, decision => Assert.Equal("active:4567", decision.WouldAttachCurrentIncidentId));
        Assert.Equal("active:4567", plan.TargetIncidentId);
        Assert.Equal("update_current", plan.Action);
    }

    [Fact]
    public void Build_PrefersActiveIncidentAnchorMatchOverModelCurrentAnchorMatch()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(221, 1000, "Fire alarm at 100 Main Street."), 0.8, "100 main st")
        };
        var activeIncidents = new[]
        {
            new IncidentDto
            {
                Id = 4568,
                IncidentKey = "llm:test:900:event",
                Status = "active",
                Title = "Fire alarm at 100 Main St",
                Detail = "Caller reports alarms sounding.",
                Category = "fire",
                Calls =
                [
                    IncidentCall(900, 940, "Fire alarm at 100 Main Street.")
                ]
            }
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:800:event",
                "active",
                "Fire alarm at 100 Main St",
                "fire",
                ["800"],
                ["800"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", activeIncidents, candidates, current, 18));
        var resolverDecisions = builder.BuildResolverDecisions([frame], candidates);
        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([frame], resolverDecisions));

        Assert.Equal("active:4568", frame.MatchedCurrentIncidentId);
        Assert.All(resolverDecisions, decision => Assert.Equal("active:4568", decision.WouldAttachCurrentIncidentId));
        Assert.Equal("active:4568", plan.TargetIncidentId);
        Assert.Equal("hold_pending", plan.Action);
        Assert.Contains("planDroppedBecause=single_call_current_update_unproven", plan.Reason);
    }

    [Fact]
    public void Build_UsesActiveIncidentTargetWhenModelCurrentKeyMatchesManagedIncidentWithoutCallOverlap()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(222, 1000, "Engine update for fire alarm at 100 Main Street."), 0.8, "100 main st")
        };
        var activeIncidents = new[]
        {
            new IncidentDto
            {
                Id = 4569,
                IncidentKey = "llm:test:900:event",
                Status = "active",
                Title = "Fire alarm at 100 Main St",
                Detail = "Alarm company callback.",
                Category = "fire",
                Calls =
                [
                    IncidentCall(900, 940, "Fire alarm at 100 Main Street.")
                ]
            }
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:900:event",
                "active",
                "Fire alarm at 100 Main St",
                "fire",
                ["222"],
                ["222"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", activeIncidents, candidates, current, 18));
        var resolverDecisions = builder.BuildResolverDecisions([frame], candidates);
        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([frame], resolverDecisions));

        Assert.Equal("active:4569", frame.MatchedCurrentIncidentId);
        Assert.Equal("active:4569", plan.TargetIncidentId);
        Assert.Equal("hold_pending", plan.Action);
        Assert.Contains("planDroppedBecause=single_call_current_update_unproven", plan.Reason);
    }

    [Fact]
    public void Build_UsesActiveIncidentTargetWhenPlaceholderCurrentSharesManagedIncidentAnchorWithoutCallOverlap()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(223, 1000, "Engine update for fire alarm at 100 Main Street."), 0.8, "100 main st")
        };
        var activeIncidents = new[]
        {
            new IncidentDto
            {
                Id = 4570,
                IncidentKey = "llm:test:901:event",
                Status = "active",
                Title = "Fire alarm at 100 Main St",
                Detail = "Alarm company callback.",
                Category = "fire",
                Calls =
                [
                    IncidentCall(901, 940, "Fire alarm at 100 Main Street.")
                ]
            }
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Fire alarm at 100 Main St",
                "fire",
                ["223"],
                ["223"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", activeIncidents, candidates, current, 18));
        var resolverDecisions = builder.BuildResolverDecisions([frame], candidates);
        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([frame], resolverDecisions));

        Assert.Equal("active:4570", frame.MatchedCurrentIncidentId);
        Assert.Equal("active:4570", plan.TargetIncidentId);
        Assert.Equal("hold_pending", plan.Action);
        Assert.Contains("planDroppedBecause=single_call_current_update_unproven", plan.Reason);
    }

    [Fact]
    public void Build_CollapsedCurrentMatchKeepsCurrentAlignedLocationAndAmbiguousConflict()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(120, 1000, "Police disturbance at 5700 Main Street."), 0.8, "5700 main st"),
            Candidate(Call(121, 1030, "Police disturbance at 23 Quail Mountain Drive."), 0.8, "23 quail mountain dr")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Child in yard screaming, 23 Quail Mountain Dr",
                "police",
                ["C000000000078", "C000000000079"],
                ["C000000000078", "C000000000079"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildPromotionDecisions([frame]));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal("23 quail mountain dr", frame.LocationLabel);
        Assert.Contains("23 Quail Mountain Dr", frame.Title);
        Assert.DoesNotContain("5700 Main St", frame.Title);
        Assert.Equal("match_current_with_ambiguous_members", decision.Action);
        Assert.Equal([121], decision.PromotedCallIds);
        Assert.Equal([120], decision.AmbiguousCallIds);
        Assert.Contains("collapseAmbiguousCallIds=120", frame.Reason);
    }

    [Fact]
    public void Build_CollapsedCurrentMatchFlagsInternalLocationConflictWithoutCurrentLocation()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(130, 1000, "Fall patient at 16 Georgia Circle Northwest."), 0.8, "16 georgia circle nw"),
            Candidate(Call(131, 1030, "Police on I-75 at mile marker 1 checking the same fall transport."), 0.8, "i 75")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:130:event",
                "active",
                "Fall patient transport to Bradley Medical Center",
                "ems",
                ["C000000000082", "C000000000083"],
                ["C000000000082", "C000000000083"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildPromotionDecisions([frame]));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("conflicted", frame.Lifecycle);
        Assert.Equal("match_current_location_disagreement", decision.Action);
        Assert.Contains("lifecycle=conflicted", frame.Reason);
    }

    [Fact]
    public void Build_DoesNotConflictBroadHighwayWithSameHighwayMileMarker()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(140, 1000, "Disabled vehicle on I-75, law checking.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "i 75")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Disabled vehicle on I-75 near mile marker 42",
                "traffic",
                ["C00000000008C"],
                ["C00000000008C"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildPromotionDecisions([frame]));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal("match_current", decision.Action);
    }

    [Fact]
    public void Build_AssignsPendingLifecycleToGenericLocatedSingleton()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(150, 1000, "Engine responding to check the area at 123 Main Street.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "123 main st")
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, [], 18));

        Assert.Equal("single_call", frame.Maturity);
        Assert.Equal("pending", frame.Lifecycle);
        Assert.Equal("123 main st", frame.LocationLabel);
    }

    [Fact]
    public void Build_FlagsConflictedLifecycleWhenCurrentMatchLocationDisagrees()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(160, 1000, "Chest pain patient at 100 Alpha Road.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "100 alpha rd")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:160:event",
                "active",
                "Chest pain at 200 Beta Road",
                "fire",
                ["C0000000000A0"],
                ["C0000000000A0"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("conflicted", frame.Lifecycle);
        Assert.Equal("llm:test:160:event", frame.MatchedCurrentIncidentId);
    }

    [Fact]
    public void Build_FlagsPostFrameCurrentMatchLocationDisagreement()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(165, 1000, "Police responding to 800 Harper Road for smoke from a tractor trailer."), 0.8, "800 harper rd")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:165:event",
                "active",
                "Smoke/Tractor Trailer on Eastburn and Rodenly Highway",
                "police",
                ["C0000000000A5"],
                ["C0000000000A5"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildPromotionDecisions([frame]));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("conflicted", frame.Lifecycle);
        Assert.Equal("match_current_location_disagreement", decision.Action);
        Assert.Contains("lifecycle=conflicted", frame.Reason);
    }

    [Fact]
    public void Build_FlagsUngeocodedCurrentCallAddressConflictForDetach()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(167, 1000, "Automatic fire alarm at 2202 Fairmont Pike.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "2202 fairmont pike", highConfidenceGeocode: false)
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Automatic Fire Alarm at 2502 Fairmont Pike",
                "fire",
                ["167"],
                ["167"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildResolverDecisions([frame], candidates));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("conflicted", frame.Lifecycle);
        Assert.Equal("detach_create", decision.Decision);
        Assert.Equal("strong_location_conflicts_with_current", decision.DecisionReason);
        Assert.True(decision.WouldDetachCreate);
    }

    [Fact]
    public void Build_FlagsFrameTitleRoadConflictForCurrentDetach()
    {
        var builder = new IncidentFrameBuilderV3();
        var call = Call(168, 1000, "MVC with injuries at Alton Park Boulevard and West 37th Street.");
        var candidates = new[]
        {
            Candidate(call, 0.8, "alton park blvd", highConfidenceGeocode: false)
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "active:430",
                "active",
                "MVC with possible entrapment at 5120 Highway 153",
                "traffic",
                ["C0000000000A8"],
                ["C0000000000A8"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildResolverDecisions([frame], candidates));

        Assert.Contains("Alton Park", frame.Title);
        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("conflicted", frame.Lifecycle);
        Assert.Equal("detach_create", decision.Decision);
        Assert.Equal("strong_location_conflicts_with_current", decision.DecisionReason);
    }

    [Fact]
    public void Build_DoesNotMergeUngeocodedConflictingAddressIntoCurrentUpdate()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(168, 1000, "Automatic fire alarm at 2502 Fairmont Pike."), 0.8, "2502 fairmont pike", highConfidenceGeocode: false),
            Candidate(Call(169, 1040, "Automatic fire alarm at 2202 Fairmont Pike."), 0.8, "2202 fairmont pike", highConfidenceGeocode: false)
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Automatic Fire Alarm at 2502 Fairmont Pike",
                "fire",
                ["168"],
                ["168"],
                [])
        };

        var frames = builder.Build("test", [], candidates, current, 18);
        var plans = builder.BuildIncidentPlanDecisions(frames, builder.BuildResolverDecisions(frames, candidates));

        Assert.Contains(frames, frame =>
            frame.CandidateCallIds.SequenceEqual([168]) &&
            frame.MatchedCurrentTitle == "Automatic Fire Alarm at 2502 Fairmont Pike");
        Assert.DoesNotContain(frames, frame =>
            frame.CandidateCallIds.Contains(168) &&
            frame.CandidateCallIds.Contains(169));
        Assert.DoesNotContain(plans, plan =>
            plan.Action == "update_current" &&
            plan.CallIds.Contains(169));
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsUnmatchedCreateWithConflictingMemberLocations()
    {
        var builder = new IncidentFrameBuilderV3();
        var conflictingFrame = new IncidentFrameV3(
            "conflicting-location-frame",
            "medical:address-7673-north-bishop-dr",
            "multi_call",
            "mature",
            "Difficulty breathing near 7673 North Bishop Dr",
            "ems",
            "",
            [170, 171],
            [],
            ["address:7673 north bishop dr", "address:72 north bishop dr"],
            "",
            "",
            "",
            "test")
        {
            HasConflictingMemberLocations = true
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(170, "attach_new", conflictingFrame.FrameId, wouldCreateIncident: true),
            ResolverDecision(171, "attach_new", conflictingFrame.FrameId, wouldCreateIncident: true)
        };

        var plans = builder.BuildIncidentPlanDecisions([conflictingFrame], resolverDecisions);

        var conflictingPlan = Assert.Single(plans);
        Assert.Equal("hold_pending", conflictingPlan.Action);
        Assert.Equal([170, 171], conflictingPlan.CallIds);
        Assert.Contains("planDroppedBecause=conflicting_member_locations", conflictingPlan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsCurrentUpdateWithConflictingMemberLocations()
    {
        var builder = new IncidentFrameBuilderV3();
        var conflictingFrame = new IncidentFrameV3(
            "conflicting-current-frame",
            "current:active-430:mvc-highway-153",
            "current_matched",
            "mature",
            "Traffic crash near Alton Park Blvd",
            "traffic",
            "alton park blvd",
            [108947, 109013, 109291],
            [],
            ["address:5120 highway 153", "location:alton park blvd"],
            "active:430",
            "MVC with possible entrapment at 5120 Highway 153",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [108935, 108941, 108943, 108945],
            HasConflictingMemberLocations = true
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(108947, "attach_current", conflictingFrame.FrameId, wouldAttachCurrentIncidentId: "active:430"),
            ResolverDecision(109013, "attach_current", conflictingFrame.FrameId, wouldAttachCurrentIncidentId: "active:430"),
            ResolverDecision(109291, "attach_current", conflictingFrame.FrameId, wouldAttachCurrentIncidentId: "active:430")
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([conflictingFrame], resolverDecisions));

        Assert.Equal("hold_pending", plan.Action);
        Assert.Equal("active:430", plan.TargetIncidentId);
        Assert.Equal([108947, 109013, 109291], plan.CallIds);
        Assert.Contains("planDroppedBecause=conflicting_member_locations", plan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsGenericCurrentUpdateWithNewCalls()
    {
        var builder = new IncidentFrameBuilderV3();
        var genericCurrentFrame = new IncidentFrameV3(
            "generic-current-frame",
            "current:active-448:commercial-fire-alarm",
            "current_matched",
            "mature",
            "Fire response",
            "fire",
            "",
            [115191, 115233, 115241],
            [],
            [],
            "active:448",
            "Commercial fire alarm at 16 Hickson Pike",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [115191],
            MatchedCurrentCategory = "fire"
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(115233, "attach_current", genericCurrentFrame.FrameId, wouldAttachCurrentIncidentId: "active:448"),
            ResolverDecision(115241, "attach_current", genericCurrentFrame.FrameId, wouldAttachCurrentIncidentId: "active:448")
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([genericCurrentFrame], resolverDecisions));

        Assert.Equal("hold_pending", plan.Action);
        Assert.Equal("active:448", plan.TargetIncidentId);
        Assert.Equal([115233, 115241], plan.CallIds);
        Assert.Contains("planDroppedBecause=generic_current_update_unproven", plan.Reason);
    }

    [Fact]
    public void Build_DoesNotFlagEquivalentCurrentMatchLocationText()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(166, 1000, "Fire response at 53 and Howard School Road for an injury."), 0.8, "53 and howard school rd")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "llm:test:166:event",
                "active",
                "Fire response to injury at 53 Howard School Rd",
                "fire",
                ["C0000000000A6"],
                ["C0000000000A6"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildPromotionDecisions([frame]));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal("match_current", decision.Action);
        Assert.DoesNotContain("currentLocationConflict", frame.Reason);
    }

    [Fact]
    public void Build_CollapsedCurrentMatchKeepsConflictingMemberAmbiguous()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(167, 1000, "Possible unconscious person at 100 Riverside Drive."), 0.8, "100 riverside dr"),
            Candidate(Call(168, 1030, "Fire response at 81 Oak Lane."), 0.8, "81 oak ln")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Possible unconscious person at 100 Riverside Dr",
                "police",
                ["C0000000000A7", "C0000000000A8"],
                ["C0000000000A7", "C0000000000A8"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildPromotionDecisions([frame]));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Equal("100 riverside dr", frame.LocationLabel);
        Assert.Equal([167, 168], frame.CandidateCallIds);
        Assert.Equal("match_current_with_ambiguous_members", decision.Action);
        Assert.Equal([167], decision.PromotedCallIds);
        Assert.Equal([168], decision.AmbiguousCallIds);
        Assert.Contains("collapseAmbiguousCallIds=168", frame.Reason);
        Assert.DoesNotContain("currentLocationConflict", frame.Reason);
    }

    [Fact]
    public void Build_IgnoresWeakIntersectionCurrentMatchDisagreement()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(169, 1000, "Reckless driver at Driver Bullet a Little Highway and Hollow Road."), 0.8, "none")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Reckless driver Ford Ranger on Little Highway at Hollow Road NE",
                "police",
                ["C0000000000A9"],
                ["C0000000000A9"],
                [])
        };

        var frame = Assert.Single(builder.Build("test", [], candidates, current, 18));
        var decision = Assert.Single(builder.BuildPromotionDecisions([frame]));

        Assert.Equal("current_matched", frame.Maturity);
        Assert.Equal("mature", frame.Lifecycle);
        Assert.Contains(frame.Anchors, anchor => anchor.Contains("driver bullet", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("match_current", decision.Action);
        Assert.Equal([169], decision.PromotedCallIds);
        Assert.Empty(decision.AmbiguousCallIds);
        Assert.DoesNotContain("currentLocationConflict", frame.Reason);
    }

    [Fact]
    public void BuildPromotionDecisions_MapsLifecycleToShadowActions()
    {
        var builder = new IncidentFrameBuilderV3();
        var matureUnmatched = builder.Build("test", [], [Candidate(Call(170, 1000, "Chest pain patient, medic responding."), 0.8, "none")], [], 18).Single();
        var pending = builder.Build("test", [], [Candidate(Call(171, 1000, "Engine responding to check the area at 123 Main Street."), 0.8, "123 main st")], [], 18).Single();
        var ambiguousFrames = builder.Build(
            "test",
            [],
            [
                Candidate(Call(172, 1000, "Crash at Mahan Gap Road, engine responding."), 0.8, "mahan gap road"),
                Candidate(Call(173, 1040, "Crash reported at Mahan Gap Road and Highway 58."), 0.7, "none"),
                Candidate(Call(174, 1080, "Disabled vehicle blocking Highway 58, request law enforcement."), 0.8, "highway 58")
            ],
            [],
            18);
        var currentMatched = builder.Build(
            "test",
            [],
            [Candidate(Call(175, 1000, "Debris and pothole hazard on I-24 west near mile marker 178."), 0.8, "none")],
            [
                new IncidentFrameCurrentIncidentV3(
                    "llm:test:175:event",
                    "active",
                    "I-24 West debris and pothole hazard at mile marker 178",
                    "traffic",
                    ["C0000000000AF"],
                    ["C0000000000AF"],
                    [])
            ],
            18).Single();
        var conflicted = builder.Build(
            "test",
            [],
            [Candidate(Call(176, 1000, "Chest pain patient at 100 Alpha Road."), 0.8, "100 alpha rd")],
            [
                new IncidentFrameCurrentIncidentV3(
                    "llm:test:176:event",
                    "active",
                    "Chest pain at 200 Beta Road",
                    "fire",
                    ["C0000000000B0"],
                    ["C0000000000B0"],
                    [])
            ],
            18).Single();
        var hardConflict = conflicted with
        {
            FrameId = "hard-conflict",
            MatchedCurrentIncidentId = "",
            MatchedCurrentTitle = ""
        };
        var frames = new[] { matureUnmatched, pending, currentMatched, conflicted }
            .Concat(ambiguousFrames.Where(frame => frame.Lifecycle == "ambiguous"))
            .Append(hardConflict)
            .ToList();

        var decisions = builder.BuildPromotionDecisions(frames);

        Assert.Contains(decisions, decision => decision.FrameId == matureUnmatched.FrameId && decision.Action == "create");
        Assert.Contains(decisions, decision => decision.FrameId == pending.FrameId && decision.Action == "hold_pending");
        Assert.Contains(decisions, decision => decision.FrameId == currentMatched.FrameId && decision.Action == "match_current");
        Assert.Contains(decisions, decision => decision.FrameId == conflicted.FrameId && decision.Action == "match_current_location_disagreement");
        Assert.Contains(decisions, decision => decision.FrameId == hardConflict.FrameId && decision.Action == "flag_conflict");
        Assert.Contains(decisions, decision => decision.Action == "mark_ambiguous");
    }

    [Fact]
    public void BuildPromotionDecisions_ExposesAmbiguousMembersOnCurrentMatch()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(180, 1000, "Crash at Mahan Gap Road, engine responding."), 0.8, "mahan gap road"),
            Candidate(Call(181, 1040, "Crash reported at Mahan Gap Road and Highway 58."), 0.7, "none"),
            Candidate(Call(182, 1080, "Disabled vehicle blocking Highway 58, request law enforcement."), 0.8, "highway 58")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Crash at Mahan Gap Road",
                "traffic",
                ["C0000000000B4"],
                ["C0000000000B4"],
                [])
        };

        var frames = builder.Build("test", [], candidates, current, 18);
        var decisions = builder.BuildPromotionDecisions(frames);

        var matched = Assert.Single(decisions, decision => decision.MatchedCurrentTitle == "Crash at Mahan Gap Road");
        Assert.Equal("match_current_with_ambiguous_members", matched.Action);
        Assert.Equal([180], matched.PromotedCallIds);
        Assert.Equal([181, 182], matched.AmbiguousCallIds);
        Assert.Contains(decisions, decision => decision.Action == "mark_ambiguous" && decision.CandidateCallIds.Contains(181));
    }

    [Fact]
    public void BuildPromotionDecisions_DoesNotPromoteSameCallToMultipleCurrentMatches()
    {
        var builder = new IncidentFrameBuilderV3();
        var snowHill = new IncidentFrameV3(
            "snow-hill",
            "traffic:location-5911-snow-hill-rd",
            "current_matched",
            "mature",
            "Traffic crash at 5911 Snow Hill Rd",
            "police",
            "5911 snow hill rd",
            [882105, 882225, 882329],
            [],
            ["address:5911 snow hill rd", "location:5911 snow hill rd"],
            "new",
            "MVC at 5911 Snow Hill Rd involving Chevy Equinox and Ford Escape",
            "active",
            "test");
        var pondRoad = new IncidentFrameV3(
            "pond-road",
            "traffic:location-449-pond-rd-near-falkland-dr",
            "current_matched",
            "mature",
            "Traffic crash at 449 Pond Rd Near Falkland Dr",
            "police",
            "449 pond rd near falkland dr",
            [882225, 882329],
            [],
            ["address:449 pond rd near falkland dr", "location:449 pond rd near falkland dr"],
            "new",
            "Highway Dept vehicle vs deer at 449 Pond Rd near Falkland Dr",
            "active",
            "test");

        var decisions = builder.BuildPromotionDecisions([snowHill, pondRoad]);

        var snowHillDecision = Assert.Single(decisions, decision => decision.FrameId == "snow-hill");
        var pondRoadDecision = Assert.Single(decisions, decision => decision.FrameId == "pond-road");
        Assert.Equal("match_current_with_ambiguous_members", snowHillDecision.Action);
        Assert.Equal([882105], snowHillDecision.PromotedCallIds);
        Assert.Equal([882225, 882329], snowHillDecision.AmbiguousCallIds);
        Assert.Equal("match_current", pondRoadDecision.Action);
        Assert.Equal([882225, 882329], pondRoadDecision.PromotedCallIds);
        Assert.Empty(pondRoadDecision.AmbiguousCallIds);
    }

    [Fact]
    public void BuildResolverDecisions_AttachesClearCurrentWinner()
    {
        var builder = new IncidentFrameBuilderV3();
        var currentFrame = new IncidentFrameV3(
            "current-frame",
            "medical:location-100-riverside-dr",
            "current_matched",
            "mature",
            "Unconscious person at 100 Riverside Dr",
            "ems",
            "100 riverside dr",
            [301],
            [],
            ["address:100 riverside dr", "location:100 riverside dr"],
            "llm:test:301:event",
            "Possible unconscious person at 100 Riverside Dr",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [301]
        };
        var weakFrame = new IncidentFrameV3(
            "weak-frame",
            "medical:calls-301",
            "single_call",
            "pending",
            "Medical response",
            "ems",
            "",
            [301],
            [],
            [],
            "",
            "",
            "",
            "test");

        var decision = Assert.Single(builder.BuildResolverDecisions(
            [currentFrame, weakFrame],
            [Candidate(Call(301, 1000, "Unconscious person at 100 Riverside Drive."), 0.8, "100 riverside dr")]));

        Assert.Equal(301, decision.CallId);
        Assert.Equal("current-frame", decision.WinningFrameId);
        Assert.Equal("attach_current", decision.Decision);
        Assert.Equal("llm:test:301:event", decision.WouldAttachCurrentIncidentId);
        Assert.False(decision.WouldCreateIncident);
        Assert.Contains(decision.MembershipEvidence, value => value == "current_overlap=100");
    }

    [Fact]
    public void BuildResolverDecisions_CurrentOverlapOnlyScoresOwningCurrentIncident()
    {
        var builder = new IncidentFrameBuilderV3();
        var medicalFrame = new IncidentFrameV3(
            "medical-current-frame",
            "current:medical",
            "current_matched",
            "mature",
            "Medical response",
            "ems",
            "",
            [915127, 915215, 915257, 915295, 915305, 915427],
            [],
            ["address:3360 hwy"],
            "llm:test:915181:event",
            "Medical call: 65yo reaction to medication at 3360 Hwy 411 N",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [915127, 915215],
            MatchedCurrentCategory = "ems"
        };
        var fireFrame = new IncidentFrameV3(
            "fire-current-frame",
            "fire:calls-915257-915295-915305-915427",
            "current_matched",
            "mature",
            "Fire response",
            "fire",
            "",
            [915257, 915295, 915305, 915427],
            [],
            [],
            "new",
            "Smoke detector alarm at Walker Valley High School",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [915305, 915427],
            MatchedCurrentCategory = "fire"
        };

        var decisions = builder.BuildResolverDecisions(
            [medicalFrame, fireFrame],
            [
                Candidate(Call(915127, 1000, "Medical reaction to medication at 3360 Highway 411 North."), 0.8, ""),
                Candidate(Call(915215, 1010, "EMS response at 3360 Highway 411 North."), 0.8, ""),
                Candidate(Call(915257, 1020, "Fire response at Walker Valley High School."), 0.8, ""),
                Candidate(Call(915295, 1030, "Fire alarm response at Walker Valley High School."), 0.8, ""),
                Candidate(Call(915305, 1040, "Smoke detector alarm at Walker Valley High School."), 0.8, ""),
                Candidate(Call(915427, 1050, "Smoke detector reset at Walker Valley High School."), 0.8, "")
            ]);

        var smokeDetectorCall = Assert.Single(decisions, decision => decision.CallId == 915305);
        Assert.Equal("attach_current", smokeDetectorCall.Decision);
        Assert.Equal("fire-current-frame", smokeDetectorCall.WinningFrameId);
        Assert.Equal("new", smokeDetectorCall.WouldAttachCurrentIncidentId);
        Assert.Contains(smokeDetectorCall.MembershipEvidence, value => value == "current_overlap=100");

        var medicalScore = Assert.Single(smokeDetectorCall.ScoresByFrame, score => score.FrameId == "medical-current-frame");
        Assert.DoesNotContain(medicalScore.Evidence, value => value == "current_overlap=100");
    }

    [Fact]
    public void BuildResolverDecisions_HoldsPromotionAmbiguousMember()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(302, 1000, "Possible unconscious person at 100 Riverside Drive."), 0.8, "100 riverside dr"),
            Candidate(Call(303, 1030, "Fire response at 81 Oak Lane."), 0.8, "81 oak ln")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Possible unconscious person at 100 Riverside Dr",
                "police",
                ["C00000000012E", "C00000000012F"],
                ["C00000000012E", "C00000000012F"],
                [])
        };
        var frames = builder.Build("test", [], candidates, current, 18);

        var decisions = builder.BuildResolverDecisions(frames, candidates);

        var cleanMember = Assert.Single(decisions, decision => decision.CallId == 302);
        var conflictingMember = Assert.Single(decisions, decision => decision.CallId == 303);
        Assert.Equal("attach_current", cleanMember.Decision);
        Assert.Equal("ambiguous_hold", conflictingMember.Decision);
        Assert.Equal("promotion_ambiguous", conflictingMember.DecisionReason);
        Assert.NotNull(conflictingMember.AmbiguousUntil);
    }

    [Fact]
    public void BuildResolverDecisions_ExpiredAmbiguityReportsExpiredCutoff()
    {
        var builder = new IncidentFrameBuilderV3();
        var firstFrame = new IncidentFrameV3(
            "first-frame",
            "medical:first",
            "multi_call",
            "mature",
            "Medical response",
            "ems",
            "",
            [401],
            [],
            [],
            "",
            "",
            "",
            "test");
        var secondFrame = new IncidentFrameV3(
            "second-frame",
            "medical:second",
            "multi_call",
            "mature",
            "Fire response",
            "fire",
            "",
            [401],
            [],
            [],
            "",
            "",
            "",
            "test");

        var lateCall = new EngineCall
        {
            Id = 401,
            StartTime = 1000,
            StopTime = 2000,
            SystemShortName = "test",
            Talkgroup = 100,
            TalkgroupName = "Fire Dispatch",
            Category = "fire",
            Transcription = "Medical or fire response details.",
            TranscriptionStatus = "complete",
            QualityReason = "ok"
        };

        var decision = Assert.Single(builder.BuildResolverDecisions(
            [firstFrame, secondFrame],
            [Candidate(lateCall, 0.8, "none")]));

        Assert.Equal("ambiguous_drop", decision.Decision);
        Assert.Equal("winner_margin_too_small", decision.DecisionReason);
        Assert.Equal(1300, decision.CutoffAt);
        Assert.Equal(decision.CutoffAt, decision.AmbiguousUntil);
        Assert.True(decision.AmbiguousUntil < 2000);
    }

    [Fact]
    public void BuildResolverDecisions_BlocksGenericNewCreate()
    {
        var builder = new IncidentFrameBuilderV3();
        var genericFrame = new IncidentFrameV3(
            "generic-frame",
            "police:calls-304",
            "single_call",
            "mature",
            "Police response",
            "police",
            "",
            [304],
            [],
            [],
            "",
            "",
            "",
            "test");

        var decision = Assert.Single(builder.BuildResolverDecisions(
            [genericFrame],
            [Candidate(Call(304, 1000, "Unit responding."), 0.8, "none")]));

        Assert.Equal("hold_pending", decision.Decision);
        Assert.False(decision.WouldCreateIncident);
        Assert.Equal("generic_title", decision.DroppedBecause);
    }

    [Fact]
    public void BuildResolverDecisions_BlocksResponderLocationNewCreate()
    {
        var builder = new IncidentFrameBuilderV3();
        var genericLocationFrame = new IncidentFrameV3(
            "generic-location-frame",
            "medical:location-1605-s-orchard-maw-ave",
            "multi_call",
            "mature",
            "Medical response at 1605 S Orchard Maw Ave",
            "ems",
            "1605 s orchard maw ave",
            [304, 305],
            [],
            ["address:1605 s orchard maw ave", "location:1605 s orchard maw ave"],
            "",
            "",
            "",
            "test");

        var decisions = builder.BuildResolverDecisions(
            [genericLocationFrame],
            [
                Candidate(Call(304, 1000, "Medical response at sixteen oh five south orchard maw avenue."), 0.8, "1605 s orchard maw ave"),
                Candidate(Call(305, 1030, "Second unit responding to the same address."), 0.8, "1605 s orchard maw ave")
            ]);

        Assert.All(decisions, decision =>
        {
            Assert.Equal("hold_pending", decision.Decision);
            Assert.False(decision.WouldCreateIncident);
            Assert.Equal("generic_title", decision.DroppedBecause);
        });
    }

    [Fact]
    public void BuildResolverDecisions_HoldsBorderlineSingleCallNewCreate()
    {
        var builder = new IncidentFrameBuilderV3();
        var borderlineFrame = new IncidentFrameV3(
            "borderline-frame",
            "traffic:calls-305",
            "single_call",
            "mature",
            "Traffic crash",
            "traffic",
            "",
            [305],
            [],
            [],
            "",
            "",
            "",
            "test");

        var decision = Assert.Single(builder.BuildResolverDecisions(
            [borderlineFrame],
            [Candidate(Call(305, 1000, "Report of a traffic crash."), 0.8, "none")]));

        Assert.Equal("hold_pending", decision.Decision);
        Assert.Equal("borderline_single_call_new_candidate", decision.DecisionReason);
        Assert.False(decision.WouldCreateIncident);
        Assert.Contains(decision.MembershipEvidence, value => value == "mature=30");
        Assert.Contains(decision.MembershipEvidence, value => value == "specific_title=20");
        Assert.DoesNotContain(decision.MembershipEvidence, value => value == "trusted_location=70");
    }

    [Fact]
    public void BuildResolverDecisions_CreatesSingleCallNewWithTrustedGeocode()
    {
        var builder = new IncidentFrameBuilderV3();
        var trustedLocationFrame = new IncidentFrameV3(
            "trusted-location-frame",
            "traffic:location-100-main-st",
            "single_call",
            "mature",
            "Traffic crash at 100 Main St",
            "traffic",
            "100 main st",
            [306],
            [],
            ["address:100 main st", "location:100 main st"],
            "",
            "",
            "",
            "test")
        {
            LocationIsHighConfidenceGeocode = true,
            LocationGeocodeConfidence = 0.91,
            LocationGeocodeProvider = "cache",
            LocationGeocodePrecision = "address",
            LocationSource = "transcription",
            LocationGeocodeQuery = "100 Main Street",
            LocationGeocodeDisplayName = "100 Main Street, Chattanooga, TN",
            LocationLatitude = 35.001,
            LocationLongitude = -85.001
        };

        var decision = Assert.Single(builder.BuildResolverDecisions(
            [trustedLocationFrame],
            [Candidate(Call(306, 1000, "Traffic crash at 100 Main Street."), 0.8, "100 main st")]));

        Assert.Equal("attach_new", decision.Decision);
        Assert.Equal("highest_scored_new_incident_candidate", decision.DecisionReason);
        Assert.True(decision.WouldCreateIncident);
        Assert.Contains(decision.MembershipEvidence, value => value == "trusted_location=70");
    }

    [Fact]
    public void BuildResolverDecisions_DoesNotDetachCurrentForNoisyLocationLabel()
    {
        var builder = new IncidentFrameBuilderV3();
        var noisyConflictFrame = new IncidentFrameV3(
            "noisy-current-frame",
            "current:assault-at-walmart",
            "current_matched",
            "conflicted",
            "Police response",
            "police",
            "13 and five dots and ave",
            [305],
            [],
            [],
            "llm:test:305:event",
            "Assault at Walmart on Highway 153",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [305]
        };

        var decision = Assert.Single(builder.BuildResolverDecisions(
            [noisyConflictFrame],
            [Candidate(Call(305, 1000, "Police response near thirteen and five dots and avenue."), 0.8, "13 and five dots and ave")]));

        Assert.Equal("attach_current", decision.Decision);
        Assert.Equal("llm:test:305:event", decision.WouldAttachCurrentIncidentId);
        Assert.False(decision.WouldCreateIncident);
        Assert.False(decision.WouldDetachCreate);
        Assert.DoesNotContain(decision.MembershipEvidence, value => value == "trusted_location=70");
    }

    [Fact]
    public void BuildResolverDecisions_DetachesCurrentForStrongConflictingLocation()
    {
        var builder = new IncidentFrameBuilderV3();
        var strongConflictFrame = new IncidentFrameV3(
            "strong-current-frame",
            "current:assault-at-walmart",
            "current_matched",
            "conflicted",
            "Assault at 2015 Mcfarlane Ave",
            "police",
            "2015 mcfarlane ave",
            [306],
            [],
            ["address:2015 mcfarlane ave", "location:2015 mcfarlane ave"],
            "llm:test:306:event",
            "Assault at Walmart on Highway 153",
            "active",
            "test")
        {
            LocationIsHighConfidenceGeocode = true,
            LocationGeocodeConfidence = 0.92,
            LocationGeocodeProvider = "cache",
            LocationGeocodePrecision = "address",
            LocationSource = "transcription",
            LocationGeocodeQuery = "2015 McFarlane Avenue",
            LocationGeocodeDisplayName = "2015 McFarlane Avenue, Chattanooga, TN",
            LocationLatitude = 35.039,
            LocationLongitude = -85.159
        };

        var decision = Assert.Single(builder.BuildResolverDecisions(
            [strongConflictFrame],
            [Candidate(Call(306, 1000, "Police response at 2015 McFarlane Avenue."), 0.8, "2015 mcfarlane ave")]));

        Assert.Equal("detach_create", decision.Decision);
        Assert.Equal("strong_location_conflicts_with_current", decision.DecisionReason);
        Assert.True(decision.WouldCreateIncident);
        Assert.True(decision.WouldDetachCreate);
        Assert.Contains(decision.MembershipEvidence, value => value == "trusted_location=70");
    }

    [Fact]
    public void BuildIncidentPlanDecisions_GroupsResolverDecisionsIntoOperations()
    {
        var builder = new IncidentFrameBuilderV3();
        var currentFrame = new IncidentFrameV3(
            "current-frame",
            "current:test",
            "current_matched",
            "mature",
            "Fire response at 100 Main St",
            "fire",
            "100 main st",
            [401, 402],
            [],
            ["address:100 main st"],
            "llm:test:401:event",
            "Fire response at 100 Main St",
            "active",
            "test");
        var newFrame = new IncidentFrameV3(
            "new-frame",
            "medical:calls-403",
            "single_call",
            "mature",
            "Difficulty breathing",
            "ems",
            "100 alpha road",
            [403],
            [],
            [],
            "",
            "",
            "",
            "test")
        {
            LocationIsHighConfidenceGeocode = true
        };
        var pendingFrame = new IncidentFrameV3(
            "pending-frame",
            "police:location-200-oak-rd",
            "single_call",
            "pending",
            "Police response at 200 Oak Rd",
            "police",
            "200 oak rd",
            [404],
            [],
            ["address:200 oak rd"],
            "",
            "",
            "",
            "test");
        var resolverDecisions = new[]
        {
            ResolverDecision(401, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "llm:test:401:event"),
            ResolverDecision(402, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "llm:test:401:event"),
            ResolverDecision(403, "attach_new", newFrame.FrameId, wouldCreateIncident: true),
            ResolverDecision(404, "hold_pending", pendingFrame.FrameId)
        };

        var plans = builder.BuildIncidentPlanDecisions([currentFrame, newFrame, pendingFrame], resolverDecisions);

        var downgradedCurrent = Assert.Single(plans, plan =>
            plan.Action == "hold_pending" &&
            plan.Reason.Contains("current=llm:test:401:event", StringComparison.OrdinalIgnoreCase));
        var createNew = Assert.Single(plans, plan => plan.Action == "create_new");
        var holdPending = Assert.Single(plans, plan =>
            plan.Action == "hold_pending" &&
            plan.FrameId == pendingFrame.FrameId);
        Assert.Equal(string.Empty, downgradedCurrent.TargetIncidentId);
        Assert.Equal(string.Empty, downgradedCurrent.TargetIncidentTitle);
        Assert.Equal("Fire response at 100 Main St", downgradedCurrent.Title);
        Assert.Equal("Fire response at 100 Main St", downgradedCurrent.FrameTitle);
        Assert.Equal([401, 402], downgradedCurrent.CallIds);
        Assert.Contains("planDroppedBecause=non_active_current_target", downgradedCurrent.Reason);
        Assert.Equal("Difficulty breathing", createNew.Title);
        Assert.Equal("Difficulty breathing", createNew.FrameTitle);
        Assert.Equal(string.Empty, createNew.TargetIncidentTitle);
        Assert.Equal([403], createNew.CallIds);
        Assert.Equal("Police response at 200 Oak Rd", holdPending.Title);
        Assert.Equal([404], holdPending.CallIds);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_KeepsDetachCreateSeparateFromCurrentUpdate()
    {
        var builder = new IncidentFrameBuilderV3();
        var currentFrame = new IncidentFrameV3(
            "current-frame",
            "current:test",
            "current_matched",
            "mature",
            "Assault at Walmart on Highway 153",
            "police",
            "hwy 153",
            [405],
            [],
            ["location:hwy 153"],
            "llm:test:405:event",
            "Assault at Walmart on Highway 153",
            "active",
            "test");
        var detachedFrame = new IncidentFrameV3(
            "detached-frame",
            "police:location-2015-mcfarlane-ave",
            "current_matched",
            "conflicted",
            "Assault at 2015 Mcfarlane Ave",
            "police",
            "2015 mcfarlane ave",
            [406],
            [],
            ["address:2015 mcfarlane ave"],
            "llm:test:405:event",
            "Assault at Walmart on Highway 153",
            "active",
            "test");
        var resolverDecisions = new[]
        {
            ResolverDecision(405, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "llm:test:405:event"),
            ResolverDecision(406, "detach_create", detachedFrame.FrameId, wouldCreateIncident: true, wouldDetachCreate: true)
        };

        var plans = builder.BuildIncidentPlanDecisions([currentFrame, detachedFrame], resolverDecisions);

        var holdCurrent = Assert.Single(plans, plan =>
            plan.Action == "hold_pending" &&
            plan.Reason.Contains("current=llm:test:405:event", StringComparison.OrdinalIgnoreCase));
        var detachCreate = Assert.Single(plans, plan => plan.Action == "detach_create");
        Assert.Equal([405], holdCurrent.CallIds);
        Assert.Contains("planDroppedBecause=non_active_current_target", holdCurrent.Reason);
        Assert.Equal([406], detachCreate.CallIds);
        Assert.Equal(string.Empty, detachCreate.TargetIncidentId);
        Assert.Contains("resolverAction=detach_create", detachCreate.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsUnmatchedCreateWithoutStrongCreateEvidence()
    {
        var builder = new IncidentFrameBuilderV3();
        var weakFrame = new IncidentFrameV3(
            "weak-new-frame",
            "traffic:road-hint-hwy-58",
            "multi_call",
            "mature",
            "Road hazard",
            "police",
            "",
            [501, 502],
            [],
            ["road_hint:hwy 58", "address:000 hwy"],
            "",
            "",
            "",
            "test");
        var resolverDecisions = new[]
        {
            ResolverDecision(501, "attach_new", weakFrame.FrameId, wouldCreateIncident: true),
            ResolverDecision(502, "attach_new", weakFrame.FrameId, wouldCreateIncident: true)
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([weakFrame], resolverDecisions));

        Assert.Equal("hold_pending", plan.Action);
        Assert.Equal([501, 502], plan.CallIds);
        Assert.Contains("planDroppedBecause=weak_create_evidence", plan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsUnmatchedCreateWithHospitalHandoffMember()
    {
        var builder = new IncidentFrameBuilderV3();
        var hospitalMixedFrame = new IncidentFrameV3(
            "hospital-mixed-frame",
            "medical:address-2006-jenkins-rd",
            "multi_call",
            "mature",
            "Unconscious person",
            "ems",
            "2006 Jenkins Rd",
            [601, 602],
            [],
            ["address:2006 jenkins rd"],
            "",
            "",
            "",
            "test")
        {
            HasHospitalHandoffOrTransportMember = true
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(601, "attach_new", hospitalMixedFrame.FrameId, wouldCreateIncident: true),
            ResolverDecision(602, "attach_new", hospitalMixedFrame.FrameId, wouldCreateIncident: true)
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([hospitalMixedFrame], resolverDecisions));

        Assert.Equal("hold_pending", plan.Action);
        Assert.Equal([601, 602], plan.CallIds);
        Assert.Contains("planDroppedBecause=hospital_handoff_or_transport_member", plan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsCurrentUpdateWithNewHospitalHandoffMember()
    {
        var builder = new IncidentFrameBuilderV3();
        var currentFrame = new IncidentFrameV3(
            "current-handoff-frame",
            "current:active-436:stroke-dayton-pike",
            "current_matched",
            "mature",
            "Stroke call at 21 Dayton Pike Apt B",
            "ems",
            "dayton pike",
            [701, 702, 703],
            [],
            ["address:21 dayton pike"],
            "active:436",
            "Stroke call at 21 Dayton Pike Apt B",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [701, 702],
            HospitalHandoffOrTransportCallIds = [703],
            HasHospitalHandoffOrTransportMember = true
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(703, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "active:436")
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([currentFrame], resolverDecisions));

        Assert.Equal("hold_pending", plan.Action);
        Assert.Equal("active:436", plan.TargetIncidentId);
        Assert.Equal([703], plan.CallIds);
        Assert.Contains("planDroppedBecause=new_hospital_handoff_or_transport_member", plan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_AllowsCurrentUpdateWhenHospitalHandoffIsAlreadyCurrent()
    {
        var builder = new IncidentFrameBuilderV3();
        var currentFrame = new IncidentFrameV3(
            "current-existing-handoff-frame",
            "current:active-437:stroke-main-st",
            "current_matched",
            "mature",
            "Stroke call at 100 Main St",
            "ems",
            "100 main st",
            [801, 802, 803, 804],
            [],
            ["address:100 main st"],
            "active:437",
            "Stroke call at 100 Main St",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [801, 802],
            HospitalHandoffOrTransportCallIds = [802],
            HasHospitalHandoffOrTransportMember = true
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(803, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "active:437"),
            ResolverDecision(804, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "active:437")
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([currentFrame], resolverDecisions));

        Assert.Equal("update_current", plan.Action);
        Assert.Equal("active:437", plan.TargetIncidentId);
        Assert.Equal([801, 802, 803, 804], plan.CallIds);
        Assert.DoesNotContain("new_hospital_handoff_or_transport_member", plan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsGenericDetachCreateTitles()
    {
        var builder = new IncidentFrameBuilderV3();
        var detachedFrame = new IncidentFrameV3(
            "detached-frame",
            "current:new:assault-in-progress-at-hartman-ln-ne",
            "current_matched",
            "conflicted",
            "Police response at 1490 Delwood Ln NE",
            "police",
            "1490 delwood ln ne",
            [406, 407],
            [],
            ["address:1490 delwood ln ne", "location:1490 delwood ln ne"],
            "new",
            "Assault in progress at Hartman Ln NE",
            "active",
            "test");
        var resolverDecisions = new[]
        {
            ResolverDecision(406, "detach_create", detachedFrame.FrameId, wouldCreateIncident: true, wouldDetachCreate: true),
            ResolverDecision(407, "detach_create", detachedFrame.FrameId, wouldCreateIncident: true, wouldDetachCreate: true)
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([detachedFrame], resolverDecisions));

        Assert.Equal("hold_pending", plan.Action);
        Assert.Equal("Police response at 1490 Delwood Ln NE", plan.Title);
        Assert.Equal([406, 407], plan.CallIds);
        Assert.Contains("resolverAction=detach_create", plan.Reason);
        Assert.Contains("planDroppedBecause=generic_title", plan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsNewCurrentTarget()
    {
        var builder = new IncidentFrameBuilderV3();
        var currentFrame = new IncidentFrameV3(
            "current-frame",
            "current:test",
            "current_matched",
            "mature",
            "Traffic crash",
            "police",
            "",
            [407, 408],
            [],
            [],
            "new",
            "MVC with injuries at mile marker 24 WB",
            "active",
            "test")
        {
            MatchedCurrentCategory = "ems"
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(407, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "new"),
            ResolverDecision(408, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "new")
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([currentFrame], resolverDecisions));

        Assert.Equal("hold_pending", plan.Action);
        Assert.Equal(string.Empty, plan.TargetIncidentId);
        Assert.Equal(string.Empty, plan.TargetIncidentTitle);
        Assert.Equal("MVC with injuries at mile marker 24 WB", plan.Title);
        Assert.Equal("ems", plan.Category);
        Assert.Equal("Traffic crash", plan.FrameTitle);
        Assert.Equal([407, 408], plan.CallIds);
        Assert.Contains("frameTitle=Traffic crash", plan.Reason);
        Assert.Contains("planDroppedBecause=non_active_current_target", plan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_PreservesMatchedCurrentCallIdsForCurrentUpdates()
    {
        var builder = new IncidentFrameBuilderV3();
        var currentFrame = new IncidentFrameV3(
            "mvc-current-frame",
            "current:new:mvc-with-injuries",
            "current_matched",
            "mature",
            "Traffic crash",
            "ems",
            "",
            [915499, 915513],
            [],
            [],
            "new",
            "MVC with injuries at parking garage, male hit pole",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [915499, 915513, 915611],
            MatchedCurrentCategory = "ems"
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(915499, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "new"),
            ResolverDecision(915513, "attach_current", currentFrame.FrameId, wouldAttachCurrentIncidentId: "new")
        };

        var plan = Assert.Single(builder.BuildIncidentPlanDecisions([currentFrame], resolverDecisions));

        Assert.Equal("hold_pending", plan.Action);
        Assert.Equal([915499, 915513], plan.CallIds);
        Assert.DoesNotContain("currentCoverageCallIds=915611", plan.Reason);
        Assert.Contains("planDroppedBecause=non_active_current_target", plan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_DoesNotPreserveCurrentCallAlreadyResolvedToAnotherPlan()
    {
        var builder = new IncidentFrameBuilderV3();
        var trafficFrame = new IncidentFrameV3(
            "traffic-frame",
            "current:new:traffic-stop",
            "current_matched",
            "mature",
            "Police response",
            "police",
            "",
            [916561, 916621],
            [],
            [],
            "new",
            "Traffic stop at I-40 EB ramp near marker 352",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [916561, 916621],
            MatchedCurrentCategory = "police"
        };
        var warrantFrame = new IncidentFrameV3(
            "warrant-frame",
            "current:new:warrant-service",
            "current_matched",
            "mature",
            "Police response",
            "police",
            "",
            [916661],
            [],
            [],
            "new",
            "Warrant service at 255 Crabtree Street",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [916561, 916661],
            MatchedCurrentCategory = "police"
        };
        var resolverDecisions = new[]
        {
            ResolverDecision(916561, "attach_current", trafficFrame.FrameId, wouldAttachCurrentIncidentId: "new"),
            ResolverDecision(916621, "attach_current", trafficFrame.FrameId, wouldAttachCurrentIncidentId: "new"),
            ResolverDecision(916661, "attach_current", warrantFrame.FrameId, wouldAttachCurrentIncidentId: "new")
        };

        var plans = builder.BuildIncidentPlanDecisions([trafficFrame, warrantFrame], resolverDecisions);

        var warrantPlan = Assert.Single(plans, plan => plan.Title == "Warrant service at 255 Crabtree Street");
        Assert.Equal([916661], warrantPlan.CallIds);
        Assert.DoesNotContain("currentCoverageCallIds=916561", warrantPlan.Reason);
        Assert.Equal(0, plans.SelectMany(plan => plan.CallIds).GroupBy(id => id).Count(group => group.Count() > 1));
    }

    [Fact]
    public void BuildIncidentPlanDecisions_HoldsActiveCurrentUpdateThatWouldDropCurrentMembership()
    {
        var builder = new IncidentFrameBuilderV3();
        var activeCurrentFrame = new IncidentFrameV3(
            "active-current-frame",
            "current:active-451:unconscious-persons",
            "current_matched",
            "mature",
            "Unconscious person near 6708 Ringle Rd",
            "ems",
            "6708 ringle rd",
            [116287, 116305, 116325],
            [],
            ["address:6708 ringle rd"],
            "active:451",
            "Two unconscious persons at Circle K on Ringle Rd",
            "active",
            "test")
        {
            MatchedCurrentCallIds = [116287, 116317, 116325],
            MatchedCurrentCategory = "ems"
        };
        var otherFrame = new IncidentFrameV3(
            "other-frame",
            "other:116317",
            "multi_call",
            "mature",
            "Separate medical update",
            "ems",
            "other",
            [116317],
            [],
            ["address:other"],
            "",
            "",
            "",
            "test");
        var resolverDecisions = new[]
        {
            ResolverDecision(116287, "attach_current", activeCurrentFrame.FrameId, wouldAttachCurrentIncidentId: "active:451"),
            ResolverDecision(116305, "attach_current", activeCurrentFrame.FrameId, wouldAttachCurrentIncidentId: "active:451"),
            ResolverDecision(116325, "attach_current", activeCurrentFrame.FrameId, wouldAttachCurrentIncidentId: "active:451"),
            ResolverDecision(116317, "attach_new", otherFrame.FrameId, wouldCreateIncident: true)
        };

        var plans = builder.BuildIncidentPlanDecisions([activeCurrentFrame, otherFrame], resolverDecisions);

        var currentPlan = Assert.Single(plans, plan => plan.TargetIncidentId == "active:451");
        Assert.Equal("hold_pending", currentPlan.Action);
        Assert.Equal([116287, 116305, 116325], currentPlan.CallIds);
        Assert.Contains("planDroppedBecause=current_update_would_drop_existing_membership", currentPlan.Reason);
    }

    [Fact]
    public void BuildIncidentPlanDecisions_DoesNotCollapsePlaceholderCurrentIdsWithDifferentTitles()
    {
        var builder = new IncidentFrameBuilderV3();
        var mentalFrame = new IncidentFrameV3(
            "mental-frame",
            "current:new:mental-disturbance",
            "current_matched",
            "mature",
            "Medical response",
            "police",
            "",
            [501],
            [],
            [],
            "new",
            "Mental disturbance at 940 S Icoe St",
            "active",
            "test");
        var mvcFrame = new IncidentFrameV3(
            "mvc-frame",
            "current:new:mvc-highway-11",
            "current_matched",
            "mature",
            "Traffic crash",
            "police",
            "",
            [502],
            [],
            [],
            "new",
            "MVC on Highway 11 South near CR 134",
            "active",
            "test");
        var resolverDecisions = new[]
        {
            ResolverDecision(501, "attach_current", mentalFrame.FrameId, wouldAttachCurrentIncidentId: "new"),
            ResolverDecision(502, "attach_current", mvcFrame.FrameId, wouldAttachCurrentIncidentId: "new")
        };

        var plans = builder.BuildIncidentPlanDecisions([mentalFrame, mvcFrame], resolverDecisions);

        var holds = plans.Where(plan => plan.Action == "hold_pending").ToList();
        Assert.Equal(2, holds.Count);
        Assert.Contains(holds, plan =>
            plan.TargetIncidentId == string.Empty &&
            plan.Title == "Mental disturbance at 940 S Icoe St" &&
            plan.CallIds.SequenceEqual([501]));
        Assert.Contains(holds, plan =>
            plan.TargetIncidentId == string.Empty &&
            plan.Title == "MVC on Highway 11 South near CR 134" &&
            plan.CallIds.SequenceEqual([502]));
        Assert.All(holds, plan => Assert.Contains("planDroppedBecause=non_active_current_target", plan.Reason));
    }

    [Fact]
    public void Build_DoesNotCollapseDifferentPlaceholderNewIncidents()
    {
        var builder = new IncidentFrameBuilderV3();
        var candidates = new[]
        {
            Candidate(Call(201, 1000, "Unit copies the first call at 100 Alpha Road."), 0.8, "100 alpha rd"),
            Candidate(Call(202, 1030, "Unit copies the second call at 200 Beta Road."), 0.8, "200 beta rd")
        };
        var current = new[]
        {
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "Debris on I-24 East near mile marker 164",
                "traffic",
                ["C0000000000C9"],
                ["C0000000000C9"],
                []),
            new IncidentFrameCurrentIncidentV3(
                "new",
                "active",
                "MVC I-75 SB near mile marker 10 involving gray GMC and green vehicle",
                "traffic",
                ["C0000000000CA"],
                ["C0000000000CA"],
                [])
        };

        var frames = builder.Build("test", [], candidates, current, 18);

        Assert.Equal(2, frames.Count);
        Assert.Contains(frames, frame => frame.MatchedCurrentTitle.StartsWith("Debris on I-24", StringComparison.Ordinal));
        Assert.Contains(frames, frame => frame.MatchedCurrentTitle.StartsWith("MVC I-75", StringComparison.Ordinal));
    }

    private static IncidentRagCandidate Candidate(EngineCall call, double score, string location, bool highConfidenceGeocode = true) =>
        new(
            call,
            score,
            1,
            location == "none" ? 0 : 1,
            1,
            0,
            "test",
            location,
            location != "none" && highConfidenceGeocode,
            location == "none" || !highConfidenceGeocode ? 0.2 : 0.86,
            location == "none" || !highConfidenceGeocode ? "none" : "nominatim",
            location == "none" || !highConfidenceGeocode ? "unknown" : "address");

    private static IncidentFrameResolverDecisionV3 ResolverDecision(
        long callId,
        string decision,
        string winningFrameId,
        bool wouldCreateIncident = false,
        string wouldAttachCurrentIncidentId = "",
        bool wouldDetachCreate = false) =>
        new(
            callId,
            [winningFrameId],
            [new IncidentFrameResolverScoreV3(winningFrameId, winningFrameId, 100, [])],
            winningFrameId,
            100,
            decision,
            $"test_{decision}",
            null,
            null,
            wouldCreateIncident,
            wouldAttachCurrentIncidentId,
            wouldDetachCreate,
            string.Empty,
            []);

    private static EngineCall Call(long id, long start, string text) => new()
    {
        Id = id,
        StartTime = start,
        StopTime = start + 10,
        SystemShortName = "test",
        Talkgroup = 100,
        TalkgroupName = "Fire Dispatch",
        Category = "fire",
        Transcription = text,
        TranscriptionStatus = "complete",
        QualityReason = "ok"
    };

    private static IncidentCallDto IncidentCall(long id, long start, string text) =>
        new(id, start, text, $"/api/v1/calls/{id}/audio", "public_works", "Public Works", "test");

    private static string TitleCaseForTest(string value) =>
        string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
}
