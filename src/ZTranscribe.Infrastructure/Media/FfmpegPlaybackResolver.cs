using System.Security.Cryptography;
using System.Text;
using ZTranscribe.Core.Models;
using ZTranscribe.Infrastructure.Persistence;

namespace ZTranscribe.Infrastructure.Media;

public sealed class FfmpegPlaybackResolver(AppPaths paths)
{
    private readonly FfmpegAudioExtractor _audioProbe = new();

    public async Task<string> ResolveAsync(
        string sourcePath,
        UserSettings settings,
        CancellationToken cancellationToken = default)
    {
        var probe = await _audioProbe.ProbeAsync(sourcePath, settings, cancellationToken);
        if (!NeedsCompatibilityCopy(probe)) return sourcePath;

        var source = new FileInfo(sourcePath);
        var identity = string.Join('|',
            Path.GetFullPath(sourcePath),
            source.Length,
            source.LastWriteTimeUtc.Ticks,
            probe.AudioCodec,
            probe.SampleRate,
            probe.Channels,
            probe.AudioStreamIndex);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var cacheDirectory = Path.Combine(paths.WorkRoot, "playback");
        var output = Path.Combine(cacheDirectory, $"{key}.mkv");
        if (HasContent(output)) return output;

        Directory.CreateDirectory(cacheDirectory);
        var temporary = Path.Combine(cacheDirectory, $"{key}.{Guid.NewGuid():N}.tmp");
        try
        {
            await ProcessRunner.RunAsync(
                settings.FfmpegPath,
                BuildCompatibilityArguments(sourcePath, temporary, probe),
                null,
                cancellationToken);
            if (!HasContent(temporary))
                throw new InvalidOperationException("FFmpeg produced an empty playback compatibility file.");
            File.Move(temporary, output, true);
            return output;
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException)
            {
                // A failed cleanup must not hide the playback error.
            }
        }
    }

    internal static bool NeedsCompatibilityCopy(MediaProbe probe) =>
        !string.IsNullOrWhiteSpace(probe.AudioCodec) &&
        !FfmpegAudioExtractor.NormalizeProfile(probe).StreamCopy;

    internal static IReadOnlyList<string> BuildCompatibilityArguments(
        string input,
        string output,
        MediaProbe probe)
    {
        var audioStream = probe.AudioStreamIndex >= 0 ? $"0:{probe.AudioStreamIndex}" : "0:a:0";
        var sampleRate = probe.SampleRate > 0 ? probe.SampleRate : 48_000;
        return
        [
            "-hide_banner", "-nostdin", "-y",
            "-i", input,
            "-map", "0:v:0?",
            "-map", audioStream,
            "-map_metadata", "0",
            "-c:v", "copy",
            "-c:a", "pcm_s16le",
            "-ac", FfmpegAudioExtractor.OutputChannelsFor(probe.Channels).ToString(),
            "-ar", sampleRate.ToString(),
            "-f", "matroska",
            output
        ];
    }

    private static bool HasContent(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch (IOException) { return false; }
    }
}
