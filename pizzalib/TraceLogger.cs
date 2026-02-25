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
        private static ConsoleTraceListener m_ConsoleTraceListener = new ConsoleTraceListener();
        private static SourceSwitch m_Switch =
            new SourceSwitch("pizzalibSwitch", "Verbose");
        // Log rotation settings
        private static readonly long m_MaxLogFileSize = 10 * 1024 * 1024; // 10MB
        private static readonly int m_MaxLogFiles = 5;
        private static readonly TimeSpan m_RotationCheckInterval = TimeSpan.FromSeconds(5);
        private static DateTime m_LastRotationCheck = DateTime.MinValue;
        private static long m_LastFileLength = 0;
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
                if (RedirectToStdout)
                {
                    source.Listeners.Add(m_ConsoleTraceListener);
                }
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

            // Structured logging format: timestamp|level|type|message
            var structuredMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}|{(int)EventType}|{Type}|{Message}";
            using (GetColorContext(EventType))
            {
                Sources[(int)Type].TraceEvent(EventType, 1, structuredMessage);
            }

            // Rotate log file if needed (only check every few seconds to reduce I/O overhead)
            MaybeRotateLogFile();
        }

        public static void OpenTraceLog()
        {
            Utilities.LaunchFile(m_Location);
        }

        private static void MaybeRotateLogFile()
        {
            // Only check for log rotation every few seconds to reduce I/O overhead
            // This is critical for Linux/RPI where file system operations are slower
            var now = DateTime.Now;
            if ((now - m_LastRotationCheck) < m_RotationCheckInterval)
            {
                return;
            }

            m_LastRotationCheck = now;

            try
            {
                if (File.Exists(m_Location))
                {
                    var fileInfo = new FileInfo(m_Location);
                    // Only rotate if file has grown since last check
                    if (fileInfo.Length > m_MaxLogFileSize && fileInfo.Length != m_LastFileLength)
                    {
                        RotateLogFiles();
                        m_Location = Path.Combine(m_TraceFileDir,
                            $"pizzalib-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss")}.txt");
                        m_TextWriterTraceListener = new TextWriterTraceListener(m_Location, "pizzalibTextWriterListener");
                        foreach (var source in Sources)
                        {
                            source.Listeners.Add(m_TextWriterTraceListener);
                        }
                    }
                    m_LastFileLength = fileInfo.Length;
                }
            }
            catch
            {
                // Swallow rotation errors
            }
        }

        private static void RotateLogFiles()
        {
            try
            {
                var dir = new DirectoryInfo(m_TraceFileDir);
                var logFiles = dir.GetFiles("pizzalib-*.txt")
                    .OrderByDescending(f => f.CreationTime)
                    .Skip(m_MaxLogFiles - 1)
                    .ToArray();

                foreach (var file in logFiles)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch
                    {
                        // Swallow deletion errors
                    }
                }
            }
            catch
            {
                // Swallow rotation errors
            }
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
                    return new ColorContext(ConsoleColor.DarkMagenta);
                case TraceEventType.Transfer:
                    return new ColorContext(ConsoleColor.DarkYellow);
                default:
                    return new ColorContext();
            }
        }
    }

    internal sealed class ColorContext : IDisposable
    {
        private readonly ConsoleColor previousBackgroundColor;
        private readonly ConsoleColor previousForegroundColor;
        private bool isDisposed;

        public ColorContext()
            : this(Console.ForegroundColor, Console.BackgroundColor)
        {
        }

        public ColorContext(ConsoleColor foregroundColor)
            : this(foregroundColor, Console.BackgroundColor)
        {
        }

        public ColorContext(ConsoleColor foregroundColor, ConsoleColor backgroundColor)
        {
            this.isDisposed = false;
            this.previousForegroundColor = Console.ForegroundColor;
            this.previousBackgroundColor = Console.BackgroundColor;
            Console.ForegroundColor = foregroundColor;
            Console.BackgroundColor = backgroundColor;
        }

        public void Dispose()
        {
            if (this.isDisposed)
            {
                return;
            }

            Console.ForegroundColor = this.previousForegroundColor;
            Console.BackgroundColor = this.previousBackgroundColor;
            this.isDisposed = true;
        }
    }
}
