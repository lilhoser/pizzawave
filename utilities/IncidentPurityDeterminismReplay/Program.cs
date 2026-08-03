using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using pizzad;

if (args.Length != 7 ||
    !long.TryParse(args[2], out var candidateCallId) ||
    !int.TryParse(args[3], out var repeatCount) || repeatCount is < 2 or > 100)
{
    Console.Error.WriteLine(
        "Usage: IncidentPurityDeterminismReplay <snapshot.json> <results.json> <candidate-call-id> <repeats:2..100> <base-url> <model> <scope:incident|candidate>");
    return 2;
}

var snapshotPath = Path.GetFullPath(args[0]);
var resultsPath = Path.GetFullPath(args[1]);
var baseUrl = args[4];
var model = args[5];
var scope = args[6] switch
{
    "incident" => EvidencePurityScope.ExistingIncident,
    "candidate" => EvidencePurityScope.CandidateConversationSegment,
    _ => throw new ArgumentException("Scope must be 'incident' or 'candidate'.")
};
var snapshotBytes = await File.ReadAllBytesAsync(snapshotPath);
var snapshotSha256 = Convert.ToHexString(SHA256.HashData(snapshotBytes));
var snapshot = JsonSerializer.Deserialize<Snapshot>(snapshotBytes, JsonOptions())
               ?? throw new InvalidDataException("The purity snapshot was empty.");
var input = snapshot.Cases
    .Select(item => item.Deserialize<ReplayInput>(JsonOptions())
                    ?? throw new InvalidDataException("A purity snapshot case was empty."))
    .SingleOrDefault(item => item.Candidate.CallId == candidateCallId)
    ?? throw new InvalidDataException($"Candidate call {candidateCallId} was not found exactly once.");

var iterations = new List<IterationResult>();
if (File.Exists(resultsPath))
{
    var existing = JsonSerializer.Deserialize<ReplayResult>(await File.ReadAllBytesAsync(resultsPath), JsonOptions())
                   ?? throw new InvalidDataException("The existing result was empty.");
    if (existing.SchemaVersion != 1 || existing.SnapshotSha256 != snapshotSha256 ||
        existing.CandidateCallId != candidateCallId || existing.IncidentId != input.IncidentId ||
        existing.Model != model || existing.Scope != scope || existing.RepeatCount != repeatCount)
        throw new InvalidDataException("The existing result belongs to a different deterministic replay contract.");
    iterations.AddRange(existing.Iterations);
}

var context = scope == EvidencePurityScope.ExistingIncident
    ? new EvidencePurityContext(
        new EvidencePurityOwnerIdentity("incident", input.IncidentId, input.IncidentObservationId),
        scope,
        input.EstablishedCalls.Select(ToSource))
    : new EvidencePurityContext(
        new EvidencePurityOwnerIdentity("candidate", input.Candidate.CallId, input.Candidate.ObservationId),
        scope,
        [ToSource(input.Candidate)]);

for (var index = iterations.Count + 1; index <= repeatCount; index++)
{
    using var capture = new CaptureHandler();
    using var client = new HttpClient(capture) { Timeout = TimeSpan.FromMinutes(5) };
    var result = await new EvidencePurityAdapter(
        new OpenAiEvidencePurityDecider(client, baseUrl, string.Empty, model))
        .DecideAsync(context, default);
    if (!string.Equals(result.RequestSha256, capture.RequestSha256, StringComparison.Ordinal))
        throw new InvalidDataException("The decider request hash did not match the exact transmitted request body.");
    iterations.Add(new IterationResult(
        index,
        result,
        capture.RequestSha256,
        capture.ResponseSha256,
        capture.RawResponse));
    await SaveAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        iteration = index,
        disposition = result.Disposition,
        requestSha256 = result.RequestSha256,
        responseSha256 = capture.ResponseSha256,
        result.DurationMilliseconds,
        result.TotalTokens
    }, JsonOptions()));
}

await SaveAsync();
return 0;

async Task SaveAsync()
{
    var requestHashes = iterations.Select(item => item.RequestSha256).Distinct(StringComparer.Ordinal).ToArray();
    var dispositions = iterations.Select(item => item.Decision.Disposition).Distinct().ToArray();
    var output = new ReplayResult(
        1,
        snapshotSha256,
        candidateCallId,
        input.IncidentId,
        model,
        scope,
        repeatCount,
        DateTimeOffset.UtcNow,
        requestHashes.Length == 1,
        dispositions.Length == 1,
        requestHashes,
        dispositions,
        iterations.ToArray());
    Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
    var temporaryPath = resultsPath + $".tmp-{Guid.NewGuid():N}";
    try
    {
        await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(output, JsonOptions(true)));
        File.Move(temporaryPath, resultsPath, true);
    }
    finally
    {
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
    }
}

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

internal sealed class CaptureHandler : HttpClientHandler
{
    public string RequestSha256 { get; private set; } = string.Empty;
    public string ResponseSha256 { get; private set; } = string.Empty;
    public string RawResponse { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var requestBytes = await request.Content!.ReadAsByteArrayAsync(ct);
        RequestSha256 = Convert.ToHexString(SHA256.HashData(requestBytes));
        var response = await base.SendAsync(request, ct);
        var responseBytes = await response.Content.ReadAsByteArrayAsync(ct);
        ResponseSha256 = Convert.ToHexString(SHA256.HashData(responseBytes));
        RawResponse = Encoding.UTF8.GetString(responseBytes);
        var replacement = new ByteArrayContent(responseBytes);
        foreach (var header in response.Content.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content.Dispose();
        response.Content = replacement;
        return response;
    }
}

internal sealed record Snapshot(int SchemaVersion, long WindowStartUnix, long WindowEndUnix, DateTimeOffset UpdatedAtUtc, IReadOnlyList<JsonElement> Cases);
internal sealed record ReplayInput(long IncidentId, string IncidentObservationId, IReadOnlyList<ReplayCall> EstablishedCalls, ReplayCall Candidate);
internal sealed record ReplayCall(long CallId, string ObservationId, long StartTime, long StopTime, string SystemName, string TalkgroupName, string Transcript);
internal sealed record IterationResult(int Iteration, EvidencePurityResult Decision, string RequestSha256, string ResponseSha256, string RawResponse);
internal sealed record ReplayResult(
    int SchemaVersion,
    string SnapshotSha256,
    long CandidateCallId,
    long IncidentId,
    string Model,
    EvidencePurityScope Scope,
    int RepeatCount,
    DateTimeOffset UpdatedAtUtc,
    bool RequestBodiesInvariant,
    bool DispositionsInvariant,
    IReadOnlyList<string> RequestSha256Values,
    IReadOnlyList<EvidencePurityDisposition> Dispositions,
    IReadOnlyList<IterationResult> Iterations);
