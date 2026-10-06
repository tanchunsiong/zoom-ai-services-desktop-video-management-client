using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Data;
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
    private string _summaryText = "";
    private bool _isInitializing;
    private int? _lastAddedCount;
    private QueueMediaFilter _queueMediaFilter;
    private readonly QueueSearchIndex _queueSearchIndex = new();
    private CancellationTokenSource? _queueSearchCancellation;
    private HashSet<Guid>? _queueSearchMatches;
    private string _queueSearchText = "";
    private bool _isQueueSearchRunning;
    private readonly SemaphoreSlim _settingsSaveLock = new(1, 1);

    public MainWindowViewModel(
        JobQueueService queue,
        ICredentialVault vault,
        ISettingsStore settingsStore,
        VlcPlaybackService player,
        ILiveScribeClient liveScribeClient,
        IZoomAiClient zoomAiClient)
    {
        Queue = queue;
        _vault = vault;
        _settingsStore = settingsStore;
        Player = player;
        Live = new LiveModeViewModel(vault, liveScribeClient, zoomAiClient, SaveLiveSettingsAsync);
        TranslationLanguages = [new LanguageOption("", "No translation"), .. LanguageCatalog.Translation];
        SummaryOptions = [new SummaryOption(false, "Off"), new SummaryOption(true, "Summarize")];
        JobsView = CollectionViewSource.GetDefaultView(Jobs);
        JobsView.Filter = item => item is QueueJob job && MatchesQueueFilter(job);
        Jobs.CollectionChanged += Jobs_CollectionChanged;
        Queue.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsQueueRunning));
            OnPropertyChanged(nameof(IsQueueIdle));
            OnPropertyChanged(nameof(PauseResumeLabel));
        };
    }

    public JobQueueService Queue { get; }
    public VlcPlaybackService Player { get; }
    public LiveModeViewModel Live { get; }
    public ObservableCollection<QueueJob> Jobs => Queue.Jobs;
    public ICollectionView JobsView { get; }
    public IReadOnlyList<LanguageOption> SourceLanguages => LanguageCatalog.Transcription;
    public IReadOnlyList<LanguageOption> TranslationLanguages { get; }
    public IReadOnlyList<SummaryOption> SummaryOptions { get; }
    public ObservableCollection<TranscriptCue> ReviewCues { get; } = [];
    public UserSettings Settings { get; private set; } = new();
    public string ApiKey { get; private set; } = "";
    public bool IsQueueRunning => Queue.IsRunning;
    public bool IsQueueIdle => !Queue.IsRunning;
    public QueueJob? ActiveJob => Jobs.FirstOrDefault(job => job.CanEnd);
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
    public string EstimatedQueueScribeCostLabel => FormatAggregateCost(Jobs.Select(job => job.CostComparison.Estimate.ScribeUsd));
    public string EstimatedQueueTranslateCostLabel => FormatAggregateCost(Jobs.Select(job => job.CostComparison.Estimate.TranslateUsd));
    public string EstimatedQueueSummaryCostLabel => FormatAggregateCost(Jobs.Select(job => job.CostComparison.Estimate.SummarizeUsd));
    public string EstimatedQueueTotalCostLabel => FormatAggregateCost(Jobs.SelectMany(job =>
    {
        var estimate = job.CostComparison.Estimate;
        return new[] { estimate.ScribeUsd, estimate.TranslateUsd, estimate.SummarizeUsd };
    }));
    public string ActualQueueScribeCostLabel => FormatKnownCost(Jobs.Select(job => job.CostComparison.Actual.ScribeUsd));
    public string ActualQueueTranslateCostLabel => FormatKnownCost(Jobs.Select(job => job.CostComparison.Actual.TranslateUsd));
    public string ActualQueueSummaryCostLabel => FormatKnownCost(Jobs.Select(job => job.CostComparison.Actual.SummarizeUsd));
    public string ActualQueueTotalCostLabel => FormatKnownCost(Jobs.Select(job => job.CostComparison.Actual.TotalUsd));
    public string EstimatedQueueScribeTimeLabel => FormatAggregateTime(Jobs.Select(job => job.TimeComparison.Estimate.Scribe));
    public string EstimatedQueueTranslateTimeLabel => FormatAggregateTime(Jobs.Select(job => job.TimeComparison.Estimate.Translate));
    public string EstimatedQueueSummaryTimeLabel => FormatAggregateTime(Jobs.Select(job => job.TimeComparison.Estimate.Summarize));
    public string EstimatedQueueTotalTimeLabel => FormatAggregateTime(Jobs.Select(job => job.TimeComparison.Estimate.Total));
    public string ActualQueueScribeTimeLabel => FormatKnownTime(Jobs.Select(job => job.TimeComparison.Actual.Scribe));
    public string ActualQueueTranslateTimeLabel => FormatKnownTime(Jobs.Select(job => job.TimeComparison.Actual.Translate));
    public string ActualQueueSummaryTimeLabel => FormatKnownTime(Jobs.Select(job => job.TimeComparison.Actual.Summarize));
    public string ActualQueueTotalTimeLabel => FormatKnownTime(Jobs.Select(job => job.TimeComparison.Actual.Total));
    public int UnknownDurationCount => Jobs.Count(job => job.DurationSeconds is null);
    public int WithoutAudioCount => Jobs.Count(job => job.HasAudio == false);
    public int FailedCount => Jobs.Count(job => job.State == JobState.Failed);
    public bool HasFailedJobs => FailedCount > 0;
    public bool HasUnknownDurationJobs => UnknownDurationCount > 0;
    public bool HasWithoutAudioJobs => WithoutAudioCount > 0;
    public string UnknownDurationFilterLabel => $"Unknown {UnknownDurationCount:N0}";
    public string WithoutAudioFilterLabel => $"Without audio {WithoutAudioCount:N0}";
    public bool IsAllQueueFilter => _queueMediaFilter == QueueMediaFilter.All;
    public bool IsUnknownDurationFilter => _queueMediaFilter == QueueMediaFilter.UnknownDuration;
    public bool IsWithoutAudioFilter => _queueMediaFilter == QueueMediaFilter.WithoutAudio;
    public bool HasQueueSearch => !string.IsNullOrWhiteSpace(_queueSearchText);
    public bool IsQueueSearchRunning
    {
        get => _isQueueSearchRunning;
        private set => Set(ref _isQueueSearchRunning, value);
    }
    public string QueueSearchText
    {
        get => _queueSearchText;
        set
        {
            value ??= "";
            if (string.Equals(_queueSearchText, value, StringComparison.Ordinal)) return;
            _queueSearchText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasQueueSearch));
            BeginQueueSearch();
        }
    }
    public string QueueCountLabel => _queueMediaFilter == QueueMediaFilter.All && !HasQueueSearch
        ? $"{Jobs.Count:N0} jobs in queue"
        : $"Showing {Jobs.Count(MatchesQueueFilter):N0} of {Jobs.Count:N0} jobs";
    public string LastAddedCountLabel => _lastAddedCount is null ? "" : $"Last added: {_lastAddedCount:N0}";
    public string QueueDurationLabel
    {
        get
        {
            var knownSeconds = Jobs.Where(MatchesQueueFilter).Sum(job => job.DurationSeconds ?? 0);
            var duration = TimeSpan.FromSeconds(Math.Max(0, knownSeconds));
            return $"Media duration: {(int)duration.TotalHours:N0}h {duration.Minutes:00}m";
        }
    }
    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

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
        Live.InitializeSettings(
            Settings.LiveVocabularyJson,
            Settings.LiveTranslationLanguage,
            Settings.LiveFloatingCaptionTextSize);
        var credentials = await _vault.LoadAsync();
        ApiKey = credentials?.ApiKey ?? "";
        HasCredentials = credentials is { IsComplete: true };
        _isInitializing = true;
        try { await Queue.InitializeAsync(); }
        finally { _isInitializing = false; }
        RefreshQueueFilter();
        RefreshTimeCalibration();
        Notice = HasCredentials ? $"{Jobs.Count} job{(Jobs.Count == 1 ? "" : "s")} in the library" :
            "Add Zoom Build credentials in Settings before starting the queue";
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ApiKey));
    }

    public async Task<int> AddFilesAsync(IEnumerable<string> files)
    {
        _isInitializing = true;
        try { return await Queue.AddAsync(files, "en-US", null); }
        finally
        {
            _isInitializing = false;
            RefreshQueueFilter();
            RefreshTimeCalibration();
        }
    }

    public async Task SaveSettingsAsync(string apiKey, string apiSecret)
    {
        if (Settings.ScribeUsdPerMinute < 0 || Settings.TranslatorUsdPerMillionCharacters < 0 ||
            Settings.SummarizerUsdPerMillionCharacters < 0)
            throw new InvalidOperationException("Cost estimate rates cannot be negative.");
        if (Settings.EstimatedTranslationCharactersPerMinute != 0 &&
            Settings.EstimatedTranslationCharactersPerMinute is < 100 or > 10_000)
            throw new InvalidOperationException("Translation characters per minute must be zero for automatic or between 100 and 10,000.");
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
        await PersistSettingsAsync();
        _isInitializing = true;
        try
        {
            foreach (var job in Jobs) job.ConfigureCostEstimate(Settings);
        }
        finally { _isInitializing = false; }
        RefreshQueueFilter();
        RaiseQueueCostProperties();
        Notice = "Settings saved securely";
        OnPropertyChanged(nameof(ApiKey));
    }

    private async Task SaveLiveSettingsAsync(
        string vocabularyJson,
        string translationLanguage,
        double floatingCaptionTextSize)
    {
        Settings.LiveVocabularyJson = vocabularyJson;
        Settings.LiveTranslationLanguage = translationLanguage;
        Settings.LiveFloatingCaptionTextSize = Math.Clamp(floatingCaptionTextSize, 14.0, 96.0);
        await PersistSettingsAsync();
    }

    private async Task PersistSettingsAsync()
    {
        await _settingsSaveLock.WaitAsync();
        try { await _settingsStore.SaveAsync(Settings); }
        finally { _settingsSaveLock.Release(); }
    }

    public async Task<int> RemoveJobsAsync(IReadOnlyCollection<QueueJob> jobs)
    {
        _isInitializing = true;
        try
        {
            var removed = await Queue.RemoveManyAsync(jobs);
            if (SelectedJob is not null && jobs.Contains(SelectedJob)) SelectedJob = null;
            return removed;
        }
        finally
        {
            _isInitializing = false;
            RefreshQueueFilter();
            RefreshTimeCalibration();
        }
    }

    public void ReportFilesAdded(int count)
    {
        _lastAddedCount = count;
        OnPropertyChanged(nameof(LastAddedCountLabel));
    }

    public void SetQueueMediaFilter(QueueMediaFilter filter)
    {
        if (_queueMediaFilter == filter) return;
        _queueMediaFilter = filter;
        RefreshQueueView();
    }

    public async Task OpenForReviewAsync(QueueJob job, bool translated)
    {
        var subtitle = translated ? job.TranslatedVttPath : job.OriginalVttPath;
        if (translated && !File.Exists(subtitle))
            throw new InvalidOperationException("This job does not have translated captions yet. Choose a translation target in its queue row, then use Translate selected.");
        if (!File.Exists(subtitle)) throw new InvalidOperationException("This job does not have a subtitle file yet.");
        ReviewCues.Clear();
        foreach (var cue in WebVtt.Parse(await File.ReadAllTextAsync(subtitle))) ReviewCues.Add(cue);
        SummaryText = File.Exists(job.SummaryPath)
            ? await File.ReadAllTextAsync(job.SummaryPath)
            : "No summary was generated for this job.";
        ActiveCaptionText = "";
        HasReviewMedia = true;
        UpdatePlaybackDuration(TimeSpan.Zero);
        UpdatePlaybackPosition(TimeSpan.Zero);
        SelectedJob = job;
        await Player.OpenAsync(job, Settings);
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

    private static string FormatAggregateCost(IEnumerable<decimal?> costs)
    {
        var values = costs.ToArray();
        if (values.Length == 0) return JobCostEstimator.FormatUsd(0m);
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        if (known.Length == 0) return "--";
        var subtotal = JobCostEstimator.FormatUsd(known.Sum());
        return known.Length == values.Length ? subtotal : $"{subtotal}+";
    }

    private static string FormatKnownCost(IEnumerable<decimal?> costs)
    {
        var values = costs.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? "--" : JobCostEstimator.FormatUsd(values.Sum());
    }

    private static string FormatAggregateTime(IEnumerable<TimeSpan?> times)
    {
        var values = times.ToArray();
        if (values.Length == 0) return JobTimeEstimator.Format(TimeSpan.Zero);
        var known = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        if (known.Length == 0) return "--";
        var subtotal = JobTimeEstimator.Format(known.Aggregate(TimeSpan.Zero, (total, value) => total + value));
        return known.Length == values.Length ? subtotal : $"{subtotal}+";
    }

    private static string FormatKnownTime(IEnumerable<TimeSpan?> times)
    {
        var values = times.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return values.Length == 0
            ? "--"
            : JobTimeEstimator.Format(values.Aggregate(TimeSpan.Zero, (total, value) => total + value));
    }

    private void Jobs_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (QueueJob job in e.OldItems) job.PropertyChanged -= Job_PropertyChanged;
        if (e.NewItems is not null)
            foreach (QueueJob job in e.NewItems)
            {
                job.ConfigureCostEstimate(Settings);
                job.PropertyChanged += Job_PropertyChanged;
            }
        if (!_isInitializing)
        {
            RefreshQueueFilter();
            RefreshTimeCalibration();
        }
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (e.PropertyName == nameof(QueueJob.CompletedAt))
            RefreshTimeCalibration();
        else if (e.PropertyName is nameof(QueueJob.CostComparison) or nameof(QueueJob.TimeComparison))
            RaiseQueueCostProperties();
        if (e.PropertyName is nameof(QueueJob.DurationSeconds) or nameof(QueueJob.HasAudio) or nameof(QueueJob.State))
            RefreshQueueFilter();
        if (e.PropertyName == nameof(QueueJob.State))
            OnPropertyChanged(nameof(ActiveJob));
    }

    private bool MatchesQueueFilter(QueueJob job)
    {
        var matchesMediaFilter = _queueMediaFilter switch
        {
            QueueMediaFilter.UnknownDuration => job.DurationSeconds is null,
            QueueMediaFilter.WithoutAudio => job.HasAudio == false,
            _ => true
        };
        return matchesMediaFilter &&
               (_queueSearchMatches is null || _queueSearchMatches.Contains(job.Id));
    }

    private void RefreshQueueFilter()
    {
        if (HasQueueSearch)
        {
            BeginQueueSearch();
            return;
        }
        RefreshQueueView();
    }

    private void RefreshQueueView()
    {
        JobsView.Refresh();
        RaiseQueueFilterProperties();
    }

    private void BeginQueueSearch()
    {
        _queueSearchCancellation?.Cancel();
        var query = _queueSearchText.Trim();
        if (query.Length == 0)
        {
            _queueSearchMatches = null;
            IsQueueSearchRunning = false;
            RefreshQueueView();
            return;
        }

        var jobs = Jobs.ToArray();
        _queueSearchMatches = jobs
            .Where(job => job.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(job => job.Id)
            .ToHashSet();
        IsQueueSearchRunning = true;
        RefreshQueueView();

        var cancellation = new CancellationTokenSource();
        _queueSearchCancellation = cancellation;
        _ = CompleteQueueSearchAsync(jobs, query, cancellation);
    }

    private async Task CompleteQueueSearchAsync(
        IReadOnlyCollection<QueueJob> jobs,
        string query,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(200, cancellation.Token);
            var matches = await _queueSearchIndex.FindMatchesAsync(jobs, query, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            _queueSearchMatches = matches;
            RefreshQueueView();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_queueSearchCancellation, cancellation))
                Notice = $"Search could not read all sidecar files: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_queueSearchCancellation, cancellation))
            {
                _queueSearchCancellation = null;
                IsQueueSearchRunning = false;
            }
            cancellation.Dispose();
        }
    }

    private void RaiseQueueFilterProperties()
    {
        OnPropertyChanged(nameof(QueueCountLabel));
        OnPropertyChanged(nameof(QueueDurationLabel));
        OnPropertyChanged(nameof(UnknownDurationCount));
        OnPropertyChanged(nameof(WithoutAudioCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(HasFailedJobs));
        OnPropertyChanged(nameof(HasUnknownDurationJobs));
        OnPropertyChanged(nameof(HasWithoutAudioJobs));
        OnPropertyChanged(nameof(UnknownDurationFilterLabel));
        OnPropertyChanged(nameof(WithoutAudioFilterLabel));
        OnPropertyChanged(nameof(IsAllQueueFilter));
        OnPropertyChanged(nameof(IsUnknownDurationFilter));
        OnPropertyChanged(nameof(IsWithoutAudioFilter));
        OnPropertyChanged(nameof(HasQueueSearch));
    }

    private void RaiseQueueCostProperties()
    {
        OnPropertyChanged(nameof(EstimatedQueueScribeCostLabel));
        OnPropertyChanged(nameof(EstimatedQueueTranslateCostLabel));
        OnPropertyChanged(nameof(EstimatedQueueSummaryCostLabel));
        OnPropertyChanged(nameof(EstimatedQueueTotalCostLabel));
        OnPropertyChanged(nameof(ActualQueueScribeCostLabel));
        OnPropertyChanged(nameof(ActualQueueTranslateCostLabel));
        OnPropertyChanged(nameof(ActualQueueSummaryCostLabel));
        OnPropertyChanged(nameof(ActualQueueTotalCostLabel));
        OnPropertyChanged(nameof(EstimatedQueueScribeTimeLabel));
        OnPropertyChanged(nameof(EstimatedQueueTranslateTimeLabel));
        OnPropertyChanged(nameof(EstimatedQueueSummaryTimeLabel));
        OnPropertyChanged(nameof(EstimatedQueueTotalTimeLabel));
        OnPropertyChanged(nameof(ActualQueueScribeTimeLabel));
        OnPropertyChanged(nameof(ActualQueueTranslateTimeLabel));
        OnPropertyChanged(nameof(ActualQueueSummaryTimeLabel));
        OnPropertyChanged(nameof(ActualQueueTotalTimeLabel));
    }

    private void RefreshTimeCalibration()
    {
        var calibration = JobTimeCalibration.Learn(Jobs);
        var wasInitializing = _isInitializing;
        _isInitializing = true;
        try
        {
            foreach (var job in Jobs) job.ConfigureTimeCalibration(calibration);
        }
        finally
        {
            _isInitializing = wasInitializing;
        }
        RaiseQueueCostProperties();
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

public enum QueueMediaFilter
{
    All,
    UnknownDuration,
    WithoutAudio
}
