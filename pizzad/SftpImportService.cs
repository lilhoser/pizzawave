using Renci.SshNet;
using Renci.SshNet.Sftp;
using pizzalib;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class SftpImportService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EnginePipeline _pipeline;
    private readonly EventStream _events;
    private readonly ILogger<SftpImportService> _logger;
    private readonly object _jobLock = new();
    private readonly Dictionary<long, CancellationTokenSource> _runningJobs = new();
    private readonly HashSet<long> _pausedJobs = new();

    public SftpImportService(
        EngineConfig config,
        EngineDatabase database,
        EnginePipeline pipeline,
        EventStream events,
        ILogger<SftpImportService> logger)
    {
        _config = config;
        _database = database;
        _pipeline = pipeline;
        _events = events;
        _logger = logger;
    }

    public async Task<SftpEstimateResponse> EstimateAsync(SftpEstimateRequest request, CancellationToken ct)
    {
        if (!_config.SftpImport.Enabled)
            return new SftpEstimateResponse(0, 0, false, "SFTP import is disabled.");

        var span = request.EndLocal - request.StartLocal;
        var exceedsQuick = span.TotalHours > _config.SftpImport.QuickImportMaxHours;

        var candidates = await DiscoverCandidatesAsync(request.StartLocal, request.EndLocal, ct);
        var filtered = new List<SftpArchiveCandidate>();
        foreach (var candidate in candidates)
        {
            var status = await _database.GetBackfillItemStatusAsync("sftp", candidate.RemotePath, ct);
            if (!string.Equals(status, "imported", StringComparison.OrdinalIgnoreCase))
                filtered.Add(candidate);
        }
        return new SftpEstimateResponse(
            filtered.Count,
            filtered.Sum(c => c.Size),
            exceedsQuick,
            exceedsQuick
                ? $"Large import requires confirmation: {filtered.Count:N0} new files, {FormatBytes(filtered.Sum(c => c.Size))}."
                : $"Quick import estimate: {filtered.Count:N0} new files, {FormatBytes(filtered.Sum(c => c.Size))}.");
    }

    public async Task<JobDto> StartImportAsync(SftpImportRequest request, CancellationToken ct)
    {
        var span = request.EndLocal - request.StartLocal;
        if (span.TotalHours > _config.SftpImport.QuickImportMaxHours && !request.ConfirmLargeImport)
            throw new InvalidOperationException("Large SFTP imports require explicit confirmation.");

        var estimate = await EstimateAsync(new SftpEstimateRequest(request.StartLocal, request.EndLocal), ct);
        var callCap = request.CallCap ?? _config.SftpImport.DefaultBatchCallCap;
        var byteCap = request.ByteCap ?? _config.SftpImport.DefaultBatchByteCap;
        if (!request.ConfirmLargeImport && (estimate.CandidateCount > callCap || estimate.CandidateBytes > byteCap))
            throw new InvalidOperationException($"Import exceeds configured guardrails ({estimate.CandidateCount:N0} files, {FormatBytes(estimate.CandidateBytes)}).");

        var job = new JobDto
        {
            Type = "sftp_import",
            Status = "queued",
            Total = estimate.CandidateCount,
            Message = span.TotalHours > _config.SftpImport.QuickImportMaxHours
                ? "Large pizzastack prime import queued."
                : "Quick SFTP import queued.",
            CreatedAtUtc = DateTime.UtcNow
        };
        var jobId = await _database.AddJobAsync(job, ct);
        var cts = new CancellationTokenSource();
        lock (_jobLock)
            _runningJobs[jobId] = cts;
        _ = Task.Run(() => RunImportJobAsync(jobId, request, cts.Token));
        await _events.PublishAsync("job_updated", new { jobId, type = "sftp_import", status = "queued" }, ct);
        return job with { Id = jobId };
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

    private async Task RunImportJobAsync(long jobId, SftpImportRequest request, CancellationToken ct)
    {
        var completed = 0;
        var failed = 0;
        try
        {
            await _database.UpdateJobAsync(jobId, "running", null, 0, 0, "Discovering SFTP archive files...", setStarted: true, setFinished: false, ct);
            await _events.PublishAsync("job_updated", new { jobId, status = "running" }, ct);
            var candidates = await DiscoverCandidatesAsync(request.StartLocal, request.EndLocal, ct);
            await _database.UpdateJobAsync(jobId, "running", candidates.Count, 0, 0, $"Importing {candidates.Count:N0} files...", false, false, ct);

            using var client = CreateClient();
            client.Connect();
            foreach (var candidate in candidates)
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    await WaitIfPausedAsync(jobId, ct);
                    var priorStatus = await _database.GetBackfillItemStatusAsync("sftp", candidate.RemotePath, ct);
                    if (string.Equals(priorStatus, "imported", StringComparison.OrdinalIgnoreCase))
                    {
                        completed++;
                        continue;
                    }
                    var localPath = BuildCachePath(candidate);
                    await _database.UpsertBackfillItemAsync("sftp", candidate.RemotePath, localPath, string.Empty, ToUnix(candidate.TimestampLocal), candidate.Size, "downloading", string.Empty, ct);
                    Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? ".");
                    using (var output = File.Open(localPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        client.DownloadFile(candidate.RemotePath, output);
                    }

                    var settings = new Settings
                    {
                        analogSamplingRate = _config.Transcription.AnalogSampleRate,
                        listenPort = _config.Ingest.CallstreamPort
                    };
                    var raw = new RawCallData(settings);
                    try
                    {
                        using var input = File.OpenRead(localPath);
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
                    await _database.UpsertBackfillItemAsync("sftp", candidate.RemotePath, localPath, string.Empty, ToUnix(candidate.TimestampLocal), candidate.Size, "imported", string.Empty, ct);
                }
                catch (Exception ex)
                {
                    failed++;
                    await _database.UpsertBackfillItemAsync("sftp", candidate.RemotePath, string.Empty, string.Empty, ToUnix(candidate.TimestampLocal), candidate.Size, "failed", ex.Message, CancellationToken.None);
                    _logger.LogWarning(ex, "Failed to import {RemotePath}", candidate.RemotePath);
                }

                await _database.UpdateJobAsync(jobId, "running", candidates.Count, completed, failed, $"Imported {completed:N0}/{candidates.Count:N0}; failed {failed:N0}.", false, false, ct);
                await _events.PublishAsync("job_updated", new { jobId, status = "running", completed, failed, total = candidates.Count }, ct);
                await Task.Delay(150, ct);
            }

            var status = failed == 0 ? "completed" : "completed_with_errors";
            await _database.UpdateJobAsync(jobId, status, candidates.Count, completed, failed, $"SFTP import finished: {completed:N0} imported, {failed:N0} failed.", false, true, ct);
            await _events.PublishAsync("job_updated", new { jobId, status, completed, failed, total = candidates.Count }, ct);
        }
        catch (OperationCanceledException)
        {
            await _database.UpdateJobAsync(jobId, "canceled", null, completed, failed, "Canceled.", false, true, CancellationToken.None);
            await _events.PublishAsync("job_updated", new { jobId, status = "canceled", completed, failed }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SFTP import job {JobId} failed", jobId);
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

    private async Task<List<SftpArchiveCandidate>> DiscoverCandidatesAsync(DateTime startLocal, DateTime endLocal, CancellationToken ct)
    {
        var candidates = new List<SftpArchiveCandidate>();
        using var client = CreateClient();
        client.Connect();
        DiscoverRecursive(client, NormalizeRemoteRoot(_config.SftpImport.RemoteRoot), startLocal, endLocal, candidates, ct, depth: 0);
        await Task.CompletedTask;
        return candidates
            .OrderBy(c => c.TimestampLocal)
            .ToList();
    }

    private static void DiscoverRecursive(
        SftpClient client,
        string remotePath,
        DateTime startLocal,
        DateTime endLocal,
        List<SftpArchiveCandidate> candidates,
        CancellationToken ct,
        int depth)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > 8)
            return;

        foreach (var entry in client.ListDirectory(remotePath))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Name is "." or "..")
                continue;

            if (entry.IsDirectory)
            {
                if (MayContainRange(entry.FullName, startLocal, endLocal))
                    DiscoverRecursive(client, entry.FullName, startLocal, endLocal, candidates, ct, depth + 1);
                continue;
            }

            if (!entry.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                continue;

            var timestamp = TryParseArchiveTimestamp(entry);
            if (!timestamp.HasValue || timestamp.Value < startLocal || timestamp.Value > endLocal)
                continue;

            candidates.Add(new SftpArchiveCandidate(entry.FullName, entry.Name, timestamp.Value, entry.Length));
        }
    }

    private static bool MayContainRange(string remotePath, DateTime startLocal, DateTime endLocal)
    {
        var normalized = remotePath.Replace('\\', '/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i <= parts.Length - 3; i++)
        {
            if (int.TryParse(parts[i], out var year) &&
                int.TryParse(parts[i + 1], out var month) &&
                int.TryParse(parts[i + 2], out var day) &&
                year is >= 2000 and <= 2100 &&
                month is >= 1 and <= 12 &&
                day is >= 1 and <= 31)
            {
                var date = new DateTime(year, month, day);
                return date.Date >= startLocal.Date.AddDays(-1) && date.Date <= endLocal.Date.AddDays(1);
            }
        }
        return true;
    }

    private static DateTime? TryParseArchiveTimestamp(ISftpFile entry)
    {
        var text = entry.FullName.Replace('\\', '/');
        var match = Regex.Match(text, @"(?<year>\d{4})[-/](?<month>\d{1,2})[-/](?<day>\d{1,2})(?:[./_-](?<hour>\d{1,2})(?<min>\d{2})(?<sec>\d{2}))?");
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

        return entry.LastWriteTime == DateTime.MinValue ? null : entry.LastWriteTime;
    }

    private string BuildCachePath(SftpArchiveCandidate candidate)
    {
        var relative = candidate.RemotePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(_config.Storage.ImportCacheRoot, MakeSafeSegment(_config.SftpImport.Host), relative);
    }

    private static string NormalizeRemoteRoot(string root)
    {
        root = string.IsNullOrWhiteSpace(root) ? "/" : root.Replace('\\', '/').TrimEnd('/');
        return root.Length == 0 ? "/" : root;
    }

    public SftpClient CreateClient()
    {
        var cfg = _config.SftpImport;
        if (string.Equals(cfg.AuthMode, "privateKey", StringComparison.OrdinalIgnoreCase))
        {
            var key = string.IsNullOrWhiteSpace(cfg.PrivateKeyPassphrase)
                ? new PrivateKeyFile(cfg.PrivateKeyPath)
                : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.PrivateKeyPassphrase);
            return new SftpClient(new Renci.SshNet.ConnectionInfo(cfg.Host, cfg.Port, cfg.Username, new PrivateKeyAuthenticationMethod(cfg.Username, key)));
        }

        return new SftpClient(cfg.Host, cfg.Port, cfg.Username, cfg.Password);
    }

    private static string MakeSafeSegment(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '-');
        return string.IsNullOrWhiteSpace(value) ? "sftp" : value;
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

    private sealed record SftpArchiveCandidate(string RemotePath, string Name, DateTime TimestampLocal, long Size);
}
