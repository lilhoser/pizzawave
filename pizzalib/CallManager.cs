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
using Newtonsoft.Json;
using System.Diagnostics;

namespace pizzalib
{
    public class CallManager : IDisposable
    {
        private Whisper? m_Whisper;
        private Alerter? m_Alerter;
        private StreamServer? m_StreamServer;
        private bool m_Initialized;
        private bool m_Disposed;
        private Action<TranscribedCall>? NewTranscribedCallCallback;
        private string m_CaptureRoot;
        private StreamWriter? m_JournalFile;
        public static readonly string s_CallJournalFile = "calljournal.json";

        public CallManager(Action<TranscribedCall> newTranscribedCallCallback)
        {
            m_Initialized = false;
            var capture = $"{DateTime.Now:yyyy-MM-dd-HHmmss}";
            m_CaptureRoot = Path.Combine(Settings.DefaultCaptureDirectory, capture);
            NewTranscribedCallCallback = newTranscribedCallCallback;
        }

        ~CallManager()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            Trace(TraceLoggerType.CallManager, TraceEventType.Information, "Call manager disposed.");

            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_Initialized = false;
            m_Whisper?.Dispose();
            m_StreamServer?.Dispose();
            m_JournalFile?.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public async Task<bool> Initialize(Settings Settings)
        {
            if (m_Initialized)
            {
                return await Reinitialize(Settings);
            }

            try
            {
                //
                // Initialize whisper, alerter and streamserver
                //
                m_Whisper = new Whisper(Settings);
                _ = await m_Whisper.Initialize();
                m_Alerter = new Alerter(Settings);
                m_StreamServer = new StreamServer(HandleNewCall, Settings);

                //
                // Create capture folder and journal for this session
                //
                Directory.CreateDirectory(m_CaptureRoot);
                string name = new DirectoryInfo(m_CaptureRoot).Name;
                var journal = Path.Join(m_CaptureRoot, s_CallJournalFile);
                m_JournalFile = new StreamWriter(journal);
                m_Initialized = true;
            }
            catch (Exception ex)
            {
                var err = $"Unable to initialize call manager: {ex.Message}";
                throw new Exception(err);
            }
            return true;
        }

        public bool IsStarted()
        {
            if (!m_Initialized)
            {
                return false;
            }
            if (m_StreamServer == null)
            {
                return false;
            }
            return m_StreamServer.IsStarted();
        }

        public async Task<bool> Start(bool block = false)
        {
            if (!m_Initialized || m_StreamServer == null)
            {
                throw new Exception("Call manager not initialized");
            }
            if (m_StreamServer.IsStarted())
            {
                throw new Exception("Call manager already started");
            }

            if (!block)
            {
                _ = Task.Run(async() =>
                {
                    try
                    {
                        _ = await m_StreamServer.Listen();
                    }
                    catch (Exception ex)
                    {
                        Trace(TraceLoggerType.CallManager, TraceEventType.Error, $"{ex.Message}");
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
                    Trace(TraceLoggerType.CallManager, TraceEventType.Error, $"{ex.Message}");
                    return false;
                }
            }
        }

        public void Stop(bool block = true)
        {
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
            m_StreamServer.Shutdown(block:block);

            //
            // Close the call journal
            //
            m_JournalFile?.Close();
        }

        private async Task<bool> Reinitialize(Settings Settings)
        {
            m_Initialized = false;
            
            try
            {
                m_StreamServer?.Dispose();
                m_Whisper?.Dispose();
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.CallManager, TraceEventType.Error, $"{ex.Message}");
                return false;
            }

            return await Initialize(Settings);
        }

        private async Task HandleNewCall(WavStreamData CallData)
        {
            if (!m_Initialized || m_Whisper == null || m_StreamServer == null || m_Alerter == null)
            {
                throw new Exception("Call manager not initialized");
            }

            //
            // This routine is a callback invoked from a worker thread in StreamServer.cs
            // It is safe/OK to perform blocking calls here.
            // NOTE: This method is invoked PER CALL, and calls can happen in parallel.
            //

            var call = new TranscribedCall();
            call.UniqueId = Guid.NewGuid();

            try
            {
                var jsonObject = CallData.GetJsonObject();
                call.StopTime = jsonObject["StopTime"]!.ToObject<long>();
                call.StartTime = jsonObject["StartTime"]!.ToObject<long>();
                call.CallId = jsonObject["CallId"]!.ToObject<long>();
                call.Source = jsonObject["Source"]!.ToObject<int>();
                call.Talkgroup = jsonObject["Talkgroup"]!.ToObject<long>();
                call.PatchedTalkgroups = jsonObject["PatchedTalkgroups"]!.ToObject<List<long>>();
                call.Frequency = jsonObject["Frequency"]!.ToObject<double>();
                call.SystemShortName = jsonObject["SystemShortName"]!.ToObject<string>();
            }
            catch (Exception ex)
            {
                var err = $"Unable to parse JSON data: {ex.Message}";
                Trace(TraceLoggerType.CallManager, TraceEventType.Error, err);
                throw new Exception(err);
            }
            
            try
            {
                //
                // Transcribe the wav audio
                //
                call.Transcription = await m_Whisper.TranscribeCall(CallData.GetRawStream());
                CallData.RewindStream();

                //
                // Send any alerts on the transcription and update call meta data with matches.
                //
                m_Alerter.ProcessAlerts(call);

                //
                // Write the call mp3 to disk and update call journal.
                //
                var fileName = $"audio-{DateTime.Now:yyyy-MM-dd-HHmmss.ff}.mp3";
                CallData.DumpStreamToFile(m_CaptureRoot, fileName, OutputFileFormat.Mp3);
                call.Location = Path.Combine(m_CaptureRoot, fileName);

                var journalJson = JsonConvert.SerializeObject(call, Formatting.None);
                m_JournalFile?.WriteLine(journalJson);

                //
                // If our instantiator wants to see the transcribed call, notify them.
                //
                NewTranscribedCallCallback?.Invoke(call);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.CallManager, TraceEventType.Error, $"{ex.Message}");
                throw; // back up to worker thread
            }
        }
    }
}
