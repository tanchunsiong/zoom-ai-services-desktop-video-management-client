using LibVLCSharp.Shared;
using System.IO;
using ZTranscribe.Core.Models;

namespace ZTranscribe.App.Services;

public sealed class VlcPlaybackService : IDisposable
{
    private readonly LibVLC _libVlc;
    private Media? _media;
    public MediaPlayer MediaPlayer { get; }
    public QueueJob? CurrentJob { get; private set; }

    public VlcPlaybackService()
    {
        LibVLCSharp.Shared.Core.Initialize();
        _libVlc = new LibVLC("--no-video-title-show");
        MediaPlayer = new MediaPlayer(_libVlc);
    }

    public void Open(QueueJob job, string? subtitlePath)
    {
        _media?.Dispose();
        _media = new Media(_libVlc, new Uri(job.SourcePath));
        if (!string.IsNullOrWhiteSpace(subtitlePath) && File.Exists(subtitlePath))
            _media.AddOption($":sub-file={new Uri(subtitlePath).AbsoluteUri}");
        CurrentJob = job;
        MediaPlayer.Play(_media);
    }

    public void Seek(TimeSpan position)
    {
        MediaPlayer.Time = (long)position.TotalMilliseconds;
        if (!MediaPlayer.IsPlaying) MediaPlayer.Play();
    }

    public void Dispose()
    {
        MediaPlayer.Dispose();
        _media?.Dispose();
        _libVlc.Dispose();
    }
}
