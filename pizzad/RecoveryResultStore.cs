using System.Text.Json;

namespace pizzad;

public sealed class RecoveryResultStore
{
    private readonly EngineConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RecoveryResultStore(EngineConfig config) => _config = config;

    public async Task StartAsync(string operation, long? jobId, string message, CancellationToken ct) =>
        await WriteAsync(new RecoveryOperationResultDto(operation, jobId, "running", DateTime.UtcNow, DateTime.UtcNow, null, false, [new(DateTime.UtcNow, "started", "running", message)]), ct);

    public async Task AppendAsync(string operation, string stage, string status, string message, bool finished, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = Read(operation) ?? new RecoveryOperationResultDto(operation, null, "running", DateTime.UtcNow, DateTime.UtcNow, null, false, []);
            var stages = current.Stages.Append(new RecoveryOperationStageDto(DateTime.UtcNow, stage, status, message)).ToList();
            var finalStatus = finished ? status : current.Status;
            await WriteCoreAsync(current with { Status = finalStatus, UpdatedUtc = DateTime.UtcNow, FinishedUtc = finished ? DateTime.UtcNow : null, Acknowledged = false, Stages = stages }, ct);
        }
        finally { _gate.Release(); }
    }

    public IReadOnlyList<RecoveryOperationResultDto> List()
    {
        var root = Root();
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.json").Select(path =>
        {
            try { return JsonSerializer.Deserialize<RecoveryOperationResultDto>(File.ReadAllText(path), EngineConfig.JsonOptions()); }
            catch { return null; }
        }).Where(item => item != null).Cast<RecoveryOperationResultDto>().OrderByDescending(item => item.UpdatedUtc).ToList();
    }

    public async Task<bool> AcknowledgeAsync(string operation, CancellationToken ct)
    {
        var current = Read(operation);
        if (current == null) return false;
        await WriteAsync(current with { Acknowledged = true, UpdatedUtc = DateTime.UtcNow }, ct);
        return true;
    }

    private RecoveryOperationResultDto? Read(string operation)
    {
        var path = PathFor(operation);
        return File.Exists(path) ? JsonSerializer.Deserialize<RecoveryOperationResultDto>(File.ReadAllText(path), EngineConfig.JsonOptions()) : null;
    }

    private async Task WriteAsync(RecoveryOperationResultDto value, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await WriteCoreAsync(value, ct); }
        finally { _gate.Release(); }
    }

    private async Task WriteCoreAsync(RecoveryOperationResultDto value, CancellationToken ct)
    {
        var root = Root();
        Directory.CreateDirectory(root);
        var path = PathFor(value.Operation);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, EngineConfig.JsonOptions()) + Environment.NewLine, ct);
        File.Move(temp, path, overwrite: true);
    }

    private string Root() => Path.Combine(_config.Storage.AppDataRoot, "recovery-results");
    private string PathFor(string operation) => Path.Combine(Root(), string.Concat(operation.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')) + ".json");
}

public sealed record RecoveryOperationResultDto(string Operation, long? JobId, string Status, DateTime CreatedUtc, DateTime UpdatedUtc, DateTime? FinishedUtc, bool Acknowledged, IReadOnlyList<RecoveryOperationStageDto> Stages);
public sealed record RecoveryOperationStageDto(DateTime AtUtc, string Stage, string Status, string Message);
