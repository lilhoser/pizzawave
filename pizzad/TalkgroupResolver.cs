namespace pizzad;

public sealed record ResolvedTalkgroup(string Label, string Category);

public sealed class TalkgroupResolver
{
    private readonly TalkgroupCatalogService _catalog;

    public TalkgroupResolver(TalkgroupCatalogService catalog)
    {
        _catalog = catalog;
    }

    public EngineCall Enrich(EngineCall call)
    {
        var resolved = Resolve(call.SystemShortName, call.Talkgroup);
        return call with
        {
            TalkgroupName = string.IsNullOrWhiteSpace(call.TalkgroupName) || call.TalkgroupName.StartsWith("TG ", StringComparison.OrdinalIgnoreCase)
                ? resolved.Label
                : call.TalkgroupName,
            Category = resolved.Category
        };
    }

    public ResolvedTalkgroup Resolve(long talkgroup)
    {
        var resolved = _catalog.Resolve(talkgroup);
        return new ResolvedTalkgroup(resolved.Label, resolved.Category);
    }

    public ResolvedTalkgroup Resolve(string? systemShortName, long talkgroup)
    {
        var resolved = _catalog.Resolve(systemShortName, talkgroup);
        return new ResolvedTalkgroup(resolved.Label, resolved.Category);
    }

    public IReadOnlyList<TalkgroupOptionDto> ListOptions() => _catalog.ListEnabledOptions();
}
