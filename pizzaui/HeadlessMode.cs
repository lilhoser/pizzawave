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
using pizzalib;

namespace pizzaui
{
    using static TraceLogger;

    internal class HeadlessMode : StandaloneClient
    {
        public HeadlessMode() : base()
        {
            m_CallManager = new LiveCallManager(NewCallTranscribed);
        }

        protected override void PrintUsage(string Message = null)
        {
            if (!string.IsNullOrEmpty(Message))
            {
                Trace(TraceLoggerType.Headless, TraceEventType.Error, Message);
            }
            Trace(TraceLoggerType.Headless,
                  TraceEventType.Information,
                  "Usage: pizzawave.exe --headless [--settings=<path>]");
        }

        protected override void NewCallTranscribed(TranscribedCall Call)
        {
            Trace(TraceLoggerType.Headless, TraceEventType.Information, $"{Call.ToString(m_Settings!)}");
        }

        public override async Task<int> Run(string[] Args)
        {
            //
            // Redirect console output to parent process
            //
            using (var console = new WinConsole())
            {
                console.Initialize(false);
                TraceLogger.Initialize(true);
                pizzalib.TraceLogger.Initialize(true);
                Trace(TraceLoggerType.Headless, TraceEventType.Information, "");
                Trace(TraceLoggerType.Headless, TraceEventType.Information, "Pizzawave Headless Mode.");
                Trace(TraceLoggerType.Headless, TraceEventType.Information, "Console initialized");

                var args = new List<string>();
                foreach (var arg in Args)
                {
                    if (arg.ToLower().StartsWith("--headless") ||
                        arg.ToLower().StartsWith("-headless"))
                    {
                        continue;
                    }
                    args.Add(arg);
                }

                var result = await base.Run(args.ToArray());
                TraceLogger.Shutdown();
                pizzalib.TraceLogger.Shutdown();
                return result;
            }
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
