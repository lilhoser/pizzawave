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
    public class Talkgroup : IEquatable<Talkgroup>, IComparable<Talkgroup>
    {
        [Index(0)]
        public long Id { get; set; }
        [Index(1)]
        public string Mode { get; set; }
        [Index(2)]
        public string AlphaTag { get; set; }
        [Index(3)]
        public string Description { get; set; }
        [Index(4)]
        public string Tag { get; set; }
        [Index(5)]
        public string Category { get; set; }

        public int CompareTo(Talkgroup obj)
        {
            //
            // Sorting Talkgroup objects is by Id
            //
            return Id.CompareTo(obj.Id);
        }

        public override string ToString()
        {
            return $"{AlphaTag} ({Tag}/{Category}) - {Description}";
        }

        public override bool Equals(object? Other)
        {
            if (Other == null)
            {
                return false;
            }
            var field = Other as Talkgroup;
            return Equals(field);
        }

        public bool Equals(Talkgroup? Other)
        {
            if (Other == null)
            {
                return false;
            }
            return Id == Other.Id &&
                Mode == Other.Mode &&
                AlphaTag == Other.AlphaTag &&
                Description == Other.Description &&
                Tag == Other.Tag &&
                Category == Other.Category;
        }

        public static bool operator ==(Talkgroup? Talkgroup1, Talkgroup? Talkgroup2)
        {
            if ((object)Talkgroup1 == null || (object)Talkgroup2 == null)
                return Equals(Talkgroup1, Talkgroup2);
            return Talkgroup1.Equals(Talkgroup2);
        }

        public static bool operator !=(Talkgroup? Talkgroup1, Talkgroup? Talkgroup2)
        {
            if ((object)Talkgroup1 == null || (object)Talkgroup2 == null)
                return !Equals(Talkgroup1, Talkgroup2);
            return !(Talkgroup1.Equals(Talkgroup2));
        }

        public override int GetHashCode()
        {
            return (Id, Mode, AlphaTag, Description, Tag, Category
                ).GetHashCode();
        }
    }

    public static class TalkgroupHelper
    {
        public static List<Talkgroup> GetTalkgroupsFromCsv(string FileName)
        {
            using (var reader = new StreamReader(FileName))
            using (var csv = new CsvReader(reader, CultureInfo.InvariantCulture))
            {
                return csv.GetRecords<Talkgroup>().ToList();
            }
        }

        public static Talkgroup? LookupTalkgroup(Settings Settings, long Talkgroup)
        {
            if (Settings.talkgroups == null || Settings.talkgroups.Count == 0)
            {
                return null;
            }
            return Settings.talkgroups.FirstOrDefault(t => t.Id == Talkgroup);
        }

        public static string FormatTalkgroup(Settings Settings, long Talkgroup, bool ShortFormat = false)
        {
            var talkgroup = LookupTalkgroup(Settings, Talkgroup);
            if (talkgroup == null)
            {
                return $"{Talkgroup}";
            }

            return ShortFormat ? $"{talkgroup.AlphaTag}" : $"{talkgroup}";
        }
    }
}
