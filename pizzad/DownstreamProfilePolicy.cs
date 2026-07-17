namespace pizzad;

public static class DownstreamProfilePolicy
{
    public static bool Allows(EngineConfig config, TalkgroupCatalogService catalog, EngineCall call) =>
        Allows(config, catalog, call.Category, call.SystemShortName, call.Talkgroup);

    public static bool Allows(EngineConfig config, TalkgroupCatalogService catalog, string category, string? systemShortName, long talkgroup)
    {
        if (!catalog.IsGloballyEnabled(systemShortName, talkgroup))
            return false;

        return AllowsProfile(config, category, systemShortName, talkgroup);
    }

    public static bool Allows(EngineConfig config, EngineCall call) =>
        AllowsProfile(config, call.Category, call.SystemShortName, call.Talkgroup);

    public static bool Allows(EngineConfig config, string category, long talkgroup)
        => AllowsProfile(config, category, string.Empty, talkgroup);

    public static bool Allows(EngineConfig config, string category, string? systemShortName, long talkgroup)
        => AllowsProfile(config, category, systemShortName, talkgroup);

    private static bool AllowsProfile(EngineConfig config, string category, string? systemShortName, long talkgroup)
    {
        var profile = config.Profiles.Items.FirstOrDefault(p => p.Id == config.Profiles.ActiveProfileId);
        if (profile == null)
            return true;

        var setting = FindSetting(profile, systemShortName, talkgroup);
        if (setting?.Enabled == false)
            return false;

        var effectiveCategory = string.IsNullOrWhiteSpace(setting?.Category) ? category : setting!.Category;
        return (effectiveCategory ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "police" => profile.IncludePolice,
            "fire" => profile.IncludeFire,
            "ems" => profile.IncludeEMS,
            "traffic" => profile.IncludeTraffic,
            "utilities" => profile.IncludeUtilities,
            _ => profile.IncludeOther
        };
    }

    private static ProfileTalkgroupSetting? FindSetting(ProcessingProfile profile, string? systemShortName, long talkgroup)
    {
        var rows = profile.Talkgroups.Where(t => t.Id == talkgroup).ToList();
        if (rows.Count == 0)
            return null;
        var exactKey = TalkgroupCatalogService.CatalogKey(systemShortName, talkgroup);
        return rows.LastOrDefault(row => string.Equals(TalkgroupCatalogService.SettingKey(row), exactKey, StringComparison.OrdinalIgnoreCase))
            ?? rows.LastOrDefault(row => string.Equals(TalkgroupCatalogService.SettingKey(row), talkgroup.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            ?? rows.LastOrDefault(row => string.IsNullOrWhiteSpace(row.SystemShortName))
            ?? rows[^1];
    }
}
