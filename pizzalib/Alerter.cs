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
using System.Net.Mail;
using System.Net.Mime;
using System.Net;
using static pizzalib.TraceLogger;

namespace pizzalib
{
    public class Alerter
    {
        private Dictionary<Guid, AlertEvent> m_AlertEvents;
        private const int MaxAlertEvents = 100;
        private Settings m_Settings;
        private readonly object m_AlertLock = new object();

        public Alerter(Settings Settings)
        {
            m_Settings = Settings;
            m_AlertEvents = new Dictionary<Guid, AlertEvent>();
        }

        public void ProcessAlerts(TranscribedCall call)
        {
            lock (m_AlertLock)
            {
                call.IsAlertMatch = false;
                call.ShouldAutoplay = false;

                foreach (var alert in m_Settings.Alerts)
                {
                    Trace(TraceLoggerType.Alerts, TraceEventType.Verbose,
                          $"Processing alert {alert.Name} for call ID {call.CallId}");

                    if (!alert.Enabled)
                    {
                        Trace(TraceLoggerType.Alerts, TraceEventType.Information,
                              $"Alert {alert.Name} is disabled, skipping.");
                        continue;
                    }

                    var keywords = alert.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var keyword in keywords)
                    {
                        if (!string.IsNullOrEmpty(call.Transcription) &&
                            call.Transcription.ToLower().Contains(keyword.ToLower()))
                        {
                            Trace(TraceLoggerType.Alerts, TraceEventType.Information,
                                  $"Alert {alert.Name} keyword {keyword} matches call ID {call.CallId} transcription.");
                            call.IsAlertMatch = true;
                            call.IsPinned = true;
                            call.ShouldAutoplay = alert.Autoplay;
                            TriggerAlertEvent(alert, call);
                            break;
                        }
                    }

                    if (call.IsAlertMatch) break;
                }
            }
        }

        public static void ProcessAlertsOffline(TranscribedCall call, List<Alert> alerts)
        {
            call.IsAlertMatch = false;

            foreach (var alert in alerts)
            {
                Trace(TraceLoggerType.Alerts, TraceEventType.Verbose,
                      $"Processing offline alert {alert.Name} for call ID {call.CallId}");

                if (!alert.Enabled)
                {
                    Trace(TraceLoggerType.Alerts, TraceEventType.Information,
                          $"Offline alert {alert.Name} is disabled, skipping.");
                    continue;
                }

                var keywords = alert.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries);
                foreach (var keyword in keywords)
                {
                    if (!string.IsNullOrEmpty(call.Transcription) &&
                        call.Transcription.ToLower().Contains(keyword.ToLower()))
                    {
                        Trace(TraceLoggerType.Alerts, TraceEventType.Information,
                              $"Offline alert {alert.Name} keyword {keyword} matches call ID {call.CallId} transcription.");
                        call.IsAlertMatch = true;
                        call.IsPinned = true;
                        break;
                    }
                }

                if (call.IsAlertMatch) break;
            }
        }

        private void TriggerAlertEvent(Alert alert, TranscribedCall call)
        {
            // Get or create alert event tracker
            var alertEvent = m_AlertEvents.GetValueOrDefault(alert.Id) ?? CreateAlertEvent(alert.Id);

            alertEvent.LockExclusive();
            try
            {
                if (!ShouldTriggerAlert(alert, alertEvent)) return;

                alertEvent.LastTriggered = DateTime.Now;
                alertEvent.TriggerCountLastInterval++;

                // Send email notification only if alert has email AND Gmail is configured
                if (!string.IsNullOrEmpty(alert.Email) &&
                    !string.IsNullOrEmpty(m_Settings.gmailUser) &&
                    !string.IsNullOrEmpty(m_Settings.gmailPassword))
                {
                    alertEvent.Unlock();
                    SendEmailNotification(alert, alertEvent, call);
                    alertEvent.LockExclusive();
                }
                else
                {
                    Trace(TraceLoggerType.Alerts, TraceEventType.Information,
                          $"Alert {alert.Name}: UI notification only (no email configured)");
                }
            }
            finally
            {
                alertEvent.Unlock();
            }
        }

        private AlertEvent CreateAlertEvent(Guid alertId)
        {
            // Evict oldest entries if we've reached the limit
            if (m_AlertEvents.Count >= MaxAlertEvents)
            {
                var oldestKey = m_AlertEvents.Keys.FirstOrDefault();
                if (oldestKey != Guid.Empty) m_AlertEvents.Remove(oldestKey);
            }

            var alertEvent = new AlertEvent(alertId);
            m_AlertEvents.Add(alertId, alertEvent);
            return alertEvent;
        }

        private bool ShouldTriggerAlert(Alert alert, AlertEvent alertEvent)
        {
            if (alertEvent.LastTriggered == DateTime.MinValue) return true;

            var now = DateTime.Now;
            int diff;

            switch (alert.Frequency)
            {
                case AlertFrequency.Daily:
                    diff = (int)(now - alertEvent.LastTriggered).TotalDays;
                    break;
                case AlertFrequency.Hourly:
                    diff = (int)(now - alertEvent.LastTriggered).TotalHours;
                    break;
                case AlertFrequency.RealTime:
                    var seconds = (int)(now - alertEvent.LastTriggered).TotalSeconds;
                    if (alertEvent.TriggerCountLastInterval > AlertEvent.s_RealtimeThresholdPerInterval &&
                        seconds <= AlertEvent.s_RealtimeIntervalSec)
                    {
                        Trace(TraceLoggerType.Alerts, TraceEventType.Warning,
                              $"NOT triggering: Alert set to real-time has been triggered " +
                              $"{alertEvent.TriggerCountLastInterval} times in the past {seconds} seconds.");
                        return false;
                    }
                    if (seconds > AlertEvent.s_RealtimeIntervalSec) alertEvent.TriggerCountLastInterval = 0;
                    diff = 1;
                    break;
                default:
                    Trace(TraceLoggerType.Alerts, TraceEventType.Error,
                          $"Unrecognized alert frequency {alert.Frequency}");
                    return false;
            }

            if (diff < 1)
            {
                Trace(TraceLoggerType.Alerts, TraceEventType.Warning,
                      $"NOT triggering: Alert set to {alert.Frequency}, last triggered " +
                      $"{alertEvent.LastTriggered:M/d/yyyy h:mm tt}");
                return false;
            }

            return true;
        }

        private void SendEmailNotification(Alert Alert, AlertEvent AlertEvent, TranscribedCall Call)
        {
            var formattedTalkgroup = TalkgroupHelper.FormatTalkgroup(m_Settings, Call.Talkgroup);
            var sender = new MailAddress(m_Settings.gmailUser!, "pizzawave notifications");
            string password = m_Settings.gmailPassword!;
            var smtp = new SmtpClient
            {
                Host = "smtp.gmail.com",
                Port = 587,
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(sender.Address, password),
                Timeout = 20000
            };
            var recipients = Alert.GetEmailRecipients();

            foreach (var recipient in recipients)
            {
                var recipientAddress = new MailAddress(recipient, null);
                using (var message = new MailMessage(sender, recipientAddress)
                {
                    Subject = $"pizzawave alert: {Alert.Name}",
                    IsBodyHtml = true,
                    Body = $"The following audio transcription from talkgroup <b>{formattedTalkgroup}" +
                        $"</b> has triggered your alert named <b>" +
                        $"{Alert.Name}</b> on <b>{AlertEvent.LastTriggered:M/d/yyyy h:mm tt}</b>:" +
                        $"<P><I>{Call.Transcription}</I></P>"
                })
                {
                    // Attach audio file if location is available
                    if (!string.IsNullOrEmpty(Call.Location))
                    {
                        var contentType = new ContentType();
                        contentType.MediaType = MediaTypeNames.Application.Octet;
                        contentType.Name = Path.GetFileName(Call.Location);
                        message.Attachments.Add(new Attachment(Call.Location, contentType));
                    }
                    smtp.Send(message);
                }
            }
        }
    }
}
