using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace pizzad;

public sealed class SystemManagerService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EnginePipeline _pipeline;
    private readonly IngestControlService _ingestControl;
    private readonly EmbeddingService _embeddings;
    private readonly LiveTrActivityMonitor _liveTrActivity;

    public SystemManagerService(EngineConfig config, EngineDatabase database, EnginePipeline pipeline, IngestControlService ingestControl, EmbeddingService embeddings, LiveTrActivityMonitor liveTrActivity)
    {
        _config = config;
        _database = database;
        _pipeline = pipeline;
        _ingestControl = ingestControl;
        _embeddings = embeddings;
        _liveTrActivity = liveTrActivity;
    }

    public async Task<object> BuildAsync(CancellationToken ct)
    {
        var process = Process.GetCurrentProcess();
        var dbPath = _config.Storage.DatabasePath;
        var audioRoot = _config.Storage.AudioRoot;
        var dbBytes = FileSize(dbPath) + FileSize(dbPath + "-wal") + FileSize(dbPath + "-shm");
        var audio = Directory.Exists(audioRoot) ? DirectorySize(audioRoot, maxFiles: 25000) : (Bytes: 0L, Files: 0, Truncated: false);
        var appDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && dbPath.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase));

        var tableCounts = await TableCountsAsync(ct);
        const int throughputWindowMinutes = 10;
        var now = DateTime.UtcNow;
        var recentStartUnix = new DateTimeOffset(now.AddMinutes(-throughputWindowMinutes)).ToUnixTimeSeconds();
        var recentCalls = await _database.CountCallsStartedSinceAsync(recentStartUnix, ct);
        var recentTranscribed = await _database.CountTranscriptionCompletionsSinceAsync(now.AddMinutes(-throughputWindowMinutes), ct);
        var recentAudioIngested = await _database.SumAudioSecondsStartedSinceAsync(recentStartUnix, ct);
        var recentAudioTranscribed = await _database.SumAudioSecondsTranscriptionCompletionsSinceAsync(now.AddMinutes(-throughputWindowMinutes), ct);
        var transcriptionPerformance = _pipeline.GetTranscriptionPerformance(TimeSpan.FromMinutes(throughputWindowMinutes));
        var trUnitName = string.IsNullOrWhiteSpace(_config.TrunkRecorder.LogServiceName)
            ? "trunk-recorder"
            : _config.TrunkRecorder.LogServiceName.Trim();
        var trUnit = trUnitName.EndsWith(".service", StringComparison.OrdinalIgnoreCase) ? trUnitName : $"{trUnitName}.service";
        var qdrantUnitName = string.IsNullOrWhiteSpace(_config.Embeddings.QdrantServiceName)
            ? "qdrant"
            : _config.Embeddings.QdrantServiceName.Trim();
        var qdrantUnit = qdrantUnitName.EndsWith(".service", StringComparison.OrdinalIgnoreCase) ? qdrantUnitName : $"{qdrantUnitName}.service";
        var qdrantStorage = Directory.Exists(_config.Embeddings.QdrantStoragePath)
            ? DirectorySize(_config.Embeddings.QdrantStoragePath, maxFiles: 25000)
            : (Bytes: 0L, Files: 0, Truncated: false);
        return new
        {
            service = new
            {
                pizzad = await SystemdStatusAsync("pizzad.service", ct),
                trunkRecorder = await SystemdStatusAsync(trUnit, ct),
                qdrant = await SystemdStatusAsync(qdrantUnit, ct)
            },
            liveTrActivity = _liveTrActivity.GetStatus(now, TrServiceFaultReader.ReadLatest(), TrServiceControlStateReader.ReadLatest()),
            trunkRecorderFault = TrServiceFaultReader.ReadLatest(),
            trunkRecorderControlState = TrServiceControlStateReader.ReadLatest(),
            process = new
            {
                pid = Environment.ProcessId,
                uptimeSeconds = (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
                workingSetBytes = process.WorkingSet64,
                privateMemoryBytes = process.PrivateMemorySize64,
                totalProcessorTimeSeconds = process.TotalProcessorTime.TotalSeconds,
                threadCount = process.Threads.Count
            },
            queues = new
            {
                transcriptionQueueDepth = _pipeline.QueueDepth,
                liveQueueDepth = _pipeline.LiveQueueDepth,
                priorityLiveQueueDepth = _pipeline.PriorityLiveQueueDepth,
                backlogQueueDepth = _pipeline.BacklogQueueDepth,
                pressureThreshold = _pipeline.LivePressureQueueDepth,
                underPressure = _pipeline.IsUnderLivePressure,
                pendingTranscriptions = await _database.CountPendingTranscriptionCallsAsync(ct),
                liveTranscriptionWorkers = _pipeline.LiveTranscriptionWorkerCount,
                whisperThreadsPerWorker = _pipeline.WhisperThreadsPerWorker,
                throughputWindowMinutes,
                deferredLiveQueueDepth = _pipeline.DeferredLiveQueueDepth,
                recentCallsIngested = recentCalls,
                recentCallsTranscribed = recentTranscribed,
                recentIngestPerMinute = recentCalls / (double)throughputWindowMinutes,
                recentTranscribedPerMinute = recentTranscribed / (double)throughputWindowMinutes,
                recentAudioSecondsIngested = recentAudioIngested,
                recentAudioSecondsTranscribed = recentAudioTranscribed,
                recentAudioSecondsIngestedPerMinute = recentAudioIngested / (double)throughputWindowMinutes,
                recentAudioSecondsTranscribedPerMinute = recentAudioTranscribed / (double)throughputWindowMinutes,
                pendingAudioSeconds = await _database.SumPendingTranscriptionAudioSecondsAsync(ct),
                recentTranscriptionSamples = transcriptionPerformance.Count,
                averageTranscriptionSeconds = transcriptionPerformance.AverageWallSeconds,
                averageAudioSeconds = transcriptionPerformance.AverageAudioSeconds,
                averageTranscriptionRealtimeFactor = transcriptionPerformance.AverageRealtimeFactor,
                ingest = _ingestControl.GetStatus(_pipeline.QueueDepth),
                embeddings = await _embeddings.GetHealthAsync(ct)
            },
            storage = new
            {
                databasePath = dbPath,
                databaseBytes = dbBytes,
                audioRoot,
                sampledAudioBytes = audio.Bytes,
                sampledAudioFiles = audio.Files,
                audioSampleTruncated = audio.Truncated,
                qdrantPath = _config.Embeddings.QdrantStoragePath,
                qdrantBytes = qdrantStorage.Bytes,
                qdrantFiles = qdrantStorage.Files,
                qdrantSampleTruncated = qdrantStorage.Truncated,
                diskRoot = appDrive?.RootDirectory.FullName ?? string.Empty,
                diskFreeBytes = appDrive?.AvailableFreeSpace ?? 0,
                diskTotalBytes = appDrive?.TotalSize ?? 0
            },
            tables = tableCounts
        };
    }

    private async Task<Dictionary<string, long>> TableCountsAsync(CancellationToken ct)
    {
        var tables = new[] { "calls", "incidents", "incident_calls", "incident_operation_audit", "call_embedding_jobs", "alert_matches", "jobs", "tr_health_samples", "insight_windows", "insight_events", "lm_usage", "geocode_cache" };
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var connection = _database.OpenConnection();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            try
            {
                result[table] = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
            }
            catch (SqliteException)
            {
                result[table] = 0;
            }
        }
        return result;
    }

    private static async Task<object> SystemdStatusAsync(string unit, CancellationToken ct)
    {
        var active = await CaptureAsync("systemctl", ["is-active", unit], ct);
        var enabled = await CaptureAsync("systemctl", ["is-enabled", unit], ct);
        var show = await CaptureAsync("systemctl", ["show", unit, "--property=ActiveEnterTimestamp,MainPID,SubState,LoadState,NRestarts", "--no-page"], ct);
        return new
        {
            unit,
            active = active.Stdout.Trim(),
            enabled = enabled.Stdout.Trim(),
            detail = ParseSystemctlShow(show.Stdout),
            ok = active.ExitCode == 0
        };
    }

    private static Dictionary<string, string> ParseSystemctlShow(string text)
    {
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf('=');
            if (idx > 0)
                rows[line[..idx]] = line[(idx + 1)..];
        }
        return rows;
    }

    private static async Task<(int ExitCode, string Stdout)> CaptureAsync(string fileName, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process == null)
                return (-1, string.Empty);
            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, stdout + stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static (long Bytes, int Files, bool Truncated) DirectorySize(string root, int maxFiles)
    {
        var bytes = 0L;
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files++;
            if (files > maxFiles)
                return (bytes, files - 1, true);
            try { bytes += new FileInfo(file).Length; } catch { }
        }
        return (bytes, files, false);
    }
}
