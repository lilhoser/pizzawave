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
using pizzalib;

namespace pizzapi
{
    public static class TraceLogger
    {
        public static readonly string m_TraceFileDir = Path.Combine(new string[] {
            pizzalib.Settings.DefaultWorkingDirectory, "Logs"});
        private static string m_Location = Path.Combine(new string[] { m_TraceFileDir,
                            $"pizzapi-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss")}.txt"});
        public static string CurrentLogPath => m_Location;
        private static TextWriterTraceListener? m_TextWriterTraceListener;
        private static SourceSwitch m_Switch =
            new SourceSwitch("pizzapiSwitch", "Verbose");
        private static bool m_IsInitialized;
        private static TraceSource[] Sources = {
            new TraceSource("MainWindow", SourceLevels.Verbose),
            new TraceSource("Settings", SourceLevels.Verbose),
            new TraceSource("Alerts", SourceLevels.Verbose),
            new TraceSource("OfflineMode", SourceLevels.Verbose),
            new TraceSource("Cleanup", SourceLevels.Verbose),
            new TraceSource("Audio", SourceLevels.Verbose),
            new TraceSource("Headless", SourceLevels.Verbose),
            new TraceSource("Insights", SourceLevels.Information),
        };

        public enum TraceLoggerType
        {
            MainWindow,
            Settings,
            Alerts,
            OfflineMode,
            Cleanup,
            Audio,
            Headless,
            Insights,
            Max
        }

        public static void Initialize(bool RedirectToStdout = false)
        {
            if (m_IsInitialized) return;

            // Disable AutoFlush to prevent excessive disk I/O on Linux/RPI
            // Traces will be flushed when Shutdown() is called or manually
            System.Diagnostics.Trace.AutoFlush = false;

            if (Directory.Exists(pizzalib.Settings.DefaultWorkingDirectory))
            {
                if (!Directory.Exists(m_TraceFileDir))
                {
                    try
                    {
                        Directory.CreateDirectory(m_TraceFileDir);
                    }
                    catch (Exception) // swallow
                    {
                    }
                }

                try
                {
                    var stream = new FileStream(
                        m_Location,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    var writer = new StreamWriter(stream) { AutoFlush = false };
                    m_TextWriterTraceListener =
                        new TextWriterTraceListener(writer, "pizzapiTextWriterListener");
                }
                catch (IOException)
                {
                    m_TextWriterTraceListener = null;
                }
                catch (UnauthorizedAccessException)
                {
                    m_TextWriterTraceListener = null;
                }
            }

            foreach (var source in Sources)
            {
                if (m_TextWriterTraceListener != null)
                {
                    source.Listeners.Add(m_TextWriterTraceListener);
                }
                source.Switch = m_Switch;
            }

            m_IsInitialized = true;
        }

        public static void Shutdown()
        {
            try
            {
                m_TextWriterTraceListener?.Close();
            }
            catch
            {
                // Ignore errors during shutdown
            }
        }

        public static void SetLevel(SourceLevels Level)
        {
            m_Switch.Level = Level;
        }

        public static void Flush()
        {
            try
            {
                foreach (var source in Sources)
                    source.Flush();
                m_TextWriterTraceListener?.Flush();
                System.Diagnostics.Trace.Flush();
            }
            catch
            {
                // Ignore flush errors for troubleshooting UX.
            }
        }

        public static void Trace(TraceLoggerType Type, TraceEventType EventType, string Message)
        {
            if (Type >= TraceLoggerType.Max)
            {
                throw new Exception("Invalid logger type");
            }

            try
            {
                Sources[(int)Type].TraceEvent(EventType, 1, $"{DateTime.Now:M/d/yyyy h:mm tt}: {Message}");
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public static void OpenTraceLog()
        {
            Utilities.LaunchFile(m_Location);
        }
    }
}
