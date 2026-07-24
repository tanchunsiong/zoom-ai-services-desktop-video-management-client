using System.Collections.ObjectModel;
using System.Text.Json;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;
using ZTranscribe.Infrastructure.Media;
using ZTranscribe.Infrastructure.Persistence;
using ZTranscribe.Infrastructure.Zoom;

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
        var foundExistingOutputs = false;
        foreach (var job in await queueStore.LoadAsync(cancellationToken))
        {
            foundExistingOutputs |= ApplyExistingOutputs(job, markComplete: true);
            Jobs.Add(job);
        }
        var missing = Jobs.Where(job => job.DurationSeconds is null && File.Exists(job.SourcePath)).ToArray();
        if (missing.Length == 0)
        {
            if (foundExistingOutputs) await SaveAsync();
            return;
        }
        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (await ProbeDurationsAsync(missing, settings, cancellationToken) > 0 || foundExistingOutputs)
            await SaveAsync();
    }

    public async Task<int> AddAsync(IEnumerable<string> files, string sourceLanguage, string? translationLanguage)
    {
        var added = new List<QueueJob>();
        foreach (var file in files.Where(File.Exists))
        {
            var full = Path.GetFullPath(file);
            var job = new QueueJob
            {
                SourcePath = full,
                SourceLanguage = sourceLanguage,
                TranslationLanguage = translationLanguage ?? "",
                Summarize = true
            };
            ApplyExistingOutputs(job, markComplete: true);
            Jobs.Add(job);
            added.Add(job);
        }

        if (added.Count > 0)
        {
            var settings = await settingsStore.LoadAsync();
            await ProbeDurationsAsync(added, settings, CancellationToken.None);
        }
        await SaveAsync();
        return added.Count;
    }

    public async Task RetryAsync(QueueJob job)
    {
        QueueForRetry(job);
        await SaveAsync();
    }

    public async Task<int> RetryAllFailedAsync()
    {
        if (IsRunning) throw new InvalidOperationException("Wait for the active queue operation to finish.");
        var failed = Jobs.Where(job => job.CanRetry).ToArray();
        foreach (var job in failed) QueueForRetry(job);
        if (failed.Length > 0) await SaveAsync();
        return failed.Length;
    }

    public async Task UpdateSourceLanguageAsync(QueueJob job, string sourceLanguage)
    {
        if (!job.CanConfigureSourceLanguage)
            throw new InvalidOperationException("The spoken language can only be changed before transcription starts.");
        if (!string.Equals(job.SourceLanguage, sourceLanguage, StringComparison.Ordinal))
        {
            job.SourceLanguage = sourceLanguage;
            job.ExistingSummaryIsStale = true;
            job.ReuseExistingSummary = false;
        }
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
            job.SummaryPath = null;
            job.SummaryInputCharacters = 0;
            job.SummaryOutputCharacters = 0;
            job.ExistingSummaryIsStale = true;
            job.ReuseExistingSummary = false;
        }
        job.TranslationLanguage = translationLanguage;
        ApplyExistingOutputs(job, markComplete: false);
        if (job.State == JobState.Ready)
            job.StatusMessage = string.IsNullOrWhiteSpace(translationLanguage)
                ? "Transcript is ready to review"
                : job.ReuseExistingTranslation
                    ? "Existing translated captions found"
                    : "Transcript ready; translation has not been generated";
        await SaveAsync();
    }

    public async Task UpdateSummarizeAsync(QueueJob job, bool summarize)
    {
        if (!job.CanConfigureLanguages)
            throw new InvalidOperationException("Summarizer cannot be changed while this job is processing.");
        if (job.Summarize == summarize) return;

        job.Summarize = summarize;
        job.SummaryInputCharacters = 0;
        job.SummaryOutputCharacters = 0;
        job.SummaryPath = null;
        ApplyExistingOutputs(job, markComplete: false);
        if (job.State == JobState.Ready)
            job.StatusMessage = summarize
                ? job.ReuseExistingSummary
                    ? "Existing summary found"
                    : "Transcript ready; summary has not been generated"
                : "Transcript is ready to review";
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

        ApplyExistingOutputs(job, markComplete: false);
        if (job.ReuseExistingTranslation)
        {
            job.Error = null;
            job.Report(JobState.Ready, 100, "Using existing translated captions");
            await SaveAsync();
            return;
        }

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
            job.StartedAt = DateTimeOffset.UtcNow;
            job.CompletedAt = null;
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

            job.TranslatedVttPath = OutputPathsFor(job).TranslatedVtt(job.TranslationLanguage);
            await File.WriteAllTextAsync(
                job.TranslatedVttPath, WebVtt.Write(translated), cancellationToken);
            job.CompletedAt = DateTimeOffset.UtcNow;
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

    public async Task SummarizeExistingAsync(QueueJob job, bool force = false)
    {
        if (IsRunning) throw new InvalidOperationException("Wait for the active queue operation to finish.");
        if (!job.CanReview || !File.Exists(job.OriginalVttPath))
            throw new InvalidOperationException("Complete transcription before summarizing this job.");
        if (!job.Summarize)
            throw new InvalidOperationException("Choose Summarize in this job's queue row first.");

        ApplyExistingOutputs(job, markComplete: false);
        if (force)
        {
            // An explicit rerun is allowed to replace an existing summary.
            job.ReuseExistingSummary = false;
        }

        if (job.ReuseExistingSummary)
        {
            job.Error = null;
            job.Report(JobState.Ready, 100, "Using existing summary");
            await SaveAsync();
            return;
        }

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
            job.StartedAt = DateTimeOffset.UtcNow;
            job.CompletedAt = null;
            var cancellationToken = _currentCancellation.Token;
            var hasTranslatedCaptions = !string.IsNullOrWhiteSpace(job.TranslationLanguage) &&
                File.Exists(job.TranslatedVttPath);
            var captionPath = hasTranslatedCaptions ? job.TranslatedVttPath! : job.OriginalVttPath;
            var cues = WebVtt.Parse(await File.ReadAllTextAsync(captionPath, cancellationToken));
            var language = hasTranslatedCaptions ? job.TranslationLanguage : job.SourceLanguage;
            await GenerateSummaryAsync(job, cues, language, credentials, cancellationToken);
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.Report(JobState.Ready, 100, "Summary is ready to review");
        }
        catch (OperationCanceledException)
        {
            job.Report(JobState.Ready, 100, "Transcript ready; summarization canceled");
        }
        catch (Exception exception)
        {
            job.Error = exception.Message;
            job.Report(JobState.Ready, 100, "Transcript ready; summarization failed");
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

    public Task RemoveAsync(QueueJob job) => RemoveManyAsync([job]);

    public async Task<int> RemoveManyAsync(IEnumerable<QueueJob> jobs)
    {
        var selected = jobs.Distinct().Where(Jobs.Contains).ToArray();
        if (selected.Any(job => job.CanEnd))
            throw new InvalidOperationException("Cancel the active job before removing it.");
        foreach (var job in selected) Jobs.Remove(job);
        if (selected.Length == 0) return 0;
        await SaveAsync();
        return selected.Length;
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

    private static void QueueForRetry(QueueJob job)
    {
        job.Error = null;
        job.StartedAt = null;
        job.CompletedAt = null;
        job.Report(JobState.Queued, 0, "Queued to retry");
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
            job.Error = null;
            job.StartedAt = DateTimeOffset.UtcNow;
            job.CompletedAt = null;
            job.TranscriptCharacters = 0;
            job.TranslationInputCharacters = 0;
            job.TranslationOutputCharacters = 0;
            job.SummaryInputCharacters = 0;
            job.SummaryOutputCharacters = 0;
            var output = OutputPathsFor(job);
            Directory.CreateDirectory(output.Directory);
            ApplyExistingOutputs(job, markComplete: false);

            if (job.ReuseExistingTranscript)
            {
                var existingCues = await TryReadExistingVttAsync(job.OriginalVttPath, cancellationToken);
                if (existingCues is not null)
                {
                    job.Report(JobState.Preparing, 20, "Using existing original captions");
                    job.TranscriptCharacters = existingCues.Sum(cue => (long)cue.Text.Length);
                    await CompleteRemainingTasksAsync(job, existingCues, credentials, cancellationToken);
                    return;
                }

                job.ReuseExistingTranscript = false;
                job.OriginalVttPath = null;
            }

            job.Report(JobState.Preparing, 4, "Inspecting media and preparing a Zoom-compatible audio stream");
            await SaveAsync();
            var extractionProgress = new Progress<double>(value =>
                job.Progress = 4 + (int)Math.Round(value * 16));
            var (parts, documents) = await TranscribeWithAdaptivePartsAsync(
                job, work, settings, credentials, extractionProgress, cancellationToken);

            var originalCues = documents.SelectMany((document, index) =>
                    document.Cues.Select(cue => cue.OffsetBy(parts[index].TimelineStart)))
                .OrderBy(x => x.Start)
                .Select((cue, index) => cue with { Index = index + 1 })
                .ToArray();
            var original = new TranscriptDocument(job.SourceLanguage, originalCues,
                string.Join("\n", originalCues.Select(x => x.Text)));
            job.TranscriptCharacters = original.Text.Length;
            job.OriginalVttPath = output.OriginalVtt;
            job.TranscriptJsonPath = output.TranscriptJson;
            await File.WriteAllTextAsync(job.OriginalVttPath, WebVtt.Write(originalCues), cancellationToken);
            await File.WriteAllTextAsync(job.TranscriptJsonPath,
                JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
            await CompleteRemainingTasksAsync(job, originalCues, credentials, cancellationToken);
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
            var cleaned = await WorkFileCleaner.DeleteDirectoryAsync(work);
            if (!cleaned)
            {
                const string cleanupError = "Temporary audio cleanup failed. Close applications using the files and restart Z Transcribe to retry cleanup.";
                job.Error = string.IsNullOrWhiteSpace(job.Error) ? cleanupError : $"{job.Error}{Environment.NewLine}{cleanupError}";
            }
            await SaveAsync();
        }
    }

    private async Task<(IReadOnlyList<PreparedAudioPart> Parts, TranscriptDocument[] Documents)> TranscribeWithAdaptivePartsAsync(
        QueueJob job,
        string work,
        UserSettings settings,
        ApiCredentials credentials,
        IProgress<double> extractionProgress,
        CancellationToken cancellationToken)
    {
        long? uploadTargetBytes = null;
        TimeSpan? maximumSegmentDuration = null;
        while (true)
        {
            var parts = await audioExtractor.ExtractAsync(
                job, work, settings, extractionProgress, cancellationToken,
                uploadTargetBytes, maximumSegmentDuration);
            job.Report(JobState.Transcribing, 22,
                $"Transcribing {parts.Count} audio part{(parts.Count == 1 ? "" : "s")}");
            await SaveAsync();
            var documents = new TranscriptDocument[parts.Count];
            try
            {
                using var gate = new SemaphoreSlim(Math.Clamp(settings.ScribeConcurrency, 1, 4));
                var completed = 0;
                await Task.WhenAll(parts.Select(async part =>
                {
                    await gate.WaitAsync(cancellationToken);
                    try
                    {
                        documents[part.Index] = await zoom.TranscribeAsync(
                            part, job.SourceLanguage, credentials, cancellationToken);
                        var done = Interlocked.Increment(ref completed);
                        job.Progress = 22 + (int)Math.Round(done / (double)parts.Count * 53);
                        job.StatusMessage = $"Transcribed {done} of {parts.Count} parts";
                    }
                    finally
                    {
                        gate.Release();
                        await WorkFileCleaner.DeleteFileAsync(part.Path);
                    }
                }));
                return (parts, documents);
            }
            catch (ZoomApiException exception) when (exception.StatusCode == 413)
            {
                var currentTarget = uploadTargetBytes ?? FfmpegAudioExtractor.UploadPartTargetBytes;
                var smallerTarget = currentTarget / 2;
                if (smallerTarget < 10_000_000L)
                    throw;

                uploadTargetBytes = smallerTarget;
                job.Report(JobState.Preparing, 20,
                    $"Zoom rejected the audio size; retrying with {smallerTarget / 1_000_000:N0} MB parts");
                await SaveAsync();
            }
            catch (ZoomApiException exception) when (exception.StatusCode == 503)
            {
                var currentDuration = maximumSegmentDuration ??
                    TimeSpan.FromMinutes(Math.Clamp(settings.SegmentMinutes, 1, 30));
                var smallerDuration = SmallerSegmentDuration(currentDuration);
                if (smallerDuration is null)
                    throw;

                maximumSegmentDuration = smallerDuration.Value;
                job.Report(JobState.Preparing, 20,
                    $"Zoom could not process an audio segment; retrying with {smallerDuration.Value.TotalMinutes:0.##}-minute parts");
                await SaveAsync();
            }
        }
    }

    internal static TimeSpan? SmallerSegmentDuration(TimeSpan currentDuration)
    {
        var smaller = TimeSpan.FromSeconds(Math.Floor(currentDuration.TotalSeconds / 2));
        return smaller < TimeSpan.FromMinutes(1) ? null : smaller;
    }

    private async Task CompleteRemainingTasksAsync(
        QueueJob job,
        IReadOnlyList<TranscriptCue> originalCues,
        ApiCredentials credentials,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<TranscriptCue> summaryCues = originalCues;
        var summaryLanguage = job.SourceLanguage;
        if (!string.IsNullOrWhiteSpace(job.TranslationLanguage) && job.TranslationLanguage != job.SourceLanguage)
        {
            var translated = job.ReuseExistingTranslation
                ? await TryReadExistingVttAsync(job.TranslatedVttPath, cancellationToken)
                : null;
            if (translated is not null)
            {
                job.Report(JobState.Translating, 90, "Using existing translated captions");
            }
            else
            {
                job.ReuseExistingTranslation = false;
                translated = originalCues;
                var route = TranslationRoute.Build(job.SourceLanguage, job.TranslationLanguage);
                for (var step = 0; step < route.Count; step++)
                {
                    var (source, target) = route[step];
                    job.Report(JobState.Translating, 78 + step * 8,
                        route.Count == 1
                            ? $"Translating to {LanguageCatalog.NameFor(target)}"
                            : $"Translation step {step + 1} of {route.Count}: {LanguageCatalog.NameFor(source)} to {LanguageCatalog.NameFor(target)}");
                    var result = await zoom.TranslateCuesAsync(
                        translated, source, target, credentials, cancellationToken);
                    translated = result.Cues;
                    job.TranslationInputCharacters += result.InputCharacters;
                    job.TranslationOutputCharacters += result.OutputCharacters;
                    await SaveAsync();
                }
                job.TranslatedVttPath = OutputPathsFor(job).TranslatedVtt(job.TranslationLanguage);
                await File.WriteAllTextAsync(
                    job.TranslatedVttPath, WebVtt.Write(translated), cancellationToken);
            }
            summaryCues = translated;
            summaryLanguage = job.TranslationLanguage;
        }

        if (job.Summarize)
        {
            if (job.ReuseExistingSummary && NonEmptyFile(job.SummaryPath!))
                job.Report(JobState.Summarizing, 96, "Using existing summary");
            else
            {
                job.ReuseExistingSummary = false;
                await GenerateSummaryAsync(
                    job, summaryCues, summaryLanguage, credentials, cancellationToken);
            }
        }

        job.CompletedAt = DateTimeOffset.UtcNow;
        ReportReady(job);
    }

    private static void ReportReady(QueueJob job)
    {
        var reused = new List<string>();
        if (job.ReuseExistingTranscript) reused.Add("captions");
        if (job.ReuseExistingTranslation) reused.Add("translation");
        if (job.ReuseExistingSummary) reused.Add("summary");
        job.Report(JobState.Ready, 100, reused.Count > 0
            ? $"Ready; reused existing {string.Join(", ", reused)}"
            : job.Summarize
                ? "Captions and summary are ready to review"
                : "Transcript is ready to review");
    }

    private async Task GenerateSummaryAsync(
        QueueJob job,
        IReadOnlyList<TranscriptCue> cues,
        string language,
        ApiCredentials credentials,
        CancellationToken cancellationToken)
    {
        job.SummaryInputCharacters = 0;
        job.SummaryOutputCharacters = 0;
        job.Report(JobState.Summarizing, 92, $"Summarizing in {LanguageCatalog.NameFor(language)}");
        await SaveAsync();
        var text = string.Join("\n", cues.Select(cue => cue.Text));
        var result = await zoom.SummarizeAsync(text, language, credentials, cancellationToken);
        job.SummaryInputCharacters = result.InputCharacters;
        job.SummaryOutputCharacters = result.OutputCharacters;
        job.ExistingSummaryIsStale = false;
        job.ReuseExistingSummary = false;
        job.SummaryPath = OutputPathsFor(job).Summary;
        await File.WriteAllTextAsync(job.SummaryPath, result.Text, cancellationToken);
        await SaveAsync();
    }

    internal static JobOutputPaths OutputPathsFor(QueueJob job)
    {
        var directory = Path.GetDirectoryName(job.SourcePath)
            ?? throw new InvalidOperationException("The source file does not have a parent directory.");
        return new JobOutputPaths(directory, Path.GetFileNameWithoutExtension(job.SourcePath));
    }

    internal sealed record JobOutputPaths(string Directory, string Stem)
    {
        public string OriginalVtt => Path.Combine(Directory, $"{Stem}.vtt");
        public string TranscriptJson => Path.Combine(Directory, $"{Stem}.transcript.json");
        public string Summary => Path.Combine(Directory, $"{Stem}.summary.md");
        public string TranslatedVtt(string language) =>
            Path.Combine(Directory, $"{Stem}.translated-{language}.vtt");
    }

    internal static ExistingJobOutputs ExistingOutputsFor(QueueJob job)
    {
        var paths = OutputPathsFor(job);
        var originalVtt = ValidVttFile(paths.OriginalVtt) ? paths.OriginalVtt : null;
        var transcriptJson = NonEmptyFile(paths.TranscriptJson) ? paths.TranscriptJson : null;
        var translatedVtt = !string.IsNullOrWhiteSpace(job.TranslationLanguage) &&
                            job.TranslationLanguage != job.SourceLanguage &&
                            ValidVttFile(paths.TranslatedVtt(job.TranslationLanguage))
            ? paths.TranslatedVtt(job.TranslationLanguage)
            : null;
        var summary = job.Summarize && !job.ExistingSummaryIsStale && NonEmptyFile(paths.Summary)
            ? paths.Summary
            : null;
        return new ExistingJobOutputs(originalVtt, transcriptJson, translatedVtt, summary);
    }

    internal sealed record ExistingJobOutputs(
        string? OriginalVtt,
        string? TranscriptJson,
        string? TranslatedVtt,
        string? Summary)
    {
        public bool HasAny => OriginalVtt is not null || TranscriptJson is not null ||
                              TranslatedVtt is not null || Summary is not null;
    }

    internal static bool ApplyExistingOutputs(QueueJob job, bool markComplete)
    {
        var outputs = ExistingOutputsFor(job);
        if (outputs.Summary is { } summaryPath) NormalizeSummarySidecar(summaryPath);
        job.OriginalVttPath = outputs.OriginalVtt;
        job.TranscriptJsonPath = outputs.TranscriptJson;
        job.TranslatedVttPath = outputs.TranslatedVtt;
        job.SummaryPath = outputs.Summary;
        job.ReuseExistingTranscript = outputs.OriginalVtt is not null;
        job.ReuseExistingTranslation = outputs.TranslatedVtt is not null;
        job.ReuseExistingSummary = outputs.Summary is not null;

        var needsTranslation = !string.IsNullOrWhiteSpace(job.TranslationLanguage) &&
                               job.TranslationLanguage != job.SourceLanguage;
        var allRequestedOutputsExist = outputs.OriginalVtt is not null &&
                                       (!needsTranslation || outputs.TranslatedVtt is not null) &&
                                       (!job.Summarize || outputs.Summary is not null);
        if (markComplete && allRequestedOutputsExist && job.State != JobState.Ready)
        {
            job.Error = null;
            job.CompletedAt ??= DateTimeOffset.UtcNow;
            job.Report(JobState.Ready, 100, "All requested outputs already exist");
        }
        else if (markComplete && outputs.HasAny && job.State == JobState.Queued)
        {
            job.StatusMessage = "Existing outputs found; only missing tasks will run";
        }
        return outputs.HasAny;
    }

    private static bool NonEmptyFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static void NormalizeSummarySidecar(string path)
    {
        try
        {
            var original = File.ReadAllText(path);
            var normalized = ZoomAiClient.NormalizeSummaryText(original);
            if (!string.Equals(original, normalized, StringComparison.Ordinal))
                File.WriteAllText(path, normalized);
        }
        catch (IOException)
        {
            // A locked sidecar can still be reused; cleanup will be retried next launch.
        }
        catch (UnauthorizedAccessException)
        {
            // A protected sidecar can still be reused; cleanup will be retried next launch.
        }
    }

    private static bool ValidVttFile(string path)
    {
        if (!NonEmptyFile(path)) return false;
        try
        {
            return WebVtt.Parse(File.ReadAllText(path)).Count > 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<IReadOnlyList<TranscriptCue>?> TryReadExistingVttAsync(
        string? path,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !NonEmptyFile(path)) return null;
        try
        {
            var cues = WebVtt.Parse(await File.ReadAllTextAsync(path, cancellationToken));
            return cues.Count > 0 ? cues : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<int> ProbeDurationsAsync(
        IReadOnlyCollection<QueueJob> jobs,
        UserSettings settings,
        CancellationToken cancellationToken)
    {
        var updated = 0;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(jobs.Select(async job =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var probe = await audioExtractor.ProbeAsync(job.SourcePath, settings, cancellationToken);
                job.DurationSeconds = probe.Duration.TotalSeconds;
                job.HasAudio = !string.IsNullOrWhiteSpace(probe.AudioCodec);
                job.MediaProbeError = null;
                Interlocked.Increment(ref updated);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                job.MediaProbeError = exception.Message;
                Interlocked.Increment(ref updated);
            }
            finally { gate.Release(); }
        }));
        return updated;
    }

    private Task SaveAsync() => queueStore.SaveAsync(Jobs);
}

