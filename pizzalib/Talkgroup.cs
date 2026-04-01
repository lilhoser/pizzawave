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
using System.Globalization;

namespace pizzalib
{
    public record Talkgroup
    {
        public long Id { get; init; }
        public string? Mode { get; init; }
        public string? AlphaTag { get; init; }
        public string? Description { get; init; }
        public string? Tag { get; init; }
        public string? Category { get; init; }
    }

    public class TalkgroupHelper
    {
        public static List<Talkgroup> GetTalkgroupsFromCsv(string fileName)
        {
            using var reader = new StreamReader(fileName);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            if (!csv.Read() || !csv.ReadHeader())
                return new List<Talkgroup>();

            var headers = (csv.HeaderRecord ?? Array.Empty<string>())
                .Select((name, index) => new { Key = NormalizeHeader(name), index })
                .GroupBy(x => x.Key)
                .ToDictionary(g => g.Key, g => g.First().index);

            var rows = new List<Talkgroup>();
            while (csv.Read())
            {
                var idRaw = GetField(csv, headers, "decimal", "dec", "id", "tgid");
                if (string.IsNullOrWhiteSpace(idRaw))
                    continue;

                var normalizedId = idRaw.Replace(",", string.Empty).Trim();
                if (!long.TryParse(normalizedId, out var id) || id <= 0)
                    continue;

                rows.Add(new Talkgroup
                {
                    Id = id,
                    Mode = NullIfWhiteSpace(GetField(csv, headers, "mode")),
                    AlphaTag = NullIfWhiteSpace(GetField(csv, headers, "alphatag", "alpha", "alpha tag")),
                    Description = NullIfWhiteSpace(GetField(csv, headers, "description", "desc")),
                    Tag = NullIfWhiteSpace(GetField(csv, headers, "tag")),
                    Category = NullIfWhiteSpace(GetField(csv, headers, "category"))
                });
            }

            return rows;
        }

        private static string NormalizeHeader(string? header)
        {
            return new string((header ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
        }

        private static string? GetField(CsvReader csv, Dictionary<string, int> headers, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (!headers.TryGetValue(NormalizeHeader(key), out var index))
                    continue;
                return csv.GetField(index);
            }
            return null;
        }

        private static string? NullIfWhiteSpace(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public static Talkgroup? LookupTalkgroup(IEnumerable<Talkgroup>? talkgroups, long talkgroupId)
        {
            return talkgroups?.FirstOrDefault(t => t.Id == talkgroupId);
        }

        public static string FormatTalkgroup(IEnumerable<Talkgroup>? talkgroups, long talkgroupId, bool shortFormat = true)
        {
            var talkgroup = LookupTalkgroup(talkgroups, talkgroupId);
            if (talkgroup == null)
            {
                return $"{talkgroupId}";
            }
            if (shortFormat)
            {
                return $"{talkgroup.AlphaTag} - {talkgroup.Description} ({talkgroup.Category})";
            }
            return talkgroup.ToString();
        }
    }
}
