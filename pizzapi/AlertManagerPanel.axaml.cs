using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using pizzalib;

namespace pizzapi;

public partial class AlertManagerPanel : UserControl
{
    private Settings _settings;
    private string _settingsPath = Settings.DefaultSettingsFileLocation;
    private Alert? _editingAlert;
    private ObservableCollection<Talkgroup> _talkgroups;
    private ObservableCollection<Alert> _alertsCollection;
    private HashSet<long> _selectedTalkgroupIds;
    private Dictionary<long, CheckBox> _talkgroupCheckboxes;

    public event EventHandler? RequestClose;

    public AlertManagerPanel() : this(new Settings())
    {
    }

    public AlertManagerPanel(Settings settings)
    {
        _settings = settings;
        _settingsPath = Settings.DefaultSettingsFileLocation;
        _talkgroups = new ObservableCollection<Talkgroup>(_settings.Talkgroups ?? new List<Talkgroup>());
        _alertsCollection = new ObservableCollection<Alert>(_settings.Alerts ?? new List<Alert>());
        _selectedTalkgroupIds = new HashSet<long>();
        _talkgroupCheckboxes = new Dictionary<long, CheckBox>();

        InitializeComponent();
        LoadAlertsList();
        SetupEventHandlers();
        LoadTalkgroupCheckboxes();
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

        var freqCombo = this.FindControl<ComboBox>("AlertFrequencyComboBox");
        if (freqCombo != null)
            freqCombo.SelectedIndex = 0;
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

        _talkgroups = new ObservableCollection<Talkgroup>(_settings.Talkgroups ?? new List<Talkgroup>());
        _alertsCollection = new ObservableCollection<Alert>(_settings.Alerts ?? new List<Alert>());
        _selectedTalkgroupIds.Clear();
        _editingAlert = null;

        LoadAlertsList();
        LoadTalkgroupCheckboxes();
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

    private Alert GetAlertFromForm()
    {
        var alertName = this.FindControl<TextBox>("AlertNameTextBox")?.Text ?? string.Empty;
        var alertEmail = this.FindControl<TextBox>("AlertEmailTextBox")?.Text ?? string.Empty;
        var alertKeywords = this.FindControl<TextBox>("AlertKeywordsTextBox")?.Text ?? string.Empty;
        var alertFrequency = this.FindControl<ComboBox>("AlertFrequencyComboBox")?.SelectedIndex ?? 0;
        var enabled = this.FindControl<CheckBox>("EnabledCheckBox")?.IsChecked ?? true;
        var autoplay = this.FindControl<CheckBox>("AutoplayCheckBox")?.IsChecked ?? true;
        var allRadio = this.FindControl<RadioButton>("AllTalkgroupsRadio");

        var alert = new Alert
        {
            Name = alertName,
            Email = alertEmail,
            Keywords = alertKeywords,
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
        if (alertFrequency != null) alertFrequency.SelectedIndex = (int)alert.Frequency;
        if (enabled != null) enabled.IsChecked = alert.Enabled;
        if (autoplay != null) autoplay.IsChecked = alert.Autoplay;

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
        if (alertFrequency != null) alertFrequency.SelectedIndex = 0;
        if (enabled != null) enabled.IsChecked = true;
        if (autoplay != null) autoplay.IsChecked = true;

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
