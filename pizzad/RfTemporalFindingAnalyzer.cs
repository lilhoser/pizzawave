namespace pizzad;

public static class RfTemporalFindingAnalyzer
{
    private static readonly TimeSpan EpisodeGap = TimeSpan.FromMinutes(10);

    public static IReadOnlyList<RfTemporalFindingDto> Analyze(
        IReadOnlyList<TrHealthSampleDto> samples,
        IReadOnlyList<MaintenanceIntervalDto> maintenance,
        DateTime nowUtc)
    {
        var excluded = maintenance
            .Where(row => row.ExcludeFromBaselines)
            .Select(row => (Start: row.StartUtc.ToUniversalTime(), End: (row.EndUtc ?? nowUtc).ToUniversalTime()))
            .ToList();
        // The collector can persist successively wider views of the same journal
        // interval. Keep the first (narrowest) observation for each interval start
        // so one real degradation window cannot become many overlapping episodes.
        var normalizedSamples = samples
            .GroupBy(row => (row.Scope.ToUpperInvariant(), row.WindowStartUtc.ToUniversalTime()))
            .Select(group => group
                .OrderBy(row => row.WindowEndUtc.ToUniversalTime() - row.WindowStartUtc.ToUniversalTime())
                .First())
            .ToList();
        var episodes = normalizedSamples
            .Where(row => !string.Equals(row.Scope, "global", StringComparison.OrdinalIgnoreCase))
            .Where(row => !excluded.Any(window => row.WindowStartUtc.ToUniversalTime() < window.End && row.WindowEndUtc.ToUniversalTime() > window.Start))
            .GroupBy(row => row.Scope, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => BuildEpisodes(group.Key, group.OrderBy(row => row.WindowStartUtc).ToList()))
            .ToList();

        var observableStart = normalizedSamples.Count == 0 ? nowUtc : normalizedSamples.Min(row => row.WindowStartUtc.ToUniversalTime());
        var observableDays = Math.Max(1.0, (nowUtc - observableStart).TotalDays);
        return episodes
            .GroupBy(row => (row.OwnerKey, row.Signature))
            .Select(group => BuildFinding(group.Key.OwnerKey, group.Key.Signature, group.OrderBy(row => row.StartUtc).ToList(), observableDays, nowUtc))
            .Where(row => row.IsActive || row.Episodes.Count >= 3)
            .OrderByDescending(row => SeverityRank(row.Severity))
            .ThenBy(row => row.OwnerKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<RfTemporalEpisodeDto> BuildEpisodes(string owner, IReadOnlyList<TrHealthSampleDto> rows)
    {
        var result = new List<RfTemporalEpisodeDto>();
        EpisodeBuilder? current = null;
        foreach (var row in rows)
        {
            var classified = Classify(row);
            if (classified is null)
            {
                // Do not split a degradation merely because one five-minute
                // bucket briefly returns to normal. A later abnormal bucket
                // beyond EpisodeGap closes the prior episode.
                continue;
            }

            if (current == null || row.WindowStartUtc.ToUniversalTime() - current.EndUtc > EpisodeGap)
            {
                if (current != null) result.Add(current.Build());
                current = new EpisodeBuilder(owner, classified.Value.Signature, row, classified.Value.Conditions, classified.Value.Severity);
            }
            else
            {
                current.Add(row, classified.Value.Signature, classified.Value.Conditions, classified.Value.Severity);
            }
        }
        if (current != null) result.Add(current.Build());
        return result;
    }

    private static (string Signature, IReadOnlyList<string> Conditions, string Severity)? Classify(TrHealthSampleDto row)
    {
        var seconds = Math.Max(1.0, (row.WindowEndUtc - row.WindowStartUtc).TotalSeconds);
        var retunesPerHour = row.Retunes * 3600.0 / seconds;
        var noAudioPercent = row.CallsConcluded <= 0 ? 0 : row.NoTxRecorded * 100.0 / row.CallsConcluded;
        var hasDecode = row.CcSummaryDecodeLines > 0;
        var decodeLoss = hasDecode && (row.CcSummaryAvgDecodeRate < 1.0 || row.CcSummaryDecodeZeroPct >= 90.0);
        var decodeDegraded = hasDecode && (row.CcSummaryAvgDecodeRate < 35.0 || row.CcSummaryDecodeZeroPct >= 10.0);
        var retuneHigh = retunesPerHour > 48.0;
        var captureDegraded = row.CallsConcluded >= 4 && noAudioPercent >= 25.0;
        if (!decodeLoss && !decodeDegraded && !retuneHigh && !captureDegraded) return null;

        var conditions = new List<string>();
        if (decodeLoss) conditions.Add("decode_unavailable");
        else if (decodeDegraded) conditions.Add("decode_degraded");
        if (retuneHigh) conditions.Add("retunes_elevated");
        if (captureDegraded) conditions.Add("no_audio_elevated");
        var signature = decodeLoss
            ? "decode-loss"
            : decodeDegraded && retuneHigh
                ? "control-instability"
                : captureDegraded && !decodeDegraded
                    ? "capture-degradation"
                    : "rf-degradation";
        var severity = decodeLoss || (decodeDegraded && captureDegraded) ? "high" : "medium";
        return (signature, conditions, severity);
    }

    private static RfTemporalFindingDto BuildFinding(string owner, string signature, IReadOnlyList<RfTemporalEpisodeDto> episodes, double observableDays, DateTime nowUtc)
    {
        var active = episodes.LastOrDefault()?.EndUtc >= nowUtc.AddMinutes(-15);
        var severity = episodes.Any(row => row.Severity == "high") ? "high" : "medium";
        var occurrencesPerWeek = episodes.Count / observableDays * 7.0;
        var confidence = episodes.Count >= 8 ? "high" : episodes.Count >= 5 ? "medium" : "provisional";
        var hourCounts = episodes.GroupBy(row => row.StartUtc.ToLocalTime().Hour).ToDictionary(group => group.Key, group => group.Count());
        var strongestHours = Enumerable.Range(0, 24)
            .Select(hour => new { Hour = hour, Count = Enumerable.Range(0, 4).Sum(offset => hourCounts.GetValueOrDefault((hour + offset) % 24)) })
            .OrderByDescending(row => row.Count)
            .FirstOrDefault();
        var schedule = strongestHours != null && episodes.Count >= 3 && strongestHours.Count >= Math.Ceiling(episodes.Count * 0.6)
            ? $"Most episodes started between {FormatHour(strongestHours.Hour)} and {FormatHour((strongestHours.Hour + 4) % 24)} local time."
            : "No reliable time-of-day association is established.";
        var conditions = episodes.SelectMany(row => row.Conditions).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        return new RfTemporalFindingDto(owner, signature, severity, confidence, active, occurrencesPerWeek, schedule, conditions, episodes);
    }

    private static string FormatHour(int hour) => new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt");
    private static int SeverityRank(string value) => value == "high" ? 2 : 1;

    private sealed class EpisodeBuilder
    {
        private readonly string _owner;
        private readonly HashSet<string> _conditions;
        private int _windows;
        private int _ccSamples;
        private double _decodeTotal;
        private int _decodeZero;
        private int _retunes;
        private int _calls;
        private int _noAudio;

        public EpisodeBuilder(string owner, string signature, TrHealthSampleDto row, IEnumerable<string> conditions, string severity)
        {
            _owner = owner;
            Signature = signature;
            StartUtc = row.WindowStartUtc.ToUniversalTime();
            EndUtc = row.WindowEndUtc.ToUniversalTime();
            Severity = severity;
            _conditions = new HashSet<string>(conditions, StringComparer.OrdinalIgnoreCase);
            AddMetrics(row);
        }

        public string Signature { get; private set; }
        public DateTime StartUtc { get; }
        public DateTime EndUtc { get; private set; }
        public string Severity { get; private set; }

        public void Add(TrHealthSampleDto row, string signature, IEnumerable<string> conditions, string severity)
        {
            EndUtc = row.WindowEndUtc.ToUniversalTime();
            if (severity == "high") Severity = "high";
            foreach (var condition in conditions) _conditions.Add(condition);
            if (SignatureRank(signature) > SignatureRank(Signature)) Signature = signature;
            AddMetrics(row);
        }

        private void AddMetrics(TrHealthSampleDto row)
        {
            _windows++;
            _ccSamples += row.CcSummaryDecodeLines;
            _decodeTotal += row.CcSummaryDecodeRateTotal;
            _decodeZero += row.CcSummaryDecodeZero;
            _retunes += row.Retunes;
            _calls += row.CallsConcluded;
            _noAudio += row.NoTxRecorded;
        }

        public RfTemporalEpisodeDto Build()
        {
            var evidence = new RfTemporalEvidenceDto(
                _windows,
                _ccSamples,
                _ccSamples == 0 ? 0 : _decodeTotal / _ccSamples,
                _ccSamples == 0 ? 0 : _decodeZero * 100.0 / _ccSamples,
                _retunes,
                _calls,
                _calls == 0 ? 0 : _noAudio * 100.0 / _calls);
            var key = $"{_owner}:{Signature}:{StartUtc:O}:{EndUtc:O}";
            return new RfTemporalEpisodeDto(key, _owner, Signature, StartUtc, EndUtc, Severity, _conditions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(), evidence);
        }

        private static int SignatureRank(string value) => value switch
        {
            "decode-loss" => 4,
            "control-instability" => 3,
            "capture-degradation" => 2,
            _ => 1
        };
    }
}

public sealed record RfTemporalFindingDto(
    string OwnerKey,
    string Signature,
    string Severity,
    string Confidence,
    bool IsActive,
    double OccurrencesPerWeek,
    string ScheduleSummary,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<RfTemporalEpisodeDto> Episodes);

public sealed record RfTemporalEpisodeDto(
    string EpisodeKey,
    string OwnerKey,
    string Signature,
    DateTime StartUtc,
    DateTime EndUtc,
    string Severity,
    IReadOnlyList<string> Conditions,
    RfTemporalEvidenceDto Evidence);

public sealed record RfTemporalEvidenceDto(
    int Windows,
    int DecodeSamples,
    double AverageDecodeRate,
    double ZeroDecodePercent,
    int Retunes,
    int CallsConcluded,
    double NoAudioPercent);

public sealed record MaintenanceIntervalDto(
    long Id,
    DateTime StartUtc,
    DateTime? EndUtc,
    string Source,
    string Reason,
    bool ExcludeFromBaselines,
    string DetailsJson,
    DateTime CreatedAtUtc);
