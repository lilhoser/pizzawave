using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;

namespace pizzad;

public sealed class EngineConfig
{
    public ServerConfig Server { get; set; } = new();
    public BrandingConfig Branding { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public IngestConfig Ingest { get; set; } = new();
    public TranscriptionConfig Transcription { get; set; } = new();
    public AiInsightsConfig AiInsights { get; set; } = new();
    public EmbeddingsConfig Embeddings { get; set; } = new();
    public TrunkRecorderConfig TrunkRecorder { get; set; } = new();
    public RfSurveyConfig RfSurvey { get; set; } = new();
    public SiteSetupConfig SiteSetup { get; set; } = new();
    public AlertConfig Alerts { get; set; } = new();
    public ProfileConfig Profiles { get; set; } = new();
    public LocationConfig Locations { get; set; } = new();
    public SetupConfig Setup { get; set; } = new();

    [JsonIgnore]
    public string ConfigPath { get; set; } = string.Empty;

    public static EngineConfig Load(string? path)
    {
        path = ExpandPath(path);
        EngineConfig config;
        if (!File.Exists(path))
        {
            config = new EngineConfig();
            config.ConfigPath = path;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions()));
        }
        else
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var hasSetupSection = document.RootElement.TryGetProperty("setup", out _);
            config = JsonSerializer.Deserialize<EngineConfig>(json, JsonOptions()) ?? new EngineConfig();
            config.ConfigPath = path;
            if (!hasSetupSection)
            {
                config.Setup.Completed = true;
                config.Setup.CompletedAtUtc = DateTime.UtcNow;
                config.Setup.CurrentStep = "complete";
            }
        }

        config.ApplyDefaults();
        return config;
    }

    public void Save()
    {
        if (string.IsNullOrWhiteSpace(ConfigPath))
            throw new InvalidOperationException("ConfigPath is not set.");

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath) ?? ".");
        SaveAtomic(ConfigPath, JsonSerializer.Serialize(this, JsonOptions()));
    }

    public EngineConfig Clone()
    {
        var clone = JsonSerializer.Deserialize<EngineConfig>(JsonSerializer.Serialize(this, JsonOptions()), JsonOptions()) ?? new EngineConfig();
        clone.ConfigPath = ConfigPath;
        clone.ApplyDefaults();
        return clone;
    }

    private static void SaveAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            TryPreserveUnixMode(path, tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (UnauthorizedAccessException) when (File.Exists(path))
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            File.WriteAllText(path, content);
        }
    }

    private static void TryPreserveUnixMode(string originalPath, string tempPath)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(originalPath))
            return;

        try
        {
            File.SetUnixFileMode(tempPath, File.GetUnixFileMode(originalPath));
        }
        catch
        {
            // Best effort only. Deploy/install scripts own final permission repair.
        }
    }

    public void ApplyDefaults()
    {
        Branding.StackName = string.IsNullOrWhiteSpace(Branding.StackName) ? "PizzaWave" : Branding.StackName.Trim();
        Server.HttpBind = string.IsNullOrWhiteSpace(Server.HttpBind) ? "0.0.0.0" : Server.HttpBind.Trim();
        if (Server.HttpPort <= 0) Server.HttpPort = 8080;
        Storage.DatabasePath = ExpandPath(Storage.DatabasePath);
        Storage.AudioRoot = ExpandPath(Storage.AudioRoot);
        Storage.AppDataRoot = ExpandPath(Storage.AppDataRoot);
        Auth.TokenFile = ExpandPath(Auth.TokenFile);
        Ingest.CallstreamBind = string.IsNullOrWhiteSpace(Ingest.CallstreamBind) ? "127.0.0.1" : Ingest.CallstreamBind.Trim();
        if (Ingest.CallstreamPort <= 0) Ingest.CallstreamPort = 9123;
        if (Ingest.MaxConcurrentClients <= 0) Ingest.MaxConcurrentClients = 4;
        Transcription.Provider = string.IsNullOrWhiteSpace(Transcription.Provider) ? "none" : Transcription.Provider.Trim();
        Transcription.WhisperModelFile ??= string.Empty;
        Transcription.VoskModelPath ??= string.Empty;
        Transcription.FasterWhisperPythonPath = string.IsNullOrWhiteSpace(Transcription.FasterWhisperPythonPath) ? "/opt/pizzawave/venv/faster-whisper/bin/python" : Transcription.FasterWhisperPythonPath.Trim();
        Transcription.FasterWhisperScriptPath = Transcription.FasterWhisperScriptPath?.Trim() ?? string.Empty;
        Transcription.FasterWhisperModel = string.IsNullOrWhiteSpace(Transcription.FasterWhisperModel) ? "tiny" : Transcription.FasterWhisperModel.Trim();
        Transcription.FasterWhisperDevice = string.IsNullOrWhiteSpace(Transcription.FasterWhisperDevice) ? "cpu" : Transcription.FasterWhisperDevice.Trim();
        Transcription.FasterWhisperComputeType = string.IsNullOrWhiteSpace(Transcription.FasterWhisperComputeType) ? "int8" : Transcription.FasterWhisperComputeType.Trim();
        if (Transcription.FasterWhisperWorkers <= 0) Transcription.FasterWhisperWorkers = 1;
        Transcription.FasterWhisperWorkers = Math.Clamp(Transcription.FasterWhisperWorkers, 1, 4);
        Transcription.OpenAiBaseUrl = string.IsNullOrWhiteSpace(Transcription.OpenAiBaseUrl) ? "http://localhost:1234/v1" : Transcription.OpenAiBaseUrl.Trim();
        Transcription.OpenAiApiKey ??= string.Empty;
        Transcription.OpenAiModel ??= string.Empty;
        if (Transcription.AnalogSampleRate <= 0) Transcription.AnalogSampleRate = 8000;
        if (Transcription.LivePressureQueueDepth <= 0) Transcription.LivePressureQueueDepth = 200;
        if (Transcription.LiveTranscriptionWorkers <= 0) Transcription.LiveTranscriptionWorkers = 1;
        Transcription.LiveTranscriptionWorkers = Math.Clamp(Transcription.LiveTranscriptionWorkers, 1, 4);
        Transcription.DeferredTalkgroups ??= new();
        if (Transcription.WhisperThreads <= 0)
        {
            Transcription.WhisperThreads = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm
                ? Math.Min(2, Math.Max(1, Environment.ProcessorCount / 2))
                : Math.Min(4, Math.Max(2, Environment.ProcessorCount / 4));
        }
        Transcription.WhisperThreads = Math.Clamp(Transcription.WhisperThreads, 1, 8);
        AiInsights.OpenAiBaseUrl ??= string.Empty;
        AiInsights.OpenAiApiKey ??= string.Empty;
        AiInsights.OpenAiModel ??= string.Empty;
        AiInsights.ExecutionMode = NormalizeExecutionMode(AiInsights.ExecutionMode, AiInsights.OpenAiBaseUrl);
        if (AiInsights.BatchSize <= 0) AiInsights.BatchSize = 50;
        if (AiInsights.MaxPendingCalls <= 0) AiInsights.MaxPendingCalls = 1000;
        if (AiInsights.TimeoutMs <= 0) AiInsights.TimeoutMs = 600000;
        if (AiInsights.MaxRetries < 0) AiInsights.MaxRetries = 0;
        if (AiInsights.MaxManualLookbackHours <= 0) AiInsights.MaxManualLookbackHours = 24;
        if (AiInsights.MaxManualSummaryCalls <= 0) AiInsights.MaxManualSummaryCalls = 300;
        if (AiInsights.MaxManualSummaryWindows <= 0) AiInsights.MaxManualSummaryWindows = 20;
        if (AiInsights.MaxQueueDepthForManualSummary < 0) AiInsights.MaxQueueDepthForManualSummary = 0;
        if (AiInsights.IncidentRunIntervalSeconds <= 0) AiInsights.IncidentRunIntervalSeconds = 300;
        AiInsights.IncidentRunIntervalSeconds = Math.Clamp(AiInsights.IncidentRunIntervalSeconds, 60, 1800);
        if (AiInsights.IncidentPromptCandidateLimit <= 0) AiInsights.IncidentPromptCandidateLimit = 18;
        AiInsights.IncidentPromptCandidateLimit = Math.Clamp(AiInsights.IncidentPromptCandidateLimit, 6, 40);
        if (AiInsights.IncidentV2ShadowCandidateLimit <= 0) AiInsights.IncidentV2ShadowCandidateLimit = 18;
        AiInsights.IncidentV2ShadowCandidateLimit = Math.Clamp(AiInsights.IncidentV2ShadowCandidateLimit, 6, 40);
        if (AiInsights.IncidentV3FrameCandidateLimit <= 0) AiInsights.IncidentV3FrameCandidateLimit = 18;
        AiInsights.IncidentV3FrameCandidateLimit = Math.Clamp(AiInsights.IncidentV3FrameCandidateLimit, 6, 40);
        // Incident V3 is retained only as a read-only comparison baseline. Its
        // semantic executor is retired and must not be enabled by deployed
        // configuration left over from an earlier experiment.
        AiInsights.IncidentV3PlanExecutorDryRun = true;
        if (AiInsights.IncidentNewVectorQueryLimit <= 0) AiInsights.IncidentNewVectorQueryLimit = 8;
        AiInsights.IncidentNewVectorQueryLimit = Math.Clamp(AiInsights.IncidentNewVectorQueryLimit, 0, 20);
        if (AiInsights.IncidentActiveVectorQueryLimit <= 0) AiInsights.IncidentActiveVectorQueryLimit = 6;
        AiInsights.IncidentActiveVectorQueryLimit = Math.Clamp(AiInsights.IncidentActiveVectorQueryLimit, 0, 15);
        if (AiInsights.EvidenceVerifierRagCandidateLimit <= 0) AiInsights.EvidenceVerifierRagCandidateLimit = 5;
        AiInsights.EvidenceVerifierRagCandidateLimit = Math.Clamp(AiInsights.EvidenceVerifierRagCandidateLimit, 0, 10);
        if (AiInsights.EvidenceVerifierMaxCalls <= 0) AiInsights.EvidenceVerifierMaxCalls = 8;
        AiInsights.EvidenceVerifierMaxCalls = Math.Clamp(AiInsights.EvidenceVerifierMaxCalls, 4, 12);
        Embeddings.OpenAiBaseUrl = string.IsNullOrWhiteSpace(Embeddings.OpenAiBaseUrl) ? "http://localhost:1234/v1" : Embeddings.OpenAiBaseUrl.Trim();
        Embeddings.OpenAiApiKey ??= string.Empty;
        Embeddings.OpenAiModel = string.IsNullOrWhiteSpace(Embeddings.OpenAiModel) ? "nomic-embed-text" : Embeddings.OpenAiModel.Trim();
        Embeddings.ExecutionMode = NormalizeExecutionMode(Embeddings.ExecutionMode, Embeddings.OpenAiBaseUrl);
        Embeddings.QdrantBaseUrl = string.IsNullOrWhiteSpace(Embeddings.QdrantBaseUrl) ? "http://localhost:6333" : Embeddings.QdrantBaseUrl.Trim().TrimEnd('/');
        Embeddings.QdrantApiKey ??= string.Empty;
        Embeddings.QdrantServiceName = string.IsNullOrWhiteSpace(Embeddings.QdrantServiceName) ? "qdrant" : Embeddings.QdrantServiceName.Trim();
        Embeddings.QdrantStoragePath = ExpandPath(string.IsNullOrWhiteSpace(Embeddings.QdrantStoragePath) ? "/var/lib/pizzawave/qdrant" : Embeddings.QdrantStoragePath);
        Embeddings.Collection = string.IsNullOrWhiteSpace(Embeddings.Collection) ? "pizzawave_calls" : Embeddings.Collection.Trim();
        if (Embeddings.VectorSize <= 0) Embeddings.VectorSize = 768;
        if (Embeddings.Workers <= 0) Embeddings.Workers = 1;
        Embeddings.Workers = Math.Clamp(Embeddings.Workers, 1, 2);
        if (Embeddings.MaxQueueDepthWhenTranscriptionBusy <= 0) Embeddings.MaxQueueDepthWhenTranscriptionBusy = 25;
        if (Embeddings.SearchLimit <= 0) Embeddings.SearchLimit = 40;
        if (Embeddings.SearchWindowMinutes <= 0) Embeddings.SearchWindowMinutes = 120;
        TrunkRecorder.ConfigPath = ExpandPath(TrunkRecorder.ConfigPath);
        TrunkRecorder.TalkgroupsPath = ExpandPath(TrunkRecorder.TalkgroupsPath);
        TrunkRecorder.LogServiceName = string.IsNullOrWhiteSpace(TrunkRecorder.LogServiceName)
            ? "trunk-recorder"
            : TrunkRecorder.LogServiceName.Trim();
        if (TrunkRecorder.HealthWindowMinutes <= 0) TrunkRecorder.HealthWindowMinutes = 5;
        RfSurvey.P25ProbeCommandTemplate = RfSurvey.P25ProbeCommandTemplate?.Trim() ?? string.Empty;
        RfSurvey.P25ProbeWorkingDirectory = ExpandPath(RfSurvey.P25ProbeWorkingDirectory);
        if (RfSurvey.P25ProbeDurationSeconds <= 0) RfSurvey.P25ProbeDurationSeconds = 45;
        RfSurvey.P25ProbeDurationSeconds = Math.Clamp(RfSurvey.P25ProbeDurationSeconds, 10, 300);
        if (RfSurvey.P25ProbeTimeoutSeconds <= 0) RfSurvey.P25ProbeTimeoutSeconds = Math.Max(30, RfSurvey.P25ProbeDurationSeconds + 15);
        SiteSetup.SiteLabel = SiteSetup.SiteLabel?.Trim() ?? string.Empty;
        SiteSetup.LocationNotes = SiteSetup.LocationNotes?.Trim() ?? string.Empty;
        SiteSetup.SourcePlanMode = string.IsNullOrWhiteSpace(SiteSetup.SourcePlanMode) ? "full" : SiteSetup.SourcePlanMode.Trim();
        SiteSetup.SystemShortNames ??= new();
        SiteSetup.SourcePlanSystemShortNames ??= new();
        SiteSetup.Systems ??= new();
        SiteSetup.SelectedSourceIndexes ??= new();
        SiteSetup.Sources ??= new();
        SiteSetup.RfPath ??= new();
        if (SiteSetup.DesiredVersion <= 0) SiteSetup.DesiredVersion = 1;
        Auth.Mode = string.IsNullOrWhiteSpace(Auth.Mode) ? "token" : Auth.Mode.Trim();
        Alerts.EmailProvider = string.IsNullOrWhiteSpace(Alerts.EmailProvider) ? "gmail" : Alerts.EmailProvider.Trim();
        Alerts.EmailUser ??= string.Empty;
        Alerts.EmailPassword ??= string.Empty;
        Alerts.AdministrativeEmailRecipients ??= string.Empty;
        if (Alerts.AdministrativeOutageDelayMinutes <= 0) Alerts.AdministrativeOutageDelayMinutes = 2;
        Alerts.AdministrativeOutageDelayMinutes = Math.Clamp(Alerts.AdministrativeOutageDelayMinutes, 1, 60);
        Alerts.Rules ??= new();
        foreach (var rule in Alerts.Rules)
        {
            rule.Name = string.IsNullOrWhiteSpace(rule.Name) ? "Alert" : rule.Name.Trim();
            rule.MatchType = AlertRulePolicy.NormalizeMatchType(rule.MatchType);
            rule.Talkgroups ??= new();
            if (rule.Talkgroups.Any(t => t.Id <= 0 || string.IsNullOrWhiteSpace(t.SystemShortName)))
                throw new InvalidOperationException($"Alert rule '{rule.Name}' has an unsupported unscoped talkgroup. Every talkgroup requires systemShortName and a positive id.");
            rule.Talkgroups = rule.Talkgroups
                .Select(t => new AlertTalkgroupRef
                {
                    SystemShortName = TalkgroupCatalogService.NormalizeSystemShortName(t.SystemShortName),
                    Id = t.Id
                })
                .GroupBy(AlertRulePolicy.TalkgroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();
        }
        Alerts.Playback ??= new();
        if (Alerts.Playback.CooldownSeconds <= 0) Alerts.Playback.CooldownSeconds = 15;
        if (Alerts.Playback.RepeatCount <= 0) Alerts.Playback.RepeatCount = 1;
        if (Alerts.Playback.RepeatCount > 10) Alerts.Playback.RepeatCount = 10;
        Profiles.Items ??= new();
        if (Profiles.Items.Count == 0)
            Profiles.Items.Add(new ProcessingProfile { Name = "Default" });
        if (Profiles.ActiveProfileId == Guid.Empty || Profiles.Items.All(p => p.Id != Profiles.ActiveProfileId))
            Profiles.ActiveProfileId = Profiles.Items[0].Id;
        foreach (var profile in Profiles.Items)
        {
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name.Trim();
            profile.Talkgroups ??= new();
            foreach (var talkgroup in profile.Talkgroups)
            {
                talkgroup.SystemShortName = TalkgroupCatalogService.SystemFromKeyOrValue(talkgroup.Key, talkgroup.SystemShortName, talkgroup.Id);
                talkgroup.Key = TalkgroupCatalogService.CatalogKey(talkgroup.SystemShortName, talkgroup.Id);
                talkgroup.Category = string.IsNullOrWhiteSpace(talkgroup.Category) ? string.Empty : talkgroup.Category.Trim().ToLowerInvariant();
                talkgroup.Label = talkgroup.Label?.Trim() ?? string.Empty;
            }
        }
        Locations.MonitoredAreas ??= new();
        foreach (var area in Locations.MonitoredAreas)
        {
            area.AreaId = string.IsNullOrWhiteSpace(area.AreaId) ? Slug(area.AreaLabel) : area.AreaId.Trim();
            area.AreaLabel = string.IsNullOrWhiteSpace(area.AreaLabel) ? area.AreaId : area.AreaLabel.Trim();
            area.SystemShortName = string.IsNullOrWhiteSpace(area.SystemShortName) ? area.AreaId : area.SystemShortName.Trim();
            area.Aliases ??= new();
            if (area.Aliases.Count == 0)
                area.Aliases.Add(area.SystemShortName);
        }
        Setup.CurrentStep = string.IsNullOrWhiteSpace(Setup.CurrentStep) ? "tr" : Setup.CurrentStep.Trim();
        if (Setup.CurrentStep == "stack")
            Setup.CurrentStep = "tr";
    }

    public static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return Environment.ExpandEnvironmentVariables(path)
            .Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private static string NormalizeExecutionMode(string? value, string? baseUrl)
    {
        var mode = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is "local" or "remote" or "lmlink")
            return mode;
        if (Uri.TryCreate(baseUrl ?? string.Empty, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(host) && host is not "localhost" and not "127.0.0.1" and not "::1")
                return "remote";
        }
        return "local";
    }

    private static string Slug(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "area" : value.Trim().ToLowerInvariant();
        var chars = value.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }
}

public sealed class BrandingConfig
{
    public string StackName { get; set; } = "PizzaWave";
}

public sealed class ServerConfig
{
    public string HttpBind { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; } = 8080;
}

public sealed class AuthConfig
{
    public string Mode { get; set; } = "token";
    public bool ReadRequiresAuth { get; set; }
    public bool WriteRequiresAuth { get; set; } = true;
    public string TokenFile { get; set; } = "/etc/pizzawave/pizzad.token";
}

public sealed class StorageConfig
{
    public string DatabasePath { get; set; } = "/var/lib/pizzawave/pizzad.db";
    public string AudioRoot { get; set; } = "/var/lib/pizzawave/audio";
    public string AppDataRoot { get; set; } = "/var/lib/pizzawave/appdata";
}

public sealed class IngestConfig
{
    public string CallstreamBind { get; set; } = "127.0.0.1";
    public int CallstreamPort { get; set; } = 9123;
    public int MaxConcurrentClients { get; set; } = 4;
}

public sealed class TranscriptionConfig
{
    public string Provider { get; set; } = "none";
    public string WhisperModelFile { get; set; } = string.Empty;
    public string VoskModelPath { get; set; } = string.Empty;
    public string FasterWhisperPythonPath { get; set; } = "/opt/pizzawave/venv/faster-whisper/bin/python";
    public string FasterWhisperScriptPath { get; set; } = string.Empty;
    public string FasterWhisperModel { get; set; } = "tiny";
    public string FasterWhisperDevice { get; set; } = "cpu";
    public string FasterWhisperComputeType { get; set; } = "int8";
    public int FasterWhisperWorkers { get; set; } = 1;
    public bool FasterWhisperVadFilter { get; set; }
    public string OpenAiBaseUrl { get; set; } = "http://localhost:1234/v1";
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = string.Empty;
    public int AnalogSampleRate { get; set; } = 8000;
    public int WhisperThreads { get; set; }
    public int LiveTranscriptionWorkers { get; set; } = 1;
    public int LivePressureQueueDepth { get; set; } = 200;
    public List<long> DeferredTalkgroups { get; set; } = new();
}

public sealed class AiInsightsConfig
{
    public bool Enabled { get; set; } = true;
    public string ExecutionMode { get; set; } = string.Empty;
    public string OpenAiBaseUrl { get; set; } = string.Empty;
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = string.Empty;
    public int BatchSize { get; set; } = 50;
    public int MaxPendingCalls { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 600000;
    public int MaxRetries { get; set; } = 2;
    public int MaxManualLookbackHours { get; set; } = 24;
    public int MaxManualSummaryCalls { get; set; } = 300;
    public int MaxManualSummaryWindows { get; set; } = 20;
    public int MaxQueueDepthForManualSummary { get; set; } = 100;
    public int IncidentRunIntervalSeconds { get; set; } = 300;
    public int IncidentPromptCandidateLimit { get; set; } = 18;
    public bool IncidentV2ShadowEnabled { get; set; }
    public int IncidentV2ShadowCandidateLimit { get; set; } = 18;
    public bool IncidentV3FrameShadowEnabled { get; set; }
    public int IncidentV3FrameCandidateLimit { get; set; } = 18;
    public bool IncidentV3PlanExecutorEnabled { get; set; }
    public bool IncidentV3PlanExecutorDryRun { get; set; } = true;
    public int IncidentNewVectorQueryLimit { get; set; } = 8;
    public int IncidentActiveVectorQueryLimit { get; set; } = 6;
    public int EvidenceVerifierRagCandidateLimit { get; set; } = 5;
    public int EvidenceVerifierMaxCalls { get; set; } = 8;
}

public sealed class EmbeddingsConfig
{
    public bool Enabled { get; set; }
    public string ExecutionMode { get; set; } = string.Empty;
    public string OpenAiBaseUrl { get; set; } = "http://localhost:1234/v1";
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = "nomic-embed-text";
    public string QdrantBaseUrl { get; set; } = "http://localhost:6333";
    public string QdrantApiKey { get; set; } = string.Empty;
    public string QdrantServiceName { get; set; } = "qdrant";
    public string QdrantStoragePath { get; set; } = "/var/lib/pizzawave/qdrant";
    public string Collection { get; set; } = "pizzawave_calls";
    public int VectorSize { get; set; } = 768;
    public int Workers { get; set; } = 1;
    public int MaxQueueDepthWhenTranscriptionBusy { get; set; } = 25;
    public int SearchLimit { get; set; } = 40;
    public int SearchWindowMinutes { get; set; } = 120;
}

public sealed class TrunkRecorderConfig
{
    public string ConfigPath { get; set; } = "/etc/trunk-recorder/config.json";
    public string TalkgroupCatalogPath { get; set; } = "/var/lib/pizzawave/appdata/talkgroups.json";
    public string TalkgroupsPath { get; set; } = "/etc/trunk-recorder/talkgroups.csv";
    public string LogServiceName { get; set; } = "trunk-recorder";
    public int HealthWindowMinutes { get; set; } = 5;
}

public sealed class SiteSetupConfig
{
    public long DesiredVersion { get; set; } = 1;
    public string SiteLabel { get; set; } = string.Empty;
    public string LocationNotes { get; set; } = string.Empty;
    public List<MonitoredAreaConfig> MonitoredAreas { get; set; } = new();
    public List<string> SystemShortNames { get; set; } = new();
    public List<string> SourcePlanSystemShortNames { get; set; } = new();
    public string SourcePlanMode { get; set; } = "full";
    public List<RfSurveySystemDto> Systems { get; set; } = new();
    public List<int> SelectedSourceIndexes { get; set; } = new();
    public Dictionary<string, int> SourceAssignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RfSurveySourceDto> Sources { get; set; } = new();
    public List<SiteSetupRfSelection> RfSelections { get; set; } = new();
    public RfSurveyPathProfileDto RfPath { get; set; } = new();
    public DateTime? UpdatedAtUtc { get; set; }
    public DateTime? LastAppliedAtUtc { get; set; }
    public string LastAppliedConfigHash { get; set; } = string.Empty;
    public string LastAppliedSourceAssignmentSummary { get; set; } = string.Empty;
    public string LastAppliedRfPathSummary { get; set; } = string.Empty;
    public string LastAppliedTalkgroupPolicyHash { get; set; } = string.Empty;
    public string LastAppliedTalkgroupPolicyJson { get; set; } = string.Empty;
    public string LastAppliedDesiredJson { get; set; } = string.Empty;
}

public sealed class SiteSetupRfSelection
{
    public long FrequencyHz { get; set; }
    public int? SourceIndex { get; set; }
    public string SourceSerial { get; set; } = string.Empty;
    public string Gain { get; set; } = string.Empty;
    public int? SampleRateHz { get; set; }
    public int? MeasuredSignalOffsetHz { get; set; }
    public int? ErrorHz { get; set; }
    public double? SnrDb { get; set; }
    public double? Confidence { get; set; }
}

public sealed class RfSurveyConfig
{
    public string P25ProbeCommandTemplate { get; set; } = string.Empty;
    public string P25ProbeWorkingDirectory { get; set; } = "/tmp";
    public int P25ProbeDurationSeconds { get; set; } = 45;
    public int P25ProbeTimeoutSeconds { get; set; } = 90;
}

public sealed class AlertConfig
{
    public bool EmailEnabled { get; set; }
    public string EmailProvider { get; set; } = "gmail";
    public string EmailUser { get; set; } = string.Empty;
    public string EmailPassword { get; set; } = string.Empty;
    public bool AdministrativeEmailEnabled { get; set; }
    public string AdministrativeEmailRecipients { get; set; } = string.Empty;
    public int AdministrativeOutageDelayMinutes { get; set; } = 2;
    public List<EngineAlertRule> Rules { get; set; } = new();
    public AlertPlaybackConfig Playback { get; set; } = new();
}

public sealed class AlertPlaybackConfig
{
    public bool Enabled { get; set; }
    public bool AlertMatches { get; set; } = true;
    public bool NewIncidents { get; set; }
    public bool TrafficIncidents { get; set; }
    public bool PoliceCalls { get; set; }
    public int CooldownSeconds { get; set; } = 15;
    public int RepeatCount { get; set; } = 1;
}

public sealed class EngineAlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string MatchType { get; set; } = "keyword";
    public string Keywords { get; set; } = string.Empty;
    public string PoliceCodes { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Frequency { get; set; } = "realtime";
    public bool Autoplay { get; set; } = true;
    public List<AlertTalkgroupRef> Talkgroups { get; set; } = new();
}

public sealed class AlertTalkgroupRef
{
    public string SystemShortName { get; set; } = string.Empty;
    public long Id { get; set; }
}

public sealed class ProfileConfig
{
    public Guid ActiveProfileId { get; set; }
    public List<ProcessingProfile> Items { get; set; } = new();
}

public sealed class LocationConfig
{
    public List<MonitoredAreaConfig> MonitoredAreas { get; set; } = new();
}

public sealed class SetupConfig
{
    public bool Completed { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int WizardVersion { get; set; } = 1;
    public string CurrentStep { get; set; } = "tr";
    public string InstallMode { get; set; } = "reuseExistingTr";
    public string TrConfigMode { get; set; } = "radioReference";
    public string RadioReferenceSid { get; set; } = string.Empty;
    public DateTime? RestoreAppliedAtUtc { get; set; }
    public bool TrDetected { get; set; }
    public bool TrConfigured { get; set; }
    public bool InstallOptionalDiagnosticTools { get; set; }
    public string PendingRestorePath { get; set; } = string.Empty;
    public string PendingRestoreManifestJson { get; set; } = string.Empty;
}

public sealed class MonitoredAreaConfig
{
    public string AreaId { get; set; } = string.Empty;
    public string AreaLabel { get; set; } = string.Empty;
    public string SystemShortName { get; set; } = string.Empty;
    public double North { get; set; }
    public double South { get; set; }
    public double West { get; set; }
    public double East { get; set; }
    public List<string> Aliases { get; set; } = new();
    public bool IsOverride { get; set; }
    public string ContextKey { get; set; } = string.Empty;
}

public sealed class ProcessingProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default";
    public bool IncludePolice { get; set; } = true;
    public bool IncludeFire { get; set; } = true;
    public bool IncludeEMS { get; set; } = true;
    public bool IncludeTraffic { get; set; } = true;
    public bool IncludeUtilities { get; set; } = true;
    public bool IncludeOther { get; set; } = true;
    public List<ProfileTalkgroupSetting> Talkgroups { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class ProfileTalkgroupSetting
{
    public string Key { get; set; } = string.Empty;
    public string SystemShortName { get; set; } = string.Empty;
    public long Id { get; set; }
    public bool? Enabled { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool? IncidentEligible { get; set; }
}
