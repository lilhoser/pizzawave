using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    private ComboBox? _emailProviderComboBox;
    private TextBox? _gmailUserTextBox;
    private TextBox? _gmailPasswordTextBox;
    private Button? _testEmailButton;
    private CheckBox? _autoplayAlertsCheckBox;
    private ComboBox? _snoozeDurationComboBox;
    private Button? _importTalkgroupsButton;
    private Button? _viewTalkgroupsButton;
    private TextBlock? _talkgroupCountText;
    private ComboBox? _traceLevelComboBox;
    private bool _handlersWired;
    private string _currentSettingsPath = Settings.DefaultSettingsFileLocation;
    private TextBlock? _settingsPathText;
    private CheckBox? _lmLinkEnabledCheckBox;
    private TextBox? _lmLinkBaseUrlTextBox;
    private TextBox? _lmLinkApiKeyTextBox;
    private TextBox? _lmLinkModelTextBox;
    private TextBox? _lmLinkTimeoutTextBox;
    private TextBox? _lmLinkRetriesTextBox;
    private CheckBox? _dailyInsightsDigestCheckBox;

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
        _emailProviderComboBox = this.FindControl<ComboBox>("EmailProviderComboBox");
        _gmailUserTextBox = this.FindControl<TextBox>("GmailUserTextBox");
        _gmailPasswordTextBox = this.FindControl<TextBox>("GmailPasswordTextBox");
        _testEmailButton = this.FindControl<Button>("TestEmailButton");
        _autoplayAlertsCheckBox = this.FindControl<CheckBox>("AutoplayAlertsCheckBox");
        _snoozeDurationComboBox = this.FindControl<ComboBox>("SnoozeDurationComboBox");
        _importTalkgroupsButton = this.FindControl<Button>("ImportTalkgroupsButton");
        _viewTalkgroupsButton = this.FindControl<Button>("ViewTalkgroupsButton");
        _talkgroupCountText = this.FindControl<TextBlock>("TalkgroupCountText");
        _traceLevelComboBox = this.FindControl<ComboBox>("TraceLevelComboBox");
        _settingsPathText = this.FindControl<TextBlock>("SettingsPathText");
        _lmLinkEnabledCheckBox = this.FindControl<CheckBox>("LmLinkEnabledCheckBox");
        _lmLinkBaseUrlTextBox = this.FindControl<TextBox>("LmLinkBaseUrlTextBox");
        _lmLinkApiKeyTextBox = this.FindControl<TextBox>("LmLinkApiKeyTextBox");
        _lmLinkModelTextBox = this.FindControl<TextBox>("LmLinkModelTextBox");
        _lmLinkTimeoutTextBox = this.FindControl<TextBox>("LmLinkTimeoutTextBox");
        _lmLinkRetriesTextBox = this.FindControl<TextBox>("LmLinkRetriesTextBox");
        _dailyInsightsDigestCheckBox = this.FindControl<CheckBox>("DailyInsightsDigestCheckBox");
        var loadButton = this.FindControl<Button>("LoadButton");
        var saveButton = this.FindControl<Button>("SaveButton");
        var saveAsButton = this.FindControl<Button>("SaveAsButton");
        var cancelButton = this.FindControl<Button>("CancelButton");

        if (_listenPortTextBox != null && _transcriptionModelComboBox != null &&
            _transcriptionEngineComboBox != null &&
            _gmailUserTextBox != null && _gmailPasswordTextBox != null &&
            _autoplayAlertsCheckBox != null && _snoozeDurationComboBox != null &&
            _traceLevelComboBox != null &&
            saveButton != null && cancelButton != null)
        {
            var settings = _settings ??= new Settings();

            _listenPortTextBox.Text = settings.ListenPort.ToString();
            _transcriptionModelComboBox.SelectedIndex =
                GetModelPresetIndex(settings.TranscriptionModelPreset);
            _transcriptionEngineComboBox.SelectedIndex =
                string.Equals(settings.TranscriptionEngine, "vosk", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var provider = Settings.NormalizeEmailProvider(settings.EmailProvider);
            if (_emailProviderComboBox != null)
                _emailProviderComboBox.SelectedIndex = provider == "yahoo" ? 1 : 0;
            _gmailUserTextBox.Text = settings.EmailUser ?? string.Empty;
            _gmailPasswordTextBox.Text = settings.EmailPassword ?? string.Empty;
            _autoplayAlertsCheckBox.IsChecked = settings.AutoplayAlerts;

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
            
            if (_lmLinkEnabledCheckBox != null)
                _lmLinkEnabledCheckBox.IsChecked = settings.LmLinkEnabled;
            if (_lmLinkBaseUrlTextBox != null)
                _lmLinkBaseUrlTextBox.Text = settings.LmLinkBaseUrl ?? string.Empty;
            if (_lmLinkApiKeyTextBox != null)
                _lmLinkApiKeyTextBox.Text = settings.LmLinkApiKey ?? string.Empty;
            if (_lmLinkModelTextBox != null)
                _lmLinkModelTextBox.Text = settings.LmLinkModel ?? string.Empty;
            if (_lmLinkTimeoutTextBox != null)
                _lmLinkTimeoutTextBox.Text = settings.LmLinkTimeoutMs.ToString();
            if (_lmLinkRetriesTextBox != null)
                _lmLinkRetriesTextBox.Text = settings.LmLinkMaxRetries.ToString();
            if (_dailyInsightsDigestCheckBox != null)
                _dailyInsightsDigestCheckBox.IsChecked = settings.DailyInsightsDigestEnabled;
            UpdateDailyDigestAvailability();
            
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

                    var provider = SelectedEmailProvider();
                    if (!string.IsNullOrEmpty(newUser) && string.IsNullOrEmpty(newPass))
                    {
                        throw new Exception("Email password is required when email user is set");
                    }

                    var settingsCopy = new Settings
                    {
                        ListenPort = currentSettings.ListenPort,
                        TranscriptionEngine = currentSettings.TranscriptionEngine,
                        TranscriptionModelPreset = currentSettings.TranscriptionModelPreset,
                        EmailUser = currentSettings.EmailUser,
                        EmailPassword = currentSettings.EmailPassword,
                        LmLinkEnabled = currentSettings.LmLinkEnabled,
                        LmLinkBaseUrl = currentSettings.LmLinkBaseUrl,
                        LmLinkApiKey = currentSettings.LmLinkApiKey,
                        LmLinkModel = currentSettings.LmLinkModel,
                        LmLinkTimeoutMs = currentSettings.LmLinkTimeoutMs,
                        LmLinkMaxRetries = currentSettings.LmLinkMaxRetries,
                        DailyInsightsDigestEnabled = currentSettings.DailyInsightsDigestEnabled,
                        EmailProvider = currentSettings.EmailProvider,
                        AutoplayAlerts = _autoplayAlertsCheckBox.IsChecked ?? false,
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
                    settingsCopy.EmailUser = newUser;
                    settingsCopy.EmailPassword = newPass;
                    settingsCopy.LmLinkEnabled = _lmLinkEnabledCheckBox?.IsChecked ?? false;
                    settingsCopy.LmLinkBaseUrl = _lmLinkBaseUrlTextBox?.Text ?? string.Empty;
                    settingsCopy.LmLinkApiKey = _lmLinkApiKeyTextBox?.Text ?? string.Empty;
                    settingsCopy.LmLinkModel = _lmLinkModelTextBox?.Text ?? string.Empty;
                    settingsCopy.LmLinkTimeoutMs = int.TryParse(_lmLinkTimeoutTextBox?.Text, out var timeoutMs) ? timeoutMs : settingsCopy.LmLinkTimeoutMs;
                    settingsCopy.LmLinkMaxRetries = int.TryParse(_lmLinkRetriesTextBox?.Text, out var retries) ? retries : settingsCopy.LmLinkMaxRetries;
                    settingsCopy.DailyInsightsDigestEnabled = (_dailyInsightsDigestCheckBox?.IsChecked ?? false) && IsDailyDigestPrereqsMet();
                    settingsCopy.EmailProvider = provider;

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
                    currentSettings.EmailUser = settingsCopy.EmailUser;
                    currentSettings.EmailPassword = settingsCopy.EmailPassword;
                    currentSettings.LmLinkEnabled = settingsCopy.LmLinkEnabled;
                    currentSettings.LmLinkBaseUrl = settingsCopy.LmLinkBaseUrl;
                    currentSettings.LmLinkApiKey = settingsCopy.LmLinkApiKey;
                    currentSettings.LmLinkModel = settingsCopy.LmLinkModel;
                    currentSettings.LmLinkTimeoutMs = settingsCopy.LmLinkTimeoutMs;
                    currentSettings.LmLinkMaxRetries = settingsCopy.LmLinkMaxRetries;
                    currentSettings.DailyInsightsDigestEnabled = settingsCopy.DailyInsightsDigestEnabled;
                    currentSettings.EmailProvider = settingsCopy.EmailProvider;
                    currentSettings.AutoplayAlerts = settingsCopy.AutoplayAlerts;
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

        if (_lmLinkEnabledCheckBox != null)
        {
            _lmLinkEnabledCheckBox.PropertyChanged += (_, args) =>
            {
                if (args.Property == ToggleButton.IsCheckedProperty)
                    UpdateDailyDigestAvailability();
            };
        }
        if (_lmLinkBaseUrlTextBox != null)
            _lmLinkBaseUrlTextBox.TextChanged += (_, _) => UpdateDailyDigestAvailability();
        if (_lmLinkModelTextBox != null)
            _lmLinkModelTextBox.TextChanged += (_, _) => UpdateDailyDigestAvailability();
        if (_gmailUserTextBox != null)
            _gmailUserTextBox.TextChanged += (_, _) =>
            {
                AutoSelectProviderFromEmail();
                UpdateDailyDigestAvailability();
            };
        if (_gmailPasswordTextBox != null)
            _gmailPasswordTextBox.TextChanged += (_, _) => UpdateDailyDigestAvailability();
        if (_emailProviderComboBox != null)
        {
            _emailProviderComboBox.SelectionChanged += (_, _) =>
            {
                UpdateDailyDigestAvailability();
            };
        }
        if (_testEmailButton != null)
        {
            _testEmailButton.Click += (_, _) => SendTestEmail();
        }
    }

    private bool IsDailyDigestPrereqsMet()
    {
        var lmEnabled = _lmLinkEnabledCheckBox?.IsChecked ?? false;
        var baseUrl = _lmLinkBaseUrlTextBox?.Text;
        var model = _lmLinkModelTextBox?.Text;
        var emailUser = _gmailUserTextBox?.Text;
        var emailPassword = _gmailPasswordTextBox?.Text;
        var hasAppPassword = !string.IsNullOrWhiteSpace(emailUser)
                             && !string.IsNullOrWhiteSpace(emailPassword);

        return lmEnabled
               && !string.IsNullOrWhiteSpace(baseUrl)
               && !string.IsNullOrWhiteSpace(model)
               && hasAppPassword;
    }

    private string SelectedEmailProvider()
    {
        return _emailProviderComboBox?.SelectedIndex == 1 ? "yahoo" : "gmail";
    }

    private void AutoSelectProviderFromEmail()
    {
        if (_emailProviderComboBox == null)
            return;

        var provider = InferProviderFromEmail(_gmailUserTextBox?.Text);
        if (provider == null)
            return;

        var targetIndex = provider == "yahoo" ? 1 : 0;
        if (_emailProviderComboBox.SelectedIndex != targetIndex)
            _emailProviderComboBox.SelectedIndex = targetIndex;
    }

    private static string? InferProviderFromEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var at = email.LastIndexOf('@');
        if (at < 0 || at == email.Length - 1)
            return null;

        var domain = email[(at + 1)..].Trim().ToLowerInvariant();
        if (domain.EndsWith("gmail.com", StringComparison.Ordinal) ||
            domain.EndsWith("googlemail.com", StringComparison.Ordinal))
            return "gmail";

        if (domain.EndsWith("yahoo.com", StringComparison.Ordinal) ||
            domain.EndsWith("yahoo.co.uk", StringComparison.Ordinal) ||
            domain.EndsWith("yahoo.ca", StringComparison.Ordinal) ||
            domain.EndsWith("yahoo.com.au", StringComparison.Ordinal) ||
            domain.EndsWith("yahoo.co.jp", StringComparison.Ordinal) ||
            domain.EndsWith("ymail.com", StringComparison.Ordinal) ||
            domain.EndsWith("rocketmail.com", StringComparison.Ordinal))
            return "yahoo";

        return null;
    }

    private void UpdateDailyDigestAvailability()
    {
        if (_dailyInsightsDigestCheckBox == null)
            return;

        var isAvailable = IsDailyDigestPrereqsMet();
        _dailyInsightsDigestCheckBox.IsEnabled = isAvailable;
        if (!isAvailable)
            _dailyInsightsDigestCheckBox.IsChecked = false;
    }

    private void SendTestEmail()
    {
        try
        {
            var emailUser = _gmailUserTextBox?.Text?.Trim();
            var emailPassword = _gmailPasswordTextBox?.Text;
            var provider = SelectedEmailProvider();

            if (string.IsNullOrWhiteSpace(emailUser))
                throw new Exception("Set Email User before sending a test.");
            if (string.IsNullOrWhiteSpace(emailPassword))
                throw new Exception("Set Email Password before sending a test.");

            var testSettings = new Settings
            {
                EmailUser = emailUser,
                EmailPassword = emailPassword,
                EmailProvider = provider
            };

            EmailSender.SendHtml(
                testSettings,
                "pizzawave test",
                emailUser,
                $"pizzawave test email ({provider})",
                $"Test email sent at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} using provider '{provider}'.");

            ShowMessage("Test Email Sent", $"Sent test email to {emailUser}");
        }
        catch (Exception ex)
        {
            ShowMessage("Test Email Failed", ex.Message);
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
            // Persist loaded settings to the app default so future launches use them
            loaded.SaveToFile(Settings.DefaultSettingsFileLocation);

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
            currentSettings.EmailUser = _gmailUserTextBox?.Text;
            currentSettings.EmailPassword = _gmailPasswordTextBox?.Text;
            currentSettings.EmailProvider = SelectedEmailProvider();
            currentSettings.LmLinkEnabled = _lmLinkEnabledCheckBox?.IsChecked ?? currentSettings.LmLinkEnabled;
            currentSettings.LmLinkBaseUrl = _lmLinkBaseUrlTextBox?.Text ?? currentSettings.LmLinkBaseUrl;
            currentSettings.LmLinkApiKey = _lmLinkApiKeyTextBox?.Text ?? currentSettings.LmLinkApiKey;
            currentSettings.LmLinkModel = _lmLinkModelTextBox?.Text ?? currentSettings.LmLinkModel;
            currentSettings.LmLinkTimeoutMs = int.TryParse(_lmLinkTimeoutTextBox?.Text, out var timeoutMs) ? timeoutMs : currentSettings.LmLinkTimeoutMs;
            currentSettings.LmLinkMaxRetries = int.TryParse(_lmLinkRetriesTextBox?.Text, out var retries) ? retries : currentSettings.LmLinkMaxRetries;
            currentSettings.DailyInsightsDigestEnabled = (_dailyInsightsDigestCheckBox?.IsChecked ?? false) && IsDailyDigestPrereqsMet();
            currentSettings.AutoplayAlerts = _autoplayAlertsCheckBox?.IsChecked ?? currentSettings.AutoplayAlerts;
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


