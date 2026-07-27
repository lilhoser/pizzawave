using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;

namespace pizzad.Tests;

public sealed class AutomaticInsightsServiceMembershipTests
{
    [Fact]
    public void IncidentProcessingSlicesPreserveHistoricalCallsAndBoundEachPrompt()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var calls = Enumerable.Range(0, 45).Select(index => new EngineCall
        {
            Id = index + 1,
            SystemShortName = index < 30 ? "site-a" : "site-b",
            StartTime = now - 8 * 3600 + index * 60,
            StopTime = now - 8 * 3600 + index * 60 + 10
        }).ToList();

        var slices = AutomaticInsightsService.BuildIncidentProcessingSlices(calls, 10, now);

        Assert.True(slices.Count >= 5);
        Assert.All(slices, slice => Assert.InRange(slice.Calls.Count, 1, 10));
        Assert.Equal(calls.Select(call => call.Id).Order(), slices.SelectMany(slice => slice.Calls).Select(call => call.Id).Order());
        Assert.All(slices, slice => Assert.True(slice.Start <= slice.Calls.Min(call => call.StartTime)));
        Assert.All(slices, slice => Assert.True(slice.End >= slice.Calls.Max(call => call.StopTime)));
    }
    [Fact]
    public void ParseIncidentExtractionResponse_RemovesMalformedCallIdsFromIncidentState()
    {
        const string response = """
        {
          "incidents": [
            {
              "incident_id": "new",
              "status": "active",
              "title": "Mental health call",
              "detail": "Female unstable.",
              "category": "police",
              "confidence": 0.9,
              "call_ids": [
                "C0000000CC2CB",
                "event_class",
                "event_location",
                "{\"kind\":\"unknown\"}",
                "835723",
                "0000000CC2CF"
              ],
              "call_evidence": [],
              "event_class": "mental_health",
              "parent_event_evidence": [],
              "logistics_only": false
            }
          ]
        }
        """;

        var result = ParseIncidentExtractionResponse(response);
        var incident = Assert.Single(result);

        Assert.Equal(["C0000000CC2CB", "835723", "0000000CC2CF"], GetStringList(incident, "CallIds"));
        Assert.Equal(["C0000000CC2CB", "835723", "0000000CC2CF"], GetStringList(incident, "RawCallIds"));
        Assert.Empty(GetStringList(incident, "DroppedCallIds"));
    }

    [Fact]
    public void IncidentV3CurrentCallFilter_DropsUnresolvableCurrentModelCallIds()
    {
        var filtered = FilterIncidentV3CurrentCallIds(
            ["8", "925213", "C0000000E1E1D"],
            ["925213", "0000000E1E1D", "C0000000E1E1D"]);

        Assert.Equal(["925213", "C0000000E1E1D"], filtered);
    }

    [Fact]
    public void IncidentExtractionFallbackChunkTimeout_BoundsRecoveredChunksBelowConfiguredTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), IncidentExtractionFallbackChunkTimeout(1, 600_000));
        Assert.Equal(TimeSpan.FromSeconds(90), IncidentExtractionFallbackChunkTimeout(2, 600_000));
        Assert.Equal(TimeSpan.FromSeconds(180), IncidentExtractionFallbackChunkTimeout(4, 600_000));
        Assert.Equal(TimeSpan.FromSeconds(300), IncidentExtractionFallbackChunkTimeout(8, 600_000));
    }

    [Fact]
    public void IncidentExtractionFallbackChunkTimeout_DoesNotExceedConfiguredTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), IncidentExtractionFallbackChunkTimeout(8, 30_000));
    }

    [Fact]
    public void IsIncidentExtractionTimeout_TreatsFallbackCancellationAsTimeout()
    {
        var exception = new InvalidOperationException("Incident extraction request failed.", new TaskCanceledException("A task was canceled."));

        Assert.True(IsIncidentExtractionTimeout(exception));
    }

    [Fact]
    public void IsCallerRequestedCompletionCancellation_RequiresCallerCanceledToken()
    {
        using var callerCanceled = new CancellationTokenSource();
        callerCanceled.Cancel();

        Assert.True(IsCallerRequestedCompletionCancellation(new TaskCanceledException("The operation was canceled."), callerCanceled.Token));
        Assert.False(IsCallerRequestedCompletionCancellation(new TaskCanceledException("The operation was canceled."), CancellationToken.None));
        Assert.False(IsCallerRequestedCompletionCancellation(new InvalidOperationException("HTTP 500"), callerCanceled.Token));
    }

    [Fact]
    public void ShouldStopIncidentExtractionFallbackAfterTerminalTimeouts_StopsAfterThreeConsecutiveTerminalTimeouts()
    {
        Assert.False(ShouldStopIncidentExtractionFallbackAfterTerminalTimeouts(2));
        Assert.True(ShouldStopIncidentExtractionFallbackAfterTerminalTimeouts(3));
    }

    [Fact]
    public void ShadowCandidateLifecycle_KeepsNewCandidatePendingUntilReobservedOrResolved()
    {
        var service = new AutomaticInsightsService(
            new EngineConfig(),
            null!,
            null!,
            null!,
            null!,
            null!,
            NullLogger<AutomaticInsightsService>.Instance);
        var emptyCurrentResult = ParseIncidentExtractionResult("""
        {
          "incidents": []
        }
        """);
        var currentWithIncident = ParseIncidentExtractionResult("""
        {
          "incidents": [
            {
              "incident_id": "llm:test:100:event",
              "status": "active",
              "title": "Difficulty breathing at Main St",
              "detail": "EMS response.",
              "category": "ems",
              "confidence": 0.9,
              "call_ids": ["100"],
              "call_evidence": [],
              "event_class": "medical",
              "parent_event_evidence": [],
              "logistics_only": false
            }
          ]
        }
        """);
        var decision = new PersistenceDecisionV2(
            "shadow_accept",
            "shadow:test:hypothesis-1",
            [100],
            [],
            "Difficulty breathing",
            "Accepted calls contain source-backed evidence.",
            "ems",
            [],
            []);
        var firstSeen = new DateTimeOffset(2026, 6, 21, 18, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            "pending_new_candidate_incident",
            ClassifyShadowDecisionRelationship(service, "test", decision, emptyCurrentResult, firstSeen));
        Assert.Equal(
            "reobserved_new_candidate_incident",
            ClassifyShadowDecisionRelationship(service, "test", decision, emptyCurrentResult, firstSeen.AddMinutes(4)));

        var notices = ResolveShadowCandidatesFromCurrentResult(service, "test", currentWithIncident, firstSeen.AddMinutes(5));
        var notice = Assert.Single(notices);
        Assert.Equal("resolved_to_existing_incident", GetString(notice, "Lifecycle"));
        Assert.Equal("llm:test:100:event", GetString(notice, "CurrentIncidentId"));

        Assert.Equal(
            "resolved_existing_incident_followup",
            ClassifyShadowDecisionRelationship(service, "test", decision, emptyCurrentResult, firstSeen.AddMinutes(6)));

        var expandedDecision = decision with { AcceptedCallIds = [100, 101] };
        Assert.Equal(
            "resolved_existing_incident_followup",
            ClassifyShadowDecisionRelationship(service, "test", expandedDecision, emptyCurrentResult, firstSeen.AddMinutes(7)));
    }

    [Fact]
    public void ExistingConcreteAnchorReplacementConflict_BlocksDroppingProtectedLocationForConflictingLocation()
    {
        var existing = new IncidentDto
        {
            Title = "Seizure at 114 Jane Manor Circle",
            Detail = "EMS responding to a seizure call for a patient at 114 Jane Manor Circle on Outdating Pike.",
            Calls =
            [
                IncidentCall(796751, 1000, "Seven responded to one fourteen Jane Manor for a seizure."),
                IncidentCall(796969, 1588, "I'm showing you responding to 114 Jane Manor Circle, 114 Jane Manor Circle, Outdating Pike. The child came in as a seizure.")
            ]
        };
        var proposed =
            new[]
            {
                EngineCall(796751, 1000, "Seven responded to one fourteen Jane Manor for a seizure."),
                EngineCall(796973, 1596, "Ladder 5 responding with medic 9, 1970 Buckley Street, for a status seizure.")
            };

        Assert.True(HasExistingAnchorReplacementConflict(
            existing,
            proposed,
            AddressAnchor(796969, "114 jane manor circle", "114 Jane Manor Circle"),
            AddressAnchor(796973, "1970 buckley st", "1970 Buckley Street")));
    }

    [Fact]
    public void ExistingConcreteAnchorReplacementConflict_AllowsMembershipStillSupportingProtectedLocation()
    {
        var existing = new IncidentDto
        {
            Title = "Seizure at 114 Jane Manor Circle",
            Detail = "EMS responding to a seizure call for a patient at 114 Jane Manor Circle on Outdating Pike.",
            Calls =
            [
                IncidentCall(796751, 1000, "Seven responded to one fourteen Jane Manor for a seizure."),
                IncidentCall(796969, 1588, "I'm showing you responding to 114 Jane Manor Circle, 114 Jane Manor Circle, Outdating Pike. The child came in as a seizure.")
            ]
        };
        var proposed =
            new[]
            {
                EngineCall(796751, 1000, "Seven responded to one fourteen Jane Manor for a seizure."),
                EngineCall(796969, 1588, "I'm showing you responding to 114 Jane Manor Circle, 114 Jane Manor Circle, Outdating Pike. The child came in as a seizure.")
            };

        Assert.False(HasExistingAnchorReplacementConflict(
            existing,
            proposed,
            AddressAnchor(796969, "114 jane manor circle", "114 Jane Manor Circle")));
    }

    [Fact]
    public void ExistingConcreteAnchorReplacementConflict_BlocksAddingConflictingLocatedCallToProtectedIncident()
    {
        var existing = new IncidentDto
        {
            Title = "Chest pain at 523 Callaway Court",
            Detail = "EMS and Fire responding to a 50-year-old male with chest pain at 523 Callaway Court off Davidson Road.",
            Calls =
            [
                IncidentCall(797519, 1000, "Stand by for a call at 523 Callaway Court for a 50 year old male with chest pain."),
                IncidentCall(797529, 1040, "Responding 523 Callaway Court off the dead end of Davidson Road for chest pain."),
                IncidentCall(797537, 1050, "50 year old male, rapid breathing, pain in the middle of the chest.")
            ]
        };
        var proposed =
            new[]
            {
                EngineCall(797519, 1000, "Stand by for a call at 523 Callaway Court for a 50 year old male with chest pain."),
                EngineCall(797529, 1040, "Responding 523 Callaway Court off the dead end of Davidson Road for chest pain."),
                EngineCall(797537, 1050, "50 year old male, rapid breathing, pain in the middle of the chest."),
                EngineCall(797833, 1990, "Ladder 21 responding to 7301 East Brainerd Road, apartment C8, chest pain.")
            };

        Assert.True(HasExistingAnchorReplacementConflict(
            existing,
            proposed,
            AddressAnchor(797519, "523 callaway court", "523 Callaway Court"),
            AddressAnchor(797529, "523 callaway court", "523 Callaway Court"),
            AddressAnchor(797833, "7301 e brainerd rd", "7301 East Brainerd Road")));
    }

    [Fact]
    public void ExistingConcreteAnchorReplacementConflict_UsesTranscriptExtractedWeakLocationsAsConflictBlockers()
    {
        var existing = new IncidentDto
        {
            Title = "Chest pain at 523 Callaway Court",
            Detail = "EMS and Fire responding to a 50-year-old male with chest pain at 523 Callaway Court off Davidson Road.",
            Calls =
            [
                IncidentCall(797519, 1000, "Stand by for a call. 523 Callaway Court, 523 Callaway Court, 50 year old male with chest pain."),
                IncidentCall(797529, 1040, "Responding 523 Callaway Court off the dead end of Davidson Road for chest pain.")
            ]
        };
        var proposed =
            new[]
            {
                EngineCall(797809, 1900, "Medic 7 respond to 601 East Brainerd Road, apartment C8, 46 year old male with chest pain."),
                EngineCall(797833, 1990, "Ladder 21 responding to 7301 East Brainerd Road, apartment C8, chest pain.")
            };

        Assert.True(HasExistingAnchorReplacementConflict(existing, proposed));
    }

    [Fact]
    public void ExistingConcreteAnchorReplacementConflict_DoesNotBlockRewriteWhenExistingTitleWasNotSourceBacked()
    {
        var existing = new IncidentDto
        {
            Title = "Vehicle accident at Oliver Ln/Hwy 193",
            Detail = "Traffic crash reported at Oliver Lane and Highway 193.",
            Calls =
            [
                IncidentCall(794517, 1000, "I-75 northbound at the 20.6 for a motor vehicle accident with possible injuries.")
            ]
        };
        var proposed =
            new[]
            {
                EngineCall(794517, 1000, "I-75 northbound at the 20.6 for a motor vehicle accident with possible injuries.")
            };

        Assert.False(HasExistingAnchorReplacementConflict(
            existing,
            proposed,
                new CallAnchorRecord(794517, "highway_mile_marker", "i-75|mm:20.6", "I-75 MM 20.6", "deterministic", 0.98, "{}")));
    }

    [Fact]
    public void ExistingCallSupportsProtectedNarrativeAnchor_RetainsSameSceneFireFollowUp()
    {
        var existing = new IncidentDto
        {
            Title = "Power line fire at 5-6 S Washington St",
            Detail = "Squad 7 reports a limb on main power line that is also on fire at 5-6 South Washington Street.",
            Category = "fire",
            Calls =
            [
                IncidentCall(799715, 1000, "Tree on fire under power lines at 5, 8, South Washington Street.", "fire"),
                IncidentCall(799839, 1374, "Squad 7 is on scene. 5-6 South Washington Street. South Washington Street command. There's a limb across the main power line and it's also on fire.", "fire")
            ]
        };

        var call = EngineCall(
            799839,
            1374,
            "Squad 7 is on scene. 5-6 South Washington Street. We'll have South Washington Street command. There's a limb that is falling across the main power line. It's also on fire.",
            "fire");

        Assert.True(ExistingCallSupportsProtectedNarrativeAnchor(existing, call));
    }

    [Fact]
    public void ExistingCallSupportsProtectedNarrativeAnchor_DoesNotRetainDifferentLocationUtilityFire()
    {
        var existing = new IncidentDto
        {
            Title = "Power line fire at 5-6 S Washington St",
            Detail = "Squad 7 reports a limb on main power line that is also on fire at 5-6 South Washington Street.",
            Category = "fire",
            Calls =
            [
                IncidentCall(799715, 1000, "Tree on fire under power lines at 5, 8, South Washington Street.", "fire")
            ]
        };

        var call = EngineCall(
            799877,
            1491,
            "Behind the 400 block of FEMA Avenue off West 45th and West 6th, wires are sparking and the transformer is possibly on fire.",
            "fire");

        Assert.False(ExistingCallSupportsProtectedNarrativeAnchor(existing, call));
    }

    [Fact]
    public void SiblingIncidentMergeConflict_BlocksDifferentFireAssistanceLocations()
    {
        var target = new IncidentDto
        {
            Id = 3892,
            Title = "Fire assistance at 17 Off A Date Pike, Borders Loop",
            Detail = "Fire unit 5 responding to an apartment complex at 331 or 371 Borders Loop for fire assistance.",
            Calls =
            [
                IncidentCall(799031, 1000, "331 Borders Loop, 371 Borders Loop apartment, 17 off a date and pike for a fire assistant to fire number 5.")
            ]
        };
        var duplicate = new IncidentDto
        {
            Id = 3890,
            Title = "Fire assistance at 6931 Anabela Lane",
            Detail = "Fire crew six responding to 6931 Anabela Lane for lift assistance for a 73 year old male.",
            Calls =
            [
                IncidentCall(799089, 1040, "Sixteen eighteen, you're responding, 6931 and a view lane, lift assist on a 73 year old male.")
            ]
        };

        Assert.True(HasConflictingSiblingIncidentLocations(target, duplicate));
    }

    [Fact]
    public void SiblingIncidentMergeConflict_AllowsSameFireAssistanceLocationVariant()
    {
        var target = new IncidentDto
        {
            Id = 3890,
            Title = "Fire assistance at 6931 Anabela Lane",
            Detail = "Highway 58 fire responding to 6931 Anabela Lane for assistance.",
            Calls =
            [
                IncidentCall(799059, 1000, "Highway 58 respond, 6931 Anabela Lane for fire assistance to citizen.")
            ]
        };
        var duplicate = new IncidentDto
        {
            Id = 3895,
            Title = "Fire assistance at 6931 Anabela Lane",
            Detail = "Fire crew six responding to 6931 Anabela Lane for lift assistance.",
            Calls =
            [
                IncidentCall(799089, 1040, "Sixteen eighteen, you're responding, 6931 and a view lane, lift assist on a 73 year old male.")
            ]
        };

        Assert.False(HasConflictingSiblingIncidentLocations(target, duplicate));
    }

    private static bool HasConflictingSiblingIncidentLocations(IncidentDto target, IncidentDto duplicate)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "HaveConflictingSiblingIncidentLocations",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var targetCalls = target.Calls.Select(ToEngineCall).ToList();
        var duplicateCalls = duplicate.Calls.Select(ToEngineCall).ToList();
        return (bool)method.Invoke(
            null,
            [
                target,
                duplicate,
                targetCalls,
                duplicateCalls,
                Array.Empty<CallLocationDashboardRow>(),
                new Dictionary<long, IReadOnlyList<CallAnchorRecord>>()
            ])!;
    }

    private static bool HasExistingAnchorReplacementConflict(
        IncidentDto existing,
        IReadOnlyList<EngineCall> proposed,
        params CallAnchorRecord[] anchors)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "HasExistingIncidentConcreteAnchorReplacementConflict",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var anchorsByCall = anchors
            .GroupBy(anchor => anchor.CallId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<CallAnchorRecord>)group.ToList());
        return (bool)method.Invoke(
            null,
            [
                existing,
                proposed,
                Array.Empty<CallLocationDashboardRow>(),
                anchorsByCall
            ])!;
    }

    private static bool ExistingCallSupportsProtectedNarrativeAnchor(IncidentDto existing, EngineCall call)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "ExistingCallSupportsProtectedNarrativeAnchor",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(
            null,
            [
                existing,
                call,
                Array.Empty<CallLocationDashboardRow>(),
                new Dictionary<long, IReadOnlyList<CallAnchorRecord>>()
            ])!;
    }

    private static IReadOnlyList<object> ParseIncidentExtractionResponse(string json)
    {
        var result = ParseIncidentExtractionResult(json);
        var incidents = result.GetType().GetProperty("Incidents")!.GetValue(result);
        Assert.NotNull(incidents);
        return ((System.Collections.IEnumerable)incidents).Cast<object>().ToList();
    }

    private static object ParseIncidentExtractionResult(string json)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "ParseIncidentExtractionResponse",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method.Invoke(null, [json]);
        Assert.NotNull(result);
        return result;
    }

    private static IReadOnlyList<string> FilterIncidentV3CurrentCallIds(
        IReadOnlyList<string> values,
        IReadOnlyList<string> validCallTokens)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "FilterIncidentV3CurrentCallIds",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method.Invoke(
            null,
            [
                values,
                validCallTokens.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ]);
        Assert.NotNull(result);
        return ((System.Collections.IEnumerable)result).Cast<string>().ToList();
    }

    private static TimeSpan IncidentExtractionFallbackChunkTimeout(int candidateCount, int configuredTimeoutMs)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "IncidentExtractionFallbackChunkTimeout",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (TimeSpan)method.Invoke(null, [candidateCount, configuredTimeoutMs])!;
    }

    private static bool IsIncidentExtractionTimeout(Exception exception)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "IsIncidentExtractionTimeout",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [exception])!;
    }

    private static bool IsCallerRequestedCompletionCancellation(Exception exception, CancellationToken ct)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "IsCallerRequestedCompletionCancellation",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [exception, ct])!;
    }

    private static bool ShouldStopIncidentExtractionFallbackAfterTerminalTimeouts(int consecutiveTerminalTimeoutSkips)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "ShouldStopIncidentExtractionFallbackAfterTerminalTimeouts",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [consecutiveTerminalTimeoutSkips])!;
    }

    private static string ClassifyShadowDecisionRelationship(
        AutomaticInsightsService service,
        string systemShortName,
        PersistenceDecisionV2 decision,
        object currentResult,
        DateTimeOffset now)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "ClassifyShadowDecisionRelationship",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(service, [systemShortName, decision, currentResult, now])!;
    }

    private static IReadOnlyList<object> ResolveShadowCandidatesFromCurrentResult(
        AutomaticInsightsService service,
        string systemShortName,
        object currentResult,
        DateTimeOffset now)
    {
        var method = typeof(AutomaticInsightsService).GetMethod(
            "ResolveShadowCandidatesFromCurrentResult",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method.Invoke(service, [systemShortName, currentResult, now]);
        Assert.NotNull(result);
        return ((System.Collections.IEnumerable)result).Cast<object>().ToList();
    }

    private static string GetString(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return (string)property.GetValue(value)!;
    }

    private static List<string> GetStringList(object value, string propertyName)
    {
        var property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        var list = property.GetValue(value);
        Assert.NotNull(list);
        return ((System.Collections.IEnumerable)list).Cast<string>().ToList();
    }

    private static CallAnchorRecord AddressAnchor(long callId, string value, string display) =>
        new(callId, "address", value, display, "deterministic", 0.86, "{}");

    private static IncidentCallDto IncidentCall(long id, long start, string transcript, string category = "ems") =>
        new(id, start, transcript, "/audio", category, category == "fire" ? "Fire Dispatch" : "EMS Dispatch", "test");

    private static EngineCall ToEngineCall(IncidentCallDto call) => EngineCall(call.CallId, call.RawTimestamp, call.Transcript, call.Category);

    private static EngineCall EngineCall(long id, long start, string transcript, string category = "ems") => new()
    {
        Id = id,
        StartTime = start,
        StopTime = start + 5,
        SystemShortName = "test",
        Talkgroup = 100,
        TalkgroupName = category == "fire" ? "Fire Dispatch" : "EMS Dispatch",
        Category = category,
        Transcription = transcript,
        TranscriptionStatus = "complete",
        QualityReason = "ok"
    };
}
