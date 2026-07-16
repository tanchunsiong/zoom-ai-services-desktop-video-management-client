namespace ZTranscribe.Core.Models;

public sealed record LanguageOption(string Locale, string Name)
{
    public override string ToString() => Name;
}

public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageOption> Transcription { get; } =
    [
        new("en-US", "English"),
        new("zh-CN", "Chinese (Simplified)"),
        new("ja-JP", "Japanese"),
        new("es-ES", "Spanish"),
        new("it-IT", "Italian")
    ];

    // Zoom Translator requires English on one side. The pipeline automatically
    // bridges supported non-English pairs through English.
    public static IReadOnlyList<LanguageOption> Translation { get; } = Transcription;

    public static string NameFor(string locale) =>
        Transcription.FirstOrDefault(x => x.Locale == locale)?.Name ?? locale;
}

