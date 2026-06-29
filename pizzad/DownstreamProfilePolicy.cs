namespace pizzad;

public static class DownstreamProfilePolicy
{
    public static bool Allows(EngineConfig config, EngineCall call) =>
        Allows(config, call.Category, call.Talkgroup);

    public static bool Allows(EngineConfig config, string category, long talkgroup)
    {
        var profile = config.Profiles.Items.FirstOrDefault(p => p.Id == config.Profiles.ActiveProfileId);
        if (profile == null)
            return true;

        if (profile.AllowedTalkgroups.Count > 0 && !profile.AllowedTalkgroups.Contains(talkgroup))
            return false;

        var setting = profile.Talkgroups.FirstOrDefault(t => t.Id == talkgroup);
        if (setting?.Enabled == false)
            return false;

        return (category ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "police" => profile.IncludePolice,
            "fire" => profile.IncludeFire,
            "ems" => profile.IncludeEMS,
            "traffic" => profile.IncludeTraffic,
            _ => profile.IncludeOther
        };
    }
}
