using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZTranscribe.Infrastructure.Zoom;

internal sealed class DataUriJsonContent : HttpContent
{
    private readonly string _filePath;
    private readonly byte[] _prefix;
    private readonly byte[] _suffix;
    private readonly long _contentLength;

    public DataUriJsonContent(string filePath, string mimeType, object config)
    {
        _filePath = filePath;
        var dataUri = JsonSerializer.Serialize($"data:{mimeType};base64,");
        _prefix = Encoding.UTF8.GetBytes($"{{\"file\":{dataUri[..^1]}");
        _suffix = Encoding.UTF8.GetBytes($"\",\"config\":{JsonSerializer.Serialize(config)}}}");

        var fileLength = new FileInfo(filePath).Length;
        var base64Length = checked(4 * ((fileLength + 2) / 3));
        _contentLength = checked(_prefix.LongLength + base64Length + _suffix.LongLength);
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        Headers.ContentLength = _contentLength;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_prefix, cancellationToken);
        await using (var source = File.OpenRead(_filePath))
        using (var base64 = new CryptoStream(stream, new ToBase64Transform(), CryptoStreamMode.Write, leaveOpen: true))
        {
            await source.CopyToAsync(base64, cancellationToken);
            base64.FlushFinalBlock();
        }
        await stream.WriteAsync(_suffix, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _contentLength;
        return true;
    }
}
