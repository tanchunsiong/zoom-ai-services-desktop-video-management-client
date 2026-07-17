using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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

    public Guid Id { get; init; } = Guid.NewGuid();
    public required string SourcePath { get; init; }
    public string DisplayName => Path.GetFileName(SourcePath);
    public string SourceLanguage
    {
        get => _sourceLanguage;
        set => Set(ref _sourceLanguage, value);
    }
    public string TranslationLanguage
    {
        get => _translationLanguage;
        set => Set(ref _translationLanguage, value ?? "");
    }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public double? DurationSeconds { get; set; }
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
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

