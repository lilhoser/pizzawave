using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
using System.Threading;
using static pizzapi.TraceLogger;

namespace pizzapi;

// Wrapper class for grouped call display
public class CallGroupItem
{
    public bool IsHeader { get; set; }
    public string? HeaderText { get; set; }
    public TranscribedCall? Call { get; set; }
    public bool ShowTalkgroup { get; set; } = true;
    public bool IsExpanded { get; set; } = true; // For collapsible groups
    public bool IsSearchMatch { get; set; }  // For search highlighting
    public int MatchIndex { get; set; }      // Position among matches for navigation
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
        // Handle alert match with parameterized colors (#4a2a2a for matches, #3a3a3a for non-matches)
        if (value is bool isVisible && parameter is string @params && @params.Contains('|'))
        {
            var split = @params.Split('|');
            var colorHex = isVisible ? split[0] : split[1];
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));
        }
        
        // Default alert match color
        if (value is bool isMatch && isMatch)
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4a2a2a"));
            
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("Transparent"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Not used for color conversion
        return false;
    }
}

public class BoolToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isTrue = value is bool b && b;
        string trueColor = "#ffa500";
        string falseColor = "#555555";

        if (parameter is string param)
        {
            var parts = param.Contains('|') ? param.Split('|') : param.Split(',');
            if (parts.Length >= 2)
            {
                trueColor = parts[0].Trim();
                falseColor = parts[1].Trim();
            }
        }

        var colorHex = isTrue ? trueColor : falseColor;
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return false;
    }
}

public class AlertSearchBackgroundConverter : IValueConverter
{
    // Combines alert match background with search highlighting
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var item = value as CallGroupItem;
        
        // Search highlight takes precedence - lime green for matches
        if (item?.IsSearchMatch == true)
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#00ff00"));
        
        // Alert match background
        if (item?.Call?.IsAlertMatch == true)
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#4a2a2a"));
            
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("Transparent"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class IntGreaterThanZeroConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int count)
            return count > 0;
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SearchHighlightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isMatch && isMatch)
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#00ff00"));
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("Transparent"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class SearchTextForegroundConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Black text for search matches (lime green background needs black text)
        if (value is bool isMatch && isMatch)
            return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#000000"));
        
        // White text for alert matches and normal items
        return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("White"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class ButtonActiveStateConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string filterType && parameter is string compareType)
        {
            bool isActive = filterType == compareType;
            
            // Return a dictionary with both styles so we can select based on active state
            var styleDict = new Dictionary<string, string>
            {
                {"active", "ActiveButtonStyle"},
                {"inactive", "InactiveButtonStyle"}
            };
            
            return isActive ? styleDict["active"] : styleDict["inactive"];
        }
        return "InactiveButtonStyle";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public enum RightPaneType
{
    None,
    View,
    Settings,
    Alerts,
    Cleanup,
    Range
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
    private string _currentSettingsPath = Settings.DefaultSettingsFileLocation;

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
    private bool _isAlertSnoozed = false;
    private string _bellToolTipText = "Alert audio: Enabled (click to snooze)";
    private Avalonia.Media.SolidColorBrush? _bellColor = null;
    private Button? _alwaysPinAlertsButton;
    // Sort menu buttons for updating checkmarks
    private Button? _sortNewestButton;
    private Button? _sortOldestButton;
    private Button? _sortTalkgroupButton;
    // Group menu buttons for updating checkmarks
    private Button? _groupNoneButton;
    private Button? _groupTalkgroupButton;
    private Button? _groupTimeOfDayButton;
    private Button? _groupSourceButton;

    // Right pane content
    private RightPaneType _activePane = RightPaneType.None;
    private Control? _activePaneContent;
    private SettingsPanel? _settingsPanel;
    private AlertManagerPanel? _alertsPanel;
    private CleanupPanel? _cleanupPanel;
    private ViewPanel? _viewPanel;
    private OfflineRangePanel? _offlineRangePanel;
    
    // Time range filter state for persistent button bar
    private string _currentFilter = "all"; // all, 24h, 2d, week, custom
    
    public string CurrentFilter
    {
        get => _currentFilter;
        set { _currentFilter = value; RaisePropertyChanged(); }
    }
    
    // Button state properties for styling
    public bool IsLiveActive => _currentFilter == "all";
    public bool Is24hActive => _currentFilter == "24h";
    public bool Is2dActive => _currentFilter == "2d";
    public bool IsWeekActive => _currentFilter == "week";
    public bool IsRangeActive => _currentFilter == "custom";

    public bool IsPaneOpen => _activePane != RightPaneType.None;
    public bool IsViewPaneOpen => _activePane == RightPaneType.View;
    public bool IsSettingsPaneOpen => _activePane == RightPaneType.Settings;
    public bool IsAlertsPaneOpen => _activePane == RightPaneType.Alerts;
    public bool IsCleanupPaneOpen => _activePane == RightPaneType.Cleanup;
    public bool IsRangePaneOpen => _activePane == RightPaneType.Range;

    public Control? ActivePaneContent
    {
        get => _activePaneContent;
        private set { _activePaneContent = value; RaisePropertyChanged(); }
    }
    
    // Custom date range for Pick Range button
    private DateTime? _customStartDate;
    private DateTime? _customEndDate;
    public DateTime? CustomStartDate
    {
        get => _customStartDate;
        set { _customStartDate = value; RaisePropertyChanged(); }
    }
    public DateTime? CustomEndDate
    {
        get => _customEndDate;
        set { _customEndDate = value; RaisePropertyChanged(); }
    }
    
    // Track expanded group headers
    private HashSet<string> _collapsedGroups = new HashSet<string>();
    // Flag to prevent re-entrancy in collapse/expand
    private bool _isCollapsingExpanding = false;

    // Search bar UI references and state
    private TextBox? _searchTextBox;
    private Button? _clearSearchButton;
    private TextBlock? _searchPlaceholder;
    private int _currentMatchIndex = -1;
    private bool _isApplyingSettings;
    public bool IsApplyingSettings
    {
        get => _isApplyingSettings;
        private set { _isApplyingSettings = value; RaisePropertyChanged(); }
    }

    // Timer for periodic usage text updates
    private CancellationTokenSource? _usageTimerCts;
    private Task? _usageTimerTask;
    private readonly SemaphoreSlim _usageUpdateLock = new SemaphoreSlim(1, 1);
    private bool _isClosing;
    
    public bool IsAlertSnoozed
    {
        get => _isAlertSnoozed;
        set { _isAlertSnoozed = value; RaisePropertyChanged(); }
    }

    public bool IsOfflineMode => _isOfflineMode;

    public string ModeIndicatorText => _isOfflineMode ? "OFFLINE" : "LIVE";

    public string ModeIndicatorColor => _isOfflineMode ? "#ffaa00" : "#00ff00";

    public string BellToolTipText
    {
        get => _bellToolTipText;
        set { _bellToolTipText = value; RaisePropertyChanged(); }
    }

    public Avalonia.Media.SolidColorBrush BellColor
    {
        get => _bellColor ?? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#666666"));
        set { _bellColor = value; RaisePropertyChanged(); }
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

    // Cached usage text - updated periodically via timer
    private string _usageText = "Usage: 0mb";

    public string UsageText => _usageText;

    // Search functionality
    private string _searchText = "";
    
    // Search text property for XAML binding (inverted logic - true when NOT searching)
    public bool IsSearching => !string.IsNullOrEmpty(_searchText);

    public string SearchText
    {
        get => _searchText;
        set 
        { 
            _searchText = value; 
            RaisePropertyChanged(nameof(IsSearching));
            RaisePropertyChanged(nameof(SearchText));
        }
    }
    
    private readonly ObservableCollection<TranscribedCall> _allCalls = new ObservableCollection<TranscribedCall>();
    public ObservableCollection<TranscribedCall> Calls { get; } = new ObservableCollection<TranscribedCall>();
    public ObservableCollection<CallGroupItem> GroupedCalls { get; } = new ObservableCollection<CallGroupItem>();
    
    // Time range filtering methods
    private void ApplyTimeRangeFilter()
    {
        var now = DateTimeOffset.Now;
        long? minStartTime = null, maxEndTime = null;
        
        switch (_currentFilter)
        {
            case "24h":
                minStartTime = now.AddHours(-24).ToUnixTimeSeconds();
                maxEndTime = now.ToUnixTimeSeconds();
                break;
            case "2d":
                minStartTime = now.AddHours(-48).ToUnixTimeSeconds();
                maxEndTime = now.ToUnixTimeSeconds();
                break;
            case "week":
                minStartTime = now.AddDays(-7).ToUnixTimeSeconds();
                maxEndTime = now.ToUnixTimeSeconds();
                break;
            case "custom":
                if (_customStartDate.HasValue && _customEndDate.HasValue)
                {
                    var startOffset = new DateTimeOffset(_customStartDate.Value);
                    var endOffset = new DateTimeOffset(_customEndDate.Value.AddDays(1));
                    minStartTime = startOffset.ToUnixTimeSeconds();
                    maxEndTime = endOffset.ToUnixTimeSeconds();
                }
                break;
        }
        
        // Rebuild Calls with filter applied if needed
        var filteredCalls = new List<TranscribedCall>();

        if (minStartTime.HasValue && maxEndTime.HasValue)
        {
            foreach (var call in _allCalls)
            {
                // Check if call is within the time range
                bool afterMinStart = call.StartTime >= minStartTime.Value;
                bool beforeMaxEnd = call.StopTime <= maxEndTime.Value;

                if (afterMinStart && beforeMaxEnd)
                    filteredCalls.Add(call);
            }
        }
        else
        {
            // No filter - use all calls
            filteredCalls = _allCalls.ToList();
        }
        
        // Update the Calls collection
        Calls.Clear();
        foreach (var call in filteredCalls)
            Calls.Add(call);
            
        // Update grouped view
        if (_useGrouping)
        {
            ApplyGrouping();
        }
        else
        {
            GroupedCalls.Clear();
            foreach (var call in Calls)
                GroupedCalls.Add(new CallGroupItem { IsHeader = false, Call = call, ShowTalkgroup = true });
        }
        
        StatusText = _currentFilter switch
        {
            "all" => $"Showing all calls ({Calls.Count})",
            "24h" => $"Last 24 hours ({Calls.Count})",
            "2d" => $"Last 2 days ({Calls.Count})",
            "week" => $"Last week ({Calls.Count})",
            "custom" => _customStartDate.HasValue && _customEndDate.HasValue
                ? $"Custom range: {_customStartDate.Value:d} - {_customEndDate.Value:d} ({Calls.Count})"
                : $"Showing all calls ({Calls.Count})",
            _ => $"Showing {Calls.Count} calls"
        };
        
        // Update button states
        RaisePropertyChanged(nameof(IsLiveActive));
        RaisePropertyChanged(nameof(Is24hActive));
        RaisePropertyChanged(nameof(Is2dActive));
        RaisePropertyChanged(nameof(IsWeekActive));
        RaisePropertyChanged(nameof(IsRangeActive));
    }
    
    public void SetTimeRangeFilter(string filterType)
    {
        _currentFilter = filterType;
        
        switch (filterType)
        {
            case "all":
                _customStartDate = null;
                _customEndDate = null;
                break;
            case "24h":
            case "2d":
            case "week":
                // No custom dates needed for preset ranges
                break;
            case "custom":
                if (!_customStartDate.HasValue)
                    _customStartDate = DateTime.Now.AddDays(-7);
                if (!_customEndDate.HasValue)
                    _customEndDate = DateTime.Now;
                break;
        }
        
        ApplyTimeRangeFilter();
        RaisePropertyChanged(nameof(CurrentFilter));
        RaisePropertyChanged(nameof(IsLiveActive));
        RaisePropertyChanged(nameof(Is24hActive));
        RaisePropertyChanged(nameof(Is2dActive));
        RaisePropertyChanged(nameof(IsWeekActive));
        RaisePropertyChanged(nameof(IsRangeActive));
    }

    private void ClearVisibleCalls()
    {
        Calls.Clear();
        GroupedCalls.Clear();
        _currentMatchIndex = -1;
    }

    private void SwitchToLiveMode()
    {
        StopAudio();

        if (_offlineCallManager != null)
        {
            _offlineCallManager.Stop();
            _offlineCallManager.Dispose();
            _offlineCallManager = null;
        }

        _isOfflineMode = false;
        RaisePropertyChanged(nameof(IsOfflineMode));
        RaisePropertyChanged(nameof(ModeIndicatorText));
        RaisePropertyChanged(nameof(ModeIndicatorColor));

        _allCalls.Clear();
        ClearVisibleCalls();

        _currentFilter = "all";
        _customStartDate = null;
        _customEndDate = null;
        UpdateButtonStates();

        if (_callManager == null || !_callManager.IsStarted())
        {
            _ = InitializeAsync();
        }

        Title = "PizzaPi";
        StatusText = "Live mode";
    }

    private void SwitchToOfflineHistory(DateTime start, DateTime end, string filterLabel)
    {
        StopAudio();

        if (_callManager != null && _callManager.IsStarted())
        {
            _callManager.Stop();
        }

        if (_offlineCallManager != null)
        {
            _offlineCallManager.Stop();
            _offlineCallManager.Dispose();
            _offlineCallManager = null;
        }

        _isOfflineMode = true;
        RaisePropertyChanged(nameof(IsOfflineMode));
        RaisePropertyChanged(nameof(ModeIndicatorText));
        RaisePropertyChanged(nameof(ModeIndicatorColor));

        _allCalls.Clear();
        ClearVisibleCalls();

        _currentFilter = filterLabel;
        if (filterLabel == "custom")
        {
            _customStartDate = start;
            _customEndDate = end;
        }
        else
        {
            _customStartDate = null;
            _customEndDate = null;
        }

        var loadedCalls = LoadOfflineCallsFromHistory(start, end);
        foreach (var call in loadedCalls)
        {
            call.FriendlyTalkgroup = TalkgroupHelper.FormatTalkgroup(_settings, call.Talkgroup);
            call.FriendlyFrequency = FormatFrequency(call.Frequency);
            call.PlayAudioCommand = (c) => PlayAudio(c);

            int insertIndex = GetInsertIndexInList(_allCalls, call);
            _allCalls.Insert(insertIndex, call);
        }

        _callsReceivedCount = loadedCalls.Count;
        _alertsTriggeredCount = loadedCalls.Count(c => c.IsAlertMatch);
        RaisePropertyChanged(nameof(CallsReceivedCount));
        RaisePropertyChanged(nameof(AlertsTriggeredCount));

        ApplyTimeRangeFilter();
        UpdateButtonStates();

        Title = "PizzaPi - Offline History";
    }

    private List<TranscribedCall> LoadOfflineCallsFromHistory(DateTime start, DateTime end)
    {
        var capturesRoot = Settings.DefaultLiveCaptureDirectory;
        if (!Directory.Exists(capturesRoot))
        {
            StatusText = $"Captures directory not found: {capturesRoot}";
            return new List<TranscribedCall>();
        }

        var candidateFolders = Directory.EnumerateDirectories(capturesRoot)
            .Where(dir => ShouldIncludeFolderForRange(dir, start, end))
            .ToList();

        if (candidateFolders.Count == 0)
        {
            StatusText = "No capture folders found for the selected date range";
            return new List<TranscribedCall>();
        }

        var loadedCalls = new List<TranscribedCall>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in candidateFolders)
        {
            var journalPath = Path.Combine(folder, "calljournal.json");
            if (!File.Exists(journalPath))
                continue;

            try
            {
                foreach (var line in File.ReadLines(journalPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    TranscribedCall? call;
                    try { call = Newtonsoft.Json.JsonConvert.DeserializeObject<TranscribedCall>(line); }
                    catch { continue; }

                    if (call == null)
                        continue;

                    var key = call.Location ?? call.UniqueId.ToString();
                    if (seenKeys.Add(key))
                        loadedCalls.Add(call);
                }
            }
            catch
            {
                // Skip folders with unreadable journals
            }
        }

        var startUnix = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Local)).ToUnixTimeSeconds();
        var endUnix = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Local)).ToUnixTimeSeconds();

        return loadedCalls
            .Where(c => c.StartTime >= startUnix && c.StartTime <= endUnix)
            .ToList();
    }

    private bool ShouldIncludeFolderForRange(string folder, DateTime start, DateTime end)
    {
        try
        {
            var folderName = Path.GetFileName(folder);
            var match = System.Text.RegularExpressions.Regex.Match(folderName, @"^(?<date>\d{4}-\d{2}-\d{2})-(?<time>\d{6})");
            if (match.Success)
            {
                var datePart = match.Groups["date"].Value;
                var timePart = match.Groups["time"].Value;

                if (DateTime.TryParseExact($"{datePart} {timePart}", "yyyy-MM-dd HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var folderTimestamp))
                {
                    return folderTimestamp >= start && folderTimestamp <= end;
                }

                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var folderDate))
                {
                    return folderDate.Date >= start.Date && folderDate.Date <= end.Date;
                }
            }

            var lastWrite = Directory.GetLastWriteTime(folder);
            return lastWrite >= start.AddDays(-1) && lastWrite <= end.AddDays(1);
        }
        catch
        {
            return false;
        }
    }
    
    public void SetCustomDateRange(DateTime? start, DateTime? end)
    {
        _customStartDate = start;
        _customEndDate = end;
        _currentFilter = "custom";
        ApplyTimeRangeFilter();
        RaisePropertyChanged(nameof(CurrentFilter));
    }
    private bool _useGrouping => _currentGroupMode > 0;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        InitializeRightPanePanels();

        // Get version from assembly metadata (populated by CI from git tag)
        _versionString = GetAssemblyVersion();
        RaisePropertyChanged(nameof(VersionString));

        // Initialize font size resource
        if (Application.Current != null)
            Application.Current.Resources["CurrentFontSize"] = FontSize;

        // Get search bar elements
        _searchTextBox = this.FindControl<TextBox>("SearchTextBox");
        _clearSearchButton = this.FindControl<Button>("ClearSearchButton");
        _searchPlaceholder = this.FindControl<TextBlock>("SearchPlaceholder");
        
// Register converters in Application.Resources for use in XAML bindings
        if (Application.Current != null)
        {
            Application.Current.Resources["IntGreaterThanZeroConverter"] = new IntGreaterThanZeroConverter();
            Application.Current.Resources["SearchHighlightConverter"] = new SearchHighlightConverter();
            Application.Current.Resources["SearchTextForegroundConverter"] = new SearchTextForegroundConverter();
            Application.Current.Resources["AlertSearchBackgroundConverter"] = new AlertSearchBackgroundConverter();
        }

    }

    protected override void OnOpened(EventArgs e)
    {
        // Start initialization in background to avoid blocking UI thread
        _ = InitializeAsync();
        StartUsageTimer();

        base.OnOpened(e);
    }
    
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_isClosing)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = CloseAfterStoppingAsync();
    }
    
    private async Task CloseAfterStoppingAsync()
    {
        try
        {
            await StopUsageTimerAsync().ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _isClosing = true;
                Close();
            });
        }
    }

    private void OnViewButtonClicked(object? sender, RoutedEventArgs e)
    {
        TogglePane(RightPaneType.View);
    }

    private void InitializeRightPanePanels()
    {
        _settingsPanel = new SettingsPanel(_settings);
        _alertsPanel = new AlertManagerPanel(_settings);
        _cleanupPanel = new CleanupPanel();
        _viewPanel = new ViewPanel();
        _offlineRangePanel = new OfflineRangePanel(_settings);

        _settingsPanel.RequestClose += (_, _) => ClosePane(reloadSettings: true);
        _settingsPanel.ApplySettingsRequested += ApplySettingsAndRestartAsync;
        _settingsPanel.SettingsPathChanged += path =>
        {
            _currentSettingsPath = path;
            _alertsPanel?.SetSettings(_settings, _currentSettingsPath);
        };
        _alertsPanel.RequestClose += (_, _) => ClosePane(reloadSettings: true);
        _cleanupPanel.RequestClose += (_, _) => ClosePane(reloadSettings: false);
        _cleanupPanel.CleanupCompleted += async (_, _) => await UpdateUsageTextAsync();
        _offlineRangePanel.RequestClose += (_, _) => ClosePane(reloadSettings: false);
        _offlineRangePanel.OfflineLoaded += (_, result) =>
        {
            SwitchToOfflineHistory(result.Start, result.End, "custom");
            ClosePane(reloadSettings: false);
        };

        WireViewPanelButtons(_viewPanel);
        ActivePaneContent = null;
    }

    private void WireViewPanelButtons(ViewPanel panel)
    {
        _alwaysPinAlertsButton = panel.FindControl<Button>("AlwaysPinAlertsButton");
        _sortNewestButton = panel.FindControl<Button>("SortNewestButton");
        _sortOldestButton = panel.FindControl<Button>("SortOldestButton");
        _sortTalkgroupButton = panel.FindControl<Button>("SortTalkgroupButton");
        _groupNoneButton = panel.FindControl<Button>("GroupNoneButton");
        _groupTalkgroupButton = panel.FindControl<Button>("GroupTalkgroupButton");
        _groupTimeOfDayButton = panel.FindControl<Button>("GroupTimeOfDayButton");
        _groupSourceButton = panel.FindControl<Button>("GroupSourceButton");

        var clearCallsButton = panel.FindControl<Button>("ClearCallsButton");
        var viewLogButton = panel.FindControl<Button>("ViewLogButton");
        var decreaseFontButton = panel.FindControl<Button>("DecreaseFontButton");
        var increaseFontButton = panel.FindControl<Button>("IncreaseFontButton");
        var collapseExpandButton = panel.FindControl<Button>("CollapseExpandButton");

        if (_sortNewestButton != null) _sortNewestButton.Click += OnSortByTimeNewestClicked;
        if (_sortOldestButton != null) _sortOldestButton.Click += OnSortByTimeOldestClicked;
        if (_sortTalkgroupButton != null) _sortTalkgroupButton.Click += OnSortByTalkgroupClicked;
        if (_groupNoneButton != null) _groupNoneButton.Click += OnGroupByNoneClicked;
        if (_groupTalkgroupButton != null) _groupTalkgroupButton.Click += OnGroupByTalkgroupClicked;
        if (_groupTimeOfDayButton != null) _groupTimeOfDayButton.Click += OnGroupByTimeOfDayClicked;
        if (_groupSourceButton != null) _groupSourceButton.Click += OnGroupBySourceClicked;
        if (_alwaysPinAlertsButton != null) _alwaysPinAlertsButton.Click += OnAlwaysPinAlertsClicked;

        if (clearCallsButton != null) clearCallsButton.Click += OnClearClicked;
        if (viewLogButton != null) viewLogButton.Click += OnViewLogClicked;
        if (decreaseFontButton != null) decreaseFontButton.Click += OnDecreaseFontSizeClicked;
        if (increaseFontButton != null) increaseFontButton.Click += OnIncreaseFontSizeClicked;
        if (collapseExpandButton != null) collapseExpandButton.Click += OnCollapseExpandAllClicked;
    }

    private void TogglePane(RightPaneType pane)
    {
        if (_activePane == pane)
        {
            ClosePane(reloadSettings: false);
            return;
        }

        _activePane = pane;
        ActivePaneContent = pane switch
        {
            RightPaneType.View => _viewPanel,
            RightPaneType.Settings => _settingsPanel,
            RightPaneType.Alerts => _alertsPanel,
            RightPaneType.Cleanup => _cleanupPanel,
            RightPaneType.Range => _offlineRangePanel,
            _ => null
        };

        if (pane == RightPaneType.View)
        {
            UpdateSortCheckmarks();
            UpdateGroupCheckmarks();
            UpdateAlwaysPinLabel();
        }

        RaisePaneStateChanged();
    }

    private void ClosePane(bool reloadSettings)
    {
        _activePane = RightPaneType.None;
        ActivePaneContent = null;
        RaisePaneStateChanged();

        if (reloadSettings)
        {
            _settings = Settings.LoadFromFile(_currentSettingsPath);
            _settingsPanel?.SetSettings(_settings);
            _alertsPanel?.SetSettings(_settings, _currentSettingsPath);
            UpdateBellState();
        }
    }

    private void RaisePaneStateChanged()
    {
        RaisePropertyChanged(nameof(IsPaneOpen));
        RaisePropertyChanged(nameof(IsViewPaneOpen));
        RaisePropertyChanged(nameof(IsSettingsPaneOpen));
        RaisePropertyChanged(nameof(IsAlertsPaneOpen));
        RaisePropertyChanged(nameof(IsCleanupPaneOpen));
        RaisePropertyChanged(nameof(IsRangePaneOpen));
    }

    private async Task ApplySettingsAndRestartAsync(Settings newSettings)
    {
        if (IsApplyingSettings)
            return;

        IsApplyingSettings = true;
        // Update settings and UI state
        _settings = newSettings;
        TraceLogger.SetLevel(_settings.TraceLevelApp);
        pizzalib.TraceLogger.SetLevel(_settings.TraceLevelApp);

        _currentSortMode = _settings.SortMode;
        _currentGroupMode = _settings.GroupMode;
        FontSize = _settings.FontSize;

        UpdateSortCheckmarks();
        UpdateGroupCheckmarks();
        UpdateBellState();
        _alertsPanel?.SetSettings(_settings, _currentSettingsPath);

        if (_isOfflineMode)
        {
            _isApplyingSettings = false;
            return;
        }

        try
        {
            StatusText = "Applying settings...";

            if (_callManager != null && _callManager.IsStarted())
            {
                await Task.Run(() =>
                {
                    _callManager.Stop(block: true);
                    _callManager.Dispose();
                });
                _callManager = null;
            }

            _callManager = new LiveCallManager(OnNewCall);
            await Task.Run(async () => await _callManager.Initialize(_settings));
            _ = _callManager.Start();

            StatusText = "Settings applied";
            _serverStatusText = "Server: Running on port " + _settings.ListenPort;
            ServerInfo = $"Port {_settings.ListenPort}";
            RaisePropertyChanged(nameof(ServerStatusText));
            RaisePropertyChanged(nameof(ServerInfo));
        }
        catch (Exception ex)
        {
            StatusText = $"Error applying settings: {ex.Message}";
        }
        finally
        {
            IsApplyingSettings = false;
        }
    }

    private void UpdateAlwaysPinLabel()
    {
        if (_alwaysPinAlertsButton != null)
        {
            _alwaysPinAlertsButton.Content = _alwaysPinAlertMatches ? "✓ Always pin alert matches" : "  Always pin alert matches";
        }
    }

    private void HideSubMenus()
    {
    }

    private void OnSortButtonClicked(object? sender, RoutedEventArgs e)
    {
        // No-op: sort controls are now hosted in the right pane.
    }

    private void OnGroupByButtonClicked(object? sender, RoutedEventArgs e)
    {
        // No-op: group-by controls are now hosted in the right pane.
    }

    private void CloseSortSubMenu()
    {
    }

    private void CloseGroupBySubMenu()
    {
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

    private void ApplySearchFilter(string searchText)
    {
        _searchText = searchText ?? "";

        ApplyTimeRangeFilter();
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var textBox = (TextBox)sender!;
        bool hadFocus = textBox.IsFocused;
        
        // Update search text field
        _searchText = textBox.Text ?? "";
        
        // Show/hide clear button based on whether searching
        if (_clearSearchButton != null)
            _clearSearchButton.IsVisible = !string.IsNullOrEmpty(_searchText);
        
        // Control watermark visibility
        if (_searchPlaceholder != null)
        {
            _searchPlaceholder.IsVisible = string.IsNullOrEmpty(_searchText);
        }
        
        // Apply time range + search filter
        ApplyTimeRangeFilter();

        if (hadFocus)
        {
            Dispatcher.UIThread.Post(() => textBox.Focus());
        }
    }

    private void OnClearSearchClicked(object? sender, RoutedEventArgs e)
    {
        _searchText = "";

        if (_searchTextBox != null)
            _searchTextBox.Text = "";

        if (_clearSearchButton != null)
            _clearSearchButton.IsVisible = false;

        _currentMatchIndex = -1;
        StatusText = "No search filter";

        ApplyTimeRangeFilter();
    }

    private void OnSearchTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            // Check shift key state from event
            var modifiers = e.KeyModifiers;
            bool shiftPressed = (modifiers & KeyModifiers.Shift) != 0;
            NavigateToMatch(shiftPressed);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Clear search on Escape key
            _searchText = "";
            
            if (_searchTextBox != null)
                _searchTextBox.Text = "";
            
            if (_clearSearchButton != null)
                _clearSearchButton.IsVisible = false;
            
            _currentMatchIndex = -1;
            StatusText = "No search filter";
            
            ApplyGrouping();
        }
    }

    private void OnSearchTextBoxGotFocus(object? sender, RoutedEventArgs e)
    {
        // Show watermark when textbox is empty and focused
        if (_searchTextBox != null && string.IsNullOrEmpty(_searchTextBox.Text))
        {
            _searchText = "";
            ApplyGrouping();
        }
    }

    private void OnSearchTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        // Hide watermark when textbox loses focus and is empty
        if (_searchTextBox != null && string.IsNullOrEmpty(_searchTextBox.Text))
        {
            _searchText = "";
            ApplyGrouping();
        }
    }

    private void NavigateToMatch(bool goPrevious)
    {
        // Only navigate if currently searching
        if (string.IsNullOrEmpty(_searchText)) return;
        
        var matches = GroupedCalls.Where(i => i.IsSearchMatch && !i.IsHeader).ToList();
        int matchCount = matches.Count;
        
        if (matchCount == 0) return;
        
        // Remove orange border from all items first
        foreach (var item in GroupedCalls.Where(i => !i.IsHeader))
            if (item.Call != null) item.Call.IsCurrentMatch = false;
        
        // Calculate next index with wrap-around
        _currentMatchIndex = goPrevious 
            ? (_currentMatchIndex - 1 + matchCount) % matchCount
            : (_currentMatchIndex + 1) % matchCount;
        
        // Apply orange border to new match
        var matchedItem = matches[_currentMatchIndex];
        if (matchedItem.Call != null)
            matchedItem.Call.IsCurrentMatch = true;
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
            // Close transient menus first...
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
        // Always group the visible calls; search only affects highlighting
        var callsToGroup = Calls.ToList();  // Convert to list for consistency

        GroupedCalls.Clear();

        // First, sort the Calls collection based on current sort mode
        // Always put manually pinned calls at top
        List<TranscribedCall> sortedCalls;

        // Check if any calls are manually pinned
        bool hasPinnedCalls = callsToGroup.Any(c => c.IsPinned);

        if (hasPinnedCalls)
        {
            // Pinned calls at top (sorted by selected sort mode), then non-pinned (sorted by selected sort mode)
            var pinnedCalls = callsToGroup.Where(c => c.IsPinned).ToList();
            var nonPinnedCalls = callsToGroup.Where(c => !c.IsPinned).ToList();

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
                0 => callsToGroup.OrderByDescending(c => c.StartTime).ToList(),
                1 => callsToGroup.OrderBy(c => c.StartTime).ToList(),
                2 => callsToGroup.OrderBy(c => c.FriendlyTalkgroup).ThenByDescending(c => c.StartTime).ToList(),
                _ => callsToGroup.OrderByDescending(c => c.StartTime).ToList()
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
        
        // Set search match indicators on GroupedCalls items if searching
        if (!string.IsNullOrEmpty(_searchText))
        {
            int matchCounter = -1;
            foreach (var item in GroupedCalls.Where(i => !i.IsHeader))
            {
                bool isMatch = item.Call?.Transcription?.ToLowerInvariant().Contains(_searchText.ToLowerInvariant()) == true;
                
                if (isMatch && !string.IsNullOrEmpty(_searchText))
                    matchCounter++;
                
                item.IsSearchMatch = isMatch;
                item.MatchIndex = isMatch ? matchCounter : -1;
            }
            
            // Update status text with match count
            var matchingCount = GroupedCalls.Count(i => i.IsSearchMatch && !i.IsHeader);
            StatusText = $"Search: '{_searchText}' - {matchingCount} matches";
        }
        else
        {
            // Clear match indicators when no search active
            foreach (var item in GroupedCalls.Where(i => !i.IsHeader))
                item.IsSearchMatch = false;
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

    private string GetAssemblyVersion()
    {
        // Use assembly version - populated by CI from git tag at build time
        var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var version = assemblyVersion?.ToString(3) ?? "1.0.0";
        return $"PizzaPi v{version}";
    }

    private async Task InitializeAsync()
    {
        // Run initialization on background thread to avoid blocking UI
        await Task.Run(async () =>
        {
            StatusText = "Initializing..";

            // Initialize trace logging to file
            TraceLogger.Initialize(RedirectToStdout: false);
            pizzalib.TraceLogger.Initialize(RedirectToStdout: false);
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, "PizzaPi UI starting...");

            // Settings are already loaded in constructor, just validate/load defaults
            try
            {
                _settings = Settings.LoadFromFile(_currentSettingsPath);
                TraceLogger.Trace(TraceLoggerType.Settings, TraceEventType.Information, $"Settings loaded from file");
            }
            catch (Exception ex)
            {
                // Use default settings
                _settings = new Settings();
                TraceLogger.Trace(TraceLoggerType.Settings, TraceEventType.Warning,
                    $"Using default settings: {ex.Message}");
            }

            // Set trace level from settings
            TraceLogger.SetLevel(_settings.TraceLevelApp);
            pizzalib.TraceLogger.SetLevel(_settings.TraceLevelApp);
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, $"Trace level set to {_settings.TraceLevelApp}");

            // Apply saved sort, group, and font size settings
            _currentSortMode = _settings.SortMode;
            _currentGroupMode = _settings.GroupMode;
            FontSize = _settings.FontSize;

            // Update settings panel with loaded settings
            Dispatcher.UIThread.Post(() =>
            {
                _settingsPanel?.SetSettings(_settings);
            });

            _callManager = new LiveCallManager(OnNewCall);

            try
            {
                await _callManager.Initialize(_settings);

                // Update UI on dispatcher thread
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = "Ready to receive calls";
                    _serverStatusText = "Server: Running on port " + _settings.ListenPort;
                    ServerInfo = $"Port {_settings.ListenPort}";
                    _connectionStatus = "Connection: Ready";
                    TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, $"Server initialized on port {_settings.ListenPort}");

                    // Start listening on port 9123
                    _ = _callManager.Start();
                    // Populate usage text once at startup
                    _ = UpdateUsageTextAsync();
                    _infoText = "PizzaPi is listening on port " + _settings.ListenPort + " for trunk-recorder calls";
                    RaisePropertyChanged(nameof(ServerStatusText));
                    RaisePropertyChanged(nameof(ConnectionStatus));
                    RaisePropertyChanged(nameof(ServerInfo));

                    UpdateSortCheckmarks();
                    UpdateGroupCheckmarks();
                    UpdateBellState();
                });
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error: {ex.Message}";
                var errorServerStatus = $"Server: Error - " + ex.Message;
                var errorInfo = "Error: " + ex.Message;

                // Update UI on dispatcher thread
                Dispatcher.UIThread.Post(() =>
                {
                    StatusText = errorMsg;
                    _serverStatusText = errorServerStatus;
                    ServerInfo = errorInfo;
                    RaisePropertyChanged(nameof(ServerStatusText));
                    RaisePropertyChanged(nameof(ServerInfo));
                });
                TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Error, errorMsg);
            }
        });
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
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, 
                $"Call received: {call.FriendlyTalkgroup} @ {call.FriendlyFrequency} ({call.Duration}s)");

            // Insert at correct position: pinned calls at top, then by recency
            int insertIndexAll = GetInsertIndexInList(_allCalls, call);
            _allCalls.Insert(insertIndexAll, call);
            ApplyTimeRangeFilter();

            // Autoplay audio for alert matches (if enabled globally, not snoozed, and alert has autoplay enabled)
            if (call.IsAlertMatch && call.ShouldAutoplay && _settings.AutoplayAlerts && !IsAlertSnoozed)
            {
                TraceLogger.Trace(TraceLoggerType.Alerts, TraceEventType.Information, 
                    $"Alert triggered: {call.FriendlyTalkgroup} - autoplaying audio");
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
                TraceLogger.Trace(TraceLoggerType.Alerts, TraceEventType.Information, 
                    $"Alert match #{_alertsTriggeredCount}: {call.FriendlyTalkgroup}");
            }

            // If always pin alert matches is enabled, ensure the call stays pinned
            if (call.IsAlertMatch && _alwaysPinAlertMatches)
            {
                call.IsPinned = true;
                RepositionCall(call);
            }

            // Cleanup old calls to prevent memory leaks
            CleanupOldCalls();
        });
    }

    /// <summary>
    /// Returns the index where a call should be inserted to keep pinned items at top.
    /// Pinned calls go to the top (in order of arrival), non-pinned calls go after all pinned.
    /// </summary>
    private int GetInsertIndex(TranscribedCall call)
    {
        return GetInsertIndexInList(Calls, call);
    }

    private int GetInsertIndexInList(IList<TranscribedCall> list, TranscribedCall call)
    {
        if (call.IsPinned)
        {
            // Pinned: insert at the top, before all non-pinned items
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].IsPinned)
                    return i;
            }
            return list.Count; // All are pinned, add at end of pinned section
        }
        else
        {
            // Non-pinned: insert after all pinned items (at end of list)
            return list.Count;
        }
    }

    /// <summary>
    /// Repositions a call in the list based on its pinned state.
    /// Called when a call's IsPinned property changes.
    /// </summary>
    private void RepositionCall(TranscribedCall call)
    {
        RepositionCallInList(Calls, call);
        RepositionCallInList(_allCalls, call);
    }

    private void RepositionCallInList(IList<TranscribedCall> list, TranscribedCall call)
    {
        int currentIndex = list.IndexOf(call);
        if (currentIndex < 0) return;

        int newIndex = GetInsertIndexInList(list, call);
        if (newIndex > currentIndex)
            newIndex--;

        newIndex = Math.Max(0, Math.Min(newIndex, list.Count - 1));

        if (currentIndex != newIndex)
        {
            list.RemoveAt(currentIndex);
            list.Insert(newIndex, call);
        }
    }

    /// <summary>
    /// Removes oldest non-pinned calls to prevent memory leaks on long-running sessions.
    /// Only applies to live mode (not offline call loading).
    /// MaxCallsToKeep = 0 means unlimited (no cleanup).
    /// </summary>
    private void CleanupOldCalls()
    {
        // Only cleanup in live mode when auto-cleanup is enabled and limit > 0
        if (!_isOfflineMode && _settings.AutoCleanupCalls && _settings.MaxCallsToKeep > 0)
        {
            // Remove oldest non-pinned calls if we exceed the limit
            while (Calls.Count > _settings.MaxCallsToKeep)
            {
                // Find oldest non-pinned call (at the end since sorted newest first)
                for (int i = Calls.Count - 1; i >= 0; i--)
                {
                    if (!Calls[i].IsPinned)
                    {
                        Calls.RemoveAt(i);
                        break;
                    }
                }
                // If all remaining calls are pinned, stop removing
                if (Calls.All(c => c.IsPinned))
                    break;
            }
        }
    }

    private string FormatFrequency(double frequency)
    {
        // Convert Hz to MHz for display
        double mhz = frequency / 1_000_000.0;
        return $"{mhz:F3}mhz";
    }

    private async Task UpdateUsageTextAsync()
    {
        await _usageUpdateLock.WaitAsync();
        try
        {
            string usageText = await Task.Run(GetCaptureFolderSize);
            Dispatcher.UIThread.Post(() =>
            {
                _usageText = usageText;
                RaisePropertyChanged(nameof(UsageText));
            });
        }
        catch (Exception ex)
        {
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Warning,
                $"Error calculating usage: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                _usageText = "Usage: ?mb";
                RaisePropertyChanged(nameof(UsageText));
            });
        }
        finally
        {
            _usageUpdateLock.Release();
        }
    }
    
    private void StartUsageTimer()
    {
        if (_usageTimerCts != null)
            return;
        _usageTimerCts = new CancellationTokenSource();
        _usageTimerTask = RunUsageTimerAsync(_usageTimerCts.Token);
    }

    private async Task RunUsageTimerAsync(CancellationToken token)
    {
        await UpdateUsageTextAsync();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(10), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (token.IsCancellationRequested)
                break;

            await UpdateUsageTextAsync();
        }
    }

    private async Task StopUsageTimerAsync()
    {
        if (_usageTimerCts == null)
            return;

        _usageTimerCts.Cancel();

        if (_usageTimerTask != null)
        {
            try
            {
                await _usageTimerTask;
            }
            catch (TaskCanceledException)
            {
            }
        }

        _usageTimerCts.Dispose();
        _usageTimerCts = null;
        _usageTimerTask = null;
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
        catch (Exception ex)
        {
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Warning,
                $"Error calculating capture size: {ex.Message}");
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
    }

    private void OnBellClicked(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        ToggleSnoozeMenu();
    }

    private void SnoozeAlerts(int minutes)
    {
        _snoozeUntil = DateTime.Now.AddMinutes(minutes);
        UpdateBellState();
        StatusText = $"Snoozed for {minutes} min";
    }

    private void EnableAlertAudio()
    {
        _snoozeUntil = null;
        UpdateBellState();
        StatusText = "Enabled";
    }

    // Update bell icon state - called when snooze or autoplay settings change
    private void UpdateBellState()
    {
        // Check if snooze has expired
        if (_snoozeUntil.HasValue && DateTime.Now >= _snoozeUntil.Value)
        {
            _snoozeUntil = null;
            _isAlertSnoozed = false;
        }
        else
        {
            _isAlertSnoozed = _snoozeUntil.HasValue;
        }

        // Determine tooltip text
        if (!_settings.AutoplayAlerts)
        {
            _bellToolTipText = "Alert audio: Disabled (click to enable)";
        }
        else if (_isAlertSnoozed)
        {
            _bellToolTipText = $"Alert audio: Snoozed until {_snoozeUntil!.Value:t} (click to change)";
        }
        else
        {
            _bellToolTipText = "Alert audio: Enabled (click to snooze)";
        }

        // Determine color
        bool alertsEnabled = _settings?.AutoplayAlerts ?? false;
        bool isSnoozed = _isAlertSnoozed;
        string colorHex = !alertsEnabled
            ? "#666666"  // Gray when disabled
            : isSnoozed
                ? "#ffaa00"  // Orange when snoozed
                : "#00ff00"; // Green when enabled

        _bellColor = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));

        // Raise property changed notifications
        RaisePropertyChanged(nameof(IsAlertSnoozed));
        RaisePropertyChanged(nameof(BellToolTipText));
        RaisePropertyChanged(nameof(BellColor));
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
                    UpdateBellState();
                    StatusText = "Enabled";
                })
            });
        }
        else if (IsAlertSnoozed && _snoozeUntil.HasValue)
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
                UpdateBellState();
                StatusText = "Disabled";
            })
        });

        menu.Open(this);
    }

    private void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        TogglePane(RightPaneType.Settings);
    }

    private void OnAlertsClicked(object? sender, RoutedEventArgs e)
    {
        TogglePane(RightPaneType.Alerts);
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        _allCalls.Clear();
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
            _allCalls.Clear();
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
                int insertIndex = GetInsertIndexInList(_allCalls, call);
                _allCalls.Insert(insertIndex, call);
            }
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Calls.Count after load: {Calls.Count}");

            ApplyTimeRangeFilter();
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
        _allCalls.Clear();
        Calls.Clear();

        // Switch back to live mode
        _isOfflineMode = false;

        // Update UI
        RaisePropertyChanged(nameof(ModeIndicatorText));
        RaisePropertyChanged(nameof(ModeIndicatorColor));
        RaisePropertyChanged(nameof(IsOfflineMode));

        // Restart live call manager
        _ = InitializeAsync();

        Title = "PizzaPi";
        StatusText = "Returned to live mode";

        HideMenu();
    }

    private void OnCleanupClicked(object? sender, RoutedEventArgs e)
    {
        TogglePane(RightPaneType.Cleanup);
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
            
            // Create explicit handler so we can unsubscribe (prevents memory leak)
            EventHandler<StoppedEventArgs>? playbackHandler = null;
            playbackHandler = (sender, e) =>
            {
                // Unsubscribe first to prevent leak
                if (_waveOut != null)
                    _waveOut.PlaybackStopped -= playbackHandler;
                
                StopAudio();
                HandlePlaybackComplete(newCall);
            };
            
            _waveOut.PlaybackStopped += playbackHandler;
            _waveOut.Init((IWaveProvider)_audioReader);
            _waveOut.Play();
        }
        catch (Exception ex)
        {
            StatusText = $"Error playing audio: {ex.Message}";
        }
    }

    private void StopAudio()
    {
        // Linux path - ffplay process
        if (_currentAudioProcess != null)
        {
            try
            {
                if (!_currentAudioProcess.HasExited)
                {
                    _currentAudioProcess.Kill();
                    // Wait for process to exit gracefully with timeout
                    _currentAudioProcess.WaitForExit(2000);
                }
            }
            catch { }
            finally
            {
                _currentAudioProcess.Dispose();
                _currentAudioProcess = null;
            }
        }

        // Windows path - only dispose if on Windows (NAudio is Windows-only)
        if (!_isLinux && _waveOut != null)
        {
            try
            {
                _waveOut.Stop();
                _waveOut.Dispose();
            }
            catch { }
            _waveOut = null;

            try
            {
                (_audioReader as IDisposable)?.Dispose();
            }
            catch { }
            _audioReader = null;
        }
    }

    private void PlayAudioLinux(string filePath, TranscribedCall call)
    {
        Process? process = null;
        EventHandler? processExited = null;

        try
        {
            process = new Process
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

            // Create explicit handler so we can unsubscribe
            processExited = (sender, e) =>
            {
                // Unsubscribe to prevent memory leak
                if (process != null)
                    process.Exited -= processExited;
                
                Dispatcher.UIThread.Post(() => HandlePlaybackComplete(call));
            };

            process.Exited += processExited;
            _currentAudioProcess = process;
            process.Start();

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
            process?.Dispose();
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
        try
        {
            // Stop audio playback first
            StopAudio();

            // Stop live call manager and wait for background threads
            if (_callManager != null && _callManager.IsStarted())
            {
                try
                {
                    _callManager.Stop(block: true);
                    _callManager.Dispose();
                }
                catch (Exception ex)
                {
                    // Log but don't crash on shutdown errors
                    System.Diagnostics.Debug.WriteLine($"Shutdown error: {ex.Message}");
                }
                _callManager = null;
            }

            // Stop offline call manager if active
            if (_offlineCallManager != null)
            {
                try
                {
                    _offlineCallManager.Stop();
                    _offlineCallManager.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Shutdown error: {ex.Message}");
                }
                _offlineCallManager = null;
            }

            // Flush and close trace logs
            TraceLogger.Shutdown();
            pizzalib.TraceLogger.Shutdown();
        }
        catch (Exception ex)
        {
            // Prevent any shutdown exception from causing a crash
            System.Diagnostics.Debug.WriteLine($"Shutdown error: {ex.Message}");
        }

        base.OnClosed(e);
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
    }

    // Time range filter button handlers
    private void OnLiveModeClicked(object? sender, RoutedEventArgs e)
    {
        SwitchToLiveMode();
    }

    private void OnLast24hClicked(object? sender, RoutedEventArgs e)
    {
        var end = DateTime.Now;
        var start = end.AddHours(-24);
        SwitchToOfflineHistory(start, end, "24h");
    }

    private void OnLast2dClicked(object? sender, RoutedEventArgs e)
    {
        var end = DateTime.Now;
        var start = end.AddHours(-48);
        SwitchToOfflineHistory(start, end, "2d");
    }

    private void OnLastWeekClicked(object? sender, RoutedEventArgs e)
    {
        var end = DateTime.Now;
        var start = end.AddDays(-7);
        SwitchToOfflineHistory(start, end, "week");
    }

    private void OnPickRangeClicked(object? sender, RoutedEventArgs e)
    {
        TogglePane(RightPaneType.Range);
    }

    private void OnViewClicked(object? sender, RoutedEventArgs e)
    {
        // Toggle View submenu (same as existing behavior)
        OnViewButtonClicked(sender, e);
    }

    private void OnAlertsClicked2(object? sender, RoutedEventArgs e)
    {
        OnAlertsClicked(sender, e);
    }

    private void OnSettingsClicked2(object? sender, RoutedEventArgs e)
    {
        OnSettingsClicked(sender, e);
    }

    private void OnCleanupClicked2(object? sender, RoutedEventArgs e)
    {
        OnCleanupClicked(sender, e);
    }

    private void OnExitClicked2(object? sender, RoutedEventArgs e)
    {
        Close();
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
                if (clipboard == null)
                {
                    StatusText = "Error: Clipboard not available";
                    return;
                }
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
            _settings.FontSize = FontSize;
            _settings.SaveToFile();
        }
        HideMenu();
    }

    private void OnDecreaseFontSizeClicked(object? sender, RoutedEventArgs e)
    {
        if (FontSize > 10)
        {
            FontSize -= 2;
            _settings.FontSize = FontSize;
            _settings.SaveToFile();
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
        
        // Set initial button states
        UpdateButtonStates();
    }
    
    private void OnWindowLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Initialize time range filter to 'all' (live mode)
        _currentFilter = "all";
        _customStartDate = null;
        _customEndDate = null;
        
        UpdateButtonStates();
        ApplyTimeRangeFilter();
    }
    
    private void UpdateButtonStates()
    {
        RaisePropertyChanged(nameof(IsLiveActive));
        RaisePropertyChanged(nameof(Is24hActive));
        RaisePropertyChanged(nameof(Is2dActive));
        RaisePropertyChanged(nameof(IsWeekActive));
        RaisePropertyChanged(nameof(IsRangeActive));
    }
}
