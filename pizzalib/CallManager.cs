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
using FFMpegCore;
using FFMpegCore.Extensions.Downloader;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static pizzalib.TraceLogger;

namespace pizzalib
{
    public abstract class CallManager : IDisposable
    {
        protected Whisper? m_Whisper;
        private bool m_Initialized;
        private bool m_Disposed;
        private StreamWriter? m_JournalFile;
        private string m_CaptureRoot;
        public static readonly string s_CallJournalFile = "calljournal.json";
        protected Action<TranscribedCall> m_NewTranscribedCallCallback;
        private bool _ffmpegReady = false;
        private static readonly SemaphoreSlim _ffmpegSemaphore = new SemaphoreSlim(1, 1);
        private static string _ffmpegBinFolder = Path.Combine(AppContext.BaseDirectory, "ffmpeg-bin");

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

            // Clean up FFmpeg on Linux to prevent core dump on exit
            // FFmpeg can leave behind processes or shared resources that cause crashes
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    // Force FFmpeg to clean up any remaining processes
                    GlobalFFOptions.Configure(options => options.BinaryFolder = string.Empty);
                    Trace(TraceLoggerType.CallManager, TraceEventType.Information, "FFmpeg cleanup performed on Linux");
                }
                catch
                {
                    // Swallow cleanup errors
                }
            }

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
                m_JournalFile = new StreamWriter(journal) { AutoFlush = true };

                //
                // Initialize whisper
                //
                m_Whisper = new Whisper(Settings);
                _ = await m_Whisper.Initialize();
                m_Initialized = true;

                //
                // Download ffmpeg if needed
                //
                await EnsureFfmpegExists();
            }
            catch (Exception ex)
            {
                var err = $"Unable to initialize call manager: {ex.Message}";
                throw new Exception(err);
            }
            return true;
        }

        private async Task EnsureFfmpegExists()
        {
            if (_ffmpegReady) return;
            await _ffmpegSemaphore.WaitAsync();
            try
            {
                if (_ffmpegReady)
                    return;
                if (File.Exists(Path.Combine(_ffmpegBinFolder, "ffmpeg.exe")))
                {
                    GlobalFFOptions.Configure(options => options.BinaryFolder = _ffmpegBinFolder);
                    _ffmpegReady = true;
                    Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                          "FFmpeg binaries already exist in ffmpeg-bin folder");
                    return;
                }
                if (await Utilities.IsFfmpegInPathAsync())
                {
                    GlobalFFOptions.Configure(options => options.BinaryFolder = string.Empty); // use PATH
                    _ffmpegReady = true;
                    Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                          "Using system FFmpeg from PATH (no download needed)");
                    return;
                }
                Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                      "Downloading FFmpeg (~80 MB, one-time only)...");
                GlobalFFOptions.Configure(options => options.BinaryFolder = _ffmpegBinFolder);
                string ffmpegExe = Path.Combine(_ffmpegBinFolder, "ffmpeg.exe");
                if (!File.Exists(ffmpegExe))
                {
                    Directory.CreateDirectory(_ffmpegBinFolder);
                    await FFMpegDownloader.DownloadBinaries();
                }

                Trace(TraceLoggerType.CallManager, TraceEventType.Information, "FFmpeg download complete.");
                _ffmpegReady = true;
            }
            finally
            {
                _ffmpegSemaphore.Release();
            }
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
            return await Task.FromResult(true);
        }

        public virtual void Stop(bool block = true)
        {
            m_JournalFile?.Close();
            m_JournalFile?.Dispose();
            m_JournalFile = null;
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

        protected virtual async Task HandleNewCall(RawCallData CallData)
        {
            try
            {
                //
                // Get the transcribed call info
                //
                var call = await GetTranscribedCall(CallData);

                //
                // Write the call mp3 to disk first (so it's available for alert emails)
                //
                var fileName = $"audio-{DateTime.Now:yyyy-MM-dd-HHmmss.ff}.mp3";
                await CallData.DumpStreamToFile(m_CaptureRoot, fileName, OutputFileFormat.Mp3);
                call.Location = Path.Combine(m_CaptureRoot, fileName);

                //
                // Send any alerts on the transcription and update call meta data with matches.
                //
                ProcessAlerts(call);

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
            finally
            {
                // Dispose RawCallData to release large PCM buffer (prevents memory leak)
                CallData?.Dispose();
            }
        }

        protected virtual void ProcessAlerts(TranscribedCall Call)
        {
            return;
        }

        protected async Task<TranscribedCall> GetTranscribedCall(RawCallData CallData)
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
                // Transcribe the raw audio (Whisper requires 16KHz)
                //
                using (var audioStream = await CallData.GetAudioStreamForWhisperAsync())
                {
                    call.Transcription = await m_Whisper.TranscribeCall(audioStream);
                }
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
