namespace ZTranscribe.Core.Models;

public sealed record TranscriptCue(int Index, TimeSpan Start, TimeSpan End, string Text)
{
    public TranscriptCue OffsetBy(TimeSpan offset) => this with
    {
        Start = Start + offset,
        End = End + offset
    };
}

public sealed record TranscriptDocument(
    string Language,
    IReadOnlyList<TranscriptCue> Cues,
    string Text,
    string? RequestId = null,
    string? Model = null);

public sealed record TranslationResult(
    IReadOnlyList<TranscriptCue> Cues,
    long InputCharacters,
    long OutputCharacters);

public sealed record SummaryResult(
    string Text,
    long InputCharacters,
    long OutputCharacters,
    string? RequestId = null,
    string? Model = null);

public sealed record SummaryOption(bool Enabled, string Name);

public sealed record PreparedAudioPart(
    int Index,
    string Path,
    TimeSpan TimelineStart,
    TimeSpan Duration,
    string MimeType);

public sealed record MediaProbe(
    TimeSpan Duration,
    string AudioCodec,
    int SampleRate,
    int Channels,
    long? BitRate,
    bool HasVideo);

public sealed record ApiCredentials(string ApiKey, string ApiSecret)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}

public sealed class UserSettings
{
    public const decimal DefaultScribeFastUsdPerMinute = 0.0033m;
    public const decimal DefaultTranslatorUsdPerMillionCharacters = 7.50m;
    public const decimal DefaultSummarizerUsdPerMillionCharacters = 0.40m;

    public string FfmpegPath { get; set; } = "ffmpeg.exe";
    public string FfprobePath { get; set; } = "ffprobe.exe";
    public string? OutputRoot { get; set; }
    public int ScribeConcurrency { get; set; } = 2;
    public int SegmentMinutes { get; set; } = 15;
    public decimal ScribeUsdPerMinute { get; set; } = DefaultScribeFastUsdPerMinute;
    public decimal TranslatorUsdPerMillionCharacters { get; set; } = DefaultTranslatorUsdPerMillionCharacters;
    public decimal SummarizerUsdPerMillionCharacters { get; set; } = DefaultSummarizerUsdPerMillionCharacters;
    public int EstimatedTranslationCharactersPerMinute { get; set; }
}

