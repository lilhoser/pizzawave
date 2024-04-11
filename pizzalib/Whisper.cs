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

    public class Whisper : IDisposable
    {
        private string m_ModelFile;
        private readonly string s_ModelFolder = Path.Combine(
            Settings.DefaultWorkingDirectory, "model");
        private bool m_Initialized;
        private Settings m_Settings;
        private bool m_Disposed;
        private WhisperFactory? m_Factory;
        private WhisperProcessor? m_Processor;

        public Whisper(Settings Settings)
        {
            m_Initialized = false;
            m_ModelFile = string.Empty;
            m_Settings = Settings;
        }

        ~Whisper()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            Trace(TraceLoggerType.Whisper, TraceEventType.Information, "Model disposed.");

            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_Initialized = false;
            m_Factory?.Dispose();
            m_Processor?.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
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

            //
            // Build the factory & processor once! This consumes MASSIVE memory depending on model.
            //
            try
            {
                m_Factory = WhisperFactory.FromPath(m_ModelFile);
                m_Processor = m_Factory.CreateBuilder().WithLanguage("auto").Build();
            }
            catch (Exception ex)
            {
                var err = $"Failed to build processor/factory: {ex.Message}";
                Trace(TraceLoggerType.Whisper, TraceEventType.Error, err);
                throw new Exception(err);
            }

            m_Initialized = true;
            m_Settings.UpdateProgressLabelCallback?.Invoke("Whisper initialized.");
            Trace(TraceLoggerType.Whisper, TraceEventType.Information, "Model initialized.");
            return true;
        }

        public async Task<string> TranscribeCall(MemoryStream WavData)
        {
            if (!m_Initialized || m_Processor == null)
            {
                throw new Exception("Whisper model is not initialized.");
            }           

            try
            {
                var sb = new StringBuilder();
                await foreach (var result in m_Processor.ProcessAsync(WavData))
                {
                    sb.Append($"{result.Text} ");
                }
                var transcription = sb.ToString();
                if (string.IsNullOrEmpty(transcription))
                {
                    var err = $"Transcription was empty";
                    Trace(TraceLoggerType.Whisper, TraceEventType.Error, err);
                }
                else
                {
                    var length = Math.Min(25, transcription.Length);
                    var snippet = transcription.Substring(0, length);
                    Trace(TraceLoggerType.Whisper,
                          TraceEventType.Verbose,
                          $"Transcription: \"{snippet}...\"");
                    return transcription;
                }
            }
            catch (Exception ex)
            {
                var err = $"Failed to transcribe WAV data: {ex.Message}";
                Trace(TraceLoggerType.Whisper, TraceEventType.Error, err);
                throw new Exception(err);
            }

            return string.Empty;
        }
    }
}
