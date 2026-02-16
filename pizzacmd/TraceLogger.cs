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

namespace pizzacmd
{
    public static class TraceLogger
    {
        public static readonly string m_TraceFileDir = Path.Combine(new string[] {pizzalib.Settings.DefaultWorkingDirectory, "Logs"});
        private static string m_Location = Path.Combine(new string[] { m_TraceFileDir,
                            $"pizzacmd-{DateTime.Now.ToString("yyyy-MM-dd-HHmmss")}.txt"});
        private static TextWriterTraceListener m_TextWriterTraceListener =
            new TextWriterTraceListener(m_Location, "pizzacmdTextWriterListener");
        private static ConsoleTraceListener m_ConsoleTraceListener = new ConsoleTraceListener();
        private static SourceSwitch m_Switch =
            new SourceSwitch("pizzacmdSwitch", "Verbose");
        private static TraceSource[] Sources = {
            new TraceSource("Main", SourceLevels.Verbose),
        };

        public enum TraceLoggerType
        {
            Main,
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
            using (GetColorContext(EventType))
            {
                Sources[(int)Type].TraceEvent(EventType, 1, $"{DateTime.Now:M/d/yyyy h:mm tt}: {Message}");
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
