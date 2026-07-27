namespace pizzad;

public static class AlertRulePolicy
{
    public const string Keyword = "keyword";
    public const string PoliceCode = "police_code";
    public const string KeywordOrPoliceCode = "keyword_or_police_code";

    public static string NormalizeMatchType(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_') switch
        {
            PoliceCode => PoliceCode,
            "both" or KeywordOrPoliceCode => KeywordOrPoliceCode,
            _ => Keyword
        };

    public static bool IsSupportedMatchType(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        return normalized is Keyword or PoliceCode or "both" or KeywordOrPoliceCode;
    }

    public static string TalkgroupKey(AlertTalkgroupRef talkgroup) =>
        $"{TalkgroupCatalogService.NormalizeSystemShortName(talkgroup.SystemShortName)}:{talkgroup.Id}";

    public static bool MatchesTalkgroup(AlertTalkgroupRef talkgroup, EngineCall call) =>
        talkgroup.Id == call.Talkgroup &&
        string.Equals(
            TalkgroupCatalogService.NormalizeSystemShortName(talkgroup.SystemShortName),
            TalkgroupCatalogService.NormalizeSystemShortName(call.SystemShortName),
            StringComparison.OrdinalIgnoreCase);
}
