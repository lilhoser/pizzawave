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
    public abstract class CallManager : IDisposable
    {
        private Whisper? m_Whisper;
        private bool m_Initialized;
        private bool m_Disposed;
        private StreamWriter? m_JournalFile;
        private string m_CaptureRoot;
        public static readonly string s_CallJournalFile = "calljournal.json";
        private Action<TranscribedCall> m_NewTranscribedCallCallback;

        public CallManager(Action<TranscribedCall> NewTranscribedCallCallback)
        {
            m_NewTranscribedCallCallback = NewTranscribedCallCallback;
            m_CaptureRoot = Path.Combine(
                Settings.DefaultLiveCaptureDirectory, $"{DateTime.Now:yyyy-MM-dd-HHmmss}");
            m_Initialized = false;
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
            m_JournalFile?.Dispose();
        }

        public virtual void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public virtual async Task<bool> Initialize(Settings Settings)
        {
            if (m_Initialized)
            {
                return true;
            }

            try
            {
                //
                // Create capture folder and journal for this session
                //
                Directory.CreateDirectory(m_CaptureRoot);
                string name = new DirectoryInfo(m_CaptureRoot).Name;
                var journal = Path.Join(m_CaptureRoot, s_CallJournalFile);
                m_JournalFile = new StreamWriter(journal);

                //
                // Initialize whisper
                //
                m_Whisper = new Whisper(Settings);
                _ = await m_Whisper.Initialize();
                m_Initialized = true;
            }
            catch (Exception ex)
            {
                var err = $"Unable to initialize call manager: {ex.Message}";
                throw new Exception(err);
            }
            return true;
        }

        protected void DeleteCaptureRoot()
        {
            if (!m_Initialized)
            {
                return;
            }
            m_JournalFile?.Dispose();
            Directory.Delete(m_CaptureRoot, true);
        }

        public virtual bool IsStarted()
        {
            return m_Initialized;
        }

        public virtual async Task<bool> Start(bool block = false)
        {
            return true;
        }

        public virtual async void Stop(bool block = true)
        {
            m_JournalFile?.Close();
        }

        protected virtual async Task<bool> Reinitialize(Settings Settings)
        {
            if (!m_Initialized)
            {
                throw new Exception("Invalid CallManager state for re-initialize");
            }

            m_Initialized = false;

            try
            {
                m_Whisper?.Dispose();
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.CallManager, TraceEventType.Error, $"{ex.Message}");
                return false;
            }

            return await Initialize(Settings);
        }

        protected virtual async Task HandleNewCall(WavStreamData CallData)
        {
            try
            {
                //
                // Get the transcribed call info
                //
                var call = await GetTranscribedCall(CallData);

                //
                // Send any alerts on the transcription and update call meta data with matches.
                //
                ProcessAlerts(call);

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
                m_NewTranscribedCallCallback?.Invoke(call);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.LiveCallManager, TraceEventType.Error, $"{ex.Message}");
                throw; // back up to worker thread
            }
        }

        protected virtual void ProcessAlerts(TranscribedCall Call)
        {
            return;
        }

        private async Task<TranscribedCall> GetTranscribedCall(WavStreamData CallData)
        {
            if (!m_Initialized || m_Whisper == null)
            {
                throw new Exception("CallManager not initialized");
            }

            var call = new TranscribedCall();
            call.UniqueId = Guid.NewGuid();

            try
            {
                var jsonObject = CallData.GetJsonObject();
                call.StopTime = jsonObject["StopTime"]!.ToObject<long>()!;
                call.StartTime = jsonObject["StartTime"]!.ToObject<long>()!;
                call.CallId = jsonObject["CallId"]!.ToObject<long>()!;
                call.Source = jsonObject["Source"]!.ToObject<int>()!;
                call.Talkgroup = jsonObject["Talkgroup"]!.ToObject<long>()!;
                call.PatchedTalkgroups = jsonObject["PatchedTalkgroups"]!.ToObject<List<long>>()!;
                call.Frequency = jsonObject["Frequency"]!.ToObject<double>()!;
                call.SystemShortName = jsonObject["SystemShortName"]!.ToObject<string>()!;
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
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.CallManager, TraceEventType.Error, $"{ex.Message}");
                throw; // back up to worker thread
            }

            return call;
        }
    }
}
