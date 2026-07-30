namespace ZTranscribe.Core.Models;

public sealed record LiveVadSettings(
    double Threshold,
    int PrefixPaddingMs,
    int SilenceDurationMs,
    int MinPauseDurationMs);

public static class LiveVadPresets
{
    public static LiveVadSettings Microphone { get; } = new(0.5, 300, 350, 100);
    public static LiveVadSettings SpeakerLoopback { get; } = new(0.45, 300, 250, 50);
}

public sealed record LiveScribeOptions(
    string Language,
    double VadThreshold = 0.5,
    int PrefixPaddingMs = 300,
    int SilenceDurationMs = 350,
    int MinPauseDurationMs = 100,
    int ForcedCaptionIntervalMs = 0)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Language))
            throw new InvalidOperationException("A transcription language is required.");
        if (VadThreshold is < 0 or > 1)
            throw new InvalidOperationException("VAD threshold must be between 0 and 1.");
        if (PrefixPaddingMs is < 0 or > 5_000)
            throw new InvalidOperationException("Prefix padding must be between 0 and 5,000 ms.");
        if (SilenceDurationMs is < 250 or > 10_000)
            throw new InvalidOperationException("Silence duration must be between 250 and 10,000 ms.");
        if (MinPauseDurationMs is < 0 or > 5_000)
            throw new InvalidOperationException("Minimum pause must be between 0 and 5,000 ms.");
        if (ForcedCaptionIntervalMs != 0 &&
            ForcedCaptionIntervalMs is < 500 or > 15_000)
            throw new InvalidOperationException(
                "Forced caption cadence must be off or between 500 and 15,000 ms.");
    }
}

public sealed record LiveScribeEvent(
    string Type,
    string? Transcript = null,
    string? Error = null,
    bool IsDelta = false);
