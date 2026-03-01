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
        private WhisperProcessor? m_Processor;
        private readonly SemaphoreSlim m_ProcessorLock = new SemaphoreSlim(1, 1);

        public Whisper(Settings Settings)
        {
            m_Initialized = false;
            m_ModelFile = string.Empty;
            m_Settings = Settings;
        }
        
        // Removed finalizer - native resources must be explicitly disposed
        // Finalizers on Linux can cause segfaults when cleaning up native libraries

        protected virtual void Dispose(bool disposing)
        {
            Trace(TraceLoggerType.Whisper, TraceEventType.Information, "Model disposed.");

            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_Initialized = false;
            m_Processor?.Dispose();
            m_Factory?.Dispose();
            m_ProcessorLock?.Dispose();
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
                var modelMap = new Dictionary<string, (string filename, GgmlType type)>(StringComparer.OrdinalIgnoreCase)
                {
                    { "large-v3", ("ggml-large-v3.bin", GgmlType.LargeV3) },
                    { "medium", ("ggml-medium.bin", GgmlType.Medium) },
                    { "small", ("ggml-small.bin", GgmlType.Small) },
                    { "tiny", ("ggml-tiny.bin", GgmlType.Tiny) }
                };

                string modelFilename = "ggml-base.bin";
                GgmlType modelType = GgmlType.Base;

                if (!string.IsNullOrEmpty(m_Settings.whisperModelFile))
                {
                    var configuredModel = Path.GetFileName(m_Settings.whisperModelFile);
                    foreach (var kvp in modelMap)
                    {
                        if (configuredModel.Contains(kvp.Key))
                        {
                            modelFilename = kvp.Value.filename;
                            modelType = kvp.Value.type;
                            Trace(TraceLoggerType.Whisper, TraceEventType.Information,
                                  $"Attempting to download configured model: {modelFilename}");
                            break;
                        }
                    }
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
                        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelType).ConfigureAwait(false);
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

            //
            // Create a persistent processor. This keeps the Whisper thread pool
            // alive but stable, avoiding the overhead of creating/destroying
            // processors for each call (which was causing high CPU on Linux).
            // The processor will be reused for all transcriptions.
            //
            m_Processor = m_Factory.CreateBuilder()
                .WithLanguage("auto")
                .WithNoContext()
                .WithSingleSegment()
                .Build();

            // Reset semaphore to allow concurrent access (it was created with 1, 1)
            // This is fine since we only allow one wait at a time
            await m_ProcessorLock.WaitAsync();
            m_ProcessorLock.Release();

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
                // Use the persistent processor, protected by a semaphore to prevent
                // concurrent use from multiple threads (which caused transcription bleed).
                await m_ProcessorLock.WaitAsync();
                try
                {
                    var sb = new StringBuilder();
                    await foreach (var result in m_Processor!.ProcessAsync(WavData))
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
                finally
                {
                    m_ProcessorLock.Release();
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
