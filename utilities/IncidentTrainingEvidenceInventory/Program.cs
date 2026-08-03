using System.IO.Compression;
using System.Text.Json;
using IncidentTrainingEvidenceInventory;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: IncidentTrainingEvidenceInventory <archive.tar.gz> [expected-sha256]");
    return 2;
}

var report = EvidenceArchiveInventory.Create(args[0], args.Length == 2 ? args[1] : null);
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true
}));
return 0;
