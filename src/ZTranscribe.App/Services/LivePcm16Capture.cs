using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ZTranscribe.Core.Services;

namespace ZTranscribe.App.Services;

public enum LiveAudioSource
{
    Microphone,
    SpeakerLoopback
}

public sealed record LiveAudioDeviceOption(
    string Id,
    string Name,
    LiveAudioSource Source,
    int? WaveInDeviceNumber = null)
{
    public override string ToString() => Name;
}

public sealed class LivePcm16Capture : IAsyncDisposable
{
    private static readonly WaveFormat TargetFormat = new(16_000, 16, 1);
    private static readonly byte[] SilentFrame = new byte[Pcm16FrameAssembler.DefaultFrameBytes];
    private readonly LiveAudioDeviceOption _device;
    private readonly bool _automaticGain;
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(50)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private readonly Pcm16FrameAssembler _assembler = new();
    private readonly Pcm16AudioProcessor _audioProcessor = new();
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IWaveIn? _capture;
    private MMDeviceEnumerator? _deviceEnumerator;
    private MMDevice? _renderDevice;
    private BufferedWaveProvider? _loopbackBuffer;
    private ISampleProvider? _loopbackResampler;
    private readonly float[] _loopbackSamples = new float[3_200];
    private Timer? _silenceTimer;
    private long _lastAudioTimestamp;
    private IProgress<Pcm16LevelReading>? _levelProgress;

    public LivePcm16Capture(LiveAudioDeviceOption device, bool automaticGain = false)
    {
        _device = device;
        _automaticGain = automaticGain;
    }

    public static IReadOnlyList<LiveAudioDeviceOption> EnumerateDevices(LiveAudioSource source) =>
        source == LiveAudioSource.Microphone
            ? EnumerateMicrophones()
            : EnumerateRenderDevices();

    private static IReadOnlyList<LiveAudioDeviceOption> EnumerateMicrophones()
    {
        var devices = new List<LiveAudioDeviceOption>();
        for (var index = 0; index < WaveIn.DeviceCount; index++)
        {
            var capabilities = WaveIn.GetCapabilities(index);
            devices.Add(new LiveAudioDeviceOption(
                $"wave-in:{index}",
                capabilities.ProductName,
                LiveAudioSource.Microphone,
                index));
        }
        return devices;
    }

    private static IReadOnlyList<LiveAudioDeviceOption> EnumerateRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultRenderDeviceId(enumerator);
        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var devices = new List<LiveAudioDeviceOption>(endpoints.Count);
        foreach (var endpoint in endpoints)
        {
            devices.Add(new LiveAudioDeviceOption(
                endpoint.ID,
                endpoint.ID == defaultId ? $"{endpoint.FriendlyName} (Default)" : endpoint.FriendlyName,
                LiveAudioSource.SpeakerLoopback));
            endpoint.Dispose();
        }
        return devices
            .OrderByDescending(device => device.Id == defaultId)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? TryGetDefaultRenderDeviceId(MMDeviceEnumerator enumerator)
    {
        try
        {
            using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return endpoint.ID;
        }
        catch
        {
            return null;
        }
    }

    public IAsyncEnumerable<byte[]> ReadFramesAsync(CancellationToken cancellationToken) =>
        _frames.Reader.ReadAllAsync(cancellationToken);

    public void Start(IProgress<Pcm16LevelReading>? levelProgress = null)
    {
        if (_capture is not null) throw new InvalidOperationException("Audio capture is already running.");
        _levelProgress = levelProgress;
        var capture = CreateCapture();
        capture.DataAvailable += Capture_DataAvailable;
        capture.RecordingStopped += Capture_RecordingStopped;
        _capture = capture;
        _lastAudioTimestamp = Stopwatch.GetTimestamp();
        try
        {
            capture.StartRecording();
            if (_device.Source == LiveAudioSource.SpeakerLoopback)
                _silenceTimer = new Timer(WriteSilenceIfIdle, null, 100, 100);
        }
        catch (Exception exception)
        {
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
            capture.Dispose();
            _capture = null;
            DisposeRenderDevice();
            _frames.Writer.TryComplete(exception);
            throw;
        }
    }

    private IWaveIn CreateCapture()
    {
        if (_device.Source == LiveAudioSource.Microphone)
        {
            return new WaveInEvent
            {
                DeviceNumber = _device.WaveInDeviceNumber
                    ?? throw new InvalidOperationException("The microphone device number is missing."),
                WaveFormat = TargetFormat,
                BufferMilliseconds = 100,
                NumberOfBuffers = 3
            };
        }

        _deviceEnumerator = new MMDeviceEnumerator();
        _renderDevice = _deviceEnumerator.GetDevice(_device.Id);
        var capture = new WasapiLoopbackCapture(_renderDevice);
        _loopbackBuffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false
        };
        var nativeSamples = _loopbackBuffer.ToSampleProvider();
        var monoSamples = new DownmixToMonoSampleProvider(nativeSamples);
        _loopbackResampler = new WdlResamplingSampleProvider(monoSamples, TargetFormat.SampleRate);
        return capture;
    }

    public async Task StopAsync()
    {
        DisposeSilenceTimer();
        var capture = _capture;
        if (capture is null)
        {
            _frames.Writer.TryComplete();
            return;
        }
        if (_stopped.Task.IsCompleted)
        {
            await _stopped.Task;
            return;
        }
        try { capture.StopRecording(); }
        catch (InvalidOperationException) when (_stopped.Task.IsCompleted) { }
        await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Abort()
    {
        DisposeSilenceTimer();
        _frames.Writer.TryComplete(new OperationCanceledException("Audio capture was canceled."));
        try { _capture?.StopRecording(); }
        catch (InvalidOperationException) { }
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_device.Source == LiveAudioSource.SpeakerLoopback)
        {
            ProcessLoopbackData(e.Buffer, e.BytesRecorded);
            return;
        }
        ProcessPcm16(e.Buffer, e.BytesRecorded);
    }

    private void ProcessLoopbackData(byte[] buffer, int count)
    {
        if (count == 0 || _loopbackBuffer is null || _loopbackResampler is null) return;
        _loopbackBuffer.AddSamples(buffer, 0, count);
        for (var pass = 0; pass < 8; pass++)
        {
            var samplesRead = _loopbackResampler.Read(
                _loopbackSamples,
                0,
                _loopbackSamples.Length);
            if (samplesRead == 0) break;

            var pcm16 = new byte[samplesRead * 2];
            for (var index = 0; index < samplesRead; index++)
            {
                var sample = Math.Clamp(_loopbackSamples[index], -1f, 1f);
                var value = sample < 0
                    ? (short)Math.Round(sample * 32_768f)
                    : (short)Math.Round(sample * 32_767f);
                BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(index * 2, 2), value);
            }
            ProcessPcm16(pcm16, pcm16.Length);
            if (samplesRead < _loopbackSamples.Length) break;
        }
    }

    private void ProcessPcm16(byte[] buffer, int count)
    {
        var completeSampleBytes = count & ~1;
        if (completeSampleBytes == 0) return;
        Interlocked.Exchange(ref _lastAudioTimestamp, Stopwatch.GetTimestamp());
        var reading = _audioProcessor.ProcessInPlace(buffer, 0, completeSampleBytes, _automaticGain);
        foreach (var frame in _assembler.Append(buffer, 0, completeSampleBytes))
            _frames.Writer.TryWrite(frame);
        _levelProgress?.Report(reading);
    }

    private void WriteSilenceIfIdle(object? state)
    {
        var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAudioTimestamp));
        if (elapsed < TimeSpan.FromMilliseconds(150)) return;
        _frames.Writer.TryWrite(SilentFrame.ToArray());
        _levelProgress?.Report(new Pcm16LevelReading(
            double.NegativeInfinity,
            double.NegativeInfinity,
            false,
            1));
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        DisposeSilenceTimer();
        var remainder = _assembler.Drain();
        if (remainder is not null) _frames.Writer.TryWrite(remainder);
        _frames.Writer.TryComplete(e.Exception);
        _levelProgress?.Report(new Pcm16LevelReading(
            double.NegativeInfinity,
            double.NegativeInfinity,
            false,
            1));
        _stopped.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        var capture = _capture;
        if (capture is null) return;
        try
        {
            await StopAsync();
        }
        catch (Exception)
        {
        }
        finally
        {
            DisposeSilenceTimer();
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
            capture.Dispose();
            _capture = null;
            DisposeRenderDevice();
        }
    }

    private void DisposeSilenceTimer()
    {
        Interlocked.Exchange(ref _silenceTimer, null)?.Dispose();
    }

    private void DisposeRenderDevice()
    {
        _loopbackResampler = null;
        _loopbackBuffer = null;
        _renderDevice?.Dispose();
        _renderDevice = null;
        _deviceEnumerator?.Dispose();
        _deviceEnumerator = null;
    }

    private sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = [];

        public DownmixToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            if (_channels <= 0) throw new InvalidOperationException("The speaker mix has no audio channels.");
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var required = count * _channels;
            if (_sourceBuffer.Length < required) _sourceBuffer = new float[required];
            var samplesRead = _source.Read(_sourceBuffer, 0, required);
            var framesRead = samplesRead / _channels;
            for (var frame = 0; frame < framesRead; frame++)
            {
                var dominant = 0f;
                var sourceOffset = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                {
                    var sample = _sourceBuffer[sourceOffset + channel];
                    if (Math.Abs(sample) > Math.Abs(dominant)) dominant = sample;
                }
                buffer[offset + frame] = dominant;
            }
            return framesRead;
        }
    }
}
