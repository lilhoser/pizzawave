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
    public SftpImportConfig SftpImport { get; set; } = new();
    public TrunkRecorderConfig TrunkRecorder { get; set; } = new();
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
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions()));
    }

    private void ApplyDefaults()
    {
        Branding.StackName = string.IsNullOrWhiteSpace(Branding.StackName) ? "PizzaWave" : Branding.StackName.Trim();
        Server.HttpBind = string.IsNullOrWhiteSpace(Server.HttpBind) ? "0.0.0.0" : Server.HttpBind.Trim();
        if (Server.HttpPort <= 0) Server.HttpPort = 8080;
        Storage.DatabasePath = ExpandPath(Storage.DatabasePath);
        Storage.AudioRoot = ExpandPath(Storage.AudioRoot);
        Storage.ImportCacheRoot = ExpandPath(Storage.ImportCacheRoot);
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
        if (AiInsights.BatchSize <= 0) AiInsights.BatchSize = 50;
        if (AiInsights.MaxPendingCalls <= 0) AiInsights.MaxPendingCalls = 1000;
        if (AiInsights.TimeoutMs <= 0) AiInsights.TimeoutMs = 600000;
        if (AiInsights.MaxRetries < 0) AiInsights.MaxRetries = 0;
        if (AiInsights.MaxManualLookbackHours <= 0) AiInsights.MaxManualLookbackHours = 24;
        if (AiInsights.MaxManualSummaryCalls <= 0) AiInsights.MaxManualSummaryCalls = 300;
        if (AiInsights.MaxManualSummaryWindows <= 0) AiInsights.MaxManualSummaryWindows = 20;
        if (AiInsights.MaxQueueDepthForManualSummary < 0) AiInsights.MaxQueueDepthForManualSummary = 0;
        SftpImport.Host ??= string.Empty;
        if (SftpImport.Port <= 0) SftpImport.Port = 22;
        SftpImport.Username ??= string.Empty;
        SftpImport.AuthMode = string.IsNullOrWhiteSpace(SftpImport.AuthMode) ? "privateKey" : SftpImport.AuthMode.Trim();
        SftpImport.Password ??= string.Empty;
        SftpImport.PrivateKeyPath ??= string.Empty;
        SftpImport.PrivateKeyPassphrase ??= string.Empty;
        SftpImport.RemoteRoot ??= string.Empty;
        if (SftpImport.QuickImportMaxHours <= 0) SftpImport.QuickImportMaxHours = 48;
        if (SftpImport.DefaultBatchCallCap <= 0) SftpImport.DefaultBatchCallCap = 5000;
        if (SftpImport.DefaultBatchByteCap <= 0) SftpImport.DefaultBatchByteCap = 20L * 1024 * 1024 * 1024;
        TrunkRecorder.ConfigPath = ExpandPath(TrunkRecorder.ConfigPath);
        TrunkRecorder.TalkgroupsPath = ExpandPath(TrunkRecorder.TalkgroupsPath);
        TrunkRecorder.LogServiceName = string.IsNullOrWhiteSpace(TrunkRecorder.LogServiceName)
            ? "trunk-recorder"
            : TrunkRecorder.LogServiceName.Trim();
        if (TrunkRecorder.HealthWindowMinutes <= 0) TrunkRecorder.HealthWindowMinutes = 5;
        Auth.Mode = string.IsNullOrWhiteSpace(Auth.Mode) ? "token" : Auth.Mode.Trim();
        Alerts.EmailProvider = string.IsNullOrWhiteSpace(Alerts.EmailProvider) ? "gmail" : Alerts.EmailProvider.Trim();
        Alerts.EmailUser ??= string.Empty;
        Alerts.EmailPassword ??= string.Empty;
        Alerts.Rules ??= new();
        Profiles.Items ??= new();
        if (Profiles.Items.Count == 0)
            Profiles.Items.Add(new ProcessingProfile { Name = "Default" });
        if (Profiles.ActiveProfileId == Guid.Empty || Profiles.Items.All(p => p.Id != Profiles.ActiveProfileId))
            Profiles.ActiveProfileId = Profiles.Items[0].Id;
        foreach (var profile in Profiles.Items)
        {
            profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name.Trim();
            profile.AllowedTalkgroups ??= new();
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
        Setup.CurrentStep = string.IsNullOrWhiteSpace(Setup.CurrentStep) ? "stack" : Setup.CurrentStep.Trim();
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
    public bool WriteRequiresAuth { get; set; }
    public string TokenFile { get; set; } = "/etc/pizzawave/pizzad.token";
}

public sealed class StorageConfig
{
    public string DatabasePath { get; set; } = "/var/lib/pizzawave/pizzad.db";
    public string AudioRoot { get; set; } = "/var/lib/pizzawave/audio";
    public string ImportCacheRoot { get; set; } = "/var/lib/pizzawave/import-cache";
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
}

public sealed class SftpImportConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string AuthMode { get; set; } = "privateKey";
    public string Password { get; set; } = string.Empty;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string PrivateKeyPassphrase { get; set; } = string.Empty;
    public string RemoteRoot { get; set; } = string.Empty;
    public int QuickImportMaxHours { get; set; } = 48;
    public int DefaultBatchCallCap { get; set; } = 5000;
    public long DefaultBatchByteCap { get; set; } = 20L * 1024 * 1024 * 1024;
}

public sealed class TrunkRecorderConfig
{
    public string ConfigPath { get; set; } = "/etc/trunk-recorder/config.json";
    public string TalkgroupCatalogPath { get; set; } = "/var/lib/pizzawave/appdata/talkgroups.json";
    public string TalkgroupsPath { get; set; } = "/etc/trunk-recorder/talkgroups.csv";
    public string LogServiceName { get; set; } = "trunk-recorder";
    public int HealthWindowMinutes { get; set; } = 5;
}

public sealed class AlertConfig
{
    public bool EmailEnabled { get; set; }
    public string EmailProvider { get; set; } = "gmail";
    public string EmailUser { get; set; } = string.Empty;
    public string EmailPassword { get; set; } = string.Empty;
    public List<EngineAlertRule> Rules { get; set; } = new();
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
    public List<long> Talkgroups { get; set; } = new();
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
    public string CurrentStep { get; set; } = "stack";
    public string InstallMode { get; set; } = "reuseExistingTr";
    public string TrConfigMode { get; set; } = "radioReference";
    public bool TrDetected { get; set; }
    public bool TrConfigured { get; set; }
    public bool TalkgroupsValidated { get; set; }
    public bool CallstreamValidated { get; set; }
    public bool TranscriptionValidated { get; set; }
    public bool MonitoredAreasValidated { get; set; }
    public bool HealthValidated { get; set; }
    public bool AiInsightsSkippedOrValidated { get; set; } = true;
    public bool AlertsSkippedOrValidated { get; set; } = true;
    public bool SftpSkippedOrValidated { get; set; } = true;
    public bool CalibrationSkippedOrCompleted { get; set; } = true;
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
}
