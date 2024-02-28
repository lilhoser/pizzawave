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
        private Action<TranscribedCall>? TranscriptionCompleteCallback;
        private Whisper m_Whisper;
        private Dictionary<Guid, AlertEvent> m_AlertEvents;
        private Settings m_Settings;

        public Alerter(
            Settings Settings,
            Whisper WhisperInstance,
            Action<TranscribedCall>? Callback)
        {
            m_Settings = Settings;
            m_Whisper = WhisperInstance;
            m_AlertEvents = new Dictionary<Guid, AlertEvent>();
            TranscriptionCompleteCallback = Callback;
        }

        public async Task NewCallDataAvailable(WavStreamData Data)
        {
            //
            // This routine is a callback invoked from a worker thread in StreamServer.cs
            // It is safe/OK to perform blocking calls here.
            // NOTE: This method is invoked PER CALL, and calls can happen in parallel.
            //
            try
            {
                var wavLocation = string.Empty;
                if (!string.IsNullOrEmpty(m_Settings.WavFileLocation))
                {
                    var baseDir = m_Settings.WavFileLocation;
                    var fileName = $"audio-{DateTime.Now:yyyy-MM-dd-HHmmss}.mp3";
                    Data.DumpStreamToFile(baseDir, fileName, OutputFileFormat.Mp3);
                    wavLocation = Path.Combine(baseDir, fileName);
                }
                var call = await m_Whisper.TranscribeCall(Data);
                call.Location = wavLocation;
                ProcessAlerts(call, Data);
                TranscriptionCompleteCallback?.Invoke(call);
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.Alerts, TraceEventType.Error, $"{ex.Message}");
                throw; // back up to worker thread
            }
        }

        private void ProcessAlerts(TranscribedCall Call, WavStreamData CallWavData)
        {
            foreach (var alert in m_Settings.Alerts)
            {
                Trace(TraceLoggerType.Alerts,
                      TraceEventType.Verbose,
                      $"Processing alert {alert.Name} for call ID {Call.CallId}");
                if (!alert.Enabled)
                {
                    Trace(TraceLoggerType.Alerts,
                          TraceEventType.Information,
                          $"Alert {alert.Name} is disabled, skipping.");
                    continue;
                }
                var keywords = alert.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                foreach (var keyword in keywords)
                {
                    if (Call.Transcription.ToLower().Contains(keyword.ToLower()))
                    {
                        Trace(TraceLoggerType.Alerts,
                              TraceEventType.Information,
                              $"Alert {alert.Name} keyword {keyword} matches call ID {Call.CallId} transcription.");
                        Call.IsAlertMatch = true;                        
                        TriggerAlertEvent(alert, Call, CallWavData);
                    }
                }
            }
        }

        private void TriggerAlertEvent(Alert Alert, TranscribedCall Call, WavStreamData CallWavData)
        {
            //
            // If this alert has triggered before, pull its record, otherwise create
            // create a new event tracker.
            //
            AlertEvent alertEvent;
            if (m_AlertEvents.ContainsKey(Alert.Id))
            {
                alertEvent = m_AlertEvents[Alert.Id];
            }
            else
            {
                alertEvent = new AlertEvent(Alert.Id);
            }

            //
            // Because this function can be invoked by multiple threads processing different
            // calls, we must acquire the AlertEvent lock exclusive (writer).
            //
            alertEvent.LockExclusive();

            try
            {
                if (alertEvent.LastTriggered != DateTime.MinValue)
                {
                    int diff;
                    switch (Alert.Frequency)
                    {
                        case AlertFrequency.Daily:
                            {
                                diff = (int)(DateTime.Now - alertEvent.LastTriggered).TotalDays;
                                break;
                            }
                        case AlertFrequency.Hourly:
                            {
                                diff = (int)(DateTime.Now - alertEvent.LastTriggered).TotalHours;
                                break;
                            }
                        case AlertFrequency.RealTime:
                            {
                                diff = (int)(DateTime.Now - alertEvent.LastTriggered).TotalSeconds;
                                if (alertEvent.TriggerCountLastInterval > AlertEvent.s_RealtimeThresholdPerInterval)
                                {
                                    if (diff <= AlertEvent.s_RealtimeIntervalSec)
                                    {
                                        Trace(TraceLoggerType.Alerts,
                                              TraceEventType.Warning,
                                              $"NOT triggering: Alert set to real-time has been triggered " +
                                              $"{alertEvent.TriggerCountLastInterval} times in the past {diff} seconds.");
                                        return;
                                    }
                                    alertEvent.TriggerCountLastInterval = 0; // reset the count
                                }
                                diff = 1;
                                break;
                            }
                        default:
                            {
                                Trace(TraceLoggerType.Alerts,
                                      TraceEventType.Error,
                                      $"Unrecognized alert frequency {Alert.Frequency}");
                                return;
                            }
                    }

                    if (diff < 1)
                    {
                        Trace(TraceLoggerType.Alerts,
                              TraceEventType.Warning,
                              $"NOT triggering: Alert set to {Alert.Frequency}, last triggered " +
                              $"{alertEvent.LastTriggered:M/d/yyyy h:mm tt}");
                        return;
                    }
                }

                alertEvent.LastTriggered = DateTime.Now;
                alertEvent.TriggerCountLastInterval++;

                if (Alert.CaptureWAV)
                {
                    //
                    // Only write the audio file once - either this was already done because the global
                    // setting to capture MP3 files was enabled, or a prior alert triggered for
                    // this call and wrote it.
                    //
                    if (string.IsNullOrEmpty(Call.Location))
                    {
                        var fileName = $"alert-audio-{DateTime.Now:yyyy-MM-dd-HHmmss}.mp3";
                        CallWavData.DumpStreamToFile(
                            Settings.DefaultAlertWavLocation, fileName, OutputFileFormat.Mp3);
                        Call.Location = Path.Combine(Settings.DefaultAlertWavLocation, fileName);
                    }
                }

                if (!string.IsNullOrEmpty(Alert.Email) &&
                    !string.IsNullOrEmpty(m_Settings.gmailUser) &&
                    !string.IsNullOrEmpty(m_Settings.gmailPassword))
                {
                    alertEvent.Unlock(); // don't hold lock, email could block
                    SendEmailNotification(Alert, alertEvent, Call);
                }
            }
            finally
            {
                alertEvent.Unlock();
            }
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
                    var contentType = new ContentType();
                    contentType.MediaType = MediaTypeNames.Application.Octet;
                    contentType.Name = Path.GetFileName(Call.Location);
                    message.Attachments.Add(new Attachment(Call.Location, contentType));
                    smtp.Send(message);
                }
            }
        }
    }
}
