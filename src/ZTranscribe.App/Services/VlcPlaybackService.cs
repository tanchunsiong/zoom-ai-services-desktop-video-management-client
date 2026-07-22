using LibVLCSharp.Shared;
using ZTranscribe.Core.Models;
using ZTranscribe.Infrastructure.Media;
using ZTranscribe.Infrastructure.Persistence;

namespace ZTranscribe.App.Services;

public sealed class VlcPlaybackService : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly FfmpegPlaybackResolver _playbackResolver;
    private Media? _media;
    private float _playbackRate = 1f;
    public MediaPlayer MediaPlayer { get; }
    public QueueJob? CurrentJob { get; private set; }
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;
    public event Action<string>? PlaybackFailed;

    public VlcPlaybackService()
    {
        _playbackResolver = new FfmpegPlaybackResolver(new AppPaths());
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show", "--avcodec-hw=none");
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.TimeChanged += (_, eventArgs) =>
            PositionChanged?.Invoke(TimeSpan.FromMilliseconds(eventArgs.Time));
        MediaPlayer.LengthChanged += (_, eventArgs) =>
            DurationChanged?.Invoke(TimeSpan.FromMilliseconds(eventArgs.Length));
        MediaPlayer.Playing += (_, _) => MediaPlayer.SetRate(_playbackRate);
        MediaPlayer.EncounteredError += (_, _) =>
            PlaybackFailed?.Invoke("The media could not be played. Check that the file is still available and readable.");
    }

    public async Task OpenAsync(
        QueueJob job,
        UserSettings settings,
        CancellationToken cancellationToken = default)
    {
        MediaPlayer.Stop();
        _media?.Dispose();
        _media = null;
        CurrentJob = null;
        try
        {
            var playbackPath = await _playbackResolver.ResolveAsync(job.SourcePath, settings, cancellationToken);
            _media = new Media(_libVlc, playbackPath, FromType.FromPath);
            CurrentJob = job;
            PositionChanged?.Invoke(TimeSpan.Zero);
            DurationChanged?.Invoke(TimeSpan.Zero);
            if (!MediaPlayer.Play(_media))
                PlaybackFailed?.Invoke("The media player could not start this file.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PlaybackFailed?.Invoke($"Playback preparation failed: {exception.Message}");
        }
    }

    public void Play() => MediaPlayer.Play();

    public void SetRate(float rate)
    {
        _playbackRate = rate;
        if (CurrentJob is not null) MediaPlayer.SetRate(rate);
    }

    public void Pause()
    {
        if (MediaPlayer.IsPlaying) MediaPlayer.Pause();
    }

    public void Seek(TimeSpan position, bool playIfPaused = false)
    {
        MediaPlayer.Time = (long)position.TotalMilliseconds;
        PositionChanged?.Invoke(position);
        if (playIfPaused && !MediaPlayer.IsPlaying) MediaPlayer.Play();
    }

    public void Dispose()
    {
        MediaPlayer.Dispose();
        _media?.Dispose();
        _libVlc.Dispose();
    }
}
