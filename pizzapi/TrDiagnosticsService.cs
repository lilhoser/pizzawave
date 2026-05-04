using pizzalib;
using Renci.SshNet;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace pizzapi;

public sealed class TrDiagnosticsResult
{
    public string RecentLogText { get; set; } = string.Empty;
    public string SummaryCsvText { get; set; } = string.Empty;
    public string SummarySource { get; set; } = string.Empty;
    public string DiagnosticsText { get; set; } = string.Empty;
}

public sealed class TrDiagnosticsService
{
    private const string DefaultLogDir = "/var/log/trunk-recorder";
    private const string DefaultCollectorSummaryCsv = "/var/lib/pizzapi/tr-health/summary_5m.csv";
    private const long MaxRecentLogBytes = 256 * 1024;
    private const long MaxCachedLogBytes = 10 * 1024 * 1024;
    private static readonly Regex TimestampRegex = new(
        @"(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled);
    private static readonly Regex DecodeRateRegex = new(
        @"(?:decode|decoded)[^0-9-]*(?<rate>-?\d+(?:\.\d+)?)\s*(?:/sec|per sec|hz)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SourceRegex = new(
        @"(?:source|rtl|device|serial)[\s:=]+(?<source>[A-Za-z0-9_.:-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] LogExtensions = { ".log", ".txt", ".out", "" };
    private const string ParseManifestFileName = "parse_manifest.json";

    private readonly Settings _settings;
    private readonly string? _password;
    private readonly string? _privateKeyPassphrase;

    public TrDiagnosticsService(Settings settings, string? password, string? privateKeyPassphrase)
    {
        _settings = settings;
        _password = password;
        _privateKeyPassphrase = privateKeyPassphrase;
    }

    public Task<TrDiagnosticsResult> LoadAsync(CancellationToken cancellationToken)
    {
        return LoadAsync(null, forceRawLogs: false, cancellationToken);
    }

    public Task<TrDiagnosticsResult> LoadAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        return LoadAsync(progress, forceRawLogs: false, cancellationToken);
    }

    public Task<TrDiagnosticsResult> LoadAsync(IProgress<string>? progress, bool forceRawLogs, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            var diagnostics = new List<string>();
            void Report(string message)
            {
                var line = $"{DateTime.Now:HH:mm:ss} {message}";
                diagnostics.Add(line);
                progress?.Report(line);
            }

            var lookback = TimeSpan.FromHours(24);
            var sinceUtc = DateTime.UtcNow.Subtract(lookback);
            var baselineSinceUtc = DateTime.UtcNow.AddDays(-30);
            var effectiveSinceUtc = sinceUtc < baselineSinceUtc ? sinceUtc : baselineSinceUtc;
            Report($"TR diagnostics mode: {(IsSshMode() ? "SSH" : "Local")}");
            Report($"Lookback: 24h since {sinceUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            Report($"Baseline history target: 30d since {baselineSinceUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            var cacheRoot = ResolveCacheRoot();
            Report($"Local cache: {cacheRoot}");
            string summaryCsv;
            string recentLogText;

            if (!forceRawLogs)
            {
                try
                {
                    var collectorCsv = IsSshMode()
                        ? FetchRemoteCollectorSummaryCsv(diagnostics, progress, cancellationToken)
                        : ReadLocalCollectorSummaryCsv(diagnostics, progress);
                    if (!string.IsNullOrWhiteSpace(collectorCsv))
                    {
                        summaryCsv = collectorCsv;
                        recentLogText = "Collector CSV mode active. Raw log output not fetched.";
                        Report("Using collector summary CSV output.");
                    }
                    else
                    {
                        Report("WARNING: Collector summary CSV not found. Falling back to raw logs.");
                        (summaryCsv, recentLogText) = BuildSummaryFromRawLogs(cacheRoot, effectiveSinceUtc, diagnostics, progress, cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    Report($"WARNING: Collector summary read failed ({ex.Message}). Falling back to raw logs.");
                    try
                    {
                        (summaryCsv, recentLogText) = BuildSummaryFromRawLogs(cacheRoot, effectiveSinceUtc, diagnostics, progress, cancellationToken);
                    }
                    catch (ObjectDisposedException odx) when (odx.ObjectName?.Contains("SafeWaitHandle", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Report($"WARNING: SSH handle disposal race detected ({odx.Message}). Retrying raw-log parse once.");
                        (summaryCsv, recentLogText) = BuildSummaryFromRawLogs(cacheRoot, effectiveSinceUtc, diagnostics, progress, cancellationToken);
                    }
                }
            }
            else
            {
                Report("Raw-log mode forced from Troubleshoot UI. Collector CSV is skipped.");
                (summaryCsv, recentLogText) = BuildSummaryFromRawLogs(cacheRoot, effectiveSinceUtc, diagnostics, progress, cancellationToken);
            }

            var summaryPath = Path.Combine(cacheRoot, "summary_5m.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(summaryPath) ?? cacheRoot);
            File.WriteAllText(summaryPath, summaryCsv);
            Report($"Wrote summary: {summaryPath}");

            return new TrDiagnosticsResult
            {
                RecentLogText = recentLogText,
                SummaryCsvText = summaryCsv,
                SummarySource = summaryPath,
                DiagnosticsText = string.Join(Environment.NewLine, diagnostics)
            };
        }, cancellationToken);
    }

    public bool IsSshMode()
    {
        return string.Equals(_settings.TrDiagnosticsMode, "ssh", StringComparison.OrdinalIgnoreCase);
    }

    private List<TrLogFile> FindLocalLogs(DateTime sinceUtc, List<string> diagnostics, IProgress<string>? progress)
    {
        var logDir = NormalizeLocalPath(_settings.TrDiagnosticsRemoteLogDir, DefaultLogDir);
        Report(diagnostics, progress, $"Local log directory: {logDir}");
        if (!Directory.Exists(logDir))
        {
            Report(diagnostics, progress, "Local log directory does not exist.");
            return new List<TrLogFile>();
        }

        var files = Directory.GetFiles(logDir)
            .Select(path => new FileInfo(path))
            .Where(info => IsLogFileName(info.Name))
            .Where(info => info.LastWriteTimeUtc >= sinceUtc.AddHours(-2))
            .OrderBy(info => info.LastWriteTimeUtc)
            .Select(info => new TrLogFile(info.FullName, info.Name, info.LastWriteTimeUtc))
            .ToList();
        Report(diagnostics, progress, $"Found {files.Count} local log file(s).");
        return files;
    }

    private List<TrLogFile> FetchRemoteLogs(
        string cacheRoot,
        DateTime sinceUtc,
        List<string> diagnostics,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return FetchRemoteLogsViaSsh(cacheRoot, sinceUtc, diagnostics, progress, cancellationToken);
    }

    private (string SummaryCsv, string RecentLogText) BuildSummaryFromRawLogs(
        string cacheRoot,
        DateTime effectiveSinceUtc,
        List<string> diagnostics,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var logFiles = IsSshMode()
            ? FetchRemoteLogs(cacheRoot, effectiveSinceUtc, diagnostics, progress, cancellationToken)
            : FindLocalLogs(effectiveSinceUtc, diagnostics, progress);
        cancellationToken.ThrowIfCancellationRequested();
        Report(diagnostics, progress, $"Loading parse cache for {logFiles.Count} cached log file(s).");
        var summaryRows = ParseWithCache(cacheRoot, logFiles, effectiveSinceUtc, DateTime.UtcNow, diagnostics, progress);
        Report(diagnostics, progress, $"Parsed {summaryRows.Count} health bucket row(s).");
        return (TrLogHealthParser.ToCsv(summaryRows), BuildRecentLogText(logFiles));
    }

    private string? ReadLocalCollectorSummaryCsv(List<string> diagnostics, IProgress<string>? progress)
    {
        Report(diagnostics, progress, $"Reading local collector CSV: {DefaultCollectorSummaryCsv}");
        if (!File.Exists(DefaultCollectorSummaryCsv))
            return null;
        var csv = File.ReadAllText(DefaultCollectorSummaryCsv);
        return string.IsNullOrWhiteSpace(csv) ? null : csv;
    }

    private string? FetchRemoteCollectorSummaryCsv(List<string> diagnostics, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        using var client = CreateSshClient();
        var host = Require(_settings.TrDiagnosticsHost, "TR diagnostics host");
        var user = Require(_settings.TrDiagnosticsUsername, "TR diagnostics username");
        var port = _settings.TrDiagnosticsPort <= 0 ? 22 : _settings.TrDiagnosticsPort;
        Report(diagnostics, progress, $"Connecting SSH for collector CSV: {user}@{host}:{port}");
        client.Connect();
        cancellationToken.ThrowIfCancellationRequested();
        var cmd = client.RunCommand($"cat {ShellQuote(DefaultCollectorSummaryCsv)} 2>/dev/null");
        if (cmd.ExitStatus != 0)
            return null;
        return string.IsNullOrWhiteSpace(cmd.Result) ? null : cmd.Result;
    }

    private List<TrLogFile> FetchRemoteLogsViaSsh(
        string cacheRoot,
        DateTime sinceUtc,
        List<string> diagnostics,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var client = CreateSshClient();
        var host = Require(_settings.TrDiagnosticsHost, "TR diagnostics host");
        var user = Require(_settings.TrDiagnosticsUsername, "TR diagnostics username");
        var port = _settings.TrDiagnosticsPort <= 0 ? 22 : _settings.TrDiagnosticsPort;
        var authMode = (_settings.TrDiagnosticsAuthMode ?? "password").Trim().ToLowerInvariant();
        Report(diagnostics, progress, $"Connecting SSH: {user}@{host}:{port} auth={authMode}");
        try
        {
            client.Connect();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"SSH connect failed for {user}@{host}:{port} using {authMode} auth. " +
                "This indicates the SSH server is rejecting/closing the authentication attempt before diagnostics can run. " +
                $"Original error: {ex.Message}",
                ex);
        }

        Report(diagnostics, progress, "SSH connected.");
        cancellationToken.ThrowIfCancellationRequested();

        var remoteDir = NormalizeRemotePath(_settings.TrDiagnosticsRemoteLogDir);
        var hostRoot = Path.Combine(cacheRoot, MakeSafePathSegment(_settings.TrDiagnosticsHost), MakeSafePathSegment(remoteDir));
        Directory.CreateDirectory(hostRoot);
        var minutes = Math.Max(1, (int)Math.Ceiling((DateTime.UtcNow - sinceUtc.AddHours(-2)).TotalMinutes));
        var findCommand = $"find {ShellQuote(remoteDir)} -maxdepth 1 -type f -mmin -{minutes} -printf '%T@|%s|%p\\n' 2>/dev/null";
        Report(diagnostics, progress, $"Running remote find in {remoteDir}.");
        var result = client.RunCommand(findCommand);
        if (result.ExitStatus != 0)
            throw new InvalidOperationException($"SSH connected, but remote log listing failed for '{remoteDir}'. {FirstNonEmpty(result.Error, result.Result, "No remote error output.")}");

        var entries = ParseSshFindOutput(result.Result)
            .Where(e => IsLogFileName(Path.GetFileName(e.RemotePath)))
            .OrderBy(e => e.LastWriteUtc)
            .ToList();
        Report(diagnostics, progress, $"Found {entries.Count} remote log file(s) through SSH.");

        var files = new List<TrLogFile>();
        var downloaded = 0;
        var reused = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(entry.RemotePath);
            var localPath = Path.Combine(hostRoot, MakeSafePathSegment(fileName));
            var expectedLength = Math.Min(entry.Size, MaxCachedLogBytes);
            var shouldDownload = !File.Exists(localPath)
                || new FileInfo(localPath).Length != expectedLength
                || Math.Abs((File.GetLastWriteTimeUtc(localPath) - entry.LastWriteUtc).TotalSeconds) > 2;
            if (shouldDownload)
            {
                Report(diagnostics, progress, $"Downloading via SSH {fileName} ({entry.Size:N0} bytes).");
                var tailCommand = $"tail -c {MaxCachedLogBytes.ToString(CultureInfo.InvariantCulture)} -- {ShellQuote(entry.RemotePath)} 2>/dev/null";
                var tail = client.RunCommand(tailCommand);
                if (tail.ExitStatus != 0)
                    throw new InvalidOperationException($"Failed to read remote log '{entry.RemotePath}' over SSH. {FirstNonEmpty(tail.Error, tail.Result, "No remote error output.")}");
                File.WriteAllText(localPath, tail.Result);
                File.SetLastWriteTimeUtc(localPath, entry.LastWriteUtc);
                downloaded++;
            }
            else
            {
                reused++;
            }

            files.Add(new TrLogFile(localPath, entry.RemotePath, entry.LastWriteUtc));
        }

        Report(diagnostics, progress, $"SSH disconnected. Downloaded {downloaded}, reused {reused}.");
        return files;
    }

    private string BuildRecentLogText(List<TrLogFile> logFiles)
    {
        if (logFiles.Count == 0)
            return IsSshMode()
                ? "No remote trunk-recorder log files found for the selected lookback window."
                : "No local trunk-recorder log files found for the selected lookback window.";

        var newest = logFiles.OrderByDescending(f => f.LastWriteUtc).First();
        var text = ReadTail(newest.LocalPath, MaxRecentLogBytes);
        return string.IsNullOrWhiteSpace(text)
            ? $"Latest trunk-recorder log was empty: {newest.DisplayName}"
            : $"source: {newest.DisplayName}{Environment.NewLine}{text}";
    }

    private SshClient CreateSshClient()
    {
        var connection = CreateConnectionInfo();
        return new SshClient(connection);
    }

    private ConnectionInfo CreateConnectionInfo()
    {
        var host = Require(_settings.TrDiagnosticsHost, "TR diagnostics host");
        var user = Require(_settings.TrDiagnosticsUsername, "TR diagnostics username");
        var port = _settings.TrDiagnosticsPort <= 0 ? 22 : _settings.TrDiagnosticsPort;
        var authMode = (_settings.TrDiagnosticsAuthMode ?? "password").Trim().ToLowerInvariant();

        if (authMode == "privatekey")
        {
            var keyPath = Require(_settings.TrDiagnosticsPrivateKeyPath, "TR diagnostics private key path");
            PrivateKeyFile keyFile = string.IsNullOrWhiteSpace(_privateKeyPassphrase)
                ? new PrivateKeyFile(keyPath)
                : new PrivateKeyFile(keyPath, _privateKeyPassphrase);
            return new ConnectionInfo(host, port, user, new PrivateKeyAuthenticationMethod(user, keyFile));
        }

        if (string.IsNullOrWhiteSpace(_password))
            throw new InvalidOperationException("TR diagnostics password is missing from secure storage. Enter the password in Settings > Trunk recorder, then save settings before refreshing diagnostics.");

        return new ConnectionInfo(host, port, user, new PasswordAuthenticationMethod(user, _password ?? string.Empty));
    }

    private string ResolveCacheRoot()
    {
        var configured = _settings.TrDiagnosticsLocalCachePath;
        var cacheRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Settings.DefaultWorkingDirectory, "tr-diagnostics")
            : Environment.ExpandEnvironmentVariables(configured);
        Directory.CreateDirectory(cacheRoot);
        return cacheRoot;
    }

    private static string ReadTail(string path, long maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maxBytes && stream.CanSeek)
                stream.Seek(stream.Length - maxBytes, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static bool IsLogFileName(string name)
    {
        var ext = Path.GetExtension(name);
        return LogExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
            || name.Contains("trunk", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRemotePath(string? path)
    {
        var normalized = (string.IsNullOrWhiteSpace(path) ? DefaultLogDir : path.Trim()).Replace('\\', '/').TrimEnd('/');
        return string.IsNullOrWhiteSpace(normalized) ? DefaultLogDir : normalized;
    }

    private static string NormalizeLocalPath(string? path, string fallback)
    {
        return string.IsNullOrWhiteSpace(path) ? fallback : Environment.ExpandEnvironmentVariables(path.Trim());
    }

    private static string Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{name} is required.");
        return value.Trim();
    }

    private static string MakeSafePathSegment(string? value)
    {
        var segment = string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');
        return segment.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

    private static void Report(List<string> diagnostics, IProgress<string>? progress, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        diagnostics.Add(line);
        progress?.Report(line);
    }

    private static string ShellQuote(string value)
    {
        return "'" + value.Replace("'", "'\"'\"'") + "'";
    }

    private static List<SshLogEntry> ParseSshFindOutput(string output)
    {
        var entries = new List<SshLogEntry>();
        foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('|', 3);
            if (parts.Length != 3)
                continue;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var unixSeconds))
                continue;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                continue;

            var lastWrite = DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000)).UtcDateTime;
            entries.Add(new SshLogEntry(parts[2], Math.Max(0, size), lastWrite));
        }

        return entries;
    }

    private static List<TrHealthSummaryRow> ParseWithCache(
        string cacheRoot,
        List<TrLogFile> logFiles,
        DateTime sinceUtc,
        DateTime nowUtc,
        List<string> diagnostics,
        IProgress<string>? progress)
    {
        var manifestPath = Path.Combine(cacheRoot, ParseManifestFileName);
        var manifest = LoadManifest(manifestPath);
        var existing = manifest.Files.ToDictionary(f => f.LocalPath, StringComparer.OrdinalIgnoreCase);
        var cachedRows = new List<TrHealthSummaryRow>();
        var nextFiles = new List<CachedParsedFile>();
        var reused = 0;
        var changed = 0;

        foreach (var file in logFiles)
        {
            var info = new FileInfo(file.LocalPath);
            if (!info.Exists)
                continue;
            var ticks = info.LastWriteTimeUtc.Ticks;
            var length = info.Length;
            if (existing.TryGetValue(file.LocalPath, out var prior)
                && prior.LastWriteUtcTicks == ticks
                && prior.Length == length
                && prior.Rows != null)
            {
                reused++;
                nextFiles.Add(prior);
                cachedRows.AddRange(prior.Rows);
                continue;
            }

            changed++;
            Report(diagnostics, progress, $"Parsing changed log: {Path.GetFileName(file.LocalPath)}");
            var rows = TrLogHealthParser.Parse(new[] { file }, sinceUtc, nowUtc);
            cachedRows.AddRange(rows);
            nextFiles.Add(new CachedParsedFile
            {
                LocalPath = file.LocalPath,
                LastWriteUtcTicks = ticks,
                Length = length,
                Rows = rows
            });
        }

        Report(diagnostics, progress, $"Parse cache reused {reused}, reparsed {changed}.");
        manifest.Files = nextFiles;
        SaveManifest(manifestPath, manifest);
        return MergeRows(cachedRows);
    }

    private static ParseManifest LoadManifest(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new ParseManifest();
            var text = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ParseManifest>(text) ?? new ParseManifest();
        }
        catch
        {
            return new ParseManifest();
        }
    }

    private static void SaveManifest(string path, ParseManifest manifest)
    {
        try
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(manifest));
        }
        catch
        {
            // best effort cache
        }
    }

    private static List<TrHealthSummaryRow> MergeRows(IEnumerable<TrHealthSummaryRow> rows)
    {
        var map = new Dictionary<(DateTime StartUtc, string Scope), TrHealthSummaryRow>();
        foreach (var row in rows)
        {
            var key = (row.StartUtc, row.Scope ?? "global");
            if (!map.TryGetValue(key, out var agg))
            {
                map[key] = new TrHealthSummaryRow
                {
                    StartUtc = row.StartUtc,
                    EndUtc = row.EndUtc,
                    Scope = row.Scope,
                    DecodeLines = row.DecodeLines,
                    DecodeZero = row.DecodeZero,
                    DecodeRateTotal = row.DecodeRateTotal,
                    Retunes = row.Retunes,
                    CallsConcluded = row.CallsConcluded,
                    UpdateNotGrant = row.UpdateNotGrant,
                    NoTxRecorded = row.NoTxRecorded,
                    SampleStops = row.SampleStops,
                    UnableSource = row.UnableSource,
                    TuningErrSamples = row.TuningErrSamples,
                    TuningErrTotalAbsHz = row.TuningErrTotalAbsHz,
                    TuningErrMaxAbsHz = row.TuningErrMaxAbsHz
                };
                continue;
            }

            agg.DecodeLines += row.DecodeLines;
            agg.DecodeZero += row.DecodeZero;
            agg.DecodeRateTotal += row.DecodeRateTotal;
            agg.Retunes += row.Retunes;
            agg.CallsConcluded += row.CallsConcluded;
            agg.UpdateNotGrant += row.UpdateNotGrant;
            agg.NoTxRecorded += row.NoTxRecorded;
            agg.SampleStops += row.SampleStops;
            agg.UnableSource += row.UnableSource;
            agg.TuningErrSamples += row.TuningErrSamples;
            agg.TuningErrTotalAbsHz += row.TuningErrTotalAbsHz;
            agg.TuningErrMaxAbsHz = Math.Max(agg.TuningErrMaxAbsHz, row.TuningErrMaxAbsHz);
        }

        return map.Values
            .OrderBy(r => r.StartUtc)
            .ThenBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record TrLogFile(string LocalPath, string DisplayName, DateTime LastWriteUtc);

public sealed record SshLogEntry(string RemotePath, long Size, DateTime LastWriteUtc);

public sealed class ParseManifest
{
    public List<CachedParsedFile> Files { get; set; } = new();
}

public sealed class CachedParsedFile
{
    public string LocalPath { get; set; } = string.Empty;
    public long LastWriteUtcTicks { get; set; }
    public long Length { get; set; }
    public List<TrHealthSummaryRow>? Rows { get; set; }
}

public sealed class TrHealthSummaryRow
{
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public string Scope { get; set; } = "global";
    public int DecodeLines { get; set; }
    public int DecodeZero { get; set; }
    public double DecodeRateTotal { get; set; }
    public int Retunes { get; set; }
    public int CallsConcluded { get; set; }
    public int UpdateNotGrant { get; set; }
    public int NoTxRecorded { get; set; }
    public int SampleStops { get; set; }
    public int UnableSource { get; set; }
    public int TuningErrSamples { get; set; }
    public double TuningErrTotalAbsHz { get; set; }
    public double TuningErrMaxAbsHz { get; set; }
}

public static class TrLogHealthParser
{
    private static readonly Regex TimestampRegex = new(
        @"(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)",
        RegexOptions.Compiled);
    private static readonly Regex SystemScopeRegex = new(
        @"\]\s+\((?:info|error|warning|debug)\)\s+\[(?<scope>[^\]]+)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DecodeRateRegex = new(
        @"(?:decode|decoded)[^0-9-]*(?<rate>-?\d+(?:\.\d+)?)\s*(?:/sec|per sec|hz)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TuningErrorRegex = new(
        @"(?:tuning|tune)[^0-9-]*(?:error|err)[^0-9-]*(?<hz>-?\d+(?:\.\d+)?)\s*hz",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<TrHealthSummaryRow> Parse(IEnumerable<TrLogFile> logFiles, DateTime sinceUtc, DateTime nowUtc)
    {
        var rows = new Dictionary<(DateTime Start, string Scope), TrHealthSummaryRow>();
        foreach (var file in logFiles.OrderBy(f => f.LastWriteUtc))
        {
            string? lastSystemScope = null;
            DateTime? lastSystemScopeTs = null;
            foreach (var line in ReadLinesShared(file.LocalPath))
            {
                var timestamp = ParseTimestampUtc(line) ?? file.LastWriteUtc;
                if (timestamp < sinceUtc || timestamp > nowUtc.AddMinutes(5))
                    continue;

                ApplyLine(rows, timestamp, "global", line);
                var scope = TryParseSystemScope(line);
                if (!string.IsNullOrWhiteSpace(scope))
                {
                    lastSystemScope = scope;
                    lastSystemScopeTs = timestamp;
                    ApplyLine(rows, timestamp, scope, line);
                }
                else if (IsSampleStopLine(line)
                    && !string.IsNullOrWhiteSpace(lastSystemScope)
                    && lastSystemScopeTs.HasValue
                    && (timestamp - lastSystemScopeTs.Value).TotalMinutes <= 10)
                {
                    // TR sample-stop lines are sometimes emitted without system scope.
                    // Attribute to the most recent system-scoped line in the same log file.
                    ApplyLine(rows, timestamp, lastSystemScope, line);
                }
            }
        }

        return rows.Values
            .OrderBy(r => r.StartUtc)
            .ThenBy(r => r.Scope, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ToCsv(IEnumerable<TrHealthSummaryRow> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ts_start,ts_end,scope,decode_lines,decode_zero,decode_nonzero,avg_decode_rate,grant_updates,retunes,calls_started,calls_concluded,update_not_grant,no_tx_recorded,sample_stops,unable_source,tuning_err_samples,tuning_err_avg_abs_hz,tuning_err_max_abs_hz");
        foreach (var row in rows)
        {
            var decodeNonZero = Math.Max(0, row.DecodeLines - row.DecodeZero);
            var avgDecode = row.DecodeLines > 0 ? row.DecodeRateTotal / row.DecodeLines : 0;
            var tuningAvg = row.TuningErrSamples > 0 ? row.TuningErrTotalAbsHz / row.TuningErrSamples : 0;
            sb.AppendLine(string.Join(",", new[]
            {
                FormatTs(row.StartUtc),
                FormatTs(row.EndUtc),
                EscapeCsv(row.Scope),
                row.DecodeLines.ToString(CultureInfo.InvariantCulture),
                row.DecodeZero.ToString(CultureInfo.InvariantCulture),
                decodeNonZero.ToString(CultureInfo.InvariantCulture),
                avgDecode.ToString("F3", CultureInfo.InvariantCulture),
                "0",
                row.Retunes.ToString(CultureInfo.InvariantCulture),
                "0",
                row.CallsConcluded.ToString(CultureInfo.InvariantCulture),
                row.UpdateNotGrant.ToString(CultureInfo.InvariantCulture),
                row.NoTxRecorded.ToString(CultureInfo.InvariantCulture),
                row.SampleStops.ToString(CultureInfo.InvariantCulture),
                row.UnableSource.ToString(CultureInfo.InvariantCulture),
                row.TuningErrSamples.ToString(CultureInfo.InvariantCulture),
                tuningAvg.ToString("F3", CultureInfo.InvariantCulture),
                row.TuningErrMaxAbsHz.ToString("F3", CultureInfo.InvariantCulture)
            }));
        }

        return sb.ToString();
    }

    private static void ApplyLine(
        Dictionary<(DateTime Start, string Scope), TrHealthSummaryRow> rows,
        DateTime timestampUtc,
        string scope,
        string line)
    {
        var row = GetRow(rows, FloorToFiveMinutes(timestampUtc), scope);
        if (TryParseDecodeRate(line, out var decodeRate))
        {
            row.DecodeLines++;
            row.DecodeRateTotal += decodeRate;
            if (Math.Abs(decodeRate) < 0.0001)
                row.DecodeZero++;
        }

        if (line.Contains("Retuning to Control Channel", StringComparison.OrdinalIgnoreCase))
            row.Retunes++;
        if (line.Contains("Concluding Recorded Call", StringComparison.OrdinalIgnoreCase)
            || line.Contains("call concluded", StringComparison.OrdinalIgnoreCase)
            || line.Contains("calls concluded", StringComparison.OrdinalIgnoreCase))
            row.CallsConcluded++;
        if (line.Contains("update not grant", StringComparison.OrdinalIgnoreCase)
            || line.Contains("This was an UPDATE", StringComparison.OrdinalIgnoreCase))
            row.UpdateNotGrant++;
        if (line.Contains("No Transmissions were recorded", StringComparison.OrdinalIgnoreCase)
            || line.Contains("no transmission", StringComparison.OrdinalIgnoreCase)
            || line.Contains("no tx", StringComparison.OrdinalIgnoreCase)
            || line.Contains("not recording transmission", StringComparison.OrdinalIgnoreCase)
            || line.Contains("only 0 recorders are available", StringComparison.OrdinalIgnoreCase))
            row.NoTxRecorded++;
        if (line.Contains("has stopped receiving samples", StringComparison.OrdinalIgnoreCase)
            || line.Contains("sample stop", StringComparison.OrdinalIgnoreCase)
            || line.Contains("stopped samples", StringComparison.OrdinalIgnoreCase))
            row.SampleStops++;
        if (line.Contains("no source covering", StringComparison.OrdinalIgnoreCase)
            || (line.Contains("unable", StringComparison.OrdinalIgnoreCase)
                && line.Contains("source", StringComparison.OrdinalIgnoreCase)))
            row.UnableSource++;

        var tuningMatch = TuningErrorRegex.Match(line);
        if (tuningMatch.Success && double.TryParse(tuningMatch.Groups["hz"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hz))
        {
            var absHz = Math.Abs(hz);
            row.TuningErrSamples++;
            row.TuningErrTotalAbsHz += absHz;
            row.TuningErrMaxAbsHz = Math.Max(row.TuningErrMaxAbsHz, absHz);
        }
    }

    private static TrHealthSummaryRow GetRow(Dictionary<(DateTime Start, string Scope), TrHealthSummaryRow> rows, DateTime startUtc, string scope)
    {
        var key = (startUtc, scope);
        if (rows.TryGetValue(key, out var row))
            return row;

        row = new TrHealthSummaryRow
        {
            StartUtc = startUtc,
            EndUtc = startUtc.AddMinutes(5),
            Scope = scope
        };
        rows[key] = row;
        return row;
    }

    private static bool TryParseDecodeRate(string line, out double rate)
    {
        rate = 0;
        if (!line.Contains("decode", StringComparison.OrdinalIgnoreCase))
            return false;

        var match = DecodeRateRegex.Match(line);
        if (!match.Success)
            return false;

        return double.TryParse(match.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rate);
    }

    private static bool IsSampleStopLine(string line)
    {
        return line.Contains("has stopped receiving samples", StringComparison.OrdinalIgnoreCase)
            || line.Contains("sample stop", StringComparison.OrdinalIgnoreCase)
            || line.Contains("stopped samples", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryParseSystemScope(string line)
    {
        var match = SystemScopeRegex.Match(line);
        if (!match.Success)
            return null;

        var scope = match.Groups["scope"].Value.Trim();
        if (scope.All(char.IsDigit))
            return null;
        if (scope.StartsWith("source", StringComparison.OrdinalIgnoreCase))
            return null;
        return scope.Any(char.IsLetter) ? scope : null;
    }

    private static DateTime? ParseTimestampUtc(string line)
    {
        var match = TimestampRegex.Match(line);
        if (!match.Success)
            return null;

        var raw = match.Groups["ts"].Value.Replace('T', ' ');
        if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            return null;
        return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
    }

    private static DateTime FloorToFiveMinutes(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        var minute = utc.Minute - utc.Minute % 5;
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, minute, 0, DateTimeKind.Utc);
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        while (reader.ReadLine() is { } line)
            yield return line;
    }

    private static string FormatTs(DateTime value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
