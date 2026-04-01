using Newtonsoft.Json;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace pizzapi;

public enum MainSection
{
    Highlights,
    Alerts,
    Police,
    Fire,
    EMS,
    Traffic,
    Other,
    Settings,
    Troubleshoot
}

public enum GlobalTimeRangePreset
{
    Last24Hours,
    Last2Days,
    LastWeek,
    Custom
}

public sealed class GlobalTimeRange
{
    public GlobalTimeRangePreset Preset { get; set; } = GlobalTimeRangePreset.Last24Hours;
    public DateTime? StartLocal { get; set; }
    public DateTime? EndLocal { get; set; }

    public (DateTime Start, DateTime End) Resolve(DateTime nowLocal)
    {
        return Preset switch
        {
            GlobalTimeRangePreset.Last24Hours => (nowLocal.AddHours(-24), nowLocal),
            GlobalTimeRangePreset.Last2Days => (nowLocal.AddDays(-2), nowLocal),
            GlobalTimeRangePreset.LastWeek => (nowLocal.AddDays(-7), nowLocal),
            GlobalTimeRangePreset.Custom when StartLocal.HasValue && EndLocal.HasValue
                => (StartLocal.Value, EndLocal.Value),
            _ => (nowLocal.AddHours(-24), nowLocal)
        };
    }
}

public sealed class ProcessingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default";
    public bool IncludePolice { get; set; } = true;
    public bool IncludeFire { get; set; } = true;
    public bool IncludeEMS { get; set; } = true;
    public bool IncludeTraffic { get; set; } = true;
    public bool IncludeOther { get; set; } = true;
    public List<long> AllowedTalkgroups { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool HasTalkgroupScope => AllowedTalkgroups.Count > 0;

    public bool IncludesSection(MainSection section)
    {
        return section switch
        {
            MainSection.Police => IncludePolice,
            MainSection.Fire => IncludeFire,
            MainSection.EMS => IncludeEMS,
            MainSection.Traffic => IncludeTraffic,
            MainSection.Other => IncludeOther,
            _ => true
        };
    }
}

public sealed class AlertMatchRecord : INotifyPropertyChanged
{
    private bool _isAudioPlaying;

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime MatchedAtUtc { get; set; } = DateTime.UtcNow;
    public string CallHash { get; set; } = string.Empty;
    public long CallId { get; set; }
    public Guid? AlertRuleId { get; set; }
    public string AlertRuleName { get; set; } = string.Empty;
    public string AlertType { get; set; } = "keyword";
    public string TypeDetail { get; set; } = string.Empty;
    public string Transcription { get; set; } = string.Empty;
    public int DurationSec { get; set; }
    public long TimestampUnix { get; set; }
    public string AudioPath { get; set; } = string.Empty;
    public bool IsRead { get; set; }
    public DateTime? ReadAtUtc { get; set; }
    [JsonIgnore]
    public string HighlightPrefix
    {
        get
        {
            var (prefix, _, _) = SplitHighlight();
            return prefix;
        }
    }
    [JsonIgnore]
    public string HighlightMatch
    {
        get
        {
            var (_, match, _) = SplitHighlight();
            return match;
        }
    }
    [JsonIgnore]
    public string HighlightSuffix
    {
        get
        {
            var (_, _, suffix) = SplitHighlight();
            return suffix;
        }
    }
    [JsonIgnore]
    public bool HasHighlightMatch => !string.IsNullOrWhiteSpace(HighlightMatch);
    [JsonIgnore]
    public bool IsAudioPlaying
    {
        get => _isAudioPlaying;
        set
        {
            if (_isAudioPlaying == value)
                return;
            _isAudioPlaying = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAudioPlaying)));
        }
    }
    [JsonIgnore]
    public string DisplayTimestamp => DateTimeOffset.FromUnixTimeSeconds(TimestampUnix).ToLocalTime().ToString("g");

    public event PropertyChangedEventHandler? PropertyChanged;

    private (string Prefix, string Match, string Suffix) SplitHighlight()
    {
        var transcript = Transcription ?? string.Empty;
        var needle = TypeDetail?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(transcript) || string.IsNullOrWhiteSpace(needle))
            return (transcript, string.Empty, string.Empty);

        var index = transcript.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var prefixDirect = transcript[..index];
            var matchDirect = transcript.Substring(index, needle.Length);
            var suffixDirect = transcript[(index + needle.Length)..];
            return (prefixDirect, matchDirect, suffixDirect);
        }

        var normalizedNeedle = NormalizeForLooseMatch(needle, out _);
        if (normalizedNeedle.Length == 0)
            return (transcript, string.Empty, string.Empty);

        var normalizedTranscript = NormalizeForLooseMatch(transcript, out var transcriptMap);
        var looseIndex = normalizedTranscript.IndexOf(normalizedNeedle, StringComparison.OrdinalIgnoreCase);
        if (looseIndex < 0)
            return (transcript, string.Empty, string.Empty);

        var startOriginal = transcriptMap[looseIndex];
        var endOriginal = transcriptMap[looseIndex + normalizedNeedle.Length - 1] + 1;
        if (startOriginal < 0 || endOriginal <= startOriginal || endOriginal > transcript.Length)
            return (transcript, string.Empty, string.Empty);

        var prefix = transcript[..startOriginal];
        var match = transcript[startOriginal..endOriginal];
        var suffix = transcript[endOriginal..];
        return (prefix, match, suffix);
    }

    private static string NormalizeForLooseMatch(string input, out List<int> map)
    {
        map = new List<int>(input.Length);
        var sb = new StringBuilder(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];
            if (!char.IsLetterOrDigit(ch))
                continue;

            sb.Append(ch);
            map.Add(i);
        }

        return sb.ToString();
    }
}
