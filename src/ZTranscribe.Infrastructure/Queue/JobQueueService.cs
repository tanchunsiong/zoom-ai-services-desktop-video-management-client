using System.Collections.ObjectModel;
using System.Text.Json;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;
using ZTranscribe.Infrastructure.Persistence;

namespace ZTranscribe.Infrastructure.Queue;

public sealed class JobQueueService(
    IQueueStore queueStore,
    ISettingsStore settingsStore,
    ICredentialVault credentialVault,
    IAudioExtractor audioExtractor,
    IZoomAiClient zoom,
    AppPaths paths)
{
    private CancellationTokenSource? _runCancellation;
    public ObservableCollection<QueueJob> Jobs { get; } = [];
    public bool IsRunning { get; private set; }
    public event EventHandler? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var job in await queueStore.LoadAsync(cancellationToken)) Jobs.Add(job);
    }

    public async Task AddAsync(IEnumerable<string> files, string sourceLanguage, string? translationLanguage)
    {
        var existing = Jobs.Select(x => Path.GetFullPath(x.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.Where(File.Exists))
        {
            var full = Path.GetFullPath(file);
            if (existing.Add(full)) Jobs.Add(new QueueJob
            {
                SourcePath = full,
                SourceLanguage = sourceLanguage,
                TranslationLanguage = translationLanguage
            });
        }
        await SaveAsync();
    }

    public async Task RetryAsync(QueueJob job)
    {
        job.Error = null;
        job.Report(JobState.Queued, 0, "Queued to retry");
        await SaveAsync();
    }

    public async Task RemoveAsync(QueueJob job)
    {
        if (job.State is JobState.Preparing or JobState.Transcribing or JobState.Translating)
            throw new InvalidOperationException("Cancel the active job before removing it.");
        Jobs.Remove(job);
        await SaveAsync();
    }

    public async Task StartAsync()
    {
        if (IsRunning) return;
        var credentials = await credentialVault.LoadAsync();
        if (credentials is not { IsComplete: true }) throw new InvalidOperationException("Add your Zoom API key and API secret in Settings first.");
        var settings = await settingsStore.LoadAsync();
        _runCancellation = new CancellationTokenSource();
        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            foreach (var job in Jobs.Where(x => x.State == JobState.Queued).ToArray())
            {
                if (_runCancellation.IsCancellationRequested) break;
                await ProcessAsync(job, credentials, settings, _runCancellation.Token);
            }
        }
        finally
        {
            IsRunning = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _runCancellation.Dispose();
            _runCancellation = null;
        }
    }

    public void CancelCurrent() => _runCancellation?.Cancel();

    private async Task ProcessAsync(QueueJob job, ApiCredentials credentials, UserSettings settings, CancellationToken cancellationToken)
    {
        var work = Path.Combine(paths.WorkRoot, job.Id.ToString("N"));
        try
        {
            job.Report(JobState.Preparing, 4, "Inspecting media and copying the audio stream");
            await SaveAsync();
            var extractionProgress = new Progress<double>(value =>
                job.Progress = 4 + (int)Math.Round(value * 16));
            var parts = await audioExtractor.ExtractAsync(job, work, settings, extractionProgress, cancellationToken);

            job.Report(JobState.Transcribing, 22, $"Transcribing {parts.Count} audio part{(parts.Count == 1 ? "" : "s")}");
            await SaveAsync();
            var documents = new TranscriptDocument[parts.Count];
            using (var gate = new SemaphoreSlim(Math.Clamp(settings.ScribeConcurrency, 1, 4)))
            {
                var completed = 0;
                await Task.WhenAll(parts.Select(async part =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        documents[part.Index] = await zoom.TranscribeAsync(part, job.SourceLanguage, credentials, cancellationToken);
                        var done = Interlocked.Increment(ref completed);
                        job.Progress = 22 + (int)Math.Round(done / (double)parts.Count * 53);
                        job.StatusMessage = $"Transcribed {done} of {parts.Count} parts";
                    }
                    finally { gate.Release(); }
                }));
            }

            var originalCues = documents.SelectMany((document, index) =>
                    document.Cues.Select(cue => cue.OffsetBy(parts[index].TimelineStart)))
                .OrderBy(x => x.Start)
                .Select((cue, index) => cue with { Index = index + 1 })
                .ToArray();
            var original = new TranscriptDocument(job.SourceLanguage, originalCues,
                string.Join("\n", originalCues.Select(x => x.Text)));
            var output = OutputDirectory(job, settings);
            Directory.CreateDirectory(output);
            job.OriginalVttPath = Path.Combine(output, "original.vtt");
            job.TranscriptJsonPath = Path.Combine(output, "transcript.json");
            await File.WriteAllTextAsync(job.OriginalVttPath, WebVtt.Write(originalCues), cancellationToken);
            await File.WriteAllTextAsync(job.TranscriptJsonPath,
                JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

            if (!string.IsNullOrWhiteSpace(job.TranslationLanguage) && job.TranslationLanguage != job.SourceLanguage)
            {
                var route = TranslationRoute.Build(job.SourceLanguage, job.TranslationLanguage);
                var translated = (IReadOnlyList<TranscriptCue>)originalCues;
                for (var step = 0; step < route.Count; step++)
                {
                    var (source, target) = route[step];
                    job.Report(JobState.Translating, 78 + step * 8,
                        route.Count == 1 ? $"Translating to {LanguageCatalog.NameFor(target)}" :
                        $"Translation step {step + 1} of {route.Count}: {LanguageCatalog.NameFor(source)} → {LanguageCatalog.NameFor(target)}");
                    translated = await zoom.TranslateCuesAsync(translated, source, target, credentials, cancellationToken);
                }
                job.TranslatedVttPath = Path.Combine(output, $"translated-{job.TranslationLanguage}.vtt");
                await File.WriteAllTextAsync(job.TranslatedVttPath, WebVtt.Write(translated), cancellationToken);
            }

            job.CompletedAt = DateTimeOffset.UtcNow;
            job.Report(JobState.Ready, 100, "Transcript is ready to review");
        }
        catch (OperationCanceledException)
        {
            job.Report(JobState.Canceled, job.Progress, "Canceled");
        }
        catch (Exception exception)
        {
            job.Error = exception.Message;
            job.Report(JobState.Failed, 100, "Processing failed");
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { /* next launch cleanup */ }
            await SaveAsync();
        }
    }

    private static string OutputDirectory(QueueJob job, UserSettings settings)
    {
        var parent = settings.OutputRoot;
        if (string.IsNullOrWhiteSpace(parent))
            parent = Path.Combine(Path.GetDirectoryName(job.SourcePath)!, "Z Transcribe Outputs");
        var name = string.Concat(Path.GetFileNameWithoutExtension(job.SourcePath)
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(parent, name);
    }

    private Task SaveAsync() => queueStore.SaveAsync(Jobs);
}

