using ZTranscribe.Core.Models;

namespace ZTranscribe.Core.Services;

public sealed record JobCostEstimate(
    decimal? ScribeUsd,
    decimal? TranslateUsd,
    long TranslationBillableCharacters,
    bool UsesActualTranslationUsage)
{
    public decimal? TotalUsd => ScribeUsd is not null && TranslateUsd is not null
        ? ScribeUsd.Value + TranslateUsd.Value
        : null;
}

public static class JobCostEstimator
{
    public static JobCostEstimate Estimate(QueueJob job, UserSettings settings)
    {
        var scribe = EstimateScribe(job, settings);
        if (string.IsNullOrWhiteSpace(job.TranslationLanguage) ||
            job.TranslationLanguage == job.SourceLanguage)
            return new JobCostEstimate(scribe, 0m, 0, false);

        var actualCharacters = job.TranslationInputCharacters + job.TranslationOutputCharacters;
        var usesActualUsage = actualCharacters > 0;
        var billableCharacters = usesActualUsage
            ? actualCharacters
            : EstimateTranslationCharacters(job, settings);
        decimal? translate = settings.TranslatorUsdPerMillionCharacters > 0 && billableCharacters > 0
            ? billableCharacters / 1_000_000m * settings.TranslatorUsdPerMillionCharacters
            : null;

        return new JobCostEstimate(scribe, translate, billableCharacters, usesActualUsage);
    }

    public static string FormatUsd(decimal? value)
    {
        if (value is null) return "--";
        if (value is > 0 and < 0.01m) return $"${value:0.0000}";
        return $"${value:0.00}";
    }

    private static decimal? EstimateScribe(QueueJob job, UserSettings settings)
    {
        if (settings.ScribeUsdPerMinute <= 0 || job.DurationSeconds is null) return null;
        return (decimal)Math.Max(0, job.DurationSeconds.Value) / 60m * settings.ScribeUsdPerMinute;
    }

    private static long EstimateTranslationCharacters(QueueJob job, UserSettings settings)
    {
        var sourceCharacters = job.TranscriptCharacters;
        if (sourceCharacters <= 0 && job.DurationSeconds is { } duration)
            sourceCharacters = (long)Math.Ceiling(
                Math.Max(0, duration) / 60d * Math.Clamp(settings.EstimatedTranslationCharactersPerMinute, 100, 10_000));
        if (sourceCharacters <= 0) return 0;

        var routeSteps = TranslationRoute.Build(job.SourceLanguage, job.TranslationLanguage).Count;
        return checked(sourceCharacters * 2L * routeSteps);
    }
}
