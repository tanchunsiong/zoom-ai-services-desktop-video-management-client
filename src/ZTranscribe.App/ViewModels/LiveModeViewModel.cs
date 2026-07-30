using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ZTranscribe.App.Services;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

namespace ZTranscribe.App.ViewModels;

public sealed record LiveTranscriptSegment(int Number, DateTimeOffset At, string Text)
{
    public string TimeLabel => At.ToLocalTime().ToString("HH:mm:ss");
}

public sealed record CaptionCadenceOption(int Milliseconds, string Label)
{
    public override string ToString() => Label;
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
    private LiveAudioSource _audioSource = LiveAudioSource.Microphone;
    private LiveAudioDeviceOption? _selectedAudioDevice;
    private LanguageOption _selectedLanguage = LanguageCatalog.Transcription[0];
    private double _vadThreshold = LiveVadPresets.Microphone.Threshold;
    private int _prefixPaddingMs = LiveVadPresets.Microphone.PrefixPaddingMs;
    private int _silenceDurationMs = LiveVadPresets.Microphone.SilenceDurationMs;
    private int _minPauseDurationMs = LiveVadPresets.Microphone.MinPauseDurationMs;
    private LiveVadSettings _microphoneVad = LiveVadPresets.Microphone;
    private LiveVadSettings _speakerLoopbackVad = LiveVadPresets.SpeakerLoopback;
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
    private bool _forceCaptionCadenceEnabled;
    private bool _microphoneForceCaptionCadence;
    private bool _speakerLoopbackForceCaptionCadence = true;
    private CaptionCadenceOption _selectedCaptionCadence;
    private DateTimeOffset _clipHoldUntil;
    private LivePcm16Capture? _capture;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _sessionTask;

    public LiveModeViewModel(ICredentialVault credentialVault, ILiveScribeClient liveScribeClient)
    {
        _credentialVault = credentialVault;
        _liveScribeClient = liveScribeClient;
        _selectedCaptionCadence = CaptionCadences.First(option => option.Milliseconds == 3_000);
        RefreshAudioDevices();
    }

    public ObservableCollection<LiveAudioDeviceOption> AudioDevices { get; } = [];
    public ObservableCollection<LiveTranscriptSegment> Segments { get; } = [];
    public IReadOnlyList<LanguageOption> Languages => LanguageCatalog.Transcription;
    public IReadOnlyList<CaptionCadenceOption> CaptionCadences { get; } =
    [
        new(500, "500 ms"),
        new(1_000, "1 second"),
        new(2_000, "2 seconds"),
        new(3_000, "3 seconds"),
        new(5_000, "5 seconds"),
        new(10_000, "10 seconds")
    ];

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
            if (value is not null) Set(ref _selectedLanguage, value);
        }
    }

    public double VadThreshold
    {
        get => _vadThreshold;
        set => Set(ref _vadThreshold, Math.Round(Math.Clamp(value, 0, 1), 2));
    }

    public int PrefixPaddingMs
    {
        get => _prefixPaddingMs;
        set => Set(ref _prefixPaddingMs, Math.Clamp(value, 0, 5_000));
    }

    public int SilenceDurationMs
    {
        get => _silenceDurationMs;
        set => Set(ref _silenceDurationMs, Math.Clamp(value, 250, 10_000));
    }

    public int MinPauseDurationMs
    {
        get => _minPauseDurationMs;
        set => Set(ref _minPauseDurationMs, Math.Clamp(value, 0, 5_000));
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

    public bool ForceCaptionCadenceEnabled
    {
        get => _forceCaptionCadenceEnabled;
        set => Set(ref _forceCaptionCadenceEnabled, value);
    }

    public CaptionCadenceOption SelectedCaptionCadence
    {
        get => _selectedCaptionCadence;
        set
        {
            if (value is not null) Set(ref _selectedCaptionCadence, value);
        }
    }

    public string InterimTranscript
    {
        get => _interimTranscript;
        private set
        {
            if (!Set(ref _interimTranscript, value)) return;
            OnPropertyChanged(nameof(InterimCaptionText));
            OnPropertyChanged(nameof(HasInterimTranscript));
        }
    }

    public string InterimCaptionText =>
        InterimTranscript.Length <= 320
            ? InterimTranscript
            : $"...{InterimTranscript[^320..]}";

    public bool IsSessionActive => IsConnecting || IsStreaming || IsStopping;
    public bool IsConfigurationEnabled => !IsSessionActive;
    public bool CanStart => !IsSessionActive && SelectedAudioDevice is not null;
    public bool CanStop => (IsConnecting || IsStreaming) && !IsStopping;
    public bool HasAudioDevices => AudioDevices.Count > 0;
    public bool HasSegments => Segments.Count > 0;
    public bool HasInterimTranscript => !string.IsNullOrWhiteSpace(InterimTranscript);
    public string SegmentCountLabel => $"{Segments.Count:N0} completed segment{(Segments.Count == 1 ? "" : "s")}";
    public string TranscriptText => string.Join(Environment.NewLine, Segments.Select(segment => segment.Text));

    public void SetAudioSource(LiveAudioSource source)
    {
        if (IsSessionActive || _audioSource == source) return;
        SaveCurrentVadSettings();
        _audioSource = source;
        OnPropertyChanged(nameof(IsMicrophoneSource));
        OnPropertyChanged(nameof(IsSpeakerLoopbackSource));
        OnPropertyChanged(nameof(AudioDeviceLabel));
        ApplyVadSettings(source == LiveAudioSource.Microphone
            ? _microphoneVad
            : _speakerLoopbackVad);
        ForceCaptionCadenceEnabled = source == LiveAudioSource.Microphone
            ? _microphoneForceCaptionCadence
            : _speakerLoopbackForceCaptionCadence;
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

        var options = new LiveScribeOptions(
            SelectedLanguage.Locale,
            VadThreshold,
            PrefixPaddingMs,
            SilenceDurationMs,
            MinPauseDurationMs,
            ForceCaptionCadenceEnabled
                ? SelectedCaptionCadence.Milliseconds
                : 0);
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
        var events = new Progress<LiveScribeEvent>(HandleServerEvent);
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
        Segments.Clear();
        InterimTranscript = "";
        OnPropertyChanged(nameof(HasSegments));
        OnPropertyChanged(nameof(SegmentCountLabel));
        OnPropertyChanged(nameof(TranscriptText));
    }

    private void HandleServerEvent(LiveScribeEvent serverEvent)
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
                    Segments.Add(new LiveTranscriptSegment(
                        Segments.Count + 1,
                        DateTimeOffset.Now,
                        serverEvent.Transcript.Trim()));
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

    private void SaveCurrentVadSettings()
    {
        var settings = new LiveVadSettings(
            VadThreshold,
            PrefixPaddingMs,
            SilenceDurationMs,
            MinPauseDurationMs);
        if (_audioSource == LiveAudioSource.Microphone)
        {
            _microphoneVad = settings;
            _microphoneForceCaptionCadence = ForceCaptionCadenceEnabled;
        }
        else
        {
            _speakerLoopbackVad = settings;
            _speakerLoopbackForceCaptionCadence = ForceCaptionCadenceEnabled;
        }
    }

    private void ApplyVadSettings(LiveVadSettings settings)
    {
        VadThreshold = settings.Threshold;
        PrefixPaddingMs = settings.PrefixPaddingMs;
        SilenceDurationMs = settings.SilenceDurationMs;
        MinPauseDurationMs = settings.MinPauseDurationMs;
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
