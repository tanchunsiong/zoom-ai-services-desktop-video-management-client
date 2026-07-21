using ZTranscribe.Core.Models;

namespace ZTranscribe.Core.Services;

public sealed record TimeBreakdown(
    TimeSpan? Scribe,
    TimeSpan? Translate,
    TimeSpan? Summarize)
{
    public TimeSpan? Total => Scribe is not null && Translate is not null && Summarize is not null
        ? Scribe.Value + Translate.Value + Summarize.Value
        : null;
}

public sealed record JobTimeComparison(TimeBreakdown Estimate, TimeBreakdown Actual);

public static class JobTimeEstimator
{
    // These are intentionally conservative service-time heuristics, not SLAs.
    private const double ScribeSecondsPerMediaSecond = 0.30;
    private const double ScribeOverheadSeconds = 12;
    private const double TextSecondsPerThousandCharacters = 0.60;
    private const double TranslationRequestOverheadSeconds = 3;
    private const double SummaryRequestOverheadSeconds = 6;

    public static JobTimeComparison Compare(QueueJob job, UserSettings settings)
    {
        var cost = JobCostEstimator.Compare(job, settings);
        var routeSteps = string.IsNullOrWhiteSpace(job.TranslationLanguage) ||
                         job.TranslationLanguage == job.SourceLanguage
            ? 0
            : TranslationRoute.Build(job.SourceLanguage, job.TranslationLanguage).Count;

        return new JobTimeComparison(
            new TimeBreakdown(
                EstimateScribe(job),
                EstimateTranslation(job, cost.EstimatedTranslationCharacters, routeSteps),
                EstimateSummary(job, cost.EstimatedSummaryCharacters)),
            new TimeBreakdown(
                    job.ReuseExistingTranscript || job.HasAudio == false
                        ? TimeSpan.Zero
                        : ActualStage(job, JobState.Preparing, JobState.Transcribing),
                    routeSteps == 0 || job.ReuseExistingTranslation
                        ? TimeSpan.Zero
                        : ActualStage(job, JobState.Translating),
                    !job.Summarize || job.ReuseExistingSummary
                        ? TimeSpan.Zero
                        : ActualStage(job, JobState.Summarizing)));
    }

    public static string Format(TimeSpan? value)
    {
        if (value is null) return "--";
        var seconds = Math.Max(0, (int)Math.Ceiling(value.Value.TotalSeconds));
        if (seconds < 60) return $"{seconds}s";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}h {time.Minutes:00}m"
            : $"{time.Minutes}m {time.Seconds:00}s";
    }

    private static TimeSpan? EstimateScribe(QueueJob job)
    {
        if (job.ReuseExistingTranscript || job.HasAudio == false) return TimeSpan.Zero;
        return job.DurationSeconds is { } duration && duration > 0
            ? TimeSpan.FromSeconds(ScribeOverheadSeconds + duration * ScribeSecondsPerMediaSecond)
            : null;
    }

    private static TimeSpan? EstimateTranslation(QueueJob job, long characters, int routeSteps)
    {
        if (routeSteps == 0 || job.ReuseExistingTranslation) return TimeSpan.Zero;
        return characters > 0
            ? TimeSpan.FromSeconds(routeSteps * TranslationRequestOverheadSeconds +
                                   characters / 1_000d * TextSecondsPerThousandCharacters)
            : null;
    }

    private static TimeSpan? EstimateSummary(QueueJob job, long characters)
    {
        if (!job.Summarize || job.ReuseExistingSummary) return TimeSpan.Zero;
        return characters > 0
            ? TimeSpan.FromSeconds(SummaryRequestOverheadSeconds +
                                   characters / 1_000d * TextSecondsPerThousandCharacters)
            : null;
    }

    private static TimeSpan? ActualStage(QueueJob job, params JobState[] stages)
    {
        if (job.CompletedAt is null || job.StartedAt is not { } startedAt) return null;
        var stageSet = stages.ToHashSet();
        var events = job.Events
            .Where(item => item.At >= startedAt && item.At <= job.CompletedAt.Value)
            .OrderBy(item => item.At)
            .ToArray();
        if (events.Length == 0) return null;

        var total = TimeSpan.Zero;
        var found = false;
        for (var index = 0; index < events.Length; index++)
        {
            if (!stageSet.Contains(events[index].Stage) ||
                (index > 0 && stageSet.Contains(events[index - 1].Stage))) continue;

            var end = events.Skip(index + 1)
                .FirstOrDefault(item => !stageSet.Contains(item.Stage))?.At ?? job.CompletedAt.Value;
            if (end <= events[index].At) continue;
            total += end - events[index].At;
            found = true;
        }
        return found ? total : null;
    }
}
