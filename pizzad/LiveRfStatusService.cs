using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class LiveRfStatusService : BackgroundService
{
    private const int DecodeWindowSeconds = 120;
    private const int RetuneWindowSeconds = 300;
    private const int MinimumDecodeSamples = 3;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BaselineRefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecoveryHold = TimeSpan.FromSeconds(60);
    private static readonly Regex TimestampRegex = new(
        @"\[(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]",
        RegexOptions.Compiled);

    private readonly EngineConfig _config;
    private readonly EventStream _events;
    private readonly TrHealthTroubleshootService _troubleshoot;
    private readonly ILogger<LiveRfStatusService> _logger;
    private readonly object _snapshotGate = new();
    private readonly Dictionary<string, RecoveryState> _recovery = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, TrSystemHealthDto> _baselines = new Dictionary<string, TrSystemHealthDto>(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastBaselineRefreshUtc = DateTime.MinValue;
    private LiveRfStatusDto _snapshot = EmptySnapshot(DateTime.UtcNow, "Starting");

    public LiveRfStatusService(EngineConfig config, EventStream events, TrHealthTroubleshootService troubleshoot, ILogger<LiveRfStatusService> logger)
    {
        _config = config;
        _events = events;
        _troubleshoot = troubleshoot;
        _logger = logger;
    }

    public LiveRfStatusDto GetSnapshot()
    {
        lock (_snapshotGate)
            return _snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (OperatingSystem.IsWindows())
        {
            SetSnapshot(EmptySnapshot(DateTime.UtcNow, "Unavailable"));
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live RF status update failed");
                SetSnapshot(EmptySnapshot(DateTime.UtcNow, "Unavailable"));
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - _lastBaselineRefreshUtc >= BaselineRefreshInterval)
            await RefreshBaselinesAsync(now, ct);
        var log = await ReadJournalAsync(now.AddSeconds(-RetuneWindowSeconds), now, ct);
        var current = BuildSnapshot(now, log, _baselines);
        var stabilized = ApplyRecoveryHysteresis(current, now);
        SetSnapshot(stabilized);
        await _events.PublishAsync("rf_status_updated", new { stabilized.GeneratedAtUtc, stabilized.Tone, siteCount = stabilized.Sites.Count }, ct);
    }

    private async Task RefreshBaselinesAsync(DateTime nowUtc, CancellationToken ct)
    {
        var end = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var start = end - 2 * 3600;
        var rows = await _troubleshoot.BuildSystemAssessmentsAsync(start, end, "7d", ct);
        _baselines = rows.ToDictionary(row => row.SystemShortName, StringComparer.OrdinalIgnoreCase);
        _lastBaselineRefreshUtc = nowUtc;
    }

    private LiveRfStatusDto ApplyRecoveryHysteresis(LiveRfStatusDto snapshot, DateTime nowUtc)
    {
        var sites = new List<LiveRfSiteStatusDto>();
        foreach (var site in snapshot.Sites)
        {
            if (!_recovery.TryGetValue(site.SystemShortName, out var previous))
                previous = new RecoveryState(site.Tone, null);

            if (site.Tone == "ok" && previous.Tone is "warning" or "error")
            {
                var recoveryStarted = previous.RecoveryStartedUtc ?? nowUtc;
                if (nowUtc - recoveryStarted < RecoveryHold)
                {
                    sites.Add(site with
                    {
                        Tone = previous.Tone,
                        Status = "Recovering",
                        Detail = $"Current readings are healthy; holding the prior {previous.Tone} state until they remain stable for {RecoveryHold.TotalSeconds:F0} seconds. {site.Detail}"
                    });
                    _recovery[site.SystemShortName] = previous with { RecoveryStartedUtc = recoveryStarted };
                    continue;
                }
            }

            _recovery[site.SystemShortName] = new RecoveryState(site.Tone, null);
            sites.Add(site);
        }

        var activeNames = sites.Select(site => site.SystemShortName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var missing in _recovery.Keys.Where(name => !activeNames.Contains(name)).ToList())
            _recovery.Remove(missing);
        return snapshot with { Sites = sites, Tone = OverallTone(sites), Status = OverallStatus(sites) };
    }

    private void SetSnapshot(LiveRfStatusDto snapshot)
    {
        lock (_snapshotGate)
            _snapshot = snapshot;
    }

    public static LiveRfStatusDto BuildSnapshot(DateTime nowUtc, string log, IReadOnlyDictionary<string, TrSystemHealthDto>? baselines = null)
    {
        nowUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        baselines ??= new Dictionary<string, TrSystemHealthDto>(StringComparer.OrdinalIgnoreCase);
        var entries = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => new LogEntry(ParseTimestampUtc(line), TrHealthCollector.ExtractDisplaySystemScope(line), line))
            .Where(entry => entry.TimestampUtc.HasValue && !string.IsNullOrWhiteSpace(entry.Scope))
            .ToList();
        var decodeCutoff = nowUtc.AddSeconds(-DecodeWindowSeconds);
        var retuneCutoff = nowUtc.AddSeconds(-RetuneWindowSeconds);
        var recentEntries = entries
            .Where(entry => entry.TimestampUtc >= retuneCutoff && entry.TimestampUtc <= nowUtc.AddSeconds(5))
            .ToList();
        var scopes = recentEntries.Select(entry => entry.Scope)
            .Concat(baselines.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var sites = scopes
            .Select(scope => BuildSite(
                nowUtc,
                scope,
                recentEntries.Where(entry => string.Equals(entry.Scope, scope, StringComparison.OrdinalIgnoreCase)).ToList(),
                decodeCutoff,
                baselines.GetValueOrDefault(scope)))
            .OrderByDescending(site => ToneRank(site.Tone))
            .ThenBy(site => site.SystemShortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new LiveRfStatusDto(nowUtc, DecodeWindowSeconds, RetuneWindowSeconds, OverallTone(sites), OverallStatus(sites), sites);
    }

    private static LiveRfSiteStatusDto BuildSite(DateTime nowUtc, string scope, IReadOnlyList<LogEntry> entries, DateTime decodeCutoff, TrSystemHealthDto? baseline)
    {
        var decode = entries
            .Where(entry => entry.TimestampUtc >= decodeCutoff)
            .Select(entry => new { Entry = entry, Parsed = TrHealthCollector.TryParseLiveControlChannelDecodeRate(entry.Line, out var rate), Rate = rate })
            .Where(row => row.Parsed)
            .ToList();
        var samples = decode.Count;
        var averageRate = samples == 0 ? 0 : decode.Average(row => row.Rate);
        var zeroPercent = samples == 0 ? 0 : decode.Count(row => Math.Abs(row.Rate) < 0.0001) * 100.0 / samples;
        var lastDecodeUtc = decode.MaxBy(row => row.Entry.TimestampUtc)?.Entry.TimestampUtc;
        var freshnessSeconds = lastDecodeUtc.HasValue ? Math.Max(0, (nowUtc - lastDecodeUtc.Value).TotalSeconds) : -1;
        var retunes = entries.Count(entry => entry.Line.Contains("Retuning to Control Channel", StringComparison.OrdinalIgnoreCase));
        var retunesPerHour = retunes * 3600.0 / RetuneWindowSeconds;
        var decodeAssessment = TrHealthTroubleshootService.AssessDecodeRate(samples, averageRate, zeroPercent, baseline?.DecodeAssessment.BaselineValue, MinimumDecodeSamples);
        var zeroAssessment = TrHealthTroubleshootService.AssessZeroDecodeRate(samples, zeroPercent, baseline?.ZeroDecodeAssessment.BaselineValue, MinimumDecodeSamples);
        var retuneAssessment = TrHealthTroubleshootService.AssessRetunes(retunesPerHour, baseline?.RetunesAssessment.BaselineValue, averageRate < 1.0 && samples >= MinimumDecodeSamples);

        string tone;
        string status;
        if (!lastDecodeUtc.HasValue || freshnessSeconds > 45)
        {
            tone = "stale";
            status = "Stale";
        }
        else if (samples < MinimumDecodeSamples)
        {
            tone = "unknown";
            status = "Insufficient data";
        }
        else
        {
            tone = WorstTone(decodeAssessment.Tone, zeroAssessment.Tone, retuneAssessment.Tone);
            status = tone == "error" ? "Critical" : tone == "warning" ? "Degraded" : "Healthy";
        }

        var basis = new[] { decodeAssessment.Basis, zeroAssessment.Basis, retuneAssessment.Basis }.Contains("local", StringComparer.OrdinalIgnoreCase) ? "local" : "static";
        var detail = $"Decode: {decodeAssessment.Detail} Zero decode: {zeroAssessment.Detail} Retunes: {retuneAssessment.Detail}";
        return new LiveRfSiteStatusDto(scope, tone, status, averageRate, zeroPercent, samples, retunes, retunesPerHour, lastDecodeUtc, freshnessSeconds, basis, detail);
    }

    private async Task<string> ReadJournalAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("journalctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[]
        {
            "-u", _config.TrunkRecorder.LogServiceName,
            "--utc", "--since", startUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC",
            "--until", endUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC",
            "--no-pager", "--output=cat", "-n", "5000"
        }) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi);
        if (process == null)
            return string.Empty;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
                _logger.LogWarning("Live RF journalctl exited with code {ExitCode}: {Error}", process.ExitCode, error.Trim());
            return output;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            _logger.LogWarning("Live RF journalctl timed out");
            return string.Empty;
        }
    }

    private static DateTime? ParseTimestampUtc(string line)
    {
        var match = TimestampRegex.Match(line);
        if (!match.Success || !DateTime.TryParse(match.Groups["ts"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
            return null;
        return local.ToUniversalTime();
    }

    private static string WorstTone(params string[] tones) => tones.OrderByDescending(ToneRank).FirstOrDefault() ?? "unknown";
    private static int ToneRank(string tone) => tone switch { "error" => 4, "warning" => 3, "stale" => 2, "unknown" => 1, "ok" => 0, _ => 1 };
    private static string OverallTone(IReadOnlyList<LiveRfSiteStatusDto> sites) => sites.Count == 0 ? "unknown" : sites.OrderByDescending(site => ToneRank(site.Tone)).First().Tone;
    private static string OverallStatus(IReadOnlyList<LiveRfSiteStatusDto> sites)
    {
        if (sites.Count == 0) return "Waiting for RF samples";
        var problemCount = sites.Count(site => site.Tone is "error" or "warning");
        if (problemCount > 0) return $"{problemCount} degraded site{(problemCount == 1 ? string.Empty : "s")}";
        if (sites.Any(site => site.Tone == "stale")) return "RF samples stale";
        if (sites.Any(site => site.Tone == "unknown")) return "RF data incomplete";
        return $"{sites.Count} site{(sites.Count == 1 ? string.Empty : "s")} healthy";
    }

    private static LiveRfStatusDto EmptySnapshot(DateTime nowUtc, string status) => new(nowUtc, DecodeWindowSeconds, RetuneWindowSeconds, "unknown", status, []);
    private sealed record LogEntry(DateTime? TimestampUtc, string Scope, string Line);
    private sealed record RecoveryState(string Tone, DateTime? RecoveryStartedUtc);
}
