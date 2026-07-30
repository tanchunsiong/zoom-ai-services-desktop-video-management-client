using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ZTranscribe.Core.Models;
using ZTranscribe.Core.Services;

namespace ZTranscribe.Infrastructure.Zoom;

public sealed class ZoomLiveScribeClient : ILiveScribeClient
{
    internal static readonly Uri Endpoint = new("wss://api.zoom.us/v2/aiservices/scribe/live");
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SessionUpdateTimeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] ForcedTurnSilence = new byte[3_200];

    public async Task StreamAsync(
        IAsyncEnumerable<byte[]> pcm16Frames,
        LiveScribeOptions options,
        ApiCredentials credentials,
        IProgress<LiveScribeEvent>? progress,
        CancellationToken cancellationToken)
    {
        options.Validate();
        if (!credentials.IsComplete) throw new InvalidOperationException("Zoom AI Services credentials are incomplete.");

        using var socket = new ClientWebSocket();
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sessionToken = sessionCancellation.Token;
        var sessionReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task? receiveTask = null;
        socket.Options.AddSubProtocol("live-asr");
        socket.Options.SetRequestHeader("Authorization", $"Bearer {ZoomJwt.Create(credentials)}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        try
        {
            await socket.ConnectAsync(Endpoint, sessionToken);
            receiveTask = ReceiveEventsAsync(socket, progress, sessionReady, sessionToken);
            await SendTextAsync(socket, BuildSessionUpdateJson(options), sessionToken);

            var readyTask = sessionReady.Task.WaitAsync(SessionUpdateTimeout, sessionToken);
            var readyResult = await Task.WhenAny(readyTask, receiveTask);
            if (readyResult == receiveTask)
            {
                await receiveTask;
                throw new InvalidOperationException(
                    "The Zoom Live session ended before acknowledging its configuration.");
            }
            await readyTask;

            var sendTask = SendAudioAsync(
                socket,
                pcm16Frames,
                options.ForcedCaptionIntervalMs,
                options.SilenceDurationMs,
                sessionToken);
            var firstCompleted = await Task.WhenAny(sendTask, receiveTask);
            if (firstCompleted == receiveTask)
            {
                await receiveTask;
                throw new InvalidOperationException("The Zoom Live session ended before microphone capture stopped.");
            }

            await sendTask;
            await SendTextAsync(socket, """{"type":"session.close"}""", sessionToken);
            await receiveTask.WaitAsync(GracefulCloseTimeout, sessionToken);
        }
        catch (TimeoutException) when (!sessionReady.Task.IsCompleted)
        {
            throw new InvalidOperationException(
                "Zoom did not acknowledge the Live session configuration within 10 seconds.");
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Zoom did not close the Live session within 15 seconds.");
        }
        finally
        {
            sessionCancellation.Cancel();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Live transcription finished",
                        CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
            if (socket.State is not (WebSocketState.Closed or WebSocketState.None)) socket.Abort();
            if (receiveTask is not null)
            {
                try { await receiveTask; }
                catch (Exception) when (sessionCancellation.IsCancellationRequested) { }
            }
        }
    }

    internal static string BuildSessionUpdateJson(LiveScribeOptions options)
    {
        options.Validate();
        return JsonSerializer.Serialize(new
        {
            type = "session.update",
            input_audio_format = "pcm16",
            language = options.Language,
            turn_detection = new
            {
                threshold = options.VadThreshold,
                prefix_padding_ms = options.PrefixPaddingMs,
                silence_duration_ms = options.SilenceDurationMs,
                min_pause_duration_ms = options.MinPauseDurationMs
            }
        });
    }

    internal static LiveScribeEvent ParseServerEvent(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var type = root.TryGetProperty("type", out var typeValue)
            ? typeValue.GetString() ?? "unknown"
            : "unknown";
        var transcript = FindTranscriptText(root);
        string? error = null;
        if (root.TryGetProperty("error", out var errorValue))
        {
            error = errorValue.ValueKind switch
            {
                JsonValueKind.String => errorValue.GetString(),
                JsonValueKind.Object when errorValue.TryGetProperty("message", out var message) =>
                    message.GetString(),
                _ => errorValue.GetRawText()
            };
        }
        var isDelta = type.Contains("delta", StringComparison.OrdinalIgnoreCase)
                      || root.TryGetProperty("delta", out _);
        return new LiveScribeEvent(type, transcript, error, isDelta);
    }

    private static string? FindTranscriptText(JsonElement element, int depth = 0)
    {
        if (depth > 4) return null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "transcript", "text", "delta" })
            {
                if (element.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                    return value.GetString();
            }

            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                    continue;
                var nested = FindTranscriptText(property.Value, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindTranscriptText(item, depth + 1);
                if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        return null;
    }

    private static async Task SendAudioAsync(
        ClientWebSocket socket,
        IAsyncEnumerable<byte[]> frames,
        int forcedCaptionIntervalMs,
        int silenceDurationMs,
        CancellationToken cancellationToken)
    {
        var lastForcedTurn = Stopwatch.GetTimestamp();
        var forcedSilenceFrames = Math.Max(3, (int)Math.Ceiling((silenceDurationMs + 50) / 100d));
        await foreach (var frame in frames.WithCancellation(cancellationToken))
        {
            if (frame.Length == 0) continue;
            if (forcedCaptionIntervalMs > 0 &&
                Stopwatch.GetElapsedTime(lastForcedTurn) >=
                TimeSpan.FromMilliseconds(forcedCaptionIntervalMs))
            {
                // Buffering continues upstream while these paced 100 ms silent frames
                // create a VAD boundary, so no captured source audio is discarded.
                for (var index = 0; index < forcedSilenceFrames; index++)
                {
                    await socket.SendAsync(
                        ForcedTurnSilence,
                        WebSocketMessageType.Binary,
                        true,
                        cancellationToken);
                    await Task.Delay(100, cancellationToken);
                }
                lastForcedTurn = Stopwatch.GetTimestamp();
            }
            await socket.SendAsync(frame, WebSocketMessageType.Binary, true, cancellationToken);
        }
    }

    private static async Task SendTextAsync(
        ClientWebSocket socket,
        string json,
        CancellationToken cancellationToken) =>
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            true,
            cancellationToken);

    private static async Task ReceiveEventsAsync(
        ClientWebSocket socket,
        IProgress<LiveScribeEvent>? progress,
        TaskCompletionSource sessionReady,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                progress?.Report(new LiveScribeEvent("session.closed"));
                return;
            }

            // The beta quickstart decodes both string and binary messages as UTF-8 JSON.
            if (result.MessageType is not (WebSocketMessageType.Text or WebSocketMessageType.Binary))
            {
                if (result.EndOfMessage) message.SetLength(0);
                continue;
            }

            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (!result.EndOfMessage) continue;

            var serverEvent = ParseServerEvent(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
            progress?.Report(serverEvent);
            if (serverEvent.Type == "session.updated") sessionReady.TrySetResult();
            if (serverEvent.Type == "error")
                throw new InvalidOperationException(serverEvent.Error ?? "Zoom Live returned an error.");
            if (serverEvent.Type == "session.closed") return;
        }
    }
}
