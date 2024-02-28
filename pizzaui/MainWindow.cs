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
using Newtonsoft.Json;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using System.Globalization;
using pizzalib;

namespace pizzaui
{
    using static TraceLogger;
    using Whisper = pizzalib.Whisper;

    public partial class MainWindow : Form
    {
        private bool m_ShutdownComplete;
        private bool m_ListenerStarted;
        private bool m_CloseFormRequested;
        private AudioPlayer m_AudioPlayer;
        private StreamServer? m_StreamServer;
        private Whisper? m_Whisper;
        private Alerter? m_Alerter;
        private Settings m_Settings;

        public MainWindow()
        {
            InitializeComponent();
            TraceLogger.Initialize();
            m_AudioPlayer = new AudioPlayer();
            string settingsPath = pizzalib.Settings.DefaultSettingsFileLocation;
            if (!File.Exists(settingsPath))
            {
                Trace(TraceLoggerType.MainWindow,
                      TraceEventType.Warning,
                      $"Settings file {settingsPath} does not exist, loading default...");
                m_Settings = new Settings();
                SetUiCallbacks();
                m_Settings.SaveToFile(string.Empty); // persist it
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(settingsPath);
                    m_Settings = (Settings)JsonConvert.DeserializeObject(json, typeof(Settings))!;
                    SetUiCallbacks();
                }
                catch (Exception ex)
                {
                    Trace(TraceLoggerType.MainWindow,
                          TraceEventType.Error,
                          $"Unable to load settings {settingsPath}: {ex.Message}");
                    m_Settings = new Settings();
                    SetUiCallbacks();
                    return;
                }
            }
        }

        private async void MainWindow_Shown(object sender, EventArgs e)
        {
            try
            {
                m_Whisper = new Whisper(m_Settings);
                m_Alerter = new Alerter(m_Settings, m_Whisper, NewCallTranscribed);
                m_StreamServer = new StreamServer(m_Alerter.NewCallDataAvailable, m_Settings);
                _ = await ApplyNewSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to apply settings: {ex.Message}");
                return;
            }
        }

        private void MainWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            m_AudioPlayer.Shutdown();
            TraceLogger.Shutdown();
            if (m_ListenerStarted && !m_ShutdownComplete)
            {
                m_CloseFormRequested = true;
                m_StreamServer?.Shutdown();
                e.Cancel = true;
            }
        }

        #region file menuitem handlers

        private void exportJSONToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (transcriptionListview.FilteredObjects == null ||
                transcriptionListview.FilteredObjects.Cast<TranscribedCall>().Count() == 0)
            {
                return;
            }

            SaveFileDialog sfd = new SaveFileDialog();
            sfd.OverwritePrompt = true;
            sfd.DefaultExt = "JSON";
            sfd.Filter = "JSON File (*.json)|*.json";

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            var jsonContents = new StringBuilder();
            try
            {                
                foreach (var obj in transcriptionListview.FilteredObjects)
                {
                    var call = (TranscribedCall)obj;
                    var jsonObject = JsonConvert.SerializeObject(call, Formatting.Indented);
                    jsonContents.AppendLine(jsonObject);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export JSON: {ex.Message}");
                return;
            }
            try
            {
                File.WriteAllText(sfd.FileName, jsonContents.ToString());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save JSON: {ex.Message}");
                return;
            }

            UpdateProgressLabel($"JSON exported to {sfd.FileName}");
        }

        private void exportCSVToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (transcriptionListview.FilteredObjects == null ||
                transcriptionListview.FilteredObjects.Cast<TranscribedCall>().Count() == 0)
            {
                return;
            }
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.OverwritePrompt = true;
            sfd.DefaultExt = "CSV";
            sfd.Filter = "CSV File (*.csv)|*.csv";

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                return;
            }
            try
            {
                var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,  
                };

                var filteredRecords = transcriptionListview.FilteredObjects.Cast<TranscribedCall>().ToList();
                using (var writer = new StreamWriter(sfd.FileName, false, Encoding.UTF8))
                {
                    var csv = new CsvWriter(writer, configuration, false);
                    csv.WriteRecords(filteredRecords);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export CSV: {ex.Message}");
                return;
            }
            UpdateProgressLabel($"CSV exported to {sfd.FileName}");
        }

        private void saveSettingsAsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog();
            sfd.OverwritePrompt = true;
            sfd.DefaultExt = "JSON";
            sfd.Filter = "JSON File (*.json)|*.json";

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            try
            {
                m_Settings.SaveToFile(sfd.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to save settings to {sfd.FileName}: {ex.Message}");
            }
            UpdateProgressLabel($"Settings saved to {sfd.FileName}");
        }

        private async void openSettingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (m_ListenerStarted)
            {
                MessageBox.Show("Please stop the server before loading new settings.");
                return;
            }

            OpenFileDialog dialog = new OpenFileDialog();
            dialog.CheckFileExists = true;
            dialog.CheckPathExists = true;
            dialog.Multiselect = false;

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(dialog.FileName);
                m_Settings = (Settings)JsonConvert.DeserializeObject(json, typeof(Settings))!;
                _ = await ApplyNewSettings();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to load settings from {dialog.FileName}: {ex.Message}");
            }
            UpdateProgressLabel($"Settings loaded from {dialog.FileName}");
        }

        private void startToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!m_ListenerStarted)
            {
                StartServer();
            }
        }

        private void stopToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (m_ListenerStarted)
            {
                UpdateProgressLabel("Stopping server...");
                stopToolStripMenuItem.Enabled = false;
                m_StreamServer?.Shutdown();
            }
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Application.Exit();
        }

        #endregion

        #region edit menuitem handlers

        private void alertsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            //
            // While it's possible to allow alert creation without talkgroups, it makes the
            // experience much worse and adds unnecessary complexity.
            //
            if (m_Settings.talkgroups == null || m_Settings.talkgroups.Count == 0)
            {
                MessageBox.Show("Please import talkgroups in the Settings Window before creating alerts.");
                return;
            }

            var window = new AlertManagerWindow(m_Settings, m_ListenerStarted);
            if (window.ShowDialog() == DialogResult.OK)
            {
                PersistSettingsSilent();
            }
        }

        private void settingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var window = new SettingsWindow(m_Settings, m_ListenerStarted);
            if (window.ShowDialog() == DialogResult.OK)
            {
                //
                // DialogResult.OK means at least something changed in Settings.
                //
                m_Settings = window.m_UpdatedSettings;
                _ = ApplyNewSettings();
            }
        }

        #endregion

        #region view menuitem handlers

        private void clearToolStripMenuItem_Click(object sender, EventArgs e)
        {
            DialogResult result = MessageBox.Show(
                "Clear all call records?",
                "Confirmation",
                MessageBoxButtons.YesNoCancel);
            if (result != DialogResult.Yes)
            {
                return;
            }
            transcriptionListview.ClearObjects();
        }

        private void showAlertMatchesOnlyToolStripMenuItem_Click(object sender, EventArgs e)
        {
            m_Settings.ShowAlertMatchesOnly = showAlertMatchesOnlyToolStripMenuItem.Checked;
            PersistSettingsSilent();
            if (m_Settings.ShowAlertMatchesOnly)
            {
                ShowOnlyAlertMatches();
            }
            else
            {
                ShowAllCalls();
            }
        }

        #region group by

        private void alphaTagToolStripMenuItem_Click(object sender, EventArgs e)
        {
            alphaTagToolStripMenuItem.Checked = true;
            m_Settings.GroupingStrategy = CallDisplayGrouping.AlphaTag;
            PersistSettingsSilent();
            ApplyGroupByToForm();
        }

        private void tagToolStripMenuItem_Click(object sender, EventArgs e)
        {
            tagToolStripMenuItem.Checked = true;
            m_Settings.GroupingStrategy = CallDisplayGrouping.Tag;
            PersistSettingsSilent();
            ApplyGroupByToForm();
        }

        private void descriptionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            descriptionToolStripMenuItem.Checked = true;
            m_Settings.GroupingStrategy = CallDisplayGrouping.Description;
            PersistSettingsSilent();
            ApplyGroupByToForm();
        }

        private void categoryToolStripMenuItem_Click(object sender, EventArgs e)
        {
            categoryToolStripMenuItem.Checked = true;
            m_Settings.GroupingStrategy = CallDisplayGrouping.Category;
            PersistSettingsSilent();
            ApplyGroupByToForm();
        }

        private void offGroupByToolStripMenuItem_Click(object sender, EventArgs e)
        {
            offGroupByToolStripMenuItem.Checked = true;
            m_Settings.GroupingStrategy = CallDisplayGrouping.Off;
            PersistSettingsSilent();
            ApplyGroupByToForm();
        }

        private void ApplyGroupByToForm()
        {
            var allItems = new Dictionary<CallDisplayGrouping, ToolStripMenuItem>() {
                { CallDisplayGrouping.Off, offGroupByToolStripMenuItem },
                { CallDisplayGrouping.AlphaTag, alphaTagToolStripMenuItem },
                { CallDisplayGrouping.Tag, tagToolStripMenuItem },
                { CallDisplayGrouping.Category, categoryToolStripMenuItem },
                { CallDisplayGrouping.Description, descriptionToolStripMenuItem },
            };
            var selectedItem = allItems[m_Settings.GroupingStrategy];
            selectedItem.Checked = true;
            foreach (var kvp in allItems)
            {
                if (kvp.Key == m_Settings.GroupingStrategy)
                {
                    continue;
                }
                kvp.Value.Checked = false;
            }
            SetGroupingStrategy(); // apply to the listview
        }
        #endregion

        #endregion

        #region diagnostics menuitem handlers

        private void openLogsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var psi = new ProcessStartInfo();
            psi.FileName = Settings.DefaultWorkingDirectory;
            psi.WorkingDirectory = Settings.DefaultWorkingDirectory;
            psi.UseShellExecute = true;
            Process.Start(psi);
        }

        #region trace level
        private void offTraceLevelToolStripMenuItem_Click(object sender, EventArgs e)
        {
            offTraceLevelToolStripMenuItem.Checked = true;
            m_Settings.TraceLevelApp = SourceLevels.Off;
            PersistSettingsSilent();
            TraceLogger.SetLevel(m_Settings.TraceLevelApp);
            ApplyTraceLevelToForm();
        }

        private void errorToolStripMenuItem_Click(object sender, EventArgs e)
        {
            errorToolStripMenuItem.Checked = true;
            m_Settings.TraceLevelApp = SourceLevels.Error;
            PersistSettingsSilent();
            TraceLogger.SetLevel(m_Settings.TraceLevelApp);
            ApplyTraceLevelToForm();
        }

        private void warningToolStripMenuItem_Click(object sender, EventArgs e)
        {
            warningToolStripMenuItem.Checked = true;
            m_Settings.TraceLevelApp = SourceLevels.Warning;
            PersistSettingsSilent();
            TraceLogger.SetLevel(m_Settings.TraceLevelApp);
            ApplyTraceLevelToForm();
        }

        private void informationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            informationToolStripMenuItem.Checked = true;
            m_Settings.TraceLevelApp = SourceLevels.Information;
            PersistSettingsSilent();
            TraceLogger.SetLevel(m_Settings.TraceLevelApp);
            ApplyTraceLevelToForm();
        }

        private void verboseToolStripMenuItem_Click(object sender, EventArgs e)
        {
            verboseToolStripMenuItem.Checked = true;
            m_Settings.TraceLevelApp = SourceLevels.Verbose;
            PersistSettingsSilent();
            TraceLogger.SetLevel(m_Settings.TraceLevelApp);
            ApplyTraceLevelToForm();
        }

        private void ApplyTraceLevelToForm()
        {
            var allItems = new Dictionary<SourceLevels, ToolStripMenuItem>() {
                {  SourceLevels.Off, offTraceLevelToolStripMenuItem },
                { SourceLevels.Error, errorToolStripMenuItem },
                { SourceLevels.Warning, warningToolStripMenuItem },
                { SourceLevels.Information, informationToolStripMenuItem },
                { SourceLevels.Verbose, verboseToolStripMenuItem },
            };
            var selectedItem = allItems[m_Settings.TraceLevelApp];
            selectedItem.Checked = true;
            foreach (var kvp in allItems)
            {
                if (kvp.Key == m_Settings.TraceLevelApp)
                {
                    continue;
                }
                kvp.Value.Checked = false;
            }
        }
        #endregion

        #endregion

        #region tools menuitem handlers
        private void transcriptionQualityToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void findTalkgroupsToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }

        private void cleanupToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var alertsDir = Settings.DefaultAlertWavLocation;
            var logsDir = TraceLogger.m_TraceFileDir;
            var dirs = new List<string>() { alertsDir, logsDir };
            var numDeleted = 0;
            foreach (var dir in dirs)
            {
                foreach (var file in Directory.GetFiles(dir))
                {
                    try
                    {
                        File.Delete(file);
                        numDeleted++;
                    }
                    catch { }
                }
            }
            MessageBox.Show($"Deleted {numDeleted} files.");
        }
        #endregion

        #region help menuitem handlers
        private void githubToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Utilities.LaunchBrowser(@"http://www.github.com/lilhoser/pizzawave");
        }
        #endregion

        #region settings management

        private async Task<bool> ApplyNewSettings()
        {
            if (m_ListenerStarted)
            {
                MessageBox.Show("Cannot apply new settings when server is active.");
                return false;
            }

            //
            // This routine can be called:
            //  by MainWindow_Shown on app startup
            //  when SettingsWindow has been closed
            //  when user selects File->Load settings...
            // In all cases, the new settings must be already applied in m_Settings.
            // It's important that the server is not active.
            //
            TraceLogger.SetLevel(m_Settings.TraceLevelApp);
            ApplySettingsToForm();
            InitializeListview();

            try
            {
                _ = await m_Whisper!.Initialize();
            }
            catch (Exception ex)
            {
                Trace(TraceLoggerType.MainWindow, TraceEventType.Error, $"{ex.Message}");
                return false;
            }

            if (m_Settings.AutostartListener)
            {
                StartServer();
            }

            return true;
        }

        private void ApplySettingsToForm()
        {
            //
            // See settings.cs - only some of the settings are managed from MainWindow
            //
            ApplyTraceLevelToForm();
            ApplyGroupByToForm();
            showAlertMatchesOnlyToolStripMenuItem.Checked = m_Settings.ShowAlertMatchesOnly;
        }

        private void PersistSettingsSilent()
        {
            try
            {
                m_Settings.SaveToFile(string.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex.Message}");
                Trace(TraceLoggerType.MainWindow,
                      TraceEventType.Error,
                      $"Unable to persist a settings change: {ex.Message}");
            }
        }

        #endregion

        #region stream server management

        private void StartServer()
        {
            startToolStripMenuItem.Enabled = false;
            stopToolStripMenuItem.Enabled = true;
            UpdateProgressLabel("Server started");
            Task.Run(async () =>
            {
                try
                {
                    m_ListenerStarted = true;
                    var result = await m_StreamServer!.Listen();
                    if (result)
                    {
                        m_ShutdownComplete = true;
                        if (m_CloseFormRequested)
                        {
                            this.Invoke((MethodInvoker)(() => Close()));
                            return;
                        }
                    }
                    m_ListenerStarted = false;
                    startToolStripMenuItem.Enabled = true;
                    stopToolStripMenuItem.Enabled = false;
                    UpdateProgressLabel("Server stopped");
                    UpdateConnectionLabel("Not connected");
                }
                catch (Exception ex)
                {
                    m_ListenerStarted = false;
                    Trace(TraceLoggerType.MainWindow, TraceEventType.Error, $"{ex.Message}");
                }
            });
        }
        #endregion

        #region process call transcription data

        private void NewCallTranscribed(TranscribedCall Call)
        {
            transcriptionListview.Invoke((MethodInvoker)(() =>
            {
                var focused = transcriptionListview.FocusedObject;
                var focusedItem = transcriptionListview.FocusedItem;
                transcriptionListview.AddObject(Call);
                if (focused != null)
                {
                    transcriptionListview.EnsureModelVisible(focused);
                    transcriptionListview.FocusedObject = focused;
                    transcriptionListview.TopItem = focusedItem;
                }
            }));
        }

        #endregion

        #region UI progress callbacks

        private void SetUiCallbacks()
        {
            m_Settings.UpdateConnectionLabelCallback = UpdateConnectionLabel;
            m_Settings.UpdateProgressLabelCallback = UpdateProgressLabel;
            m_Settings.ProgressBarStepCallback = ProgressBarStep;
            m_Settings.HideProgressBarCallback = HideProgressBar;
            m_Settings.SetProgressBarCallback = SetProgressBar;
        }

        public void UpdateProgressLabel(string Message)
        {
            statusStrip1.Invoke((MethodInvoker)(() =>
            {
                toolStripStatusLabel1.Visible = true;
                toolStripStatusLabel1.Text = Message;
            }));
        }

        public void UpdateConnectionLabel(string Message)
        {
            statusStrip1.Invoke((MethodInvoker)(() =>
            {
                toolStripConnectionLabel.Visible = true;
                toolStripConnectionLabel.Text = Message;
            }));
        }

        public void ProgressBarStep()
        {
            statusStrip1.Invoke((MethodInvoker)(() =>
            {
                toolStripProgressBar1.PerformStep();
                if (toolStripProgressBar1.Value == toolStripProgressBar1.Maximum)
                {
                    HideProgressBar(); // all done
                }
            }));
        }

        public void SetProgressBar(int TotalStep, int Step)
        {
            statusStrip1.Invoke((MethodInvoker)(() =>
            {
                toolStripProgressBar1.Visible = true;
                toolStripProgressBar1.Maximum = TotalStep;
                toolStripProgressBar1.Step = Step;
                toolStripProgressBar1.Value = 0;
            }));
        }

        public void HideProgressBar()
        {
            statusStrip1.Invoke((MethodInvoker)(() =>
            {
                toolStripProgressBar1.Visible = false;
                toolStripProgressBar1.Maximum = 0;
                toolStripProgressBar1.Step = 0;
                toolStripProgressBar1.Value = 0;
            }));
        }
        #endregion
    }
}
