using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

namespace ZTranscribe.Infrastructure.Media;

public sealed partial class FfmpegAudioExtractor : IAudioExtractor
{
    private const long ZoomPartLimitBytes = 100L * 1024L * 1024L;
    private const long CompatibilityBitRate = 128_000L;
    internal const long UploadPartTargetBytes = 80_000_000L;
    internal sealed record AudioProfile(
        string Extension,
        string MimeType,
        string OutputCodec,
        bool StreamCopy,
        long? OutputBitRate = null);

    public async Task<MediaProbe> ProbeAsync(string inputPath, UserSettings settings, CancellationToken cancellationToken)
    {
        var json = await ProcessRunner.RunAsync(settings.FfprobePath,
        [
            "-v", "error", "-show_entries", "format=duration:stream=index,codec_type,codec_name,sample_rate,channels,bit_rate,duration,disposition",
            "-of", "json", inputPath
        ], null, cancellationToken);
        return ParseProbe(json);
    }

    internal static MediaProbe ParseProbe(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.TryGetProperty("streams", out var streamProperty) && streamProperty.ValueKind == JsonValueKind.Array
            ? streamProperty.EnumerateArray().Select((stream, position) => new StreamCandidate(
                stream,
                position,
                StreamIndex(stream, position)))
                .ToArray()
            : [];
        var audio = streams
            .Where(candidate => IsType(candidate.Stream, "audio") &&
                                !string.IsNullOrWhiteSpace(GetString(candidate.Stream, "codec_name")))
            .OrderByDescending(candidate => IsDefault(candidate.Stream))
            .ThenBy(candidate => candidate.Position)
            .FirstOrDefault();
        var hasAudio = audio is not null;
        var durationSeconds = PositiveSeconds(
            root.TryGetProperty("format", out var format) ? GetString(format, "duration") : "")
            ?? (audio is not null ? PositiveSeconds(GetString(audio.Stream, "duration")) : null)
            ?? streams.Select(candidate => PositiveSeconds(GetString(candidate.Stream, "duration")))
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .DefaultIfEmpty()
                .Max();
        if (durationSeconds <= 0)
            throw new InvalidOperationException("FFprobe did not return a valid positive media duration.");

        var probe = new MediaProbe(
            TimeSpan.FromSeconds(durationSeconds),
            hasAudio ? GetString(audio!.Stream, "codec_name").Trim().ToLowerInvariant() : "",
            hasAudio ? GetInt(audio!.Stream, "sample_rate") : 0,
            hasAudio ? GetInt(audio!.Stream, "channels") : 0,
            hasAudio ? GetLong(audio!.Stream, "bit_rate") : null,
            streams.Any(candidate => IsType(candidate.Stream, "video")))
        {
            AudioStreamIndex = hasAudio
                ? audio!.Index
                : -1
        };
        return probe;
    }

    public async Task<IReadOnlyList<PreparedAudioPart>> ExtractAsync(
        QueueJob job,
        string workDirectory,
        UserSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        long? uploadTargetBytes = null)
    {
        var probe = await ProbeAsync(job.SourcePath, settings, cancellationToken);
        if (string.IsNullOrWhiteSpace(probe.AudioCodec))
            throw new InvalidOperationException("The selected file has no audio stream to transcribe.");
        if (probe.Duration <= TimeSpan.Zero)
            throw new InvalidOperationException("The selected media has no positive duration.");
        var profile = NormalizeProfile(probe);
        job.DurationSeconds = probe.Duration.TotalSeconds;
        Directory.CreateDirectory(workDirectory);
        var requestedSegment = TimeSpan.FromMinutes(Math.Clamp(settings.SegmentMinutes, 1, 30));
        var segment = SegmentDurationFor(probe, profile, requestedSegment, uploadTargetBytes);
        var count = Math.Max(1, (int)Math.Ceiling(probe.Duration.TotalSeconds / segment.TotalSeconds));
        var parts = new List<PreparedAudioPart>(count);
        var outputChannels = OutputChannelsFor(probe.Channels);
        if (!profile.StreamCopy)
            job.StatusMessage = $"Decoding {probe.AudioCodec} to {profile.Extension.ToUpperInvariant()} ({outputChannels} channels) for Scribe compatibility";

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = TimeSpan.FromSeconds(index * segment.TotalSeconds);
            var duration = probe.Duration - start < segment ? probe.Duration - start : segment;
            if (duration <= TimeSpan.Zero) break;
            var output = Path.Combine(workDirectory, $"audio-{index + 1:000}.{profile.Extension}");
            await ProcessRunner.RunAsync(
                settings.FfmpegPath,
                BuildExtractionArguments(job.SourcePath, output, start, duration, profile, outputChannels, probe.AudioStreamIndex),
                null,
                cancellationToken);

            var outputInfo = new FileInfo(output);
            if (!outputInfo.Exists || outputInfo.Length == 0)
                throw new InvalidOperationException($"FFmpeg produced an empty audio part {index + 1}. The source may be truncated or unreadable.");
            if (outputInfo.Length > ZoomPartLimitBytes)
                throw new InvalidOperationException($"Audio part {index + 1} is larger than Zoom's 100 MB request limit. Choose a shorter segment duration.");
            parts.Add(new PreparedAudioPart(index, output, start, duration, profile.MimeType));
            progress?.Report((index + 1d) / count);
        }
        return parts;
    }

    internal static AudioProfile ProfileFor(string? codec) => (codec ?? "").Trim().ToLowerInvariant() switch
    {
        "aac" or "alac" => new("m4a", "audio/mp4", "copy", true),
        "mp3" => new("mp3", "audio/mpeg", "copy", true),
        _ => CompatibilityProfile()
    };

    internal static AudioProfile NormalizeProfile(MediaProbe probe)
    {
        var profile = ProfileFor(probe.AudioCodec);
        return profile.StreamCopy && (probe.Channels > 2 || probe.BitRate is not > 0)
            ? CompatibilityProfile()
            : profile;
    }

    private static AudioProfile CompatibilityProfile() =>
        new("mp3", "audio/mpeg", "libmp3lame", false, CompatibilityBitRate);

    internal static TimeSpan SegmentDurationFor(
        MediaProbe probe,
        AudioProfile profile,
        TimeSpan requested,
        long? uploadTargetBytes = null)
    {
        if (requested <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requested), "The segment duration must be positive.");

        long bytesPerSecond;
        if (profile.OutputBitRate is > 0)
        {
            bytesPerSecond = Math.Max(1, profile.OutputBitRate.Value / 8);
        }
        else if (profile.StreamCopy && probe.BitRate is > 0)
        {
            bytesPerSecond = Math.Max(1, probe.BitRate.Value / 8);
        }
        else
        {
            var sampleRate = probe.SampleRate > 0 ? probe.SampleRate : 48_000;
            var channels = OutputChannelsFor(probe.Channels);
            bytesPerSecond = checked((long)sampleRate * channels * 2L);
        }
        var maximumSeconds = Math.Max(1, (uploadTargetBytes ?? UploadPartTargetBytes) / bytesPerSecond);
        return TimeSpan.FromSeconds(Math.Min(requested.TotalSeconds, maximumSeconds));
    }

    internal static IReadOnlyList<string> BuildExtractionArguments(
        string input,
        string output,
        TimeSpan start,
        TimeSpan duration,
        AudioProfile profile,
        int outputChannels = 2,
        int audioStreamIndex = -1)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-i", input,
            "-ss", start.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-t", duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-map", audioStreamIndex >= 0 ? $"0:{audioStreamIndex}" : "0:a:0", "-vn", "-c:a", profile.OutputCodec
        };
        if (!profile.StreamCopy)
        {
            arguments.Add("-ac");
            arguments.Add(OutputChannelsFor(outputChannels).ToString(CultureInfo.InvariantCulture));
            if (profile.OutputBitRate is > 0)
            {
                arguments.Add("-b:a");
                arguments.Add($"{profile.OutputBitRate.Value / 1000}k");
            }
        }
        arguments.Add(output);
        return arguments;
    }

    internal static int OutputChannelsFor(int channels) => Math.Clamp(channels, 1, 2);

    private sealed record StreamCandidate(JsonElement Stream, int Position, int Index);

    private static bool IsType(JsonElement stream, string type) =>
        stream.TryGetProperty("codec_type", out var value) &&
        string.Equals(value.GetString(), type, StringComparison.OrdinalIgnoreCase);

    private static bool IsDefault(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var disposition) &&
        GetInt(disposition, "default") == 1;

    private static int StreamIndex(JsonElement stream, int fallback) =>
        stream.TryGetProperty("index", out var value) &&
        int.TryParse(GetString(stream, "index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            ? index
            : fallback;

    private static double? PositiveSeconds(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        double.IsFinite(seconds) && seconds > 0
            ? seconds
            : null;

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        } : "";
    private static int GetInt(JsonElement element, string property) =>
        int.TryParse(GetString(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static long? GetLong(JsonElement element, string property) =>
        long.TryParse(GetString(element, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex ProgressTime();
}

