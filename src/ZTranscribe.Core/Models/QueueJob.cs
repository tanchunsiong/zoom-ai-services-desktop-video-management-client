using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using ZTranscribe.Core.Services;

namespace ZTranscribe.Core.Models;

public enum JobState
{
    Queued,
    Preparing,
    Transcribing,
    Translating,
    Summarizing,
    Ready,
    Failed,
    Canceled
}

public sealed record JobEvent(DateTimeOffset At, JobState Stage, string Message);

public sealed class QueueJob : INotifyPropertyChanged
{
    private JobState _state = JobState.Queued;
    private int _progress;
    private string _statusMessage = "Waiting in queue";
    private string? _error;
    private string _sourceLanguage = "en-US";
    private string _translationLanguage = "";
    private double? _durationSeconds;
    private long _transcriptCharacters;
    private long _translationInputCharacters;
    private long _translationOutputCharacters;
    private bool _summarize;
    private long _summaryInputCharacters;
    private long _summaryOutputCharacters;
    private bool? _hasAudio;
    private string? _mediaProbeError;
    private bool _reuseExistingTranscript;
    private bool _reuseExistingTranslation;
    private bool _reuseExistingSummary;
    private bool _existingSummaryIsStale;
    private UserSettings _costSettings = new();

    public Guid Id { get; init; } = Guid.NewGuid();
    public required string SourcePath { get; init; }
    public string DisplayName => Path.GetFileName(SourcePath);
    [JsonIgnore]
    public string MediaGlyph => Path.GetExtension(SourcePath).ToLowerInvariant() is
        ".wav" or ".m4a" or ".mp3" or ".wma" or ".aac" or ".flac" or ".ogg" or ".opus" or ".aiff" or ".aif"
        ? "\uE8D6"
        : "\uE714";
    public string SourceLanguage
    {
        get => _sourceLanguage;
        set
        {
            if (Set(ref _sourceLanguage, value)) NotifyCostChanged();
        }
    }
    public string TranslationLanguage
    {
        get => _translationLanguage;
        set
        {
            if (Set(ref _translationLanguage, value ?? "")) NotifyCostChanged();
        }
    }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public double? DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (Set(ref _durationSeconds, value)) NotifyCostChanged();
        }
    }
    public bool? HasAudio
    {
        get => _hasAudio;
        set
        {
            if (Set(ref _hasAudio, value)) NotifyCostChanged();
        }
    }
    public string? MediaProbeError
    {
        get => _mediaProbeError;
        set
        {
            if (Set(ref _mediaProbeError, value)) OnPropertyChanged(nameof(DurationStatusLabel));
        }
    }
    public bool ReuseExistingTranscript
    {
        get => _reuseExistingTranscript;
        set
        {
            if (Set(ref _reuseExistingTranscript, value)) NotifyCostChanged();
        }
    }
    public bool ReuseExistingTranslation
    {
        get => _reuseExistingTranslation;
        set
        {
            if (Set(ref _reuseExistingTranslation, value)) NotifyCostChanged();
        }
    }
    public bool ReuseExistingSummary
    {
        get => _reuseExistingSummary;
        set
        {
            if (Set(ref _reuseExistingSummary, value)) NotifyCostChanged();
        }
    }
    public bool ExistingSummaryIsStale
    {
        get => _existingSummaryIsStale;
        set => Set(ref _existingSummaryIsStale, value);
    }
    public long TranscriptCharacters
    {
        get => _transcriptCharacters;
        set
        {
            if (Set(ref _transcriptCharacters, Math.Max(0, value))) NotifyCostChanged();
        }
    }
    public long TranslationInputCharacters
    {
        get => _translationInputCharacters;
        set
        {
            if (Set(ref _translationInputCharacters, Math.Max(0, value))) NotifyCostChanged();
        }
    }
    public long TranslationOutputCharacters
    {
        get => _translationOutputCharacters;
        set
        {
            if (Set(ref _translationOutputCharacters, Math.Max(0, value))) NotifyCostChanged();
        }
    }
    public bool Summarize
    {
        get => _summarize;
        set
        {
            if (Set(ref _summarize, value)) NotifyCostChanged();
        }
    }
    public long SummaryInputCharacters
    {
        get => _summaryInputCharacters;
        set
        {
            if (Set(ref _summaryInputCharacters, Math.Max(0, value))) NotifyCostChanged();
        }
    }
    public long SummaryOutputCharacters
    {
        get => _summaryOutputCharacters;
        set
        {
            if (Set(ref _summaryOutputCharacters, Math.Max(0, value))) NotifyCostChanged();
        }
    }
    public string? OriginalVttPath { get; set; }
    public string? TranslatedVttPath { get; set; }
    public string? TranscriptJsonPath { get; set; }
    public string? SummaryPath { get; set; }
    public List<JobEvent> Events { get; init; } = [];

    public JobState State
    {
        get => _state;
        set => Set(ref _state, value);
    }

    public int Progress
    {
        get => _progress;
        set => Set(ref _progress, Math.Clamp(value, 0, 100));
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
    }

    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    [JsonIgnore]
    public bool CanReview => State == JobState.Ready && File.Exists(OriginalVttPath);
    [JsonIgnore]
    public bool CanConfigureLanguages => State is not (JobState.Preparing or JobState.Transcribing or JobState.Translating or JobState.Summarizing);
    [JsonIgnore]
    public bool CanConfigureSourceLanguage => State is JobState.Queued or JobState.Failed or JobState.Canceled;
    [JsonIgnore]
    public bool CanStart => State is JobState.Queued or JobState.Canceled;
    [JsonIgnore]
    public bool CanEnd => State is JobState.Preparing or JobState.Transcribing or JobState.Translating or JobState.Summarizing;
    [JsonIgnore]
    public bool CanRetry => State == JobState.Failed;
    [JsonIgnore]
    public bool CanRemove => !CanEnd;
    [JsonIgnore]
    public string StateGlyph => State switch
    {
        JobState.Queued => "\uE768",
        JobState.Preparing => "\uE90F",
        JobState.Transcribing => "\uE8D6",
        JobState.Translating => "\uE8FA",
        JobState.Summarizing => "\uE8A5",
        JobState.Ready => "\uE73E",
        JobState.Failed => "\uEA39",
        JobState.Canceled => "\uE711",
        _ => "\uE946"
    };
    [JsonIgnore]
    public string DurationLabel
    {
        get
        {
            if (DurationSeconds is not { } seconds) return "--";
            var duration = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }
    }
    [JsonIgnore]
    public string DurationStatusLabel => HasAudio == false
        ? "Media duration; no audio stream detected"
        : DurationSeconds is null
            ? string.IsNullOrWhiteSpace(MediaProbeError)
                ? "Duration unavailable"
                : $"Duration unavailable: {MediaProbeError}"
            : "Media duration";
    [JsonIgnore]
    public JobCostComparison CostComparison => JobCostEstimator.Compare(this, _costSettings);
    [JsonIgnore]
    public string EstimatedScribeCostLabel => JobCostEstimator.FormatUsd(CostComparison.Estimate.ScribeUsd);
    [JsonIgnore]
    public string EstimatedTranslateCostLabel => JobCostEstimator.FormatUsd(CostComparison.Estimate.TranslateUsd);
    [JsonIgnore]
    public string EstimatedSummaryCostLabel => JobCostEstimator.FormatUsd(CostComparison.Estimate.SummarizeUsd);
    [JsonIgnore]
    public string EstimatedTotalCostLabel => JobCostEstimator.FormatUsd(CostComparison.Estimate.TotalUsd);
    [JsonIgnore]
    public string ActualScribeCostLabel => JobCostEstimator.FormatUsd(CostComparison.Actual.ScribeUsd);
    [JsonIgnore]
    public string ActualTranslateCostLabel => JobCostEstimator.FormatUsd(CostComparison.Actual.TranslateUsd);
    [JsonIgnore]
    public string ActualSummaryCostLabel => JobCostEstimator.FormatUsd(CostComparison.Actual.SummarizeUsd);
    [JsonIgnore]
    public string ActualTotalCostLabel => JobCostEstimator.FormatUsd(CostComparison.Actual.TotalUsd);
    [JsonIgnore]
    public string EstimateQualityLabel => TranscriptCharacters > 0 ? "Estimate" : "Rough";

    public void ConfigureCostEstimate(UserSettings settings)
    {
        _costSettings = settings;
        NotifyCostChanged();
    }

    public void Report(JobState state, int percent, string message)
    {
        State = state;
        Progress = percent;
        StatusMessage = message;
        Events.Add(new JobEvent(DateTimeOffset.UtcNow, state, message));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanConfigureLanguages));
        OnPropertyChanged(nameof(CanConfigureSourceLanguage));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanEnd));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(StateGlyph));
        NotifyCostChanged();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void NotifyCostChanged()
    {
        OnPropertyChanged(nameof(DurationLabel));
        OnPropertyChanged(nameof(DurationStatusLabel));
        OnPropertyChanged(nameof(CostComparison));
        OnPropertyChanged(nameof(EstimatedScribeCostLabel));
        OnPropertyChanged(nameof(EstimatedTranslateCostLabel));
        OnPropertyChanged(nameof(EstimatedSummaryCostLabel));
        OnPropertyChanged(nameof(EstimatedTotalCostLabel));
        OnPropertyChanged(nameof(ActualScribeCostLabel));
        OnPropertyChanged(nameof(ActualTranslateCostLabel));
        OnPropertyChanged(nameof(ActualSummaryCostLabel));
        OnPropertyChanged(nameof(ActualTotalCostLabel));
        OnPropertyChanged(nameof(EstimateQualityLabel));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

