using pizzalib;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class LocalImportService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EnginePipeline _pipeline;
    private readonly EventStream _events;
    private readonly ILogger<LocalImportService> _logger;
    private readonly object _jobLock = new();
    private readonly Dictionary<long, CancellationTokenSource> _runningJobs = new();
    private readonly HashSet<long> _pausedJobs = new();
    private static readonly TimeSpan StaleActiveJobAge = TimeSpan.FromHours(6);
    private const int QuickImportMaxHours = 48;
    private const int DefaultBatchCallCap = 5000;
    private const long DefaultBatchByteCap = 20L * 1024 * 1024 * 1024;

    public LocalImportService(
        EngineConfig config,
        EngineDatabase database,
        EnginePipeline pipeline,
        EventStream events,
        ILogger<LocalImportService> logger)
    {
        _config = config;
        _database = database;
        _pipeline = pipeline;
        _events = events;
        _logger = logger;
    }

    public async Task<LocalArchiveAvailabilityResponse> GetAvailabilityAsync(CancellationToken ct)
    {
        var captureDir = ResolveCaptureDir();
        if (string.IsNullOrWhiteSpace(captureDir))
            return new LocalArchiveAvailabilityResponse(false, string.Empty, null, null, 0, 0, 0, 0, "TR config does not specify captureDir.");
        if (!Directory.Exists(captureDir))
            return new LocalArchiveAvailabilityResponse(false, captureDir, null, null, 0, 0, 0, 0, "Local TR captureDir does not exist.");

        var stats = new LocalAvailabilityStats();
        DiscoverAvailabilityRecursive(captureDir, stats, ct, depth: 0);
        await Task.CompletedTask;

        var available = stats.EarliestLocal.HasValue && stats.LatestLocal.HasValue;
        var message = available
            ? $"Local TR recordings are visible from {stats.EarliestLocal.GetValueOrDefault():d} to {stats.LatestLocal.GetValueOrDefault():d}. Pick a range and click Estimate for exact file counts."
            : "No dated .bin recordings were visible under the configured TR captureDir.";
        if (stats.SkippedDirectories > 0)
            message += $" Skipped {stats.SkippedDirectories:N0} unreadable folder(s).";

        return new LocalArchiveAvailabilityResponse(
            available,
            captureDir,
            stats.EarliestLocal,
            stats.LatestLocal,
            stats.FileCount,
            stats.TotalBytes,
            stats.ScannedDirectories,
            stats.SkippedDirectories,
            message);
    }

    public async Task<LocalImportEstimateResponse> EstimateAsync(LocalImportEstimateRequest request, CancellationToken ct)
    {
        await EnsureImportAllowedAsync(ct);
        var captureDir = ResolveCaptureDir();
        if (string.IsNullOrWhiteSpace(captureDir) || !Directory.Exists(captureDir))
            return new LocalImportEstimateResponse(0, 0, false, "Local TR captureDir is not available.");

        var span = request.EndLocal - request.StartLocal;
        var exceedsQuick = span.TotalHours > QuickImportMaxHours;
        var candidates = DiscoverCandidates(captureDir, request.StartLocal, request.EndLocal, ct);
        var filtered = new List<LocalArchiveCandidate>();
        foreach (var candidate in candidates)
        {
            var status = await _database.GetBackfillItemStatusAsync("local", candidate.Path, ct);
            if (!string.Equals(status, "imported", StringComparison.OrdinalIgnoreCase))
                filtered.Add(candidate);
        }

        return new LocalImportEstimateResponse(
            filtered.Count,
            filtered.Sum(c => c.Size),
            exceedsQuick,
            exceedsQuick
                ? $"Large local import requires confirmation: {filtered.Count:N0} new files, {FormatBytes(filtered.Sum(c => c.Size))}."
                : $"Quick local import estimate: {filtered.Count:N0} new files, {FormatBytes(filtered.Sum(c => c.Size))}.");
    }

    public async Task<JobDto?> StartRecentReconciliationAsync(DateTime startLocal, DateTime endLocal, CancellationToken ct)
    {
        var captureDir = ResolveCaptureDir();
        if (string.IsNullOrWhiteSpace(captureDir) || !Directory.Exists(captureDir))
            return null;

        var canceled = await _database.CancelStaleActiveJobsAsync(
            "local_import",
            StaleActiveJobAge,
            "Canceled stale local import job after pizzad restart; no worker was attached.",
            ct);
        if (canceled > 0)
            _logger.LogWarning("Canceled {Count} stale local import job(s) before recent reconciliation", canceled);

        if (await _database.HasActiveJobAsync("local_import", ct))
            return null;

        var estimate = await EstimateAsync(new LocalImportEstimateRequest(startLocal, endLocal), ct);
        if (estimate.CandidateCount <= 0)
            return null;

        return await StartImportAsync(
            new LocalImportRequest(startLocal, endLocal, ConfirmLargeImport: false, CallCap: null, ByteCap: null),
            ct,
            "Recent 48h local TR reconciliation queued.");
    }

    public Task<JobDto> StartImportAsync(LocalImportRequest request, CancellationToken ct)
    {
        return StartImportAsync(request, ct, null);
    }

    private async Task<JobDto> StartImportAsync(LocalImportRequest request, CancellationToken ct, string? messageOverride)
    {
        await EnsureImportAllowedAsync(ct);
        var canceled = await _database.CancelStaleActiveJobsAsync(
            "local_import",
            StaleActiveJobAge,
            "Canceled stale local import job after pizzad restart; no worker was attached.",
            ct);
        if (canceled > 0)
            _logger.LogWarning("Canceled {Count} stale local import job(s) before starting a new import", canceled);

        if (await _database.HasActiveJobAsync("local_import", ct))
            throw new InvalidOperationException("Another local import is already queued, running, or paused.");

        var span = request.EndLocal - request.StartLocal;
        if (span.TotalHours > QuickImportMaxHours && !request.ConfirmLargeImport)
            throw new InvalidOperationException("Large local imports require explicit confirmation.");

        var estimate = await EstimateAsync(new LocalImportEstimateRequest(request.StartLocal, request.EndLocal), ct);
        if (estimate.CandidateCount <= 0)
            throw new InvalidOperationException("No new local TR files were found for the selected range.");

        var callCap = request.CallCap ?? DefaultBatchCallCap;
        var byteCap = request.ByteCap ?? DefaultBatchByteCap;
        if (!request.ConfirmLargeImport && (estimate.CandidateCount > callCap || estimate.CandidateBytes > byteCap))
            throw new InvalidOperationException($"Import exceeds configured guardrails ({estimate.CandidateCount:N0} files, {FormatBytes(estimate.CandidateBytes)}).");

        var job = new JobDto
        {
            Type = "local_import",
            Status = "queued",
            Total = estimate.CandidateCount,
            Message = messageOverride ?? (span.TotalHours > QuickImportMaxHours ? "Large local pizzastack prime import queued." : "Quick local import queued."),
            CreatedAtUtc = DateTime.UtcNow
        };
        var jobId = await _database.AddJobAsync(job, ct);
        var cts = new CancellationTokenSource();
        lock (_jobLock)
            _runningJobs[jobId] = cts;
        _ = Task.Run(() => RunImportJobAsync(jobId, request, cts.Token));
        await _events.PublishAsync("job_updated", new { jobId, type = "local_import", status = "queued" }, ct);
        return job with { Id = jobId };
    }

    private async Task EnsureImportAllowedAsync(CancellationToken ct)
    {
        var pending = await _database.CountPendingTranscriptionCallsAsync(ct);
        if (pending > 0)
            throw new InvalidOperationException($"Local import is paused until the transcription queue drains. Pending transcriptions: {pending:N0}.");
    }

    public async Task<JobDto?> ControlJobAsync(long jobId, string action, CancellationToken ct)
    {
        action = (action ?? string.Empty).Trim().ToLowerInvariant();
        switch (action)
        {
            case "pause":
                lock (_jobLock) _pausedJobs.Add(jobId);
                await _database.UpdateJobAsync(jobId, "paused", null, null, null, "Paused by user.", false, false, ct);
                break;
            case "resume":
                lock (_jobLock) _pausedJobs.Remove(jobId);
                await _database.UpdateJobAsync(jobId, "running", null, null, null, "Resumed by user.", false, false, ct);
                break;
            case "cancel":
                lock (_jobLock)
                {
                    if (_runningJobs.TryGetValue(jobId, out var cts))
                        cts.Cancel();
                    _pausedJobs.Remove(jobId);
                }
                await _database.UpdateJobAsync(jobId, "canceled", null, null, null, "Canceled by user.", false, true, ct);
                break;
            default:
                throw new InvalidOperationException("Job action must be pause, resume, or cancel.");
        }

        await _events.PublishAsync("job_updated", new { jobId, action }, ct);
        return await _database.GetJobAsync(jobId, ct);
    }

    private async Task RunImportJobAsync(long jobId, LocalImportRequest request, CancellationToken ct)
    {
        var completed = 0;
        var failed = 0;
        try
        {
            await _database.UpdateJobAsync(jobId, "running", null, 0, 0, "Discovering local TR recordings...", setStarted: true, setFinished: false, ct);
            await _events.PublishAsync("job_updated", new { jobId, status = "running" }, ct);
            var captureDir = ResolveCaptureDir();
            if (string.IsNullOrWhiteSpace(captureDir) || !Directory.Exists(captureDir))
                throw new InvalidOperationException("Local TR captureDir is not available.");

            var discovered = DiscoverCandidates(captureDir, request.StartLocal, request.EndLocal, ct);
            var candidates = new List<LocalArchiveCandidate>();
            foreach (var candidate in discovered)
            {
                var backfillStatus = await _database.GetBackfillItemStatusAsync("local", candidate.Path, ct);
                if (!string.Equals(backfillStatus, "imported", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(candidate);
            }

            await _database.UpdateJobAsync(jobId, "running", candidates.Count, 0, 0, $"Importing {candidates.Count:N0} local file(s)...", false, false, ct);
            if (candidates.Count == 0)
            {
                await _database.UpdateJobAsync(jobId, "completed", 0, 0, 0, "Local import finished: no new files.", false, true, ct);
                await _events.PublishAsync("job_updated", new { jobId, status = "completed", completed = 0, failed = 0, total = 0 }, ct);
                return;
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(jobId, ct);
                    var priorStatus = await _database.GetBackfillItemStatusAsync("local", candidate.Path, ct);
                    if (string.Equals(priorStatus, "imported", StringComparison.OrdinalIgnoreCase))
                        continue;

                    await _database.UpsertBackfillItemAsync("local", candidate.Path, candidate.Path, string.Empty, ToUnix(candidate.TimestampLocal), candidate.Size, "importing", string.Empty, ct);

                    var settings = new Settings
                    {
                        analogSamplingRate = _config.Transcription.AnalogSampleRate,
                        listenPort = _config.Ingest.CallstreamPort
                    };
                    var raw = new RawCallData(settings);
                    try
                    {
                        using var input = File.OpenRead(candidate.Path);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        if (await raw.ProcessClientData(input, linked))
                            await _pipeline.IngestRawCallAsync(raw, imported: true, ct);
                        else
                            raw.Dispose();
                    }
                    catch
                    {
                        raw.Dispose();
                        throw;
                    }

                    completed++;
                    await _database.UpsertBackfillItemAsync("local", candidate.Path, candidate.Path, string.Empty, ToUnix(candidate.TimestampLocal), candidate.Size, "imported", string.Empty, ct);
                }
                catch (Exception ex)
                {
                    failed++;
                    await _database.UpsertBackfillItemAsync("local", candidate.Path, candidate.Path, string.Empty, ToUnix(candidate.TimestampLocal), candidate.Size, "failed", ex.Message, CancellationToken.None);
                    _logger.LogWarning(ex, "Failed to import local TR file {Path}", candidate.Path);
                }

                await _database.UpdateJobAsync(jobId, "running", candidates.Count, completed, failed, $"Imported {completed:N0}/{candidates.Count:N0}; failed {failed:N0}.", false, false, ct);
                await _events.PublishAsync("job_updated", new { jobId, status = "running", completed, failed, total = candidates.Count }, ct);
                await Task.Delay(75, ct);
            }

            var status = failed == 0 ? "completed" : "completed_with_errors";
            await _database.UpdateJobAsync(jobId, status, candidates.Count, completed, failed, $"Local import finished: {completed:N0} imported, {failed:N0} failed.", false, true, ct);
            await _events.PublishAsync("job_updated", new { jobId, status, completed, failed, total = candidates.Count }, ct);
        }
        catch (OperationCanceledException)
        {
            await _database.UpdateJobAsync(jobId, "canceled", null, completed, failed, "Canceled.", false, true, CancellationToken.None);
            await _events.PublishAsync("job_updated", new { jobId, status = "canceled", completed, failed }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local import job {JobId} failed", jobId);
            await _database.UpdateJobAsync(jobId, "failed", null, completed, failed, ex.Message, false, true, CancellationToken.None);
            await _events.PublishAsync("job_updated", new { jobId, status = "failed", completed, failed }, CancellationToken.None);
        }
        finally
        {
            lock (_jobLock)
            {
                if (_runningJobs.Remove(jobId, out var cts))
                    cts.Dispose();
                _pausedJobs.Remove(jobId);
            }
        }
    }

    private async Task WaitIfPausedAsync(long jobId, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lock (_jobLock)
            {
                if (!_pausedJobs.Contains(jobId))
                    return;
            }
            await Task.Delay(1000, ct);
        }
    }

    private List<LocalArchiveCandidate> DiscoverCandidates(string root, DateTime startLocal, DateTime endLocal, CancellationToken ct)
    {
        var candidates = new List<LocalArchiveCandidate>();
        DiscoverRecursive(root, startLocal, endLocal, candidates, ct, depth: 0);
        return candidates.OrderBy(c => c.TimestampLocal).ToList();
    }

    private static void DiscoverRecursive(string path, DateTime startLocal, DateTime endLocal, List<LocalArchiveCandidate> candidates, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 8)
            return;

        IEnumerable<string> directories;
        IEnumerable<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(path).ToList();
            files = Directory.EnumerateFiles(path, "*.bin").ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var timestamp = TryParseArchiveTimestamp(file, info.LastWriteTime);
            if (!timestamp.HasValue || timestamp.Value < startLocal || timestamp.Value > endLocal)
                continue;
            candidates.Add(new LocalArchiveCandidate(Path.GetFullPath(file), timestamp.Value, info.Length));
        }

        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();
            if (MayContainRange(directory, startLocal, endLocal))
                DiscoverRecursive(directory, startLocal, endLocal, candidates, ct, depth + 1);
        }
    }

    private static void DiscoverAvailabilityRecursive(string path, LocalAvailabilityStats stats, CancellationToken ct, int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 8)
            return;

        IEnumerable<string> directories;
        IEnumerable<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(path).ToList();
            files = Directory.EnumerateFiles(path, "*.bin").ToList();
            stats.ScannedDirectories++;
        }
        catch (UnauthorizedAccessException)
        {
            stats.SkippedDirectories++;
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        foreach (var file in files)
        {
            var info = new FileInfo(file);
            var timestamp = TryParseArchiveTimestamp(file, info.LastWriteTime);
            if (!timestamp.HasValue)
                continue;
            stats.FileCount++;
            stats.TotalBytes += info.Length;
            stats.EarliestLocal = !stats.EarliestLocal.HasValue || timestamp.Value < stats.EarliestLocal.Value ? timestamp.Value : stats.EarliestLocal;
            stats.LatestLocal = !stats.LatestLocal.HasValue || timestamp.Value > stats.LatestLocal.Value ? timestamp.Value : stats.LatestLocal;
        }

        foreach (var directory in directories)
            DiscoverAvailabilityRecursive(directory, stats, ct, depth + 1);
    }

    private string ResolveCaptureDir()
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var captureDir = root.TryGetProperty("captureDir", out var cap) ? cap.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(captureDir))
                return string.Empty;
            if (Path.IsPathRooted(captureDir))
                return Path.GetFullPath(captureDir);
            var configDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Directory.GetCurrentDirectory();
            return Path.GetFullPath(Path.Combine(configDir, captureDir));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool MayContainRange(string path, DateTime startLocal, DateTime endLocal)
    {
        var date = TryParseArchivePathDate(path);
        if (!date.HasValue)
            return true;
        return date.Value.Date >= startLocal.Date.AddDays(-1) && date.Value.Date <= endLocal.Date.AddDays(1);
    }

    private static DateTime? TryParseArchiveTimestamp(string path, DateTime fallback)
    {
        var normalized = path.Replace('\\', '/');
        var match = Regex.Match(normalized, @"(?<year>\d{4})[-/](?<month>\d{1,2})[-/](?<day>\d{1,2})(?:[./_-](?<hour>\d{1,2})(?<min>\d{2})(?<sec>\d{2}))?");
        if (match.Success)
        {
            var year = int.Parse(match.Groups["year"].Value);
            var month = int.Parse(match.Groups["month"].Value);
            var day = int.Parse(match.Groups["day"].Value);
            var hour = match.Groups["hour"].Success ? int.Parse(match.Groups["hour"].Value) : 0;
            var min = match.Groups["min"].Success ? int.Parse(match.Groups["min"].Value) : 0;
            var sec = match.Groups["sec"].Success ? int.Parse(match.Groups["sec"].Value) : 0;
            if (year is >= 2000 and <= 2100 && month is >= 1 and <= 12 && day is >= 1 and <= 31 && hour <= 23 && min <= 59 && sec <= 59)
                return new DateTime(year, month, day, hour, min, sec, DateTimeKind.Local);
        }

        return fallback == DateTime.MinValue ? null : fallback;
    }

    private static DateTime? TryParseArchivePathDate(string path)
    {
        var normalized = path.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i <= parts.Length - 3; i++)
        {
            if (int.TryParse(parts[i], out var year) &&
                int.TryParse(parts[i + 1], out var month) &&
                int.TryParse(parts[i + 2], out var day) &&
                year is >= 2000 and <= 2100 &&
                month is >= 1 and <= 12 &&
                day is >= 1 and <= 31)
                return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Local);
        }

        var match = Regex.Match(normalized, @"(?<year>\d{4})[-_](?<month>\d{1,2})[-_](?<day>\d{1,2})");
        if (!match.Success)
            return null;

        var y = int.Parse(match.Groups["year"].Value);
        var m = int.Parse(match.Groups["month"].Value);
        var d = int.Parse(match.Groups["day"].Value);
        return y is >= 2000 and <= 2100 && m is >= 1 and <= 12 && d is >= 1 and <= 31
            ? new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Local)
            : null;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:F1} {units[unit]}";
    }

    private static long ToUnix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

    private sealed record LocalArchiveCandidate(string Path, DateTime TimestampLocal, long Size);

    private sealed class LocalAvailabilityStats
    {
        public DateTime? EarliestLocal { get; set; }
        public DateTime? LatestLocal { get; set; }
        public int FileCount { get; set; }
        public long TotalBytes { get; set; }
        public int ScannedDirectories { get; set; }
        public int SkippedDirectories { get; set; }
    }
}
