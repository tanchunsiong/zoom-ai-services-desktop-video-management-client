using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

namespace ZTranscribe.Infrastructure.Zoom;

public sealed partial class ZoomAiClient(HttpClient httpClient) : IZoomAiClient
{
    private static readonly Uri ScribeUri = new("https://api.zoom.us/v2/aiservices/scribe/transcribe");
    private static readonly Uri TranslateUri = new("https://api.zoom.us/v2/aiservices/translator/translate");
    private static readonly Uri SummarizeUri = new("https://api.zoom.us/v2/aiservices/summarizer/summarize");
    private const int SummaryChunkBytes = 80 * 1024;
    private static readonly HashSet<HttpStatusCode> Retryable =
    [HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout];

    public async Task<TranscriptDocument> TranscribeAsync(
        PreparedAudioPart part,
        string language,
        ApiCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(async () =>
        {
            var request = Authorized(HttpMethod.Post, ScribeUri, credentials);
            var multipart = new MultipartFormDataContent();
            var stream = File.OpenRead(part.Path);
            var file = new StreamContent(stream);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(part.MimeType);
            multipart.Add(file, "file", Path.GetFileName(part.Path));
            multipart.Add(new StringContent(JsonSerializer.Serialize(new
            {
                language,
                word_time_offsets = true,
                channel_separation = false,
                timestamps = true,
                output_format = "json"
            }), Encoding.UTF8), "config");
            request.Content = multipart;
            return request;
        }, cancellationToken);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, json, "Zoom Scribe");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = root.TryGetProperty("result", out var value) ? value : root;
        var text = FirstString(result, "text_display", "text_lexical", "text");
        var cues = ReadSegments(result, text, part.Duration.TotalSeconds / part.TranscriptTimeScale);
        return new TranscriptDocument(language, cues, text,
            FirstString(root, "request_id"), FirstString(root, "model"));
    }

    public async Task<TranslationResult> TranslateCuesAsync(
        IReadOnlyList<TranscriptCue> cues,
        string sourceLanguage,
        string targetLanguage,
        ApiCredentials credentials,
        CancellationToken cancellationToken)
    {
        var translated = new Dictionary<int, string>();
        long inputCharacters = 0;
        long outputCharacters = 0;
        foreach (var batch in CueBatches(cues, 3600))
        {
            var body = string.Join("\n", batch.Select(x => $"[[[ZT_CUE_{x.Index:000000}]]] {Flatten(x.Text)}"));
            var translation = await TranslateTextAsync(body, sourceLanguage, targetLanguage, credentials, cancellationToken);
            inputCharacters += translation.InputCharacters;
            outputCharacters += translation.OutputCharacters;
            foreach (Match match in CuePattern().Matches(translation.Text))
                translated[int.Parse(match.Groups[1].Value)] = match.Groups[2].Value.Trim();
        }

        // A marker can occasionally be changed by the model. Retry those cues individually.
        foreach (var cue in cues.Where(x => !translated.ContainsKey(x.Index)))
        {
            var translation = await TranslateTextAsync(cue.Text, sourceLanguage, targetLanguage, credentials, cancellationToken);
            inputCharacters += translation.InputCharacters;
            outputCharacters += translation.OutputCharacters;
            translated[cue.Index] = translation.Text;
        }

        return new TranslationResult(
            cues.Select(x => x with { Text = translated[x.Index] }).ToArray(),
            inputCharacters,
            outputCharacters);
    }

    public async Task<SummaryResult> SummarizeAsync(
        string text,
        string language,
        ApiCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new SummaryResult("No spoken content was available to summarize.", 0, 0);

        var chunks = SplitUtf8(text, SummaryChunkBytes);
        if (chunks.Count == 1)
            return await SummarizeTextAsync(chunks[0], language, "full_summary", credentials, cancellationToken);

        long inputCharacters = 0;
        long outputCharacters = 0;
        var summaries = new List<string>();
        foreach (var chunk in chunks)
        {
            var partial = await SummarizeTextAsync(chunk, language, "summary", credentials, cancellationToken);
            inputCharacters += partial.InputCharacters;
            outputCharacters += partial.OutputCharacters;
            summaries.Add(partial.Text);
        }

        var combined = string.Join("\n\n", summaries);
        while (Encoding.UTF8.GetByteCount(combined) > SummaryChunkBytes)
        {
            var reduced = new List<string>();
            foreach (var chunk in SplitUtf8(combined, SummaryChunkBytes))
            {
                var partial = await SummarizeTextAsync(chunk, language, "summary", credentials, cancellationToken);
                inputCharacters += partial.InputCharacters;
                outputCharacters += partial.OutputCharacters;
                reduced.Add(partial.Text);
            }
            var next = string.Join("\n\n", reduced);
            if (next.Length >= combined.Length)
                throw new InvalidOperationException("The transcript is too large for Zoom Summarizer Fast mode.");
            combined = next;
        }

        var final = await SummarizeTextAsync(combined, language, "full_summary", credentials, cancellationToken);
        return final with
        {
            InputCharacters = inputCharacters + final.InputCharacters,
            OutputCharacters = outputCharacters + final.OutputCharacters
        };
    }

    public async Task TestCredentialsAsync(ApiCredentials credentials, CancellationToken cancellationToken)
    {
        // Authentication is validated locally here. A paid AI request is intentionally not made.
        // The first queued job remains the authoritative server-side credential check.
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var token = ZoomJwt.Create(credentials);
        if (token.Count(x => x == '.') != 2) throw new InvalidOperationException("Could not create a Zoom Build token.");
    }

    private async Task<TranslationTextResult> TranslateTextAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        ApiCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = Authorized(HttpMethod.Post, TranslateUri, credentials);
            request.Content = JsonContent.Create(new
            {
                text,
                config = new { source_language = sourceLanguage, target_languages = new[] { targetLanguage } },
                reference_id = $"desktop-{Guid.NewGuid():N}"
            });
            return Task.FromResult(request);
        }, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, json, "Zoom Translator");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var translations = root.GetProperty("result").GetProperty("translations");
        var translatedText = translations.GetProperty(targetLanguage).GetString() ?? "";
        long inputCharacters = text.Length;
        long outputCharacters = translatedText.Length;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputCharacters = ReadInt64(usage, "input_units", inputCharacters);
            outputCharacters = ReadInt64(usage, "output_units", outputCharacters);
        }
        return new TranslationTextResult(translatedText, inputCharacters, outputCharacters);
    }

    private async Task<SummaryResult> SummarizeTextAsync(
        string text,
        string language,
        string task,
        ApiCredentials credentials,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = Authorized(HttpMethod.Post, SummarizeUri, credentials);
            request.Content = JsonContent.Create(new
            {
                input = new { text },
                config = new
                {
                    summary_type = "CONVERSATION",
                    task,
                    language,
                    output_format = "text"
                }
            });
            return Task.FromResult(request);
        }, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, json, "Zoom Summarizer");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var result = root.GetProperty("result");
        var summary = task == "full_summary"
            ? FirstString(result, "full_summary", "summary_text", "recap")
            : FirstString(result, "summary_text", "full_summary", "recap");
        long inputCharacters = text.Length;
        long outputCharacters = summary.Length;
        if (root.TryGetProperty("usage", out var usage))
        {
            inputCharacters = ReadInt64(usage, "input_units", inputCharacters);
            outputCharacters = ReadInt64(usage, "output_units", outputCharacters);
        }
        return new SummaryResult(summary, inputCharacters, outputCharacters,
            FirstString(root, "request_id"), FirstString(root, "model"));
    }

    private static IReadOnlyList<string> SplitUtf8(string text, int maximumBytes)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > maximumBytes && current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
                bytes = 0;
            }
            current.Append(rune.ToString());
            bytes += runeBytes;
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken)
    {
        var delays = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };
        for (var attempt = 0; ; attempt++)
        {
            using var request = await requestFactory();
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], cancellationToken);
                continue;
            }
            if (!Retryable.Contains(response.StatusCode) || attempt >= delays.Length) return response;
            var delay = response.Headers.RetryAfter?.Delta ?? delays[attempt];
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri uri, ApiCredentials credentials)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ZoomJwt.Create(credentials));
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response, string body, string service)
    {
        if (response.IsSuccessStatusCode) return;
        var safeBody = body.Length > 800 ? body[..800] : body;
        throw new ZoomApiException(service, (int)response.StatusCode, safeBody);
    }

    private static IReadOnlyList<TranscriptCue> ReadSegments(JsonElement result, string fallbackText, double fallbackDurationSeconds)
    {
        if (!result.TryGetProperty("segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            return string.IsNullOrWhiteSpace(fallbackText)
                ? []
                : [new TranscriptCue(1, TimeSpan.Zero, TimeSpan.FromSeconds(Math.Max(0.001, fallbackDurationSeconds)), fallbackText)];

        var cues = new List<TranscriptCue>();
        foreach (var segment in segments.EnumerateArray())
        {
            var text = FirstString(segment, "text_display", "text_lexical", "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var start = ReadSeconds(segment, "start", "start_time", "start_sec");
            var end = ReadSeconds(segment, "end", "end_time", "end_sec");
            if (end <= start) end = start + 2;
            cues.Add(new TranscriptCue(cues.Count + 1, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text));
        }
        return cues;
    }

    private static double ReadSeconds(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
            if (double.TryParse(value.GetString(), out number)) return number;
        }
        return 0;
    }

    private static long ReadInt64(JsonElement element, string name, long fallback)
    {
        if (!element.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.GetString(), out number) ? number : fallback;
    }

    private static string FirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        return "";
    }

    private static IEnumerable<IReadOnlyList<TranscriptCue>> CueBatches(IReadOnlyList<TranscriptCue> cues, int maxLength)
    {
        var batch = new List<TranscriptCue>();
        var current = 0;
        foreach (var cue in cues)
        {
            var size = cue.Text.Length + 30;
            if (batch.Count > 0 && current + size > maxLength)
            {
                yield return batch;
                batch = [];
                current = 0;
            }
            batch.Add(cue);
            current += size;
        }
        if (batch.Count > 0) yield return batch;
    }

    private static string Flatten(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"\[\[\[\s*ZT_CUE_(\d{6})\s*\]\]\]\s*([\s\S]*?)(?=\s*\[\[\[\s*ZT_CUE_\d{6}\s*\]\]\]|$)")]
    private static partial Regex CuePattern();

    private sealed record TranslationTextResult(string Text, long InputCharacters, long OutputCharacters);
}

public sealed class ZoomApiException(string service, int statusCode, string responseBody)
    : Exception($"{service} returned HTTP {statusCode}. {responseBody}")
{
    public int StatusCode { get; } = statusCode;
    public string ResponseBody { get; } = responseBody;
}
