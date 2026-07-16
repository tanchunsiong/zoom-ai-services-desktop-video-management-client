using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

namespace ZTranscribe.Infrastructure.Media;

public sealed partial class FfmpegAudioExtractor : IAudioExtractor
{
    private const long ZoomPartLimitBytes = 100L * 1024L * 1024L;

    public async Task<MediaProbe> ProbeAsync(string inputPath, UserSettings settings, CancellationToken cancellationToken)
    {
        var json = await ProcessRunner.RunAsync(settings.FfprobePath,
        [
            "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,sample_rate,channels,bit_rate",
            "-of", "json", inputPath
        ], null, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var audio = streams.FirstOrDefault(x => x.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");
        if (audio.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("The selected file has no audio stream.");
        var duration = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        return new MediaProbe(
            TimeSpan.FromSeconds(duration),
            GetString(audio, "codec_name"),
            GetInt(audio, "sample_rate"),
            GetInt(audio, "channels"),
            GetLong(audio, "bit_rate"),
            streams.Any(x => x.TryGetProperty("codec_type", out var type) && type.GetString() == "video"));
    }

    public async Task<IReadOnlyList<PreparedAudioPart>> ExtractAsync(
        QueueJob job,
        string workDirectory,
        UserSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var probe = await ProbeAsync(job.SourcePath, settings, cancellationToken);
        var profile = ProfileFor(probe.AudioCodec);
        job.DurationSeconds = probe.Duration.TotalSeconds;
        Directory.CreateDirectory(workDirectory);
        var segment = TimeSpan.FromMinutes(Math.Clamp(settings.SegmentMinutes, 1, 30));
        var count = Math.Max(1, (int)Math.Ceiling(probe.Duration.TotalSeconds / segment.TotalSeconds));
        var parts = new List<PreparedAudioPart>(count);

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = TimeSpan.FromSeconds(index * segment.TotalSeconds);
            var duration = probe.Duration - start < segment ? probe.Duration - start : segment;
            var output = Path.Combine(workDirectory, $"audio-{index + 1:000}.{profile.Extension}");
            await ProcessRunner.RunAsync(settings.FfmpegPath,
            [
                "-hide_banner", "-nostdin", "-y",
                "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-t", duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", job.SourcePath,
                "-map", "0:a:0", "-vn", "-c:a", "copy", output
            ], null, cancellationToken);

            var outputInfo = new FileInfo(output);
            if (outputInfo.Length > ZoomPartLimitBytes)
                throw new InvalidOperationException($"Audio part {index + 1} is larger than Zoom's 100 MB request limit. Choose a shorter segment duration.");
            parts.Add(new PreparedAudioPart(index, output, start, duration, profile.MimeType));
            progress?.Report((index + 1d) / count);
        }
        return parts;
    }

    private static (string Extension, string MimeType) ProfileFor(string codec) => codec.ToLowerInvariant() switch
    {
        "aac" or "alac" => ("m4a", "audio/mp4"),
        "mp3" => ("mp3", "audio/mpeg"),
        "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" or "pcm_f64le" => ("wav", "audio/wav"),
        _ => throw new InvalidOperationException(
            $"Audio codec '{codec}' cannot be placed in WAV, M4A, or MP3 without changing the audio. " +
            "Z Transcribe will not silently transcode it; convert it explicitly first.")
    };

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
    private static int GetInt(JsonElement element, string property) =>
        int.TryParse(GetString(element, property), out var value) ? value : 0;
    private static long? GetLong(JsonElement element, string property) =>
        long.TryParse(GetString(element, property), out var value) ? value : null;

    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex ProgressTime();
}

