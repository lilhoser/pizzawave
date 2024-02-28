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
using System.Text;
using Whisper.net.Ggml;
using Whisper.net;

namespace pizzalib
{
    using static TraceLogger;

    public class Whisper
    {
        private string m_ModelFile;
        private readonly string s_ModelFolder = Path.Combine(
            Settings.DefaultWorkingDirectory, "model");
        private bool m_Initialized;
        private Settings m_Settings;

        public Whisper(Settings Settings)
        {
            m_Initialized = false;
            m_ModelFile = string.Empty;
            m_Settings = Settings;
        }

        public async Task<bool> Initialize()
        {
            m_Settings.UpdateProgressLabelCallback?.Invoke("Initializing Whisper model...");

            if (!string.IsNullOrEmpty(m_Settings.whisperModelFile))
            {
                m_ModelFile = m_Settings.whisperModelFile; // nothing to do
            }
            else
            {
                if (!Directory.Exists(s_ModelFolder))
                {
                    try
                    {
                        Directory.CreateDirectory(s_ModelFolder);
                    }
                    catch (Exception ex)
                    {
                        Trace(TraceLoggerType.Whisper,
                              TraceEventType.Error,
                              $"Unable to create model directory " +
                              $"'{s_ModelFolder}': {ex.Message}");
                        throw;
                    }
                }

                m_ModelFile = Path.Combine(s_ModelFolder, "ggml-base.bin");

                if (!File.Exists(m_ModelFile))
                {
                    m_Settings.UpdateProgressLabelCallback?.Invoke("Downloading Whisper model...");
                    m_Settings.SetProgressBarCallback?.Invoke(2, 1);
                    m_Settings.ProgressBarStepCallback?.Invoke();
                    Trace(TraceLoggerType.Whisper,
                          TraceEventType.Information,
                          $"Downloading model file to {m_ModelFile}");
                    try
                    {
                        var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.Base);
                        var fileWriter = File.OpenWrite(m_ModelFile);
                        await modelStream.CopyToAsync(fileWriter);
                    }
                    catch (Exception ex)
                    {
                        Trace(TraceLoggerType.Whisper,
                              TraceEventType.Error,
                              $"Failed to download model {m_ModelFile}: {ex.Message}");
                        throw;
                    }
                    m_Settings.ProgressBarStepCallback?.Invoke();
                }
            }
            m_Initialized = true;
            m_Settings.UpdateProgressLabelCallback?.Invoke("Whisper initialized.");
            return true;
        }

        public async Task<TranscribedCall> TranscribeCall(WavStreamData CallData)
        {
            if (!m_Initialized)
            {
                throw new Exception("Whisper model is not initialized.");
            }

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
                Trace(TraceLoggerType.Whisper, TraceEventType.Error, err);
                throw new Exception(err);
            }            

            try
            {
                using (var whisperFactory = WhisperFactory.FromPath(m_ModelFile))
                {
                    var processor = whisperFactory.CreateBuilder().WithLanguage("auto").Build();
                    var sb = new StringBuilder();
                    await foreach (var result in processor.ProcessAsync(CallData.GetRawStream()))
                    {
                        sb.Append($"{result.Text} ");
                    }
                    call.Transcription = sb.ToString();
                }
            }
            catch (Exception ex)
            {
                var err = $"Failed to transcribe WAV data: {ex.Message}";
                Trace(TraceLoggerType.Whisper, TraceEventType.Error, err);
                throw new Exception(err);
            }

            if (string.IsNullOrEmpty(call.Transcription))
            {
                var err = $"Transcription was empty";
                Trace(TraceLoggerType.Whisper, TraceEventType.Error, err);
                throw new Exception(err);
            }

            var length = Math.Min(25, call.Transcription.Length);
            var snippet = call.Transcription.Substring(0, length);
            Trace(TraceLoggerType.Whisper,
                  TraceEventType.Information,
                  $"Transcribed call id={call.CallId}:  \"{snippet}...\"");
            return call;
        }
    }
}
