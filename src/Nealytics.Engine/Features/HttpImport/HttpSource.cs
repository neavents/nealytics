namespace Nealytics.Engine.Features.HttpImport;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class HttpSource : IDisposable
{
    private readonly byte[] _key;
    private readonly string _configuration;
    private bool _disposed;

    public HttpSource(string name, string projectId, Uri url, FrozenDictionary<string, string> headers,
        byte[] ownedKey, long maxObjectBytes, TimeSpan timeout)
    {
        Name = name;
        ProjectId = projectId;
        Url = url;
        Headers = headers;
        MaxObjectBytes = maxObjectBytes;
        Timeout = timeout;
        _key = ownedKey;
        try
        {
            using WalRecordBuffer buffer = new(262_144, reserveForEncoding: true);
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartArray();
                writer.WriteStringValue("http-source-v1");
                writer.WriteStringValue(name);
                writer.WriteStringValue(projectId);
                writer.WriteStringValue(url.AbsoluteUri);
                writer.WriteStartObject();
                foreach (KeyValuePair<string, string> header in headers.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    writer.WriteString(header.Key, header.Value);
                }
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            _configuration = Digest(buffer.WrittenMemory.Span);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(_key);
            throw;
        }
    }

    public string Name { get; }
    public string ProjectId { get; }
    public Uri Url { get; }
    public FrozenDictionary<string, string> Headers { get; }
    public long MaxObjectBytes { get; }
    public TimeSpan Timeout { get; }

    public string Identity(string tag, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using WalRecordBuffer buffer = new(8_192, reserveForEncoding: true);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("http-object-v1");
            writer.WriteStringValue(_configuration);
            writer.WriteStringValue(tag);
            writer.WriteNumberValue(bytes);
            writer.WriteEndArray();
        }
        return Digest(buffer.WrittenMemory.Span);
    }

    private string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexString(HMACSHA256.HashData(_key, bytes));

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
        _disposed = true;
    }
}
