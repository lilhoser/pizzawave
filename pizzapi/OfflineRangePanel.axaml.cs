using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using pizzalib;

namespace pizzapi;

public class OfflineRangeResult
{
    public string FolderPath { get; }
    public List<TranscribedCall> Calls { get; }
    public DateTime Start { get; }
    public DateTime End { get; }

    public OfflineRangeResult(string folderPath, List<TranscribedCall> calls, DateTime start, DateTime end)
    {
        FolderPath = folderPath;
        Calls = calls;
        Start = start;
        End = end;
    }
}

public partial class OfflineRangePanel : UserControl
{
    private Settings _settings;

    public event EventHandler? RequestClose;
    public event EventHandler<OfflineRangeResult>? OfflineLoaded;

    public OfflineRangePanel() : this(new Settings())
    {
    }

    public OfflineRangePanel(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        SetupEventHandlers();
        InitializeDefaultDates();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void InitializeDefaultDates()
    {
        var startPicker = this.FindControl<DatePicker>("StartDatePicker");
        var endPicker = this.FindControl<DatePicker>("EndDatePicker");
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        if (startPicker != null) startPicker.SelectedDate = today;
        if (endPicker != null) endPicker.SelectedDate = today;
    }

    private void SetupEventHandlers()
    {
        var cancelBtn = this.FindControl<Button>("CancelButton");
        var openBtn = this.FindControl<Button>("OpenButton");

        if (cancelBtn != null)
            cancelBtn.Click += (s, e) => RequestClose?.Invoke(this, EventArgs.Empty);

        if (openBtn != null)
            openBtn.Click += OnOpenClicked;
    }

    private void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        var startPicker = this.FindControl<DatePicker>("StartDatePicker");
        var endPicker = this.FindControl<DatePicker>("EndDatePicker");

        if (startPicker?.SelectedDate == null || endPicker?.SelectedDate == null)
        {
            ShowError("Please select a start and end date");
            return;
        }

        var startDate = startPicker.SelectedDate.Value.Date;
        var endDate = endPicker.SelectedDate.Value.Date.AddDays(1).AddSeconds(-1);

        if (endDate < startDate)
        {
            ShowError("End date must be on or after start date");
            return;
        }

        try
        {
            // Use default captures folder - folders will be filtered by date range internally
            var capturesRoot = Settings.DefaultLiveCaptureDirectory;
            if (!Directory.Exists(capturesRoot))
            {
                ShowError($"Captures directory not found: {capturesRoot}");
                return;
            }

            // Get all dated subdirectories and filter them based on date range
            var candidateFolders = Directory.EnumerateDirectories(capturesRoot)
                .Where(dir => ShouldIncludeFolderForRange(dir, startDate, endDate))
                .ToList();

            if (candidateFolders.Count == 0)
            {
                ShowError("No capture folders found for the selected date range");
                return;
            }

            var loadedCalls = new List<TranscribedCall>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Parse calljournal.json from each matching folder as JSONL
            foreach (var folder in candidateFolders)
            {
                if (!Directory.Exists(folder))
                    continue;

                var journalPath = Path.Combine(folder, "calljournal.json");
                if (File.Exists(journalPath))
                {
                    try
                    {
                        var lines = File.ReadLines(journalPath);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            TranscribedCall? call;
                            try { call = Newtonsoft.Json.JsonConvert.DeserializeObject<TranscribedCall>(line); }
                            catch { continue; } // Skip malformed JSON, continue to next record

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
            }

            if (loadedCalls.Count == 0)
            {
                ShowError("No calls were found for the selected date range");
                return;
            }

            var startUnix = new DateTimeOffset(DateTime.SpecifyKind(startDate, DateTimeKind.Local)).ToUnixTimeSeconds();
            var endUnix = new DateTimeOffset(DateTime.SpecifyKind(endDate, DateTimeKind.Local)).ToUnixTimeSeconds();

            var filteredCalls = loadedCalls.Where(c => c.StartTime >= startUnix && c.StartTime <= endUnix).ToList();

            if (filteredCalls.Count == 0)
            {
                ShowError("No calls were found for the selected date range");
                return;
            }

            OfflineLoaded?.Invoke(this, new OfflineRangeResult(capturesRoot, filteredCalls, startDate, endDate));
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to load offline records: {ex.Message}");
        }
    }

    private bool ShouldIncludeFolderForRange(string folder, DateTime start, DateTime end)
    {
        try
        {
            var folderName = Path.GetFileName(folder);
            // Match folders like "2026-03-11-130445" - date followed by time HHmmss
            var match = System.Text.RegularExpressions.Regex.Match(folderName, @"^(?<date>\d{4}-\d{2}-\d{2})-(?<time>\d{6})");
            if (match.Success)
            {
                var datePart = match.Groups["date"].Value;
                var timePart = match.Groups["time"].Value;

                // Parse the full timestamp from folder name
                if (DateTime.TryParseExact($"{datePart} {timePart}", "yyyy-MM-dd HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var folderTimestamp))
                {
                    return folderTimestamp >= start && folderTimestamp <= end;
                }

                // Fallback: only use date portion if time parsing fails
                if (DateTime.TryParseExact(datePart, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var folderDate))
                {
                    return folderDate.Date >= start.Date && folderDate.Date <= end.Date;
                }
            }

            // Ultimate fallback: use last write time of folder
            var lastWrite = Directory.GetLastWriteTime(folder);
            return lastWrite >= start.AddDays(-1) && lastWrite <= end.AddDays(1);
        }
        catch
        {
            return false;
        }
    }

    private void ShowError(string message)
    {
        var errorWindow = new Window
        {
            Title = "Error",
            Width = 400,
            Height = 200,
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
