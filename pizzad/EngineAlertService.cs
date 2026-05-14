using System.Net.Mail;
using System.Text.RegularExpressions;

namespace pizzad;

public sealed class EngineAlertService
{
    private readonly EngineConfig _config;
    private readonly PoliceCodeService _policeCodes;
    private readonly ILogger<EngineAlertService> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, DateTime> _lastTriggered = new();
    private readonly Dictionary<Guid, int> _realtimeCounts = new();

    public EngineAlertService(EngineConfig config, PoliceCodeService policeCodes, ILogger<EngineAlertService> logger)
    {
        _config = config;
        _policeCodes = policeCodes;
        _logger = logger;
    }

    public EngineAlertMatchResult Evaluate(EngineCall call, string transcription, bool imported)
    {
        if (!_config.Setup.Completed)
            return new EngineAlertMatchResult(false, null, string.Empty, string.Empty, string.Empty, false, string.Empty);

        foreach (var rule in _config.Alerts.Rules.Where(r => r.Enabled))
        {
            if (rule.Talkgroups.Count > 0 && !rule.Talkgroups.Contains(call.Talkgroup))
                continue;

            if (!TryMatch(rule, transcription, out var type, out var detail))
                continue;

            var emailSent = false;
            var error = string.Empty;
            if (!imported && _config.Alerts.EmailEnabled && !string.IsNullOrWhiteSpace(rule.Email))
            {
                try
                {
                    if (ShouldSend(rule))
                    {
                        SendEmail(rule, call, transcription);
                        emailSent = true;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _logger.LogWarning(ex, "Failed to send alert email for {Rule}", rule.Name);
                }
            }

            return new EngineAlertMatchResult(true, rule.Id, rule.Name, type, detail, emailSent, error);
        }

        return new EngineAlertMatchResult(false, null, string.Empty, string.Empty, string.Empty, false, string.Empty);
    }

    private bool TryMatch(EngineAlertRule rule, string transcription, out string type, out string detail)
    {
        type = string.Empty;
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(transcription))
            return false;

        if (string.Equals(rule.MatchType, "police_code", StringComparison.OrdinalIgnoreCase))
        {
            var configured = Split(rule.PoliceCodes).Select(NormalizeConfiguredCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (configured.Count == 0)
                return false;
            foreach (var annotation in _policeCodes.Detect(transcription))
            {
                if (configured.Contains(annotation.NormalizedCode))
                {
                    type = "police_code";
                    detail = annotation.NormalizedCode;
                    return true;
                }
            }
            return false;
        }

        foreach (var keyword in Split(rule.Keywords))
        {
            if (transcription.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                type = "keyword";
                detail = keyword;
                return true;
            }
        }

        return false;
    }

    private bool ShouldSend(EngineAlertRule rule)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_lastTriggered.TryGetValue(rule.Id, out var last))
            {
                _lastTriggered[rule.Id] = now;
                _realtimeCounts[rule.Id] = 1;
                return true;
            }

            var frequency = (rule.Frequency ?? "realtime").Trim().ToLowerInvariant();
            var elapsed = now - last;
            if (frequency == "daily" && elapsed.TotalDays < 1) return false;
            if (frequency == "hourly" && elapsed.TotalHours < 1) return false;
            if (frequency == "realtime")
            {
                if (elapsed.TotalSeconds <= 5 && _realtimeCounts.GetValueOrDefault(rule.Id) > 1)
                    return false;
                if (elapsed.TotalSeconds > 5)
                    _realtimeCounts[rule.Id] = 0;
                _realtimeCounts[rule.Id] = _realtimeCounts.GetValueOrDefault(rule.Id) + 1;
            }

            _lastTriggered[rule.Id] = now;
            return true;
        }
    }

    private void SendEmail(EngineAlertRule rule, EngineCall call, string transcription)
    {
        var tg = string.IsNullOrWhiteSpace(call.TalkgroupName) ? $"TG {call.Talkgroup}" : call.TalkgroupName;
        foreach (var recipient in Split(rule.Email))
        {
            _ = new MailAddress(recipient);
            SmtpEmailSender.SendHtml(
                _config.Alerts,
                "pizzawave notifications",
                recipient,
                $"pizzawave alert: {rule.Name}",
                $"The following transcription from <b>{System.Net.WebUtility.HtmlEncode(tg)}</b> triggered <b>{System.Net.WebUtility.HtmlEncode(rule.Name)}</b>:<p><i>{System.Net.WebUtility.HtmlEncode(transcription)}</i></p>");
        }
    }

    private static IEnumerable<string> Split(string value) =>
        (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v));

    private static string NormalizeConfiguredCode(string code)
    {
        code = code.Trim().ToLowerInvariant();
        var digits = Regex.Match(code, @"^10[-\s]?(\d{1,3})$");
        if (digits.Success)
            return $"10-{digits.Groups[1].Value.TrimStart('0')}";
        return Regex.Replace(code, @"\s+", "-");
    }
}
