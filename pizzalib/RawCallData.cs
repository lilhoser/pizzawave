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
using FFMpegCore.Enums;
using FFMpegCore.Extensions.Downloader;
using FFMpegCore.Pipes;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text;

namespace pizzalib
{
    using static pizzalib.TraceLogger;

    public enum OutputFileFormat
    {
        Wav,
        Mp3
    }

    public class RawCallData : IDisposable
    {
        private readonly static int PIZZA_MAGIC = 0x415A5A50; // pzza
        private readonly static int MAX_JSON_LENGTH = 4096 * 2;
        private readonly static int MAX_SAMPLE_COUNT = 0xfffffe;
        private byte[]? m_rawPcmData;

        private MemoryStream m_JsonData;
        private Settings m_Settings;
        private bool m_Disposed;

        public RawCallData(Settings settings)
        {
            m_JsonData = new MemoryStream();
            m_Settings = settings;
        }

        ~RawCallData()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (m_Disposed) return;
            m_Disposed = true;

            if (disposing)
            {
                m_JsonData?.Dispose();
            }

            m_rawPcmData = null; // Release large PCM buffer
            m_JsonData = null!;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public async Task<bool> ProcessClientData(Stream ClientStream, CancellationTokenSource CancelSource)
        {
            byte[] buffer4 = new byte[4];
            byte[] buffer8 = new byte[8];

            // Read header
            await ClientStream.ReadExactlyAsync(buffer4, 0, 4, CancelSource.Token);
            if (BitConverter.ToInt32(buffer4, 0) != PIZZA_MAGIC)
            {
                Trace(TraceLoggerType.RawCallData, TraceEventType.Error, "Bad pizza magic header");
                return false;
            }

            await ClientStream.ReadExactlyAsync(buffer8, 0, 8, CancelSource.Token);
            long jsonLength = BitConverter.ToInt64(buffer8);
            if (jsonLength > MAX_JSON_LENGTH) return false;

            await ClientStream.ReadExactlyAsync(buffer4, 0, 4, CancelSource.Token);
            int sampleCount = BitConverter.ToInt32(buffer4, 0);
            if (sampleCount > MAX_SAMPLE_COUNT) return false;

            // Clear previous data
            m_JsonData.SetLength(0);

            // Read JSON
            byte[] jsonBuffer = new byte[jsonLength];
            await ClientStream.ReadAtLeastAsync(jsonBuffer, jsonBuffer.Length, true, CancelSource.Token);
            m_JsonData.Write(jsonBuffer, 0, jsonBuffer.Length);

            // Read raw PCM (s16le)
            int expectedBytes = sampleCount * 2;
            m_rawPcmData = new byte[expectedBytes];
            var numBytesRead = await ClientStream.ReadAtLeastAsync(m_rawPcmData, expectedBytes, true, CancelSource.Token);

            Trace(TraceLoggerType.RawCallData, TraceEventType.Verbose,
                  $"Raw call data: {m_JsonData.Length} bytes JSON / {numBytesRead} bytes sample data @ "+
                  $"{m_Settings.analogSamplingRate} Hz");

            return true;
        }

        public async Task<MemoryStream> GetAudioStreamAsync(
            OutputFileFormat format,
            int mp3Bitrate = 128)
        {
            if (m_rawPcmData == null || m_rawPcmData.Length == 0)
                throw new InvalidOperationException("No audio data processed yet.");

            if (format == OutputFileFormat.Wav)
            {
                return CreateWavStream(); // caller owns!
            }

            // MP3 on-demand (requires ffmpeg)
            var mp3Stream = new MemoryStream();
            var sampleRate = m_Settings.analogSamplingRate;

            await FFMpegArguments
                .FromPipeInput(new RawS16lePipeSource(m_rawPcmData, sampleRate))
                .OutputToPipe(new StreamPipeSink(mp3Stream), options => options
                    .ForceFormat("mp3")
                    .WithAudioCodec(AudioCodec.LibMp3Lame)
                    .WithAudioBitrate(mp3Bitrate)
                // .WithCustomArgument("-q:a 4")   // uncomment for best VBR
                )
                .ProcessAsynchronously();

            mp3Stream.Position = 0;
            return mp3Stream;
        }

        /// <summary>
        /// Returns audio resampled to 16KHz for Whisper transcription.
        /// Whisper.net only supports 16KHz sample rate.
        /// </summary>
        public async Task<MemoryStream> GetAudioStreamForWhisperAsync()
        {
            if (m_rawPcmData == null || m_rawPcmData.Length == 0)
                throw new InvalidOperationException("No audio data processed yet.");

            var sourceSampleRate = m_Settings.analogSamplingRate;
            const int targetSampleRate = 16000;

            // Use ffmpeg to resample to 16KHz and output as WAV
            var wavStream = new MemoryStream();
            await FFMpegArguments
                .FromPipeInput(new RawS16lePipeSource(m_rawPcmData, sourceSampleRate))
                .OutputToPipe(new StreamPipeSink(wavStream), options => options
                    .ForceFormat("wav")
                    .WithCustomArgument($"-ar {targetSampleRate} -acodec pcm_s16le")
                )
                .ProcessAsynchronously();

            wavStream.Position = 0;
            return wavStream;
        }

        private MemoryStream CreateWavStream() // caller owns resources!
        {
            const int headerSize = 44;
            int dataSize = m_rawPcmData!.Length;

            var wav = new MemoryStream(headerSize + dataSize);
            var w = new BinaryWriter(wav);
            var sampleRate = m_Settings.analogSamplingRate;

            w.Write(Encoding.ASCII.GetBytes("RIFF"));
            w.Write(0xFFFFFFFF);                    // unknown size
            w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(sampleRate);
            w.Write(sampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(0xFFFFFFFF);                    // unknown size
            w.Write(m_rawPcmData);

            wav.Position = 0;
            return wav;
        }

        public JObject GetJsonObject()
        {
            var json = Encoding.UTF8.GetString(m_JsonData.GetBuffer(), 0, (int)m_JsonData.Length);
            return JObject.Parse(json);
        }

        public async Task DumpStreamToFile(string BaseDir, string FileName, OutputFileFormat Format)
        {
            if (string.IsNullOrEmpty(BaseDir))
                throw new Exception("No output location specified");

            if (!Directory.Exists(BaseDir))
                Directory.CreateDirectory(BaseDir);

            var target = Path.Combine(BaseDir, FileName);

            try
            {
                if (Format == OutputFileFormat.Wav)
                {
                    using var wavStream = await GetAudioStreamAsync(OutputFileFormat.Wav);
                    File.WriteAllBytes(target, wavStream.ToArray());
                }
                else
                {
                    using var mp3Stream = await GetAudioStreamAsync(OutputFileFormat.Mp3);
                    File.WriteAllBytes(target, mp3Stream.ToArray());
                }

                Trace(TraceLoggerType.RawCallData, TraceEventType.Information,
                      $"Wrote {Format} to {target} ({new FileInfo(target).Length / 1024} KB)");
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.RawCallData, TraceEventType.Error,
                      $"Failed to write {Format} to {target}: {ex.Message}");
            }
        }
    }

    public class RawS16lePipeSource : IPipeSource
    {
        private readonly byte[] _pcmData;
        private readonly int _sourceSampleRate;

        public RawS16lePipeSource(byte[] pcmData, int sourceSampleRate = 8000)
        {
            _pcmData = pcmData ?? throw new ArgumentNullException(nameof(pcmData));
            _sourceSampleRate = sourceSampleRate;
        }

        public string GetStreamArguments() => $"-f s16le -ar {_sourceSampleRate} -ac 1";

        public Task WriteAsync(Stream outputStream, CancellationToken cancellationToken)
        {
            if (_pcmData.Length == 0)
                return Task.CompletedTask;

            return outputStream.WriteAsync(_pcmData, 0, _pcmData.Length, cancellationToken);
        }
    }
}