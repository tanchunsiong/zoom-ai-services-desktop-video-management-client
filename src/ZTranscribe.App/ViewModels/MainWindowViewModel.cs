using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ZTranscribe.App.Services;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;
using ZTranscribe.Infrastructure.Queue;

namespace ZTranscribe.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly ICredentialVault _vault;
    private readonly ISettingsStore _settingsStore;
    private QueueJob? _selectedJob;
    private string _notice = "Ready";
    private bool _hasCredentials;
    private string _activeCaptionText = "";
    private bool _hasReviewMedia;
    private bool _isUpdatingPlaybackPosition;
    private double _playbackPositionSeconds;
    private double _playbackDurationSeconds;

    public MainWindowViewModel(
        JobQueueService queue,
        ICredentialVault vault,
        ISettingsStore settingsStore,
        VlcPlaybackService player)
    {
        Queue = queue;
        _vault = vault;
        _settingsStore = settingsStore;
        Player = player;
        TranslationLanguages = [new LanguageOption("", "No translation"), .. LanguageCatalog.Translation];
        Queue.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsQueueRunning));
            OnPropertyChanged(nameof(IsQueueIdle));
            OnPropertyChanged(nameof(PauseResumeLabel));
        };
    }

    public JobQueueService Queue { get; }
    public VlcPlaybackService Player { get; }
    public ObservableCollection<QueueJob> Jobs => Queue.Jobs;
    public IReadOnlyList<LanguageOption> SourceLanguages => LanguageCatalog.Transcription;
    public IReadOnlyList<LanguageOption> TranslationLanguages { get; }
    public ObservableCollection<TranscriptCue> ReviewCues { get; } = [];
    public UserSettings Settings { get; private set; } = new();
    public string ApiKey { get; private set; } = "";
    public bool IsQueueRunning => Queue.IsRunning;
    public bool IsQueueIdle => !Queue.IsRunning;
    public string PauseResumeLabel => Queue.IsPaused ? "Resume" : "Pause";
    public string ActiveCaptionText
    {
        get => _activeCaptionText;
        private set => Set(ref _activeCaptionText, value);
    }
    public bool HasReviewMedia
    {
        get => _hasReviewMedia;
        private set => Set(ref _hasReviewMedia, value);
    }
    public double PlaybackPositionSeconds
    {
        get => _playbackPositionSeconds;
        set
        {
            var maximum = _playbackDurationSeconds > 0 ? _playbackDurationSeconds : double.MaxValue;
            var position = Math.Clamp(value, 0, maximum);
            if (Math.Abs(_playbackPositionSeconds - position) < 0.01) return;
            _playbackPositionSeconds = position;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaybackPositionLabel));
            if (!_isUpdatingPlaybackPosition)
                Player.Seek(TimeSpan.FromSeconds(position));
        }
    }
    public double PlaybackDurationSeconds => Math.Max(1, _playbackDurationSeconds);
    public string PlaybackPositionLabel => FormatPlaybackTime(_playbackPositionSeconds);
    public string PlaybackDurationLabel => FormatPlaybackTime(_playbackDurationSeconds);

    public QueueJob? SelectedJob
    {
        get => _selectedJob;
        set => Set(ref _selectedJob, value);
    }

    public string Notice
    {
        get => _notice;
        set => Set(ref _notice, value);
    }

    public bool HasCredentials
    {
        get => _hasCredentials;
        set => Set(ref _hasCredentials, value);
    }

    public async Task InitializeAsync()
    {
        Settings = await _settingsStore.LoadAsync();
        var credentials = await _vault.LoadAsync();
        ApiKey = credentials?.ApiKey ?? "";
        HasCredentials = credentials is { IsComplete: true };
        await Queue.InitializeAsync();
        Notice = HasCredentials ? $"{Jobs.Count} job{(Jobs.Count == 1 ? "" : "s")} in the library" :
            "Add Zoom Build credentials in Settings before starting the queue";
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ApiKey));
    }

    public Task AddFilesAsync(IEnumerable<string> files) => Queue.AddAsync(files, "en-US", null);

    public async Task SaveSettingsAsync(string apiKey, string apiSecret)
    {
        if (!string.IsNullOrWhiteSpace(apiSecret))
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("API key is required when saving a new secret.");
            await _vault.SaveAsync(new ApiCredentials(apiKey.Trim(), apiSecret));
            ApiKey = apiKey.Trim();
            HasCredentials = true;
        }
        else if (!string.Equals(apiKey.Trim(), ApiKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Enter the API secret when changing the API key.");
        }
        await _settingsStore.SaveAsync(Settings);
        Notice = "Settings saved securely";
        OnPropertyChanged(nameof(ApiKey));
    }

    public async Task OpenForReviewAsync(QueueJob job, bool translated)
    {
        var subtitle = translated ? job.TranslatedVttPath : job.OriginalVttPath;
        if (translated && !File.Exists(subtitle))
            throw new InvalidOperationException("This job does not have translated captions yet. Choose a translation target in its queue row, then use Translate selected.");
        if (!File.Exists(subtitle)) throw new InvalidOperationException("This job does not have a subtitle file yet.");
        ReviewCues.Clear();
        foreach (var cue in WebVtt.Parse(await File.ReadAllTextAsync(subtitle))) ReviewCues.Add(cue);
        ActiveCaptionText = "";
        HasReviewMedia = true;
        UpdatePlaybackDuration(TimeSpan.Zero);
        UpdatePlaybackPosition(TimeSpan.Zero);
        SelectedJob = job;
        Player.Open(job, subtitle);
        Notice = $"Reviewing {job.DisplayName}";
    }

    public void SeekToCue(TranscriptCue cue)
    {
        ActiveCaptionText = cue.Text;
        Player.Seek(cue.Start, playIfPaused: true);
    }

    public void UpdatePlaybackPosition(TimeSpan position)
    {
        _isUpdatingPlaybackPosition = true;
        PlaybackPositionSeconds = position.TotalSeconds;
        _isUpdatingPlaybackPosition = false;
        var cue = ReviewCues.FirstOrDefault(item => item.Start <= position && position < item.End);
        ActiveCaptionText = cue?.Text ?? "";
    }

    public void UpdatePlaybackDuration(TimeSpan duration)
    {
        _playbackDurationSeconds = Math.Max(0, duration.TotalSeconds);
        OnPropertyChanged(nameof(PlaybackDurationSeconds));
        OnPropertyChanged(nameof(PlaybackDurationLabel));
    }

    private static string FormatPlaybackTime(double totalSeconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
