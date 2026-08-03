using System.Text.Json;
using System.Text.Json.Serialization;
using pizzad;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: IncidentPurityReplay <target-membership-input.json|->");
    return 2;
}

var inputJson = args[0] == "-"
    ? await Console.In.ReadToEndAsync()
    : await File.ReadAllTextAsync(args[0]);
inputJson = inputJson.TrimStart('\uFEFF');
var input = JsonSerializer.Deserialize<ReplayInput>(inputJson, EngineConfig.JsonOptions())
            ?? throw new InvalidDataException("Purity replay input was empty.");

using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var decider = new OpenAiEvidencePurityDecider(client, input.BaseUrl, input.ApiKey ?? string.Empty, input.Model);
var adapter = new EvidencePurityAdapter(decider);
var incidentContext = new EvidencePurityContext(
    new EvidencePurityOwnerIdentity("incident", input.IncidentId, input.IncidentObservationId),
    EvidencePurityScope.ExistingIncident,
    input.EstablishedCalls.Select(ToSource));
var candidateContext = new EvidencePurityContext(
    new EvidencePurityOwnerIdentity("candidate", input.Candidate.CallId, input.Candidate.ObservationId),
    EvidencePurityScope.CandidateConversationSegment,
    [ToSource(input.Candidate)]);

var incident = await adapter.DecideAsync(incidentContext, default);
var candidate = await adapter.DecideAsync(candidateContext, default);
var result = new PurityReplayResult(
    incident,
    candidate,
    IncidentMembershipPurityGate.Evaluate(incident, candidate));
var outputOptions = EngineConfig.JsonOptions();
outputOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
Console.WriteLine(JsonSerializer.Serialize(result, outputOptions));
return 0;

static (IncidentMembershipSourceIdentity Identity, IncidentMembershipModelEvidence Evidence) ToSource(ReplayCall call) =>
    (new IncidentMembershipSourceIdentity(call.CallId, call.ObservationId),
        new IncidentMembershipModelEvidence(
            DateTimeOffset.FromUnixTimeSeconds(call.StartTime),
            call.Transcript,
            call.SystemName,
            call.TalkgroupName,
            call.StopTime >= call.StartTime ? TimeSpan.FromSeconds(call.StopTime - call.StartTime) : null));

internal sealed record PurityReplayResult(
    EvidencePurityResult ExistingIncident,
    EvidencePurityResult CandidateConversationSegment,
    IncidentMembershipPurityGateResult Gate);

internal sealed record ReplayInput(
    string BaseUrl,
    string? ApiKey,
    string Model,
    long IncidentId,
    string IncidentObservationId,
    IReadOnlyList<ReplayCall> EstablishedCalls,
    ReplayCall Candidate);

internal sealed record ReplayCall(
    long CallId,
    string ObservationId,
    long StartTime,
    long StopTime,
    string SystemName,
    string TalkgroupName,
    string Transcript);
