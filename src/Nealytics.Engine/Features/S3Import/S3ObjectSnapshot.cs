namespace Nealytics.Engine.Features.S3Import;

using System;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record S3ObjectRequest(string Key, string? VersionId);

public sealed record S3ObjectSnapshot(
    string Key, string? VersionId, [property: JsonPropertyName("etag")] string ETag,
    long Bytes, string SourceId)
{
    internal bool IsValid(S3Source source) => S3Sources.Key(source, Key) && S3Sources.Version(VersionId)
        && IsETag(ETag) && Bytes >= 0 && Bytes <= source.MaxObjectBytes
        && SourceId == Identity(source, Key, VersionId, ETag, Bytes);

    internal static bool IsETag(string? value)
    {
        if (value is null || value.Length is < 3 or > 258 || value[0] != '"' || value[^1] != '"')
        {
            return false;
        }
        for (int index = 1; index < value.Length - 1; index++)
        {
            if (value[index] is < '!' or > '~' or '"')
            {
                return false;
            }
        }
        return true;
    }

    internal static string Identity(S3Source source, string key, string? version, string tag, long bytes)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("s3-object-v1");
            writer.WriteStringValue(source.Name);
            writer.WriteStringValue(source.ProjectId);
            writer.WriteStringValue(source.Endpoint.AbsoluteUri);
            writer.WriteStringValue(source.Bucket);
            writer.WriteBooleanValue(source.PathStyle);
            writer.WriteStringValue(key);
            writer.WriteStringValue(version);
            writer.WriteStringValue(tag);
            writer.WriteNumberValue(bytes);
            writer.WriteEndArray();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }
}
