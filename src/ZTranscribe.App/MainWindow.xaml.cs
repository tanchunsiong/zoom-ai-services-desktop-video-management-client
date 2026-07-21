using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using ZTranscribe.App.ViewModels;
using ZTranscribe.Core.Models;

namespace ZTranscribe.App;

public partial class MainWindow : Window
{
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
        viewModel.Player.PositionChanged += Player_PositionChanged;
        viewModel.Player.DurationChanged += Player_DurationChanged;
        viewModel.Player.PlaybackFailed += Player_PlaybackFailed;
    }

    private void ApplyMetroIconography()
    {
        foreach (var button in FindVisualChildren<Button>(this))
        {
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
        }

        foreach (var tab in FindVisualChildren<TabItem>(this))
        {
            if (tab.Header is not string header) continue;
            if (header.Contains("Queue", StringComparison.Ordinal)) tab.Header = IconLabel("\uE8A5", "Queue");
            else if (header.Contains("Review", StringComparison.Ordinal)) tab.Header = IconLabel("\uE7B3", "Review");
            else if (header.Contains("Settings", StringComparison.Ordinal)) tab.Header = IconLabel("\uE713", "Settings");
        }

        ApplyContextMenuIconography();
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

    private static TextBlock GlyphBlock(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 15,
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
            ShellTabs.SelectedIndex = 1;
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
            await ViewModel.Queue.SummarizeExistingAsync(job);
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

        new MediaPreviewWindow(job) { Owner = this }.Show();
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
        ViewModel.Player.PositionChanged -= Player_PositionChanged;
        ViewModel.Player.DurationChanged -= Player_DurationChanged;
        ViewModel.Player.PlaybackFailed -= Player_PlaybackFailed;
        ViewModel.Queue.StopAll();
        base.OnClosed(e);
    }

    private void ShowError(Exception exception)
    {
        ViewModel.Notice = exception.Message;
        MessageBox.Show(this, exception.Message, "Z Scribe", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
