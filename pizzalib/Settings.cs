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
        public string? gmailUser;
        public string? gmailPassword;

        // Public properties for UI access
        public int ListenPort
        {
            get => listenPort;
            set => listenPort = value;
        }
        public string? WhisperModelFile
        {
            get => whisperModelFile;
            set => whisperModelFile = value;
        }
        public string? GmailUser
        {
            get => gmailUser;
            set => gmailUser = value;
        }
        public string? GmailPassword
        {
            get => gmailPassword;
            set => gmailPassword = value;
        }

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
        public int listenPort;
        public int analogChannels;
        public int analogBitDepth;
        public int analogSamplingRate;

        // Backing field for Talkgroups property (for JSON serialization)
        private List<Talkgroup>? talkgroups;

        /// <summary>
        /// Public property for Talkgroups list (for JSON serialization)
        /// </summary>
        public List<Talkgroup>? Talkgroups
        {
            get => talkgroups;
            set => talkgroups = value;
        }

        // whisper.net settings
        public string? whisperModelFile;

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
        public string? EncryptedGmailUser
        {
            get => ObfuscateString(gmailUser);
            set => gmailUser = DeobfuscateString(value);
        }
        [JsonIgnore]
        public string? EncryptedGmailPassword
        {
            get => ObfuscateString(gmailPassword);
            set => gmailPassword = DeobfuscateString(value);
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
        }

        public bool Equals(Settings? other)
        {
            if (other == null) return false;
            return TraceLevelApp == other.TraceLevelApp &&
                Alerts.SequenceEqual(other.Alerts) &&
                AutostartListener == other.AutostartListener &&
                gmailUser == other.gmailUser &&
                gmailPassword == other.gmailPassword &&
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
                talkgroups?.SequenceEqual(other.talkgroups) == true &&
                whisperModelFile == other.whisperModelFile;
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
            if (!string.IsNullOrEmpty(whisperModelFile) &&
                !File.Exists(whisperModelFile))
            {
                throw new Exception($"Invalid whisper model file: {whisperModelFile}");
            }

            if (!string.IsNullOrEmpty(gmailUser))
            {
                if (!IsValidEmail(gmailUser))
                {
                    throw new Exception($"Invalid Gmail address: {gmailUser}");
                }
                if (string.IsNullOrEmpty(gmailPassword))
                {
                    throw new Exception("Gmail password is required");
                }
            }
            else if (!string.IsNullOrEmpty(gmailPassword))
            {
                throw new Exception("Gmail user is required when password is specified");
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
                // No migration needed currently
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
    }
}