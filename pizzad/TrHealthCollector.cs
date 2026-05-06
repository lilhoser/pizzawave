using System.Diagnostics;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class TrHealthCollector : BackgroundService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly EventStream _events;
    private readonly ILogger<TrHealthCollector> _logger;

    public TrHealthCollector(EngineConfig config, EngineDatabase database, EventStream events, ILogger<TrHealthCollector> logger)
    {
        _config = config;
        _database = database;
        _events = events;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TR health collection failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _config.TrunkRecorder.HealthWindowMinutes)), stoppingToken);
        }
    }

    private async Task CollectOnceAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return;

        var end = DateTime.UtcNow;
        var start = end.AddMinutes(-Math.Max(1, _config.TrunkRecorder.HealthWindowMinutes));
        var log = await ReadJournalAsync(start, end, ct);
        if (string.IsNullOrWhiteSpace(log))
            return;

        var global = BuildSample("global", start, end, log);
        await _database.InsertHealthSampleAsync(global, ct);

        foreach (var scope in ExtractSystemScopes(log))
        {
            var scopedLines = string.Join('\n', log.Split('\n').Where(line => line.Contains($"[{scope}]", StringComparison.OrdinalIgnoreCase)));
            if (scopedLines.Length == 0)
                continue;
            await _database.InsertHealthSampleAsync(BuildSample(scope, start, end, scopedLines), ct);
        }

        await _events.PublishAsync("health_updated", new { windowStartUtc = start, windowEndUtc = end }, ct);
    }

    private async Task<string> ReadJournalAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        var args = $"-u {_config.TrunkRecorder.LogServiceName} --since \"{startUtc:yyyy-MM-dd HH:mm:ss}\" --until \"{endUtc:yyyy-MM-dd HH:mm:ss}\" --no-pager";
        var psi = new ProcessStartInfo("journalctl", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = Process.Start(psi);
        if (process == null)
            return string.Empty;

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return output;
    }

    private static TrHealthSampleDto BuildSample(string scope, DateTime startUtc, DateTime endUtc, string log)
    {
        var decodeLines = Count(log, "Control Channel Message Decode Rate");
        var decodeZero = Count(log, "Control Channel Message Decode Rate: 0/sec");
        return new TrHealthSampleDto
        {
            WindowStartUtc = startUtc,
            WindowEndUtc = endUtc,
            Scope = scope,
            DecodeLines = decodeLines,
            DecodeZero = decodeZero,
            DecodeZeroPct = decodeLines == 0 ? 0 : decodeZero * 100.0 / decodeLines,
            Retunes = Count(log, "Retuning to Control Channel"),
            CallsStarted = Count(log, "Starting P25 Recorder"),
            CallsConcluded = Count(log, "Concluding Recorded Call"),
            SampleStops = Count(log, "stopped receiving samples"),
            UnableSource = Count(log, "Unable to find a source")
        };
    }

    private static IReadOnlyList<string> ExtractSystemScopes(string log) =>
        Regex.Matches(log, @"\([a-z]+\)\s+\[([^]]+)\]")
            .Select(m => m.Groups[1].Value)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int Count(string haystack, string needle) =>
        Regex.Matches(haystack, Regex.Escape(needle), RegexOptions.IgnoreCase).Count;
}
