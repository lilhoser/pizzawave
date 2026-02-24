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
using static pizzalib.TraceLogger;

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
                // Don't call base.Initialize() - we don't want to create a new capture folder
                // Just initialize whisper for transcription
                m_Whisper = new Whisper(Settings);
                _ = await m_Whisper.Initialize();
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
            if (!m_Initialized)
            {
                throw new Exception("Offline call manager not initialized");
            }

            //
            // Instead of blocking on StreamServer which invokes HandleNewCall
            // whenever the live server sends a call, we process the list of
            // offline call records immediately and return.
            //
            try
            {
                await Task.Run(async () =>
                {
                    m_Settings!.UpdateProgressLabelCallback?.Invoke($"Reading folder {m_OfflineFilesPath}...");

                    // Try to load .bin files first (SFTP backup format)
                    var targets = Directory.GetFiles(
                        m_OfflineFilesPath, "*.bin", SearchOption.AllDirectories).ToList();

                    // If no .bin files, try .mp3 files (live capture format)
                    if (targets.Count == 0)
                    {
                        targets = Directory.GetFiles(
                            m_OfflineFilesPath, "*.mp3", SearchOption.AllDirectories).ToList();
                    }

                    m_Settings.SetProgressBarCallback?.Invoke(targets.Count, 1);
                    if (targets.Count == 0)
                    {
                        throw new Exception("No call records found. Supported formats:\n• .mp3 files (from live captures)\n• .bin files (from Trunk-Recorder SFTP backup)");
                    }
                    var i = 1;
                    foreach (var file in targets)
                    {
                        if (CancelSource!.IsCancellationRequested)
                        {
                            break;
                        }
                        m_Settings.ProgressBarStepCallback?.Invoke();
                        m_Settings.UpdateProgressLabelCallback?.Invoke(
                            $"Loading offline records from {m_OfflineFilesPath}...{i++} of {targets.Count}");

                        if (file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                        {
                            // Process .bin file (SFTP backup format)
                            using (var stream = new MemoryStream(File.ReadAllBytes(file)))
                            {
                                var wavStream = new RawCallData(m_Settings);
                                try
                                {
                                    var cancelSource = new CancellationTokenSource();
                                    var result = await wavStream.ProcessClientData(stream, cancelSource);
                                    if (result)
                                    {
                                        await HandleNewCall(wavStream);
                                    }
                                }
                                finally
                                {
                                    wavStream.Dispose(); // Release PCM buffer
                                }
                            }
                        }
                        else if (file.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                        {
                            // Process .mp3 file (live capture format) - create minimal call from filename
                            await LoadMp3File(file);
                        }
                    }
                });
            }
            catch (OperationCanceledException ex)
            {
                Trace(TraceLoggerType.OfflineCallManager, TraceEventType.Warning,
                    $"Offline load cancelled: {ex.Message}");
                // Don't rethrow - allow partial results
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.OfflineCallManager, TraceEventType.Error,
                    $"Failed to load offline captures: {ex.Message}");
                throw;
            }
            return true;
        }

        private async Task LoadMp3File(string mp3Path)
        {
            try
            {
                // Extract metadata from filename: audio-2026-02-18-210053.25.mp3
                var fileName = Path.GetFileNameWithoutExtension(mp3Path);
                var match = System.Text.RegularExpressions.Regex.Match(fileName, @"audio-(\d{4}-\d{2}-\d{2}-\d{6})\.(\d+)");

                var call = new TranscribedCall();
                call.UniqueId = Guid.NewGuid();
                call.Location = mp3Path;
                call.Transcription = string.Empty; // Default to empty string

                if (match.Success)
                {
                    // Parse timestamp from filename
                    var timestampStr = match.Groups[1].Value;
                    if (DateTime.TryParseExact(timestampStr, "yyyy-MM-dd-HHmmss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var timestamp))
                    {
                        var startTime = new DateTimeOffset(timestamp).ToUnixTimeSeconds();
                        call.StartTime = startTime;
                        call.StopTime = startTime + 30; // Default 30 second duration
                    }
                }
                else
                {
                    // Use file modification time as fallback
                    var fileInfo = new FileInfo(mp3Path);
                    var startTime = new DateTimeOffset(fileInfo.LastWriteTime).ToUnixTimeSeconds();
                    call.StartTime = startTime;
                    call.StopTime = startTime + 30;
                }

                // Try to load transcription from calljournal.json if it exists
                var journalPath = Path.Combine(m_OfflineFilesPath, "calljournal.json");
                if (File.Exists(journalPath))
                {
                    try
                    {
                        var journalLines = await File.ReadAllLinesAsync(journalPath);
                        var mp3FileName = Path.GetFileName(mp3Path);
                        
                        foreach (var line in journalLines)
                        {
                            // Match by filename in the Location field
                            if (line.Contains(mp3FileName))
                            {
                                var journalCall = Newtonsoft.Json.JsonConvert.DeserializeObject<TranscribedCall>(line);
                                if (journalCall != null)
                                {
                                    call.Transcription = journalCall.Transcription ?? string.Empty;
                                    call.Talkgroup = journalCall.Talkgroup;
                                    call.Frequency = journalCall.Frequency;
                                    call.CallId = journalCall.CallId;
                                    call.IsAlertMatch = journalCall.IsAlertMatch;
                                    call.IsPinned = journalCall.IsPinned;
                                    break;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Trace(TraceLoggerType.OfflineCallManager, TraceEventType.Warning,
                            $"Failed to read journal {journalPath}: {ex.Message}");
                    }
                }

                // Ensure transcription is never null
                if (string.IsNullOrEmpty(call.Transcription))
                {
                    call.Transcription = "[No transcription available]";
                }

                // Process alerts
                ProcessAlerts(call);

                // Notify the UI
                m_NewTranscribedCallCallback?.Invoke(call);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.OfflineCallManager, TraceEventType.Error,
                    $"Failed to load MP3 file {mp3Path}: {ex.Message}");
            }
        }

        public override void Stop(bool block = true)
        {
            if (CancelSource == null)
            {
                return;
            }

            base.Stop(block);

            if (block)
            {
                CancelSource!.Cancel();
            }
            else
            {
                CancelSource!.CancelAsync();
            }
        }

        protected override Task<bool> Reinitialize(Settings Settings)
        {
            throw new Exception("OfflineCallManager does not support re-initialization");
        }

        protected override Task HandleNewCall(RawCallData CallData)
        {
            if (!m_Initialized)
            {
                throw new Exception("OfflineCallManager not initialized");
            }

            // For offline mode, we just transcribe and notify - don't save files or write journal
            return ProcessOfflineCall(CallData);
        }

        private async Task ProcessOfflineCall(RawCallData CallData)
        {
            try
            {
                // Transcribe the call
                var call = await GetTranscribedCall(CallData);

                // Process alerts (visual only, no emails)
                ProcessAlerts(call);

                // Notify the UI
                m_NewTranscribedCallCallback?.Invoke(call);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.OfflineCallManager, TraceEventType.Error, $"{ex.Message}");
                throw;
            }
        }

        protected override void ProcessAlerts(TranscribedCall Call)
        {
            //
            // Alerts are processed in the sense that if they match an alert, it
            // is noted in the TranscribedCall object - no actual alerts are sent
            // in terms of emails, phone notifications, etc.
            //
            Alerter.ProcessAlertsOffline(Call, m_Settings!.Alerts);
            base.ProcessAlerts(Call);
        }
    }
}
