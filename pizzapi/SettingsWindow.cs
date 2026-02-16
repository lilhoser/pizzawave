using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using pizzalib;

namespace pizzapi;

public class SettingsWindow : Window
{
    private Settings? _settings;
    private TextBox? _listenPortTextBox;
    private TextBox? _whisperModelFilePathTextBox;
    private TextBox? _gmailUserTextBox;
    private TextBox? _gmailPasswordTextBox;

    // Parameterless constructor for XAML loading
    public SettingsWindow()
    {
        _settings = new Settings();
        InitializeComponent();
        LoadSettings();
    }

    public SettingsWindow(Settings settings) : this()
    {
        _settings = settings;
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
        var saveButton = this.FindControl<Button>("SaveButton");
        var cancelButton = this.FindControl<Button>("CancelButton");

        if (_listenPortTextBox != null && _whisperModelFilePathTextBox != null &&
            _gmailUserTextBox != null && _gmailPasswordTextBox != null &&
            saveButton != null && cancelButton != null)
        {
            var settings = _settings ??= new Settings();

            _listenPortTextBox.Text = settings.ListenPort.ToString();
            _whisperModelFilePathTextBox.Text = settings.WhisperModelFile ?? string.Empty;
            _gmailUserTextBox.Text = settings.GmailUser ?? string.Empty;
            _gmailPasswordTextBox.Text = settings.GmailPassword ?? string.Empty;

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
                        GmailPassword = settings.GmailPassword
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
}
