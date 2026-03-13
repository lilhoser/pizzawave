using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.IO.Compression;
using Vosk;
using static pizzalib.TraceLogger;

namespace pizzalib
{
    public class VoskTranscriber : ITranscriber
    {
        private readonly Settings m_Settings;
        private readonly SemaphoreSlim m_RecognizerLock = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim s_ModelDownloadLock = new SemaphoreSlim(1, 1);
        private const string DefaultVoskPreset = "vosk-model-small-en-us-0.15";
        private const string DefaultVoskModelZipUrl =
            "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip";
        private Model? m_Model;
        private bool m_Initialized;
        private bool m_Disposed;

        public VoskTranscriber(Settings settings)
        {
            m_Settings = settings;
        }

        public async Task<bool> Initialize()
        {
            if (m_Initialized)
            {
                return true;
            }

            await Task.Run(() =>
            {
                var modelPath = EnsureModelPathAsync().GetAwaiter().GetResult();

                Vosk.Vosk.SetLogLevel(0);
                m_Model = new Model(modelPath);
                m_Initialized = true;
                Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                    $"Vosk initialized. ModelPath='{modelPath}'");
            }).ConfigureAwait(false);

            return true;
        }

        public async Task<string> TranscribeCall(MemoryStream wavData)
        {
            if (!m_Initialized || m_Model == null)
            {
                throw new Exception("Vosk model is not initialized.");
            }

            await m_RecognizerLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using var recognizer = new VoskRecognizer(m_Model, 16000.0f);
                recognizer.SetWords(false);
                recognizer.SetPartialWords(false);
                recognizer.SetMaxAlternatives(0);

                var pcmData = ExtractPcmData(wavData);
                const int chunkSize = 4000;
                var offset = 0;
                while (offset < pcmData.Length)
                {
                    var len = Math.Min(chunkSize, pcmData.Length - offset);
                    var chunk = new byte[len];
                    Buffer.BlockCopy(pcmData, offset, chunk, 0, len);
                    recognizer.AcceptWaveform(chunk, len);
                    offset += len;
                }

                var finalJson = recognizer.FinalResult();
                if (string.IsNullOrWhiteSpace(finalJson))
                {
                    return string.Empty;
                }

                var obj = JObject.Parse(finalJson);
                return obj["text"]?.ToString() ?? string.Empty;
            }
            catch (Exception ex)
            {
                var err = $"Failed to transcribe WAV data with Vosk: {ex.Message}";
                Trace(TraceLoggerType.CallManager, TraceEventType.Error, err);
                throw new Exception(err);
            }
            finally
            {
                m_RecognizerLock.Release();
            }
        }

        public void Dispose()
        {
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_Initialized = false;
            m_Model?.Dispose();
            m_RecognizerLock.Dispose();
        }

        private string ResolveModelPath()
        {
            if (!string.IsNullOrWhiteSpace(m_Settings.VoskModelPath))
            {
                return m_Settings.VoskModelPath!;
            }

            return Path.Combine(Settings.DefaultWorkingDirectory, "model", "vosk");
        }

        private async Task<string> EnsureModelPathAsync()
        {
            var configuredPath = ResolveModelPath();
            Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                $"Vosk model path requested: '{configuredPath}'");
            if (IsValidModelDirectory(configuredPath) &&
                IsPathForSelectedPreset(configuredPath, m_Settings.TranscriptionModelPreset))
            {
                return configuredPath;
            }

            var modelRoot = Path.Combine(Settings.DefaultWorkingDirectory, "model");
            var preset = GetVoskPreset(m_Settings.TranscriptionModelPreset);
            var modelName = preset.Name;
            var modelUrl = preset.Url;
            var downloadedDefaultPath = Path.Combine(modelRoot, modelName);
            if (IsValidModelDirectory(downloadedDefaultPath))
            {
                Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                    $"Using previously downloaded Vosk model: '{downloadedDefaultPath}'");
                PersistResolvedModelPath(downloadedDefaultPath);
                return downloadedDefaultPath;
            }

            await s_ModelDownloadLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsValidModelDirectory(downloadedDefaultPath))
                {
                    return downloadedDefaultPath;
                }

                Directory.CreateDirectory(modelRoot);
                var zipPath = Path.Combine(modelRoot, $"{modelName}.zip");

                Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                    $"Downloading Vosk model '{modelName}' from {modelUrl}");
                using (var http = new HttpClient())
                using (var response = await http.GetAsync(modelUrl).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await response.Content.CopyToAsync(fs).ConfigureAwait(false);
                }

                Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                    $"Extracting Vosk model archive to {modelRoot}");
                ZipFile.ExtractToDirectory(zipPath, modelRoot, overwriteFiles: true);
                File.Delete(zipPath);

                if (!IsValidModelDirectory(downloadedDefaultPath))
                {
                    throw new Exception(
                        $"Vosk model extraction completed but expected model folder is missing: {downloadedDefaultPath}");
                }

                PersistResolvedModelPath(downloadedDefaultPath);
                return downloadedDefaultPath;
            }
            finally
            {
                s_ModelDownloadLock.Release();
            }
        }

        private static bool IsValidModelDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            var hasAm = Directory.Exists(Path.Combine(path, "am"));
            var hasConf = Directory.Exists(Path.Combine(path, "conf"));
            return hasAm && hasConf;
        }

        private static bool IsPathForSelectedPreset(string path, string? preset)
        {
            if (string.IsNullOrWhiteSpace(preset))
            {
                return true;
            }

            var dirName = new DirectoryInfo(path).Name.Trim().ToLowerInvariant();
            var presetName = preset.Trim().ToLowerInvariant();
            return dirName == presetName;
        }

        private static byte[] ExtractPcmData(MemoryStream wavData)
        {
            var data = wavData.ToArray();
            if (data.Length > 44 &&
                data[0] == (byte)'R' &&
                data[1] == (byte)'I' &&
                data[2] == (byte)'F' &&
                data[3] == (byte)'F' &&
                data[8] == (byte)'W' &&
                data[9] == (byte)'A' &&
                data[10] == (byte)'V' &&
                data[11] == (byte)'E')
            {
                var pcm = new byte[data.Length - 44];
                Buffer.BlockCopy(data, 44, pcm, 0, pcm.Length);
                return pcm;
            }

            return data;
        }

        private void PersistResolvedModelPath(string modelPath)
        {
            if (string.Equals(m_Settings.VoskModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                m_Settings.VoskModelPath = modelPath;
                Trace(TraceLoggerType.CallManager, TraceEventType.Information,
                    $"Updated VoskModelPath in memory: '{modelPath}'");
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.CallManager, TraceEventType.Warning,
                    $"Failed to update VoskModelPath in memory: {ex.Message}");
            }
        }

        private static (string Name, string Url) GetVoskPreset(string? preset)
        {
            if (string.IsNullOrWhiteSpace(preset))
            {
                return (DefaultVoskPreset, DefaultVoskModelZipUrl);
            }

            return preset.Trim().ToLowerInvariant() switch
            {
                "vosk-model-small-en-us-0.15" => ("vosk-model-small-en-us-0.15",
                    "https://alphacephei.com/vosk/models/vosk-model-small-en-us-0.15.zip"),
                "vosk-model-en-us-0.22" => ("vosk-model-en-us-0.22",
                    "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22.zip"),
                "vosk-model-en-us-0.22-lgraph" => ("vosk-model-en-us-0.22-lgraph",
                    "https://alphacephei.com/vosk/models/vosk-model-en-us-0.22-lgraph.zip"),
                _ => (DefaultVoskPreset, DefaultVoskModelZipUrl)
            };
        }
    }
}
