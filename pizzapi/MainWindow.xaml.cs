using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NAudio.Wave;
using pizzalib;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace pizzapi;

// Wrapper class for grouped call display
public class CallGroupItem
{
    public bool IsHeader { get; set; }
    public string? HeaderText { get; set; }
    public TranscribedCall? Call { get; set; }
    public bool ShowTalkgroup { get; set; } = true;
    public bool IsExpanded { get; set; } = true; // For collapsible groups
}

public class CallGroupItemTemplateSelector : IDataTemplate
{
    public IDataTemplate? HeaderTemplate { get; set; }
    public IDataTemplate? CallTemplate { get; set; }
    
    public bool Match(object? data) => true;
    
    public Control? Build(object? param)
    {
        if (param is CallGroupItem item)
        {
            if (item.IsHeader && HeaderTemplate != null)
            {
                return HeaderTemplate.Build(item);
            }
            else if (!item.IsHeader && CallTemplate != null)
            {
                return CallTemplate.Build(item);
            }
        }
        return null;
    }
}

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

public class InvertedBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return !b;
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
            var colorHex = isVisible ? split[0] : split[1];
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));
        }
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4CAF50"));
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
    private OfflineCallManager? _offlineCallManager;
    private bool _isOfflineMode;
    private int _callsReceivedCount;
    private int _alertsTriggeredCount;
    private string _serverStatusText = "Server: Not started";
    private string _connectionStatus = "Connection: Disconnected";
    private string _infoText = "Waiting for calls...";
    private Settings _settings = new Settings();
    private string _versionString;
    private double _fontSize = 14;

    // Audio playback
    private WaveOutEvent? _waveOut;
    private object? _audioReader;
    private Process? _currentAudioProcess;     // for Linux ffplay
    private readonly bool _isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // Alert autoplay and snooze
    private DateTime? _snoozeUntil = null;  // Explicitly initialize to null
    private bool _alwaysPinAlertMatches = true;
    private int _currentSortMode = 0; // 0=newest first, 1=oldest first, 2=talkgroup
    private int _currentGroupMode = 0; // 0=none, 1=talkgroup, 2=time of day, 3=source
    private Button? _alwaysPinAlertsButton;
    private Popup? _viewSubMenuPopup;
    private Border? _sortSubMenuBorder;
    private Border? _groupBySubMenuBorder;
    // Sort menu buttons for updating checkmarks
    private Button? _sortNewestButton;
    private Button? _sortOldestButton;
    private Button? _sortTalkgroupButton;
    // Group menu buttons for updating checkmarks
    private Button? _groupNoneButton;
    private Button? _groupTalkgroupButton;
    private Button? _groupTimeOfDayButton;
    private Button? _groupSourceButton;
    // Track expanded group headers
    private HashSet<string> _collapsedGroups = new HashSet<string>();
    // Flag to prevent re-entrancy in collapse/expand
    private bool _isCollapsingExpanding = false;

    public bool IsAlertSnoozed => _snoozeUntil.HasValue && DateTime.Now < _snoozeUntil.Value;

    public bool IsOfflineMode => _isOfflineMode;

    public string ModeIndicatorText => _isOfflineMode ? "OFFLINE" : "LIVE";

    public string ModeIndicatorColor => _isOfflineMode ? "#ffaa00" : "#00ff00";

    public string BellToolTipText
    {
        get
        {
            // Check if snooze has expired
            if (_snoozeUntil.HasValue && DateTime.Now >= _snoozeUntil.Value)
            {
                _snoozeUntil = null;
            }
            
            if (!_settings.AutoplayAlerts)
                return "Alert audio: Disabled (click to enable)";
            if (IsAlertSnoozed)
                return $"Alert audio: Snoozed until {_snoozeUntil.Value:t} (click to change)";
            return $"Alert audio: Enabled (click to snooze)";
        }
    }

    public Avalonia.Media.SolidColorBrush BellColor
    {
        get
        {
            // Check if snooze has expired
            if (_snoozeUntil.HasValue && DateTime.Now >= _snoozeUntil.Value)
            {
                _snoozeUntil = null;
                RaisePropertyChanged(nameof(IsAlertSnoozed));
                RaisePropertyChanged(nameof(BellToolTipText));
            }

            // Determine color based on current state
            // Gray when disabled, orange when snoozed, green when enabled
            bool alertsEnabled = _settings?.AutoplayAlerts ?? false;
            bool isSnoozed = _snoozeUntil.HasValue;
            
            var colorHex = !alertsEnabled
                ? "#666666"  // Gray when disabled
                : isSnoozed
                    ? "#ffaa00"  // Orange when snoozed
                    : "#00ff00"; // Green when enabled

            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));
        }
    }

    // Menu visibility
    private bool _menuVisible;
    public bool MenuVisible
    {
        get { return _menuVisible; }
        set { _menuVisible = value; RaisePropertyChanged(); }
    }

    public new double FontSize
    {
        get { return _fontSize; }
        set
        {
            _fontSize = value;
            RaisePropertyChanged();
            // Update the application resource so bindings can pick it up
            if (Application.Current != null)
                Application.Current.Resources["CurrentFontSize"] = value;
        }
    }

    public string VersionString
    {
        get { return _versionString; }
        set { _versionString = value; RaisePropertyChanged(); }
    }

    public string UsageText
    {
        get { return GetCaptureFolderSize(); }
    }

    public ObservableCollection<TranscribedCall> Calls { get; } = new ObservableCollection<TranscribedCall>();
    public ObservableCollection<CallGroupItem> GroupedCalls { get; } = new ObservableCollection<CallGroupItem>();
    private bool _useGrouping => _currentGroupMode > 0;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        // Get reference to menu items
        _alwaysPinAlertsButton = this.FindControl<Button>("AlwaysPinAlertsButton");
        _viewSubMenuPopup = this.FindControl<Popup>("ViewSubMenuPopup");
        _sortSubMenuBorder = this.FindControl<Border>("SortSubMenuBorder");
        _groupBySubMenuBorder = this.FindControl<Border>("GroupBySubMenuBorder");
        _sortNewestButton = this.FindControl<Button>("SortNewestButton");
        _sortOldestButton = this.FindControl<Button>("SortOldestButton");
        _sortTalkgroupButton = this.FindControl<Button>("SortTalkgroupButton");
        _groupNoneButton = this.FindControl<Button>("GroupNoneButton");
        _groupTalkgroupButton = this.FindControl<Button>("GroupTalkgroupButton");
        _groupTimeOfDayButton = this.FindControl<Button>("GroupTimeOfDayButton");
        _groupSourceButton = this.FindControl<Button>("GroupSourceButton");

        // Get version from git describe (works for both CI and local builds)
        _versionString = GetVersionFromGit();
        RaisePropertyChanged(nameof(VersionString));

        // Initialize font size resource
        if (Application.Current != null)
            Application.Current.Resources["CurrentFontSize"] = FontSize;

        InitializeAsync();
    }

    private void OnViewButtonClicked(object? sender, RoutedEventArgs e)
    {
        // Close submenus when opening View menu
        HideSubMenus();
        
        if (_viewSubMenuPopup != null)
        {
            _viewSubMenuPopup.IsOpen = !_viewSubMenuPopup.IsOpen;
        }
    }

    private void HideSubMenus()
    {
        if (_sortSubMenuBorder != null) _sortSubMenuBorder.IsVisible = false;
        if (_groupBySubMenuBorder != null) _groupBySubMenuBorder.IsVisible = false;
    }

    private void OnSortButtonClicked(object? sender, RoutedEventArgs e)
    {
        // Hide Group By submenu, show Sort submenu
        if (_groupBySubMenuBorder != null) _groupBySubMenuBorder.IsVisible = false;
        if (_sortSubMenuBorder != null) _sortSubMenuBorder.IsVisible = !_sortSubMenuBorder.IsVisible;
    }

    private void OnGroupByButtonClicked(object? sender, RoutedEventArgs e)
    {
        // Hide Sort submenu, show Group By submenu
        if (_sortSubMenuBorder != null) _sortSubMenuBorder.IsVisible = false;
        if (_groupBySubMenuBorder != null) _groupBySubMenuBorder.IsVisible = !_groupBySubMenuBorder.IsVisible;
    }

    private void CloseSortSubMenu()
    {
        if (_sortSubMenuBorder != null) _sortSubMenuBorder.IsVisible = false;
    }

    private void CloseGroupBySubMenu()
    {
        if (_groupBySubMenuBorder != null) _groupBySubMenuBorder.IsVisible = false;
    }

    private void UpdateSortCheckmarks()
    {
        if (_sortNewestButton != null)
            _sortNewestButton.Content = _currentSortMode == 0 ? "✓ Time (newest first)" : "  Time (newest first)";
        if (_sortOldestButton != null)
            _sortOldestButton.Content = _currentSortMode == 1 ? "✓ Time (oldest first)" : "  Time (oldest first)";
        if (_sortTalkgroupButton != null)
            _sortTalkgroupButton.Content = _currentSortMode == 2 ? "✓ Talkgroup (A-Z)" : "  Talkgroup (A-Z)";
    }

    private void UpdateGroupCheckmarks()
    {
        if (_groupNoneButton != null)
            _groupNoneButton.Content = _currentGroupMode == 0 ? "✓ None (flat list)" : "  None (flat list)";
        if (_groupTalkgroupButton != null)
            _groupTalkgroupButton.Content = _currentGroupMode == 1 ? "✓ Talkgroup" : "  Talkgroup";
        if (_groupTimeOfDayButton != null)
            _groupTimeOfDayButton.Content = _currentGroupMode == 2 ? "✓ Time of Day" : "  Time of Day";
        if (_groupSourceButton != null)
            _groupSourceButton.Content = _currentGroupMode == 3 ? "✓ Source" : "  Source";
    }

    private void OnSortByTimeNewestClicked(object? sender, RoutedEventArgs e)
    {
        _currentSortMode = 0;
        _settings.SortMode = _currentSortMode;
        _settings.SaveToFile();
        UpdateSortCheckmarks();
        SortCalls();
        CloseSortSubMenu();
        HideMenu();
    }

    private void OnSortByTimeOldestClicked(object? sender, RoutedEventArgs e)
    {
        _currentSortMode = 1;
        _settings.SortMode = _currentSortMode;
        _settings.SaveToFile();
        UpdateSortCheckmarks();
        SortCalls();
        CloseSortSubMenu();
        HideMenu();
    }

    private void OnSortByTalkgroupClicked(object? sender, RoutedEventArgs e)
    {
        _currentSortMode = 2;
        _settings.SortMode = _currentSortMode;
        _settings.SaveToFile();
        UpdateSortCheckmarks();
        SortCalls();
        CloseSortSubMenu();
        HideMenu();
    }

    private void OnAlwaysPinAlertsClicked(object? sender, RoutedEventArgs e)
    {
        _alwaysPinAlertMatches = !_alwaysPinAlertMatches;
        if (_alwaysPinAlertsButton != null)
        {
            _alwaysPinAlertsButton.Content = _alwaysPinAlertMatches ? "✓ Always pin alert matches" : "  Always pin alert matches";
        }
        // Don't close menu for toggle - user might want to change other settings
    }

    private void OnGroupByNoneClicked(object? sender, RoutedEventArgs e)
    {
        _currentGroupMode = 0;
        _settings.GroupMode = _currentGroupMode;
        _settings.SaveToFile();
        UpdateGroupCheckmarks();
        ApplyGrouping();
        CloseGroupBySubMenu();
        HideMenu();
    }

    private void OnGroupByTalkgroupClicked(object? sender, RoutedEventArgs e)
    {
        _currentGroupMode = 1;
        _settings.GroupMode = _currentGroupMode;
        _settings.SaveToFile();
        UpdateGroupCheckmarks();
        ApplyGrouping();
        CloseGroupBySubMenu();
        HideMenu();
    }

    private void OnGroupByTimeOfDayClicked(object? sender, RoutedEventArgs e)
    {
        _currentGroupMode = 2;
        _settings.GroupMode = _currentGroupMode;
        _settings.SaveToFile();
        UpdateGroupCheckmarks();
        ApplyGrouping();
        CloseGroupBySubMenu();
        HideMenu();
    }

    private void OnGroupBySourceClicked(object? sender, RoutedEventArgs e)
    {
        _currentGroupMode = 3;
        _settings.GroupMode = _currentGroupMode;
        _settings.SaveToFile();
        UpdateGroupCheckmarks();
        ApplyGrouping();
        CloseGroupBySubMenu();
        HideMenu();
    }

    private void OnGroupHeaderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.DataContext is CallGroupItem header && header.HeaderText != null)
        {
            if (_collapsedGroups.Contains(header.HeaderText))
                _collapsedGroups.Remove(header.HeaderText);   // expand
            else
                _collapsedGroups.Add(header.HeaderText);      // collapse

            ApplyGrouping();
            e.Handled = true;
        }
    }

    private void OnCollapseExpandAllClicked(object? sender, RoutedEventArgs e)
    {
        if (_isCollapsingExpanding) return;
        _isCollapsingExpanding = true;

        try
        {
            // Close popups first...
            if (_viewSubMenuPopup != null) _viewSubMenuPopup.IsOpen = false;
            HideSubMenus();
            HideMenu();

            bool shouldExpandAll = _collapsedGroups.Count > 0;   // if anything is collapsed → expand all

            if (shouldExpandAll)
                _collapsedGroups.Clear();           // expand everything
            else
            {
                // collapse everything
                _collapsedGroups.Clear();
                foreach (var item in GroupedCalls.Where(i => i.IsHeader && i.HeaderText != null))
                    _collapsedGroups.Add(item.HeaderText!);
            }

            ApplyGrouping();
            StatusText = shouldExpandAll ? "All groups expanded" : "All groups collapsed";
        }
        finally
        {
            _isCollapsingExpanding = false;
        }
    }

    private void ApplyGrouping()
    {
        GroupedCalls.Clear();

        // First, sort the Calls collection based on current sort mode
        // Always put manually pinned calls at top
        List<TranscribedCall> sortedCalls;

        // Check if any calls are manually pinned
        bool hasPinnedCalls = Calls.Any(c => c.IsPinned);

        if (hasPinnedCalls)
        {
            // Pinned calls at top (sorted by selected sort mode), then non-pinned (sorted by selected sort mode)
            var pinnedCalls = Calls.Where(c => c.IsPinned).ToList();
            var nonPinnedCalls = Calls.Where(c => !c.IsPinned).ToList();

            sortedCalls = _currentSortMode switch
            {
                0 => pinnedCalls.OrderByDescending(c => c.StartTime)
                               .Concat(nonPinnedCalls.OrderByDescending(c => c.StartTime)).ToList(),
                1 => pinnedCalls.OrderBy(c => c.StartTime)
                               .Concat(nonPinnedCalls.OrderBy(c => c.StartTime)).ToList(),
                2 => pinnedCalls.OrderBy(c => c.FriendlyTalkgroup).ThenByDescending(c => c.StartTime)
                               .Concat(nonPinnedCalls.OrderBy(c => c.FriendlyTalkgroup).ThenByDescending(c => c.StartTime)).ToList(),
                _ => pinnedCalls.OrderByDescending(c => c.StartTime)
                               .Concat(nonPinnedCalls.OrderByDescending(c => c.StartTime)).ToList()
            };
        }
        else
        {
            // No pinned calls - just sort by selected mode
            sortedCalls = _currentSortMode switch
            {
                0 => Calls.OrderByDescending(c => c.StartTime).ToList(),
                1 => Calls.OrderBy(c => c.StartTime).ToList(),
                2 => Calls.OrderBy(c => c.FriendlyTalkgroup).ThenByDescending(c => c.StartTime).ToList(),
                _ => Calls.OrderByDescending(c => c.StartTime).ToList()
            };
        }

        if (_currentGroupMode == 0)
        {
            // No grouping - just add all sorted calls
            foreach (var call in sortedCalls)
            {
                GroupedCalls.Add(new CallGroupItem { IsHeader = false, Call = call, ShowTalkgroup = true });
            }
            StatusText = "Grouping: None (flat list)";
        }
        else
        {
            // Group calls based on mode
            var grouped = _currentGroupMode switch
            {
                1 => GroupByTalkgroup(sortedCalls),
                2 => GroupByTimeOfDay(sortedCalls),
                3 => GroupBySource(sortedCalls),
                _ => sortedCalls.Select(c => new CallGroupItem { IsHeader = false, Call = c, ShowTalkgroup = true }).ToList()
            };

            foreach (var item in grouped)
            {
                GroupedCalls.Add(item);
            }

            StatusText = _currentGroupMode switch
            {
                1 => "Grouping: Talkgroup",
                2 => "Grouping: Time of Day",
                3 => "Grouping: Source",
                _ => "Grouping: None"
            };
        }
        
        // Force UI refresh
        RaisePropertyChanged(nameof(GroupedCalls));
    }

    private List<CallGroupItem> GroupByTalkgroup(List<TranscribedCall> calls)
    {
        var result = new List<CallGroupItem>();
        var grouped = calls.GroupBy(c => c.FriendlyTalkgroup)
                          .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            bool isExpanded = !_collapsedGroups.Contains(group.Key ?? "");
            result.Add(new CallGroupItem { IsHeader = true, HeaderText = group.Key, IsExpanded = isExpanded });
            if (isExpanded)
            {
                foreach (var call in group)
                {
                    result.Add(new CallGroupItem { IsHeader = false, Call = call, ShowTalkgroup = false });
                }
            }
        }
        return result;
    }

    private List<CallGroupItem> GroupByTimeOfDay(List<TranscribedCall> calls)
    {
        var result = new List<CallGroupItem>();

        string GetTimeOfDay(long timestamp)
        {
            var hour = DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime.Hour;
            if (hour >= 5 && hour < 12) return "Morning (5AM-12PM)";
            if (hour >= 12 && hour < 17) return "Afternoon (12PM-5PM)";
            if (hour >= 17 && hour < 22) return "Evening (5PM-10PM)";
            return "Night (10PM-5AM)";
        }

        var grouped = calls.GroupBy(c => GetTimeOfDay(c.StartTime))
                          .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            bool isExpanded = !_collapsedGroups.Contains(group.Key ?? "");
            result.Add(new CallGroupItem { IsHeader = true, HeaderText = group.Key, IsExpanded = isExpanded });
            if (isExpanded)
            {
                foreach (var call in group)
                {
                    result.Add(new CallGroupItem { IsHeader = false, Call = call, ShowTalkgroup = true });
                }
            }
        }
        return result;
    }

    private List<CallGroupItem> GroupBySource(List<TranscribedCall> calls)
    {
        var result = new List<CallGroupItem>();
        var grouped = calls.GroupBy(c => c.SystemShortName ?? "Unknown")
                          .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            bool isExpanded = !_collapsedGroups.Contains(group.Key ?? "");
            result.Add(new CallGroupItem { IsHeader = true, HeaderText = group.Key, IsExpanded = isExpanded });
            if (isExpanded)
            {
                foreach (var call in group)
                {
                    result.Add(new CallGroupItem { IsHeader = false, Call = call, ShowTalkgroup = true });
                }
            }
        }
        return result;
    }

    private string GetVersionFromGit()
    {
        try
        {
            var workingDir = AppDomain.CurrentDomain.BaseDirectory;

            // Quick check - if there's no .git folder, don't even try
            if (!Directory.Exists(Path.Combine(workingDir, ".git")))
            {
                throw new Exception("Not a git repository");
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "describe --tags --long",
                RedirectStandardOutput = true,
                RedirectStandardError = true,     // ← IMPORTANT: suppress the fatal message
                UseShellExecute = false,
                WorkingDirectory = workingDir,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                throw new Exception("Failed to start git");

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();  // discard any error output

            process.WaitForExit();

            if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
            {
                // Parse git describe output: v1.0.6-2-g98ce57b
                var match = System.Text.RegularExpressions.Regex.Match(output, @"v?(\d+\.\d+\.\d+)(?:-(\d+)-g([a-f0-9]+))?");
                if (match.Success)
                {
                    var version = match.Groups[1].Value;
                    var commits = match.Groups[2].Value;
                    var hash = match.Groups[3].Value;

                    if (!string.IsNullOrEmpty(commits) && commits != "0")
                        return $"PizzaPi v{version}+{commits} commits ({hash})";

                    return $"PizzaPi v{version}";
                }
            }
        }
        catch
        {
            // Git not available, not a git repo, or any other failure → silent fallback
        }

        // Fallback to assembly version (this is what you see in the footer)
        var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return $"PizzaPi v{assemblyVersion?.ToString(3) ?? "1.0.0"}";
    }

    private async void InitializeAsync()
    {
        StatusText = "Initializing..";

        // Initialize trace logging to file
        TraceLogger.Initialize(RedirectToStdout: false);
        pizzalib.TraceLogger.Initialize(RedirectToStdout: false);

        // Settings are already loaded in constructor, just validate/load defaults
        try
        {
            _settings = Settings.LoadFromFile();
        }
        catch
        {
            // Use default settings
            _settings = new Settings();
        }

        // Set trace level from settings
        TraceLogger.SetLevel(_settings.TraceLevelApp);
        pizzalib.TraceLogger.SetLevel(_settings.TraceLevelApp);

        // Apply saved sort and group settings
        _currentSortMode = _settings.SortMode;
        _currentGroupMode = _settings.GroupMode;
        UpdateSortCheckmarks();
        UpdateGroupCheckmarks();

        // Force refresh bell icon state after settings are loaded
        RaisePropertyChanged(nameof(IsAlertSnoozed));
        RaisePropertyChanged(nameof(BellColor));
        RaisePropertyChanged(nameof(BellToolTipText));

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

            // Insert at correct position: pinned calls at top, then by recency
            int insertIndex = GetInsertIndex(call);
            Calls.Insert(insertIndex, call);

            // Autoplay audio for alert matches (if enabled globally, not snoozed, and alert has autoplay enabled)
            if (call.IsAlertMatch && call.ShouldAutoplay && _settings.AutoplayAlerts && !IsAlertSnoozed)
            {
                PlayAudio(call);
            }

            _callsReceivedCount++;
            RaisePropertyChanged(nameof(CallsReceivedCount));
            RaisePropertyChanged(nameof(CallCountText));

            // Increment alert counter if this call matched an alert
            if (call.IsAlertMatch)
            {
                _alertsTriggeredCount++;
                RaisePropertyChanged(nameof(AlertsTriggeredCount));
            }

            // If always pin alert matches is enabled, ensure the call stays pinned
            if (call.IsAlertMatch && _alwaysPinAlertMatches)
            {
                call.IsPinned = true;
                RepositionCall(call);
            }

            // Apply grouping if enabled
            if (_useGrouping)
            {
                ApplyGrouping();
            }
            else
            {
                // When not grouping, also update GroupedCalls to mirror Calls
                GroupedCalls.Clear();
                foreach (var c in Calls)
                {
                    GroupedCalls.Add(new CallGroupItem { IsHeader = false, Call = c, ShowTalkgroup = true });
                }
            }
        });
    }

    /// <summary>
    /// Returns the index where a call should be inserted to keep pinned items at top.
    /// Pinned calls go to the top (in order of arrival), non-pinned calls go after all pinned.
    /// </summary>
    private int GetInsertIndex(TranscribedCall call)
    {
        if (call.IsPinned)
        {
            // Pinned: insert at the top, before all non-pinned items
            for (int i = 0; i < Calls.Count; i++)
            {
                if (!Calls[i].IsPinned)
                    return i;
            }
            return Calls.Count; // All are pinned, add at end of pinned section
        }
        else
        {
            // Non-pinned: insert after all pinned items (at end of list)
            return Calls.Count;
        }
    }

    /// <summary>
    /// Repositions a call in the list based on its pinned state.
    /// Called when a call's IsPinned property changes.
    /// </summary>
    private void RepositionCall(TranscribedCall call)
    {
        int currentIndex = Calls.IndexOf(call);
        if (currentIndex < 0) return;

        // Calculate new index based on pinned state
        // Note: GetInsertIndex uses the current state of Calls (which still includes this call)
        int newIndex = GetInsertIndex(call);
        
        // Adjust newIndex to account for the removal that will happen
        if (newIndex > currentIndex)
            newIndex--; // Removal shifts everything after currentIndex down by 1
        
        // Clamp to valid range
        newIndex = Math.Max(0, Math.Min(newIndex, Calls.Count - 1));

        // Only move if position changed
        if (currentIndex != newIndex)
        {
            Calls.RemoveAt(currentIndex);
            Calls.Insert(newIndex, call);
        }
    }

    private string FormatFrequency(double frequency)
    {
        // Convert Hz to MHz for display
        double mhz = frequency / 1_000_000.0;
        return $"{mhz:F3}mhz";
    }

    private string GetCaptureFolderSize()
    {
        try
        {
            var capturePath = Settings.DefaultLiveCaptureDirectory;
            if (!Directory.Exists(capturePath))
                return "Usage: 0mb";

            var size = GetDirectorySize(capturePath);
            var mb = size / (1024.0 * 1024.0);
            return $"Usage: {mb:F0}mb";
        }
        catch
        {
            return "Usage: ?mb";
        }
    }

    private long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            // Sum file sizes in current directory
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var fileInfo = new FileInfo(file);
                size += fileInfo.Length;
            }
            // Recursively sum subdirectory sizes
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                size += GetDirectorySize(dir);
            }
        }
        catch
        {
            // Ignore access errors
        }
        return size;
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
        HideSubMenus();
        if (_viewSubMenuPopup != null) _viewSubMenuPopup.IsOpen = false;
    }

    private void OnMenuButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (MenuVisible)
        {
            // Menu is open, closing it
            MenuVisible = false;
            HideSubMenus();
            if (_viewSubMenuPopup != null) _viewSubMenuPopup.IsOpen = false;
        }
        else
        {
            // Menu is closed, opening it
            MenuVisible = true;
        }
    }

    private void OnBellClicked(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        ToggleSnoozeMenu();
    }

    private void SnoozeAlerts(int minutes)
    {
        _snoozeUntil = DateTime.Now.AddMinutes(minutes);
        RaisePropertyChanged(nameof(IsAlertSnoozed));
        RaisePropertyChanged(nameof(BellToolTipText));
        RaisePropertyChanged(nameof(BellColor));
        StatusText = $"Snoozed for {minutes} min";
    }

    private void EnableAlertAudio()
    {
        _snoozeUntil = null;
        RaisePropertyChanged(nameof(IsAlertSnoozed));
        RaisePropertyChanged(nameof(BellToolTipText));
        RaisePropertyChanged(nameof(BellColor));
        StatusText = "Enabled";
    }

    private void ToggleSnoozeMenu()
    {
        // Show a simple context menu for snooze/enable options
        var menu = new ContextMenu();

        if (!_settings.AutoplayAlerts)
        {
            // Alert audio is disabled - show enable option
            menu.Items.Add(new MenuItem
            {
                Header = "Enable alert audio",
                Command = new RelayCommand(() =>
                {
                    _settings.AutoplayAlerts = true;
                    _settings.SaveToFile();
                    RaisePropertyChanged(nameof(BellToolTipText));
                    RaisePropertyChanged(nameof(BellColor));
                    StatusText = "Enabled";
                })
            });
        }
        else if (IsAlertSnoozed)
        {
            // Alert audio is snoozed - show enable option
            menu.Items.Add(new MenuItem
            {
                Header = $"Enable alert audio (currently snoozed until {_snoozeUntil.Value:t})",
                Command = new RelayCommand(() => EnableAlertAudio())
            });
            menu.Items.Add(new Separator());
        }
        else
        {
            // Alert audio is enabled - show snooze options
            var defaultDuration = _settings.SnoozeDurationMinutes;
            menu.Items.Add(new MenuItem
            {
                Header = $"Snooze for {defaultDuration} minutes (default)",
                Command = new RelayCommand(() => SnoozeAlerts(defaultDuration))
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Snooze for 5 minutes",
                Command = new RelayCommand(() => SnoozeAlerts(5))
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Snooze for 15 minutes",
                Command = new RelayCommand(() => SnoozeAlerts(15))
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Snooze for 30 minutes",
                Command = new RelayCommand(() => SnoozeAlerts(30))
            });
            menu.Items.Add(new MenuItem
            {
                Header = "Snooze for 1 hour",
                Command = new RelayCommand(() => SnoozeAlerts(60))
            });
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = _settings.AutoplayAlerts ? "Disable alert audio" : "Alert audio is disabled",
            IsEnabled = _settings.AutoplayAlerts,
            Command = new RelayCommand(() =>
            {
                _settings.AutoplayAlerts = false;
                _settings.SaveToFile();
                RaisePropertyChanged(nameof(BellToolTipText));
                RaisePropertyChanged(nameof(BellColor));
                StatusText = "Disabled";
            })
        });

        menu.Open(this);
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

    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        // Create and show settings window
        var settingsWindow = new SettingsWindow(_settings);
        await settingsWindow.ShowDialog(this);
        // Reload settings from file and refresh bell icon state after dialog closes
        _settings = Settings.LoadFromFile();
        RaisePropertyChanged(nameof(BellToolTipText));
        RaisePropertyChanged(nameof(BellColor));
        HideMenu();
    }

    private void OnAlertsClicked(object? sender, RoutedEventArgs e)
    {
        // Create and show alert manager window
        var alertManagerWindow = new AlertManagerWindow(_settings);
        alertManagerWindow.Show(this);
        HideMenu();
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        Calls.Clear();
        GroupedCalls.Clear();
        _collapsedGroups.Clear();
        _callsReceivedCount = 0;
        _alertsTriggeredCount = 0;
        RaisePropertyChanged(nameof(CallsReceivedCount));
        RaisePropertyChanged(nameof(AlertsTriggeredCount));
        StatusText = "Calls cleared";
        HideMenu();
    }

    private async void OnOpenOfflineCaptureClicked(object? sender, RoutedEventArgs e)
    {
        // Show offline mode dialog (don't stop live capture yet)
        var offlineWindow = new OfflineModeWindow(_settings);
        await offlineWindow.ShowDialog(this);

        // Debug: log what we got from the dialog
        System.Diagnostics.Debug.WriteLine($"[MainWindow] Dialog returned: SelectedPath={offlineWindow.SelectedPath}, LoadedCalls={(offlineWindow.LoadedCalls != null ? offlineWindow.LoadedCalls.Count.ToString() : "null")}");

        // Only switch to offline mode if loading was successful
        if (offlineWindow.SelectedPath != null && offlineWindow.LoadedCalls != null && offlineWindow.LoadedCalls.Count > 0)
        {
            // Stop any audio playback first
            StopAudio();

            // User selected a folder and loading succeeded - now stop live capture if running
            if (!_isOfflineMode && _callManager != null && _callManager.IsStarted())
            {
                _callManager.Stop();
                StatusText = "Live capture stopped";
            }

            // Clear live calls before loading offline captures
            Calls.Clear();

            // Switch to offline mode (or load new offline capture if already in offline mode)
            _isOfflineMode = true;

            // Clean up previous offline manager if exists
            if (_offlineCallManager != null)
            {
                _offlineCallManager.Stop();
                _offlineCallManager.Dispose();
            }

            _offlineCallManager = new OfflineCallManager(offlineWindow.SelectedPath, OnNewCall);

            // Load calls into UI with proper sorting (pinned first)
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Loading {offlineWindow.LoadedCalls.Count} calls into UI");
            foreach (var call in offlineWindow.LoadedCalls)
            {
                call.FriendlyTalkgroup = TalkgroupHelper.FormatTalkgroup(_settings, call.Talkgroup);
                call.FriendlyFrequency = FormatFrequency(call.Frequency);
                call.PlayAudioCommand = (c) => PlayAudio(c);

                // Use GetInsertIndex to properly sort pinned calls to top
                int insertIndex = GetInsertIndex(call);
                Calls.Insert(insertIndex, call);
            }
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Calls.Count after load: {Calls.Count}");

            // Apply grouping to update GroupedCalls (the UI binds to GroupedCalls, not Calls)
            if (_useGrouping)
            {
                ApplyGrouping();
            }
            else
            {
                // When not grouping, also update GroupedCalls to mirror Calls
                GroupedCalls.Clear();
                foreach (var c in Calls)
                {
                    GroupedCalls.Add(new CallGroupItem { IsHeader = false, Call = c, ShowTalkgroup = true });
                }
            }
            System.Diagnostics.Debug.WriteLine($"[MainWindow] GroupedCalls.Count after load: {GroupedCalls.Count}");

            // Update call and alert counts to reflect loaded offline captures
            _callsReceivedCount = offlineWindow.LoadedCalls.Count;
            _alertsTriggeredCount = offlineWindow.LoadedCalls.Count(c => c.IsAlertMatch);
            RaisePropertyChanged(nameof(CallsReceivedCount));
            RaisePropertyChanged(nameof(AlertsTriggeredCount));
            CallCountText = $"Calls: {_callsReceivedCount}";

            // Update UI
            RaisePropertyChanged(nameof(ModeIndicatorText));
            RaisePropertyChanged(nameof(ModeIndicatorColor));
            RaisePropertyChanged(nameof(IsOfflineMode));

            // Update menu visibility - keep Open Offline enabled so user can load another
            var returnToLiveBtn = this.FindControl<Button>("ReturnToLiveButton");
            if (returnToLiveBtn != null) returnToLiveBtn.IsVisible = true;

            Title = $"PizzaPi - Offline: {offlineWindow.SelectedPath}";
            StatusText = $"Loaded {offlineWindow.LoadedCalls.Count} offline calls";
        }
        else if (offlineWindow.SelectedPath != null)
        {
            // User selected a folder but loading failed or no calls found
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Offline load failed - no calls loaded");
            StatusText = "Offline load failed - no calls loaded";
        }
        // else: user cancelled - do nothing, stay in current mode

        HideMenu();
    }

    private void OnReturnToLiveModeClicked(object? sender, RoutedEventArgs e)
    {
        if (!_isOfflineMode)
        {
            StatusText = "Already in live mode";
            HideMenu();
            return;
        }

        // Stop offline call manager
        _offlineCallManager?.Stop();
        _offlineCallManager?.Dispose();
        _offlineCallManager = null;

        // Clear calls
        Calls.Clear();

        // Switch back to live mode
        _isOfflineMode = false;

        // Update UI
        RaisePropertyChanged(nameof(ModeIndicatorText));
        RaisePropertyChanged(nameof(ModeIndicatorColor));
        RaisePropertyChanged(nameof(IsOfflineMode));

        // Update menu visibility
        var openOfflineBtn = this.FindControl<Button>("OpenOfflineButton");
        var returnToLiveBtn = this.FindControl<Button>("ReturnToLiveButton");
        if (openOfflineBtn != null) openOfflineBtn.IsEnabled = true;
        if (returnToLiveBtn != null) returnToLiveBtn.IsVisible = false;

        // Restart live call manager
        InitializeAsync();

        Title = "PizzaPi";
        StatusText = "Returned to live mode";

        HideMenu();
    }

    private void OnCleanupClicked(object? sender, RoutedEventArgs e)
    {
        var cleanupWindow = new CleanupWindow();
        cleanupWindow.ShowDialog(this);
        HideMenu();
    }

    private void OnViewLogClicked(object? sender, RoutedEventArgs e)
    {
        // Open the log file in default text editor
        var logPath = Path.Combine(Settings.DefaultWorkingDirectory, "Logs");
        if (Directory.Exists(logPath))
        {
            // Find the most recent log file (both .txt and .log)
            var logFiles = Directory.GetFiles(logPath, "*.*")
                .Where(f => f.EndsWith(".txt") || f.EndsWith(".log"))
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(logFiles) && File.Exists(logFiles))
            {
                // Open in default text editor
                try
                {
                    var proc = new System.Diagnostics.Process
                    {
                        StartInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = logFiles,
                            UseShellExecute = true
                        }
                    };
                    proc.Start();
                    StatusText = $"Opened log: {Path.GetFileName(logFiles)}";
                }
                catch (Exception ex)
                {
                    StatusText = $"Error opening log: {ex.Message}";
                }
            }
            else
            {
                StatusText = "No log files found. Enable verbose logging in settings.";
            }
        }
        else
        {
            StatusText = "Log directory not found";
        }
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
        StopAudio(); // always stop anything currently playing first

        var audioFilePath = newCall.Location;
        if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
        {
            StatusText = "Error: Audio file not found";
            return;
        }

        StatusText = $"Playing: {newCall.FriendlyTalkgroup} @ {newCall.FriendlyFrequency}";

        if (_isLinux)
        {
            PlayAudioLinux(audioFilePath, newCall);
            return;
        }

        // === Windows path ===
        try
        {
            // Reset all playing flags first
            foreach (var c in Calls.ToList())   // .ToList() makes a snapshot so we can safely set properties
            {
                c.IsAudioPlaying = (c.UniqueId == newCall.UniqueId);
            }

            _audioReader = new MediaFoundationReader(audioFilePath);
            _waveOut = new WaveOutEvent();
            _waveOut.Init((IWaveProvider)_audioReader);
            _waveOut.Play();

            // Playback complete handler
            _waveOut.PlaybackStopped += (sender, e) =>
            {
                StopAudio();
                HandlePlaybackComplete(newCall);
            };
        }
        catch (Exception ex)
        {
            StatusText = $"Error playing audio: {ex.Message}";
        }
    }

    private void StopAudio()
    {
        // Linux path
        if (_currentAudioProcess != null)
        {
            try
            {
                if (!_currentAudioProcess.HasExited)
                    _currentAudioProcess.Kill();
            }
            catch { }
            _currentAudioProcess.Dispose();
            _currentAudioProcess = null;
        }

        // Windows path
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;

        (_audioReader as IDisposable)?.Dispose();
        _audioReader = null;
    }

    private void PlayAudioLinux(string filePath, TranscribedCall call)
    {
        try
        {
            _currentAudioProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffplay",
                    Arguments = $"-nodisp -autoexit -loglevel quiet \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                },
                EnableRaisingEvents = true
            };

            _currentAudioProcess.Exited += (sender, e) =>
                Dispatcher.UIThread.Post(() => HandlePlaybackComplete(call));

            _currentAudioProcess.Start();

            // Set playing flag safely (use .ToList() snapshot)
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var c in Calls.ToList())
                {
                    c.IsAudioPlaying = (c.UniqueId == call.UniqueId);
                }
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Error starting ffplay: {ex.Message}\n\nInstall ffmpeg with:\nsudo apt install ffmpeg";
            _currentAudioProcess = null;
        }
    }

    private void HandlePlaybackComplete(TranscribedCall call)
    {
        Dispatcher.UIThread.Post(() =>
        {
            call.IsAudioPlaying = false;

            // Only unpin if it wasn't an alert match
            if (!call.IsAlertMatch)
            {
                call.IsPinned = false;
                RepositionCall(call);

                if (_useGrouping)
                    ApplyGrouping();
                else
                {
                    GroupedCalls.Clear();
                    foreach (var c in Calls)
                        GroupedCalls.Add(new CallGroupItem { IsHeader = false, Call = c, ShowTalkgroup = true });
                }
            }

            StatusText = "Playback complete";

            // Force UI refresh of this call
            var index = Calls.IndexOf(call);
            if (index >= 0)
                Calls[index] = call;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Clean up audio resources when window is closed
        StopAudio();
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Close menu when clicking outside of it
        if (MenuVisible)
        {
            var point = e.GetCurrentPoint(this);
            var menuPanel = this.FindControl<Border>("MenuPanel");
            var hamburgerButton = this.FindControl<Button>("HamburgerButton");
            
            if (menuPanel != null && hamburgerButton != null)
            {
                var menuBounds = new Avalonia.Rect(menuPanel.Bounds.Size);
                var hamburgerBounds = new Avalonia.Rect(hamburgerButton.Bounds.Size);
                
                var clickPosition = point.Position;
                var menuPosition = menuPanel.TranslatePoint(new Avalonia.Point(0, 0), this);
                var hamburgerPosition = hamburgerButton.TranslatePoint(new Avalonia.Point(0, 0), this);
                
                if (menuPosition.HasValue && hamburgerPosition.HasValue)
                {
                    var menuRect = new Avalonia.Rect(menuPosition.Value.X, menuPosition.Value.Y, menuBounds.Width, menuBounds.Height);
                    var hamburgerRect = new Avalonia.Rect(hamburgerPosition.Value.X, hamburgerPosition.Value.Y, hamburgerBounds.Width, hamburgerBounds.Height);
                    
                    if (!menuRect.Contains(clickPosition) && !hamburgerRect.Contains(clickPosition))
                    {
                        HideMenu();
                    }
                }
            }
        }
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnCallRowClicked(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;

        if (sender is Border border && border.DataContext is CallGroupItem groupItem && groupItem.Call != null)
        {
            var call = groupItem.Call;

            if (call.IsAudioPlaying)
            {
                // === MANUAL STOP (click row while playing) ===
                StopAudio();
                call.IsAudioPlaying = false;
                StatusText = "Playback stopped";

                if (!call.IsAlertMatch)
                {
                    call.IsPinned = false;
                    RepositionCall(call);

                    if (_useGrouping)
                        ApplyGrouping();
                    else
                    {
                        GroupedCalls.Clear();
                        foreach (var c in Calls)
                            GroupedCalls.Add(new CallGroupItem { IsHeader = false, Call = c, ShowTalkgroup = true });
                    }
                }

                // Force UI refresh of this call (updates pin icon + play/stop icon)
                var index = Calls.IndexOf(call);
                if (index >= 0)
                    Calls[index] = call;
            }
            else
            {
                // === START PLAYING ===
                // Pin it to the top
                call.IsPinned = true;
                RepositionCall(call);

                if (_useGrouping)
                    ApplyGrouping();
                else
                {
                    GroupedCalls.Clear();
                    foreach (var c in Calls)
                        GroupedCalls.Add(new CallGroupItem { IsHeader = false, Call = c, ShowTalkgroup = true });
                }

                PlayAudio(call);
            }
        }
    }

    private void OnPinClicked(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;

        if (sender is Control control && control.DataContext is CallGroupItem groupItem && groupItem.Call != null)
        {
            groupItem.Call.IsPinned = !groupItem.Call.IsPinned;
            ApplyGrouping();   // rebuilds list with pinned items at top
        }
    }

    private async void OnSaveClicked(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;

        e.Handled = true;

        if (sender is Control control && control.DataContext is CallGroupItem groupItem && groupItem.Call != null)
        {
            var call = groupItem.Call;
            if (string.IsNullOrEmpty(call.Location))
            {
                StatusText = "Error: Audio file not available";
                return;
            }

            // Show folder picker dialog
            var topLevel = TopLevel.GetTopLevel(this);
            var storageProvider = topLevel?.StorageProvider;
            if (storageProvider == null)
            {
                StatusText = "Error: Could not access file picker";
                return;
            }

            var options = new FolderPickerOpenOptions
            {
                Title = "Select folder to save audio and transcription"
            };

            var folders = await storageProvider.OpenFolderPickerAsync(options);
            var folder = folders?.FirstOrDefault();

            if (folder == null)
            {
                StatusText = "Save cancelled";
                return;
            }

            try
            {
                // Create filename base from talkgroup and time
                var safeName = $"{call.FriendlyTalkgroup}_{call.StartTime}";
                safeName = new string(safeName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());

                // Copy audio file
                var sourceAudio = call.Location;
                var destAudio = Path.Combine(folder.Path.LocalPath, $"{safeName}.mp3");
                File.Copy(sourceAudio, destAudio, overwrite: true);

                // Save transcription
                var destTxt = Path.Combine(folder.Path.LocalPath, $"{safeName}.txt");
                var transcriptionContent = $"Talkgroup: {call.FriendlyTalkgroup}\n" +
                                          $"Frequency: {call.FriendlyFrequency}\n" +
                                          $"Time: {call.CallTime}\n" +
                                          $"Duration: {call.Duration}s\n\n" +
                                          $"Transcription:\n{call.Transcription}";
                File.WriteAllText(destTxt, transcriptionContent);

                StatusText = $"Saved to {folder.Path.LocalPath}";
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving: {ex.Message}";
            }
        }
    }

    private async void OnCopyClicked(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;

        if (sender is Control control && control.DataContext is CallGroupItem groupItem && groupItem.Call != null)
        {
            var call = groupItem.Call;

            if (string.IsNullOrEmpty(call.Transcription))
            {
                StatusText = "Error: No transcription to copy";
                return;
            }

            // Copy transcription to clipboard using Avalonia's clipboard
            try
            {
                var clipboard = this.Clipboard;
                await clipboard.SetTextAsync(call.Transcription);
                StatusText = "Transcription copied to clipboard";
            }
            catch (Exception ex)
            {
                StatusText = $"Error copying to clipboard: {ex.Message}";
            }
        }
    }

    private void OnIncreaseFontSizeClicked(object? sender, RoutedEventArgs e)
    {
        if (FontSize < 24)
        {
            FontSize += 2;
        }
        HideMenu();
    }

    private void OnDecreaseFontSizeClicked(object? sender, RoutedEventArgs e)
    {
        if (FontSize > 10)
        {
            FontSize -= 2;
        }
        HideMenu();
    }

    private void SortCalls()
    {
        // Apply grouping which will also apply sorting
        ApplyGrouping();

        StatusText = _currentSortMode switch
        {
            0 => "Sorted by time (newest first)",
            1 => "Sorted by time (oldest first)",
            2 => "Sorted by talkgroup",
            _ => "Sorted"
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
