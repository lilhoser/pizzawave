using Microsoft.Data.Sqlite;

namespace pizzad;

public sealed class IncidentEventStateCorpusSnapshotReader(string databasePath)
{
    public async Task<IReadOnlyList<EngineCall>> ListCallsAsync(
        long startInclusive,
        long endExclusive,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Snapshot database path is required.", nameof(databasePath));
        if (startInclusive < 0 || endExclusive <= startInclusive)
            throw new ArgumentException("Snapshot call window is invalid.");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT *
            FROM calls
            WHERE start_time >= $start AND start_time < $end
            ORDER BY start_time, id;
            """;
        command.Parameters.AddWithValue("$start", startInclusive);
        command.Parameters.AddWithValue("$end", endExclusive);
        var calls = new List<EngineCall>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            calls.Add(EngineDatabase.ReadCall(reader));
        return calls;
    }
}
