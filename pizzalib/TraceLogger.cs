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
using System.IO;

namespace pizzalib
{
    public static class TraceLogger
    {
        public static readonly string m_TraceFileDir = Path.Combine(new string[] {
            Settings.DefaultWorkingDirectory, "Logs"});
        private static string m_Location = Path.Combine(new string[] { m_TraceFileDir,
                            $"pizzalib-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss")}.txt"});
        private static TextWriterTraceListener? m_TextWriterTraceListener;
        private static SourceSwitch m_Switch =
            new SourceSwitch("pizzalibSwitch", "Verbose");
        private static bool m_IsInitialized;
        private static TraceSource[] Sources = {
            new TraceSource("StreamServer", SourceLevels.Verbose),
            new TraceSource("RawCallData", SourceLevels.Verbose),
            new TraceSource("Settings", SourceLevels.Verbose),
            new TraceSource("Whisper", SourceLevels.Verbose),
            new TraceSource("Alerts", SourceLevels.Verbose),
            new TraceSource("Utilities", SourceLevels.Verbose),
            new TraceSource("StandaloneClient", SourceLevels.Verbose),
            new TraceSource("CallManager", SourceLevels.Verbose),
            new TraceSource("LiveCallManager", SourceLevels.Verbose),
            new TraceSource("OfflineCallManager", SourceLevels.Verbose),
            new TraceSource("Headless", SourceLevels.Verbose),
        };

        public enum TraceLoggerType
        {
            StreamServer,
            RawCallData,
            Settings,
            Whisper,
            Alerts,
            Utilities,
            StandaloneClient,
            CallManager,
            LiveCallManager,
            OfflineCallManager,
            Headless,
            Max
        }

        public static void Initialize(bool RedirectToStdout = false)
        {
            if (m_IsInitialized) return;

            // Disable AutoFlush to prevent excessive disk I/O on Linux/RPI
            // Traces will be flushed when Shutdown() is called or manually
            System.Diagnostics.Trace.AutoFlush = false;

            if (Directory.Exists(Settings.DefaultWorkingDirectory))
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
                        new TextWriterTraceListener(writer, "pizzalibTextWriterListener");
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
                // Ignore flush errors to avoid impacting runtime.
            }
        }

        public static void Trace(TraceLoggerType Type, TraceEventType EventType, string Message)
        {
            if (Type >= TraceLoggerType.Max)
            {
                throw new Exception("Invalid logger type");
            }

            // Structured logging format: timestamp|level|type|message
            // Note: AutoFlush is disabled to prevent excessive disk I/O on Linux/RPI
            var structuredMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}|{(int)EventType}|{Type}|{Message}";
            try
            {
                Sources[(int)Type].TraceEvent(EventType, 1, structuredMessage);
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
