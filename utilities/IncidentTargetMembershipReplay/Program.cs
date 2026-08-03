using System.Text.Json;
using System.Text.Json.Serialization;
using pizzad;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: IncidentTargetMembershipReplay <input.json|->");
    return 2;
}

var inputJson = args[0] == "-"
    ? await Console.In.ReadToEndAsync()
    : await File.ReadAllTextAsync(args[0]);
inputJson = inputJson.TrimStart('\uFEFF');
var input = JsonSerializer.Deserialize<ReplayInput>(
    inputJson,
    EngineConfig.JsonOptions()) ?? throw new InvalidDataException("Replay input was empty.");
using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var decider = new OpenAiIncidentTargetMembershipDecider(
    client,
    input.BaseUrl,
    input.ApiKey ?? string.Empty,
    input.Model);
var context = new IncidentTargetMembershipContext(
    new IncidentTargetIdentity(input.IncidentId, input.IncidentObservationId),
    input.EstablishedCalls.Select(ToSource),
    new IncidentMembershipSourceIdentity(input.DirectlyLinkedCallId, input.DirectlyLinkedObservationId),
    ToSource(input.Candidate));
var result = await new IncidentTargetMembershipAdapter(decider).DecideAsync(context, default);
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

internal sealed record ReplayInput(
    string BaseUrl,
    string? ApiKey,
    string Model,
    long IncidentId,
    string IncidentObservationId,
    long DirectlyLinkedCallId,
    string DirectlyLinkedObservationId,
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
