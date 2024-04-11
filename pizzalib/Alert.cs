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
    public enum AlertFrequency
    {
        RealTime,
        Hourly,
        Daily
    }

    public class Alert : IEquatable<Alert>
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string Keywords { get; set; }
        public AlertFrequency Frequency { get; set; }
        public List<long> Talkgroups { get; set; }
        public bool Enabled { get; set; }

        public Alert()
        {
            Name = string.Empty;
            Email = string.Empty;
            Keywords = string.Empty;
            Id = Guid.NewGuid();
            Talkgroups = new List<long>();
        }

        public override string ToString()
        {
            return $"{Name}({(Enabled ? "on" : "off")}: {Keywords}";
        }

        public override bool Equals(object? Other)
        {
            if (Other == null)
            {
                return false;
            }
            var field = Other as Alert;
            return Equals(field);
        }

        public bool Equals(Alert? Other)
        {
            if (Other == null)
            {
                return false;
            }
            return Id == Other.Id &&
                Name == Other.Name &&
                Email == Other.Email &&
                Keywords == Other.Keywords &&
                Frequency == Other.Frequency &&
                Talkgroups == Other.Talkgroups &&
                Enabled == Other.Enabled;
        }

        public static bool operator ==(Alert? Alert1, Alert? Alert2)
        {
            if ((object)Alert1 == null || (object)Alert2 == null)
                return Equals(Alert1, Alert2);
            return Alert1.Equals(Alert2);
        }

        public static bool operator !=(Alert? Alert1, Alert? Alert2)
        {
            if ((object)Alert1 == null || (object)Alert2 == null)
                return !Equals(Alert1, Alert2);
            return !(Alert1.Equals(Alert2));
        }

        public override int GetHashCode()
        {
            return (Name, Email, Keywords, Frequency, Talkgroups, Enabled
                ).GetHashCode();
        }

        public void Validate()
        {
            if (string.IsNullOrEmpty(Name))
            {
                throw new Exception("Name is required");
            }

            if (string.IsNullOrEmpty(Email))
            {
                throw new Exception("Email is required");
            }
            if (string.IsNullOrEmpty(Keywords))
            {
                throw new Exception("Keywords are required");
            }
            var emails = GetEmailRecipients();
            if (emails.Count == 0)
            {
                throw new Exception("Email is required");
            }
            foreach (var email in emails)
            {
                try
                {
                    var m = new MailAddress(email);
                }
                catch (FormatException ex)
                {
                    throw new Exception($"Alert email address {email} is invalid: {ex.Message}");
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
    }
}
