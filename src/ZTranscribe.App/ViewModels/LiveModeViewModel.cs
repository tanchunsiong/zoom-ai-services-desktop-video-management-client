using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZTranscribe.App.Services;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

namespace ZTranscribe.App.ViewModels;

public sealed record LiveTranscriptSegment(
    Guid Id,
    int Number,
    DateTimeOffset At,
    string Text,
    string? Translation = null,
    string? TranslationError = null,
    bool IsTranslating = false)
{
    public string TimeLabel => At.ToLocalTime().ToString("HH:mm:ss");
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
    public bool HasTranslationError => !string.IsNullOrWhiteSpace(TranslationError);
}

public sealed record FloatingCaptionEntry(string Text, string? Translation)
{
    public bool HasTranslation => !string.IsNullOrWhiteSpace(Translation);
}

public enum AudioMeterState
{
    Normal,
    Warning,
    Clipping
}

public sealed class LiveModeViewModel : INotifyPropertyChanged
{
    private readonly ICredentialVault _credentialVault;
    private readonly ILiveScribeClient _liveScribeClient;
    private readonly IZoomAiClient _zoomAiClient;
    private readonly Func<string, string, double, Task> _persistLiveSettingsAsync;
    private static readonly LanguageOption NoTranslation = new("", "No translation");
    private LiveAudioSource _audioSource = LiveAudioSource.Microphone;
    private LiveAudioDeviceOption? _selectedAudioDevice;
    private LanguageOption _selectedLanguage = LanguageCatalog.Transcription[0];
    private LanguageOption _selectedTranslationLanguage = NoTranslation;
    private string _status = "Select an audio input to begin";
    private bool _isConnecting;
    private bool _isStreaming;
    private bool _isStopping;
    private bool _isSpeechActive;
    private double _inputLevel;
    private string _inputLevelLabel = "-- dBFS";
    private AudioMeterState _inputLevelState;
    private bool _automaticGainEnabled;
    private string _interimTranscript = "";
    private string _vocabularyJson = ScribeVocabularyJson.Sample;
    private double _floatingCaptionTextSize = 22.0;
    private CancellationTokenSource? _settingsSaveCancellation;
    private readonly Dictionary<Guid, CancellationTokenSource> _translationCancellations = [];
    private DateTimeOffset _clipHoldUntil;
    private LivePcm16Capture? _capture;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;

    public LiveModeViewModel(
        ICredentialVault credentialVault,
        ILiveScribeClient liveScribeClient,
        IZoomAiClient zoomAiClient,
        Func<string, string, double, Task> persistLiveSettingsAsync)
    {
        _credentialVault = credentialVault;
        _liveScribeClient = liveScribeClient;
        _zoomAiClient = zoomAiClient;
        _persistLiveSettingsAsync = persistLiveSettingsAsync;
        RefreshAudioDevices();
    }

    public ObservableCollection<LiveAudioDeviceOption> AudioDevices { get; } = [];
    public ObservableCollection<LiveTranscriptSegment> Segments { get; } = [];
    public IReadOnlyList<LanguageOption> Languages => LanguageCatalog.Transcription;
    public IReadOnlyList<LanguageOption> TranslationLanguages =>
        [NoTranslation, .. LanguageCatalog.Translation.Where(option => option.Locale != SelectedLanguage.Locale)];

    public LiveAudioDeviceOption? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (!Set(ref _selectedAudioDevice, value)) return;
            OnPropertyChanged(nameof(CanStart));
        }
    }

    public bool IsMicrophoneSource => _audioSource == LiveAudioSource.Microphone;
    public bool IsSpeakerLoopbackSource => _audioSource == LiveAudioSource.SpeakerLoopback;
    public string AudioDeviceLabel => IsMicrophoneSource ? "Microphone" : "Speaker output";

    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !Set(ref _selectedLanguage, value)) return;
            if (SelectedTranslationLanguage.Locale == value.Locale)
                SelectedTranslationLanguage = NoTranslation;
            OnPropertyChanged(nameof(TranslationLanguages));
        }
    }

    public LanguageOption SelectedTranslationLanguage
    {
        get => _selectedTranslationLanguage;
        set
        {
            var normalized = value is null || value.Locale == SelectedLanguage.Locale
                ? NoTranslation
                : value;
            if (!Set(ref _selectedTranslationLanguage, normalized)) return;
            ScheduleSettingsSave();
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (!Set(ref _isConnecting, value)) return;
            RaiseStateProperties();
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (!Set(ref _isStreaming, value)) return;
            RaiseStateProperties();
        }
    }

    public bool IsStopping
    {
        get => _isStopping;
        private set
        {
            if (!Set(ref _isStopping, value)) return;
            RaiseStateProperties();
        }
    }

    public bool IsSpeechActive
    {
        get => _isSpeechActive;
        private set => Set(ref _isSpeechActive, value);
    }

    public double InputLevel
    {
        get => _inputLevel;
        private set => Set(ref _inputLevel, Math.Clamp(value, 0, 1));
    }

    public string InputLevelLabel
    {
        get => _inputLevelLabel;
        private set => Set(ref _inputLevelLabel, value);
    }

    public AudioMeterState InputLevelState
    {
        get => _inputLevelState;
        private set => Set(ref _inputLevelState, value);
    }

    public bool AutomaticGainEnabled
    {
        get => _automaticGainEnabled;
        set => Set(ref _automaticGainEnabled, value);
    }

    public string VocabularyJson
    {
        get => _vocabularyJson;
        set
        {
            if (!Set(ref _vocabularyJson, value ?? "")) return;
            OnPropertyChanged(nameof(VocabularyError));
            OnPropertyChanged(nameof(VocabularyStatus));
            OnPropertyChanged(nameof(HasVocabularyError));
            OnPropertyChanged(nameof(CanStart));
            ScheduleSettingsSave();
        }
    }

    public string? VocabularyError
    {
        get
        {
            try
            {
                _ = ScribeVocabularyJson.Parse(VocabularyJson);
                return null;
            }
            catch (InvalidOperationException exception)
            {
                return exception.Message;
            }
        }
    }

    public bool HasVocabularyError => VocabularyError is not null;
    public string VocabularyStatus => VocabularyError ??
        (string.IsNullOrWhiteSpace(VocabularyJson) ? "Optional" : "Valid vocabulary JSON");

    public string InterimTranscript
    {
        get => _interimTranscript;
        private set
        {
            if (!Set(ref _interimTranscript, value)) return;
            OnPropertyChanged(nameof(InterimCaptionText));
            OnPropertyChanged(nameof(HasInterimTranscript));
            RaiseFloatingCaptionProperties();
        }
    }

    public string InterimCaptionText =>
        InterimTranscript.Length <= 320
            ? InterimTranscript
            : $"...{InterimTranscript[^320..]}";

    public bool IsSessionActive => IsConnecting || IsStreaming || IsStopping;
    public bool IsConfigurationEnabled => !IsSessionActive;
    public bool CanStart => !IsSessionActive && SelectedAudioDevice is not null && !HasVocabularyError;
    public bool CanStop => (IsConnecting || IsStreaming) && !IsStopping;
    public bool HasAudioDevices => AudioDevices.Count > 0;
    public bool HasSegments => Segments.Count > 0;
    public bool HasInterimTranscript => !string.IsNullOrWhiteSpace(InterimTranscript);
    public string SegmentCountLabel => $"{Segments.Count:N0} completed segment{(Segments.Count == 1 ? "" : "s")}";
    public string TranscriptText => string.Join(
        Environment.NewLine,
        Segments.SelectMany(segment => new[] { segment.Text, segment.Translation })
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    public string FloatingCaptionText => HasInterimTranscript
        ? InterimCaptionText
        : Segments.LastOrDefault()?.Text ?? "";
    public string FloatingTranslationText => HasInterimTranscript
        ? ""
        : Segments.LastOrDefault()?.Translation ?? "";
    public bool HasFloatingTranslation => !string.IsNullOrWhiteSpace(FloatingTranslationText);

    public double FloatingCaptionTextSize
    {
        get => _floatingCaptionTextSize;
        set
        {
            var normalized = Math.Clamp(value, 14.0, 96.0);
            if (!Set(ref _floatingCaptionTextSize, normalized)) return;
            OnPropertyChanged(nameof(FloatingTranslationTextSize));
            ScheduleSettingsSave();
        }
    }

    public double FloatingTranslationTextSize => Math.Max(12.0, FloatingCaptionTextSize * 0.77);

    public IReadOnlyList<FloatingCaptionEntry> FloatingCaptionEntries
    {
        get
        {
            var result = new List<FloatingCaptionEntry>();
            if (HasInterimTranscript)
                result.Add(new FloatingCaptionEntry(InterimCaptionText, null));

            var limit = HasInterimTranscript ? 2 : 3;
            result.AddRange(Segments
                .Reverse()
                .Take(limit)
                .Select(segment => new FloatingCaptionEntry(segment.Text, segment.Translation)));
            return result;
        }
    }

    public void InitializeSettings(
        string? vocabularyJson,
        string? translationLanguage,
        double floatingCaptionTextSize = 22.0)
    {
        _vocabularyJson = vocabularyJson ?? ScribeVocabularyJson.Sample;
        _floatingCaptionTextSize = Math.Clamp(floatingCaptionTextSize, 14.0, 96.0);
        _selectedTranslationLanguage = LanguageCatalog.Translation.FirstOrDefault(option =>
            option.Locale == translationLanguage && option.Locale != SelectedLanguage.Locale) ?? NoTranslation;
        OnPropertyChanged(nameof(VocabularyJson));
        OnPropertyChanged(nameof(VocabularyError));
        OnPropertyChanged(nameof(VocabularyStatus));
        OnPropertyChanged(nameof(HasVocabularyError));
        OnPropertyChanged(nameof(SelectedTranslationLanguage));
        OnPropertyChanged(nameof(TranslationLanguages));
        OnPropertyChanged(nameof(FloatingCaptionTextSize));
        OnPropertyChanged(nameof(FloatingTranslationTextSize));
        OnPropertyChanged(nameof(FloatingCaptionEntries));
        OnPropertyChanged(nameof(CanStart));
    }

    public void SetAudioSource(LiveAudioSource source)
    {
        if (IsSessionActive || _audioSource == source) return;
        _audioSource = source;
        OnPropertyChanged(nameof(IsMicrophoneSource));
        OnPropertyChanged(nameof(IsSpeakerLoopbackSource));
        OnPropertyChanged(nameof(AudioDeviceLabel));
        RefreshAudioDevices();
    }

    public void RefreshAudioDevices()
    {
        if (IsSessionActive) return;
        try
        {
            var selectedId = SelectedAudioDevice?.Source == _audioSource
                ? SelectedAudioDevice.Id
                : null;
            AudioDevices.Clear();
            foreach (var device in LivePcm16Capture.EnumerateDevices(_audioSource))
                AudioDevices.Add(device);
            SelectedAudioDevice = AudioDevices.FirstOrDefault(device => device.Id == selectedId)
                                  ?? AudioDevices.FirstOrDefault();
            OnPropertyChanged(nameof(HasAudioDevices));
            Status = HasAudioDevices
                ? "Ready to connect"
                : IsMicrophoneSource
                    ? "No microphone was detected"
                    : "No speaker output was detected";
        }
        catch (Exception exception)
        {
            AudioDevices.Clear();
            SelectedAudioDevice = null;
            OnPropertyChanged(nameof(HasAudioDevices));
            Status = $"Audio devices could not be read: {exception.Message}";
        }
    }

    public async Task StartAsync()
    {
        if (!CanStart || SelectedAudioDevice is null) return;
        var credentials = await _credentialVault.LoadAsync();
        if (credentials is not { IsComplete: true })
        {
            Status = "Add Zoom AI Services credentials in Settings first";
            return;
        }

        var options = new LiveScribeOptions(SelectedLanguage.Locale, VocabularyJson);
        options.Validate();

        ClearTranscript();
        Status = $"Connecting {SelectedAudioDevice.Name}...";
        IsConnecting = true;
        IsSpeechActive = false;
        ResetInputLevel();

        var capture = new LivePcm16Capture(
            SelectedAudioDevice,
            AutomaticGainEnabled);
        var cancellation = new CancellationTokenSource();
        _capture = capture;
        _sessionCancellation = cancellation;
        var events = new Progress<LiveScribeEvent>(serverEvent => HandleServerEvent(serverEvent, credentials));
        var levels = new Progress<Pcm16LevelReading>(UpdateInputLevel);

        try
        {
            capture.Start(levels);
            var sessionTask = _liveScribeClient.StreamAsync(
                capture.ReadFramesAsync(cancellation.Token),
                options,
                credentials,
                events,
                cancellation.Token);
            _sessionTask = sessionTask;
            await sessionTask;
            if (!cancellation.IsCancellationRequested)
                Status = Segments.Count == 0 ? "Session closed; no speech was transcribed" : "Session closed";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = "Live session canceled";
        }
        catch (Exception exception)
        {
            Status = $"Live transcription failed: {exception.Message}";
        }
        finally
        {
            try { await capture.DisposeAsync(); }
            catch (Exception) { }
            if (ReferenceEquals(_capture, capture))
            {
                _capture = null;
                _sessionTask = null;
                _sessionCancellation = null;
            }
            cancellation.Dispose();
            IsSpeechActive = false;
            ResetInputLevel();
            IsConnecting = false;
            IsStreaming = false;
            IsStopping = false;
        }
    }

    public async Task StopAsync()
    {
        var capture = _capture;
        if (capture is null || IsStopping) return;
        IsStopping = true;
        Status = "Finishing the last speech turn...";
        try
        {
            await capture.StopAsync();
            if (_sessionTask is { } sessionTask) await sessionTask;
        }
        catch (Exception)
        {
            // StartAsync owns session error reporting and final state cleanup.
        }
    }

    public void Abort()
    {
        _sessionCancellation?.Cancel();
        _capture?.Abort();
    }

    public void ClearTranscript()
    {
        foreach (var cancellation in _translationCancellations.Values)
            cancellation.Cancel();
        _translationCancellations.Clear();
        Segments.Clear();
        InterimTranscript = "";
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(SegmentCountLabel));
        OnPropertyChanged(nameof(TranscriptText));
        RaiseFloatingCaptionProperties();
    }

    private void HandleServerEvent(LiveScribeEvent serverEvent, ApiCredentials credentials)
    {
        switch (serverEvent.Type)
        {
            case "session.created":
                Status = "Connected; configuring Live mode...";
                break;
            case "session.updated":
                IsConnecting = false;
                IsStreaming = true;
                Status = "Listening";
                break;
            case "speech_started":
                IsSpeechActive = true;
                Status = "Speech detected";
                break;
            case "speech_stopped":
                IsSpeechActive = false;
                Status = "Transcribing speech turn...";
                break;
            case "transcription.completed":
                if (!string.IsNullOrWhiteSpace(serverEvent.Transcript))
                {
                    var translationLanguage = SelectedTranslationLanguage.Locale;
                    var segment = new LiveTranscriptSegment(
                        Guid.NewGuid(),
                        Segments.Count + 1,
                        DateTimeOffset.Now,
                        serverEvent.Transcript.Trim(),
                        IsTranslating: translationLanguage.Length > 0);
                    Segments.Add(segment);
                    RaiseFloatingCaptionProperties();
                    if (translationLanguage.Length > 0)
                        StartSegmentTranslation(
                            segment,
                            SelectedLanguage.Locale,
                            translationLanguage,
                            credentials);
                }
                InterimTranscript = "";
                OnPropertyChanged(nameof(HasSegments));
                OnPropertyChanged(nameof(SegmentCountLabel));
                OnPropertyChanged(nameof(TranscriptText));
                Status = "Listening";
                break;
            case var _ when !string.IsNullOrWhiteSpace(serverEvent.Transcript):
                InterimTranscript = serverEvent.Transcript.Trim();
                Status = "Receiving captions";
                break;
            case "session.closed":
                IsSpeechActive = false;
                Status = "Session closed";
                break;
            case "error":
                Status = $"Zoom Live error: {serverEvent.Error ?? "Unknown error"}";
                break;
        }
    }

    private void StartSegmentTranslation(
        LiveTranscriptSegment segment,
        string sourceLanguage,
        string targetLanguage,
        ApiCredentials credentials)
    {
        var cancellation = new CancellationTokenSource();
        _translationCancellations[segment.Id] = cancellation;
        _ = TranslateSegmentAsync(
            segment,
            sourceLanguage,
            targetLanguage,
            credentials,
            cancellation);
    }

    private async Task TranslateSegmentAsync(
        LiveTranscriptSegment segment,
        string sourceLanguage,
        string targetLanguage,
        ApiCredentials credentials,
        CancellationTokenSource cancellation)
    {
        try
        {
            var translation = await _zoomAiClient.TranslateTextAsync(
                segment.Text,
                sourceLanguage,
                targetLanguage,
                credentials,
                cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            ReplaceSegment(segment.Id, current => current with
            {
                Translation = translation,
                TranslationError = null,
                IsTranslating = false
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ReplaceSegment(segment.Id, current => current with
            {
                TranslationError = exception.Message,
                IsTranslating = false
            });
        }
        finally
        {
            if (_translationCancellations.TryGetValue(segment.Id, out var activeCancellation) &&
                ReferenceEquals(activeCancellation, cancellation))
                _translationCancellations.Remove(segment.Id);
            cancellation.Dispose();
        }
    }

    private void ReplaceSegment(Guid id, Func<LiveTranscriptSegment, LiveTranscriptSegment> update)
    {
        var index = Segments.ToList().FindIndex(segment => segment.Id == id);
        if (index < 0) return;
        Segments[index] = update(Segments[index]);
        OnPropertyChanged(nameof(TranscriptText));
        RaiseFloatingCaptionProperties();
    }

    private void RaiseFloatingCaptionProperties()
    {
        OnPropertyChanged(nameof(FloatingCaptionText));
        OnPropertyChanged(nameof(FloatingTranslationText));
        OnPropertyChanged(nameof(HasFloatingTranslation));
        OnPropertyChanged(nameof(FloatingCaptionEntries));
    }

    private void UpdateInputLevel(Pcm16LevelReading reading)
    {
        var now = DateTimeOffset.UtcNow;
        if (reading.IsClipping) _clipHoldUntil = now.AddSeconds(1);
        var clipping = now < _clipHoldUntil;
        InputLevel = clipping ? 1 : Pcm16AudioProcessor.NormalizeMeter(reading.PeakDbfs);
        InputLevelLabel = clipping
            ? "CLIPPING"
            : double.IsNegativeInfinity(reading.PeakDbfs)
                ? "-- dBFS"
                : $"{reading.PeakDbfs:0.0} dBFS";
        InputLevelState = clipping
            ? AudioMeterState.Clipping
            : reading.PeakDbfs >= -12
                ? AudioMeterState.Warning
                : AudioMeterState.Normal;
    }

    private void ResetInputLevel()
    {
        _clipHoldUntil = default;
        InputLevel = 0;
        InputLevelLabel = "-- dBFS";
        InputLevelState = AudioMeterState.Normal;
    }

    private void ScheduleSettingsSave()
    {
        _settingsSaveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _settingsSaveCancellation = cancellation;
        var vocabularyJson = VocabularyJson;
        var translationLanguage = SelectedTranslationLanguage.Locale;
        var floatingCaptionTextSize = FloatingCaptionTextSize;
        _ = SaveSettingsAfterDelayAsync(
            vocabularyJson, translationLanguage, floatingCaptionTextSize, cancellation);
    }

    private async Task SaveSettingsAfterDelayAsync(
        string vocabularyJson,
        string translationLanguage,
        double floatingCaptionTextSize,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(500, cancellation.Token);
            await _persistLiveSettingsAsync(
                vocabularyJson, translationLanguage, floatingCaptionTextSize);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Status = $"Live settings could not be saved: {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_settingsSaveCancellation, cancellation))
                _settingsSaveCancellation = null;
            cancellation.Dispose();
        }
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(IsSessionActive));
        OnPropertyChanged(nameof(IsConfigurationEnabled));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
