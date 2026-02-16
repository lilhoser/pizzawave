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

namespace pizzalib
{
    public class TranscribedCall
    {
        public long StartTime { get; set; }
        public long StopTime { get; set; }
        public int Source { get; set; }
        public string? SystemShortName { get; set; }
        public long CallId { get; set; }
        public List<long>? PatchedTalkgroups { get; set; }
        public long Talkgroup { get; set; }
        public double Frequency { get; set; }
        public string? Location { get; set; }
        public string? Transcription { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public bool IsAudioPlaying { get; set; } // Used to track when the call is being played in the UI
        public Guid UniqueId { get; set; }
        [Newtonsoft.Json.JsonIgnore]
        public bool IsAlertMatch { get; set; } // must be re-evaluated on every load or change in alerts

        [Newtonsoft.Json.JsonIgnore]
        public string FriendlyTalkgroup { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonIgnore]
        public string FriendlyFrequency { get; set; } = string.Empty;

        [Newtonsoft.Json.JsonIgnore]
        public int Duration => (int)(StopTime - StartTime);

        public string CallTime
        {
            get
            {
                DateTime date = DateTimeOffset.FromUnixTimeSeconds(StartTime).ToLocalTime().DateTime;
                return date.ToString("M/d/yyyy h:mm:ss tt");
            }
        }

        // Command to play audio (set by UI layer)
        [Newtonsoft.Json.JsonIgnore]
        public Action<TranscribedCall>? PlayAudioCommand { get; set; }

        public string ToString(Settings Settings)
        {
            var talkgroup = TalkgroupHelper.FormatTalkgroup(Settings, Talkgroup);
            DateTime date = DateTimeOffset.FromUnixTimeSeconds(StartTime).ToLocalTime().DateTime;
            var dateStr = $"{date:M/d/yyyy h:mm tt}";
            return $"[{talkgroup}]:{dateStr}: {Transcription}";
        }
    }
}
