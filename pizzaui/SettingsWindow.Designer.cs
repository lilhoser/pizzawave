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
    partial class SettingsWindow
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(SettingsWindow));
            saveButton = new Button();
            tableLayoutPanel1 = new TableLayoutPanel();
            settingsTabControl = new TabControl();
            tabPage3 = new TabPage();
            gmailAppPasswordTextbox = new TextBox();
            label12 = new Label();
            gmailUserTextbox = new TextBox();
            label11 = new Label();
            autostartListenerCheckbox = new CheckBox();
            wavOutputLocationTextbox = new TextBox();
            label7 = new Label();
            browseButton = new Button();
            tabPage1 = new TabPage();
            talkgroupCountLabel = new Label();
            label10 = new Label();
            browseButton3 = new Button();
            samplingRateTextbox = new TextBox();
            bitDepthTextbox = new TextBox();
            label6 = new Label();
            label5 = new Label();
            label4 = new Label();
            numChannelsTextbox = new TextBox();
            label2 = new Label();
            listenPortTextbox = new TextBox();
            label1 = new Label();
            tabPage2 = new TabPage();
            browseButton2 = new Button();
            whisperModelFileTextbox = new TextBox();
            label8 = new Label();
            tableLayoutPanel1.SuspendLayout();
            settingsTabControl.SuspendLayout();
            tabPage3.SuspendLayout();
            tabPage1.SuspendLayout();
            tabPage2.SuspendLayout();
            SuspendLayout();
            // 
            // saveButton
            // 
            saveButton.Anchor = AnchorStyles.None;
            saveButton.Location = new Point(301, 820);
            saveButton.Margin = new Padding(4, 5, 4, 5);
            saveButton.Name = "saveButton";
            saveButton.Size = new Size(164, 84);
            saveButton.TabIndex = 5;
            saveButton.Text = "Save";
            saveButton.UseVisualStyleBackColor = true;
            saveButton.Click += saveButton_Click;
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 1;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            tableLayoutPanel1.Controls.Add(settingsTabControl, 0, 0);
            tableLayoutPanel1.Controls.Add(saveButton, 0, 1);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 0);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 2;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 89.03509F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 10.9649124F));
            tableLayoutPanel1.Size = new Size(766, 912);
            tableLayoutPanel1.TabIndex = 9;
            // 
            // settingsTabControl
            // 
            settingsTabControl.Controls.Add(tabPage3);
            settingsTabControl.Controls.Add(tabPage1);
            settingsTabControl.Controls.Add(tabPage2);
            settingsTabControl.Dock = DockStyle.Fill;
            settingsTabControl.Location = new Point(3, 3);
            settingsTabControl.Name = "settingsTabControl";
            settingsTabControl.SelectedIndex = 0;
            settingsTabControl.Size = new Size(760, 806);
            settingsTabControl.TabIndex = 9;
            // 
            // tabPage3
            // 
            tabPage3.Controls.Add(gmailAppPasswordTextbox);
            tabPage3.Controls.Add(label12);
            tabPage3.Controls.Add(gmailUserTextbox);
            tabPage3.Controls.Add(label11);
            tabPage3.Controls.Add(autostartListenerCheckbox);
            tabPage3.Controls.Add(wavOutputLocationTextbox);
            tabPage3.Controls.Add(label7);
            tabPage3.Controls.Add(browseButton);
            tabPage3.Location = new Point(4, 34);
            tabPage3.Name = "tabPage3";
            tabPage3.Size = new Size(752, 768);
            tabPage3.TabIndex = 2;
            tabPage3.Text = "PizzaWave";
            tabPage3.UseVisualStyleBackColor = true;
            // 
            // gmailAppPasswordTextbox
            // 
            gmailAppPasswordTextbox.Location = new Point(196, 118);
            gmailAppPasswordTextbox.Margin = new Padding(4, 5, 4, 5);
            gmailAppPasswordTextbox.Name = "gmailAppPasswordTextbox";
            gmailAppPasswordTextbox.Size = new Size(373, 31);
            gmailAppPasswordTextbox.TabIndex = 6;
            // 
            // label12
            // 
            label12.AutoSize = true;
            label12.Location = new Point(7, 118);
            label12.Margin = new Padding(4, 0, 4, 0);
            label12.Name = "label12";
            label12.Size = new Size(179, 25);
            label12.TabIndex = 28;
            label12.Text = "Gmail app password:";
            // 
            // gmailUserTextbox
            // 
            gmailUserTextbox.Location = new Point(196, 63);
            gmailUserTextbox.Margin = new Padding(4, 5, 4, 5);
            gmailUserTextbox.Name = "gmailUserTextbox";
            gmailUserTextbox.Size = new Size(373, 31);
            gmailUserTextbox.TabIndex = 5;
            // 
            // label11
            // 
            label11.AutoSize = true;
            label11.Location = new Point(7, 63);
            label11.Margin = new Padding(4, 0, 4, 0);
            label11.Name = "label11";
            label11.Size = new Size(148, 25);
            label11.TabIndex = 26;
            label11.Text = "Gmail user name:";
            // 
            // autostartListenerCheckbox
            // 
            autostartListenerCheckbox.AutoSize = true;
            autostartListenerCheckbox.Checked = true;
            autostartListenerCheckbox.CheckState = CheckState.Checked;
            autostartListenerCheckbox.Location = new Point(7, 169);
            autostartListenerCheckbox.Name = "autostartListenerCheckbox";
            autostartListenerCheckbox.Size = new Size(164, 29);
            autostartListenerCheckbox.TabIndex = 25;
            autostartListenerCheckbox.Text = "Autostart server";
            autostartListenerCheckbox.UseVisualStyleBackColor = true;
            // 
            // wavOutputLocationTextbox
            // 
            wavOutputLocationTextbox.Location = new Point(196, 13);
            wavOutputLocationTextbox.Margin = new Padding(4, 5, 4, 5);
            wavOutputLocationTextbox.Name = "wavOutputLocationTextbox";
            wavOutputLocationTextbox.Size = new Size(373, 31);
            wavOutputLocationTextbox.TabIndex = 2;
            // 
            // label7
            // 
            label7.AutoSize = true;
            label7.Location = new Point(7, 13);
            label7.Margin = new Padding(4, 0, 4, 0);
            label7.Name = "label7";
            label7.Size = new Size(161, 25);
            label7.TabIndex = 21;
            label7.Text = "Save MP3s to disk:";
            // 
            // browseButton
            // 
            browseButton.Location = new Point(577, 10);
            browseButton.Margin = new Padding(4, 5, 4, 5);
            browseButton.Name = "browseButton";
            browseButton.Size = new Size(94, 36);
            browseButton.TabIndex = 3;
            browseButton.Text = "Browse...";
            browseButton.UseVisualStyleBackColor = true;
            browseButton.Click += browseButton_Click;
            // 
            // tabPage1
            // 
            tabPage1.Controls.Add(talkgroupCountLabel);
            tabPage1.Controls.Add(label10);
            tabPage1.Controls.Add(browseButton3);
            tabPage1.Controls.Add(samplingRateTextbox);
            tabPage1.Controls.Add(bitDepthTextbox);
            tabPage1.Controls.Add(label6);
            tabPage1.Controls.Add(label5);
            tabPage1.Controls.Add(label4);
            tabPage1.Controls.Add(numChannelsTextbox);
            tabPage1.Controls.Add(label2);
            tabPage1.Controls.Add(listenPortTextbox);
            tabPage1.Controls.Add(label1);
            tabPage1.Location = new Point(4, 34);
            tabPage1.Name = "tabPage1";
            tabPage1.Padding = new Padding(3);
            tabPage1.Size = new Size(752, 768);
            tabPage1.TabIndex = 0;
            tabPage1.Text = "Trunk Recorder";
            tabPage1.UseVisualStyleBackColor = true;
            // 
            // talkgroupCountLabel
            // 
            talkgroupCountLabel.AutoSize = true;
            talkgroupCountLabel.Location = new Point(132, 135);
            talkgroupCountLabel.Margin = new Padding(4, 0, 4, 0);
            talkgroupCountLabel.Name = "talkgroupCountLabel";
            talkgroupCountLabel.Size = new Size(149, 25);
            talkgroupCountLabel.TabIndex = 25;
            talkgroupCountLabel.Text = "(talkgroup count)";
            // 
            // label10
            // 
            label10.AutoSize = true;
            label10.Location = new Point(7, 132);
            label10.Margin = new Padding(4, 0, 4, 0);
            label10.Name = "label10";
            label10.Size = new Size(102, 25);
            label10.TabIndex = 24;
            label10.Text = "Talkgroups:";
            // 
            // browseButton3
            // 
            browseButton3.Location = new Point(390, 129);
            browseButton3.Margin = new Padding(4, 5, 4, 5);
            browseButton3.Name = "browseButton3";
            browseButton3.Size = new Size(94, 36);
            browseButton3.TabIndex = 5;
            browseButton3.Text = "Load...";
            browseButton3.UseVisualStyleBackColor = true;
            browseButton3.Click += browseButton3_Click;
            // 
            // samplingRateTextbox
            // 
            samplingRateTextbox.Location = new Point(587, 71);
            samplingRateTextbox.Margin = new Padding(4, 5, 4, 5);
            samplingRateTextbox.Name = "samplingRateTextbox";
            samplingRateTextbox.Size = new Size(91, 31);
            samplingRateTextbox.TabIndex = 4;
            samplingRateTextbox.Text = "16000";
            // 
            // bitDepthTextbox
            // 
            bitDepthTextbox.Location = new Point(390, 71);
            bitDepthTextbox.Margin = new Padding(4, 5, 4, 5);
            bitDepthTextbox.Name = "bitDepthTextbox";
            bitDepthTextbox.Size = new Size(45, 31);
            bitDepthTextbox.TabIndex = 3;
            bitDepthTextbox.Text = "16";
            // 
            // label6
            // 
            label6.AutoSize = true;
            label6.Location = new Point(453, 74);
            label6.Margin = new Padding(4, 0, 4, 0);
            label6.Name = "label6";
            label6.Size = new Size(126, 25);
            label6.TabIndex = 15;
            label6.Text = "Sampling rate:";
            // 
            // label5
            // 
            label5.AutoSize = true;
            label5.Location = new Point(132, 71);
            label5.Margin = new Padding(4, 0, 4, 0);
            label5.Name = "label5";
            label5.Size = new Size(87, 25);
            label5.TabIndex = 14;
            label5.Text = "Channels:";
            // 
            // label4
            // 
            label4.AutoSize = true;
            label4.Location = new Point(294, 71);
            label4.Margin = new Padding(4, 0, 4, 0);
            label4.Name = "label4";
            label4.Size = new Size(88, 25);
            label4.TabIndex = 13;
            label4.Text = "Bit depth:";
            // 
            // numChannelsTextbox
            // 
            numChannelsTextbox.Location = new Point(227, 71);
            numChannelsTextbox.Margin = new Padding(4, 5, 4, 5);
            numChannelsTextbox.Name = "numChannelsTextbox";
            numChannelsTextbox.Size = new Size(45, 31);
            numChannelsTextbox.TabIndex = 2;
            numChannelsTextbox.Text = "1";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(7, 71);
            label2.Margin = new Padding(4, 0, 4, 0);
            label2.Name = "label2";
            label2.Size = new Size(113, 25);
            label2.TabIndex = 11;
            label2.Text = "Analog data:";
            // 
            // listenPortTextbox
            // 
            listenPortTextbox.Location = new Point(141, 10);
            listenPortTextbox.Margin = new Padding(4, 5, 4, 5);
            listenPortTextbox.Name = "listenPortTextbox";
            listenPortTextbox.Size = new Size(78, 31);
            listenPortTextbox.TabIndex = 1;
            listenPortTextbox.Text = "9123";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Location = new Point(7, 13);
            label1.Margin = new Padding(4, 0, 4, 0);
            label1.Name = "label1";
            label1.Size = new Size(126, 25);
            label1.TabIndex = 8;
            label1.Text = "Listen on port:";
            // 
            // tabPage2
            // 
            tabPage2.Controls.Add(browseButton2);
            tabPage2.Controls.Add(whisperModelFileTextbox);
            tabPage2.Controls.Add(label8);
            tabPage2.Location = new Point(4, 34);
            tabPage2.Name = "tabPage2";
            tabPage2.Padding = new Padding(3);
            tabPage2.Size = new Size(752, 768);
            tabPage2.TabIndex = 1;
            tabPage2.Text = "Whisper.net";
            tabPage2.UseVisualStyleBackColor = true;
            // 
            // browseButton2
            // 
            browseButton2.Location = new Point(583, 8);
            browseButton2.Margin = new Padding(4, 5, 4, 5);
            browseButton2.Name = "browseButton2";
            browseButton2.Size = new Size(94, 36);
            browseButton2.TabIndex = 21;
            browseButton2.Text = "Browse...";
            browseButton2.UseVisualStyleBackColor = true;
            browseButton2.Click += browseButton2_Click;
            // 
            // whisperModelFileTextbox
            // 
            whisperModelFileTextbox.Location = new Point(228, 12);
            whisperModelFileTextbox.Margin = new Padding(4, 5, 4, 5);
            whisperModelFileTextbox.Name = "whisperModelFileTextbox";
            whisperModelFileTextbox.Size = new Size(347, 31);
            whisperModelFileTextbox.TabIndex = 1;
            // 
            // label8
            // 
            label8.AutoSize = true;
            label8.Location = new Point(7, 12);
            label8.Margin = new Padding(4, 0, 4, 0);
            label8.Name = "label8";
            label8.Size = new Size(222, 25);
            label8.TabIndex = 10;
            label8.Text = "Whisper model file (ggml):";
            // 
            // SettingsWindow
            // 
            AutoScaleDimensions = new SizeF(10F, 25F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(766, 912);
            Controls.Add(tableLayoutPanel1);
            DoubleBuffered = true;
            Icon = (Icon)resources.GetObject("$this.Icon");
            Margin = new Padding(4, 5, 4, 5);
            Name = "SettingsWindow";
            StartPosition = FormStartPosition.CenterParent;
            Text = "Settings";
            tableLayoutPanel1.ResumeLayout(false);
            settingsTabControl.ResumeLayout(false);
            tabPage3.ResumeLayout(false);
            tabPage3.PerformLayout();
            tabPage1.ResumeLayout(false);
            tabPage1.PerformLayout();
            tabPage2.ResumeLayout(false);
            tabPage2.PerformLayout();
            ResumeLayout(false);
        }

        #endregion
        private System.Windows.Forms.Button saveButton;
        private TableLayoutPanel tableLayoutPanel1;
        private TabControl settingsTabControl;
        private TabPage tabPage1;
        private TextBox numChannelsTextbox;
        private Label label2;
        private TextBox listenPortTextbox;
        private Label label1;
        private TabPage tabPage2;
        private TabPage tabPage3;
        private Label label6;
        private Label label5;
        private Label label4;
        private TextBox samplingRateTextbox;
        private TextBox bitDepthTextbox;
        private TextBox wavOutputLocationTextbox;
        private Label label7;
        private Button browseButton;
        private Button browseButton2;
        private TextBox whisperModelFileTextbox;
        private Label label8;
        private Label label10;
        private Button browseButton3;
        private Label talkgroupCountLabel;
        private CheckBox autostartListenerCheckbox;
        private TextBox gmailUserTextbox;
        private Label label11;
        private TextBox gmailAppPasswordTextbox;
        private Label label12;
    }
}