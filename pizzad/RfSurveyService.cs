using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace pizzad;

public sealed class RfSurveyService
{
    private const double TrUsableHalfBandwidthFactor = 0.46875;
    private const int MinUsableTranscriptionGateChars = 12;
    private static readonly TimeSpan WaterfallConsumeTimeout = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeExperimentCancellations = new();
    private readonly ConcurrentDictionary<string, WaterfallRuntime> _activeWaterfalls = new();
    private readonly ConcurrentDictionary<string, RfSurveyWaterfallStatusDto> _lastWaterfalls = new();

    private static readonly string[] ScopeInvalidatedExperimentTypes =
    [
        "control_channel_quality",
        "rf_power_scan",
        "control_channel_p25_probe",
        "error_gain_sweep",
        "voice_capture_trial",
        "transcription_gate",
        "stability_verdict",
        "temp_tr_config"
    ];

    private static readonly Regex CcSummaryLineRegex = new(
        @"\[(?<system>[^\]]+)\]\s+(?:freq:\s*)?(?<freq>\d+(?:\.\d+)?)\s+MHz\s+(?:Control Channel Message Decode Rate:\s*)?(?<rate>-?\d+(?:\.\d+)?)\s*(?:/sec|msg/sec)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrCallEventRegex = new(
        @"\[(?<system>[^\]]+)\]\s+(?<call>\d+)C\s+TG:\s+(?<tg>\d+)\b.*?\bFreq:\s+(?<freq>\d+(?:\.\d+)?)\s+MHz\s+(?<message>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CallstreamNoSamplesRegex = new(
        @"call_end\s+callstream\s+with\s+call\s+id\s+(?<call>\d+)\s+has\s+no\s+samples",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly EngineConfig _config;
    private readonly EngineDatabase _database;
    private readonly SetupCalibrationService _calibration;
    private readonly SetupJobService _jobs;
    private readonly TalkgroupCatalogService _talkgroups;
    private readonly ILogger<RfSurveyService> _logger;

    public RfSurveyService(
        EngineConfig config,
        EngineDatabase database,
        SetupCalibrationService calibration,
        SetupJobService jobs,
        TalkgroupCatalogService talkgroups,
        ILogger<RfSurveyService> logger)
    {
        _config = config;
        _database = database;
        _calibration = calibration;
        _jobs = jobs;
        _talkgroups = talkgroups;
        _logger = logger;
    }

    public string ArtifactRoot => Path.Combine(_config.Storage.AppDataRoot, "rf-surveys");

    public async Task<RfSurveyListDto> ListAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ArtifactRoot);
        var sessions = new List<RfSurveySessionDto>();
        foreach (var session in await _database.ListRfSurveySessionsAsync(ct))
        {
            var row = await _database.GetRfSurveySessionAsync(session.Id, ct);
            if (row == null)
            {
                sessions.Add(NormalizeAppliedSourcePlanSession(session));
                continue;
            }

            var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
                row.Value.Session,
                NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.Value.ProfileJson) ?? new RfSurveyProfileDto()),
                ct);
            var recovered = await RecoverAppliedSourcePlanSessionAsync(row.Value.Session, row.Value.ProfileJson, row.Value.ToolPrepJson, persist: false, ct);
            sessions.Add(recovered with { SdrSummary = SummarizeSelectedSdrs(profile) });
        }
        return new RfSurveyListDto(sessions, ArtifactRoot);
    }

    private static bool HasAppliedSourcePlan(RfSurveySessionDto session) =>
        !string.IsNullOrWhiteSpace(session.SourcePlanSummary) ||
        string.Equals(session.Status, "source_plan_applied", StringComparison.OrdinalIgnoreCase);

    private static RfSurveySessionDto NormalizeAppliedSourcePlanSession(RfSurveySessionDto session)
    {
        if (!HasAppliedSourcePlan(session) ||
            string.Equals(session.Status, "completed", StringComparison.OrdinalIgnoreCase))
            return session;
        return session with
        {
            Status = "source_plan_applied",
            Verdict = string.Equals(session.Verdict, "not_started", StringComparison.OrdinalIgnoreCase)
                ? "source_plan_candidate"
                : session.Verdict
        };
    }

    private async Task<RfSurveySessionDto> RecoverAppliedSourcePlanSessionAsync(
        RfSurveySessionDto session,
        string profileJson,
        string toolPrepJson,
        bool persist,
        CancellationToken ct)
    {
        if (HasAppliedSourcePlan(session))
            return NormalizeAppliedSourcePlanSession(session);
        var artifactPath = Path.Combine(session.ArtifactPath, "tr-config-source-apply.json");
        if (!File.Exists(artifactPath))
            return session;
        try
        {
            var artifact = JsonNode.Parse(await File.ReadAllTextAsync(artifactPath, ct)) as JsonObject;
            var candidatePath = artifact?["candidatePath"]?.GetValue<string>() ?? string.Empty;
            var livePath = artifact?["livePath"]?.GetValue<string>() ?? _config.TrunkRecorder.ConfigPath;
            if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(livePath) || !File.Exists(candidatePath) || !File.Exists(livePath))
                return session;
            var candidate = JsonNode.Parse(await File.ReadAllTextAsync(candidatePath, ct));
            var live = JsonNode.Parse(await File.ReadAllTextAsync(livePath, ct));
            if (candidate == null || live == null || !string.Equals(NormalizeJson(candidate), NormalizeJson(live), StringComparison.Ordinal))
                return session;

            var changedSources = artifact?["changedSources"] as JsonArray;
            var changed = changedSources?
                .Select(ReadIntNode)
                .Where(index => index >= 0)
                .Distinct()
                .Order()
                .ToList() ?? [];
            var sourcePlanSummary = BuildAppliedSourcePlanSummary(candidate, changed);
            var recovered = session with
            {
                Status = "source_plan_applied",
                Verdict = "source_plan_candidate",
                SourcePlanSummary = sourcePlanSummary
            };
            if (persist)
            {
                await _database.UpdateRfSurveySessionAsync(recovered, profileJson, toolPrepJson, ct);
                await WriteArtifactAsync(recovered.ArtifactPath, "survey.json", recovered, ct);
            }
            return recovered;
        }
        catch
        {
            return session;
        }
    }

    public RfSurveyProfileDto BuildProfile(RfSurveyCreateRequest request)
    {
        var plan = _calibration.BuildPlan();
        var requestedSystemNames = NormalizeRequestedSystemNames(request.SystemShortNames, request.SystemShortName);
        var warnings = new List<string>(plan.Warnings);
        var liveDefinitions = plan.Systems.Select(system => new RfSurveySystemDto(system.ShortName, system.ShortName, system.ControlChannelsHz, system.VoiceFrequenciesHz)).ToList();
        var requestDefinitions = NormalizeSystemDefinitions(request.SystemDefinitions);
        var availableDefinitions = MergeSystemDefinitions(requestDefinitions, liveDefinitions);
        var selectedSystems = SelectCalibrationSystems(plan.Systems, requestedSystemNames);
        var selectedDefinitions = SelectSurveySystems(availableDefinitions, requestedSystemNames);
        var selectedSystem = selectedSystems.FirstOrDefault(system =>
                selectedDefinitions.Any(definition => string.Equals(definition.ShortName, system.ShortName, StringComparison.OrdinalIgnoreCase)))
            ?? selectedSystems.FirstOrDefault();
        var selectedSystemNames = selectedDefinitions.Select(system => system.ShortName).ToList();
        var profileSystemNames = requestedSystemNames.Count > 0 ? requestedSystemNames : selectedSystemNames;
        var requestedSourcePlanNames = NormalizeRequestedSystemNames(request.SourcePlanSystemShortNames, null);
        var sourcePlanSystemNames = requestedSourcePlanNames.Count > 0
            ? requestedSourcePlanNames
                .Where(name => profileSystemNames.Any(selected => string.Equals(selected, name, StringComparison.OrdinalIgnoreCase)))
                .DefaultIfEmpty(profileSystemNames.FirstOrDefault() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : profileSystemNames;
        var unresolvedNames = requestedSystemNames
            .Where(requested => selectedSystemNames.All(selected => !string.Equals(selected, requested, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        foreach (var name in unresolvedNames)
            warnings.Add($"TR system '{name}' was not found in the current Radio Setup source plan.");

        if (requestedSystemNames.Count > 0 && selectedDefinitions.Count == 0)
            warnings.Add("No TR system was available. Complete setup/TR config before running Radio Setup.");

        var liveSources = plan.Sources.Select(source => new RfSurveySourceDto(
            source.Index,
            source.Device,
            source.Serial,
            InferSdrType(source.Device, source.Serial),
            source.CenterFrequency,
            source.SampleRate,
            source.ErrorHz,
            source.Gain)).ToList();
        var sourceOverride = request.SdrSources is { Count: > 0 };
        var sources = sourceOverride
            ? NormalizeRfSurveySources(request.SdrSources!)
            : requestedSystemNames.Count > 0 ? liveSources : [];

        var devices = BuildSdrDevices(sources);

        return new RfSurveyProfileDto
        {
            SiteLabel = string.IsNullOrWhiteSpace(request.SiteLabel)
                ? profileSystemNames.Count == 1 ? profileSystemNames[0] : profileSystemNames.Count > 1 ? string.Join(", ", profileSystemNames) : "Radio Setup"
                : request.SiteLabel.Trim(),
            RadioReferenceSid = string.IsNullOrWhiteSpace(request.RadioReferenceSid) ? string.Empty : request.RadioReferenceSid.Trim(),
            SystemShortName = selectedSystem?.ShortName ?? profileSystemNames.FirstOrDefault() ?? request.SystemShortName?.Trim() ?? string.Empty,
            SystemShortNames = profileSystemNames.Count > 0
                ? profileSystemNames
                : string.IsNullOrWhiteSpace(request.SystemShortName) ? [] : [request.SystemShortName.Trim()],
            SourcePlanSystemShortNames = sourcePlanSystemNames,
            SourcePlanMode = NormalizeSourcePlanMode(request.SourcePlanMode),
            Systems = selectedDefinitions,
            Mode = NormalizeMode(request.Mode),
            GroundTruthSource = string.IsNullOrWhiteSpace(request.GroundTruthSource) ? "tr-config" : request.GroundTruthSource.Trim(),
            ControlChannelsHz = selectedDefinitions.SelectMany(system => system.ControlChannelsHz).Where(value => value > 0).Distinct().Order().ToList(),
            VoiceFrequenciesHz = selectedDefinitions.SelectMany(system => system.VoiceFrequenciesHz).Where(value => value > 0).Distinct().Order().ToList(),
            Sources = sources,
            Devices = devices,
            SelectedSourceIndexes = NormalizeSelectedSourceIndexes(request.SelectedSourceIndexes, sources, selectedSystems),
            SourceOverride = sourceOverride,
            RfPath = request.RfPath ?? new RfSurveyPathProfileDto(),
            CurrentStep = Math.Clamp(request.CurrentStep, 0, 8),
            MeasurementMode = NormalizeMode(string.IsNullOrWhiteSpace(request.MeasurementMode) ? request.Mode : request.MeasurementMode),
            ProbeDurationSeconds = Math.Clamp(request.ProbeDurationSeconds <= 0 ? 45 : request.ProbeDurationSeconds, 5, 3600),
            Warnings = warnings
        };
    }

    private RfSurveyProfileDto RebuildProfileFacts(RfSurveyProfileDto current)
    {
        var rebuilt = BuildProfile(new RfSurveyCreateRequest(
            current.SystemShortName,
            current.SiteLabel,
            current.Mode,
            current.GroundTruthSource,
            current.RfPath,
            current.SelectedSourceIndexes,
            current.CurrentStep,
            current.MeasurementMode,
            current.ProbeDurationSeconds,
            current.SystemShortNames,
            current.SourcePlanSystemShortNames,
            current.SourcePlanMode,
            current.Systems,
            current.Sources,
            current.RadioReferenceSid));
        return rebuilt with
        {
            SiteLabel = string.IsNullOrWhiteSpace(current.SiteLabel) ? rebuilt.SiteLabel : current.SiteLabel,
            Mode = string.IsNullOrWhiteSpace(current.Mode) ? rebuilt.Mode : current.Mode,
            GroundTruthSource = string.IsNullOrWhiteSpace(current.GroundTruthSource) ? rebuilt.GroundTruthSource : current.GroundTruthSource,
            RfPath = current.RfPath,
            CurrentStep = current.CurrentStep,
            MeasurementMode = string.IsNullOrWhiteSpace(current.MeasurementMode) ? rebuilt.MeasurementMode : current.MeasurementMode,
            ProbeDurationSeconds = current.ProbeDurationSeconds <= 0 ? rebuilt.ProbeDurationSeconds : current.ProbeDurationSeconds,
            SelectedSourceIndexes = NormalizeSelectedSourceIndexes(current.SelectedSourceIndexes, rebuilt.Sources, null)
        };
    }

    private static List<RfSurveySdrDeviceDto> BuildSdrDevices(IReadOnlyList<RfSurveySourceDto> sources) =>
        sources.Select(source => new RfSurveySdrDeviceDto(
            source.Index,
            source.Serial,
            string.IsNullOrWhiteSpace(source.Serial) ? $"{source.SdrType} source {source.Index}" : $"{source.SdrType} {source.Serial}",
            source.SdrType,
            source.Device,
            string.IsNullOrWhiteSpace(source.Serial) ? "Serial was not present in the TR source device string." : string.Empty)).ToList();

    private async Task<RfSurveyProfileDto> RecoverProfileSourcesFromSavedTrConfigAsync(RfSurveySessionDto session, RfSurveyProfileDto profile, CancellationToken ct)
    {
        if (profile.Sources.Count > 0)
            return profile;

        var sources = await ReadSavedTrConfigSourcesAsync(Path.Combine(session.ArtifactPath, "tr-config-before.json"), ct);
        if (sources.Count == 0)
            return profile;

        return profile with
        {
            Sources = sources,
            Devices = BuildSdrDevices(sources),
            SourceOverride = true,
            SelectedSourceIndexes = NormalizeSelectedSourceIndexes(profile.SelectedSourceIndexes.Count > 0 ? profile.SelectedSourceIndexes : null, sources, null)
        };
    }

    private static async Task<IReadOnlyList<RfSurveySourceDto>> ReadSavedTrConfigSourcesAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            if (!doc.RootElement.TryGetProperty("sources", out var sourcesElement) || sourcesElement.ValueKind != JsonValueKind.Array)
                return [];

            var sources = new List<RfSurveySourceDto>();
            var ordinal = 0;
            foreach (var source in sourcesElement.EnumerateArray())
            {
                if (source.ValueKind != JsonValueKind.Object)
                    continue;
                var device = JsonString(source, "device").Trim();
                var serial = FirstNonEmpty(JsonString(source, "serial"), ExtractAirspySerial(device), ExtractRtlSerial(device));
                var index = JsonInt(source, "index");
                var sampleRate = (int)JsonLong(source, "rate");
                var gain = ReadTrSourceGain(source);
                if (index < 0 || sources.Any(existing => existing.Index == index))
                    index = ordinal;
                sources.Add(new RfSurveySourceDto(
                    index,
                    device,
                    serial,
                    InferSdrType(device, serial),
                    JsonLong(source, "center"),
                    sampleRate > 0 ? sampleRate : 2_400_000,
                    JsonInt(source, "error"),
                    string.IsNullOrWhiteSpace(gain) ? "auto" : gain.Trim()));
                ordinal++;
            }
            return sources.OrderBy(source => source.Index).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string ReadTrSourceGain(JsonElement source)
    {
        var gain = JsonString(source, "gain");
        if (!string.IsNullOrWhiteSpace(gain))
            return gain.Trim();
        var lna = JsonInt(source, "lnaGain");
        var mix = JsonInt(source, "mixGain");
        var ifGain = JsonInt(source, "ifGain");
        if (lna <= 0 && mix <= 0 && ifGain <= 0)
            return string.Empty;
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

    private static RfSurveyProfileDto NormalizeStoredProfileForWorkflow(RfSurveyProfileDto profile)
    {
        if (profile.Warnings.Count == 0)
            return profile;
        var hasSavedRadioFacts = profile.Systems.Count > 0 || profile.ControlChannelsHz.Count > 0 || !string.IsNullOrWhiteSpace(profile.RadioReferenceSid);
        if (!hasSavedRadioFacts)
            return profile;
        var warnings = profile.Warnings
            .Where(warning => !warning.Contains("No TR system was available", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return warnings.Count == profile.Warnings.Count
            ? profile
            : profile with { Warnings = warnings };
    }

    private async Task<(RfSurveySessionDto Session, RfSurveyProfileDto Profile, string ProfileJson)> RefreshProfileFactsAsync(
        RfSurveySessionDto session,
        RfSurveyProfileDto current,
        string currentProfileJson,
        string toolPrepJson,
        bool invalidateExperiments,
        CancellationToken ct)
    {
        var rebuilt = RebuildProfileFacts(current);
        if (SameProfileRadioFacts(current, rebuilt))
            return (session, current, currentProfileJson);

        var refreshedSession = session with
        {
            SiteLabel = rebuilt.SiteLabel,
            SystemShortName = rebuilt.SystemShortName,
            SdrSummary = SummarizeSelectedSdrs(rebuilt),
            UpdatedAtUtc = DateTime.UtcNow
        };
        var profileJson = JsonSerializer.Serialize(rebuilt, EngineConfig.JsonOptions());
        if (invalidateExperiments)
            await _database.DeleteRfSurveyExperimentsAsync(session.Id, ScopeInvalidatedExperimentTypes, ct);
        await WriteArtifactAsync(refreshedSession.ArtifactPath, "survey.json", refreshedSession, ct);
        await WriteArtifactAsync(refreshedSession.ArtifactPath, "input-profile.json", rebuilt, ct);
        await _database.UpdateRfSurveySessionAsync(refreshedSession, profileJson, toolPrepJson, ct);
        return (refreshedSession, rebuilt, profileJson);
    }

    private static bool SameProfileRadioFacts(RfSurveyProfileDto left, RfSurveyProfileDto right)
    {
        return left.SystemShortName == right.SystemShortName
            && left.RadioReferenceSid == right.RadioReferenceSid
            && left.SystemShortNames.SequenceEqual(right.SystemShortNames)
            && left.SourcePlanSystemShortNames.SequenceEqual(right.SourcePlanSystemShortNames)
            && left.SourcePlanMode == right.SourcePlanMode
            && left.Systems.SequenceEqual(right.Systems)
            && left.ControlChannelsHz.SequenceEqual(right.ControlChannelsHz)
            && left.VoiceFrequenciesHz.SequenceEqual(right.VoiceFrequenciesHz)
            && left.Sources.SequenceEqual(right.Sources)
            && left.Devices.SequenceEqual(right.Devices)
            && left.SelectedSourceIndexes.SequenceEqual(right.SelectedSourceIndexes)
            && left.SourceOverride == right.SourceOverride
            && left.Warnings.SequenceEqual(right.Warnings);
    }

    public async Task<RfSurveyDetailDto> CreateAsync(RfSurveyCreateRequest request, CancellationToken ct)
    {
        Directory.CreateDirectory(ArtifactRoot);
        var now = DateTime.UtcNow;
        var id = $"rf-{now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..29];
        var artifactPath = Path.Combine(ArtifactRoot, id);
        Directory.CreateDirectory(artifactPath);

        var profile = BuildProfile(request);
        var session = new RfSurveySessionDto
        {
            Id = id,
            Status = "draft",
            Mode = profile.Mode,
            SiteLabel = profile.SiteLabel,
            SystemShortName = profile.SystemShortName,
            SdrSummary = SummarizeSdrs(profile.Sources),
            RfPathSummary = SummarizeRfPath(profile.RfPath),
            ArtifactPath = artifactPath,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var toolPrep = await LatestReusableToolPrepAsync(null, ct) ?? EmptyToolPrep();
        await WriteArtifactAsync(artifactPath, "survey.json", session, ct);
        await WriteArtifactAsync(artifactPath, "input-profile.json", profile, ct);
        await WriteArtifactAsync(artifactPath, "tool-prep.json", toolPrep, ct);
        await TryCopyTrConfigAsync(artifactPath, ct);

        await _database.AddRfSurveySessionAsync(
            session,
            JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()),
            JsonSerializer.Serialize(toolPrep, EngineConfig.JsonOptions()),
            ct);

        return new RfSurveyDetailDto(session, profile, [], [], toolPrep, PlanNextExperiments(session, profile, toolPrep, []));
    }

    public async Task<RfSurveyDetailDto> UpsertSiteSetupAsync(SiteSetupConfig desired, CancellationToken ct)
    {
        const string id = "site-setup";
        Directory.CreateDirectory(ArtifactRoot);
        var artifactPath = Path.Combine(ArtifactRoot, id);
        Directory.CreateDirectory(artifactPath);

        var request = BuildSiteSetupRequest(desired);
        var row = await _database.GetRfSurveySessionAsync(id, ct);
        if (row != null)
            return await UpdateDraftAsync(id, ToDraftUpdate(request), ct);

        var now = DateTime.UtcNow;
        var profile = BuildProfile(request);
        var session = new RfSurveySessionDto
        {
            Id = id,
            Status = "draft",
            Mode = profile.Mode,
            SiteLabel = profile.SiteLabel,
            SystemShortName = profile.SystemShortName,
            SdrSummary = SummarizeSdrs(profile.Sources),
            RfPathSummary = SummarizeRfPath(profile.RfPath),
            ArtifactPath = artifactPath,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        var toolPrep = await LatestReusableToolPrepAsync(null, ct) ?? EmptyToolPrep();
        await WriteArtifactAsync(artifactPath, "survey.json", session, ct);
        await WriteArtifactAsync(artifactPath, "input-profile.json", profile, ct);
        await WriteArtifactAsync(artifactPath, "tool-prep.json", toolPrep, ct);
        await TryCopyTrConfigAsync(artifactPath, ct);

        await _database.AddRfSurveySessionAsync(
            session,
            JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()),
            JsonSerializer.Serialize(toolPrep, EngineConfig.JsonOptions()),
            ct);

        return new RfSurveyDetailDto(session, profile, [], [], toolPrep, PlanNextExperiments(session, profile, toolPrep, []));
    }

    private static RfSurveyCreateRequest BuildSiteSetupRequest(SiteSetupConfig desired)
    {
        var systemNames = desired.Systems.Count > 0
            ? desired.Systems.Select(system => system.ShortName).Where(name => !string.IsNullOrWhiteSpace(name)).ToList()
            : desired.SystemShortNames;
        return new RfSurveyCreateRequest(
            SystemShortName: systemNames.FirstOrDefault(),
            SiteLabel: string.IsNullOrWhiteSpace(desired.SiteLabel) ? "Site Setup" : desired.SiteLabel,
            Mode: "guided",
            RadioReferenceSid: string.IsNullOrWhiteSpace(desired.RadioReferenceSid) ? null : desired.RadioReferenceSid,
            SystemShortNames: systemNames,
            SourcePlanSystemShortNames: desired.SourcePlanSystemShortNames.Count > 0 ? desired.SourcePlanSystemShortNames : systemNames,
            SourcePlanMode: string.IsNullOrWhiteSpace(desired.SourcePlanMode) ? "full" : desired.SourcePlanMode,
            RfPath: desired.RfPath,
            SelectedSourceIndexes: desired.SelectedSourceIndexes,
            CurrentStep: 2,
            MeasurementMode: "guided",
            ProbeDurationSeconds: 45,
            GroundTruthSource: "site-setup",
            SystemDefinitions: desired.Systems,
            SdrSources: desired.Sources);
    }

    private static RfSurveyDraftUpdateRequest ToDraftUpdate(RfSurveyCreateRequest request) => new(
        SystemShortName: request.SystemShortName,
        SiteLabel: request.SiteLabel,
        Mode: request.Mode,
        RadioReferenceSid: request.RadioReferenceSid,
        SystemShortNames: request.SystemShortNames,
        SourcePlanSystemShortNames: request.SourcePlanSystemShortNames,
        SourcePlanMode: request.SourcePlanMode,
        RfPath: request.RfPath,
        SelectedSourceIndexes: request.SelectedSourceIndexes,
        CurrentStep: request.CurrentStep,
        MeasurementMode: request.MeasurementMode,
        ProbeDurationSeconds: request.ProbeDurationSeconds,
        GroundTruthSource: request.GroundTruthSource,
        SystemDefinitions: request.SystemDefinitions,
        SdrSources: request.SdrSources);

    public async Task<RfSurveyDetailDto?> GetAsync(string id, CancellationToken ct, bool compactExperiments = false)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct);
        if (row == null)
            return null;

        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Value.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.Value.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var recoveredSession = await RecoverAppliedSourcePlanSessionAsync(row.Value.Session, row.Value.ProfileJson, row.Value.ToolPrepJson, persist: false, ct);
        var toolPrepState = await ResolveToolPrepForReadAsync(recoveredSession.Id, row.Value.ToolPrepJson, ct);
        var session = NormalizeAppliedSourcePlanSession(recoveredSession) with { SdrSummary = SummarizeSelectedSdrs(profile) };
        var toolPrep = toolPrepState.Prep;
        var experiments = await _database.ListRfSurveyExperimentsAsync(id, ct);
        if (compactExperiments)
            experiments = experiments.Select(CompactExperimentForWorkspaceOpen).ToList();
        var notes = await _database.ListRfSurveyNotesAsync(id, ct);
        return new RfSurveyDetailDto(session, profile, experiments, notes, toolPrep, PlanNextExperiments(session, profile, toolPrep, experiments));
    }

    private static RfSurveyExperimentDto CompactExperimentForWorkspaceOpen(RfSurveyExperimentDto experiment)
    {
        const int MaxInlineJsonLength = 16_000;
        return experiment with
        {
            EvidenceJson = experiment.EvidenceJson.Length > MaxInlineJsonLength ? CompactExperimentEvidenceJson(experiment) : experiment.EvidenceJson,
            InterpretationJson = experiment.InterpretationJson.Length > MaxInlineJsonLength ? "{}" : experiment.InterpretationJson
        };
    }

    private static string CompactExperimentEvidenceJson(RfSurveyExperimentDto experiment)
    {
        if (!string.Equals(experiment.Type, "rf_validation_sweep", StringComparison.OrdinalIgnoreCase))
            return "{}";

        try
        {
            if (JsonNode.Parse(experiment.EvidenceJson) is not JsonObject root)
                return "{}";

            var compact = new JsonObject();
            CopyNode(root, compact, "systemShortName");
            CopyNode(root, compact, "selectedControlChannelHz");
            CopyNode(root, compact, "selectedSourceIndex");
            CopyNode(root, compact, "selectedGain");
            CopyNode(root, compact, "selectedErrorHz");
            CopyNode(root, compact, "technicalBlocker");
            CopyNode(root, compact, "parameters");
            CopyNode(root, compact, "liveCandidateId");
            CopyNode(root, compact, "p25ProbedCandidateIds");
            CopyNode(root, compact, "voiceTestedCandidateIds");
            CopyNode(root, compact, "siteReadiness");

            if (root["power"] is JsonObject power)
            {
                var compactPower = new JsonObject();
                CopyNode(power, compactPower, "controlChannelHz");
                CopyNode(power, compactPower, "controlChannelsHz");
                CopyNode(power, compactPower, "rows");
                compact["power"] = compactPower;
            }

            if (root["candidates"] is JsonArray candidates)
            {
                var compactCandidates = new JsonArray();
                foreach (var item in candidates.OfType<JsonObject>())
                    compactCandidates.Add(CompactRfValidationCandidate(item));
                compact["candidates"] = compactCandidates;
            }

            return compact.ToJsonString(EngineConfig.JsonOptions());
        }
        catch
        {
            return "{}";
        }
    }

    private static JsonObject CompactRfValidationCandidate(JsonObject candidate)
    {
        var compact = new JsonObject();
        foreach (var name in new[]
                 {
                     "id", "systemShortName", "siteLabel", "sourceIndex", "sdrType", "serial", "device",
                     "centerHz", "sampleRate", "errorHz", "errorOffsetHz", "gain", "controlChannelHz",
                     "rfStatus", "snrDb", "peakFrequencyHz", "peakOffsetHz", "peakDb", "noiseFloorDb",
                     "overload", "score", "p25Status", "p25Summary", "p25Frames", "p25Demod",
                     "p25ExitCode", "metricsStatus", "metricsSummary", "metricsRow", "voiceStatus",
                     "voiceSummary", "voiceTotalCalls", "voiceRealCalls"
                 })
            CopyNode(candidate, compact, name);
        return compact;
    }

    private static void CopyNode(JsonObject source, JsonObject target, string name)
    {
        if (source.TryGetPropertyValue(name, out var value) && value != null)
            target[name] = JsonNode.Parse(value.ToJsonString());
    }

    public async Task<RfSurveySweepProgressDto> GetSweepProgressAsync(string id, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var root = Path.Combine(row.Session.ArtifactPath, "rf-power-scans");
        if (!Directory.Exists(root))
            return new RfSurveySweepProgressDto(_activeExperimentCancellations.ContainsKey(id), string.Empty, []);

        var latest = Directory.GetDirectories(root)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .FirstOrDefault();
        if (latest == null)
            return new RfSurveySweepProgressDto(_activeExperimentCancellations.ContainsKey(id), string.Empty, []);

        var jsonPath = Path.Combine(latest.FullName, "rf-power-scan.json");
        if (!File.Exists(jsonPath))
            return new RfSurveySweepProgressDto(_activeExperimentCancellations.ContainsKey(id), latest.FullName, []);

        try
        {
            var rootNode = JsonNode.Parse(await File.ReadAllTextAsync(jsonPath, ct)) as JsonObject;
            var rows = (rootNode?["rows"] as JsonArray)?.OfType<JsonObject>()
                .Select(row => new RfSurveySweepProgressRowDto(
                    ReadIntNode(row["index"]),
                    ReadLongNode(row["controlChannelHz"]),
                    row["gain"]?.GetValue<object>()?.ToString() ?? string.Empty,
                    row["status"]?.GetValue<string>() ?? string.Empty,
                    row["issue"]?.GetValue<string>() ?? string.Empty,
                    ReadNullableDoubleNode(row["snrDb"]),
                    ReadNullableDoubleNode(row["peakOffsetHz"]),
                    row["overload"]?.GetValue<bool>() ?? false))
                .ToList() ?? [];
            var candidates = (rootNode?["candidates"] as JsonArray)?.OfType<JsonObject>()
                .Select(candidate => new RfSurveySweepCandidateProgressDto(
                    candidate["id"]?.GetValue<string>() ?? string.Empty,
                    ReadIntNode(candidate["sourceIndex"]),
                    ReadLongNode(candidate["controlChannelHz"]),
                    candidate["gain"]?.GetValue<object>()?.ToString() ?? string.Empty,
                    ReadIntNode(candidate["errorHz"]),
                    candidate["p25Status"]?.GetValue<string>() ?? string.Empty,
                    candidate["p25Summary"]?.GetValue<string>() ?? string.Empty,
                    candidate["metricsStatus"]?.GetValue<string>() ?? string.Empty,
                    candidate["metricsSummary"]?.GetValue<string>() ?? string.Empty,
                    candidate["voiceStatus"]?.GetValue<string>() ?? string.Empty,
                    candidate["voiceSummary"]?.GetValue<string>() ?? string.Empty,
                    ReadIntNode(candidate["voiceTotalCalls"]),
                    ReadIntNode(candidate["voiceRealCalls"])))
                .ToList() ?? [];
            return new RfSurveySweepProgressDto(_activeExperimentCancellations.ContainsKey(id), latest.FullName, rows, candidates);
        }
        catch
        {
            return new RfSurveySweepProgressDto(_activeExperimentCancellations.ContainsKey(id), latest.FullName, []);
        }
    }

    public async Task<RfSurveyDetailDto> UpdateDraftAsync(string id, RfSurveyDraftUpdateRequest request, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var current = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var toolPrepState = await EnsureReusableToolPrepAsync(row.Session, row.ProfileJson, row.ToolPrepJson, ct);
        var toolPrep = toolPrepState.Prep ?? EmptyToolPrep();
        var requestedSystems = NormalizeRequestedSystemNames(request.SystemShortNames, request.SystemShortName);
        var effectiveSystems = requestedSystems.Count > 0
            ? requestedSystems
            : current.SystemShortNames.Count > 0 ? current.SystemShortNames : NormalizeRequestedSystemNames(null, current.SystemShortName);
        var systemShortName = effectiveSystems.FirstOrDefault() ?? string.Empty;
        var currentSystems = current.SystemShortNames.Count > 0 ? current.SystemShortNames : NormalizeRequestedSystemNames(null, current.SystemShortName);
        var sourcePlanSystems = NormalizeRequestedSystemNames(request.SourcePlanSystemShortNames, null);
        var effectiveSourcePlanSystems = sourcePlanSystems.Count > 0
            ? sourcePlanSystems
            : current.SourcePlanSystemShortNames.Count > 0 ? current.SourcePlanSystemShortNames : effectiveSystems;
        var effectiveSourcePlanMode = NormalizeSourcePlanMode(request.SourcePlanMode ?? current.SourcePlanMode);
        var incomingDefinitions = request.SystemDefinitions ?? current.Systems;
        var incomingSources = request.SdrSources ?? current.Sources;
        var definitionsChanged = request.SystemDefinitions != null && !NormalizeSystemDefinitions(request.SystemDefinitions).SequenceEqual(current.Systems);
        var sourcesChanged = request.SdrSources != null && !NormalizeRfSurveySources(request.SdrSources).SequenceEqual(current.Sources);
        var sourcePlanAppliedBeforeDraft = HasAppliedSourcePlan(row.Session);
        var staleAppliedEmptySelectionAutosave = sourcePlanAppliedBeforeDraft &&
            (request.CurrentStep ?? current.CurrentStep) >= 3 &&
            request.SelectedSourceIndexes != null &&
            request.SelectedSourceIndexes.Count == 0 &&
            current.SelectedSourceIndexes.Count > 0 &&
            !sourcesChanged;
        var effectiveSelectedSourceIndexes = staleAppliedEmptySelectionAutosave
            ? current.SelectedSourceIndexes
            : request.SelectedSourceIndexes;
        var staleStepFourSupersetAutosave = sourcePlanAppliedBeforeDraft &&
            request.CurrentStep >= 4 &&
            sourcePlanSystems.Count > 0 &&
            current.SourcePlanSystemShortNames.Count > 0 &&
            current.SourcePlanSystemShortNames.All(name => sourcePlanSystems.Any(incoming => string.Equals(incoming, name, StringComparison.OrdinalIgnoreCase))) &&
            sourcePlanSystems.Count > current.SourcePlanSystemShortNames.Count &&
            !sourcesChanged &&
            SameIntSet(effectiveSelectedSourceIndexes ?? current.SelectedSourceIndexes, current.SelectedSourceIndexes);
        if (staleStepFourSupersetAutosave)
        {
            effectiveSourcePlanSystems = current.SourcePlanSystemShortNames;
            effectiveSourcePlanMode = NormalizeSourcePlanMode(current.SourcePlanMode);
        }
        var sourcePlanChanged = !SameStringSet(effectiveSourcePlanSystems, current.SourcePlanSystemShortNames.Count > 0 ? current.SourcePlanSystemShortNames : currentSystems);
        var sourcePlanModeChanged = !string.Equals(effectiveSourcePlanMode, NormalizeSourcePlanMode(current.SourcePlanMode), StringComparison.OrdinalIgnoreCase);
        var staleAppliedSamePlanAutosave = sourcePlanAppliedBeforeDraft &&
            !sourcesChanged &&
            !sourcePlanChanged &&
            !sourcePlanModeChanged &&
            SameStringSet(effectiveSystems, currentSystems) &&
            SameIntSet(effectiveSelectedSourceIndexes ?? current.SelectedSourceIndexes, current.SelectedSourceIndexes);
        var effectiveDefinitionsChanged = staleStepFourSupersetAutosave || staleAppliedEmptySelectionAutosave || staleAppliedSamePlanAutosave ? false : definitionsChanged;
        var systemChanged = !SameStringSet(effectiveSystems, currentSystems) || sourcePlanChanged || sourcePlanModeChanged || effectiveDefinitionsChanged || sourcesChanged;
        var radioFactsChanged = false;
        var effectiveRadioReferenceSid = string.IsNullOrWhiteSpace(request.RadioReferenceSid) ? current.RadioReferenceSid : request.RadioReferenceSid.Trim();
        var rebuilt = systemChanged
            ? BuildProfile(new RfSurveyCreateRequest(systemShortName, request.SiteLabel ?? string.Join(", ", effectiveSystems), request.Mode ?? current.Mode, request.GroundTruthSource ?? current.GroundTruthSource, request.RfPath ?? current.RfPath, effectiveSelectedSourceIndexes ?? current.SelectedSourceIndexes, request.CurrentStep ?? current.CurrentStep, request.MeasurementMode ?? current.MeasurementMode, request.ProbeDurationSeconds ?? current.ProbeDurationSeconds, effectiveSystems, effectiveSourcePlanSystems, effectiveSourcePlanMode, incomingDefinitions, incomingSources, effectiveRadioReferenceSid))
            : current;

        var sources = rebuilt.Sources;
        var selected = effectiveSelectedSourceIndexes == null
            ? rebuilt.SelectedSourceIndexes
            : NormalizeSelectedSourceIndexes(effectiveSelectedSourceIndexes, sources, null);
        var scopeInvalidated = systemChanged || radioFactsChanged || !SameIntSet(selected, current.SelectedSourceIndexes);
        var incomingRfPath = request.RfPath ?? rebuilt.RfPath;
        var rfPath = HasMeaningfulRfPath(incomingRfPath) || !HasMeaningfulRfPath(current.RfPath)
            ? incomingRfPath
            : current.RfPath;
        var profile = rebuilt with
        {
            SiteLabel = string.IsNullOrWhiteSpace(request.SiteLabel) ? rebuilt.SiteLabel : request.SiteLabel.Trim(),
            RadioReferenceSid = effectiveRadioReferenceSid,
            Mode = NormalizeMode(request.Mode ?? rebuilt.Mode),
            GroundTruthSource = string.IsNullOrWhiteSpace(request.GroundTruthSource) ? rebuilt.GroundTruthSource : request.GroundTruthSource.Trim(),
            RfPath = rfPath,
            SelectedSourceIndexes = selected,
            CurrentStep = Math.Clamp(request.CurrentStep ?? rebuilt.CurrentStep, 0, 8),
            MeasurementMode = NormalizeMode(request.MeasurementMode ?? rebuilt.MeasurementMode),
            ProbeDurationSeconds = Math.Clamp(request.ProbeDurationSeconds ?? rebuilt.ProbeDurationSeconds, 5, 3600),
            SourcePlanMode = effectiveSourcePlanMode
        };

        var sourcePlanApplied = sourcePlanAppliedBeforeDraft;
        var staleAppliedSourceOverride = sourcePlanApplied &&
            sourcesChanged &&
            !definitionsChanged &&
            !sourcePlanChanged &&
            !sourcePlanModeChanged &&
            SameStringSet(effectiveSystems, currentSystems) &&
            !radioFactsChanged &&
            SameIntSet(selected, current.SelectedSourceIndexes);
        var effectiveScopeInvalidated = scopeInvalidated && !staleAppliedSourceOverride;
        var baseSession = NormalizeAppliedSourcePlanSession(row.Session);
        var session = baseSession with
        {
            SiteLabel = profile.SiteLabel,
            SystemShortName = profile.SystemShortName,
            Mode = profile.Mode,
            SdrSummary = SummarizeSelectedSdrs(profile),
            RfPathSummary = SummarizeRfPath(profile.RfPath),
            Status = effectiveScopeInvalidated ? "draft" : baseSession.Status,
            Verdict = effectiveScopeInvalidated ? "not_started" : baseSession.Verdict,
            Stability = effectiveScopeInvalidated ? "unknown" : baseSession.Stability,
            BestControlChannel = effectiveScopeInvalidated ? string.Empty : baseSession.BestControlChannel,
            SourcePlanSummary = effectiveScopeInvalidated ? string.Empty : baseSession.SourcePlanSummary,
            UpdatedAtUtc = DateTime.UtcNow
        };
        if (effectiveScopeInvalidated)
            await _database.DeleteRfSurveyExperimentsAsync(id, ScopeInvalidatedExperimentTypes, ct);
        await WriteArtifactAsync(session.ArtifactPath, "survey.json", session, ct);
        await WriteArtifactAsync(session.ArtifactPath, "input-profile.json", profile, ct);
        await _database.UpdateRfSurveySessionAsync(session, JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()), toolPrepState.Json, ct);
        var experiments = await _database.ListRfSurveyExperimentsAsync(id, ct);
        var notes = await _database.ListRfSurveyNotesAsync(id, ct);
        return new RfSurveyDetailDto(session, profile, experiments, notes, toolPrep, PlanNextExperiments(session, profile, toolPrep, experiments));
    }

    public async Task<RfSurveyDetailDto> CompleteAsync(string id, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var toolPrep = DeserializeOrDefault<RfSurveyToolPrepDto>(row.ToolPrepJson) ?? EmptyToolPrep();
        var completedAt = DateTime.UtcNow;
        var session = row.Session with
        {
            Status = "completed",
            Verdict = "applied",
            RecommendationState = "none",
            UpdatedAtUtc = completedAt,
            CompletedAtUtc = completedAt
        };
        await WriteArtifactAsync(session.ArtifactPath, "survey.json", session, ct);
        await _database.UpdateRfSurveySessionAsync(session, row.ProfileJson, row.ToolPrepJson, ct);
        var experiments = await _database.ListRfSurveyExperimentsAsync(id, ct);
        var notes = await _database.ListRfSurveyNotesAsync(id, ct);
        return new RfSurveyDetailDto(session, profile, experiments, notes, toolPrep, PlanNextExperiments(session, profile, toolPrep, experiments));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct);
        if (row == null)
            return false;

        var deleted = await _database.DeleteRfSurveySessionAsync(id, ct);
        if (deleted)
            DeleteArtifactDirectory(row.Value.Session.ArtifactPath);
        return deleted;
    }

    public async Task<IReadOnlyList<RfSurveyExperimentPlanDto>> GetNextExperimentsAsync(string id, CancellationToken ct)
    {
        var detail = await GetAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        return detail.NextExperiments;
    }

    public async Task<RfSurveyP25ProbePreviewDto> PreviewP25ProbeAsync(string id, long? controlChannelHz, int? durationSeconds, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        return BuildP25ProbePreview(profile, row.Session.ArtifactPath, controlChannelHz, durationSeconds);
    }

    public async Task<RfSurveyExportPlanDto> ExportPlanAsync(string id, CancellationToken ct)
    {
        var detail = await GetAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var (recommendations, blockers) = BuildExportContent(detail);
        var planPath = Path.Combine(detail.Session.ArtifactPath, "export-plan.json");
        var markdownPath = Path.Combine(detail.Session.ArtifactPath, "export-plan.md");
        var plan = new RfSurveyExportPlanDto(
            detail.Session.Id,
            detail.Session.ArtifactPath,
            planPath,
            markdownPath,
            detail.Session.Verdict,
            detail.Session.Stability,
            recommendations,
            blockers);
        await WriteArtifactAsync(detail.Session.ArtifactPath, "export-plan.json", plan, ct);
        await File.WriteAllTextAsync(markdownPath, RenderExportMarkdown(detail, recommendations, blockers), ct);
        return plan;
    }

    public async Task<RfSurveyExportDocumentDto> ExportPlanDocumentAsync(string id, CancellationToken ct)
    {
        var detail = await GetAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var (recommendations, blockers) = BuildExportContent(detail);
        var label = string.IsNullOrWhiteSpace(detail.Session.SiteLabel) ? detail.Session.Id : detail.Session.SiteLabel;
        var fileName = $"radio-setup-{SanitizeFileToken(label)}-{DateTime.UtcNow:yyyyMMddHHmmss}.md";
        return new RfSurveyExportDocumentDto(fileName, RenderExportMarkdown(detail, recommendations, blockers));
    }

    public async Task<RfSurveyTrActionResultDto> StopTrForSurveyAsync(string id, RfSurveyTrActionRequest request, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        if (!request.Confirmed)
            throw new InvalidOperationException("Stopping trunk-recorder requires explicit confirmation.");
        var stopOutput = await RunServiceHelperAsync("stop-tr", ct);
        var startOutput = await RunServiceHelperAsync("start-tr", ct);
        var output = stopOutput + Environment.NewLine + startOutput;
        await WriteArtifactAsync(row.Session.ArtifactPath, $"tr-stop-{DateTime.UtcNow:yyyyMMddHHmmss}.json", new { stopOutput, startOutput, service = TrUnitName() }, ct);
        return new RfSurveyTrActionResultDto(true, "stop-tr", "trunk-recorder was briefly paused and restarted. Radio Setup experiments now manage TR pauses automatically.", "", "", "", output);
    }

    public async Task<RfSurveyTrActionResultDto> ApplyTempTrConfigAsync(string id, RfSurveyTrActionRequest request, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        if (!request.Confirmed)
            throw new InvalidOperationException("Applying a temporary TR config requires explicit confirmation.");
        var livePath = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(livePath) || !File.Exists(livePath))
            throw new InvalidOperationException("Live trunk-recorder config was not found.");

        var candidatePath = await EnsureCandidateTrConfigAsync(row.Session.ArtifactPath, ct);
        var backupPath = Path.Combine(row.Session.ArtifactPath, $"tr-config-live-backup-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
        File.Copy(livePath, backupPath, overwrite: false);
        await WriteArtifactAsync(row.Session.ArtifactPath, "tr-config-restore-pointer.json", new { backupPath, livePath, createdAtUtc = DateTime.UtcNow }, ct);

        await InstallTrFileAsync(candidatePath, livePath, ct);
        await _talkgroups.GenerateTrCsvAsync(ct);
        var serviceOutput = request.RestartTr ? await RunServiceHelperAsync("restart-tr", ct) : "Restart TR manually before capture trials.";
        return new RfSurveyTrActionResultDto(true, "apply-temp-config", "Temporary TR config was installed. TR restart is required before measuring this candidate.", candidatePath, backupPath, backupPath, serviceOutput);
    }

    public async Task<RfSurveyTrActionResultDto> ApplySourceDraftAsync(string id, RfSurveyApplySourceDraftRequest request, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var livePath = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(livePath) || !File.Exists(livePath))
            throw new InvalidOperationException("Live trunk-recorder config was not found.");
        if (string.IsNullOrWhiteSpace(request.ConfigJson))
            throw new InvalidOperationException("No candidate TR config JSON was supplied.");

        var selected = profile.SelectedSourceIndexes.Count > 0
            ? profile.SelectedSourceIndexes
            : profile.Sources.Select(source => source.Index).ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Select at least one source before applying the Radio Setup config draft.");

        var liveRoot = JsonNode.Parse(await File.ReadAllTextAsync(livePath, ct)) as JsonObject
            ?? throw new JsonException("Live TR config root must be a JSON object.");
        var draftRoot = JsonNode.Parse(request.ConfigJson) as JsonObject
            ?? throw new JsonException("Candidate TR config root must be a JSON object.");
        var liveSources = liveRoot["sources"] as JsonArray
            ?? throw new JsonException("Live TR config does not contain a sources array.");
        var draftSources = draftRoot["sources"] as JsonArray
            ?? throw new JsonException("Candidate TR config does not contain a sources array.");

        var changed = new List<int>();
        var selectedSet = selected.Distinct().Order().ToHashSet();
        foreach (var index in selectedSet)
        {
            if (index < 0 || index >= draftSources.Count || draftSources[index] is not JsonObject draftSource)
                throw new InvalidOperationException($"Candidate TR config does not contain source {index}.");
            changed.Add(index);
        }
        liveRoot["sources"] = CloneJsonArray(draftSources);

        if (draftRoot["systems"] is JsonArray draftSystems)
            liveRoot["systems"] = CloneJsonArray(draftSystems);
        if (draftRoot["plugins"] is JsonArray draftPlugins)
            PatchLiveCallstreamFromDraft(liveRoot, draftPlugins);
        var configChanges = new List<string>();
        NormalizeRadioSetupTrConfig(liveRoot, configChanges, _config.TrunkRecorder.TalkgroupsPath);

        var candidateJson = liveRoot.ToJsonString(EngineConfig.JsonOptions()) + Environment.NewLine;
        var coverage = TrConfigSourceCoverageValidator.Validate(candidateJson);
        if (!coverage.Ok)
            throw new InvalidOperationException("Candidate TR config still cannot start with the selected source plan: " + string.Join(" ", coverage.Blockers));

        var candidatePath = Path.Combine(row.Session.ArtifactPath, $"tr-config-source-apply-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
        Directory.CreateDirectory(row.Session.ArtifactPath);
        await File.WriteAllTextAsync(candidatePath, candidateJson, ct);
        var backupPath = await InstallTrFileAsync(candidatePath, livePath, ct);
        await _talkgroups.GenerateTrCsvAsync(ct);
        var serviceOutput = request.RestartTr ? await RunServiceHelperAsync("restart-tr", ct) : "Restart TR when you are ready for the change to take effect.";
        var sourcePlanSummary = BuildAppliedSourcePlanSummary(liveRoot, changed);
        await WriteArtifactAsync(row.Session.ArtifactPath, "tr-config-source-apply.json", new
        {
            livePath,
            candidatePath,
            backupPath,
            changedSources = changed,
            configChanges,
            restarted = request.RestartTr,
            preservedRfValidationEvidence = request.PreserveRfValidationEvidence,
            createdAtUtc = DateTime.UtcNow
        }, ct);
        var session = row.Session with
        {
            Status = "source_plan_applied",
            Verdict = "source_plan_candidate",
            SourcePlanSummary = sourcePlanSummary,
            UpdatedAtUtc = DateTime.UtcNow
        };
        var refreshed = await RefreshProfileFactsAsync(session, profile, row.ProfileJson, row.ToolPrepJson, invalidateExperiments: !request.PreserveRfValidationEvidence, ct);
        if (refreshed.Session.SourcePlanSummary != sourcePlanSummary || refreshed.Session.Status != "source_plan_applied")
        {
            var appliedSession = refreshed.Session with
            {
                Status = "source_plan_applied",
                Verdict = "source_plan_candidate",
                SourcePlanSummary = sourcePlanSummary,
                UpdatedAtUtc = DateTime.UtcNow
            };
            await _database.UpdateRfSurveySessionAsync(appliedSession, refreshed.ProfileJson, row.ToolPrepJson, ct);
            await WriteArtifactAsync(appliedSession.ArtifactPath, "survey.json", appliedSession, ct);
        }
        return new RfSurveyTrActionResultDto(true, "apply-source-draft", $"{sourcePlanSummary} Workspace TR system list applied.", candidatePath, backupPath, backupPath, serviceOutput);
    }

    public async Task<RfSurveyConfigDraftDto> BuildConfigDraftAsync(string id, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var livePath = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(livePath) || !File.Exists(livePath))
            throw new InvalidOperationException("Live trunk-recorder config was not found.");

        var warnings = new List<string>();
        var changes = new List<string>();
        var selected = profile.SelectedSourceIndexes.Count > 0
            ? profile.SelectedSourceIndexes.Distinct().Order().ToList()
            : profile.Sources.Select(source => source.Index).Distinct().Order().ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Select at least one source before reviewing the Radio Setup config draft.");
        var liveJson = await File.ReadAllTextAsync(livePath, ct);
        var sourcePlanSystemNames = SourcePlanSystemNames(profile);
        var experiments = await _database.ListRfSurveyExperimentsAsync(id, ct);
        var rfSourceCandidates = BuildRfValidationSourceCandidates(profile, experiments);
        var observedFrequencies = await BuildObservedVoiceFrequenciesBySystemAsync(ct);
        var plannedSystems = AddObservedVoiceFrequencies(
            BuildSourcePlanSystems(profile, sourcePlanSystemNames, experiments, warnings),
            observedFrequencies,
            warnings);
        var controlOnlyPlan = string.Equals(profile.SourcePlanMode, "control", StringComparison.OrdinalIgnoreCase);
        var draftSystems = controlOnlyPlan
            ? plannedSystems.Select(system => system with { VoiceFrequenciesHz = [] }).ToList()
            : plannedSystems;
        var draftSystemNames = draftSystems.Select(system => system.ShortName).ToList();
        var draftRoot = BuildCleanRadioSetupTrConfigRoot();
        var keptSystems = EnsureDraftWorkspaceSystems(draftRoot, draftSystems, draftSystemNames, [], changes, warnings);
        var draftSources = draftRoot["sources"] as JsonArray
            ?? throw new JsonException("Radio Setup draft root does not contain a sources array.");

        var frequencies = plannedSystems
            .SelectMany(system => controlOnlyPlan
                ? system.ControlChannelsHz
                : system.ControlChannelsHz.Concat(system.VoiceFrequenciesHz))
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToList();
        if (controlOnlyPlan)
            warnings.Add("Control-channel source plan selected: Config Draft centers sources for validated control channels only. Voice traffic may be incomplete until full site frequency coverage is planned.");
        if (frequencies.Count == 0)
            warnings.Add("No site frequencies were available; source centers were left unchanged.");

        var defaultRate = selected
            .Select(index => profile.Sources.FirstOrDefault(source => source.Index == index)?.SampleRate ?? ReadInt(draftSources, index, "rate"))
            .Where(rate => rate > 0)
            .DefaultIfEmpty(2_400_000)
            .Max();
        var priorityFrequencies = controlOnlyPlan
            ? plannedSystems
                .SelectMany(system => system.ControlChannelsHz)
                .Where(value => value > 0)
                .Distinct()
                .Order()
                .ToList()
            : [];
        var windows = BuildSourceWindows(frequencies, defaultRate, priorityFrequencies);
        if (windows.Count > selected.Count)
            warnings.Add($"The selected systems need {windows.Count} source window(s) at {defaultRate} sps, but only {selected.Count} source(s) are selected.");

        for (var planIndex = 0; planIndex < selected.Count; planIndex++)
        {
            var sourceIndex = selected[planIndex];
            var source = EnsureSourceObject(draftRoot, sourceIndex);
            var profileSource = profile.Sources.FirstOrDefault(row => row.Index == sourceIndex);
            rfSourceCandidates.TryGetValue(sourceIndex, out var rfCandidate);
            SourceWindow? window = windows.Count == 0
                ? null
                : windows[Math.Min(planIndex, windows.Count - 1)];

            PatchSourceField(source, "device", NormalizeTrSourceDeviceArgs(profile, profileSource), changes, sourceIndex);
            var plannedRate = profileSource?.SampleRate > 0 ? profileSource.SampleRate : defaultRate;
            var runtimeRate = TrRuntimeSampleRate(profile, profileSource, plannedRate);
            PatchSourceField(source, "rate", runtimeRate, changes, sourceIndex);
            if (window != null)
                PatchSourceField(source, "center", window.Value.CenterHz, changes, sourceIndex);
            var error = rfCandidate?.ErrorHz ?? profileSource?.ErrorHz;
            if (error is int errorHz)
                PatchSourceField(source, "error", errorHz, changes, sourceIndex);
            var gain = !string.IsNullOrWhiteSpace(rfCandidate?.Gain) ? rfCandidate.Gain : profileSource?.Gain;
            PatchSourceGainFields(source, profileSource, gain, changes, sourceIndex);
        }
        PatchCallstreamStreams(draftRoot, keptSystems, changes);
        NormalizeRadioSetupTrConfig(draftRoot, changes, _config.TrunkRecorder.TalkgroupsPath);

        var draftJson = NormalizeJson(draftRoot);
        var draftPath = Path.Combine(row.Session.ArtifactPath, "config-draft.json");
        Directory.CreateDirectory(row.Session.ArtifactPath);
        await File.WriteAllTextAsync(draftPath, draftJson, ct);
        await WriteArtifactAsync(row.Session.ArtifactPath, "config-draft-summary.json", new
        {
            selectedSourceIndexes = selected,
            changes,
            warnings,
            draftPath,
            createdAtUtc = DateTime.UtcNow
        }, ct);

        return new RfSurveyConfigDraftDto(
            livePath,
            draftPath,
            draftJson,
            liveJson,
            new RfSurveyConfigDraftSummaryDto(selected, changes, warnings, draftPath));
    }

    public async Task<RfSurveyCandidateDto> GenerateCandidateTrConfigAsync(string id, RfSurveyCandidateRequest request, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var livePath = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(livePath) || !File.Exists(livePath))
            throw new InvalidOperationException("Live trunk-recorder config was not found.");
        var originalText = await File.ReadAllTextAsync(livePath, ct);
        var root = JsonNode.Parse(originalText) as JsonObject ?? throw new InvalidOperationException("TR config root must be an object.");
        var warnings = new List<string>();
        var trialType = NormalizeCandidateTrialType(request.TrialType);
        var summary = PatchCandidate(root, profile, trialType, request, warnings);
        var candidateText = NormalizeJson(root);
        var candidatePath = Path.Combine(row.Session.ArtifactPath, "tr-config-candidate.json");
        var diffLines = BuildSimpleDiff(originalText, candidateText);
        var diffPath = Path.Combine(row.Session.ArtifactPath, "tr-config-candidate.diff.txt");
        await File.WriteAllTextAsync(candidatePath, candidateText, ct);
        await File.WriteAllLinesAsync(diffPath, diffLines, ct);
        var dto = new RfSurveyCandidateDto(trialType, candidatePath, diffPath, summary, diffLines, warnings);
        await WriteArtifactAsync(row.Session.ArtifactPath, "tr-config-candidate-summary.json", dto, ct);
        return dto;
    }

    public async Task<RfSurveyCaptureTrialResultDto> RunCaptureTrialAsync(string id, RfSurveyRunCaptureTrialRequest request, CancellationToken ct)
    {
        if (!request.Confirmed)
            throw new InvalidOperationException("Running a capture trial requires explicit confirmation.");
        var waitSeconds = Math.Clamp(request.DurationSeconds, 10, 3600);
        var apply = await ApplyTempTrConfigAsync(id, new RfSurveyTrActionRequest(true, request.RestartTr), ct);
        await Task.Delay(TimeSpan.FromSeconds(waitSeconds), ct);
        var voice = await RunExperimentAsync(id, new RfSurveyRunExperimentRequest("voice_capture_trial", waitSeconds), ct);
        RfSurveyTrActionResultDto? restore = null;
        if (request.RestoreAfter)
            restore = await RestoreTrConfigAsync(id, new RfSurveyTrActionRequest(true, request.RestartTr), ct);
        return new RfSurveyCaptureTrialResultDto(apply, voice, restore, waitSeconds);
    }

    public async Task<RfSurveyTrActionResultDto> RestoreTrConfigAsync(string id, RfSurveyTrActionRequest request, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        if (!request.Confirmed)
            throw new InvalidOperationException("Restoring TR config requires explicit confirmation.");
        var pointerPath = Path.Combine(row.Session.ArtifactPath, "tr-config-restore-pointer.json");
        if (!File.Exists(pointerPath))
            throw new InvalidOperationException("No Radio Setup TR restore pointer was found.");
        using var pointerDoc = JsonDocument.Parse(await File.ReadAllTextAsync(pointerPath, ct));
        var backupPath = pointerDoc.RootElement.TryGetProperty("backupPath", out var backup) ? backup.GetString() ?? string.Empty : string.Empty;
        var livePath = pointerDoc.RootElement.TryGetProperty("livePath", out var live) ? live.GetString() ?? _config.TrunkRecorder.ConfigPath : _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
            throw new InvalidOperationException("Radio Setup TR backup file was not found.");
        await InstallTrFileAsync(backupPath, livePath, ct);
        var serviceOutput = request.RestartTr ? await RunServiceHelperAsync("restart-tr", ct) : "Restart TR manually before resuming coverage.";
        return new RfSurveyTrActionResultDto(true, "restore-config", "Original TR config was restored from the Radio Setup backup.", "", backupPath, backupPath, serviceOutput);
    }

    public async Task<RfSurveyToolPrepDto> RunToolPrepAsync(string id, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var generatedP25Template = await EnsureP25ProbeTemplateAsync(ct);
        var tools = new List<RfSurveyToolStatusDto>
        {
            await ToolAsync("rtl-sdr", "RTL-SDR tools", "sdr", profile.Sources.Any(IsRtlSource), "rtl_test", "rtl_test -t", "Enumerates and smoke-tests RTL-SDR devices.", "Run SDR prep/install from setup."),
            await ToolAsync("rtl-sdr-capture", "RTL-SDR capture", "sdr", profile.Sources.Any(IsRtlSource), "rtl_sdr", "rtl_sdr 2>&1 | head -20", "Captures short IQ windows for RF power/noise scans.", "Install rtl-sdr."),
            await ToolAsync("airspy", "Airspy tools", "sdr", profile.Sources.Any(IsAirspySource), "airspy_info", "airspy_info", "Enumerates and validates Airspy devices.", "Install airspy host tools for this platform."),
            await ToolAsync("airspy-capture", "Airspy capture", "sdr", profile.Sources.Any(IsAirspySource), "airspy_rx", "airspy_rx -h", "Captures short IQ windows for RF power/noise scans.", "Install airspy host tools for this platform."),
            await P25ToolAsync(),
            await ToolAsync("trunk-recorder", "trunk-recorder", "capture", true, "trunk-recorder", "trunk-recorder --version", "Runs temporary candidate configs and voice capture trials.", "Complete trunk-recorder setup first."),
            await ToolAsync("ffprobe", "ffprobe", "audio", true, "ffprobe", "ffprobe -version", "Inspects captured audio duration and levels.", "Install ffmpeg/ffprobe."),
            TranscriptionTool()
        };
        var warnings = new List<string>();
        if (profile.Mode == "guided" && !_config.AiInsights.Enabled)
            warnings.Add("AI Insights is disabled. Guided Radio Setup requires AI; switch to manual mode or enable AI Insights.");
        if (string.Equals(_config.Transcription.Provider, "none", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Transcription provider is disabled. A survey cannot pass without usable captured-call transcription.");
        if (!tools.Any(t => t.Category == "p25" && t.Installed))
            warnings.Add("No validated P25 control-channel tool was found. Control-channel experiments are blocked until P25 tooling is installed.");
        if (generatedP25Template)
            warnings.Add("Generated the default OP25 rx.py P25 probe command template. Review it if this host uses a nonstandard OP25 install.");

        var prep = new RfSurveyToolPrepDto(
            DateTime.UtcNow,
            (profile.Mode != "guided" || _config.AiInsights.Enabled) && tools.Where(t => t.Required).All(t => t.Installed),
            tools.Any(t => t.Category == "p25" && t.Installed) && tools.Any(t => t.Category == "sdr" && t.Installed),
            tools.First(t => t.Id == "trunk-recorder").Installed,
            !string.Equals(_config.Transcription.Provider, "none", StringComparison.OrdinalIgnoreCase),
            tools,
            warnings);

        var session = row.Session with { Status = "tool_prep", UpdatedAtUtc = DateTime.UtcNow };
        await WriteArtifactAsync(session.ArtifactPath, "tool-prep.json", prep, ct);
        await _database.UpdateRfSurveySessionAsync(
            session,
            row.ProfileJson,
            JsonSerializer.Serialize(prep, EngineConfig.JsonOptions()),
            ct);
        return prep;
    }

    public async Task<RfSurveyNoteDto> AddNoteAsync(string id, string text, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        text = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Note text is required.");
        await _database.AddRfSurveyNoteAsync(id, text, ct);
        var note = new RfSurveyNoteDto(text, DateTime.UtcNow);
        var notes = await _database.ListRfSurveyNotesAsync(id, ct);
        await WriteArtifactAsync(row.Session.ArtifactPath, "notes.json", notes, ct);
        return note;
    }

    public async Task<RfSurveyWaterfallStatusDto> StartWaterfallAsync(string id, RfSurveyWaterfallStartRequest request, CancellationToken ct)
    {
        await StopWaterfallAsync(id, CancellationToken.None);
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var source = SelectWaterfallSource(profile, request);
        if (source == null)
            throw new InvalidOperationException("Waterfall requires a configured SDR source.");
        var isAirspy = IsAirspySource(source);
        var commandName = isAirspy ? "airspy_rx" : "rtl_sdr";
        if (!await CommandExistsAsync(commandName, ct))
            throw new InvalidOperationException($"{commandName} was not found on this host.");

        var requestedSampleRate = Math.Clamp(request.SampleRateHz ?? (isAirspy ? 6_000_000 : source.SampleRate), 240_000, 10_000_000);
        var sampleRate = isAirspy ? TrRuntimeSampleRate(profile, source, requestedSampleRate) : requestedSampleRate;
        var centerHz = request.FrequencyHz is > 0 ? request.FrequencyHz.Value : profile.ControlChannelsHz.FirstOrDefault();
        if (centerHz <= 0)
            centerHz = source.CenterHz > 0 ? source.CenterHz : 0;
        if (centerHz <= 0)
            throw new InvalidOperationException("Waterfall requires a frequency or known control channel.");
        var requestedGain = string.IsNullOrWhiteSpace(request.Gain) ? source.Gain : request.Gain!.Trim();
        if (isAirspy && string.IsNullOrWhiteSpace(requestedGain))
            requestedGain = "15";
        if (isAirspy && !IsValidAirspyLinearityGain(requestedGain))
            throw new InvalidOperationException("Airspy waterfall uses linearity gain 0-21.");
        var gain = NormalizeAirspyRxGain(isAirspy, requestedGain);
        var binCount = NormalizeWaterfallBinCount(request.BinCount <= 0 ? 1024 : request.BinCount);
        var captureMs = Math.Clamp(request.CaptureMilliseconds <= 0 ? 60 : request.CaptureMilliseconds, 40, 1000);
        var refreshMs = Math.Clamp(request.RefreshMilliseconds <= 0 ? 300 : request.RefreshMilliseconds, 100, 5000);

        var trState = await QueryTrActiveAsync(ct);
        var trWasActive = trState.Active;
        var trStopOutput = string.Empty;
        if (trState.Active)
        {
            trStopOutput = await RunServiceHelperAsync("stop-tr", ct);
            trState = await QueryTrActiveAsync(ct);
            if (trState.Active)
                throw new InvalidOperationException("Waterfall needs exclusive SDR access, but trunk-recorder remained active after the stop request.");
            if (isAirspy)
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
        }

        var runtime = new WaterfallRuntime(
            id,
            profile,
            source with { Gain = gain },
            centerHz,
            sampleRate,
            binCount,
            captureMs,
            refreshMs,
            trWasActive,
            trStopOutput);
        _activeWaterfalls[id] = runtime;
        runtime.Task = Task.Run(() => RunWaterfallLoopAsync(runtime), CancellationToken.None);
        RememberWaterfall(runtime, active: true);
        return runtime.ToDto(true);
    }

    public async Task<RfSurveyWaterfallStatusDto> GetWaterfallAsync(string id, bool includeHistory, CancellationToken ct)
    {
        if (_activeWaterfalls.TryGetValue(id, out var runtime))
        {
            runtime.MarkConsumed();
            return runtime.ToDto(!runtime.Cancellation.IsCancellationRequested && runtime.Task?.IsCompleted != true, includeHistory);
        }
        if (_lastWaterfalls.TryGetValue(id, out var last))
            return includeHistory ? last : last with { Frames = null };
        if (includeHistory)
        {
            var persisted = await LoadPersistedWaterfallAsync(id, ct);
            if (persisted != null)
            {
                _lastWaterfalls[id] = persisted;
                return persisted;
            }
        }
        return new RfSurveyWaterfallStatusDto(false, "stopped", "Waterfall is stopped.", 0, "", 0, 0, "", 0, null, null, null, false);
    }

    public async Task<string> StopActiveWaterfallsBeforeTrStartAsync(CancellationToken ct)
    {
        var active = _activeWaterfalls.Keys.ToArray();
        if (active.Length == 0)
            return string.Empty;

        var lines = new List<string>();
        foreach (var waterfallId in active)
        {
            if (!_activeWaterfalls.ContainsKey(waterfallId))
                continue;
            var result = await StopWaterfallAsync(waterfallId, ct);
            lines.Add($"Stopped active waterfall {waterfallId} before starting trunk-recorder: {result.Status}.");
        }
        return string.Join(Environment.NewLine, lines);
    }

    public async Task<RfSurveyWaterfallStatusDto> StopWaterfallAsync(string id, CancellationToken ct)
    {
        if (!_activeWaterfalls.TryRemove(id, out var runtime))
        {
            if (_lastWaterfalls.TryGetValue(id, out var last))
                return last with { Frames = null };
            return new RfSurveyWaterfallStatusDto(false, "stopped", "Waterfall is stopped.", 0, "", 0, 0, "", 0, null, null, null, false);
        }

        runtime.Cancellation.Cancel();
        if (runtime.Task != null)
        {
            var completed = await Task.WhenAny(runtime.Task, Task.Delay(TimeSpan.FromSeconds(8), ct));
            if (completed == runtime.Task)
            {
                try { await runtime.Task; } catch { }
            }
        }
        runtime.Cancellation.Dispose();
        var stopped = runtime.ToDto(false, true) with { Status = "stopped", Message = "Waterfall stopped." };
        _lastWaterfalls[id] = stopped;
        await PersistWaterfallAsync(id, stopped, ct);
        return stopped;
    }

    private async Task RunWaterfallLoopAsync(WaterfallRuntime runtime)
    {
        try
        {
            var source = runtime.Source;
            var isAirspy = IsAirspySource(source);
            var pinAirspySerial = runtime.Profile.Sources.Count(IsAirspySource) > 1;
            var rtlIndexesBySerial = isAirspy
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : await ReadRtlIndexesBySerialAsync(runtime.Cancellation.Token);
            var rtlSelector = isAirspy ? new RtlDeviceSelector("", "") : ResolveRtlDeviceSelector(source, rtlIndexesBySerial);
            if (!string.IsNullOrWhiteSpace(rtlSelector.Issue))
            {
                runtime.SetMessage("blocked", rtlSelector.Issue);
                return;
            }

            await RunWaterfallStreamAsync(runtime, isAirspy, rtlSelector, pinAirspySerial);
        }
        catch (OperationCanceledException)
        {
            runtime.SetMessage("stopping", "Stopping waterfall.");
        }
        catch (Exception ex)
        {
            runtime.SetMessage("failed", ex.Message);
            _logger.LogError(ex, "Waterfall loop failed for survey {SurveyId}.", runtime.SurveyId);
        }
        finally
        {
            if (runtime.TrWasActive)
            {
                try
                {
                    runtime.TrRestartOutput = await RunServiceHelperAsync("start-tr", CancellationToken.None, stopWaterfallsFirst: false);
                }
                catch (Exception ex)
                {
                    runtime.TrRestartError = ex.Message;
                    _logger.LogError(ex, "Waterfall failed to restart trunk-recorder for survey {SurveyId}.", runtime.SurveyId);
                }
            }
            if (_activeWaterfalls.TryGetValue(runtime.SurveyId, out var current) && ReferenceEquals(current, runtime))
                _activeWaterfalls.TryRemove(runtime.SurveyId, out _);
            await RememberWaterfallAsync(runtime, active: false, CancellationToken.None);
        }
    }

    private void RememberWaterfall(WaterfallRuntime runtime, bool active)
    {
        _lastWaterfalls[runtime.SurveyId] = runtime.ToDto(active, includeHistory: true);
    }

    private async Task RememberWaterfallAsync(WaterfallRuntime runtime, bool active, CancellationToken ct)
    {
        var dto = runtime.ToDto(active, includeHistory: true);
        _lastWaterfalls[runtime.SurveyId] = dto;
        await PersistWaterfallAsync(runtime.SurveyId, dto, ct);
    }

    private async Task PersistWaterfallAsync(string id, RfSurveyWaterfallStatusDto status, CancellationToken ct)
    {
        if (status.Frame == null && status.Frames?.Count is not > 0)
            return;
        try
        {
            var row = await _database.GetRfSurveySessionAsync(id, ct);
            if (row == null)
                return;
            await WriteArtifactAsync(row.Value.Session.ArtifactPath, "waterfall-last.json", status, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to persist waterfall snapshot for survey {SurveyId}.", id);
        }
    }

    private async Task<RfSurveyWaterfallStatusDto?> LoadPersistedWaterfallAsync(string id, CancellationToken ct)
    {
        try
        {
            var row = await _database.GetRfSurveySessionAsync(id, ct);
            if (row == null)
                return null;
            var path = Path.Combine(row.Value.Session.ArtifactPath, "waterfall-last.json");
            if (!File.Exists(path))
                return null;
            var status = JsonSerializer.Deserialize<RfSurveyWaterfallStatusDto>(
                await File.ReadAllTextAsync(path, ct),
                EngineConfig.JsonOptions());
            return status?.Frame == null && status?.Frames?.Count is not > 0
                ? null
                : status with { Active = false, Status = "stopped" };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to load persisted waterfall snapshot for survey {SurveyId}.", id);
            return null;
        }
    }

    public async Task<RfSurveyExperimentDto> RunExperimentAsync(string id, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        var row = await _database.GetRfSurveySessionAsync(id, ct) ?? throw new KeyNotFoundException("Radio setup workspace was not found.");
        var profile = await RecoverProfileSourcesFromSavedTrConfigAsync(
            row.Session,
            NormalizeStoredProfileForWorkflow(DeserializeOrDefault<RfSurveyProfileDto>(row.ProfileJson) ?? new RfSurveyProfileDto()),
            ct);
        var sessionForRun = NormalizeAppliedSourcePlanSession(row.Session);
        var profileJson = JsonSerializer.Serialize(profile, EngineConfig.JsonOptions());
        var toolPrep = DeserializeOrDefault<RfSurveyToolPrepDto>(row.ToolPrepJson) ?? EmptyToolPrep();
        var started = DateTime.UtcNow;
        var type = NormalizeExperimentType(request.Type);
        SetupSdrDetectionDto? sdrDetection = null;
        var cancellable = type is "rf_power_scan" or "rf_validation_sweep" or "control_channel_p25_probe" or "voice_capture_trial";
        CancellationTokenSource? linkedRunCancellation = null;
        var runCt = ct;
        if (cancellable)
        {
            linkedRunCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeExperimentCancellations.AddOrUpdate(id, linkedRunCancellation, (_, existing) =>
            {
                try { existing.Cancel(); existing.Dispose(); } catch { }
                return linkedRunCancellation;
            });
            runCt = linkedRunCancellation.Token;
        }

        try
        {
            ExperimentOutcome outcome;
            try
            {
                if (type == "sdr_inventory")
                {
                    sdrDetection = await _jobs.DetectSdrsAsync(runCt);
                }

                outcome = type switch
                {
                    "ground_truth_review" => await RunGroundTruthReviewAsync(profile, runCt),
                    "tr_stopped_check" => await RunTrStoppedCheckAsync(runCt),
                    "sdr_inventory" => await RunSdrInventoryAsync(profile, toolPrep, sdrDetection, runCt),
                    "rf_power_scan" => await RunRfPowerScanAsync(sessionForRun.ArtifactPath, profile, request, runCt),
                    "rf_validation_sweep" => await RunRfValidationSweepAsync(sessionForRun, profile, toolPrep, request, runCt),
                    "control_channel_quality" => await RunControlChannelQualityAsync(sessionForRun, profile, request, runCt),
                    "control_channel_p25_probe" => await RunControlChannelProbeAsync(sessionForRun.ArtifactPath, profile, toolPrep, request, runCt),
                    "error_gain_sweep" => await RunErrorGainSweepAsync(sessionForRun.ArtifactPath, profile, request, runCt),
                    "error_gain_sweep_cancel" => await RunErrorGainSweepCancelAsync(sessionForRun.ArtifactPath, runCt),
                    "temp_tr_config_plan" => await RunTempTrConfigPlanAsync(sessionForRun.ArtifactPath, profile, runCt),
                    "voice_capture_trial" => await RunVoiceCaptureTrialAsync(sessionForRun, profile, request, runCt),
                    "transcription_gate" => await RunTranscriptionGateAsync(sessionForRun, profile, request, runCt),
                    "stability_verdict" => await RunStabilityVerdictAsync(sessionForRun, profile, request, runCt),
                    _ => throw new InvalidOperationException("Unsupported Radio Setup experiment type.")
                };
            }
            catch (OperationCanceledException) when (cancellable && runCt.IsCancellationRequested)
            {
                var displayType = type.Replace('_', ' ');
                outcome = new ExperimentOutcome(
                    "canceled",
                    $"{displayType} was canceled before all planned checks completed.",
                    "Cancellation requested from Radio Setup.",
                    $"{displayType} was canceled.",
                    "",
                    new { canceled = true, type, artifactPath = sessionForRun.ArtifactPath },
                    new { recommendation = "Review the partial RF Sweep result rows, adjust hardware if needed, and rerun when ready." });
            }

            var experiment = new RfSurveyExperimentDto
            {
                Id = $"rfx-{started:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..30],
                Type = type,
                Status = outcome.Status,
                Hypothesis = outcome.Hypothesis,
                RequiredSetup = outcome.RequiredSetup,
                ResultSummary = outcome.ResultSummary,
                BlockingIssue = outcome.BlockingIssue,
                EvidenceJson = JsonSerializer.Serialize(outcome.Evidence, EngineConfig.JsonOptions()),
                InterpretationJson = JsonSerializer.Serialize(outcome.Interpretation, EngineConfig.JsonOptions()),
                CreatedAtUtc = started,
                StartedAtUtc = started,
                FinishedAtUtc = outcome.Status == "running" ? null : DateTime.UtcNow
            };

            var persistCt = ct.IsCancellationRequested ? CancellationToken.None : ct;
            await _database.AddRfSurveyExperimentAsync(id, experiment, experiment.EvidenceJson, experiment.InterpretationJson, persistCt);
            await WriteArtifactAsync(sessionForRun.ArtifactPath, $"experiment-{experiment.Id}.json", experiment, persistCt);
            var experiments = await _database.ListRfSurveyExperimentsAsync(id, persistCt);
            await WriteArtifactAsync(sessionForRun.ArtifactPath, "experiments.json", experiments, persistCt);

            var session = UpdateSessionFromExperiment(sessionForRun, experiment) with { SdrSummary = SummarizeSelectedSdrs(profile) };
            await _database.UpdateRfSurveySessionAsync(session, profileJson, row.ToolPrepJson, persistCt);
            await WriteArtifactAsync(sessionForRun.ArtifactPath, "survey.json", session, persistCt);
            return experiment;
        }
        finally
        {
            if (cancellable && linkedRunCancellation != null &&
                _activeExperimentCancellations.TryGetValue(id, out var currentCancellation) &&
                ReferenceEquals(currentCancellation, linkedRunCancellation) &&
                _activeExperimentCancellations.TryRemove(id, out var removedCancellation))
                removedCancellation.Dispose();
        }
    }

    public async Task<RfSurveyCancelExperimentResultDto> CancelActiveExperimentAsync(string id, CancellationToken ct)
    {
        var canceled = false;
        if (_activeExperimentCancellations.TryGetValue(id, out var active))
        {
            active.Cancel();
            canceled = true;
        }
        var cleanup = await CleanupStaleP25ProcessesAsync(ct);
        var message = canceled
            ? "Cancel requested for the active Radio Setup experiment."
            : "No active cancellable Radio Setup experiment was registered; stale P25 cleanup was still checked.";
        if (!string.IsNullOrWhiteSpace(cleanup.BlockingIssue))
            message += " " + cleanup.BlockingIssue;
        return new RfSurveyCancelExperimentResultDto(canceled, message, cleanup.Output);
    }

    private IReadOnlyList<RfSurveyExperimentPlanDto> PlanNextExperiments(
        RfSurveySessionDto session,
        RfSurveyProfileDto profile,
        RfSurveyToolPrepDto? toolPrep,
        IReadOnlyList<RfSurveyExperimentDto> experiments)
    {
        var completed = experiments
            .GroupBy(e => e.Type, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.OrderBy(e => e.CreatedAtUtc).LastOrDefault()?.Status == "passed")
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var p25Ready = toolPrep?.ReadyForControlChannelTests == true;
        var voiceReady = toolPrep?.ReadyForVoiceCapture == true;
        var p25ProbeConfigured = !string.IsNullOrWhiteSpace(_config.RfSurvey.P25ProbeCommandTemplate);
        var trCoverage = CurrentTrSourceCoverageValidation();
        var trCoverageReady = trCoverage?.Ok != false;
        var combinedSweepReady = p25ProbeConfigured && trCoverageReady;
        var combinedSweepBlocker = !p25ProbeConfigured
            ? "RF Sweep is blocked until the Radio Setup P25 probe command template is configured."
            : !trCoverageReady
                ? "RF Sweep is blocked until the TR config source plan can cover the selected control channels: " + string.Join(" ", trCoverage?.Blockers ?? [])
                : "";
        var plans = new List<RfSurveyExperimentPlanDto>();
        if (toolPrep == null || toolPrep.Tools.Count == 0)
            plans.Add(new("tool_prep", "Run tool prep", "Detect required SDR, P25, capture, audio, and transcription tooling.", true, "", "Run Tool Prep."));
        if (!completed.Contains("ground_truth_review"))
            plans.Add(new("ground_truth_review", "Review ground truth", "Verify the setup-derived control channels, voice frequencies, SDR sources, and RF path facts before RF tests.", true, "", "Completed setup wizard or imported TR/RR data."));
        if (!completed.Contains("tr_stopped_check"))
            plans.Add(new("tr_stopped_check", "Check TR service state", "Radio Setup needs to know whether trunk-recorder is active before bounded SDR/P25 measurements.", true, "", "Radio Setup will pause and restart trunk-recorder automatically when exclusive SDR access is required."));
        if (!completed.Contains("sdr_inventory"))
            plans.Add(new("sdr_inventory", "Inventory SDRs", "Run the installed Airspy/RTL-SDR inventory tools and capture factual device output.", voiceReady || toolPrep?.Tools.Any(t => t.Category == "sdr" && t.Installed) == true, voiceReady ? "" : "Install SDR tools so the SDR can be claimed during a bounded TR pause.", "Radio Setup pauses TR if needed, runs rtl_test and/or airspy_info, then restarts TR."));
        var combinedRfPassed = completed.Contains("rf_validation_sweep");
        if (!combinedRfPassed && !completed.Contains("rf_power_scan"))
            plans.Add(new("rf_power_scan", "Measure RF power", "Capture a short IQ window at the selected control channel and estimate peak power, noise floor, SNR, overload risk, and frequency offset.", true, "", "Radio Setup pauses TR if needed, runs rtl_sdr or airspy_rx, then restarts TR."));
        if (!completed.Contains("rf_validation_sweep"))
            plans.Add(new("rf_validation_sweep", "Run RF validation sweep", "Rank control-channel/source/gain/error candidates with a short RF screen, P25 probe, and TR CC metrics on only the best candidates.", combinedSweepReady, combinedSweepBlocker, "Known control channels, selected SDR source, configured P25 probe command, valid TR source coverage, and trunk-recorder service control."));
        if (!combinedRfPassed && !completed.Contains("control_channel_quality"))
            plans.Add(new("control_channel_quality", "Measure CC quality", "Measure per-control-channel decode rate, zero-decode, continuity, retunes, and call events from a fresh TR journal window.", trCoverageReady, trCoverageReady ? "" : "TR CC Metrics are blocked until the TR config source plan can cover the selected control channels: " + string.Join(" ", trCoverage?.Blockers ?? []), "trunk-recorder running on the current TR config; known control channel list."));
        if (!combinedRfPassed && !completed.Contains("control_channel_p25_probe"))
            plans.Add(new("control_channel_p25_probe", "Probe P25 control channel", "Capture control-channel evidence with validated P25 tooling before voice-call trials.", p25Ready, p25Ready ? "" : "P25 probe is blocked until P25 and SDR tooling are installed.", "Radio Setup pauses TR if needed, runs the validated OP25/P25 command, then restarts TR."));
        if (!completed.Contains("error_gain_sweep"))
            plans.Add(new("error_gain_sweep", "Run error/gain sweep", "Run controlled tr_tune measurements through the Radio Setup experiment API.", true, "", "The sweep runner manages temporary TR configs and restarts TR after cleanup."));
        var latestP25 = experiments
            .Where(e => e.Type == "control_channel_p25_probe")
            .OrderBy(e => e.CreatedAtUtc)
            .LastOrDefault();
        var sourcePlanRfPassed = SourcePlanRfValidationPassed(profile, experiments);
        var p25Passed = combinedRfPassed || sourcePlanRfPassed || latestP25?.Status == "passed";
        var sourcePlanApplied = !string.IsNullOrWhiteSpace(session.SourcePlanSummary) ||
                                string.Equals(session.Status, "source_plan_applied", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(session.Status, "completed", StringComparison.OrdinalIgnoreCase);
        if (!completed.Contains("temp_tr_config_plan"))
            plans.Add(new("temp_tr_config_plan", "Prepare temp TR config", "Create an artifact-only plan for the temporary TR config/run; this does not overwrite live TR config.", true, "", "Known control channel/source facts."));
        if (!completed.Contains("voice_capture_trial"))
            plans.Add(new("voice_capture_trial", "Check voice capture", "Optionally prove real call audio against the applied Config Draft source plan; healthy TR metrics may carry the setup forward with a no-traffic caveat.", p25Passed && sourcePlanApplied, !p25Passed ? "Voice capture is blocked until RF Sweep/P25 validation passes." : !sourcePlanApplied ? "Apply the Config Draft source plan before Call Quality." : "", "Applied Config Draft source plan is running in trunk-recorder."));
        var latestVoice = experiments
            .Where(e => e.Type == "voice_capture_trial")
            .OrderBy(e => e.CreatedAtUtc)
            .LastOrDefault();
        var voicePassed = latestVoice?.Status == "passed";
        if (!completed.Contains("transcription_gate"))
            plans.Add(new("transcription_gate", "Check transcription gate", "Test transcription when real call audio exists; skip with a caveat when no calls occurred and TR metrics were healthy.", voicePassed, voicePassed ? "" : "Transcription gate is blocked until voice capture passes or produces a no-traffic caveat.", "Captured calls have completed transcription, or no calls occurred while TR metrics stayed healthy."));
        var latestTranscription = experiments
            .Where(e => e.Type == "transcription_gate")
            .OrderBy(e => e.CreatedAtUtc)
            .LastOrDefault();
        var transcriptionPassed = latestTranscription?.Status == "passed";
        if (!completed.Contains("stability_verdict"))
            plans.Add(new("stability_verdict", "Compute stability verdict", "Account for decode persistence and call evidence when traffic is available.", transcriptionPassed, transcriptionPassed ? "" : "Stability verdict is blocked until transcription gate passes or records a no-traffic caveat.", "At least several minutes of captured-call evidence, or healthy TR metrics with no observed calls."));
        return plans;
    }

    private async Task<ExperimentOutcome> RunGroundTruthReviewAsync(RfSurveyProfileDto profile, CancellationToken ct)
    {
        await Task.CompletedTask.WaitAsync(ct);
        var issues = new List<string>();
        if (profile.ControlChannelsHz.Count == 0) issues.Add("No control channels are available from setup/TR ground truth.");
        if (profile.VoiceFrequenciesHz.Count == 0) issues.Add("No voice frequencies are available from setup/TR ground truth.");
        if (profile.Sources.Count == 0) issues.Add("No SDR sources are configured.");
        if (string.Equals(profile.GroundTruthSource, "unknown", StringComparison.OrdinalIgnoreCase)) issues.Add("Ground-truth source is unknown; RadioReference/imported data should be used when possible.");
        var status = issues.Count == 0 ? "passed" : "failed";
        return new ExperimentOutcome(
            status,
            "Setup-derived ground truth should identify the system, control channels, voice frequencies, and configured SDR sources.",
            "Completed setup wizard, TR config, and preferably RR/imported source data.",
            issues.Count == 0 ? $"Ground truth looks usable: {profile.ControlChannelsHz.Count} control channel(s), {profile.VoiceFrequenciesHz.Count} voice frequenc(ies), {profile.Sources.Count} SDR source(s)." : string.Join(" ", issues),
            issues.Count == 0 ? "" : string.Join(" ", issues),
            new
            {
                profile.SystemShortName,
                profile.GroundTruthSource,
                controlChannelsHz = profile.ControlChannelsHz,
                voiceFrequencyCount = profile.VoiceFrequenciesHz.Count,
                sourceCount = profile.Sources.Count,
                rfPath = profile.RfPath
            },
            new
            {
                recommendation = issues.Count == 0
                    ? "Proceed to TR service state check and SDR inventory."
                    : "Fix setup/RR ground truth before treating Radio Setup measurements as conclusive.",
                followUps = issues
            });
    }

    private async Task<ExperimentOutcome> RunTrStoppedCheckAsync(CancellationToken ct)
    {
        var service = TrUnitName();
        var result = OperatingSystem.IsWindows()
            ? (ExitCode: -1, Stdout: "systemctl is not available on Windows.")
            : await RunCaptureAsync("systemctl", $"is-active {service}", ct);
        var active = result.Stdout.Trim().Equals("active", StringComparison.OrdinalIgnoreCase);
        var unknown = result.ExitCode < 0 || result.Stdout.Contains("not available", StringComparison.OrdinalIgnoreCase);
        var status = unknown ? "failed" : "passed";
        var blocking = unknown ? "Unable to query trunk-recorder service state on this host." : string.Empty;
        return new ExperimentOutcome(
            status,
            "Radio Setup should know whether trunk-recorder is active before scoped SDR/P25 measurements.",
            $"Check systemd unit {service}.",
            unknown ? blocking : active ? "trunk-recorder is active; SDR/P25 experiments will pause and restart it automatically." : $"trunk-recorder is not active ({result.Stdout.Trim()}).",
            blocking,
            new { service, result.ExitCode, output = TrimOutput(result.Stdout) },
            new
            {
                recommendation = unknown ? "Run this survey on the target Linux host or use expert mode with external evidence." : "Proceed to SDR inventory; Radio Setup will manage short TR pauses.",
                trMustRemainStopped = false
            });
    }

    private async Task<ExperimentOutcome> RunSdrInventoryAsync(RfSurveyProfileDto profile, RfSurveyToolPrepDto toolPrep, SetupSdrDetectionDto? detection, CancellationToken ct)
    {
        var trState = await QueryTrActiveAsync(ct);
        var trWasActive = trState.Active;
        var trStopOutput = string.Empty;
        var trRestartOutput = string.Empty;
        var trRestartError = string.Empty;
        if (trState.Active)
        {
            trStopOutput = await RunServiceHelperAsync("stop-tr", ct);
            trState = await QueryTrActiveAsync(ct);
            if (trState.Active)
            {
                return BlockedOutcome(
                    "sdr_inventory",
                    "SDR inventory needs exclusive access to the configured Airspy/RTL-SDR devices.",
                    "Radio Setup can pause trunk-recorder through the service helper.",
                    "trunk-recorder remained active after the stop request.",
                    new { trState, trStopOutput });
            }
        }

        var outputs = new List<object>();
        try
        {
            if (profile.Sources.Any(IsRtlSource))
            {
                if (!await CommandExistsAsync("rtl_test", ct))
                    outputs.Add(new { tool = "rtl_test", status = "missing", output = "" });
                else
                {
                    var result = await RunCaptureAsync("bash", "-lc \"timeout 10 rtl_test -t 2>&1 || true\"", ct);
                    outputs.Add(new { tool = "rtl_test", status = result.ExitCode == 0 ? "ran" : "error", output = TrimOutput(result.Stdout) });
                }
            }
            if (profile.Sources.Any(IsAirspySource))
            {
                if (!await CommandExistsAsync("airspy_info", ct))
                    outputs.Add(new { tool = "airspy_info", status = "missing", output = "" });
                else
                {
                    var result = await RunCaptureAsync("bash", "-lc \"timeout 10 airspy_info 2>&1 || true\"", ct);
                    outputs.Add(new { tool = "airspy_info", status = result.ExitCode == 0 ? "ran" : "error", output = TrimOutput(result.Stdout) });
                }
            }
        }
        finally
        {
            if (trWasActive)
            {
                try
                {
                    trRestartOutput = await RunServiceHelperAsync("start-tr", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    trRestartError = ex.Message;
                    _logger.LogError(ex, "SDR inventory failed to restart trunk-recorder after pausing it.");
                }
            }
        }

        if (outputs.Count == 0)
            return BlockedOutcome("sdr_inventory", "The survey needs at least one configured SDR source.", "Complete setup/TR source configuration.", "No SDR sources are configured.", new { sourceCount = profile.Sources.Count });

        var outputText = JsonSerializer.Serialize(outputs, EngineConfig.JsonOptions());
        var missing = outputText.Contains("\"missing\"", StringComparison.OrdinalIgnoreCase);
        var visible = !missing && ConfiguredSdrClassVisible(profile.Sources, outputText);
        var restartFailed = !string.IsNullOrWhiteSpace(trRestartError);
        var serialNotes = BuildSdrInventorySerialNotes(profile.Sources, outputText);
        return new ExperimentOutcome(
            restartFailed ? "failed" : missing ? "blocked" : visible ? "passed" : "failed",
            "Configured SDR devices should be visible to host tools while TR is stopped.",
            "Radio Setup temporarily paused TR if needed; configured SDR host tools installed.",
            restartFailed ? "SDR inventory ran, but trunk-recorder did not restart afterward." : missing ? "One or more configured SDR toolchains are missing." : visible ? "SDR inventory found compatible hardware." : "SDR inventory did not find compatible hardware.",
            restartFailed ? $"trunk-recorder did not restart after SDR inventory: {trRestartError}" : missing ? "Install the missing SDR host tools before RF measurements." : "",
            new { trState, trWasActive, trStopOutput, trRestartOutput, trRestartError, configuredSources = profile.Sources, serialNotes, detection, outputs },
            new
            {
                recommendation = restartFailed ? "Restart trunk-recorder before continuing Radio Setup." : missing ? "Run tool prep/install for missing SDR host tools." : visible ? "Proceed to RF power scan before P25 probing." : "Check USB, permissions, drivers, and whether another process is using the SDR.",
                followUps = missing ? new[] { "Install missing SDR toolchain.", "Re-run Tool Prep.", "Re-run SDR inventory." } : Array.Empty<string>()
            });
    }

    private static bool ConfiguredSdrClassVisible(IReadOnlyList<RfSurveySourceDto> sources, string outputText)
    {
        if (sources.Count == 0)
            return false;
        var needsAirspy = sources.Any(IsAirspySource);
        var needsRtl = sources.Any(IsRtlSource);
        var airspyOk = !needsAirspy || outputText.Contains("AirSpy", StringComparison.OrdinalIgnoreCase) || outputText.Contains("Airspy", StringComparison.OrdinalIgnoreCase);
        var rtlOk = !needsRtl || outputText.Contains("RTL", StringComparison.OrdinalIgnoreCase) || outputText.Contains("Found ", StringComparison.OrdinalIgnoreCase);
        return airspyOk && rtlOk;
    }

    private static IReadOnlyList<string> BuildSdrInventorySerialNotes(IReadOnlyList<RfSurveySourceDto> sources, string outputText)
    {
        var normalizedOutput = NormalizeInventorySerialText(outputText);
        return sources
            .Select(source => FirstNonEmpty(source.Serial, ExtractAirspySerial(source.Device), ExtractRtlSerial(source.Device)))
            .Where(serial => !string.IsNullOrWhiteSpace(serial) && !normalizedOutput.Contains(NormalizeInventorySerialText(serial), StringComparison.OrdinalIgnoreCase))
            .Select(serial => $"Saved source serial {serial} was not reported by inventory; Radio Setup will bind by SDR type for single-device workflows.")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeInventorySerialText(string value) =>
        Regex.Replace(value ?? string.Empty, @"[^0-9a-fA-F]", string.Empty)
            .TrimStart('0');

    private static bool OutputShowsVisibleSdr(object output)
    {
        var json = JsonSerializer.Serialize(output, EngineConfig.JsonOptions());
        return json.Contains("Found AirSpy board", StringComparison.OrdinalIgnoreCase)
            || json.Contains("Serial Number:", StringComparison.OrdinalIgnoreCase)
            || json.Contains("Found 1 device", StringComparison.OrdinalIgnoreCase)
            || json.Contains("Found 2 device", StringComparison.OrdinalIgnoreCase)
            || json.Contains("Found 3 device", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ExperimentOutcome> RunRfPowerScanAsync(string artifactPath, RfSurveyProfileDto profile, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        var controlChannels = ReadPowerScanControlChannels(request.Parameters, request.ControlChannelHz, profile.ControlChannelsHz);
        var firstControlChannel = controlChannels.FirstOrDefault();
        if (firstControlChannel <= 0)
            return new ExperimentOutcome(
                "failed",
                "RF power scan requires a known control channel.",
                "Import/confirm RR or TR ground truth first.",
                "No control channel is available.",
                "No control channel is available.",
                new { profile.ControlChannelsHz },
                new { recommendation = "Select or import site ground truth before running RF Sweep.", followUps = new[] { "Select the site again.", "Refresh TR/RR ground truth.", "Confirm the active TR config has control_channels." } });

        var selectedSourceIndexes = request.SourceIndex.HasValue
            ? new[] { request.SourceIndex.Value }
            : profile.SelectedSourceIndexes.Count > 0 ? profile.SelectedSourceIndexes : profile.Sources.Select(s => s.Index).ToList();
        var sources = profile.Sources
            .Where(source => selectedSourceIndexes.Contains(source.Index))
            .DefaultIfEmpty(SelectSourceForFrequency(profile, firstControlChannel))
            .Where(source => source != null)
            .Cast<RfSurveySourceDto>()
            .ToList();
        if (sources.Count == 0)
            return new ExperimentOutcome(
                "failed",
                "RF power scan requires at least one configured SDR source.",
                "Complete setup/TR source configuration.",
                "No SDR source is available.",
                "No SDR source is available.",
                new { profile.Sources },
                new { recommendation = "Select or add an SDR source before running RF Sweep.", followUps = new[] { "Return to Scope and select SDRs.", "Review the active TR source list.", "Run SDR inventory after sources are selected." } });

        var duration = Math.Clamp(request.DurationSeconds <= 0 ? 5 : request.DurationSeconds, 2, 15);
        var outputDir = Path.Combine(artifactPath, "rf-power-scans", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(outputDir);
        var rows = new List<RfPowerScanRow>();
        var gainOverrides = ReadPowerScanGainOverrides(request.Parameters);
        var gainSequence = ReadPowerScanGainSequence(request.Parameters);

        var trState = await QueryTrActiveAsync(ct);
        var trWasActive = trState.Active;
        var trStopOutput = string.Empty;
        var trRestartOutput = string.Empty;
        var trRestartError = string.Empty;
        if (trState.Active)
        {
            trStopOutput = await RunServiceHelperAsync("stop-tr", ct);
            trState = await QueryTrActiveAsync(ct);
            if (trState.Active)
                return new ExperimentOutcome(
                    "failed",
                    "RF Sweep should automatically pause trunk-recorder before claiming SDR hardware.",
                    "Radio Setup can pause trunk-recorder through the service helper.",
                    "trunk-recorder remained active after the stop request.",
                    "trunk-recorder remained active after the stop request.",
                    new { trState, trStopOutput },
                    new { recommendation = "Review service permissions/helper output, then rerun RF Sweep.", followUps = new[] { "Check pizzawave setup helper permissions.", "Use System > Services to restart trunk-recorder if service control is unavailable.", "Rerun RF Sweep." } });
            if (sources.Any(IsAirspySource))
                await Task.Delay(TimeSpan.FromSeconds(6), ct);
        }

        try
        {
            var pinAirspySerial = sources.Count(IsAirspySource) > 1;
            var rtlIndexesBySerial = sources.Any(source => !IsAirspySource(source))
                ? await ReadRtlIndexesBySerialAsync(ct)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var controlChannel in controlChannels)
            {
                foreach (var source in sources)
                {
                    var sourceGainSequence = PowerScanGainSequenceForSource(source, gainSequence, gainOverrides);
                    foreach (var gain in sourceGainSequence)
                    {
                        var scanSource = source with { Gain = gain };
                        var isAirspy = IsAirspySource(scanSource);
                        var commandName = isAirspy ? "airspy_rx" : "rtl_sdr";
                        if (!await CommandExistsAsync(commandName, ct))
                        {
                            rows.Add(new RfPowerScanRow(scanSource.Index, scanSource.SdrType, scanSource.Serial, scanSource.Device, scanSource.CenterHz, scanSource.SampleRate, scanSource.SampleRate, scanSource.ErrorHz, scanSource.Gain, controlChannel, duration, commandName, -1, "", "", 0, "unavailable", $"{commandName} was not found.", null, null, null, null, 0, false, "", null, null));
                            continue;
                        }

                        var requestedSampleRate = Math.Clamp(scanSource.SampleRate <= 0 ? 2_048_000 : scanSource.SampleRate, 240_000, 10_000_000);
                        var sampleRate = isAirspy ? TrRuntimeSampleRate(profile, scanSource, requestedSampleRate) : requestedSampleRate;
                        var sampleCount = Math.Min(sampleRate * duration, sampleRate * 8);
                        var extension = isAirspy ? "cs16" : "u8";
                        var rawPath = Path.Combine(outputDir, $"source-{scanSource.Index}-{controlChannel}-gain-{SanitizeFileToken(scanSource.Gain)}.{extension}");
                        var rtlSelector = isAirspy ? new RtlDeviceSelector("", "") : ResolveRtlDeviceSelector(scanSource, rtlIndexesBySerial);
                        if (!string.IsNullOrWhiteSpace(rtlSelector.Issue))
                        {
                            rows.Add(new RfPowerScanRow(scanSource.Index, scanSource.SdrType, scanSource.Serial, scanSource.Device, scanSource.CenterHz, scanSource.SampleRate, scanSource.SampleRate, scanSource.ErrorHz, scanSource.Gain, controlChannel, duration, commandName, -1, "", "", 0, "unavailable", rtlSelector.Issue, null, null, null, null, 0, false, "", null, null));
                            continue;
                        }
                        var command = BuildRfPowerScanCommandWithSerialPinning(scanSource, controlChannel, sampleRate, sampleCount, rawPath, isAirspy, rtlSelector.Argument, pinAirspySerial);
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        linked.CancelAfter(TimeSpan.FromSeconds(duration + 12));
                        var result = OperatingSystem.IsWindows()
                            ? await RunCaptureAsync("powershell", "-NoProfile -Command " + Quote(command), linked.Token)
                            : await RunCaptureAsync("bash", "-lc " + Quote(command), linked.Token);
                        if (isAirspy && IsAirspyOpenFailure(result.Stdout))
                        {
                            if (File.Exists(rawPath))
                                File.Delete(rawPath);
                            await Task.Delay(TimeSpan.FromSeconds(3), ct);
                            using var retryLinked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            retryLinked.CancelAfter(TimeSpan.FromSeconds(duration + 12));
                            result = OperatingSystem.IsWindows()
                                ? await RunCaptureAsync("powershell", "-NoProfile -Command " + Quote(command), retryLinked.Token)
                                : await RunCaptureAsync("bash", "-lc " + Quote(command), retryLinked.Token);
                        }
                        var analysis = AnalyzeIqFile(rawPath, sampleRate, isAirspy);
                        rows.Add(new RfPowerScanRow(
                            scanSource.Index,
                            scanSource.SdrType,
                            scanSource.Serial,
                            scanSource.Device,
                            scanSource.CenterHz,
                            requestedSampleRate,
                            sampleRate,
                            scanSource.ErrorHz,
                            scanSource.Gain,
                            controlChannel,
                            duration,
                            command,
                            result.ExitCode,
                            TrimOutput(result.Stdout),
                            rawPath,
                            File.Exists(rawPath) ? new FileInfo(rawPath).Length : 0,
                            analysis.Completed ? "measured" : "failed",
                            analysis.Issue,
                            analysis.PeakDb,
                            analysis.NoiseFloorDb,
                            analysis.SnrDb,
                            analysis.PeakOffsetHz,
                            analysis.ClipPct,
                            analysis.Overload,
                            analysis.Sparkline,
                            analysis.StrongestPeakDb,
                            analysis.StrongestPeakOffsetHz));
                        await WriteRfPowerScanProgressAsync(outputDir, profile, firstControlChannel, controlChannels, rows, trState, trWasActive, trStopOutput, trRestartOutput, trRestartError, CancellationToken.None);
                    }
                }
            }
        }
        finally
        {
            if (trWasActive)
            {
                try
                {
                    var airspyOpenFailed = rows.Any(row =>
                        string.Equals(row.SdrType, "Airspy", StringComparison.OrdinalIgnoreCase) &&
                        IsAirspyOpenFailure(row.Output));
                    if (airspyOpenFailed)
                    {
                        trRestartError = "trunk-recorder was left stopped because Airspy did not reopen after the RF capture pause. Replug or reset the Airspy before restarting TR.";
                    }
                    else
                    {
                        trRestartOutput = await RunServiceHelperAsync("start-tr", CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    trRestartError = ex.Message;
                    _logger.LogError(ex, "RF power scan failed to restart trunk-recorder after pausing it.");
                }
            }
        }

        var measured = rows.Where(row => string.Equals(row.Status, "measured", StringComparison.OrdinalIgnoreCase)).ToList();
        var best = measured
            .OrderBy(row => row.Overload ? 1 : 0)
            .ThenByDescending(row => row.SnrDb ?? double.NegativeInfinity)
            .FirstOrDefault();
        var blockers = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.Issue) || !string.IsNullOrWhiteSpace(row.Output))
            .Select(SummarizeRfPowerBlocker)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var bestStrongestIsOffChannel = best != null && IsRfPeakOffTarget(best.StrongestPeakOffsetHz);
        var good = best != null && (best.SnrDb ?? 0) >= 8 && !best.Overload;
        var status = !string.IsNullOrWhiteSpace(trRestartError) ? "failed" : measured.Count == 0 ? "failed" : good ? "passed" : "failed";
        var summary = best == null
            ? "RF sweep did not produce an analyzable IQ capture."
            : $"Best RF sweep: source {best.Index}, CC {best.ControlChannelHz}, gain {best.Gain}, control-channel SNR {best.SnrDb:F1} dB, CC peak {best.PeakDb:F1} dB, noise floor {best.NoiseFloorDb:F1} dB, CC offset {best.PeakOffsetHz:F0} Hz.";
        var blocker = !string.IsNullOrWhiteSpace(trRestartError) ? $"trunk-recorder did not restart after RF power scan: {trRestartError}" :
            status == "passed" ? "" :
            best == null ? string.Join(" ", blockers) :
            best.Overload ? "RF sweep indicates possible overload/clipping across the measured gain range." :
            bestStrongestIsOffChannel ? "RF sweep found a stronger signal away from the selected control channel while the control-channel window remained weak." :
            "RF sweep did not show enough SNR margin at the selected control channel.";

        var evidence = new
        {
            profile.SystemShortName,
            controlChannelHz = firstControlChannel,
            controlChannelsHz = controlChannels,
            gainSequence,
            durationSeconds = duration,
            outputDir,
            trState,
            trStopOutput,
            trWasActive,
            trRestartOutput,
            trRestartError,
            rows
        };
        await WriteArtifactAsync(outputDir, "rf-power-scan.json", evidence, ct);
        return new ExperimentOutcome(
            status,
            "A usable control channel should stand above the local noise floor without SDR overload before P25 decoding is expected to work.",
            "Radio Setup paused TR if needed; selected SDR available; known control channels; rtl_sdr or airspy_rx installed.",
            summary,
            blocker,
            evidence,
            new
            {
                recommendation = status == "passed"
                    ? "Proceed to P25 probing with the selected SDR/control-channel/gain condition."
                    : best?.Overload == true
                        ? "Reduce gain sequence, remove excess amplification, or bypass an LNA/multicoupler path, then rerun the sweep."
                        : bestStrongestIsOffChannel
                        ? "A stronger adjacent signal is present, but the tuned control-channel window is weak. Confirm the active control channel, try the other configured CC, or inspect the spectrum before running P25 on this CC."
                        : "Check antenna aim, RF path loss, filters, splitters, source center coverage, and alternate control channels before treating P25 failure as a decoder issue.",
                selectedControlChannelHz = best?.ControlChannelHz ?? firstControlChannel,
                selectedGain = best?.Gain ?? "",
                best,
                followUps = status == "passed"
                    ? new[] { "Run P25 probe on the same control channel.", "If P25 fails despite good SNR, inspect error/gain and modulation settings." }
                    : new[] { "Try alternate control channel.", "Change gain sequence and rerun RF sweep.", "Bypass suspect RF path components and rerun.", "Verify antenna aim/polarization." }
            });
    }

    private async Task WriteRfPowerScanProgressAsync(
        string outputDir,
        RfSurveyProfileDto profile,
        long firstControlChannel,
        IReadOnlyList<long> controlChannels,
        IReadOnlyList<RfPowerScanRow> rows,
        TrServiceState trState,
        bool trWasActive,
        string trStopOutput,
        string trRestartOutput,
        string trRestartError,
        CancellationToken ct)
    {
        var evidence = new
        {
            profile.SystemShortName,
            controlChannelHz = firstControlChannel,
            controlChannelsHz = controlChannels,
            outputDir,
            rows,
            trState,
            trWasActive,
            trStopOutput,
            trRestartOutput,
            trRestartError,
            progressUpdatedAtUtc = DateTime.UtcNow
        };
        await WriteArtifactAsync(outputDir, "rf-power-scan.json", evidence, ct);
    }

    private async Task WriteRfValidationProgressAsync(object powerEvidence, IReadOnlyList<RfValidationCandidate> candidates, CancellationToken ct)
    {
        var root = JsonSerializer.SerializeToNode(powerEvidence, EngineConfig.JsonOptions()) as JsonObject;
        if (root == null)
            return;
        var outputDir = root["outputDir"]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outputDir))
            return;
        root["candidates"] = JsonSerializer.SerializeToNode(candidates.Select(candidate => new
        {
            candidate.Id,
            candidate.SourceIndex,
            candidate.ControlChannelHz,
            candidate.Gain,
            candidate.ErrorHz,
            candidate.P25Status,
            candidate.P25Summary,
            candidate.MetricsStatus,
            candidate.MetricsSummary,
            candidate.VoiceStatus,
            candidate.VoiceSummary,
            candidate.VoiceTotalCalls,
            candidate.VoiceRealCalls
        }).ToList(), EngineConfig.JsonOptions());
        root["progressUpdatedAtUtc"] = DateTime.UtcNow;
        await WriteArtifactAsync(outputDir, "rf-power-scan.json", root, ct);
    }

    private async Task<ExperimentOutcome> RunRfValidationSweepAsync(RfSurveySessionDto session, RfSurveyProfileDto profile, RfSurveyToolPrepDto toolPrep, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        var artifactPath = session.ArtifactPath;
        var rfDuration = ReadIntParameter(request.Parameters, "rfDurationSeconds", 2, 2, 5);
        var p25Duration = ReadIntParameter(request.Parameters, "p25DurationSeconds", 10, 10, 45);
        var metricsDuration = ReadIntParameter(request.Parameters, "metricsDurationSeconds", 15, 15, 30);
        var rfCandidateLimit = ReadIntParameter(request.Parameters, "rfCandidateLimit", 3, 1, 5);
        var p25CandidateLimit = ReadIntParameter(request.Parameters, "p25CandidateLimit", 3, 1, 3);
        var metricsCandidateLimit = ReadIntParameter(request.Parameters, "metricsCandidateLimit", 2, 1, 3);
        var voiceCandidateLimit = ReadIntParameter(request.Parameters, "voiceCandidateLimit", 2, 0, 3);
        var voiceDuration = ReadIntParameter(request.Parameters, "voiceDurationSeconds", 45, 20, 90);
        var errorDiscovery = ReadBoolParameter(request.Parameters, "errorDiscovery", true);
        var errorOffsets = ReadIntSequenceParameter(request.Parameters, "errorOffsetsHz", errorDiscovery ? [0, -300, 300] : [-300, 0, 300], 7);
        var p25Demods = ReadP25DemodSequence(request.Parameters);
        var sampleRateOverride = ReadIntParameter(request.Parameters, "sampleRateHz", 0, 0, 10_000_000);
        var baseProfile = sampleRateOverride > 0 ? ProfileWithSampleRateOverride(profile, sampleRateOverride) : profile;
        var targetSystemShortName = ReadStringParameter(request.Parameters, "targetSystemShortName");
        var targetSystem = !string.IsNullOrWhiteSpace(targetSystemShortName)
            ? baseProfile.Systems.FirstOrDefault(system => string.Equals(system.ShortName, targetSystemShortName, StringComparison.OrdinalIgnoreCase))
            : null;
        var sweepProfile = targetSystem == null
            ? baseProfile
            : baseProfile with
            {
                SystemShortName = targetSystem.ShortName,
                SystemShortNames = [targetSystem.ShortName],
                Systems = [targetSystem],
                ControlChannelsHz = targetSystem.ControlChannelsHz,
                VoiceFrequenciesHz = targetSystem.VoiceFrequenciesHz
            };
        var firstControlChannel = request.ControlChannelHz ?? sweepProfile.ControlChannelsHz.FirstOrDefault();
        var p25Preview = BuildP25ProbePreview(sweepProfile, artifactPath, firstControlChannel, p25Duration);
        if (!p25Preview.Ready)
            return BlockedOutcome(
                "rf_validation_sweep",
                "RF Sweep requires a configured P25 probe before RF candidates can be ranked for decode.",
                "Configure Radio Setup P25 probe command template.",
                p25Preview.BlockingIssue,
                new
                {
                    sweepProfile.SystemShortName,
                    p25Preview,
                    parameters = new { rfDuration, p25Duration, metricsDuration, voiceDuration, rfCandidateLimit, metricsCandidateLimit, voiceCandidateLimit, errorDiscovery, errorOffsets, p25Demods }
                });

        var trCoverage = CurrentTrSourceCoverageValidation();
        if (trCoverage?.Ok == false)
            return BlockedOutcome(
                "rf_validation_sweep",
                "RF Sweep includes live TR CC Metrics, so the current TR config must be able to start before the sweep runs.",
                "Generate a TR config whose source windows cover the selected site control channels.",
                "TR config source coverage failed: " + string.Join(" ", trCoverage.Blockers),
                new
                {
                    sweepProfile.SystemShortName,
                    trCoverage,
                    parameters = new { rfDuration, p25Duration, metricsDuration, voiceDuration, rfCandidateLimit, metricsCandidateLimit, voiceCandidateLimit, errorDiscovery, errorOffsets, p25Demods }
                });

        var powerRequest = request with
        {
            DurationSeconds = rfDuration,
            ControlChannelHz = request.ControlChannelHz,
            Parameters = BuildRfValidationPowerParameters(request.Parameters)
        };
        var powerOutcome = await RunRfPowerScanAsync(artifactPath, sweepProfile, powerRequest, ct);
        var allPowerCandidates = ReadRfValidationPowerCandidates(powerOutcome)
            .Where(candidate => string.Equals(candidate.RfStatus, "measured", StringComparison.OrdinalIgnoreCase))
            .OrderBy(candidate => candidate.Overload ? 1 : 0)
            .ThenByDescending(candidate => candidate.SnrDb ?? double.NegativeInfinity)
            .ThenBy(candidate => Math.Abs(candidate.PeakOffsetHz ?? 0))
            .ToList();
        var powerCandidates = SelectRfValidationPowerCandidates(sweepProfile, allPowerCandidates, rfCandidateLimit);
        var livePowerCandidate = allPowerCandidates.FirstOrDefault(candidate => CandidateMatchesLiveSource(sweepProfile, candidate) && candidate.ControlChannelHz == firstControlChannel);
        if (livePowerCandidate != null && !powerCandidates.Any(candidate => SameRfSeed(candidate, livePowerCandidate)))
        {
            if (powerCandidates.Count >= rfCandidateLimit && powerCandidates.Count > 0)
                powerCandidates.RemoveAt(powerCandidates.Count - 1);
            powerCandidates.Add(livePowerCandidate);
        }

        if (powerCandidates.Count == 0)
        {
            return new ExperimentOutcome(
                "failed",
                "A combined RF validation sweep should find at least one analyzable control-channel RF condition before decode checks run.",
                "Known control channels, selected SDR source, and SDR command-line tools.",
                "RF validation sweep could not find an analyzable RF candidate.",
                powerOutcome.BlockingIssue,
                new
                {
                    sweepProfile.SystemShortName,
                    power = powerOutcome.Evidence,
                    parameters = new { rfDuration, p25Duration, metricsDuration, voiceDuration, rfCandidateLimit, metricsCandidateLimit, voiceCandidateLimit, errorOffsets, p25Demods }
                },
                new
                {
                    recommendation = "Fix SDR access, antenna/path, gain sequence, or control-channel ground truth before running P25 or TR metrics.",
                    candidates = Array.Empty<RfValidationCandidate>()
                });
        }

        var candidates = new List<RfValidationCandidate>();
        foreach (var seed in powerCandidates)
        {
            var source = profile.Sources.FirstOrDefault(row => row.Index == seed.SourceIndex);
            var baseError = source?.ErrorHz ?? seed.ErrorHz;
            foreach (var offset in BuildValidationErrorOffsets(errorDiscovery, errorOffsets, seed.PeakOffsetHz))
            {
                var errorHz = baseError + offset;
                var candidateSystem = SystemForControlChannel(sweepProfile, seed.ControlChannelHz);
                var candidate = seed with
                {
                    Id = $"s{seed.SourceIndex}-cc{seed.ControlChannelHz}-g{SanitizeFileToken(seed.Gain)}-e{errorHz}",
                    SystemShortName = candidateSystem?.ShortName ?? string.Empty,
                    SiteLabel = candidateSystem?.SiteLabel ?? string.Empty,
                    ErrorHz = errorHz,
                    ErrorOffsetHz = offset
                };
                if (candidates.Any(existing =>
                    existing.SourceIndex == candidate.SourceIndex &&
                    existing.ControlChannelHz == candidate.ControlChannelHz &&
                    string.Equals(existing.Gain, candidate.Gain, StringComparison.OrdinalIgnoreCase) &&
                    existing.ErrorHz == candidate.ErrorHz))
                    continue;
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            return new ExperimentOutcome(
                "failed",
                "A combined RF validation sweep should produce at least one source/control-channel/gain/error candidate before decode checks run.",
                "Known control channels, selected SDR source, and candidate error offsets.",
                "RF validation sweep produced RF measurements but no decode candidates.",
                "No candidate error-offset combinations were generated.",
                new { sweepProfile.SystemShortName, power = powerOutcome.Evidence, parameters = new { rfDuration, p25Duration, metricsDuration, voiceDuration, rfCandidateLimit, metricsCandidateLimit, voiceCandidateLimit, errorDiscovery, errorOffsets, p25Demods } },
                new { recommendation = "Reset the RF sweep error offsets or rerun with error discovery enabled.", candidates = Array.Empty<RfValidationCandidate>() });
        }
        await WriteRfValidationProgressAsync(powerOutcome.Evidence, candidates, ct);

        p25Preview = BuildP25ProbePreview(sweepProfile, artifactPath, candidates[0].ControlChannelHz, p25Duration);
        if (!p25Preview.Ready)
        {
            candidates = candidates
                .Select(candidate => candidate with
                {
                    P25Status = "blocked",
                    P25Summary = p25Preview.BlockingIssue,
                    Score = ScoreRfValidationCandidate(candidate)
                })
                .OrderByDescending(candidate => candidate.Score)
                .ToList();
            await WriteRfValidationProgressAsync(powerOutcome.Evidence, candidates, ct);
            var p25BlockedEvidence = new
            {
                sweepProfile.SystemShortName,
                p25Preview,
                power = powerOutcome.Evidence,
                parameters = new { rfDuration, p25Duration, metricsDuration, voiceDuration, rfCandidateLimit, metricsCandidateLimit, voiceCandidateLimit, errorDiscovery, errorOffsets, p25Demods },
                candidates
            };
            await WriteArtifactAsync(artifactPath, $"rf-validation-sweep-{DateTime.UtcNow:yyyyMMddHHmmss}.json", p25BlockedEvidence, ct);
            return new ExperimentOutcome(
                "blocked",
                "A usable RF path should rank one or more control-channel/source/gain/error candidates with RF margin, P25 frame evidence, and live TR decode metrics.",
                "Known control channels, selected SDR source, P25 probe command, SDR tools, and trunk-recorder service control.",
                $"RF screen found {candidates.Count} candidate condition(s), but P25 probing is not configured.",
                p25Preview.BlockingIssue,
                p25BlockedEvidence,
                new
                {
                    recommendation = "Configure the Radio Setup P25 probe command template, then rerun the combined RF sweep. Do not change antenna or gain based only on this blocked decode step.",
                    candidates,
                    followUps = new[] { "Configure P25 probe command template.", "Rerun combined RF sweep.", "Review the RF-ranked candidates only as preliminary signal evidence." }
                });
        }

        var p25ProbeSeeds = SelectRfValidationP25Seeds(sweepProfile, candidates, p25CandidateLimit).ToHashSet();
        p25CandidateLimit = Math.Max(p25CandidateLimit, p25ProbeSeeds.Count);
        var p25ProbeIndexes = candidates
            .Select((candidate, index) => new { candidate, index })
            .Where(row => p25ProbeSeeds.Contains(RfValidationSeedKey.From(row.candidate)))
            .Select(row => row.index)
            .ToHashSet();
        var liveCandidateIndex = FindLiveCandidateIndex(candidates, sweepProfile, firstControlChannel);
        if (liveCandidateIndex >= 0 && !p25ProbeIndexes.Contains(liveCandidateIndex))
        {
            if (p25ProbeIndexes.Count >= p25CandidateLimit && p25ProbeIndexes.Count > 0)
            {
                var leastPreferredSeed = p25ProbeIndexes
                    .Select(index => candidates[index])
                    .GroupBy(RfValidationSeedKey.From)
                    .Select(group => new
                    {
                        group.Key,
                        Best = group
                            .OrderBy(candidate => candidate.Overload)
                            .ThenByDescending(candidate => candidate.SnrDb ?? double.NegativeInfinity)
                            .ThenBy(candidate => Math.Abs(candidate.ErrorOffsetHz))
                            .First()
                    })
                    .OrderByDescending(row => row.Best.Overload)
                    .ThenBy(row => row.Best.SnrDb ?? double.NegativeInfinity)
                    .ThenByDescending(row => Math.Abs(row.Best.PeakOffsetHz ?? 0))
                    .First().Key;
                p25ProbeIndexes.RemoveWhere(index =>
                    RfValidationSeedKey.From(candidates[index]) == leastPreferredSeed);
            }
            var liveCandidate = candidates[liveCandidateIndex];
            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].SourceIndex == liveCandidate.SourceIndex &&
                    candidates[i].ControlChannelHz == liveCandidate.ControlChannelHz &&
                    string.Equals(candidates[i].Gain ?? string.Empty, liveCandidate.Gain ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    p25ProbeIndexes.Add(i);
            }
        }
        var liveCandidateId = liveCandidateIndex >= 0 ? candidates[liveCandidateIndex].Id : null;
        var p25ProbedCandidateIds = p25ProbeIndexes
            .Select(index => candidates[index].Id)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!p25ProbeIndexes.Contains(i))
            {
                candidates[i] = candidate with
                {
                    P25Status = "not_run",
                    P25Summary = $"Skipped P25 probe because only the top {p25CandidateLimit} RF-ranked candidate(s) are probed inside the bounded sweep.",
                    Score = ScoreRfValidationCandidate(candidate with { P25Status = "not_run" })
                };
                continue;
            }

            candidates[i] = candidate with { P25Status = "running", P25Summary = $"Running P25 probe for {p25Duration} second(s)." };
            await WriteRfValidationProgressAsync(powerOutcome.Evidence, candidates, ct);
            candidate = candidates[i];
            var candidateProfile = ProfileWithCandidateSource(sweepProfile, candidate.SourceIndex, candidate.Gain, candidate.ErrorHz, candidate.ControlChannelHz);
            ExperimentOutcome? probe = null;
            JsonObject? p25Evidence = null;
            var triedDemods = new List<string>();
            var p25Attempts = new List<RfValidationP25Attempt>();
            foreach (var demod in p25Demods)
            {
                triedDemods.Add(demod);
                probe = await RunControlChannelProbeAsync(
                    artifactPath,
                    candidateProfile,
                    toolPrep,
                    request with
                    {
                        DurationSeconds = p25Duration,
                        ControlChannelHz = candidate.ControlChannelHz,
                        SourceIndex = candidate.SourceIndex,
                        Parameters = BuildP25DemodParameters(demod)
                    },
                    ct);
                p25Evidence = JsonSerializer.SerializeToNode(probe.Evidence, EngineConfig.JsonOptions()) as JsonObject;
                p25Attempts.Add(new RfValidationP25Attempt(
                    demod,
                    probe.Status,
                    p25Evidence?["command"]?.GetValue<string>() ?? string.Empty,
                    p25Evidence?["exitCode"]?.GetValue<int>(),
                    TrimOutput(p25Evidence?["output"]?.GetValue<string>() ?? string.Empty, 2000),
                    probe.BlockingIssue.Length > 0 ? probe.BlockingIssue : probe.ResultSummary));
                if (string.Equals(probe.Status, "passed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(probe.Status, "blocked", StringComparison.OrdinalIgnoreCase))
                    break;
            }
            probe ??= new ExperimentOutcome(
                "blocked",
                "P25 probing requires at least one demodulator mode.",
                "RF Sweep should provide a P25 demodulator list.",
                "No P25 demodulator modes were configured.",
                "No P25 demodulator modes were configured.",
                new { p25Demods },
                new { recommendation = "Configure p25Demods, then rerun RF Sweep." });
            p25Evidence ??= JsonSerializer.SerializeToNode(probe.Evidence, EngineConfig.JsonOptions()) as JsonObject;
            candidates[i] = candidate with
            {
                P25Status = probe.Status,
                P25Frames = string.Equals(probe.Status, "passed", StringComparison.OrdinalIgnoreCase),
                P25Summary = (probe.BlockingIssue.Length > 0 ? probe.BlockingIssue : probe.ResultSummary) +
                    (triedDemods.Count > 1 ? $" Tried demods: {string.Join(", ", triedDemods)}." : ""),
                P25Demod = p25Evidence?["demod"]?.GetValue<string>() ?? triedDemods.LastOrDefault() ?? string.Empty,
                P25Command = p25Evidence?["command"]?.GetValue<string>() ?? string.Empty,
                P25ExitCode = p25Evidence?["exitCode"]?.GetValue<int>(),
                P25Output = TrimOutput(p25Evidence?["output"]?.GetValue<string>() ?? string.Empty, 2000),
                P25Attempts = p25Attempts,
                Score = ScoreRfValidationCandidate(candidate with { P25Status = probe.Status, P25Frames = string.Equals(probe.Status, "passed", StringComparison.OrdinalIgnoreCase) })
            };
            await WriteRfValidationProgressAsync(powerOutcome.Evidence, candidates, ct);
        }

        var runStandaloneMetrics = voiceCandidateLimit <= 0;
        if (runStandaloneMetrics)
        {
            var metricIndexes = SelectRfValidationFollowUpIndexesBySite(
                sweepProfile,
                candidates,
                candidates.Select((candidate, index) => new { candidate, index }).Where(row => row.candidate.P25Frames).Select(row => row.index),
                metricsCandidateLimit);
            metricsCandidateLimit = Math.Max(metricsCandidateLimit, metricIndexes.Count);

            var metricsResults = await RunCandidateTrMetricsAsync(session, sweepProfile, candidates, metricIndexes, metricsDuration, async (index, result) =>
            {
                if (index < 0 || index >= candidates.Count)
                    return;
                var candidate = candidates[index];
                if (result == null)
                {
                    candidates[index] = candidate with { MetricsStatus = "running", MetricsSummary = $"Running TR metrics for {metricsDuration} second(s)." };
                }
                else
                {
                    var updated = candidate with
                    {
                        MetricsStatus = result.Status,
                        MetricsSummary = result.Summary,
                        MetricsRow = result.Row
                    };
                    candidates[index] = updated with { Score = ScoreRfValidationCandidate(updated) };
                }
                await WriteRfValidationProgressAsync(powerOutcome.Evidence, candidates, ct);
            }, ct);
            foreach (var (index, result) in metricsResults)
            {
                var candidate = candidates[index];
                var updated = candidate with
                {
                    MetricsStatus = result.Status,
                    MetricsSummary = result.Summary,
                    MetricsRow = result.Row
                };
                candidates[index] = updated with { Score = ScoreRfValidationCandidate(updated) };
            }
        }

        if (voiceCandidateLimit > 0)
        {
            var voiceIndexes = SelectRfValidationFollowUpIndexesBySite(
                sweepProfile,
                candidates,
                candidates.Select((candidate, index) => new { candidate, index }).Where(row => row.candidate.P25Frames).Select(row => row.index),
                voiceCandidateLimit,
                preferMetricsPassed: runStandaloneMetrics);
            voiceCandidateLimit = Math.Max(voiceCandidateLimit, voiceIndexes.Count);

            for (var i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].P25Frames && !voiceIndexes.Contains(i))
                    candidates[i] = candidates[i] with
                    {
                        VoiceStatus = "not_run",
                        VoiceSummary = $"Skipped voice trial because only the top {voiceCandidateLimit} P25/RF candidate(s) get short voice-capture trials inside the bounded sweep."
                    };
            }
            await WriteRfValidationProgressAsync(powerOutcome.Evidence, candidates, ct);

            var voiceResults = await RunVoiceCandidateTrialsAsync(session, sweepProfile, candidates, voiceIndexes, voiceDuration, async (index, result) =>
            {
                if (index < 0 || index >= candidates.Count)
                    return;
                var candidate = candidates[index];
                if (result == null)
                {
                    candidates[index] = candidate with { VoiceStatus = "running", VoiceSummary = $"Running voice trial for {voiceDuration} second(s)." };
                }
                else
                {
                    var updated = candidate with
                    {
                        VoiceStatus = result.Status,
                        VoiceSummary = result.Summary,
                        VoiceTotalCalls = result.TotalCalls,
                        VoiceRealCalls = result.RealCalls,
                        VoiceTrial = result.Analysis,
                        MetricsStatus = string.IsNullOrWhiteSpace(result.MetricsStatus) ? candidate.MetricsStatus : result.MetricsStatus,
                        MetricsSummary = string.IsNullOrWhiteSpace(result.MetricsSummary) ? candidate.MetricsSummary : result.MetricsSummary,
                        MetricsRow = result.MetricsRow ?? candidate.MetricsRow
                    };
                    candidates[index] = updated with { Score = ScoreRfValidationCandidate(updated) };
                }
                await WriteRfValidationProgressAsync(powerOutcome.Evidence, candidates, ct);
            }, ct);
            foreach (var (index, result) in voiceResults)
            {
                var candidate = candidates[index];
                var updated = candidate with
                {
                    VoiceStatus = result.Status,
                    VoiceSummary = result.Summary,
                    VoiceTotalCalls = result.TotalCalls,
                    VoiceRealCalls = result.RealCalls,
                    VoiceTrial = result.Analysis,
                    MetricsStatus = string.IsNullOrWhiteSpace(result.MetricsStatus) ? candidate.MetricsStatus : result.MetricsStatus,
                    MetricsSummary = string.IsNullOrWhiteSpace(result.MetricsSummary) ? candidate.MetricsSummary : result.MetricsSummary,
                    MetricsRow = result.MetricsRow ?? candidate.MetricsRow
                };
                candidates[index] = updated with { Score = ScoreRfValidationCandidate(updated) };
            }
        }

        candidates = candidates
            .Select(candidate => candidate.Score == 0 ? candidate with { Score = ScoreRfValidationCandidate(candidate) } : candidate)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();
        if (targetSystem != null)
            candidates = (await MergePreviousRfValidationCandidatesForOtherSitesAsync(session.Id, profile, targetSystem.ShortName, candidates, ct))
                .OrderByDescending(candidate => candidate.Score)
                .ToList();

        var siteReadiness = BuildRfValidationSiteReadiness(profile, candidates);
        var best = SelectBestRfValidationCandidate(candidates, voiceRequired: false);
        var monitorableSites = siteReadiness.Where(site => site.Monitorable).ToList();
        var unprovenSites = siteReadiness.Where(site => !site.Monitorable).ToList();
        var metricsRequired = true;
        var pass = siteReadiness.Count > 0
            ? unprovenSites.Count == 0
            : best is { P25Frames: true } &&
              (!metricsRequired || string.Equals(best.MetricsStatus, "passed", StringComparison.OrdinalIgnoreCase));
        var p25Only = best is { P25Frames: true } && !string.Equals(best.MetricsStatus, "passed", StringComparison.OrdinalIgnoreCase);
        var p25Blocked = candidates.Count > 0 && candidates.All(candidate => string.Equals(candidate.P25Status, "blocked", StringComparison.OrdinalIgnoreCase));
        var status = pass ? "passed" : p25Blocked ? "blocked" : "failed";
        var summary = siteReadiness.Count > 0
            ? $"Validated {monitorableSites.Count}/{siteReadiness.Count} selected site(s): {string.Join("; ", siteReadiness.Select(site => site.Monitorable && site.BestControlChannelHz.HasValue ? $"{site.Label} {FormatHz(site.BestControlChannelHz.Value)} passed" : $"{site.Label} not proven"))}."
            : best == null
            ? "RF validation sweep produced no ranked candidates."
            : $"Best candidate: source {best.SourceIndex}, CC {FormatHz(best.ControlChannelHz)}, gain {best.Gain}, error {best.ErrorHz} Hz, RF SNR {best.SnrDb:F1} dB, P25 {best.P25Status}, TR metrics {(string.IsNullOrWhiteSpace(best.MetricsStatus) ? "not run" : best.MetricsStatus)}, voice {(string.IsNullOrWhiteSpace(best.VoiceStatus) ? "not run" : best.VoiceStatus)}.";
        var technicalBlocker = BuildRfValidationTechnicalBlocker(candidates);
        var blocker = pass ? "" :
            !string.IsNullOrWhiteSpace(technicalBlocker) ? technicalBlocker :
            p25Blocked ? string.Join(" ", candidates.Select(candidate => candidate.P25Summary).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.OrdinalIgnoreCase)) :
            unprovenSites.Count > 0 ? "No fully usable control channel was proven for: " + string.Join(", ", unprovenSites.Select(site => site.Label)) + "." :
            metricsRequired && candidates.Any(candidate => candidate.P25Frames) && !candidates.Any(candidate => string.Equals(candidate.MetricsStatus, "passed", StringComparison.OrdinalIgnoreCase)) ? "No candidate produced both P25 evidence and passing TR control-channel metrics inside the bounded validation window." :
            "No candidate produced P25 evidence inside the bounded validation window.";
        var evidence = new
        {
            systemShortName = targetSystem?.ShortName ?? profile.SystemShortName,
            targetSystemShortName = targetSystem?.ShortName ?? string.Empty,
            siteReadiness,
            selectedControlChannelHz = best?.ControlChannelHz,
            selectedSourceIndex = best?.SourceIndex,
            selectedGain = best?.Gain,
            selectedErrorHz = best?.ErrorHz,
            technicalBlocker,
            warning = "RF Sweep validates each selected site independently and does not apply a global winning candidate to live TR. Use Config Draft to turn site-readiness evidence into a source plan.",
            appliedCandidateToLiveTr = false,
            appliedCandidate = (object?)null,
            persistenceIssue = "",
            parameters = new { rfDuration, p25Duration, metricsDuration, voiceDuration, rfCandidateLimit, p25CandidateLimit, metricsCandidateLimit, voiceCandidateLimit, errorDiscovery, errorOffsets, p25Demods },
            liveCandidateId,
            p25ProbedCandidateIds,
            voiceTestedCandidateIds = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.VoiceStatus) && !string.Equals(candidate.VoiceStatus, "not_run", StringComparison.OrdinalIgnoreCase))
                .Select(candidate => candidate.Id)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            power = powerOutcome.Evidence,
            candidates
        };
        await WriteArtifactAsync(artifactPath, $"rf-validation-sweep-{DateTime.UtcNow:yyyyMMddHHmmss}.json", evidence, ct);
        return new ExperimentOutcome(
            status,
            "A usable RF path should rank one or more control-channel/source/gain/error candidates with RF margin, P25 frame evidence, and live TR decode metrics.",
            "Known control channels, selected SDR source, P25 probe command, SDR tools, and trunk-recorder service control.",
            summary,
            blocker,
            evidence,
            new
            {
                recommendation = pass
                    ? p25Only
                        ? "The ranked candidate produced RF and P25 evidence, but TR metrics were not strong enough. Rerun RF Sweep before source planning."
                        : "Every selected site has at least one RF/P25/TR-metrics validated control channel. Continue to Config Draft source planning. Voice evidence is optional here; Call Quality may be inconclusive until real traffic appears."
                    : p25Only
                    ? "A P25-capable candidate exists, but it did not produce passing TR control-channel metrics. Review the TR scan clues before applying anything."
                    : "Do not treat every selected site as monitorable yet. Check antenna aim/RF path, lower gain if overloaded, verify the active control channels, then rerun the combined sweep.",
                selectedControlChannelHz = best?.ControlChannelHz,
                selectedSourceIndex = best?.SourceIndex,
                selectedGain = best?.Gain,
                selectedErrorHz = best?.ErrorHz,
                siteReadiness,
                best,
                candidates,
                followUps = pass
                    ? p25Only
                        ? new[] { "Rerun RF Sweep.", "Run final Call Quality verification only after TR metrics pass." }
                        : new[] { "Open Config Draft.", "Use the site-readiness rows when assigning SDR source windows." }
                    : new[] { "Review ranked candidate TR scan clues.", "Try a narrower gain/error set around the strongest P25 candidate.", "Verify antenna aim and selected control channels.", "Use more SDR bandwidth or a different source center if no-source grants dominate." }
            });
    }

    private static Dictionary<int, string> ReadPowerScanGainOverrides(JsonElement? parameters)
    {
        var result = new Dictionary<int, string>();
        if (parameters == null || parameters.Value.ValueKind != JsonValueKind.Object)
            return result;
        if (!parameters.Value.TryGetProperty("powerScanGains", out var gains) || gains.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var property in gains.EnumerateObject())
        {
            if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sourceIndex))
                continue;
            result[sourceIndex] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? "",
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.Null => "",
                _ => property.Value.ToString() ?? ""
            };
        }
        return result;
    }

    private static IReadOnlyList<long> ReadPowerScanControlChannels(JsonElement? parameters, long? requestedControlChannel, IReadOnlyList<long> profileControlChannels)
    {
        if (parameters != null &&
            parameters.Value.ValueKind == JsonValueKind.Object &&
            parameters.Value.TryGetProperty("controlChannelsHz", out var requestedChannels) &&
            requestedChannels.ValueKind == JsonValueKind.Array)
        {
            var channels = requestedChannels
                .EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number)
                    ? number
                    : element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var textNumber)
                        ? textNumber
                        : 0)
                .Where(hz => hz > 0)
                .Distinct()
                .ToList();
            if (channels.Count > 0)
                return channels;
        }

        if (parameters != null &&
            parameters.Value.ValueKind == JsonValueKind.Object &&
            parameters.Value.TryGetProperty("scanAllControlChannels", out var scanAll) &&
            scanAll.ValueKind == JsonValueKind.True)
            return profileControlChannels.Where(hz => hz > 0).Distinct().ToList();

        if (requestedControlChannel.HasValue && requestedControlChannel.Value > 0)
            return new[] { requestedControlChannel.Value };

        return profileControlChannels.Where(hz => hz > 0).Take(1).ToList();
    }

    private static string ReadStringParameter(JsonElement? parameters, string name)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(name, out var element))
            return string.Empty;
        return element.ValueKind == JsonValueKind.String
            ? element.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static IReadOnlyList<string> ReadStringSequenceParameter(JsonElement? parameters, string name, IReadOnlyList<string> fallback, int maxCount)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(name, out var element))
            return fallback.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount).ToList();
        var values = new List<string>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            values.AddRange(element.EnumerateArray().Select(JsonElementText));
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            values.AddRange((element.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            values.Add(JsonElementText(element));
        }

        var normalized = values
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
        return normalized.Count == 0
            ? fallback.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount).ToList()
            : normalized;
    }

    private static IReadOnlyList<string> ReadP25DemodSequence(JsonElement? parameters)
    {
        var requested = ReadStringSequenceParameter(parameters, "p25Demods", ["fsk4", "cqpsk"], 2);
        var demods = requested
            .Select(NormalizeP25Demod)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return demods.Count == 0 ? ["fsk4", "cqpsk"] : demods;
    }

    private static JsonElement BuildP25DemodParameters(string demod) =>
        JsonSerializer.SerializeToElement(new { p25Demod = NormalizeP25Demod(demod) }, EngineConfig.JsonOptions());

    private static string NormalizeP25Demod(string? demod)
    {
        var normalized = (demod ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "fsk4" => "fsk4",
            "cqpsk" or "qpsk" => "cqpsk",
            _ => string.Empty
        };
    }

    private static IReadOnlyList<string> ReadPowerScanGainSequence(JsonElement? parameters)
    {
        if (parameters == null || parameters.Value.ValueKind != JsonValueKind.Object)
            return [];
        if (!parameters.Value.TryGetProperty("gainSequence", out var sequence))
            return [];

        var values = new List<string>();
        if (sequence.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sequence.EnumerateArray())
                values.Add(JsonElementText(item));
        }
        else if (sequence.ValueKind == JsonValueKind.String)
        {
            values.AddRange((sequence.GetString() ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        }
        else
        {
            values.Add(JsonElementText(sequence));
        }

        return values
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
    }

    private static IReadOnlyList<string> PowerScanGainSequenceForSource(
        RfSurveySourceDto source,
        IReadOnlyList<string> requestedSequence,
        IReadOnlyDictionary<int, string> gainOverrides)
    {
        var sequence = requestedSequence.Count > 0
            ? requestedSequence
            : gainOverrides.TryGetValue(source.Index, out var gainOverride)
                ? new[] { gainOverride }
                : new[] { source.Gain };

        if (!IsAirspySource(source))
            return sequence;

        var valid = sequence
            .Select(value => value.Trim())
            .Where(IsValidAirspyLinearityGain)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return valid.Count > 0 ? valid : ["15"];
    }

    private static bool IsValidAirspyLinearityGain(string gain)
    {
        return int.TryParse(gain, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
               && value is >= 0 and <= 21;
    }

    private static JsonElement BuildRfValidationPowerParameters(JsonElement? parameters)
    {
        var root = parameters is { ValueKind: JsonValueKind.Object }
            ? JsonNode.Parse(parameters.Value.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        if (!root.ContainsKey("scanAllControlChannels"))
            root["scanAllControlChannels"] = true;
        if (!root.ContainsKey("gainSequence"))
            root["gainSequence"] = new JsonArray("0", "8", "14", "20", "21");
        return JsonSerializer.SerializeToElement(root, EngineConfig.JsonOptions());
    }

    private static int ReadIntParameter(JsonElement? parameters, string name, int fallback, int min, int max)
    {
        if (parameters is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty(name, out var element) &&
            TryReadInt(element, out var value))
            return Math.Clamp(value, min, max);
        return Math.Clamp(fallback, min, max);
    }

    private static int? ReadNullableIntParameter(JsonElement? parameters, string name)
    {
        if (parameters is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty(name, out var element) &&
            TryReadInt(element, out var value))
            return value;
        return null;
    }

    private static bool ReadBoolParameter(JsonElement? parameters, string name, bool fallback)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(name, out var element))
            return fallback;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var parsed) ? parsed : fallback,
            _ => fallback
        };
    }

    private static IReadOnlyList<int> BuildValidationErrorOffsets(bool errorDiscovery, IReadOnlyList<int> configuredOffsets, double? peakOffsetHz)
    {
        if (!errorDiscovery)
            return configuredOffsets.Distinct().Take(7).ToList();

        var offsets = new List<int> { 0 };
        if (peakOffsetHz.HasValue && double.IsFinite(peakOffsetHz.Value) && Math.Abs(peakOffsetHz.Value) >= 100)
        {
            var residual = (int)Math.Clamp(Math.Round(peakOffsetHz.Value / 100.0) * 100, -10_000, 10_000);
            offsets.Add(residual);
            offsets.Add(-residual);
        }
        else
        {
            offsets.AddRange(configuredOffsets.Where(offset => offset != 0));
        }

        return offsets.Distinct().Take(3).ToList();
    }

    private static IReadOnlyList<int> ReadIntSequenceParameter(JsonElement? parameters, string name, IReadOnlyList<int> fallback, int maxCount)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } root || !root.TryGetProperty(name, out var element))
            return fallback.Distinct().Take(maxCount).ToList();
        var values = new List<int>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryReadInt(item, out var value))
                    values.Add(value);
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            foreach (var part in (element.GetString() ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    values.Add(value);
        }
        else if (TryReadInt(element, out var single))
        {
            values.Add(single);
        }
        return values.Count == 0
            ? fallback.Distinct().Take(maxCount).ToList()
            : values.Distinct().Take(maxCount).ToList();
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            return true;
        if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        value = 0;
        return false;
    }

    private static IReadOnlyList<RfValidationCandidate> ReadRfValidationPowerCandidates(ExperimentOutcome powerOutcome)
    {
        var candidates = new List<RfValidationCandidate>();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(powerOutcome.Evidence, EngineConfig.JsonOptions()));
        if (!doc.RootElement.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return candidates;
        foreach (var row in rows.EnumerateArray())
        {
            var sourceIndex = JsonInt(row, "index");
            var controlChannel = JsonLong(row, "controlChannelHz");
            var gain = JsonString(row, "gain");
            candidates.Add(new RfValidationCandidate
            {
                Id = $"s{sourceIndex}-cc{controlChannel}-g{SanitizeFileToken(gain)}",
                SourceIndex = sourceIndex,
                SdrType = JsonString(row, "sdrType"),
                Serial = JsonString(row, "serial"),
                Device = JsonString(row, "device"),
                ControlChannelHz = controlChannel,
                Gain = gain,
                ErrorHz = JsonInt(row, "errorHz"),
                RfStatus = JsonString(row, "status"),
                RfIssue = JsonString(row, "issue"),
                PeakDb = JsonNullableDouble(row, "peakDb"),
                NoiseFloorDb = JsonNullableDouble(row, "noiseFloorDb"),
                SnrDb = JsonNullableDouble(row, "snrDb"),
                PeakOffsetHz = JsonNullableDouble(row, "peakOffsetHz"),
                ClipPct = JsonDouble(row, "clipPct"),
                Overload = JsonBool(row, "overload")
            });
        }
        return candidates;
    }

    private static List<RfValidationCandidate> SelectRfValidationPowerCandidates(RfSurveyProfileDto profile, IReadOnlyList<RfValidationCandidate> allPowerCandidates, int requestedLimit)
    {
        var selected = new List<RfValidationCandidate>();
        var selectedSeeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in PreferredP25ProbeSeedTargets(profile))
        {
            var preferred = OrderRfValidationCandidates(allPowerCandidates
                    .Where(candidate => candidate.SourceIndex == target.SourceIndex &&
                                        candidate.ControlChannelHz == target.ControlChannelHz &&
                                        GainMatches(target.Gain, candidate.Gain)))
                .FirstOrDefault();
            if (preferred != null)
                AddPowerSeed(preferred);
        }

        foreach (var channel in OrderedValidationControlChannels(profile))
        {
            var best = OrderRfValidationCandidates(allPowerCandidates.Where(candidate => candidate.ControlChannelHz == channel)).FirstOrDefault();
            if (best != null)
                AddPowerSeed(best);
        }

        foreach (var system in profile.Systems)
        {
            var backup = OrderRfValidationCandidates(allPowerCandidates
                    .Where(candidate => system.ControlChannelsHz.Contains(candidate.ControlChannelHz))
                    .Where(candidate => !selectedSeeds.Contains(PowerSeedKey(candidate))))
                .FirstOrDefault();
            if (backup != null)
                AddPowerSeed(backup);
        }

        var minimumCount = Math.Max(requestedLimit, selected.Count);
        foreach (var candidate in OrderRfValidationCandidates(allPowerCandidates))
        {
            if (selected.Count >= minimumCount)
                break;
            AddPowerSeed(candidate);
        }

        return selected;

        void AddPowerSeed(RfValidationCandidate candidate)
        {
            var key = PowerSeedKey(candidate);
            if (selectedSeeds.Add(key))
                selected.Add(candidate);
        }
    }

    private static List<RfValidationSeedKey> SelectRfValidationP25Seeds(RfSurveyProfileDto profile, IReadOnlyList<RfValidationCandidate> candidates, int requestedLimit)
    {
        var seedRows = candidates
            .GroupBy(RfValidationSeedKey.From)
            .Select(group => new
            {
                Key = group.Key,
                Best = OrderRfValidationCandidates(group).ThenBy(candidate => Math.Abs(candidate.ErrorOffsetHz)).First()
            })
            .ToList();
        var selected = new List<RfValidationSeedKey>();
        var selectedSet = new HashSet<RfValidationSeedKey>();

        foreach (var target in PreferredP25ProbeSeedTargets(profile))
        {
            var preferred = seedRows
                .Where(row => row.Key.SourceIndex == target.SourceIndex &&
                              row.Key.ControlChannelHz == target.ControlChannelHz &&
                              GainMatches(target.Gain, row.Key.Gain))
                .OrderBy(row => row.Best.Overload)
                .ThenByDescending(row => row.Best.SnrDb ?? double.NegativeInfinity)
                .ThenBy(row => Math.Abs(row.Best.PeakOffsetHz ?? 0))
                .FirstOrDefault();
            if (preferred != null && selectedSet.Add(preferred.Key))
                selected.Add(preferred.Key);
        }

        foreach (var channel in OrderedValidationControlChannels(profile))
        {
            var best = seedRows
                .Where(row => row.Key.ControlChannelHz == channel)
                .OrderBy(row => row.Best.Overload)
                .ThenByDescending(row => row.Best.SnrDb ?? double.NegativeInfinity)
                .ThenBy(row => Math.Abs(row.Best.PeakOffsetHz ?? 0))
                .FirstOrDefault();
            if (best != null && selectedSet.Add(best.Key))
                selected.Add(best.Key);
        }

        foreach (var system in profile.Systems)
        {
            var backup = seedRows
                .Where(row => system.ControlChannelsHz.Contains(row.Key.ControlChannelHz))
                .Where(row => !selectedSet.Contains(row.Key))
                .OrderBy(row => row.Best.Overload)
                .ThenByDescending(row => row.Best.SnrDb ?? double.NegativeInfinity)
                .ThenBy(row => Math.Abs(row.Best.PeakOffsetHz ?? 0))
                .FirstOrDefault();
            if (backup != null && selectedSet.Add(backup.Key))
                selected.Add(backup.Key);
        }

        var minimumCount = Math.Max(requestedLimit, selected.Count);
        foreach (var row in seedRows
            .OrderBy(row => row.Best.Overload)
            .ThenByDescending(row => row.Best.SnrDb ?? double.NegativeInfinity)
            .ThenBy(row => Math.Abs(row.Best.ErrorOffsetHz)))
        {
            if (selected.Count >= minimumCount)
                break;
            if (selectedSet.Add(row.Key))
                selected.Add(row.Key);
        }

        return selected;
    }

    private static IEnumerable<(int SourceIndex, long ControlChannelHz, string Gain)> PreferredP25ProbeSeedTargets(RfSurveyProfileDto profile)
    {
        var selectedSourceIndexes = profile.SelectedSourceIndexes.Count > 0
            ? profile.SelectedSourceIndexes.ToHashSet()
            : profile.Sources.Select(source => source.Index).ToHashSet();
        var channels = profile.Systems
            .Select(system => system.ControlChannelsHz.FirstOrDefault())
            .Concat(profile.ControlChannelsHz.Take(1))
            .Where(channel => channel > 0)
            .Distinct()
            .ToList();
        foreach (var source in profile.Sources.Where(source => selectedSourceIndexes.Count == 0 || selectedSourceIndexes.Contains(source.Index)))
        {
            foreach (var gain in PreferredP25ProbeGains(source))
            {
                foreach (var channel in channels)
                    yield return (source.Index, channel, gain);
            }
        }
    }

    private static IReadOnlyList<string> PreferredP25ProbeGains(RfSurveySourceDto source)
    {
        var gains = new List<string>();
        Add(source.Gain);
        if (IsAirspySource(source))
            Add("20");
        return gains;

        void Add(string? gain)
        {
            var normalized = NormalizeP25ProbeGain(gain);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !gains.Any(existing => GainMatches(existing, normalized)))
                gains.Add(normalized);
        }
    }

    private static string PowerSeedKey(RfValidationCandidate candidate) =>
        $"{candidate.SourceIndex}|{candidate.ControlChannelHz}|{candidate.Gain}|{candidate.ErrorHz}";

    private static List<int> SelectRfValidationFollowUpIndexesBySite(RfSurveyProfileDto profile, IReadOnlyList<RfValidationCandidate> candidates, IEnumerable<int> indexes, int requestedLimit, bool preferMetricsPassed = false)
    {
        var available = indexes.Distinct().Where(index => index >= 0 && index < candidates.Count).ToList();
        var selected = new List<int>();
        var selectedSet = new HashSet<int>();

        foreach (var system in profile.Systems)
        {
            var best = available
                .Where(index => string.Equals(candidates[index].SystemShortName, system.ShortName, StringComparison.OrdinalIgnoreCase) ||
                                system.ControlChannelsHz.Contains(candidates[index].ControlChannelHz))
                .OrderBy(index => candidates[index].Overload)
                .ThenByDescending(index => preferMetricsPassed && string.Equals(candidates[index].MetricsStatus, "passed", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(index => candidates[index].SnrDb ?? double.NegativeInfinity)
                .ThenBy(index => Math.Abs(candidates[index].ErrorOffsetHz))
                .FirstOrDefault(-1);
            if (best >= 0 && selectedSet.Add(best))
                selected.Add(best);
        }

        var minimumCount = Math.Max(requestedLimit, selected.Count);
        foreach (var index in available
            .OrderBy(index => candidates[index].Overload)
            .ThenByDescending(index => preferMetricsPassed && string.Equals(candidates[index].MetricsStatus, "passed", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(index => candidates[index].SnrDb ?? double.NegativeInfinity)
            .ThenBy(index => Math.Abs(candidates[index].ErrorOffsetHz)))
        {
            if (selected.Count >= minimumCount)
                break;
            if (selectedSet.Add(index))
                selected.Add(index);
        }

        return selected;
    }

    private static IOrderedEnumerable<RfValidationCandidate> OrderRfValidationCandidates(IEnumerable<RfValidationCandidate> candidates) =>
        candidates
            .OrderBy(candidate => candidate.Overload ? 1 : 0)
            .ThenByDescending(candidate => candidate.SnrDb ?? double.NegativeInfinity)
            .ThenBy(candidate => Math.Abs(candidate.PeakOffsetHz ?? 0));

    private static IReadOnlyList<long> OrderedValidationControlChannels(RfSurveyProfileDto profile)
    {
        var channels = new List<long>();
        foreach (var system in profile.Systems)
            channels.AddRange(system.ControlChannelsHz);
        channels.AddRange(profile.ControlChannelsHz);
        return channels.Where(channel => channel > 0).Distinct().ToList();
    }

    private static RfSurveyProfileDto ProfileWithCandidateSource(RfSurveyProfileDto profile, int sourceIndex, string gain, int errorHz, long? controlChannelHz = null)
    {
        var scoped = profile;
        var candidateSystem = controlChannelHz.HasValue ? SystemForControlChannel(profile, controlChannelHz.Value) : null;
        if (candidateSystem != null)
        {
            scoped = scoped with
            {
                SystemShortName = candidateSystem.ShortName,
                SystemShortNames = [candidateSystem.ShortName],
                ControlChannelsHz = candidateSystem.ControlChannelsHz,
                VoiceFrequenciesHz = candidateSystem.VoiceFrequenciesHz
            };
        }

        var candidateControlChannels = candidateSystem?.ControlChannelsHz is { Count: > 0 }
            ? candidateSystem.ControlChannelsHz
            : controlChannelHz.HasValue && controlChannelHz.Value > 0 ? [controlChannelHz.Value] : scoped.ControlChannelsHz;

        return scoped with
        {
            Sources = scoped.Sources
                .Select(source =>
                {
                    if (source.Index != sourceIndex)
                        return source;
                    var centerHz = CenterForCandidateControlChannels(candidateControlChannels, source.SampleRate);
                    return source with
                    {
                        CenterHz = centerHz > 0 ? centerHz : source.CenterHz,
                        Gain = gain,
                        ErrorHz = errorHz
                    };
                })
                .ToList()
        };
    }

    private static RfSurveyProfileDto ProfileWithP25ProbeOverrides(RfSurveyProfileDto profile, RfSurveyRunExperimentRequest request)
    {
        if (!request.SourceIndex.HasValue || request.Parameters is not { ValueKind: JsonValueKind.Object })
            return profile;

        var overrides = ReadP25ProbeOverrides(request);
        if (string.IsNullOrWhiteSpace(overrides.Gain) &&
            !overrides.ErrorHz.HasValue &&
            !overrides.SampleRateHz.HasValue)
            return profile;

        return profile with
        {
            Sources = profile.Sources
                .Select(source =>
                {
                    if (source.Index != request.SourceIndex.Value)
                        return source;
                    return source with
                    {
                        Gain = string.IsNullOrWhiteSpace(overrides.Gain) ? source.Gain : overrides.Gain,
                        ErrorHz = overrides.ErrorHz ?? source.ErrorHz,
                        SampleRate = overrides.SampleRateHz ?? source.SampleRate
                    };
                })
                .ToList()
        };
    }

    private static P25ProbeOverrides ReadP25ProbeOverrides(RfSurveyRunExperimentRequest request)
    {
        var gain = ReadStringParameter(request.Parameters, "probeGain");
        if (string.IsNullOrWhiteSpace(gain))
            gain = ReadStringParameter(request.Parameters, "gain");

        return new P25ProbeOverrides(
            gain,
            ReadNullableIntParameter(request.Parameters, "probeErrorHz"),
            ReadNullableIntParameter(request.Parameters, "probeSampleRateHz"));
    }

    private static RfSurveySystemDto? SystemForControlChannel(RfSurveyProfileDto profile, long controlChannelHz) =>
        profile.Systems.FirstOrDefault(system => system.ControlChannelsHz.Contains(controlChannelHz));

    private static int FindLiveCandidateIndex(IReadOnlyList<RfValidationCandidate> candidates, RfSurveyProfileDto profile, long preferredControlChannelHz)
    {
        var match = candidates
            .Select((candidate, index) => new { candidate, index })
            .Where(row => CandidateMatchesLiveSource(profile, row.candidate))
            .OrderBy(row => row.candidate.ControlChannelHz == preferredControlChannelHz ? 0 : 1)
            .ThenBy(row => row.candidate.Overload)
            .ThenByDescending(row => row.candidate.SnrDb ?? double.NegativeInfinity)
            .FirstOrDefault();
        return match?.index ?? -1;
    }

    private static RfValidationCandidate? SelectBestRfValidationCandidate(IReadOnlyList<RfValidationCandidate> candidates, bool voiceRequired)
    {
        if (candidates.Count == 0)
            return null;

        if (voiceRequired)
        {
            var voicePassed = candidates
                .Where(candidate => candidate.P25Frames && string.Equals(candidate.VoiceStatus, "passed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();
            if (voicePassed != null)
                return voicePassed;
        }

        var p25Passed = candidates
            .Where(candidate => candidate.P25Frames)
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (p25Passed != null)
            return p25Passed;

        var p25Attempted = candidates
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.P25Status) &&
                !string.Equals(candidate.P25Status, "not_run", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
        if (p25Attempted != null)
            return p25Attempted;

        return candidates.OrderByDescending(candidate => candidate.Score).First();
    }

    private static IReadOnlyList<RfValidationSiteReadiness> BuildRfValidationSiteReadiness(RfSurveyProfileDto profile, IReadOnlyList<RfValidationCandidate> candidates)
    {
        var results = new List<RfValidationSiteReadiness>();
        foreach (var system in profile.Systems.Where(system => system.ControlChannelsHz.Count > 0))
        {
            var siteCandidates = candidates
                .Where(candidate =>
                    string.Equals(candidate.SystemShortName, system.ShortName, StringComparison.OrdinalIgnoreCase) ||
                    system.ControlChannelsHz.Contains(candidate.ControlChannelHz))
                .OrderByDescending(ScoreRfValidationCandidate)
                .ToList();
            var passing = siteCandidates
                .Where(IsMonitorableRfCandidate)
                .OrderByDescending(ScoreRfValidationCandidate)
                .FirstOrDefault();
            var best = passing ?? siteCandidates.FirstOrDefault();
            var stage = best == null ? "not_run" :
                IsMonitorableRfCandidate(best) && string.Equals(best.VoiceStatus, "passed", StringComparison.OrdinalIgnoreCase) ? "voice_passed" :
                IsMonitorableRfCandidate(best) ? "voice_inconclusive" :
                string.Equals(best.VoiceStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(best.VoiceStatus, "blocked", StringComparison.OrdinalIgnoreCase) ? "voice_failed" :
                string.Equals(best.MetricsStatus, "passed", StringComparison.OrdinalIgnoreCase) ? "tr_metrics_passed" :
                string.Equals(best.MetricsStatus, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(best.MetricsStatus, "blocked", StringComparison.OrdinalIgnoreCase) ? "tr_metrics_failed" :
                best.P25Frames || string.Equals(best.P25Status, "passed", StringComparison.OrdinalIgnoreCase) ? "p25_passed" :
                string.Equals(best.P25Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(best.P25Status, "blocked", StringComparison.OrdinalIgnoreCase) ? "p25_failed" :
                string.Equals(best.RfStatus, "measured", StringComparison.OrdinalIgnoreCase) ? "rf_measured" :
                "not_run";
            var issue = passing != null
                ? string.Equals(passing.VoiceStatus, "passed", StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : "Voice proof is inconclusive: no audio-bearing calls were captured in the optional voice window. Call Quality may be unable to test transcription or long-run call stability until real traffic appears."
                :
                best == null ? "No RF Sweep candidate was generated for this site." :
                !string.IsNullOrWhiteSpace(best.VoiceSummary) ? best.VoiceSummary :
                !string.IsNullOrWhiteSpace(best.MetricsSummary) ? best.MetricsSummary :
                !string.IsNullOrWhiteSpace(best.P25Summary) ? best.P25Summary :
                !string.IsNullOrWhiteSpace(best.RfIssue) ? best.RfIssue :
                "No candidate passed RF, P25, and TR metrics for this site.";
            results.Add(new RfValidationSiteReadiness(
                system.ShortName,
                string.IsNullOrWhiteSpace(system.SiteLabel) ? system.ShortName : system.SiteLabel,
                passing != null,
                passing?.ControlChannelHz,
                passing?.SourceIndex,
                passing?.Gain ?? string.Empty,
                passing?.ErrorHz,
                best?.Id ?? string.Empty,
                stage,
                issue,
                siteCandidates.Select(candidate => candidate.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList()));
        }

        return results;
    }

    private async Task<List<RfValidationCandidate>> MergePreviousRfValidationCandidatesForOtherSitesAsync(
        string surveyId,
        RfSurveyProfileDto profile,
        string targetSystemShortName,
        IReadOnlyList<RfValidationCandidate> targetCandidates,
        CancellationToken ct)
    {
        var merged = new List<RfValidationCandidate>(targetCandidates);
        var target = profile.Systems.FirstOrDefault(system => string.Equals(system.ShortName, targetSystemShortName, StringComparison.OrdinalIgnoreCase));
        if (target == null)
            return merged;
        var targetChannels = target.ControlChannelsHz.ToHashSet();
        try
        {
            var previous = (await _database.ListRfSurveyExperimentsAsync(surveyId, ct))
                .Where(experiment => string.Equals(experiment.Type, "rf_validation_sweep", StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrWhiteSpace(experiment.EvidenceJson))
                .OrderByDescending(experiment => experiment.CreatedAtUtc)
                .FirstOrDefault();
            if (previous == null)
                return merged;
            using var document = JsonDocument.Parse(previous.EvidenceJson);
            if (!document.RootElement.TryGetProperty("candidates", out var candidatesElement) ||
                candidatesElement.ValueKind != JsonValueKind.Array)
                return merged;
            var previousCandidates = JsonSerializer.Deserialize<List<RfValidationCandidate>>(candidatesElement.GetRawText(), EngineConfig.JsonOptions()) ?? [];
            foreach (var candidate in previousCandidates)
            {
                if (targetChannels.Contains(candidate.ControlChannelHz) ||
                    string.Equals(candidate.SystemShortName, targetSystemShortName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (merged.Any(existing => string.Equals(existing.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)))
                    continue;
                merged.Add(candidate);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to merge previous RF validation candidates for targeted sweep {SurveyId}.", surveyId);
        }
        return merged;
    }

    private static bool IsMonitorableRfCandidate(RfValidationCandidate candidate) =>
        (string.Equals(candidate.RfStatus, "measured", StringComparison.OrdinalIgnoreCase) || candidate.SnrDb.HasValue) &&
        (candidate.P25Frames || string.Equals(candidate.P25Status, "passed", StringComparison.OrdinalIgnoreCase)) &&
        string.Equals(candidate.MetricsStatus, "passed", StringComparison.OrdinalIgnoreCase);

    private static bool CandidateMatchesLiveSource(RfSurveyProfileDto profile, RfValidationCandidate candidate, long? controlChannelHz = null)
    {
        var source = profile.Sources.FirstOrDefault(row => row.Index == candidate.SourceIndex);
        return source != null &&
               source.ErrorHz == candidate.ErrorHz &&
               GainMatches(source.Gain, candidate.Gain) &&
               (!controlChannelHz.HasValue || candidate.ControlChannelHz == controlChannelHz.Value);
    }

    private static bool SameRfSeed(RfValidationCandidate left, RfValidationCandidate right) =>
        left.SourceIndex == right.SourceIndex &&
        left.ControlChannelHz == right.ControlChannelHz &&
        left.ErrorHz == right.ErrorHz &&
        GainMatches(left.Gain, right.Gain);

    private static ControlChannelQualityRow? ReadControlChannelQualityRow(ExperimentOutcome outcome)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(outcome.Evidence, EngineConfig.JsonOptions()));
        if (!doc.RootElement.TryGetProperty("evaluatedRow", out var row) || row.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return new ControlChannelQualityRow(
            JsonLong(row, "frequencyHz"),
            JsonBool(row, "known"),
            JsonInt(row, "samples"),
            JsonDouble(row, "avgDecodeRate"),
            JsonDouble(row, "minDecodeRate"),
            JsonDouble(row, "maxDecodeRate"),
            JsonDouble(row, "zeroDecodePct"),
            JsonDouble(row, "lowDecodePct"),
            JsonInt(row, "longestZeroStreak"),
            JsonDouble(row, "score"));
    }

    private static double ScoreRfValidationCandidate(RfValidationCandidate candidate)
    {
        var score = (candidate.SnrDb ?? -40) * 10;
        score -= candidate.Overload ? 600 : 0;
        score -= Math.Abs(candidate.PeakOffsetHz ?? 0) / 5000.0;
        score -= candidate.ClipPct * 30;
        if (string.Equals(candidate.P25Status, "passed", StringComparison.OrdinalIgnoreCase))
            score += 1000;
        else if (string.Equals(candidate.P25Status, "blocked", StringComparison.OrdinalIgnoreCase))
            score -= 700;
        else if (string.Equals(candidate.P25Status, "not_run", StringComparison.OrdinalIgnoreCase))
            score -= 900;
        else if (!string.IsNullOrWhiteSpace(candidate.P25Status))
            score -= 500;
        if (string.Equals(candidate.MetricsStatus, "passed", StringComparison.OrdinalIgnoreCase))
            score += 600;
        else if (string.Equals(candidate.MetricsStatus, "failed", StringComparison.OrdinalIgnoreCase))
            score -= 150;
        if (candidate.MetricsRow != null)
        {
            score += candidate.MetricsRow.AvgDecodeRate * 10;
            score -= candidate.MetricsRow.ZeroDecodePct * 3;
            score += Math.Min(candidate.MetricsRow.Samples, 10) * 8;
        }
        if (string.Equals(candidate.VoiceStatus, "passed", StringComparison.OrdinalIgnoreCase))
            score += 1500;
        else if (string.Equals(candidate.VoiceStatus, "failed", StringComparison.OrdinalIgnoreCase))
            score -= 350;
        else if (string.Equals(candidate.VoiceStatus, "blocked", StringComparison.OrdinalIgnoreCase))
            score -= 600;
        else if (string.Equals(candidate.VoiceStatus, "not_run", StringComparison.OrdinalIgnoreCase))
            score -= 500;
        score += Math.Min(candidate.VoiceRealCalls, 5) * 400;
        if (candidate.VoiceTrial != null)
        {
            score += Math.Min(candidate.VoiceTrial.TrRecorderStarts, 10) * 25;
            score -= candidate.VoiceTrial.NoSourceGrantCount * 4;
            score -= candidate.VoiceTrial.CallstreamNoSampleEnds * 90;
            score -= candidate.VoiceTrial.AvgTuningErrorAbsHz / 35.0;
        }
        return score;
    }

    private static string JsonString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var element) ? JsonElementText(element) : string.Empty;
    }

    private static int JsonInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return 0;
        return TryReadInt(element, out var value) ? value : 0;
    }

    private static long JsonLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric))
            return numeric;
        return element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var textValue)
            ? textValue
            : 0;
    }

    private static double JsonDouble(JsonElement root, string propertyName)
    {
        return JsonNullableDouble(root, propertyName) ?? 0;
    }

    private static double? JsonNullableDouble(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var numeric))
            return numeric;
        return element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var textValue)
            ? textValue
            : null;
    }

    private static bool JsonBool(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var element))
            return false;
        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(element.GetString(), out var value) && value,
            _ => false
        };
    }

    private static string JsonElementText(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.Null => "",
            _ => element.ToString() ?? ""
        };

    private static string SanitizeFileToken(string? value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim();
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        return new string(chars);
    }

    private async Task<ExperimentOutcome> RunControlChannelQualityAsync(RfSurveySessionDto session, RfSurveyProfileDto profile, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.SystemShortName))
            return BlockedOutcome("control_channel_quality", "CC quality requires a selected site/system.", "Select a site before measuring control-channel quality.", "No system short name is selected.", new { profile.SystemShortName });
        if (profile.ControlChannelsHz.Count == 0)
            return BlockedOutcome("control_channel_quality", "CC quality requires known control channels.", "Import/confirm RR or TR ground truth first.", "No control channels are available.", new { profile.ControlChannelsHz });

        long? selectedCc = request.ControlChannelHz ?? profile.ControlChannelsHz.FirstOrDefault();
        var coverage = CheckTrSourceCoverage(profile, request.SourceIndex, selectedCc);
        if (!coverage.Covered)
            return BlockedOutcome(
                "control_channel_quality",
                "CC quality is measured from live trunk-recorder decode summaries.",
                "The selected site control channel must fit inside the selected source tuning window before trunk-recorder is started.",
                coverage.BlockingIssue,
                new { profile.SystemShortName, selectedControlChannelHz = selectedCc, sourceIndex = request.SourceIndex, coverage });

        var duration = Math.Clamp(request.DurationSeconds <= 0 ? 60 : request.DurationSeconds, 15, 900);
        var trState = await QueryTrActiveAsync(ct);
        var trStartOutput = string.Empty;
        if (!trState.Active)
        {
            trStartOutput = await RunServiceHelperAsync("start-tr", ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            trState = await QueryTrActiveAsync(ct);
            if (!trState.Active)
            {
                return new ExperimentOutcome(
                    "failed",
                    "CC quality is measured from live trunk-recorder decode summaries.",
                    "Radio Setup can start trunk-recorder before measuring CC quality.",
                    "trunk-recorder did not become active after the start request.",
                    "trunk-recorder did not become active after the start request.",
                    new { trState, trStartOutput },
                    new { recommendation = "Review trunk-recorder service logs, fix the service/config issue, then rerun TR CC Metrics.", followUps = new[] { "Open System > Trunk Recorder logs.", "Check active TR config.", "Rerun TR CC Metrics." } });
            }
        }

        var start = DateTime.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(duration), ct);
        var end = DateTime.UtcNow;
        var log = await ReadTrJournalAsync(start, end, ct);
        var scopedLog = string.Join('\n', log.Split('\n').Where(line => line.Contains($"[{profile.SystemShortName}]", StringComparison.OrdinalIgnoreCase)));
        var health = TrHealthCollector.BuildSample(profile.SystemShortName, start, end, scopedLog);
        var rows = BuildControlChannelQualityRows(profile, scopedLog).ToList();
        var callStarts = CountIgnoreCase(scopedLog, "Starting P25 Recorder");
        var callConclusions = CountIgnoreCase(scopedLog, "Concluding Recorded Call");
        var updateNotGrant = CountIgnoreCase(scopedLog, "Call was UPDATE not GRANT");
        var noTx = CountIgnoreCase(scopedLog, "No Transmissions were recorded!");
        var retunes = CountIgnoreCase(scopedLog, "Retuning to Control Channel");
        var knownRows = rows.Where(row => row.Known).ToList();
        var focused = selectedCc.HasValue ? rows.FirstOrDefault(row => row.FrequencyHz == selectedCc.Value) : null;
        var evaluated = focused ?? knownRows.OrderByDescending(row => row.Score).FirstOrDefault();
        var aggregateSamples = evaluated?.Samples ?? knownRows.Sum(row => row.Samples);
        var aggregateAvgDecodeRate = evaluated?.AvgDecodeRate
            ?? (knownRows.Sum(row => row.Samples) == 0 ? 0 : knownRows.Sum(row => row.AvgDecodeRate * row.Samples) / knownRows.Sum(row => row.Samples));
        var aggregateZeroDecodePct = evaluated?.ZeroDecodePct
            ?? (knownRows.Sum(row => row.Samples) == 0 ? 0 : knownRows.Sum(row => row.ZeroDecodePct * row.Samples) / knownRows.Sum(row => row.Samples));
        var enoughSamples = knownRows.Any(row => row.Samples >= Math.Max(3, duration / 30));
        var strongEnough = evaluated is { Known: true, Samples: >= 3, AvgDecodeRate: >= 10, ZeroDecodePct: <= 10 };
        var status = strongEnough ? "passed" : enoughSamples ? "failed" : "failed";
        var blocker = strongEnough ? "" : enoughSamples
            ? selectedCc.HasValue
                ? $"Selected control channel {FormatHz(selectedCc.Value)} did not meet the CC quality threshold in this window."
                : "No known control channel met the CC quality threshold in this window."
            : "Not enough control-channel summary samples were captured in this window.";
        var evidence = new
        {
            session.Id,
            profile.SystemShortName,
            selectedControlChannelHz = selectedCc,
            durationSeconds = duration,
            windowStartUtc = start,
            windowEndUtc = end,
            trState,
            trStartOutput,
            ccRows = rows,
            evaluatedRow = evaluated,
            aggregate = new
            {
                CcSummaryDecodeLines = aggregateSamples,
                CcSummaryAvgDecodeRate = aggregateAvgDecodeRate,
                CcSummaryDecodeZeroPct = aggregateZeroDecodePct,
                RawCcSummaryDecodeLines = health.CcSummaryDecodeLines,
                health.LowDecodeWarningLines,
                retunes,
                callStarts,
                callConclusions,
                updateNotGrant,
                noTx
            }
        };
        await WriteArtifactAsync(session.ArtifactPath, $"cc-quality-{DateTime.UtcNow:yyyyMMddHHmmss}.json", evidence, ct);
        return new ExperimentOutcome(
            status,
            "Known control channels should produce stable periodic TR decode summaries before error/gain sweeps are considered meaningful.",
            "trunk-recorder running; selected site has setup/RR control-channel ground truth.",
            strongEnough
                ? $"CC quality passed. {FormatHz(evaluated!.FrequencyHz)} averaged {evaluated.AvgDecodeRate:F1} msg/sec with {evaluated.ZeroDecodePct:F1}% zero-decode across {evaluated.Samples} sample(s)."
                : blocker,
            blocker,
            evidence,
            new
            {
                recommendation = strongEnough
                    ? "Proceed to SDR inventory/P25 probe or use this CC as the primary candidate for follow-up sweeps."
                    : "Do not rank error/gain sweeps as conclusive until a known control channel has stable decode samples.",
                selectedControlChannelHz = selectedCc,
                evaluatedRow = evaluated,
                strongest = knownRows.OrderByDescending(row => row.Score).FirstOrDefault(),
                followUps = strongEnough
                    ? new[] { "Run P25 probe against the strongest CC.", "Run error/gain sweep only after CC samples are stable." }
                    : new[] { "Extend the CC quality window.", "Probe each alternate CC.", "Check antenna aim/RF path and source center coverage.", "Review TR config for off-center source mapping." }
            });
    }

    private async Task<string> ReadTrJournalAsync(DateTime startUtc, DateTime endUtc, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return string.Empty;
        var since = startUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var until = endUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var result = await RunCaptureAsync("journalctl", $"-u {TrUnitName()} --since {Quote(since)} --until {Quote(until)} --no-pager", ct);
        return result.Stdout;
    }

    private static IEnumerable<ControlChannelQualityRow> BuildControlChannelQualityRows(RfSurveyProfileDto profile, string scopedLog)
    {
        var known = profile.ControlChannelsHz.ToHashSet();
        var rows = new Dictionary<long, List<double>>();
        foreach (Match match in CcSummaryLineRegex.Matches(scopedLog))
        {
            if (!string.Equals(match.Groups["system"].Value, profile.SystemShortName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!double.TryParse(match.Groups["freq"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
                continue;
            if (!double.TryParse(match.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                continue;
            var hz = (long)Math.Round(mhz * 1_000_000d);
            if (!rows.TryGetValue(hz, out var rates))
            {
                rates = [];
                rows[hz] = rates;
            }
            rates.Add(rate);
        }

        foreach (var cc in known)
            rows.TryAdd(cc, []);

        foreach (var pair in rows.OrderBy(row => row.Key))
        {
            var rates = pair.Value;
            var samples = rates.Count;
            var zero = rates.Count(rate => Math.Abs(rate) < 0.0001);
            var low = rates.Count(rate => rate > 0 && rate < 10);
            var avg = samples == 0 ? 0 : rates.Average();
            var min = samples == 0 ? 0 : rates.Min();
            var max = samples == 0 ? 0 : rates.Max();
            var zeroPct = samples == 0 ? 0 : zero * 100.0 / samples;
            var lowPct = samples == 0 ? 0 : low * 100.0 / samples;
            var longestZero = LongestStreak(rates, rate => Math.Abs(rate) < 0.0001);
            var score = samples == 0 ? -1_000_000 : avg * 1000 - zeroPct * 100 - longestZero * 50 + samples;
            yield return new ControlChannelQualityRow(pair.Key, known.Contains(pair.Key), samples, avg, min, max, zeroPct, lowPct, longestZero, score);
        }
    }

    private static ControlChannelQualityRow BuildControlChannelQualityRowFromAnalysis(RfSurveyProfileDto profile, RfValidationCandidate candidate, CallQualityTrWindowAnalysis analysis)
    {
        var known = profile.ControlChannelsHz.Contains(candidate.ControlChannelHz);
        var samples = analysis.DecodeLines;
        var avg = analysis.AvgDecodeRate;
        var zeroPct = analysis.DecodeZeroPct;
        var lowPct = avg > 0 && avg < 10 ? 100.0 : 0.0;
        var longestZero = samples > 0 && zeroPct >= 99.9 ? samples : 0;
        var score = samples == 0 ? -1_000_000 : avg * 1000 - zeroPct * 100 - longestZero * 50 + samples;
        return new ControlChannelQualityRow(candidate.ControlChannelHz, known, samples, avg, avg, avg, zeroPct, lowPct, longestZero, score);
    }

    private static ControlChannelQualityRow BuildControlChannelQualityRowFromAnalysis(RfSurveyProfileDto profile, CallQualityTrWindowAnalysis analysis)
    {
        var controlChannelHz = profile.ControlChannelsHz.FirstOrDefault();
        var samples = analysis.DecodeLines;
        var avg = analysis.AvgDecodeRate;
        var zeroPct = analysis.DecodeZeroPct;
        var lowPct = avg > 0 && avg < 10 ? 100.0 : 0.0;
        var longestZero = samples > 0 && zeroPct >= 99.9 ? samples : 0;
        var score = samples == 0 ? -1_000_000 : avg * 1000 - zeroPct * 100 - longestZero * 50 + samples;
        return new ControlChannelQualityRow(controlChannelHz, controlChannelHz > 0 && profile.ControlChannelsHz.Contains(controlChannelHz), samples, avg, avg, avg, zeroPct, lowPct, longestZero, score);
    }

    private static string BuildOptionalVoiceCaveat(ControlChannelQualityRow metricsRow) =>
        $"No audio-bearing calls were observed in this optional voice window. TR metrics passed: {FormatHz(metricsRow.FrequencyHz)} averaged {metricsRow.AvgDecodeRate:F1} msg/sec with {metricsRow.ZeroDecodePct:F1}% zero-decode across {metricsRow.Samples} sample(s). Call Quality may be unable to test transcription or long-run call stability until real traffic appears.";

    private static bool IsPassingControlChannelMetrics(ControlChannelQualityRow row) =>
        row is { Known: true, Samples: >= 3, AvgDecodeRate: >= 10, ZeroDecodePct: <= 10 };

    private static int LongestStreak(IEnumerable<double> values, Func<double, bool> predicate)
    {
        var longest = 0;
        var current = 0;
        foreach (var value in values)
        {
            current = predicate(value) ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }
        return longest;
    }

    private static int CountIgnoreCase(string haystack, string needle) =>
        Regex.Matches(haystack, Regex.Escape(needle), RegexOptions.IgnoreCase).Count;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static string FormatHz(long value) =>
        value >= 1_000_000 ? $"{value / 1_000_000d:F6} MHz" : $"{value:N0} Hz";

    private sealed record ControlChannelQualityRow(
        long FrequencyHz,
        bool Known,
        int Samples,
        double AvgDecodeRate,
        double MinDecodeRate,
        double MaxDecodeRate,
        double ZeroDecodePct,
        double LowDecodePct,
        int LongestZeroStreak,
        double Score);

    private sealed record RfValidationSeedKey(int SourceIndex, long ControlChannelHz, string Gain)
    {
        public static RfValidationSeedKey From(RfValidationCandidate candidate) =>
            new(candidate.SourceIndex, candidate.ControlChannelHz, candidate.Gain ?? string.Empty);
    }

    private sealed record P25ProbeOverrides(string Gain, int? ErrorHz, int? SampleRateHz);

    private sealed record RfValidationCandidate
    {
        public string Id { get; init; } = string.Empty;
        public int SourceIndex { get; init; }
        public string SdrType { get; init; } = string.Empty;
        public string Serial { get; init; } = string.Empty;
        public string Device { get; init; } = string.Empty;
        public string SystemShortName { get; init; } = string.Empty;
        public string SiteLabel { get; init; } = string.Empty;
        public long ControlChannelHz { get; init; }
        public string Gain { get; init; } = string.Empty;
        public int ErrorHz { get; init; }
        public int ErrorOffsetHz { get; init; }
        public string RfStatus { get; init; } = string.Empty;
        public string RfIssue { get; init; } = string.Empty;
        public double? PeakDb { get; init; }
        public double? NoiseFloorDb { get; init; }
        public double? SnrDb { get; init; }
        public double? PeakOffsetHz { get; init; }
        public double ClipPct { get; init; }
        public bool Overload { get; init; }
        public string P25Status { get; init; } = string.Empty;
        public bool P25Frames { get; init; }
        public string P25Summary { get; init; } = string.Empty;
        public string P25Demod { get; init; } = string.Empty;
        public string P25Command { get; init; } = string.Empty;
        public int? P25ExitCode { get; init; }
        public string P25Output { get; init; } = string.Empty;
        public IReadOnlyList<RfValidationP25Attempt> P25Attempts { get; init; } = [];
        public string MetricsStatus { get; init; } = string.Empty;
        public string MetricsSummary { get; init; } = string.Empty;
        public ControlChannelQualityRow? MetricsRow { get; init; }
        public string VoiceStatus { get; init; } = string.Empty;
        public string VoiceSummary { get; init; } = string.Empty;
        public int VoiceTotalCalls { get; init; }
        public int VoiceRealCalls { get; init; }
        public CallQualityTrWindowAnalysis? VoiceTrial { get; init; }
        public double Score { get; init; }
    }

    private sealed record RfValidationP25Attempt(
        string Demod,
        string Status,
        string Command,
        int? ExitCode,
        string Output,
        string Summary);

    private static string BuildRfValidationTechnicalBlocker(IReadOnlyList<RfValidationCandidate> candidates)
    {
        var blockers = candidates
            .Select(CandidateTechnicalBlocker)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        return blockers.Count == 0 ? string.Empty : string.Join(" ", blockers);
    }

    private static string CandidateTechnicalBlocker(RfValidationCandidate candidate)
    {
        if (string.Equals(candidate.MetricsStatus, "blocked", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(candidate.MetricsSummary))
            return $"TR metrics blocked: {candidate.MetricsSummary}";
        if (string.Equals(candidate.VoiceStatus, "blocked", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(candidate.VoiceSummary))
            return $"Voice blocked: {candidate.VoiceSummary}";
        if (string.Equals(candidate.P25Status, "blocked", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(candidate.P25Summary))
            return $"P25 blocked: {candidate.P25Summary}";
        return string.Empty;
    }

    private sealed record CallQualityReadiness(
        IReadOnlyList<string> Blockers,
        IReadOnlyList<string> Warnings,
        RfValidationCandidate? LatestRfCandidate,
        object? LiveSource,
        object? VoiceCoverage);

    private sealed record CallQualityTrWindowAnalysis(
        int DecodeLines,
        double AvgDecodeRate,
        double DecodeZeroPct,
        int Retunes,
        int TrRecorderStarts,
        int TrCallsConcluded,
        int TrUnableSourceMentions,
        int NoSourceGrantCount,
        int EncryptedGrantCount,
        int CallstreamNoSampleEnds,
        int TuningErrSamples,
        double AvgTuningErrorAbsHz,
        double MaxTuningErrorAbsHz,
        IReadOnlyList<FrequencyCount> RecordedFrequencies,
        IReadOnlyList<FrequencyCount> NoSourceFrequencies,
        IReadOnlyList<FrequencyCount> EncryptedFrequencies,
        IReadOnlyList<string> Recommendations,
        IReadOnlyList<string> SampleLines);

    private sealed record VoiceCandidateTrialResult(
        string Status,
        string Summary,
        int TotalCalls,
        int RealCalls,
        CallQualityTrWindowAnalysis? Analysis,
        string MetricsStatus,
        string MetricsSummary,
        ControlChannelQualityRow? MetricsRow)
    {
        public static VoiceCandidateTrialResult Blocked(string summary) => new("blocked", summary, 0, 0, null, "blocked", summary, null);
    }

    private sealed record CandidateTrMetricsResult(
        string Status,
        string Summary,
        ControlChannelQualityRow? Row)
    {
        public static CandidateTrMetricsResult Blocked(string summary) => new("blocked", summary, null);
    }

    private sealed record CandidatePersistenceResult(
        string CandidateId,
        string CandidatePath,
        string BackupPath,
        string ServiceOutput,
        DateTime AppliedAtUtc);

    private sealed record RfValidationSiteReadiness(
        string SystemShortName,
        string Label,
        bool Monitorable,
        long? BestControlChannelHz,
        int? SourceIndex,
        string Gain,
        int? ErrorHz,
        string BestCandidateId,
        string Stage,
        string Issue,
        IReadOnlyList<string> CandidateIds);

    private sealed record FrequencyCount(string Frequency, int Count);

    private sealed record SourceCoverageWindow(int SourceIndex, long CenterHz, int SampleRate, long LowHz, long HighHz);

    private sealed record AirspyStageGains(int Lna, int Mix, int If);

    private sealed record VoiceCoverageEvidence(
        int TotalVoiceFrequencies,
        int CoveredVoiceFrequencies,
        IReadOnlyList<long> UncoveredVoiceFrequencies,
        IReadOnlyList<SourceCoverageWindow> SourceWindows);

    private sealed record StaleP25CleanupResult(
        string Before,
        string After,
        string Output,
        string BlockingIssue);

    private sealed record TrSourceCoverageCheck(
        bool Covered,
        string BlockingIssue,
        IReadOnlyList<TrSourceCoverageRow> Sources);

    private sealed record TrSourceCoverageRow(
        int Index,
        string Serial,
        long CenterHz,
        int SampleRate,
        long LowHz,
        long HighHz,
        bool Covers);

    private sealed record RfPowerScanRow(
        int Index,
        string SdrType,
        string Serial,
        string Device,
        long CenterHz,
        int SampleRate,
        int CaptureSampleRate,
        int ErrorHz,
        string Gain,
        long ControlChannelHz,
        int DurationSeconds,
        string Command,
        int ExitCode,
        string Output,
        string RawPath,
        long Bytes,
        string Status,
        string Issue,
        double? PeakDb,
        double? NoiseFloorDb,
        double? SnrDb,
        double? PeakOffsetHz,
        double ClipPct,
        bool Overload,
        string Sparkline,
        double? StrongestPeakDb,
        double? StrongestPeakOffsetHz);

    private static string SummarizeRfPowerBlocker(RfPowerScanRow row)
    {
        var output = TrimOutput(row.Output ?? string.Empty, 220).Replace('\n', ' ').Replace('\r', ' ').Trim();
        var issue = (row.Issue ?? string.Empty).Trim();
        var detail = !string.IsNullOrWhiteSpace(output)
            ? output
            : !string.IsNullOrWhiteSpace(issue)
                ? issue
                : "RF capture failed.";
        var rateDetail = row.CaptureSampleRate != row.SampleRate
            ? $"requested rate {row.SampleRate:N0}, capture rate {row.CaptureSampleRate:N0}"
            : $"rate {row.SampleRate:N0}";
        return $"Source {row.Index}, {FormatHz(row.ControlChannelHz)}, gain {row.Gain}, {rateDetail}: {detail}";
    }

    private sealed record RfPowerAnalysis(
        bool Completed,
        string Issue,
        double? PeakDb,
        double? NoiseFloorDb,
        double? SnrDb,
        double? PeakOffsetHz,
        double ClipPct,
        bool Overload,
        string Sparkline,
        double? StrongestPeakDb,
        double? StrongestPeakOffsetHz);

    private sealed record RtlDeviceSelector(string Argument, string Issue, string DeviceArgument = "");

    private sealed class WaterfallRuntime
    {
        private const int MaxFrameHistory = 90;
        private readonly object _sync = new();
        private readonly Queue<RfSurveyWaterfallFrameDto> _frames = new();
        private DateTime _lastConsumedAtUtc = DateTime.UtcNow;
        private int _sequence;

        public WaterfallRuntime(
            string surveyId,
            RfSurveyProfileDto profile,
            RfSurveySourceDto source,
            long centerHz,
            int sampleRate,
            int binCount,
            int captureMilliseconds,
            int refreshMilliseconds,
            bool trWasActive,
            string trStopOutput)
        {
            SurveyId = surveyId;
            Profile = profile;
            Source = source;
            CenterHz = centerHz;
            SampleRate = sampleRate;
            BinCount = binCount;
            CaptureMilliseconds = captureMilliseconds;
            RefreshMilliseconds = refreshMilliseconds;
            TrWasActive = trWasActive;
            TrStopOutput = trStopOutput;
        }

        public string SurveyId { get; }
        public RfSurveyProfileDto Profile { get; }
        public RfSurveySourceDto Source { get; }
        public long CenterHz { get; }
        public int SampleRate { get; }
        public int BinCount { get; }
        public int CaptureMilliseconds { get; }
        public int RefreshMilliseconds { get; }
        public bool TrWasActive { get; }
        public string TrStopOutput { get; }
        public string TrRestartOutput { get; set; } = "";
        public string TrRestartError { get; set; } = "";
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public DateTime? UpdatedAtUtc { get; private set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }
        public string Status { get; private set; } = "starting";
        public string Message { get; private set; } = "Starting waterfall.";
        public RfSurveyWaterfallFrameDto? Frame { get; private set; }

        public int NextSequence() => Interlocked.Increment(ref _sequence);

        public void MarkConsumed()
        {
            lock (_sync)
            {
                _lastConsumedAtUtc = DateTime.UtcNow;
            }
        }

        public bool IsUnconsumedFor(TimeSpan interval)
        {
            lock (_sync)
            {
                return DateTime.UtcNow - _lastConsumedAtUtc >= interval;
            }
        }

        public void SetFrame(RfSurveyWaterfallFrameDto frame)
        {
            lock (_sync)
            {
                Frame = frame;
                if (frame.PowersDb.Count > 0)
                {
                    _frames.Enqueue(frame);
                    while (_frames.Count > MaxFrameHistory)
                        _frames.Dequeue();
                }
                UpdatedAtUtc = frame.CapturedAtUtc;
                var hasPowers = frame.PowersDb.Count > 0;
                Status = hasPowers ? "running" : "failed";
                Message = hasPowers ? "Waterfall running." : FirstNonEmpty(frame.Output, "Waterfall capture failed.");
            }
        }

        public void SetMessage(string status, string message)
        {
            lock (_sync)
            {
                Status = status;
                Message = message;
                UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        public RfSurveyWaterfallStatusDto ToDto(bool active, bool includeHistory = false)
        {
            lock (_sync)
            {
                var frames = includeHistory ? _frames.ToArray() : null;
                return new RfSurveyWaterfallStatusDto(
                    active,
                    active ? Status : "stopped",
                    active ? Message : string.IsNullOrWhiteSpace(TrRestartError) ? "Waterfall stopped." : $"Waterfall stopped; TR restart failed: {TrRestartError}",
                    Source.Index,
                    Source.SdrType,
                    CenterHz,
                    SampleRate,
                    Source.Gain,
                    BinCount,
                    StartedAtUtc,
                    UpdatedAtUtc,
                    Frame,
                    TrWasActive,
                    TrStopOutput,
                    TrRestartOutput,
                    TrRestartError,
                    frames);
            }
        }
    }

    private sealed class WaterfallByteRing
    {
        private readonly object _sync = new();
        private readonly byte[] _buffer;
        private int _writeIndex;
        private int _count;

        public WaterfallByteRing(int capacity)
        {
            _buffer = new byte[Math.Max(4096, capacity)];
        }

        public void Append(byte[] bytes, int count)
        {
            if (count <= 0)
                return;
            lock (_sync)
            {
                if (count >= _buffer.Length)
                {
                    Buffer.BlockCopy(bytes, count - _buffer.Length, _buffer, 0, _buffer.Length);
                    _writeIndex = 0;
                    _count = _buffer.Length;
                    return;
                }

                var first = Math.Min(count, _buffer.Length - _writeIndex);
                Buffer.BlockCopy(bytes, 0, _buffer, _writeIndex, first);
                var remaining = count - first;
                if (remaining > 0)
                    Buffer.BlockCopy(bytes, first, _buffer, 0, remaining);
                _writeIndex = (_writeIndex + count) % _buffer.Length;
                _count = Math.Min(_buffer.Length, _count + count);
            }
        }

        public byte[] SnapshotLatest(int requestedBytes)
        {
            lock (_sync)
            {
                var length = Math.Min(Math.Max(0, requestedBytes), _count);
                if (length == 0)
                    return [];
                length -= length % 4;
                if (length <= 0)
                    return [];
                var result = new byte[length];
                var start = (_writeIndex - length + _buffer.Length) % _buffer.Length;
                var first = Math.Min(length, _buffer.Length - start);
                Buffer.BlockCopy(_buffer, start, result, 0, first);
                var remaining = length - first;
                if (remaining > 0)
                    Buffer.BlockCopy(_buffer, 0, result, first, remaining);
                return result;
            }
        }
    }

    private sealed class ProcessOutputTail
    {
        private readonly object _sync = new();
        private readonly int _maxChars;
        private string _text = "";

        public ProcessOutputTail(int maxChars)
        {
            _maxChars = Math.Max(256, maxChars);
        }

        public string Text
        {
            get
            {
                lock (_sync)
                    return _text;
            }
        }

        public void Append(char[] chars, int count)
        {
            if (count <= 0)
                return;
            lock (_sync)
            {
                _text += new string(chars, 0, count);
                if (_text.Length > _maxChars)
                    _text = _text[^_maxChars..];
            }
        }
    }

    private static bool IsRfPeakOffTarget(double? peakOffsetHz) =>
        peakOffsetHz.HasValue && Math.Abs(peakOffsetHz.Value) > 25_000;

    private static string BuildRfPowerScanCommand(RfSurveySourceDto source, long frequencyHz, int sampleRate, int sampleCount, string rawPath, bool isAirspy, string rtlDeviceArg) =>
        BuildRfPowerScanCommandWithSerialPinning(source, frequencyHz, sampleRate, sampleCount, rawPath, isAirspy, rtlDeviceArg, pinAirspySerial: true);

    private static string BuildRfPowerScanCommandWithSerialPinning(RfSurveySourceDto source, long frequencyHz, int sampleRate, int sampleCount, string rawPath, bool isAirspy, string rtlDeviceArg, bool pinAirspySerial)
    {
        var output = ShellQuote(rawPath);
        if (isAirspy)
        {
            var gain = NormalizeAirspyRxGain(true, source.Gain);
            var airspyGainArg = string.IsNullOrWhiteSpace(gain) ? "" : $" -g {gain}";
            var serial = pinAirspySerial ? NormalizeAirspyRxSerial(FirstNonEmpty(source.Serial, ExtractAirspySerial(source.Device))) : string.Empty;
            var serialArg = string.IsNullOrWhiteSpace(serial) ? "" : $" -s {ShellQuote(serial)}";
            var frequencyMhz = (frequencyHz / 1_000_000d).ToString("0.######", CultureInfo.InvariantCulture);
            return $"airspy_rx{serialArg} -r {output} -f {frequencyMhz} -a {sampleRate}{airspyGainArg} -n {sampleCount}";
        }

        var gainText = NumericText(source.Gain);
        var gainArg = string.IsNullOrWhiteSpace(gainText) ? "" : $" -g {gainText}";
        var ppm = frequencyHz > 0 ? (int)Math.Round(-source.ErrorHz / (frequencyHz / 1_000_000d), MidpointRounding.AwayFromZero) : 0;
        var ppmArg = ppm == 0 ? "" : $" -p {ppm}";
        return $"rtl_sdr{rtlDeviceArg} -f {frequencyHz} -s {sampleRate}{gainArg}{ppmArg} -n {sampleCount} {output}";
    }

    private static Process? StartWaterfallStream(RfSurveySourceDto source, long frequencyHz, int sampleRate, bool isAirspy, string rtlDeviceArg, bool pinAirspySerial, string? streamOutputPath = null)
    {
        var psi = new ProcessStartInfo(isAirspy ? "airspy_rx" : "rtl_sdr")
        {
            RedirectStandardOutput = string.IsNullOrWhiteSpace(streamOutputPath),
            RedirectStandardError = true,
            UseShellExecute = false
        };

        if (isAirspy)
        {
            var gain = NormalizeAirspyRxGain(true, source.Gain);
            var serial = pinAirspySerial ? NormalizeAirspyRxSerial(FirstNonEmpty(source.Serial, ExtractAirspySerial(source.Device))) : string.Empty;
            var frequencyMhz = (frequencyHz / 1_000_000d).ToString("0.######", CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(serial))
            {
                psi.ArgumentList.Add("-s");
                psi.ArgumentList.Add(serial);
            }
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(streamOutputPath) ? "-" : streamOutputPath);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(frequencyMhz);
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(gain))
            {
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add(gain);
            }
        }
        else
        {
            var gainText = NumericText(source.Gain);
            var ppm = frequencyHz > 0 ? (int)Math.Round(-source.ErrorHz / (frequencyHz / 1_000_000d), MidpointRounding.AwayFromZero) : 0;
            if (!string.IsNullOrWhiteSpace(rtlDeviceArg))
            {
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(rtlDeviceArg);
            }
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(frequencyHz.ToString(CultureInfo.InvariantCulture));
            psi.ArgumentList.Add("-s");
            psi.ArgumentList.Add(sampleRate.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(gainText))
            {
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add(gainText);
            }
            if (ppm != 0)
            {
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(ppm.ToString(CultureInfo.InvariantCulture));
            }
            psi.ArgumentList.Add("-");
        }

        return Process.Start(psi);
    }

    private static int AirspyCaptureSampleRate(int requestedSampleRate)
    {
        return AirspyRuntimeSampleRate(requestedSampleRate, [2_500_000, 3_000_000, 6_000_000, 10_000_000]);
    }

    private static int TrRuntimeSampleRate(RfSurveyProfileDto profile, RfSurveySourceDto? source, int requestedSampleRate)
    {
        if (requestedSampleRate <= 0 || !IsAirspySource(source))
            return requestedSampleRate;
        return AirspyRuntimeSampleRate(requestedSampleRate, AirspySampleRateOptionsForSource(profile, source));
    }

    private static IReadOnlyList<int> AirspySampleRateOptionsForSource(RfSurveyProfileDto profile, RfSurveySourceDto? source)
    {
        if (source == null)
            return [];
        var device = profile.Devices.FirstOrDefault(row =>
            row.Index == source.Index ||
            (!string.IsNullOrWhiteSpace(source.Serial) && string.Equals(row.Serial, source.Serial, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(source.Device) && string.Equals(row.UsbLine, source.Device, StringComparison.OrdinalIgnoreCase)));
        var options = (device?.SampleRateOptions ?? [])
            .Where(rate => rate > 0)
            .Distinct()
            .Order()
            .ToList();
        if (options.Count == 0 && device?.DefaultSampleRate > 0)
            options.Add(device.DefaultSampleRate);
        return options;
    }

    private static string NormalizeAirspyRxGain(bool isAirspy, string? gain)
    {
        var normalized = NumericText(gain);
        if (!isAirspy || string.IsNullOrWhiteSpace(normalized))
            return normalized;
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return normalized;
        return Math.Clamp((int)Math.Round(parsed, MidpointRounding.AwayFromZero), 0, 21).ToString(CultureInfo.InvariantCulture);
    }

    private static string? NormalizeTrSourceDeviceArgs(RfSurveyProfileDto profile, RfSurveySourceDto? source)
    {
        if (!IsAirspySource(source))
            return source?.Device;
        if (profile.Sources.Count(IsAirspySource) > 1)
            return source?.Device;
        return NormalizeAirspyDeviceSelector(source?.Device, pinSerial: false);
    }

    private static string NormalizeAirspyDeviceSelector(string? device, bool pinSerial)
    {
        var trimmed = (device ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "airspy";
        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count == 0 || !parts[0].Contains("airspy", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        if (!pinSerial)
            parts[0] = "airspy";
        return string.Join(',', parts);
    }

    private static int AirspyRuntimeSampleRate(int requestedSampleRate, IReadOnlyList<int>? supportedSampleRates)
    {
        var rates = (supportedSampleRates is { Count: > 0 } ? supportedSampleRates : [2_500_000, 3_000_000, 6_000_000, 10_000_000])
            .Where(rate => rate > 0)
            .Distinct()
            .Order()
            .ToList();
        if (requestedSampleRate <= 0)
            return rates.FirstOrDefault();
        if (rates.Contains(requestedSampleRate))
            return requestedSampleRate;
        var nextHigher = rates.FirstOrDefault(rate => rate >= requestedSampleRate);
        if (nextHigher > 0)
            return nextHigher;
        return rates.LastOrDefault(requestedSampleRate);
    }

    private static RtlDeviceSelector ResolveRtlDeviceSelector(RfSurveySourceDto source, IReadOnlyDictionary<string, int> indexesBySerial)
    {
        var serial = FirstNonEmpty(source.Serial, ExtractRtlSerial(source.Device));
        if (string.IsNullOrWhiteSpace(serial))
            return new("", "");
        if (indexesBySerial.TryGetValue(serial, out var index))
            return new($" -d {index}", "", index.ToString(CultureInfo.InvariantCulture));
        if (!Regex.IsMatch(serial, @"^\d+$"))
            return new($" -d {ShellQuote(serial)}", "", serial);
        return new("", $"RTL-SDR serial {serial} is numeric and could not be mapped to a host device index from rtl_test output. Numeric serials are ambiguous to rtl_sdr, so the RF scan was not run for source {source.Index}.");
    }

    private static string ExtractRtlSerial(string? device)
    {
        var match = Regex.Match(device ?? string.Empty, @"rtl[=:](?<serial>[^,\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["serial"].Value.Trim() : string.Empty;
    }

    private static string ExtractAirspySerial(string? device)
    {
        var match = Regex.Match(device ?? string.Empty, @"airspy[=:](?<serial>[^,\s]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["serial"].Value.Trim() : string.Empty;
    }

    private static string NormalizeAirspyRxSerial(string? serial)
    {
        var value = (serial ?? string.Empty).Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return value;
        return Regex.IsMatch(value, "^[A-Fa-f0-9]{16}$") ? "0x" + value : value;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return string.Empty;
    }

    private static async Task<Dictionary<string, int>> ReadRtlIndexesBySerialAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows())
            return map;
        var result = await RunCaptureAsync("bash", "-lc \"timeout 10 rtl_test -t 2>&1 || true\"", ct);
        foreach (Match match in Regex.Matches(result.Stdout, @"^\s*(?<index>\d+):\s+.*?\bSN:\s*(?<serial>\S+)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (int.TryParse(match.Groups["index"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                map[match.Groups["serial"].Value.Trim()] = index;
        }
        return map;
    }

    private static string NumericText(string? value)
    {
        value = (value ?? string.Empty).Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("0.###", CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static RfSurveySourceDto? SelectWaterfallSource(RfSurveyProfileDto profile, RfSurveyWaterfallStartRequest request)
    {
        if (request.SourceIndex.HasValue)
        {
            var selected = profile.Sources.FirstOrDefault(source => source.Index == request.SourceIndex.Value);
            if (selected != null)
                return selected;
        }
        if (request.FrequencyHz is > 0)
            return SelectSourceForFrequency(profile, request.FrequencyHz.Value);
        var selectedIndexes = profile.SelectedSourceIndexes.Count > 0 ? profile.SelectedSourceIndexes : [];
        return profile.Sources.FirstOrDefault(source => selectedIndexes.Contains(source.Index)) ?? profile.Sources.FirstOrDefault();
    }

    private async Task RunWaterfallStreamAsync(WaterfallRuntime runtime, bool isAirspy, RtlDeviceSelector rtlSelector, bool pinAirspySerial)
    {
        var bytesPerSample = isAirspy ? 4 : 2;
        var frameBytes = Math.Max(256, runtime.BinCount) * bytesPerSample;
        var ring = new WaterfallByteRing(Math.Max(frameBytes * 24, runtime.SampleRate * bytesPerSample));
        var errorTail = new ProcessOutputTail(4096);
        string? fifoPath = null;
        if (isAirspy && !OperatingSystem.IsWindows())
        {
            var outputDir = Path.Combine(ArtifactRoot, runtime.SurveyId, "waterfall-live");
            Directory.CreateDirectory(outputDir);
            fifoPath = Path.Combine(outputDir, $"waterfall-{Guid.NewGuid():N}.pipe");
            var fifoResult = await RunCaptureAsync("bash", "-lc " + Quote($"rm -f {ShellQuote(fifoPath)} && mkfifo {ShellQuote(fifoPath)}"), runtime.Cancellation.Token);
            if (fifoResult.ExitCode != 0)
            {
                runtime.SetMessage("failed", $"Unable to create waterfall stream pipe: {TrimOutput(fifoResult.Stdout, 240)}");
                return;
            }
        }

        using var process = StartWaterfallStream(runtime.Source, runtime.CenterHz, runtime.SampleRate, isAirspy, rtlSelector.DeviceArgument, pinAirspySerial, fifoPath);
        if (process == null)
        {
            runtime.SetMessage("failed", "Unable to start waterfall capture process.");
            return;
        }

        var readTask = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            await using var fifoStream = string.IsNullOrWhiteSpace(fifoPath)
                ? null
                : new FileStream(fifoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length, FileOptions.Asynchronous);
            var stream = fifoStream ?? process.StandardOutput.BaseStream;
            while (!runtime.Cancellation.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), runtime.Cancellation.Token);
                if (read <= 0)
                    break;
                ring.Append(buffer, read);
            }
        }, CancellationToken.None);
        var errorTask = Task.Run(async () =>
        {
            var buffer = new char[1024];
            while (!runtime.Cancellation.IsCancellationRequested)
            {
                var read = await process.StandardError.ReadAsync(buffer.AsMemory(0, buffer.Length), runtime.Cancellation.Token);
                if (read <= 0)
                    break;
                errorTail.Append(buffer, read);
            }
        }, CancellationToken.None);

        try
        {
            var firstFrameDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!runtime.Cancellation.IsCancellationRequested)
            {
                if (runtime.IsUnconsumedFor(WaterfallConsumeTimeout))
                {
                    runtime.SetMessage("stopping", $"Waterfall auto-stopped after {WaterfallConsumeTimeout.TotalMinutes:N0} minutes without UI consumption.");
                    runtime.Cancellation.Cancel();
                    return;
                }

                var snapshot = ring.SnapshotLatest(frameBytes);
                if (snapshot.Length >= frameBytes)
                {
                    var output = TrimOutput(errorTail.Text, 320);
                    var analysis = AnalyzeWaterfallSamples(runtime.NextSequence(), snapshot, runtime.CenterHz, runtime.SampleRate, runtime.BinCount, isAirspy, output);
                    runtime.SetFrame(analysis);
                    firstFrameDeadline = DateTime.UtcNow.AddSeconds(5);
                    await Task.Delay(TimeSpan.FromMilliseconds(runtime.RefreshMilliseconds), runtime.Cancellation.Token);
                    continue;
                }

                if (process.HasExited)
                {
                    var output = TrimOutput(errorTail.Text, 420);
                    runtime.SetMessage("failed", $"Waterfall capture exited {process.ExitCode}. {output}".Trim());
                    return;
                }
                if (DateTime.UtcNow > firstFrameDeadline)
                {
                    runtime.SetMessage("failed", "Waterfall capture did not produce IQ samples. Verify that the SDR tool can stream to stdout.");
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(30), runtime.Cancellation.Token);
            }
        }
        finally
        {
            TryKillProcessTree(process);
            await WaitForProcessExitAfterKillAsync(process);
            try { await readTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            try { await errorTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            if (!string.IsNullOrWhiteSpace(fifoPath))
            {
                try { File.Delete(fifoPath); } catch { }
            }
        }
    }

    private static int NormalizeWaterfallBinCount(int requested)
    {
        var clamped = Math.Clamp(requested, 64, 4096);
        var size = 64;
        while (size * 2 <= clamped)
            size *= 2;
        return size;
    }

    private static int HighestPowerOfTwoAtMost(int value)
    {
        var size = 1;
        while (size * 2 <= value)
            size *= 2;
        return size;
    }

    private static void FftInPlace(Complex[] values)
    {
        var n = values.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (values[i], values[j]) = (values[j], values[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += length)
            {
                var w = Complex.One;
                var half = length >> 1;
                for (var j = 0; j < half; j++)
                {
                    var even = values[i + j];
                    var odd = values[i + j + half] * w;
                    values[i + j] = even + odd;
                    values[i + j + half] = even - odd;
                    w *= step;
                }
            }
        }
    }

    private static RfSurveyWaterfallFrameDto AnalyzeWaterfallFrame(int sequence, string path, long centerHz, int sampleRate, int binCount, bool isAirspy, string output)
    {
        if (!File.Exists(path))
            return EmptyWaterfallFrame(sequence, centerHz, sampleRate, binCount, $"IQ capture file was not created. {output}".Trim());
        var file = new FileInfo(path);
        if (file.Length < 4096)
            return EmptyWaterfallFrame(sequence, centerHz, sampleRate, binCount, $"IQ capture file was too small ({file.Length} bytes). {output}".Trim());

        var bytesNeeded = isAirspy ? binCount * 4 : binCount * 2;
        var bytes = new byte[Math.Min(bytesNeeded, file.Length)];
        using (var stream = File.OpenRead(path))
            _ = stream.Read(bytes, 0, bytes.Length);

        return AnalyzeWaterfallSamples(sequence, bytes, centerHz, sampleRate, binCount, isAirspy, output, file.Length);
    }

    private static RfSurveyWaterfallFrameDto AnalyzeWaterfallSamples(int sequence, byte[] bytes, long centerHz, int sampleRate, int binCount, bool isAirspy, string output, long? sourceBytes = null)
    {
        var samples = isAirspy ? Math.Min(binCount, bytes.Length / 4) : Math.Min(binCount, bytes.Length / 2);
        samples = HighestPowerOfTwoAtMost(samples);
        if (samples < 256)
            return EmptyWaterfallFrame(sequence, centerHz, sampleRate, binCount, $"IQ capture did not contain enough samples. {output}".Trim());

        var i = new double[samples];
        var q = new double[samples];
        var clipped = 0;
        for (var s = 0; s < samples; s++)
        {
            if (isAirspy)
            {
                var ii = BitConverter.ToInt16(bytes, s * 4);
                var qq = BitConverter.ToInt16(bytes, s * 4 + 2);
                i[s] = ii / 32768.0;
                q[s] = qq / 32768.0;
                if (Math.Abs(ii) > 32200 || Math.Abs(qq) > 32200) clipped++;
            }
            else
            {
                var ii = bytes[s * 2];
                var qq = bytes[s * 2 + 1];
                i[s] = (ii - 127.5) / 127.5;
                q[s] = (qq - 127.5) / 127.5;
                if (ii <= 2 || ii >= 253 || qq <= 2 || qq >= 253) clipped++;
            }
        }

        var meanI = i.Take(samples).Average();
        var meanQ = q.Take(samples).Average();
        var fft = new Complex[samples];
        for (var s = 0; s < samples; s++)
        {
            var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * s / (samples - 1));
            fft[s] = new Complex((i[s] - meanI) * window, (q[s] - meanQ) * window);
        }
        FftInPlace(fft);

        var powers = new double[samples];
        for (var b = 0; b < samples; b++)
        {
            var index = (b + samples / 2) % samples;
            var value = fft[index];
            powers[b] = 10 * Math.Log10((value.Real * value.Real + value.Imaginary * value.Imaginary) / samples + 1e-12);
        }

        var min = powers.Min();
        var max = powers.Max();
        var sorted = powers.OrderBy(value => value).ToArray();
        var noise = Median(sorted.Take(Math.Max(1, (int)(sorted.Length * 0.80))).ToArray());
        var peakIndex = Array.IndexOf(powers, max);
        var startHz = centerHz - sampleRate / 2.0;
        var binWidth = sampleRate / (double)samples;
        var peakFrequency = startHz + (peakIndex + 0.5) * binWidth;
        var clipPct = clipped * 100.0 / samples;
        var overload = clipPct > 1.0 || max > -3;
        return new RfSurveyWaterfallFrameDto(
            sequence,
            DateTime.UtcNow,
            centerHz,
            sampleRate,
            startHz,
            binWidth,
            powers.Select(value => Math.Round(value, 2)).ToArray(),
            Math.Round(min, 2),
            Math.Round(max, 2),
            Math.Round(noise, 2),
            Math.Round(max, 2),
            Math.Round(peakFrequency, 0),
            Math.Round(clipPct, 2),
            overload,
            sourceBytes ?? bytes.Length,
            output);
    }

    private static RfSurveyWaterfallFrameDto EmptyWaterfallFrame(int sequence, long centerHz, int sampleRate, int binCount, string issue)
    {
        var startHz = centerHz - sampleRate / 2.0;
        var binWidth = sampleRate / (double)Math.Max(1, binCount);
        return new RfSurveyWaterfallFrameDto(sequence, DateTime.UtcNow, centerHz, sampleRate, startHz, binWidth, [], 0, 0, 0, 0, centerHz, 0, false, 0, issue);
    }

    private static RfPowerAnalysis AnalyzeIqFile(string path, int sampleRate, bool isAirspy)
    {
        if (!File.Exists(path))
            return new(false, "IQ capture file was not created.", null, null, null, null, 0, false, "", null, null);
        var file = new FileInfo(path);
        if (file.Length < 4096)
            return new(false, $"IQ capture file was too small ({file.Length} bytes).", null, null, null, null, 0, false, "", null, null);

        const int n = 1024;
        var i = new double[n];
        var q = new double[n];
        var bytesNeeded = isAirspy ? n * 4 : n * 2;
        var bytes = new byte[Math.Min(bytesNeeded, file.Length)];
        using (var stream = File.OpenRead(path))
            _ = stream.Read(bytes, 0, bytes.Length);

        var samples = isAirspy ? Math.Min(n, bytes.Length / 4) : Math.Min(n, bytes.Length / 2);
        if (samples < 256)
            return new(false, "IQ capture did not contain enough samples for spectrum analysis.", null, null, null, null, 0, false, "", null, null);

        var clipped = 0;
        for (var s = 0; s < samples; s++)
        {
            if (isAirspy)
            {
                var ii = BitConverter.ToInt16(bytes, s * 4);
                var qq = BitConverter.ToInt16(bytes, s * 4 + 2);
                i[s] = ii / 32768.0;
                q[s] = qq / 32768.0;
                if (Math.Abs(ii) > 32200 || Math.Abs(qq) > 32200) clipped++;
            }
            else
            {
                var ii = bytes[s * 2];
                var qq = bytes[s * 2 + 1];
                i[s] = (ii - 127.5) / 127.5;
                q[s] = (qq - 127.5) / 127.5;
                if (ii <= 2 || ii >= 253 || qq <= 2 || qq >= 253) clipped++;
            }
        }

        var meanI = i.Take(samples).Average();
        var meanQ = q.Take(samples).Average();
        var bins = new double[samples];
        for (var k = 0; k < samples; k++)
        {
            var real = 0.0;
            var imag = 0.0;
            for (var s = 0; s < samples; s++)
            {
                var window = 0.54 - 0.46 * Math.Cos(2 * Math.PI * s / (samples - 1));
                var sampleI = (i[s] - meanI) * window;
                var sampleQ = (q[s] - meanQ) * window;
                var angle = -2 * Math.PI * k * s / samples;
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);
                real += sampleI * cos - sampleQ * sin;
                imag += sampleI * sin + sampleQ * cos;
            }
            bins[k] = 10 * Math.Log10((real * real + imag * imag) / samples + 1e-12);
        }

        var strongestPeakIndex = 0;
        var strongestPeak = double.NegativeInfinity;
        var targetPeakIndex = -1;
        var targetPeak = double.NegativeInfinity;
        for (var k = 0; k < bins.Length; k++)
        {
            var binOffset = RfBinOffsetHz(k, samples, sampleRate);
            if (bins[k] > strongestPeak)
            {
                strongestPeak = bins[k];
                strongestPeakIndex = k;
            }
            if (Math.Abs(binOffset) <= 25_000 && bins[k] > targetPeak)
            {
                targetPeak = bins[k];
                targetPeakIndex = k;
            }
        }
        if (targetPeakIndex < 0)
        {
            targetPeak = strongestPeak;
            targetPeakIndex = strongestPeakIndex;
        }
        var sorted = bins.OrderBy(v => v).ToArray();
        var noiseCount = Math.Max(1, (int)(sorted.Length * 0.80));
        var noise = Median(sorted.Take(noiseCount).ToArray());
        var snr = targetPeak - noise;
        var offset = RfBinOffsetHz(targetPeakIndex, samples, sampleRate);
        var strongestOffset = RfBinOffsetHz(strongestPeakIndex, samples, sampleRate);
        var clipPct = clipped * 100.0 / samples;
        var overload = clipPct > 1.0 || strongestPeak > -3;
        return new(true, "", targetPeak, noise, snr, offset, clipPct, overload, BuildSparkline(bins), strongestPeak, strongestOffset);
    }

    private static double RfBinOffsetHz(int binIndex, int samples, int sampleRate) =>
        binIndex <= samples / 2
            ? binIndex * sampleRate / (double)samples
            : (binIndex - samples) * sampleRate / (double)samples;

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var mid = values.Count / 2;
        return values.Count % 2 == 0 ? (values[mid - 1] + values[mid]) / 2 : values[mid];
    }

    private static string BuildSparkline(IReadOnlyList<double> bins)
    {
        if (bins.Count == 0) return "";
        const string ramp = " .:-=+*#%@";
        var groups = 48;
        var min = bins.Min();
        var max = bins.Max();
        var span = Math.Max(1e-6, max - min);
        var chars = new char[groups];
        for (var g = 0; g < groups; g++)
        {
            var start = g * bins.Count / groups;
            var end = Math.Max(start + 1, (g + 1) * bins.Count / groups);
            var value = bins.Skip(start).Take(end - start).Max();
            var index = (int)Math.Clamp(Math.Round((value - min) / span * (ramp.Length - 1)), 0, ramp.Length - 1);
            chars[g] = ramp[index];
        }
        return new string(chars);
    }

    private async Task<ExperimentOutcome> RunControlChannelProbeAsync(string artifactPath, RfSurveyProfileDto profile, RfSurveyToolPrepDto toolPrep, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        var staleP25Cleanup = await CleanupStaleP25ProcessesAsync(ct);
        if (!string.IsNullOrWhiteSpace(staleP25Cleanup.BlockingIssue))
            return BlockedOutcome(
                "control_channel_p25_probe",
                "P25 probing requires exclusive SDR access and no stale OP25 process may already hold the device.",
                "Radio Setup should clean up abandoned OP25 probes before starting a new P25 probe.",
                staleP25Cleanup.BlockingIssue,
                new { staleP25Cleanup.Before, staleP25Cleanup.After, staleP25Cleanup.Output });

        var trState = await QueryTrActiveAsync(ct);
        var controlChannel = request.ControlChannelHz ?? profile.ControlChannelsHz.FirstOrDefault();
        if (controlChannel <= 0)
            return BlockedOutcome("control_channel_p25_probe", "P25 probing requires a known control channel.", "Import/confirm RR or TR ground truth first.", "No control channel is available.", new { profile.ControlChannelsHz });

        var probeProfile = ProfileWithP25ProbeOverrides(profile, request);
        var preview = BuildP25ProbePreview(probeProfile, artifactPath, controlChannel, request.DurationSeconds);
        if (!preview.Ready)
            return BlockedOutcome(
                "control_channel_p25_probe",
                "P25 frames must be measured with a configured command profile.",
                "Configure Radio Setup P25 probe command template.",
                preview.BlockingIssue,
                new { preview, toolPrep.Tools });

        var trWasActive = trState.Active;
        var trStopOutput = string.Empty;
        var trRestartOutput = string.Empty;
        var trRestartError = string.Empty;
        if (trState.Active)
        {
            trStopOutput = await RunServiceHelperAsync("stop-tr", ct);
            trState = await QueryTrActiveAsync(ct);
            if (trState.Active)
                return BlockedOutcome(
                    "control_channel_p25_probe",
                    "P25 probing needs exclusive SDR access.",
                    "Radio Setup can pause trunk-recorder through the service helper.",
                    "trunk-recorder remained active after the stop request.",
                    new { trState, trStopOutput });
        }

        var outputDir = Path.Combine(artifactPath, "probe-runs", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(outputDir);
        var demod = NormalizeP25Demod(ReadStringParameter(request.Parameters, "p25Demod"));
        if (string.IsNullOrWhiteSpace(demod))
            demod = NormalizeP25Demod(ReadStringParameter(request.Parameters, "demod"));
        var command = RenderP25ProbeCommand(probeProfile, controlChannel, request.DurationSeconds, outputDir, request.SourceIndex, demod);
        var requestedDuration = Math.Clamp(request.DurationSeconds, 10, 300);
        var timeout = request.DurationSeconds > 0
            ? Math.Clamp(requestedDuration + 15, 20, 90)
            : Math.Max(_config.RfSurvey.P25ProbeTimeoutSeconds, requestedDuration + 15);
        (int ExitCode, string Stdout) result;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(timeout));
            result = OperatingSystem.IsWindows()
                ? await RunCaptureAsync("powershell", "-NoProfile -Command " + Quote(command), linked.Token)
                : await RunCaptureAsync("bash", "-lc " + Quote(command), linked.Token);
        }
        finally
        {
            if (trWasActive)
            {
                try
                {
                    trRestartOutput = await RunServiceHelperAsync("start-tr", CancellationToken.None);
                }
                catch (Exception ex)
                {
                    trRestartError = ex.Message;
                    _logger.LogError(ex, "P25 probe failed to restart trunk-recorder after pausing it.");
                }
            }
        }
        var files = Directory.Exists(outputDir)
            ? Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories).Select(p => new FileInfo(p)).Select(f => new { path = f.FullName, bytes = f.Length }).ToList()
            : [];
        var output = TrimOutput(result.Stdout);
        var toolFailure = HasP25ProbeToolFailure(output);
        var hasFrames = HasP25FrameEvidence(output);
        var restartFailed = !string.IsNullOrWhiteSpace(trRestartError);
        var status = restartFailed ? "failed" : hasFrames ? "passed" : toolFailure ? "blocked" : "failed";
        return new ExperimentOutcome(
            status,
            "A known control channel should produce P25 frame/sync evidence when the SDR path is viable.",
            "Radio Setup temporarily paused TR if needed; configured P25 probe command; SDR available; known control channel.",
            restartFailed ? "P25 probe ran, but trunk-recorder did not restart afterward." : hasFrames ? "P25 probe output contained frame/sync evidence." : toolFailure ? "P25 probe command failed before RF evidence could be measured." : "P25 probe ran but did not contain recognizable P25 frame/sync evidence.",
            restartFailed ? $"trunk-recorder did not restart after P25 probe: {trRestartError}" : hasFrames ? "" : toolFailure ? "P25 probe command failed before RF evidence could be measured." : "No recognizable P25 frame/sync evidence was captured.",
            new { controlChannelHz = controlChannel, sourceIndex = request.SourceIndex, demod, probeOverrides = ReadP25ProbeOverrides(request), staleP25Cleanup, preview, command, outputDir, result.ExitCode, output, files, trWasActive, trStopOutput, trRestartOutput, trRestartError },
            new
            {
                recommendation = restartFailed
                    ? "Restart trunk-recorder before continuing Radio Setup."
                    : hasFrames
                    ? "Proceed to a longer stability probe or voice capture trial."
                    : toolFailure
                    ? "Fix the P25 probe command/tooling, then re-run the same control-channel probe before changing RF assumptions."
                    : "Try alternate control channel, gain, antenna aim/polarization, source center, or SDR path checks. Do not proceed to voice capture until P25 frame evidence is present.",
                followUps = hasFrames
                    ? new[] { "Run a stability-duration control-channel probe.", "Run a voice capture trial." }
                    : toolFailure
                    ? new[] { "Review the P25 probe log.", "Fix the command template or rendered arguments.", "Re-run the same control-channel probe." }
                    : new[] { "Probe alternate control channel.", "Try a gain sweep.", "Verify antenna aim and polarization.", "Verify source center/rate/error values." }
            });
    }

    private async Task<ExperimentOutcome> RunErrorGainSweepAsync(string artifactPath, RfSurveyProfileDto profile, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        if (request.Parameters == null)
            return new ExperimentOutcome(
                "failed",
                "Error/gain sweep requires concrete sweep parameters.",
                "Selected site, control channel, and one or more selected SDR sources.",
                "Sweep parameters were not supplied.",
                "Sweep parameters were not supplied.",
                new { profile.SystemShortName, profile.ControlChannelsHz, profile.SelectedSourceIndexes },
                new { recommendation = "Reopen the sweep step and run with selected SDR sweep values." });

        var job = await _jobs.StartAsync("tr-calibration-sweep", confirmed: true, request.Parameters, ct);
        var evidence = new
        {
            job,
            artifactPath,
            profile.SystemShortName,
            profile.ControlChannelsHz,
            profile.SelectedSourceIndexes,
            parameters = request.Parameters.Value
        };
        await WriteArtifactAsync(artifactPath, $"error-gain-sweep-job-{job.Id}.json", evidence, ct);
        return new ExperimentOutcome(
            "running",
            "Error/gain sweep runs controlled tr_tune measurements for selected SDR source settings.",
            "Selected site, selected SDR source(s), control channel, and sweep parameters.",
            $"Started error/gain sweep job {job.Id}. Results will populate from the job output feed.",
            "",
            evidence,
            new
            {
                recommendation = "Watch the sweep job feed and review parsed candidates after completion.",
                jobId = job.Id,
                jobType = job.Type
            });
    }

    private async Task<ExperimentOutcome> RunErrorGainSweepCancelAsync(string artifactPath, CancellationToken ct)
    {
        var job = await _jobs.StartAsync("tr-calibration-cancel", confirmed: true, parameters: null, ct);
        var evidence = new { job, artifactPath };
        await WriteArtifactAsync(artifactPath, $"error-gain-sweep-cancel-{job.Id}.json", evidence, ct);
        return new ExperimentOutcome(
            "running",
            "Cancel active error/gain sweep processes.",
            "An active or recently active sweep job.",
            $"Requested sweep cancellation with job {job.Id}.",
            "",
            evidence,
            new { recommendation = "Watch the job feed for cancellation progress.", jobId = job.Id, jobType = job.Type });
    }

    private async Task<ExperimentOutcome> RunTempTrConfigPlanAsync(string artifactPath, RfSurveyProfileDto profile, CancellationToken ct)
    {
        var candidatePath = await EnsureCandidateTrConfigAsync(artifactPath, ct);
        var plan = new
        {
            createdAtUtc = DateTime.UtcNow,
            source = "artifact-only",
            warning = "This plan does not modify trunk-recorder config. Applying it must create a backup and restart TR.",
            trunkRecorderConfig = _config.TrunkRecorder.ConfigPath,
            candidatePath,
            system = profile.SystemShortName,
            controlChannelsHz = profile.ControlChannelsHz,
            voiceFrequencyCount = profile.VoiceFrequenciesHz.Count,
            sources = profile.Sources.Select(s => new { s.Index, s.SdrType, s.Serial, s.CenterHz, s.SampleRate, s.ErrorHz, s.Gain }).ToList(),
            requiredActions = new[]
            {
                "Pause trunk-recorder only inside bounded SDR-only probes.",
                "Create a timestamped backup before any temporary TR config is applied.",
                "Restart trunk-recorder for each candidate temp config.",
                "Capture calls long enough to test stability, not just a brief decode burst.",
                "Restore or intentionally apply final config after the survey."
            }
        };
        await WriteArtifactAsync(artifactPath, "temp-tr-config-plan.json", plan, ct);
        var blockers = new List<string>();
        if (profile.ControlChannelsHz.Count == 0) blockers.Add("No control channel is available for temp TR config planning.");
        if (profile.Sources.Count == 0) blockers.Add("No SDR source is available for temp TR config planning.");
        return new ExperimentOutcome(
            blockers.Count == 0 ? "passed" : "blocked",
            "Temporary TR config trials should be explicit, backed up, and restart-gated.",
            "Known control channels and configured SDR sources.",
            blockers.Count == 0 ? "Temporary TR config plan was written to the survey artifact folder." : string.Join(" ", blockers),
            string.Join(" ", blockers),
            plan,
            new
            {
                recommendation = blockers.Count == 0
                    ? "Use this plan for the upcoming TR-run phase; do not mutate live config without backup/restart confirmation."
                    : "Fix setup ground truth before temp TR config trials.",
                artifact = Path.Combine(artifactPath, "temp-tr-config-plan.json"),
                candidatePath
            });
    }

    private async Task<ExperimentOutcome> RunVoiceCaptureTrialAsync(RfSurveySessionDto session, RfSurveyProfileDto profile, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        var readiness = await EvaluateCallQualityReadinessAsync(session, profile, ct);
        if (readiness.Blockers.Count > 0)
        {
            return new ExperimentOutcome(
                "blocked",
                "Call Quality needs live TR to match the selected RF recommendation before optional voice evidence can be evaluated.",
                "Passed RF Sweep recommendation applied to the live TR config; callstream ingest active; matching calls captured if traffic occurs.",
                string.Join(" ", readiness.Blockers),
                string.Join(" ", readiness.Blockers),
                new
                {
                    readiness.Blockers,
                    readiness.Warnings,
                    readiness.LatestRfCandidate,
                    readiness.LiveSource,
                    readiness.VoiceCoverage
                },
                new
                {
                    recommendation = "Apply the selected RF recommendation through Radio Setup, restart TR, then rerun Call Quality."
                });
        }

        var duration = Math.Clamp(request.DurationSeconds <= 0 ? 180 : request.DurationSeconds, 15, 3600);
        var start = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(duration), ct);
        var end = DateTimeOffset.UtcNow;
        var window = (StartUnix: start.ToUnixTimeSeconds(), EndUnix: end.ToUnixTimeSeconds());
        var (calls, realCalls) = await WaitForSurveyCallsAsync(profile, window.StartUnix, window.EndUnix, TimeSpan.FromSeconds(12), ct);
        var trAnalysis = await AnalyzeCallQualityTrWindowAsync(profile, start, end, ct);
        var trMetricsRow = BuildControlChannelQualityRowFromAnalysis(profile, trAnalysis);
        var trMetricsPassed = IsPassingControlChannelMetrics(trMetricsRow);
        var voiceInconclusive = realCalls.Count == 0 && trMetricsPassed;
        var blockers = new List<string>();
        if (realCalls.Count == 0 && !voiceInconclusive)
            blockers.Add("No real captured calls with audio were found in this capture window.");
        var failedSummary = BuildVoiceCaptureFailureSummary(blockers, trAnalysis);
        var resultSummary = realCalls.Count > 0
            ? $"Captured {realCalls.Count} real call(s) with audio in this {duration} second window."
            : voiceInconclusive
                ? BuildOptionalVoiceCaveat(trMetricsRow)
                : failedSummary;
        return new ExperimentOutcome(
            blockers.Count == 0 ? "passed" : "failed",
            "Voice capture is optional when TR control-channel metrics stay healthy but no traffic occurs.",
            "TR remained active for a bounded live capture window; callstream ingest active; matching calls captured when traffic occurred.",
            resultSummary,
            string.Join(" ", blockers),
            new
            {
                window.StartUnix,
                window.EndUnix,
                durationSeconds = duration,
                voiceOptional = true,
                voiceInconclusive,
                readiness.Warnings,
                readiness.LatestRfCandidate,
                readiness.LiveSource,
                readiness.VoiceCoverage,
                trAnalysis,
                trMetricsRow,
                trMetricsPassed,
                totalCalls = calls.Count,
                realCallsWithAudio = realCalls.Count,
                sample = realCalls.Take(10).Select(c => new { c.Id, c.StartTime, c.StopTime, c.SystemShortName, c.Talkgroup, c.AudioPath, c.TranscriptionStatus, c.QualityReason }).ToList()
            },
            new
            {
                recommendation = blockers.Count == 0
                    ? "Proceed to transcription gate."
                    : trAnalysis.Recommendations.FirstOrDefault() ?? "Run a temporary TR capture trial long enough to catch real traffic, or use healthy TR metrics as a no-traffic caveat.",
                followUps = trAnalysis.Recommendations
            });
    }

    private async Task<Dictionary<int, CandidateTrMetricsResult>> RunCandidateTrMetricsAsync(
        RfSurveySessionDto session,
        RfSurveyProfileDto profile,
        IReadOnlyList<RfValidationCandidate> candidates,
        IReadOnlyCollection<int> indexes,
        int durationSeconds,
        Func<int, CandidateTrMetricsResult?, Task>? onProgress,
        CancellationToken ct)
    {
        var results = new Dictionary<int, CandidateTrMetricsResult>();
        if (indexes.Count == 0)
            return results;

        var livePath = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(livePath) || !File.Exists(livePath))
        {
            foreach (var index in indexes)
            {
                results[index] = CandidateTrMetricsResult.Blocked("Live trunk-recorder config was not found.");
                if (onProgress != null)
                    await onProgress(index, results[index]);
            }
            return results;
        }

        var originalJson = await File.ReadAllTextAsync(livePath, ct);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var originalPath = Path.Combine(session.ArtifactPath, $"tr-config-before-metrics-candidates-{stamp}.json");
        Directory.CreateDirectory(session.ArtifactPath);
        await File.WriteAllTextAsync(originalPath, NormalizeJson(JsonNode.Parse(originalJson) as JsonObject ?? throw new JsonException("Live TR config root must be a JSON object.")), ct);
        var restored = false;
        try
        {
            foreach (var index in indexes)
            {
                if (index < 0 || index >= candidates.Count)
                    continue;
                var candidate = candidates[index];
                try
                {
                    if (onProgress != null)
                        await onProgress(index, null);
                    var candidateJson = BuildVoiceCandidateTrConfigJson(originalJson, profile, candidate);
                    var coverage = TrConfigSourceCoverageValidator.Validate(candidateJson);
                    if (!coverage.Ok)
                    {
                        results[index] = CandidateTrMetricsResult.Blocked("Candidate TR config cannot start: " + string.Join(" ", coverage.Blockers));
                        if (onProgress != null)
                            await onProgress(index, results[index]);
                        continue;
                    }

                    var candidatePath = Path.Combine(session.ArtifactPath, $"tr-config-metrics-candidate-{SanitizeFileToken(candidate.Id)}-{stamp}.json");
                    await File.WriteAllTextAsync(candidatePath, candidateJson, ct);
                    await InstallTrFileAsync(candidatePath, livePath, ct);
                    await RunServiceHelperAsync("restart-tr", ct);

                    var trialProfile = ProfileWithCandidateSource(profile, candidate.SourceIndex, candidate.Gain, candidate.ErrorHz, candidate.ControlChannelHz);
                    var start = DateTimeOffset.UtcNow;
                    await Task.Delay(TimeSpan.FromSeconds(durationSeconds), ct);
                    var end = DateTimeOffset.UtcNow;
                    var analysis = await AnalyzeCallQualityTrWindowAsync(trialProfile, start, end, ct);
                    var row = BuildControlChannelQualityRowFromAnalysis(trialProfile, candidate, analysis);
                    var status = IsPassingControlChannelMetrics(row) ? "passed" : "failed";
                    var summary = status == "passed"
                        ? $"TR metrics passed. {FormatHz(row.FrequencyHz)} averaged {row.AvgDecodeRate:F1} msg/sec with {row.ZeroDecodePct:F1}% zero-decode across {row.Samples} sample(s)."
                        : $"TR metrics failed. {FormatHz(row.FrequencyHz)} averaged {row.AvgDecodeRate:F1} msg/sec with {row.ZeroDecodePct:F1}% zero-decode across {row.Samples} sample(s).";
                    results[index] = new CandidateTrMetricsResult(status, summary, row);
                    if (onProgress != null)
                        await onProgress(index, results[index]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results[index] = CandidateTrMetricsResult.Blocked(ex.Message);
                    if (onProgress != null)
                        await onProgress(index, results[index]);
                }
            }
        }
        finally
        {
            try
            {
                await InstallTrFileAsync(originalPath, livePath, CancellationToken.None);
                await RunServiceHelperAsync("restart-tr", CancellationToken.None);
                restored = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RF validation metrics candidate sweep failed to restore the original TR config.");
            }

            await WriteArtifactAsync(session.ArtifactPath, $"metrics-candidate-restore-{stamp}.json", new { livePath, originalPath, restored, restoredAtUtc = DateTime.UtcNow }, CancellationToken.None);
        }

        return results;
    }

    private async Task<Dictionary<int, VoiceCandidateTrialResult>> RunVoiceCandidateTrialsAsync(
        RfSurveySessionDto session,
        RfSurveyProfileDto profile,
        IReadOnlyList<RfValidationCandidate> candidates,
        IReadOnlyList<int> indexes,
        int durationSeconds,
        Func<int, VoiceCandidateTrialResult?, Task>? onProgress,
        CancellationToken ct)
    {
        var results = new Dictionary<int, VoiceCandidateTrialResult>();
        if (indexes.Count == 0)
            return results;

        var livePath = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(livePath) || !File.Exists(livePath))
        {
            foreach (var index in indexes)
            {
                results[index] = VoiceCandidateTrialResult.Blocked("Live trunk-recorder config was not found.");
                if (onProgress != null)
                    await onProgress(index, results[index]);
            }
            return results;
        }

        var originalJson = await File.ReadAllTextAsync(livePath, ct);
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var originalPath = Path.Combine(session.ArtifactPath, $"tr-config-before-voice-candidates-{stamp}.json");
        Directory.CreateDirectory(session.ArtifactPath);
        await File.WriteAllTextAsync(originalPath, NormalizeJson(JsonNode.Parse(originalJson) as JsonObject ?? throw new JsonException("Live TR config root must be a JSON object.")), ct);
        var restored = false;
        try
        {
            foreach (var index in indexes)
            {
                if (index < 0 || index >= candidates.Count)
                    continue;
                var candidate = candidates[index];
                try
                {
                    if (onProgress != null)
                        await onProgress(index, null);
                    var candidateJson = BuildVoiceCandidateTrConfigJson(originalJson, profile, candidate);
                    var coverage = TrConfigSourceCoverageValidator.Validate(candidateJson);
                    if (!coverage.Ok)
                    {
                        results[index] = VoiceCandidateTrialResult.Blocked("Candidate TR config cannot start: " + string.Join(" ", coverage.Blockers));
                        if (onProgress != null)
                            await onProgress(index, results[index]);
                        continue;
                    }

                    var candidatePath = Path.Combine(session.ArtifactPath, $"tr-config-voice-candidate-{SanitizeFileToken(candidate.Id)}-{stamp}.json");
                    await File.WriteAllTextAsync(candidatePath, candidateJson, ct);
                    await InstallTrFileAsync(candidatePath, livePath, ct);
                    await RunServiceHelperAsync("restart-tr", ct);

                    var trialProfile = ProfileWithCandidateSource(profile, candidate.SourceIndex, candidate.Gain, candidate.ErrorHz, candidate.ControlChannelHz);
                    var start = DateTimeOffset.UtcNow;
                    await Task.Delay(TimeSpan.FromSeconds(durationSeconds), ct);
                    var end = DateTimeOffset.UtcNow;
                    var (calls, realCalls) = await WaitForSurveyCallsAsync(trialProfile, start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds(), TimeSpan.FromSeconds(12), ct);
                    var analysis = await AnalyzeCallQualityTrWindowAsync(trialProfile, start, end, ct);
                    var metricsRow = BuildControlChannelQualityRowFromAnalysis(trialProfile, candidate, analysis);
                    var metricsStatus = IsPassingControlChannelMetrics(metricsRow) ? "passed" : "failed";
                    var metricsSummary = metricsStatus == "passed"
                        ? $"TR metrics passed during voice trial. {FormatHz(metricsRow.FrequencyHz)} averaged {metricsRow.AvgDecodeRate:F1} msg/sec with {metricsRow.ZeroDecodePct:F1}% zero-decode across {metricsRow.Samples} sample(s)."
                        : $"TR metrics failed during voice trial. {FormatHz(metricsRow.FrequencyHz)} averaged {metricsRow.AvgDecodeRate:F1} msg/sec with {metricsRow.ZeroDecodePct:F1}% zero-decode across {metricsRow.Samples} sample(s).";
                    var voiceInconclusive = realCalls.Count == 0 && metricsStatus == "passed";
                    var status = realCalls.Count > 0 ? "passed" : voiceInconclusive ? "inconclusive" : "failed";
                    var summary = realCalls.Count > 0
                        ? $"Voice trial captured {realCalls.Count} real call(s) with audio for source {candidate.SourceIndex}, {FormatHz(candidate.ControlChannelHz)}, gain {candidate.Gain}, error {candidate.ErrorHz} Hz."
                        : voiceInconclusive
                            ? BuildOptionalVoiceCaveat(metricsRow)
                            : BuildVoiceCaptureFailureSummary(["No real captured calls with audio were found in this candidate voice window."], analysis);
                    results[index] = new VoiceCandidateTrialResult(status, summary, calls.Count, realCalls.Count, analysis, metricsStatus, metricsSummary, metricsRow);
                    if (onProgress != null)
                        await onProgress(index, results[index]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results[index] = VoiceCandidateTrialResult.Blocked(ex.Message);
                    if (onProgress != null)
                        await onProgress(index, results[index]);
                }
            }
        }
        finally
        {
            try
            {
                await InstallTrFileAsync(originalPath, livePath, CancellationToken.None);
                await RunServiceHelperAsync("restart-tr", CancellationToken.None);
                restored = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RF validation voice candidate sweep failed to restore the original TR config.");
            }

            await WriteArtifactAsync(session.ArtifactPath, $"voice-candidate-restore-{stamp}.json", new { livePath, originalPath, restored, restoredAtUtc = DateTime.UtcNow }, CancellationToken.None);
        }

        return results;
    }

    private async Task<CandidatePersistenceResult> PersistRfValidationCandidateAsync(RfSurveySessionDto session, RfSurveyProfileDto profile, RfValidationCandidate candidate, CancellationToken ct)
    {
        var livePath = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(livePath) || !File.Exists(livePath))
            throw new InvalidOperationException("Live trunk-recorder config was not found.");

        var liveJson = await File.ReadAllTextAsync(livePath, ct);
        var candidateJson = BuildVoiceCandidateTrConfigJson(liveJson, profile, candidate);
        var coverage = TrConfigSourceCoverageValidator.Validate(candidateJson);
        if (!coverage.Ok)
            throw new InvalidOperationException("Validated candidate TR config cannot start: " + string.Join(" ", coverage.Blockers));

        var candidatePath = Path.Combine(session.ArtifactPath, $"tr-config-selected-rf-validation-{SanitizeFileToken(candidate.Id)}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
        Directory.CreateDirectory(session.ArtifactPath);
        await File.WriteAllTextAsync(candidatePath, candidateJson, ct);
        var backupPath = await InstallTrFileAsync(candidatePath, livePath, ct);
        var serviceOutput = await RunServiceHelperAsync("restart-tr", ct);
        var row = await _database.GetRfSurveySessionAsync(session.Id, ct);
        await RefreshProfileFactsAsync(
            row?.Session ?? session,
            profile,
            row?.ProfileJson ?? JsonSerializer.Serialize(profile, EngineConfig.JsonOptions()),
            row?.ToolPrepJson ?? JsonSerializer.Serialize(EmptyToolPrep(), EngineConfig.JsonOptions()),
            invalidateExperiments: false,
            ct);
        return new CandidatePersistenceResult(candidate.Id, candidatePath, backupPath, serviceOutput.Trim(), DateTime.UtcNow);
    }

    private string BuildVoiceCandidateTrConfigJson(string originalJson, RfSurveyProfileDto profile, RfValidationCandidate candidate)
    {
        var template = JsonNode.Parse(originalJson) as JsonObject
            ?? throw new JsonException("Live TR config root must be a JSON object.");
        var root = BuildCleanRadioSetupTrConfigRoot(template);
        root["audioStreaming"] = true;
        var source = EnsureSourceObject(root, 0);
        var profileSource = profile.Sources.FirstOrDefault(row => row.Index == candidate.SourceIndex);
        if (profileSource != null)
        {
            var runtimeRate = TrRuntimeSampleRate(profile, profileSource, profileSource.SampleRate);
            if (runtimeRate > 0)
                source["rate"] = runtimeRate;
            if (!string.IsNullOrWhiteSpace(profileSource.Device))
                source["device"] = IsAirspySource(profileSource)
                    ? NormalizeP25ProbeDeviceArgsWithSerialPinning(profileSource, useNamedAirspyStageGains: true, pinAirspySerial: true)
                    : profileSource.Device;
            if (IsAirspySource(profileSource) || IsRtlSource(profileSource))
                source["driver"] = "osmosdr";
        }
        if (IsAirspySource(profileSource))
        {
            var stageGains = BuildAirspyStageGains(candidate.Gain);
            source.Remove("gain");
            source["lnaGain"] = stageGains.Lna;
            source["mixGain"] = stageGains.Mix;
            source["ifGain"] = stageGains.If;
        }
        else
        {
            source.Remove("lnaGain");
            source.Remove("mixGain");
            source.Remove("ifGain");
            if (double.TryParse(candidate.Gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var gainNumber))
                source["gain"] = gainNumber;
            else
                source["gain"] = candidate.Gain;
        }
        source["error"] = candidate.ErrorHz;
        var candidateSystem = SystemForControlChannel(profile, candidate.ControlChannelHz);
        var candidateControlChannels = candidateSystem?.ControlChannelsHz is { Count: > 0 }
            ? candidateSystem.ControlChannelsHz
            : candidate.ControlChannelHz > 0 ? [candidate.ControlChannelHz] : profile.ControlChannelsHz;
        var runtimeSampleRate = ReadIntNode(source["rate"]);
        var currentCenter = profileSource?.CenterHz > 0 ? profileSource.CenterHz : ReadLongNode(source["center"]);
        if (candidate.ControlChannelHz > 0 && SourceWindowCovers(currentCenter, runtimeSampleRate, candidate.ControlChannelHz))
            source["center"] = currentCenter;
        else if (candidate.ControlChannelHz > 0)
            source["center"] = candidate.ControlChannelHz;
        else if (candidateControlChannels.Count > 0)
            source["center"] = CenterForCandidateControlChannels(candidateControlChannels, runtimeSampleRate);

        var systemName = candidateSystem?.ShortName ?? profile.SystemShortName;
        var changes = new List<string>();
        var warnings = new List<string>();
        EnsureDraftWorkspaceSystems(root, profile.Systems, string.IsNullOrWhiteSpace(systemName) ? [] : [systemName], [], changes, warnings);
        var system = FindSystemObject(root, systemName);
        if (!string.IsNullOrWhiteSpace(systemName))
        {
            var oldSystemName = system["shortName"]?.GetValue<string>() ?? profile.SystemShortName;
            system["shortName"] = systemName;
            PatchPluginStreamShortNames(root, oldSystemName, systemName);
        }
        var controlChannels = system["control_channels"] as JsonArray ?? system["controlChannels"] as JsonArray;
        if (controlChannels != null && candidate.ControlChannelHz > 0)
        {
            var existing = controlChannels.Select(ReadLongNode).Where(value => value > 0 && value != candidate.ControlChannelHz).ToList();
            system["control_channels"] = new JsonArray(new[] { candidate.ControlChannelHz }.Concat(existing).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            system.Remove("controlChannels");
        }
        PatchCallstreamStreams(root, string.IsNullOrWhiteSpace(systemName) ? [] : [systemName], changes);
        NormalizeRadioSetupTrConfig(root, changes, _config.TrunkRecorder.TalkgroupsPath);
        if (FindSystemObject(root, systemName) is JsonObject voiceSystem)
        {
            voiceSystem["minDuration"] = 0;
            voiceSystem["minTransmissionDuration"] = 0;
            voiceSystem["callLog"] = true;
            voiceSystem["recordUUVCalls"] = true;
        }
        if (IsAirspySource(profileSource))
        {
            var stageGains = BuildAirspyStageGains(candidate.Gain);
            source.Remove("gain");
            source["lnaGain"] = stageGains.Lna;
            source["mixGain"] = stageGains.Mix;
            source["ifGain"] = stageGains.If;
        }

        return NormalizeJson(root);
    }

    private static long CenterForCandidateControlChannels(IReadOnlyList<long> controlChannels, int sampleRate)
    {
        var channels = controlChannels.Where(value => value > 0).Distinct().Order().ToList();
        if (channels.Count == 0)
            return 0;
        var min = channels.First();
        var max = channels.Last();
        var half = TrUsableHalfBandwidthHz(sampleRate);
        if (half > 0 && max - min <= half * 2)
            return (long)Math.Round((min + max) / 2.0, MidpointRounding.AwayFromZero);
        return channels[0];
    }

    private static bool SourceWindowCovers(long centerHz, int sampleRate, long frequencyHz)
    {
        if (centerHz <= 0 || frequencyHz <= 0)
            return false;
        var half = TrUsableHalfBandwidthHz(sampleRate);
        return half > 0 && frequencyHz >= centerHz - half && frequencyHz <= centerHz + half;
    }

    private static void PatchPluginStreamShortNames(JsonObject root, string oldShortName, string newShortName)
    {
        if (string.IsNullOrWhiteSpace(newShortName))
            return;
        if (root["plugins"] is not JsonArray plugins)
            return;
        foreach (var stream in plugins
            .OfType<JsonObject>()
            .SelectMany(plugin => (plugin["streams"] as JsonArray)?.OfType<JsonObject>() ?? []))
        {
            var current = stream["shortName"]?.GetValue<string>() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(current) ||
                string.IsNullOrWhiteSpace(oldShortName) ||
                string.Equals(current, oldShortName, StringComparison.OrdinalIgnoreCase))
                stream["shortName"] = newShortName;
        }
    }

    private async Task<CallQualityTrWindowAnalysis> AnalyzeCallQualityTrWindowAsync(RfSurveyProfileDto profile, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var log = await ReadTrJournalAsync(start.UtcDateTime, end.UtcDateTime, ct);
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var scopedLines = lines
            .Where(line => string.IsNullOrWhiteSpace(profile.SystemShortName) || line.Contains($"[{profile.SystemShortName}]", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var scopedLog = string.Join('\n', scopedLines);
        var health = TrHealthCollector.BuildSample(
            string.IsNullOrWhiteSpace(profile.SystemShortName) ? "global" : profile.SystemShortName,
            start.UtcDateTime,
            end.UtcDateTime,
            scopedLog);

        var recordedFrequencies = CountCallFrequencies(scopedLines, message => message.Contains("Starting P25 Recorder", StringComparison.OrdinalIgnoreCase));
        var noSourceFrequencies = CountCallFrequencies(scopedLines, message => message.Contains("no source covering", StringComparison.OrdinalIgnoreCase));
        var encryptedFrequencies = CountCallFrequencies(scopedLines, message => message.Contains("Not Recording: ENCRYPTED", StringComparison.OrdinalIgnoreCase));
        var noSampleCallEnds = CallstreamNoSamplesRegex.Matches(log).Count;
        var avgDecodeRate = health.DecodeLines == 0 ? 0 : health.DecodeRateTotal / health.DecodeLines;
        var avgTuningErrorAbsHz = health.TuningErrSamples == 0 ? 0 : health.TuningErrTotalAbsHz / health.TuningErrSamples;
        var sampleLines = scopedLines
            .Where(line =>
                line.Contains("Starting P25 Recorder", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("no source covering", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Concluding Recorded Call", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("No Transmissions were recorded", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("TuningErr", StringComparison.OrdinalIgnoreCase))
            .TakeLast(20)
            .ToList();

        var recommendations = BuildCallQualityRecommendations(health, noSampleCallEnds, recordedFrequencies, noSourceFrequencies, avgDecodeRate, avgTuningErrorAbsHz);
        return new CallQualityTrWindowAnalysis(
            health.DecodeLines,
            avgDecodeRate,
            health.DecodeZeroPct,
            health.Retunes,
            health.CallsStarted,
            health.CallsConcluded,
            health.UnableSource,
            noSourceFrequencies.Sum(row => row.Count),
            encryptedFrequencies.Sum(row => row.Count),
            noSampleCallEnds,
            health.TuningErrSamples,
            avgTuningErrorAbsHz,
            health.TuningErrMaxAbsHz,
            recordedFrequencies,
            noSourceFrequencies,
            encryptedFrequencies,
            recommendations,
            sampleLines);
    }

    private static string BuildVoiceCaptureFailureSummary(IReadOnlyList<string> blockers, CallQualityTrWindowAnalysis analysis)
    {
        if (blockers.Count == 0)
            return string.Empty;
        if (analysis.TrRecorderStarts > 0 || analysis.NoSourceGrantCount > 0 || analysis.CallstreamNoSampleEnds > 0)
        {
            var parts = new List<string>
            {
                blockers[0],
                $"TR saw {analysis.TrRecorderStarts} recorder start(s), {analysis.TrCallsConcluded} concluded call(s), {analysis.NoSourceGrantCount} no-source grant(s), and {analysis.CallstreamNoSampleEnds} callstream no-sample ending(s)."
            };
            if (analysis.DecodeLines > 0)
                parts.Add($"Control-channel decode averaged {analysis.AvgDecodeRate:F1}/sec with {analysis.DecodeZeroPct:F1}% zero-decode.");
            if (analysis.TuningErrSamples > 0)
                parts.Add($"Voice recorder tuning error averaged {analysis.AvgTuningErrorAbsHz:F0} Hz, max {analysis.MaxTuningErrorAbsHz:F0} Hz.");
            return string.Join(" ", parts);
        }
        return blockers[0] + " TR did not show usable recorder starts in this capture window.";
    }

    private static IReadOnlyList<FrequencyCount> CountCallFrequencies(IEnumerable<string> lines, Func<string, bool> includeMessage)
    {
        return lines
            .Select(line => (Line: line, Match: TrCallEventRegex.Match(line)))
            .Where(row => row.Match.Success && includeMessage(row.Match.Groups["message"].Value))
            .Select(row => FormatHz((long)Math.Round(ParseDouble(row.Match.Groups["freq"].Value) * 1_000_000d)))
            .GroupBy(freq => freq, StringComparer.OrdinalIgnoreCase)
            .Select(group => new FrequencyCount(group.Key, group.Count()))
            .OrderByDescending(row => row.Count)
            .ThenBy(row => row.Frequency)
            .Take(12)
            .ToList();
    }

    private static IReadOnlyList<string> BuildCallQualityRecommendations(
        TrHealthSampleDto health,
        int noSampleCallEnds,
        IReadOnlyList<FrequencyCount> recordedFrequencies,
        IReadOnlyList<FrequencyCount> noSourceFrequencies,
        double avgDecodeRate,
        double avgTuningErrorAbsHz)
    {
        var recommendations = new List<string>();
        if (health.DecodeLines > 0 && avgDecodeRate >= 20 && health.CallsStarted > 0 && noSampleCallEnds > 0)
            recommendations.Add("Control-channel decode is healthy and TR is assigning voice recorders, but callstream is ending covered calls with no samples. Treat voice capture as the failing stage, not RR/site selection.");
        if (noSourceFrequencies.Count > 0)
            recommendations.Add($"One SDR cannot cover much of the active voice traffic. Top uncovered grant frequencies: {string.Join(", ", noSourceFrequencies.Take(5).Select(row => $"{row.Frequency} ({row.Count})"))}.");
        if (recordedFrequencies.Count > 0 && noSampleCallEnds > 0)
            recommendations.Add($"Covered voice calls were attempted on {string.Join(", ", recordedFrequencies.Take(5).Select(row => $"{row.Frequency} ({row.Count})"))}, but no audio samples reached callstream.");
        if (avgTuningErrorAbsHz >= 1000)
            recommendations.Add($"Voice recorder tuning error is high enough to matter for capture ({avgTuningErrorAbsHz:F0} Hz average). The RF work-loop should score error/ppm/gain using voice capture, not only P25 sync.");
        if (health.CallsStarted == 0 && noSourceFrequencies.Count == 0 && health.DecodeLines > 0)
            recommendations.Add("The control channel decoded during the window, but no voice grants were captured. Rerun the gate during busier traffic before changing hardware.");
        if (recommendations.Count == 0)
            recommendations.Add("Inspect TR logs and callstream ingest; the gate did not find audio-bearing calls in PizzaWave.");
        return recommendations;
    }

    private async Task<CallQualityReadiness> EvaluateCallQualityReadinessAsync(RfSurveySessionDto session, RfSurveyProfileDto profile, CancellationToken ct)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        RfValidationCandidate? latestCandidate = null;
        object? liveSourceEvidence = null;
        object? voiceCoverage = null;
        TrConfigSourceCoverageValidation? coverage = null;

        var experiments = await _database.ListRfSurveyExperimentsAsync(session.Id, ct);
        var latestSweep = experiments
            .Where(e => string.Equals(e.Type, "rf_validation_sweep", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.Status, "passed", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.CreatedAtUtc)
            .LastOrDefault();

        if (latestSweep != null)
            TryReadSelectedRfValidationCandidate(latestSweep.EvidenceJson, out latestCandidate);

        var livePath = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(livePath) || !File.Exists(livePath))
        {
            blockers.Add("Live trunk-recorder config was not found; Call Quality cannot verify the applied Config Draft source plan.");
        }
        else
        {
            var liveJson = await File.ReadAllTextAsync(livePath, ct);
            var root = JsonNode.Parse(liveJson) as JsonObject
                ?? throw new JsonException("Live TR config root must be a JSON object.");
            coverage = TrConfigSourceCoverageValidator.Validate(liveJson);
            if (!coverage.Ok)
                blockers.Add("Applied TR source plan cannot start cleanly: " + string.Join(" ", coverage.Blockers));

        var requestedSystems = SourcePlanSystemNames(profile);
            var liveSystems = ReadSystemShortNames(root).ToList();
            var missingSystems = requestedSystems
                .Where(name => liveSystems.All(live => !string.Equals(live, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (requestedSystems.Count == 0)
                blockers.Add("Workspace has no selected systems; return to Sites before Call Quality.");
            if (missingSystems.Count > 0)
                blockers.Add("Live TR config is not running the applied workspace system list. Missing: " + string.Join(", ", missingSystems) + ".");

            var selected = profile.SelectedSourceIndexes.Count > 0
                ? profile.SelectedSourceIndexes.Distinct().Order().ToList()
                : profile.Sources.Select(source => source.Index).Distinct().Order().ToList();
            var missingSources = selected
                .Where(index => coverage.Sources.All(source => source.Index != index))
                .ToList();
            if (selected.Count == 0)
                blockers.Add("Workspace has no selected SDR sources; return to Config Draft before Call Quality.");
            if (missingSources.Count > 0)
                blockers.Add("Live TR config is not running the selected source indexes: " + string.Join(", ", missingSources) + ".");

            try
            {
                var draft = await BuildConfigDraftAsync(session.Id, ct);
                var draftCoverage = TrConfigSourceCoverageValidator.Validate(draft.ConfigJson);
                var drift = AppliedDraftDrift(coverage, draftCoverage);
                if (drift.Count > 0)
                    blockers.Add("Live TR config does not match the current Config Draft. Click Apply Config Draft before Call Quality. " + string.Join(" ", drift));
            }
            catch (Exception ex)
            {
                blockers.Add("Unable to verify live TR config against the current Config Draft: " + ex.Message);
            }

            liveSourceEvidence = new
            {
                livePath,
                requestedSystems,
                liveSystems,
                selectedSourceIndexes = selected,
                coverage.Ok,
                coverage.Blockers,
                coverage.Sources,
                coverage.Systems
            };
        }

        var observedVoiceFrequencies = await BuildObservedVoiceFrequenciesBySystemAsync(ct);
        var coverageSystems = AddObservedVoiceFrequencies(profile.Systems, observedVoiceFrequencies, warnings);
        var coverageProfile = profile with
        {
            Systems = coverageSystems,
            VoiceFrequenciesHz = coverageSystems
                .SelectMany(system => system.VoiceFrequenciesHz)
                .Where(value => value > 0)
                .Distinct()
                .Order()
                .ToList()
        };
        voiceCoverage = BuildVoiceCoverageEvidence(coverageProfile, coverage?.Sources);
        if (voiceCoverage is VoiceCoverageEvidence voiceCoverageEvidence && voiceCoverageEvidence.TotalVoiceFrequencies > 0 && voiceCoverageEvidence.UncoveredVoiceFrequencies.Count > 0)
            warnings.Add($"Current selected source window does not cover {voiceCoverageEvidence.UncoveredVoiceFrequencies.Count} of {voiceCoverageEvidence.TotalVoiceFrequencies} known site voice frequencies. Call Quality may pass only if traffic lands inside the covered window.");

        return new CallQualityReadiness(blockers, warnings, latestCandidate, liveSourceEvidence, voiceCoverage);
    }

    private static IReadOnlyList<string> AppliedDraftDrift(TrConfigSourceCoverageValidation live, TrConfigSourceCoverageValidation draft)
    {
        var drift = new List<string>();
        if (!draft.Ok)
            drift.Add("Current Config Draft has source coverage blockers: " + string.Join(" ", draft.Blockers));
        if (live.Sources.Count != draft.Sources.Count)
            drift.Add($"Live TR has {live.Sources.Count} source window(s), but the current draft has {draft.Sources.Count}.");
        foreach (var draftSource in draft.Sources)
        {
            var liveSource = live.Sources.FirstOrDefault(source => source.Index == draftSource.Index);
            if (liveSource == null)
            {
                drift.Add($"Live TR is missing source {draftSource.Index}.");
                continue;
            }
            if (!string.Equals(liveSource.Device, draftSource.Device, StringComparison.OrdinalIgnoreCase) ||
                liveSource.CenterHz != draftSource.CenterHz ||
                liveSource.SampleRate != draftSource.SampleRate)
                drift.Add($"Source {draftSource.Index} differs from draft.");
        }

        var liveSystems = live.Systems
            .Select(system => $"{system.ShortName}|{string.Join(",", system.ControlChannelsHz.Order())}")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var draftSystems = draft.Systems
            .Select(system => $"{system.ShortName}|{string.Join(",", system.ControlChannelsHz.Order())}")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!liveSystems.SequenceEqual(draftSystems, StringComparer.OrdinalIgnoreCase))
            drift.Add("Live TR system/control-channel list differs from draft.");
        return drift;
    }

    private static object? BuildVoiceCoverageEvidence(RfSurveyProfileDto profile, IReadOnlyList<TrConfigSourceCoverageSource>? liveSources = null)
    {
        var frequencies = profile.VoiceFrequenciesHz
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToList();
        if (frequencies.Count == 0)
            return null;
        var windows = liveSources is { Count: > 0 }
            ? liveSources.Select(source => new SourceCoverageWindow(source.Index, source.CenterHz, source.SampleRate, source.LowHz, source.HighHz)).ToList()
            : (profile.SelectedSourceIndexes.Count > 0
                ? profile.Sources.Where(source => profile.SelectedSourceIndexes.Contains(source.Index)).ToList()
                : profile.Sources.Take(1).ToList())
            .Select(source =>
            {
                var half = Math.Max(1, source.SampleRate) * 0.46875;
                return new SourceCoverageWindow(source.Index, source.CenterHz, source.SampleRate, (long)Math.Round(source.CenterHz - half), (long)Math.Round(source.CenterHz + half));
            }).ToList();
        var uncovered = frequencies
            .Where(freq => !windows.Any(window => freq >= window.LowHz && freq <= window.HighHz))
            .ToList();
        return new VoiceCoverageEvidence(frequencies.Count, frequencies.Count - uncovered.Count, uncovered, windows);
    }

    private static bool TryReadSelectedRfValidationCandidate(string? evidenceJson, out RfValidationCandidate candidate)
    {
        candidate = new RfValidationCandidate();
        if (string.IsNullOrWhiteSpace(evidenceJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
                return false;
            var best = candidates.EnumerateArray().FirstOrDefault();
            if (best.ValueKind != JsonValueKind.Object)
                return false;
            candidate = new RfValidationCandidate
            {
                Id = best.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                SourceIndex = best.TryGetProperty("sourceIndex", out var sourceIndex) && sourceIndex.TryGetInt32(out var sourceValue) ? sourceValue : 0,
                Serial = best.TryGetProperty("serial", out var serial) ? serial.GetString() ?? string.Empty : string.Empty,
                Device = best.TryGetProperty("device", out var device) ? device.GetString() ?? string.Empty : string.Empty,
                ControlChannelHz = best.TryGetProperty("controlChannelHz", out var cc) && cc.TryGetInt64(out var ccValue) ? ccValue : 0,
                Gain = best.TryGetProperty("gain", out var gain) ? gain.ToString() ?? string.Empty : string.Empty,
                ErrorHz = best.TryGetProperty("errorHz", out var error) && error.TryGetInt32(out var errorValue) ? errorValue : 0
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadIntNode(JsonNode? node)
    {
        if (node == null)
            return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intValue))
                return intValue;
            if (value.TryGetValue<double>(out var doubleValue))
                return (int)Math.Round(doubleValue);
            if (value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return 0;
    }

    private static string BuildAppliedSourcePlanSummary(JsonNode? configRoot, IReadOnlyList<int> changedSourceIndexes)
    {
        var root = configRoot as JsonObject;
        var sourceCount = root?["sources"] is JsonArray sources ? sources.Count : 0;
        var systems = root?["systems"] is JsonArray systemArray
            ? systemArray
                .OfType<JsonObject>()
                .Select(system => system["shortName"]?.GetValue<string>() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        var sourceText = sourceCount == 1 ? "1 SDR source window" : $"{sourceCount} SDR source windows";
        var systemText = systems.Count == 1 ? "1 system" : $"{systems.Count} systems";
        var changed = changedSourceIndexes
            .Where(index => index >= 0)
            .Distinct()
            .Order()
            .ToList();
        var changedText = changed.Count > 0
            ? $" Updated source index(es): {string.Join(", ", changed)}."
            : "";
        var systemList = systems.Count > 0
            ? $": {string.Join(", ", systems)}"
            : "";
        return $"Applied {sourceText} for {systemText}{systemList}.{changedText}";
    }

    private static long ReadLongNode(JsonNode? node)
    {
        if (node == null)
            return 0;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue))
                return longValue;
            if (value.TryGetValue<double>(out var doubleValue))
                return (long)Math.Round(doubleValue);
            if (value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return 0;
    }

    private static double? ReadNullableDoubleNode(JsonNode? node)
    {
        if (node == null)
            return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<double>(out var doubleValue))
                return doubleValue;
            if (value.TryGetValue<float>(out var floatValue))
                return floatValue;
            if (value.TryGetValue<int>(out var intValue))
                return intValue;
            if (value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        return null;
    }

    private static bool GainMatches(string liveGain, string candidateGain)
    {
        liveGain = (liveGain ?? string.Empty).Trim();
        candidateGain = (candidateGain ?? string.Empty).Trim();
        if (double.TryParse(liveGain, NumberStyles.Float, CultureInfo.InvariantCulture, out var live) &&
            double.TryParse(candidateGain, NumberStyles.Float, CultureInfo.InvariantCulture, out var candidate))
            return Math.Abs(live - candidate) < 0.1;
        return string.Equals(liveGain, candidateGain, StringComparison.OrdinalIgnoreCase);
    }

    private static string DisplayValue(string value) => string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim();

    private async Task<ExperimentOutcome> RunTranscriptionGateAsync(RfSurveySessionDto session, RfSurveyProfileDto profile, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        var window = await ResolveCallQualityWindowAsync(session, request, 300, ct);
        var timeout = Math.Clamp(request.DurationSeconds <= 0 ? 120 : request.DurationSeconds, 15, 900);
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        var provider = await CheckTranscriptionProviderReadinessAsync(ct);
        List<EngineCall> calls;
        List<EngineCall> realCalls;
        List<EngineCall> usable;
        (calls, realCalls) = await ListSurveyCallsAsync(profile, window.StartUnix, window.EndUnix, ct);
        usable = UsableTranscripts(realCalls).ToList();
        if (!provider.Available && realCalls.Count > 0)
        {
            var immediateBlockers = new List<string> { provider.BlockingIssue };
            return TranscriptionGateOutcome("blocked", window, timeout, calls, realCalls, usable, provider, immediateBlockers);
        }
        do
        {
            (calls, realCalls) = await ListSurveyCallsAsync(profile, window.StartUnix, window.EndUnix, ct);
            usable = UsableTranscripts(realCalls).ToList();
            if (usable.Count > 0 || DateTime.UtcNow >= deadline)
                break;
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        while (true);
        var trAnalysis = await AnalyzeCallQualityTrWindowAsync(profile, DateTimeOffset.FromUnixTimeSeconds(window.StartUnix), DateTimeOffset.FromUnixTimeSeconds(window.EndUnix), ct);
        var trMetricsRow = BuildControlChannelQualityRowFromAnalysis(profile, trAnalysis);
        var trMetricsPassed = IsPassingControlChannelMetrics(trMetricsRow);
        var noTrafficCaveat = realCalls.Count == 0 && trMetricsPassed
            ? BuildOptionalVoiceCaveat(trMetricsRow) + " Transcription was not tested because there was no call audio to transcribe."
            : "";
        var blockers = new List<string>();
        if (realCalls.Count == 0 && string.IsNullOrWhiteSpace(noTrafficCaveat)) blockers.Add("No real captured calls are available for transcription testing.");
        if (!provider.Available && realCalls.Count > 0) blockers.Add(provider.BlockingIssue);
        if (realCalls.Count > 0 && usable.Count == 0 && provider.Available) blockers.Add("No captured call produced a usable completed transcript.");
        var status = blockers.Count == 0 ? "passed" : !provider.Available ? "blocked" : "failed";
        return TranscriptionGateOutcome(status, window, timeout, calls, realCalls, usable, provider, blockers, trAnalysis, trMetricsRow, noTrafficCaveat);
    }

    private ExperimentOutcome TranscriptionGateOutcome(
        string status,
        (long StartUnix, long EndUnix) window,
        int timeout,
        IReadOnlyList<EngineCall> calls,
        IReadOnlyList<EngineCall> realCalls,
        IReadOnlyList<EngineCall> usable,
        TranscriptionProviderReadiness provider,
        IReadOnlyList<string> blockers,
        CallQualityTrWindowAnalysis? trAnalysis = null,
        ControlChannelQualityRow? trMetricsRow = null,
        string caveat = "")
    {
        return new ExperimentOutcome(
            status,
            "PizzaWave quality uses transcripts when real call audio exists; no-traffic windows can proceed with a caveat when TR metrics pass.",
            "Real captured calls with completed transcription, or healthy TR metrics when no traffic occurs.",
            blockers.Count == 0
                ? string.IsNullOrWhiteSpace(caveat) ? $"Transcription gate passed with {usable.Count} usable call transcript(s)." : caveat
                : string.Join(" ", blockers),
            string.Join(" ", blockers),
            new
            {
                window.StartUnix,
                window.EndUnix,
                waitedSeconds = timeout,
                voiceOptional = true,
                voiceInconclusive = !string.IsNullOrWhiteSpace(caveat),
                trAnalysis,
                trMetricsRow,
                provider.Provider,
                provider.Endpoint,
                provider.Model,
                provider.Available,
                provider.CheckStatus,
                provider.CheckDetail,
                totalCalls = calls.Count,
                realCalls = realCalls.Count,
                usableTranscripts = usable.Count,
                statuses = realCalls.GroupBy(c => $"{c.TranscriptionStatus}/{c.QualityReason}").Select(g => new { status = g.Key, count = g.Count() }).ToList(),
                recentTranscriptionErrors = realCalls
                    .Select(c => ExtractTranscriptionError(c.RawMetadataJson))
                    .Where(error => !string.IsNullOrWhiteSpace(error))
                    .Distinct()
                    .Take(5)
                    .ToList(),
                recentCalls = realCalls
                    .OrderByDescending(c => c.StartTime)
                    .Take(8)
                    .Select(c => new
                    {
                        c.Id,
                        c.StartTime,
                        c.StopTime,
                        c.Talkgroup,
                        c.TalkgroupName,
                        c.AudioPath,
                        c.TranscriptionStatus,
                        c.QualityReason,
                        transcriptChars = c.Transcription?.Trim().Length ?? 0,
                        transcriptionError = ExtractTranscriptionError(c.RawMetadataJson)
                    })
                    .ToList()
            },
            new
            {
                recommendation = blockers.Count == 0
                    ? string.IsNullOrWhiteSpace(caveat) ? "Proceed to stability verdict." : "Proceed with the no-traffic caveat; rerun Call Quality during busier traffic if transcription proof is required."
                    : !provider.Available
                        ? "Fix the configured transcription provider endpoint, then rerun Call Quality. The RF capture window already contains real call audio."
                        : "Do not fail a no-traffic RF path solely for missing transcription; rerun when traffic exists or fix transcription if call audio was captured."
            });
    }

    private async Task<ExperimentOutcome> RunStabilityVerdictAsync(RfSurveySessionDto session, RfSurveyProfileDto profile, RfSurveyRunExperimentRequest request, CancellationToken ct)
    {
        var window = await ResolveCallQualityWindowAsync(session, request, Math.Max(request.DurationSeconds, 300), ct);
        var (calls, realCallRows) = await ListSurveyCallsAsync(profile, window.StartUnix, window.EndUnix, ct);
        var realCalls = realCallRows.OrderBy(c => c.StartTime).ToList();
        var usable = UsableTranscripts(realCalls).ToList();
        var spanSeconds = ObservedCallSpanSeconds(realCalls);
        var trAnalysis = await AnalyzeCallQualityTrWindowAsync(profile, DateTimeOffset.FromUnixTimeSeconds(window.StartUnix), DateTimeOffset.FromUnixTimeSeconds(window.EndUnix), ct);
        var trMetricsRow = BuildControlChannelQualityRowFromAnalysis(profile, trAnalysis);
        var trMetricsPassed = IsPassingControlChannelMetrics(trMetricsRow);
        var noTrafficCaveat = realCalls.Count == 0 && trMetricsPassed
            ? BuildOptionalVoiceCaveat(trMetricsRow) + " Stability was not scored from calls because there were no calls to score."
            : "";
        var blockers = new List<string>();
        if (string.IsNullOrWhiteSpace(noTrafficCaveat))
        {
            if (usable.Count == 0) blockers.Add("No usable real-call transcripts were available for stability scoring.");
            if (realCalls.Count < 2 || spanSeconds < 120) blockers.Add("Capture evidence is too short to prove stability; a brief burst is not enough.");
        }
        var status = blockers.Count == 0 ? "passed" : "failed";
        return new ExperimentOutcome(
            status,
            "Decode persistence is required; call-based stability is evaluated when traffic exists.",
            "Multiple real captured calls over a stability window, or healthy TR metrics when no traffic occurs.",
            status == "passed" ? string.IsNullOrWhiteSpace(noTrafficCaveat) ? $"Stable candidate: {usable.Count} usable call(s) over {spanSeconds} second(s)." : noTrafficCaveat : string.Join(" ", blockers),
            string.Join(" ", blockers),
            new
            {
                window.StartUnix,
                window.EndUnix,
                voiceOptional = true,
                voiceInconclusive = !string.IsNullOrWhiteSpace(noTrafficCaveat),
                trAnalysis,
                trMetricsRow,
                realCalls = realCalls.Count,
                usableTranscripts = usable.Count,
                spanSeconds,
                firstCall = realCalls.FirstOrDefault()?.StartTime,
                lastCall = realCalls.LastOrDefault()?.StartTime,
                lastCallStop = realCalls.LastOrDefault()?.StopTime
            },
            new
            {
                recommendation = status == "passed"
                    ? string.IsNullOrWhiteSpace(noTrafficCaveat) ? "Survey can be marked as a stable RF path candidate. Export or apply the plan." : "Proceed with the no-traffic caveat; rerun Call Quality during busier traffic if call-based stability proof is required."
                    : "Continue controlled experiments; include longer capture windows because short pass/fail swings were observed in MS testing."
            });
    }

    private static long ObservedCallSpanSeconds(IReadOnlyList<EngineCall> calls)
    {
        if (calls.Count < 2)
            return 0;
        var firstStart = calls.Min(call => call.StartTime);
        var lastObserved = calls.Max(call => call.StopTime > call.StartTime ? call.StopTime : call.StartTime);
        return Math.Max(0, lastObserved - firstStart);
    }

    private static ExperimentOutcome BlockedOutcome(string type, string hypothesis, string requiredSetup, string blockingIssue, object evidence) =>
        new("blocked", hypothesis, requiredSetup, blockingIssue, blockingIssue, evidence, new
        {
            recommendation = blockingIssue,
            nextExperiment = type == "control_channel_p25_probe" ? "Resolve the blocker, then re-run P25 probing before voice capture." : "Resolve the blocker and repeat this experiment."
        });

    private RfSurveySessionDto UpdateSessionFromExperiment(RfSurveySessionDto session, RfSurveyExperimentDto experiment)
    {
        var verdict = session.Verdict;
        var stability = session.Stability;
        if (experiment.Type == "rf_validation_sweep" && experiment.Status == "passed")
            verdict = "rf_path_candidate";
        if (experiment.Type == "rf_validation_sweep" && experiment.Status is "failed" or "blocked")
            verdict = "blocked";
        if (experiment.Type == "control_channel_p25_probe" && experiment.Status == "passed")
            verdict = "rf_path_candidate";
        if (experiment.Type is "control_channel_quality" or "control_channel_p25_probe" && experiment.Status is "failed" or "blocked")
            verdict = "blocked";
        if (experiment.Type == "transcription_gate" && experiment.Status == "passed")
            verdict = "voice_transcription_candidate";
        if (experiment.Type == "stability_verdict")
        {
            stability = experiment.Status == "passed" ? "stable_candidate" : "unstable_or_unproven";
            verdict = experiment.Status == "passed" ? "pass_candidate" : "failed";
        }
        if (experiment.Type is "tr_stopped_check" or "sdr_inventory" && experiment.Status is "failed" or "blocked")
            verdict = "blocked";
        return session with
        {
            Status = experiment.Type == "stability_verdict" && experiment.Status == "passed" ? "completed" : experiment.Status == "blocked" ? "blocked" : "experimenting",
            Verdict = verdict,
            Stability = stability,
            UpdatedAtUtc = DateTime.UtcNow,
            CompletedAtUtc = experiment.Type == "stability_verdict" && experiment.Status == "passed" ? DateTime.UtcNow : session.CompletedAtUtc
        };
    }

    private async Task<RfSurveyToolStatusDto> P25ToolAsync()
    {
        if (!string.IsNullOrWhiteSpace(_config.RfSurvey.P25ProbeCommandTemplate))
        {
            return new RfSurveyToolStatusDto(
                "p25",
                "Configured P25 probe command",
                "p25",
                true,
                true,
                "configured template",
                _config.RfSurvey.P25ProbeCommandTemplate,
                "Reports P25 frame/sync presence, decode quality, and grant evidence before voice trials.",
                "Installed.");
        }
        foreach (var candidate in new[] { "rx.py", "multi_rx.py", "op25_rx.py" })
        {
            var result = await RunCaptureAsync("bash", $"-lc \"command -v {candidate} >/dev/null 2>&1 && {candidate} --help 2>&1 | head -1 || true\"", CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(result.Stdout))
                return new RfSurveyToolStatusDto("p25", "P25 control-channel tooling", "p25", true, true, result.Stdout.Trim(), candidate, "Reports P25 frame/sync presence, decode quality, and grant evidence before voice trials.", "Installed.");
        }
        return new RfSurveyToolStatusDto("p25", "P25 control-channel tooling", "p25", true, false, "", "rx.py / multi_rx.py / op25_rx.py", "Reports P25 frame/sync presence, decode quality, and grant evidence before voice trials.", "Install a validated OP25/P25 toolchain for this host architecture.");
    }

    private async Task<bool> EnsureP25ProbeTemplateAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_config.RfSurvey.P25ProbeCommandTemplate))
            return false;
        if (!await CommandExistsAsync("rx.py", ct))
            return false;

        _config.RfSurvey.P25ProbeCommandTemplate =
            "rx.py --args {device} -f {frequency_hz} -S {sample_rate} -q {error_ppm} -g {gain} -D cqpsk -l 56120 -v 10";
        if (string.IsNullOrWhiteSpace(_config.RfSurvey.P25ProbeWorkingDirectory))
            _config.RfSurvey.P25ProbeWorkingDirectory = "/tmp";
        _config.RfSurvey.P25ProbeDurationSeconds = Math.Clamp(_config.RfSurvey.P25ProbeDurationSeconds <= 0 ? 45 : _config.RfSurvey.P25ProbeDurationSeconds, 10, 300);
        _config.RfSurvey.P25ProbeTimeoutSeconds = Math.Max(_config.RfSurvey.P25ProbeTimeoutSeconds, _config.RfSurvey.P25ProbeDurationSeconds + 30);
        await SaveEngineConfigAsync(ct);
        return true;
    }

    private async Task SaveEngineConfigAsync(CancellationToken ct)
    {
        _config.ApplyDefaults();
        if (OperatingSystem.IsWindows() || !_config.ConfigPath.StartsWith("/etc/", StringComparison.Ordinal))
        {
            _config.Save();
            return;
        }

        var stagingRoot = Path.Combine(_config.Storage.AppDataRoot, "protected-config");
        Directory.CreateDirectory(stagingRoot);
        var candidatePath = Path.Combine(stagingRoot, $"pizzad-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(candidatePath, JsonSerializer.Serialize(_config, EngineConfig.JsonOptions()) + Environment.NewLine, ct);
        try
        {
            var helper = FindAdminHelper() ?? throw new FileNotFoundException("pizzawave_setup_admin.sh was not found; protected config writes are unavailable.");
            var result = await RunAdminHelperAsync(helper, ["install-pizzad-config", candidatePath, _config.ConfigPath], ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"install-pizzad-config failed: {result.Output.Trim()}");
        }
        finally
        {
            try { File.Delete(candidatePath); } catch { }
        }
    }

    private static async Task<RfSurveyToolStatusDto> ToolAsync(string id, string label, string category, bool required, string command, string versionCommand, string purpose, string installHint)
    {
        var exists = await CommandExistsAsync(command, CancellationToken.None);
        var version = exists
            ? (await RunCaptureAsync("bash", "-lc " + Quote(versionCommand + " 2>&1 | head -3"), CancellationToken.None)).Stdout.Trim()
            : string.Empty;
        return new RfSurveyToolStatusDto(id, label, category, required, exists, version, command, purpose, exists ? "Installed." : installHint);
    }

    private RfSurveyToolStatusDto TranscriptionTool()
    {
        var provider = string.IsNullOrWhiteSpace(_config.Transcription.Provider) ? "none" : _config.Transcription.Provider;
        return new RfSurveyToolStatusDto(
            "transcription",
            $"Transcription provider: {provider}",
            "transcription",
            true,
            !string.Equals(provider, "none", StringComparison.OrdinalIgnoreCase),
            provider,
            provider,
            "Runs the captured-call transcription acceptance gate.",
            "Configure and test transcription settings before Radio Setup.");
    }

    private async Task<(RfSurveyToolPrepDto? Prep, string Json)> EnsureReusableToolPrepAsync(
        RfSurveySessionDto session,
        string profileJson,
        string toolPrepJson,
        CancellationToken ct)
    {
        var prep = DeserializeOrDefault<RfSurveyToolPrepDto>(toolPrepJson);
        if (HasToolPrepRun(prep))
            return (prep, toolPrepJson);

        var reusable = await LatestReusableToolPrepAsync(session.Id, ct);
        if (reusable == null)
            return (prep, toolPrepJson);

        var json = JsonSerializer.Serialize(reusable, EngineConfig.JsonOptions());
        await WriteArtifactAsync(session.ArtifactPath, "tool-prep.json", reusable, ct);
        await _database.UpdateRfSurveySessionAsync(session, profileJson, json, ct);
        return (reusable, json);
    }

    private async Task<(RfSurveyToolPrepDto? Prep, string Json)> ResolveToolPrepForReadAsync(
        string sessionId,
        string toolPrepJson,
        CancellationToken ct)
    {
        var prep = DeserializeOrDefault<RfSurveyToolPrepDto>(toolPrepJson);
        if (HasToolPrepRun(prep))
            return (prep, toolPrepJson);

        var reusable = await LatestReusableToolPrepAsync(sessionId, ct);
        if (reusable == null)
            return (prep, toolPrepJson);

        return (reusable, JsonSerializer.Serialize(reusable, EngineConfig.JsonOptions()));
    }

    private async Task<RfSurveyToolPrepDto?> LatestReusableToolPrepAsync(string? exceptSessionId, CancellationToken ct)
    {
        RfSurveyToolPrepDto? latest = null;
        foreach (var session in await _database.ListRfSurveySessionsAsync(ct))
        {
            if (!string.IsNullOrWhiteSpace(exceptSessionId) &&
                string.Equals(session.Id, exceptSessionId, StringComparison.OrdinalIgnoreCase))
                continue;

            var row = await _database.GetRfSurveySessionAsync(session.Id, ct);
            if (row == null)
                continue;

            var prep = DeserializeOrDefault<RfSurveyToolPrepDto>(row.Value.ToolPrepJson);
            if (HasToolPrepRun(prep))
            {
                if (latest == null || prep!.GeneratedAtUtc > latest.GeneratedAtUtc)
                    latest = prep;
            }
        }

        return latest;
    }

    private static bool HasToolPrepRun(RfSurveyToolPrepDto? prep) => prep?.Tools.Count > 0;

    private static RfSurveyToolPrepDto EmptyToolPrep() => new(
        DateTime.UtcNow,
        false,
        false,
        false,
        false,
        [],
        ["Tool prep has not run yet."]);

    private async Task TryCopyTrConfigAsync(string artifactPath, CancellationToken ct)
    {
        try
        {
            if (File.Exists(_config.TrunkRecorder.ConfigPath))
            {
                await using var source = File.OpenRead(_config.TrunkRecorder.ConfigPath);
                await using var target = File.Create(Path.Combine(artifactPath, "tr-config-before.json"));
                await source.CopyToAsync(target, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to copy TR config into Radio Setup artifact folder");
        }
    }

    private static async Task WriteArtifactAsync<T>(string root, string fileName, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, fileName),
            JsonSerializer.Serialize(value, EngineConfig.JsonOptions()) + Environment.NewLine,
            ct);
    }

    private void DeleteArtifactDirectory(string artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
            return;

        var root = Path.GetFullPath(ArtifactRoot);
        var target = Path.GetFullPath(artifactPath);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refusing to delete Radio Setup artifact path outside root: {ArtifactPath}", artifactPath);
            return;
        }

        if (!Directory.Exists(target))
            return;

        try
        {
            Directory.Delete(target, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Deleted Radio Setup database row but could not remove artifact path {ArtifactPath}", target);
        }
    }

    private static T? DeserializeOrDefault<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try { return JsonSerializer.Deserialize<T>(json, EngineConfig.JsonOptions()); }
        catch { return default; }
    }

    private static string NormalizeMode(string mode)
    {
        mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return mode is "manual" or "expert" ? "expert" : mode is "desk" ? "desk" : "guided";
    }

    private static string NormalizeSourcePlanMode(string? mode)
    {
        mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return mode is "control" or "control_channels" or "cc" ? "control" : "full";
    }

    private static IReadOnlyList<string> NormalizeRequestedSystemNames(IReadOnlyList<string>? systemShortNames, string? fallbackSystemShortName)
    {
        var names = (systemShortNames ?? [])
            .Concat(string.IsNullOrWhiteSpace(fallbackSystemShortName) ? [] : [fallbackSystemShortName])
            .Select(name => (name ?? string.Empty).Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return names;
    }

    private static IReadOnlyList<string> EffectiveSystemNames(RfSurveyProfileDto profile) =>
        profile.SystemShortNames.Count > 0
            ? profile.SystemShortNames
            : NormalizeRequestedSystemNames(null, profile.SystemShortName);

    private static IReadOnlyList<string> SourcePlanSystemNames(RfSurveyProfileDto profile) =>
        profile.SourcePlanSystemShortNames.Count > 0
            ? profile.SourcePlanSystemShortNames
            : EffectiveSystemNames(profile);

    private static IReadOnlyList<RfSurveySystemDto> BuildSourcePlanSystems(
        RfSurveyProfileDto profile,
        IReadOnlyList<string> sourcePlanSystemNames,
        IReadOnlyList<RfSurveyExperimentDto> experiments,
        List<string> warnings)
    {
        var systems = profile.Systems
            .Where(system => sourcePlanSystemNames.Any(name => string.Equals(name, system.ShortName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var latestSweep = experiments
            .Where(experiment => string.Equals(experiment.Type, "rf_validation_sweep", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(experiment => experiment.CreatedAtUtc)
            .FirstOrDefault();
        var candidates = ReadRfValidationCandidates(latestSweep?.EvidenceJson);
        if (candidates.Count == 0)
            return systems;

        var planned = new List<RfSurveySystemDto>();
        foreach (var system in systems)
        {
            var siteCandidates = candidates
                .Where(candidate => CandidateBelongsToSystem(candidate, system))
                .ToList();
            if (siteCandidates.Count == 0)
            {
                planned.Add(system);
                continue;
            }

            var passing = siteCandidates
                .Where(IsMonitorableRfCandidate)
                .OrderByDescending(ScoreRfValidationCandidate)
                .FirstOrDefault();
            if (passing == null)
            {
                warnings.Add($"RF Sweep did not prove a usable control channel for {system.SiteLabel}; Config Draft will not allocate source bandwidth to that site.");
                continue;
            }

            planned.Add(system with { ControlChannelsHz = [passing.ControlChannelHz] });
        }
        return planned;
    }

    private static IReadOnlyList<RfSurveySystemDto> AddObservedVoiceFrequencies(
        IReadOnlyList<RfSurveySystemDto> systems,
        IReadOnlyDictionary<string, IReadOnlyList<long>> observedFrequencies,
        List<string> warnings)
    {
        if (systems.Count == 0 || observedFrequencies.Count == 0)
            return systems;

        var augmented = new List<RfSurveySystemDto>(systems.Count);
        foreach (var system in systems)
        {
            if (system.VoiceFrequenciesHz.Count > 0 ||
                !observedFrequencies.TryGetValue(system.ShortName, out var observed) ||
                observed.Count == 0)
            {
                augmented.Add(system);
                continue;
            }

            var controlChannels = system.ControlChannelsHz.ToHashSet();
            var voice = observed
                .Where(value => value > 0 && !controlChannels.Contains(value))
                .Distinct()
                .Order()
                .ToList();
            if (voice.Count == 0)
            {
                augmented.Add(system);
                continue;
            }

            augmented.Add(system with { VoiceFrequenciesHz = voice });
            warnings.Add($"{system.SiteLabel}: no imported voice channel list was available; Config Draft is using {voice.Count} observed PizzaWave call frequenc{(voice.Count == 1 ? "y" : "ies")} for source-window planning.");
        }
        return augmented;
    }

    private async Task<Dictionary<string, IReadOnlyList<long>>> BuildObservedVoiceFrequenciesBySystemAsync(CancellationToken ct)
    {
        var combined = (await _database.ListObservedCallFrequenciesBySystemAsync(ct))
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Where(value => value > 0).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);

        var log = await ReadTrJournalAsync(DateTime.UtcNow.AddHours(-12), DateTime.UtcNow, ct);
        foreach (Match match in TrCallEventRegex.Matches(log))
        {
            var message = match.Groups["message"].Value;
            if (!message.Contains("no source covering", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("Starting P25 Recorder", StringComparison.OrdinalIgnoreCase))
                continue;

            var system = match.Groups["system"].Value.Trim();
            if (string.IsNullOrWhiteSpace(system))
                continue;
            var frequency = (long)Math.Round(ParseDouble(match.Groups["freq"].Value) * 1_000_000d);
            if (frequency <= 0)
                continue;
            if (!combined.TryGetValue(system, out var values))
            {
                values = [];
                combined[system] = values;
            }
            values.Add(frequency);
        }

        return combined.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<long>)kvp.Value.Distinct().Order().ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool SourcePlanRfValidationPassed(RfSurveyProfileDto profile, IReadOnlyList<RfSurveyExperimentDto> experiments)
    {
        var requested = SourcePlanSystemNames(profile);
        if (requested.Count == 0)
            return false;
        var systems = profile.Systems
            .Where(system => requested.Any(name => string.Equals(name, system.ShortName, StringComparison.OrdinalIgnoreCase)))
            .Where(system => system.ControlChannelsHz.Count > 0)
            .ToList();
        if (systems.Count == 0 || systems.Count != requested.Count)
            return false;
        var latestSweep = experiments
            .Where(experiment => string.Equals(experiment.Type, "rf_validation_sweep", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(experiment => experiment.CreatedAtUtc)
            .FirstOrDefault();
        var candidates = ReadRfValidationCandidates(latestSweep?.EvidenceJson);
        if (candidates.Count == 0)
            return false;
        return systems.All(system => candidates
            .Where(candidate => CandidateBelongsToSystem(candidate, system))
            .Any(IsMonitorableRfCandidate));
    }

    private static IReadOnlyDictionary<int, RfValidationCandidate> BuildRfValidationSourceCandidates(
        RfSurveyProfileDto profile,
        IReadOnlyList<RfSurveyExperimentDto> experiments)
    {
        var requested = SourcePlanSystemNames(profile);
        var systems = profile.Systems
            .Where(system => requested.Count == 0 || requested.Any(name => string.Equals(name, system.ShortName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var selectedSources = profile.SelectedSourceIndexes.Count > 0
            ? profile.SelectedSourceIndexes.ToHashSet()
            : profile.Sources.Select(source => source.Index).ToHashSet();
        foreach (var experiment in experiments
            .Where(experiment => string.Equals(experiment.Type, "rf_validation_sweep", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(experiment => experiment.CreatedAtUtc))
        {
            var candidates = ReadRfValidationCandidates(experiment.EvidenceJson)
                .Where(candidate => selectedSources.Count == 0 || selectedSources.Contains(candidate.SourceIndex))
                .Where(candidate => systems.Count == 0 || systems.Any(system => CandidateBelongsToSystem(candidate, system)))
                .Select(candidate => candidate.Score == 0 ? candidate with { Score = ScoreRfValidationCandidate(candidate) } : candidate)
                .ToList();
            if (candidates.Count == 0)
                continue;

            var usable = candidates.Any(IsMonitorableRfCandidate)
                ? candidates.Where(IsMonitorableRfCandidate).ToList()
                : candidates.Where(candidate => candidate.P25Frames || string.Equals(candidate.P25Status, "passed", StringComparison.OrdinalIgnoreCase)).ToList();
            var chosen = usable
                .GroupBy(candidate => candidate!.SourceIndex)
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(ScoreRfValidationCandidate)
                        .First());
            if (chosen.Count > 0)
                return chosen;
        }

        return new Dictionary<int, RfValidationCandidate>();
    }

    private static IReadOnlyList<RfValidationCandidate> ReadRfValidationCandidates(string? evidenceJson)
    {
        if (string.IsNullOrWhiteSpace(evidenceJson))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
                return [];
            return JsonSerializer.Deserialize<List<RfValidationCandidate>>(candidates.GetRawText(), EngineConfig.JsonOptions()) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool CandidateBelongsToSystem(RfValidationCandidate candidate, RfSurveySystemDto system)
    {
        if (!string.IsNullOrWhiteSpace(candidate.SystemShortName) &&
            string.Equals(candidate.SystemShortName, system.ShortName, StringComparison.OrdinalIgnoreCase))
            return true;
        return system.ControlChannelsHz.Contains(candidate.ControlChannelHz);
    }

    private static IReadOnlyList<RfSurveySystemDto> NormalizeSystemDefinitions(IReadOnlyList<RfSurveySystemDto>? definitions) =>
        (definitions ?? [])
        .Where(definition => !string.IsNullOrWhiteSpace(definition.ShortName))
        .Select(definition => definition with
        {
            ShortName = definition.ShortName.Trim(),
            SiteLabel = string.IsNullOrWhiteSpace(definition.SiteLabel) ? definition.ShortName.Trim() : definition.SiteLabel.Trim(),
            ControlChannelsHz = definition.ControlChannelsHz.Where(value => value > 0).Distinct().Order().ToList(),
            VoiceFrequenciesHz = definition.VoiceFrequenciesHz.Where(value => value > 0).Distinct().Order().ToList()
        })
        .GroupBy(definition => definition.ShortName, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .ToList();

    private static IReadOnlyList<RfSurveySourceDto> NormalizeRfSurveySources(IReadOnlyList<RfSurveySourceDto>? sources) =>
        (sources ?? [])
        .Where(source => !string.IsNullOrWhiteSpace(source.Device) || !string.IsNullOrWhiteSpace(source.Serial))
        .Select((source, index) => new RfSurveySourceDto(
            source.Index >= 0 ? source.Index : index,
            (source.Device ?? string.Empty).Trim(),
            (source.Serial ?? string.Empty).Trim(),
            string.IsNullOrWhiteSpace(source.SdrType) ? InferSdrType(source.Device ?? string.Empty, source.Serial ?? string.Empty) : source.SdrType.Trim(),
            source.CenterHz > 0 ? source.CenterHz : 0,
            source.SampleRate > 0 ? source.SampleRate : 2_400_000,
            source.ErrorHz,
            string.IsNullOrWhiteSpace(source.Gain) ? "auto" : source.Gain.Trim()))
        .GroupBy(source => source.Index)
        .Select(group => group.First())
        .OrderBy(source => source.Index)
        .ToList();

    private static IReadOnlyList<RfSurveySystemDto> MergeSystemDefinitions(
        IReadOnlyList<RfSurveySystemDto> primaryDefinitions,
        IReadOnlyList<RfSurveySystemDto> fallbackDefinitions)
    {
        var merged = new List<RfSurveySystemDto>(primaryDefinitions);
        foreach (var definition in fallbackDefinitions)
        {
            if (merged.Any(existing => string.Equals(existing.ShortName, definition.ShortName, StringComparison.OrdinalIgnoreCase)))
                continue;
            merged.Add(definition);
        }
        return merged;
    }

    private static IReadOnlyList<SetupCalibrationSystemPlanDto> SelectCalibrationSystems(
        IReadOnlyList<SetupCalibrationSystemPlanDto> systems,
        IReadOnlyList<string> requestedSystemNames)
    {
        if (systems.Count == 0)
            return [];
        if (requestedSystemNames.Count == 0)
            return [];

        var selected = new List<SetupCalibrationSystemPlanDto>();
        foreach (var requested in requestedSystemNames)
        {
            var match = systems.FirstOrDefault(system => string.Equals(system.ShortName, requested, StringComparison.OrdinalIgnoreCase));
            if (match != null && selected.All(system => !string.Equals(system.ShortName, match.ShortName, StringComparison.OrdinalIgnoreCase)))
                selected.Add(match);
        }
        return selected;
    }

    private static IReadOnlyList<RfSurveySystemDto> SelectSurveySystems(
        IReadOnlyList<RfSurveySystemDto> systems,
        IReadOnlyList<string> requestedSystemNames)
    {
        if (systems.Count == 0)
            return [];
        if (requestedSystemNames.Count == 0)
            return [];

        var selected = new List<RfSurveySystemDto>();
        foreach (var requested in requestedSystemNames)
        {
            var match = systems.FirstOrDefault(system => string.Equals(system.ShortName, requested, StringComparison.OrdinalIgnoreCase));
            if (match != null && selected.All(system => !string.Equals(system.ShortName, match.ShortName, StringComparison.OrdinalIgnoreCase)))
                selected.Add(match);
        }
        return selected;
    }


    private static IReadOnlyList<int> NormalizeSelectedSourceIndexes(IReadOnlyList<int>? selected, IReadOnlyList<RfSurveySourceDto> sources, IReadOnlyList<SetupCalibrationSystemPlanDto>? systems)
    {
        var valid = sources.Select(source => source.Index).ToHashSet();
        if (selected != null)
            return selected.Where(valid.Contains).Distinct().Order().ToList();
        var proposed = (systems ?? [])
            .SelectMany(system => system.ProposedSourceIndexes)
            .Where(valid.Contains)
            .Distinct()
            .Order()
            .ToList();
        return proposed.Count > 0 ? proposed : sources.Select(source => source.Index).ToList();
    }

    private static bool IsValidTrSampleRate(int sampleRate) =>
        sampleRate > 0;

    private static RfSurveyProfileDto ProfileWithSampleRateOverride(RfSurveyProfileDto profile, int sampleRate)
    {
        var selected = profile.SelectedSourceIndexes.Count > 0
            ? profile.SelectedSourceIndexes.ToHashSet()
            : profile.Sources.Select(source => source.Index).ToHashSet();
        return profile with
        {
            Sources = profile.Sources
                .Select(source => selected.Contains(source.Index) ? source with { SampleRate = sampleRate } : source)
                .ToList()
        };
    }

    private static bool SameIntSet(IReadOnlyList<int> left, IReadOnlyList<int> right) =>
        left.Count == right.Count && left.Order().SequenceEqual(right.Order());

    private static bool SameStringSet(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(right.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    private static string SummarizeSdrs(IReadOnlyList<RfSurveySourceDto> sources)
    {
        if (sources.Count == 0) return "No configured SDR sources";
        var grouped = sources.GroupBy(s => s.SdrType).Select(g => $"{g.Count()} {g.Key}");
        return string.Join(", ", grouped);
    }

    private static string SummarizeSelectedSdrs(RfSurveyProfileDto profile) =>
        SummarizeSdrs(profile.Sources
            .Where(source => profile.SelectedSourceIndexes.Count == 0 || profile.SelectedSourceIndexes.Contains(source.Index))
            .ToList());

    private static string SummarizeRfPath(RfSurveyPathProfileDto path)
    {
        if (path.Chain.Count > 0)
        {
            var antenna = string.IsNullOrWhiteSpace(path.AntennaType) ? "antenna" : path.AntennaType.Trim();
            return $"{antenna} / {path.Chain.Count} RF chain item(s)";
        }
        var parts = new[] { path.AntennaType, path.Antenna, path.Coax, path.SplitterOrMulticoupler, path.Lna, path.Filters }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim());
        var summary = string.Join(" / ", parts);
        return string.IsNullOrWhiteSpace(summary) ? "RF path not described yet" : summary;
    }

    private static bool HasMeaningfulRfPath(RfSurveyPathProfileDto? path)
    {
        if (path == null)
            return false;
        if (new[] { path.Antenna, path.PositionNotes, path.ConnectorChain, path.Coax, path.SplitterOrMulticoupler, path.Lna, path.Filters, path.SdrNotes, path.Observations }
            .Any(value => !string.IsNullOrWhiteSpace(value)))
            return true;
        if (!string.IsNullOrWhiteSpace(path.AntennaType) && !string.Equals(path.AntennaType, "yagi", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var item in path.Chain)
        {
            var type = (item.Type ?? string.Empty).Trim().ToLowerInvariant();
            var label = (item.Label ?? string.Empty).Trim();
            var isDefaultAntenna = type == "antenna" && (string.IsNullOrWhiteSpace(label) || string.Equals(label, "Yagi", StringComparison.OrdinalIgnoreCase));
            var isDefaultSdr = type == "sdr" && (string.IsNullOrWhiteSpace(label) || string.Equals(label, "Configured SDR", StringComparison.OrdinalIgnoreCase));
            if (!isDefaultAntenna && !isDefaultSdr)
                return true;
            if (new[] { item.ConnectorIn, item.ConnectorOut, item.Length, item.Loss, item.Power, item.Notes, item.GainDb, item.Passband }
                .Any(value => !string.IsNullOrWhiteSpace(value) && !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!string.IsNullOrWhiteSpace(item.GroundPlane) && !string.Equals(item.GroundPlane, "unknown", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(item.PowerPass) && !string.Equals(item.PowerPass, "unknown", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(item.PowerMethod) && !string.Equals(item.PowerMethod, "unknown", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(item.PortCount))
                return true;
        }
        return false;
    }

    private static JsonObject CloneJsonObject(JsonObject? source)
    {
        if (source == null)
            return [];
        return JsonNode.Parse(source.ToJsonString()) as JsonObject ?? [];
    }

    private static JsonArray CloneJsonArray(JsonArray source)
    {
        return JsonNode.Parse(source.ToJsonString()) as JsonArray ?? [];
    }

    private static JsonObject BuildCleanRadioSetupTrConfigRoot(JsonObject? template = null)
    {
        var root = template == null ? [] : CloneJsonObject(template);
        root["sources"] = new JsonArray();
        root["systems"] = new JsonArray();
        root["plugins"] = new JsonArray();
        return root;
    }

    private static JsonObject EnsureSourceObject(JsonObject root, int sourceIndex)
    {
        if (sourceIndex < 0)
            sourceIndex = 0;
        var sources = root["sources"] as JsonArray ?? [];
        root["sources"] = sources;
        while (sources.Count <= sourceIndex)
            sources.Add(new JsonObject());
        if (sources[sourceIndex] is JsonObject source)
            return source;
        source = [];
        sources[sourceIndex] = source;
        return source;
    }

    private static IReadOnlyList<string> TrimDraftToWorkspaceSystems(JsonObject root, IReadOnlyList<string> systemShortNames, List<string> changes, List<string> warnings)
    {
        var systems = root["systems"] as JsonArray;
        if (systems == null || systems.Count == 0)
        {
            warnings.Add("Live TR config does not contain systems to prune.");
            return [];
        }
        var requested = systemShortNames
            .Select(name => (name ?? string.Empty).Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            warnings.Add("Workspace system short names are missing; TR systems were left unchanged.");
            return systems
                .OfType<JsonObject>()
                .Select(system => system["shortName"]?.GetValue<string>() ?? string.Empty)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
        }

        var kept = new JsonArray();
        var removed = new List<string>();
        var keptNames = new List<string>();
        foreach (var system in systems.OfType<JsonObject>())
        {
            var shortName = system["shortName"]?.GetValue<string>() ?? string.Empty;
            if (requested.Contains(shortName))
            {
                kept.Add(CloneJsonObject(system));
                keptNames.Add(shortName);
            }
            else if (!string.IsNullOrWhiteSpace(shortName))
            {
                removed.Add(shortName);
            }
        }

        if (kept.Count == 0)
        {
            warnings.Add($"Workspace systems '{string.Join(", ", requested)}' were not found in the live TR config; Radio Setup will add them from workspace ground truth if available.");
            root["systems"] = kept;
            return [];
        }

        root["systems"] = kept;
        if (removed.Count > 0)
            changes.Add($"systems: kept {string.Join(", ", keptNames)}; removed {string.Join(", ", removed)} from the active TR config for this workspace draft");
        return keptNames;
    }

    private static IReadOnlyList<string> EnsureDraftWorkspaceSystems(
        JsonObject root,
        IReadOnlyList<RfSurveySystemDto> definitions,
        IReadOnlyList<string> requestedNames,
        IReadOnlyList<string> keptNames,
        List<string> changes,
        List<string> warnings)
    {
        var systems = root["systems"] as JsonArray ?? [];
        root["systems"] = systems;
        var names = keptNames.ToList();
        foreach (var requested in requestedNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            if (names.Any(name => string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)))
                continue;
            var definition = definitions.FirstOrDefault(row => string.Equals(row.ShortName, requested, StringComparison.OrdinalIgnoreCase));
            if (definition == null)
            {
                warnings.Add($"Workspace system '{requested}' has no frequency definition; it was not added to the TR config draft.");
                continue;
            }
            JsonObject system = [];
            system["shortName"] = definition.ShortName;
            system["type"] = "p25";
            system["modulation"] = "qpsk";
            if (!string.IsNullOrWhiteSpace(definition.TalkgroupSystemShortName))
                system["talkgroupSystemShortName"] = TalkgroupCatalogService.NormalizeSystemShortName(definition.TalkgroupSystemShortName);
            system["control_channels"] = new JsonArray(definition.ControlChannelsHz.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            if (definition.VoiceFrequenciesHz.Count > 0)
                system["channels"] = new JsonArray(definition.VoiceFrequenciesHz.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            systems.Add(system);
            names.Add(definition.ShortName);
            changes.Add($"systems: added {definition.ShortName} from Radio Setup workspace ground truth");
        }
        return names;
    }

    private void PatchCallstreamStreams(JsonObject root, IReadOnlyList<string> systemShortNames, List<string> changes)
    {
        if (systemShortNames.Count == 0)
            return;
        var plugins = root["plugins"] as JsonArray ?? [];
        root["plugins"] = plugins;
        var callstream = plugins.OfType<JsonObject>()
            .FirstOrDefault(plugin => string.Equals(plugin["name"]?.GetValue<string>(), "callstream", StringComparison.OrdinalIgnoreCase));
        if (callstream == null)
        {
            callstream = [];
            plugins.Add(callstream);
        }
        callstream["name"] = "callstream";
        callstream["library"] = callstream["library"]?.GetValue<string>() ?? "libcallstream.so";
        callstream["host"] = _config.Ingest.CallstreamBind;
        callstream["port"] = _config.Ingest.CallstreamPort;
        var clients = new JsonArray();
        JsonObject client = [];
        client["address"] = _config.Ingest.CallstreamBind;
        client["port"] = _config.Ingest.CallstreamPort;
        clients.Add(client);
        callstream["clients"] = clients;
        var before = callstream["streams"]?.ToJsonString() ?? "null";
        callstream["streams"] = new JsonArray(systemShortNames.Select(shortName =>
        {
            JsonObject stream = [];
            stream["TGID"] = 0;
            stream["shortName"] = shortName;
            return (JsonNode?)stream;
        }).ToArray());
        var after = callstream["streams"]?.ToJsonString() ?? "null";
        if (!string.Equals(before, after, StringComparison.Ordinal))
            changes.Add("callstream streams updated for workspace system list");
    }

    private static void NormalizeRadioSetupTrConfig(JsonObject root, List<string> changes, string talkgroupsPath)
    {
        SetRootValue(root, "ver", 2, changes);
        SetRootValue(root, "defaultMode", "digital", changes);
        SetRootValue(root, "logFile", true, changes);
        SetRootValue(root, "logDir", "/var/log/trunk-recorder", changes);
        SetRootValue(root, "tempDir", "/var/lib/trunk-recorder/tmp", changes);
        SetRootValue(root, "frequencyFormat", "mhz", changes);
        SetRootValue(root, "statusAsString", true, changes);
        SetRootValue(root, "broadcastSignals", true, changes);
        SetRootValue(root, "logLevel", "info", changes);
        SetRootValue(root, "controlRetuneLimit", 0, changes);
        SetRootValue(root, "controlWarnRate", -1, changes, overwrite: true);
        SetRootValue(root, "audioStreaming", true, changes, overwrite: true);

        if (root["systems"] is JsonArray systems)
        {
            foreach (var system in systems.OfType<JsonObject>())
                NormalizeRadioSetupSystem(system, changes, talkgroupsPath);
        }

        if (root["sources"] is JsonArray sources)
        {
            for (var index = 0; index < sources.Count; index++)
            {
                if (sources[index] is JsonObject source)
                    NormalizeRadioSetupSource(source, index, changes);
            }
        }

        TrRecorderCapacitySizer.EnsureJsonConfigRecorderCapacity(root, changes);
    }

    private static void NormalizeRadioSetupSystem(JsonObject system, List<string> changes, string talkgroupsPath)
    {
        SetObjectValue(system, "system", "type", "p25", changes);
        SetObjectValue(system, "system", "modulation", "qpsk", changes);
        SetObjectValue(system, "system", "compressWav", false, changes);
        SetObjectValue(system, "system", "audioArchive", false, changes);
        SetObjectValue(system, "system", "transmissionArchive", false, changes);
        SetObjectValue(system, "system", "callLog", true, changes);
        SetObjectValue(system, "system", "recordUnknown", false, changes);
        SetObjectValue(system, "system", "recordUUVCalls", true, changes);
        SetObjectValue(system, "system", "hideEncrypted", true, changes);
        SetObjectValue(system, "system", "hideUnknownTalkgroups", false, changes);
        SetObjectValue(system, "system", "minDuration", 0, changes);
        SetObjectValue(system, "system", "minTransmissionDuration", 0, changes);
        SetObjectValue(system, "system", "talkgroupDisplayFormat", "id_tag", changes);
        SetObjectValue(system, "system", "multiSite", true, changes);
        var shortName = system["shortName"]?.GetValue<string>() ?? system["short_name"]?.GetValue<string>() ?? system["name"]?.GetValue<string>() ?? string.Empty;
        var talkgroupSystem = system["talkgroupSystemShortName"]?.GetValue<string>() ?? string.Empty;
        var existingTalkgroupsFile = system["talkgroupsFile"]?.GetValue<string>() ?? string.Empty;
        var resolvedTalkgroupsFile = !string.IsNullOrWhiteSpace(talkgroupSystem)
            ? TalkgroupCatalogService.TrCsvPathForSystem(talkgroupsPath, talkgroupSystem)
            : !string.IsNullOrWhiteSpace(existingTalkgroupsFile)
                ? existingTalkgroupsFile
                : TalkgroupCatalogService.TrCsvPathForSystem(talkgroupsPath, shortName);
        SetObjectValue(system, "system", "talkgroupsFile", resolvedTalkgroupsFile, changes);
        system.Remove("talkgroupSystemShortName");

        var postprocess = system["audio_postprocess"] as JsonObject;
        if (postprocess == null)
        {
            postprocess = [];
            system["audio_postprocess"] = postprocess;
            changes.Add("system audio_postprocess default added");
        }
        SetObjectValue(postprocess, "system audio_postprocess", "loudnorm", false, changes);
        SetObjectValue(postprocess, "system audio_postprocess", "loudnorm_two_pass", false, changes);
    }

    private static void PatchSourceGainFields(JsonObject source, RfSurveySourceDto? profileSource, string? gain, List<string> changes, int sourceIndex)
    {
        if (string.IsNullOrWhiteSpace(gain))
            return;

        if (IsAirspySource(profileSource))
        {
            if (source.Remove("gain"))
                changes.Add($"source {sourceIndex} gain removed for Airspy stage gains");
            var stageGains = BuildAirspyStageGains(gain);
            PatchSourceField(source, "lnaGain", stageGains.Lna, changes, sourceIndex);
            PatchSourceField(source, "mixGain", stageGains.Mix, changes, sourceIndex);
            PatchSourceField(source, "ifGain", stageGains.If, changes, sourceIndex);
            return;
        }

        if (source.Remove("lnaGain"))
            changes.Add($"source {sourceIndex} lnaGain removed for generic gain");
        if (source.Remove("mixGain"))
            changes.Add($"source {sourceIndex} mixGain removed for generic gain");
        if (source.Remove("ifGain"))
            changes.Add($"source {sourceIndex} ifGain removed for generic gain");
        PatchSourceField(source, "gain", ParseJsonValue(gain), changes, sourceIndex);
    }

    private static bool HasAirspyStageGains(JsonObject source) =>
        source.ContainsKey("lnaGain") || source.ContainsKey("mixGain") || source.ContainsKey("ifGain");

    private static void NormalizeRadioSetupSource(JsonObject source, int sourceIndex, List<string> changes)
    {
        SetObjectValue(source, $"source {sourceIndex}", "error", 0, changes);
        if (!HasAirspyStageGains(source))
            SetObjectValue(source, $"source {sourceIndex}", "gain", 32, changes);
        SetObjectValue(source, $"source {sourceIndex}", "digitalRecorders", TrRecorderCapacitySizer.MinimumDigitalRecorders, changes);
        SetObjectValue(source, $"source {sourceIndex}", "analogRecorders", 0, changes);
        SetObjectValue(source, $"source {sourceIndex}", "driver", "osmosdr", changes);
        SetObjectValue(source, $"source {sourceIndex}", "agc", false, changes);

        if (source["device"] is JsonValue value &&
            value.TryGetValue<string>(out var device) &&
            device.StartsWith("rtl=", StringComparison.OrdinalIgnoreCase) &&
            !device.Contains(',', StringComparison.Ordinal))
        {
            source["device"] = device + ",buflen=65536";
                changes.Add($"source {sourceIndex} device: {JsonSerializer.Serialize(device)} -> {JsonSerializer.Serialize(device + ",buflen=65536")}");
        }
    }

    private static void SetRootValue(JsonObject root, string key, object value, List<string> changes, bool overwrite = false) =>
        SetObjectValue(root, "root", key, value, changes, overwrite);

    private static void SetObjectValue(JsonObject target, string scope, string key, object value, List<string> changes, bool overwrite = false)
    {
        var next = CreateJsonValue(value);
        if (!overwrite && target.ContainsKey(key))
            return;
        if (JsonNodeEquals(target[key], next))
            return;
        var before = target[key]?.ToJsonString() ?? "null";
        var after = next?.ToJsonString() ?? "null";
        target[key] = next;
        changes.Add($"{scope} {key}: {before} -> {after}");
    }

    private static void PatchLiveCallstreamFromDraft(JsonObject liveRoot, JsonArray draftPlugins)
    {
        var draftCallstream = draftPlugins.OfType<JsonObject>()
            .FirstOrDefault(plugin => string.Equals(plugin["name"]?.GetValue<string>(), "callstream", StringComparison.OrdinalIgnoreCase));
        if (draftCallstream == null)
            return;
        var livePlugins = liveRoot["plugins"] as JsonArray ?? [];
        liveRoot["plugins"] = livePlugins;
        for (var i = 0; i < livePlugins.Count; i++)
        {
            if (livePlugins[i] is JsonObject plugin &&
                string.Equals(plugin["name"]?.GetValue<string>(), "callstream", StringComparison.OrdinalIgnoreCase))
            {
                livePlugins[i] = CloneJsonObject(draftCallstream);
                return;
            }
        }
        livePlugins.Add(CloneJsonObject(draftCallstream));
    }

    private static int ReadInt(JsonArray sources, int sourceIndex, string field)
    {
        if (sourceIndex < 0 || sourceIndex >= sources.Count || sources[sourceIndex] is not JsonObject source)
            return 0;
        return source[field] switch
        {
            JsonValue value when value.TryGetValue<int>(out var intValue) => intValue,
            JsonValue value when value.TryGetValue<long>(out var longValue) => (int)longValue,
            JsonValue value when value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static List<SourceWindow> BuildSourceWindows(IReadOnlyList<long> frequencies, int sampleRate, IReadOnlyList<long>? priorityFrequencies = null)
    {
        var rows = new List<SourceWindow>();
        if (frequencies.Count == 0)
            return rows;
        var halfSpan = Math.Max(1, TrUsableHalfBandwidthHz(sampleRate));
        var span = halfSpan * 2;
        var priorities = (priorityFrequencies ?? [])
            .Where(value => value > 0)
            .Distinct()
            .Order()
            .ToList();
        var index = 0;
        while (index < frequencies.Count)
        {
            var start = frequencies[index];
            var endIndex = index;
            while (endIndex + 1 < frequencies.Count && frequencies[endIndex + 1] - start <= span)
                endIndex++;
            var end = frequencies[endIndex];
            var center = CenterForSourceWindow(start, end, halfSpan, priorities);
            rows.Add(new SourceWindow(center, start, end, endIndex - index + 1));
            index = endIndex + 1;
        }
        return rows;
    }

    private static long CenterForSourceWindow(long start, long end, long halfSpan, IReadOnlyList<long> priorityFrequencies)
    {
        var midpoint = (long)Math.Round((start + end) / 2.0, MidpointRounding.AwayFromZero);
        var minCenter = end - halfSpan;
        var maxCenter = start + halfSpan;
        if (minCenter > maxCenter)
            return midpoint;

        var priority = priorityFrequencies
            .Where(value => value >= start && value <= end)
            .OrderBy(value => Math.Abs(value - midpoint))
            .FirstOrDefault();
        var desired = priority > 0 ? priority : midpoint;
        return Math.Clamp(desired, minCenter, maxCenter);
    }

    private static void PatchSourceField(JsonObject source, string field, object? value, List<string> changes, int sourceIndex)
    {
        if (value == null)
            return;
        var next = CreateJsonValue(value);
        if (JsonNodeEquals(source[field], next))
            return;
        var before = source[field]?.ToJsonString() ?? "null";
        var after = next?.ToJsonString() ?? "null";
        source[field] = next;
        changes.Add($"source {sourceIndex} {field}: {before} -> {after}");
    }

    private static JsonNode? CreateJsonValue(object value) =>
        value switch
        {
            string text => JsonValue.Create(text),
            int intValue => JsonValue.Create(intValue),
            long longValue => JsonValue.Create(longValue),
            double doubleValue => JsonValue.Create(doubleValue),
            bool boolValue => JsonValue.Create(boolValue),
            _ => JsonValue.Create(value.ToString() ?? string.Empty)
        };

    private static object ParseJsonValue(string text)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            return intValue;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
            return doubleValue;
        return text;
    }

    private static bool JsonNodeEquals(JsonNode? left, JsonNode? right) =>
        string.Equals(left?.ToJsonString() ?? "null", right?.ToJsonString() ?? "null", StringComparison.Ordinal);

    private static string NormalizeJson(JsonNode node) =>
        node.ToJsonString(EngineConfig.JsonOptions()).Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd() + "\n";

    private readonly record struct SourceWindow(long CenterHz, long LowHz, long HighHz, int Count);

    private static string NormalizeCandidateTrialType(string type)
    {
        type = (type ?? string.Empty).Trim().Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return type switch
        {
            "control" or "control_channel" => "control_channel",
            "center" or "source_center" => "source_center",
            "gain" or "source_gain" => "source_gain",
            "rate" or "sample_rate" => "sample_rate",
            _ => type
        };
    }

    private static string PatchCandidate(JsonObject root, RfSurveyProfileDto profile, string trialType, RfSurveyCandidateRequest request, List<string> warnings)
    {
        return trialType switch
        {
            "control_channel" => PatchControlChannel(root, profile, request, warnings),
            "source_center" => PatchSourceNumber(root, profile, request.SourceIndex, "center", request.CenterHz, warnings),
            "source_gain" => PatchSourceGain(root, profile, request.SourceIndex, request.Gain, warnings),
            "sample_rate" => PatchSourceNumber(root, profile, request.SourceIndex, "rate", request.SampleRate, warnings),
            _ => throw new InvalidOperationException("Unsupported Radio Setup candidate trial type.")
        };
    }

    private static string PatchControlChannel(JsonObject root, RfSurveyProfileDto profile, RfSurveyCandidateRequest request, List<string> warnings)
    {
        var control = request.ControlChannelHz ?? profile.ControlChannelsHz.FirstOrDefault();
        if (control <= 0)
            throw new InvalidOperationException("A control channel is required for this candidate.");
        var system = FindSystemObject(root, profile.SystemShortName);
        system["control_channels"] = new JsonArray(JsonValue.Create(control));
        var label = string.IsNullOrWhiteSpace(profile.SystemShortName) ? "first system" : profile.SystemShortName;
        return $"Set {label} control_channels to {control} Hz.";
    }

    private static string PatchSourceNumber(JsonObject root, RfSurveyProfileDto profile, int? sourceIndex, string field, long? value, List<string> warnings)
    {
        if (value is null or <= 0)
            throw new InvalidOperationException($"{field} value is required for this candidate.");
        var source = FindSourceObject(root, sourceIndex ?? profile.Sources.FirstOrDefault()?.Index ?? 0);
        source[field] = value.Value;
        return $"Set source {sourceIndex ?? profile.Sources.FirstOrDefault()?.Index ?? 0} {field} to {value.Value}.";
    }

    private static string PatchSourceGain(JsonObject root, RfSurveyProfileDto profile, int? sourceIndex, string? gain, List<string> warnings)
    {
        gain = (gain ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(gain))
            throw new InvalidOperationException("Gain value is required for this candidate.");
        var source = FindSourceObject(root, sourceIndex ?? profile.Sources.FirstOrDefault()?.Index ?? 0);
        if (double.TryParse(gain, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            source["gain"] = numeric;
        else
            source["gain"] = gain;
        return $"Set source {sourceIndex ?? profile.Sources.FirstOrDefault()?.Index ?? 0} gain to {gain}.";
    }

    private static JsonObject FindSystemObject(JsonObject root, string shortName)
    {
        var systems = root["systems"] as JsonArray ?? throw new InvalidOperationException("TR config has no systems array.");
        foreach (var node in systems)
        {
            if (node is JsonObject obj &&
                (string.IsNullOrWhiteSpace(shortName) || string.Equals(obj["shortName"]?.GetValue<string>(), shortName, StringComparison.OrdinalIgnoreCase)))
                return obj;
        }
        return systems.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException("TR config has no system objects.");
    }

    private static IEnumerable<string> ReadSystemShortNames(JsonObject root)
    {
        if (root["systems"] is not JsonArray systems)
            yield break;
        foreach (var system in systems.OfType<JsonObject>())
        {
            var shortName = system["shortName"]?.GetValue<string>() ?? system["short_name"]?.GetValue<string>() ?? system["name"]?.GetValue<string>() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(shortName))
                yield return shortName.Trim();
        }
    }

    private static JsonObject FindSourceObject(JsonObject root, int index)
    {
        var sources = root["sources"] as JsonArray ?? throw new InvalidOperationException("TR config has no sources array.");
        if (index >= 0 && index < sources.Count && sources[index] is JsonObject byIndex)
            return byIndex;
        return sources.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException("TR config has no source objects.");
    }

    private static IReadOnlyList<string> BuildSimpleDiff(string before, string after)
    {
        var beforeLines = before.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var afterLines = after.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var rows = new List<string>();
        var max = Math.Max(beforeLines.Length, afterLines.Length);
        for (var i = 0; i < max; i++)
        {
            var left = i < beforeLines.Length ? beforeLines[i] : string.Empty;
            var right = i < afterLines.Length ? afterLines[i] : string.Empty;
            if (left == right) continue;
            rows.Add($"- {left}");
            rows.Add($"+ {right}");
            if (rows.Count >= 80)
            {
                rows.Add("... diff truncated ...");
                break;
            }
        }
        return rows.Count == 0 ? ["No textual diff; candidate matches live config."] : rows;
    }

    private static string InferSdrType(string device, string serial)
    {
        var text = $"{device} {serial}";
        if (text.Contains("airspy", StringComparison.OrdinalIgnoreCase))
            return "Airspy";
        return "RTL-SDR";
    }

    private RfSurveyP25ProbePreviewDto BuildP25ProbePreview(RfSurveyProfileDto profile, string artifactPath, long? controlChannelHz, int? durationSeconds)
    {
        var configured = !string.IsNullOrWhiteSpace(_config.RfSurvey.P25ProbeCommandTemplate);
        var controlChannel = controlChannelHz ?? profile.ControlChannelsHz.FirstOrDefault();
        var blocking = string.Empty;
        if (!configured)
            blocking = "Radio Setup P25 probe command template is not configured.";
        else if (controlChannel <= 0)
            blocking = "No control channel is available from setup/RR ground truth.";
        else if (profile.Sources.Count == 0)
            blocking = "No SDR source is available from setup/TR config.";

        var outputDir = Path.Combine(artifactPath, "probe-runs", "<timestamp>");
        var command = configured && string.IsNullOrWhiteSpace(blocking)
            ? RenderP25ProbeCommand(profile, controlChannel, durationSeconds ?? _config.RfSurvey.P25ProbeDurationSeconds, outputDir)
            : _config.RfSurvey.P25ProbeCommandTemplate;

        return new RfSurveyP25ProbePreviewDto(
            configured,
            configured && string.IsNullOrWhiteSpace(blocking),
            command,
            _config.RfSurvey.P25ProbeWorkingDirectory,
            blocking,
            [
                "{frequency_hz}", "{frequency_mhz}", "{sample_rate}", "{gain}", "{error_hz}", "{error_ppm}",
                "{device}", "{serial}", "{source_index}", "{duration_seconds}", "{output_dir}", "{sdr_type}", "{p25_gain_args}"
            ]);
    }

    private string RenderP25ProbeCommand(RfSurveyProfileDto profile, long controlChannelHz, int? durationSeconds, string outputDir, int? sourceIndex = null, string? demod = null)
    {
        var source = sourceIndex.HasValue
            ? profile.Sources.FirstOrDefault(s => s.Index == sourceIndex.Value) ?? SelectSourceForFrequency(profile, controlChannelHz)
            : SelectSourceForFrequency(profile, controlChannelHz);
        var duration = Math.Clamp(durationSeconds ?? _config.RfSurvey.P25ProbeDurationSeconds, 10, 300);
        var command = _config.RfSurvey.P25ProbeCommandTemplate;
        var usesTypedGainArgs = command.Contains("{p25_gain_args}", StringComparison.OrdinalIgnoreCase);
        var errorPpm = source == null || controlChannelHz <= 0
            ? 0
            : -source.ErrorHz / (controlChannelHz / 1_000_000.0);
        var p25SampleRate = P25ProbeSampleRate(profile, source);
        var pinAirspySerial = profile.Sources.Count(IsAirspySource) > 1;
        var normalizedDemod = NormalizeP25Demod(demod);
        if (string.IsNullOrWhiteSpace(normalizedDemod))
            normalizedDemod = "cqpsk";
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{frequency_hz}"] = controlChannelHz.ToString(CultureInfo.InvariantCulture),
            ["{frequency_mhz}"] = (controlChannelHz / 1_000_000.0).ToString("F6", CultureInfo.InvariantCulture),
            ["{sample_rate}"] = p25SampleRate.ToString(CultureInfo.InvariantCulture),
            ["{gain}"] = ShellQuote(NormalizeP25ProbeGain(source?.Gain)),
            ["{error_hz}"] = (source?.ErrorHz ?? 0).ToString(CultureInfo.InvariantCulture),
            ["{error_ppm}"] = errorPpm.ToString("F6", CultureInfo.InvariantCulture),
            ["{device}"] = ShellQuote(NormalizeP25ProbeDeviceArgsWithSerialPinning(source, usesTypedGainArgs, pinAirspySerial)),
            ["{serial}"] = ShellQuote(pinAirspySerial ? source?.Serial ?? string.Empty : string.Empty),
            ["{source_index}"] = (source?.Index ?? -1).ToString(CultureInfo.InvariantCulture),
            ["{duration_seconds}"] = duration.ToString(CultureInfo.InvariantCulture),
            ["{output_dir}"] = ShellQuote(outputDir),
            ["{sdr_type}"] = ShellQuote(source?.SdrType ?? string.Empty),
            ["{demod}"] = ShellQuote(normalizedDemod),
            ["{p25_gain_args}"] = BuildP25ProbeGainArgs(source)
        };
        foreach (var pair in replacements)
            command = command.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);
        command = ApplyP25DemodOverride(command, demod);
        return command;
    }

    private static string ApplyP25DemodOverride(string command, string? demod)
    {
        var normalized = NormalizeP25Demod(demod);
        if (string.IsNullOrWhiteSpace(normalized))
            return command;
        var shortDemod = new Regex(@"(?<!\S)-D\s+\S+");
        if (shortDemod.IsMatch(command))
            return shortDemod.Replace(command, "-D " + normalized, 1);
        var longDemod = new Regex(@"(?<!\S)--demod-type(?:=|\s+)\S+");
        if (longDemod.IsMatch(command))
            return longDemod.Replace(command, "--demod-type " + normalized, 1);
        return command + " -D " + normalized;
    }

    private static int P25ProbeSampleRate(RfSurveyProfileDto profile, RfSurveySourceDto? source)
    {
        var requested = Math.Max(0, source?.SampleRate ?? 0);
        if (requested <= 0 || !IsAirspySource(source))
            return requested;
        return AirspyRuntimeSampleRate(Math.Max(requested, 6_000_000), AirspySampleRateOptionsForSource(profile, source));
    }

    private static RfSurveySourceDto? SelectSourceForFrequency(RfSurveyProfileDto profile, long frequencyHz)
    {
        if (frequencyHz <= 0)
            return profile.Sources.FirstOrDefault();
        return profile.Sources
            .Select(source =>
            {
                var half = TrUsableHalfBandwidthHz(source.SampleRate);
                var distance = Math.Abs(source.CenterHz - frequencyHz);
                var covers = half > 0 && distance <= half;
                return new { source, covers, distance };
            })
            .OrderByDescending(row => row.covers)
            .ThenBy(row => row.distance)
            .Select(row => row.source)
            .FirstOrDefault();
    }

    private static TrSourceCoverageCheck CheckTrSourceCoverage(RfSurveyProfileDto profile, int? sourceIndex, long? controlChannelHz)
    {
        if (!controlChannelHz.HasValue || controlChannelHz.Value <= 0)
            return new TrSourceCoverageCheck(true, string.Empty, []);

        var sources = sourceIndex.HasValue
            ? profile.Sources.Where(source => source.Index == sourceIndex.Value).ToList()
            : profile.Sources.ToList();
        var rows = sources.Select(source =>
        {
            var half = TrUsableHalfBandwidthHz(source.SampleRate);
            var low = source.CenterHz - half;
            var high = source.CenterHz + half;
            return new TrSourceCoverageRow(
                source.Index,
                source.Serial,
                source.CenterHz,
                source.SampleRate,
                low,
                high,
                half > 0 && controlChannelHz.Value >= low && controlChannelHz.Value <= high);
        }).ToList();

        if (rows.Any(row => row.Covers))
            return new TrSourceCoverageCheck(true, string.Empty, rows);

        if (sources.Count == 0)
        {
            var missing = sourceIndex.HasValue ? $"Source {sourceIndex.Value} is not present in the Radio Setup profile." : "No SDR source is present in the Radio Setup profile.";
            return new TrSourceCoverageCheck(false, $"{missing} Select or generate a TR config source before running TR CC Metrics.", rows);
        }

        var nearest = rows
            .OrderBy(row => Math.Min(Math.Abs(controlChannelHz.Value - row.LowHz), Math.Abs(controlChannelHz.Value - row.HighHz)))
            .First();
        var sourceLabel = sourceIndex.HasValue ? $"Source {nearest.Index}" : $"Nearest source {nearest.Index}";
        return new TrSourceCoverageCheck(
            false,
            $"{sourceLabel} usable TR window {FormatHz(nearest.LowHz)}-{FormatHz(nearest.HighHz)} does not cover {profile.SystemShortName} control channel {FormatHz(controlChannelHz.Value)}. Increase sample rate or adjust source center/control-channel selection before TR metrics.",
            rows);
    }

    private static long TrUsableHalfBandwidthHz(int sampleRate) =>
        (long)Math.Floor(Math.Max(0, sampleRate) * TrUsableHalfBandwidthFactor);

    private TrConfigSourceCoverageValidation? CurrentTrSourceCoverageValidation()
    {
        var path = _config.TrunkRecorder.ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;
        try
        {
            return TrConfigSourceCoverageValidator.Validate(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return new TrConfigSourceCoverageValidation(
                false,
                [$"TR config source coverage could not be validated: {ex.Message}"],
                [],
                []);
        }
    }

    private static bool HasP25FrameEvidence(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;
        var lowered = output.ToLowerInvariant();
        if (lowered.Contains("no p25", StringComparison.OrdinalIgnoreCase) ||
            lowered.Contains("could not find terminal", StringComparison.OrdinalIgnoreCase) ||
            lowered.Contains("terminal: exception", StringComparison.OrdinalIgnoreCase))
            return false;
        var strongMarkers = new[]
        {
            "tsbk", "duid", "voice grant", "grp_v_ch_grant", "net_sts_bcst", "rfss_sts_bcst",
            "secondary control channel", "mbt"
        };
        return strongMarkers.Any(lowered.Contains);
    }

    private static bool HasP25ProbeToolFailure(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;
        var lowered = output.ToLowerInvariant();
        return lowered.Contains("traceback", StringComparison.OrdinalIgnoreCase) ||
               lowered.Contains("valueerror", StringComparison.OrdinalIgnoreCase) ||
               lowered.Contains("modulenotfounderror", StringComparison.OrdinalIgnoreCase) ||
               lowered.Contains("usb_claim_interface", StringComparison.OrdinalIgnoreCase) ||
               lowered.Contains("source_c creation failure", StringComparison.OrdinalIgnoreCase) ||
               lowered.Contains("failed to open rtlsdr device", StringComparison.OrdinalIgnoreCase) ||
               lowered.Contains("no such file or directory", StringComparison.OrdinalIgnoreCase) ||
               lowered.Contains("command not found", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAirspyOpenFailure(string output) =>
        !string.IsNullOrWhiteSpace(output) &&
        (output.Contains("AIRSPY_ERROR_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("Failed to open AirSpy device", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("airspy_open", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeP25ProbeGain(string? gain)
    {
        var trimmed = (gain ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "0";
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
            return ((int)Math.Round(numeric, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        return trimmed;
    }

    private static string BuildP25ProbeGainArgs(RfSurveySourceDto? source)
    {
        if (IsAirspySource(source))
            return "-N " + ShellQuote(FormatAirspyStageGains(BuildAirspyStageGains(source?.Gain)));
        return "-g " + ShellQuote(NormalizeP25ProbeGain(source?.Gain));
    }

    private static string FormatAirspyStageGains(AirspyStageGains gains) =>
        $"LNA:{gains.Lna},MIX:{gains.Mix},IF:{gains.If}";

    private static AirspyStageGains BuildAirspyStageGains(string? gain)
    {
        var normalized = NormalizeP25ProbeGain(gain);
        if (!int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            value = 20;
        value = Math.Clamp(value, 0, 21);
        return value switch
        {
            <= 0 => new(0, 0, 0),
            <= 7 => new(4, 4, 2),
            <= 13 => new(8, 6, 4),
            <= 19 => new(12, 10, 6),
            _ => new(15, 12, 8)
        };
    }

    private static string NormalizeP25ProbeDeviceArgs(RfSurveySourceDto? source, bool useNamedAirspyStageGains = false) =>
        NormalizeP25ProbeDeviceArgsWithSerialPinning(source, useNamedAirspyStageGains, pinAirspySerial: true);

    private static string NormalizeP25ProbeDeviceArgsWithSerialPinning(RfSurveySourceDto? source, bool useNamedAirspyStageGains, bool pinAirspySerial)
    {
        var device = (source?.Device ?? string.Empty).Trim();
        if (!IsAirspySource(source))
            return device;

        if (string.IsNullOrWhiteSpace(device))
        {
            var serial = (source?.Serial ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(serial))
                device = $"airspy={serial}";
        }

        if (string.IsNullOrWhiteSpace(device))
            return device;

        device = NormalizeAirspyDeviceSelector(device, pinAirspySerial);
        var parts = device
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Count == 0)
            return device;

        var hasAirspySelector = parts[0].Contains("airspy", StringComparison.OrdinalIgnoreCase);
        if (!hasAirspySelector)
            return device;

        if (useNamedAirspyStageGains)
        {
            parts = parts
                .Where((part, index) => index == 0 ||
                    (!part.Equals("linearity", StringComparison.OrdinalIgnoreCase) &&
                     !part.StartsWith("linearity=", StringComparison.OrdinalIgnoreCase) &&
                     !part.Equals("sensitivity", StringComparison.OrdinalIgnoreCase) &&
                     !part.StartsWith("sensitivity=", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            parts.Add("sensitivity=0");
            parts.Add("linearity=0");
        }
        else
        {
        var hasGainMode = parts.Any(part =>
            part.Equals("linearity", StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith("linearity=", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("sensitivity", StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith("sensitivity=", StringComparison.OrdinalIgnoreCase));
        if (!hasGainMode)
            parts.Add("linearity");
        }

        var hasBias = parts.Any(part => part.StartsWith("bias=", StringComparison.OrdinalIgnoreCase));
        if (!hasBias)
            parts.Add("bias=0");

        return string.Join(',', parts);
    }

    private static bool IsAirspySource(RfSurveySourceDto? source)
    {
        if (source == null)
            return false;
        return string.Equals(source.SdrType, "Airspy", StringComparison.OrdinalIgnoreCase) ||
               source.Device.Contains("airspy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRtlSource(RfSurveySourceDto? source)
    {
        if (source == null || IsAirspySource(source))
            return false;
        return string.Equals(source.SdrType, "RTL-SDR", StringComparison.OrdinalIgnoreCase) ||
               source.Device.Contains("rtl", StringComparison.OrdinalIgnoreCase);
    }

    private string TrUnitName()
    {
        var service = string.IsNullOrWhiteSpace(_config.TrunkRecorder.LogServiceName)
            ? "trunk-recorder"
            : _config.TrunkRecorder.LogServiceName.Trim();
        return service.EndsWith(".service", StringComparison.OrdinalIgnoreCase) ? service : service + ".service";
    }

    private async Task<string> EnsureCandidateTrConfigAsync(string artifactPath, CancellationToken ct)
    {
        var candidatePath = Path.Combine(artifactPath, "tr-config-candidate.json");
        if (File.Exists(candidatePath))
            return candidatePath;
        if (string.IsNullOrWhiteSpace(_config.TrunkRecorder.ConfigPath) || !File.Exists(_config.TrunkRecorder.ConfigPath))
            throw new InvalidOperationException("Live trunk-recorder config was not found.");
        var text = await File.ReadAllTextAsync(_config.TrunkRecorder.ConfigPath, ct);
        using var doc = JsonDocument.Parse(text);
        await File.WriteAllTextAsync(candidatePath, JsonSerializer.Serialize(doc.RootElement, EngineConfig.JsonOptions()) + Environment.NewLine, ct);
        return candidatePath;
    }

    private async Task<string> InstallTrFileAsync(string sourcePath, string targetPath, CancellationToken ct)
    {
        if (NeedsProtectedTrWrite(targetPath))
        {
            var helper = FindAdminHelper() ?? throw new FileNotFoundException("pizzawave_setup_admin.sh was not found; protected TR config writes are unavailable.");
            var output = await RunAdminHelperAsync(helper, ["install-tr-file", sourcePath, targetPath], ct);
            if (output.ExitCode != 0)
                throw new InvalidOperationException($"install-tr-file failed: {output.Output.Trim()}");
            return output.Output.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Contains(".bak-", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");
        var backup = string.Empty;
        if (File.Exists(targetPath))
        {
            backup = $"{targetPath}.bak-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Copy(targetPath, backup, overwrite: false);
        }
        File.Copy(sourcePath, targetPath, overwrite: true);
        return backup;
    }

    private async Task<string> RunServiceHelperAsync(string action, CancellationToken ct, bool stopWaterfallsFirst = true)
    {
        if (OperatingSystem.IsWindows())
            return $"{action} is unavailable on Windows.";
        var waterfallStopOutput = stopWaterfallsFirst && (string.Equals(action, "start-tr", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "restart-tr", StringComparison.OrdinalIgnoreCase))
            ? await StopActiveWaterfallsBeforeTrStartAsync(ct)
            : string.Empty;
        var helper = FindAdminHelper();
        if (!string.IsNullOrWhiteSpace(helper))
        {
            var result = await RunAdminHelperAsync(helper, [action], ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"{action} failed: {result.Output.Trim()}");
            return string.IsNullOrWhiteSpace(waterfallStopOutput) ? result.Output : waterfallStopOutput + Environment.NewLine + result.Output;
        }

        var command = action switch
        {
            "stop-tr" => "stop",
            "start-tr" => "start",
            "restart-tr" => "restart",
            _ => throw new InvalidOperationException("Unsupported TR service action.")
        };
        var direct = await RunCaptureAsync("systemctl", $"{command} {TrUnitName()}", ct);
        if (direct.ExitCode != 0)
            throw new InvalidOperationException($"systemctl {command} {TrUnitName()} failed: {direct.Stdout.Trim()}");
        return string.IsNullOrWhiteSpace(waterfallStopOutput) ? direct.Stdout : waterfallStopOutput + Environment.NewLine + direct.Stdout;
    }

    private async Task<TrServiceState> QueryTrActiveAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return new TrServiceState(TrUnitName(), false, "unknown", -1, "systemctl is not available on Windows.");
        var result = await RunCaptureAsync("systemctl", $"is-active {TrUnitName()}", ct);
        var state = result.Stdout.Trim();
        return new TrServiceState(
            TrUnitName(),
            state.Equals("active", StringComparison.OrdinalIgnoreCase),
            string.IsNullOrWhiteSpace(state) ? "unknown" : state,
            result.ExitCode,
            TrimOutput(result.Stdout));
    }

    private static string NormalizeExperimentType(string type)
    {
        type = (type ?? string.Empty).Trim().Replace("-", "_", StringComparison.Ordinal);
        return type switch
        {
            "ground_truth" or "ground_truth_review" => "ground_truth_review",
            "tr_stopped" or "tr_stopped_check" => "tr_stopped_check",
            "sdr" or "sdr_inventory" => "sdr_inventory",
            "rf_power" or "rf_power_scan" or "signal_capture" => "rf_power_scan",
            "rf_validation" or "rf_validation_sweep" or "combined_rf_sweep" => "rf_validation_sweep",
            "cc_quality" or "control_channel_quality" => "control_channel_quality",
            "p25" or "control_channel" or "control_channel_p25_probe" => "control_channel_p25_probe",
            "sweep" or "error_gain" or "error_gain_sweep" => "error_gain_sweep",
            "sweep_cancel" or "error_gain_sweep_cancel" => "error_gain_sweep_cancel",
            "temp_tr_config" or "temp_tr_config_plan" => "temp_tr_config_plan",
            "voice" or "voice_capture" or "voice_capture_trial" => "voice_capture_trial",
            "transcription" or "transcription_gate" => "transcription_gate",
            "stability" or "stability_verdict" => "stability_verdict",
            _ => type
        };
    }

    private static string TrimOutput(string value, int maxLength = 4000)
    {
        value = value ?? string.Empty;
        maxLength = Math.Max(100, maxLength);
        if (value.Length <= maxLength) return value.Trim();
        return value[..maxLength].Trim() + "\n...[truncated]";
    }

    private static bool NeedsProtectedTrWrite(string path) =>
        !OperatingSystem.IsWindows() && path.StartsWith("/etc/trunk-recorder/", StringComparison.Ordinal);

    private static string? FindAdminHelper()
    {
        var candidates = new[]
        {
            "/usr/lib/pizzawave/scripts/pizzawave_setup_admin.sh",
            "/opt/pizzawave/pizzad/scripts/pizzawave_setup_admin.sh",
            "/opt/pizzawave/scripts/pizzawave_setup_admin.sh",
            Path.Combine(AppContext.BaseDirectory, "scripts", "pizzawave_setup_admin.sh"),
            Path.Combine(AppContext.BaseDirectory, "pizzawave_setup_admin.sh")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task<(int ExitCode, string Output)> RunAdminHelperAsync(string helper, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("sudo")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(helper);
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi);
        if (process == null)
            return (-1, "Unable to start sudo helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, (await stdout) + (await stderr));
    }

    private static async Task<bool> CommandExistsAsync(string command, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return false;
        var result = await RunCaptureAsync("bash", $"-lc \"command -v {command} >/dev/null 2>&1\"", ct);
        return result.ExitCode == 0;
    }

    private static async Task<(int ExitCode, string Stdout)> RunCaptureAsync(string fileName, string arguments, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            process = Process.Start(psi);
            if (process == null) return (-1, "failed to start process");
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                await WaitForProcessExitAfterKillAsync(process);
                var partialOutput = await ReadProcessOutputAfterKillAsync(stdoutTask, stderrTask);
                return (-1, "Process timed out or was canceled and was killed.\n" + partialOutput);
            }
            return (process.ExitCode, (await stdoutTask) + (await stderrTask));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static async Task WaitForProcessExitAfterKillAsync(Process process)
    {
        try
        {
            using var killWait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(killWait.Token);
        }
        catch { }
    }

    private static async Task<string> ReadProcessOutputAfterKillAsync(Task<string> stdoutTask, Task<string> stderrTask)
    {
        try
        {
            var output = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(2));
            var error = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2));
            return output + error;
        }
        catch
        {
            return "Process output could not be fully read after kill.";
        }
    }

    private async Task<StaleP25CleanupResult> CleanupStaleP25ProcessesAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
            return new StaleP25CleanupResult("", "", "", "");

        const string pidSelector = "ps -eo pid=,comm=,args= | awk '$2 == \"python3\" && index($0, \"/rx.py --args\") > 0 { print $1 }'";
        var before = (await RunCaptureAsync("bash", "-lc " + Quote(pidSelector), ct)).Stdout.Trim();
        if (string.IsNullOrWhiteSpace(before))
            return new StaleP25CleanupResult("", "", "", "");

        var cleanupCommand = string.Join("; ", [
            $"pids=$({pidSelector})",
            "if [ -n \"$pids\" ]; then kill $pids 2>/dev/null || true; fi",
            "sleep 1",
            $"pids=$({pidSelector})",
            "if [ -n \"$pids\" ]; then kill -9 $pids 2>/dev/null || true; fi",
            "sleep 1",
            pidSelector
        ]);
        var cleanup = await RunCaptureAsync("bash", "-lc " + Quote(cleanupCommand), ct);
        var after = cleanup.Stdout.Trim();
        var blocking = string.IsNullOrWhiteSpace(after)
            ? ""
            : "A stale OP25/P25 probe process is still running and may hold the SDR. Stop it or reboot before running P25 probing again.";
        return new StaleP25CleanupResult(before, after, cleanup.Stdout, blocking);
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string ShellQuote(string value)
    {
        value ??= string.Empty;
        if (OperatingSystem.IsWindows())
            return Quote(value);
        return "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    private sealed record TrServiceState(string Service, bool Active, string State, int ExitCode, string Output);

    private sealed record ExperimentOutcome(
        string Status,
        string Hypothesis,
        string RequiredSetup,
        string ResultSummary,
        string BlockingIssue,
        object Evidence,
        object Interpretation);

    private sealed record TranscriptionProviderReadiness(
        string Provider,
        string Endpoint,
        string Model,
        bool Available,
        string CheckStatus,
        string CheckDetail,
        string BlockingIssue)
    {
        public static TranscriptionProviderReadiness Ready(string provider, string endpoint, string model, string status, string detail) =>
            new(provider, endpoint, model, true, status, detail, string.Empty);

        public static TranscriptionProviderReadiness Blocked(string provider, string endpoint, string model, string status, string detail) =>
            new(provider, endpoint, model, false, status, detail, detail);
    }

    private static (long StartUnix, long EndUnix) ResolveSurveyWindow(RfSurveySessionDto session, int durationSeconds)
    {
        var start = new DateTimeOffset(session.CreatedAtUtc).ToUnixTimeSeconds();
        var requestedEnd = start + Math.Clamp(durationSeconds <= 0 ? 3600 : durationSeconds, 60, 86400);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (start, Math.Max(requestedEnd, now));
    }

    private async Task<(List<EngineCall> Calls, List<EngineCall> RealCalls)> WaitForSurveyCallsAsync(RfSurveyProfileDto profile, long startUnix, long endUnix, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        (List<EngineCall> Calls, List<EngineCall> RealCalls) latest;
        while (true)
        {
            latest = await ListSurveyCallsAsync(profile, startUnix, endUnix, ct);
            if (latest.RealCalls.Count > 0 || DateTimeOffset.UtcNow >= deadline)
                return latest;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private async Task<(List<EngineCall> Calls, List<EngineCall> RealCalls)> ListSurveyCallsAsync(RfSurveyProfileDto profile, long startUnix, long endUnix, CancellationToken ct)
    {
        var calls = await _database.ListCallsAsync(startUnix, endUnix, null, ct);
        if (!string.IsNullOrWhiteSpace(profile.SystemShortName))
            calls = calls.Where(c => string.Equals(c.SystemShortName, profile.SystemShortName, StringComparison.OrdinalIgnoreCase)).ToList();
        var realCalls = calls.Where(c => !c.IsImported && !string.IsNullOrWhiteSpace(c.AudioPath)).ToList();
        return (calls, realCalls);
    }

    private async Task<(long StartUnix, long EndUnix)> ResolveCallQualityWindowAsync(RfSurveySessionDto session, RfSurveyRunExperimentRequest request, int fallbackDurationSeconds, CancellationToken ct)
    {
        if (TryReadWindow(request.Parameters, out var fromRequest))
            return fromRequest;

        var experiments = await _database.ListRfSurveyExperimentsAsync(session.Id, ct);
        var latestVoice = experiments
            .Where(e => string.Equals(e.Type, "voice_capture_trial", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.CreatedAtUtc)
            .LastOrDefault(e => string.Equals(e.Status, "passed", StringComparison.OrdinalIgnoreCase) || string.Equals(e.Status, "failed", StringComparison.OrdinalIgnoreCase));
        if (TryReadWindow(latestVoice?.EvidenceJson, out var fromVoice))
            return fromVoice;

        var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var duration = Math.Clamp(fallbackDurationSeconds <= 0 ? 300 : fallbackDurationSeconds, 15, 86400);
        return (end - duration, end);
    }

    private static IEnumerable<EngineCall> UsableTranscripts(IEnumerable<EngineCall> calls) =>
        calls.Where(c =>
            string.Equals(c.TranscriptionStatus, "complete", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.QualityReason, "ok", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(c.Transcription) &&
            c.Transcription.Trim().Length >= MinUsableTranscriptionGateChars);

    private async Task<TranscriptionProviderReadiness> CheckTranscriptionProviderReadinessAsync(CancellationToken ct)
    {
        var provider = (_config.Transcription.Provider ?? "none").Trim().ToLowerInvariant();
        var endpoint = (_config.Transcription.OpenAiBaseUrl ?? string.Empty).Trim();
        var model = ResolveTranscriptionModel(provider);
        if (provider == "none")
            return TranscriptionProviderReadiness.Blocked(provider, endpoint, model, "not_configured", "Transcription is not configured.");

        if (provider is not ("remote-faster-whisper" or "lmstudio" or "openai"))
            return TranscriptionProviderReadiness.Ready(provider, endpoint, model, "local_provider", "Local transcription provider readiness is checked by setup settings.");

        if (string.IsNullOrWhiteSpace(endpoint))
            return TranscriptionProviderReadiness.Blocked(provider, endpoint, model, "missing_endpoint", $"The {provider} transcription provider has no Base URL.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return TranscriptionProviderReadiness.Blocked(provider, endpoint, model, "invalid_endpoint", $"The transcription Base URL is invalid: {endpoint}");

        var port = uri.IsDefaultPort ? (string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : uri.Port;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, port, timeout.Token);
            return TranscriptionProviderReadiness.Ready(provider, endpoint, model, "reachable", $"Connected to {uri.Host}:{port}.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TranscriptionProviderReadiness.Blocked(provider, endpoint, model, "connection_timeout", $"The transcription endpoint {endpoint} did not accept a connection within 3 seconds.");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return TranscriptionProviderReadiness.Blocked(provider, endpoint, model, "connection_failed", $"The transcription endpoint {endpoint} is unreachable: {ex.Message}");
        }
    }

    private string ResolveTranscriptionModel(string provider)
    {
        if (provider == "faster-whisper")
            return _config.Transcription.FasterWhisperModel;
        if (provider == "whisper")
            return Path.GetFileName(_config.Transcription.WhisperModelFile ?? string.Empty);
        if (provider == "vosk")
            return _config.Transcription.VoskModelPath;
        return string.IsNullOrWhiteSpace(_config.Transcription.OpenAiModel) ? "whisper-1" : _config.Transcription.OpenAiModel;
    }

    private static string ExtractTranscriptionError(string rawMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(rawMetadataJson))
            return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(rawMetadataJson);
            if (!doc.RootElement.TryGetProperty("transcription", out var transcription) ||
                transcription.ValueKind != JsonValueKind.Object)
                return string.Empty;
            var type = transcription.TryGetProperty("errorType", out var errorType) ? errorType.GetString() ?? string.Empty : string.Empty;
            var message = transcription.TryGetProperty("errorMessage", out var errorMessage) ? errorMessage.GetString() ?? string.Empty : string.Empty;
            return string.IsNullOrWhiteSpace(type) ? message : string.IsNullOrWhiteSpace(message) ? type : $"{type}: {message}";
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static bool TryReadWindow(JsonElement? parameters, out (long StartUnix, long EndUnix) window)
    {
        window = default;
        if (parameters is not { ValueKind: JsonValueKind.Object } root)
            return false;
        if (!TryGetInt64(root, "windowStartUnix", out var start) || !TryGetInt64(root, "windowEndUnix", out var end) || end <= start)
            return false;
        window = (start, end);
        return true;
    }

    private static bool TryReadWindow(string? evidenceJson, out (long StartUnix, long EndUnix) window)
    {
        window = default;
        if (string.IsNullOrWhiteSpace(evidenceJson))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(evidenceJson);
            if (!TryGetInt64(doc.RootElement, "startUnix", out var start) || !TryGetInt64(doc.RootElement, "endUnix", out var end) || end <= start)
                return false;
            window = (start, end);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetInt64(JsonElement root, string propertyName, out long value)
    {
        value = 0;
        if (!root.TryGetProperty(propertyName, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
            return true;
        if (element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        return false;
    }

    private static IReadOnlyList<string> BuildRecommendations(RfSurveyDetailDto detail)
    {
        var rows = new List<string>();
        var combinedRfPassed = LatestStatus(detail.Experiments, "rf_validation_sweep") == "passed";
        if (LatestStatus(detail.Experiments, "ground_truth_review") != "passed")
            rows.Add("Confirm RR/setup ground truth before treating RF findings as conclusive.");
        if (!combinedRfPassed && LatestStatus(detail.Experiments, "rf_validation_sweep") != "passed")
            rows.Add("Run the combined RF Sweep to rank candidates using RF margin, P25 evidence, and TR CC metrics.");
        if (!combinedRfPassed && LatestStatus(detail.Experiments, "control_channel_quality") != "passed")
            rows.Add("Measure stable per-control-channel decode quality before treating error/gain sweeps as conclusive.");
        if (!combinedRfPassed && LatestStatus(detail.Experiments, "rf_power_scan") != "passed")
            rows.Add("Run RF power scan to confirm the selected control channel stands above the noise floor without overload.");
        if (!combinedRfPassed && LatestStatus(detail.Experiments, "control_channel_p25_probe") != "passed")
            rows.Add("Do not proceed to voice acceptance until a real P25 control-channel probe passes.");
        if (LatestStatus(detail.Experiments, "voice_capture_trial") != "passed")
            rows.Add("Run a TR capture trial; real calls prove voice, while healthy TR metrics without calls can carry a no-traffic caveat.");
        if (LatestStatus(detail.Experiments, "transcription_gate") != "passed")
            rows.Add("Test transcription when captured call audio exists; do not block only because no calls occurred during healthy TR metrics.");
        if (LatestStatus(detail.Experiments, "stability_verdict") != "passed")
            rows.Add("Run a longer stability window when traffic exists, or carry the no-traffic caveat from healthy TR metrics.");
        if (rows.Count == 0)
            rows.Add("Radio Setup evidence supports applying the plan, subject to operator review.");
        return rows;
    }

    private static string LatestStatus(IReadOnlyList<RfSurveyExperimentDto> experiments, string type) =>
        experiments
            .Where(e => string.Equals(e.Type, type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.CreatedAtUtc)
            .LastOrDefault()
            ?.Status ?? string.Empty;

    private static (IReadOnlyList<string> Recommendations, IReadOnlyList<string> Blockers) BuildExportContent(RfSurveyDetailDto detail)
    {
        var recommendations = BuildRecommendations(detail);
        var blockers = detail.Experiments
            .Where(e => !string.IsNullOrWhiteSpace(e.BlockingIssue))
            .Select(e => $"{e.Type}: {e.BlockingIssue}")
            .Distinct()
            .ToList();
        return (recommendations, blockers);
    }

    private static string RenderExportMarkdown(RfSurveyDetailDto detail, IReadOnlyList<string> recommendations, IReadOnlyList<string> blockers)
    {
        var lines = new List<string>
        {
            $"# Radio Setup Export: {detail.Session.SiteLabel}",
            "",
            $"- Survey: {detail.Session.Id}",
            $"- Verdict: {detail.Session.Verdict}",
            $"- Stability: {detail.Session.Stability}",
            $"- RF path: {detail.Session.RfPathSummary}",
            "",
            "## Recommendations"
        };
        lines.AddRange(recommendations.Select(r => $"- {r}"));
        lines.Add("");
        lines.Add("## Blockers");
        lines.AddRange(blockers.Count == 0 ? ["- None recorded."] : blockers.Select(b => $"- {b}"));
        lines.Add("");
        lines.Add("## Experiments");
        lines.AddRange(detail.Experiments.Select(e => $"- {e.Type}: {e.Status} - {(string.IsNullOrWhiteSpace(e.BlockingIssue) ? e.ResultSummary : e.BlockingIssue)}"));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
