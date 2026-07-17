using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        viewModel.Player.PositionChanged += Player_PositionChanged;
        viewModel.Player.DurationChanged += Player_DurationChanged;
        viewModel.Player.PlaybackFailed += Player_PlaybackFailed;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add media to Z Transcribe",
            Multiselect = true,
            Filter = "Media files|*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.mpg;*.mpeg;*.mod;*.3gp;*.3g2;*.mts;*.m2ts;*.ts;*.flv;*.vob;*.asf;*.wav;*.m4a;*.mp3;*.wma;*.aac;*.flac;*.ogg;*.opus;*.aiff;*.aif|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) await AddAsync(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a media folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var files = Directory.EnumerateFiles(dialog.FolderName, "*", RecursiveEnumeration)
            .Where(x => MediaExtensions.Contains(Path.GetExtension(x)));
        await AddAsync(files);
    }

    private async Task AddAsync(IEnumerable<string> paths)
    {
        try
        {
            var files = paths.Where(x => MediaExtensions.Contains(Path.GetExtension(x))).ToArray();
            var added = await ViewModel.AddFilesAsync(files);
            var duplicates = files.Length - added;
            ViewModel.Notice = duplicates > 0
                ? $"Added {added} new media file{(added == 1 ? "" : "s")}; {duplicates} already in the queue"
                : $"Added {added} media file{(added == 1 ? "" : "s")}";
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

    private async void JobsGrid_DoubleClick(object sender, MouseButtonEventArgs e) => await ReviewSelectedAsync(false);
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

    private async void JobDoubleSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: QueueJob job } checkBox) return;
        var requested = checkBox.IsChecked == true;
        try { await ViewModel.Queue.UpdateDoubleSpeedAsync(job, requested); }
        catch (Exception exception)
        {
            checkBox.IsChecked = job.UseDoubleSpeed;
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
        await AddAsync(files);
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
        MessageBox.Show(this, exception.Message, "Z Transcribe", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
