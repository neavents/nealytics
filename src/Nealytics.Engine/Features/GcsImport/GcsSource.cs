namespace Nealytics.Engine.Features.GcsImport;

using System;
using System.Security.Cryptography;
using System.Text.Json;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class GcsSource : IDisposable
{
    private readonly byte[] _key;
    private readonly string _configuration;
    private bool _disposed;

    public GcsSource(GcsSourceOptions declaration, Uri endpoint, byte[] ownedKey)
    {
        Name = declaration.Name;
        ProjectId = declaration.ProjectId;
        Endpoint = endpoint;
        Bucket = declaration.Bucket;
        Prefix = declaration.Prefix;
        AccessKey = declaration.AccessKey;
        SecretKey = declaration.SecretKey;
        MaxObjectBytes = declaration.MaxObjectBytes;
        Timeout = TimeSpan.FromSeconds(declaration.RequestTimeoutSeconds);
        _key = ownedKey;
        try
        {
            using WalRecordBuffer buffer = new(32_768);
            using (Utf8JsonWriter writer = new(buffer))
            {
                writer.WriteStartArray();
                writer.WriteStringValue("gcs-source-v1");
                writer.WriteStringValue(Name);
                writer.WriteStringValue(ProjectId);
                writer.WriteStringValue(Endpoint.AbsoluteUri);
                writer.WriteStringValue(Bucket);
                writer.WriteStringValue(Prefix);
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
    public Uri Endpoint { get; }
    public string Bucket { get; }
    public string Prefix { get; }
    public string AccessKey { get; }
    public string SecretKey { get; }
    public long MaxObjectBytes { get; }
    public TimeSpan Timeout { get; }

    public string Identity(string key, long generation, long metageneration, string tag, long bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using WalRecordBuffer buffer = new(16_384, reserveForEncoding: true);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            writer.WriteStringValue("gcs-object-v1");
            writer.WriteStringValue(_configuration);
            writer.WriteStringValue(key);
            writer.WriteNumberValue(generation);
            writer.WriteNumberValue(metageneration);
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
