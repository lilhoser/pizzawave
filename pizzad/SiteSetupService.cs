using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace pizzad;

public sealed class SiteSetupService
{
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly IngestControlService _ingest;
    private readonly LiveTrActivityMonitor _liveTrActivity;
    private readonly ILogger<SiteSetupService> _logger;

    public SiteSetupService(
        EngineConfig config,
        EngineDatabase database,
        IngestControlService ingest,
        LiveTrActivityMonitor liveTrActivity,
        ILogger<SiteSetupService> logger)
    {
        _config = config;
        _database = database;
        _ingest = ingest;
        _liveTrActivity = liveTrActivity;
        _logger = logger;
    }

    public async Task<SiteSetupDto> GetAsync(CancellationToken ct)
    {
        var applied = await BuildAppliedConfigAsync(ct);
        var desired = EffectiveDesired(applied);
        var activity = await _database.ListSiteSetupActivityAsync(25, ct);
        var pending = BuildPendingChanges(desired, applied);
        var monitoring = MonitoringState();
        return new SiteSetupDto(
            desired,
            applied,
            new SiteSetupStatusDto(
                monitoring.State,
                monitoring.Message,
                pending.Count > 0,
                desired.DesiredVersion,
                applied.ConfigHash,
                desired.LastAppliedAtUtc),
            pending,
            activity);
    }

    public async Task<SiteSetupDto> UpdateDesiredAsync(SiteSetupUpdateRequest request, CancellationToken ct)
    {
        var applied = await BuildAppliedConfigAsync(ct);
        var before = EffectiveDesired(applied);
        var next = Normalize(request.Desired);
        next.DesiredVersion = Math.Max(before.DesiredVersion + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        next.UpdatedAtUtc = DateTime.UtcNow;
        next.LastAppliedAtUtc = _config.SiteSetup.LastAppliedAtUtc;
        next.LastAppliedConfigHash = _config.SiteSetup.LastAppliedConfigHash ?? string.Empty;

        var changes = DescribeChanges(before, next);
        _config.SiteSetup = next;
        _config.Save();

        if (changes.Count > 0)
        {
            await AddActivityAsync(
                "change",
                "desired_setup_updated",
                $"Desired setup updated: {string.Join(", ", changes.Take(4))}{(changes.Count > 4 ? $" and {changes.Count - 4} more" : "")}.",
                new { changes, before = ActivitySnapshot(before), after = ActivitySnapshot(next) },
                request.Source,
                ct);
            _logger.LogInformation("Site Setup desired state updated: {Changes}", string.Join("; ", changes));
        }

        return await GetAsync(ct);
    }

    public async Task<SiteSetupActivityDto> AddActivityAsync(SiteSetupActivityRequest request, CancellationToken ct)
    {
        var details = request.Details.HasValue
            ? request.Details.Value.GetRawText()
            : "{}";
        return await AddActivityAsync(request.Category, request.Action, request.Summary, details, request.Source, ct);
    }

    public async Task<SiteSetupDto> MarkAppliedAsync(SiteSetupMarkAppliedRequest request, CancellationToken ct)
    {
        var applied = await BuildAppliedConfigAsync(ct);
        var next = Normalize(_config.SiteSetup);
        next.LastAppliedAtUtc = DateTime.UtcNow;
        next.LastAppliedConfigHash = applied.ConfigHash;
        _config.SiteSetup = next;
        _config.Save();

        var details = request.Details.HasValue
            ? request.Details.Value.GetRawText()
            : "{}";
        var summary = string.IsNullOrWhiteSpace(request.Summary)
            ? "Applied Site Setup TR config and resumed monitoring."
            : request.Summary.Trim();
        await AddActivityAsync("apply", "setup_config_applied", summary, details, request.Source, ct);
        return await GetAsync(ct);
    }

    private async Task<SiteSetupActivityDto> AddActivityAsync(string category, string action, string summary, object details, string source, CancellationToken ct) =>
        await AddActivityAsync(category, action, summary, JsonSerializer.Serialize(details, EngineConfig.JsonOptions()), source, ct);

    private async Task<SiteSetupActivityDto> AddActivityAsync(string category, string action, string summary, string detailsJson, string source, CancellationToken ct)
    {
        var applied = await BuildAppliedConfigAsync(ct);
        var monitoring = MonitoringState();
        var entry = new SiteSetupActivityDto(
            0,
            DateTime.UtcNow,
            Clean(category, "change"),
            Clean(action, "updated"),
            summary?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(detailsJson) ? "{}" : detailsJson,
            _config.SiteSetup.DesiredVersion,
            applied.ConfigHash,
            monitoring.State,
            Clean(source, "ui"));
        await _database.AddSiteSetupActivityAsync(entry, ct);
        return entry;
    }

    private SiteSetupConfig EffectiveDesired(SiteSetupAppliedConfigDto applied)
    {
        var configured = Normalize(_config.SiteSetup);
        if (configured.SystemShortNames.Count > 0 || configured.Systems.Count > 0 || configured.Sources.Count > 0)
            return configured;

        var seededSystems = applied.SystemShortNames
            .Select(name => new RfSurveySystemDto(
                name,
                name,
                applied.ControlChannelsHz,
                []))
            .ToList();
        configured.SystemShortNames = applied.SystemShortNames.ToList();
        configured.SourcePlanSystemShortNames = applied.SystemShortNames.ToList();
        configured.Systems = seededSystems;
        configured.Sources = applied.Sources
            .Select(source => new RfSurveySourceDto(source.Index, source.Device, source.Serial, SdrTypeFromDevice(source.Device), source.CenterHz, source.SampleRate, source.ErrorHz, source.Gain))
            .ToList();
        return Normalize(configured);
    }

    private async Task<SiteSetupAppliedConfigDto> BuildAppliedConfigAsync(CancellationToken ct)
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new SiteSetupAppliedConfigDto(path, false, "", null, [], [], []);

        var json = await File.ReadAllTextAsync(path, ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var systems = new List<string>();
        var controlChannels = new List<long>();
        if (root.TryGetProperty("systems", out var systemsElement) && systemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var system in systemsElement.EnumerateArray())
            {
                var shortName = system.TryGetProperty("shortName", out var shortNameElement)
                    ? shortNameElement.GetString() ?? string.Empty
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(shortName))
                    systems.Add(shortName.Trim());
                controlChannels.AddRange(ReadFrequencyArray(system, "control_channels"));
            }
        }

        var sources = new List<SiteSetupAppliedSourceDto>();
        if (root.TryGetProperty("sources", out var sourcesElement) && sourcesElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var source in sourcesElement.EnumerateArray())
            {
                var device = ReadString(source, "device");
                sources.Add(new SiteSetupAppliedSourceDto(
                    index,
                    device,
                    ExtractSerial(device),
                    ReadLong(source, "center"),
                    (int)ReadLong(source, "rate"),
                    (int)ReadLong(source, "error"),
                    ReadStringOrNumber(source, "gain")));
                index++;
            }
        }

        var info = new FileInfo(path);
        return new SiteSetupAppliedConfigDto(
            path,
            true,
            Sha256(json),
            info.LastWriteTimeUtc,
            systems.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            controlChannels.Distinct().Order().ToList(),
            sources);
    }

    private IReadOnlyList<SiteSetupPendingChangeDto> BuildPendingChanges(SiteSetupConfig desired, SiteSetupAppliedConfigDto applied)
    {
        var rows = new List<SiteSetupPendingChangeDto>();
        var desiredSystems = desired.SystemShortNames.Count > 0
            ? desired.SystemShortNames
            : desired.Systems.Select(system => system.ShortName).ToList();
        if (!SetEquals(desiredSystems, applied.SystemShortNames))
            rows.Add(new SiteSetupPendingChangeDto("Systems/Sites", $"Desired systems ({desiredSystems.Count}) differ from applied systems ({applied.SystemShortNames.Count})."));

        var desiredControls = desired.Systems.SelectMany(system => system.ControlChannelsHz).Distinct().Order().ToList();
        if (desiredControls.Count > 0 && !desiredControls.SequenceEqual(applied.ControlChannelsHz))
            rows.Add(new SiteSetupPendingChangeDto("Control Channels", $"Desired CCs ({desiredControls.Count}) differ from applied CCs ({applied.ControlChannelsHz.Count})."));

        if (desired.Sources.Count > 0 && desired.Sources.Count != applied.Sources.Count)
            rows.Add(new SiteSetupPendingChangeDto("TR Sources", $"Desired sources ({desired.Sources.Count}) differ from applied sources ({applied.Sources.Count})."));

        if (!string.IsNullOrWhiteSpace(desired.LastAppliedConfigHash) &&
            !string.Equals(desired.LastAppliedConfigHash, applied.ConfigHash, StringComparison.OrdinalIgnoreCase))
            rows.Add(new SiteSetupPendingChangeDto("Applied Config", "Live TR config has changed since Site Setup last applied it."));

        return rows;
    }

    private (string State, string Message) MonitoringState()
    {
        if (_ingest.Paused)
            return ("paused", string.IsNullOrWhiteSpace(_ingest.Reason) ? "Ingest is paused." : _ingest.Reason);
        var control = TrServiceControlStateReader.ReadLatest();
        if (control != null && string.Equals(control.State, "stopped", StringComparison.OrdinalIgnoreCase))
            return ("stopped", control.Reason);
        var live = _liveTrActivity.GetStatus(DateTime.UtcNow, TrServiceFaultReader.ReadLatest(), control);
        if (live.Stale)
            return ("stale", live.Message);
        return ("active", live.Message);
    }

    private static SiteSetupConfig Normalize(SiteSetupConfig value) => new()
    {
        DesiredVersion = value.DesiredVersion <= 0 ? 1 : value.DesiredVersion,
        SiteLabel = value.SiteLabel?.Trim() ?? string.Empty,
        LocationNotes = value.LocationNotes?.Trim() ?? string.Empty,
        RadioReferenceSid = value.RadioReferenceSid?.Trim() ?? string.Empty,
        SystemShortNames = NormalizeStrings(value.SystemShortNames),
        SourcePlanSystemShortNames = NormalizeStrings(value.SourcePlanSystemShortNames),
        SourcePlanMode = string.IsNullOrWhiteSpace(value.SourcePlanMode) ? "full" : value.SourcePlanMode.Trim(),
        Systems = value.Systems?.ToList() ?? [],
        SelectedSourceIndexes = value.SelectedSourceIndexes?.Distinct().Order().ToList() ?? [],
        Sources = value.Sources?.ToList() ?? [],
        RfPath = value.RfPath ?? new RfSurveyPathProfileDto(),
        UpdatedAtUtc = value.UpdatedAtUtc,
        LastAppliedAtUtc = value.LastAppliedAtUtc,
        LastAppliedConfigHash = value.LastAppliedConfigHash?.Trim() ?? string.Empty
    };

    private static List<string> DescribeChanges(SiteSetupConfig before, SiteSetupConfig after)
    {
        var changes = new List<string>();
        AddIfChanged(changes, "site label", before.SiteLabel, after.SiteLabel);
        AddIfChanged(changes, "location notes", before.LocationNotes, after.LocationNotes);
        AddIfChanged(changes, "RadioReference SID", before.RadioReferenceSid, after.RadioReferenceSid);
        AddIfChanged(changes, "systems/sites", string.Join(",", before.SystemShortNames), string.Join(",", after.SystemShortNames));
        AddIfChanged(changes, "source-plan systems", string.Join(",", before.SourcePlanSystemShortNames), string.Join(",", after.SourcePlanSystemShortNames));
        AddIfChanged(changes, "selected sources", string.Join(",", before.SelectedSourceIndexes), string.Join(",", after.SelectedSourceIndexes));
        AddIfChanged(changes, "RF path", RfPathSummary(before.RfPath), RfPathSummary(after.RfPath));
        AddIfChanged(changes, "desired sources", SourceSummary(before.Sources), SourceSummary(after.Sources));
        return changes;
    }

    private static object ActivitySnapshot(SiteSetupConfig value) => new
    {
        value.DesiredVersion,
        value.SiteLabel,
        value.LocationNotes,
        value.RadioReferenceSid,
        value.SystemShortNames,
        value.SourcePlanSystemShortNames,
        value.SelectedSourceIndexes,
        value.SourcePlanMode,
        rfPath = RfPathSummary(value.RfPath),
        sources = SourceSummary(value.Sources)
    };

    private static List<string> NormalizeStrings(IEnumerable<string>? values) =>
        (values ?? [])
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void AddIfChanged(List<string> changes, string label, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
            changes.Add(label);
    }

    private static bool SetEquals(IEnumerable<string> left, IEnumerable<string> right) =>
        new HashSet<string>(NormalizeStrings(left), StringComparer.OrdinalIgnoreCase).SetEquals(NormalizeStrings(right));

    private static string RfPathSummary(RfSurveyPathProfileDto? path)
    {
        if (path == null)
            return "";
        return string.Join("|", new[] { path.Antenna, path.AntennaType, path.AntennaMount, path.AimedAtSite, path.PositionNotes, path.Coax, path.Lna, path.Filters, path.SdrNotes }.Select(value => value?.Trim() ?? ""));
    }

    private static string SourceSummary(IEnumerable<RfSurveySourceDto>? sources) =>
        string.Join("|", (sources ?? []).Select(source => $"{source.Index}:{source.Device}:{source.CenterHz}:{source.SampleRate}:{source.ErrorHz}:{source.Gain}"));

    private static IReadOnlyList<long> ReadFrequencyArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray()
            .Select(ReadFrequency)
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToList();
    }

    private static long ReadFrequency(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when element.TryGetDouble(out var number) => (long)Math.Round(number > 10_000_000 ? number : number * 1_000_000d),
            JsonValueKind.String => ParseFrequency(element.GetString() ?? string.Empty),
            _ => 0
        };

    private static long ParseFrequency(string value)
    {
        value = value.Trim();
        if (long.TryParse(value, out var integer))
            return integer;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number))
            return (long)Math.Round(number > 10_000_000 ? number : number * 1_000_000d);
        return 0;
    }

    private static long ReadLong(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer))
            return integer;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out integer))
            return integer;
        return 0;
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string ReadStringOrNumber(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static string ExtractSerial(string device)
    {
        if (string.IsNullOrWhiteSpace(device))
            return string.Empty;
        foreach (var part in device.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length == 2 && pieces[0].Equals("serial", StringComparison.OrdinalIgnoreCase))
                return pieces[1];
        }
        return string.Empty;
    }

    private static string SdrTypeFromDevice(string device)
    {
        var normalized = device.ToLowerInvariant();
        if (normalized.Contains("airspy", StringComparison.Ordinal))
            return "airspy";
        if (normalized.Contains("rtl", StringComparison.Ordinal))
            return "rtl";
        return string.Empty;
    }

    private static string Sha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Clean(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
