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
        private static TextWriterTraceListener m_TextWriterTraceListener =
            new TextWriterTraceListener(m_Location, "pizzapiTextWriterListener");
        private static ConsoleTraceListener m_ConsoleTraceListener = new ConsoleTraceListener();
        private static SourceSwitch m_Switch =
            new SourceSwitch("pizzapiSwitch", "Verbose");
        private static TraceSource[] Sources = {
            new TraceSource("MainWindow", SourceLevels.Verbose),
            new TraceSource("Settings", SourceLevels.Verbose),
            new TraceSource("Alerts", SourceLevels.Verbose),
            new TraceSource("OfflineMode", SourceLevels.Verbose),
            new TraceSource("Cleanup", SourceLevels.Verbose),
            new TraceSource("Audio", SourceLevels.Verbose),
            new TraceSource("Headless", SourceLevels.Verbose),
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
            Max
        }

        public static void Initialize(bool RedirectToStdout = false)
        {
            System.Diagnostics.Trace.AutoFlush = true;
            foreach (var source in Sources)
            {
                source.Listeners.Add(m_TextWriterTraceListener);
                source.Switch = m_Switch;
                if (RedirectToStdout)
                {
                    source.Listeners.Add(m_ConsoleTraceListener);
                }
            }

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
            try
            {
                m_ConsoleTraceListener?.Close();
            }
            catch
            {
                // Ignore errors during shutdown - on Linux this can crash if console is already closed
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
            using (GetColorContext(EventType))
            {
                Sources[(int)Type].TraceEvent(EventType, 1, $"{DateTime.Now:M/d/yyyy h:mm tt}: {Message}");
            }
        }

        public static void OpenTraceLog()
        {
            Utilities.LaunchFile(m_Location);
        }

        private static ColorContext GetColorContext(TraceEventType eventType)
        {
            switch (eventType)
            {
                case TraceEventType.Verbose:
                    return new ColorContext(ConsoleColor.DarkGray);
                case TraceEventType.Information:
                    return new ColorContext(ConsoleColor.Gray);
                case TraceEventType.Critical:
                    return new ColorContext(ConsoleColor.DarkRed);
                case TraceEventType.Error:
                    return new ColorContext(ConsoleColor.Red);
                case TraceEventType.Warning:
                    return new ColorContext(ConsoleColor.Yellow);
                case TraceEventType.Start:
                    return new ColorContext(ConsoleColor.DarkGreen);
                case TraceEventType.Stop:
                    return new ColorContext(ConsoleColor.Green);
                case TraceEventType.Suspend:
                    return new ColorContext(ConsoleColor.DarkYellow);
                case TraceEventType.Resume:
                    return new ColorContext(ConsoleColor.Yellow);
                default:
                    return new ColorContext(ConsoleColor.White);
            }
        }

        private class ColorContext : IDisposable
        {
            public ColorContext(ConsoleColor NewColor)
            {
                m_OriginalColor = Console.ForegroundColor;
                Console.ForegroundColor = NewColor;
            }

            public void Dispose()
            {
                Console.ForegroundColor = m_OriginalColor;
            }

            private ConsoleColor m_OriginalColor;
        }
    }
}
