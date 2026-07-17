using LibVLCSharp.Shared;
using System.IO;
using ZTranscribe.Core.Models;

namespace ZTranscribe.App.Services;

public sealed class VlcPlaybackService : IDisposable
{
    private readonly LibVLC _libVlc;
    private Media? _media;
    private float _playbackRate = 1f;
    public MediaPlayer MediaPlayer { get; }
    public QueueJob? CurrentJob { get; private set; }
    public event Action<TimeSpan>? PositionChanged;
    public event Action<TimeSpan>? DurationChanged;

    public VlcPlaybackService()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        MediaPlayer = new MediaPlayer(_libVlc);
        MediaPlayer.TimeChanged += (_, eventArgs) =>
            PositionChanged?.Invoke(TimeSpan.FromMilliseconds(eventArgs.Time));
        MediaPlayer.LengthChanged += (_, eventArgs) =>
            DurationChanged?.Invoke(TimeSpan.FromMilliseconds(eventArgs.Length));
        MediaPlayer.Playing += (_, _) => MediaPlayer.SetRate(_playbackRate);
    }

    public void Open(QueueJob job, string? subtitlePath)
    {
        _media?.Dispose();
        _media = new Media(_libVlc, new Uri(job.SourcePath));
        if (!string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath))
            _media.AddOption($":sub-file={new Uri(subtitlePath).AbsoluteUri}");
        CurrentJob = job;
        PositionChanged?.Invoke(TimeSpan.Zero);
        DurationChanged?.Invoke(TimeSpan.Zero);
        MediaPlayer.Play(_media);
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
