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
        public static string DefaultCallLogFileName = "calls.json";
        public static string DefaultSettingsFileLocation = Path.Combine(
            DefaultWorkingDirectory, DefaultSettingsFileName);
        public static string DefaultAlertWavLocation = Path.Combine(DefaultWorkingDirectory, "alerts");
        //
        // pizzalib library settings
        //
        public SourceLevels TraceLevelApp;
        public string? WavFileLocation;
        public List<Alert> Alerts;
        public bool AutostartListener;
        public string? gmailUser;
        public string? gmailPassword;
        //
        // TrunkRecorder settings
        //
        public int listenPort;
        public int analogChannels;
        public int analogBitDepth;
        public int analogSamplingRate;
        public List<Talkgroup>? talkgroups;
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
                WavFileLocation == Other.WavFileLocation &&
                Alerts == Other.Alerts &&
                AutostartListener == Other.AutostartListener &&
                gmailUser == Other.gmailUser &&
                gmailPassword == Other.gmailPassword &&
                listenPort == Other.listenPort &&
                analogChannels == Other.analogChannels &&
                analogBitDepth == Other.analogBitDepth &&
                analogSamplingRate == Other.analogSamplingRate &&
                talkgroups == Other.talkgroups &&
                whisperModelFile == Other.whisperModelFile;
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
                WavFileLocation,
                Alerts,
                AutostartListener,
                gmailUser,
                gmailPassword,
                listenPort,
                analogBitDepth,
                analogChannels,
                analogSamplingRate,
                talkgroups,
                whisperModelFile
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
            if (!string.IsNullOrEmpty(whisperModelFile) &&
                !File.Exists(whisperModelFile))
            {
                throw new Exception($"Invalid whisper model file: {whisperModelFile}");
            }

            if (!string.IsNullOrEmpty(gmailUser))
            {
                if (string.IsNullOrEmpty(gmailPassword))
                {
                    throw new Exception("Gmail password is required");
                }
            }
            else if (!string.IsNullOrEmpty(gmailPassword))
            {
                if (string.IsNullOrEmpty(gmailUser))
                {
                    throw new Exception("Gmail user is required");
                }
            }
        }

        public void SaveToFile(string? Target)
        {
            var target = Target;
            if (string.IsNullOrEmpty(target))
            {
                target = Settings.DefaultSettingsFileLocation;
            }

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
    }
}

