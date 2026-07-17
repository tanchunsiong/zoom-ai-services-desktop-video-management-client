using ZTranscribe.Core.Models;

namespace ZTranscribe.Core.Services;

public sealed record CostBreakdown(
    decimal? ScribeUsd,
    decimal? TranslateUsd,
    decimal? SummarizeUsd)
{
    public decimal? TotalUsd =>
        ScribeUsd is not null && TranslateUsd is not null && SummarizeUsd is not null
            ? ScribeUsd.Value + TranslateUsd.Value + SummarizeUsd.Value
            : null;
}

public sealed record JobCostComparison(
    CostBreakdown Estimate,
    CostBreakdown Actual,
    long EstimatedTranslationCharacters,
    long ActualTranslationCharacters,
    long EstimatedSummaryCharacters,
    long ActualSummaryCharacters);

public static class JobCostEstimator
{
    public static JobCostComparison Compare(QueueJob job, UserSettings settings)
    {
        var estimatedScribe = ScribeCost(job, settings);
        var actualScribe = job.CompletedAt is not null ? estimatedScribe : null;

        var hasTranslation = !string.IsNullOrWhiteSpace(job.TranslationLanguage) &&
            job.TranslationLanguage != job.SourceLanguage;
        var estimatedTranslationCharacters = hasTranslation
            ? EstimateTranslationCharacters(job, settings)
            : 0;
        var actualTranslationCharacters = hasTranslation
            ? job.TranslationInputCharacters + job.TranslationOutputCharacters
            : 0;
        var estimatedTranslate = hasTranslation
            ? CharacterCost(estimatedTranslationCharacters, settings.TranslatorUsdPerMillionCharacters)
            : 0m;
        var actualTranslate = !hasTranslation
            ? 0m
            : job.CompletedAt is not null && actualTranslationCharacters > 0
                ? CharacterCost(actualTranslationCharacters, settings.TranslatorUsdPerMillionCharacters)
                : null;

        var estimatedSummaryCharacters = job.Summarize
            ? EstimateSummaryCharacters(job, settings)
            : 0;
        var actualSummaryCharacters = job.Summarize
            ? job.SummaryInputCharacters + job.SummaryOutputCharacters
            : 0;
        var estimatedSummarize = job.Summarize
            ? CharacterCost(estimatedSummaryCharacters, settings.SummarizerUsdPerMillionCharacters)
            : 0m;
        var actualSummarize = !job.Summarize
            ? 0m
            : job.CompletedAt is not null && actualSummaryCharacters > 0
                ? CharacterCost(actualSummaryCharacters, settings.SummarizerUsdPerMillionCharacters)
                : null;

        return new JobCostComparison(
            new CostBreakdown(estimatedScribe, estimatedTranslate, estimatedSummarize),
            new CostBreakdown(actualScribe, actualTranslate, actualSummarize),
            estimatedTranslationCharacters,
            actualTranslationCharacters,
            estimatedSummaryCharacters,
            actualSummaryCharacters);
    }

    public static string FormatUsd(decimal? value)
    {
        if (value is null) return "--";
        if (value is > 0 and < 0.01m) return $"${value:0.0000}";
        return $"${value:0.00}";
    }

    private static decimal? ScribeCost(QueueJob job, UserSettings settings)
    {
        if (job.HasAudio == false) return 0m;
        if (settings.ScribeUsdPerMinute <= 0 || job.DurationSeconds is null) return null;
        return (decimal)Math.Max(0, job.DurationSeconds.Value) / 60m * settings.ScribeUsdPerMinute;
    }

    private static decimal? CharacterCost(long characters, decimal rate)
    {
        if (rate <= 0 || characters <= 0) return null;
        return characters / 1_000_000m * rate;
    }

    private static long EstimateTranslationCharacters(QueueJob job, UserSettings settings)
    {
        var sourceCharacters = EstimateSourceCharacters(job, settings);
        if (sourceCharacters <= 0) return 0;
        var routeSteps = TranslationRoute.Build(job.SourceLanguage, job.TranslationLanguage).Count;
        return checked(sourceCharacters * 2L * routeSteps);
    }

    private static long EstimateSummaryCharacters(QueueJob job, UserSettings settings)
    {
        var inputCharacters = EstimateSourceCharacters(job, settings);
        return inputCharacters <= 0 ? 0 : checked(inputCharacters + (long)Math.Ceiling(inputCharacters * 0.1m));
    }

    private static long EstimateSourceCharacters(QueueJob job, UserSettings settings)
    {
        if (job.HasAudio == false) return 0;
        if (job.TranscriptCharacters > 0) return job.TranscriptCharacters;
        if (job.DurationSeconds is not { } duration) return 0;
        var charactersPerMinute = settings.EstimatedTranslationCharactersPerMinute > 0
            ? Math.Clamp(settings.EstimatedTranslationCharactersPerMinute, 100, 10_000)
            : DefaultCharactersPerMinute(job.SourceLanguage);
        return (long)Math.Ceiling(Math.Max(0, duration) / 60d * charactersPerMinute);
    }

    private static int DefaultCharactersPerMinute(string language) => language switch
    {
        "zh-CN" => 300,
        "ja-JP" => 300,
        "es-ES" => 900,
        "it-IT" => 850,
        _ => 800
    };
}
