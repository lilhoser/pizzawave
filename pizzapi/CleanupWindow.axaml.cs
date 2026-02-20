using System;
using System.IO;
using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using pizzalib;

namespace pizzapi;

public partial class CleanupWindow : Window
{
    private string _capturePath;
    private long _estimatedFreeSpace;

    public CleanupWindow()
    {
        InitializeComponent();
        _capturePath = Settings.DefaultLiveCaptureDirectory;
        LoadCurrentUsage();
        SetupEventHandlers();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void LoadCurrentUsage()
    {
        var currentUsage = GetCaptureFolderSize();
        var currentUsageText = this.FindControl<TextBlock>("CurrentUsageText");
        if (currentUsageText != null)
            currentUsageText.Text = currentUsage;
    }

    private void SetupEventHandlers()
    {
        var cancelBtn = this.FindControl<Button>("CancelButton");
        var cleanupBtn = this.FindControl<Button>("CleanupButton");

        if (cancelBtn != null)
            cancelBtn.Click += (s, e) => Close();

        if (cleanupBtn != null)
            cleanupBtn.Click += OnCleanupClicked;

        // Update estimate when selection changes
        var radioButtons = new[] {
            this.FindControl<RadioButton>("AllFilesRadio"),
            this.FindControl<RadioButton>("OlderThanDayRadio"),
            this.FindControl<RadioButton>("OlderThanWeekRadio"),
            this.FindControl<RadioButton>("OlderThanMonthRadio"),
            this.FindControl<RadioButton>("OlderThan3MonthsRadio")
        };

        foreach (var radio in radioButtons)
        {
            if (radio != null)
                radio.IsCheckedChanged += (s, e) => UpdateEstimate();
        }
    }

    private string GetCaptureFolderSize()
    {
        try
        {
            if (!Directory.Exists(_capturePath))
                return "0 MB";

            var size = GetDirectorySize(_capturePath);
            var mb = size / (1024.0 * 1024.0);
            return $"{mb:F1} MB";
        }
        catch
        {
            return "Unknown";
        }
    }

    private long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var fileInfo = new FileInfo(file);
                size += fileInfo.Length;
            }
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

    private void UpdateEstimate()
    {
        var cutoffDate = GetCutoffDate();
        _estimatedFreeSpace = CalculateEstimatedFreeSpace(cutoffDate);

        var estimatedFreeText = this.FindControl<TextBlock>("EstimatedFreeText");
        if (estimatedFreeText != null)
        {
            var mb = _estimatedFreeSpace / (1024.0 * 1024.0);
            estimatedFreeText.Text = $"{mb:F1} MB";
        }
    }

    private DateTime GetCutoffDate()
    {
        var olderThanDay = this.FindControl<RadioButton>("OlderThanDayRadio");
        var olderThanWeek = this.FindControl<RadioButton>("OlderThanWeekRadio");
        var olderThanMonth = this.FindControl<RadioButton>("OlderThanMonthRadio");
        var olderThan3Months = this.FindControl<RadioButton>("OlderThan3MonthsRadio");

        if (olderThanDay?.IsChecked == true)
            return DateTime.Now.AddDays(-1);
        if (olderThanWeek?.IsChecked == true)
            return DateTime.Now.AddDays(-7);
        if (olderThanMonth?.IsChecked == true)
            return DateTime.Now.AddDays(-30);
        if (olderThan3Months?.IsChecked == true)
            return DateTime.Now.AddDays(-90);
        
        // All files - return DateTime.MaxValue to include everything
        return DateTime.MaxValue;
    }

    private long CalculateEstimatedFreeSpace(DateTime cutoffDate)
    {
        if (!Directory.Exists(_capturePath))
            return 0;

        return GetDirectorySizeForCutoff(_capturePath, cutoffDate);
    }

    private long GetDirectorySizeForCutoff(string path, DateTime cutoffDate)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var fileInfo = new FileInfo(file);
                if (cutoffDate == DateTime.MaxValue || fileInfo.LastWriteTime < cutoffDate)
                {
                    size += fileInfo.Length;
                }
            }
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                size += GetDirectorySizeForCutoff(dir, cutoffDate);
            }
        }
        catch
        {
            // Ignore access errors
        }
        return size;
    }

    private async void OnCleanupClicked(object? sender, RoutedEventArgs e)
    {
        var cleanupBtn = this.FindControl<Button>("CleanupButton");
        if (cleanupBtn != null)
            cleanupBtn.IsEnabled = false;

        try
        {
            var cutoffDate = GetCutoffDate();
            var wipeAction = this.FindControl<RadioButton>("WipeRadio")?.IsChecked == true;
            var archiveAction = this.FindControl<RadioButton>("ArchiveRadio")?.IsChecked == true;

            if (wipeAction)
            {
                await PerformWipe(cutoffDate);
            }
            else if (archiveAction)
            {
                await PerformArchive(cutoffDate);
            }

            LoadCurrentUsage();
            UpdateEstimate();
            Close();
        }
        catch (Exception ex)
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
                            Text = $"Cleanup failed: {ex.Message}",
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
            okButton.Click += (s, args) => errorWindow.Close();

            await errorWindow.ShowDialog(this);
        }
        finally
        {
            if (cleanupBtn != null)
                cleanupBtn.IsEnabled = true;
        }
    }

    private System.Threading.Tasks.Task PerformWipe(DateTime cutoffDate)
    {
        return System.Threading.Tasks.Task.Run(() =>
        {
            DeleteFilesForCutoff(_capturePath, cutoffDate);
        });
    }

    private void DeleteFilesForCutoff(string path, DateTime cutoffDate)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var fileInfo = new FileInfo(file);
                if (cutoffDate == DateTime.MaxValue || fileInfo.LastWriteTime < cutoffDate)
                {
                    File.Delete(file);
                }
            }
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                DeleteFilesForCutoff(dir, cutoffDate);
                // Remove empty directories
                try
                {
                    if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    {
                        Directory.Delete(dir);
                    }
                }
                catch
                {
                    // Ignore
                }
            }
        }
        catch
        {
            // Ignore access errors
        }
    }

    private System.Threading.Tasks.Task PerformArchive(DateTime cutoffDate)
    {
        return System.Threading.Tasks.Task.Run(() =>
        {
            var archiveNameText = this.FindControl<TextBox>("ArchiveNameTextBox");
            var archiveName = archiveNameText?.Text ?? "";
            if (string.IsNullOrEmpty(archiveName) || archiveName == "pizzawave-archive-{}.zip")
            {
                archiveName = "pizzawave-archive-{date}.zip";
            }

            // Replace {date} placeholder
            archiveName = archiveName.Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"));

            var archivePath = Path.Combine(Settings.DefaultWorkingDirectory, archiveName);

            // Collect files to archive
            var filesToArchive = new System.Collections.Generic.List<string>();
            CollectFilesForCutoff(_capturePath, cutoffDate, filesToArchive);

            if (filesToArchive.Count > 0)
            {
                // Create ZIP archive
                using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
                {
                    foreach (var file in filesToArchive)
                    {
                        // Use relative path for entry name
                        var entryName = Path.GetRelativePath(_capturePath, file);
                        archive.CreateEntryFromFile(file, entryName);
                    }
                }

                // Delete archived files
                foreach (var file in filesToArchive)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Ignore
                    }
                }

                // Clean up empty directories
                CleanupEmptyDirectories(_capturePath);
            }
        });
    }

    private void CollectFilesForCutoff(string path, DateTime cutoffDate, System.Collections.Generic.List<string> files)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                var fileInfo = new FileInfo(file);
                if (cutoffDate == DateTime.MaxValue || fileInfo.LastWriteTime < cutoffDate)
                {
                    files.Add(file);
                }
            }
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                CollectFilesForCutoff(dir, cutoffDate, files);
            }
        }
        catch
        {
            // Ignore access errors
        }
    }

    private void CleanupEmptyDirectories(string path)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                CleanupEmptyDirectories(dir);
            }
            if (Directory.GetFiles(path).Length == 0 && Directory.GetDirectories(path).Length == 0)
            {
                Directory.Delete(path);
            }
        }
        catch
        {
            // Ignore
        }
    }
}
