using System.Text.Json;

namespace ZTranscribe.Core.Models;

public sealed record LiveScribeOptions(
    string Language,
    string VocabularyJson = "")
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Language))
            throw new InvalidOperationException("A transcription language is required.");
        _ = ScribeVocabularyJson.Parse(VocabularyJson);
    }
}

public static class ScribeVocabularyJson
{
    public const string Sample = """
        {
          "phrases": [
            "AIAGW",
            "Zoom AI Companion",
            "ServiceNow"
          ],
          "pronunciations": [
            {
              "phrase": "AIAGW",
              "pronunciation": "A I A gateway"
            }
          ],
          "aliases": [
            {
              "canonical": "Zoom AI Companion",
              "variants": [
                "AI Companion",
                "Zoom Companion"
              ]
            }
          ]
        }
        """;

    public static JsonElement? Parse(string? json)
    {
        var normalized = Normalize(json ?? "");
        if (normalized.Length == 0) return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(normalized);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Vocabulary must be valid JSON: {exception.Message}", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Vocabulary JSON must contain an object.");

            var vocabulary = root;
            if (root.TryGetProperty("config", out var config))
            {
                if (config.ValueKind != JsonValueKind.Object ||
                    !config.TryGetProperty("vocabulary", out vocabulary) ||
                    vocabulary.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The config object does not contain a vocabulary object.");
            }
            else if (root.TryGetProperty("vocabulary", out var nestedVocabulary))
            {
                if (nestedVocabulary.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The vocabulary field must contain an object.");
                vocabulary = nestedVocabulary;
            }

            ValidateVocabulary(vocabulary);
            return vocabulary.Clone();
        }
    }

    private static void ValidateVocabulary(JsonElement vocabulary)
    {
        if (vocabulary.TryGetProperty("phrases", out var phrases) &&
            (phrases.ValueKind != JsonValueKind.Array ||
             phrases.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)))
            throw new InvalidOperationException("phrases must be an array of strings.");

        if (vocabulary.TryGetProperty("pronunciations", out var pronunciations) &&
            (pronunciations.ValueKind != JsonValueKind.Array ||
             pronunciations.EnumerateArray().Any(entry =>
                 entry.ValueKind != JsonValueKind.Object ||
                 !HasString(entry, "phrase") ||
                 !HasString(entry, "pronunciation"))))
            throw new InvalidOperationException(
                "pronunciations must contain phrase and pronunciation strings.");

        if (vocabulary.TryGetProperty("aliases", out var aliases) &&
            (aliases.ValueKind != JsonValueKind.Array ||
             aliases.EnumerateArray().Any(entry =>
                 entry.ValueKind != JsonValueKind.Object ||
                 !HasString(entry, "canonical") ||
                 !entry.TryGetProperty("variants", out var variants) ||
                 variants.ValueKind != JsonValueKind.Array ||
                 variants.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))))
            throw new InvalidOperationException(
                "aliases must contain a canonical string and an array of variant strings.");
    }

    private static bool HasString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String;

    private static string Normalize(string json)
    {
        var value = json
            .Replace("\uFEFF", "", StringComparison.Ordinal)
            .Replace('\u00A0', ' ')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal)) return value;

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0) lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim() == "```") lines.RemoveAt(lines.Count - 1);
        return string.Join(Environment.NewLine, lines).Trim();
    }
}

public sealed record LiveScribeEvent(
    string Type,
    string? Transcript = null,
    string? Error = null,
    bool IsDelta = false);
