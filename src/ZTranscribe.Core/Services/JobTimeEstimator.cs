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

public sealed record TimeRate(double InterceptSeconds, double UnitsPerMinute, int SampleCount)
{
    public double Estimate(double units) => Math.Max(0, InterceptSeconds + Math.Max(0, units) * UnitsPerMinute);
}

public sealed record JobTimeCalibration(
    TimeRate Scribe,
    TimeRate Translate,
    TimeRate Summarize,
    IReadOnlyDictionary<string, TimeRate> ScribeByExtension)
{
    public static JobTimeCalibration Default { get; } = new(
        new TimeRate(12, 18, 0),
        new TimeRate(3, 0.6, 0),
        new TimeRate(6, 0.6, 0),
        new Dictionary<string, TimeRate>(StringComparer.OrdinalIgnoreCase));

    public TimeRate ScribeFor(QueueJob job) =>
        ScribeByExtension.TryGetValue(Path.GetExtension(job.SourcePath), out var rate) ? rate : Scribe;

    public static JobTimeCalibration Learn(IEnumerable<QueueJob> jobs)
    {
        var completed = jobs
            .Where(job => job.CompletedAt is not null)
            .Select(job => new
            {
                Job = job,
                Actual = JobTimeEstimator.ActualFor(job)
            })
            .ToArray();

        var scribeObservations = completed
            .Where(item => item.Job.DurationSeconds is > 0 && item.Actual.Scribe is { TotalSeconds: > 0 })
            .Select(item => new TimeObservation(
                Path.GetExtension(item.Job.SourcePath),
                item.Job.DurationSeconds!.Value / 60d,
                item.Actual.Scribe!.Value.TotalSeconds))
            .ToArray();
        var translationObservations = completed
            .Where(item => item.Job.TranslationInputCharacters + item.Job.TranslationOutputCharacters > 0 &&
                           item.Actual.Translate is { TotalSeconds: > 0 })
            .Select(item => new TimeObservation(
                "",
                (item.Job.TranslationInputCharacters + item.Job.TranslationOutputCharacters) / 1_000d,
                item.Actual.Translate!.Value.TotalSeconds))
            .ToArray();
        var summaryObservations = completed
            .Where(item => item.Job.SummaryInputCharacters > 0 && item.Actual.Summarize is { TotalSeconds: > 0 })
            .Select(item => new TimeObservation(
                "",
                item.Job.SummaryInputCharacters / 1_000d,
                item.Actual.Summarize!.Value.TotalSeconds))
            .ToArray();

        var scribeByExtension = scribeObservations
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() >= 3)
            .ToDictionary(
                group => group.Key,
                group => Fit(group, Default.Scribe),
                StringComparer.OrdinalIgnoreCase);

        return new JobTimeCalibration(
            Fit(scribeObservations, Default.Scribe),
            Fit(translationObservations, Default.Translate),
            Fit(summaryObservations, Default.Summarize),
            scribeByExtension);
    }

    private sealed record TimeObservation(string Key, double Units, double Seconds);

    private static TimeRate Fit(IEnumerable<TimeObservation> observations, TimeRate fallback)
    {
        var values = observations.ToArray();
        if (values.Length == 0) return fallback;
        if (values.Length == 1)
            return new TimeRate(values[0].Seconds, 0, 1);

        var averageUnits = values.Average(value => value.Units);
        var averageSeconds = values.Average(value => value.Seconds);
        var denominator = values.Sum(value => Math.Pow(value.Units - averageUnits, 2));
        var slope = denominator <= double.Epsilon
            ? fallback.UnitsPerMinute
            : values.Sum(value => (value.Units - averageUnits) * (value.Seconds - averageSeconds)) / denominator;
        var intercept = averageSeconds - slope * averageUnits;
        return new TimeRate(
            Math.Clamp(intercept, 0, 60),
            Math.Clamp(slope, 0.01, fallback.UnitsPerMinute * 4),
            values.Length);
    }
}

public static class JobTimeEstimator
{
    // These are intentionally conservative service-time heuristics, not SLAs.
    private const double ScribeSecondsPerMediaSecond = 0.30;
    private const double ScribeOverheadSeconds = 12;
    private const double TextSecondsPerThousandCharacters = 0.60;
    private const double TranslationRequestOverheadSeconds = 3;
    private const double SummaryRequestOverheadSeconds = 6;

    public static JobTimeComparison Compare(
        QueueJob job,
        UserSettings settings,
        JobTimeCalibration? calibration = null)
    {
        calibration ??= JobTimeCalibration.Default;
        var cost = JobCostEstimator.Compare(job, settings);
        var routeSteps = string.IsNullOrWhiteSpace(job.TranslationLanguage) ||
                         job.TranslationLanguage == job.SourceLanguage
            ? 0
            : TranslationRoute.Build(job.SourceLanguage, job.TranslationLanguage).Count;

        return new JobTimeComparison(
                new TimeBreakdown(
                    EstimateScribe(job, calibration),
                    EstimateTranslation(job, cost.EstimatedTranslationCharacters, routeSteps, calibration),
                    EstimateSummary(job, cost.EstimatedSummaryCharacters, calibration)),
            ActualFor(job));
    }

    public static TimeBreakdown ActualFor(QueueJob job)
    {
        var routeSteps = string.IsNullOrWhiteSpace(job.TranslationLanguage) ||
                         job.TranslationLanguage == job.SourceLanguage
            ? 0
            : TranslationRoute.Build(job.SourceLanguage, job.TranslationLanguage).Count;
        return new TimeBreakdown(
            job.ReuseExistingTranscript || job.HasAudio == false
                ? TimeSpan.Zero
                : ActualStage(job, JobState.Preparing, JobState.Transcribing),
            routeSteps == 0 || job.ReuseExistingTranslation
                ? TimeSpan.Zero
                : ActualStage(job, JobState.Translating),
            !job.Summarize || job.ReuseExistingSummary
                ? TimeSpan.Zero
                : ActualStage(job, JobState.Summarizing));
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

    private static TimeSpan? EstimateScribe(QueueJob job, JobTimeCalibration calibration)
    {
        if (job.ReuseExistingTranscript || job.HasAudio == false) return TimeSpan.Zero;
        return job.DurationSeconds is { } duration && duration > 0
            ? TimeSpan.FromSeconds(calibration.ScribeFor(job).SampleCount > 0
                ? calibration.ScribeFor(job).Estimate(duration / 60d)
                : ScribeOverheadSeconds + duration * ScribeSecondsPerMediaSecond)
            : null;
    }

    private static TimeSpan? EstimateTranslation(
        QueueJob job,
        long characters,
        int routeSteps,
        JobTimeCalibration calibration)
    {
        if (routeSteps == 0 || job.ReuseExistingTranslation) return TimeSpan.Zero;
        return characters > 0
            ? TimeSpan.FromSeconds(calibration.Translate.SampleCount > 0
                ? calibration.Translate.Estimate(characters / 1_000d)
                : routeSteps * TranslationRequestOverheadSeconds +
                  characters / 1_000d * TextSecondsPerThousandCharacters)
            : null;
    }

    private static TimeSpan? EstimateSummary(QueueJob job, long characters, JobTimeCalibration calibration)
    {
        if (!job.Summarize || job.ReuseExistingSummary) return TimeSpan.Zero;
        return characters > 0
            ? TimeSpan.FromSeconds(calibration.Summarize.SampleCount > 0
                ? calibration.Summarize.Estimate(characters / 1_000d)
                : SummaryRequestOverheadSeconds +
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
