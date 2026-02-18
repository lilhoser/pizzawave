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
using System.Net.Sockets;
using NAudio.Utils;
using NAudio.Wave.SampleProviders;
using NAudio.Wave;
using Newtonsoft.Json.Linq;
using System.Text;

namespace pizzalib
{
    using static pizzalib.TraceLogger;

    public enum OutputFileFormat
    {
        Wav,
        Mp3
    }

    public class WavStreamData
    {
        private readonly static int PIZZA_MAGIC = 0x415A5A50; // pzza
        private readonly static int MAX_JSON_LENGTH = 4096 * 2;
        private readonly static int MAX_SAMPLE_COUNT = 0xfffffe;
        private MemoryStream m_WavData;
        private MemoryStream m_JsonData;
        private Settings m_Settings;

        public WavStreamData(Settings settings)
        {
            m_WavData = new MemoryStream();
            m_JsonData = new MemoryStream();
            m_Settings = settings;
        }

        public async Task<bool> ProcessClientData(Stream ClientStream, CancellationTokenSource CancelSource)
        {
            byte[] buffer4 = new byte[4];
            byte[] buffer8 = new byte[8];
            //
            // Data format (from trunk-recorder/plugins/callstream):
            //      4-byte magic header
            //      8-byte JSON string length
            //      4-byte sample count
            //      [json data - string]
            //      [sample data - array of int16s]
            //
            await ClientStream.ReadExactlyAsync(buffer4, 0, buffer4.Length, CancelSource.Token);
            var magic = BitConverter.ToInt32(buffer4, 0);
            if (magic != PIZZA_MAGIC)
            {
                Trace(TraceLoggerType.WavStreamData,
                      TraceEventType.Error,
                      $"Got bad pizza magic: 0x{magic:X}!");
                return false;
            }
            await ClientStream.ReadExactlyAsync(buffer8, 0, buffer8.Length, CancelSource.Token);
            var jsonLength = BitConverter.ToInt64(buffer8);
            if (jsonLength > MAX_JSON_LENGTH)
            {
                Trace(TraceLoggerType.WavStreamData,
                      TraceEventType.Error,
                      $"Got bad JSON length {jsonLength}");
                return false;
            }
            await ClientStream.ReadExactlyAsync(buffer4, 0, buffer4.Length, CancelSource.Token);
            var sampleCount = BitConverter.ToInt32(buffer4, 0);
            if (sampleCount > MAX_SAMPLE_COUNT)
            {
                Trace(TraceLoggerType.WavStreamData,
                      TraceEventType.Error,
                      $"Got bad sample count {sampleCount}");
                return false;
            }

            //
            // Clear previous call data
            //
            m_JsonData.SetLength(0);
            m_WavData?.Dispose();
            m_WavData = new MemoryStream();

            //
            // Read in JSON data
            //
            byte[] dataBuffer = new byte[jsonLength];
            var bytesRead = await ClientStream.ReadAtLeastAsync(dataBuffer, dataBuffer.Length, true, CancelSource.Token);
            if (bytesRead != jsonLength)
            {
                Trace(TraceLoggerType.WavStreamData,
                      TraceEventType.Error,
                      $"Received incomplete JSON data: expected {jsonLength} but got {bytesRead}");
                return false;
            }
            m_JsonData.Write(dataBuffer, 0, bytesRead);

            //
            // Read in sample data
            //
            var expectedSampleSize = sampleCount * sizeof(ushort);
            dataBuffer = new byte[expectedSampleSize];
            bytesRead = await ClientStream.ReadAtLeastAsync(dataBuffer, dataBuffer.Length, true, CancelSource.Token);
            if (bytesRead != expectedSampleSize)
            {
                Trace(TraceLoggerType.WavStreamData,
                      TraceEventType.Error,
                      $"Received incomplete sample data: expected {expectedSampleSize} but got {bytesRead}");
                return false;
            }

            //
            // Create a WAV memorystream from the sample data.
            //
            try
            {
                m_WavData = GetWavStream(dataBuffer);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.WavStreamData,
                      TraceEventType.Error,
                      $"Failed to create WAV stream from sample data: {ex.Message}");
                return false;
            }

            if (m_WavData.Length == 0)
            {
                Trace(TraceLoggerType.WavStreamData,
                      TraceEventType.Error,
                      $"WavWriter produced an empty WAV stream");
                return false;
            }

            Trace(TraceLoggerType.WavStreamData,
                  TraceEventType.Verbose,
                  $"Received data: {m_JsonData.Length} bytes JSON / {m_WavData.Length} bytes samples.");
            return true;
        }

        public MemoryStream GetRawStream()
        {
            return m_WavData;
        }

        public void RewindStream()
        {
            m_WavData.Seek(0, SeekOrigin.Begin);
        }

        public JObject GetJsonObject()
        {
            var json = Encoding.UTF8.GetString(m_JsonData.GetBuffer());
            return JObject.Parse(json);
        }

        public void DumpStreamToFile(string BaseDir, string FileName, OutputFileFormat Format)
        {
            if (string.IsNullOrEmpty(BaseDir))
            {
                throw new Exception("No output location specified");
            }

            if (!Directory.Exists(BaseDir))
            {
                try
                {
                    Directory.CreateDirectory(BaseDir);
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.WavStreamData,
                          TraceEventType.Error,
                          $"Unable to create output directory '{BaseDir}': {ex.Message}");
                    throw;
                }
            }

            var target = Path.Combine(BaseDir, FileName);
            try
            {
                switch (Format)
                {
                    case OutputFileFormat.Wav:
                        {
                            File.WriteAllBytes(target, m_WavData.GetBuffer());
                            Trace(TraceLoggerType.WavStreamData,
                                  TraceEventType.Information,
                                  $"ProcessAudioData: Wrote WAV data to {target}");
                            break;
                        }
                    case OutputFileFormat.Mp3:
                        {
                            //
                            // If the specified TR sample rate was less than 16khz, it was resampled.
                            //
                            var sampleRate = Math.Max(m_Settings.analogSamplingRate, 16000);
                            var wavFormat = new WaveFormat(
                                sampleRate,
                                m_Settings.analogBitDepth,
                                m_Settings.analogChannels);
                            using (var reader = new WaveFileReader(m_WavData))
                            {
                                MediaFoundationEncoder.EncodeToMp3(reader, target);
                            }
                            Trace(TraceLoggerType.WavStreamData,
                                  TraceEventType.Information,
                                  $"ProcessAudioData: Wrote MP3 data to {target}");
                            break;
                        }
                    default:
                        {
                            throw new Exception("Unsupported output format");
                        }
                }
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.WavStreamData,
                      TraceEventType.Error,
                      $"Failed to write stream data to {target}: {ex.Message}");
                return;
            }
            finally
            {
                RewindStream();
            }
        }

        private MemoryStream GetWavStream(byte[] SampleData)
        {
            MemoryStream wavStream = new MemoryStream();
            var format = new WaveFormat(m_Settings.analogSamplingRate,
                m_Settings.analogBitDepth, m_Settings.analogChannels);
            using (var wavWriter = new WaveFileWriter(new IgnoreDisposeStream(wavStream), format))
            {
                wavWriter.Write(SampleData, 0, SampleData.Length);
            }
            wavStream.Seek(0, SeekOrigin.Begin);
            if (format.SampleRate < 16000) // upconversion required by whisper
            {
                using (var reader = new WaveFileReader(wavStream))
                {
                    var resampler = new WdlResamplingSampleProvider(reader.ToSampleProvider(), 16000);
                    MemoryStream wavStream2 = new MemoryStream();
                    WaveFileWriter.WriteWavFileToStream(wavStream2, resampler.ToWaveProvider16());
                    wavStream2.Seek(0, SeekOrigin.Begin);
                    return wavStream2;
                }
            }
            return wavStream;
        }
    }
}
