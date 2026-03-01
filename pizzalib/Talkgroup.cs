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
using CsvHelper;
using CsvHelper.Configuration.Attributes;
using System.Globalization;

namespace pizzalib
{
    public record Talkgroup
    {
        [Index(0)]
        public long Id { get; init; }
        [Index(1)]
        public string? Mode { get; init; }
        [Index(2)]
        public string? AlphaTag { get; init; }
        [Index(3)]
        public string? Description { get; init; }
        [Index(4)]
        public string? Tag { get; init; }
        [Index(5)]
        public string? Category { get; init; }
    }

    public class TalkgroupHelper
    {
        public static List<Talkgroup> GetTalkgroupsFromCsv(string fileName)
        {
            using var reader = new StreamReader(fileName);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            return csv.GetRecords<Talkgroup>().ToList();
        }

        public static Talkgroup? LookupTalkgroup(Settings settings, long talkgroupId)
        {
            return settings.Talkgroups?.FirstOrDefault(t => t.Id == talkgroupId);
        }

        public static string FormatTalkgroup(Settings settings, long talkgroupId, bool shortFormat = true)
        {
            var talkgroup = LookupTalkgroup(settings, talkgroupId);
            if (talkgroup == null)
            {
                return $"{talkgroupId}";
            }
            if (shortFormat)
            {
                // Return empty string if AlphaTag is null to avoid CS8603
                //return talkgroup.AlphaTag ?? string.Empty;
                return $"{talkgroup.Category} ({talkgroup.AlphaTag})";
            }
            return talkgroup.ToString();
        }
    }
}
