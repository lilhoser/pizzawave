using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using pizzalib;

namespace pizzapi;

public partial class AlertManagerPanel : UserControl
{
    private Settings _settings;
    private string _settingsPath = Settings.DefaultSettingsFileLocation;
    private readonly TalkgroupMappingStore _talkgroupMappingStore = new();
    private Alert? _editingAlert;
    private ObservableCollection<Talkgroup> _talkgroups;
    private ObservableCollection<Alert> _alertsCollection;
    private HashSet<long> _selectedTalkgroupIds;
    private Dictionary<long, CheckBox> _talkgroupCheckboxes;
    private Dictionary<string, CheckBox> _policeCodeCheckboxes;

    public event EventHandler? RequestClose;

    public AlertManagerPanel() : this(new Settings())
    {
    }

    public AlertManagerPanel(Settings settings)
    {
        _settings = settings;
        _settingsPath = Settings.DefaultSettingsFileLocation;
        _talkgroups = new ObservableCollection<Talkgroup>(LoadTalkgroupsFromMappings());
        _alertsCollection = new ObservableCollection<Alert>(_settings.Alerts ?? new List<Alert>());
        _selectedTalkgroupIds = new HashSet<long>();
        _talkgroupCheckboxes = new Dictionary<long, CheckBox>();
        _policeCodeCheckboxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);

        InitializeComponent();
        LoadAlertsList();
        SetupEventHandlers();
        LoadTalkgroupCheckboxes();
        LoadPoliceCodeCheckboxes();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void LoadTalkgroupCheckboxes()
    {
        var panel = this.FindControl<StackPanel>("TalkgroupCheckboxesPanel");
        if (panel == null) return;

        panel.Children.Clear();
        _talkgroupCheckboxes.Clear();

        foreach (var talkgroup in _talkgroups)
        {
            var checkBox = new CheckBox
            {
                Content = talkgroup.Description,
                Tag = talkgroup.Id,
                Margin = new Avalonia.Thickness(0, 4, 0, 0)
            };
            checkBox.IsCheckedChanged += OnTalkgroupCheckedChanged;
            panel.Children.Add(checkBox);
            _talkgroupCheckboxes[talkgroup.Id] = checkBox;
        }
    }

    private void LoadPoliceCodeCheckboxes()
    {
        var panel = this.FindControl<StackPanel>("PoliceCodeCheckboxesPanel");
        if (panel == null) return;

        panel.Children.Clear();
        _policeCodeCheckboxes.Clear();

        foreach (var kv in PoliceCodeLookup.GetSupportedCodes().OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var checkBox = new CheckBox
            {
                Content = $"{kv.Key} - {kv.Value}",
                Tag = kv.Key
            };
            panel.Children.Add(checkBox);
            _policeCodeCheckboxes[kv.Key] = checkBox;
        }
    }

    private void UpdateMatchTypeInputVisibility()
    {
        var keywordPanel = this.FindControl<StackPanel>("KeywordInputPanel");
        var policePanel = this.FindControl<StackPanel>("PoliceCodeInputPanel");

        if (keywordPanel == null || policePanel == null)
            return;

        var matchType = GetSelectedMatchType();
        keywordPanel.IsVisible = matchType == AlertMatchType.Keyword;
        policePanel.IsVisible = matchType == AlertMatchType.PoliceCode;
    }

    private AlertMatchType GetSelectedMatchType()
    {
        if (this.FindControl<RadioButton>("MatchPoliceCodeRadio")?.IsChecked == true)
            return AlertMatchType.PoliceCode;
        return AlertMatchType.Keyword;
    }

    private void SetSelectedMatchType(AlertMatchType type)
    {
        var keyword = this.FindControl<RadioButton>("MatchKeywordRadio");
        var police = this.FindControl<RadioButton>("MatchPoliceCodeRadio");

        if (keyword == null || police == null)
            return;

        var normalized = type == AlertMatchType.PoliceCode
            ? AlertMatchType.PoliceCode
            : AlertMatchType.Keyword;
        keyword.IsChecked = normalized == AlertMatchType.Keyword;
        police.IsChecked = normalized == AlertMatchType.PoliceCode;
        UpdateMatchTypeInputVisibility();
    }

    private string GetSelectedPoliceCodesCsv()
    {
        var selected = _policeCodeCheckboxes
            .Where(kvp => kvp.Value.IsChecked == true)
            .Select(kvp => kvp.Key)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", selected);
    }

    private void SetSelectedPoliceCodes(string? csv)
    {
        var selected = new HashSet<string>(
            (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _policeCodeCheckboxes)
        {
            kvp.Value.IsChecked = selected.Contains(kvp.Key);
        }
    }

    private void OnTalkgroupCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is long tgId)
        {
            if (checkBox.IsChecked == true)
                _selectedTalkgroupIds.Add(tgId);
            else
                _selectedTalkgroupIds.Remove(tgId);
        }
    }

    private void SetupEventHandlers()
    {
        var closeBtn = this.FindControl<Button>("CloseButton");
        var addUpdateBtn = this.FindControl<Button>("AddUpdateButton");
        var clearFormBtn = this.FindControl<Button>("ClearFormButton");
        var deleteBtn = this.FindControl<Button>("DeleteButton");
        var allRadio = this.FindControl<RadioButton>("AllTalkgroupsRadio");
        var selectedRadio = this.FindControl<RadioButton>("SelectedTalkgroupsRadio");
        var matchKeywordRadio = this.FindControl<RadioButton>("MatchKeywordRadio");
        var matchPoliceCodeRadio = this.FindControl<RadioButton>("MatchPoliceCodeRadio");
        var talkgroupBorder = this.FindControl<Border>("TalkgroupListBorder");
        var alertsListBox = this.FindControl<ListBox>("AlertsListBox");

        if (closeBtn != null)
            closeBtn.Click += (s, e) => RequestClose?.Invoke(this, EventArgs.Empty);

        if (addUpdateBtn != null)
            addUpdateBtn.Click += OnAddUpdateClicked;

        if (clearFormBtn != null)
            clearFormBtn.Click += (s, e) => ResetForm();

        if (deleteBtn != null)
            deleteBtn.Click += OnDeleteClicked;

        if (alertsListBox != null)
            alertsListBox.SelectionChanged += OnAlertsListSelectionChanged;

        if (allRadio != null)
            allRadio.IsCheckedChanged += (s, e) =>
            {
                if (allRadio.IsChecked == true)
                {
                    talkgroupBorder!.IsVisible = false;
                }
            };

        if (selectedRadio != null)
            selectedRadio.IsCheckedChanged += (s, e) =>
            {
                if (selectedRadio.IsChecked == true)
                {
                    talkgroupBorder!.IsVisible = true;
                }
            };

        if (talkgroupBorder != null)
            talkgroupBorder.IsVisible = false;

        if (matchKeywordRadio != null)
            matchKeywordRadio.IsCheckedChanged += (s, e) => UpdateMatchTypeInputVisibility();
        if (matchPoliceCodeRadio != null)
            matchPoliceCodeRadio.IsCheckedChanged += (s, e) => UpdateMatchTypeInputVisibility();

        var freqCombo = this.FindControl<ComboBox>("AlertFrequencyComboBox");
        if (freqCombo != null)
            freqCombo.SelectedIndex = 0;

        UpdateMatchTypeInputVisibility();
    }

    private void OnAlertsListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var listBox = sender as ListBox;
        if (listBox?.SelectedItem is Alert selectedAlert && _alertsCollection.Contains(selectedAlert))
        {
            LoadAlertIntoForm(selectedAlert);
        }
        else
        {
            ResetForm();
        }
    }

    private void LoadAlertsList()
    {
        var listBox = this.FindControl<ListBox>("AlertsListBox");
        if (listBox != null)
        {
            listBox.ItemsSource = _alertsCollection;
        }
    }

    public void SetSettings(Settings settings, string? settingsPath = null)
    {
        _settings = settings;
        if (!string.IsNullOrWhiteSpace(settingsPath))
            _settingsPath = settingsPath;

        _talkgroups = new ObservableCollection<Talkgroup>(LoadTalkgroupsFromMappings());
        _alertsCollection = new ObservableCollection<Alert>(_settings.Alerts ?? new List<Alert>());
        _selectedTalkgroupIds.Clear();
        _editingAlert = null;

        LoadAlertsList();
        LoadTalkgroupCheckboxes();
        LoadPoliceCodeCheckboxes();
        ResetForm();
    }

    private void OnAddUpdateClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var alert = GetAlertFromForm();
            alert.Validate();

            if (_editingAlert != null)
            {
                var existingAlert = _alertsCollection.FirstOrDefault(a => a.Id == _editingAlert.Id);
                if (existingAlert != null)
                {
                    existingAlert.Name = alert.Name;
                    existingAlert.Email = alert.Email;
                    existingAlert.Keywords = alert.Keywords;
                    existingAlert.PoliceCodes = alert.PoliceCodes;
                    existingAlert.MatchType = alert.MatchType;
                    existingAlert.Frequency = alert.Frequency;
                    existingAlert.Enabled = alert.Enabled;
                    existingAlert.Autoplay = alert.Autoplay;
                    existingAlert.Talkgroups = alert.Talkgroups;

                    var settingsIndex = _settings.Alerts.IndexOf(existingAlert);
                    if (settingsIndex >= 0)
                    {
                        _settings.Alerts[settingsIndex] = existingAlert;
                    }
                }
            }
            else
            {
                _alertsCollection.Add(alert);
                _settings.Alerts.Add(alert);
            }

            _settings.SaveToFile(_settingsPath);
            ResetForm();
        }
        catch (Exception ex)
        {
            ShowError($"Error: {ex.Message}");
        }
    }

    private void OnDeleteClicked(object? sender, RoutedEventArgs e)
    {
        var listBox = this.FindControl<ListBox>("AlertsListBox");
        if (listBox?.SelectedItem is Alert selectedAlert)
        {
            _alertsCollection.Remove(selectedAlert);
            _settings.Alerts.Remove(selectedAlert);
            _settings.SaveToFile(_settingsPath);

            listBox.SelectedItem = null;
            ResetForm();
        }
        else
        {
            ShowError("Please select an alert to delete");
        }
    }

    private List<Talkgroup> LoadTalkgroupsFromMappings()
    {
        var rows = _talkgroupMappingStore.LoadAll()
            .GroupBy(m => m.TalkgroupId)
            .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
            .OrderBy(m => m.TalkgroupId)
            .Select(m => new Talkgroup
            {
                Id = m.TalkgroupId,
                Mode = m.Mode,
                AlphaTag = m.AlphaTag,
                Description = m.Description,
                Tag = m.Tag,
                Category = m.Category
            })
            .ToList();
        return rows;
    }

    private Alert GetAlertFromForm()
    {
        var alertName = this.FindControl<TextBox>("AlertNameTextBox")?.Text ?? string.Empty;
        var alertEmail = this.FindControl<TextBox>("AlertEmailTextBox")?.Text ?? string.Empty;
        var alertKeywords = this.FindControl<TextBox>("AlertKeywordsTextBox")?.Text ?? string.Empty;
        var alertPoliceCodes = GetSelectedPoliceCodesCsv();
        var alertFrequency = this.FindControl<ComboBox>("AlertFrequencyComboBox")?.SelectedIndex ?? 0;
        var enabled = this.FindControl<CheckBox>("EnabledCheckBox")?.IsChecked ?? true;
        var autoplay = this.FindControl<CheckBox>("AutoplayCheckBox")?.IsChecked ?? true;
        var allRadio = this.FindControl<RadioButton>("AllTalkgroupsRadio");
        var selectedMatchType = GetSelectedMatchType();

        var alert = new Alert
        {
            Name = alertName,
            Email = alertEmail,
            Keywords = alertKeywords,
            PoliceCodes = alertPoliceCodes,
            MatchType = selectedMatchType,
            Frequency = (AlertFrequency)alertFrequency,
            Enabled = enabled,
            Autoplay = autoplay
        };

        if (allRadio?.IsChecked == false)
        {
            alert.Talkgroups = _selectedTalkgroupIds.ToList();
        }

        return alert;
    }

    private void LoadAlertIntoForm(Alert alert)
    {
        _editingAlert = alert;

        var alertName = this.FindControl<TextBox>("AlertNameTextBox");
        var alertEmail = this.FindControl<TextBox>("AlertEmailTextBox");
        var alertKeywords = this.FindControl<TextBox>("AlertKeywordsTextBox");
        var alertFrequency = this.FindControl<ComboBox>("AlertFrequencyComboBox");
        var enabled = this.FindControl<CheckBox>("EnabledCheckBox");
        var autoplay = this.FindControl<CheckBox>("AutoplayCheckBox");
        var allRadio = this.FindControl<RadioButton>("AllTalkgroupsRadio");
        var selectedRadio = this.FindControl<RadioButton>("SelectedTalkgroupsRadio");
        var talkgroupBorder = this.FindControl<Border>("TalkgroupListBorder");

        if (alertName != null) alertName.Text = alert.Name;
        if (alertEmail != null) alertEmail.Text = alert.Email;
        if (alertKeywords != null) alertKeywords.Text = alert.Keywords;
        SetSelectedPoliceCodes(alert.PoliceCodes);
        if (alertFrequency != null) alertFrequency.SelectedIndex = (int)alert.Frequency;
        if (enabled != null) enabled.IsChecked = alert.Enabled;
        if (autoplay != null) autoplay.IsChecked = alert.Autoplay;

        var selectedType = alert.MatchType;
        SetSelectedMatchType(selectedType);

        if (alert.Talkgroups.Count > 0)
        {
            selectedRadio!.IsChecked = true;
            allRadio!.IsChecked = false;
            talkgroupBorder!.IsVisible = true;
            _selectedTalkgroupIds = new HashSet<long>(alert.Talkgroups);
        }
        else
        {
            allRadio!.IsChecked = true;
            selectedRadio!.IsChecked = false;
            talkgroupBorder!.IsVisible = false;
            _selectedTalkgroupIds.Clear();
        }

        foreach (var kvp in _talkgroupCheckboxes)
        {
            kvp.Value.IsChecked = _selectedTalkgroupIds.Contains(kvp.Key);
        }

        var addUpdateBtn = this.FindControl<Button>("AddUpdateButton");
        if (addUpdateBtn != null)
            addUpdateBtn.Content = "Update Alert";
    }

    private void ResetForm()
    {
        _editingAlert = null;

        var alertName = this.FindControl<TextBox>("AlertNameTextBox");
        var alertEmail = this.FindControl<TextBox>("AlertEmailTextBox");
        var alertKeywords = this.FindControl<TextBox>("AlertKeywordsTextBox");
        var alertFrequency = this.FindControl<ComboBox>("AlertFrequencyComboBox");
        var enabled = this.FindControl<CheckBox>("EnabledCheckBox");
        var autoplay = this.FindControl<CheckBox>("AutoplayCheckBox");
        var allRadio = this.FindControl<RadioButton>("AllTalkgroupsRadio");
        var selectedRadio = this.FindControl<RadioButton>("SelectedTalkgroupsRadio");
        var talkgroupBorder = this.FindControl<Border>("TalkgroupListBorder");

        if (alertName != null) alertName.Text = string.Empty;
        if (alertEmail != null) alertEmail.Text = string.Empty;
        if (alertKeywords != null) alertKeywords.Text = string.Empty;
        SetSelectedPoliceCodes(string.Empty);
        if (alertFrequency != null) alertFrequency.SelectedIndex = 0;
        if (enabled != null) enabled.IsChecked = true;
        if (autoplay != null) autoplay.IsChecked = true;
        SetSelectedMatchType(AlertMatchType.Keyword);

        allRadio!.IsChecked = true;
        selectedRadio!.IsChecked = false;
        talkgroupBorder!.IsVisible = false;
        _selectedTalkgroupIds.Clear();

        foreach (var kvp in _talkgroupCheckboxes)
        {
            kvp.Value.IsChecked = false;
        }

        var addUpdateBtn = this.FindControl<Button>("AddUpdateButton");
        if (addUpdateBtn != null)
            addUpdateBtn.Content = "Add Alert";
    }

    private void ShowError(string message)
    {
        var errorWindow = new Window
        {
            Title = "Error",
            Width = 350,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(15),
                Spacing = 15,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Foreground = Avalonia.Media.Brushes.White
                    },
                    new Button
                    {
                        Content = "OK",
                        Width = 80,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                    }
                }
            }
        };

        var okButton = (Button)((StackPanel)errorWindow.Content!).Children[1];
        okButton.Click += (s, e) => errorWindow.Close();

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null)
        {
            errorWindow.ShowDialog(owner);
        }
        else
        {
            errorWindow.Show();
        }
    }
}

public class EnabledStatusConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is bool enabled && enabled ? "enabled" : "disabled";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
