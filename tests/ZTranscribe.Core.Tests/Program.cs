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
        while (!File.Exists(markerPath)) await Task.Delay(25, timeout.Token);
        var processId = int.Parse(await File.ReadAllTextAsync(markerPath, timeout.Token));

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
                using var process = Process.GetProcessById(processId);
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

