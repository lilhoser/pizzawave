using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using pizzalib;

namespace pizzapi;

public class SettingsWindow : Window
{
    private Settings? _settings;
    private TextBox? _listenPortTextBox;
    private TextBox? _whisperModelFilePathTextBox;
    private TextBox? _gmailUserTextBox;
    private TextBox? _gmailPasswordTextBox;
    private CheckBox? _autoplayAlertsCheckBox;
    private ComboBox? _snoozeDurationComboBox;
    private Button? _importTalkgroupsButton;

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
        _whisperModelFilePathTextBox = this.FindControl<TextBox>("WhisperModelTextBox");
        _gmailUserTextBox = this.FindControl<TextBox>("GmailUserTextBox");
        _gmailPasswordTextBox = this.FindControl<TextBox>("GmailPasswordTextBox");
        _autoplayAlertsCheckBox = this.FindControl<CheckBox>("AutoplayAlertsCheckBox");
        _snoozeDurationComboBox = this.FindControl<ComboBox>("SnoozeDurationComboBox");
        _importTalkgroupsButton = this.FindControl<Button>("ImportTalkgroupsButton");
        var saveButton = this.FindControl<Button>("SaveButton");
        var cancelButton = this.FindControl<Button>("CancelButton");

        if (_listenPortTextBox != null && _whisperModelFilePathTextBox != null &&
            _gmailUserTextBox != null && _gmailPasswordTextBox != null &&
            _autoplayAlertsCheckBox != null && _snoozeDurationComboBox != null &&
            saveButton != null && cancelButton != null)
        {
            var settings = _settings ??= new Settings();

            _listenPortTextBox.Text = settings.ListenPort.ToString();
            _whisperModelFilePathTextBox.Text = settings.WhisperModelFile ?? string.Empty;
            _gmailUserTextBox.Text = settings.GmailUser ?? string.Empty;
            _gmailPasswordTextBox.Text = settings.GmailPassword ?? string.Empty;
            _autoplayAlertsCheckBox.IsChecked = settings.AutoplayAlerts;

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
                        WhisperModelFile = settings.WhisperModelFile,
                        GmailUser = settings.GmailUser,
                        GmailPassword = settings.GmailPassword,
                        AutoplayAlerts = _autoplayAlertsCheckBox.IsChecked ?? false,
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

                    settingsCopy.WhisperModelFile = _whisperModelFilePathTextBox.Text;
                    settingsCopy.GmailUser = newUser;
                    settingsCopy.GmailPassword = newPass;

                    // Validate the copy
                    settingsCopy.Validate();

                    // If validation passes, apply to real settings and save
                    settings.ListenPort = settingsCopy.ListenPort;
                    settings.WhisperModelFile = settingsCopy.WhisperModelFile;
                    settings.GmailUser = settingsCopy.GmailUser;
                    settings.GmailPassword = settingsCopy.GmailPassword;
                    settings.AutoplayAlerts = settingsCopy.AutoplayAlerts;
                    settings.SnoozeDurationMinutes = settingsCopy.SnoozeDurationMinutes;

                    settings.SaveToFile();
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
