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
using BrightIdeasSoftware;
using pizzalib;

namespace pizzaui
{
    public partial class AlertManagerWindow : Form
    {
        private bool m_SaveDisabled;
        private Settings m_Settings;

        public AlertManagerWindow(Settings Settings, bool SaveDisabled)
        {
            InitializeComponent();
            m_Settings = Settings;
            m_SaveDisabled = SaveDisabled;

            if (m_SaveDisabled)
            {
                addButton.Enabled = false;
                removeButton.Enabled = false;
            }

            Generator.GenerateColumns(alertsListview, typeof(Alert), true);
            Generator.GenerateColumns(talkgroupsListview, typeof(Talkgroup), true);

            //
            // Hide unwanted columns. Cannot annotate classes with [OLVColumn] for xplat.
            //
            var hidden = new List<string>() {
                "Id","Talkgroups" };
            foreach (var col in alertsListview.AllColumns)
            {
                if (hidden.Any(c => col.Name == c))
                {
                    col.IsVisible = false;
                }
            }

            //
            // Setup header styles
            //
            alertsListview.HeaderUsesThemes = false;
            alertsListview.HeaderFormatStyle = new HeaderFormatStyle();
            alertsListview.HeaderFormatStyle.Normal = new HeaderStateStyle()
            {
                BackColor = Color.Honeydew,
            };
            alertsListview.HeaderFormatStyle.Pressed = new HeaderStateStyle()
            {
                BackColor = Color.LightGreen,
            };
            alertsListview.HeaderFormatStyle.Hot = new HeaderStateStyle()
            {
                BackColor = Color.LimeGreen,
            };
            talkgroupsListview.HeaderUsesThemes = false;
            talkgroupsListview.HeaderFormatStyle = new HeaderFormatStyle();
            talkgroupsListview.HeaderFormatStyle.Normal = new HeaderStateStyle()
            {
                BackColor = Color.Honeydew,
            };
            talkgroupsListview.HeaderFormatStyle.Pressed = new HeaderStateStyle()
            {
                BackColor = Color.LightGreen,
            };
            talkgroupsListview.HeaderFormatStyle.Hot = new HeaderStateStyle()
            {
                BackColor = Color.LimeGreen,
            };
            alertsListview.RebuildColumns();
            alertsListview.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            alertsListview.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
        }

        private void LoadListviews()
        {
            talkgroupsListview.HideSelection = false;
            //
            // Load alerts from settings
            //
            alertsListview.SetObjects(m_Settings.Alerts);
            //
            // Load talkgroups from settings
            //
            talkgroupsListview.SetObjects(m_Settings.Talkgroups);

            alertsListview.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            alertsListview.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
            talkgroupsListview.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            talkgroupsListview.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
        }

        private Alert GetAlert()
        {
            var alert = new Alert();
            object? freq;
            if (alertFrequencyCombobox.SelectedItem == null ||
                string.IsNullOrEmpty((string)alertFrequencyCombobox.SelectedItem) ||
                !Enum.TryParse(typeof(AlertFrequency), (string)alertFrequencyCombobox.SelectedItem, out freq))
            {
                throw new Exception("Invalid alert frequency");
            }
            alert.Frequency = (AlertFrequency)freq;
            alert.Name = alertNameTextbox.Text;
            alert.Email = alertEmailTextbox.Text;
            alert.Keywords = alertKeywordsTextbox.Text;
            alert.Enabled = enableCheckbox.Checked;
            if (applyToSelectedRadioButton.Checked)
            {
                if (string.IsNullOrEmpty(talkgroupsTextbox.Text) ||
                    talkgroupsListview.SelectedObjects == null ||
                    talkgroupsListview.SelectedObjects.Count == 0)
                {
                    throw new Exception("At least one talkgroup must be selected");
                }
                var selected = talkgroupsListview.SelectedObjects.Cast<Talkgroup>().ToList();
                alert.Talkgroups.AddRange(selected!.Select(s => s.Id).ToArray());
            }
            alert.Validate();
            return alert;
        }

        private void LoadAlert(Alert alert)
        {
            if (alert.Talkgroups.Count > 0)
            {
                applyToSelectedRadioButton.Checked = true;
                applyToAllRadioButton.Checked = false;
                talkgroupsTextbox.Text = string.Join(',', alert.Talkgroups.ToArray());
            }
            else
            {
                applyToSelectedRadioButton.Checked = false;
                applyToAllRadioButton.Checked = true;
                talkgroupsTextbox.Enabled = false;
            }
            alertNameTextbox.Text = alert.Name;
            alertEmailTextbox.Text = alert.Email;
            alertKeywordsTextbox.Text = alert.Keywords;
            enableCheckbox.Checked = alert.Enabled;
            foreach (var item in alertFrequencyCombobox.Items)
            {
                if (!Enum.TryParse((string)item, out AlertFrequency value))
                {
                    continue;
                }
                if (value == alert.Frequency)
                {
                    alertFrequencyCombobox.SelectedItem = item;
                    break;
                }
            }
            addButton.Text = "Update";
            addButton.Tag = alert.Id;
        }

        private void ResetForm()
        {
            addButton.Tag = null;
            addButton.Text = "Add";
            applyToAllRadioButton.Checked = true;
            applyToSelectedRadioButton.Checked = false;
            talkgroupsTextbox.Enabled = false;
            talkgroupsTextbox.Text = "";
            alertNameTextbox.Text = "";
            alertEmailTextbox.Text = "";
            alertKeywordsTextbox.Text = "";
            enableCheckbox.Checked = true;
            alertFrequencyCombobox.SelectedIndex = 0;
        }

        private void addButton_Click(object sender, EventArgs e)
        {
            try
            {
                var data = GetAlert();
                //
                // Update operation
                //
                if (addButton.Tag != null)
                {
                    var alert = m_Settings.Alerts.Where(a => a.Id == (Guid)addButton.Tag).FirstOrDefault();
                    if (alert == null)
                    {
                        throw new Exception($"Unable to locate existing alert with ID {(Guid)addButton.Tag}.");
                    }
                    alert.Name = data.Name;
                    alert.Email = data.Email;
                    alert.Keywords = data.Keywords;
                    alert.Talkgroups = data.Talkgroups;
                    alert.Frequency = data.Frequency;
                    alert.Enabled = data.Enabled;
                    alert.Validate();
                }
                else
                {
                    m_Settings.Alerts.Add(data);
                }
                ResetForm();
                alertsListview.SetObjects(m_Settings.Alerts);
                alertsListview.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
                alertsListview.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{ex.Message}");
                return;
            }
        }

        private void removeButton_Click(object sender, EventArgs e)
        {
            if (alertsListview.SelectedObjects != null &&
                alertsListview.SelectedObjects.Count > 1)
            {
                MessageBox.Show("Select one or more alerts to remove.");
                return;
            }
            var selectedItems = alertsListview.SelectedObjects!.Cast<Alert>().ToList();
            foreach (var alert in selectedItems)
            {
                if (!m_Settings.Alerts.Remove(alert))
                {
                    throw new Exception($"Unable to remove alert with ID {alert.Id}.");
                }
            }
            alertsListview.RemoveObjects(selectedItems);
            ResetForm();
        }

        private void AlertManagerWindow_Shown(object sender, EventArgs e)
        {
            if (m_Settings.Talkgroups == null || m_Settings.Talkgroups.Count == 0)
            {
                MessageBox.Show("Unable to load alert manager: no talkgroups");
                Close();
                return;
            }

            LoadListviews();
            ResetForm();
        }

        private void alertsListview_SelectionChanged(object sender, EventArgs e)
        {
            if (alertsListview.SelectedObjects != null &&
                alertsListview.SelectedObjects.Count > 1)
            {
                //
                // Multi-select treated as a bulk remove operation
                //
                return;
            }

            if (alertsListview.SelectedObject == null)
            {
                ResetForm();
                return;
            }
            var alert = (Alert)alertsListview.SelectedObject;
            LoadAlert(alert);
            //
            // Select appropriate TG(s)
            //
            talkgroupsListview.SelectedObjects = new List<Talkgroup>();
            if (alert.Talkgroups.Count > 0)
            {
                talkgroupsListview.SelectedObjects = talkgroupsListview.Objects.Cast<Talkgroup>().Where(tg =>
                    alert.Talkgroups.Any(t => t == tg.Id)).ToList();
                talkgroupsListview.EnsureModelVisible(talkgroupsListview.SelectedObjects[0]);
            }
            talkgroupsListview.RefreshSelectedObjects();
        }

        private void applyToSelectedRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            talkgroupsTextbox.Enabled = true;
        }

        private void applyToAllRadioButton_CheckedChanged(object sender, EventArgs e)
        {
            talkgroupsTextbox.Text = "";
            talkgroupsTextbox.Enabled = false;
        }

        private void talkgroupsListview_SelectionChanged(object sender, EventArgs e)
        {
            if (talkgroupsListview.SelectedObjects == null)
            {
                talkgroupsTextbox.Text = "";
                return;
            }
            talkgroupsTextbox.Enabled = true;
            applyToSelectedRadioButton.Checked = true;
            applyToAllRadioButton.Checked = false;
            var selectedItems = talkgroupsListview.SelectedObjects.Cast<Talkgroup>().ToList();
            selectedItems.Sort();
            talkgroupsTextbox.Text = string.Join(',', selectedItems.Select(s => s.Id).ToArray());
        }

        private void AlertManagerWindow_FormClosing(object sender, FormClosingEventArgs e)
        {
            DialogResult = DialogResult.OK;
        }
    }
}
