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
using Avalonia.Media;
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
    Range,
    InsightTranscript
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
    private string _currentFilter = "24h"; // 24h, 2d, week, custom
    private List<TranscribedCall>? _historicalRangeCalls;
    
    public string CurrentFilter
    {
        get => _currentFilter;
        set { _currentFilter = value; RaisePropertyChanged(); }
    }
    
    // Button state properties for styling
    public bool IsLiveActive => _currentFilter == "24h";
    public bool Is24hActive => _currentFilter == "24h";
    public bool Is2dActive => _currentFilter == "2d";
    public bool IsWeekActive => _currentFilter == "week";
    public bool IsRangeActive => _currentFilter == "custom";

    // Radio menu active state - true when in offline mode or live with all filter
    // Range button aliases for XAML binding compatibility
    public bool IsRangeLiveActive => _currentFilter == "24h";
    public bool IsRange24hActive => _currentFilter == "24h";
    public bool IsRange2dActive => _currentFilter == "2d";
    public bool IsRangeWeekActive => _currentFilter == "week";
    public bool IsRangeCustomActive => _currentFilter == "custom";

    public bool IsRadioMode => !_isInsightsMode;
    public bool ShowRadioSubmenu
    {
        get => _showRadioSubmenu;
        set
        {
            if (_showRadioSubmenu == value) return;
            _showRadioSubmenu = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsRadioSubmenuVisible));
        }
    }
    public bool ShowInsightsSubmenu
    {
        get => _showInsightsSubmenu;
        set
        {
            if (_showInsightsSubmenu == value) return;
            _showInsightsSubmenu = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsInsightsSubmenuVisible));
        }
    }
    public bool IsRadioSubmenuVisible => IsCallsMode && ShowRadioSubmenu;
    public bool IsInsightsSubmenuVisible => IsInsightsMode && ShowInsightsSubmenu;
    public string RadioDataSourceText =>
        _isInsightsMode
            ? string.Empty
            : _currentFilter == "24h"
                ? "Radio source: memory + 24h capture folders"
                : "Radio source: historical disk";

    // Insights mode button states
    // Note: morning/afternoon/night are kept for auto-activation logic but not shown in UI
    public bool IsInsightsMorningActive => _isInsightsMode && _insightsFilter == "morning";
    public bool IsInsightsAfternoonActive => _isInsightsMode && _insightsFilter == "afternoon";
    public bool IsInsightsNightActive => _isInsightsMode && _insightsFilter == "night";
    public bool IsInsightsTodayActive => _isInsightsMode && _insightsFilter == "today";
    public bool IsInsights24hActive => _isInsightsMode && _insightsFilter == "24h";
    public bool IsInsights2dActive => _isInsightsMode && _insightsFilter == "2d";
    public bool IsInsightsWeekActive => _isInsightsMode && _insightsFilter == "week";
    public bool IsInsightsRangeActive => _isInsightsMode && _insightsFilter == "range";

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

    // Insights mode fields and properties
    private InsightsService? _insightsService;
    private readonly InsightsStorage _insightsStorage = new InsightsStorage();
    private bool _isInsightsMode;
    private bool _showRadioSubmenu;
    private bool _showInsightsSubmenu;
    private string _insightsFilter = "today";
    private DateTime? _insightsCustomStart;
    private DateTime? _insightsCustomEnd;
    private readonly Dictionary<string, List<TranscribedCall>> _insightsWindowCallCache = new Dictionary<string, List<TranscribedCall>>();
    private readonly Dictionary<string, Dictionary<string, List<TranscribedCall>>> _insightsWindowCallIdCache = new Dictionary<string, Dictionary<string, List<TranscribedCall>>>();
    private readonly Dictionary<string, Dictionary<string, List<TranscribedCall>>> _insightsWindowHashCache = new Dictionary<string, Dictionary<string, List<TranscribedCall>>>();
    private const int MaxInsightsTilesPerCategory = 18;
    private const int MaxInsightsEventsPerSummary = 80;
    private string? _activeInsightTranscriptKey;
    private string? _activeInsightAudioKey;
    private readonly Queue<DateTimeOffset> _liveCallArrivalTimes = new Queue<DateTimeOffset>();
    private bool _liveUiRefreshScheduled;
    private int _liveUiRefreshIntervalMs = 150;

    // Real-time progress tracking for insights loading
    public string InsightsProgressMessage => _insightsProgressStatus;
    private string _insightsProgressStatus = "";

    public bool IsInsightsMode => _isInsightsMode;
    public bool IsCallsMode => !_isInsightsMode;
    
    // Insights loading state
    public bool InsightsLoading { get; private set; }
    public string InsightsStatusText
    {
        get => _insightsStatusText;
        set { _insightsStatusText = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(InsightsStatusLineText)); } 
    }
    private string _insightsStatusText = "";
    public string NextInsightStatusText
    {
        get => _nextInsightStatusText;
        set { _nextInsightStatusText = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(InsightsStatusLineText)); }
    }
    private string _nextInsightStatusText = "Insights disabled";
    public string InsightsStatusLineText
    {
        get
        {
            var active = (_insightsStatusText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(active))
                return active;
            return _nextInsightStatusText ?? string.Empty;
        }
    }
    public ObservableCollection<InsightSummaryWindow> InsightsSummaries { get; } = new();
    public ObservableCollection<InsightCategorySection> InsightsCategorySections { get; } = new();
    public ObservableCollection<InsightsNarrativeSection> InsightsNarrativeSections { get; } = new();
    public ObservableCollection<string> InsightsNarrativeBullets { get; } = new();
    public bool IsInsightsNarrativePaneWide
    {
        get => _isInsightsNarrativePaneWide;
        set
        {
            if (_isInsightsNarrativePaneWide == value) return;
            _isInsightsNarrativePaneWide = value;
            InsightsNarrativePaneWidth = _isInsightsNarrativePaneWide
                ? new GridLength(5, GridUnitType.Star)
                : new GridLength(2, GridUnitType.Star);
            InsightsTilesPaneWidth = _isInsightsNarrativePaneWide
                ? new GridLength(5, GridUnitType.Star)
                : new GridLength(8, GridUnitType.Star);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(InsightsSummaryPaneArrow));
            RaisePropertyChanged(nameof(InsightsSummaryVisibilityButtonText));
        }
    }
    public bool IsInsightsNarrativePaneHidden
    {
        get => _isInsightsNarrativePaneHidden;
        set
        {
            if (_isInsightsNarrativePaneHidden == value) return;
            _isInsightsNarrativePaneHidden = value;
            if (_isInsightsNarrativePaneHidden)
            {
                InsightsNarrativePaneWidth = new GridLength(0, GridUnitType.Pixel);
                InsightsTilesPaneWidth = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                InsightsNarrativePaneWidth = IsInsightsNarrativePaneWide
                    ? new GridLength(5, GridUnitType.Star)
                    : new GridLength(2, GridUnitType.Star);
                InsightsTilesPaneWidth = IsInsightsNarrativePaneWide
                    ? new GridLength(5, GridUnitType.Star)
                    : new GridLength(8, GridUnitType.Star);
            }
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsInsightsNarrativePaneVisible));
            RaisePropertyChanged(nameof(InsightsSummaryVisibilityButtonText));
        }
    }
    public bool IsInsightsNarrativePaneVisible => !IsInsightsNarrativePaneHidden;
    public GridLength InsightsNarrativePaneWidth
    {
        get => _insightsNarrativePaneWidth;
        set
        {
            if (_insightsNarrativePaneWidth.Equals(value)) return;
            _insightsNarrativePaneWidth = value;
            RaisePropertyChanged();
        }
    }
    public GridLength InsightsTilesPaneWidth
    {
        get => _insightsTilesPaneWidth;
        set
        {
            if (_insightsTilesPaneWidth.Equals(value)) return;
            _insightsTilesPaneWidth = value;
            RaisePropertyChanged();
        }
    }
    public string InsightsSummaryPaneArrow => IsInsightsNarrativePaneWide ? "←" : "→";
    public string InsightsSummaryVisibilityButtonText => IsInsightsNarrativePaneHidden ? "Show summaries" : "Hide summaries";
    private string _insightsCategoryFilter = "all";
    private bool _resetInsightsPreviewOnNextRefresh;
    private bool _isInsightsNarrativePaneWide;
    private bool _isInsightsNarrativePaneHidden;
    private GridLength _insightsNarrativePaneWidth = new GridLength(2, GridUnitType.Star);
    private GridLength _insightsTilesPaneWidth = new GridLength(8, GridUnitType.Star);
    public string InsightsOverviewText
    {
        get
        {
            var summaries = InsightsSummaries
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.SummaryText))
                .OrderByDescending(s => s.WindowEnd)
                .ToList();

            if (summaries.Count == 0)
                return string.Empty;

            return summaries[0].SummaryText.Trim();
        }
    }

    public bool IsInsightsEnabled => _settings != null 
                                     && _settings.LmLinkEnabled
                                     && !string.IsNullOrWhiteSpace(_settings.LmLinkBaseUrl)
                                     && !string.IsNullOrWhiteSpace(_settings.LmLinkModel);
    public bool IsInsightsCategoryAllActive => string.Equals(_insightsCategoryFilter, "all", StringComparison.OrdinalIgnoreCase);
    public bool IsInsightsCategoryPoliceActive => string.Equals(_insightsCategoryFilter, "police", StringComparison.OrdinalIgnoreCase);
    public bool IsInsightsCategoryFireActive => string.Equals(_insightsCategoryFilter, "fire", StringComparison.OrdinalIgnoreCase);
    public bool IsInsightsCategoryEmsActive => string.Equals(_insightsCategoryFilter, "ems", StringComparison.OrdinalIgnoreCase);
    public bool IsInsightsCategoryTrafficActive => string.Equals(_insightsCategoryFilter, "traffic", StringComparison.OrdinalIgnoreCase);
    public bool IsInsightsCategoryUtilitiesActive => string.Equals(_insightsCategoryFilter, "utilities", StringComparison.OrdinalIgnoreCase);
    public bool IsInsightsCategoryOtherActive => string.Equals(_insightsCategoryFilter, "other", StringComparison.OrdinalIgnoreCase);
    
    public bool IsRangeBarVisible => IsCallsMode || IsInsightsMode;

    
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
            {
                Application.Current.Resources["CurrentFontSize"] = value;
                Application.Current.Resources["InsightsTileFontSize"] = value + 1;
            }
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
    private readonly List<TranscribedCall> _liveCalls = new List<TranscribedCall>();
    public ObservableCollection<TranscribedCall> Calls { get; } = new ObservableCollection<TranscribedCall>();
    public ObservableCollection<CallGroupItem> GroupedCalls { get; } = new ObservableCollection<CallGroupItem>();
    
    // Time range filtering methods
    private void ApplyTimeRangeFilter()
    {
        var now = DateTimeOffset.Now;
        var filteredCalls = new List<TranscribedCall>();

        if (_currentFilter == "24h")
        {
            var start = now.AddHours(-24).LocalDateTime;
            var end = now.LocalDateTime;
            var folderCalls = LoadOfflineCallsFromHistory(start, end);

            var merged = new List<TranscribedCall>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var call in _allCalls)
            {
                var key = GetCallDedupKey(call);
                if (seen.Add(key))
                    merged.Add(call);
            }

            foreach (var call in folderCalls)
            {
                var key = GetCallDedupKey(call);
                if (seen.Add(key))
                    merged.Add(call);
            }

            filteredCalls = merged;
        }
        else
        {
            filteredCalls = _historicalRangeCalls?.ToList() ?? new List<TranscribedCall>();
        }
        
        // Update the Calls collection
        Calls.Clear();
        foreach (var call in filteredCalls)
            Calls.Add(call);
        RaisePropertyChanged(nameof(VisibleCallCount));
        RaisePropertyChanged(nameof(VisibleAlertCount));
            
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
        
        RadioStatusText = _currentFilter switch
        {
            "24h" => $"Last 24 hours ({Calls.Count})",
            "2d" => $"Last 2 days ({Calls.Count})",
            "week" => $"Last week ({Calls.Count})",
            "custom" => _customStartDate.HasValue && _customEndDate.HasValue
                ? $"Custom range: {_customStartDate.Value:d} - {_customEndDate.Value:d} ({Calls.Count})"
                : $"Custom range ({Calls.Count})",
            _ => $"Showing {Calls.Count} calls"
        };
        RaisePropertyChanged(nameof(RadioDataSourceText));
        
        // Update button states
        RaisePropertyChanged(nameof(IsLiveActive));
        RaisePropertyChanged(nameof(IsRadioMode));
        RaisePropertyChanged(nameof(Is24hActive));
        RaisePropertyChanged(nameof(IsRangeLiveActive));
        RaisePropertyChanged(nameof(IsRange24hActive));
        RaisePropertyChanged(nameof(IsRange2dActive));
        RaisePropertyChanged(nameof(Is2dActive));
        RaisePropertyChanged(nameof(IsWeekActive));
        RaisePropertyChanged(nameof(IsRangeActive));
    }
    
    public void SetTimeRangeFilter(string filterType)
    {
        _currentFilter = filterType;

        if (filterType == "24h")
        {
            _historicalRangeCalls = null;
            _customStartDate = null;
            _customEndDate = null;
        }
        else if (filterType == "custom")
        {
            if (!_customStartDate.HasValue)
                _customStartDate = DateTime.Now.AddDays(-7);
            if (!_customEndDate.HasValue)
                _customEndDate = DateTime.Now;
        }
        
        ApplyTimeRangeFilter();
        RaisePropertyChanged(nameof(CurrentFilter));
        RaisePropertyChanged(nameof(IsLiveActive));
        RaisePropertyChanged(nameof(IsRadioMode));
        RaisePropertyChanged(nameof(Is24hActive));
        RaisePropertyChanged(nameof(IsRangeLiveActive));
        RaisePropertyChanged(nameof(IsRange24hActive));
        RaisePropertyChanged(nameof(IsRange2dActive));
RaisePropertyChanged(nameof(IsWeekActive));
        RaisePropertyChanged(nameof(IsRangeWeekActive));
        RaisePropertyChanged(nameof(IsRangeCustomActive));
        RaisePropertyChanged(nameof(IsRangeActive));
        RaisePropertyChanged(nameof(RadioDataSourceText));
    }

    private void ClearVisibleCalls()
    {
        Calls.Clear();
        GroupedCalls.Clear();
        _currentMatchIndex = -1;
        RaisePropertyChanged(nameof(VisibleCallCount));
        RaisePropertyChanged(nameof(VisibleAlertCount));
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
        _isInsightsMode = false;
        RaisePropertyChanged(nameof(IsOfflineMode));
        RaisePropertyChanged(nameof(IsInsightsMode));
        RaisePropertyChanged(nameof(IsCallsMode));
        RaisePropertyChanged(nameof(ModeIndicatorText));
        RaisePropertyChanged(nameof(ModeIndicatorColor));
        RaisePropertyChanged(nameof(IsInsightsEnabled));
        RaisePropertyChanged(nameof(RadioDataSourceText));

        _allCalls.Clear();
        foreach (var call in _liveCalls)
        {
            _allCalls.Add(call);
        }
        ClearVisibleCalls();

        _currentFilter = "24h";
        _historicalRangeCalls = null;
        _customStartDate = null;
        _customEndDate = null;
        _callsReceivedCount = _liveCalls.Count;
        _alertsTriggeredCount = _liveCalls.Count(c => c.IsAlertMatch);
        RaisePropertyChanged(nameof(CallsReceivedCount));
        RaisePropertyChanged(nameof(AlertsTriggeredCount));
        UpdateButtonStates();
        ApplyTimeRangeFilter();

        if (_callManager == null || !_callManager.IsStarted())
        {
            _ = InitializeAsync();
        }

        Title = "PizzaPi";
        RadioStatusText = "Live mode";
    }

private void SwitchToInsightsMode()
    {
        Trace(TraceLoggerType.Insights, TraceEventType.Information, "SwitchToInsightsMode called");

        StopAudio();

        if (_offlineCallManager != null)
        {
            _offlineCallManager.Stop();
            _offlineCallManager.Dispose();
            _offlineCallManager = null;
        }

        _isOfflineMode = false;
        _isInsightsMode = true;
        ShowRadioSubmenu = false;
        RaisePropertyChanged(nameof(IsOfflineMode));
        RaisePropertyChanged(nameof(IsInsightsMode));
        RaisePropertyChanged(nameof(IsCallsMode));
        RaisePropertyChanged(nameof(IsRadioSubmenuVisible));
        RaisePropertyChanged(nameof(IsInsightsSubmenuVisible));
        RaisePropertyChanged(nameof(ModeIndicatorText));
        RaisePropertyChanged(nameof(ModeIndicatorColor));
        RaisePropertyChanged(nameof(IsRadioMode));
        RaisePropertyChanged(nameof(RadioDataSourceText));

        Title = "PizzaPi - Insights";
        StatusText = "Insights mode";

        // Default to the Today insights view when entering insights mode.
        EnsureInsightsServiceStarted();
        _insightsService?.UpdateSettings(_settings);

        SafeUiPost(() =>
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Information, "Posting Today insights load");
            SetInsightsFilter("today");
        });

    }

    private void OnInsightWindowSaved(InsightSummaryWindow window)
    {
        SafeUiPost(() =>
        {
            // Match by actual window identity (start/end), not date only.
            var existing = InsightsSummaries.FirstOrDefault(s => 
                s.WindowStart == window.WindowStart && s.WindowEnd == window.WindowEnd);
            if (existing != null)
            {
                // Update existing entry
                int index = InsightsSummaries.IndexOf(existing);
                InsightsSummaries[index] = FilterTopEvents(window, maxEvents: MaxInsightsEventsPerSummary);
            }
            else
            {
                InsightsSummaries.Add(FilterTopEvents(window, maxEvents: MaxInsightsEventsPerSummary));
            }
            RefreshInsightsCategorySections();
            RefreshNextInsightStatus();
        });
    }

    private void EnsureInsightsServiceStarted()
    {
        if (_insightsService != null)
            return;

        _insightsService = new InsightsService(_settings);
        _insightsService.WindowSaved += OnInsightWindowSaved;
        RefreshNextInsightStatus();
    }

    private void RefreshNextInsightStatus()
    {
        if (!IsInsightsEnabled || _insightsService == null)
        {
            NextInsightStatusText = "Insights disabled";
            return;
        }

        NextInsightStatusText = _insightsService.GetNextSummaryStatusText();
    }

    private async Task LoadInsightsSummariesAsync()
    {
        try
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Information, "LoadInsightsSummariesAsync called");
            
            if (InsightsLoading) return;
            
            InsightsLoading = true;
            SafeUiPost(() =>
            {
                RaisePropertyChanged(nameof(InsightsLoading));
                InsightsStatusText = "Loading insights...";
                Trace(TraceLoggerType.Insights, TraceEventType.Information, "Set InsightsLoading=true and status text");
            });
            Trace(TraceLoggerType.Insights, TraceEventType.Information, $"Current filter: {_insightsFilter}");
            Trace(TraceLoggerType.Insights, TraceEventType.Information, $"Settings null? {_settings == null}, LmLinkEnabled? {_settings?.LmLinkEnabled}, BaseUrl? {_settings?.LmLinkBaseUrl}");
            
            var now = DateTimeOffset.Now;
            DateTimeOffset start, end;
            List<InsightSummaryWindow> summaries = new List<InsightSummaryWindow>();

            if (_insightsFilter == "today")
            {
                var todayStart = new DateTimeOffset(now.Date);
                InsightsSummaries.Clear();
                
                summaries = _insightsStorage.LoadDaily(todayStart, todayStart.AddDays(1));
                InsightsStatusText = $"Loading daily summary for {now:yyyy-MM-dd}...";

                // On some Linux/RPi timezone setups, "today" boundary checks can miss a valid
                // recently generated window summary. Reuse recent summaries before generating.
                if (summaries.Count == 0)
                {
                    var recentStart = now.AddHours(-12);
                    var recent = _insightsStorage.LoadDaily(recentStart, now);
                    if (recent.Count > 0)
                    {
                        summaries = recent
                            .OrderByDescending(s => s.WindowEnd)
                            .Take(3)
                            .ToList();
                        InsightsStatusText = "Loaded recent insights window(s)";
                    }
                }
                
                // If no summary exists and LM Link is enabled, generate it
                if (summaries.Count == 0 && _settings.LmLinkEnabled)
                {
                    // Prevent repeat regeneration loops (observed on some RPi/Linux setups)
                    // when summary files exist but day-window lookup returned empty.
                    if (_insightsStorage.HasRecentSummaryFiles(TimeSpan.FromHours(10)))
                    {
                        Trace(TraceLoggerType.Insights, TraceEventType.Warning,
                            "Recent insights summary files exist; skipping regeneration for Today.");
                        InsightsStatusText = "Recent insights already exist; skipping regeneration";
                    }
                    else
                    {
                    Trace(TraceLoggerType.Insights, TraceEventType.Information, "No daily summary for today, triggering generation");

                    var generationEnd = now;
                    var generationStart = todayStart;
                    var allCalls = LoadOfflineCallsFromHistory(generationStart.DateTime, generationEnd.DateTime);
                    if (allCalls.Count > 0)
                    {
                        SafeUiPost(() =>
                        {
                            InsightsStatusText = $"Generating summary for {generationStart:HH:mm} - {generationEnd:HH:mm}...";
                        });

                        try
                        {
                            var success = await _insightsService.FinalizeWindowAsync(generationStart, generationEnd, allCalls);
                            summaries = _insightsStorage.LoadDaily(todayStart, todayStart.AddDays(1));
                            if (!success && summaries.Count == 0)
                            {
                                var reason = string.IsNullOrWhiteSpace(_insightsService.LastFailureReason)
                                    ? "LM Link returned no usable summary content."
                                    : _insightsService.LastFailureReason!;
                                summaries = new List<InsightSummaryWindow>
                                {
                                    CreateInsightsErrorSummary(reason, generationStart, generationEnd)
                                };
                            }
                            
                            SafeUiPost(() =>
                            {
                                InsightsStatusText = success
                                    ? $"Generated summary for {generationStart:HH:mm} - {generationEnd:HH:mm}"
                                    : $"Generation failed for {generationStart:HH:mm} - {generationEnd:HH:mm}";
                                RaisePropertyChanged(nameof(InsightsStatusText));
                            });
                        }
                        catch (Exception ex)
                        {
                            Trace(TraceLoggerType.Insights, TraceEventType.Error, $"Error generating summary: {ex.Message}");
                            summaries = new List<InsightSummaryWindow>
                            {
                                CreateInsightsErrorSummary(ex.Message, todayStart, todayStart.AddDays(1))
                            };
                            SafeUiPost(() =>
                            {
                                InsightsStatusText = $"Error: {ex.Message}";
                                RaisePropertyChanged(nameof(InsightsStatusText));
                            });
                        }
                    }
                    }
                }
                
                InsightsSummaries.Clear();
                foreach (var s in summaries)
                {
                    var filteredSummary = FilterTopEvents(s, maxEvents: MaxInsightsEventsPerSummary);
                    InsightsSummaries.Add(filteredSummary);
                }
                RefreshInsightsCategorySections();
                return;
            }
            else if (_insightsFilter == "range")
            {
                start = _insightsCustomStart.HasValue ? new DateTimeOffset(_insightsCustomStart.Value) : now.AddHours(-24);
                end = _insightsCustomEnd.HasValue
                    ? new DateTimeOffset(_insightsCustomEnd.Value.AddDays(1))
                    : now;
            }
            else
            {
                start = _insightsFilter switch
                {
                    "24h" => now.AddHours(-24),
                    "2d" => now.AddDays(-2),
                    "week" => now.AddDays(-7),
                    _ => now.AddHours(-24)
                };
                end = now;
            }

            InsightsStatusText = $"Loading insights for {start:MM/dd HH:mm} to {end:MM/dd HH:mm}...";

            summaries = new List<InsightSummaryWindow>();

            Func<List<InsightSummaryWindow>> loadSummariesForFilter = () =>
            {
                if (_insightsFilter == "range")
                    return LoadInsightsForRange(start, end);
                return _insightsStorage.LoadDaily(start, end);
            };

            summaries = loadSummariesForFilter();
            if (_insightsFilter != "range")
                InsightsStatusText = $"Loading {summaries.Count} daily summary(ies)...";

            Trace(TraceLoggerType.Insights, TraceEventType.Information, $"Loaded {summaries.Count} summaries from storage");

            // Historical filters are storage-only lookups.
            // On-demand LLM generation is intentionally limited to "today" mode above.
            
            // Filter to top 20 high-confidence events per summary
            InsightsSummaries.Clear();
            foreach (var s in summaries)
            {
                var filteredSummary = FilterTopEvents(s, maxEvents: MaxInsightsEventsPerSummary);
                InsightsSummaries.Add(filteredSummary);
                _insightsProgressStatus = $"Loaded {InsightsSummaries.Count}/{summaries.Count} summary(ies)";
                InsightsStatusText = _insightsProgressStatus;
            }
            RefreshInsightsCategorySections();

            if (summaries.Count == 0 && !InsightsStatusText.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
            {
                InsightsStatusText = "No daily summaries available for this time range";
            }
        }
        catch (Exception ex)
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Error, $"Insights load failed: {ex.Message}");
            var errorSummary = CreateInsightsErrorSummary(ex.Message, DateTimeOffset.Now, DateTimeOffset.Now);
            SafeUiPost(() =>
            {
                InsightsSummaries.Clear();
                InsightsSummaries.Add(errorSummary);
                InsightsStatusText = $"Error: {ex.Message}";
                RaisePropertyChanged(nameof(InsightsStatusText));
                RefreshInsightsCategorySections();
            });
        }
        finally
        {
            InsightsLoading = false;
            SafeUiPost(() =>
            {
                // Clear transient load/generation text so status line falls back to
                // persistent next-summary status when work is complete.
                if (!InsightsLoading)
                    InsightsStatusText = string.Empty;
                RaisePropertyChanged(nameof(InsightsLoading));
                Trace(TraceLoggerType.Insights, TraceEventType.Information, $"Insights loading complete. Status: {InsightsStatusText}");
            });
        }
    }

    private InsightSummaryWindow FilterTopEvents(InsightSummaryWindow summary, int maxEvents)
    {
        if (summary.NotableEvents == null || summary.NotableEvents.Count <= maxEvents)
        {
            AttachNotableEventParents(summary);
            PopulateNotableEventMetadata(summary);
            return summary;
        }

        var filtered = new InsightSummaryWindow
        {
            WindowStart = summary.WindowStart,
            WindowEnd = summary.WindowEnd,
            SummaryText = summary.SummaryText,
            Model = summary.Model,
            SourceCounts = summary.SourceCounts,
            SourceHashes = summary.SourceHashes,
            Error = summary.Error
        };

        filtered.NotableEvents = summary.NotableEvents
            .OrderByDescending(e => e.HasAlertMatch)
            .ThenByDescending(GetNotableSortKey)
            .ThenByDescending(e => e.Confidence)
            .Take(maxEvents)
            .ToList();
        AttachNotableEventParents(filtered);
        PopulateNotableEventMetadata(filtered);

        return filtered;
    }

    private void RefreshInsightsCategorySections()
    {
        var singleCategoryMode = !string.Equals(_insightsCategoryFilter, "all", StringComparison.OrdinalIgnoreCase);
        var priorStates = InsightsCategorySections
            .GroupBy(s => s.CategoryKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IsCollapsed: g.First().IsCollapsed, ShowAll: g.First().ShowAll),
                StringComparer.OrdinalIgnoreCase);

        var allEvents = InsightsSummaries
            .Where(s => s.NotableEvents != null)
            .SelectMany(s => s.NotableEvents)
            .Where(e => e != null)
            .ToList();

        var grouped = allEvents
            .GroupBy(e => e.CategoryKey)
            .Where(g => string.Equals(_insightsCategoryFilter, "all", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(g.Key, _insightsCategoryFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(g => InsightCategoryPalette.Order(g.Key))
            .Select(g => new InsightCategorySection
            {
                CategoryKey = g.Key,
                DisplayName = InsightCategoryPalette.DisplayName(g.Key),
                Icon = InsightCategoryPalette.Icon(g.Key),
                AccentColor = InsightCategoryPalette.AccentColor(g.Key),
                IsCollapsed = singleCategoryMode
                    ? false
                    : _resetInsightsPreviewOnNextRefresh
                        ? false
                    : priorStates.TryGetValue(g.Key, out var state) && state.IsCollapsed,
                ShowAll = singleCategoryMode
                    ? true
                    : _resetInsightsPreviewOnNextRefresh
                        ? false
                    : priorStates.TryGetValue(g.Key, out state) && state.ShowAll,
                Events = g
                    .OrderByDescending(e => e.HasAlertMatch)
                    .ThenByDescending(GetNotableSortKey)
                    .ThenByDescending(e => e.Confidence)
                    .Take(MaxInsightsTilesPerCategory)
                    .ToList()
            })
            .ToList();

        InsightsCategorySections.Clear();
        foreach (var section in grouped)
            InsightsCategorySections.Add(section);

        _resetInsightsPreviewOnNextRefresh = false;
        BuildInsightsNarrativeSections();
        RaisePropertyChanged(nameof(InsightsOverviewText));
    }

    private void BuildInsightsNarrativeSections()
    {
        var nowDate = DateTimeOffset.Now.Date;
        var relevantSummaries = InsightsSummaries
            .Where(s => s != null)
            .OrderByDescending(s => s.WindowEnd)
            .ToList();

        InsightsNarrativeSections.Clear();
        InsightsNarrativeBullets.Clear();

        if (relevantSummaries.Count == 0)
        {
            InsightsNarrativeSections.Add(new InsightsNarrativeSection
            {
                Title = GetDaySectionTitle(nowDate, nowDate),
                Bullets = new List<InsightsNarrativeBullet>
                {
                    new InsightsNarrativeBullet { Lead = "Info", Text = "No LLM summaries available for selected period." }
                }
            });
            return;
        }

        var groupedByDay = relevantSummaries
            .GroupBy(s => s.WindowStart.ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .ToList();

        foreach (var dayGroup in groupedByDay)
        {
            var section = new InsightsNarrativeSection
            {
                Title = GetDaySectionTitle(dayGroup.Key, nowDate)
            };

            foreach (var summary in dayGroup.OrderByDescending(s => s.WindowEnd))
            {
                var text = string.IsNullOrWhiteSpace(summary.SummaryText)
                    ? "No summary text available."
                    : summary.SummaryText.Trim();
                text = NormalizeNarrativeSummaryText(text);
                section.Bullets.Add(new InsightsNarrativeBullet
                {
                    Lead = GetNarrativeLead(summary),
                    Text = text
                });
            }

            InsightsNarrativeSections.Add(section);
        }
    }

    private static string NormalizeNarrativeSummaryText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"^\s*The radio transcript window\s*\([^)]+\)\s*(contains|captures|includes)\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"^\s*During\s+the\s+[^,]{1,120}\s+window(?:\s+on\s+[^,]{1,80})?\s*,?\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        normalized = System.Text.RegularExpressions.Regex.Replace(
            normalized,
            @"^\s*In\s+the\s+[^,]{1,120}\s+window(?:\s+on\s+[^,]{1,80})?\s*,?\s*",
            "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

        if (string.IsNullOrWhiteSpace(normalized))
            return text.Trim();

        if (char.IsLower(normalized[0]))
            normalized = char.ToUpperInvariant(normalized[0]) + normalized.Substring(1);

        return normalized;
    }

    private static string GetNarrativeLead(InsightSummaryWindow summary)
    {
        var midpoint = summary.WindowStart + TimeSpan.FromTicks((summary.WindowEnd - summary.WindowStart).Ticks / 2);
        var hour = midpoint.ToLocalTime().Hour + (midpoint.ToLocalTime().Minute / 60.0);

        if (hour < 4) return "Overnight";
        if (hour < 7) return "Early morning";
        if (hour < 10) return "In the morning";
        if (hour < 11.5) return "In the late morning";
        if (hour < 13.5) return "Around noon";
        if (hour < 16) return "In the afternoon";
        if (hour < 18) return "In the late afternoon";
        if (hour < 21) return "In the evening";
        return "At night";
    }

    private string GetDaySectionTitle(DateTime day, DateTime nowDate)
    {
        if (day == nowDate)
            return "What happened today";
        if (day == nowDate.AddDays(-1))
            return "What happened yesterday";

        return $"What happened on {day:dddd, MMMM d}";
    }

    private void OnInsightCategoryHeaderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not InsightCategorySection section)
            return;

        section.IsCollapsed = !section.IsCollapsed;
        if (!section.IsCollapsed)
        {
            // Re-expand to preview mode by default.
            section.ShowAll = false;
        }

        RefreshInsightsCategorySections();
    }

    private void OnInsightShowMoreClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not InsightCategorySection section)
            return;

        section.IsCollapsed = false;
        section.ShowAll = true;
        RefreshInsightsCategorySections();
    }

    private void OnInsightShowFewerClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not InsightCategorySection section)
            return;

        section.IsCollapsed = false;
        section.ShowAll = false;
        RefreshInsightsCategorySections();
    }

    private void OnInsightsSummaryPaneArrowClicked(object? sender, RoutedEventArgs e)
    {
        IsInsightsNarrativePaneWide = !IsInsightsNarrativePaneWide;
    }

    private void OnInsightsSummaryVisibilityClicked(object? sender, RoutedEventArgs e)
    {
        IsInsightsNarrativePaneHidden = !IsInsightsNarrativePaneHidden;
    }

    private void SetInsightsCategoryFilter(string filter)
    {
        var wasAll = string.Equals(_insightsCategoryFilter, "all", StringComparison.OrdinalIgnoreCase);
        _insightsCategoryFilter = filter;
        var isAll = string.Equals(_insightsCategoryFilter, "all", StringComparison.OrdinalIgnoreCase);
        if (isAll && !wasAll)
            _resetInsightsPreviewOnNextRefresh = true;
        RefreshInsightsCategorySections();
        RaisePropertyChanged(nameof(IsInsightsCategoryAllActive));
        RaisePropertyChanged(nameof(IsInsightsCategoryPoliceActive));
        RaisePropertyChanged(nameof(IsInsightsCategoryFireActive));
        RaisePropertyChanged(nameof(IsInsightsCategoryEmsActive));
        RaisePropertyChanged(nameof(IsInsightsCategoryTrafficActive));
        RaisePropertyChanged(nameof(IsInsightsCategoryUtilitiesActive));
        RaisePropertyChanged(nameof(IsInsightsCategoryOtherActive));
    }

    private void OnInsightsCategoryAllClicked(object? sender, RoutedEventArgs e) => SetInsightsCategoryFilter("all");
    private void OnInsightsCategoryPoliceClicked(object? sender, RoutedEventArgs e) => SetInsightsCategoryFilter("police");
    private void OnInsightsCategoryFireClicked(object? sender, RoutedEventArgs e) => SetInsightsCategoryFilter("fire");
    private void OnInsightsCategoryEmsClicked(object? sender, RoutedEventArgs e) => SetInsightsCategoryFilter("ems");
    private void OnInsightsCategoryTrafficClicked(object? sender, RoutedEventArgs e) => SetInsightsCategoryFilter("traffic");
    private void OnInsightsCategoryUtilitiesClicked(object? sender, RoutedEventArgs e) => SetInsightsCategoryFilter("utilities");
    private void OnInsightsCategoryOtherClicked(object? sender, RoutedEventArgs e) => SetInsightsCategoryFilter("other");

    private static DateTimeOffset GetNotableSortKey(InsightNotableEvent notable)
    {
        var parent = notable.ParentSummary;
        if (parent == null)
            return DateTimeOffset.MinValue;

        var baseLocal = parent.WindowStart.ToLocalTime();
        if (!string.IsNullOrWhiteSpace(notable.Timestamp))
        {
            var raw = notable.Timestamp.Trim();
            var formats = new[] { "HH:mm", "H:mm", "h:mm tt", "hh:mm tt", "h:mmtt", "hh:mmtt" };
            if (DateTime.TryParseExact(raw, formats, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                var combinedLocal = baseLocal.Date.Add(parsed.TimeOfDay);
                return new DateTimeOffset(combinedLocal, baseLocal.Offset);
            }
        }

        return parent.WindowStart;
    }

    private static void AttachNotableEventParents(InsightSummaryWindow summary)
    {
        if (summary.NotableEvents == null)
            return;

        foreach (var notable in summary.NotableEvents)
        {
            notable.ParentSummary = summary;
        }
    }

    private void PopulateNotableEventMetadata(InsightSummaryWindow summary)
    {
        if (summary.NotableEvents == null)
            return;

        foreach (var notable in summary.NotableEvents)
        {
            var matchedCalls = ResolveCallsForNotable(notable);
            notable.MatchedCallCount = matchedCalls.Count;
            notable.HasAlertMatch = matchedCalls.Any(c => c.IsAlertMatch);
            var best = matchedCalls.OrderByDescending(c => c.StartTime).FirstOrDefault();
            if (best != null)
            {
                // Always anchor tile timestamp to matched transcript call time for consistency.
                notable.Timestamp = DateTimeOffset.FromUnixTimeSeconds(best.StartTime)
                    .ToLocalTime()
                    .ToString("h:mm tt");
            }
        }
    }

    private static InsightSummaryWindow CreateInsightsErrorSummary(string errorMessage, DateTimeOffset start, DateTimeOffset end)
    {
        var message = string.IsNullOrWhiteSpace(errorMessage) ? "Unknown insights error." : errorMessage;
        return new InsightSummaryWindow
        {
            WindowStart = start,
            WindowEnd = end,
            SummaryText = "Unable to generate insights for this range.",
            Error = message,
            Model = "error",
            NotableEvents = new List<InsightNotableEvent>
            {
                new InsightNotableEvent
                {
                    Title = "Insights Error",
                    Detail = message,
                    Confidence = 1.0
                }
            }
        };
    }

    private List<DateTimeOffset> GetDailyWindowsForPeriod(DateTimeOffset start, DateTimeOffset end)
    {
        var windows = new List<DateTimeOffset>();
        var current = new DateTimeOffset(start.Year, start.Month, start.Day, 0, 0, 0, start.Offset);
        
        while (current.Date < end.Date.AddDays(1))
        {
            windows.Add(current);
            current = current.AddDays(1);
        }
        
        return windows;
    }

    private List<InsightSummaryWindow> LoadInsightsForRange(DateTimeOffset start, DateTimeOffset end)
    {
        var summaries = _insightsStorage.LoadDaily(start, end);
        return summaries.Select(s => FilterTopEvents(s, maxEvents: MaxInsightsEventsPerSummary)).ToList();
    }

    private void SwitchToOfflineHistory(DateTime start, DateTime end, string filterLabel)
    {
        StopAudio();
        _isOfflineMode = false;
        _isInsightsMode = false;
        ShowInsightsSubmenu = false;
        RaisePropertyChanged(nameof(IsOfflineMode));
        RaisePropertyChanged(nameof(IsInsightsMode));
        RaisePropertyChanged(nameof(IsCallsMode));
        RaisePropertyChanged(nameof(IsRadioSubmenuVisible));
        RaisePropertyChanged(nameof(IsInsightsSubmenuVisible));
        RaisePropertyChanged(nameof(ModeIndicatorText));
        RaisePropertyChanged(nameof(ModeIndicatorColor));
        RaisePropertyChanged(nameof(IsInsightsEnabled));
        RaisePropertyChanged(nameof(RadioDataSourceText));

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

        _historicalRangeCalls = LoadOfflineCallsFromHistory(start, end);
        foreach (var call in _historicalRangeCalls)
        {
            call.FriendlyTalkgroup = TalkgroupHelper.FormatTalkgroup(_settings, call.Talkgroup);
            call.FriendlyFrequency = FormatFrequency(call.Frequency);
            call.PlayAudioCommand = (c) => PlayAudio(c);
        }

        _callsReceivedCount = _allCalls.Count;
        _alertsTriggeredCount = _allCalls.Count(c => c.IsAlertMatch);
        RaisePropertyChanged(nameof(CallsReceivedCount));
        RaisePropertyChanged(nameof(AlertsTriggeredCount));

        ApplyTimeRangeFilter();
        UpdateButtonStates();

        Title = "PizzaPi - Radio History";
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

                    var key = GetCallDedupKey(call);
                    if (seenKeys.Add(key))
                        loadedCalls.Add(call);
                }
            }
            catch
            {
                // Skip folders with unreadable journals
            }
        }

        return loadedCalls;
    }

    private static string GetCallDedupKey(TranscribedCall call)
    {
        return !string.IsNullOrWhiteSpace(call.Location)
            ? call.Location.Trim()
            : $"{call.StartTime}|{call.StopTime}|{call.Talkgroup}|{call.Frequency}|{(call.Transcription ?? string.Empty).Trim()}";
    }

    private bool ShouldIncludeFolderForRange(string folder, DateTime start, DateTime end)
    {
        try
        {
            var folderName = Path.GetFileName(folder);
            var match = System.Text.RegularExpressions.Regex.Match(folderName, @"^(?<date>\d{4}-\d{2}-\d{2})-(?<time>\d{6})");
            if (!match.Success)
                return false;

            var datePart = match.Groups["date"].Value;
            var timePart = match.Groups["time"].Value;
            if (!DateTime.TryParseExact($"{datePart} {timePart}", "yyyy-MM-dd HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var folderTimestamp))
            {
                return false;
            }

            return folderTimestamp >= start && folderTimestamp <= end;
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
        if (start.HasValue && end.HasValue)
        {
            SwitchToOfflineHistory(start.Value, end.Value.AddDays(1), "custom");
        }
        else
        {
            _currentFilter = "custom";
            ApplyTimeRangeFilter();
            RaisePropertyChanged(nameof(CurrentFilter));
        }
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
        {
            Application.Current.Resources["CurrentFontSize"] = FontSize;
            Application.Current.Resources["InsightsTileFontSize"] = FontSize + 1;
        }

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

        _isClosing = true;
        e.Cancel = true;
        _ = CloseAfterStoppingAsync();
    }
    
    private async Task CloseAfterStoppingAsync()
    {
        try
        {
            await StopUsageTimerAsync().ConfigureAwait(false);
            _insightsService?.Dispose();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Close();
            });
        }
    }

    private void SafeUiPost(Action action)
    {
        if (_isClosing)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing)
                return;

            try
            {
                action();
            }
            catch
            {
                // Swallow UI-update races during teardown.
            }
        });
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
            if (_isInsightsMode)
            {
                _insightsCustomStart = result.Start;
                _insightsCustomEnd = result.End;
                SetInsightsFilter("range");
            }
            else
            {
                SwitchToOfflineHistory(result.Start, result.End, "custom");
            }
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
        if (_activePane == RightPaneType.InsightTranscript)
            _activeInsightTranscriptKey = null;

        _activePane = RightPaneType.None;
        ActivePaneContent = null;
        RaisePaneStateChanged();

        if (reloadSettings)
        {
            _settings = Settings.LoadFromFile(_currentSettingsPath);
            _settingsPanel?.SetSettings(_settings);
            _alertsPanel?.SetSettings(_settings, _currentSettingsPath);
            EnsureInsightsServiceStarted();
            _insightsService?.UpdateSettings(_settings);
            UpdateBellState();
            RaisePropertyChanged(nameof(IsInsightsEnabled));
            RefreshNextInsightStatus();
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
        EnsureInsightsServiceStarted();
        _insightsService?.UpdateSettings(_settings);
        RefreshNextInsightStatus();
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
            _alwaysPinAlertsButton.Content = _alwaysPinAlertMatches ? "[x] Always pin alert matches" : "[ ] Always pin alert matches";
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
            _sortNewestButton.Content = _currentSortMode == 0 ? "[x] Time (newest first)" : "[ ] Time (newest first)";
        if (_sortOldestButton != null)
            _sortOldestButton.Content = _currentSortMode == 1 ? "[x] Time (oldest first)" : "[ ] Time (oldest first)";
        if (_sortTalkgroupButton != null)
            _sortTalkgroupButton.Content = _currentSortMode == 2 ? "[x] Talkgroup (A-Z)" : "[ ] Talkgroup (A-Z)";
    }

    private void UpdateGroupCheckmarks()
    {
        if (_groupNoneButton != null)
            _groupNoneButton.Content = _currentGroupMode == 0 ? "[x] None (flat list)" : "[ ] None (flat list)";
        if (_groupTalkgroupButton != null)
            _groupTalkgroupButton.Content = _currentGroupMode == 1 ? "[x] Talkgroup" : "[ ] Talkgroup";
        if (_groupTimeOfDayButton != null)
            _groupTimeOfDayButton.Content = _currentGroupMode == 2 ? "[x] Time of Day" : "[ ] Time of Day";
        if (_groupSourceButton != null)
            _groupSourceButton.Content = _currentGroupMode == 3 ? "[x] Source" : "[ ] Source";
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
            _alwaysPinAlertsButton.Content = _alwaysPinAlertMatches ? "[x] Always pin alert matches" : "[ ] Always pin alert matches";
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
            SafeUiPost(() => textBox.Focus());
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

        bool shouldExpandAll = _collapsedGroups.Count > 0;   // if anything is collapsed, expand all

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

            RaisePropertyChanged(nameof(IsInsightsEnabled));
            EnsureInsightsServiceStarted();
            _insightsService?.UpdateSettings(_settings);
            RefreshNextInsightStatus();

            // Set trace level from settings
            TraceLogger.SetLevel(_settings.TraceLevelApp);
            pizzalib.TraceLogger.SetLevel(_settings.TraceLevelApp);
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, $"Trace level set to {_settings.TraceLevelApp}");

            // Apply saved sort, group, and font size settings
            _currentSortMode = _settings.SortMode;
            _currentGroupMode = _settings.GroupMode;
            FontSize = _settings.FontSize;

            // Update settings panel with loaded settings
            SafeUiPost(() =>
            {
                _settingsPanel?.SetSettings(_settings);
            });

            _callManager = new LiveCallManager(OnNewCall);

            try
            {
                await _callManager.Initialize(_settings);

                // Update UI on dispatcher thread
                SafeUiPost(() =>
                {
            RadioStatusText = "Ready to receive calls";
                    _serverStatusText = "Server: Running on port " + _settings.ListenPort;
                    ServerInfo = $"Port {_settings.ListenPort}";
                    _connectionStatus = "Connection: Ready";
                    TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, $"Server initialized on port {_settings.ListenPort}");

                    // Prime radio 24h view from persisted capture history at startup.
                    var nowLocal = DateTime.Now;
                    var primeStart = nowLocal.AddHours(-24);
                    var primedCalls = LoadOfflineCallsFromHistory(primeStart, nowLocal);
                    _allCalls.Clear();
                    _liveCalls.Clear();
                    foreach (var call in primedCalls)
                    {
                        call.FriendlyTalkgroup = TalkgroupHelper.FormatTalkgroup(_settings, call.Talkgroup);
                        call.FriendlyFrequency = FormatFrequency(call.Frequency);
                        call.PlayAudioCommand = c => PlayAudio(c);
                        _allCalls.Add(call);
                        _liveCalls.Add(call);
                    }

                    _callsReceivedCount = _allCalls.Count;
                    _alertsTriggeredCount = _allCalls.Count(c => c.IsAlertMatch);
                    RaisePropertyChanged(nameof(CallsReceivedCount));
                    RaisePropertyChanged(nameof(AlertsTriggeredCount));
                    _currentFilter = "24h";
                    _historicalRangeCalls = null;
                    UpdateButtonStates();
                    ApplyTimeRangeFilter();

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
                SafeUiPost(() =>
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
        SafeUiPost(() =>
        {
            // Populate friendly fields
            call.FriendlyTalkgroup = TalkgroupHelper.FormatTalkgroup(_settings, call.Talkgroup);
            call.FriendlyFrequency = FormatFrequency(call.Frequency);

            // Set up the play command for this call
            call.PlayAudioCommand = (c) => PlayAudio(c);

            RadioStatusText = $"New call: {call.FriendlyTalkgroup} @ {call.FriendlyFrequency}";
            CallCountText = $"Calls: {_callsReceivedCount + 1}";
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, 
                $"Call received: {call.FriendlyTalkgroup} @ {call.FriendlyFrequency} ({call.Duration}s)");

            // Insert at correct position: pinned calls at top, then by recency
            int insertIndexAll = GetInsertIndexInList(_allCalls, call);
            _allCalls.Insert(insertIndexAll, call);
            int insertIndexLive = GetInsertIndexInList(_liveCalls, call);
            _liveCalls.Insert(insertIndexLive, call);
            PruneLiveMemoryWindow();
            if (_currentFilter == "24h")
            {
                ScheduleAdaptiveLiveUiRefresh();
            }

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

            // Ingest call into insights service for real-time processing (if insights mode is active)
            _insightsService?.IngestCall(call);
            RefreshNextInsightStatus();
        });
    }

    private void ScheduleAdaptiveLiveUiRefresh()
    {
        var now = DateTimeOffset.Now;
        _liveCallArrivalTimes.Enqueue(now);
        while (_liveCallArrivalTimes.Count > 0 && (now - _liveCallArrivalTimes.Peek()) > TimeSpan.FromSeconds(10))
            _liveCallArrivalTimes.Dequeue();

        var callsPerSecond = _liveCallArrivalTimes.Count / 10.0;
        _liveUiRefreshIntervalMs = callsPerSecond switch
        {
            >= 8 => 2000,
            >= 4 => 1000,
            >= 2 => 500,
            _ => 120
        };

        if (_liveUiRefreshScheduled)
            return;

        _liveUiRefreshScheduled = true;
        DispatcherTimer.RunOnce(() =>
        {
            _liveUiRefreshScheduled = false;
            ApplyTimeRangeFilter();
        }, TimeSpan.FromMilliseconds(_liveUiRefreshIntervalMs));
    }

    private void PruneLiveMemoryWindow()
    {
        var cutoffUnix = DateTimeOffset.Now.AddHours(-24).ToUnixTimeSeconds();

        for (int i = _allCalls.Count - 1; i >= 0; i--)
        {
            if (_allCalls[i].StartTime < cutoffUnix)
                _allCalls.RemoveAt(i);
        }
        _liveCalls.RemoveAll(c => c.StartTime < cutoffUnix);
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
        RepositionCallInList(_liveCalls, call);
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
            var removedFromLive = TrimOldestNonPinned(_liveCalls, _settings.MaxCallsToKeep);
            var removedFromAll = TrimOldestNonPinned(_allCalls, _settings.MaxCallsToKeep);
            if (removedFromLive || removedFromAll)
            {
                ApplyTimeRangeFilter();
            }
        }
    }

    private static bool TrimOldestNonPinned(IList<TranscribedCall> calls, int maxCallsToKeep)
    {
        var removedAny = false;
        var removedPinnedFallback = false;
        while (calls.Count > maxCallsToKeep)
        {
            // Remove oldest non-pinned first.
            var firstNonPinned = -1;
            for (int i = 0; i < calls.Count; i++)
            {
                if (!calls[i].IsPinned)
                {
                    firstNonPinned = i;
                    break;
                }
            }

            if (firstNonPinned >= 0)
            {
                calls.RemoveAt(firstNonPinned);
                removedAny = true;
                continue;
            }

            // Fallback: if all calls are pinned, trim oldest pinned to enforce hard cap.
            calls.RemoveAt(0);
            removedAny = true;
            removedPinnedFallback = true;
        }

        if (removedPinnedFallback)
        {
            TraceLogger.Trace(
                TraceLoggerType.MainWindow,
                TraceEventType.Warning,
                "Retention cap exceeded with all calls pinned; removed oldest pinned calls to enforce MaxCallsToKeep.");
        }

        return removedAny;
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
            SafeUiPost(() =>
            {
                _usageText = usageText;
                RaisePropertyChanged(nameof(UsageText));
            });
        }
        catch (Exception ex)
        {
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Warning,
                $"Error calculating usage: {ex.Message}");
            SafeUiPost(() =>
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

    private string _radioStatusText = "Radio idle";
    public string RadioStatusText
    {
        get { return _radioStatusText; }
        set { _radioStatusText = value; RaisePropertyChanged(); }
    }

    private string _callCountText = "Calls: 0";
    public string CallCountText
    {
        get { return _callCountText; }
        set { _callCountText = value; RaisePropertyChanged(); }
    }

    public int VisibleCallCount => Calls.Count;
    public int VisibleAlertCount => Calls.Count(c => c.IsAlertMatch);

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
        _liveCalls.Clear();
        _insightsWindowCallCache.Clear();
        _insightsWindowCallIdCache.Clear();
        _insightsWindowHashCache.Clear();
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
                
                SafeUiPost(() => HandlePlaybackComplete(call));
            };

            process.Exited += processExited;
            _currentAudioProcess = process;
            process.Start();

            // Set playing flag safely (use .ToList() snapshot)
            SafeUiPost(() =>
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
        SafeUiPost(() =>
        {
            call.IsAudioPlaying = false;
            _activeInsightAudioKey = null;

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

    private static string GetInsightWindowKey(InsightSummaryWindow summary)
    {
        return $"{summary.WindowStart:O}|{summary.WindowEnd:O}";
    }

    private static string GetInsightNotableKey(InsightNotableEvent notable)
    {
        var parent = notable.ParentSummary;
        var windowKey = parent == null
            ? "no_window"
            : GetInsightWindowKey(parent);
        return $"{windowKey}|{notable.Title}|{notable.Timestamp}|{notable.Detail}";
    }

    private bool IsInsightAudioPlaying()
    {
        if (_currentAudioProcess != null)
            return true;
        if (_waveOut != null)
            return true;
        return Calls.Any(c => c.IsAudioPlaying);
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
        if (IsRadioMode)
        {
            ShowRadioSubmenu = !ShowRadioSubmenu;
            ShowInsightsSubmenu = false;
            return;
        }

        SwitchToLiveMode();
        ShowRadioSubmenu = false;
    }

    private void OnInsightsClicked(object? sender, RoutedEventArgs e)
    {
        Trace(TraceLoggerType.Insights, TraceEventType.Information, "OnInsightsClicked fired");
        if (IsInsightsMode)
        {
            ShowInsightsSubmenu = !ShowInsightsSubmenu;
            ShowRadioSubmenu = false;
            return;
        }

        SwitchToInsightsMode();
        ShowInsightsSubmenu = false;
    }

    private void OnInsightsMorningClicked(object? sender, RoutedEventArgs e)
    {
        SetInsightsFilter("morning");
    }

    private void OnInsightsAfternoonClicked(object? sender, RoutedEventArgs e)
    {
        SetInsightsFilter("afternoon");
    }

    private void OnInsightsNightClicked(object? sender, RoutedEventArgs e)
    {
        SetInsightsFilter("night");
    }

    private void OnInsights24hClicked(object? sender, RoutedEventArgs e)
    {
        SetInsightsFilter("24h");
    }

    private void OnInsights2dClicked(object? sender, RoutedEventArgs e)
    {
        SetInsightsFilter("2d");
    }

    private void OnInsightsWeekClicked(object? sender, RoutedEventArgs e)
    {
        SetInsightsFilter("week");
    }

    private void OnInsightsTodayClicked(object? sender, RoutedEventArgs e)
    {
        SetInsightsFilter("today");
    }

    private void SetInsightsFilter(string filterType)
    {
        _insightsFilter = filterType;
        
        // Update button states
        RaisePropertyChanged(nameof(IsInsightsMorningActive));
        RaisePropertyChanged(nameof(IsInsightsAfternoonActive));
        RaisePropertyChanged(nameof(IsInsightsNightActive));
        RaisePropertyChanged(nameof(IsInsightsTodayActive));
        RaisePropertyChanged(nameof(IsInsights24hActive));
        RaisePropertyChanged(nameof(IsInsights2dActive));
        RaisePropertyChanged(nameof(IsInsightsWeekActive));
        RaisePropertyChanged(nameof(IsInsightsRangeActive));

        // Only load if not already loading (prevent duplicate calls)
        if (!InsightsLoading)
        {
            _ = LoadInsightsSummariesAsync();
        }
    }

    private void OnLast24hClicked(object? sender, RoutedEventArgs e)
    {
        _currentFilter = "24h";
        _historicalRangeCalls = null;
        _customStartDate = null;
        _customEndDate = null;
        ApplyTimeRangeFilter();
        UpdateButtonStates();
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

    private void OnRangeLiveClicked(object? sender, RoutedEventArgs e)
    {
        OnRange24hClicked(sender, e);
    }

    private void OnRange24hClicked(object? sender, RoutedEventArgs e)
    {
        _currentFilter = "24h";
        _historicalRangeCalls = null;
        _customStartDate = null;
        _customEndDate = null;
        PruneLiveMemoryWindow();
        ApplyTimeRangeFilter();
        UpdateButtonStates();
    }

    private void OnRange2dClicked(object? sender, RoutedEventArgs e)
    {
        var end = DateTime.Now;
        var start = end.AddHours(-48);
        SwitchToOfflineHistory(start, end, "2d");
    }

    private void OnRangeWeekClicked(object? sender, RoutedEventArgs e)
    {
        var end = DateTime.Now;
        var start = end.AddDays(-7);
        SwitchToOfflineHistory(start, end, "week");
    }

    private void OnPickRangeClicked(object? sender, RoutedEventArgs e)
    {
        TogglePane(RightPaneType.Range);
    }

    private void OnInsightTranscriptClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not InsightNotableEvent notable)
            return;

        var notableKey = GetInsightNotableKey(notable);
        if (_activePane == RightPaneType.InsightTranscript &&
            string.Equals(_activeInsightTranscriptKey, notableKey, StringComparison.Ordinal))
        {
            ClosePane(reloadSettings: false);
            StatusText = "Transcript pane closed.";
            return;
        }

        var calls = ResolveCallsForNotable(notable);
        if (calls.Count == 0)
        {
            StatusText = "No matching calls found for this insight.";
            return;
        }

        _activePane = RightPaneType.InsightTranscript;
        ActivePaneContent = BuildInsightTranscriptPane(notable, calls);
        _activeInsightTranscriptKey = notableKey;
        RaisePaneStateChanged();
    }

    private void OnInsightAudioClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not InsightNotableEvent notable)
            return;

        var notableKey = GetInsightNotableKey(notable);
        if (string.Equals(_activeInsightAudioKey, notableKey, StringComparison.Ordinal) && IsInsightAudioPlaying())
        {
            StopAudio();
            foreach (var c in Calls.ToList())
                c.IsAudioPlaying = false;
            _activeInsightAudioKey = null;
            StatusText = "Audio stopped.";
            return;
        }

        var calls = ResolveCallsForNotable(notable);
        var playable = calls.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.Location) && File.Exists(c.Location));
        if (playable == null)
        {
            StatusText = "Audio not available for this insight.";
            return;
        }

        PlayAudio(playable);
        _activeInsightAudioKey = notableKey;
    }

    private Control BuildInsightTranscriptPane(InsightNotableEvent notable, List<TranscribedCall> calls)
    {
        var titleBlock = new TextBlock
        {
            Text = notable.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold,
            TextWrapping = TextWrapping.Wrap
        };

        var subtitleBlock = new TextBlock
        {
            Text = $"Matched calls: {calls.Count}",
            Foreground = Brushes.Gray
        };

        var transcriptBuilder = new System.Text.StringBuilder();
        foreach (var call in calls.Take(25))
        {
            var localTime = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).ToLocalTime().ToString("g");
            transcriptBuilder.AppendLine($"[{localTime}] TG {call.Talkgroup}");
            transcriptBuilder.AppendLine(call.Transcription ?? string.Empty);
            transcriptBuilder.AppendLine();
        }

        var transcriptBox = new TextBox
        {
            Text = transcriptBuilder.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Background = Brush.Parse("#1f1f1f"),
            Foreground = Brushes.White,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        var closeButton = new Button
        {
            Content = "Close",
            Classes = { "menu-btn" },
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) => ClosePane(reloadSettings: false);

        var layoutGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 10
        };
        Grid.SetRow(titleBlock, 0);
        Grid.SetRow(subtitleBlock, 1);
        Grid.SetRow(transcriptBox, 2);
        Grid.SetRow(closeButton, 3);
        layoutGrid.Children.Add(titleBlock);
        layoutGrid.Children.Add(subtitleBlock);
        layoutGrid.Children.Add(transcriptBox);
        layoutGrid.Children.Add(closeButton);

        return new Border
        {
            Padding = new Thickness(12),
            Child = layoutGrid
        };
    }

    private List<TranscribedCall> ResolveCallsForNotable(InsightNotableEvent notable)
    {
        var summary = notable.ParentSummary;
        if (summary == null)
            return new List<TranscribedCall>();

        var cacheKey = $"{summary.WindowStart:O}|{summary.WindowEnd:O}";
        if (!_insightsWindowCallCache.TryGetValue(cacheKey, out var windowCalls))
        {
            windowCalls = LoadOfflineCallsFromHistory(summary.WindowStart.LocalDateTime, summary.WindowEnd.LocalDateTime);
            foreach (var call in windowCalls)
            {
                call.FriendlyTalkgroup = TalkgroupHelper.FormatTalkgroup(_settings, call.Talkgroup);
                call.FriendlyFrequency = FormatFrequency(call.Frequency);
                call.PlayAudioCommand = c => PlayAudio(c);
            }

            _insightsWindowCallCache[cacheKey] = windowCalls;
            _insightsWindowCallIdCache[cacheKey] = BuildWindowCallIdMap(windowCalls);
            _insightsWindowHashCache[cacheKey] = BuildWindowHashMap(windowCalls);
            if (_insightsWindowCallCache.Count > 24)
            {
                var oldest = _insightsWindowCallCache.Keys.First();
                _insightsWindowCallCache.Remove(oldest);
                _insightsWindowCallIdCache.Remove(oldest);
                _insightsWindowHashCache.Remove(oldest);
            }
        }

        if (!_insightsWindowCallIdCache.TryGetValue(cacheKey, out var callIdMap))
        {
            callIdMap = BuildWindowCallIdMap(windowCalls);
            _insightsWindowCallIdCache[cacheKey] = callIdMap;
        }

        if (!_insightsWindowHashCache.TryGetValue(cacheKey, out var hashMap))
        {
            hashMap = BuildWindowHashMap(windowCalls);
            _insightsWindowHashCache[cacheKey] = hashMap;
        }

        var hasDeclaredCallIds = notable.CallIds != null && notable.CallIds.Count > 0;
        if (hasDeclaredCallIds)
        {
            var callIdSet = new HashSet<string>(notable.CallIds!, StringComparer.OrdinalIgnoreCase);
            var matchedByCallId = callIdSet
                .Where(callIdMap.ContainsKey)
                .SelectMany(id => callIdMap[id])
                .Distinct()
                .OrderByDescending(c => c.StartTime)
                .ToList();
            if (matchedByCallId.Count > 0)
                return matchedByCallId;

            // New summaries must map by call_ids only; do not widen matching if IDs fail.
            return new List<TranscribedCall>();
        }

        var hasDeclaredHashes = notable.CallHashes != null && notable.CallHashes.Count > 0;
        if (hasDeclaredHashes)
        {
            var hashSet = new HashSet<string>(notable.CallHashes!, StringComparer.OrdinalIgnoreCase);
            var matchedByHash = hashSet
                .Where(hashMap.ContainsKey)
                .SelectMany(h => hashMap[h])
                .Distinct()
                .OrderByDescending(c => c.StartTime)
                .ToList();
            if (matchedByHash.Count > 0)
                return matchedByHash;

            // If model provided explicit call hashes but none matched, avoid broad keyword matches
            // that can incorrectly attach unrelated calls.
            return new List<TranscribedCall>();
        }

        var keywords = ExtractNotableKeywords(notable);
        var hasTargetTime = TryGetNotableTargetUnix(notable, out var targetUnix);

        var transcriptCalls = windowCalls
            .Where(c => !string.IsNullOrWhiteSpace(c.Transcription))
            .ToList();

        if (keywords.Count == 0)
        {
            if (hasTargetTime)
            {
                return transcriptCalls
                    .Select(c => new
                    {
                        Call = c,
                        DistanceSeconds = Math.Abs(c.StartTime - targetUnix)
                    })
                    .Where(x => x.DistanceSeconds <= 20 * 60)
                    .OrderBy(x => x.DistanceSeconds)
                    .ThenByDescending(x => x.Call.StartTime)
                    .Select(x => x.Call)
                    .Take(3)
                    .ToList();
            }

            return windowCalls.OrderByDescending(c => c.StartTime).Take(3).ToList();
        }

        var ranked = transcriptCalls
            .Select(c =>
            {
                var matchCount = keywords.Count(k =>
                    c.Transcription!.Contains(k, StringComparison.OrdinalIgnoreCase));
                var distanceSeconds = hasTargetTime
                    ? Math.Abs(c.StartTime - targetUnix)
                    : long.MaxValue;
                return new
                {
                    Call = c,
                    MatchCount = matchCount,
                    DistanceSeconds = distanceSeconds
                };
            })
            .Where(x => x.MatchCount > 0)
            .ToList();

        if (hasTargetTime)
        {
            return ranked
                .Where(x => x.DistanceSeconds <= 30 * 60)
                .OrderByDescending(x => x.MatchCount)
                .ThenBy(x => x.DistanceSeconds)
                .ThenByDescending(x => x.Call.StartTime)
                .Select(x => x.Call)
                .Take(5)
                .ToList();
        }

        return ranked
            .Where(x => x.MatchCount >= 2)
            .OrderByDescending(x => x.MatchCount)
            .ThenByDescending(x => x.Call.StartTime)
            .Select(x => x.Call)
            .Take(5)
            .ToList();
    }

    private static Dictionary<string, List<TranscribedCall>> BuildWindowHashMap(List<TranscribedCall> calls)
    {
        var map = new Dictionary<string, List<TranscribedCall>>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in calls)
        {
            var hash = HashCallForInsight(call);
            if (!map.TryGetValue(hash, out var bucket))
            {
                bucket = new List<TranscribedCall>();
                map[hash] = bucket;
            }
            bucket.Add(call);
        }
        return map;
    }

    private static Dictionary<string, List<TranscribedCall>> BuildWindowCallIdMap(List<TranscribedCall> calls)
    {
        var map = new Dictionary<string, List<TranscribedCall>>(StringComparer.OrdinalIgnoreCase);
        foreach (var call in calls)
        {
            var callId = CallIdForInsight(call);
            if (!map.TryGetValue(callId, out var bucket))
            {
                bucket = new List<TranscribedCall>();
                map[callId] = bucket;
            }
            bucket.Add(call);
        }
        return map;
    }

    private static List<string> ExtractNotableKeywords(InsightNotableEvent notable)
    {
        var text = $"{notable.Title} {notable.Detail}";
        var tokens = text
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ':', ';', '!', '?', '-', '(', ')', '[', ']', '"' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length >= 4)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        return tokens;
    }

    private static string HashCallForInsight(TranscribedCall call)
    {
        return CallHash.Compute(call);
    }

    private static string CallIdForInsight(TranscribedCall call)
    {
        return CallHash.ComputeCallId(call);
    }

    private static bool TryGetNotableTargetUnix(InsightNotableEvent notable, out long targetUnix)
    {
        targetUnix = 0;
        var parent = notable.ParentSummary;
        if (parent == null || string.IsNullOrWhiteSpace(notable.Timestamp))
            return false;

        var raw = notable.Timestamp.Trim();
        var formats = new[] { "HH:mm", "H:mm", "h:mm tt", "hh:mm tt", "h:mmtt", "hh:mmtt" };
        if (!DateTime.TryParseExact(raw, formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return false;

        var startLocal = parent.WindowStart.ToLocalTime();
        var endLocal = parent.WindowEnd.ToLocalTime();
        var candidateLocal = startLocal.Date.Add(parsed.TimeOfDay);

        if (candidateLocal < startLocal.DateTime && endLocal.Date > startLocal.Date)
            candidateLocal = candidateLocal.AddDays(1);

        var candidateOffset = new DateTimeOffset(candidateLocal, startLocal.Offset);
        if (candidateOffset < parent.WindowStart)
            candidateOffset = parent.WindowStart;
        if (candidateOffset > parent.WindowEnd)
            candidateOffset = parent.WindowEnd;

        targetUnix = candidateOffset.ToUnixTimeSeconds();
        return true;
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
        _currentFilter = "24h";
        _customStartDate = null;
        _customEndDate = null;
        
        UpdateButtonStates();
        ApplyTimeRangeFilter();
    }
    
    private void UpdateButtonStates()
    {
        RaisePropertyChanged(nameof(IsLiveActive));
        RaisePropertyChanged(nameof(IsRadioMode));
        RaisePropertyChanged(nameof(IsRadioSubmenuVisible));
        RaisePropertyChanged(nameof(IsInsightsSubmenuVisible));
        RaisePropertyChanged(nameof(Is24hActive));
        RaisePropertyChanged(nameof(IsRangeLiveActive));
        RaisePropertyChanged(nameof(IsRange24hActive));
        RaisePropertyChanged(nameof(IsRange2dActive));
        RaisePropertyChanged(nameof(IsWeekActive));
        RaisePropertyChanged(nameof(IsRangeWeekActive));
        RaisePropertyChanged(nameof(IsRangeCustomActive));
        RaisePropertyChanged(nameof(IsRangeActive));
    }
}

public class StringNotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string text && !string.IsNullOrWhiteSpace(text);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class ConfidenceColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is double confidence)
        {
            if (confidence >= 0.75) return Avalonia.Media.Color.Parse("#00ff00");
            if (confidence >= 0.5) return Avalonia.Media.Color.Parse("#ffff00");
            return Avalonia.Media.Color.Parse("#ff4400");
        }
        return Avalonia.Media.Colors.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

