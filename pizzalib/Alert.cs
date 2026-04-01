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
using System.Net.Mail;

namespace pizzalib
{
    public enum AlertMatchType
    {
        Keyword = 0,
        PoliceCode = 2
    }

    public enum AlertFrequency
    {
        RealTime,
        Hourly,
        Daily
    }

    public class Alert
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Keywords { get; set; } = string.Empty;
        public string PoliceCodes { get; set; } = string.Empty;
        public AlertMatchType MatchType { get; set; } = AlertMatchType.Keyword;
        public AlertFrequency Frequency { get; set; }
        public List<long> Talkgroups { get; set; } = [];
        public bool Enabled { get; set; }
        public bool Autoplay { get; set; } = true;

        public override string ToString() => $"{Name}({(Enabled ? "on" : "off")}: {Keywords}";

        public void Validate()
        {
            if (string.IsNullOrEmpty(Name))
            {
                throw new Exception("Name is required");
            }

            MatchType = NormalizeMatchType(MatchType);

            bool hasAnyCriteria = false;
            if (MatchType == AlertMatchType.Keyword && !string.IsNullOrWhiteSpace(Keywords))
                hasAnyCriteria = true;
            if (MatchType == AlertMatchType.PoliceCode && !string.IsNullOrWhiteSpace(PoliceCodes))
                hasAnyCriteria = true;

            if (!hasAnyCriteria)
            {
                throw new Exception("At least one enabled alert criteria is required (keyword/police code).");
            }

            // Email is optional - if provided, validate format
            if (!string.IsNullOrEmpty(Email))
            {
                var emails = GetEmailRecipients();
                if (emails.Count == 0)
                {
                    throw new Exception("Email is required");
                }
                foreach (var email in emails)
                {
                    try
                    {
                        _ = new MailAddress(email);
                    }
                    catch (FormatException)
                    {
                        throw new Exception($"Alert email address {email} is invalid");
                    }
                }
            }
        }

        public List<string> GetEmailRecipients()
        {
            var emails = Email.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (emails.Length == 0)
            {
                return new List<string>();
            }
            return emails.ToList();
        }

        private static AlertMatchType NormalizeMatchType(AlertMatchType type)
        {
            return type == AlertMatchType.PoliceCode
                ? AlertMatchType.PoliceCode
                : AlertMatchType.Keyword;
        }
    }
}
