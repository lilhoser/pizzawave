using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using pizzad;

internal static class ObservationIsolationAudit
{
    public static async Task RunAsync(string[] args)
    {
        var values = Parse(args);
        var databasePath = Path.GetFullPath(Required(values, "--database"));
        var runId = Required(values, "--run-id");
        var outputPath = Path.GetFullPath(Required(values, "--output"));
        if (File.Exists(outputPath))
            throw new InvalidOperationException($"refusing to overwrite existing isolation audit '{outputPath}'");

        var entries = new List<IncidentBatchLedgerEntry>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, content_hash, payload_json
                FROM incident_batch_constructor_shadow_ledger
                WHERE run_id=$run_id
                ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$run_id", runId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var sequence = reader.GetInt64(0);
                var expectedHash = reader.GetString(1);
                var payload = reader.GetString(2);
                var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
                if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                    throw new InvalidDataException($"incident batch ledger entry {sequence} failed content-integrity verification");
                entries.Add(JsonSerializer.Deserialize<IncidentBatchLedgerEntry>(payload, EngineConfig.JsonOptions())
                            ?? throw new InvalidDataException($"incident batch ledger entry {sequence} is empty"));
            }
        }

        var proposalCount = 0;
        var proposedMultiObservationEvents = 0;
        var previouslyAcceptedEvents = 0;
        var acceptedUnderIsolation = 0;
        var rejectedUnderIsolation = 0;
        var acceptedMultiObservationEvents = 0;
        foreach (var entry in entries)
        {
            proposalCount += entry.Proposal.Events.Count;
            proposedMultiObservationEvents += entry.Proposal.Events.Count(item => item.NewObservationIds.Count != 1);
            previouslyAcceptedEvents += IncidentBatchContract.AcceptedEvents(entry).Count;
            var identity = $"{entry.Execution.ConfigurationIdentity};{IncidentBatchContract.ObservationIsolatedOwnershipConfigurationToken}";
            var validation = IncidentBatchContract.ValidateProposal(
                entry.Bundle,
                entry.NewObservationIds,
                entry.Candidates,
                entry.Proposal,
                identity);
            var accepted = IncidentBatchContract.AcceptedEvents(
                entry.Bundle,
                entry.NewObservationIds,
                entry.Candidates,
                entry.Proposal,
                validation.Errors,
                identity);
            acceptedUnderIsolation += accepted.Count;
            rejectedUnderIsolation += entry.Proposal.Events.Count - accepted.Count;
            acceptedMultiObservationEvents += accepted.Count(item => item.NewObservationIds.Count != 1);
        }

        var report = new
        {
            protocolIdentity = "incident-observation-isolation-audit-v1",
            runId,
            ledgerEntries = entries.Count,
            proposalCount,
            previouslyAcceptedEvents,
            proposedMultiObservationEvents,
            acceptedUnderIsolation,
            rejectedUnderIsolation,
            acceptedMultiObservationEvents,
            invariantSatisfied = acceptedMultiObservationEvents == 0
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, EngineConfig.JsonOptions()));
        Console.WriteLine(JsonSerializer.Serialize(report, EngineConfig.JsonOptions()));
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (string.Equals(argument, "--observation-isolation-audit", StringComparison.Ordinal))
                continue;
            if (!argument.StartsWith("--", StringComparison.Ordinal) || ++index >= args.Length)
                throw new ArgumentException("observation-isolation audit arguments must be --name value pairs");
            values[argument] = args[index];
        }
        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"{name} is required");
}
