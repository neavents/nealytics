namespace Nealytics.Engine.Features.HttpImport;

using System;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

public sealed record HttpObjectSnapshot(
    [property: JsonPropertyName("etag")] string ETag, long Bytes, string SourceId)
{
    internal bool IsValid(HttpSource source) => IsETag(ETag) && Bytes >= 0 && Bytes <= source.MaxObjectBytes
        && SourceId is { Length: 64 }
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(source.Identity(ETag, Bytes)),
            Encoding.UTF8.GetBytes(SourceId));

    internal static bool IsETag(string? value) => value is not null && value.Length <= 1_024
        && EntityTagHeaderValue.TryParse(value, out EntityTagHeaderValue? tag) && !tag.IsWeak
        && string.Equals(tag.ToString(), value, StringComparison.Ordinal);
}
