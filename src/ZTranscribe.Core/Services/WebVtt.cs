using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ZTranscribe.Core.Models;

namespace ZTranscribe.Core.Services;

public static partial class WebVtt
{
    public static string Write(IEnumerable<TranscriptCue> cues)
    {
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var cue in cues.OrderBy(x => x.Start))
        {
            builder.AppendLine(cue.Index.ToString(CultureInfo.InvariantCulture));
            builder.Append(Format(cue.Start)).Append(" --> ").AppendLine(Format(cue.End));
            builder.AppendLine(cue.Text.Replace("-->", "→", StringComparison.Ordinal).Trim());
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public static IReadOnlyList<TranscriptCue> Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var blocks = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var cues = new List<TranscriptCue>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n');
            var timeLineIndex = Array.FindIndex(lines, x => x.Contains(" --> ", StringComparison.Ordinal));
            if (timeLineIndex < 0) continue;
            var match = TimestampLine().Match(lines[timeLineIndex]);
            if (!match.Success) continue;
            var body = string.Join("\n", lines.Skip(timeLineIndex + 1)).Trim();
            cues.Add(new TranscriptCue(cues.Count + 1, ParseTimestamp(match.Groups[1].Value),
                ParseTimestamp(match.Groups[2].Value), body));
        }
        return cues;
    }

    public static string Format(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    public static TimeSpan ParseTimestamp(string value)
    {
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
            !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
            throw new FormatException($"Invalid WebVTT timestamp: {value}");
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
    }

    [GeneratedRegex(@"(\d{1,3}:\d{2}:\d{2}[.,]\d{3})\s+-->\s+(\d{1,3}:\d{2}:\d{2}[.,]\d{3})")]
    private static partial Regex TimestampLine();
}
