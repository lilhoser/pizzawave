using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Newtonsoft.Json;
using System.IO;
using System.Linq;
using pizzalib;

namespace pizzapi;

public partial class SettingsPanel : UserControl
{
    private Settings? _settings;
    private TextBox? _listenPortTextBox;
    private ComboBox? _transcriptionModelComboBox;
    private ComboBox? _transcriptionEngineComboBox;
    private TextBox? _gmailUserTextBox;
    private TextBox? _gmailPasswordTextBox;
    private CheckBox? _autoplayAlertsCheckBox;
    private ComboBox? _snoozeDurationComboBox;
    private Button? _importTalkgroupsButton;
    private Button? _viewTalkgroupsButton;
    private TextBlock? _talkgroupCountText;
    private CheckBox? _autoCleanupCallsCheckBox;
    private TextBox? _maxCallsToKeepTextBox;
    private ComboBox? _traceLevelComboBox;
    private bool _handlersWired;
    private string _currentSettingsPath = Settings.DefaultSettingsFileLocation;
    private TextBlock? _settingsPathText;

    public event EventHandler? RequestClose;
    public event Func<Settings, Task>? ApplySettingsRequested;
    public event Action<string>? SettingsPathChanged;

    public SettingsPanel()
    {
        InitializeComponent();
        _settings = new Settings();
        _currentSettingsPath = Settings.DefaultSettingsFileLocation;
        LoadSettings();
    }

    public SettingsPanel(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        _currentSettingsPath = Settings.DefaultSettingsFileLocation;
        LoadSettings();
    }

    public void SetSettings(Settings settings)
    {
        _settings = settings;
        _currentSettingsPath = Settings.DefaultSettingsFileLocation;
        LoadSettings();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void LoadSettings()
    {
        _listenPortTextBox = this.FindControl<TextBox>("ListenPortTextBox");
        _transcriptionModelComboBox = this.FindControl<ComboBox>("TranscriptionModelComboBox");
        _transcriptionEngineComboBox = this.FindControl<ComboBox>("TranscriptionEngineComboBox");
        _gmailUserTextBox = this.FindControl<TextBox>("GmailUserTextBox");
        _gmailPasswordTextBox = this.FindControl<TextBox>("GmailPasswordTextBox");
        _autoplayAlertsCheckBox = this.FindControl<CheckBox>("AutoplayAlertsCheckBox");
        _snoozeDurationComboBox = this.FindControl<ComboBox>("SnoozeDurationComboBox");
        _importTalkgroupsButton = this.FindControl<Button>("ImportTalkgroupsButton");
        _viewTalkgroupsButton = this.FindControl<Button>("ViewTalkgroupsButton");
        _talkgroupCountText = this.FindControl<TextBlock>("TalkgroupCountText");
        _autoCleanupCallsCheckBox = this.FindControl<CheckBox>("AutoCleanupCallsCheckBox");
        _maxCallsToKeepTextBox = this.FindControl<TextBox>("MaxCallsToKeepTextBox");
        _traceLevelComboBox = this.FindControl<ComboBox>("TraceLevelComboBox");
        _settingsPathText = this.FindControl<TextBlock>("SettingsPathText");
        var loadButton = this.FindControl<Button>("LoadButton");
        var saveButton = this.FindControl<Button>("SaveButton");
        var saveAsButton = this.FindControl<Button>("SaveAsButton");
        var cancelButton = this.FindControl<Button>("CancelButton");

        if (_listenPortTextBox != null && _transcriptionModelComboBox != null &&
            _transcriptionEngineComboBox != null &&
            _gmailUserTextBox != null && _gmailPasswordTextBox != null &&
            _autoplayAlertsCheckBox != null && _snoozeDurationComboBox != null &&
            _autoCleanupCallsCheckBox != null && _maxCallsToKeepTextBox != null &&
            _traceLevelComboBox != null &&
            saveButton != null && cancelButton != null)
        {
            var settings = _settings ??= new Settings();

            _listenPortTextBox.Text = settings.ListenPort.ToString();
            _transcriptionModelComboBox.SelectedIndex =
                GetModelPresetIndex(settings.TranscriptionModelPreset);
            _transcriptionEngineComboBox.SelectedIndex =
                string.Equals(settings.TranscriptionEngine, "vosk", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _gmailUserTextBox.Text = settings.GmailUser ?? string.Empty;
            _gmailPasswordTextBox.Text = settings.GmailPassword ?? string.Empty;
            _autoplayAlertsCheckBox.IsChecked = settings.AutoplayAlerts;
            _autoCleanupCallsCheckBox.IsChecked = settings.AutoCleanupCalls;
            _maxCallsToKeepTextBox.Text = settings.MaxCallsToKeep.ToString();

            _traceLevelComboBox.SelectedIndex = settings.TraceLevelApp switch
            {
                System.Diagnostics.SourceLevels.Error => 0,
                System.Diagnostics.SourceLevels.Warning => 1,
                System.Diagnostics.SourceLevels.Information => 2,
                System.Diagnostics.SourceLevels.Verbose => 3,
                _ => 0
            };

            _snoozeDurationComboBox.SelectedIndex = settings.SnoozeDurationMinutes switch
            {
                5 => 0,
                15 => 1,
                30 => 2,
                60 => 3,
                _ => 1
            };
            
            UpdateTalkgroupCount();
            UpdateSettingsPathText();

            WireHandlersOnce(settings, loadButton, saveButton, saveAsButton, cancelButton);
        }
    }
    
    private void WireHandlersOnce(Settings settings, Button? loadButton, Button? saveButton, Button? saveAsButton, Button? cancelButton)
    {
        if (_handlersWired)
            return;
        _handlersWired = true;

        if (_importTalkgroupsButton != null)
        {
            _importTalkgroupsButton.Click += async (s, e) =>
            {
                await ImportTalkgroupsFromCsv();
            };
        }
        
        if (_viewTalkgroupsButton != null)
        {
            _viewTalkgroupsButton.Click += (s, e) => ShowTalkgroupsTable();
        }

        if (loadButton != null)
        {
            loadButton.Click += async (s, e) =>
            {
                await LoadSettingsFromFile();
            };
        }

        if (saveButton != null)
        {
            saveButton.Click += (s, e) =>
            {
                try
                {
                    var currentSettings = _settings ??= new Settings();
                    var newUser = _gmailUserTextBox.Text;
                    var newPass = _gmailPasswordTextBox.Text;

                    if (!string.IsNullOrEmpty(newUser) && string.IsNullOrEmpty(newPass))
                    {
                        throw new Exception("Gmail password is required when Gmail user is set");
                    }

                    var settingsCopy = new Settings
                    {
                        ListenPort = currentSettings.ListenPort,
                        TranscriptionEngine = currentSettings.TranscriptionEngine,
                        TranscriptionModelPreset = currentSettings.TranscriptionModelPreset,
                        GmailUser = currentSettings.GmailUser,
                        GmailPassword = currentSettings.GmailPassword,
                        AutoplayAlerts = _autoplayAlertsCheckBox.IsChecked ?? false,
                        AutoCleanupCalls = _autoCleanupCallsCheckBox.IsChecked ?? true,
                        MaxCallsToKeep = int.TryParse(_maxCallsToKeepTextBox.Text, out int maxCalls) ? maxCalls : 100,
                        TraceLevelApp = _traceLevelComboBox.SelectedIndex switch
                        {
                            0 => System.Diagnostics.SourceLevels.Error,
                            1 => System.Diagnostics.SourceLevels.Warning,
                            2 => System.Diagnostics.SourceLevels.Information,
                            3 => System.Diagnostics.SourceLevels.Verbose,
                            _ => System.Diagnostics.SourceLevels.Error
                        },
                        SnoozeDurationMinutes = _snoozeDurationComboBox.SelectedIndex switch
                        {
                            0 => 5,
                            1 => 15,
                            2 => 30,
                            3 => 60,
                            _ => 15
                        }
                    };

                    if (int.TryParse(_listenPortTextBox.Text, out int port))
                    {
                        settingsCopy.ListenPort = port;
                    }

                    settingsCopy.TranscriptionModelPreset =
                        GetModelPresetValue(_transcriptionModelComboBox.SelectedIndex);
                    settingsCopy.TranscriptionEngine =
                        _transcriptionEngineComboBox.SelectedIndex == 1 ? "vosk" : "whisper";
                    settingsCopy.GmailUser = newUser;
                    settingsCopy.GmailPassword = newPass;

                    if (settingsCopy.TranscriptionModelPreset.StartsWith("vosk-",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        settingsCopy.TranscriptionEngine = "vosk";
                    }
                    else if (settingsCopy.TranscriptionModelPreset.StartsWith("whisper-",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        settingsCopy.TranscriptionEngine = "whisper";
                    }

                    settingsCopy.Validate();

                    currentSettings.ListenPort = settingsCopy.ListenPort;
                    currentSettings.TranscriptionEngine = settingsCopy.TranscriptionEngine;
                    currentSettings.TranscriptionModelPreset = settingsCopy.TranscriptionModelPreset;
                    currentSettings.GmailUser = settingsCopy.GmailUser;
                    currentSettings.GmailPassword = settingsCopy.GmailPassword;
                    currentSettings.AutoplayAlerts = settingsCopy.AutoplayAlerts;
                    currentSettings.AutoCleanupCalls = settingsCopy.AutoCleanupCalls;
                    currentSettings.MaxCallsToKeep = settingsCopy.MaxCallsToKeep;
                    currentSettings.TraceLevelApp = settingsCopy.TraceLevelApp;
                    currentSettings.SnoozeDurationMinutes = settingsCopy.SnoozeDurationMinutes;

                    currentSettings.SaveToFile(_currentSettingsPath);
                    if (ApplySettingsRequested != null)
                    {
                        _ = ApplySettingsRequested.Invoke(currentSettings);
                    }
                    else
                    {
                        StartBackgroundModelPreload(currentSettings);
                    }
                    RequestClose?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    ShowMessage("Save Settings Failed", ex.Message);
                }
            };
        }

        if (saveAsButton != null)
        {
            saveAsButton.Click += async (s, e) => await SaveSettingsAsAsync();
        }

        if (cancelButton != null)
        {
            cancelButton.Click += (s, e) => RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task LoadSettingsFromFile()
    {
        SetUiEnabled(false);
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var storageProvider = topLevel?.StorageProvider;
            if (storageProvider == null)
            {
                ShowMessage("Load Settings Failed", "Could not access file picker.");
                return;
            }

            var options = new FilePickerOpenOptions
            {
                Title = "Load settings JSON",
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new FilePickerFileType("JSON files") { Patterns = new List<string> { "*.json" } },
                    new FilePickerFileType("All files") { Patterns = new List<string> { "*.*" } }
                }
            };

            var files = await storageProvider.OpenFilePickerAsync(options);
            var file = files?.FirstOrDefault();
            if (file == null)
                return;

            var path = file.Path.LocalPath;
            var json = await File.ReadAllTextAsync(path);
            var loaded = JsonConvert.DeserializeObject<Settings>(json);

            if (loaded == null)
            {
                ShowMessage("Load Settings Failed", "The selected file did not contain valid settings.");
                return;
            }

            loaded.Validate();
            _settings = loaded;
            _currentSettingsPath = path;
            LoadSettings();

            if (ApplySettingsRequested != null)
            {
                await ApplySettingsRequested.Invoke(loaded);
            }
        }
        catch (Exception ex)
        {
            ShowMessage("Load Settings Failed", ex.Message);
        }
        finally
        {
            SetUiEnabled(true);
        }
    }

    private async Task SaveSettingsAsAsync()
    {
        SetUiEnabled(false);
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            var storageProvider = topLevel?.StorageProvider;
            if (storageProvider == null)
            {
                ShowMessage("Save As Failed", "Could not access file picker.");
                return;
            }

            var options = new FilePickerSaveOptions
            {
                Title = "Save settings JSON",
                SuggestedFileName = "settings.json",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new FilePickerFileType("JSON files") { Patterns = new List<string> { "*.json" } },
                    new FilePickerFileType("All files") { Patterns = new List<string> { "*.*" } }
                }
            };

            var file = await storageProvider.SaveFilePickerAsync(options);
            if (file == null)
                return;

            var path = file.Path.LocalPath;

            var currentSettings = _settings ??= new Settings();
            currentSettings.ListenPort = int.TryParse(_listenPortTextBox?.Text, out int port) ? port : currentSettings.ListenPort;
            currentSettings.TranscriptionModelPreset = GetModelPresetValue(_transcriptionModelComboBox?.SelectedIndex);
            currentSettings.TranscriptionEngine = _transcriptionEngineComboBox?.SelectedIndex == 1 ? "vosk" : "whisper";
            currentSettings.GmailUser = _gmailUserTextBox?.Text;
            currentSettings.GmailPassword = _gmailPasswordTextBox?.Text;
            currentSettings.AutoplayAlerts = _autoplayAlertsCheckBox?.IsChecked ?? currentSettings.AutoplayAlerts;
            currentSettings.AutoCleanupCalls = _autoCleanupCallsCheckBox?.IsChecked ?? currentSettings.AutoCleanupCalls;
            currentSettings.MaxCallsToKeep = int.TryParse(_maxCallsToKeepTextBox?.Text, out int maxCalls) ? maxCalls : currentSettings.MaxCallsToKeep;
            currentSettings.TraceLevelApp = _traceLevelComboBox?.SelectedIndex switch
            {
                0 => System.Diagnostics.SourceLevels.Error,
                1 => System.Diagnostics.SourceLevels.Warning,
                2 => System.Diagnostics.SourceLevels.Information,
                3 => System.Diagnostics.SourceLevels.Verbose,
                _ => currentSettings.TraceLevelApp
            };
            currentSettings.SnoozeDurationMinutes = _snoozeDurationComboBox?.SelectedIndex switch
            {
                0 => 5,
                1 => 15,
                2 => 30,
                3 => 60,
                _ => currentSettings.SnoozeDurationMinutes
            };

            if (!string.IsNullOrWhiteSpace(currentSettings.TranscriptionModelPreset))
            {
                if (currentSettings.TranscriptionModelPreset.StartsWith("vosk-",
                        StringComparison.OrdinalIgnoreCase))
                {
                    currentSettings.TranscriptionEngine = "vosk";
                }
                else if (currentSettings.TranscriptionModelPreset.StartsWith("whisper-",
                    StringComparison.OrdinalIgnoreCase))
                {
                    currentSettings.TranscriptionEngine = "whisper";
                }
            }

            currentSettings.Validate();
            currentSettings.SaveToFile(path);
            _currentSettingsPath = path;
            UpdateSettingsPathText();

            ShowMessage("Save As Complete", $"Saved settings to {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            ShowMessage("Save As Failed", ex.Message);
        }
        finally
        {
            SetUiEnabled(true);
        }
    }

    private void SetUiEnabled(bool isEnabled)
    {
        if (this.Content is Control root)
        {
            root.IsEnabled = isEnabled;
        }
        else
        {
            IsEnabled = isEnabled;
        }
    }

    private void ShowMessage(string title, string message)
    {
        var okButton = new Button { Content = "OK", Width = 80, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 10)
        };

        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(12),
            Spacing = 8,
            Children = { text, okButton }
        };

        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        okButton.Click += (_, _) => window.Close();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }

    private void StartBackgroundModelPreload(Settings settings)
    {
        _ = Task.Run(async () =>
        {
            await TranscriberPreloader.PreloadAsync(settings, SetMainWindowStatus).ConfigureAwait(false);
        });
    }

    private void SetMainWindowStatus(string message)
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if (desktop?.MainWindow is MainWindow mainWindow)
        {
            Dispatcher.UIThread.Post(() => mainWindow.StatusText = message);
        }
    }

    private static int GetModelPresetIndex(string? preset)
    {
        return preset?.Trim().ToLowerInvariant() switch
        {
            "whisper-tiny" => 1,
            "whisper-base" => 2,
            "whisper-small" => 3,
            "whisper-medium" => 4,
            "whisper-large-v3" => 5,
            "vosk-model-small-en-us-0.15" => 6,
            "vosk-model-en-us-0.22" => 7,
            "vosk-model-en-us-0.22-lgraph" => 8,
            _ => 0
        };
    }

    private static string GetModelPresetValue(int? selectedIndex)
    {
        return selectedIndex switch
        {
            1 => "whisper-tiny",
            2 => "whisper-base",
            3 => "whisper-small",
            4 => "whisper-medium",
            5 => "whisper-large-v3",
            6 => "vosk-model-small-en-us-0.15",
            7 => "vosk-model-en-us-0.22",
            8 => "vosk-model-en-us-0.22-lgraph",
            _ => string.Empty
        };
    }

    private async Task ImportTalkgroupsFromCsv()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider == null)
        {
            return;
        }

        var csvProvider = new FilePickerFileType("CSV Files")
        {
            Patterns = new[] { "*.csv" }
        };

        var options = new FilePickerOpenOptions
        {
            Title = "Import Talkgroups CSV",
            FileTypeFilter = new[] { csvProvider }
        };

        var files = await storageProvider.OpenFilePickerAsync(options);

        if (files?.Count > 0)
        {
            var file = files[0];
            var path = file.Path.LocalPath;
            try
            {
                _settings!.Talkgroups = TalkgroupHelper.GetTalkgroupsFromCsv(path);
                var count = _settings.Talkgroups.Count;

                _settings.SaveToFile(_currentSettingsPath);
                UpdateTalkgroupCount();

                ShowMessage("Import Complete", $"Imported {count} talkgroups from {System.IO.Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                ShowMessage("Import Failed", ex.Message);
            }
        }
    }

    private void UpdateTalkgroupCount()
    {
        if (_talkgroupCountText != null)
        {
            var count = _settings?.Talkgroups?.Count ?? 0;
            _talkgroupCountText.Text = $"({count})";
        }
    }

    private void UpdateSettingsPathText()
    {
        if (_settingsPathText != null)
        {
            _settingsPathText.Text = _currentSettingsPath;
        }
        SettingsPathChanged?.Invoke(_currentSettingsPath);
    }

    private void ShowTalkgroupsTable()
    {
        var talkgroups = _settings?.Talkgroups ?? new List<Talkgroup>();

        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("80,70,140,*,120,140"),
            Margin = new Avalonia.Thickness(0, 0, 0, 6)
        };
        headerGrid.Children.Add(new TextBlock { Text = "ID", FontWeight = Avalonia.Media.FontWeight.Bold });
        headerGrid.Children.Add(new TextBlock { Text = "Mode", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
        headerGrid.Children.Add(new TextBlock { Text = "AlphaTag", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
        headerGrid.Children.Add(new TextBlock { Text = "Description", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
        headerGrid.Children.Add(new TextBlock { Text = "Tag", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
        headerGrid.Children.Add(new TextBlock { Text = "Category", FontWeight = Avalonia.Media.FontWeight.Bold, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
        Grid.SetColumn(headerGrid.Children[0], 0);
        Grid.SetColumn(headerGrid.Children[1], 1);
        Grid.SetColumn(headerGrid.Children[2], 2);
        Grid.SetColumn(headerGrid.Children[3], 3);
        Grid.SetColumn(headerGrid.Children[4], 4);
        Grid.SetColumn(headerGrid.Children[5], 5);

        var items = new ItemsControl
        {
            ItemsSource = talkgroups
        };
        items.ItemTemplate = new FuncDataTemplate<Talkgroup>((tg, _) =>
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("80,70,140,*,120,140"),
                Margin = new Avalonia.Thickness(0, 0, 0, 4)
            };
            row.Children.Add(new TextBlock { Text = tg.Id.ToString() });
            row.Children.Add(new TextBlock { Text = tg.Mode ?? string.Empty, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
            row.Children.Add(new TextBlock { Text = tg.AlphaTag ?? string.Empty, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
            row.Children.Add(new TextBlock { Text = tg.Description ?? string.Empty, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
            row.Children.Add(new TextBlock { Text = tg.Tag ?? string.Empty, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
            row.Children.Add(new TextBlock { Text = tg.Category ?? string.Empty, Margin = new Avalonia.Thickness(8, 0, 0, 0) });
            Grid.SetColumn(row.Children[0], 0);
            Grid.SetColumn(row.Children[1], 1);
            Grid.SetColumn(row.Children[2], 2);
            Grid.SetColumn(row.Children[3], 3);
            Grid.SetColumn(row.Children[4], 4);
            Grid.SetColumn(row.Children[5], 5);
            return row;
        });

        var closeButton = new Button { Content = "Close", Width = 90, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        var countLabel = new TextBlock
        {
            Text = $"Talkgroups: {talkgroups.Count}",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var headerRow = new DockPanel { LastChildFill = true, Margin = new Avalonia.Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(countLabel, Dock.Left);
        DockPanel.SetDock(closeButton, Dock.Right);
        headerRow.Children.Add(countLabel);
        headerRow.Children.Add(closeButton);

        var panel = new DockPanel { Margin = new Avalonia.Thickness(12) };
        DockPanel.SetDock(headerRow, Dock.Top);
        panel.Children.Add(headerRow);
        DockPanel.SetDock(headerGrid, Dock.Top);
        panel.Children.Add(headerGrid);
        panel.Children.Add(new ScrollViewer { Content = items });

        var window = new Window
        {
            Title = "Talkgroups",
            Width = 900,
            Height = 600,
            Content = panel,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        closeButton.Click += (_, _) => window.Close();
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            window.ShowDialog(owner);
        }
        else
        {
            window.Show();
        }
    }
}
