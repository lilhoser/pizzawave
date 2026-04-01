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
using Avalonia.VisualTree;
using NAudio.Wave;
using Newtonsoft.Json;
using pizzalib;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using static pizzapi.TraceLogger;

namespace pizzapi;

// Wrapper class for grouped call display
public class CallGroupItem
{
    public bool IsHeader { get; set; }
    public string? HeaderText { get; set; }
    public string? GroupKey { get; set; }
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

public sealed class LmUsageLedgerRow
{
    public string Timestamp { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public long TotalTokens { get; set; }
    public double CostStandard { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PromptTokensDisplay => PromptTokens.ToString("N0");
    public string CompletionTokensDisplay => CompletionTokens.ToString("N0");
    public string TotalTokensDisplay => TotalTokens.ToString("N0");
    public string CostStandardDisplay => CostStandard.ToString("F2");
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
    private bool _isAutoplayAlertAudioPlaying = false;
    private bool _openBellMenuOnNextClick;
    private string _bellToolTipText = "Alert audio: Enabled (click to snooze)";
    private Avalonia.Media.SolidColorBrush? _bellColor = null;
    // Legacy submenu button references removed with content-centric shell.
    // Embedded settings tabs content
    private AlertManagerPanel? _alertsPanel;
    private CleanupPanel? _cleanupPanel;
    private readonly TalkgroupMappingStore _talkgroupMappingStore = new();
    private List<TalkgroupMapping> _talkgroupMappings = new();
    private Dictionary<long, TalkgroupMapping> _talkgroupMappingIndex = new();
    private readonly ObservableCollection<TalkgroupMappingRow> _talkgroupMappingRows = new();
    private readonly ObservableCollection<TalkgroupMappingRow> _visibleTalkgroupMappingRows = new();
    private DataGrid? _talkgroupMappingsDataGrid;
    private CheckBox? _talkgroupSelectAllCheckBox;
    private bool _suppressTalkgroupSelectionTracking;
    private bool _shiftPressedDuringTalkgroupCheckboxPress;
    private long? _talkgroupSelectionAnchorId;
    private int _selectedTalkgroupRowCount;
    private static readonly string[] _talkgroupOpsCategoryOptions = { "police", "fire", "ems", "traffic", "other" };
    private static readonly string[] _talkgroupOpsCategoryFilterOptions = { "all", "police", "fire", "ems", "traffic", "other" };
    private string _selectedTalkgroupOpsCategoryFilter = "all";
    private string _talkgroupKeywordFilter = string.Empty;
    private ProcessingProfile? _selectedTalkgroupAddProfile;
    private TalkgroupLiveSnapshot _liveTalkgroupSnapshot = TalkgroupLiveSnapshot.Empty;
    private int _talkgroupLiveSnapshotVersion;
    private bool _isTalkgroupApplyBusy;
    private bool _hasPendingTalkgroupMappingChanges;
    private int _talkgroupRefreshRequestVersion;
    private readonly ObservableCollection<Talkgroup> _wizardTalkgroupDraftRows = new();
    private Talkgroup? _selectedSettingsTalkgroup;
    private string _wizardDraftIdText = string.Empty;
    private string _wizardDraftAlphaTag = string.Empty;
    private string _wizardDraftDescription = string.Empty;
    private string _wizardDraftCategory = string.Empty;
    private string _wizardSourceUrl = string.Empty;
    private string _talkgroupWizardStatusText = string.Empty;
    private string _talkgroupAutoResolveStatusText = string.Empty;
    private bool _talkgroupMappingEnabledInTr = true;
    private bool _talkgroupMappingEnabledInPp = true;
    private string _talkgroupMappingOpsCategory = string.Empty;
    private string _talkgroupMappingNotes = string.Empty;
    
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
    private readonly HashSet<string> _collapsedLegacyGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collapsedCategoryGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenCategoryGroupHeaders = new(StringComparer.OrdinalIgnoreCase);
    // Flag to prevent re-entrancy in collapse/expand
    private bool _isCollapsingExpanding = false;

    // Search bar UI references and state
    private TextBox? _searchTextBox;
    private Button? _clearSearchButton;
    private TextBlock? _searchPlaceholder;
    private DataGrid? _categoryCallsDataGrid;
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
    private readonly LmUsageLedgerStore _lmUsageLedgerStore = new();
    private readonly DateTime _sessionStartedUtc = DateTime.UtcNow;
    private long _sessionTotalTokens;
    private string _lmUsageLedgerSummaryText = "No LM Link usage recorded yet.";

    // Insights mode fields and properties
    private InsightsService? _insightsService;
    private readonly InsightsStorage _insightsStorage = new InsightsStorage();
    private bool _isInsightsMode;
    private bool _showRadioSubmenu;
    private bool _showInsightsSubmenu;
    private long _insightsLoadRequestVersion;
    private bool _pendingInsightsReload;
    private readonly Dictionary<string, List<TranscribedCall>> _insightsWindowCallCache = new Dictionary<string, List<TranscribedCall>>();
    private readonly Dictionary<string, Dictionary<string, List<TranscribedCall>>> _insightsWindowCallIdCache = new Dictionary<string, Dictionary<string, List<TranscribedCall>>>();
    private readonly Dictionary<string, Dictionary<string, List<TranscribedCall>>> _insightsWindowHashCache = new Dictionary<string, Dictionary<string, List<TranscribedCall>>>();
    private readonly Dictionary<string, int> _insightCallNavigateIndex = new(StringComparer.Ordinal);
    private const int MaxInsightsTilesPerCategory = 100;
    private const int MaxInsightsEventsPerSummary = 80;
    private string? _activeInsightAudioKey;
    private readonly Queue<DateTimeOffset> _liveCallArrivalTimes = new Queue<DateTimeOffset>();
    private bool _liveUiRefreshScheduled;
    private int _liveUiRefreshIntervalMs = 150;
    private int _unreadAlertCount;

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
    private bool _isGeneratingInsightsNow;
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
    public bool IsGenerateNowVisible => _currentFilter == "24h" || _currentFilter == "2d";
    public bool CanGenerateNow => IsGenerateNowVisible && !_isGeneratingInsightsNow;
    public bool IsHighlightsEmpty => IsHighlightsSelected && !InsightsLoading && InsightsSummaries.Count == 0;
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

    public bool IsBellAlertState => _settings.AutoplayAlerts && _isAutoplayAlertAudioPlaying && IsInsightAudioPlaying();

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
    public string SessionTokenUsageText => FormatCompactTokenCount(_sessionTotalTokens);
    public string LmUsageLedgerSummaryText
    {
        get => _lmUsageLedgerSummaryText;
        private set { _lmUsageLedgerSummaryText = value; RaisePropertyChanged(); }
    }
    public string LmUsageLedgerPath => _lmUsageLedgerStore.LedgerPath;
    public string LmUsageTierText => "Estimated OpenAI tiers per 1M tokens: budget in/out $0.15/$0.60, standard $2.00/$8.00, premium $5.00/$20.00";
    public ObservableCollection<LmUsageLedgerRow> LmUsageLedgerRows { get; } = new();
    public bool HasLmUsageLedgerRows => LmUsageLedgerRows.Count > 0;
    public string LmUsage24hText { get; private set; } = "24h: tokens 0 | std $0.00";
    public string LmUsage2dText { get; private set; } = "2d: tokens 0 | std $0.00";
    public string LmUsageWeekText { get; private set; } = "Week: tokens 0 | std $0.00";
    public string LmUsageMonthText { get; private set; } = "Month: tokens 0 | std $0.00";

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
    private (DateTime start, DateTime end) GetCurrentRangeBounds()
    {
        var now = DateTimeOffset.Now;
        var start = _currentFilter switch
        {
            "2d" => now.AddDays(-2).LocalDateTime,
            "week" => now.AddDays(-7).LocalDateTime,
            "custom" when _customStartDate.HasValue => _customStartDate.Value,
            _ => now.AddHours(-24).LocalDateTime
        };
        var end = _currentFilter == "custom" && _customEndDate.HasValue
            ? _customEndDate.Value
            : now.LocalDateTime;
        if (_currentFilter == "custom")
        {
            start = NormalizeCustomRangeStart(start);
            end = NormalizeCustomRangeEnd(end);
        }

        return (start, end);
    }

    private void ApplyTimeRangeFilter(List<TranscribedCall>? preloadedFolderCalls = null)
    {
        _insightCallNavigateIndex.Clear();
        var (start, end) = GetCurrentRangeBounds();
        var folderCalls = preloadedFolderCalls ?? LoadOfflineCallsFromHistory(start, end);
        foreach (var call in folderCalls)
        {
            PopulateDerivedCallFields(call);
        }
        var merged = new List<TranscribedCall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var call in _allCalls)
        {
            if (!IsCallInRange(call, start, end))
                continue;
            var key = GetCallDedupKey(call);
            if (seen.Add(key))
                merged.Add(call);
        }

        foreach (var call in folderCalls)
        {
            if (!IsCallInRange(call, start, end))
                continue;
            var key = GetCallDedupKey(call);
            if (seen.Add(key))
                merged.Add(call);
        }

        var filteredCalls = merged
            .Where(ShouldIncludeCallForProfile)
            .ToList();
        
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

        RefreshCategoryGroupedCalls(filteredCalls);
        
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
        RaisePropertyChanged(nameof(IsGenerateNowVisible));
        RaisePropertyChanged(nameof(CanGenerateNow));
        RaisePropertyChanged(nameof(CompositeStatusText));
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
        RaisePropertyChanged(nameof(IsGenerateNowVisible));
        RaisePropertyChanged(nameof(CanGenerateNow));
        RaisePropertyChanged(nameof(RadioDataSourceText));
    }

    private static bool IsCallInRange(TranscribedCall call, DateTime start, DateTime end)
    {
        var local = DateTimeOffset.FromUnixTimeSeconds(call.StartTime).LocalDateTime;
        return local >= start && local <= end;
    }

    private static DateTime NormalizeCustomRangeStart(DateTime start)
    {
        return start.TimeOfDay == TimeSpan.Zero ? start.Date : start;
    }

    private static DateTime NormalizeCustomRangeEnd(DateTime end)
    {
        if (end.TimeOfDay != TimeSpan.Zero)
            return end;

        return end.Date.AddDays(1).AddTicks(-1);
    }

    private bool ShouldIncludeCallForProfile(TranscribedCall call)
    {
        if (_activeProfile == null)
            return true;
        if (_activeProfile.HasTalkgroupScope && !_activeProfile.AllowedTalkgroups.Contains(call.Talkgroup))
            return false;
        var category = ResolveCategoryKeyFromTalkgroup(call.Talkgroup);
        return IsCategoryEnabledForProfile(category);
    }

    private string? GetSelectedCategoryKey()
    {
        if (IsPoliceSelected) return "police";
        if (IsFireSelected) return "fire";
        if (IsEmsSelected) return "ems";
        if (IsTrafficSelected) return "traffic";
        if (IsOtherSelected) return "other";
        return null;
    }

    private string GetInsightsRangeFilterKey() => _currentFilter == "custom" ? "range" : _currentFilter;

    private void RefreshCategoryGroupedCalls(List<TranscribedCall> visibleCalls)
    {
        IEnumerable<TranscribedCall> calls = visibleCalls;
        var selectedCategoryKey = GetSelectedCategoryKey();
        if (!string.IsNullOrWhiteSpace(selectedCategoryKey))
            calls = calls.Where(c => ResolveCategoryKeyFromTalkgroup(c.Talkgroup) == selectedCategoryKey);

        var grouped = calls
            .GroupBy(c => c.FriendlyTalkgroup)
            .OrderBy(g => g.Key);

        CategoryGroupedCalls.Clear();
        foreach (var group in grouped)
        {
            var title = group.Key;
            if (!_seenCategoryGroupHeaders.Contains(title))
            {
                _seenCategoryGroupHeaders.Add(title);
                _collapsedCategoryGroups.Add(title);
            }
            bool isExpanded = !_collapsedCategoryGroups.Contains(title);
            CategoryGroupedCalls.Add(new CallGroupItem
            {
                IsHeader = true,
                GroupKey = title,
                HeaderText = $"{title} ({group.Count()})",
                IsExpanded = isExpanded
            });
            if (!isExpanded)
                continue;
            foreach (var call in group.OrderByDescending(c => c.StartTime))
            {
                CategoryGroupedCalls.Add(new CallGroupItem { IsHeader = false, Call = call, ShowTalkgroup = false });
            }
        }
        RaisePropertyChanged(nameof(IsCategoryRawCallsEmpty));
    }

    private void ClearVisibleCalls()
    {
        Calls.Clear();
        GroupedCalls.Clear();
        _currentMatchIndex = -1;
        RaisePropertyChanged(nameof(VisibleCallCount));
        RaisePropertyChanged(nameof(VisibleAlertCount));
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
            if (IsHighlightsSelected)
                MenuSpecificStatusText = NextInsightStatusText;
            return;
        }

        NextInsightStatusText = _insightsService.GetNextSummaryStatusText();
        if (IsHighlightsSelected)
            MenuSpecificStatusText = NextInsightStatusText;
    }

    private async Task LoadInsightsSummariesAsync()
    {
        var requestVersion = Interlocked.Increment(ref _insightsLoadRequestVersion);
        await Task.Yield();
        try
        {
            Trace(TraceLoggerType.Insights, TraceEventType.Information, "LoadInsightsSummariesAsync called");

            if (InsightsLoading)
            {
                _pendingInsightsReload = true;
                return;
            }

            InsightsLoading = true;
            SafeUiPost(() =>
            {
                RaisePropertyChanged(nameof(InsightsLoading));
                RaisePropertyChanged(nameof(IsHighlightsEmpty));
                InsightsStatusText = "Loading insights...";
                Trace(TraceLoggerType.Insights, TraceEventType.Information, "Set InsightsLoading=true and status text");
            });
            var insightsRangeFilter = GetInsightsRangeFilterKey();
            Trace(TraceLoggerType.Insights, TraceEventType.Information, $"Current filter: {insightsRangeFilter}");
            Trace(TraceLoggerType.Insights, TraceEventType.Information, $"Settings null? {_settings == null}, LmLinkEnabled? {_settings?.LmLinkEnabled}, BaseUrl? {_settings?.LmLinkBaseUrl}");

            var now = DateTimeOffset.Now;
            var start = insightsRangeFilter switch
            {
                "2d" => now.AddDays(-2),
                "week" => now.AddDays(-7),
                "range" => _customStartDate.HasValue ? new DateTimeOffset(_customStartDate.Value) : now.AddHours(-24),
                _ => now.AddHours(-24)
            };
            var end = insightsRangeFilter == "range" && _customEndDate.HasValue
                ? new DateTimeOffset(NormalizeCustomRangeEnd(_customEndDate.Value))
                : now;

            InsightsStatusText = $"Loading insights for {start:MM/dd HH:mm} to {end:MM/dd HH:mm}...";

            var summaries = insightsRangeFilter == "range"
                ? LoadInsightsForRange(start, end)
                : _insightsStorage.LoadDaily(start, end);
            if (insightsRangeFilter != "range")
                InsightsStatusText = $"Loading {summaries.Count} daily summary(ies)...";

            Trace(TraceLoggerType.Insights, TraceEventType.Information, $"Loaded {summaries.Count} summaries from storage");

            if (requestVersion != Volatile.Read(ref _insightsLoadRequestVersion))
                return;

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
            if (requestVersion != Volatile.Read(ref _insightsLoadRequestVersion))
                return;
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
                RaisePropertyChanged(nameof(IsHighlightsEmpty));
                Trace(TraceLoggerType.Insights, TraceEventType.Information, $"Insights loading complete. Status: {InsightsStatusText}");
            });

            var latestVersion = Volatile.Read(ref _insightsLoadRequestVersion);
            if (_pendingInsightsReload || requestVersion < latestVersion)
            {
                _pendingInsightsReload = false;
                _ = LoadInsightsSummariesAsync();
            }
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
                Events = AnnotateDateDividers(g
                    .OrderByDescending(e => e.HasAlertMatch)
                    .ThenByDescending(GetNotableSortKey)
                    .ThenByDescending(e => e.Confidence)
                    .Take(MaxInsightsTilesPerCategory)
                    .ToList())
            })
            .ToList();

        InsightsCategorySections.Clear();
        foreach (var section in grouped)
            InsightsCategorySections.Add(section);

        _resetInsightsPreviewOnNextRefresh = false;
        BuildInsightsNarrativeSections();
        RaisePropertyChanged(nameof(InsightsOverviewText));
        RaisePropertyChanged(nameof(IsCategoryInsightsEmpty));
        RaisePropertyChanged(nameof(IsHighlightsEmpty));
    }

    private static List<InsightNotableEvent> AnnotateDateDividers(List<InsightNotableEvent> events)
    {
        DateTime? lastDate = null;
        foreach (var evt in events)
        {
            var eventLocal = GetNotableSortKey(evt).ToLocalTime().Date;
            if (!lastDate.HasValue || eventLocal != lastDate.Value)
            {
                evt.ShowDateDivider = true;
                evt.DateDividerText = eventLocal.ToString("dddd, MMMM d");
                lastDate = eventLocal;
            }
            else
            {
                evt.ShowDateDivider = false;
                evt.DateDividerText = string.Empty;
            }
        }
        return events;
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

            var normalizedCategory = InsightCategoryPalette.Normalize(notable.Category);
            if (normalizedCategory == "other")
            {
                var derivedCategory = matchedCalls
                    .Select(c => ResolveCategoryKeyFromTalkgroup(c.Talkgroup))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .GroupBy(c => c)
                    .OrderByDescending(g => g.Count())
                    .Select(g => g.Key)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(derivedCategory) &&
                    !string.Equals(derivedCategory, "other", StringComparison.OrdinalIgnoreCase))
                {
                    notable.Category = derivedCategory;
                }
                else
                {
                    var keywordDerived = ResolveCategoryFromKeywords($"{notable.Title} {notable.Detail}");
                    if (!string.Equals(keywordDerived, "other", StringComparison.OrdinalIgnoreCase))
                        notable.Category = keywordDerived;
                }
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
            call.FriendlyTalkgroup = FormatTalkgroupFromLiveSnapshot(call.Talkgroup);
            call.FriendlyFrequency = FormatFrequency(call.Frequency);
            call.PlayAudioCommand = (c) => PlayAudio(c);
        }

        _callsReceivedCount = _allCalls.Count;
        _alertsTriggeredCount = _allCalls.Count(c => c.IsAlertMatch);
        RaisePropertyChanged(nameof(CallsReceivedCount));
        RaisePropertyChanged(nameof(AlertsTriggeredCount));
        RaisePropertyChanged(nameof(FooterCallsText));
        RaisePropertyChanged(nameof(FooterAlertsText));
        RaisePropertyChanged(nameof(CompositeStatusText));

        ApplyTimeRangeFilter(_historicalRangeCalls);
        UpdateButtonStates();

        Title = "PizzaPi - Radio History";
    }

    private List<TranscribedCall> LoadOfflineCallsFromHistory(DateTime start, DateTime end)
    {
        var capturesRoot = Settings.DefaultLiveCaptureDirectory;
        if (!Directory.Exists(capturesRoot))
        {
            return new List<TranscribedCall>();
        }

        var candidateFolders = Directory.EnumerateDirectories(capturesRoot)
            .Where(dir => ShouldIncludeFolderForRange(dir, start, end))
            .ToList();

        if (candidateFolders.Count == 0)
        {
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
                using var stream = new FileStream(
                    journalPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (!reader.EndOfStream)
                {
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    TranscribedCall? call;
                    try { call = Newtonsoft.Json.JsonConvert.DeserializeObject<TranscribedCall>(line); }
                    catch { continue; }

                    if (call == null)
                        continue;
                    if (!ShouldIncludeCallForProfile(call))
                        continue;
                    if (!IsCallInRange(call, start, end))
                        continue;

                    PopulateDerivedCallFields(call);
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

            // Lightweight range inclusion: avoid reading journal files during folder filtering.
            // Include folders that start within a conservative buffered window so downstream
            // per-call time checks can do the precise filtering.
            var bufferedStart = start.AddDays(-1);
            var bufferedEnd = end.AddHours(6);
            return folderTimestamp >= bufferedStart && folderTimestamp <= bufferedEnd;
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
            _currentFilter = "custom";
            ApplyTimeRangeFilter();
            if (IsHighlightsSelected)
                _ = LoadInsightsSummariesAsync();
        }
        else
        {
            _currentFilter = "custom";
            ApplyTimeRangeFilter();
            RaisePropertyChanged(nameof(CurrentFilter));
        }
    }
    private bool _useGrouping => _currentGroupMode > 0;

    private readonly ProfileStore _profileStore = new();
    private readonly AlertHistoryStore _alertHistoryStore = new();
    private MainSection _selectedSection = MainSection.Highlights;
    private ProcessingProfile? _activeProfile;
    private ProcessingProfile? _profileEditorSelectedProfile;
    private string _menuSpecificStatusText = string.Empty;
    private static readonly string[] _troubleshootTraceLevelOptions = { "Error", "Warning", "Information", "Debug" };
    private string _selectedTroubleshootTraceLevel = "Information";
    private const string CurrentPizzaPiLogOption = "Current";
    private bool _isRefreshingPizzaPiLogOptions;
    private string _selectedPizzaPiLogOption = CurrentPizzaPiLogOption;
    private readonly Dictionary<string, string> _pizzaPiLogOptionPathMap = new(StringComparer.OrdinalIgnoreCase);
    public ObservableCollection<ProcessingProfile> Profiles { get; } = new();
    public ObservableCollection<ProcessingProfile> TalkgroupEditableProfiles { get; } = new();
    public ObservableCollection<AlertMatchRecord> AlertHistory { get; } = new();
    public ObservableCollection<string> PizzaPiLogOptions { get; } = new();
    public ObservableCollection<CallGroupItem> CategoryGroupedCalls { get; } = new();
    public ObservableCollection<Talkgroup> TalkgroupRows { get; } = new();
    public ObservableCollection<TalkgroupMappingRow> TalkgroupMappingRows => _talkgroupMappingRows;
    public ObservableCollection<TalkgroupMappingRow> VisibleTalkgroupMappingRows => _visibleTalkgroupMappingRows;
    public IReadOnlyList<string> TalkgroupOpsCategoryOptions => _talkgroupOpsCategoryOptions;
    public IReadOnlyList<string> TalkgroupOpsCategoryFilterOptions => _talkgroupOpsCategoryFilterOptions;
    public ObservableCollection<Talkgroup> WizardTalkgroupDraftRows => _wizardTalkgroupDraftRows;
    public string TalkgroupSummaryText => $"({TalkgroupRows.Count})";
    public string TalkgroupCoverageText =>
        $"Talkgroups: {_talkgroupMappings.Select(m => m.TalkgroupId).Distinct().Count()}";
    public int SelectedTalkgroupRowCount
    {
        get => _selectedTalkgroupRowCount;
        private set
        {
            if (_selectedTalkgroupRowCount == value)
                return;
            _selectedTalkgroupRowCount = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsTalkgroupAddProfileEnabled));
            RaisePropertyChanged(nameof(CanAddSelectedTalkgroups));
        }
    }

    public ProcessingProfile? SelectedTalkgroupAddProfile
    {
        get => _selectedTalkgroupAddProfile;
        set
        {
            if (_selectedTalkgroupAddProfile?.Id == value?.Id)
                return;
            _selectedTalkgroupAddProfile = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CanAddSelectedTalkgroups));
        }
    }

    public bool IsTalkgroupAddProfileEnabled =>
        TalkgroupEditableProfiles.Count > 0 &&
        SelectedTalkgroupRowCount > 0 &&
        !IsTalkgroupEditorBusy;

    public bool CanAddSelectedTalkgroups =>
        IsTalkgroupAddProfileEnabled &&
        SelectedTalkgroupAddProfile != null;

    public string WizardDraftIdText
    {
        get => _wizardDraftIdText;
        set { _wizardDraftIdText = value; RaisePropertyChanged(); }
    }

    public string WizardDraftAlphaTag
    {
        get => _wizardDraftAlphaTag;
        set { _wizardDraftAlphaTag = value; RaisePropertyChanged(); }
    }

    public string WizardDraftDescription
    {
        get => _wizardDraftDescription;
        set { _wizardDraftDescription = value; RaisePropertyChanged(); }
    }

    public string WizardDraftCategory
    {
        get => _wizardDraftCategory;
        set { _wizardDraftCategory = value; RaisePropertyChanged(); }
    }

    public string TalkgroupWizardStatusText
    {
        get => _talkgroupWizardStatusText;
        set { _talkgroupWizardStatusText = value; RaisePropertyChanged(); }
    }

    public string SelectedTalkgroupOpsCategoryFilter
    {
        get => _selectedTalkgroupOpsCategoryFilter;
        set
        {
            // ComboBox can emit transient null/blank values during focus/edit transitions.
            // Ignore those so we do not accidentally reset the active filter to "all".
            if (string.IsNullOrWhiteSpace(value))
                return;

            var normalized = NormalizeOpsCategoryFilter(value);
            if (string.Equals(_selectedTalkgroupOpsCategoryFilter, normalized, StringComparison.Ordinal))
                return;
            _selectedTalkgroupOpsCategoryFilter = normalized;
            RaisePropertyChanged();
            RefreshVisibleTalkgroupRows();
        }
    }

    public string TalkgroupKeywordFilter
    {
        get => _talkgroupKeywordFilter;
        set
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (string.Equals(_talkgroupKeywordFilter, normalized, StringComparison.Ordinal))
                return;
            _talkgroupKeywordFilter = normalized;
            RaisePropertyChanged();
            RefreshVisibleTalkgroupRows();
        }
    }

    public bool IsTalkgroupApplyBusy
    {
        get => _isTalkgroupApplyBusy;
        set
        {
            if (_isTalkgroupApplyBusy == value) return;
            _isTalkgroupApplyBusy = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsTalkgroupEditorBusy));
            RaisePropertyChanged(nameof(IsTalkgroupApplyEnabled));
            RaisePropertyChanged(nameof(IsTalkgroupAddProfileEnabled));
            RaisePropertyChanged(nameof(CanAddSelectedTalkgroups));
        }
    }

    public bool HasPendingTalkgroupMappingChanges
    {
        get => _hasPendingTalkgroupMappingChanges;
        set
        {
            if (_hasPendingTalkgroupMappingChanges == value) return;
            _hasPendingTalkgroupMappingChanges = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsTalkgroupApplyEnabled));
        }
    }

    public bool IsTalkgroupEditorBusy => IsTalkgroupApplyBusy;
    public bool IsTalkgroupApplyEnabled => HasPendingTalkgroupMappingChanges && !IsTalkgroupEditorBusy;

    public string TalkgroupAutoResolveStatusText
    {
        get => _talkgroupAutoResolveStatusText;
        set { _talkgroupAutoResolveStatusText = value; RaisePropertyChanged(); }
    }

    public string WizardSourceUrl
    {
        get => _wizardSourceUrl;
        set { _wizardSourceUrl = value; RaisePropertyChanged(); }
    }

    public Talkgroup? SelectedSettingsTalkgroup
    {
        get => _selectedSettingsTalkgroup;
        set
        {
            if (ReferenceEquals(_selectedSettingsTalkgroup, value))
                return;
            _selectedSettingsTalkgroup = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(SelectedTalkgroupDisplay));
            LoadSelectedTalkgroupMappingIntoEditor();
        }
    }

    public string SelectedTalkgroupDisplay
    {
        get
        {
            if (_selectedSettingsTalkgroup == null)
                return "No talkgroup selected.";
            return $"TG {_selectedSettingsTalkgroup.Id} | {_selectedSettingsTalkgroup.AlphaTag} | {_selectedSettingsTalkgroup.Description}";
        }
    }

    public bool TalkgroupMappingEnabledInTr
    {
        get => _talkgroupMappingEnabledInTr;
        set { _talkgroupMappingEnabledInTr = value; RaisePropertyChanged(); }
    }

    public bool TalkgroupMappingEnabledInPp
    {
        get => _talkgroupMappingEnabledInPp;
        set { _talkgroupMappingEnabledInPp = value; RaisePropertyChanged(); }
    }

    public string TalkgroupMappingOpsCategory
    {
        get => _talkgroupMappingOpsCategory;
        set { _talkgroupMappingOpsCategory = value; RaisePropertyChanged(); }
    }

    public string TalkgroupMappingNotes
    {
        get => _talkgroupMappingNotes;
        set { _talkgroupMappingNotes = value; RaisePropertyChanged(); }
    }

    public MainSection SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (_selectedSection == value)
                return;
            _selectedSection = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(IsHighlightsSelected));
            RaisePropertyChanged(nameof(IsAlertsSelected));
            RaisePropertyChanged(nameof(IsPoliceSelected));
            RaisePropertyChanged(nameof(IsFireSelected));
            RaisePropertyChanged(nameof(IsEmsSelected));
            RaisePropertyChanged(nameof(IsTrafficSelected));
            RaisePropertyChanged(nameof(IsOtherSelected));
            RaisePropertyChanged(nameof(IsSettingsSelected));
            RaisePropertyChanged(nameof(IsTroubleshootSelected));
            RaisePropertyChanged(nameof(IsAnyCategorySelected));
            RaisePropertyChanged(nameof(IsCallsSectionVisible));
            RaisePropertyChanged(nameof(IsInsightsSectionVisible));
            RaisePropertyChanged(nameof(IsAlertsSectionVisible));
            RaisePropertyChanged(nameof(IsSettingsSectionVisible));
            RaisePropertyChanged(nameof(IsTroubleshootSectionVisible));
            RaisePropertyChanged(nameof(IsCategoryInsightsEmpty));
            RaisePropertyChanged(nameof(IsCategoryRawCallsEmpty));
            RaisePropertyChanged(nameof(IsHighlightsEmpty));
            RaisePropertyChanged(nameof(IsAlertsEmpty));
            RaisePropertyChanged(nameof(CompositeStatusText));

            if (value == MainSection.Alerts)
            {
                MarkAlertHistoryRead();
            }
        }
    }

    public bool IsHighlightsSelected => SelectedSection == MainSection.Highlights;
    public bool IsAlertsSelected => SelectedSection == MainSection.Alerts;
    public bool IsPoliceSelected => SelectedSection == MainSection.Police;
    public bool IsFireSelected => SelectedSection == MainSection.Fire;
    public bool IsEmsSelected => SelectedSection == MainSection.EMS;
    public bool IsTrafficSelected => SelectedSection == MainSection.Traffic;
    public bool IsOtherSelected => SelectedSection == MainSection.Other;
    public bool IsSettingsSelected => SelectedSection == MainSection.Settings;
    public bool IsTroubleshootSelected => SelectedSection == MainSection.Troubleshoot;
    public bool IsAnyCategorySelected => IsPoliceSelected || IsFireSelected || IsEmsSelected || IsTrafficSelected || IsOtherSelected;
    public bool IsCallsSectionVisible => IsAnyCategorySelected;
    public bool IsInsightsSectionVisible => IsHighlightsSelected;
    public bool IsAlertsSectionVisible => IsAlertsSelected;
    public bool IsSettingsSectionVisible => IsSettingsSelected;
    public bool IsTroubleshootSectionVisible => IsTroubleshootSelected;
    public bool IsCategoryInsightsEmpty => IsAnyCategorySelected && InsightsCategorySections.Count == 0;
    public bool IsCategoryRawCallsEmpty => IsAnyCategorySelected && CategoryGroupedCalls.Count == 0;
    public bool IsAlertsEmpty => IsAlertsSelected && AlertHistory.Count == 0;

    public string CurrentProfileName => _activeProfile?.Name ?? "Default";
        public Control? AlertsPanelControl => _alertsPanel;
    public Control? CleanupPanelControl => _cleanupPanel;
    public bool IsTrunkTroubleshootVisible => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public string TrunkRecorderLogText { get; private set; } = "Trunk recorder log unavailable.";
    public string PizzaPiLogText { get; private set; } = "PizzaPi log unavailable.";
    public IReadOnlyList<string> TroubleshootTraceLevelOptions => _troubleshootTraceLevelOptions;
    public string SelectedPizzaPiLogOption
    {
        get => _selectedPizzaPiLogOption;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? CurrentPizzaPiLogOption : value.Trim();
            if (string.Equals(_selectedPizzaPiLogOption, normalized, StringComparison.Ordinal))
                return;

            _selectedPizzaPiLogOption = normalized;
            RaisePropertyChanged();
            if (!_isRefreshingPizzaPiLogOptions)
                LoadSelectedPizzaPiLogText();
        }
    }
    public string SelectedTroubleshootTraceLevel
    {
        get => _selectedTroubleshootTraceLevel;
        set
        {
            var normalized = NormalizeTroubleshootTraceLevel(value);
            if (string.Equals(_selectedTroubleshootTraceLevel, normalized, StringComparison.Ordinal))
                return;

            _selectedTroubleshootTraceLevel = normalized;
            RaisePropertyChanged();
            ApplyTroubleshootTraceLevelSelection(normalized);
        }
    }
    public bool IsBellMenuVisible => _settings.AutoplayAlerts && _isAutoplayAlertAudioPlaying && IsInsightAudioPlaying();
    public bool HasTroubleshootIssue { get; private set; }
    public string TroubleshootMenuText => HasTroubleshootIssue ? "Troubleshoot (!)" : "Troubleshoot";

    public ProcessingProfile? ActiveProfile
    {
        get => _activeProfile;
        set
        {
            if (value == null || _activeProfile?.Id == value.Id)
                return;
            _activeProfile = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CurrentProfileName));
            RaisePropertyChanged(nameof(FooterProfileText));
            RefreshMenuVisibility();
            ApplyActiveProfileToManagers();
            ClearSectionCachesForProfileSwitch();
            _profileStore.Save(Profiles.ToList(), _activeProfile.Id);
            EnsureValidSelectedSection();
            ApplyGlobalRangeForSelectedSection();
            if (_profileEditorSelectedProfile == null || !Profiles.Any(p => p.Id == _profileEditorSelectedProfile.Id))
                ProfileEditorSelectedProfile = _activeProfile;
            EnsureTalkgroupAddProfileSelection();
        }
    }

    public ProcessingProfile? ProfileEditorSelectedProfile
    {
        get => _profileEditorSelectedProfile;
        set
        {
            if (_profileEditorSelectedProfile?.Id == value?.Id)
                return;
            _profileEditorSelectedProfile = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(HasProfileEditorSelection));
            RaisePropertyChanged(nameof(CanDeleteSelectedProfile));
            RefreshProfileEditorFromSelectedProfile();
        }
    }

    public bool HasProfileEditorSelection => ProfileEditorSelectedProfile != null;

    public bool CanDeleteSelectedProfile =>
        ProfileEditorSelectedProfile != null &&
        Profiles.Count > 1;

    public bool IsPoliceMenuVisible => _activeProfile?.IncludePolice ?? true;
    public bool IsFireMenuVisible => _activeProfile?.IncludeFire ?? true;
    public bool IsEmsMenuVisible => _activeProfile?.IncludeEMS ?? true;
    public bool IsTrafficMenuVisible => _activeProfile?.IncludeTraffic ?? true;
    public bool IsOtherMenuVisible => _activeProfile?.IncludeOther ?? true;
    public int UnreadAlertCount => _unreadAlertCount;
    public string AlertsMenuText => UnreadAlertCount > 0 ? $"Alerts ({UnreadAlertCount})" : "Alerts";
    public string CompositeStatusText => $"Calls: {CallsReceivedCount}  Alerts: {AlertsTriggeredCount}  Tokens: {SessionTokenUsageText} | {MenuSpecificStatusText}";
    public string FooterProfileText => $"Profile: {CurrentProfileName}";
    public string FooterCallsText => $"Calls: {CallsReceivedCount}";
    public string FooterAlertsText => $"Alerts: {AlertsTriggeredCount}";
    public string FooterTokensText => $"Tokens: {SessionTokenUsageText}";
    public string FooterStatusText => MenuSpecificStatusText;
    private void ApplyMenuStatusText(string value)
    {
        _menuSpecificStatusText = value;
        RaisePropertyChanged(nameof(MenuSpecificStatusText));
        RaisePropertyChanged(nameof(CompositeStatusText));
        RaisePropertyChanged(nameof(FooterStatusText));
    }

    private void SetMenuStatus(string value) => ApplyMenuStatusText(value);

    public string MenuSpecificStatusText
    {
        get => _menuSpecificStatusText;
        set => ApplyMenuStatusText(value);
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _alertsPanel = new AlertManagerPanel(_settings);
        _alertsPanel.RequestClose += (_, _) => { };
        _cleanupPanel = new CleanupPanel();
        _cleanupPanel.RequestClose += (_, _) => { };
        _cleanupPanel.CleanupCompleted += async (_, _) => await OnCleanupCompletedAsync();
        RaisePropertyChanged(nameof(AlertsPanelControl));
        RaisePropertyChanged(nameof(CleanupPanelControl));

        // Get version from assembly metadata (populated by CI from git tag)
        _versionString = GetAssemblyVersion();
        RaisePropertyChanged(nameof(VersionString));
        SyncTroubleshootTraceLevelFromSettings();

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
        _categoryCallsDataGrid = this.FindControl<DataGrid>("CategoryCallsDataGrid");
        _talkgroupMappingsDataGrid = this.FindControl<DataGrid>("TalkgroupMappingsDataGrid");
        _talkgroupSelectAllCheckBox = this.FindControl<CheckBox>("TalkgroupSelectAllCheckBox");
        _visibleTalkgroupMappingRows.CollectionChanged += (_, _) => UpdateTalkgroupSelectionUiState();
        Profiles.CollectionChanged += (_, _) =>
        {
            RefreshTalkgroupEditableProfiles();
            RaisePropertyChanged(nameof(CanDeleteSelectedProfile));
        };
        
// Register converters in Application.Resources for use in XAML bindings
        if (Application.Current != null)
        {
            Application.Current.Resources["IntGreaterThanZeroConverter"] = new IntGreaterThanZeroConverter();
            Application.Current.Resources["SearchHighlightConverter"] = new SearchHighlightConverter();
            Application.Current.Resources["SearchTextForegroundConverter"] = new SearchTextForegroundConverter();
            Application.Current.Resources["AlertSearchBackgroundConverter"] = new AlertSearchBackgroundConverter();
            Application.Current.Resources["InsightNavigateButtonTextConverter"] = new InsightNavigateButtonTextConverter();
        }

        var profileLoad = _profileStore.Load();
        Profiles.Clear();
        foreach (var profile in profileLoad.Profiles)
        {
            Profiles.Add(profile);
        }
        RefreshTalkgroupEditableProfiles();
        ActiveProfile = Profiles.FirstOrDefault(p => p.Id == profileLoad.ActiveProfileId) ?? Profiles.FirstOrDefault();
        ProfileEditorSelectedProfile = ActiveProfile;
        EnsureTalkgroupAddProfileSelection();

        ReloadAlertHistory();
        RefreshMenuVisibility();
        EnsureValidSelectedSection();
        MenuSpecificStatusText = "Ready";
        _isInsightsMode = true;
        LoadTalkgroupMappings();

    }

    private async Task OnCleanupCompletedAsync()
    {
        await UpdateUsageTextAsync();
        ReloadAlertHistory();
        await LoadInsightsSummariesAsync();
    }

    private void RefreshSettingsUiAndPanels()
    {
        RefreshSettingsTabsFromSettings();
        _alertsPanel?.SetSettings(_settings, _currentSettingsPath);
    }

    protected override void OnOpened(EventArgs e)
    {
        // Start initialization in background to avoid blocking UI thread
        _ = InitializeAsync();
        StartUsageTimer();

        base.OnOpened(e);
    }

    private void RefreshSettingsTabsFromSettings()
    {
        RequireControl<TextBox>("SettingsListenPortTextBox").Text = _settings.ListenPort.ToString();
        RequireControl<ComboBox>("SettingsModelComboBox").SelectedIndex = ModelPresetToIndex(_settings.TranscriptionModelPreset);
        RequireControl<ComboBox>("SettingsEngineComboBox").SelectedIndex =
            string.Equals(_settings.TranscriptionEngine, "vosk", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        RequireControl<ComboBox>("SettingsEmailProviderComboBox").SelectedIndex =
            string.Equals(_settings.EmailProvider, "yahoo", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        RequireControl<TextBox>("SettingsEmailUserTextBox").Text = _settings.EmailUser ?? string.Empty;
        RequireControl<TextBox>("SettingsEmailPasswordTextBox").Text = _settings.EmailPassword ?? string.Empty;
        RequireControl<CheckBox>("SettingsAutoplayAlertsCheckBox").IsChecked = _settings.AutoplayAlerts;
        RequireControl<ComboBox>("SettingsSnoozeComboBox").SelectedIndex = _settings.SnoozeDurationMinutes switch
        {
            5 => 0,
            15 => 1,
            30 => 2,
            60 => 3,
            _ => 1
        };
        RequireControl<CheckBox>("SettingsLmLinkEnabledCheckBox").IsChecked = _settings.LmLinkEnabled;
        RequireControl<CheckBox>("SettingsDailyDigestCheckBox").IsChecked = _settings.DailyInsightsDigestEnabled;
        RequireControl<TextBox>("SettingsLmLinkBaseUrlTextBox").Text = _settings.LmLinkBaseUrl ?? string.Empty;
        RequireControl<TextBox>("SettingsLmLinkModelTextBox").Text = _settings.LmLinkModel ?? string.Empty;
        RequireControl<TextBox>("SettingsLmLinkApiKeyTextBox").Text = _settings.LmLinkApiKey ?? string.Empty;
        RequireControl<TextBox>("SettingsLmLinkTimeoutTextBox").Text = _settings.LmLinkTimeoutMs.ToString();
        RequireControl<TextBox>("SettingsLmLinkRetriesTextBox").Text = _settings.LmLinkMaxRetries.ToString();
        LoadTalkgroupMappings();
        RefreshTalkgroupRows();
    }

    private void ApplySettingsTabsToSettings()
    {
        var listenPortTextBox = RequireControl<TextBox>("SettingsListenPortTextBox");
        if (int.TryParse(listenPortTextBox.Text, out var listenPort))
            _settings.ListenPort = listenPort;

        var modelCombo = RequireControl<ComboBox>("SettingsModelComboBox");
        _settings.TranscriptionModelPreset = IndexToModelPreset(modelCombo.SelectedIndex);

        var engineCombo = RequireControl<ComboBox>("SettingsEngineComboBox");
        _settings.TranscriptionEngine = engineCombo.SelectedIndex == 1 ? "vosk" : "whisper";

        var emailProviderCombo = RequireControl<ComboBox>("SettingsEmailProviderComboBox");
        _settings.EmailProvider = emailProviderCombo.SelectedIndex == 1 ? "yahoo" : "gmail";

        var emailUserTextBox = RequireControl<TextBox>("SettingsEmailUserTextBox");
        _settings.EmailUser = emailUserTextBox.Text ?? string.Empty;

        var emailPasswordTextBox = RequireControl<TextBox>("SettingsEmailPasswordTextBox");
        _settings.EmailPassword = emailPasswordTextBox.Text ?? string.Empty;

        var autoplayCheckBox = RequireControl<CheckBox>("SettingsAutoplayAlertsCheckBox");
        _settings.AutoplayAlerts = autoplayCheckBox.IsChecked == true;

        var snoozeCombo = RequireControl<ComboBox>("SettingsSnoozeComboBox");
        _settings.SnoozeDurationMinutes = snoozeCombo.SelectedIndex switch
        {
            0 => 5,
            1 => 15,
            2 => 30,
            3 => 60,
            _ => _settings.SnoozeDurationMinutes
        };

        var lmEnabledCheckBox = RequireControl<CheckBox>("SettingsLmLinkEnabledCheckBox");
        _settings.LmLinkEnabled = lmEnabledCheckBox.IsChecked == true;

        var dailyDigestCheckBox = RequireControl<CheckBox>("SettingsDailyDigestCheckBox");
        _settings.DailyInsightsDigestEnabled = dailyDigestCheckBox.IsChecked == true;

        var lmBaseUrlTextBox = RequireControl<TextBox>("SettingsLmLinkBaseUrlTextBox");
        _settings.LmLinkBaseUrl = lmBaseUrlTextBox.Text ?? string.Empty;

        var lmModelTextBox = RequireControl<TextBox>("SettingsLmLinkModelTextBox");
        _settings.LmLinkModel = lmModelTextBox.Text ?? string.Empty;

        var lmApiKeyTextBox = RequireControl<TextBox>("SettingsLmLinkApiKeyTextBox");
        _settings.LmLinkApiKey = lmApiKeyTextBox.Text ?? string.Empty;

        var lmTimeoutTextBox = RequireControl<TextBox>("SettingsLmLinkTimeoutTextBox");
        if (int.TryParse(lmTimeoutTextBox.Text, out var timeoutMs))
            _settings.LmLinkTimeoutMs = timeoutMs;

        var lmRetriesTextBox = RequireControl<TextBox>("SettingsLmLinkRetriesTextBox");
        if (int.TryParse(lmRetriesTextBox.Text, out var retries))
            _settings.LmLinkMaxRetries = retries;
    }

    private T RequireControl<T>(string name) where T : Control
    {
        var control = this.FindControl<T>(name);
        if (control != null)
            return control;

        var message = $"Required settings control '{name}' ({typeof(T).Name}) was not found in the visual tree.";
        TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Error, message);
        throw new InvalidOperationException(message);
    }

    private void RefreshTalkgroupRows()
    {
        TalkgroupRows.Clear();
        _talkgroupMappingRows.Clear();
        _talkgroupSelectionAnchorId = null;
        foreach (var mapping in _talkgroupMappings
                     .GroupBy(m => m.TalkgroupId)
                     .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
                     .OrderBy(m => m.TalkgroupId))
        {
            var tg = new Talkgroup
            {
                Id = mapping.TalkgroupId,
                Mode = mapping.Mode,
                AlphaTag = mapping.AlphaTag,
                Description = mapping.Description,
                Tag = mapping.Tag,
                Category = mapping.Category
            };
            TalkgroupRows.Add(tg);
            _talkgroupMappingRows.Add(new TalkgroupMappingRow
            {
                Id = tg.Id,
                Mode = tg.Mode ?? string.Empty,
                AlphaTag = tg.AlphaTag ?? string.Empty,
                Description = tg.Description ?? string.Empty,
                Category = tg.Category ?? string.Empty,
                OpsCategory = ResolveOpsCategoryForMapping(mapping),
                OnOpsCategoryChanged = OnTalkgroupMappingRowOpsCategoryChanged,
                OnSelectionChanged = OnTalkgroupMappingRowSelectionChanged
            });
        }
        RefreshVisibleTalkgroupRows();
        UpdateTalkgroupSelectionUiState();
        RaisePropertyChanged(nameof(TalkgroupSummaryText));
        RaisePropertyChanged(nameof(TalkgroupCoverageText));
        RaisePropertyChanged(nameof(TalkgroupMappingRows));
        RaisePropertyChanged(nameof(VisibleTalkgroupMappingRows));
        if (SelectedSettingsTalkgroup != null)
        {
            SelectedSettingsTalkgroup = TalkgroupRows.FirstOrDefault(t => t.Id == SelectedSettingsTalkgroup.Id);
        }
    }

    private void OnTalkgroupMappingRowOpsCategoryChanged(TalkgroupMappingRow row)
    {
        var mapping = _talkgroupMappings.FirstOrDefault(m => m.TalkgroupId == row.Id);
        if (mapping == null)
            return;

        var normalized = NormalizeOpsCategoryChoice(row.OpsCategory);
        if (string.Equals(mapping.OpsCategory, normalized, StringComparison.Ordinal))
            return;

        mapping.OpsCategory = normalized;
        mapping.UpdatedUtc = DateTime.UtcNow;
        row.OpsCategory = normalized;
        HasPendingTalkgroupMappingChanges = true;
        UpdateVisibleTalkgroupRowForFilter(row);
    }

    private void OnTalkgroupMappingRowSelectionChanged(TalkgroupMappingRow _)
    {
        if (_suppressTalkgroupSelectionTracking)
            return;
        UpdateTalkgroupSelectionUiState();
    }

    private void UpdateTalkgroupSelectionUiState()
    {
        var selectedCount = _talkgroupMappingRows.Count(r => r.IsSelectedForProfile);
        SelectedTalkgroupRowCount = selectedCount;

        _talkgroupSelectAllCheckBox ??= this.FindControl<CheckBox>("TalkgroupSelectAllCheckBox");
        if (_talkgroupSelectAllCheckBox == null)
            return;

        var visibleCount = _visibleTalkgroupMappingRows.Count;
        var visibleSelectedCount = _visibleTalkgroupMappingRows.Count(r => r.IsSelectedForProfile);
        bool? headerState = visibleCount == 0
            ? false
            : visibleSelectedCount == 0
                ? false
                : visibleSelectedCount == visibleCount
                    ? true
                    : null;
        _talkgroupSelectAllCheckBox.IsChecked = headerState;
    }

    private void OnTalkgroupMappingsGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTalkgroupSelectionTracking || sender is not DataGrid grid)
            return;

        var selected = new HashSet<long>(
            (grid.SelectedItems ?? new List<object>())
                .OfType<TalkgroupMappingRow>()
                .Select(r => r.Id));

        _suppressTalkgroupSelectionTracking = true;
        try
        {
            foreach (var row in _visibleTalkgroupMappingRows)
                row.IsSelectedForProfile = selected.Contains(row.Id);
        }
        finally
        {
            _suppressTalkgroupSelectionTracking = false;
        }

        var anchorRow = (grid.SelectedItems ?? new List<object>())
            .OfType<TalkgroupMappingRow>()
            .LastOrDefault();
        if (anchorRow != null)
            _talkgroupSelectionAnchorId = anchorRow.Id;
        UpdateTalkgroupSelectionUiState();
    }

    private static bool IsDefaultProfile(ProcessingProfile profile)
    {
        return string.Equals(profile.Name?.Trim(), "Default", StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshTalkgroupEditableProfiles()
    {
        var selectedId = _selectedTalkgroupAddProfile?.Id;
        TalkgroupEditableProfiles.Clear();
        foreach (var profile in Profiles.Where(p => !IsDefaultProfile(p)))
            TalkgroupEditableProfiles.Add(profile);

        if (selectedId.HasValue)
            SelectedTalkgroupAddProfile = TalkgroupEditableProfiles.FirstOrDefault(p => p.Id == selectedId.Value);
        else
            EnsureTalkgroupAddProfileSelection();

        RaisePropertyChanged(nameof(IsTalkgroupAddProfileEnabled));
        RaisePropertyChanged(nameof(CanAddSelectedTalkgroups));
    }

    private void EnsureTalkgroupAddProfileSelection()
    {
        if (_selectedTalkgroupAddProfile != null &&
            TalkgroupEditableProfiles.Any(p => p.Id == _selectedTalkgroupAddProfile.Id))
            return;

        SelectedTalkgroupAddProfile = TalkgroupEditableProfiles.FirstOrDefault();
    }

    private void ApplyTalkgroupRangeSelection(TalkgroupMappingRow pivotRow, bool isChecked)
    {
        if (!_talkgroupSelectionAnchorId.HasValue)
            return;

        var rows = _visibleTalkgroupMappingRows;
        var startIndex = rows
            .Select((row, index) => new { row, index })
            .FirstOrDefault(x => x.row.Id == _talkgroupSelectionAnchorId.Value)?.index ?? -1;
        var endIndex = rows
            .Select((row, index) => new { row, index })
            .FirstOrDefault(x => x.row.Id == pivotRow.Id)?.index ?? -1;

        if (startIndex < 0 || endIndex < 0)
            return;

        var lower = Math.Min(startIndex, endIndex);
        var upper = Math.Max(startIndex, endIndex);
        _suppressTalkgroupSelectionTracking = true;
        try
        {
            for (var i = lower; i <= upper; i++)
                rows[i].IsSelectedForProfile = isChecked;
        }
        finally
        {
            _suppressTalkgroupSelectionTracking = false;
        }
        UpdateTalkgroupSelectionUiState();
    }

    private void RefreshVisibleTalkgroupRows()
    {
        _visibleTalkgroupMappingRows.Clear();
        var categoryFilter = NormalizeOpsCategoryFilter(_selectedTalkgroupOpsCategoryFilter);
        var keywordFilter = _talkgroupKeywordFilter;
        var rows = _talkgroupMappingRows
            .Where(r => RowMatchesTalkgroupFilters(r, categoryFilter, keywordFilter));

        foreach (var row in rows.OrderBy(r => r.Id))
            _visibleTalkgroupMappingRows.Add(row);
    }

    private void UpdateVisibleTalkgroupRowForFilter(TalkgroupMappingRow row)
    {
        var categoryFilter = NormalizeOpsCategoryFilter(_selectedTalkgroupOpsCategoryFilter);
        var keywordFilter = _talkgroupKeywordFilter;

        var isVisible = _visibleTalkgroupMappingRows.Contains(row);
        var matchesFilter = RowMatchesTalkgroupFilters(row, categoryFilter, keywordFilter);

        if (isVisible && !matchesFilter)
        {
            _visibleTalkgroupMappingRows.Remove(row);
            return;
        }

        if (!isVisible && matchesFilter)
        {
            var insertIndex = 0;
            while (insertIndex < _visibleTalkgroupMappingRows.Count &&
                   _visibleTalkgroupMappingRows[insertIndex].Id < row.Id)
            {
                insertIndex++;
            }
            _visibleTalkgroupMappingRows.Insert(insertIndex, row);
        }
    }

    private static bool RowMatchesTalkgroupFilters(
        TalkgroupMappingRow row,
        string categoryFilter,
        string keywordFilter)
    {
        if (!string.Equals(categoryFilter, "all", StringComparison.Ordinal) &&
            !string.Equals(row.OpsCategory, categoryFilter, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(keywordFilter))
            return true;

        var keyword = keywordFilter.Trim();
        return row.Id.ToString().Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || (row.Mode?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.AlphaTag?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.Description?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.Category?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.OpsCategory?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string NormalizeOpsCategoryFilter(string? raw)
    {
        var value = (raw ?? "all").Trim().ToLowerInvariant();
        return _talkgroupOpsCategoryFilterOptions.Contains(value) ? value : "all";
    }

    private void RebuildTalkgroupMappingIndex()
    {
        _talkgroupMappingIndex = _talkgroupMappings
            .GroupBy(m => m.TalkgroupId)
            .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
            .ToDictionary(m => m.TalkgroupId);
        RaisePropertyChanged(nameof(TalkgroupCoverageText));
    }

    private void PublishLiveTalkgroupSnapshotFromStore()
    {
        var live = _talkgroupMappingStore.LoadAll();
        var mappingsById = live
            .GroupBy(m => m.TalkgroupId)
            .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
            .ToDictionary(m => m.TalkgroupId, CloneTalkgroupMapping);
        var snapshot = new TalkgroupLiveSnapshot(
            version: Interlocked.Increment(ref _talkgroupLiveSnapshotVersion),
            mappingsById: mappingsById);
        _liveTalkgroupSnapshot = snapshot;
    }

    private void LoadTalkgroupMappings()
    {
        _talkgroupMappings = _talkgroupMappingStore.LoadAll();
        HasPendingTalkgroupMappingChanges = false;
        RebuildTalkgroupMappingIndex();
        RefreshTalkgroupRows();
    }

    private void LoadSelectedTalkgroupMappingIntoEditor()
    {
        if (SelectedSettingsTalkgroup == null)
        {
            TalkgroupMappingEnabledInTr = true;
            TalkgroupMappingEnabledInPp = true;
            TalkgroupMappingOpsCategory = string.Empty;
            TalkgroupMappingNotes = string.Empty;
            return;
        }

        if (_talkgroupMappingIndex.TryGetValue(SelectedSettingsTalkgroup.Id, out var mapping))
        {
            TalkgroupMappingEnabledInTr = mapping.EnabledInTr;
            TalkgroupMappingEnabledInPp = mapping.EnabledInPp;
            TalkgroupMappingOpsCategory = mapping.OpsCategory;
            TalkgroupMappingNotes = mapping.Notes;
        }
        else
        {
            TalkgroupMappingEnabledInTr = true;
            TalkgroupMappingEnabledInPp = true;
            TalkgroupMappingOpsCategory = string.Empty;
            TalkgroupMappingNotes = string.Empty;
        }
    }

    private void SaveSelectedTalkgroupMapping()
    {
        if (SelectedSettingsTalkgroup == null)
        {
            TalkgroupWizardStatusText = "Select a talkgroup first.";
            return;
        }

        var tgId = SelectedSettingsTalkgroup.Id;
        var mapping = _talkgroupMappings.FirstOrDefault(m => m.TalkgroupId == tgId);
        if (mapping == null)
        {
            mapping = new TalkgroupMapping { TalkgroupId = tgId };
            _talkgroupMappings.Add(mapping);
        }

        mapping.EnabledInTr = TalkgroupMappingEnabledInTr;
        mapping.EnabledInPp = TalkgroupMappingEnabledInPp;
        mapping.OpsCategory = TalkgroupMappingOpsCategory?.Trim() ?? string.Empty;
        mapping.Notes = TalkgroupMappingNotes?.Trim() ?? string.Empty;
        mapping.UpdatedUtc = DateTime.UtcNow;

        RebuildTalkgroupMappingIndex();
        HasPendingTalkgroupMappingChanges = true;
        TalkgroupWizardStatusText = $"Staged mapping update for TG {tgId}. Click Apply to make it live.";
    }

    private void AutoGenerateMappingsFromTalkgroups(
        IEnumerable<Talkgroup> talkgroups,
        bool replaceExistingMappings = false)
    {
        var sourceTalkgroups = (talkgroups ?? Enumerable.Empty<Talkgroup>())
            .Where(t => t != null && t.Id > 0)
            .GroupBy(t => t.Id)
            .Select(g => g.First())
            .ToList();

        var changed = 0;
        if (replaceExistingMappings)
        {
            var keepIds = new HashSet<long>(sourceTalkgroups.Select(t => t.Id));
            var removed = _talkgroupMappings.RemoveAll(m => !keepIds.Contains(m.TalkgroupId));
            changed += removed;
        }

        foreach (var tg in sourceTalkgroups)
        {
            var mapping = _talkgroupMappings.FirstOrDefault(m => m.TalkgroupId == tg.Id);
            if (mapping == null)
            {
                mapping = new TalkgroupMapping
                {
                    TalkgroupId = tg.Id,
                    Mode = tg.Mode ?? string.Empty,
                    AlphaTag = tg.AlphaTag ?? string.Empty,
                    Description = tg.Description ?? string.Empty,
                    Tag = tg.Tag ?? string.Empty,
                    Category = tg.Category ?? string.Empty,
                    EnabledInTr = true,
                    EnabledInPp = true,
                    UpdatedUtc = DateTime.UtcNow
                };
                _talkgroupMappings.Add(mapping);
                changed++;
            }
            else
            {
                var priorMode = mapping.Mode;
                var priorAlpha = mapping.AlphaTag;
                var priorDesc = mapping.Description;
                var priorTag = mapping.Tag;
                var priorCategory = mapping.Category;
                mapping.Mode = tg.Mode ?? string.Empty;
                mapping.AlphaTag = tg.AlphaTag ?? string.Empty;
                mapping.Description = tg.Description ?? string.Empty;
                mapping.Tag = tg.Tag ?? string.Empty;
                mapping.Category = tg.Category ?? string.Empty;
                if (!string.Equals(priorMode, mapping.Mode, StringComparison.Ordinal)
                    || !string.Equals(priorAlpha, mapping.AlphaTag, StringComparison.Ordinal)
                    || !string.Equals(priorDesc, mapping.Description, StringComparison.Ordinal)
                    || !string.Equals(priorTag, mapping.Tag, StringComparison.Ordinal)
                    || !string.Equals(priorCategory, mapping.Category, StringComparison.Ordinal))
                {
                    changed++;
                }
            }

            var derivedOpsCategory = ResolveOpsCategoryForMapping(mapping);
            var normalizedExistingOpsCategory = NormalizeOpsCategoryChoice(mapping.OpsCategory);
            if (string.IsNullOrWhiteSpace(mapping.OpsCategory))
            {
                mapping.OpsCategory = derivedOpsCategory;
                mapping.UpdatedUtc = DateTime.UtcNow;
                changed++;
            }
            else if (!string.Equals(normalizedExistingOpsCategory, mapping.OpsCategory, StringComparison.Ordinal))
            {
                mapping.OpsCategory = normalizedExistingOpsCategory;
                mapping.UpdatedUtc = DateTime.UtcNow;
                changed++;
            }

        }

        if (changed > 0)
        {
            RebuildTalkgroupMappingIndex();
            HasPendingTalkgroupMappingChanges = true;
        }
        else
        {
            RaisePropertyChanged(nameof(TalkgroupCoverageText));
        }
    }

    private static int ModelPresetToIndex(string? preset)
    {
        var value = (preset ?? string.Empty).Trim().ToLowerInvariant();
        return value switch
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

    private static string IndexToModelPreset(int index)
    {
        return index switch
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

    private void ReloadAlertHistory()
    {
        var (start, end) = GetCurrentRangeBounds();
        var startOffset = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
        var endOffset = new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end));
        var startUnix = startOffset.ToUnixTimeSeconds();
        var endUnix = endOffset.ToUnixTimeSeconds();

        AlertHistory.Clear();
        var records = _alertHistoryStore.LoadAll();
        foreach (var item in records)
        {
            if (item.TimestampUnix >= startUnix && item.TimestampUnix <= endUnix)
                AlertHistory.Add(item);
        }
        _unreadAlertCount = records.Count(r => !r.IsRead);
        RaisePropertyChanged(nameof(UnreadAlertCount));
        RaisePropertyChanged(nameof(AlertsMenuText));
        RaisePropertyChanged(nameof(IsAlertsEmpty));
    }

    private void MarkAlertHistoryRead()
    {
        if (_alertHistoryStore.MarkAllRead() > 0)
        {
            ReloadAlertHistory();
        }
    }

    private void AppendAlertHistoryFromCall(TranscribedCall call)
    {
        if (!call.IsAlertMatch)
            return;

        var record = new AlertMatchRecord
        {
            MatchedAtUtc = DateTime.UtcNow,
            CallHash = CallHash.ComputeCallId(call),
            CallId = call.CallId,
            AlertRuleId = call.MatchedAlertRuleId,
            AlertRuleName = call.MatchedAlertRuleName,
            AlertType = string.IsNullOrWhiteSpace(call.MatchedAlertType) ? "keyword" : call.MatchedAlertType,
            TypeDetail = call.MatchedAlertDetail ?? string.Empty,
            Transcription = call.Transcription ?? string.Empty,
            DurationSec = call.Duration,
            TimestampUnix = call.StartTime,
            AudioPath = call.Location ?? string.Empty,
            IsRead = SelectedSection == MainSection.Alerts,
            ReadAtUtc = SelectedSection == MainSection.Alerts ? DateTime.UtcNow : null
        };

        _alertHistoryStore.Append(record);
        var (start, end) = GetCurrentRangeBounds();
        if (IsCallInRange(call, start, end))
            AlertHistory.Insert(0, record);
        if (!record.IsRead)
            _unreadAlertCount++;
        RaisePropertyChanged(nameof(UnreadAlertCount));
        RaisePropertyChanged(nameof(AlertsMenuText));
        RaisePropertyChanged(nameof(IsAlertsEmpty));
        RaisePropertyChanged(nameof(CompositeStatusText));
    }

    private void RefreshMenuVisibility()
    {
        RaisePropertyChanged(nameof(IsPoliceMenuVisible));
        RaisePropertyChanged(nameof(IsFireMenuVisible));
        RaisePropertyChanged(nameof(IsEmsMenuVisible));
        RaisePropertyChanged(nameof(IsTrafficMenuVisible));
        RaisePropertyChanged(nameof(IsOtherMenuVisible));
        RaisePropertyChanged(nameof(CurrentProfileName));
        RaisePropertyChanged(nameof(FooterProfileText));
    }

    private void RefreshProfileEditorFromSelectedProfile()
    {
        if (_profileEditorSelectedProfile == null)
            return;

        this.FindControl<CheckBox>("ProfileIncludePoliceCheckBox")?.SetCurrentValue(ToggleButton.IsCheckedProperty, _profileEditorSelectedProfile.IncludePolice);
        this.FindControl<CheckBox>("ProfileIncludeFireCheckBox")?.SetCurrentValue(ToggleButton.IsCheckedProperty, _profileEditorSelectedProfile.IncludeFire);
        this.FindControl<CheckBox>("ProfileIncludeEmsCheckBox")?.SetCurrentValue(ToggleButton.IsCheckedProperty, _profileEditorSelectedProfile.IncludeEMS);
        this.FindControl<CheckBox>("ProfileIncludeTrafficCheckBox")?.SetCurrentValue(ToggleButton.IsCheckedProperty, _profileEditorSelectedProfile.IncludeTraffic);
        this.FindControl<CheckBox>("ProfileIncludeOtherCheckBox")?.SetCurrentValue(ToggleButton.IsCheckedProperty, _profileEditorSelectedProfile.IncludeOther);
        this.FindControl<TextBox>("ProfileTalkgroupsTextBox")?.SetCurrentValue(TextBox.TextProperty,
            string.Join(",", _profileEditorSelectedProfile.AllowedTalkgroups));
    }

    private void EnsureValidSelectedSection()
    {
        if (SelectedSection == MainSection.Police && !IsPoliceMenuVisible) SelectedSection = MainSection.Highlights;
        if (SelectedSection == MainSection.Fire && !IsFireMenuVisible) SelectedSection = MainSection.Highlights;
        if (SelectedSection == MainSection.EMS && !IsEmsMenuVisible) SelectedSection = MainSection.Highlights;
        if (SelectedSection == MainSection.Traffic && !IsTrafficMenuVisible) SelectedSection = MainSection.Highlights;
        if (SelectedSection == MainSection.Other && !IsOtherMenuVisible) SelectedSection = MainSection.Highlights;
    }

    private void ClearSectionCachesForProfileSwitch()
    {
        _insightsWindowCallCache.Clear();
        _insightsWindowCallIdCache.Clear();
        _insightsWindowHashCache.Clear();
        Calls.Clear();
        GroupedCalls.Clear();
        CategoryGroupedCalls.Clear();
    }

    private void ApplyActiveProfileToManagers()
    {
        Func<RawCallData, bool> rawFilter = raw =>
        {
            if (_activeProfile == null)
                return true;

            try
            {
                var json = raw.GetJsonObject();
                var tg = json["Talkgroup"]?.ToObject<long>() ?? 0;
                if (_activeProfile.HasTalkgroupScope && !_activeProfile.AllowedTalkgroups.Contains(tg))
                    return false;

                var category = ResolveCategoryKeyFromTalkgroup(tg);
                return IsCategoryEnabledForProfile(category);
            }
            catch
            {
                return true;
            }
        };

        Func<TranscribedCall, bool> callFilter = call =>
        {
            if (_activeProfile == null)
                return true;
            if (_activeProfile.HasTalkgroupScope && !_activeProfile.AllowedTalkgroups.Contains(call.Talkgroup))
                return false;
            var category = ResolveCategoryKeyFromTalkgroup(call.Talkgroup);
            return IsCategoryEnabledForProfile(category);
        };

        _callManager?.SetRawCallFilter(rawFilter);
        _callManager?.SetTranscribedCallFilter(callFilter);
        _offlineCallManager?.SetRawCallFilter(rawFilter);
        _offlineCallManager?.SetTranscribedCallFilter(callFilter);
    }

    private bool IsCategoryEnabledForProfile(string categoryKey)
    {
        if (_activeProfile == null)
            return true;

        return categoryKey switch
        {
            "police" => _activeProfile.IncludePolice,
            "fire" => _activeProfile.IncludeFire,
            "ems" => _activeProfile.IncludeEMS,
            "traffic" => _activeProfile.IncludeTraffic,
            _ => _activeProfile.IncludeOther
        };
    }

    private void PublishLiveTalkgroupSnapshotFromStaged()
    {
        var mappingsById = _talkgroupMappings
            .GroupBy(m => m.TalkgroupId)
            .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
            .ToDictionary(m => m.TalkgroupId, CloneTalkgroupMapping);

        var snapshot = new TalkgroupLiveSnapshot(
            version: Interlocked.Increment(ref _talkgroupLiveSnapshotVersion),
            mappingsById: mappingsById);
        _liveTalkgroupSnapshot = snapshot;
    }

    private static TalkgroupMapping CloneTalkgroupMapping(TalkgroupMapping source)
    {
        return new TalkgroupMapping
        {
            TalkgroupId = source.TalkgroupId,
            Mode = source.Mode ?? string.Empty,
            AlphaTag = source.AlphaTag ?? string.Empty,
            Description = source.Description ?? string.Empty,
            Tag = source.Tag ?? string.Empty,
            Category = source.Category ?? string.Empty,
            EnabledInTr = source.EnabledInTr,
            EnabledInPp = source.EnabledInPp,
            OpsCategory = source.OpsCategory ?? string.Empty,
            Notes = source.Notes ?? string.Empty,
            UpdatedUtc = source.UpdatedUtc
        };
    }

    private string FormatTalkgroupFromLiveSnapshot(long talkgroupId)
    {
        var snapshot = _liveTalkgroupSnapshot;
        if (!snapshot.MappingsById.TryGetValue(talkgroupId, out var mapping))
            return $"{talkgroupId}";
        return $"{mapping.AlphaTag} - {mapping.Description} ({mapping.Category})";
    }

    private void PopulateDerivedCallFields(TranscribedCall call)
    {
        call.FriendlyTalkgroup = FormatTalkgroupFromLiveSnapshot(call.Talkgroup);
        call.FriendlyFrequency = FormatFrequency(call.Frequency);
        call.PlayAudioCommand = c => PlayAudio(c);
    }

    private void RefreshDerivedFieldsForLoadedCalls()
    {
        foreach (var call in _allCalls)
            PopulateDerivedCallFields(call);
        foreach (var call in _liveCalls)
            PopulateDerivedCallFields(call);
        foreach (var call in Calls)
            PopulateDerivedCallFields(call);
        if (_historicalRangeCalls != null)
        {
            foreach (var call in _historicalRangeCalls)
                PopulateDerivedCallFields(call);
        }
    }

    private string ResolveCategoryKeyFromTalkgroup(long talkgroupId)
    {
        var snapshot = _liveTalkgroupSnapshot;
        if (!snapshot.MappingsById.TryGetValue(talkgroupId, out var mapping) || mapping == null)
            return "other";
        return NormalizeOpsCategoryChoice(mapping.OpsCategory);
    }

    private static string ResolveOpsCategoryForMapping(TalkgroupMapping? mapping)
    {
        if (mapping == null)
            return "other";

        if (!string.IsNullOrWhiteSpace(mapping.OpsCategory))
            return NormalizeOpsCategoryChoice(mapping.OpsCategory);

        var category = InsightCategoryPalette.Normalize(mapping.Category);
        if (category != "other")
            return category;

        category = InsightCategoryPalette.Normalize(mapping.Tag);
        if (category != "other")
            return category;

        var searchText = string.Join(" ",
            mapping.Category ?? string.Empty,
            mapping.Tag ?? string.Empty,
            mapping.AlphaTag ?? string.Empty,
            mapping.Description ?? string.Empty);

        return NormalizeOpsCategoryChoice(ResolveCategoryFromKeywords(searchText));
    }

    private static string NormalizeOpsCategoryChoice(string? raw)
    {
        var canonical = InsightCategoryPalette.Normalize(raw) switch
        {
            "police" => "police",
            "fire" => "fire",
            "ems" => "ems",
            "traffic" => "traffic",
            _ => "other"
        };

        if (canonical != "other")
            return canonical;

        var fromKeywords = ResolveCategoryFromKeywords(raw ?? string.Empty);
        return fromKeywords switch
        {
            "police" => "police",
            "fire" => "fire",
            "ems" => "ems",
            "traffic" => "traffic",
            _ => "other"
        };
    }

    private static string ResolveCategoryFromKeywords(string raw)
    {
        var text = (raw ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
            return "other";

        static bool HasWord(string source, string token) =>
            System.Text.RegularExpressions.Regex.IsMatch(source, $@"\b{System.Text.RegularExpressions.Regex.Escape(token)}\b");

        if (text.Contains("police") || text.Contains("sheriff") || text.Contains("law") || HasWord(text, "pd"))
            return "police";
        if (text.Contains("fire") || HasWord(text, "fd"))
            return "fire";
        if (text.Contains("ems") || text.Contains("medical") || text.Contains("medic") || text.Contains("ambulance") || text.Contains("hospital"))
            return "ems";
        if (text.Contains("traffic")
            || text.Contains("accident")
            || text.Contains("crash")
            || text.Contains("road")
            || text.Contains("highway")
            || text.Contains("hwy")
            || text.Contains("highway patrol")
            || text.Contains("hwy patrol")
            || text.Contains("state patrol")
            || text.Contains("state highway patrol")
            || text.Contains("state hwy patrol")
            || HasWord(text, "dot"))
            return "traffic";

        return "other";
    }

    private void ApplyGlobalRangeForSelectedSection()
    {
        switch (_currentFilter)
        {
            case "24h":
                OnRange24hClicked(this, new RoutedEventArgs());
                break;
            case "2d":
                OnRange2dClicked(this, new RoutedEventArgs());
                break;
            case "week":
                OnRangeWeekClicked(this, new RoutedEventArgs());
                break;
            case "custom":
                OnPickRangeClicked(this, new RoutedEventArgs());
                break;
            default:
                OnRange24hClicked(this, new RoutedEventArgs());
                break;
        }
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
        SyncTroubleshootTraceLevelFromSettings();

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
            ApplyActiveProfileToManagers();
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
        // No-op: legacy always-pin submenu button was removed.
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
        // No-op: legacy sort submenu buttons were removed.
    }

    private void UpdateGroupCheckmarks()
    {
        // No-op: legacy group-by submenu buttons were removed.
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
        if (sender is Button button && button.DataContext is CallGroupItem header)
        {
            var groupKey = header.GroupKey ?? header.HeaderText;
            if (string.IsNullOrWhiteSpace(groupKey))
                return;

            var collapsedSet = IsAnyCategorySelected ? _collapsedCategoryGroups : _collapsedLegacyGroups;
            if (collapsedSet.Contains(groupKey))
                collapsedSet.Remove(groupKey);   // expand
            else
                collapsedSet.Add(groupKey);      // collapse

            if (IsAnyCategorySelected)
            {
                ApplyTimeRangeFilter();
            }
            else
            {
                ApplyGrouping();
            }
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

        bool shouldExpandAll = _collapsedLegacyGroups.Count > 0;   // if anything is collapsed, expand all

            if (shouldExpandAll)
                _collapsedLegacyGroups.Clear();           // expand everything
            else
            {
                // collapse everything
                _collapsedLegacyGroups.Clear();
                foreach (var item in GroupedCalls.Where(i => i.IsHeader && i.HeaderText != null))
                    _collapsedLegacyGroups.Add(item.HeaderText!);
            }

            ApplyGrouping();
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
            var groupKey = group.Key ?? string.Empty;
            bool isExpanded = !_collapsedLegacyGroups.Contains(groupKey);
            result.Add(new CallGroupItem { IsHeader = true, HeaderText = group.Key, GroupKey = groupKey, IsExpanded = isExpanded });
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
            var groupKey = group.Key ?? string.Empty;
            bool isExpanded = !_collapsedLegacyGroups.Contains(groupKey);
            result.Add(new CallGroupItem { IsHeader = true, HeaderText = group.Key, GroupKey = groupKey, IsExpanded = isExpanded });
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
            var groupKey = group.Key ?? string.Empty;
            bool isExpanded = !_collapsedLegacyGroups.Contains(groupKey);
            result.Add(new CallGroupItem { IsHeader = true, HeaderText = group.Key, GroupKey = groupKey, IsExpanded = isExpanded });
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

            // Settings are already loaded in constructor, just validate/load defaults
            try
            {
                _settings = Settings.LoadFromFile(_currentSettingsPath);
                TraceLogger.SetLevel(_settings.TraceLevelApp);
                pizzalib.TraceLogger.SetLevel(_settings.TraceLevelApp);
                PublishLiveTalkgroupSnapshotFromStore();
                TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, "PizzaPi UI starting...");
                TraceLogger.Trace(TraceLoggerType.Settings, TraceEventType.Information, $"Settings loaded from file");
                SafeUiPost(() =>
                {
                    RefreshSettingsUiAndPanels();
                    SyncTroubleshootTraceLevelFromSettings();
                });
            }
            catch (Exception ex)
            {
                // Use default settings
                _settings = new Settings();
                TraceLogger.SetLevel(_settings.TraceLevelApp);
                pizzalib.TraceLogger.SetLevel(_settings.TraceLevelApp);
                PublishLiveTalkgroupSnapshotFromStore();
                TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, "PizzaPi UI starting...");
                TraceLogger.Trace(TraceLoggerType.Settings, TraceEventType.Warning,
                    $"Using default settings: {ex.Message}");
                SafeUiPost(() =>
                {
                    RefreshSettingsUiAndPanels();
                    SyncTroubleshootTraceLevelFromSettings();
                });
            }

            RaisePropertyChanged(nameof(IsInsightsEnabled));
            EnsureInsightsServiceStarted();
            _insightsService?.UpdateSettings(_settings);
            RefreshNextInsightStatus();

            // Set trace level from settings
            TraceLogger.SetLevel(_settings.TraceLevelApp);
            pizzalib.TraceLogger.SetLevel(_settings.TraceLevelApp);
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Information, $"Trace level set to {_settings.TraceLevelApp}");
            SafeUiPost(SyncTroubleshootTraceLevelFromSettings);

            // Apply saved sort, group, and font size settings
            _currentSortMode = _settings.SortMode;
            _currentGroupMode = _settings.GroupMode;
            FontSize = _settings.FontSize;

            // Update settings-backed UI panels with loaded settings
            SafeUiPost(RefreshSettingsUiAndPanels);

            _callManager = new LiveCallManager(OnNewCall);
            ApplyActiveProfileToManagers();

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
                        call.FriendlyTalkgroup = FormatTalkgroupFromLiveSnapshot(call.Talkgroup);
                        call.FriendlyFrequency = FormatFrequency(call.Frequency);
                        call.PlayAudioCommand = c => PlayAudio(c);
                        _allCalls.Add(call);
                        _liveCalls.Add(call);
                    }

                    _callsReceivedCount = _allCalls.Count;
                    _alertsTriggeredCount = _allCalls.Count(c => c.IsAlertMatch);
                    RaisePropertyChanged(nameof(CallsReceivedCount));
                    RaisePropertyChanged(nameof(AlertsTriggeredCount));
                    RaisePropertyChanged(nameof(FooterCallsText));
                    RaisePropertyChanged(nameof(FooterAlertsText));
                    RaisePropertyChanged(nameof(CompositeStatusText));
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
            call.FriendlyTalkgroup = FormatTalkgroupFromLiveSnapshot(call.Talkgroup);
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
                PlayAudio(call, autoplayAlertPlayback: true);
            }

            _callsReceivedCount++;
            RaisePropertyChanged(nameof(CallsReceivedCount));
            RaisePropertyChanged(nameof(FooterCallsText));
            RaisePropertyChanged(nameof(CompositeStatusText));
            RaisePropertyChanged(nameof(CallCountText));

            // Increment alert counter if this call matched an alert
            if (call.IsAlertMatch)
            {
                _alertsTriggeredCount++;
                RaisePropertyChanged(nameof(AlertsTriggeredCount));
                RaisePropertyChanged(nameof(FooterAlertsText));
                RaisePropertyChanged(nameof(CompositeStatusText));
                TraceLogger.Trace(TraceLoggerType.Alerts, TraceEventType.Information, 
                    $"Alert match #{_alertsTriggeredCount}: {call.FriendlyTalkgroup}");
                AppendAlertHistoryFromCall(call);
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
            if (call.IsAlertMatch)
                MenuSpecificStatusText = $"Alert match: {call.MatchedAlertType} {call.MatchedAlertDetail}".Trim();
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
            var entries = await Task.Run(() => _lmUsageLedgerStore.LoadAll());
            var sessionTokens = entries
                .Where(e => e.TimestampUtc >= _sessionStartedUtc)
                .Sum(e => (long)(e.TotalTokens > 0 ? e.TotalTokens : e.PromptTokens + e.CompletionTokens));
            var ledgerSummary = BuildLmUsageLedgerSummary(entries);
            var ledgerRows = BuildLmUsageLedgerRows(entries);
            var aggregate24h = BuildAggregatePeriodText("24h", entries, DateTime.UtcNow.AddHours(-24));
            var aggregate2d = BuildAggregatePeriodText("2d", entries, DateTime.UtcNow.AddDays(-2));
            var aggregateWeek = BuildAggregatePeriodText("Week", entries, DateTime.UtcNow.AddDays(-7));
            var aggregateMonth = BuildAggregatePeriodText("Month", entries, DateTime.UtcNow.AddDays(-30));
            SafeUiPost(() =>
            {
                _usageText = usageText;
                _sessionTotalTokens = sessionTokens;
                LmUsageLedgerSummaryText = ledgerSummary;
                LmUsage24hText = aggregate24h;
                LmUsage2dText = aggregate2d;
                LmUsageWeekText = aggregateWeek;
                LmUsageMonthText = aggregateMonth;
                LmUsageLedgerRows.Clear();
                foreach (var row in ledgerRows)
                    LmUsageLedgerRows.Add(row);
                RaisePropertyChanged(nameof(UsageText));
                RaisePropertyChanged(nameof(SessionTokenUsageText));
                RaisePropertyChanged(nameof(FooterTokensText));
                RaisePropertyChanged(nameof(LmUsageLedgerPath));
                RaisePropertyChanged(nameof(LmUsageTierText));
                RaisePropertyChanged(nameof(LmUsage24hText));
                RaisePropertyChanged(nameof(LmUsage2dText));
                RaisePropertyChanged(nameof(LmUsageWeekText));
                RaisePropertyChanged(nameof(LmUsageMonthText));
                RaisePropertyChanged(nameof(HasLmUsageLedgerRows));
                RaisePropertyChanged(nameof(CompositeStatusText));
            });
        }
        catch (Exception ex)
        {
            TraceLogger.Trace(TraceLoggerType.MainWindow, TraceEventType.Warning,
                $"Error calculating usage: {ex.Message}");
            SafeUiPost(() =>
            {
                _usageText = "Usage: ?mb";
                _sessionTotalTokens = 0;
                LmUsageLedgerSummaryText = "LM usage stats unavailable.";
                LmUsage24hText = "24h: unavailable";
                LmUsage2dText = "2d: unavailable";
                LmUsageWeekText = "Week: unavailable";
                LmUsageMonthText = "Month: unavailable";
                LmUsageLedgerRows.Clear();
                RaisePropertyChanged(nameof(UsageText));
                RaisePropertyChanged(nameof(SessionTokenUsageText));
                RaisePropertyChanged(nameof(FooterTokensText));
                RaisePropertyChanged(nameof(LmUsage24hText));
                RaisePropertyChanged(nameof(LmUsage2dText));
                RaisePropertyChanged(nameof(LmUsageWeekText));
                RaisePropertyChanged(nameof(LmUsageMonthText));
                RaisePropertyChanged(nameof(HasLmUsageLedgerRows));
                RaisePropertyChanged(nameof(CompositeStatusText));
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
                await Task.Delay(TimeSpan.FromSeconds(30), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (token.IsCancellationRequested)
                break;

            await UpdateUsageTextAsync();
            LoadTroubleshootLogs();
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

    private string BuildLmUsageLedgerSummary(List<LmUsageLedgerEntry> entries)
    {
        if (entries.Count == 0)
            return "No LM Link usage recorded yet.";
        var totals = SummarizeBucket(entries);
        return $"Entries: {entries.Count}, tokens: {totals.TotalTokens:N0}";
    }

    private static string NormalizeTriggerBucket(string? trigger)
    {
        var value = (trigger ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Contains("manual", StringComparison.Ordinal))
            return "manual insights";
        if (value.Contains("periodic", StringComparison.Ordinal) || value.Contains("auto", StringComparison.Ordinal))
            return "auto insights";
        return "other";
    }

    private static (int Requests, int Successes, int Failures, long InputChars, long PromptTokens, long CompletionTokens, long TotalTokens) SummarizeBucket(IEnumerable<LmUsageLedgerEntry> source)
    {
        var list = source.ToList();
        return (
            Requests: list.Count,
            Successes: list.Count(x => x.Success),
            Failures: list.Count(x => !x.Success),
            InputChars: list.Sum(x => (long)x.InputChars),
            PromptTokens: list.Sum(x => (long)x.PromptTokens),
            CompletionTokens: list.Sum(x => (long)x.CompletionTokens),
            TotalTokens: list.Sum(x => (long)(x.TotalTokens > 0 ? x.TotalTokens : x.PromptTokens + x.CompletionTokens))
        );
    }

    private static string BuildAggregatePeriodText(string label, List<LmUsageLedgerEntry> entries, DateTime startUtc)
    {
        var scope = entries.Where(e => e.TimestampUtc >= startUtc).ToList();
        var m = SummarizeBucket(scope);
        var budget = EstimateCost(m.PromptTokens, m.CompletionTokens, 0.15, 0.60);
        var standard = EstimateCost(m.PromptTokens, m.CompletionTokens, 2.00, 8.00);
        var premium = EstimateCost(m.PromptTokens, m.CompletionTokens, 5.00, 20.00);
        return $"{label}: {m.TotalTokens:N0} tokens  |  ${standard:F2} std  (${budget:F2} budget / ${premium:F2} premium)";
    }

    private List<LmUsageLedgerRow> BuildLmUsageLedgerRows(List<LmUsageLedgerEntry> entries)
    {
        return entries
            .OrderByDescending(e => e.TimestampUtc)
            .Take(500)
            .Select(e => new LmUsageLedgerRow
            {
                Timestamp = e.TimestampUtc.ToLocalTime().ToString("g"),
                Trigger = string.IsNullOrWhiteSpace(e.TriggerActivity) ? "other" : e.TriggerActivity,
                PromptTokens = e.PromptTokens,
                CompletionTokens = e.CompletionTokens,
                TotalTokens = e.TotalTokens > 0 ? e.TotalTokens : e.PromptTokens + e.CompletionTokens,
                CostStandard = EstimateCost(e.PromptTokens, e.CompletionTokens, 2.00, 8.00),
                Status = e.Success ? "ok" : "fail"
            })
            .ToList();
    }

    private static double EstimateCost(long promptTokens, long completionTokens, double inputPerMillion, double outputPerMillion)
    {
        var inputCost = (promptTokens / 1_000_000.0) * inputPerMillion;
        var outputCost = (completionTokens / 1_000_000.0) * outputPerMillion;
        return inputCost + outputCost;
    }

    private static string FormatCompactTokenCount(long tokens)
    {
        if (tokens >= 1_000_000_000)
            return $"{tokens / 1_000_000_000d:0.#}b";
        if (tokens >= 1_000_000)
            return $"{tokens / 1_000_000d:0.#}m";
        if (tokens >= 1_000)
            return $"{tokens / 1_000d:0.#}k";
        return tokens.ToString("N0");
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
        set
        {
            _statusText = value;
            RaisePropertyChanged();
            SetMenuStatus(value);
        }
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
        set
        {
            _callsReceivedCount = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CompositeStatusText));
            RaisePropertyChanged(nameof(FooterCallsText));
        }
    }

    public int AlertsTriggeredCount
    {
        get { return _alertsTriggeredCount; }
        set
        {
            _alertsTriggeredCount = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(CompositeStatusText));
            RaisePropertyChanged(nameof(FooterAlertsText));
        }
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
    }

    private void HideMenu()
    {
    }

    private void OnBellClicked(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        OnBellMenuClicked(sender, new RoutedEventArgs());
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
        else if (IsBellAlertState)
        {
            _bellToolTipText = "Alert audio: ACTIVE alert playing (click for snooze options)";
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
        bool isAlertPlaying = IsBellAlertState;
        bool isSnoozed = _isAlertSnoozed;
        string colorHex = !alertsEnabled
            ? "#8a8a8a"   // Gray outline when disabled
            : isAlertPlaying
                ? "#ff6b6b" // Red accent when active alert is playing
                : isSnoozed
                    ? "#c2a36a" // Muted amber when snoozed
                    : "#9fb38a"; // Muted green when enabled

        _bellColor = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));

        // Raise property changed notifications
        RaisePropertyChanged(nameof(IsAlertSnoozed));
        RaisePropertyChanged(nameof(BellToolTipText));
        RaisePropertyChanged(nameof(BellColor));
        RaisePropertyChanged(nameof(IsBellAlertState));
        RaisePropertyChanged(nameof(IsBellMenuVisible));
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
            })
        });

        menu.Open(this);
    }

    private void OnRefreshClicked(object? sender, RoutedEventArgs e)
    {
        Refresh();
        HideMenu();
    }

    private void PlayAudio(TranscribedCall newCall, bool autoplayAlertPlayback = false)
    {
        StopAudio(); // always stop anything currently playing first
        _openBellMenuOnNextClick = false;

        var audioFilePath = newCall.Location;
        if (string.IsNullOrEmpty(audioFilePath) || !File.Exists(audioFilePath))
        {
            StatusText = "Error: Audio file not found";
            return;
        }

        _isAutoplayAlertAudioPlaying = _settings.AutoplayAlerts && (autoplayAlertPlayback || newCall.IsAlertMatch);
        UpdateBellState();

        if (_isLinux)
        {
            PlayAudioLinux(audioFilePath, newCall);
            return;
        }

        // === Windows path ===
        try
        {
            // Reset all playing flags first
            foreach (var c in _allCalls.ToList())
            {
                c.IsAudioPlaying = (c.UniqueId == newCall.UniqueId);
            }
            foreach (var c in _liveCalls.ToList())
            {
                c.IsAudioPlaying = (c.UniqueId == newCall.UniqueId);
            }
            foreach (var c in Calls.ToList())   // visible subset
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
            UpdateBellState();
        }
        catch (Exception ex)
        {
            _isAutoplayAlertAudioPlaying = false;
            UpdateBellState();
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

        foreach (var c in _allCalls.ToList())
            c.IsAudioPlaying = false;
        foreach (var c in _liveCalls.ToList())
            c.IsAudioPlaying = false;
        foreach (var c in Calls.ToList())
            c.IsAudioPlaying = false;

        _isAutoplayAlertAudioPlaying = false;
        SetActiveAlertHistoryAudio(null);
        UpdateBellState();
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
                foreach (var c in _allCalls.ToList())
                {
                    c.IsAudioPlaying = (c.UniqueId == call.UniqueId);
                }
                foreach (var c in _liveCalls.ToList())
                {
                    c.IsAudioPlaying = (c.UniqueId == call.UniqueId);
                }
                foreach (var c in Calls.ToList())
                {
                    c.IsAudioPlaying = (c.UniqueId == call.UniqueId);
                }
                UpdateBellState();
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Error starting ffplay: {ex.Message}\n\nInstall ffmpeg with:\nsudo apt install ffmpeg";
            process?.Dispose();
            _currentAudioProcess = null;
            _isAutoplayAlertAudioPlaying = false;
            UpdateBellState();
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
    private void OnHighlightsClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Highlights;
        _isInsightsMode = true;
        _isOfflineMode = false;
        SetInsightsCategoryFilter("all");
        _ = LoadInsightsSummariesAsync();
        MenuSpecificStatusText = NextInsightStatusText;
    }

    private void OnSectionPoliceClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Police;
        _isInsightsMode = false;
        SetInsightsCategoryFilter("police");
        RefreshCategoryGroupedCalls(Calls.ToList());
        MenuSpecificStatusText = "Police activity";
    }

    private void OnSectionFireClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Fire;
        _isInsightsMode = false;
        SetInsightsCategoryFilter("fire");
        RefreshCategoryGroupedCalls(Calls.ToList());
        MenuSpecificStatusText = "Fire activity";
    }

    private void OnSectionEmsClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.EMS;
        _isInsightsMode = false;
        SetInsightsCategoryFilter("ems");
        RefreshCategoryGroupedCalls(Calls.ToList());
        MenuSpecificStatusText = "EMS activity";
    }

    private void OnSectionTrafficClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Traffic;
        _isInsightsMode = false;
        SetInsightsCategoryFilter("traffic");
        RefreshCategoryGroupedCalls(Calls.ToList());
        MenuSpecificStatusText = "Traffic activity";
    }

    private void OnSectionOtherClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Other;
        _isInsightsMode = false;
        SetInsightsCategoryFilter("other");
        RefreshCategoryGroupedCalls(Calls.ToList());
        MenuSpecificStatusText = "Other activity";
    }

    private void OnSectionAlertsClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Alerts;
        ReloadAlertHistory();
        MarkAlertHistoryRead();
        MenuSpecificStatusText = "Alert history";
    }

    private async void OnGenerateSummariesNowClicked(object? sender, RoutedEventArgs e)
    {
        await GenerateSummariesForCurrentRangeAsync("summaries");
    }

    private async void OnGenerateHighlightsNowClicked(object? sender, RoutedEventArgs e)
    {
        await GenerateSummariesForCurrentRangeAsync("highlights");
    }

    private void OnGoToAlertConfigurationClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Settings;
        RefreshSettingsTabsFromSettings();
        var settingsTabs = this.FindControl<TabControl>("SettingsTabControl");
        if (settingsTabs != null)
            settingsTabs.SelectedIndex = 2; // Alerts tab
        MenuSpecificStatusText = "Alert configuration";
    }

    private async Task GenerateSummariesForCurrentRangeAsync(string label)
    {
        if (!IsGenerateNowVisible || _isGeneratingInsightsNow)
            return;

        if (!IsInsightsEnabled)
        {
            MenuSpecificStatusText = $"Cannot generate {label}: insights are disabled.";
            return;
        }

        EnsureInsightsServiceStarted();
        if (_insightsService == null)
        {
            MenuSpecificStatusText = $"Cannot generate {label}: insights service unavailable.";
            return;
        }

        var sourceCalls = Calls.ToList();
        if (sourceCalls.Count == 0)
        {
            MenuSpecificStatusText = $"No calls available to generate {label} for this time range.";
            return;
        }

        var (start, end) = GetCurrentRangeBounds();
        var startOffset = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start));
        var endOffset = new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end));

        _isGeneratingInsightsNow = true;
        RaisePropertyChanged(nameof(CanGenerateNow));
        InsightsStatusText = $"Generating {label}...";
        MenuSpecificStatusText = $"Generating {label} now...";

        try
        {
            var success = await _insightsService.FinalizeWindowAsync(startOffset, endOffset, sourceCalls, _settings, "manual insights summary");
            if (success)
            {
                await LoadInsightsSummariesAsync();
                MenuSpecificStatusText = $"Generated {label} for the selected time range.";
            }
            else
            {
                var reason = string.IsNullOrWhiteSpace(_insightsService.LastFailureReason)
                    ? "unknown error"
                    : _insightsService.LastFailureReason!;
                MenuSpecificStatusText = $"Unable to generate {label}: {reason}";
            }
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Unable to generate {label}: {ex.Message}";
        }
        finally
        {
            _isGeneratingInsightsNow = false;
            RaisePropertyChanged(nameof(CanGenerateNow));
            RaisePropertyChanged(nameof(IsHighlightsEmpty));
            RaisePropertyChanged(nameof(IsCategoryInsightsEmpty));
            RefreshNextInsightStatus();
            _ = UpdateUsageTextAsync();
        }
    }

    private void OnSectionSettingsClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Settings;
        RefreshSettingsTabsFromSettings();
        _ = UpdateUsageTextAsync();
        MenuSpecificStatusText = "Settings";
    }

    private void OnSectionTroubleshootClicked(object? sender, RoutedEventArgs e)
    {
        SelectedSection = MainSection.Troubleshoot;
        LoadTroubleshootLogs();
        MenuSpecificStatusText = "Troubleshoot";
    }

    private void OnBellMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (!_settings.AutoplayAlerts)
        {
            _settings.AutoplayAlerts = true;
            _snoozeUntil = null;
            _openBellMenuOnNextClick = false;
            _settings.SaveToFile(_currentSettingsPath);
            UpdateBellState();
            StatusText = "Alert audio enabled";
            return;
        }

        if (IsBellAlertState)
        {
            StopAudio();
            _openBellMenuOnNextClick = true;
            StatusText = "Alert audio stopped";
            UpdateBellState();
            return;
        }

        if (_openBellMenuOnNextClick)
        {
            _openBellMenuOnNextClick = false;
            ToggleSnoozeMenu();
            return;
        }

        ToggleSnoozeMenu();
    }

    private async void OnRestartTrunkRecorderClicked(object? sender, RoutedEventArgs e)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                ArgumentList = { "-lc", "sudo systemctl restart trunk-recorder" },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
            }
            MenuSpecificStatusText = "Trunk recorder restart requested";
            LoadTroubleshootLogs();
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Restart failed: {ex.Message}";
        }
    }

    private async void OnCopyTroubleshootLogClicked(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard == null)
            return;

        var text = $"{TrunkRecorderLogText}{Environment.NewLine}{Environment.NewLine}{PizzaPiLogText}";
        await top.Clipboard.SetTextAsync(text);
        MenuSpecificStatusText = "Troubleshoot logs copied";
    }

    private static string NormalizeTroubleshootTraceLevel(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "error" => "Error",
            "warning" => "Warning",
            "information" => "Information",
            "debug" => "Debug",
            _ => "Information"
        };
    }

    private static SourceLevels ParseTroubleshootTraceLevel(string? label)
    {
        return NormalizeTroubleshootTraceLevel(label) switch
        {
            "Error" => SourceLevels.Error,
            "Warning" => SourceLevels.Warning,
            "Debug" => SourceLevels.Verbose,
            _ => SourceLevels.Information
        };
    }

    private static string ToTroubleshootTraceLevelLabel(SourceLevels level)
    {
        return level switch
        {
            SourceLevels.Error or SourceLevels.Critical => "Error",
            SourceLevels.Warning => "Warning",
            SourceLevels.Verbose or SourceLevels.All => "Debug",
            _ => "Information"
        };
    }

    private void ApplyTroubleshootTraceLevelSelection(string selectedLabel)
    {
        var level = ParseTroubleshootTraceLevel(selectedLabel);
        _settings.TraceLevelApp = level;
        TraceLogger.SetLevel(level);
        pizzalib.TraceLogger.SetLevel(level);
        try
        {
            _settings.SaveToFile(_currentSettingsPath);
            TraceLogger.Trace(TraceLoggerType.Settings, TraceEventType.Information, $"Live trace level changed to {level}");
            MenuSpecificStatusText = $"Trace level: {selectedLabel}";
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Trace level changed in-memory, but save failed: {ex.Message}";
        }
    }

    private void SyncTroubleshootTraceLevelFromSettings()
    {
        var label = ToTroubleshootTraceLevelLabel(_settings.TraceLevelApp);
        _selectedTroubleshootTraceLevel = label;
        RaisePropertyChanged(nameof(SelectedTroubleshootTraceLevel));
    }

    private void LoadTroubleshootLogs()
    {
        try
        {
            TraceLogger.Flush();
            pizzalib.TraceLogger.Flush();

            RefreshPizzaPiLogOptions();
            LoadSelectedPizzaPiLogText();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    ArgumentList = { "-lc", "tmux capture-pane -pt trunklogs -S -250" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var stdout = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    TrunkRecorderLogText = string.IsNullOrWhiteSpace(stdout) ? "No trunk recorder log output." : stdout;
                    RaisePropertyChanged(nameof(TrunkRecorderLogText));
                }
            }

            var pizzaHasError = PizzaPiLogText.Contains("error", StringComparison.OrdinalIgnoreCase);
            var trunkHasError = TrunkRecorderLogText.Contains("error", StringComparison.OrdinalIgnoreCase);
            var retuneCount = System.Text.RegularExpressions.Regex.Matches(
                TrunkRecorderLogText ?? string.Empty,
                "Retuning to Control Channel",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;
            HasTroubleshootIssue = pizzaHasError || trunkHasError || retuneCount >= 3;
            RaisePropertyChanged(nameof(HasTroubleshootIssue));
            RaisePropertyChanged(nameof(TroubleshootMenuText));
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Troubleshoot log load failed: {ex.Message}";
        }
    }

    private void RefreshPizzaPiLogOptions()
    {
        var previousSelection = _selectedPizzaPiLogOption;
        var currentPath = TraceLogger.CurrentLogPath;
        var allLogs = GetPizzaPiLogFilesOrdered()
            .Where(path => string.IsNullOrWhiteSpace(currentPath)
                || !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .ToList();

        _isRefreshingPizzaPiLogOptions = true;
        try
        {
            PizzaPiLogOptions.Clear();
            _pizzaPiLogOptionPathMap.Clear();

            PizzaPiLogOptions.Add(CurrentPizzaPiLogOption);

            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in allLogs)
            {
                var baseLabel = FormatPizzaPiLogOptionLabel(path);
                var label = baseLabel;
                var suffix = 2;
                while (!seenLabels.Add(label))
                {
                    label = $"{baseLabel} ({suffix++})";
                }

                PizzaPiLogOptions.Add(label);
                _pizzaPiLogOptionPathMap[label] = path;
            }

            var nextSelection = previousSelection;
            if (string.IsNullOrWhiteSpace(nextSelection) || !PizzaPiLogOptions.Contains(nextSelection))
                nextSelection = CurrentPizzaPiLogOption;

            _selectedPizzaPiLogOption = nextSelection;
            RaisePropertyChanged(nameof(SelectedPizzaPiLogOption));
        }
        finally
        {
            _isRefreshingPizzaPiLogOptions = false;
        }
    }

    private List<string> GetPizzaPiLogFilesOrdered()
    {
        var logDir = TraceLogger.m_TraceFileDir;
        if (!Directory.Exists(logDir))
            return new List<string>();

        return Directory.GetFiles(logDir, "pizzapi-*.txt")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();
    }

    private List<string> GetPizzaPiLogCandidatesForSelection()
    {
        if (_selectedPizzaPiLogOption != null
            && !string.Equals(_selectedPizzaPiLogOption, CurrentPizzaPiLogOption, StringComparison.Ordinal)
            && _pizzaPiLogOptionPathMap.TryGetValue(_selectedPizzaPiLogOption, out var selectedPath))
        {
            return new List<string> { selectedPath };
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(TraceLogger.CurrentLogPath) && File.Exists(TraceLogger.CurrentLogPath))
            candidates.Add(TraceLogger.CurrentLogPath);
        candidates.AddRange(GetPizzaPiLogFilesOrdered());
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void LoadSelectedPizzaPiLogText()
    {
        var candidates = GetPizzaPiLogCandidatesForSelection();
        foreach (var candidate in candidates)
        {
            if (TryReadTextFileShared(candidate, out var text))
            {
                PizzaPiLogText = text;
                RaisePropertyChanged(nameof(PizzaPiLogText));
                return;
            }
        }

        PizzaPiLogText = "PizzaPi log unavailable.";
        RaisePropertyChanged(nameof(PizzaPiLogText));
    }

    private static bool TryReadTextFileShared(string path, out string text)
    {
        text = string.Empty;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string FormatPizzaPiLogOptionLabel(string path)
    {
        var info = new FileInfo(path);
        var ts = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
        var size = FormatFileSize(info.Length);
        return $"{ts} | {size} | {info.Name}";
    }

    private static string FormatFileSize(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        if (bytes >= mb)
            return $"{bytes / mb:0.00} MB";
        if (bytes >= kb)
            return $"{bytes / kb:0.0} KB";
        return $"{bytes} B";
    }

    private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ProcessingProfile profile)
        {
            ActiveProfile = profile;
        }
    }

    private void OnProfileEditorSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ProcessingProfile profile)
            ProfileEditorSelectedProfile = profile;
    }

    private void OnCreateProfileClicked(object? sender, RoutedEventArgs e)
    {
        var name = (this.FindControl<TextBox>("ProfileNameTextBox")?.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MenuSpecificStatusText = "Profile name is required";
            return;
        }

        if (Profiles.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MenuSpecificStatusText = "Profile name already exists";
            return;
        }

        var includePolice = this.FindControl<CheckBox>("ProfileIncludePoliceCheckBox")?.IsChecked ?? true;
        var includeFire = this.FindControl<CheckBox>("ProfileIncludeFireCheckBox")?.IsChecked ?? true;
        var includeEms = this.FindControl<CheckBox>("ProfileIncludeEmsCheckBox")?.IsChecked ?? true;
        var includeTraffic = this.FindControl<CheckBox>("ProfileIncludeTrafficCheckBox")?.IsChecked ?? true;
        var includeOther = this.FindControl<CheckBox>("ProfileIncludeOtherCheckBox")?.IsChecked ?? true;
        var talkgroups = ParseProfileTalkgroups(
            this.FindControl<TextBox>("ProfileTalkgroupsTextBox")?.Text ?? string.Empty);

        var profile = new ProcessingProfile
        {
            Name = name,
            IncludePolice = includePolice,
            IncludeFire = includeFire,
            IncludeEMS = includeEms,
            IncludeTraffic = includeTraffic,
            IncludeOther = includeOther,
            AllowedTalkgroups = talkgroups,
            UpdatedAtUtc = DateTime.UtcNow
        };
        Profiles.Add(profile);
        ProfileEditorSelectedProfile = profile;
        SelectedTalkgroupAddProfile = profile;
        _profileStore.Save(Profiles.ToList(), ActiveProfile?.Id ?? profile.Id);
        MenuSpecificStatusText = $"Created profile: {name} (active profile unchanged)";
    }

    private void OnDeleteProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (ProfileEditorSelectedProfile == null || Profiles.Count <= 1)
        {
            MenuSpecificStatusText = "Cannot delete profile";
            return;
        }

        var deletingProfile = ProfileEditorSelectedProfile;
        var deletingActive = ActiveProfile?.Id == deletingProfile.Id;
        Profiles.Remove(deletingProfile);
        ProfileEditorSelectedProfile = Profiles.FirstOrDefault();
        if (deletingActive && ActiveProfile != null)
            ActiveProfile = Profiles.First();
        EnsureTalkgroupAddProfileSelection();
        _profileStore.Save(Profiles.ToList(), ActiveProfile?.Id ?? Profiles.First().Id);
        MenuSpecificStatusText = "Profile deleted";
    }

    private void OnUpdateProfileClicked(object? sender, RoutedEventArgs e)
    {
        if (ProfileEditorSelectedProfile == null)
            return;

        var targetProfile = ProfileEditorSelectedProfile;
        targetProfile.IncludePolice = this.FindControl<CheckBox>("ProfileIncludePoliceCheckBox")?.IsChecked ?? true;
        targetProfile.IncludeFire = this.FindControl<CheckBox>("ProfileIncludeFireCheckBox")?.IsChecked ?? true;
        targetProfile.IncludeEMS = this.FindControl<CheckBox>("ProfileIncludeEmsCheckBox")?.IsChecked ?? true;
        targetProfile.IncludeTraffic = this.FindControl<CheckBox>("ProfileIncludeTrafficCheckBox")?.IsChecked ?? true;
        targetProfile.IncludeOther = this.FindControl<CheckBox>("ProfileIncludeOtherCheckBox")?.IsChecked ?? true;

        var talkgroupsRaw = this.FindControl<TextBox>("ProfileTalkgroupsTextBox")?.Text ?? string.Empty;
        var talkgroups = ParseProfileTalkgroups(talkgroupsRaw);
        targetProfile.AllowedTalkgroups = talkgroups;
        targetProfile.UpdatedAtUtc = DateTime.UtcNow;

        _profileStore.Save(Profiles.ToList(), ActiveProfile?.Id ?? targetProfile.Id);
        if (ActiveProfile?.Id == targetProfile.Id)
        {
            RefreshMenuVisibility();
            EnsureValidSelectedSection();
            ApplyActiveProfileToManagers();
            ClearSectionCachesForProfileSwitch();
            ApplyTimeRangeFilter();
            if (IsHighlightsSelected)
                _ = LoadInsightsSummariesAsync();
        }

        MenuSpecificStatusText = "Profile updated";
    }

    private static List<long> ParseProfileTalkgroups(string talkgroupsRaw)
    {
        return talkgroupsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v =>
            {
                if (long.TryParse(v, out var id))
                    return (long?)id;
                return null;
            })
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .Distinct()
            .ToList();
    }

    private void OnAlertHistoryPlayClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not AlertMatchRecord record)
            return;
        if (string.IsNullOrWhiteSpace(record.AudioPath) || !File.Exists(record.AudioPath))
        {
            MenuSpecificStatusText = "Alert audio not available";
            return;
        }

        if (record.IsAudioPlaying)
        {
            StopAudio();
            MenuSpecificStatusText = "Alert audio stopped";
            return;
        }

        var call = new TranscribedCall
        {
            Location = record.AudioPath,
            StartTime = record.TimestampUnix,
            StopTime = record.TimestampUnix + Math.Max(1, record.DurationSec),
            Transcription = record.Transcription,
            FriendlyTalkgroup = "Alert",
            FriendlyFrequency = string.Empty
        };
        PlayAudio(call);
        SetActiveAlertHistoryAudio(record);
        MenuSpecificStatusText = $"Playing alert audio ({record.AlertType})";
    }

    private void SetActiveAlertHistoryAudio(AlertMatchRecord? activeRecord)
    {
        foreach (var item in AlertHistory)
        {
            item.IsAudioPlaying = activeRecord != null && item.Id == activeRecord.Id;
        }
    }

    private async void OnSettingsLoadProxyClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            MenuSpecificStatusText = "Load failed: file picker unavailable";
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load settings JSON",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("JSON files") { Patterns = new List<string> { "*.json" } }
            }
        });

        var file = files.FirstOrDefault();
        if (file == null)
            return;

        try
        {
            var path = file.Path.LocalPath;
            _settings = Settings.LoadFromFile(path);
            _currentSettingsPath = path;
            TraceLogger.SetLevel(_settings.TraceLevelApp);
            pizzalib.TraceLogger.SetLevel(_settings.TraceLevelApp);
            SyncTroubleshootTraceLevelFromSettings();
            RefreshSettingsUiAndPanels();
            ApplyActiveProfileToManagers();
            MenuSpecificStatusText = $"Loaded settings: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Load failed: {ex.Message}";
        }
    }

    private async void OnSettingsSaveProxyClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            ApplySettingsTabsToSettings();
            _settings.SaveToFile(_currentSettingsPath);
            await ApplySettingsAndRestartAsync(_settings);
            MenuSpecificStatusText = "Settings saved";
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Save failed: {ex.Message}";
        }
    }

    private async void OnSettingsSaveAsProxyClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            MenuSpecificStatusText = "Save As failed: file picker unavailable";
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save settings JSON",
            SuggestedFileName = "settings.json",
            FileTypeChoices = new List<FilePickerFileType>
            {
                new("JSON files") { Patterns = new List<string> { "*.json" } }
            }
        });

        if (file == null)
            return;

        try
        {
            ApplySettingsTabsToSettings();
            _settings.SaveToFile(file.Path.LocalPath);
            _currentSettingsPath = file.Path.LocalPath;
            await ApplySettingsAndRestartAsync(_settings);
            MenuSpecificStatusText = $"Saved settings: {Path.GetFileName(_currentSettingsPath)}";
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Save As failed: {ex.Message}";
        }
    }

    private async void OnSettingsImportTalkgroupsClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            MenuSpecificStatusText = "Import failed: file picker unavailable";
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Talkgroups CSV",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("CSV files") { Patterns = new List<string> { "*.csv" } }
            }
        });
        var file = files.FirstOrDefault();
        if (file == null)
            return;

        try
        {
            var imported = TalkgroupHelper.GetTalkgroupsFromCsv(file.Path.LocalPath);
            if (imported.Count == 0)
            {
                MenuSpecificStatusText = "Import failed: no talkgroups found in CSV.";
                return;
            }

            AutoGenerateMappingsFromTalkgroups(imported, replaceExistingMappings: true);
            RefreshTalkgroupRows();
            HasPendingTalkgroupMappingChanges = true;
            MenuSpecificStatusText = $"Imported {imported.Count} talkgroups";
            TalkgroupWizardStatusText = $"Imported {imported.Count} talkgroups from CSV. Click Apply to make mappings live.";
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Import failed: {ex.Message}";
        }
    }

    private async void OnSettingsBuildTalkgroupsClicked(object? sender, RoutedEventArgs e)
    {
        var sid = await PromptForSidAsync();
        if (string.IsNullOrWhiteSpace(sid))
            return;

        var url = $"https://www.radioreference.com/db/sid/{sid.Trim()}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            TalkgroupWizardStatusText = "Invalid SID.";
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var html = await client.GetStringAsync(uri);
            var parsed = ParseTalkgroupsFromHtml(html, out var parseDiag);
            Trace(
                TraceLoggerType.Settings,
                TraceEventType.Information,
                $"RR build parse diagnostics for SID={sid.Trim()} url={url}: {parseDiag}");
            if (parsed.Count == 0)
            {
                Trace(
                    TraceLoggerType.Settings,
                    TraceEventType.Warning,
                    $"RR build parsed zero talkgroups for SID={sid.Trim()} url={url}; diagnostics: {parseDiag}");
                TalkgroupWizardStatusText = $"Crawl failed: no talkgroups parsed from {url}.";
                return;
            }

            AutoGenerateMappingsFromTalkgroups(parsed, replaceExistingMappings: true);
            RefreshTalkgroupRows();
            HasPendingTalkgroupMappingChanges = true;

            var exportedTrCsvPath = ExportTrSupportedTalkgroupsCsv(sid.Trim());
            TalkgroupWizardStatusText = $"Built {parsed.Count} talkgroups from {url}. Exported TR CSV: {Path.GetFileName(exportedTrCsvPath)}. Click Apply to make mappings live.";
            MenuSpecificStatusText = TalkgroupWizardStatusText;
        }
        catch (Exception ex)
        {
            TalkgroupWizardStatusText = $"Crawl failed: {ex.Message}";
        }
    }

    private async Task<string?> PromptForSidAsync()
    {
        var sidBox = new TextBox
        {
            Width = 220,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Text = string.Empty,
            PlaceholderText = "e.g. 6355"
        };
        var help = new TextBlock
        {
            Text = "Enter RadioReference SID (URL: https://www.radioreference.com/db/sid/{SID})",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.Gray,
            FontSize = 11
        };
        var okButton = new Button { Content = "Build", Width = 90 };
        var cancelButton = new Button { Content = "Cancel", Width = 90 };
        var buttonRow = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Avalonia.Thickness(12), Spacing = 8 };
        panel.Children.Add(help);
        panel.Children.Add(sidBox);
        panel.Children.Add(buttonRow);

        var window = new Window
        {
            Title = "Build RR CSV",
            Width = 500,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };

        string? result = null;
        okButton.Click += (_, _) =>
        {
            result = sidBox.Text?.Trim();
            window.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            result = null;
            window.Close();
        };

        await window.ShowDialog(this);
        return result;
    }

    private string ExportTrSupportedTalkgroupsCsv(string? sid)
    {
        var fileToken = string.IsNullOrWhiteSpace(sid)
            ? "latest"
            : new string(sid.Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrWhiteSpace(fileToken))
            fileToken = "latest";

        var outputDir = Settings.DefaultWorkingDirectory;
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, $"talkgroups-tr-{fileToken}.csv");

        var rows = _talkgroupMappings
            .GroupBy(m => m.TalkgroupId)
            .Select(g => g.OrderByDescending(x => x.UpdatedUtc).First())
            .OrderBy(m => m.TalkgroupId)
            .ToList();

        using var writer = new StreamWriter(outputPath, append: false, Encoding.UTF8);
        writer.WriteLine("Decimal,Hex,Mode,Alpha Tag,Description,Tag,Category");
        foreach (var row in rows)
        {
            writer.WriteLine(
                $"{row.TalkgroupId}," +
                $"{row.TalkgroupId:X}," +
                $"{EscapeCsvField(row.Mode)}," +
                $"{EscapeCsvField(row.AlphaTag)}," +
                $"{EscapeCsvField(row.Description)}," +
                $"{EscapeCsvField(row.Tag)}," +
                $"{EscapeCsvField(row.Category)}");
        }

        Trace(
            TraceLoggerType.Settings,
            TraceEventType.Information,
            $"Exported TR-supported talkgroups CSV: path={outputPath}, rows={rows.Count}");

        return outputPath;
    }

    private static string EscapeCsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
        if (!needsQuotes)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private void OnTalkgroupSelectAllClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox)
            return;

        var shouldSelect = checkBox.IsChecked == true;
        _suppressTalkgroupSelectionTracking = true;
        try
        {
            foreach (var row in _visibleTalkgroupMappingRows)
                row.IsSelectedForProfile = shouldSelect;
        }
        finally
        {
            _suppressTalkgroupSelectionTracking = false;
        }

        if (!shouldSelect)
            _talkgroupSelectionAnchorId = null;
        UpdateTalkgroupSelectionUiState();
    }

    private void OnTalkgroupSelectionCheckboxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _shiftPressedDuringTalkgroupCheckboxPress = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
    }

    private void OnTalkgroupSelectionCheckboxClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not TalkgroupMappingRow row)
            return;

        var shouldSelect = checkBox.IsChecked == true;
        if (_shiftPressedDuringTalkgroupCheckboxPress)
            ApplyTalkgroupRangeSelection(row, shouldSelect);
        else
            UpdateTalkgroupSelectionUiState();

        _talkgroupSelectionAnchorId = row.Id;
        _shiftPressedDuringTalkgroupCheckboxPress = false;
    }

    private void OnAddSelectedTalkgroupsClicked(object? sender, RoutedEventArgs e)
    {
        if (SelectedTalkgroupAddProfile == null)
        {
            MenuSpecificStatusText = "Select a target profile first.";
            return;
        }

        if (IsDefaultProfile(SelectedTalkgroupAddProfile))
        {
            MenuSpecificStatusText = "Default profile cannot be edited.";
            return;
        }

        var selectedTgids = _talkgroupMappingRows
            .Where(r => r.IsSelectedForProfile)
            .Select(r => r.Id)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (selectedTgids.Count == 0)
        {
            MenuSpecificStatusText = "Select at least one talkgroup row.";
            return;
        }

        var prior = new HashSet<long>(SelectedTalkgroupAddProfile.AllowedTalkgroups ?? new List<long>());
        var added = 0;
        foreach (var tgid in selectedTgids)
        {
            if (prior.Add(tgid))
                added++;
        }

        SelectedTalkgroupAddProfile.AllowedTalkgroups = prior.OrderBy(id => id).ToList();
        SelectedTalkgroupAddProfile.UpdatedAtUtc = DateTime.UtcNow;
        ProfileEditorSelectedProfile = SelectedTalkgroupAddProfile;

        var activeProfileId = ActiveProfile?.Id ?? SelectedTalkgroupAddProfile.Id;
        _profileStore.Save(Profiles.ToList(), activeProfileId);
        if (ActiveProfile?.Id == SelectedTalkgroupAddProfile.Id)
        {
            RefreshMenuVisibility();
            EnsureValidSelectedSection();
            ApplyActiveProfileToManagers();
            ClearSectionCachesForProfileSwitch();
            ApplyTimeRangeFilter();
            if (IsHighlightsSelected)
                _ = LoadInsightsSummariesAsync();
        }
        _settings.SaveToFile(_currentSettingsPath);
        RefreshProfileEditorFromSelectedProfile();

        MenuSpecificStatusText = added > 0
            ? $"Added {added} talkgroup(s) to profile '{SelectedTalkgroupAddProfile.Name}'."
            : $"No new talkgroups were added to profile '{SelectedTalkgroupAddProfile.Name}'.";
    }

    private async void OnTalkgroupApplyClicked(object? sender, RoutedEventArgs e)
    {
        if (IsTalkgroupEditorBusy || !HasPendingTalkgroupMappingChanges)
            return;

        var approved = await ConfirmTalkgroupApplyAsync();
        if (!approved)
            return;

        try
        {
            IsTalkgroupApplyBusy = true;
            TalkgroupAutoResolveStatusText = "Stopping live call manager...";

            var wasStarted = _callManager != null && _callManager.IsStarted();
            if (wasStarted && _callManager != null)
            {
                await Task.Run(() => _callManager.Stop(block: true));
            }

            TalkgroupAutoResolveStatusText = "Applying talkgroup mappings...";

            await Task.Run(() =>
            {
                // Persist staged mappings before atomic cutover so restart state matches live state.
                _talkgroupMappingStore.SaveAll(_talkgroupMappings);

                // Persist settings after mapping updates.
                _settings.SaveToFile(_currentSettingsPath);

                // Atomic in-memory switchover: readers block briefly while snapshot pointer is replaced.
                PublishLiveTalkgroupSnapshotFromStaged();
            });

            if (wasStarted && _callManager != null)
            {
                TalkgroupAutoResolveStatusText = "Restarting live call manager...";
                var restarted = await _callManager.Start();
                if (!restarted)
                    throw new Exception("Live call manager did not restart successfully.");
            }

            HasPendingTalkgroupMappingChanges = false;
            TalkgroupWizardStatusText = "Talkgroup mappings are now live.";
            MenuSpecificStatusText = "Talkgroup mappings applied. Live call manager restarted.";

            TriggerHistoricalRefreshAfterMappingApply();
        }
        catch (Exception ex)
        {
            TalkgroupWizardStatusText = $"Apply failed: {ex.Message}";
        }
        finally
        {
            IsTalkgroupApplyBusy = false;
            TalkgroupAutoResolveStatusText = string.Empty;
        }
    }

    private async Task<bool> ConfirmTalkgroupApplyAsync()
    {
        var message = new TextBlock
        {
            Text = "Note: Applying changes to the talkgroup mapping will restart live call manager. Some in-flight calls might be lost.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460
        };

        var proceedButton = new Button
        {
            Content = "Proceed",
            Width = 110,
            Classes = { "menu-btn" }
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            Width = 110,
            Classes = { "menu-btn" }
        };

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        buttons.Children.Add(proceedButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel
        {
            Margin = new Thickness(14),
            Spacing = 12
        };
        panel.Children.Add(message);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = "Apply Talkgroup Mapping",
            Width = 540,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel
        };

        var result = false;
        proceedButton.Click += (_, _) =>
        {
            result = true;
            window.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            result = false;
            window.Close();
        };

        await window.ShowDialog(this);
        return result;
    }

    private void TriggerHistoricalRefreshAfterMappingApply()
    {
        if (_currentFilter == "24h")
        {
            RefreshDerivedFieldsForLoadedCalls();
            ApplyTimeRangeFilter();
            return;
        }

        var requestVersion = Interlocked.Increment(ref _talkgroupRefreshRequestVersion);
        var (start, end) = GetCurrentRangeBounds();
        MenuSpecificStatusText = "Talkgroup mappings are live. Refreshing historical calls...";
        _ = Task.Run(() =>
        {
            var refreshed = LoadOfflineCallsFromHistory(start, end);
            foreach (var call in refreshed)
            {
                call.FriendlyTalkgroup = FormatTalkgroupFromLiveSnapshot(call.Talkgroup);
                call.FriendlyFrequency = FormatFrequency(call.Frequency);
                call.PlayAudioCommand = c => PlayAudio(c);
            }

            SafeUiPost(() =>
            {
                if (requestVersion != _talkgroupRefreshRequestVersion)
                    return;

                _historicalRangeCalls = refreshed;
                ApplyTimeRangeFilter(refreshed);
                MenuSpecificStatusText = $"Talkgroup mappings applied. Refreshed {refreshed.Count} historical calls.";
            });
        });
    }

    private static List<Talkgroup> ParseTalkgroupsFromHtml(string html)
    {
        return ParseTalkgroupsFromHtml(html, out _);
    }

    private static List<Talkgroup> ParseTalkgroupsFromHtml(string html, out string diagnostics)
    {
        var rows = new List<Talkgroup>();
        var diag = new TalkgroupParserDiagnostics();
        if (string.IsNullOrWhiteSpace(html))
        {
            diagnostics = "empty html";
            return rows;
        }

        var clean = System.Text.RegularExpressions.Regex.Replace(
            html,
            "<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        diag.CleanHtmlLength = clean.Length;

        var talkgroupsSection = ExtractTalkgroupsSectionHtml(clean, diag);
        if (string.IsNullOrWhiteSpace(talkgroupsSection))
        {
            diagnostics = diag.ToString();
            return rows;
        }

        var seen = new HashSet<long>();
        ParseTalkgroupTablesFromSection(talkgroupsSection, seen, rows, diag);
        diag.RowsParsed = rows.Count;
        diagnostics = diag.ToString();

        return rows.OrderBy(r => r.Id).ToList();
    }

    private static string ExtractTalkgroupsSectionHtml(string html, TalkgroupParserDiagnostics? diag = null)
    {
        var headingMatches = System.Text.RegularExpressions.Regex.Matches(
            html,
            "<h(?<level>[1-6])[^>]*>(?<title>[\\s\\S]*?)</h\\k<level>>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (diag != null)
            diag.HeadingCount = headingMatches.Count;
        if (headingMatches.Count == 0)
            return html;

        System.Text.RegularExpressions.Match? talkgroupsHeading = null;
        System.Text.RegularExpressions.Match? talkgroupsHeadingContains = null;
        var talkgroupsLevel = 0;
        foreach (System.Text.RegularExpressions.Match heading in headingMatches)
        {
            var title = NormalizeHtmlText(heading.Groups["title"].Value);
            if (title.Equals("Talkgroups", StringComparison.OrdinalIgnoreCase))
            {
                talkgroupsHeading = heading;
                if (!int.TryParse(heading.Groups["level"].Value, out talkgroupsLevel))
                    talkgroupsLevel = 2;
                if (diag != null)
                    diag.TalkgroupsHeadingMatchedExactly = true;
                break;
            }
            if (talkgroupsHeadingContains == null &&
                title.IndexOf("Talkgroups", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                talkgroupsHeadingContains = heading;
                if (diag != null)
                    diag.TalkgroupsHeadingMatchedContains = true;
            }
        }

        if (talkgroupsHeading == null)
        {
            talkgroupsHeading = talkgroupsHeadingContains;
            if (talkgroupsHeading == null)
                return html;
            if (!int.TryParse(talkgroupsHeading.Groups["level"].Value, out talkgroupsLevel))
                talkgroupsLevel = 2;
        }
        if (diag != null)
        {
            diag.TalkgroupsHeadingFound = talkgroupsHeading != null;
            diag.TalkgroupsHeadingLevel = talkgroupsLevel;
        }

        var start = talkgroupsHeading.Index + talkgroupsHeading.Length;
        var end = html.Length;
        foreach (System.Text.RegularExpressions.Match heading in headingMatches)
        {
            if (heading.Index <= talkgroupsHeading.Index)
                continue;
            if (!int.TryParse(heading.Groups["level"].Value, out var level))
                continue;

            if (level <= talkgroupsLevel)
            {
                end = heading.Index;
                break;
            }
        }

        if (start < 0 || start >= html.Length)
            return html;
        if (end <= start || end > html.Length)
        {
            var fallback = html[start..];
            if (diag != null)
                diag.TalkgroupsSectionLength = fallback.Length;
            return fallback;
        }
        var section = html[start..end];
        if (diag != null)
            diag.TalkgroupsSectionLength = section.Length;
        return section;
    }

    private static void ParseTalkgroupTablesFromSection(
        string sectionHtml,
        HashSet<long> seen,
        List<Talkgroup> rows,
        TalkgroupParserDiagnostics? diag = null)
    {
        var nodeMatches = System.Text.RegularExpressions.Regex.Matches(
            sectionHtml,
            "(?<heading><h(?<level>[1-6])[^>]*>(?<title>[\\s\\S]*?)</h\\k<level>>)|(?<table><table[^>]*>[\\s\\S]*?</table>)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (diag != null)
            diag.SectionNodeCount = nodeMatches.Count;
        if (nodeMatches.Count == 0)
            return;

        var currentSectionTitle = "Talkgroups";
        foreach (System.Text.RegularExpressions.Match node in nodeMatches)
        {
            if (node.Groups["heading"].Success)
            {
                var title = CleanCategoryLabel(NormalizeHtmlText(node.Groups["title"].Value));
                if (!string.IsNullOrWhiteSpace(title) &&
                    !title.Equals("Talkgroups", StringComparison.OrdinalIgnoreCase))
                {
                    currentSectionTitle = title;
                }
                continue;
            }

            if (!node.Groups["table"].Success)
                continue;
            ParseTalkgroupTable(node.Groups["table"].Value, currentSectionTitle, seen, rows, diag);
        }
    }

    private static void ParseTalkgroupTable(
        string tableHtml,
        string sectionTitle,
        HashSet<long> seen,
        List<Talkgroup> rows,
        TalkgroupParserDiagnostics? diag = null)
    {
        if (diag != null)
            diag.TableCount++;

        var trMatches = System.Text.RegularExpressions.Regex.Matches(
            tableHtml,
            "<tr[^>]*>(?<row>[\\s\\S]*?)</tr>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (trMatches.Count == 0)
            return;

        var headerIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var headerRowIndex = -1;
        for (var i = 0; i < trMatches.Count; i++)
        {
            var thMatches = System.Text.RegularExpressions.Regex.Matches(
                trMatches[i].Groups["row"].Value,
                "<th\\b(?:\"[^\"]*\"|'[^']*'|[^'\">])*>(?<cell>[\\s\\S]*?)</th>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (thMatches.Count == 0)
                continue;
            if (diag != null)
                diag.TablesWithThRows++;

            var headers = thMatches
                .Select(m => NormalizeHeaderToken(NormalizeHtmlText(m.Groups["cell"].Value)))
                .ToList();
            if (!TryGetTalkgroupHeaderIndexes(headers, headerIndexes))
            {
                if (diag != null)
                    diag.HeaderCandidateRejected++;
                continue;
            }

            headerRowIndex = i;
            break;
        }

        if (headerRowIndex < 0)
            return;
        if (diag != null)
            diag.TablesMatchedHeaderSchema++;

        for (var rowIdx = headerRowIndex + 1; rowIdx < trMatches.Count; rowIdx++)
        {
            var cellMatches = System.Text.RegularExpressions.Regex.Matches(
                trMatches[rowIdx].Groups["row"].Value,
                "<td\\b(?:\"[^\"]*\"|'[^']*'|[^'\">])*>(?<cell>[\\s\\S]*?)</td>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (cellMatches.Count == 0)
                continue;

            var cells = cellMatches
                .Select(m => NormalizeHtmlText(m.Groups["cell"].Value))
                .ToList();
            if (!headerIndexes.Values.All(i => i >= 0 && i < cells.Count))
                continue;
            if (!TryParseTalkgroupId(cells[headerIndexes["dec"]], out var id) || !seen.Add(id))
            {
                if (diag != null)
                    diag.RowsRejectedId++;
                continue;
            }

            rows.Add(new Talkgroup
            {
                Id = id,
                Mode = NullIfEmpty(cells[headerIndexes["mode"]]),
                AlphaTag = NullIfEmpty(cells[headerIndexes["alpha_tag"]]),
                Description = NullIfEmpty(cells[headerIndexes["description"]]),
                Tag = NullIfEmpty(cells[headerIndexes["tag"]]),
                Category = NullIfEmpty(CleanCategoryLabel(sectionTitle))
            });
        }
    }

    private static bool TryGetTalkgroupHeaderIndexes(
        List<string> normalizedHeaders,
        Dictionary<string, int> indexes)
    {
        indexes.Clear();
        if (normalizedHeaders == null || normalizedHeaders.Count == 0)
            return false;

        for (var i = 0; i < normalizedHeaders.Count; i++)
        {
            var h = normalizedHeaders[i];
            if (h == "dec")
                indexes["dec"] = i;
            else if (h == "hex")
                indexes["hex"] = i;
            else if (h == "mode")
                indexes["mode"] = i;
            else if (h == "alphatag" || h == "alpha")
                indexes["alpha_tag"] = i;
            else if (h == "description" || h == "desc")
                indexes["description"] = i;
            else if (h == "tag")
                indexes["tag"] = i;
        }

        return indexes.ContainsKey("dec")
               && indexes.ContainsKey("hex")
               && indexes.ContainsKey("mode")
               && indexes.ContainsKey("alpha_tag")
               && indexes.ContainsKey("description")
               && indexes.ContainsKey("tag");
    }

    private static string NormalizeHeaderToken(string value)
    {
        var lower = (value ?? string.Empty).Trim().ToLowerInvariant();
        return new string(lower.Where(char.IsLetterOrDigit).ToArray());
    }
    private static bool TryParseTalkgroupId(string? raw, out long id)
    {
        id = 0;
        var text = (raw ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Reject frequency-like values (e.g. 851.1125) and anything that's not a whole number.
        if (text.Contains('.'))
            return false;

        // Accept plain integer forms, optionally with commas/spaces around digits.
        var normalized = text.Replace(",", string.Empty).Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^\d{1,8}$"))
            return false;
        if (!long.TryParse(normalized, out id))
            return false;

        return id > 0;
    }

    private sealed class TalkgroupParserDiagnostics
    {
        public int CleanHtmlLength { get; set; }
        public int HeadingCount { get; set; }
        public bool TalkgroupsHeadingMatchedExactly { get; set; }
        public bool TalkgroupsHeadingMatchedContains { get; set; }
        public bool TalkgroupsHeadingFound { get; set; }
        public int TalkgroupsHeadingLevel { get; set; }
        public int TalkgroupsSectionLength { get; set; }
        public int SectionNodeCount { get; set; }
        public int TableCount { get; set; }
        public int TablesWithThRows { get; set; }
        public int HeaderCandidateRejected { get; set; }
        public int TablesMatchedHeaderSchema { get; set; }
        public int RowsRejectedId { get; set; }
        public int RowsParsed { get; set; }

        public override string ToString()
        {
            return $"cleanLen={CleanHtmlLength}; headings={HeadingCount}; tgHeadingFound={TalkgroupsHeadingFound}; " +
                   $"tgHeadingExact={TalkgroupsHeadingMatchedExactly}; tgHeadingContains={TalkgroupsHeadingMatchedContains}; " +
                   $"tgHeadingLevel={TalkgroupsHeadingLevel}; tgSectionLen={TalkgroupsSectionLength}; nodes={SectionNodeCount}; " +
                   $"tables={TableCount}; tablesWithTh={TablesWithThRows}; headerRejects={HeaderCandidateRejected}; " +
                   $"tgTables={TablesMatchedHeaderSchema}; rowsRejectedId={RowsRejectedId}; rowsParsed={RowsParsed}";
        }
    }

    private static string NormalizeHtmlText(string value)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(value, "<[^>]+>", string.Empty));
        return decoded.Replace("&nbsp;", " ").Trim();
    }

    private static string CleanCategoryLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ").Trim();
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?i)\bView\s+Talkgroup\s+Category\s+Details\b",
            string.Empty).Trim();
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?i)\b([A-Za-z][A-Za-z\s'\-]+County)\s*\(\s*\d+\s*\)",
            "$1");
        while (System.Text.RegularExpressions.Regex.IsMatch(text, @"\s*\([^)]*\)\s*$"))
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\([^)]*\)\s*$", string.Empty).Trim();
        }
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? GetCell(List<string> cells, Dictionary<string, int> headers, string key, int fallback)
    {
        if (headers.TryGetValue(key, out var idx) && idx >= 0 && idx < cells.Count)
            return cells[idx];
        if (fallback >= 0 && fallback < cells.Count)
            return cells[fallback];
        return null;
    }

    private void OnWizardAddTalkgroupClicked(object? sender, RoutedEventArgs e)
    {
        if (!long.TryParse((WizardDraftIdText ?? string.Empty).Trim(), out var id) || id <= 0)
        {
            TalkgroupWizardStatusText = "TGID must be a positive number.";
            return;
        }

        var existing = _wizardTalkgroupDraftRows.FirstOrDefault(t => t.Id == id);
        var next = new Talkgroup
        {
            Id = id,
            AlphaTag = string.IsNullOrWhiteSpace(WizardDraftAlphaTag) ? null : WizardDraftAlphaTag.Trim(),
            Description = string.IsNullOrWhiteSpace(WizardDraftDescription) ? null : WizardDraftDescription.Trim(),
            Category = string.IsNullOrWhiteSpace(WizardDraftCategory) ? null : WizardDraftCategory.Trim(),
            Mode = existing?.Mode,
            Tag = existing?.Tag
        };

        if (existing != null)
        {
            var idx = _wizardTalkgroupDraftRows.IndexOf(existing);
            _wizardTalkgroupDraftRows[idx] = next;
        }
        else
        {
            _wizardTalkgroupDraftRows.Add(next);
        }

        WizardDraftIdText = string.Empty;
        WizardDraftAlphaTag = string.Empty;
        WizardDraftDescription = string.Empty;
        WizardDraftCategory = string.Empty;
        TalkgroupWizardStatusText = $"Draft rows: {_wizardTalkgroupDraftRows.Count}";
    }

    private void OnWizardClearDraftClicked(object? sender, RoutedEventArgs e)
    {
        _wizardTalkgroupDraftRows.Clear();
        TalkgroupWizardStatusText = "Draft cleared.";
    }

    private void OnWizardApplyDraftClicked(object? sender, RoutedEventArgs e)
    {
        if (_wizardTalkgroupDraftRows.Count == 0)
        {
            TalkgroupWizardStatusText = "Draft is empty.";
            return;
        }

        var staged = _wizardTalkgroupDraftRows.OrderBy(t => t.Id).ToList();
        AutoGenerateMappingsFromTalkgroups(staged);
        RefreshTalkgroupRows();
        HasPendingTalkgroupMappingChanges = true;
        TalkgroupWizardStatusText = $"Staged {staged.Count} draft talkgroups. Click Apply to make them live.";
        MenuSpecificStatusText = TalkgroupWizardStatusText;
    }

    private void OnInitializeTalkgroupMappingsClicked(object? sender, RoutedEventArgs e)
    {
        TalkgroupWizardStatusText = "Mappings are already initialized from import/build. Review and click Apply.";
    }

    private void OnSaveTalkgroupMappingClicked(object? sender, RoutedEventArgs e)
    {
        SaveSelectedTalkgroupMapping();
    }

    private void OnSettingsTestEmailClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            ApplySettingsTabsToSettings();
            if (string.IsNullOrWhiteSpace(_settings.EmailUser))
            {
                MenuSpecificStatusText = "Email test failed: email user not set";
                return;
            }

            EmailSender.SendHtml(
                _settings,
                "pizzawave test",
                _settings.EmailUser!,
                "PizzaPi test email",
                "<b>PizzaPi test email</b><br/>Your email settings are working.");
            MenuSpecificStatusText = "Email test sent";
        }
        catch (Exception ex)
        {
            MenuSpecificStatusText = $"Email test failed: {ex.Message}";
        }
    }
    private void OnRange24hClicked(object? sender, RoutedEventArgs e)
    {
        _currentFilter = "24h";
        _historicalRangeCalls = null;
        _customStartDate = null;
        _customEndDate = null;
        PruneLiveMemoryWindow();
        ApplyTimeRangeFilter();
        if (IsAlertsSelected)
            ReloadAlertHistory();
        UpdateButtonStates();
        if (IsHighlightsSelected)
            _ = LoadInsightsSummariesAsync();
    }

    private void OnRange2dClicked(object? sender, RoutedEventArgs e)
    {
        _currentFilter = "2d";
        _historicalRangeCalls = null;
        ApplyTimeRangeFilter();
        if (IsAlertsSelected)
            ReloadAlertHistory();
        UpdateButtonStates();
        if (IsHighlightsSelected)
            _ = LoadInsightsSummariesAsync();
    }

    private void OnRangeWeekClicked(object? sender, RoutedEventArgs e)
    {
        _currentFilter = "week";
        _historicalRangeCalls = null;
        ApplyTimeRangeFilter();
        if (IsAlertsSelected)
            ReloadAlertHistory();
        UpdateButtonStates();
        if (IsHighlightsSelected)
            _ = LoadInsightsSummariesAsync();
    }

    private async void OnPickRangeClicked(object? sender, RoutedEventArgs e)
    {
        var dialog = new DatePickerDialog(
            _customStartDate ?? DateTime.Now.AddDays(-7),
            _customEndDate ?? DateTime.Now);
        await dialog.ShowDialog(this);

        if (dialog.SelectedStart.HasValue && dialog.SelectedEnd.HasValue)
        {
            _customStartDate = dialog.SelectedStart.Value;
            _customEndDate = dialog.SelectedEnd.Value;
            _currentFilter = "custom";
            ApplyTimeRangeFilter();
            if (IsAlertsSelected)
                ReloadAlertHistory();
            UpdateButtonStates();
            if (IsHighlightsSelected)
                _ = LoadInsightsSummariesAsync();
        }
    }

    private async void OnInsightTranscriptClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not InsightNotableEvent notable)
            return;

        var calls = ResolveCallsForNotable(notable);
        if (calls.Count == 0)
        {
            StatusText = "No matching calls found for this insight.";
            return;
        }

        var dialog = BuildInsightTranscriptDialog(notable, calls);
        await dialog.ShowDialog(this);
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

    private void OnInsightNextCallClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not InsightNotableEvent notable)
            return;

        var matched = ResolveCallsForNotable(notable);
        var visibleKeys = CategoryGroupedCalls
            .Where(i => !i.IsHeader && i.Call != null)
            .Select(i => GetCallDedupKey(i.Call!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var navigable = matched
            .Where(c => visibleKeys.Contains(GetCallDedupKey(c)))
            .ToList();
        if (navigable.Count == 0)
            navigable = matched;

        if (navigable.Count == 0)
        {
            MenuSpecificStatusText = "No matching calls found for this insight.";
            btn.Content = "Go to record";
            foreach (var call in Calls)
                call.IsCurrentMatch = false;
            return;
        }

        var notableKey = GetInsightNotableKey(notable);
        var currentIndex = _insightCallNavigateIndex.TryGetValue(notableKey, out var idx) ? idx : -1;
        var nextIndex = (currentIndex + 1) % navigable.Count;
        _insightCallNavigateIndex[notableKey] = nextIndex;
        var target = navigable[nextIndex];

        var relatedKeys = navigable
            .Select(GetCallDedupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var call in Calls)
            call.IsCurrentMatch = relatedKeys.Contains(GetCallDedupKey(call));

        var groupKey = target.FriendlyTalkgroup;
        if (!string.IsNullOrWhiteSpace(groupKey) && _collapsedCategoryGroups.Contains(groupKey))
        {
            _collapsedCategoryGroups.Remove(groupKey);
            RefreshCategoryGroupedCalls(Calls.ToList());
        }

        if (navigable.Count <= 1)
            btn.Content = "Go to record";
        else
            btn.Content = $"Go to records ({nextIndex + 1} of {navigable.Count})";

        ScrollTargetCallIntoView(target);
        var localTime = DateTimeOffset.FromUnixTimeSeconds(target.StartTime).ToLocalTime().ToString("g");
        MenuSpecificStatusText = $"Selected call {nextIndex + 1}/{navigable.Count}: {target.FriendlyTalkgroup} at {localTime}";
    }

    private void ScrollTargetCallIntoView(TranscribedCall target)
    {
        if (_categoryCallsDataGrid == null)
            return;

        var targetKey = GetCallDedupKey(target);
        var targetIndex = CategoryGroupedCalls
            .Select((item, index) => new { item, index })
            .FirstOrDefault(x =>
                !x.item.IsHeader
                && x.item.Call != null
                && string.Equals(GetCallDedupKey(x.item.Call), targetKey, StringComparison.OrdinalIgnoreCase))
            ?.index ?? -1;
        if (targetIndex < 0)
            return;

        var targetItem = CategoryGroupedCalls[targetIndex];
        const int scrollLeadRows = 5;
        var anchorIndex = Math.Max(0, targetIndex - scrollLeadRows);
        var anchorItem = CategoryGroupedCalls[anchorIndex];
        Dispatcher.UIThread.Post(() =>
        {
            if (_categoryCallsDataGrid == null)
                return;
            _categoryCallsDataGrid.SelectedItem = targetItem;
            // Scroll with a lead buffer so the target is comfortably visible instead of edge-clipped.
            _categoryCallsDataGrid.ScrollIntoView(anchorItem, null);
            _categoryCallsDataGrid.ScrollIntoView(targetItem, null);
        }, DispatcherPriority.Background);
    }

    private Window BuildInsightTranscriptDialog(InsightNotableEvent notable, List<TranscribedCall> calls)
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
        Window? dialog = null;
        closeButton.Click += (_, _) => dialog?.Close();

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

        var content = new Border
        {
            Padding = new Thickness(12),
            Child = layoutGrid
        };
        dialog = new Window
        {
            Title = "Insight Transcript",
            Width = 760,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };
        return dialog;
    }

    private List<TranscribedCall> ResolveCallsForNotable(InsightNotableEvent notable)
    {
        var summary = notable.ParentSummary;
        if (summary == null)
            return new List<TranscribedCall>();

        var cacheKey = $"{summary.WindowStart:O}|{summary.WindowEnd:O}";
        if (!_insightsWindowCallCache.TryGetValue(cacheKey, out var windowCalls))
        {
            // Prefer already-loaded calls for the active range to avoid expensive disk scans
            // and keep category-switch interactions fast.
            var fromVisibleCalls = Calls
                .Where(c =>
                {
                    var ts = DateTimeOffset.FromUnixTimeSeconds(c.StartTime);
                    return ts >= summary.WindowStart && ts <= summary.WindowEnd;
                })
                .ToList();

            if (fromVisibleCalls.Count == 0)
            {
                // Fallback for older windows not currently materialized in visible calls.
                fromVisibleCalls = LoadOfflineCallsFromHistory(summary.WindowStart.LocalDateTime, summary.WindowEnd.LocalDateTime);
            }

            windowCalls = fromVisibleCalls;
            foreach (var call in windowCalls)
            {
                call.FriendlyTalkgroup = FormatTalkgroupFromLiveSnapshot(call.Talkgroup);
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

    private void OnCallRowClicked(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Control control || control.DataContext is not CallGroupItem groupItem || groupItem.Call == null)
            return;

        var call = groupItem.Call;
        if (call.IsAudioPlaying)
        {
            StopAudio();
            call.IsAudioPlaying = false;
            return;
        }

        PlayAudio(call);
    }

    private void OnCallPlayClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Control control || control.DataContext is not CallGroupItem groupItem || groupItem.Call == null)
            return;

        var call = groupItem.Call;
        if (call.IsAudioPlaying)
        {
            StopAudio();
            call.IsAudioPlaying = false;
            return;
        }

        PlayAudio(call);
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
        RaisePropertyChanged(nameof(IsGenerateNowVisible));
        RaisePropertyChanged(nameof(CanGenerateNow));
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

public sealed class TalkgroupLiveSnapshot
{
    public static readonly TalkgroupLiveSnapshot Empty = new(0, new Dictionary<long, TalkgroupMapping>());

    public TalkgroupLiveSnapshot(
        int version,
        IReadOnlyDictionary<long, TalkgroupMapping> mappingsById)
    {
        Version = version;
        MappingsById = mappingsById;
    }

    public int Version { get; }
    public IReadOnlyDictionary<long, TalkgroupMapping> MappingsById { get; }
}

public sealed class TalkgroupMappingRow : INotifyPropertyChanged
{
    public long Id { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string AlphaTag { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    private string _opsCategory = "other";
    private bool _isSelectedForProfile;
    public Action<TalkgroupMappingRow>? OnOpsCategoryChanged { get; set; }
    public Action<TalkgroupMappingRow>? OnSelectionChanged { get; set; }

    public string OpsCategory
    {
        get => _opsCategory;
        set
        {
            var normalized = InsightCategoryPalette.Normalize(value) switch
            {
                "police" => "police",
                "fire" => "fire",
                "ems" => "ems",
                "traffic" => "traffic",
                _ => "other"
            };
            if (string.Equals(_opsCategory, normalized, StringComparison.Ordinal))
                return;
            _opsCategory = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OpsCategory)));
            OnOpsCategoryChanged?.Invoke(this);
        }
    }

    public bool IsSelectedForProfile
    {
        get => _isSelectedForProfile;
        set
        {
            if (_isSelectedForProfile == value)
                return;
            _isSelectedForProfile = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelectedForProfile)));
            OnSelectionChanged?.Invoke(this);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class InsightNavigateButtonTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int count || count <= 1)
            return "Go to record";
        return $"Go to records (1 of {count})";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}



