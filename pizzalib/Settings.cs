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

namespace pizzalib
{
    using static TraceLogger;

    public class Settings : IEquatable<Settings>
    {
        public static readonly string DefaultWorkingDirectory = Path.Combine(
            new string[] { Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "pizzawave"});
        public static string DefaultSettingsFileName = "settings.json";
        public static string DefaultLiveCaptureDirectory = Path.Combine(DefaultWorkingDirectory, "captures");
        public static string DefaultOfflineCaptureDirectory = Path.Combine(DefaultWorkingDirectory, "offline");
        public static string DefaultSettingsFileLocation = Path.Combine(
            DefaultWorkingDirectory, DefaultSettingsFileName);
        public static string DefaultAlertWavLocation = Path.Combine(DefaultWorkingDirectory, "alerts");
        //
        // pizzalib library settings
        //
        public SourceLevels TraceLevelApp;
        public List<Alert> Alerts;
        public bool AutostartListener;
        public string? gmailUser;
        public string? gmailPassword;
        //
        // Alert audio settings
        //
        public bool AutoplayAlerts;
        public int SnoozeDurationMinutes;
        //
        // UI display settings
        //
        public int SortMode;  // 0=newest first, 1=oldest first, 2=talkgroup
        public int GroupMode; // 0=none, 1=talkgroup, 2=time of day, 3=source
        public double FontSize; // Default 14.0
        public bool AutoCleanupCalls; // Auto-cleanup old calls to prevent memory leaks
        public int MaxCallsToKeep; // Number of calls to keep before auto-cleanup
        //
        // TrunkRecorder settings
        //
        public int listenPort;
        public int analogChannels;
        public int analogBitDepth;
        public int analogSamplingRate;
        //
        // Backing field for Talkgroups property (for JSON serialization)
        //
        public List<Talkgroup>? talkgroups;
        //
        // Public property wrappers for compatibility
        //
        [JsonIgnore]
        public int ListenPort
        {
            get => listenPort;
            set => listenPort = value;
        }
        [JsonIgnore]
        public int AnalogChannels
        {
            get => analogChannels;
            set => analogChannels = value;
        }
        [JsonIgnore]
        public int AnalogBitDepth
        {
            get => analogBitDepth;
            set => analogBitDepth = value;
        }
        [JsonIgnore]
        public int AnalogSamplingRate
        {
            get => analogSamplingRate;
            set => analogSamplingRate = value;
        }
        [JsonIgnore]
        public string? WhisperModelFile
        {
            get => whisperModelFile;
            set => whisperModelFile = value;
        }
        [JsonIgnore]
        public string? GmailUser
        {
            get => gmailUser;
            set => gmailUser = value;
        }
        [JsonIgnore]
        public string? GmailPassword
        {
            get => gmailPassword;
            set => gmailPassword = value;
        }
        [JsonIgnore]
        public bool IsAutoplayAlertsEnabled
        {
            get => AutoplayAlerts;
            set => AutoplayAlerts = value;
        }
        [JsonIgnore]
        public int AlertSnoozeDurationMinutes
        {
            get => SnoozeDurationMinutes;
            set => SnoozeDurationMinutes = value;
        }
        [JsonIgnore]
        public List<Talkgroup>? Talkgroups
        {
            get => talkgroups;
            set => talkgroups = value;
        }
        //
        // whisper.net settings
        //
        public string? whisperModelFile;
        //
        // Non-serializable fields
        //
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

        public Settings()
        {
            if (!Directory.Exists(DefaultWorkingDirectory))
            {
                try
                {
                    Directory.CreateDirectory(DefaultWorkingDirectory);
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.Settings,
                          TraceEventType.Warning,
                          $"Unable to create settings directory " +
                          $"'{DefaultWorkingDirectory}': {ex.Message}");
                }
            }
            Alerts = new List<Alert>();
            TraceLevelApp = SourceLevels.Error;
            AutostartListener = true;
            AutoplayAlerts = false;
            SnoozeDurationMinutes = 15;
            SortMode = 0;  // Default: newest first
            GroupMode = 0; // Default: no grouping
            FontSize = 14.0; // Default font size
            AutoCleanupCalls = true; // Default: enabled (prevents memory leaks)
            MaxCallsToKeep = 100; // Default: keep 100 calls
            listenPort = 9123;
            analogSamplingRate = 8000;
            analogBitDepth = 16;
            analogChannels = 1;
        }

        public override bool Equals(object? Other)
        {
            if (Other == null)
            {
                return false;
            }
            var field = Other as Settings;
            return Equals(field);
        }

        public bool Equals(Settings? Other)
        {
            if (Other == null)
            {
                return false;
            }
            return TraceLevelApp == Other.TraceLevelApp &&
                Alerts == Other.Alerts &&
                AutostartListener == Other.AutostartListener &&
                GmailUser == Other.GmailUser &&
                GmailPassword == Other.GmailPassword &&
                AutoplayAlerts == Other.AutoplayAlerts &&
                SnoozeDurationMinutes == Other.SnoozeDurationMinutes &&
                SortMode == Other.SortMode &&
                GroupMode == Other.GroupMode &&
                FontSize == Other.FontSize &&
                AutoCleanupCalls == Other.AutoCleanupCalls &&
                MaxCallsToKeep == Other.MaxCallsToKeep &&
                ListenPort == Other.ListenPort &&
                AnalogChannels == Other.AnalogChannels &&
                AnalogBitDepth == Other.AnalogBitDepth &&
                AnalogSamplingRate == Other.AnalogSamplingRate &&
                Talkgroups == Other.Talkgroups &&
                WhisperModelFile == Other.WhisperModelFile;
        }

        public static bool operator ==(Settings? Settings1, Settings? Settings2)
        {
            if ((object)Settings1 == null || (object)Settings2 == null)
                return Equals(Settings1, Settings2);
            return Settings1.Equals(Settings2);
        }

        public static bool operator !=(Settings? Settings1, Settings? Settings2)
        {
            if ((object)Settings1 == null || (object)Settings2 == null)
                return !Equals(Settings1, Settings2);
            return !(Settings1.Equals(Settings2));
        }

        public override int GetHashCode()
        {
            return (TraceLevelApp,
                Alerts,
                AutostartListener,
                GmailUser,
                GmailPassword,
                AutoplayAlerts,
                SnoozeDurationMinutes,
                SortMode,
                GroupMode,
                FontSize,
                AutoCleanupCalls,
                MaxCallsToKeep,
                ListenPort,
                AnalogBitDepth,
                AnalogChannels,
                AnalogSamplingRate,
                Talkgroups,
                WhisperModelFile
                ).GetHashCode();
        }

        public static bool HasFieldChanged(Settings Object1, Settings Object2, string Name)
        {
            var fields = typeof(Settings).GetFields(
                BindingFlags.Public | BindingFlags.Instance).ToList();
            var field = fields.FirstOrDefault(p => p.Name == Name);
            try
            {
                dynamic value1 = field!.GetValue(Object1)!;
                dynamic value2 = field!.GetValue(Object2)!;
                return value1 != value2;
            }
            catch (Exception) { return false; }
        }

        public virtual void Validate()
        {
            if (!string.IsNullOrEmpty(WhisperModelFile) &&
                !File.Exists(WhisperModelFile))
            {
                throw new Exception($"Invalid whisper model file: {WhisperModelFile}");
            }

            if (!string.IsNullOrEmpty(GmailUser))
            {
                if (string.IsNullOrEmpty(GmailPassword))
                {
                    throw new Exception("Gmail password is required");
                }
            }
            else if (!string.IsNullOrEmpty(GmailPassword))
            {
                if (string.IsNullOrEmpty(GmailUser))
                {
                    throw new Exception("Gmail user is required");
                }
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

