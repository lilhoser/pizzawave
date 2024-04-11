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
    public partial class MainWindow
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainWindow));
            tableLayoutPanel1 = new TableLayoutPanel();
            statusStrip1 = new StatusStrip();
            toolStripStatusLabel1 = new ToolStripStatusLabel();
            toolStripProgressBar1 = new ToolStripProgressBar();
            toolStripConnectionLabel = new ToolStripStatusLabel();
            transcriptionListview = new BrightIdeasSoftware.FastObjectListView();
            menuStrip1 = new MenuStrip();
            fileToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator3 = new ToolStripSeparator();
            saveSettingsAsToolStripMenuItem = new ToolStripMenuItem();
            openSettingsToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator1 = new ToolStripSeparator();
            startListeningToolStripMenuItem = new ToolStripMenuItem();
            startToolStripMenuItem = new ToolStripMenuItem();
            stopToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator2 = new ToolStripSeparator();
            exitToolStripMenuItem = new ToolStripMenuItem();
            editToolStripMenuItem = new ToolStripMenuItem();
            settingsToolStripMenuItem = new ToolStripMenuItem();
            alertsToolStripMenuItem = new ToolStripMenuItem();
            viewToolStripMenuItem = new ToolStripMenuItem();
            clearToolStripMenuItem = new ToolStripMenuItem();
            showAlertMatchesOnlyToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator5 = new ToolStripSeparator();
            exportJSONToolStripMenuItem = new ToolStripMenuItem();
            exportCSVToolStripMenuItem = new ToolStripMenuItem();
            toolStripSeparator4 = new ToolStripSeparator();
            groupByToolStripMenuItem = new ToolStripMenuItem();
            alphaTagToolStripMenuItem = new ToolStripMenuItem();
            tagToolStripMenuItem = new ToolStripMenuItem();
            descriptionToolStripMenuItem = new ToolStripMenuItem();
            categoryToolStripMenuItem = new ToolStripMenuItem();
            offGroupByToolStripMenuItem = new ToolStripMenuItem();
            diagnosticsToolStripMenuItem = new ToolStripMenuItem();
            openLogsToolStripMenuItem = new ToolStripMenuItem();
            traceLevelToolStripMenuItem = new ToolStripMenuItem();
            offTraceLevelToolStripMenuItem = new ToolStripMenuItem();
            errorToolStripMenuItem = new ToolStripMenuItem();
            warningToolStripMenuItem = new ToolStripMenuItem();
            informationToolStripMenuItem = new ToolStripMenuItem();
            verboseToolStripMenuItem = new ToolStripMenuItem();
            toolsToolStripMenuItem = new ToolStripMenuItem();
            transcriptionQualityToolStripMenuItem = new ToolStripMenuItem();
            findTalkgroupsToolStripMenuItem = new ToolStripMenuItem();
            cleanupToolStripMenuItem = new ToolStripMenuItem();
            helpToolStripMenuItem = new ToolStripMenuItem();
            githubToolStripMenuItem = new ToolStripMenuItem();
            openCaptureToolStripMenuItem = new ToolStripMenuItem();
            tableLayoutPanel1.SuspendLayout();
            statusStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)transcriptionListview).BeginInit();
            menuStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // tableLayoutPanel1
            // 
            tableLayoutPanel1.ColumnCount = 1;
            tableLayoutPanel1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            tableLayoutPanel1.Controls.Add(statusStrip1, 0, 1);
            tableLayoutPanel1.Controls.Add(transcriptionListview, 0, 0);
            tableLayoutPanel1.Dock = DockStyle.Fill;
            tableLayoutPanel1.Location = new Point(0, 33);
            tableLayoutPanel1.Name = "tableLayoutPanel1";
            tableLayoutPanel1.RowCount = 2;
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 97F));
            tableLayoutPanel1.RowStyles.Add(new RowStyle(SizeType.Percent, 3F));
            tableLayoutPanel1.Size = new Size(1872, 1112);
            tableLayoutPanel1.TabIndex = 0;
            // 
            // statusStrip1
            // 
            statusStrip1.Dock = DockStyle.Fill;
            statusStrip1.ImageScalingSize = new Size(24, 24);
            statusStrip1.Items.AddRange(new ToolStripItem[] { toolStripStatusLabel1, toolStripProgressBar1, toolStripConnectionLabel });
            statusStrip1.Location = new Point(0, 1078);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new Size(1872, 34);
            statusStrip1.TabIndex = 0;
            statusStrip1.Text = "statusStrip1";
            // 
            // toolStripStatusLabel1
            // 
            toolStripStatusLabel1.Name = "toolStripStatusLabel1";
            toolStripStatusLabel1.Size = new Size(0, 27);
            toolStripStatusLabel1.Visible = false;
            // 
            // toolStripProgressBar1
            // 
            toolStripProgressBar1.Name = "toolStripProgressBar1";
            toolStripProgressBar1.Size = new Size(100, 26);
            toolStripProgressBar1.Visible = false;
            // 
            // toolStripConnectionLabel
            // 
            toolStripConnectionLabel.Name = "toolStripConnectionLabel";
            toolStripConnectionLabel.Size = new Size(1857, 27);
            toolStripConnectionLabel.Spring = true;
            toolStripConnectionLabel.Text = "Not connected";
            toolStripConnectionLabel.TextAlign = ContentAlignment.MiddleRight;
            // 
            // transcriptionListview
            // 
            transcriptionListview.CellEditUseWholeCell = false;
            transcriptionListview.Dock = DockStyle.Fill;
            transcriptionListview.FullRowSelect = true;
            transcriptionListview.GridLines = true;
            transcriptionListview.Location = new Point(3, 3);
            transcriptionListview.Name = "transcriptionListview";
            transcriptionListview.ShowGroups = false;
            transcriptionListview.Size = new Size(1866, 1072);
            transcriptionListview.TabIndex = 1;
            transcriptionListview.UseFiltering = true;
            transcriptionListview.View = View.Details;
            transcriptionListview.VirtualMode = true;
            transcriptionListview.CellToolTipShowing += transcriptionListview_CellToolTipShowing;
            // 
            // menuStrip1
            // 
            menuStrip1.ImageScalingSize = new Size(24, 24);
            menuStrip1.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, editToolStripMenuItem, viewToolStripMenuItem, diagnosticsToolStripMenuItem, toolsToolStripMenuItem, helpToolStripMenuItem });
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.Size = new Size(1872, 33);
            menuStrip1.TabIndex = 1;
            menuStrip1.Text = "menuStrip1";
            // 
            // fileToolStripMenuItem
            // 
            fileToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { openCaptureToolStripMenuItem, toolStripSeparator3, saveSettingsAsToolStripMenuItem, openSettingsToolStripMenuItem, toolStripSeparator1, startListeningToolStripMenuItem, toolStripSeparator2, exitToolStripMenuItem });
            fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            fileToolStripMenuItem.Size = new Size(54, 29);
            fileToolStripMenuItem.Text = "File";
            // 
            // toolStripSeparator3
            // 
            toolStripSeparator3.Name = "toolStripSeparator3";
            toolStripSeparator3.Size = new Size(267, 6);
            // 
            // saveSettingsAsToolStripMenuItem
            // 
            saveSettingsAsToolStripMenuItem.Name = "saveSettingsAsToolStripMenuItem";
            saveSettingsAsToolStripMenuItem.Size = new Size(270, 34);
            saveSettingsAsToolStripMenuItem.Text = "Save settings as...";
            saveSettingsAsToolStripMenuItem.Click += saveSettingsAsToolStripMenuItem_Click;
            // 
            // openSettingsToolStripMenuItem
            // 
            openSettingsToolStripMenuItem.Name = "openSettingsToolStripMenuItem";
            openSettingsToolStripMenuItem.Size = new Size(270, 34);
            openSettingsToolStripMenuItem.Text = "Load settings...";
            openSettingsToolStripMenuItem.Click += openSettingsToolStripMenuItem_Click;
            // 
            // toolStripSeparator1
            // 
            toolStripSeparator1.Name = "toolStripSeparator1";
            toolStripSeparator1.Size = new Size(267, 6);
            // 
            // startListeningToolStripMenuItem
            // 
            startListeningToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { startToolStripMenuItem, stopToolStripMenuItem });
            startListeningToolStripMenuItem.Name = "startListeningToolStripMenuItem";
            startListeningToolStripMenuItem.Size = new Size(270, 34);
            startListeningToolStripMenuItem.Text = "Call Manager";
            // 
            // startToolStripMenuItem
            // 
            startToolStripMenuItem.Name = "startToolStripMenuItem";
            startToolStripMenuItem.Size = new Size(151, 34);
            startToolStripMenuItem.Text = "Start";
            startToolStripMenuItem.Click += startToolStripMenuItem_Click;
            // 
            // stopToolStripMenuItem
            // 
            stopToolStripMenuItem.Enabled = false;
            stopToolStripMenuItem.Name = "stopToolStripMenuItem";
            stopToolStripMenuItem.Size = new Size(151, 34);
            stopToolStripMenuItem.Text = "Stop";
            stopToolStripMenuItem.Click += stopToolStripMenuItem_Click;
            // 
            // toolStripSeparator2
            // 
            toolStripSeparator2.Name = "toolStripSeparator2";
            toolStripSeparator2.Size = new Size(267, 6);
            // 
            // exitToolStripMenuItem
            // 
            exitToolStripMenuItem.Name = "exitToolStripMenuItem";
            exitToolStripMenuItem.Size = new Size(270, 34);
            exitToolStripMenuItem.Text = "Exit";
            exitToolStripMenuItem.Click += exitToolStripMenuItem_Click;
            // 
            // editToolStripMenuItem
            // 
            editToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { settingsToolStripMenuItem, alertsToolStripMenuItem });
            editToolStripMenuItem.Name = "editToolStripMenuItem";
            editToolStripMenuItem.Size = new Size(58, 29);
            editToolStripMenuItem.Text = "Edit";
            // 
            // settingsToolStripMenuItem
            // 
            settingsToolStripMenuItem.Name = "settingsToolStripMenuItem";
            settingsToolStripMenuItem.Size = new Size(190, 34);
            settingsToolStripMenuItem.Text = "Settings...";
            settingsToolStripMenuItem.Click += settingsToolStripMenuItem_Click;
            // 
            // alertsToolStripMenuItem
            // 
            alertsToolStripMenuItem.Name = "alertsToolStripMenuItem";
            alertsToolStripMenuItem.Size = new Size(190, 34);
            alertsToolStripMenuItem.Text = "Alerts...";
            alertsToolStripMenuItem.Click += alertsToolStripMenuItem_Click;
            // 
            // viewToolStripMenuItem
            // 
            viewToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { clearToolStripMenuItem, showAlertMatchesOnlyToolStripMenuItem, toolStripSeparator5, exportJSONToolStripMenuItem, exportCSVToolStripMenuItem, toolStripSeparator4, groupByToolStripMenuItem });
            viewToolStripMenuItem.Name = "viewToolStripMenuItem";
            viewToolStripMenuItem.Size = new Size(65, 29);
            viewToolStripMenuItem.Text = "View";
            // 
            // clearToolStripMenuItem
            // 
            clearToolStripMenuItem.Name = "clearToolStripMenuItem";
            clearToolStripMenuItem.Size = new Size(307, 34);
            clearToolStripMenuItem.Text = "Clear";
            clearToolStripMenuItem.Click += clearToolStripMenuItem_Click;
            // 
            // showAlertMatchesOnlyToolStripMenuItem
            // 
            showAlertMatchesOnlyToolStripMenuItem.CheckOnClick = true;
            showAlertMatchesOnlyToolStripMenuItem.Name = "showAlertMatchesOnlyToolStripMenuItem";
            showAlertMatchesOnlyToolStripMenuItem.Size = new Size(307, 34);
            showAlertMatchesOnlyToolStripMenuItem.Text = "Show alert matches only";
            showAlertMatchesOnlyToolStripMenuItem.Click += showAlertMatchesOnlyToolStripMenuItem_Click;
            // 
            // toolStripSeparator5
            // 
            toolStripSeparator5.Name = "toolStripSeparator5";
            toolStripSeparator5.Size = new Size(304, 6);
            // 
            // exportJSONToolStripMenuItem
            // 
            exportJSONToolStripMenuItem.Name = "exportJSONToolStripMenuItem";
            exportJSONToolStripMenuItem.Size = new Size(307, 34);
            exportJSONToolStripMenuItem.Text = "Export JSON...";
            exportJSONToolStripMenuItem.Click += exportJSONToolStripMenuItem_Click;
            // 
            // exportCSVToolStripMenuItem
            // 
            exportCSVToolStripMenuItem.Name = "exportCSVToolStripMenuItem";
            exportCSVToolStripMenuItem.Size = new Size(307, 34);
            exportCSVToolStripMenuItem.Text = "Export CSV...";
            exportCSVToolStripMenuItem.Click += exportCSVToolStripMenuItem_Click;
            // 
            // toolStripSeparator4
            // 
            toolStripSeparator4.Name = "toolStripSeparator4";
            toolStripSeparator4.Size = new Size(304, 6);
            // 
            // groupByToolStripMenuItem
            // 
            groupByToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { alphaTagToolStripMenuItem, tagToolStripMenuItem, descriptionToolStripMenuItem, categoryToolStripMenuItem, offGroupByToolStripMenuItem });
            groupByToolStripMenuItem.Name = "groupByToolStripMenuItem";
            groupByToolStripMenuItem.Size = new Size(307, 34);
            groupByToolStripMenuItem.Text = "Group by";
            // 
            // alphaTagToolStripMenuItem
            // 
            alphaTagToolStripMenuItem.Checked = true;
            alphaTagToolStripMenuItem.CheckOnClick = true;
            alphaTagToolStripMenuItem.CheckState = CheckState.Checked;
            alphaTagToolStripMenuItem.Name = "alphaTagToolStripMenuItem";
            alphaTagToolStripMenuItem.Size = new Size(204, 34);
            alphaTagToolStripMenuItem.Text = "Alpha Tag";
            alphaTagToolStripMenuItem.Click += alphaTagToolStripMenuItem_Click;
            // 
            // tagToolStripMenuItem
            // 
            tagToolStripMenuItem.CheckOnClick = true;
            tagToolStripMenuItem.Name = "tagToolStripMenuItem";
            tagToolStripMenuItem.Size = new Size(204, 34);
            tagToolStripMenuItem.Text = "Tag";
            tagToolStripMenuItem.Click += tagToolStripMenuItem_Click;
            // 
            // descriptionToolStripMenuItem
            // 
            descriptionToolStripMenuItem.CheckOnClick = true;
            descriptionToolStripMenuItem.Name = "descriptionToolStripMenuItem";
            descriptionToolStripMenuItem.Size = new Size(204, 34);
            descriptionToolStripMenuItem.Text = "Description";
            descriptionToolStripMenuItem.Click += descriptionToolStripMenuItem_Click;
            // 
            // categoryToolStripMenuItem
            // 
            categoryToolStripMenuItem.CheckOnClick = true;
            categoryToolStripMenuItem.Name = "categoryToolStripMenuItem";
            categoryToolStripMenuItem.Size = new Size(204, 34);
            categoryToolStripMenuItem.Text = "Category";
            categoryToolStripMenuItem.Click += categoryToolStripMenuItem_Click;
            // 
            // offGroupByToolStripMenuItem
            // 
            offGroupByToolStripMenuItem.Name = "offGroupByToolStripMenuItem";
            offGroupByToolStripMenuItem.Size = new Size(204, 34);
            offGroupByToolStripMenuItem.Text = "Off";
            offGroupByToolStripMenuItem.Click += offGroupByToolStripMenuItem_Click;
            // 
            // diagnosticsToolStripMenuItem
            // 
            diagnosticsToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { openLogsToolStripMenuItem, traceLevelToolStripMenuItem });
            diagnosticsToolStripMenuItem.Name = "diagnosticsToolStripMenuItem";
            diagnosticsToolStripMenuItem.Size = new Size(120, 29);
            diagnosticsToolStripMenuItem.Text = "Diagnostics";
            // 
            // openLogsToolStripMenuItem
            // 
            openLogsToolStripMenuItem.Name = "openLogsToolStripMenuItem";
            openLogsToolStripMenuItem.Size = new Size(209, 34);
            openLogsToolStripMenuItem.Text = "Open logs...";
            openLogsToolStripMenuItem.Click += openLogsToolStripMenuItem_Click;
            // 
            // traceLevelToolStripMenuItem
            // 
            traceLevelToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { offTraceLevelToolStripMenuItem, errorToolStripMenuItem, warningToolStripMenuItem, informationToolStripMenuItem, verboseToolStripMenuItem });
            traceLevelToolStripMenuItem.Name = "traceLevelToolStripMenuItem";
            traceLevelToolStripMenuItem.Size = new Size(209, 34);
            traceLevelToolStripMenuItem.Text = "Trace level";
            // 
            // offTraceLevelToolStripMenuItem
            // 
            offTraceLevelToolStripMenuItem.CheckOnClick = true;
            offTraceLevelToolStripMenuItem.Name = "offTraceLevelToolStripMenuItem";
            offTraceLevelToolStripMenuItem.Size = new Size(208, 34);
            offTraceLevelToolStripMenuItem.Text = "Off";
            offTraceLevelToolStripMenuItem.Click += offTraceLevelToolStripMenuItem_Click;
            // 
            // errorToolStripMenuItem
            // 
            errorToolStripMenuItem.Checked = true;
            errorToolStripMenuItem.CheckOnClick = true;
            errorToolStripMenuItem.CheckState = CheckState.Checked;
            errorToolStripMenuItem.Name = "errorToolStripMenuItem";
            errorToolStripMenuItem.Size = new Size(208, 34);
            errorToolStripMenuItem.Text = "Error";
            errorToolStripMenuItem.Click += errorToolStripMenuItem_Click;
            // 
            // warningToolStripMenuItem
            // 
            warningToolStripMenuItem.CheckOnClick = true;
            warningToolStripMenuItem.Name = "warningToolStripMenuItem";
            warningToolStripMenuItem.Size = new Size(208, 34);
            warningToolStripMenuItem.Text = "Warning";
            warningToolStripMenuItem.Click += warningToolStripMenuItem_Click;
            // 
            // informationToolStripMenuItem
            // 
            informationToolStripMenuItem.CheckOnClick = true;
            informationToolStripMenuItem.Name = "informationToolStripMenuItem";
            informationToolStripMenuItem.Size = new Size(208, 34);
            informationToolStripMenuItem.Text = "Information";
            informationToolStripMenuItem.Click += informationToolStripMenuItem_Click;
            // 
            // verboseToolStripMenuItem
            // 
            verboseToolStripMenuItem.CheckOnClick = true;
            verboseToolStripMenuItem.Name = "verboseToolStripMenuItem";
            verboseToolStripMenuItem.Size = new Size(208, 34);
            verboseToolStripMenuItem.Text = "Verbose";
            verboseToolStripMenuItem.Click += verboseToolStripMenuItem_Click;
            // 
            // toolsToolStripMenuItem
            // 
            toolsToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { transcriptionQualityToolStripMenuItem, findTalkgroupsToolStripMenuItem, cleanupToolStripMenuItem });
            toolsToolStripMenuItem.Name = "toolsToolStripMenuItem";
            toolsToolStripMenuItem.Size = new Size(69, 29);
            toolsToolStripMenuItem.Text = "Tools";
            // 
            // transcriptionQualityToolStripMenuItem
            // 
            transcriptionQualityToolStripMenuItem.Name = "transcriptionQualityToolStripMenuItem";
            transcriptionQualityToolStripMenuItem.Size = new Size(272, 34);
            transcriptionQualityToolStripMenuItem.Text = "Transcription quality";
            transcriptionQualityToolStripMenuItem.Click += transcriptionQualityToolStripMenuItem_Click;
            // 
            // findTalkgroupsToolStripMenuItem
            // 
            findTalkgroupsToolStripMenuItem.Name = "findTalkgroupsToolStripMenuItem";
            findTalkgroupsToolStripMenuItem.Size = new Size(272, 34);
            findTalkgroupsToolStripMenuItem.Text = "Find talkgroups";
            findTalkgroupsToolStripMenuItem.Click += findTalkgroupsToolStripMenuItem_Click;
            // 
            // cleanupToolStripMenuItem
            // 
            cleanupToolStripMenuItem.Name = "cleanupToolStripMenuItem";
            cleanupToolStripMenuItem.Size = new Size(272, 34);
            cleanupToolStripMenuItem.Text = "Cleanup...";
            cleanupToolStripMenuItem.Click += cleanupToolStripMenuItem_Click;
            // 
            // helpToolStripMenuItem
            // 
            helpToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { githubToolStripMenuItem });
            helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            helpToolStripMenuItem.Size = new Size(65, 29);
            helpToolStripMenuItem.Text = "Help";
            // 
            // githubToolStripMenuItem
            // 
            githubToolStripMenuItem.Name = "githubToolStripMenuItem";
            githubToolStripMenuItem.Size = new Size(167, 34);
            githubToolStripMenuItem.Text = "Github";
            githubToolStripMenuItem.Click += githubToolStripMenuItem_Click;
            // 
            // openCaptureToolStripMenuItem
            // 
            openCaptureToolStripMenuItem.Name = "openCaptureToolStripMenuItem";
            openCaptureToolStripMenuItem.Size = new Size(270, 34);
            openCaptureToolStripMenuItem.Text = "Open capture...";
            openCaptureToolStripMenuItem.Click += openCaptureToolStripMenuItem_Click;
            // 
            // MainWindow
            // 
            AutoScaleDimensions = new SizeF(10F, 25F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1872, 1145);
            Controls.Add(tableLayoutPanel1);
            Controls.Add(menuStrip1);
            DoubleBuffered = true;
            Icon = (Icon)resources.GetObject("$this.Icon");
            Name = "MainWindow";
            Text = "PizzaWave";
            FormClosing += MainWindow_FormClosing;
            Shown += MainWindow_Shown;
            tableLayoutPanel1.ResumeLayout(false);
            tableLayoutPanel1.PerformLayout();
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)transcriptionListview).EndInit();
            menuStrip1.ResumeLayout(false);
            menuStrip1.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private TableLayoutPanel tableLayoutPanel1;
        private MenuStrip menuStrip1;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem exitToolStripMenuItem;
        private ToolStripMenuItem editToolStripMenuItem;
        private ToolStripMenuItem settingsToolStripMenuItem;
        private StatusStrip statusStrip1;
        private ToolStripStatusLabel toolStripStatusLabel1;
        private ToolStripProgressBar toolStripProgressBar1;
        private ToolStripMenuItem viewToolStripMenuItem;
        private ToolStripStatusLabel toolStripConnectionLabel;
        private BrightIdeasSoftware.FastObjectListView transcriptionListview;
        private ToolStripMenuItem toolsToolStripMenuItem;
        private ToolStripMenuItem transcriptionQualityToolStripMenuItem;
        private ToolStripMenuItem findTalkgroupsToolStripMenuItem;
        private ToolStripMenuItem alertsToolStripMenuItem;
        private ToolStripMenuItem startListeningToolStripMenuItem;
        private ToolStripMenuItem startToolStripMenuItem;
        private ToolStripMenuItem stopToolStripMenuItem;
        private ToolStripMenuItem clearToolStripMenuItem;
        private ToolStripMenuItem showAlertMatchesOnlyToolStripMenuItem;
        private ToolStripMenuItem diagnosticsToolStripMenuItem;
        private ToolStripMenuItem openLogsToolStripMenuItem;
        private ToolStripMenuItem traceLevelToolStripMenuItem;
        private ToolStripMenuItem offTraceLevelToolStripMenuItem;
        private ToolStripMenuItem errorToolStripMenuItem;
        private ToolStripMenuItem warningToolStripMenuItem;
        private ToolStripMenuItem informationToolStripMenuItem;
        private ToolStripMenuItem verboseToolStripMenuItem;
        private ToolStripMenuItem groupByToolStripMenuItem;
        private ToolStripMenuItem alphaTagToolStripMenuItem;
        private ToolStripMenuItem tagToolStripMenuItem;
        private ToolStripMenuItem descriptionToolStripMenuItem;
        private ToolStripMenuItem categoryToolStripMenuItem;
        private ToolStripMenuItem offGroupByToolStripMenuItem;
        private ToolStripMenuItem helpToolStripMenuItem;
        private ToolStripMenuItem githubToolStripMenuItem;
        private ToolStripMenuItem cleanupToolStripMenuItem;
        private ToolStripSeparator toolStripSeparator3;
        private ToolStripMenuItem saveSettingsAsToolStripMenuItem;
        private ToolStripMenuItem openSettingsToolStripMenuItem;
        private ToolStripSeparator toolStripSeparator1;
        private ToolStripSeparator toolStripSeparator2;
        private ToolStripSeparator toolStripSeparator5;
        private ToolStripMenuItem exportJSONToolStripMenuItem;
        private ToolStripMenuItem exportCSVToolStripMenuItem;
        private ToolStripSeparator toolStripSeparator4;
        private ToolStripMenuItem openCaptureToolStripMenuItem;
    }
}
