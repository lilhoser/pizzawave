using System.Text.Json;

if (args.Length != 4 ||
    !int.TryParse(args[1], out var maximumCases) || maximumCases is < 1 or > 200 ||
    !long.TryParse(args[2], out var windowStartUnix) ||
    !long.TryParse(args[3], out var windowEndUnix) ||
    windowEndUnix < windowStartUnix)
{
    Console.Error.WriteLine(
        "Usage: IncidentPuritySnapshot <snapshot.json> <maximum-cases:1..200> <window-start-unix> <window-end-unix>");
    return 2;
}

var snapshotPath = Path.GetFullPath(args[0]);
var cases = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
if (File.Exists(snapshotPath))
{
    using var existingDocument = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath));
    var existing = existingDocument.RootElement;
    if (existing.GetProperty("schemaVersion").GetInt32() != 1 ||
        existing.GetProperty("windowStartUnix").GetInt64() != windowStartUnix ||
        existing.GetProperty("windowEndUnix").GetInt64() != windowEndUnix)
    {
        throw new InvalidDataException("The existing snapshot belongs to a different schema or collection window.");
    }

    foreach (var item in existing.GetProperty("cases").EnumerateArray())
        cases.Add(ValidateAndKey(item), item.Clone());
}

while (cases.Count < maximumCases && await Console.In.ReadLineAsync() is { } line)
{
    line = line.TrimStart('\uFEFF');
    if (string.IsNullOrWhiteSpace(line))
        continue;
    using var document = JsonDocument.Parse(line);
    var item = document.RootElement;
    var key = ValidateAndKey(item);
    cases.TryAdd(key, item.Clone());
}

Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
var snapshot = new Snapshot(
    1,
    windowStartUnix,
    windowEndUnix,
    DateTimeOffset.UtcNow,
    cases.Values.Take(maximumCases).ToArray());
var options = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
};
var temporaryPath = snapshotPath + $".tmp-{Guid.NewGuid():N}";
try
{
    await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(snapshot, options));
    File.Move(temporaryPath, snapshotPath, true);
}
finally
{
    if (File.Exists(temporaryPath))
        File.Delete(temporaryPath);
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    snapshotPath,
    caseCount = snapshot.Cases.Count,
    complete = snapshot.Cases.Count == maximumCases
}));
return 0;

static string ValidateAndKey(JsonElement item)
{
    var incidentId = item.GetProperty("incidentId").GetInt64();
    var incidentObservationId = RequiredString(item, "incidentObservationId");
    var candidate = item.GetProperty("candidate");
    var candidateCallId = candidate.GetProperty("callId").GetInt64();
    var candidateObservationId = RequiredString(candidate, "observationId");
    RequiredString(candidate, "transcript");

    var established = item.GetProperty("establishedCalls");
    if (established.ValueKind != JsonValueKind.Array || established.GetArrayLength() is < 1 or > 5)
        throw new InvalidDataException("A purity case must contain one to five complete established calls.");

    var callIds = new HashSet<long>();
    var observationIds = new HashSet<string>(StringComparer.Ordinal);
    foreach (var call in established.EnumerateArray())
    {
        var callId = call.GetProperty("callId").GetInt64();
        if (callId == candidateCallId)
            throw new InvalidDataException("The candidate is already present in the established incident.");
        if (!callIds.Add(callId) || !observationIds.Add(RequiredString(call, "observationId")))
            throw new InvalidDataException("Established call identities must be unique and complete.");
        RequiredString(call, "transcript");
    }

    if (observationIds.Contains(candidateObservationId) || incidentObservationId == candidateObservationId)
        throw new InvalidDataException("Owner and source observation identities must be distinct.");

    return $"{candidateCallId}:{incidentId}";
}

static string RequiredString(JsonElement owner, string propertyName)
{
    var value = owner.GetProperty(propertyName).GetString();
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidDataException($"'{propertyName}' must be present and non-empty.")
        : value;
}

internal sealed record Snapshot(
    int SchemaVersion,
    long WindowStartUnix,
    long WindowEndUnix,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<JsonElement> Cases);
