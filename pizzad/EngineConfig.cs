using System.Text.Json;
using System.Text.Json.Serialization;

namespace pizzad;

public sealed class EngineConfig
{
    public ServerConfig Server { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public StorageConfig Storage { get; set; } = new();
    public IngestConfig Ingest { get; set; } = new();
    public TranscriptionConfig Transcription { get; set; } = new();
    public AiInsightsConfig AiInsights { get; set; } = new();
    public SftpImportConfig SftpImport { get; set; } = new();
    public TrunkRecorderConfig TrunkRecorder { get; set; } = new();
    public AlertConfig Alerts { get; set; } = new();

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
            config = JsonSerializer.Deserialize<EngineConfig>(json, JsonOptions()) ?? new EngineConfig();
            config.ConfigPath = path;
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
        Transcription.OpenAiBaseUrl = string.IsNullOrWhiteSpace(Transcription.OpenAiBaseUrl) ? "http://localhost:1234/v1" : Transcription.OpenAiBaseUrl.Trim();
        Transcription.OpenAiApiKey ??= string.Empty;
        Transcription.OpenAiModel ??= string.Empty;
        if (Transcription.AnalogSampleRate <= 0) Transcription.AnalogSampleRate = 8000;
        AiInsights.OpenAiBaseUrl ??= string.Empty;
        AiInsights.OpenAiApiKey ??= string.Empty;
        AiInsights.OpenAiModel ??= string.Empty;
        if (AiInsights.BatchSize <= 0) AiInsights.BatchSize = 50;
        if (AiInsights.MaxPendingCalls <= 0) AiInsights.MaxPendingCalls = 1000;
        if (AiInsights.TimeoutMs <= 0) AiInsights.TimeoutMs = 600000;
        if (AiInsights.MaxRetries < 0) AiInsights.MaxRetries = 0;
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
    public string OpenAiBaseUrl { get; set; } = "http://localhost:1234/v1";
    public string OpenAiApiKey { get; set; } = string.Empty;
    public string OpenAiModel { get; set; } = string.Empty;
    public int AnalogSampleRate { get; set; } = 8000;
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
