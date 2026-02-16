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
using Newtonsoft.Json;
using System.Diagnostics;
using System.Reflection;
using static pizzalib.TraceLogger;

namespace pizzalib
{
    public abstract class StandaloneClient
    {
        protected CallManager? m_CallManager;
        protected Settings? m_Settings;

        public StandaloneClient()
        {
        }

        protected abstract void PrintUsage(string Error);
        protected abstract void NewCallTranscribed(TranscribedCall Call);

        protected virtual void WriteBanner()
        {
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = assembly.GetManifestResourceNames().Single(
                str => str.EndsWith("banner.txt"));
            string banner = string.Empty;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName)!)
            using (StreamReader reader = new StreamReader(stream))
            {
                banner = reader.ReadToEnd();
            }
            Trace(TraceLoggerType.StandaloneClient, TraceEventType.Information, banner);
        }

        public virtual async Task<int> Run(string[] Args)
        {
            if (m_CallManager == null)
            {
                throw new Exception("Deriving class must initialize call manager");
            }

            WriteBanner();
            //
            // Look. I've tried Microsoft's System.CommandLine for parsing and it's simply
            // awful, awful awful. So this is all you'll get and YOU'LL LIKE IT.
            //
            string settingsPath = pizzalib.Settings.DefaultSettingsFileLocation;
            string tgLocation = string.Empty;
            foreach (var arg in Args)
            {
                if (arg.ToLower().StartsWith("--settings") ||
                    arg.ToLower().StartsWith("-settings"))
                {
                    settingsPath = ParsePathArgument(arg);
                    if (string.IsNullOrEmpty(settingsPath))
                    {
                        PrintUsage($"Invalid settings file: {arg}");
                        return 1;
                    }

                    break;
                }
                else if (arg.ToLower().StartsWith("--talkgroups") ||
                         arg.ToLower().StartsWith("-talkgroups"))
                {
                    tgLocation = ParsePathArgument(arg);
                    if (string.IsNullOrEmpty(tgLocation))
                    {
                        PrintUsage($"Invalid talkgroup file: {arg}");
                        return 1;
                    }
                    break;
                }
                else if (arg.ToLower().StartsWith("--help") || arg.ToLower().StartsWith("-help"))
                {
                    PrintUsage(string.Empty);
                    return 0;
                }
                else
                {
                    PrintUsage($"Unknown argument {arg}");
                    return 1;
                }
            }
            var result = await Initialize(settingsPath, tgLocation);
            if (!result)
            {
                return 1;
            }

            try
            {
                Console.CancelKeyPress += (sender, eventArgs) => {
                    eventArgs.Cancel = true;
                    Trace(TraceLoggerType.StandaloneClient, TraceEventType.Information, "Call manager shutting down...");
                    m_CallManager.Stop();
                };
                Trace(TraceLoggerType.StandaloneClient, TraceEventType.Information, "Call manager starting...");
                result = await m_CallManager.Start(block: true); // blocks until CTRL+C
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.StandaloneClient, TraceEventType.Error, $"{ex.Message}");
                return 1;
            }

            return result ? 0 : 1;
        }

        private string ParsePathArgument(string Argument)
        {
            if (!Argument.Contains('='))
            {
                return string.Empty;
            }
            var pieces = Argument.Split('=');
            if (pieces.Length != 2)
            {
                return string.Empty;
            }
            var targetPath = pieces[1];
            if (!File.Exists(targetPath))
            {
                return string.Empty;
            }
            return targetPath;
        }

        private async Task<bool> Initialize(string SettingsPath, string TalkgroupLocation)
        {
            if (m_CallManager == null)
            {
                throw new Exception("Deriving class must initialize call manager");
            }
            Trace(TraceLoggerType.StandaloneClient,
                  TraceEventType.Information,
                  $"Init: Using settings {SettingsPath}, talkgroups {TalkgroupLocation}");
            if (!File.Exists(SettingsPath))
            {
                Trace(TraceLoggerType.StandaloneClient,
                      TraceEventType.Warning,
                      $"Settings file {SettingsPath} does not exist, loading default...");
                m_Settings = new Settings();
                m_Settings.SaveToFile(SettingsPath); // persist it
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(SettingsPath);
                    m_Settings = (Settings)JsonConvert.DeserializeObject(json, typeof(Settings))!;
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.StandaloneClient, TraceEventType.Error, $"{ex.Message}");
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(TalkgroupLocation))
            {
                try
                {
                    var tgs = TalkgroupHelper.GetTalkgroupsFromCsv(TalkgroupLocation);
                    if (tgs.Count == 0)
                    {
                        Trace(TraceLoggerType.StandaloneClient,
                              TraceEventType.Warning,
                              "Invalid talkgroup file: no data in file.");
                    }
                    else
                    {
                        Trace(TraceLoggerType.StandaloneClient,
                              TraceEventType.Information,
                              $"Loaded {tgs.Count} talkgroups");
                        m_Settings.Talkgroups = tgs;
                        m_Settings.SaveToFile(SettingsPath); // persist
                    }
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.StandaloneClient,
                          TraceEventType.Warning,
                          $"Unable to parse talkgroup CSV '{TalkgroupLocation}': {ex.Message}");
                }
            }
            else
            {
                Trace(TraceLoggerType.StandaloneClient, TraceEventType.Warning, "No talkgroups provided!");
            }

            TraceLogger.SetLevel(m_Settings.TraceLevelApp);

            //
            // Use Console directly here in case trace isn't verbose enough.
            //
            Console.WriteLine($"Init: trace level {m_Settings.TraceLevelApp}");

            try
            {
                _ = await m_CallManager.Initialize(m_Settings);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.StandaloneClient, TraceEventType.Error, $"{ex.Message}");
                return false;
            }

            Trace(TraceLoggerType.StandaloneClient, TraceEventType.Verbose, "Init: Complete");

            return true;
        }
    }
}
