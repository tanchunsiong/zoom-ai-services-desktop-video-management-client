using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZTranscribe.Core.Models;

namespace ZTranscribe.Infrastructure.Zoom;

internal static class ZoomJwt
{
    public static string Create(ApiCredentials credentials, DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.UtcNow;
        var iat = instant.AddSeconds(-30).ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = credentials.ApiKey, iat, exp = iat + 3600 }));
        var data = $"{header}.{payload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(credentials.ApiSecret));
        return $"{data}.{Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(data)))}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

