using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using pizzalib;

namespace pizzapi;

public class SettingsWindow : Window
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
    private CheckBox? _autoCleanupCallsCheckBox;
    private TextBox? _maxCallsToKeepTextBox;
    private ComboBox? _traceLevelComboBox;

    // Parameterless constructor for XAML loading
    public SettingsWindow()
    {
        _settings = new Settings();
        InitializeComponent();
        LoadSettings();
    }

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
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
        _autoCleanupCallsCheckBox = this.FindControl<CheckBox>("AutoCleanupCallsCheckBox");
        _maxCallsToKeepTextBox = this.FindControl<TextBox>("MaxCallsToKeepTextBox");
        _traceLevelComboBox = this.FindControl<ComboBox>("TraceLevelComboBox");
        var saveButton = this.FindControl<Button>("SaveButton");
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
            _gmailUserTextBox.Text = settings.EmailUser ?? string.Empty;
            _gmailPasswordTextBox.Text = settings.EmailPassword ?? string.Empty;
            _autoplayAlertsCheckBox.IsChecked = settings.AutoplayAlerts;
            _autoCleanupCallsCheckBox.IsChecked = settings.AutoCleanupCalls;
            _maxCallsToKeepTextBox.Text = settings.MaxCallsToKeep.ToString();

            // Set trace level combo box based on settings
            _traceLevelComboBox.SelectedIndex = settings.TraceLevelApp switch
            {
                System.Diagnostics.SourceLevels.Error => 0,
                System.Diagnostics.SourceLevels.Warning => 1,
                System.Diagnostics.SourceLevels.Information => 2,
                System.Diagnostics.SourceLevels.Verbose => 3,
                _ => 0 // Default to Error
            };

            // Set snooze duration combo box based on settings
            _snoozeDurationComboBox.SelectedIndex = settings.SnoozeDurationMinutes switch
            {
                5 => 0,
                15 => 1,
                30 => 2,
                60 => 3,
                _ => 1 // Default to 15 minutes
            };

            // Import Talkgroups button handler
            if (_importTalkgroupsButton != null)
            {
                _importTalkgroupsButton.Click += async (s, e) =>
                {
                    await ImportTalkgroupsFromCsv();
                };
            }

            saveButton.Click += (s, e) =>
            {
                try
                {
                    // Validate BEFORE saving - check Gmail user/password together
                    var newUser = _gmailUserTextBox.Text;
                    var newPass = _gmailPasswordTextBox.Text;

                    if (!string.IsNullOrEmpty(newUser) && string.IsNullOrEmpty(newPass))
                    {
                        throw new Exception("Gmail password is required when Gmail user is set");
                    }

                    // Create a copy to validate, but don't update original until validation passes
                    var settingsCopy = new Settings
                    {
                        ListenPort = settings.ListenPort,
                        TranscriptionEngine = settings.TranscriptionEngine,
                        TranscriptionModelPreset = settings.TranscriptionModelPreset,
                        WhisperModelFile = settings.WhisperModelFile,
                        VoskModelPath = settings.VoskModelPath,
                        EmailUser = settings.EmailUser,
                        EmailPassword = settings.EmailPassword,
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

                    // Apply new values to copy for validation
                    if (int.TryParse(_listenPortTextBox.Text, out int port))
                    {
                        settingsCopy.ListenPort = port;
                    }

                    settingsCopy.TranscriptionModelPreset =
                        GetModelPresetValue(_transcriptionModelComboBox.SelectedIndex);
                    settingsCopy.TranscriptionEngine =
                        _transcriptionEngineComboBox.SelectedIndex == 1 ? "vosk" : "whisper";
                    settingsCopy.EmailUser = newUser;
                    settingsCopy.EmailPassword = newPass;

                    // If a model preset was chosen, align engine with model family.
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

                    // If a preset is chosen, clear any persisted model path so the preset takes effect.
                    if (!string.IsNullOrWhiteSpace(settingsCopy.TranscriptionModelPreset))
                    {
                        settingsCopy.VoskModelPath = string.Empty;
                        settingsCopy.WhisperModelFile = string.Empty;
                    }

                    // Validate the copy
                    settingsCopy.Validate();

                    // If validation passes, apply to real settings and save
                    settings.ListenPort = settingsCopy.ListenPort;
                    settings.TranscriptionEngine = settingsCopy.TranscriptionEngine;
                    settings.TranscriptionModelPreset = settingsCopy.TranscriptionModelPreset;
                    settings.EmailUser = settingsCopy.EmailUser;
                    settings.EmailPassword = settingsCopy.EmailPassword;
                    settings.AutoplayAlerts = settingsCopy.AutoplayAlerts;
                    settings.AutoCleanupCalls = settingsCopy.AutoCleanupCalls;
                    settings.MaxCallsToKeep = settingsCopy.MaxCallsToKeep;
                    settings.TraceLevelApp = settingsCopy.TraceLevelApp;
                    settings.SnoozeDurationMinutes = settingsCopy.SnoozeDurationMinutes;

                    settings.SaveToFile();
                    StartBackgroundModelPreload(settings);
                    Close();
                }
                catch (Exception ex)
                {
                    // Display error message
                    var errorLabel = new TextBlock { Text = $"Error: {ex.Message}", Foreground = Brushes.Red };
                    errorLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                    errorLabel.Margin = new Avalonia.Thickness(0, 10, 0, 0);
                    this.Content = new StackPanel { Children = { errorLabel } };
                }
            };

            cancelButton.Click += (s, e) => Close();
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

    private async System.Threading.Tasks.Task ImportTalkgroupsFromCsv()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider == null)
        {
            return;
        }

        // Create storage providers for CSV files
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
                // Load CSV file and update settings
                _settings!.Talkgroups = TalkgroupHelper.GetTalkgroupsFromCsv(path);
                var count = _settings.Talkgroups.Count;

                // Save settings
                _settings.SaveToFile();

                // Show success message
                var infoLabel = new TextBlock { Text = $"Imported {count} talkgroups from {System.IO.Path.GetFileName(path)}", Foreground = Brushes.Green };
                infoLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                infoLabel.Margin = new Avalonia.Thickness(0, 10, 0, 0);
                var panel = this.Content as StackPanel;
                if (panel != null)
                {
                    panel.Children.Add(infoLabel);
                }
            }
            catch (Exception ex)
            {
                // Display error message
                var errorLabel = new TextBlock { Text = $"Error importing talkgroups: {ex.Message}", Foreground = Brushes.Red };
                errorLabel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                errorLabel.Margin = new Avalonia.Thickness(0, 10, 0, 0);
                var panel = this.Content as StackPanel;
                if (panel != null)
                {
                    panel.Children.Add(errorLabel);
                }
            }
        }
    }
}


