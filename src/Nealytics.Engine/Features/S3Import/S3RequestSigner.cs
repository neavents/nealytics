namespace Nealytics.Engine.Features.S3Import;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

internal static class S3RequestSigner
{
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    public static void Sign(HttpRequestMessage request, S3Source source, DateTimeOffset timestamp, ArrayPool<byte>? pool = null)
    {
        string date = timestamp.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string instant = timestamp.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        Uri address = request.RequestUri!;
        request.Headers.Host = address.Authority;
        request.Headers.Add("x-amz-content-sha256", EmptyHash);
        request.Headers.Add("x-amz-date", instant);
        if (source.SessionToken.Length != 0)
        {
            request.Headers.Add("x-amz-security-token", source.SessionToken);
        }
        SortedDictionary<string, string> headers = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
        {
            headers.Add(header.Key.ToLowerInvariant(), string.Join(',', header.Value));
        }
        string names = string.Join(';', headers.Keys);
        string canonical = request.Method.Method + "\n" + address.AbsolutePath + "\n" + address.Query.TrimStart('?') + "\n"
            + string.Concat(headers.Select(header => header.Key + ":" + header.Value + "\n")) + "\n" + names + "\n" + EmptyHash;
        string scope = date + "/" + source.Region + "/s3/aws4_request";
        string toSign = "AWS4-HMAC-SHA256\n" + instant + "\n" + scope + "\n" + Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        string signature = Signature(source.SecretKey, date, source.Region, toSign, pool ?? ArrayPool<byte>.Shared);
        request.Headers.TryAddWithoutValidation("Authorization", "AWS4-HMAC-SHA256 Credential=" + source.AccessKey + "/"
            + scope + ",SignedHeaders=" + names + ",Signature=" + signature);
    }

    private static string Signature(string secret, string date, string region, string message, ArrayPool<byte> pool)
    {
        int keyBytes = 4 + Encoding.UTF8.GetByteCount(secret);
        byte[] buffer = pool.Rent(keyBytes + 160);
        try
        {
            Span<byte> key = buffer.AsSpan(0, keyBytes);
            "AWS4"u8.CopyTo(key);
            Encoding.UTF8.GetBytes(secret, key[4..]);
            Span<byte> dateKey = buffer.AsSpan(keyBytes, 32);
            Span<byte> regionKey = buffer.AsSpan(keyBytes + 32, 32);
            Span<byte> serviceKey = buffer.AsSpan(keyBytes + 64, 32);
            Span<byte> signingKey = buffer.AsSpan(keyBytes + 96, 32);
            Span<byte> signature = buffer.AsSpan(keyBytes + 128, 32);
            HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(date), dateKey);
            HMACSHA256.HashData(dateKey, Encoding.UTF8.GetBytes(region), regionKey);
            HMACSHA256.HashData(regionKey, "s3"u8, serviceKey);
            HMACSHA256.HashData(serviceKey, "aws4_request"u8, signingKey);
            HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(message), signature);
            return Hex(signature);
        }
        finally
        {
            pool.Return(buffer, clearArray: true);
        }
    }

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}
