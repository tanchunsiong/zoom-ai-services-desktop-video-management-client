namespace ZTranscribe.Core.Services;

public static class TranslationRoute
{
    public static IReadOnlyList<(string Source, string Target)> Build(string source, string target)
    {
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return [];
        if (source == "en-US" || target == "en-US") return [(source, target)];
        return [(source, "en-US"), ("en-US", target)];
    }
}

