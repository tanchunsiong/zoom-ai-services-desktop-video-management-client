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
    private UserSettings _costSettings = new();

    public Guid Id { get; init; } = Guid.NewGuid();
    public required string SourcePath { get; init; }
    public string DisplayName => Path.GetFileName(SourcePath);
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
    public string? OriginalVttPath { get; set; }
    public string? TranslatedVttPath { get; set; }
    public string? TranscriptJsonPath { get; set; }
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
    public bool CanConfigureLanguages => State is not (JobState.Preparing or JobState.Transcribing or JobState.Translating);
    [JsonIgnore]
    public bool CanConfigureSourceLanguage => State is JobState.Queued or JobState.Failed or JobState.Canceled;
    [JsonIgnore]
    public bool CanStart => State is JobState.Queued or JobState.Canceled;
    [JsonIgnore]
    public bool CanEnd => State is JobState.Preparing or JobState.Transcribing or JobState.Translating;
    [JsonIgnore]
    public bool CanRetry => State == JobState.Failed;
    [JsonIgnore]
    public bool CanRemove => !CanEnd;
    [JsonIgnore]
    public JobCostEstimate CostEstimate => JobCostEstimator.Estimate(this, _costSettings);
    [JsonIgnore]
    public string EstimatedScribeCostLabel => JobCostEstimator.FormatUsd(CostEstimate.ScribeUsd);
    [JsonIgnore]
    public string EstimatedTranslateCostLabel => JobCostEstimator.FormatUsd(CostEstimate.TranslateUsd);
    [JsonIgnore]
    public string EstimatedTotalCostLabel => JobCostEstimator.FormatUsd(CostEstimate.TotalUsd);
    [JsonIgnore]
    public string CostBasisLabel => CompletedAt is not null &&
        (string.IsNullOrWhiteSpace(TranslationLanguage) || CostEstimate.UsesActualTranslationUsage)
            ? "Actual"
            : "Estimate";
    [JsonIgnore]
    public string TranslationCostBasisLabel => CostEstimate.TranslationBillableCharacters == 0
        ? "No translation"
        : CostEstimate.UsesActualTranslationUsage ? "Zoom usage" : "Estimated usage";

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
        OnPropertyChanged(nameof(CostBasisLabel));
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
        OnPropertyChanged(nameof(CostEstimate));
        OnPropertyChanged(nameof(EstimatedScribeCostLabel));
        OnPropertyChanged(nameof(EstimatedTranslateCostLabel));
        OnPropertyChanged(nameof(EstimatedTotalCostLabel));
        OnPropertyChanged(nameof(CostBasisLabel));
        OnPropertyChanged(nameof(TranslationCostBasisLabel));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

