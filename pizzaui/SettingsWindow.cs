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
using pizzalib;

namespace pizzaui
{
    public partial class SettingsWindow : Form
    {
        private Settings m_OriginalSettings;
        private List<Talkgroup> m_LoadedTalkgroups;
        private bool m_SaveDisabled;
        public Settings m_UpdatedSettings;

        public SettingsWindow(Settings CurrentSettings, bool SaveDisabled)
        {
            InitializeComponent();

            m_SaveDisabled = SaveDisabled;
            m_UpdatedSettings = new Settings();

            if (m_SaveDisabled)
            {
                foreach (var tab in settingsTabControl.TabPages)
                {
                    foreach (var control in ((TabPage)tab).Controls)
                    {
                        ((Control)control).Enabled = false;
                    }
                }
                saveButton.Enabled = false;
            }

            m_LoadedTalkgroups = CurrentSettings.talkgroups;

            if (m_LoadedTalkgroups != null)
            {
                talkgroupCountLabel.Text = $"Loaded {m_LoadedTalkgroups.Count} talkgroups";
            }

            //
            // Application settings
            //
            autostartListenerCheckbox.Checked = CurrentSettings.AutostartListener;
            gmailUserTextbox.Text = CurrentSettings.gmailUser;
            if (!string.IsNullOrEmpty(CurrentSettings.gmailPassword))
            {
                gmailAppPasswordTextbox.Text = CurrentSettings.gmailPassword;
            }

            //
            // TrunkRecorder settings
            //
            listenPortTextbox.Text = $"{CurrentSettings.listenPort}";
            samplingRateTextbox.Text = $"{CurrentSettings.analogSamplingRate}";
            numChannelsTextbox.Text = $"{CurrentSettings.analogChannels}";
            bitDepthTextbox.Text = $"{CurrentSettings.analogBitDepth}";
            //
            // Whisper.net settings
            //
            whisperModelFileTextbox.Text = CurrentSettings.whisperModelFile;

            m_OriginalSettings = CurrentSettings;
        }

        private Settings GetSettings()
        {
            var settings = new Settings();

            //
            // Important: we need to preserve settings that are not managed from
            // SettingsWindow, or they'll be lost. See settings.cs for details.
            //
            settings.Alerts = m_OriginalSettings.Alerts;
            settings.TraceLevelApp = m_OriginalSettings.TraceLevelApp;
            settings.GroupingStrategy = m_OriginalSettings.GroupingStrategy;
            settings.ShowAlertMatchesOnly = m_OriginalSettings.ShowAlertMatchesOnly;

            //
            // Application settings
            //
            settings.AutostartListener = autostartListenerCheckbox.Checked;
            settings.gmailUser = gmailUserTextbox.Text;
            settings.gmailPassword = gmailAppPasswordTextbox.Text;
            //
            // TrunkRecorder settings
            //
            if (!int.TryParse(listenPortTextbox.Text, out settings.listenPort))
            {
                throw new Exception("Invalid listen port");
            }
            if (!int.TryParse(samplingRateTextbox.Text, out settings.analogSamplingRate))
            {
                throw new Exception("Invalid sampling rate");
            }
            if (!int.TryParse(numChannelsTextbox.Text, out settings.analogChannels))
            {
                throw new Exception("Invalid analog channels");
            }
            if (!int.TryParse(bitDepthTextbox.Text, out settings.analogBitDepth))
            {
                throw new Exception("Invalid bit depth");
            }
            settings.talkgroups = m_LoadedTalkgroups;
            //
            // Whisper.net settings
            //
            settings.whisperModelFile = whisperModelFileTextbox.Text;
            return settings;
        }

        public bool HasNewSettings()
        {
            try
            {
                return !(GetSettings() == m_OriginalSettings);
            }
            catch
            {
                return true;
            }
        }

        private void saveButton_Click(object sender, EventArgs e)
        {
            if (!HasNewSettings())
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            try
            {
                var newSettings = GetSettings();
                newSettings.SaveToFile(string.Empty);
                m_UpdatedSettings = newSettings;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex.Message}");
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void browseButton2_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.CheckFileExists = true;
            dialog.CheckPathExists = true;
            dialog.Multiselect = false;

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            whisperModelFileTextbox.Text = dialog.FileName;
        }

        private void browseButton3_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.CheckFileExists = true;
            dialog.CheckPathExists = true;
            dialog.Multiselect = false;

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            var location = dialog.FileName;
            try
            {
                var tgs = TalkgroupHelper.GetTalkgroupsFromCsv(location);
                if (tgs.Count == 0)
                {
                    throw new Exception($"No data in file");
                }
                m_LoadedTalkgroups = tgs;
                talkgroupCountLabel.Text = $"Loaded {m_LoadedTalkgroups.Count} talkgroups";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to parse talkgroup CSV '{location}': {ex.Message}");
            }
        }
    }
}
