using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ZTranscribe.App.Services;
using ZTranscribe.App.ViewModels;
using ZTranscribe.Core.Models;

namespace ZTranscribe.App;

public partial class MainWindow : Window
{
    private QueueJob? _lastAutoScrolledJob;
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".mpg", ".mpeg",
        ".mod", ".3gp", ".3g2", ".mts", ".m2ts", ".ts", ".flv", ".vob", ".asf",
        ".wav", ".m4a", ".mp3", ".wma", ".aac", ".flac", ".ogg", ".opus", ".aiff", ".aif"
    };
    private static readonly EnumerationOptions RecursiveEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0
    };

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => ApplyMetroIconography();
        JobsGrid.ContextMenu.Opened += (_, _) => ApplyContextMenuIconography();
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.Player.PositionChanged += Player_PositionChanged;
        viewModel.Player.DurationChanged += Player_DurationChanged;
        viewModel.Player.PlaybackFailed += Player_PlaybackFailed;
        viewModel.Live.Segments.CollectionChanged += LiveSegments_CollectionChanged;
    }

    private void ApplyMetroIconography()
    {
        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (button.ToolTip is string toolTip && (toolTip is "Play" or "Pause"))
            {
                button.Style = (Style)FindResource("IconButton");
                button.Content = GlyphBlock(toolTip == "Play" ? "\uE768" : "\uE769");
                continue;
            }

            if (button.Content is not string label) continue;
            if (label.Contains("Add files", StringComparison.Ordinal))
            {
                button.Style = (Style)FindResource("HeaderPrimaryButton");
                button.Content = IconLabel("\uE710", "Add files");
            }
            else if (label == "Preview selected") button.Content = IconLabel("\uE7B3", label);
            else if (label == "Review selected") button.Content = IconLabel("\uE8A5", label);
            else if (label == "Translate selected") button.Content = IconLabel("\uE8FA", label);
            else if (label == "Summarize selected") button.Content = IconLabel("\uE8A5", label);
            else if (label == "Open output folder") button.Content = IconLabel("\uE8B7", label);
            else if (label == "Original captions") button.Content = IconLabel("\uE8A5", "Original");
            else if (label == "Translated captions") button.Content = IconLabel("\uE8FA", "Translated");
            else if (label == "Save settings") button.Content = IconLabel("\uE74E", "Save settings");
        }

        foreach (var tab in FindVisualChildren<TabItem>(this))
        {
            if (tab.Header is not string header) continue;
            if (header.Contains("Queue", StringComparison.Ordinal)) tab.Header = IconTabLabel("\uE8A5", "Queue");
            else if (header.Contains("Live", StringComparison.Ordinal)) tab.Header = IconTabLabel("\uE8D6", "Live");
            else if (header.Contains("Review", StringComparison.Ordinal)) tab.Header = IconTabLabel("\uE7B3", "Review");
            else if (header.Contains("Settings", StringComparison.Ordinal)) tab.Header = IconTabLabel("\uE713", "Settings");
        }

        ApplyGridIconography();
        ApplyContextMenuIconography();
        ScrollActiveJobIntoView();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.ActiveJob))
            Dispatcher.BeginInvoke(ScrollActiveJobIntoView);
    }

    private void ScrollActiveJobIntoView()
    {
        var job = ViewModel.ActiveJob;
        if (job is null)
        {
            _lastAutoScrolledJob = null;
            return;
        }
        if (ReferenceEquals(job, _lastAutoScrolledJob) || !JobsGrid.Items.Contains(job)) return;

        _lastAutoScrolledJob = job;
        JobsGrid.ScrollIntoView(job);
        JobsGrid.UpdateLayout();

        var viewer = FindVisualChildren<ScrollViewer>(JobsGrid)
            .FirstOrDefault(item => item.Name == "DG_ScrollViewer");
        var index = JobsGrid.Items.IndexOf(job);
        if (viewer is null || index < 0 || viewer.ViewportHeight <= 0) return;

        var halfViewport = viewer.CanContentScroll
            ? Math.Floor(viewer.ViewportHeight / 2)
            : Math.Floor(viewer.ViewportHeight / Math.Max(1, JobsGrid.RowHeight) / 2);
        var targetOffset = viewer.CanContentScroll
            ? Math.Max(0, index - halfViewport)
            : Math.Max(0, index * JobsGrid.RowHeight - viewer.ViewportHeight / 2 + JobsGrid.RowHeight / 2);
        viewer.ScrollToVerticalOffset(targetOffset);
    }

    private void JobsGrid_Loaded(object sender, RoutedEventArgs e) => ApplyGridIconography();

    private void ApplyGridIconography()
    {
        var glyphs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Media"] = "\uE714",
            ["Spoken language"] = "\uE8D6",
            ["Translation"] = "\uE8FA",
            ["Summary"] = "\uE8A5",
            ["Stage"] = "\uE946",
            ["Progress"] = "\uE9D9",
            ["Update"] = "\uE90A",
            ["Duration"] = "\uE823",
            ["Estimate (USD)"] = "\uE8C7",
            ["Actual (USD)"] = "\uE73E",
            ["Estimate (time)"] = "\uE823",
            ["Actual (time)"] = "\uE73E",
            ["Actions"] = "\uE712"
        };

        foreach (var header in FindVisualChildren<DataGridColumnHeader>(JobsGrid))
        {
            if (header.Content is string label && glyphs.TryGetValue(label, out var glyph))
                header.Content = IconLabel(glyph, label);
        }
    }

    private void ApplyContextMenuIconography()
    {
        if (JobsGrid.ContextMenu is not { } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Header is not string label) continue;
            item.Icon = label switch
            {
                "Preview media" => GlyphBlock("\uE7B3"),
                "Review" => GlyphBlock("\uE8A5"),
                "Retry" => GlyphBlock("\uE72C"),
                "Retry all failed" => GlyphBlock("\uE72C"),
                "Remove selected" => GlyphBlock("\uE738"),
                _ => item.Icon
            };
        }
    }

    private static StackPanel IconLabel(string glyph, string label) =>
        new()
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                GlyphBlock(glyph),
                new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0) }
            }
        };

    private static StackPanel IconTabLabel(string glyph, string label) =>
        new()
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children =
            {
                new TextBlock
                {
                    Text = glyph,
                    FontFamily = (FontFamily?)Application.Current.TryFindResource("IconFont") ?? new FontFamily("Segoe Fluent Icons"),
                    FontSize = 20,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                new TextBlock
                {
                    Text = label.ToUpperInvariant(),
                    FontFamily = (FontFamily?)Application.Current.TryFindResource("DisplayFont") ?? new FontFamily("Arial"),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center
                }
            }
        };

    private static TextBlock GlyphBlock(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily?)Application.Current.TryFindResource("IconFont") ?? new FontFamily("Segoe Fluent Icons"),
        FontSize = 16,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add media to Z Scribe",
            Multiselect = true,
            Filter = "Media files|*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.mpg;*.mpeg;*.mod;*.3gp;*.3g2;*.mts;*.m2ts;*.ts;*.flv;*.vob;*.asf;*.wav;*.m4a;*.mp3;*.wma;*.aac;*.flac;*.ogg;*.opus;*.aiff;*.aif|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) await AddAsync(dialog.FileNames, recursive: false);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a media folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var files = Directory.EnumerateFiles(dialog.FolderName, "*", RecursiveEnumeration)
            .Where(x => MediaExtensions.Contains(Path.GetExtension(x)));
        await AddAsync(files, recursive: true);
    }

    private async Task AddAsync(IEnumerable<string> paths, bool recursive)
    {
        try
        {
            var files = paths.Where(x => MediaExtensions.Contains(Path.GetExtension(x))).ToArray();
            var added = await ViewModel.AddFilesAsync(files);
            ViewModel.ReportFilesAdded(added);
            ViewModel.Notice = $"Added {added:N0} media file{(added == 1 ? "" : "s")}" +
                $"{(recursive ? " recursively" : "")}; {ViewModel.Jobs.Count:N0} jobs in queue";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Notice = "Queue is running";
            await ViewModel.Queue.StartAsync();
            ViewModel.Notice = "Queue stopped";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Queue.IsPaused)
        {
            ViewModel.Queue.Resume();
            ViewModel.Notice = "Queue resumed";
        }
        else
        {
            ViewModel.Queue.Pause();
            ViewModel.Notice = "Queue will pause before the next job";
        }
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SetQueueMediaFilter(QueueMediaFilter.All);

    private void FilterUnknownDuration_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SetQueueMediaFilter(QueueMediaFilter.UnknownDuration);

    private void FilterWithoutAudio_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SetQueueMediaFilter(QueueMediaFilter.WithoutAudio);

    private void MicrophoneSource_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Live.SetAudioSource(LiveAudioSource.Microphone);

    private void SpeakerLoopbackSource_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Live.SetAudioSource(LiveAudioSource.SpeakerLoopback);

    private void RefreshAudioDevices_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Live.RefreshAudioDevices();

    private async void StartLive_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.Live.StartAsync();

    private async void StopLive_Click(object sender, RoutedEventArgs e) =>
        await ViewModel.Live.StopAsync();

    private void ClearLiveTranscript_Click(object sender, RoutedEventArgs e) =>
        ViewModel.Live.ClearTranscript();

    private void CopyLiveTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.Live.TranscriptText)) return;
        Clipboard.SetText(ViewModel.Live.TranscriptText);
        ViewModel.Notice = "Live transcript copied";
    }

    private void LiveSegments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel.Live.Segments.LastOrDefault() is not { } latest) return;
        Dispatcher.BeginInvoke(() => LiveTranscriptList.ScrollIntoView(latest));
    }

    private void ClearQueueSearch_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.QueueSearchText = "";
        QueueSearchBox.Focus();
    }

    private void QueueSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ViewModel.QueueSearchText = "";
        e.Handled = true;
    }

    private async void JobsGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedJob is { CanReview: true }) await ReviewSelectedAsync(false);
        else PreviewSelected();
    }
    private void PreviewSelected_Click(object sender, RoutedEventArgs e) => PreviewSelected();
    private async void Review_Click(object sender, RoutedEventArgs e) => await ReviewSelectedAsync(false);
    private async void OriginalCaptions_Click(object sender, RoutedEventArgs e) => await ReviewSelectedAsync(false);
    private async void TranslatedCaptions_Click(object sender, RoutedEventArgs e) => await ReviewSelectedAsync(true);

    private async Task ReviewSelectedAsync(bool translated)
    {
        if (ViewModel.SelectedJob is not { } job) return;
        try
        {
            await ViewModel.OpenForReviewAsync(job, translated);
            ReviewTab.IsSelected = true;
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void StartCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: QueueJob job }) return;
        try
        {
            ViewModel.Notice = $"Starting {job.DisplayName}";
            await ViewModel.Queue.StartJobAsync(job);
            ViewModel.Notice = job.StatusMessage;
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void EndCurrent_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Queue.CancelCurrent();
        ViewModel.Notice = "Ending the active job";
    }

    private async void RetryCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: QueueJob job }) return;
        try
        {
            await ViewModel.Queue.RetryAsync(job);
            ViewModel.Notice = $"Retrying {job.DisplayName}";
            await ViewModel.Queue.StartJobAsync(job);
            ViewModel.Notice = job.StatusMessage;
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void RetryJobMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedJob is not { CanRetry: true } job) return;
        try
        {
            await ViewModel.Queue.RetryAsync(job);
            ViewModel.Notice = $"Retrying {job.DisplayName}";
            await ViewModel.Queue.StartJobAsync(job);
            ViewModel.Notice = job.StatusMessage;
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void RetryAllFailedMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var count = await ViewModel.Queue.RetryAllFailedAsync();
            if (count == 0)
            {
                ViewModel.Notice = "There are no failed jobs to retry";
                return;
            }

            ViewModel.Notice = $"Retrying {count:N0} failed job{(count == 1 ? "" : "s")}";
            await ViewModel.Queue.StartAsync();
            ViewModel.Notice = "Retry all failed completed";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void TranslateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedJob is not { } job) return;
        try
        {
            ViewModel.Notice = "Translating the existing transcript";
            await ViewModel.Queue.TranslateExistingAsync(job);
            ViewModel.Notice = job.StatusMessage;
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void SummarizeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedJob is not { } job) return;
        try
        {
            ViewModel.Notice = "Summarizing the existing transcript";
            await ViewModel.Queue.SummarizeExistingAsync(job, force: true);
            ViewModel.Notice = job.StatusMessage;
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void JobLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { IsKeyboardFocusWithin: true, DataContext: QueueJob job, SelectedItem: LanguageOption option } comboBox)
            return;

        try
        {
            if (Equals(comboBox.Tag, "source"))
                await ViewModel.Queue.UpdateSourceLanguageAsync(job, option.Locale);
            else
                await ViewModel.Queue.UpdateTranslationLanguageAsync(job, option.Locale);
        }
        catch (Exception exception)
        {
            comboBox.SelectedValue = Equals(comboBox.Tag, "source")
                ? job.SourceLanguage
                : job.TranslationLanguage;
            ShowError(exception);
        }
    }

    private async void JobSummary_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox
            {
                IsKeyboardFocusWithin: true,
                DataContext: QueueJob job,
                SelectedItem: SummaryOption option
            }) return;

        try { await ViewModel.Queue.UpdateSummarizeAsync(job, option.Enabled); }
        catch (Exception exception)
        {
            ((ComboBox)sender).SelectedValue = job.Summarize;
            ShowError(exception);
        }
    }

    private async void RemoveJobMenuItem_Click(object sender, RoutedEventArgs e)
        => await RemoveSelectedJobsAsync();

    private async Task RemoveSelectedJobsAsync()
    {
        var jobs = JobsGrid.SelectedItems.OfType<QueueJob>().ToArray();
        if (jobs.Length == 0 && ViewModel.SelectedJob is { } selected) jobs = [selected];
        if (jobs.Length == 0) return;

        if (jobs.Length > 1 && MessageBox.Show(
                this,
                $"Remove {jobs.Length} selected jobs from the queue?\n\nMedia files and generated outputs will not be deleted.",
                "Remove selected jobs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var removed = await ViewModel.RemoveJobsAsync(jobs);
            ViewModel.Notice = $"Removed {removed} job{(removed == 1 ? "" : "s")} from the queue";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void ReviewJobMenuItem_Click(object sender, RoutedEventArgs e) =>
        await ReviewSelectedAsync(false);

    private void PreviewJobMenuItem_Click(object sender, RoutedEventArgs e) => PreviewSelected();

    private void PreviewSelected()
    {
        if (ViewModel.SelectedJob is not { } job) return;
        if (!File.Exists(job.SourcePath))
        {
            ShowError(new FileNotFoundException("The source media file is no longer available.", job.SourcePath));
            return;
        }

        new MediaPreviewWindow(job, ViewModel.Settings) { Owner = this }.Show();
    }

    private void JobsGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(JobsGrid, e.OriginalSource as DependencyObject) is DataGridRow row)
        {
            if (!row.IsSelected)
            {
                JobsGrid.SelectedItems.Clear();
                row.IsSelected = true;
            }
            JobsGrid.CurrentItem = row.Item;
            row.Focus();
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var path = ViewModel.SelectedJob?.OriginalVttPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.SaveSettingsAsync(ApiKeyBox.Text, ApiSecretBox.Password);
            ApiSecretBox.Clear();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void CueList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(CueList, e.OriginalSource as DependencyObject) is not ListBoxItem item ||
            item.DataContext is not TranscriptCue cue) return;

        CueList.SelectedItem = cue;
        ViewModel.SeekToCue(cue);
    }

    private void Player_PositionChanged(TimeSpan position) =>
        Dispatcher.BeginInvoke(() => ViewModel.UpdatePlaybackPosition(position));

    private void Player_DurationChanged(TimeSpan duration) =>
        Dispatcher.BeginInvoke(() => ViewModel.UpdatePlaybackDuration(duration));

    private void Player_PlaybackFailed(string message) =>
        Dispatcher.BeginInvoke(() => ViewModel.Notice = message);

    private void Play_Click(object sender, RoutedEventArgs e) => ViewModel.Player.Play();

    private void PausePlayback_Click(object sender, RoutedEventArgs e) => ViewModel.Player.Pause();

    private void PlaybackRate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string value } ||
            !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)) return;

        ViewModel.Player.SetRate(rate);
        ViewModel.Notice = $"Playback speed: {value}x";
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var files = paths.SelectMany(path => Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", RecursiveEnumeration)
            : [path]);
        await AddAsync(files, recursive: paths.Any(Directory.Exists));
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.Player.PositionChanged -= Player_PositionChanged;
        ViewModel.Player.DurationChanged -= Player_DurationChanged;
        ViewModel.Player.PlaybackFailed -= Player_PlaybackFailed;
        ViewModel.Live.Segments.CollectionChanged -= LiveSegments_CollectionChanged;
        ViewModel.Live.Abort();
        ViewModel.Queue.StopAll();
        base.OnClosed(e);
    }

    private void ShowError(Exception exception)
    {
        ViewModel.Notice = exception.Message;
        MessageBox.Show(this, exception.Message, "Z Scribe", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
