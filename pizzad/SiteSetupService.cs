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
    private readonly SemaphoreSlim _updateGate = new(1, 1);

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
        await _updateGate.WaitAsync(ct);
        try
        {
            var applied = await BuildAppliedConfigAsync(ct);
            var before = EffectiveDesired(applied);
            if (request.ExpectedVersion != before.DesiredVersion)
                throw new SiteSetupVersionConflictException(request.ExpectedVersion, before.DesiredVersion);

            var next = ApplyPatch(before, request.Patch);
            next.DesiredVersion = Math.Max(before.DesiredVersion + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            next.UpdatedAtUtc = DateTime.UtcNow;
            next.LastAppliedAtUtc = before.LastAppliedAtUtc;
            next.LastAppliedConfigHash = before.LastAppliedConfigHash;
            next.LastAppliedSourceAssignmentSummary = before.LastAppliedSourceAssignmentSummary;
            next.LastAppliedRfPathSummary = before.LastAppliedRfPathSummary;
            next.LastAppliedDesiredJson = before.LastAppliedDesiredJson;

            var changes = DescribeChanges(before, next);
            if (changes.Count == 0)
                return await GetAsync(ct);

            _config.SiteSetup = next;
            _config.Locations.MonitoredAreas = CloneMonitoredAreas(next.MonitoredAreas);
            _config.Save();

            await AddActivityAsync(
                "change",
                "desired_setup_updated",
                $"Desired setup updated: {string.Join(", ", changes.Take(4))}{(changes.Count > 4 ? $" and {changes.Count - 4} more" : "")}.",
                new { changes, before = ActivitySnapshot(before), after = ActivitySnapshot(next) },
                request.Source,
                ct);
            _logger.LogInformation("Site Setup desired state updated: {Changes}", string.Join("; ", changes));

            return await GetAsync(ct);
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private static SiteSetupConfig ApplyPatch(SiteSetupConfig before, SiteSetupDesiredPatch patch) => Normalize(new SiteSetupConfig
    {
        DesiredVersion = before.DesiredVersion,
        SiteLabel = patch.SiteLabel ?? before.SiteLabel,
        LocationNotes = patch.LocationNotes ?? before.LocationNotes,
        MonitoredAreas = patch.MonitoredAreas ?? before.MonitoredAreas,
        SystemShortNames = patch.SystemShortNames ?? before.SystemShortNames,
        SourcePlanSystemShortNames = patch.SourcePlanSystemShortNames ?? before.SourcePlanSystemShortNames,
        SourcePlanMode = patch.SourcePlanMode ?? before.SourcePlanMode,
        Systems = patch.Systems ?? before.Systems,
        SelectedSourceIndexes = patch.SelectedSourceIndexes ?? before.SelectedSourceIndexes,
        SourceAssignments = patch.SourceAssignments ?? before.SourceAssignments,
        Sources = patch.Sources ?? before.Sources,
        RfSelections = patch.RfSelections ?? before.RfSelections,
        RfPath = patch.RfPath ?? before.RfPath,
        UpdatedAtUtc = before.UpdatedAtUtc,
        LastAppliedAtUtc = before.LastAppliedAtUtc,
        LastAppliedConfigHash = before.LastAppliedConfigHash,
        LastAppliedSourceAssignmentSummary = before.LastAppliedSourceAssignmentSummary,
        LastAppliedRfPathSummary = before.LastAppliedRfPathSummary,
        LastAppliedDesiredJson = before.LastAppliedDesiredJson
    });

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
        var appliedAt = DateTime.UtcNow;
        next.Sources = ReconcileDesiredSourcesWithApplied(next.Sources, applied.Sources);
        next.UpdatedAtUtc = appliedAt;
        next.LastAppliedAtUtc = appliedAt;
        next.LastAppliedConfigHash = applied.ConfigHash;
        next.LastAppliedSourceAssignmentSummary = SourceAssignmentSummary(next.SourceAssignments);
        next.LastAppliedRfPathSummary = RfPathSummary(next.RfPath);
        next.LastAppliedDesiredJson = SerializeAppliedDesiredSnapshot(next);
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

    public async Task<SiteSetupDto> DiscardPendingAsync(SiteSetupDiscardRequest request, CancellationToken ct)
    {
        var applied = await BuildAppliedConfigAsync(ct);
        var before = EffectiveDesired(applied);
        if (TryReadAppliedDesiredSnapshot(before.LastAppliedDesiredJson, out var appliedDesiredSnapshot))
        {
            var restored = Normalize(appliedDesiredSnapshot);
            restored.DesiredVersion = Math.Max(before.DesiredVersion + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            restored.UpdatedAtUtc = DateTime.UtcNow;
            restored.LastAppliedAtUtc = before.LastAppliedAtUtc;
            restored.LastAppliedConfigHash = before.LastAppliedConfigHash;
            restored.LastAppliedSourceAssignmentSummary = before.LastAppliedSourceAssignmentSummary;
            restored.LastAppliedRfPathSummary = before.LastAppliedRfPathSummary;
            restored.LastAppliedDesiredJson = before.LastAppliedDesiredJson;
            _config.SiteSetup = restored;
            _config.Locations.MonitoredAreas = CloneMonitoredAreas(restored.MonitoredAreas);
            _config.Save();

            var snapshotChanges = DescribeChanges(before, restored);
            var snapshotDetails = request.Details.HasValue
                ? request.Details.Value.GetRawText()
                : JsonSerializer.Serialize(new { changes = snapshotChanges, before = ActivitySnapshot(before), after = ActivitySnapshot(restored), source = "last-applied-snapshot" }, EngineConfig.JsonOptions());
            var snapshotSummary = string.IsNullOrWhiteSpace(request.Summary)
                ? $"Discarded pending Site Setup changes and restored the last applied Setup snapshot{(snapshotChanges.Count > 0 ? $": {string.Join(", ", snapshotChanges.Take(4))}{(snapshotChanges.Count > 4 ? $" and {snapshotChanges.Count - 4} more" : "")}" : ".")}"
                : request.Summary.Trim();
            await AddActivityAsync("change", "pending_setup_discarded", snapshotSummary, snapshotDetails, request.Source, ct);
            return await GetAsync(ct);
        }

        var appliedSystems = await BuildAppliedSystemDefinitionsAsync(applied, before, ct);
        var appliedSources = applied.Sources
            .Select(source => new RfSurveySourceDto(
                source.Index,
                source.Device,
                source.Serial,
                SdrTypeFromDevice(source.Device),
                source.CenterHz,
                source.SampleRate,
                source.ErrorHz,
                source.Gain))
            .ToList();
        var sourceAssignments = BuildAppliedSourceAssignments(appliedSystems, appliedSources);
        var next = Normalize(new SiteSetupConfig
        {
            DesiredVersion = Math.Max(before.DesiredVersion + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            SiteLabel = before.SiteLabel,
            LocationNotes = before.LocationNotes,
            MonitoredAreas = CloneMonitoredAreas(_config.Locations.MonitoredAreas),
            SystemShortNames = applied.SystemShortNames.ToList(),
            SourcePlanSystemShortNames = applied.SystemShortNames.ToList(),
            SourcePlanMode = before.SourcePlanMode,
            Systems = appliedSystems,
            SelectedSourceIndexes = appliedSources.Select(source => source.Index).ToList(),
            SourceAssignments = sourceAssignments,
            Sources = appliedSources,
            RfSelections = [],
            RfPath = before.RfPath,
            UpdatedAtUtc = DateTime.UtcNow,
            LastAppliedAtUtc = before.LastAppliedAtUtc,
            LastAppliedConfigHash = applied.ConfigHash,
            LastAppliedSourceAssignmentSummary = SourceAssignmentSummary(sourceAssignments),
            LastAppliedRfPathSummary = RfPathSummary(before.RfPath),
            LastAppliedDesiredJson = before.LastAppliedDesiredJson
        });

        var changes = DescribeChanges(before, next);
        _config.SiteSetup = next;
        _config.Save();

        var details = request.Details.HasValue
            ? request.Details.Value.GetRawText()
            : JsonSerializer.Serialize(new { changes, before = ActivitySnapshot(before), after = ActivitySnapshot(next) }, EngineConfig.JsonOptions());
        var summary = string.IsNullOrWhiteSpace(request.Summary)
            ? $"Discarded pending Site Setup changes and reset desired state from live TR config{(changes.Count > 0 ? $": {string.Join(", ", changes.Take(4))}{(changes.Count > 4 ? $" and {changes.Count - 4} more" : "")}" : ".")}"
            : request.Summary.Trim();
        await AddActivityAsync("change", "pending_setup_discarded", summary, details, request.Source, ct);
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
        {
            if (configured.MonitoredAreas.Count == 0 && _config.Locations.MonitoredAreas.Count > 0)
                configured.MonitoredAreas = CloneMonitoredAreas(_config.Locations.MonitoredAreas);
            configured.Sources = FillMissingDesiredSourceGains(configured.Sources, applied.Sources);
            return configured;
        }

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
        configured.MonitoredAreas = CloneMonitoredAreas(_config.Locations.MonitoredAreas);
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
                    ReadSourceGain(source)));
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

    private async Task<List<RfSurveySystemDto>> BuildAppliedSystemDefinitionsAsync(SiteSetupAppliedConfigDto applied, SiteSetupConfig previous, CancellationToken ct)
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];

        var json = await File.ReadAllTextAsync(path, ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        var systems = new List<RfSurveySystemDto>();
        if (root.TryGetProperty("systems", out var systemsElement) && systemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var system in systemsElement.EnumerateArray())
            {
                var shortName = ReadString(system, "shortName");
                if (string.IsNullOrWhiteSpace(shortName))
                    continue;
                var previousSystem = previous.Systems.FirstOrDefault(row => string.Equals(row.ShortName, shortName, StringComparison.OrdinalIgnoreCase));
                systems.Add(new RfSurveySystemDto(
                    shortName,
                    string.IsNullOrWhiteSpace(previousSystem?.SiteLabel) ? shortName : previousSystem.SiteLabel,
                    ReadFrequencyArray(system, "control_channels"),
                    ReadFrequencyArray(system, "channels"),
                    previousSystem?.RadioReferenceSid ?? string.Empty,
                    previousSystem?.TalkgroupSystemShortName ?? string.Empty));
            }
        }

        if (systems.Count > 0)
            return systems;

        return applied.SystemShortNames
            .Select(name =>
            {
                var previousSystem = previous.Systems.FirstOrDefault(row => string.Equals(row.ShortName, name, StringComparison.OrdinalIgnoreCase));
                return new RfSurveySystemDto(
                    name,
                    string.IsNullOrWhiteSpace(previousSystem?.SiteLabel) ? name : previousSystem.SiteLabel,
                    applied.ControlChannelsHz,
                    previousSystem?.VoiceFrequenciesHz ?? [],
                    previousSystem?.RadioReferenceSid ?? string.Empty,
                    previousSystem?.TalkgroupSystemShortName ?? string.Empty);
            })
            .ToList();
    }

    private IReadOnlyList<SiteSetupPendingChangeDto> BuildPendingChanges(SiteSetupConfig desired, SiteSetupAppliedConfigDto applied)
    {
        var rows = new List<SiteSetupPendingChangeDto>();
        var desiredSystems = DesiredMonitoredSystemNames(desired);
        if (!SetEquals(desiredSystems, applied.SystemShortNames))
            rows.Add(new SiteSetupPendingChangeDto("Systems/Sites", $"Desired systems ({desiredSystems.Count}) differ from applied systems ({applied.SystemShortNames.Count})."));

        var desiredControls = desired.RfSelections.Count > 0
            ? desired.RfSelections.Select(selection => selection.FrequencyHz).Where(value => value > 0).Distinct().Order().ToList()
            : desired.Systems
                .Where(system => desiredSystems.Any(name => string.Equals(name, system.ShortName, StringComparison.OrdinalIgnoreCase)))
                .SelectMany(system => system.ControlChannelsHz)
                .Distinct()
                .Order()
                .ToList();
        if (desiredControls.Count > 0 && !desiredControls.SequenceEqual(applied.ControlChannelsHz))
            rows.Add(new SiteSetupPendingChangeDto("Control Channels", $"Desired CCs ({desiredControls.Count}) differ from applied CCs ({applied.ControlChannelsHz.Count})."));

        if (desired.Sources.Count > 0 && !string.Equals(SourceConfigSummary(desired.Sources), AppliedSourceConfigSummary(applied.Sources), StringComparison.Ordinal))
            rows.Add(new SiteSetupPendingChangeDto("TR Sources", $"Desired sources ({desired.Sources.Count}) differ from applied sources ({applied.Sources.Count})."));

        var appliedGainSummary = TryReadAppliedDesiredSnapshot(desired.LastAppliedDesiredJson, out var appliedDesired)
            ? SourceGainSummary(appliedDesired.Sources)
            : AppliedSourceGainSummary(applied.Sources);
        if (desired.Sources.Count > 0 && !string.Equals(SourceGainSummary(desired.Sources), appliedGainSummary, StringComparison.Ordinal))
            rows.Add(new SiteSetupPendingChangeDto("RF Calibration", "One or more desired source gain values changed after the last Site Setup apply."));

        if (desired.SourceAssignments.Count > 0 &&
            !string.Equals(SourceAssignmentSummary(desired.SourceAssignments), desired.LastAppliedSourceAssignmentSummary, StringComparison.Ordinal))
            rows.Add(new SiteSetupPendingChangeDto("Source Assignments", "Site-to-source assignments changed after the last Site Setup apply."));

        if (HasRfPathDetails(desired.RfPath) &&
            !string.Equals(RfPathSummary(desired.RfPath), desired.LastAppliedRfPathSummary, StringComparison.Ordinal))
            rows.Add(new SiteSetupPendingChangeDto("RF Path", "RF path documentation changed after the last Site Setup apply."));

        if (!string.IsNullOrWhiteSpace(desired.LastAppliedConfigHash) &&
            !string.Equals(desired.LastAppliedConfigHash, applied.ConfigHash, StringComparison.OrdinalIgnoreCase))
            rows.Add(new SiteSetupPendingChangeDto("Applied Config", "Live TR config has changed since Site Setup last applied it."));

        return rows;
    }

    private static List<string> DesiredMonitoredSystemNames(SiteSetupConfig desired)
    {
        if (desired.SourcePlanSystemShortNames.Count > 0)
            return desired.SourcePlanSystemShortNames;
        if (desired.SystemShortNames.Count > 0)
            return desired.SystemShortNames;
        return desired.Systems.Select(system => system.ShortName).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
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
        MonitoredAreas = NormalizeMonitoredAreas(value.MonitoredAreas),
        SystemShortNames = NormalizeStrings(value.SystemShortNames),
        SourcePlanSystemShortNames = NormalizeStrings(value.SourcePlanSystemShortNames),
        SourcePlanMode = string.IsNullOrWhiteSpace(value.SourcePlanMode) ? "full" : value.SourcePlanMode.Trim(),
        Systems = value.Systems?.ToList() ?? [],
        SelectedSourceIndexes = value.SelectedSourceIndexes?.Distinct().Order().ToList() ?? [],
        SourceAssignments = NormalizeSourceAssignments(value.SourceAssignments),
        Sources = value.Sources?.ToList() ?? [],
        RfSelections = NormalizeRfSelections(value.RfSelections),
        RfPath = value.RfPath ?? new RfSurveyPathProfileDto(),
        UpdatedAtUtc = value.UpdatedAtUtc,
        LastAppliedAtUtc = value.LastAppliedAtUtc,
        LastAppliedConfigHash = value.LastAppliedConfigHash?.Trim() ?? string.Empty,
        LastAppliedSourceAssignmentSummary = value.LastAppliedSourceAssignmentSummary?.Trim() ?? string.Empty,
        LastAppliedRfPathSummary = value.LastAppliedRfPathSummary?.Trim() ?? string.Empty,
        LastAppliedDesiredJson = value.LastAppliedDesiredJson?.Trim() ?? string.Empty
    };

    private static List<string> DescribeChanges(SiteSetupConfig before, SiteSetupConfig after)
    {
        var changes = new List<string>();
        AddIfChanged(changes, "site label", before.SiteLabel, after.SiteLabel);
        AddIfChanged(changes, "location notes", before.LocationNotes, after.LocationNotes);
        AddIfChanged(changes, "monitored areas", MonitoredAreaSummary(before.MonitoredAreas), MonitoredAreaSummary(after.MonitoredAreas));
        AddIfChanged(changes, "systems/sites", string.Join(",", before.SystemShortNames), string.Join(",", after.SystemShortNames));
        AddIfChanged(changes, "source-plan systems", string.Join(",", before.SourcePlanSystemShortNames), string.Join(",", after.SourcePlanSystemShortNames));
        AddIfChanged(changes, "selected sources", string.Join(",", before.SelectedSourceIndexes), string.Join(",", after.SelectedSourceIndexes));
        AddIfChanged(changes, "source assignments", SourceAssignmentSummary(before.SourceAssignments), SourceAssignmentSummary(after.SourceAssignments));
        AddIfChanged(changes, "RF path", RfPathSummary(before.RfPath), RfPathSummary(after.RfPath));
        AddIfChanged(changes, "desired sources", SourceSummary(before.Sources), SourceSummary(after.Sources));
        AddIfChanged(changes, "RF selections", RfSelectionSummary(before.RfSelections), RfSelectionSummary(after.RfSelections));
        return changes;
    }

    private static object ActivitySnapshot(SiteSetupConfig value) => new
    {
        value.DesiredVersion,
        value.SiteLabel,
        value.LocationNotes,
        monitoredAreas = MonitoredAreaSummary(value.MonitoredAreas),
        value.SystemShortNames,
        value.SourcePlanSystemShortNames,
        value.SelectedSourceIndexes,
        sourceAssignments = SourceAssignmentSummary(value.SourceAssignments),
        value.SourcePlanMode,
        rfPath = RfPathSummary(value.RfPath),
        rfSelections = RfSelectionSummary(value.RfSelections),
        sources = SourceSummary(value.Sources)
    };

    private static List<SiteSetupRfSelection> NormalizeRfSelections(IEnumerable<SiteSetupRfSelection>? values) =>
        (values ?? [])
            .Where(value => value != null && value.FrequencyHz > 0)
            .GroupBy(value => value.FrequencyHz)
            .Select(group =>
            {
                var value = group.Last();
                return new SiteSetupRfSelection
                {
                    FrequencyHz = value.FrequencyHz,
                    SourceIndex = value.SourceIndex >= 0 ? value.SourceIndex : null,
                    Gain = value.Gain?.Trim() ?? string.Empty,
                    SampleRateHz = value.SampleRateHz > 0 ? value.SampleRateHz : null,
                    ErrorHz = value.ErrorHz,
                    SnrDb = value.SnrDb is double snr && double.IsFinite(snr) ? snr : null,
                    Confidence = value.Confidence is double confidence && double.IsFinite(confidence)
                        ? Math.Clamp(confidence, 0, 1)
                        : null
                };
            })
            .OrderBy(value => value.FrequencyHz)
            .ToList();

    private static List<MonitoredAreaConfig> NormalizeMonitoredAreas(IEnumerable<MonitoredAreaConfig>? areas) =>
        (areas ?? [])
            .Where(area => area != null)
            .Select(area => new MonitoredAreaConfig
            {
                AreaId = string.IsNullOrWhiteSpace(area.AreaId) ? Guid.NewGuid().ToString("N") : area.AreaId.Trim(),
                AreaLabel = area.AreaLabel?.Trim() ?? string.Empty,
                SystemShortName = area.SystemShortName?.Trim() ?? string.Empty,
                North = area.North,
                South = area.South,
                East = area.East,
                West = area.West,
                Aliases = NormalizeStrings(area.Aliases)
            })
            .Where(area => !string.IsNullOrWhiteSpace(area.AreaLabel) || !string.IsNullOrWhiteSpace(area.SystemShortName))
            .OrderBy(area => area.SystemShortName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(area => area.AreaLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<MonitoredAreaConfig> CloneMonitoredAreas(IEnumerable<MonitoredAreaConfig>? areas) =>
        NormalizeMonitoredAreas(areas);

    private static string SerializeAppliedDesiredSnapshot(SiteSetupConfig value)
    {
        var snapshot = Normalize(value);
        snapshot.LastAppliedDesiredJson = string.Empty;
        return JsonSerializer.Serialize(snapshot, EngineConfig.JsonOptions());
    }

    private static bool TryReadAppliedDesiredSnapshot(string? json, out SiteSetupConfig snapshot)
    {
        snapshot = new SiteSetupConfig();
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            snapshot = JsonSerializer.Deserialize<SiteSetupConfig>(json, EngineConfig.JsonOptions()) ?? new SiteSetupConfig();
            return snapshot.SystemShortNames.Count > 0 || snapshot.Systems.Count > 0 || snapshot.Sources.Count > 0;
        }
        catch
        {
            snapshot = new SiteSetupConfig();
            return false;
        }
    }

    private static string MonitoredAreaSummary(IEnumerable<MonitoredAreaConfig>? areas) =>
        string.Join("|", NormalizeMonitoredAreas(areas)
            .Select(area => $"{area.SystemShortName}:{area.AreaLabel}:{area.North:F5},{area.South:F5},{area.East:F5},{area.West:F5}:{string.Join(",", area.Aliases.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}"));

    private static Dictionary<string, int> NormalizeSourceAssignments(IReadOnlyDictionary<string, int>? values) =>
        (values ?? new Dictionary<string, int>())
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Key) && kvp.Value >= 0)
            .GroupBy(kvp => kvp.Key.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int> BuildAppliedSourceAssignments(
        IEnumerable<RfSurveySystemDto> systems,
        IReadOnlyList<RfSurveySourceDto> sources)
    {
        var assignments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in systems)
        {
            var controlChannels = system.ControlChannelsHz.Where(value => value > 0).ToList();
            if (controlChannels.Count == 0)
                continue;
            var exact = sources.FirstOrDefault(source => controlChannels.All(frequency => SourceCovers(source, frequency)));
            var partial = exact ?? sources.FirstOrDefault(source => controlChannels.Any(frequency => SourceCovers(source, frequency)));
            if (partial != null)
                assignments[system.ShortName] = partial.Index;
        }
        return assignments;
    }

    private static bool SourceCovers(RfSurveySourceDto source, long frequencyHz)
    {
        var sampleRate = Math.Max(1, source.SampleRate);
        var halfSpan = sampleRate / 2.0;
        return frequencyHz >= source.CenterHz - halfSpan && frequencyHz <= source.CenterHz + halfSpan;
    }

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
        var chain = string.Join(";", (path.Chain ?? [])
            .Select(item => string.Join(",", new[]
            {
                item.Type,
                item.Label,
                item.ConnectorIn,
                item.ConnectorOut,
                item.Length,
                item.Loss,
                item.Power,
                item.Notes,
                item.ConnectorInType,
                item.ConnectorInGender,
                item.ConnectorOutType,
                item.ConnectorOutGender,
                item.GainDb,
                item.GroundPlane,
                item.PortCount,
                item.PowerPass,
                item.PowerMethod,
                item.Passband
            }.Select(value => value?.Trim() ?? ""))));
        return string.Join("|", new[]
        {
            path.Antenna,
            path.AntennaType,
            path.AntennaMount,
            path.AntennaPolarization,
            path.AimedAtSite,
            path.PositionNotes,
            path.ConnectorChain,
            path.Coax,
            path.SplitterOrMulticoupler,
            path.Lna,
            path.Filters,
            path.SdrNotes,
            path.Observations,
            chain
        }.Select(value => value?.Trim() ?? ""));
    }

    private static bool HasRfPathDetails(RfSurveyPathProfileDto? path) =>
        !string.IsNullOrWhiteSpace(RfPathSummary(path).Replace("|", "", StringComparison.Ordinal).Replace(";", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal));

    private static string SourceSummary(IEnumerable<RfSurveySourceDto>? sources) =>
        string.Join("|", (sources ?? []).Select(source => $"{source.Index}:{source.Device}:{source.CenterHz}:{source.SampleRate}:{source.ErrorHz}:{source.Gain}"));

    private static string SourceConfigSummary(IEnumerable<RfSurveySourceDto>? sources) =>
        string.Join("|", (sources ?? []).Select(source => $"{source.Index}:{source.Device}:{source.CenterHz}:{source.SampleRate}:{source.ErrorHz}"));

    private static string AppliedSourceConfigSummary(IEnumerable<SiteSetupAppliedSourceDto>? sources) =>
        string.Join("|", (sources ?? []).Select(source => $"{source.Index}:{source.Device}:{source.CenterHz}:{source.SampleRate}:{source.ErrorHz}"));

    private static string SourceGainSummary(IEnumerable<RfSurveySourceDto>? sources) =>
        string.Join("|", (sources ?? []).Select(source => $"{source.Index}:{source.Gain}"));

    private static string AppliedSourceGainSummary(IEnumerable<SiteSetupAppliedSourceDto>? sources) =>
        string.Join("|", (sources ?? []).Select(source => $"{source.Index}:{source.Gain}"));

    private static List<RfSurveySourceDto> ReconcileDesiredSourcesWithApplied(
        IReadOnlyList<RfSurveySourceDto> desired,
        IReadOnlyList<SiteSetupAppliedSourceDto> applied)
    {
        if (desired.Count == 0 || applied.Count == 0)
            return desired.ToList();
        return desired.Select(source =>
        {
            var live = applied.FirstOrDefault(row => row.Index == source.Index);
            return live == null
                ? source
                : source with
                {
                    Device = live.Device,
                    Serial = string.IsNullOrWhiteSpace(source.Serial) ? live.Serial : source.Serial,
                    SdrType = string.IsNullOrWhiteSpace(source.SdrType) ? SdrTypeFromDevice(live.Device) : source.SdrType,
                    CenterHz = live.CenterHz,
                    SampleRate = live.SampleRate,
                    ErrorHz = live.ErrorHz,
                    Gain = string.IsNullOrWhiteSpace(live.Gain) ? source.Gain : live.Gain
                };
        }).ToList();
    }

    private static List<RfSurveySourceDto> FillMissingDesiredSourceGains(
        IReadOnlyList<RfSurveySourceDto> desired,
        IReadOnlyList<SiteSetupAppliedSourceDto> applied) =>
        desired.Select(source =>
        {
            if (!string.IsNullOrWhiteSpace(source.Gain))
                return source;
            var live = applied.FirstOrDefault(row => row.Index == source.Index);
            return live == null || string.IsNullOrWhiteSpace(live.Gain)
                ? source
                : source with { Gain = live.Gain };
        }).ToList();

    private static string SourceAssignmentSummary(IReadOnlyDictionary<string, int>? values) =>
        string.Join("|", NormalizeSourceAssignments(values)
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => $"{kvp.Key}:{kvp.Value}"));

    private static string RfSelectionSummary(IEnumerable<SiteSetupRfSelection>? selections) =>
        string.Join("|", (selections ?? [])
            .Select(selection => $"{selection.FrequencyHz}:{selection.SourceIndex}:{selection.Gain}:{selection.SampleRateHz}:{selection.ErrorHz}")
            .Order(StringComparer.Ordinal));

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

    private static string ReadSourceGain(JsonElement source)
    {
        var gain = ReadStringOrNumber(source, "gain");
        if (!string.IsNullOrWhiteSpace(gain))
            return gain.Trim();

        var hasAirspyStages = source.TryGetProperty("lnaGain", out _) ||
            source.TryGetProperty("mixGain", out _) ||
            source.TryGetProperty("ifGain", out _);
        if (!hasAirspyStages)
            return string.Empty;

        var lna = ReadLong(source, "lnaGain");
        var mix = ReadLong(source, "mixGain");
        var ifGain = ReadLong(source, "ifGain");
        if (lna >= 15 && mix >= 12 && ifGain >= 8)
            return "21";
        if (lna >= 12 && mix >= 10 && ifGain >= 6)
            return "19";
        if (lna >= 8 && mix >= 6 && ifGain >= 4)
            return "13";
        if (lna >= 4 && mix >= 4 && ifGain >= 2)
            return "7";
        return "0";
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
