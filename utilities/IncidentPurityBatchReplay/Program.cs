using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using pizzad;

if (args.Length != 4)
{
    Console.Error.WriteLine(
        "Usage: IncidentPurityBatchReplay <snapshot.json> <results.json> <base-url> <model>");
    return 2;
}

var snapshotPath = Path.GetFullPath(args[0]);
var resultsPath = Path.GetFullPath(args[1]);
var baseUrl = args[2];
var model = args[3];
var snapshotBytes = await File.ReadAllBytesAsync(snapshotPath);
var snapshotSha256 = Convert.ToHexString(SHA256.HashData(snapshotBytes));
var snapshot = JsonSerializer.Deserialize<Snapshot>(snapshotBytes, JsonOptions())
               ?? throw new InvalidDataException("The purity snapshot was empty.");
if (snapshot.SchemaVersion != 1 || snapshot.Cases.Count is < 1 or > 200)
    throw new InvalidDataException("The purity snapshot schema or case count is invalid.");

var completed = new Dictionary<string, CaseResult>(StringComparer.Ordinal);
if (File.Exists(resultsPath))
{
    var existing = JsonSerializer.Deserialize<BatchResult>(await File.ReadAllBytesAsync(resultsPath), JsonOptions())
                   ?? throw new InvalidDataException("The existing result was empty.");
    if (existing.SchemaVersion != 1 || existing.SnapshotSha256 != snapshotSha256 || existing.Model != model)
        throw new InvalidDataException("The existing result belongs to different evidence or a different model.");
    foreach (var result in existing.Results)
        completed.Add(Key(result.CandidateCallId, result.IncidentId), result);
}

using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
var adapter = new EvidencePurityAdapter(new OpenAiEvidencePurityDecider(client, baseUrl, string.Empty, model));
foreach (var rawCase in snapshot.Cases)
{
    var input = rawCase.Deserialize<ReplayInput>(JsonOptions())
                ?? throw new InvalidDataException("A purity snapshot case was empty.");
    var key = Key(input.Candidate.CallId, input.IncidentId);
    if (completed.ContainsKey(key))
        continue;

    var incident = await adapter.DecideAsync(new EvidencePurityContext(
        new EvidencePurityOwnerIdentity("incident", input.IncidentId, input.IncidentObservationId),
        EvidencePurityScope.ExistingIncident,
        input.EstablishedCalls.Select(ToSource)), default);
    var candidate = await adapter.DecideAsync(new EvidencePurityContext(
        new EvidencePurityOwnerIdentity("candidate", input.Candidate.CallId, input.Candidate.ObservationId),
        EvidencePurityScope.CandidateConversationSegment,
        [ToSource(input.Candidate)]), default);
    completed.Add(key, new CaseResult(
        input.IncidentId,
        input.Candidate.CallId,
        incident,
        candidate,
        IncidentMembershipPurityGate.Evaluate(incident, candidate)));
    await SaveAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        input.Candidate.CallId,
        input.IncidentId,
        incident = incident.Disposition,
        candidate = candidate.Disposition,
        gate = completed[key].Gate.MayEvaluateMembership
    }, JsonOptions()));
}

await SaveAsync();
return 0;

async Task SaveAsync()
{
    Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
    var output = new BatchResult(
        1,
        snapshotSha256,
        model,
        DateTimeOffset.UtcNow,
        completed.Values.ToArray());
    var temporaryPath = resultsPath + $".tmp-{Guid.NewGuid():N}";
    try
    {
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(output, JsonOptions(true)));
        File.Move(temporaryPath, resultsPath, true);
    }
    finally
    {
        if (File.Exists(temporaryPath))
            File.Delete(temporaryPath);
    }
}

static string Key(long candidateCallId, long incidentId) => $"{candidateCallId}:{incidentId}";

static (IncidentMembershipSourceIdentity Identity, IncidentMembershipModelEvidence Evidence) ToSource(ReplayCall call) =>
    (new IncidentMembershipSourceIdentity(call.CallId, call.ObservationId),
        new IncidentMembershipModelEvidence(
            DateTimeOffset.FromUnixTimeSeconds(call.StartTime),
            call.Transcript,
            call.SystemName,
            call.TalkgroupName,
            call.StopTime >= call.StartTime ? TimeSpan.FromSeconds(call.StopTime - call.StartTime) : null));

static JsonSerializerOptions JsonOptions(bool indented = false)
{
    var options = EngineConfig.JsonOptions();
    options.WriteIndented = indented;
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
    return options;
}

internal sealed record Snapshot(
    int SchemaVersion,
    long WindowStartUnix,
    long WindowEndUnix,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<JsonElement> Cases);

internal sealed record BatchResult(
    int SchemaVersion,
    string SnapshotSha256,
    string Model,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<CaseResult> Results);

internal sealed record CaseResult(
    long IncidentId,
    long CandidateCallId,
    EvidencePurityResult ExistingIncident,
    EvidencePurityResult CandidateConversationSegment,
    IncidentMembershipPurityGateResult Gate);

internal sealed record ReplayInput(
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
