using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ZTranscribe.App.ViewModels;
using ZTranscribe.Core.Models;

namespace ZTranscribe.App;

public partial class MainWindow : Window
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm", ".wav", ".m4a", ".mp3" };

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add media to Z Transcribe",
            Multiselect = true,
            Filter = "Media files|*.mp4;*.m4v;*.mov;*.mkv;*.avi;*.webm;*.wav;*.m4a;*.mp3|All files|*.*"
        };
        if (dialog.ShowDialog(this) == true) await AddAsync(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Add a media folder", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var files = Directory.EnumerateFiles(dialog.FolderName, "*", SearchOption.AllDirectories)
            .Where(x => MediaExtensions.Contains(Path.GetExtension(x)));
        await AddAsync(files);
    }

    private async Task AddAsync(IEnumerable<string> paths)
    {
        try
        {
            var files = paths.Where(x => MediaExtensions.Contains(Path.GetExtension(x))).ToArray();
            await ViewModel.AddFilesAsync(files);
            ViewModel.Notice = $"Added {files.Length} media file{(files.Length == 1 ? "" : "s")}";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void StartQueue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ViewModel.Notice = "Queue is running";
            await ViewModel.Queue.StartAsync();
            ViewModel.Notice = "Queue stopped";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void CancelQueue_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.Queue.CancelCurrent();
        ViewModel.Notice = "Canceling the active job";
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

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedJob is not { } job) return;
        try { await ViewModel.Queue.RetryAsync(job); }
        catch (Exception exception) { ShowError(exception); }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedJob is not { } job) return;
        try { await ViewModel.Queue.RemoveAsync(job); }
        catch (Exception exception) { ShowError(exception); }
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

    private void CueList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CueList.SelectedItem is TranscriptCue cue) ViewModel.Player.Seek(cue.Start);
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
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            : [path]);
        await AddAsync(files);
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Queue.CancelCurrent();
        base.OnClosed(e);
    }

    private void ShowError(Exception exception)
    {
        ViewModel.Notice = exception.Message;
        MessageBox.Show(this, exception.Message, "Z Transcribe", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

