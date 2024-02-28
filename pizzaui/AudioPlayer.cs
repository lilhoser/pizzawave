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
using NAudio.Wave;
using System.Diagnostics;

namespace pizzaui
{
    using static TraceLogger;

    internal class AudioPlayer
    {
        private WaveOutEvent m_Player;

        public AudioPlayer()
        {
            m_Player = new WaveOutEvent();
        }

        public void Shutdown()
        {
            m_Player.Stop();
        }

        public void Stop()
        {
            m_Player.Stop();
        }

        public void PlayMp3File(string FileName, Guid UniqueCallId, Func<Guid, bool>? CompletionCallback)
        {
            if (m_Player.PlaybackState == PlaybackState.Playing)
            {
                m_Player.Stop();
            }

            //
            // NAudio plays the audio asynchronously, so we have to poll for completion.
            // Because this is a blocking operation, we'll fire off a task.
            //
            Task.Run(() =>
            {
                try
                {
                    using (var reader = new Mp3FileReader(FileName))
                    {
                        m_Player.Init(reader);
                        m_Player.Play();
                        while (m_Player.PlaybackState == PlaybackState.Playing)
                        {
                            Thread.Sleep(500);
                        }
                        CompletionCallback?.Invoke(UniqueCallId);
                    }
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.Utilities,
                          TraceEventType.Error,
                          $"Unable to play audio {FileName}: {ex.Message}");
                }
            });
        }

        public void PlayWavFile(string FileName, Guid UniqueCallId, Func<Guid, bool>? CompletionCallback)
        {
            if (m_Player.PlaybackState == PlaybackState.Playing)
            {
                m_Player.Stop();
            }

            //
            // NAudio plays the audio asynchronously, so we have to poll for completion.
            // Because this is a blocking operation, we'll fire off a task.
            //
            Task.Run(() =>
            {
                try
                {
                    using (var reader = new WaveFileReader(FileName))
                    {
                        var volumeStream = new Wave16ToFloatProvider(reader);
                        m_Player.Init(volumeStream);
                        m_Player.Play();
                        while (m_Player.PlaybackState == PlaybackState.Playing)
                        {
                            Thread.Sleep(500);
                        }
                        CompletionCallback?.Invoke(UniqueCallId);
                    }
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.Utilities,
                          TraceEventType.Error,
                          $"Unable to play audio {FileName}: {ex.Message}");
                }
            });
        }
    }
}
