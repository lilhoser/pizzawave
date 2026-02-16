using System.Collections.ObjectModel;
using Avalonia.Threading;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using pizzalib;
using Avalonia.Data;
using Avalonia.Data.Converters;
using System.Globalization;
using Avalonia.Media;
using NAudio.Wave;
using NAudio.MediaFoundation;
using Avalonia.Platform.Storage;

namespace pizzapi;

public class BoolToDoubleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool visible && visible)
        {
            return 1.0;
        }
        return 0.0;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d && d > 0.5)
        {
            return true;
        }
        return false;
    }
}

public class BoolToGridLengthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool visible && visible)
        {
            return new GridLength(200, GridUnitType.Pixel);
        }
        return new GridLength(0, GridUnitType.Pixel);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is GridLength length && length.IsAbsolute && length.Value > 0)
        {
            return true;
        }
        return false;
    }
}

public class BoolToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVisible)
        {
            if (parameter is string @params && @params.Contains('|'))
            {
                var split = @params.Split('|');
                return isVisible ? split[0] : split[1];
            }
            return isVisible ? "True" : "False";
        }
        return "False";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string str)
        {
            return str.Equals("True", StringComparison.OrdinalIgnoreCase) || str.Equals("1", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}

public class BoolToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isVisible && parameter is string @params && @params.Contains('|'))
        {
            var split = @params.Split('|');
            return isVisible ? split[0] : split[1];
        }
        return parameter is string p ? p.Split('|')[1] : "#4CAF50"; // Default to green
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Not used for color conversion
        return false;
    }
}

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private LiveCallManager? _callManager;
    private int _callsReceivedCount;
    private int _alertsTriggeredCount;
    private string _serverStatusText = "Server: Not started";
    private string _connectionStatus = "Connection: Disconnected";
    private string _infoText = "Waiting for calls...";
    private Settings _settings = new Settings();

    // Audio playback
    private WaveOutEvent? _waveOut;
    private object? _audioReader;

    // Menu visibility
    private bool _menuVisible;
    public bool MenuVisible
    {
        get { return _menuVisible; }
        set { _menuVisible = value; RaisePropertyChanged(); }
    }

    public ObservableCollection<TranscribedCall> Calls { get; } = new ObservableCollection<TranscribedCall>();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        StatusText = "Initializing..";

        // Settings are already loaded in constructor, just validate/load defaults
        try
        {
            _settings = Settings.LoadFromFile();
        }
        catch
        {
            // Use default settings
        }

        _callManager = new LiveCallManager(OnNewCall);

        try
        {
            await _callManager.Initialize(_settings);
            StatusText = "Ready to receive calls";
            _serverStatusText = "Server: Running on port " + _settings.ListenPort;
            ServerInfo = $"Port {_settings.ListenPort}";
            _connectionStatus = "Connection: Ready";

            // Start listening on port 9123
            _ = _callManager.Start();
            _infoText = "PizzaPi is listening on port " + _settings.ListenPort + " for trunk-recorder calls";
            RaisePropertyChanged(nameof(ServerStatusText));
            RaisePropertyChanged(nameof(ConnectionStatus));
            RaisePropertyChanged(nameof(ServerInfo));
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _serverStatusText = "Server: Error - " + ex.Message;
            ServerInfo = "Error: " + ex.Message;
            RaisePropertyChanged(nameof(ServerStatusText));
            RaisePropertyChanged(nameof(ServerInfo));
        }
    }

    private void OnNewCall(TranscribedCall call)
    {
        // Marshal to UI thread for UI updates
        Dispatcher.UIThread.Post(() =>
        {
            // Populate friendly fields
            call.FriendlyTalkgroup = TalkgroupHelper.FormatTalkgroup(_settings, call.Talkgroup);
            call.FriendlyFrequency = FormatFrequency(call.Frequency);

            // Set up the play command for this call
            call.PlayAudioCommand = (c) => PlayAudio(c);

            StatusText = $"New call: {call.FriendlyTalkgroup} @ {call.FriendlyFrequency}";
            CallCountText = $"Calls: {_callsReceivedCount + 1}";
            Calls.Insert(0, call); // Add to top of list
            _callsReceivedCount++;
            RaisePropertyChanged(nameof(CallsReceivedCount));
            RaisePropertyChanged(nameof(CallCountText));
        });
    }

    private string FormatFrequency(double frequency)
    {
        // Convert Hz to MHz for display
        double mhz = frequency / 1_000_000.0;
        return $"{mhz:F3}mhz";
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private string _statusText = "Initializing...";
    public string StatusText
    {
        get { return _statusText; }
        set { _statusText = value; RaisePropertyChanged(); }
    }

    private string _callCountText = "Calls: 0";
    public string CallCountText
    {
        get { return _callCountText; }
        set { _callCountText = value; RaisePropertyChanged(); }
    }

    private string _serverInfo = "Not running";
    public string ServerInfo
    {
        get { return _serverInfo; }
        set { _serverInfo = value; RaisePropertyChanged(); }
    }

    public int CallsReceivedCount
    {
        get { return _callsReceivedCount; }
        set { _callsReceivedCount = value; RaisePropertyChanged(); }
    }

    public int AlertsTriggeredCount
    {
        get { return _alertsTriggeredCount; }
        set { _alertsTriggeredCount = value; RaisePropertyChanged(); }
    }

    public string ServerStatusText
    {
        get { return _serverStatusText; }
        set { _serverStatusText = value; RaisePropertyChanged(); }
    }

    public string ConnectionStatus
    {
        get { return _connectionStatus; }
        set { _connectionStatus = value; RaisePropertyChanged(); }
    }

    public void Refresh()
    {
        // Refresh logic would go here
        StatusText = "Refreshing...";
    }

    private void HideMenu()
    {
        MenuVisible = false;
    }

    private void OnMenuButtonClicked(object? sender, RoutedEventArgs e)
    {
        MenuVisible = !MenuVisible;
    }

    private async void OnImportTalkgroupsClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider == null)
        {
            StatusText = "Error: Could not access file picker";
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
            ImportTalkgroupsFromCsv(path);
        }
    }

    private void ImportTalkgroupsFromCsv(string csvPath)
    {
        try
        {
            // Load CSV file and update settings
            _settings.Talkgroups = TalkgroupHelper.GetTalkgroupsFromCsv(csvPath);
            var count = _settings.Talkgroups.Count;

            // Save settings
            _settings.SaveToFile();

            StatusText = $"Imported {count} talkgroups from {Path.GetFileName(csvPath)}";
            HideMenu();
        }
        catch (Exception ex)
        {
            StatusText = $"Error importing talkgroups: {ex.Message}";
        }
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        // Create and show settings window
        var settingsWindow = new SettingsWindow(_settings);
        settingsWindow.Show(this);
        HideMenu();
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        Refresh();
        HideMenu();
    }

    private void OnCloseMenuClicked(object? sender, RoutedEventArgs e)
    {
        MenuVisible = false;
    }

    private void PlayAudio(TranscribedCall newCall)
    {
        // Stop any currently playing audio
        StopAudio();

        var audioFilePath = newCall.Location;
        StatusText = $"Audio path: {audioFilePath}";

        if (string.IsNullOrEmpty(audioFilePath))
        {
            StatusText = "Error: Audio location is empty";
            return;
        }

        if (!File.Exists(audioFilePath))
        {
            StatusText = $"Error: Audio file not found: {audioFilePath}";
            return;
        }

        try
        {
            // First, reset IsAudioPlaying for all other calls
            foreach (var c in Calls)
            {
                c.IsAudioPlaying = false;
            }

            // Use MediaFoundationReader which handles MP3, AAC, etc.
            _audioReader = new MediaFoundationReader(audioFilePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init((IWaveProvider)_audioReader);
            _waveOut.Play();

            StatusText = $"Playing: {newCall.FriendlyTalkgroup} @ {newCall.FriendlyFrequency}";

            // Set_IsAudioPlaying to true for the new call
            Dispatcher.UIThread.Post(() =>
            {
                newCall.IsAudioPlaying = true;
                foreach (var c in Calls)
                {
                    if (c.UniqueId == newCall.UniqueId)
                    {
                        c.IsAudioPlaying = true;
                        break;
                    }
                }
            });

            // When playback completes, clean up
            _waveOut.PlaybackStopped += (sender, e) =>
            {
                StopAudio();
                Dispatcher.UIThread.Post(() =>
                {
                    newCall.IsAudioPlaying = false;
                    StatusText = "Playback complete";
                });
            };
        }
        catch (Exception ex)
        {
            StatusText = $"Error playing audio: {ex.Message}";
        }
    }

    private void StopAudio()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        (_audioReader as IDisposable)?.Dispose();
        _audioReader = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Clean up audio resources when window is closed
        StopAudio();
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCallRowClicked(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Border border && border.DataContext is TranscribedCall call)
        {
            if (call.IsAudioPlaying)
            {
                // Stop playback
                StopAudio();
                call.IsAudioPlaying = false;
                StatusText = "Playback stopped";
            }
            else
            {
                // Start playing this call
                PlayAudio(call);
            }
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
