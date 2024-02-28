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
namespace pizzaui
{
    public partial class AlertManagerWindow
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(AlertManagerWindow));
            alertsTabControl = new TabControl();
            tabPage3 = new TabPage();
            splitContainer1 = new SplitContainer();
            enableCheckbox = new CheckBox();
            captureWavCheckbox = new CheckBox();
            talkgroupsTextbox = new TextBox();
            label6 = new Label();
            alertNameTextbox = new TextBox();
            addButton = new Button();
            removeButton = new Button();
            alertsListview = new BrightIdeasSoftware.FastObjectListView();
            label5 = new Label();
            label4 = new Label();
            alertFrequencyCombobox = new ComboBox();
            label3 = new Label();
            label2 = new Label();
            alertEmailTextbox = new TextBox();
            applyToSelectedRadioButton = new RadioButton();
            applyToAllRadioButton = new RadioButton();
            label1 = new Label();
            alertKeywordsTextbox = new TextBox();
            label9 = new Label();
            talkgroupsListview = new BrightIdeasSoftware.FastObjectListView();
            alertsTabControl.SuspendLayout();
            tabPage3.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)splitContainer1).BeginInit();
            splitContainer1.Panel1.SuspendLayout();
            splitContainer1.Panel2.SuspendLayout();
            splitContainer1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)alertsListview).BeginInit();
            ((System.ComponentModel.ISupportInitialize)talkgroupsListview).BeginInit();
            SuspendLayout();
            // 
            // alertsTabControl
            // 
            alertsTabControl.Controls.Add(tabPage3);
            alertsTabControl.Dock = DockStyle.Fill;
            alertsTabControl.Location = new Point(0, 0);
            alertsTabControl.Name = "alertsTabControl";
            alertsTabControl.SelectedIndex = 0;
            alertsTabControl.Size = new Size(1832, 912);
            alertsTabControl.TabIndex = 10;
            // 
            // tabPage3
            // 
            tabPage3.Controls.Add(splitContainer1);
            tabPage3.Location = new Point(4, 34);
            tabPage3.Name = "tabPage3";
            tabPage3.Size = new Size(1824, 874);
            tabPage3.TabIndex = 2;
            tabPage3.Text = "Alerts";
            tabPage3.UseVisualStyleBackColor = true;
            // 
            // splitContainer1
            // 
            splitContainer1.Dock = DockStyle.Fill;
            splitContainer1.Location = new Point(0, 0);
            splitContainer1.Name = "splitContainer1";
            // 
            // splitContainer1.Panel1
            // 
            splitContainer1.Panel1.Controls.Add(enableCheckbox);
            splitContainer1.Panel1.Controls.Add(captureWavCheckbox);
            splitContainer1.Panel1.Controls.Add(talkgroupsTextbox);
            splitContainer1.Panel1.Controls.Add(label6);
            splitContainer1.Panel1.Controls.Add(alertNameTextbox);
            splitContainer1.Panel1.Controls.Add(addButton);
            splitContainer1.Panel1.Controls.Add(removeButton);
            splitContainer1.Panel1.Controls.Add(alertsListview);
            splitContainer1.Panel1.Controls.Add(label5);
            splitContainer1.Panel1.Controls.Add(label4);
            splitContainer1.Panel1.Controls.Add(alertFrequencyCombobox);
            splitContainer1.Panel1.Controls.Add(label3);
            splitContainer1.Panel1.Controls.Add(label2);
            splitContainer1.Panel1.Controls.Add(alertEmailTextbox);
            splitContainer1.Panel1.Controls.Add(applyToSelectedRadioButton);
            splitContainer1.Panel1.Controls.Add(applyToAllRadioButton);
            splitContainer1.Panel1.Controls.Add(label1);
            splitContainer1.Panel1.Controls.Add(alertKeywordsTextbox);
            splitContainer1.Panel1.Controls.Add(label9);
            // 
            // splitContainer1.Panel2
            // 
            splitContainer1.Panel2.Controls.Add(talkgroupsListview);
            splitContainer1.Size = new Size(1824, 874);
            splitContainer1.SplitterDistance = 890;
            splitContainer1.TabIndex = 26;
            // 
            // enableCheckbox
            // 
            enableCheckbox.AutoSize = true;
            enableCheckbox.Checked = true;
            enableCheckbox.CheckState = CheckState.Checked;
            enableCheckbox.Location = new Point(493, 339);
            enableCheckbox.Name = "enableCheckbox";
            enableCheckbox.Size = new Size(101, 29);
            enableCheckbox.TabIndex = 45;
            enableCheckbox.Text = "Enabled";
            enableCheckbox.UseVisualStyleBackColor = true;
            // 
            // captureWavCheckbox
            // 
            captureWavCheckbox.AutoSize = true;
            captureWavCheckbox.Checked = true;
            captureWavCheckbox.CheckState = CheckState.Checked;
            captureWavCheckbox.Location = new Point(493, 304);
            captureWavCheckbox.Name = "captureWavCheckbox";
            captureWavCheckbox.Size = new Size(143, 29);
            captureWavCheckbox.TabIndex = 44;
            captureWavCheckbox.Text = "Capture WAV";
            captureWavCheckbox.UseVisualStyleBackColor = true;
            // 
            // talkgroupsTextbox
            // 
            talkgroupsTextbox.Location = new Point(296, 24);
            talkgroupsTextbox.Name = "talkgroupsTextbox";
            talkgroupsTextbox.Size = new Size(573, 31);
            talkgroupsTextbox.TabIndex = 1;
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new Point(19, 82);
            label6.Margin = new Padding(4, 0, 4, 0);
            label6.Name = "label6";
            label6.Size = new Size(63, 25);
            label6.TabIndex = 43;
            label6.Text = "Name:";
            // 
            // alertNameTextbox
            // 
            alertNameTextbox.Location = new Point(124, 76);
            alertNameTextbox.Name = "alertNameTextbox";
            alertNameTextbox.Size = new Size(274, 31);
            alertNameTextbox.TabIndex = 2;
            // 
            // addButton
            // 
            addButton.Location = new Point(643, 304);
            addButton.Margin = new Padding(4, 5, 4, 5);
            addButton.Name = "addButton";
            addButton.Size = new Size(108, 46);
            addButton.TabIndex = 6;
            addButton.Text = "Add";
            addButton.UseVisualStyleBackColor = true;
            addButton.Click += addButton_Click;
            // 
            // removeButton
            // 
            removeButton.Location = new Point(759, 304);
            removeButton.Margin = new Padding(4, 5, 4, 5);
            removeButton.Name = "removeButton";
            removeButton.Size = new Size(108, 46);
            removeButton.TabIndex = 40;
            removeButton.Text = "Remove";
            removeButton.UseVisualStyleBackColor = true;
            removeButton.Click += removeButton_Click;
            // 
            // alertsListview
            // 
            alertsListview.Dock = DockStyle.Bottom;
            alertsListview.FullRowSelect = true;
            alertsListview.GridLines = true;
            alertsListview.Location = new Point(0, 386);
            alertsListview.Name = "alertsListview";
            alertsListview.ShowGroups = false;
            alertsListview.Size = new Size(890, 488);
            alertsListview.TabIndex = 39;
            alertsListview.View = View.Details;
            alertsListview.VirtualMode = true;
            alertsListview.SelectionChanged += alertsListview_SelectionChanged;
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Font = new Font("Segoe UI", 9F, FontStyle.Italic);
            label5.Location = new Point(28, 187);
            label5.Margin = new Padding(4, 0, 4, 0);
            label5.Name = "label5";
            label5.Size = new Size(89, 25);
            label5.TabIndex = 37;
            label5.Text = "separated";
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Font = new Font("Segoe UI", 9F, FontStyle.Italic);
            label4.Location = new Point(28, 162);
            label4.Margin = new Padding(4, 0, 4, 0);
            label4.Name = "label4";
            label4.Size = new Size(77, 25);
            label4.TabIndex = 36;
            label4.Text = "comma-";
            // 
            // alertFrequencyCombobox
            // 
            alertFrequencyCombobox.DropDownStyle = ComboBoxStyle.DropDownList;
            alertFrequencyCombobox.FormattingEnabled = true;
            alertFrequencyCombobox.Items.AddRange(new object[] { "RealTime", "Hourly", "Daily" });
            alertFrequencyCombobox.Location = new Point(124, 312);
            alertFrequencyCombobox.Margin = new Padding(4, 5, 4, 5);
            alertFrequencyCombobox.Name = "alertFrequencyCombobox";
            alertFrequencyCombobox.Size = new Size(274, 33);
            alertFrequencyCombobox.TabIndex = 5;
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(19, 317);
            label3.Margin = new Padding(4, 0, 4, 0);
            label3.Name = "label3";
            label3.Size = new Size(97, 25);
            label3.TabIndex = 34;
            label3.Text = "Frequency:";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(442, 79);
            label2.Margin = new Padding(4, 0, 4, 0);
            label2.Name = "label2";
            label2.Size = new Size(58, 25);
            label2.TabIndex = 33;
            label2.Text = "Email:";
            // 
            // alertEmailTextbox
            // 
            alertEmailTextbox.Location = new Point(526, 76);
            alertEmailTextbox.Name = "alertEmailTextbox";
            alertEmailTextbox.Size = new Size(343, 31);
            alertEmailTextbox.TabIndex = 3;
            // 
            // applyToSelectedRadioButton
            // 
            applyToSelectedRadioButton.AutoSize = true;
            applyToSelectedRadioButton.Location = new Point(187, 27);
            applyToSelectedRadioButton.Name = "applyToSelectedRadioButton";
            applyToSelectedRadioButton.Size = new Size(103, 29);
            applyToSelectedRadioButton.TabIndex = 31;
            applyToSelectedRadioButton.Text = "Selected";
            applyToSelectedRadioButton.UseVisualStyleBackColor = true;
            applyToSelectedRadioButton.CheckedChanged += applyToSelectedRadioButton_CheckedChanged;
            // 
            // applyToAllRadioButton
            // 
            applyToAllRadioButton.AutoSize = true;
            applyToAllRadioButton.Checked = true;
            applyToAllRadioButton.Location = new Point(124, 27);
            applyToAllRadioButton.Name = "applyToAllRadioButton";
            applyToAllRadioButton.Size = new Size(57, 29);
            applyToAllRadioButton.TabIndex = 30;
            applyToAllRadioButton.TabStop = true;
            applyToAllRadioButton.Text = "All";
            applyToAllRadioButton.UseVisualStyleBackColor = true;
            applyToAllRadioButton.CheckedChanged += applyToAllRadioButton_CheckedChanged;
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(19, 128);
            label1.Margin = new Padding(4, 0, 4, 0);
            label1.Name = "label1";
            label1.Size = new Size(103, 25);
            label1.TabIndex = 29;
            label1.Text = "Keyword(s):";
            // 
            // alertKeywordsTextbox
            // 
            alertKeywordsTextbox.Location = new Point(124, 125);
            alertKeywordsTextbox.Multiline = true;
            alertKeywordsTextbox.Name = "alertKeywordsTextbox";
            alertKeywordsTextbox.Size = new Size(745, 170);
            alertKeywordsTextbox.TabIndex = 4;
            // 
            // label9
            // 
            label9.AutoSize = true;
            label9.Location = new Point(19, 27);
            label9.Margin = new Padding(4, 0, 4, 0);
            label9.Name = "label9";
            label9.Size = new Size(102, 25);
            label9.TabIndex = 27;
            label9.Text = "Talkgroups:";
            // 
            // talkgroupsListview
            // 
            talkgroupsListview.Dock = DockStyle.Fill;
            talkgroupsListview.FullRowSelect = true;
            talkgroupsListview.GridLines = true;
            talkgroupsListview.Location = new Point(0, 0);
            talkgroupsListview.Name = "talkgroupsListview";
            talkgroupsListview.ShowGroups = false;
            talkgroupsListview.Size = new Size(930, 874);
            talkgroupsListview.TabIndex = 0;
            talkgroupsListview.View = View.Details;
            talkgroupsListview.VirtualMode = true;
            talkgroupsListview.SelectionChanged += talkgroupsListview_SelectionChanged;
            // 
            // AlertManagerWindow
            // 
            AutoScaleDimensions = new SizeF(10F, 25F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1832, 912);
            Controls.Add(alertsTabControl);
            DoubleBuffered = true;
            Icon = (Icon)resources.GetObject("$this.Icon");
            Margin = new Padding(4, 5, 4, 5);
            Name = "AlertManagerWindow";
            StartPosition = FormStartPosition.CenterParent;
            Text = "Alert Manager";
            FormClosing += AlertManagerWindow_FormClosing;
            Shown += AlertManagerWindow_Shown;
            alertsTabControl.ResumeLayout(false);
            tabPage3.ResumeLayout(false);
            splitContainer1.Panel1.ResumeLayout(false);
            splitContainer1.Panel1.PerformLayout();
            splitContainer1.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)splitContainer1).EndInit();
            splitContainer1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)alertsListview).EndInit();
            ((System.ComponentModel.ISupportInitialize)talkgroupsListview).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private TabControl alertsTabControl;
        private TabPage tabPage3;
        private SplitContainer splitContainer1;
        private Label label6;
        private TextBox alertNameTextbox;
        private Button addButton;
        private Button removeButton;
        private BrightIdeasSoftware.FastObjectListView alertsListview;
        private Label label5;
        private Label label4;
        private ComboBox alertFrequencyCombobox;
        private Label label3;
        private Label label2;
        private TextBox alertEmailTextbox;
        private RadioButton applyToSelectedRadioButton;
        private RadioButton applyToAllRadioButton;
        private Label label1;
        private TextBox alertKeywordsTextbox;
        private Label label9;
        private BrightIdeasSoftware.FastObjectListView talkgroupsListview;
        private TextBox talkgroupsTextbox;
        private CheckBox captureWavCheckbox;
        private CheckBox enableCheckbox;
    }
}