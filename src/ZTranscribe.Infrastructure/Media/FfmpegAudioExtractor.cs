using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

namespace ZTranscribe.Infrastructure.Media;

public sealed partial class FfmpegAudioExtractor : IAudioExtractor
{
    private const long ZoomPartLimitBytes = 100L * 1024L * 1024L;
    private const long PcmPartTargetBytes = 90_000_000L;
    internal sealed record AudioProfile(string Extension, string MimeType, string OutputCodec, bool StreamCopy);

    public async Task<MediaProbe> ProbeAsync(string inputPath, UserSettings settings, CancellationToken cancellationToken)
    {
        var json = await ProcessRunner.RunAsync(settings.FfprobePath,
        [
            "-v", "error", "-show_entries", "format=duration:stream=codec_type,codec_name,sample_rate,channels,bit_rate",
            "-of", "json", inputPath
        ], null, cancellationToken);
        return ParseProbe(json);
    }

    internal static MediaProbe ParseProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var duration = double.Parse(
            GetString(document.RootElement.GetProperty("format"), "duration"),
            CultureInfo.InvariantCulture);
        var audio = streams.FirstOrDefault(x => x.TryGetProperty("codec_type", out var type) && type.GetString() == "audio");
        var hasAudio = audio.ValueKind != JsonValueKind.Undefined;
        return new MediaProbe(
            TimeSpan.FromSeconds(duration),
            hasAudio ? GetString(audio, "codec_name") : "",
            hasAudio ? GetInt(audio, "sample_rate") : 0,
            hasAudio ? GetInt(audio, "channels") : 0,
            hasAudio ? GetLong(audio, "bit_rate") : null,
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
        if (string.IsNullOrWhiteSpace(probe.AudioCodec))
            throw new InvalidOperationException("The selected file has no audio stream to transcribe.");
        var profile = ProfileFor(probe.AudioCodec);
        job.DurationSeconds = probe.Duration.TotalSeconds;
        Directory.CreateDirectory(workDirectory);
        var requestedSegment = TimeSpan.FromMinutes(Math.Clamp(settings.SegmentMinutes, 1, 30));
        var segment = SegmentDurationFor(probe, profile, requestedSegment);
        var count = Math.Max(1, (int)Math.Ceiling(probe.Duration.TotalSeconds / segment.TotalSeconds));
        var parts = new List<PreparedAudioPart>(count);
        if (!profile.StreamCopy)
            job.StatusMessage = $"Decoding {probe.AudioCodec} to PCM WAV without resampling or remixing";

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = TimeSpan.FromSeconds(index * segment.TotalSeconds);
            var duration = probe.Duration - start < segment ? probe.Duration - start : segment;
            var output = Path.Combine(workDirectory, $"audio-{index + 1:000}.{profile.Extension}");
            await ProcessRunner.RunAsync(
                settings.FfmpegPath,
                BuildExtractionArguments(job.SourcePath, output, start, duration, profile),
                null,
                cancellationToken);

            var outputInfo = new FileInfo(output);
            if (outputInfo.Length > ZoomPartLimitBytes)
                throw new InvalidOperationException($"Audio part {index + 1} is larger than Zoom's 100 MB request limit. Choose a shorter segment duration.");
            parts.Add(new PreparedAudioPart(index, output, start, duration, profile.MimeType));
            progress?.Report((index + 1d) / count);
        }
        return parts;
    }

    internal static AudioProfile ProfileFor(string codec) => codec.ToLowerInvariant() switch
    {
        "aac" or "alac" => new("m4a", "audio/mp4", "copy", true),
        "mp3" => new("mp3", "audio/mpeg", "copy", true),
        "pcm_s16le" or "pcm_s24le" or "pcm_s32le" or "pcm_f32le" or "pcm_f64le" =>
            new("wav", "audio/wav", "copy", true),
        _ => new("wav", "audio/wav", "pcm_s16le", false)
    };

    internal static TimeSpan SegmentDurationFor(
        MediaProbe probe,
        AudioProfile profile,
        TimeSpan requested)
    {
        if (profile.StreamCopy) return requested;
        var sampleRate = probe.SampleRate > 0 ? probe.SampleRate : 48_000;
        var channels = probe.Channels > 0 ? probe.Channels : 2;
        var pcmBytesPerSecond = checked((long)sampleRate * channels * 2L);
        var maximumSeconds = Math.Max(1, PcmPartTargetBytes / pcmBytesPerSecond);
        return TimeSpan.FromSeconds(Math.Min(requested.TotalSeconds, maximumSeconds));
    }

    internal static IReadOnlyList<string> BuildExtractionArguments(
        string input,
        string output,
        TimeSpan start,
        TimeSpan duration,
        AudioProfile profile) =>
    [
        "-hide_banner", "-nostdin", "-y",
        "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-t", duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-i", input,
        "-map", "0:a:0", "-vn", "-c:a", profile.OutputCodec, output
    ];

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        } : "";
    private static int GetInt(JsonElement element, string property) =>
        int.TryParse(GetString(element, property), out var value) ? value : 0;
    private static long? GetLong(JsonElement element, string property) =>
        long.TryParse(GetString(element, property), out var value) ? value : null;

    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex ProgressTime();
}

