using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

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

