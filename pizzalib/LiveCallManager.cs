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
using static pizzalib.TraceLogger;
using System.Diagnostics;

namespace pizzalib
{
    public class LiveCallManager : CallManager, IDisposable
    {
        private Alerter? m_Alerter;
        private StreamServer? m_StreamServer;
        private bool m_Disposed;

        public LiveCallManager(Action<TranscribedCall> newTranscribedCallCallback) : base(newTranscribedCallCallback)
        {
        }

        ~LiveCallManager()
        {
            Dispose(false);
        }

        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            base.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            Trace(TraceLoggerType.LiveCallManager, TraceEventType.Information, "LiveCallManager disposed.");

            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_StreamServer?.Dispose();
            // Note: base.Dispose() will handle m_Whisper disposal
        }

        public override async Task<bool> Initialize(Settings Settings)
        {
            if (IsStarted())
            {
                return await Reinitialize(Settings);
            }

            try
            {
                await base.Initialize(Settings);
                m_Alerter = new Alerter(Settings);
                m_StreamServer = new StreamServer(HandleNewCall, Settings);
            }
            catch (Exception ex)
            {
                var err = $"Unable to initialize live call manager: {ex.Message}";
                throw new Exception(err);
            }
            return true;
        }

        public override bool IsStarted()
        {
            if (!base.IsStarted() || m_StreamServer == null)
            {
                return false;
            }
            return m_StreamServer.IsStarted();
        }

        public override async Task<bool> Start(bool block = false)
        {
            await base.Start(block);

            if (!m_Initialized || m_StreamServer == null)
            {
                throw new Exception("Live call manager not initialized");
            }
            if (m_StreamServer.IsStarted())
            {
                throw new Exception("Live call manager already started");
            }

            if (!block)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        _ = await m_StreamServer.Listen();
                    }
                    catch (Exception ex)
                    {
                        Trace(TraceLoggerType.LiveCallManager, TraceEventType.Error, $"{ex.Message}");
                    }
                });
                return true;
            }
            else
            {
                try
                {
                    return await m_StreamServer.Listen();
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.LiveCallManager, TraceEventType.Error, $"{ex.Message}");
                    return false;
                }
            }
        }

        public override void Stop(bool block = true)
        {
            base.Stop(block);

            if (!m_Initialized)
            {
                return;
            }
            if (m_StreamServer == null)
            {
                return;
            }

            //
            // Shutdown the listener socket
            //
            m_StreamServer.Shutdown(block: block);
        }

        protected override async Task<bool> Reinitialize(Settings Settings)
        {
            if (!IsStarted())
            {
                throw new Exception("Invalid LiveCallManager state for re-initialize");
            }

            try
            {
                await base.Reinitialize(Settings);
                m_StreamServer?.Dispose();
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.LiveCallManager, TraceEventType.Error, $"{ex.Message}");
                return false;
            }

            return await Initialize(Settings);
        }

        protected override async Task HandleNewCall(RawCallData CallData)
        {
            if (!m_Initialized || m_StreamServer == null || m_Alerter == null)
            {
                throw new Exception("LiveCallManager not initialized");
            }

            //
            // This routine is a callback invoked from a worker thread in StreamServer.cs
            // It is safe/OK to perform blocking calls here.
            // NOTE: This method is invoked PER CALL, and calls can happen in parallel.
            //

            await base.HandleNewCall(CallData);
        }

        protected override void ProcessAlerts(TranscribedCall Call)
        {
            m_Alerter?.ProcessAlerts(Call);
            base.ProcessAlerts(Call);
        }
    }
}
