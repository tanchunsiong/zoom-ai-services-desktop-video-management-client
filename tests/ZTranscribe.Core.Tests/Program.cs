using System.Diagnostics;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;
using ZTranscribe.Infrastructure.Media;
using ZTranscribe.Infrastructure.Persistence;
using ZTranscribe.Infrastructure.Queue;
using ZTranscribe.Infrastructure.Zoom;

if (args is ["--wait-for-cancellation", var markerPath])
{
    await File.WriteAllTextAsync(markerPath, Environment.ProcessId.ToString());
    await Task.Delay(TimeSpan.FromMinutes(1));
    return 0;
}

var failures = new List<string>();

Check("VTT round trip", () =>
{
    var cues = new[] { new TranscriptCue(1, TimeSpan.FromSeconds(1.25), TimeSpan.FromSeconds(4.5), "Hello") };
    var parsed = WebVtt.Parse(WebVtt.Write(cues));
    return parsed.Count == 1 && parsed[0].Text == "Hello" && parsed[0].Start == cues[0].Start;
});

Check("Japanese to Chinese bridges through English", () =>
{
    var route = TranslationRoute.Build("ja-JP", "zh-CN");
    return route.Count == 2 && route[0] == ("ja-JP", "en-US") && route[1] == ("en-US", "zh-CN");
});

Check("English translation is direct", () =>
{
    var route = TranslationRoute.Build("en-US", "it-IT");
    return route.Count == 1 && route[0] == ("en-US", "it-IT");
});

Check("Missing translation normalizes to no translation", () =>
{
    var job = new QueueJob { SourcePath = "sample.mp4", TranslationLanguage = null! };
    return job.TranslationLanguage == "";
});

Check("Queue actions follow job state", () =>
{
    var job = new QueueJob { SourcePath = "sample.mp4" };
    var queued = job.CanStart && !job.CanEnd && !job.CanRetry;
    job.Report(JobState.Transcribing, 25, "Transcribing");
    var active = !job.CanStart && job.CanEnd && !job.CanRemove;
    job.Report(JobState.Failed, 100, "Failed");
    var failed = !job.CanStart && !job.CanEnd && job.CanRetry && job.CanRemove;
    return queued && active && failed;
});

Check("Cost estimate itemizes Scribe and direct translation", () =>
{
    var settings = new UserSettings
    {
        ScribeUsdPerMinute = 0.01m,
        TranslatorUsdPerMillionCharacters = 20m
    };
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        SourceLanguage = "en-US",
        TranslationLanguage = "fr-FR",
        DurationSeconds = 120,
        TranscriptCharacters = 1000
    };
    var comparison = JobCostEstimator.Compare(job, settings);
    return comparison.Estimate.ScribeUsd == 0.02m
        && comparison.Estimate.TranslateUsd == 0.04m
        && comparison.Estimate.TotalUsd == 0.06m
        && comparison.EstimatedTranslationCharacters == 2000
        && comparison.Actual.TotalUsd is null;
});

Check("Default Scribe rate estimates cost as soon as duration is known", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        DurationSeconds = 120
    };
    var comparison = JobCostEstimator.Compare(job, new UserSettings());
    return comparison.Estimate.ScribeUsd == 2m * UserSettings.DefaultScribeFastUsdPerMinute;
});

Check("Processing time estimates use duration and text volume", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        SourceLanguage = "en-US",
        TranslationLanguage = "fr-FR",
        DurationSeconds = 120,
        TranscriptCharacters = 1_000,
        Summarize = true
    };
    var comparison = JobTimeEstimator.Compare(job, new UserSettings());
    return comparison.Estimate.Scribe is { TotalSeconds: > 0 }
        && comparison.Estimate.Translate is { TotalSeconds: > 0 }
        && comparison.Estimate.Summarize is { TotalSeconds: > 0 }
        && comparison.Estimate.Total is { TotalSeconds: > 0 }
        && JobTimeEstimator.Format(TimeSpan.FromSeconds(63)) == "1m 03s";
});

Check("Processing time actuals measure contiguous queue stages", () =>
{
    var started = DateTimeOffset.UtcNow.AddMinutes(-1);
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        DurationSeconds = 60,
        StartedAt = started,
        CompletedAt = started.AddSeconds(9)
    };
    job.Events.Add(new JobEvent(started, JobState.Preparing, "Preparing"));
    job.Events.Add(new JobEvent(started.AddSeconds(2), JobState.Transcribing, "Transcribing"));
    job.Events.Add(new JobEvent(started.AddSeconds(7), JobState.Ready, "Ready"));
    var actual = JobTimeEstimator.Compare(job, new UserSettings()).Actual;
    return actual.Scribe == TimeSpan.FromSeconds(7)
        && actual.Translate == TimeSpan.Zero
        && actual.Summarize == TimeSpan.Zero
        && actual.Total == TimeSpan.FromSeconds(7);
});

Check("Processing time estimates learn from completed jobs", () =>
{
    var firstStarted = DateTimeOffset.UtcNow.AddMinutes(-2);
    var first = new QueueJob
    {
        SourcePath = "first.mp4",
        DurationSeconds = 60,
        TranscriptCharacters = 10_000,
        SummaryInputCharacters = 10_000,
        Summarize = true,
        StartedAt = firstStarted,
        CompletedAt = firstStarted.AddSeconds(20)
    };
    first.Events.Add(new JobEvent(firstStarted, JobState.Preparing, "Preparing"));
    first.Events.Add(new JobEvent(firstStarted.AddSeconds(2), JobState.Transcribing, "Transcribing"));
    first.Events.Add(new JobEvent(firstStarted.AddSeconds(12), JobState.Summarizing, "Summarizing"));
    first.Events.Add(new JobEvent(firstStarted.AddSeconds(20), JobState.Ready, "Ready"));

    var secondStarted = DateTimeOffset.UtcNow.AddMinutes(-1);
    var second = new QueueJob
    {
        SourcePath = "second.mp4",
        DurationSeconds = 120,
        TranscriptCharacters = 20_000,
        SummaryInputCharacters = 20_000,
        Summarize = true,
        StartedAt = secondStarted,
        CompletedAt = secondStarted.AddSeconds(24)
    };
    second.Events.Add(new JobEvent(secondStarted, JobState.Preparing, "Preparing"));
    second.Events.Add(new JobEvent(secondStarted.AddSeconds(4), JobState.Transcribing, "Transcribing"));
    second.Events.Add(new JobEvent(secondStarted.AddSeconds(14), JobState.Summarizing, "Summarizing"));
    second.Events.Add(new JobEvent(secondStarted.AddSeconds(24), JobState.Ready, "Ready"));

    var calibration = JobTimeCalibration.Learn([first, second]);
    var pending = new QueueJob
    {
        SourcePath = "pending.mp4",
        DurationSeconds = 180,
        TranscriptCharacters = 15_000,
        Summarize = true
    };
    var estimate = JobTimeEstimator.Compare(pending, new UserSettings(), calibration).Estimate;
    return calibration.Scribe.SampleCount == 2
        && calibration.Summarize.SampleCount == 2
        && estimate.Scribe is { TotalSeconds: < 30 }
        && estimate.Summarize is { TotalSeconds: < 12 };
});

Check("Summary normalization removes overlapping subsections", () =>
{
    const string noisy = """
        # Recap
        One recap.

        # Summary
        ## Topic
        The same discussion explains the selection process and the meeting ends without a decision.

        ## Topic
        The same discussion explains the selection process and the meeting ends without a decision.

        ## Other topic
        A separate point about the next presentation.

        # Action Items
        - Review the proposal.
        """;
    var clean = ZoomAiClient.NormalizeSummaryText(noisy);
    return clean.Split("## Topic", StringSplitOptions.None).Length - 1 == 1
        && clean.Contains("## Other topic", StringComparison.Ordinal)
        && clean.Contains("# Action Items", StringComparison.Ordinal);
});

Check("Queue duration labels use hours minutes and seconds", () =>
{
    var known = new QueueJob { SourcePath = "sample.mp4", DurationSeconds = 3723 };
    var unknown = new QueueJob { SourcePath = "unknown.mp4" };
    return known.DurationLabel == "01:02:03" && unknown.DurationLabel == "--";
});

Check("Generated outputs are source-named sidecars", () =>
{
    var source = Path.Combine("C:\\media", "meeting.mp4");
    var paths = JobQueueService.OutputPathsFor(new QueueJob { SourcePath = source });
    return paths.Directory == Path.GetDirectoryName(source)
        && paths.OriginalVtt == Path.Combine("C:\\media", "meeting.vtt")
        && paths.TranscriptJson == Path.Combine("C:\\media", "meeting.transcript.json")
        && paths.TranslatedVtt("zh-CN") == Path.Combine("C:\\media", "meeting.translated-zh-CN.vtt")
        && paths.Summary == Path.Combine("C:\\media", "meeting.summary.md");
});

Check("Existing sidecars are discovered per task", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"ztranscribe-existing-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var job = new QueueJob
        {
            SourcePath = Path.Combine(directory, "meeting.mp4"),
            SourceLanguage = "en-US",
            TranslationLanguage = "zh-CN",
            Summarize = true
        };
        var paths = JobQueueService.OutputPathsFor(job);
        File.WriteAllText(paths.OriginalVtt, "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nHello");
        File.WriteAllText(paths.TranslatedVtt("zh-CN"), "WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nNi hao");
        File.WriteAllText(paths.Summary, "Summary");

        var outputs = JobQueueService.ExistingOutputsFor(job);
        var detected = JobQueueService.ApplyExistingOutputs(job, markComplete: true);
        return outputs.OriginalVtt == paths.OriginalVtt
            && outputs.TranslatedVtt == paths.TranslatedVtt("zh-CN")
            && outputs.Summary == paths.Summary
            && outputs.TranscriptJson is null
            && detected
            && job.ReuseExistingTranscript
            && job.ReuseExistingTranslation
            && job.ReuseExistingSummary
            && job.State == JobState.Ready;
    }
    finally
    {
        Directory.Delete(directory, true);
    }
});

Check("Empty and stale sidecars are not reused", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"ztranscribe-stale-{Guid.NewGuid():N}");
    Directory.CreateDirectory(directory);
    try
    {
        var job = new QueueJob
        {
            SourcePath = Path.Combine(directory, "meeting.mp4"),
            Summarize = true,
            ExistingSummaryIsStale = true
        };
        var paths = JobQueueService.OutputPathsFor(job);
        File.WriteAllText(paths.OriginalVtt, "This is not WebVTT content");
        File.WriteAllText(paths.Summary, "Old summary");
        var outputs = JobQueueService.ExistingOutputsFor(job);
        return outputs.OriginalVtt is null && outputs.Summary is null;
    }
    finally
    {
        Directory.Delete(directory, true);
    }
});

Check("Reused API tasks have zero estimated and actual cost", () =>
{
    var job = new QueueJob
    {
        SourcePath = "meeting.mp4",
        SourceLanguage = "en-US",
        TranslationLanguage = "zh-CN",
        Summarize = true,
        DurationSeconds = 60,
        ReuseExistingTranscript = true,
        ReuseExistingTranslation = true,
        ReuseExistingSummary = true,
        CompletedAt = DateTimeOffset.UtcNow
    };
    var comparison = JobCostEstimator.Compare(job, new UserSettings());
    return comparison.Estimate.TotalUsd == 0m && comparison.Actual.TotalUsd == 0m;
});

Check("Probe failures are exposed in the duration tooltip", () =>
{
    var job = new QueueJob
    {
        SourcePath = "broken.wmv",
        MediaProbeError = "ffprobe.exe failed: Invalid data found when processing input"
    };
    return job.DurationStatusLabel.Contains("Invalid data", StringComparison.Ordinal);
});

Check("Japanese pre-transcription translation estimate uses language-aware density", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        SourceLanguage = "ja-JP",
        TranslationLanguage = "zh-CN",
        DurationSeconds = 60
    };
    var comparison = JobCostEstimator.Compare(job, new UserSettings());
    return comparison.EstimatedTranslationCharacters == 1200
        && job.EstimateQualityLabel == "Rough";
});

Check("Bridged translation estimates both API steps", () =>
{
    var settings = new UserSettings { TranslatorUsdPerMillionCharacters = 20m };
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        SourceLanguage = "ja-JP",
        TranslationLanguage = "zh-CN",
        TranscriptCharacters = 1000
    };
    var comparison = JobCostEstimator.Compare(job, settings);
    return comparison.Estimate.TranslateUsd == 0.08m && comparison.EstimatedTranslationCharacters == 4000;
});

Check("Actual Zoom translation usage replaces the pre-job estimate", () =>
{
    var settings = new UserSettings { TranslatorUsdPerMillionCharacters = 10m };
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        SourceLanguage = "en-US",
        TranslationLanguage = "de-DE",
        TranscriptCharacters = 1000,
        TranslationInputCharacters = 1800,
        TranslationOutputCharacters = 1200
    };
    job.CompletedAt = DateTimeOffset.UtcNow;
    var comparison = JobCostEstimator.Compare(job, settings);
    return comparison.Estimate.TranslateUsd == 0.02m
        && comparison.Actual.TranslateUsd == 0.03m
        && comparison.ActualTranslationCharacters == 3000;
});

Check("Actual costs remain unavailable until completion", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        SourceLanguage = "en-US",
        TranslationLanguage = "de-DE",
        DurationSeconds = 60,
        TranslationInputCharacters = 1800,
        TranslationOutputCharacters = 1200
    };
    var beforeCompletion = JobCostEstimator.Compare(job, new UserSettings()).Actual.TotalUsd is null;
    job.CompletedAt = DateTimeOffset.UtcNow;
    job.Report(JobState.Ready, 100, "Ready");
    return beforeCompletion && JobCostEstimator.Compare(job, new UserSettings()).Actual.TotalUsd is not null;
});

Check("Completed transcription remains estimated until requested translation finishes", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        TranslationLanguage = "fr-FR",
        TranscriptCharacters = 1000,
        CompletedAt = DateTimeOffset.UtcNow
    };
    job.Report(JobState.Ready, 100, "Transcript ready; translation has not been generated");
    return JobCostEstimator.Compare(job, new UserSettings()).Actual.TranslateUsd is null;
});

Check("Summarizer estimate and actual use separate character totals", () =>
{
    var settings = new UserSettings { SummarizerUsdPerMillionCharacters = 0.40m };
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        DurationSeconds = 60,
        TranscriptCharacters = 10_000,
        Summarize = true,
        SummaryInputCharacters = 10_000,
        SummaryOutputCharacters = 1_000,
        CompletedAt = DateTimeOffset.UtcNow
    };
    var comparison = JobCostEstimator.Compare(job, settings);
    return comparison.EstimatedSummaryCharacters == 12_100
        && comparison.ActualSummaryCharacters == 11_000
        && comparison.Estimate.SummarizeUsd == 0.00484m
        && comparison.Actual.SummarizeUsd == 0.0044m;
});

Check("Missing rates remain unavailable instead of appearing free", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        TranslationLanguage = "fr-FR",
        DurationSeconds = 60
    };
    var comparison = JobCostEstimator.Compare(job, new UserSettings
    {
        ScribeUsdPerMinute = 0,
        TranslatorUsdPerMillionCharacters = 0,
        SummarizerUsdPerMillionCharacters = 0
    });
    return comparison.Estimate.ScribeUsd is null
        && comparison.Estimate.TranslateUsd is null
        && comparison.Estimate.TotalUsd is null;
});

Check("FFprobe accepts numeric and string fields", () =>
{
    var probe = FfmpegAudioExtractor.ParseProbe("""
        {
          "streams": [{
            "codec_type": "audio",
            "codec_name": "aac",
            "sample_rate": "48000",
            "channels": 2,
            "bit_rate": "192000"
          }],
          "format": { "duration": 123.5 }
        }
        """);
    return probe.AudioCodec == "aac"
        && probe.SampleRate == 48000
        && probe.Channels == 2
        && probe.BitRate == 192000
        && probe.Duration == TimeSpan.FromSeconds(123.5);
});

Check("FFprobe chooses the default audio track and falls back to stream duration", () =>
{
    var probe = FfmpegAudioExtractor.ParseProbe("""
        {
          "streams": [
            { "index": 0, "codec_type": "video", "duration": "9.5" },
            { "index": 1, "codec_type": "audio", "codec_name": "ac3", "sample_rate": 48000, "channels": 6, "duration": "9.5", "disposition": { "default": 0 } },
            { "index": 2, "codec_type": "audio", "codec_name": "aac", "sample_rate": 48000, "channels": 2, "duration": "9.5", "disposition": { "default": 1 } }
          ],
          "format": { "duration": "N/A" }
        }
        """);
    return probe.AudioCodec == "aac"
        && probe.AudioStreamIndex == 2
        && probe.Duration == TimeSpan.FromSeconds(9.5);
});

Check("Invalid FFprobe duration fails with an actionable error", () =>
{
    try
    {
        FfmpegAudioExtractor.ParseProbe("""{ "streams": [], "format": { "duration": "N/A" } }""");
        return false;
    }
    catch (InvalidOperationException exception)
    {
        return exception.Message.Contains("valid positive media duration", StringComparison.Ordinal);
    }
});

Check("Summarizer pre-transcript estimate is derived from media duration", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        DurationSeconds = 60,
        Summarize = true
    };
    var comparison = JobCostEstimator.Compare(job, new UserSettings());
    return comparison.EstimatedSummaryCharacters == 968
        && comparison.Estimate.SummarizeUsd == 0.0003872m;
});

Check("FFprobe keeps video duration when no audio stream exists", () =>
{
    var probe = FfmpegAudioExtractor.ParseProbe("""
        {
          "streams": [{ "codec_type": "video", "codec_name": "h264" }],
          "format": { "duration": "42.5" }
        }
        """);
    return probe.Duration == TimeSpan.FromSeconds(42.5)
        && probe.AudioCodec == ""
        && probe.HasVideo;
});

Check("No-audio media has zero estimated Scribe cost", () =>
{
    var job = new QueueJob { SourcePath = "silent.mp4", DurationSeconds = 120, HasAudio = false };
    return JobCostEstimator.Compare(job, new UserSettings()).Estimate.ScribeUsd == 0m;
});

Check("WMA uses PCM WAV compatibility extraction", () =>
{
    var profile = FfmpegAudioExtractor.ProfileFor("wmav2");
    var arguments = FfmpegAudioExtractor.BuildExtractionArguments(
        "input.wmv", "output.wav", TimeSpan.Zero, TimeSpan.FromMinutes(5), profile);
    return profile.Extension == "wav"
        && profile.MimeType == "audio/wav"
        && !profile.StreamCopy
        && arguments.Contains("pcm_s16le")
        && arguments.Contains("-ac")
        && arguments.Contains("2")
        && !arguments.Contains("-ar");
});

Check("AC3 and arbitrary decodable codecs use PCM WAV compatibility extraction", () =>
{
    var ac3 = FfmpegAudioExtractor.ProfileFor("ac3");
    var unknown = FfmpegAudioExtractor.ProfileFor("future_codec");
    return ac3 == unknown
        && ac3.Extension == "wav"
        && ac3.MimeType == "audio/wav"
        && ac3.OutputCodec == "pcm_s16le"
        && !ac3.StreamCopy;
});

Check("PCM and unknown-bitrate sources use safe PCM normalization", () =>
{
    var pcm = FfmpegAudioExtractor.ProfileFor("pcm_s24le");
    var unknownBitrate = FfmpegAudioExtractor.NormalizeProfile(
        new MediaProbe(TimeSpan.FromMinutes(2), "aac", 48_000, 2, null, true));
    return !pcm.StreamCopy
        && pcm.OutputCodec == "pcm_s16le"
        && !unknownBitrate.StreamCopy
        && unknownBitrate.Extension == "wav";
});

Check("PCM compatibility segments stay below the Zoom part limit", () =>
{
    var probe = new MediaProbe(TimeSpan.FromHours(1), "ac3", 48_000, 6, 448_000, true);
    var profile = FfmpegAudioExtractor.ProfileFor("ac3");
    var segment = FfmpegAudioExtractor.SegmentDurationFor(probe, profile, TimeSpan.FromMinutes(15));
    var estimatedBytes = segment.TotalSeconds * probe.SampleRate * FfmpegAudioExtractor.OutputChannelsFor(probe.Channels) * 2;
    return segment < TimeSpan.FromMinutes(15) && estimatedBytes <= 40_000_000L;
});

Check("Multichannel audio is downmixed to a Zoom-compatible stereo stream", () =>
{
    var profile = FfmpegAudioExtractor.ProfileFor("ac3");
    var arguments = FfmpegAudioExtractor.BuildExtractionArguments(
        "input.mpg", "output.wav", TimeSpan.Zero, TimeSpan.FromMinutes(5), profile, 6);
    return FfmpegAudioExtractor.OutputChannelsFor(6) == 2
        && arguments.Contains("-ac")
        && arguments.Contains("2");
});

Check("Playback normalization follows probed codec and channel layout", () =>
{
    var ac3Stereo = new MediaProbe(TimeSpan.FromSeconds(30), "ac3", 48_000, 2, 256_000, true);
    var ac3Surround = new MediaProbe(TimeSpan.FromSeconds(30), "ac3", 48_000, 6, 448_000, true)
    {
        AudioStreamIndex = 1
    };
    var aacStereo = new MediaProbe(TimeSpan.FromSeconds(30), "aac", 48_000, 2, 128_000, true);
    var silentVideo = new MediaProbe(TimeSpan.FromSeconds(30), "", 0, 0, null, true);
    var surroundArguments = FfmpegPlaybackResolver.BuildCompatibilityArguments("input.mpg", "output.mkv", ac3Surround);
    return FfmpegPlaybackResolver.NeedsCompatibilityCopy(ac3Stereo)
        && FfmpegPlaybackResolver.NeedsCompatibilityCopy(ac3Surround)
        && !FfmpegPlaybackResolver.NeedsCompatibilityCopy(aacStereo)
        && !FfmpegPlaybackResolver.NeedsCompatibilityCopy(silentVideo)
        && surroundArguments.Contains("0:1")
        && surroundArguments.Contains("pcm_s16le")
        && surroundArguments.Contains("2");
});

Check("Extraction maps the selected absolute audio stream and seeks after input", () =>
{
    var arguments = FfmpegAudioExtractor.BuildExtractionArguments(
        "input file.mpg", "output.wav", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        FfmpegAudioExtractor.ProfileFor("ac3"), 2, 2);
    var argumentList = arguments.ToList();
    var inputIndex = argumentList.IndexOf("-i");
    var seekIndex = argumentList.IndexOf("-ss");
    return arguments.Contains("0:2") && inputIndex >= 0 && seekIndex > inputIndex;
});

Check("Missing bitrate still gets a conservative PCM segment limit", () =>
{
    var probe = new MediaProbe(TimeSpan.FromHours(1), "mp3", 48_000, 2, null, false);
    var segment = FfmpegAudioExtractor.SegmentDurationFor(
        probe, FfmpegAudioExtractor.ProfileFor("mp3"), TimeSpan.FromMinutes(15));
    var estimatedBytes = segment.TotalSeconds * probe.SampleRate * 2 * 2;
    return estimatedBytes <= 40_000_000L;
});

Check("Stream-copy segments also target the conservative upload size", () =>
{
    var probe = new MediaProbe(TimeSpan.FromHours(1), "mp3", 48_000, 2, 512_000, false);
    var profile = FfmpegAudioExtractor.ProfileFor("mp3");
    var segment = FfmpegAudioExtractor.SegmentDurationFor(probe, profile, TimeSpan.FromMinutes(15));
    return segment < TimeSpan.FromMinutes(15)
        && segment.TotalSeconds * probe.BitRate / 8 <= 40_000_000L;
});

await CheckAsync("Temporary audio is deleted after an upload outcome", async () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"ztranscribe-cleanup-{Guid.NewGuid():N}");
    var file = Path.Combine(directory, "audio.wav");
    Directory.CreateDirectory(directory);
    await File.WriteAllTextAsync(file, "temporary audio");
    var fileDeleted = await WorkFileCleaner.DeleteFileAsync(file);
    var directoryDeleted = await WorkFileCleaner.DeleteDirectoryAsync(directory);
    return fileDeleted && directoryDeleted && !File.Exists(file) && !Directory.Exists(directory);
});

Check("Legacy codec failures direct the user to retry", () =>
{
    const string legacy = "Audio codec 'ac3' cannot be placed in WAV, M4A, or MP3 without changing the audio.";
    var upgraded = JsonQueueStore.UpgradeLegacyCodecError(legacy);
    return upgraded is not null
        && upgraded.Contains("previous audio compatibility policy", StringComparison.Ordinal)
        && upgraded.Contains("Retry", StringComparison.Ordinal)
        && JsonQueueStore.UpgradeLegacyCodecError("another failure") == "another failure";
});

await CheckAsync("Canceled child process is terminated", async () =>
{
    var markerPath = Path.Combine(Path.GetTempPath(), $"ztranscribe-process-{Guid.NewGuid():N}.pid");
    using var cancellation = new CancellationTokenSource();
    var runTask = ProcessRunner.RunAsync(
        DotnetHostPath(),
        [System.Reflection.Assembly.GetEntryAssembly()!.Location, "--wait-for-cancellation", markerPath],
        null,
        cancellation.Token);

    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int? processId = null;
        while (processId is null)
        {
            try
            {
                if (File.Exists(markerPath) &&
                    int.TryParse(await File.ReadAllTextAsync(markerPath, timeout.Token), out var parsedProcessId))
                    processId = parsedProcessId;
            }
            catch (IOException)
            {
                // The child may still hold the marker briefly after creating it.
            }
            if (processId is null) await Task.Delay(25, timeout.Token);
        }

        cancellation.Cancel();
        try
        {
            await runTask;
            return false;
        }
        catch (OperationCanceledException)
        {
            try
            {
                using var process = Process.GetProcessById(processId.Value);
                return process.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }
    finally
    {
        cancellation.Cancel();
        File.Delete(markerPath);
    }
});

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("All core checks passed.");
return 0;

void Check(string name, Func<bool> assertion)
{
    try
    {
        if (!assertion()) failures.Add($"FAIL: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL: {name}: {exception.Message}");
    }
}

async Task CheckAsync(string name, Func<Task<bool>> assertion)
{
    try
    {
        if (!await assertion()) failures.Add($"FAIL: {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL: {name}: {exception.Message}");
    }
}

string DotnetHostPath()
{
    var configuredHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
    if (!string.IsNullOrWhiteSpace(configuredHost)) return configuredHost;

    var runtimeDirectory = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
    var dotnetRoot = runtimeDirectory.Parent!.Parent!.Parent!.FullName;
    return Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
}

