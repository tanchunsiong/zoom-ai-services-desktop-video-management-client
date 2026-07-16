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
    private LanguageOption _sourceLanguage = LanguageCatalog.Transcription[0];
    private LanguageOption _translationLanguage;

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
        _translationLanguage = TranslationLanguages[0];
        Queue.StateChanged += (_, _) => OnPropertyChanged(nameof(IsQueueRunning));
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

    public LanguageOption SourceLanguage
    {
        get => _sourceLanguage;
        set => Set(ref _sourceLanguage, value);
    }

    public LanguageOption TranslationLanguage
    {
        get => _translationLanguage;
        set => Set(ref _translationLanguage, value);
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
        var credentials = await _vault.LoadAsync();
        ApiKey = credentials?.ApiKey ?? "";
        HasCredentials = credentials is { IsComplete: true };
        await Queue.InitializeAsync();
        Notice = HasCredentials ? $"{Jobs.Count} job{(Jobs.Count == 1 ? "" : "s")} in the library" :
            "Add Zoom Build credentials in Settings before starting the queue";
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(ApiKey));
    }

    public Task AddFilesAsync(IEnumerable<string> files) => Queue.AddAsync(
        files, SourceLanguage.Locale,
        string.IsNullOrWhiteSpace(TranslationLanguage.Locale) ? null : TranslationLanguage.Locale);

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
        var subtitle = translated && File.Exists(job.TranslatedVttPath) ? job.TranslatedVttPath : job.OriginalVttPath;
        if (!File.Exists(subtitle)) throw new InvalidOperationException("This job does not have a subtitle file yet.");
        ReviewCues.Clear();
        foreach (var cue in WebVtt.Parse(await File.ReadAllTextAsync(subtitle))) ReviewCues.Add(cue);
        SelectedJob = job;
        Player.Open(job, subtitle);
        Notice = $"Reviewing {job.DisplayName}";
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
