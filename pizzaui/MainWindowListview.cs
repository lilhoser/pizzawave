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
using System.Text;

namespace pizzaui
{
    public partial class MainWindow
    {
        private bool m_InitializedOnce = false;

        private class ObjectListviewColumnHandlers
        {
            public GroupKeyGetterDelegate? groupKeyGetterDelegate;
            public AspectToStringConverterDelegate? aspectToStringConverterDelegate;
            public ImageGetterDelegate? imageGetterDelegate;
            public AspectGetterDelegate? aspectGetterDelegate;
            public GroupFormatterDelegate? groupFormatterDelegate;
            public string? columnName;
        }

        public enum CallDisplayGrouping
        {
            Off,
            AlphaTag,
            Tag,
            Description,
            Category
        }

        private static Dictionary<string, ObjectListviewColumnHandlers> s_columnHandlerTable =
            new Dictionary<string, ObjectListviewColumnHandlers>();

        private void InitializeListview()
        {
            transcriptionListview.UseFiltering = true;
            ShowAllCalls(); // clear any existing filter

            //
            // This routine can be called multiple times during the app, as new settings
            // are loaded or changed.
            //
            transcriptionListview.Clear();

            //
            // Generate columns and set default sort.
            //
            Generator.GenerateColumns(transcriptionListview, typeof(TranscribedCall), true);
            transcriptionListview.PrimarySortColumn = transcriptionListview.GetColumn("Start Time");
            transcriptionListview.PrimarySortOrder = SortOrder.Descending;
            //
            // Header style/formatting.
            //
            transcriptionListview.HeaderUsesThemes = false;
            transcriptionListview.HeaderFormatStyle = new HeaderFormatStyle();
            transcriptionListview.HeaderFormatStyle.Normal = new HeaderStateStyle()
            {
                BackColor = Color.Bisque,
            };
            transcriptionListview.HeaderFormatStyle.Pressed = new HeaderStateStyle()
            {
                BackColor = Color.DarkOrange,
            };
            transcriptionListview.HeaderFormatStyle.Hot = new HeaderStateStyle()
            {
                BackColor = Color.Orange,
            };

            //
            // Popup balloon for full transcription text.
            //
            transcriptionListview.CellToolTip.Font = new Font("MS Sans Serif", 12);
            transcriptionListview.CellToolTip.BackColor = Color.LightBlue;
            transcriptionListview.CellToolTip.Title = "Full Transcription";
            transcriptionListview.CellToolTip.IsBalloon = true;
            transcriptionListview.CellToolTip.AutoPopDelay = 60000; // 60s
            transcriptionListview.CellToolTip.InitialDelay = 0;
            //
            // Row height to allow for button
            //
            transcriptionListview.RowHeight = 35;
            //
            // Setup how rows will be grouped, if at all.
            //
            SetGroupingStrategy();
            //
            // Rows that match an alert get highlighted, unless the user has specified
            // they only want to show matches in the UI.
            //
            void rowHighlightHandler(object? sender, FormatRowEventArgs e)
            {
                var call = (TranscribedCall)e.Model;
                e.Item.BackColor = Color.White;
                if (call.IsAlertMatch && !m_Settings.ShowAlertMatchesOnly)
                {
                    e.Item.BackColor = Color.Orange;
                }
            }
            if (m_InitializedOnce)
            {
                transcriptionListview.FormatRow -= rowHighlightHandler;
                transcriptionListview.CellRightClick -= TranscriptionListview_CellRightClick;
            }
            transcriptionListview.FormatRow += rowHighlightHandler;
            transcriptionListview.CellRightClick += TranscriptionListview_CellRightClick;

            //
            // Configure aspect and other getters for each column.
            //
            s_columnHandlerTable = new Dictionary<string, ObjectListviewColumnHandlers>()
            {
                {  "StartTime", new ObjectListviewColumnHandlers() {
                    aspectToStringConverterDelegate = new AspectToStringConverterDelegate((value) =>
                        {
                            if (value == null)
                            {
                                return "";
                            }
                            DateTime date = DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime().DateTime;
                            return date.ToString("M/d/yyyy h:mm tt");
                        }),
                    columnName = "Start Time" // OLV does this..
                    }
                },
                {  "StopTime", new ObjectListviewColumnHandlers() {
                    aspectToStringConverterDelegate = new AspectToStringConverterDelegate((value) =>
                    {
                        if (value == null)
                        {
                            return "";
                        }
                        DateTime date = DateTimeOffset.FromUnixTimeSeconds((long)value).ToLocalTime().DateTime;
                        return date.ToString("M/d/yyyy h:mm tt");
                    }),
                    columnName = "Stop Time" // OLV does this..
                    }
                },
                { "Source", new ObjectListviewColumnHandlers() { } },
                { "SystemShortName", new ObjectListviewColumnHandlers() { columnName= "System Short Name" } },
                { "CallId", new ObjectListviewColumnHandlers() { columnName = "Call Id" } },
                { "Talkgroup", new ObjectListviewColumnHandlers() {
                    groupKeyGetterDelegate = new GroupKeyGetterDelegate((rowObject) =>
                        {
                            var call = (TranscribedCall)rowObject;
                            var talkgroup = TalkgroupHelper.LookupTalkgroup(m_Settings, call.Talkgroup);
                            if (talkgroup == null)
                            {
                                return $"{call.Talkgroup}";
                            }
                            switch(m_Settings.GroupingStrategy)
                            {
                                case CallDisplayGrouping.AlphaTag:
                                {
                                    return talkgroup.AlphaTag;
                                }
                                case CallDisplayGrouping.Tag:
                                {
                                    return talkgroup.Tag;
                                }
                                case CallDisplayGrouping.Description:
                                {
                                    return talkgroup.Description;
                                }
                                case CallDisplayGrouping.Category:
                                {
                                    return talkgroup.Category;
                                }
                                case CallDisplayGrouping.Off:
                                default:
                                {
                                    return string.Empty;
                                }
                            }
                        }),
                    groupFormatterDelegate = new GroupFormatterDelegate((group, parameters) =>
                    {
                        if (group.Items == null || group.Items.Count == 0)
                        {
                            return;
                        }
                        var row = (TranscribedCall)group.Items[0].RowObject;
                        var talkgroup = TalkgroupHelper.LookupTalkgroup(m_Settings, row.Talkgroup);
                        if (talkgroup == null)
                        {
                            return;
                        }
                        group.Subtitle = $"{talkgroup}";
                    }),
                } },
                { "Frequency", new ObjectListviewColumnHandlers() { } },
                { "Transcription", new ObjectListviewColumnHandlers() { } },
            };

            foreach (var kvp in s_columnHandlerTable)
            {
                var fieldName = kvp.Key;
                var table = kvp.Value;
                var colname = table.columnName;
                if (string.IsNullOrEmpty(colname))
                {
                    colname = fieldName;
                }
                var col = transcriptionListview.GetColumn(colname);
                if (col == null)
                {
                    // Column not found - skip it (may be a read-only property not generated by OLV)
                    continue;
                }
                col.AspectName = fieldName;
                if (table.groupKeyGetterDelegate != null)
                {
                    col.GroupKeyGetter = table.groupKeyGetterDelegate;
                }
                if (table.aspectToStringConverterDelegate != null)
                {
                    col.AspectToStringConverter = table.aspectToStringConverterDelegate;
                }
                if (table.imageGetterDelegate != null)
                {
                    col.ImageGetter = table.imageGetterDelegate;
                }
                if (table.aspectGetterDelegate != null)
                {
                    col.AspectGetter = table.aspectGetterDelegate;
                }
                if (table.groupFormatterDelegate != null)
                {
                    col.GroupFormatter = table.groupFormatterDelegate;
                }
            }

            //
            // Force columns to be wider. The control's builtin "make it as big as the
            // header AND the data" has _never_ worked right.
            //
            foreach (var col in transcriptionListview.AllColumns)
            {
                col.MinimumWidth = 125;
            }

            //
            // Create "Duration" column manually (it's a read-only computed property,
            // so Generator.GenerateColumns may not create it automatically)
            //
            var durationCol = new OLVColumn("Duration", "Duration");
            durationCol.AspectName = "Duration";
            durationCol.AspectToStringConverter = new AspectToStringConverterDelegate((value) =>
            {
                if (value == null)
                {
                    return "";
                }
                int duration = (int)value;
                return $"{duration}s";
            });
            durationCol.MinimumWidth = 120;
            durationCol.Width = 120;
            transcriptionListview.AllColumns.Add(durationCol);

            //
            // Create a combined "Talkgroup" column from FriendlyTalkgroup and FriendlyFrequency
            //
            var friendlyTalkgroupCol = transcriptionListview.GetColumn("Friendly Talkgroup");
            if (friendlyTalkgroupCol == null)
            {
                throw new Exception("Friendly Talkgroup column not found - check that the property exists in TranscribedCall");
            }
            var talkgroupCol = friendlyTalkgroupCol;
            talkgroupCol.AspectGetter = delegate (object row)
            {
                var call = (TranscribedCall)row;
                if (string.IsNullOrEmpty(call.FriendlyTalkgroup))
                {
                    return call.Talkgroup.ToString();
                }
                if (string.IsNullOrEmpty(call.FriendlyFrequency))
                {
                    return call.FriendlyTalkgroup;
                }
                return $"{call.FriendlyTalkgroup} @ {call.FriendlyFrequency}";
            };
            talkgroupCol.Text = "Talkgroup";
            talkgroupCol.Name = "Talkgroup";
            talkgroupCol.IsVisible = true;
            talkgroupCol.MinimumWidth = 400;
            talkgroupCol.Width = 400;

            //
            // Setup the "Location" aspect to hold a button for playing the audio file
            //
            InitializeButtonColumn();

            //
            // Reorder columns: move Duration to be right after Start Time
            // Only display these specific columns; all others are hidden:
            // - Start Time, Duration, Talkgroup, System Short Name, Audio, Transcription
            // Hidden columns include: Patched Talkgroups, Is Audio Playing, Unique Id,
            // Is Alert Match, Is Pinned, Should Autoplay, Play Audio Command, Call Time,
            // Stop Time, Call Id, Source, Talkgroup (raw), Frequency (raw)
            //
            var startTimeCol = transcriptionListview.GetColumn("Start Time");
            if (startTimeCol == null)
            {
                throw new Exception("Start Time column not found");
            }
            startTimeCol.Width = 185;
            startTimeCol.MinimumWidth = 185;

            var systemShortNameCol = transcriptionListview.GetColumn("System Short Name");
            var transcriptionCol = transcriptionListview.GetColumn("Transcription");
            var audioCol = transcriptionListview.GetColumn("Audio");

            if (systemShortNameCol == null || transcriptionCol == null || audioCol == null)
            {
                throw new Exception($"Column not found - System Short Name: {systemShortNameCol == null}, Transcription: {transcriptionCol == null}, Audio: {audioCol == null}");
            }

            // Set column widths
            systemShortNameCol.Width = 250;
            systemShortNameCol.MinimumWidth = 250;
            
            audioCol.Width = 80;
            audioCol.MinimumWidth = 80;
            
            transcriptionCol.FillsFreeSpace = true;
            transcriptionCol.MinimumWidth = 200;

            transcriptionListview.Columns.Clear();
            transcriptionListview.Columns.AddRange(new ColumnHeader[] {
                startTimeCol, durationCol, talkgroupCol,
                systemShortNameCol, audioCol, transcriptionCol
            });

            m_InitializedOnce = true;
        }

        private void TranscriptionListview_CellRightClick(object? sender, CellRightClickEventArgs Args)
        {
            var row = Args.Model;
            var column = Args.Column;
            if (row == null || column == null)
            {
                return;
            }
            Args.MenuStrip = new ContextMenuStrip();
            Args.MenuStrip.Items.AddRange(new ToolStripMenuItem[]
            {
                new ToolStripMenuItem("Copy row to clipboard", null, ContextMenuCopyRow),
                new ToolStripMenuItem("Copy cell to clipboard", null, ContextMenuCopyCell),
                new ToolStripMenuItem("Save MP3", null, ContextMenuSaveMp3),
                new ToolStripMenuItem("Go to capture folder", null, ContextMenuGoToCaptureFolder),
            });
            foreach (var item in Args.MenuStrip.Items)
            {
                ((ToolStripMenuItem)item).Tag = Args; // convenience for context menu handlers
            }
        }

        private void transcriptionListview_CellToolTipShowing(object sender, BrightIdeasSoftware.ToolTipShowingEventArgs e)
        {
            if (e.Column.Name != "Transcription")
            {
                return;
            }

            var transcribedCall = (TranscribedCall)e.Model;
            e.Text = Utilities.Wordwrap(transcribedCall.Transcription, 150);
        }

        private void InitializeButtonColumn()
        {
            //
            // Rename "Location" to "Audio" and place a button for playing it.
            //
            var playButtonColumn = transcriptionListview.GetColumn("Location");
            playButtonColumn.Text = "Audio";
            playButtonColumn.AspectGetter = delegate (object row)
            {
                var call = (TranscribedCall)row;
                if (string.IsNullOrEmpty(call.Location))
                {
                    //
                    // There is no audio to play at all. Show nothing in the cell
                    //
                    return string.Empty;
                }
                if (call.IsAudioPlaying)
                {
                    //
                    // There's an audio file and it's currently playing. Set the button text
                    // to "Stop".
                    //
                    return "Stop";
                }
                return "Play";
            };
            playButtonColumn.MinimumWidth = 80;
            playButtonColumn.Width = 80;
            playButtonColumn.ButtonSize = new Size(65, 32);
            playButtonColumn.ButtonSizing = OLVColumn.ButtonSizingMode.FixedBounds;
            playButtonColumn.TextAlign = HorizontalAlignment.Center;
            playButtonColumn.EnableButtonWhenItemIsDisabled = true;
            transcriptionListview.ShowImagesOnSubItems = true;

            void ButtonClickHandler(object? sender, CellClickEventArgs e)
            {
                var row = (TranscribedCall)e.Model;
                if (e.Column.Name != "Location")
                {
                    return;
                }

                //
                // User clicked "Stop" button - reset to "Start"
                //
                if (row.IsAudioPlaying)
                {
                    m_AudioPlayer.Stop();
                    row.IsAudioPlaying = false;
                    transcriptionListview.RefreshObject(row);
                    return;
                }

                //
                // User clicked "Play" button - begin playing and set button text to "Stop"
                //
                try
                {
                    m_AudioPlayer.PlayMp3File(
                        row.Location, row.UniqueId, new Func<Guid, bool>((Guid Id) =>
                        {
                            //
                            // Upon completion of playing the MP3, we have to set the
                            // button text back to "Play". Because the listview could
                            // have been cleared in the interim, we search for it.
                            // Note: we also must invoke this from the main UI thread.
                            //
                            return transcriptionListview.Invoke(() =>
                            {
                                if (transcriptionListview.Objects != null)
                                {
                                    foreach (var obj in transcriptionListview.Objects)
                                    {
                                        var row = (TranscribedCall)e.Model;
                                        if (row.UniqueId == Id)
                                        {
                                            row.IsAudioPlaying = false;
                                            transcriptionListview.RefreshObject(row);
                                            return true;
                                        }
                                    }
                                }
                                return false;
                            });
                        }));
                    row.IsAudioPlaying = true;
                    transcriptionListview.RefreshObject(row);
                }
                catch (Exception ex)
                {
                    m_AudioPlayer.Stop();
                    row.IsAudioPlaying = false;
                    transcriptionListview.RefreshObject(row);
                    MessageBox.Show(ex.Message);
                    return;
                }
            }

            if (m_InitializedOnce)
            {
                transcriptionListview.ButtonClick -= ButtonClickHandler;
            }

            transcriptionListview.ButtonClick += ButtonClickHandler;
            playButtonColumn.IsButton = true;
        }

        private void SetGroupingStrategy()
        {
            if (m_Settings.GroupingStrategy != CallDisplayGrouping.Off)
            {
                transcriptionListview.SpaceBetweenGroups = 15;
                transcriptionListview.SortGroupItemsByPrimaryColumn = true;
                transcriptionListview.ShowGroups = true;
                transcriptionListview.AlwaysGroupByColumn = transcriptionListview.GetColumn("Talkgroup");
                transcriptionListview.ShowItemCountOnGroups = true;
                transcriptionListview.GroupExpandingCollapsing += new EventHandler<GroupExpandingCollapsingEventArgs>(
                    delegate (object? sender, GroupExpandingCollapsingEventArgs args)
                {
                    //
                    // TODO: This is a workaround to an OLV bug. For whatever reason, the undelrying listview
                    // groups member is not being synced with OLV's groups member and this manifests as a crash
                    // whenever the group is collapsed/expanded. This workaround just prevents a crash; the
                    // expand/collapse button does not work.
                    //
                    if (transcriptionListview.Groups.Count == 0)
                    {
                        transcriptionListview.ShowGroups = true;
                        transcriptionListview.BuildGroups();
                    }
                    args.Canceled = false;
                });
            }
        }

        private void ShowOnlyAlertMatches()
        {
            //
            // Note: if filtering becomes any more complex, switch to CompositeAllFilter
            //
            transcriptionListview.AdditionalFilter = new ModelFilter(delegate (object row)
            {
                var call = (TranscribedCall)row;
                return call.IsAlertMatch;
            });
        }

        private void ShowAllCalls()
        {
            transcriptionListview.ModelFilter = null;
            transcriptionListview.AdditionalFilter = null;
        }

        private void HandleSearch()
        {
            var searchForm = new SearchForm();
            searchForm.Focus();
            searchForm.ShowDialog();
            var searchText = searchForm.m_SearchText;
            if (string.IsNullOrEmpty(searchText))
            {
                return;
            }

            TextMatchFilter searchFilter = new TextMatchFilter(
                transcriptionListview, searchText, StringComparison.InvariantCultureIgnoreCase);
            transcriptionListview.DefaultRenderer = new HighlightTextRenderer(searchFilter);
            transcriptionListview.ModelFilter = searchFilter;
        }

        private
        void
        ContextMenuCopyRow(
            object Sender,
            EventArgs Args
            )
        {
            object tag = ((ToolStripMenuItem)Sender).Tag;
            if (tag == null)
            {
                return;
            }
            var args = (CellRightClickEventArgs)tag;
            var listview = args.ListView;
            StringBuilder text = new StringBuilder();
            int count = 0;

            foreach (int index in listview.SelectedIndices)
            {
                foreach (ListViewItem.ListViewSubItem cell in listview.Items[index].SubItems)
                {
                    text.Append(cell.Text);
                    text.Append(',');
                }
                text.Length--; // remove trailing ','
                text.AppendLine();
                count++;
            }
            text.Length--; // remove trailing new line
            Clipboard.SetText(text.ToString());
        }

        private
        void
        ContextMenuCopyCell(
            object Sender,
            EventArgs Args
            )
        {
            object tag = ((ToolStripMenuItem)Sender).Tag;
            if (tag == null)
            {
                return;
            }

            var args = (CellRightClickEventArgs)tag;
            var targetCell = args.ColumnIndex;
            var listview = args.ListView as FastObjectListView;
            StringBuilder text = new StringBuilder();
            int count = 0;
            foreach (int index in listview.SelectedIndices)
            {
                var cell = listview.Items[index].SubItems[targetCell];
                text.AppendLine(cell.Text);
                count++;
            }
            text.Length--; // remove trailing new line
            Clipboard.SetText(text.ToString());
        }

        private
        void
        ContextMenuSaveMp3(
            object Sender,
            EventArgs Args
            )
        {
            object tag = ((ToolStripMenuItem)Sender).Tag;
            if (tag == null)
            {
                return;
            }

            var args = (CellRightClickEventArgs)tag;
            var targetCell = args.ColumnIndex;
            var listview = args.ListView as FastObjectListView;

            if (listview.SelectedItem == null)
            {
                return;
            }
            var call = listview.SelectedItem.RowObject as TranscribedCall;
            if (call == null)
            {
                return;
            }

            var sfd = new SaveFileDialog();
            sfd.OverwritePrompt = true;
            sfd.DefaultExt = "MP3";
            sfd.Filter = "MP3 File (*.mp3)|*.mp3";

            if (sfd.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            try
            {
                File.Copy(call.Location, sfd.FileName, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save source MP3 {call.Location} to " +
                    $"{sfd.FileName}: {ex.Message}");
                return;
            }
        }

        private
        void
        ContextMenuGoToCaptureFolder(
            object Sender,
            EventArgs Args
            )
        {
            object tag = ((ToolStripMenuItem)Sender).Tag;
            if (tag == null)
            {
                return;
            }

            var args = (CellRightClickEventArgs)tag;
            var targetCell = args.ColumnIndex;
            var listview = args.ListView as FastObjectListView;

            if (listview.SelectedItem == null)
            {
                return;
            }
            var call = listview.SelectedItem.RowObject as TranscribedCall;
            if (call == null)
            {
                return;
            }
            var folder = Path.GetDirectoryName(call.Location);
            try
            {
                System.Diagnostics.Process.Start($"explorer",$"{folder}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Unable to navigate to {folder}: {ex.Message}");
            }
        }
    }
}
