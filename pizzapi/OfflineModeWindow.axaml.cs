using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using pizzalib;

namespace pizzapi;

public partial class OfflineModeWindow : Window
{
    private Settings _settings;
    private string? _selectedPath;
    private List<TranscribedCall>? _loadedCalls;

    public string? SelectedPath => _selectedPath;
    public List<TranscribedCall>? LoadedCalls => _loadedCalls;

    public OfflineModeWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        SetupEventHandlers();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void SetupEventHandlers()
    {
        var cancelBtn = this.FindControl<Button>("CancelButton");
        var openBtn = this.FindControl<Button>("OpenButton");
        var browseBtn = this.FindControl<Button>("BrowseButton");
        var folderPathTextBox = this.FindControl<TextBox>("FolderPathTextBox");

        if (cancelBtn != null)
            cancelBtn.Click += (s, e) => Close();

        if (openBtn != null)
            openBtn.Click += OnOpenClicked;

        if (browseBtn != null)
            browseBtn.Click += OnBrowseClicked;

        // Update Open button state based on folder selection
        if (folderPathTextBox != null)
            folderPathTextBox.PropertyChanged += (s, e) =>
            {
                if (openBtn != null && e.Property.Name == nameof(TextBox.Text))
                {
                    openBtn.IsEnabled = !string.IsNullOrEmpty(folderPathTextBox.Text);
                }
            };
    }

    private async void OnBrowseClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider == null)
        {
            ShowError("Could not access file picker");
            return;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select offline capture folder",
            AllowMultiple = false
        });

        var folder = folders?.FirstOrDefault();
        if (folder != null)
        {
            _selectedPath = folder.Path.LocalPath;
            var folderPathTextBox = this.FindControl<TextBox>("FolderPathTextBox");
            if (folderPathTextBox != null)
                folderPathTextBox.Text = _selectedPath;
        }
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedPath))
        {
            ShowError("Please select a folder");
            return;
        }

        try
        {
            // Show progress UI
            var progressBorder = this.FindControl<Border>("ProgressBorder");
            var progressText = this.FindControl<TextBlock>("ProgressText");
            var progressBar = this.FindControl<ProgressBar>("ProgressBar");
            var openButton = this.FindControl<Button>("OpenButton");
            var cancelButton = this.FindControl<Button>("CancelButton");

            if (progressBorder != null) progressBorder.IsVisible = true;
            if (openButton != null) openButton.IsEnabled = false;
            if (cancelButton != null) cancelButton.IsEnabled = false;

            // Load offline captures
            _loadedCalls = new List<TranscribedCall>();

            using (var offlineManager = new OfflineCallManager(_selectedPath!,
                (TranscribedCall call) =>
                {
                    _loadedCalls!.Add(call);
                }))
            {
                // Setup progress callbacks
                _settings.UpdateProgressLabelCallback = (message) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (progressText != null)
                            progressText.Text = message;
                    });
                };

                _settings.SetProgressBarCallback = (total, current) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (progressBar != null)
                        {
                            progressBar.Maximum = total;
                            progressBar.Value = current;
                        }
                    });
                };

                _settings.ProgressBarStepCallback = () =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (progressBar != null)
                            progressBar.Value++;
                    });
                };

                if (!await offlineManager.Initialize(_settings))
                {
                    throw new Exception("Failed to initialize OfflineCallManager");
                }

                await offlineManager.Start();
                
                // Debug: log how many calls were loaded
                System.Diagnostics.Debug.WriteLine($"[OfflineModeWindow] Loaded {_loadedCalls.Count} calls");
            }

            // Verify we have calls before closing
            if (_loadedCalls.Count == 0)
            {
                ShowError("No calls were loaded from the selected folder. Check the logs for errors.");
                return;
            }

            // Success - close dialog
            Close();
        }
        catch (Exception ex)
        {
            ShowError($"Failed to load offline records: {ex.Message}");

            // Hide progress UI
            var progressBorder = this.FindControl<Border>("ProgressBorder");
            var openButton = this.FindControl<Button>("OpenButton");
            var cancelButton = this.FindControl<Button>("CancelButton");

            if (progressBorder != null) progressBorder.IsVisible = false;
            if (openButton != null) openButton.IsEnabled = true;
            if (cancelButton != null) cancelButton.IsEnabled = true;
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

        errorWindow.ShowDialog(this);
    }
}
