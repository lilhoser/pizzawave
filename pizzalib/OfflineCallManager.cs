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
using Newtonsoft.Json;

namespace pizzalib
{
    public class OfflineCallManager : CallManager, IDisposable
    {
        private string m_OfflineFilesPath;
        private bool m_Initialized;
        private bool m_Disposed;
        private Settings? m_Settings;
        private CancellationTokenSource? CancelSource;

        public OfflineCallManager(
            string OfflineFilesPath,
            Action<TranscribedCall> newTranscribedCallCallback) : base(newTranscribedCallCallback)
        {
            m_OfflineFilesPath = OfflineFilesPath;
            m_Initialized = false;
        }

        ~OfflineCallManager()
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
            Trace(TraceLoggerType.OfflineCallManager, TraceEventType.Information, "OfflineCallManager disposed.");

            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            base.Dispose(disposing);
        }

        public override async Task<bool> Initialize(Settings Settings)
        {
            m_Settings = Settings;
            if (m_Initialized)
            {
                return await Reinitialize(Settings);
            }

            try
            {
                CancelSource = new CancellationTokenSource();
                await base.Initialize(Settings);
                m_Initialized = true;
            }
            catch (Exception ex)
            {
                var err = $"Unable to initialize offline call manager: {ex.Message}";
                throw new Exception(err);
            }
            return true;
        }

        public void DeleteCapture()
        {
            base.DeleteCaptureRoot();
        }

        public override bool IsStarted()
        {
            if (!m_Initialized || !base.IsStarted())
            {
                return false;
            }
            return true;
        }

        public override async Task<bool> Start(bool block = false)
        {
            await base.Start(block);

            if (!m_Initialized)
            {
                throw new Exception("Offline call manager not initialized");
            }

            //
            // Instead of blocking on StreamServer which invokes HandleNewCall
            // whenever the live server sends a call, we process the list of
            // offline call records immediately and return.
            //
            await Task.Run(async () =>
            {
                m_Settings.UpdateProgressLabelCallback?.Invoke($"Reading folder {m_OfflineFilesPath}...");
                var targets = Directory.GetFiles(
                    m_OfflineFilesPath, "*.bin", SearchOption.AllDirectories).ToList();
                m_Settings.SetProgressBarCallback?.Invoke(targets.Count, 1);
                if (targets.Count == 0)
                {
                    throw new Exception("No call records found. Make sure this is an SFTP offline backup.");
                }
                var i = 1;
                foreach (var file in targets)
                {
                    if (CancelSource.IsCancellationRequested)
                    {
                        break;
                    }
                    m_Settings.ProgressBarStepCallback?.Invoke();
                    m_Settings.UpdateProgressLabelCallback?.Invoke(
                        $"Loading offline records from {m_OfflineFilesPath}...{i++} of {targets.Count}");
                    using (var stream = new MemoryStream(File.ReadAllBytes(file)))
                    {
                        var wavStream = new WavStreamData(m_Settings);
                        var cancelSource = new CancellationTokenSource();
                        var result = await wavStream.ProcessClientData(stream, cancelSource);
                        if (result)
                        {
                            await HandleNewCall(wavStream);
                        }
                    }
                }
            });
            return true;
        }

        public override void Stop(bool block = true)
        {
            base.Stop(block);

            if (block)
            {
                CancelSource.Cancel();
            }
            else
            {
                CancelSource.CancelAsync();
            }
        }

        protected override async Task<bool> Reinitialize(Settings Settings)
        {
            throw new Exception("OfflineCallManager does not support re-initialization");
        }

        protected override Task HandleNewCall(WavStreamData CallData)
        {
            if (!m_Initialized)
            {
                throw new Exception("OfflineCallManager not initialized");
            }

            return base.HandleNewCall(CallData);
        }

        protected override void ProcessAlerts(TranscribedCall Call)
        {
            //
            // Alerts are processed in the sense that if they match an alert, it
            // is noted in the TranscribedCall object - no actual alerts are sent
            // in terms of emails, phone notifications, etc.
            //
            Alerter.ProcessAlertsOffline(Call, m_Settings.Alerts);
            base.ProcessAlerts(Call);
        }
    }
}
