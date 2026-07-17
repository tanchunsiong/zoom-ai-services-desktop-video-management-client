using System.Diagnostics;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;
using ZTranscribe.Infrastructure.Media;

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
    var estimate = JobCostEstimator.Estimate(job, settings);
    return estimate.ScribeUsd == 0.02m
        && estimate.TranslateUsd == 0.04m
        && estimate.TotalUsd == 0.06m
        && estimate.TranslationBillableCharacters == 2000
        && !estimate.UsesActualTranslationUsage;
});

Check("Default Scribe rate estimates cost as soon as duration is known", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        DurationSeconds = 120
    };
    var estimate = JobCostEstimator.Estimate(job, new UserSettings());
    return estimate.ScribeUsd == 2m * UserSettings.DefaultScribeFastUsdPerMinute;
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
    var estimate = JobCostEstimator.Estimate(job, settings);
    return estimate.TranslateUsd == 0.08m && estimate.TranslationBillableCharacters == 4000;
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
    var estimate = JobCostEstimator.Estimate(job, settings);
    return estimate.TranslateUsd == 0.03m
        && estimate.TranslationBillableCharacters == 3000
        && estimate.UsesActualTranslationUsage;
});

Check("Completed rows identify measured usage as actual", () =>
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
    var beforeCompletion = job.CostBasisLabel == "Estimate";
    job.CompletedAt = DateTimeOffset.UtcNow;
    job.Report(JobState.Ready, 100, "Ready");
    return beforeCompletion && job.CostBasisLabel == "Actual";
});

Check("Completed transcription remains estimated until requested translation finishes", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        TranslationLanguage = "fr-FR",
        CompletedAt = DateTimeOffset.UtcNow
    };
    job.Report(JobState.Ready, 100, "Transcript ready; translation has not been generated");
    return job.CostBasisLabel == "Estimate";
});

Check("Missing rates remain unavailable instead of appearing free", () =>
{
    var job = new QueueJob
    {
        SourcePath = "sample.mp4",
        TranslationLanguage = "fr-FR",
        DurationSeconds = 60
    };
    var estimate = JobCostEstimator.Estimate(job, new UserSettings
    {
        ScribeUsdPerMinute = 0,
        TranslatorUsdPerMillionCharacters = 0
    });
    return estimate.ScribeUsd is null && estimate.TranslateUsd is null && estimate.TotalUsd is null;
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

