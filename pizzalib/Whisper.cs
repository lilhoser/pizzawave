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
using Whisper.net.Logger;

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

        public Whisper(Settings Settings)
        {
            m_Initialized = false;
            m_ModelFile = string.Empty;
            m_Settings = Settings;
        }

        private IDisposable? m_LoggerSubscription;

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
            m_LoggerSubscription?.Dispose();
            m_Factory?.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public async Task<bool> Initialize()
        {
            m_Settings.UpdateProgressLabelCallback?.Invoke("Initializing Whisper model...");

            m_LoggerSubscription = LogProvider.AddLogger(delegate (WhisperLogLevel arg1, string? arg2) {
                // Only log errors and warnings after initial setup
                if (!m_Initialized ||
                    arg1 == WhisperLogLevel.Error ||
                    arg1 == WhisperLogLevel.Warning)
                {
                    Trace(TraceLoggerType.Whisper,
                          TraceEventType.Information,
                          $"{arg2}");
                }
            });

            if (!string.IsNullOrEmpty(m_Settings.whisperModelFile))
            {
                m_ModelFile = m_Settings.whisperModelFile;
                // Check if configured model exists
                if (!File.Exists(m_ModelFile))
                {
                    Trace(TraceLoggerType.Whisper, TraceEventType.Warning,
                          $"Configured model file not found: {m_ModelFile}");
                    // Fall through to auto-download logic
                    m_ModelFile = string.Empty;
                }
            }
            
            if (string.IsNullOrEmpty(m_ModelFile))
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

                // Determine model type from filename or default to base
                string modelFilename = "ggml-base.bin";
                GgmlType modelType = GgmlType.Base;
                
                // If user had a configured model, try to download that type
                if (!string.IsNullOrEmpty(m_Settings.whisperModelFile))
                {
                    var configuredModel = Path.GetFileName(m_Settings.whisperModelFile);
                    if (configuredModel.Contains("large-v3"))
                    {
                        modelFilename = configuredModel;
                        modelType = GgmlType.LargeV3;
                    }
                    else if (configuredModel.Contains("medium"))
                    {
                        modelFilename = configuredModel;
                        modelType = GgmlType.Medium;
                    }
                    else if (configuredModel.Contains("small"))
                    {
                        modelFilename = configuredModel;
                        modelType = GgmlType.Small;
                    }
                    else if (configuredModel.Contains("tiny"))
                    {
                        modelFilename = configuredModel;
                        modelType = GgmlType.Tiny;
                    }
                    // else keep default base model
                    Trace(TraceLoggerType.Whisper, TraceEventType.Information,
                          $"Attempting to download configured model: {modelFilename}");
                }
                
                m_ModelFile = Path.Combine(s_ModelFolder, modelFilename);

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
                        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelType);
                        using var fileWriter = File.OpenWrite(m_ModelFile);
                        await modelStream.CopyToAsync(fileWriter);
                        Trace(TraceLoggerType.Whisper, TraceEventType.Information,
                              $"Model download complete: {m_ModelFile}");
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
            // Build the factory once! This consumes MASSIVE memory depending on model.
            // Individual transcription processors are created frequently, per-call later.
            //
            try
            {
                m_Factory = WhisperFactory.FromPath(m_ModelFile);
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
            if (!m_Initialized || m_Factory == null)
            {
                throw new Exception("Whisper model is not initialized.");
            }           

            try
            {
                using var processor = m_Factory.CreateBuilder().
                    WithLanguage("auto").
                    WithNoContext().
                    WithSingleSegment().
                    Build();
                var sb = new StringBuilder();
                await foreach (var result in processor.ProcessAsync(WavData))
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
