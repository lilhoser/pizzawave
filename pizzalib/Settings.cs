/*
Licensed to the Apache Software Foundation (ASF) under one
or more contributor license agreements.  See the NOTICE file
distributed with this work for additional information
regarding copyright ownership.  The ASF licenses this file
to you under the Apache License, Version 2.0 (the
"License"); you may not use this file except in compliance
with the License.  You may obtain a copy of the License at

  http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing,
software distributed under the License is distributed on an
"AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
KIND, either express or implied.  See the License for the
specific language governing permissions and limitations
under the License.
*/
using System.Diagnostics;
using System.Reflection;
using Newtonsoft.Json;
using System.Runtime.CompilerServices;
using System.Text;

namespace pizzalib
{
    using static TraceLogger;

    public class Settings : IEquatable<Settings>
    {
        public static readonly string DefaultWorkingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "pizzawave");
        public static string DefaultSettingsFileName = "settings.json";
        public static string DefaultLiveCaptureDirectory = Path.Combine(DefaultWorkingDirectory, "captures");
        public static string DefaultOfflineCaptureDirectory = Path.Combine(DefaultWorkingDirectory, "offline");
        public static string DefaultSettingsFileLocation = Path.Combine(DefaultWorkingDirectory, DefaultSettingsFileName);
        public static string DefaultAlertWavLocation = Path.Combine(DefaultWorkingDirectory, "alerts");

        // pizzalib library settings
        public SourceLevels TraceLevelApp;
        public List<Alert> Alerts = [];
        public bool AutostartListener;
        [JsonIgnore]
        public string? emailUser;
        [JsonIgnore]
        public string? emailPassword;

        // Public properties for UI access
        [JsonProperty("listenPort")]
        public int ListenPort
        {
            get => listenPort;
            set => listenPort = value;
        }
        [JsonIgnore]
        public string? WhisperModelFile
        {
            get => whisperModelFile;
            set => whisperModelFile = value;
        }
        [JsonProperty("transcriptionEngine")]
        public string TranscriptionEngine
        {
            get => transcriptionEngine;
            set => transcriptionEngine = value;
        }
        [JsonProperty("whisperThreads")]
        public int WhisperThreads
        {
            get => whisperThreads;
            set => whisperThreads = value;
        }
        [JsonProperty("transcriptionModelPreset")]
        public string TranscriptionModelPreset
        {
            get => transcriptionModelPreset;
            set => transcriptionModelPreset = value;
        }
        [JsonIgnore]
        public string? VoskModelPath
        {
            get => voskModelPath;
            set => voskModelPath = value;
        }
        [JsonProperty("emailUser")]
        public string? EmailUser
        {
            get => emailUser;
            set => emailUser = value;
        }
        [JsonProperty("emailPassword")]
        public string? EmailPassword
        {
            get => emailPassword;
            set => emailPassword = value;
        }
        
        // LM Link settings (Insights)
        [JsonProperty("lmLinkEnabled")]
        public bool LmLinkEnabled { get; set; }
        [JsonProperty("lmLinkBaseUrl")]
        public string? LmLinkBaseUrl { get; set; }
        [JsonProperty("lmLinkApiKey")]
        public string? LmLinkApiKey { get; set; }
        [JsonProperty("lmLinkModel")]
        public string? LmLinkModel { get; set; }
        [JsonProperty("lmLinkTimeoutMs")]
        public int LmLinkTimeoutMs { get; set; } = 600000;
        [JsonProperty("lmLinkMaxRetries")]
        public int LmLinkMaxRetries { get; set; } = 2;
        [JsonProperty("dailyInsightsDigestEnabled")]
        public bool DailyInsightsDigestEnabled { get; set; }
        [JsonProperty("emailProvider")]
        public string EmailProvider { get; set; } = "gmail";

        // Remote archive settings (SFTP)
        [JsonProperty("archiveSftpEnabled")]
        public bool ArchiveSftpEnabled { get; set; }
        [JsonProperty("archiveSftpHost")]
        public string? ArchiveSftpHost { get; set; }
        [JsonProperty("archiveSftpPort")]
        public int ArchiveSftpPort { get; set; } = 22;
        [JsonProperty("archiveSftpUsername")]
        public string? ArchiveSftpUsername { get; set; }
        [JsonProperty("archiveSftpAuthMode")]
        public string ArchiveSftpAuthMode { get; set; } = "password";
        [JsonIgnore]
        public string? ArchiveSftpPassword { get; set; }
        [JsonProperty("archiveSftpPrivateKeyPath")]
        public string? ArchiveSftpPrivateKeyPath { get; set; }
        [JsonIgnore]
        public string? ArchiveSftpPrivateKeyPassphrase { get; set; }
        [JsonProperty("archiveSftpRemoteRoot")]
        public string? ArchiveSftpRemoteRoot { get; set; }
        [JsonProperty("archiveLocalCachePath")]
        public string? ArchiveLocalCachePath { get; set; }

        // Trunk Recorder diagnostics settings
        [JsonProperty("trDiagnosticsMode")]
        public string TrDiagnosticsMode { get; set; } = "local";
        [JsonProperty("trDiagnosticsHost")]
        public string? TrDiagnosticsHost { get; set; }
        [JsonProperty("trDiagnosticsPort")]
        public int TrDiagnosticsPort { get; set; } = 22;
        [JsonProperty("trDiagnosticsUsername")]
        public string? TrDiagnosticsUsername { get; set; }
        [JsonProperty("trDiagnosticsAuthMode")]
        public string TrDiagnosticsAuthMode { get; set; } = "password";
        [JsonIgnore]
        public string? TrDiagnosticsPassword { get; set; }
        [JsonProperty("trDiagnosticsPrivateKeyPath")]
        public string? TrDiagnosticsPrivateKeyPath { get; set; }
        [JsonIgnore]
        public string? TrDiagnosticsPrivateKeyPassphrase { get; set; }
        [JsonProperty("trDiagnosticsRemoteLogDir")]
        public string? TrDiagnosticsRemoteLogDir { get; set; }
        [JsonProperty("trDiagnosticsLocalCachePath")]
        public string? TrDiagnosticsLocalCachePath { get; set; }

        // Alert audio settings
        public bool AutoplayAlerts;
        public int SnoozeDurationMinutes;

        // UI display settings
        public int SortMode;  // 0=newest first, 1=oldest first, 2=talkgroup
        public int GroupMode; // 0=none, 1=talkgroup, 2=time of day, 3=source
        public double FontSize; // Default 14.0
        public bool AutoCleanupCalls; // Auto-cleanup old calls to prevent memory leaks
        public int MaxCallsToKeep; // Number of calls to keep before auto-cleanup

        // TrunkRecorder settings
        [JsonIgnore]
        public int listenPort;
        public int analogChannels;
        public int analogBitDepth;
        public int analogSamplingRate;

        // whisper.net settings
        [JsonIgnore]
        public string? whisperModelFile;
        [JsonIgnore]
        public string transcriptionEngine = "whisper";
        [JsonIgnore]
        public int whisperThreads;
        [JsonIgnore]
        public string transcriptionModelPreset = string.Empty;
        [JsonIgnore]
        public string? voskModelPath;

        // Config versioning
        public int ConfigVersion { get; set; } = 1;

        // Non-serializable fields
        [JsonIgnore]
        public Action<string>? UpdateProgressLabelCallback;
        [JsonIgnore]
        public Action<string>? UpdateConnectionLabelCallback;
        [JsonIgnore]
        public Action<int, int>? SetProgressBarCallback;
        [JsonIgnore]
        public Action? ProgressBarStepCallback;
        [JsonIgnore]
        public Action? HideProgressBarCallback;

        // For JSON serialization - store encrypted credentials
        [JsonIgnore]
        public string? EncryptedEmailUser
        {
            get => ObfuscateString(emailUser);
            set => emailUser = DeobfuscateString(value);
        }
        [JsonIgnore]
        public string? EncryptedEmailPassword
        {
            get => ObfuscateString(emailPassword);
            set => emailPassword = DeobfuscateString(value);
        }

        public Settings()
        {
            if (!Directory.Exists(DefaultWorkingDirectory))
            {
                try { Directory.CreateDirectory(DefaultWorkingDirectory); }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.Settings, TraceEventType.Warning,
                          $"Unable to create settings directory '{DefaultWorkingDirectory}': {ex.Message}");
                }
            }

            TraceLevelApp = SourceLevels.Error;
            AutostartListener = true;
            AutoplayAlerts = false;
            SnoozeDurationMinutes = 15;
            SortMode = 0;
            GroupMode = 0;
            FontSize = 14.0;
            AutoCleanupCalls = true;
            MaxCallsToKeep = 100;
            listenPort = 9123;
            analogSamplingRate = 8000;
            analogBitDepth = 16;
            analogChannels = 1;
            LmLinkEnabled = false;
            LmLinkBaseUrl = string.Empty;
            LmLinkApiKey = string.Empty;
            LmLinkModel = string.Empty;
            LmLinkTimeoutMs = 600000;
            LmLinkMaxRetries = 2;
            DailyInsightsDigestEnabled = false;
            EmailProvider = "gmail";
            ArchiveSftpEnabled = false;
            ArchiveSftpHost = string.Empty;
            ArchiveSftpPort = 22;
            ArchiveSftpUsername = string.Empty;
            ArchiveSftpAuthMode = "password";
            ArchiveSftpPassword = string.Empty;
            ArchiveSftpPrivateKeyPath = string.Empty;
            ArchiveSftpPrivateKeyPassphrase = string.Empty;
            ArchiveSftpRemoteRoot = string.Empty;
            ArchiveLocalCachePath = Path.Combine(DefaultOfflineCaptureDirectory, "sftp-cache");
            TrDiagnosticsMode = "local";
            TrDiagnosticsHost = string.Empty;
            TrDiagnosticsPort = 22;
            TrDiagnosticsUsername = string.Empty;
            TrDiagnosticsAuthMode = "password";
            TrDiagnosticsPassword = string.Empty;
            TrDiagnosticsPrivateKeyPath = string.Empty;
            TrDiagnosticsPrivateKeyPassphrase = string.Empty;
            TrDiagnosticsRemoteLogDir = "/var/log/trunk-recorder";
            TrDiagnosticsLocalCachePath = Path.Combine(DefaultWorkingDirectory, "tr-diagnostics");
        }

        public bool Equals(Settings? other)
        {
            if (other == null) return false;
            return TraceLevelApp == other.TraceLevelApp &&
                Alerts.SequenceEqual(other.Alerts) &&
                AutostartListener == other.AutostartListener &&
                emailUser == other.emailUser &&
                emailPassword == other.emailPassword &&
                AutoplayAlerts == other.AutoplayAlerts &&
                SnoozeDurationMinutes == other.SnoozeDurationMinutes &&
                SortMode == other.SortMode &&
                GroupMode == other.GroupMode &&
                FontSize == other.FontSize &&
                AutoCleanupCalls == other.AutoCleanupCalls &&
                MaxCallsToKeep == other.MaxCallsToKeep &&
                listenPort == other.listenPort &&
                analogChannels == other.analogChannels &&
                analogBitDepth == other.analogBitDepth &&
                analogSamplingRate == other.analogSamplingRate &&
                whisperModelFile == other.whisperModelFile &&
                transcriptionEngine == other.transcriptionEngine &&
                transcriptionModelPreset == other.transcriptionModelPreset &&
                voskModelPath == other.voskModelPath &&
                LmLinkEnabled == other.LmLinkEnabled &&
                LmLinkBaseUrl == other.LmLinkBaseUrl &&
                LmLinkApiKey == other.LmLinkApiKey &&
                LmLinkModel == other.LmLinkModel &&
                LmLinkTimeoutMs == other.LmLinkTimeoutMs &&
                LmLinkMaxRetries == other.LmLinkMaxRetries &&
                DailyInsightsDigestEnabled == other.DailyInsightsDigestEnabled &&
                EmailProvider == other.EmailProvider &&
                ArchiveSftpEnabled == other.ArchiveSftpEnabled &&
                ArchiveSftpHost == other.ArchiveSftpHost &&
                ArchiveSftpPort == other.ArchiveSftpPort &&
                ArchiveSftpUsername == other.ArchiveSftpUsername &&
                ArchiveSftpAuthMode == other.ArchiveSftpAuthMode &&
                ArchiveSftpPrivateKeyPath == other.ArchiveSftpPrivateKeyPath &&
                ArchiveSftpRemoteRoot == other.ArchiveSftpRemoteRoot &&
                ArchiveLocalCachePath == other.ArchiveLocalCachePath &&
                TrDiagnosticsMode == other.TrDiagnosticsMode &&
                TrDiagnosticsHost == other.TrDiagnosticsHost &&
                TrDiagnosticsPort == other.TrDiagnosticsPort &&
                TrDiagnosticsUsername == other.TrDiagnosticsUsername &&
                TrDiagnosticsAuthMode == other.TrDiagnosticsAuthMode &&
                TrDiagnosticsPrivateKeyPath == other.TrDiagnosticsPrivateKeyPath &&
                TrDiagnosticsRemoteLogDir == other.TrDiagnosticsRemoteLogDir &&
                TrDiagnosticsLocalCachePath == other.TrDiagnosticsLocalCachePath;
        }

        public static bool HasFieldChanged(Settings Object1, Settings Object2, string Name)
        {
            var field = typeof(Settings).GetField(Name, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return false;

            var value1 = field.GetValue(Object1);
            var value2 = field.GetValue(Object2);
            return !Equals(value1, value2);
        }

        //
        // Credential obfuscation (cross-platform, not secure storage but better than plain text)
        //
        private static readonly byte[] s_Key = Encoding.UTF8.GetBytes("pizzawave-credential-key-2024");

        private static string? ObfuscateString(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            try
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                var result = new byte[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    result[i] = (byte)(bytes[i] ^ s_Key[i % s_Key.Length]);
                }
                return Convert.ToBase64String(result);
            }
            catch
            {
                return value;
            }
        }

        private static string? DeobfuscateString(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            try
            {
                var bytes = Convert.FromBase64String(value);
                var result = new byte[bytes.Length];
                for (int i = 0; i < bytes.Length; i++)
                {
                    result[i] = (byte)(bytes[i] ^ s_Key[i % s_Key.Length]);
                }
                return Encoding.UTF8.GetString(result);
            }
            catch
            {
                // If deobfuscation fails, return as-is (may be plain text)
                return value;
            }
        }

        //
        // Input validation
        //
        private static bool IsValidEmail(string? email)
        {
            if (string.IsNullOrEmpty(email))
                return false;

            // Basic email pattern validation
            var pattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
            return System.Text.RegularExpressions.Regex.IsMatch(email, pattern);
        }

        public virtual void Validate()
        {
            var engine = (transcriptionEngine ?? "whisper").Trim().ToLowerInvariant();
            if (engine != "whisper" && engine != "vosk")
            {
                throw new Exception("TranscriptionEngine must be 'whisper' or 'vosk'");
            }

            if (engine == "whisper" &&
                !string.IsNullOrEmpty(whisperModelFile) &&
                !IsWhisperModelAlias(whisperModelFile) &&
                !File.Exists(whisperModelFile))
            {
                throw new Exception($"Invalid whisper model file: {whisperModelFile}");
            }
            if (engine == "vosk" &&
                !string.IsNullOrEmpty(voskModelPath) &&
                !Directory.Exists(voskModelPath))
            {
                throw new Exception($"Invalid vosk model directory: {voskModelPath}");
            }

            if (!string.IsNullOrEmpty(emailUser))
            {
                if (!IsValidEmail(emailUser))
                {
                    throw new Exception($"Invalid email address: {emailUser}");
                }
                if (string.IsNullOrEmpty(emailPassword))
                {
                    throw new Exception("Email app password is required");
                }
            }
            else if (!string.IsNullOrEmpty(emailPassword))
            {
                throw new Exception("Email user is required when email authentication is configured");
            }

            if (LmLinkEnabled)
            {
                if (string.IsNullOrWhiteSpace(LmLinkBaseUrl))
                    throw new Exception("LM Link base URL is required when enabled");
                if (string.IsNullOrWhiteSpace(LmLinkModel))
                    throw new Exception("LM Link model is required when enabled");
                if (LmLinkTimeoutMs <= 0)
                    throw new Exception("LM Link timeout must be > 0");
                if (LmLinkMaxRetries < 0)
                    throw new Exception("LM Link max retries must be >= 0");
            }

            if (DailyInsightsDigestEnabled)
            {
                if (!LmLinkEnabled)
                    throw new Exception("Daily insights digest requires LM Link to be enabled");
                var hasAppPassword = !string.IsNullOrWhiteSpace(emailUser) && !string.IsNullOrWhiteSpace(emailPassword);
                if (!hasAppPassword)
                    throw new Exception("Daily insights digest requires email user and app password");
            }

            if (ArchiveSftpEnabled)
            {
                if (string.IsNullOrWhiteSpace(ArchiveSftpHost))
                    throw new Exception("Archive SFTP host is required when archive SFTP is enabled");
                if (ArchiveSftpPort <= 0 || ArchiveSftpPort > 65535)
                    throw new Exception("Archive SFTP port must be between 1 and 65535");
                if (string.IsNullOrWhiteSpace(ArchiveSftpUsername))
                    throw new Exception("Archive SFTP username is required when archive SFTP is enabled");
                if (string.IsNullOrWhiteSpace(ArchiveSftpRemoteRoot))
                    throw new Exception("Archive SFTP remote root is required when archive SFTP is enabled");

                var authMode = (ArchiveSftpAuthMode ?? "password").Trim().ToLowerInvariant();
                if (authMode != "password" && authMode != "privatekey")
                    throw new Exception("Archive SFTP auth mode must be 'password' or 'privatekey'");
                if (authMode == "privatekey" && string.IsNullOrWhiteSpace(ArchiveSftpPrivateKeyPath))
                    throw new Exception("Archive SFTP private key path is required when private key auth is selected");
            }

            var trMode = (TrDiagnosticsMode ?? "local").Trim().ToLowerInvariant();
            if (trMode != "local" && trMode != "ssh")
                throw new Exception("TR diagnostics mode must be 'local' or 'ssh'");
            if (TrDiagnosticsPort <= 0 || TrDiagnosticsPort > 65535)
                throw new Exception("TR diagnostics port must be between 1 and 65535");
            if (trMode == "ssh")
            {
                if (string.IsNullOrWhiteSpace(TrDiagnosticsHost))
                    throw new Exception("TR diagnostics host is required when SSH mode is enabled");
                if (string.IsNullOrWhiteSpace(TrDiagnosticsUsername))
                    throw new Exception("TR diagnostics username is required when SSH mode is enabled");
                if (string.IsNullOrWhiteSpace(TrDiagnosticsRemoteLogDir))
                    throw new Exception("TR diagnostics remote log directory is required when SSH mode is enabled");

                var authMode = (TrDiagnosticsAuthMode ?? "password").Trim().ToLowerInvariant();
                if (authMode != "password" && authMode != "privatekey")
                    throw new Exception("TR diagnostics auth mode must be 'password' or 'privatekey'");
                if (authMode == "privatekey" && string.IsNullOrWhiteSpace(TrDiagnosticsPrivateKeyPath))
                    throw new Exception("TR diagnostics private key path is required when private key auth is selected");
            }
        }

        public void SaveToFile(string? target = null)
        {
            if (string.IsNullOrEmpty(target))
                target = DefaultSettingsFileLocation;

            try
            {
                Validate();
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(target, json);
            }
            catch (Exception ex)
            {
                throw new Exception($"Could not save the Settings object " +
                    $"to JSON: {ex.Message}");
            }
        }

        public void Migrate()
        {
            if (ConfigVersion < 2)
            {
                // Migrate from version 1 to 2
                ConfigVersion = 2;
            }
        }

        public static Settings LoadFromFile(string? path = null)
        {
            var settingsPath = path ?? DefaultSettingsFileLocation;

            if (!File.Exists(settingsPath))
            {
                var settings = new Settings();
                settings.SaveToFile(settingsPath);
                return settings;
            }

            try
            {
                var json = File.ReadAllText(settingsPath);
                var settings = JsonConvert.DeserializeObject<Settings>(json) ?? new Settings();
                // Migrate if needed
                settings.Migrate();
                if (string.IsNullOrWhiteSpace(settings.transcriptionEngine))
                {
                    settings.transcriptionEngine = "whisper";
                }
                if (settings.transcriptionModelPreset == null)
                {
                    settings.transcriptionModelPreset = string.Empty;
                }
                if (settings.LmLinkTimeoutMs <= 0)
                {
                    settings.LmLinkTimeoutMs = 600000;
                }
                if (settings.LmLinkMaxRetries < 0)
                {
                    settings.LmLinkMaxRetries = 0;
                }
                if (settings.EmailProvider == null)
                {
                    settings.EmailProvider = "gmail";
                }
                settings.EmailProvider = NormalizeEmailProvider(settings.EmailProvider);
                if (settings.ArchiveSftpPort <= 0)
                {
                    settings.ArchiveSftpPort = 22;
                }
                if (string.IsNullOrWhiteSpace(settings.ArchiveSftpAuthMode))
                {
                    settings.ArchiveSftpAuthMode = "password";
                }
                if (string.IsNullOrWhiteSpace(settings.ArchiveLocalCachePath))
                {
                    settings.ArchiveLocalCachePath = Path.Combine(DefaultOfflineCaptureDirectory, "sftp-cache");
                }
                if (string.IsNullOrWhiteSpace(settings.TrDiagnosticsMode))
                {
                    settings.TrDiagnosticsMode = "local";
                }
                settings.TrDiagnosticsMode = settings.TrDiagnosticsMode.Trim().ToLowerInvariant() == "ssh" ? "ssh" : "local";
                if (settings.TrDiagnosticsPort <= 0)
                {
                    settings.TrDiagnosticsPort = 22;
                }
                if (string.IsNullOrWhiteSpace(settings.TrDiagnosticsAuthMode))
                {
                    settings.TrDiagnosticsAuthMode = "password";
                }
                if (string.IsNullOrWhiteSpace(settings.TrDiagnosticsRemoteLogDir))
                {
                    settings.TrDiagnosticsRemoteLogDir = "/var/log/trunk-recorder";
                }
                if (string.IsNullOrWhiteSpace(settings.TrDiagnosticsLocalCachePath))
                {
                    settings.TrDiagnosticsLocalCachePath = Path.Combine(DefaultWorkingDirectory, "tr-diagnostics");
                }
                // Ensure Alerts is initialized
                if (settings.Alerts == null)
                {
                    settings.Alerts = new List<Alert>();
                }
                return settings;
            }
            catch (Exception)
            {
                return new Settings();
            }
        }

        private static bool IsWhisperModelAlias(string model)
        {
            var normalized = Path.GetFileName(model).Trim().ToLowerInvariant();
            return normalized is "tiny" or "small" or "medium" or "base" or "large-v3" ||
                   normalized.StartsWith("ggml-");
        }

        public static string NormalizeEmailProvider(string? provider)
        {
            var value = (provider ?? string.Empty).Trim().ToLowerInvariant();
            return value switch
            {
                "yahoo" => "yahoo",
                _ => "gmail"
            };
        }
    }
}

