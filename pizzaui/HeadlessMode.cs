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
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using System.Text;
using pizzalib;

namespace pizzaui
{
    using static TraceLogger;
    using Whisper = pizzalib.Whisper;

    internal class HeadlessMode
    {
        private StreamServer? m_StreamServer;
        private Whisper? m_Whisper;
        private Alerter? m_Alerter;
        private Settings? m_Settings;

        public HeadlessMode()
        {
        }

        public async Task<int> Run(string[] HeadlessModeArgs)
        {
            //
            // Redirect console output to parent process
            //
            var console = new WinConsole();
            console.Initialize(false);
            TraceLogger.Initialize(true);
            pizzalib.TraceLogger.Initialize(true);
            Trace(TraceLoggerType.MainWindow, TraceEventType.Information, "");
            Trace(TraceLoggerType.MainWindow, TraceEventType.Information, "Pizzawave Headless Mode.");
            Trace(TraceLoggerType.MainWindow, TraceEventType.Information, "Console initialized");

            if (HeadlessModeArgs == null || HeadlessModeArgs.Length == 0)
            {
                PrintUsage("Error: No arguments provided.");
                return 1;
            }

            //
            // Look. I've tried Microsoft's System.CommandLine for parsing and it's simply
            // awful, awful awful. So this is all you'll get and YOU'LL LIKE IT.
            //
            string settingsPath = pizzalib.Settings.DefaultSettingsFileLocation;
            foreach (var arg in HeadlessModeArgs)
            {
                if (arg.ToLower().StartsWith("--settings") ||
                    arg.ToLower().StartsWith("-settings"))
                {
                    if (!arg.Contains('='))
                    {
                        PrintUsage($"Invalid settings file: {arg}");
                        return 1;
                    }
                    var pieces = arg.Split('=');
                    if (pieces.Length != 2)
                    {
                        PrintUsage($"Invalid settings file: {arg}");
                        return 1;
                    }
                    settingsPath = pieces[1];
                    if (!File.Exists(settingsPath))
                    {
                        PrintUsage($"Settings file doesn't exist: {settingsPath}");
                        return 1;
                    }
                    break;
                }
                else if (arg.ToLower().StartsWith("--help") || arg.ToLower().StartsWith("-help"))
                {
                    PrintUsage();
                    return 0;
                }
                else if (arg.ToLower().StartsWith("--headless") || arg.ToLower().StartsWith("-headless"))
                {
                    continue;
                }
                else
                {
                    PrintUsage($"Unknown argument {arg}");
                    return 1;
                }
            }
            var result = await Initialize(settingsPath!);
            if (!result)
            {
                return 1;
            }
            result = await StartServer(); // blocks until CTRL+C
            TraceLogger.Shutdown();
            return result ? 0 : 1;
        }

        private async Task<bool> Initialize(string SettingsPath)
        {
            if (!File.Exists(SettingsPath))
            {
                Trace(TraceLoggerType.Headless,
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
                    Trace(TraceLoggerType.Headless, TraceEventType.Error, $"{ex.Message}");
                    return false;
                }
            }

            try
            {
                TraceLogger.SetLevel(m_Settings.TraceLevelApp);
                pizzalib.TraceLogger.SetLevel(m_Settings.TraceLevelApp);
                m_Whisper = new Whisper(m_Settings);
                m_Alerter = new Alerter(m_Settings, m_Whisper, NewCallTranscribed);
                m_StreamServer = new StreamServer(m_Alerter.NewCallDataAvailable, m_Settings);
                _ = await m_Whisper.Initialize();
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Headless, TraceEventType.Error, $"{ex.Message}");
                return false;
            }
            return true;
        }

        private async Task<bool> StartServer()
        {
            try
            {
                Console.CancelKeyPress += (sender, eventArgs) => {
                    eventArgs.Cancel = true;
                    Trace(TraceLoggerType.Headless, TraceEventType.Information, "Server shutting down...");
                    m_StreamServer?.Shutdown();
                };
                _ = await m_StreamServer?.Listen(); // blocks until CTRL+C                    
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Headless, TraceEventType.Error, $"{ex.Message}");
                return false;
            }

            return true;
        }

        private void NewCallTranscribed(TranscribedCall Call)
        {
            Trace(TraceLoggerType.Headless, TraceEventType.Verbose, $"{Call.ToString(m_Settings!)}");

            var jsonContents = new StringBuilder();
            try
            {
                var jsonObject = JsonConvert.SerializeObject(Call, Formatting.Indented);
                jsonContents.AppendLine(jsonObject);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Headless,
                      TraceEventType.Error,
                      $"Failed to create JSON: {ex.Message}");
                return;
            }
            try
            {
                var target = Path.Combine(Settings.DefaultWorkingDirectory,
                    Settings.DefaultCallLogFileName);
                using (var writer = new StreamWriter(target, true, Encoding.UTF8))
                {
                    writer.WriteLine(jsonContents.ToString());
                }
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Headless,
                      TraceEventType.Error,
                      $"Failed to save JSON: {ex.Message}");
                return;
            }
        }

        private void PrintUsage(string Message = null)
        {
            if (!string.IsNullOrEmpty(Message))
            {
                Trace(TraceLoggerType.MainWindow, TraceEventType.Error, Message);
            }
            Trace(TraceLoggerType.MainWindow,
                  TraceEventType.Information,
                  "Usage: pizzawave.exe --headless [--settings=<path>]");
        }
    }

    internal class WinConsole : IDisposable
    {
        private bool m_Disposed = false;
        private bool m_ConsoleCreatedOrAttached = false;

        public WinConsole() { }

        ~WinConsole()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;

            if (m_ConsoleCreatedOrAttached)
            {
                FreeConsole();
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public void Initialize(bool alwaysCreateNewConsole = true)
        {
            if (alwaysCreateNewConsole)
            {
                if (AllocConsole() == 0)
                {
                    throw new Exception($"Unable to create console: {Marshal.GetLastWin32Error()}");
                }
                m_ConsoleCreatedOrAttached = true;
            }

            if (AttachConsole(ATTACH_PARENT) == 0)
            {
                var lastError = Marshal.GetLastWin32Error();
                if (lastError == ERROR_INVALID_HANDLE) // parent has no console!
                {
                    if (m_ConsoleCreatedOrAttached)
                    {
                        throw new Exception("Unable to attach newly created console to parent (no handle)");
                    }
                    if (AllocConsole() == 0)
                    {
                        throw new Exception($"Unable to create console: {Marshal.GetLastWin32Error()}");
                    }
                    m_ConsoleCreatedOrAttached = true;
                }
                else
                {
                    throw new Exception($"Unable to attach to process console: {lastError}");
                }
            }
            else
            {
                m_ConsoleCreatedOrAttached = true;
            }
        }

        [DllImport("kernel32.dll",
            EntryPoint = "AllocConsole",
            SetLastError = true,
            CharSet = CharSet.Auto,
            CallingConvention = CallingConvention.StdCall)]
        private static extern int AllocConsole();

        [DllImport("kernel32.dll",
            EntryPoint = "FreeConsole",
            SetLastError = true,
            CharSet = CharSet.Auto,
            CallingConvention = CallingConvention.StdCall)]
        private static extern int FreeConsole();

        [DllImport("kernel32.dll",
            EntryPoint = "AttachConsole",
            SetLastError = true,
            CharSet = CharSet.Auto,
            CallingConvention = CallingConvention.StdCall)]
        private static extern uint AttachConsole(uint dwProcessId);

        private const uint ERROR_INVALID_HANDLE = 6;
        private const uint ATTACH_PARENT = 0xFFFFFFFF;
    }
}
