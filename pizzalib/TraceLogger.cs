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
        private static TextWriterTraceListener m_TextWriterTraceListener =
            new TextWriterTraceListener(m_Location, "pizzalibTextWriterListener");
        private static SourceSwitch m_Switch =
            new SourceSwitch("pizzalibSwitch", "Verbose");
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
            System.Diagnostics.Trace.AutoFlush = true;
            foreach (var source in Sources)
            {
                source.Listeners.Add(m_TextWriterTraceListener);
                source.Switch = m_Switch;
            }

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
            }
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

        public static void Trace(TraceLoggerType Type, TraceEventType EventType, string Message)
        {
            if (Type >= TraceLoggerType.Max)
            {
                throw new Exception("Invalid logger type");
            }

            // Structured logging format: timestamp|level|type|message
            var structuredMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}|{(int)EventType}|{Type}|{Message}";
            Sources[(int)Type].TraceEvent(EventType, 1, structuredMessage);
        }

        public static void OpenTraceLog()
        {
            Utilities.LaunchFile(m_Location);
        }
    }
}