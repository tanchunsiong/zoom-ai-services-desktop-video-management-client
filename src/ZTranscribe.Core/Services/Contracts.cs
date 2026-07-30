using ZTranscribe.Core.Models;

namespace ZTranscribe.Core.Services;

public interface ICredentialVault
{
    Task<ApiCredentials?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ApiCredentials credentials, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}

public interface IQueueStore
{
    Task<IReadOnlyList<QueueJob>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IEnumerable<QueueJob> jobs, CancellationToken cancellationToken = default);
}

public interface IAudioExtractor
{
    Task<MediaProbe> ProbeAsync(string inputPath, UserSettings settings, CancellationToken cancellationToken);
    Task<IReadOnlyList<PreparedAudioPart>> ExtractAsync(
        QueueJob job,
        string workDirectory,
        UserSettings settings,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        long? uploadTargetBytes = null,
        TimeSpan? maximumSegmentDuration = null);
}

public interface IZoomAiClient
{
    Task<TranscriptDocument> TranscribeAsync(
        PreparedAudioPart part,
        string language,
        ApiCredentials credentials,
        CancellationToken cancellationToken);

    Task<TranslationResult> TranslateCuesAsync(
        IReadOnlyList<TranscriptCue> cues,
        string sourceLanguage,
        string targetLanguage,
        ApiCredentials credentials,
        CancellationToken cancellationToken);

    Task<SummaryResult> SummarizeAsync(
        string text,
        string language,
        ApiCredentials credentials,
        CancellationToken cancellationToken);

    Task TestCredentialsAsync(ApiCredentials credentials, CancellationToken cancellationToken);
}

public interface ILiveScribeClient
{
    Task StreamAsync(
        IAsyncEnumerable<byte[]> pcm16Frames,
        LiveScribeOptions options,
        ApiCredentials credentials,
        IProgress<LiveScribeEvent>? progress,
        CancellationToken cancellationToken);
}

