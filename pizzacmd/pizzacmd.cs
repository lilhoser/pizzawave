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
using pizzalib;
using System.Diagnostics;
using System.Reflection;
using System.Text;

namespace pizzacmd
{
    using static TraceLogger;
    using Whisper = pizzalib.Whisper;

    internal class pizzacmd
    {
        private StreamServer? m_StreamServer;
        private Whisper? m_Whisper;
        private Alerter? m_Alerter;
        private Settings? m_Settings;

        public pizzacmd()
        {
        }

        public async Task<int> Run(string[] Args)
        {
            TraceLogger.Initialize(true);
            pizzalib.TraceLogger.Initialize(true);
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
                        Console.WriteLine($"Invalid settings file: {arg}");
                        Console.WriteLine("Usage: pizzacmd.exe --settings=<path>");
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
                        Console.WriteLine($"Invalid talkgroup file: {arg}");
                        Console.WriteLine("Usage: pizzacmd.exe --talkgroups=<path_to_csv>");
                        return 1;
                    }
                    break;
                }
                else if (arg.ToLower().StartsWith("--help") || arg.ToLower().StartsWith("-help"))
                {
                    Console.WriteLine("Usage: pizzacmd.exe --settings=<path>");
                    return 0;
                }
                else
                {
                    Console.WriteLine($"Unknown argument {arg}");
                    Console.WriteLine("Usage: pizzacmd.exe --settings=<path> [--talkgroups=<talkgroup_csv_file>]");
                    return 1;
                }
            }
            var result = await Initialize(settingsPath, tgLocation);
            if (!result)
            {
                return 1;
            }
            result = await StartServer(); // blocks until CTRL+C
            TraceLogger.Shutdown();
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
            Trace(TraceLoggerType.Main,
                  TraceEventType.Information,
                  $"Init: Using settings {SettingsPath}, talkgroups {TalkgroupLocation}");
            if (!File.Exists(SettingsPath))
            {
                Trace(TraceLoggerType.Main,
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
                    Trace(TraceLoggerType.Main, TraceEventType.Error, $"{ex.Message}");
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
                        Trace(TraceLoggerType.Main,
                              TraceEventType.Warning,
                              "Invalid talkgroup file: no data in file.");
                    }
                    else
                    {
                        Trace(TraceLoggerType.Main,
                              TraceEventType.Information,
                              $"Loaded {tgs.Count} talkgroups");
                        m_Settings.talkgroups = tgs;
                        m_Settings.SaveToFile(SettingsPath); // persist
                    }
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.Main,
                          TraceEventType.Warning,
                          $"Unable to parse talkgroup CSV '{TalkgroupLocation}': {ex.Message}");
                }
            }
            else
            {
                Trace(TraceLoggerType.Main, TraceEventType.Warning, "No talkgroups provided!");
            }

            TraceLogger.SetLevel(m_Settings.TraceLevelApp);
            pizzalib.TraceLogger.SetLevel(m_Settings.TraceLevelApp);

            //
            // Use Console directly here in case trace isn't verbose enough.
            //
            Console.WriteLine($"Init: trace level {m_Settings.TraceLevelApp}");

            try
            {
                m_Whisper = new Whisper(m_Settings);
                m_Alerter = new Alerter(m_Settings, m_Whisper, NewCallTranscribed);
                m_StreamServer = new StreamServer(m_Alerter.NewCallDataAvailable, m_Settings);
                _ = await m_Whisper.Initialize();
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Main, TraceEventType.Error, $"{ex.Message}");
                return false;
            }

            Trace(TraceLoggerType.Main, TraceEventType.Verbose, "Init: Complete");

            return true;
        }

        private async Task<bool> StartServer()
        {
            try
            {
                Console.CancelKeyPress += (sender, eventArgs) => {
                    eventArgs.Cancel = true;
                    Trace(TraceLoggerType.Main, TraceEventType.Information, "Server shutting down...");
                    m_StreamServer!.Shutdown();
                };
                _ = await m_StreamServer!.Listen(); // blocks until CTRL+C                    
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Main, TraceEventType.Error, $"{ex.Message}");
                return false;
            }

            return true;
        }

        private void NewCallTranscribed(TranscribedCall Call)
        {
            //
            // NB: do not use Trace() here, as this could hide the call data based on user's settings
            //
            Console.WriteLine($"{Call.ToString(m_Settings!)}");

            var jsonContents = new StringBuilder();
            try
            {
                var jsonObject = JsonConvert.SerializeObject(Call, Formatting.Indented);
                jsonContents.AppendLine(jsonObject);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Main,
                      TraceEventType.Error,
                      $"Failed to create JSON: {ex.Message}");
                return;
            }
            try
            {
                var target = Path.Combine(pizzalib.Settings.DefaultWorkingDirectory,
                    pizzalib.Settings.DefaultCallLogFileName);
                using (var writer = new StreamWriter(target, true, Encoding.UTF8))
                {
                    writer.WriteLine(jsonContents.ToString());
                }
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Main,
                      TraceEventType.Error,
                      $"Failed to save JSON: {ex.Message}");
                return;
            }
        }

        private static void WriteBanner()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            var assembly = Assembly.GetExecutingAssembly();
            string resourceName = assembly.GetManifestResourceNames().Single(
                str => str.EndsWith("banner.txt"));
            string banner = string.Empty;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName)!)
            using (StreamReader reader = new StreamReader(stream))
            {
                banner = reader.ReadToEnd();
            }
            var separator = "---------------------------------------------------";

            Console.WriteLine(banner);
            Trace(TraceLoggerType.Main, TraceEventType.Warning, $"pizzacmd {version}");
            Trace(TraceLoggerType.Main, TraceEventType.Information, separator);
        }
    }
}
