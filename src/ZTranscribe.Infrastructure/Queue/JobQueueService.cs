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
    private CancellationTokenSource? _currentCancellation;
    private TaskCompletionSource<bool>? _resumeSignal;
    public ObservableCollection<QueueJob> Jobs { get; } = [];
    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }
    public event EventHandler? StateChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var job in await queueStore.LoadAsync(cancellationToken)) Jobs.Add(job);
    }

    public async Task AddAsync(IEnumerable<string> files, string sourceLanguage, string? translationLanguage)
    {
        var existing = Jobs.Select(x => Path.GetFullPath(x.SourcePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<QueueJob>();
        foreach (var file in files.Where(File.Exists))
        {
            var full = Path.GetFullPath(file);
            if (!existing.Add(full)) continue;
            var job = new QueueJob
            {
                SourcePath = full,
                SourceLanguage = sourceLanguage,
                TranslationLanguage = translationLanguage ?? ""
            };
            Jobs.Add(job);
            added.Add(job);
        }

        if (added.Count > 0)
        {
            var settings = await settingsStore.LoadAsync();
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(added.Select(async job =>
            {
                await gate.WaitAsync();
                try
                {
                    var probe = await audioExtractor.ProbeAsync(job.SourcePath, settings, CancellationToken.None);
                    job.DurationSeconds = probe.Duration.TotalSeconds;
                }
                catch
                {
                    // Processing reports a full FFprobe error later; adding a file should remain non-blocking.
                }
                finally { gate.Release(); }
            }));
        }
        await SaveAsync();
    }

    public async Task RetryAsync(QueueJob job)
    {
        job.Error = null;
        job.Report(JobState.Queued, 0, "Queued to retry");
        await SaveAsync();
    }

    public async Task UpdateSourceLanguageAsync(QueueJob job, string sourceLanguage)
    {
        if (!job.CanConfigureSourceLanguage)
            throw new InvalidOperationException("The spoken language can only be changed before transcription starts.");
        job.SourceLanguage = sourceLanguage;
        if (job.TranslationLanguage == sourceLanguage)
        {
            job.TranslationLanguage = "";
            job.TranslatedVttPath = null;
        }
        await SaveAsync();
    }

    public async Task UpdateTranslationLanguageAsync(QueueJob job, string translationLanguage)
    {
        if (!job.CanConfigureLanguages)
            throw new InvalidOperationException("The translation target cannot be changed while this job is processing.");
        if (!string.IsNullOrWhiteSpace(translationLanguage) && translationLanguage == job.SourceLanguage)
            throw new InvalidOperationException("The translation target must differ from the spoken language.");

        if (!string.Equals(job.TranslationLanguage, translationLanguage, StringComparison.Ordinal))
        {
            job.TranslatedVttPath = null;
            job.TranslationInputCharacters = 0;
            job.TranslationOutputCharacters = 0;
        }
        job.TranslationLanguage = translationLanguage;
        if (job.State == JobState.Ready)
            job.StatusMessage = string.IsNullOrWhiteSpace(translationLanguage)
                ? "Transcript is ready to review"
                : "Transcript ready; translation has not been generated";
        await SaveAsync();
    }

    public async Task TranslateExistingAsync(QueueJob job)
    {
        if (IsRunning) throw new InvalidOperationException("Wait for the active queue operation to finish.");
        if (!job.CanReview || !File.Exists(job.OriginalVttPath))
            throw new InvalidOperationException("Complete transcription before translating this job.");
        if (string.IsNullOrWhiteSpace(job.TranslationLanguage))
            throw new InvalidOperationException("Choose a translation target in this job's queue row first.");
        if (job.TranslationLanguage == job.SourceLanguage)
            throw new InvalidOperationException("The translation target must differ from the spoken language.");

        var credentials = await credentialVault.LoadAsync();
        if (credentials is not { IsComplete: true })
            throw new InvalidOperationException("Add your Zoom API key and API secret in Settings first.");

        _runCancellation = new CancellationTokenSource();
        _currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(_runCancellation.Token);
        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            job.Error = null;
            var cancellationToken = _currentCancellation.Token;
            var cues = WebVtt.Parse(await File.ReadAllTextAsync(job.OriginalVttPath, cancellationToken));
            job.TranscriptCharacters = cues.Sum(cue => (long)cue.Text.Length);
            job.TranslationInputCharacters = 0;
            job.TranslationOutputCharacters = 0;
            var translated = (IReadOnlyList<TranscriptCue>)cues;
            var route = TranslationRoute.Build(job.SourceLanguage, job.TranslationLanguage);
            for (var step = 0; step < route.Count; step++)
            {
                var (source, target) = route[step];
                job.Report(JobState.Translating, 78 + step * 8,
                    route.Count == 1 ? $"Translating to {LanguageCatalog.NameFor(target)}" :
                    $"Translation step {step + 1} of {route.Count}: {LanguageCatalog.NameFor(source)} → {LanguageCatalog.NameFor(target)}");
                await SaveAsync();
                var result = await zoom.TranslateCuesAsync(
                    translated, source, target, credentials, cancellationToken);
                translated = result.Cues;
                job.TranslationInputCharacters += result.InputCharacters;
                job.TranslationOutputCharacters += result.OutputCharacters;
                await SaveAsync();
            }

            var output = Path.GetDirectoryName(job.OriginalVttPath)!;
            job.TranslatedVttPath = Path.Combine(output, $"translated-{job.TranslationLanguage}.vtt");
            await File.WriteAllTextAsync(
                job.TranslatedVttPath, WebVtt.Write(translated), cancellationToken);
            job.Report(JobState.Ready, 100, "Translated captions are ready to review");
        }
        catch (OperationCanceledException)
        {
            job.Report(JobState.Ready, 100, "Transcript ready; translation canceled");
        }
        catch (Exception exception)
        {
            job.Error = exception.Message;
            job.Report(JobState.Ready, 100, "Transcript ready; translation failed");
            throw;
        }
        finally
        {
            await SaveAsync();
            IsRunning = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _currentCancellation.Dispose();
            _currentCancellation = null;
            _runCancellation.Dispose();
            _runCancellation = null;
        }
    }

    public async Task RemoveAsync(QueueJob job)
    {
        if (job.State is JobState.Preparing or JobState.Transcribing or JobState.Translating)
            throw new InvalidOperationException("Cancel the active job before removing it.");
        Jobs.Remove(job);
        await SaveAsync();
    }

    public Task StartAsync() => RunJobsAsync(Jobs.Where(x => x.State == JobState.Queued).ToArray());

    public Task StartJobAsync(QueueJob job)
    {
        if (!job.CanStart) throw new InvalidOperationException("This job is not ready to start.");
        return RunJobsAsync([job]);
    }

    private async Task RunJobsAsync(IReadOnlyList<QueueJob> jobs)
    {
        if (IsRunning) throw new InvalidOperationException("Another queue operation is already running.");
        if (jobs.Count == 0) return;

        var credentials = await credentialVault.LoadAsync();
        if (credentials is not { IsComplete: true }) throw new InvalidOperationException("Add your Zoom API key and API secret in Settings first.");
        var settings = await settingsStore.LoadAsync();
        _runCancellation = new CancellationTokenSource();
        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            foreach (var job in jobs)
            {
                if (_runCancellation.IsCancellationRequested) break;
                await WaitIfPausedAsync(_runCancellation.Token);
                if (_runCancellation.IsCancellationRequested) break;

                _currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(_runCancellation.Token);
                try
                {
                    await ProcessAsync(job, credentials, settings, _currentCancellation.Token);
                }
                finally
                {
                    _currentCancellation.Dispose();
                    _currentCancellation = null;
                }
            }
        }
        catch (OperationCanceledException) when (_runCancellation.IsCancellationRequested)
        {
            // Closing the app or stopping the run releases a paused scheduler.
        }
        finally
        {
            ResetPause();
            IsRunning = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _runCancellation.Dispose();
            _runCancellation = null;
        }
    }

    public void CancelCurrent() => _currentCancellation?.Cancel();

    public void StopAll()
    {
        _runCancellation?.Cancel();
        _currentCancellation?.Cancel();
        ResetPause();
    }

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        IsPaused = true;
        _resumeSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (!IsPaused) return;
        ResetPause();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task WaitIfPausedAsync(CancellationToken cancellationToken)
    {
        while (IsPaused && _resumeSignal is { } signal)
            await signal.Task.WaitAsync(cancellationToken);
    }

    private void ResetPause()
    {
        IsPaused = false;
        _resumeSignal?.TrySetResult(true);
        _resumeSignal = null;
    }

    private async Task ProcessAsync(QueueJob job, ApiCredentials credentials, UserSettings settings, CancellationToken cancellationToken)
    {
        var work = Path.Combine(paths.WorkRoot, job.Id.ToString("N"));
        try
        {
            job.TranscriptCharacters = 0;
            job.TranslationInputCharacters = 0;
            job.TranslationOutputCharacters = 0;
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
            job.TranscriptCharacters = original.Text.Length;
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
                    var result = await zoom.TranslateCuesAsync(translated, source, target, credentials, cancellationToken);
                    translated = result.Cues;
                    job.TranslationInputCharacters += result.InputCharacters;
                    job.TranslationOutputCharacters += result.OutputCharacters;
                    await SaveAsync();
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

