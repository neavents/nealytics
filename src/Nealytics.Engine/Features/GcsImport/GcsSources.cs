namespace Nealytics.Engine.Features.GcsImport;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed class GcsSources : IDisposable
{
    private static readonly Encoding StrictUtf8 = Encoding.GetEncoding(Encoding.UTF8.CodePage,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    private readonly FrozenDictionary<string, GcsSource> _sources;

    public GcsSources(TelemetryEngineOptions options, ProjectCatalog projects) : this(options, projects, DecodeKey)
    {
    }

    internal GcsSources(TelemetryEngineOptions options, ProjectCatalog projects, Func<string?, byte[]> decodeKey)
    {
        if (options.GcsSources is null || options.GcsSources.Count > 32
            || (options.GcsSources.Count != 0 && (!options.EnableNdjsonImport || !options.EnableImportJobs)))
        {
            throw new InvalidOperationException("GCS accepts at most 32 sources and requires NDJSON import jobs.");
        }
        Dictionary<string, GcsSource> sources = new(StringComparer.Ordinal);
        try
        {
            foreach (GcsSourceOptions declaration in options.GcsSources)
            {
                if (declaration is null || !Token(declaration.Name, 64) || sources.ContainsKey(declaration.Name)
                    || string.IsNullOrWhiteSpace(declaration.ProjectId) || declaration.ProjectId.Length > 256
                    || projects.Resolve(declaration.ProjectId) is null
                    || declaration.Endpoint is null || declaration.Endpoint.Length > 4_096
                    || !Uri.TryCreate(declaration.Endpoint, UriKind.Absolute, out Uri? endpoint)
                    || endpoint.AbsoluteUri.Length > 4_096
                    || (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && declaration.AllowInsecureHttp))
                    || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0
                    || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || !CanonicalHost(endpoint)
                    || !Bucket(declaration.Bucket) || !Utf8(declaration.Prefix)
                    || !Token(declaration.AccessKey, 128) || !Secret(declaration.SecretKey)
                    || declaration.MaxObjectBytes < 1 || declaration.RequestTimeoutSeconds is < 1 or > 120)
                {
                    throw Invalid();
                }
                sources.Add(declaration.Name, new(declaration, endpoint, decodeKey(declaration.IdentityKey)));
            }
            _sources = sources.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch
        {
            foreach (GcsSource source in sources.Values)
            {
                source.Dispose();
            }
            throw;
        }
    }

    public int Count => _sources.Count;

    public GcsSource? Find(string name, string project) =>
        _sources.TryGetValue(name, out GcsSource? source) && source.ProjectId == project ? source : null;

    internal static bool Key(GcsSource source, string? key) => key is not null && key.Length != 0 && Utf8(key)
        && key is not "." and not ".." && !key.StartsWith(".well-known/acme-challenge/", StringComparison.Ordinal)
        && key.StartsWith(source.Prefix, StringComparison.Ordinal);

    private static bool CanonicalHost(Uri endpoint)
    {
        try
        {
            _ = endpoint.IdnHost;
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static bool Token(string? value, int limit) => value is not null && value.Length is > 0
        && value.Length <= limit && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool Secret(string? value) => value is not null && value.Length is > 0 and <= 256
        && value.All(character => character is >= '!' and <= '~');

    private static bool Bucket(string? value)
    {
        if (value is null || value.Length is < 3 or > 222 || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1])
            || !value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.'))
        {
            return false;
        }
        string[] components = value.Split('.');
        return components.All(part => part.Length is > 0 and <= 63)
            && !(components.Length == 4 && components.All(part => byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)));
    }

    private static bool Utf8(string? value)
    {
        try
        {
            return value is not null && !value.Contains('\r') && !value.Contains('\n') && StrictUtf8.GetByteCount(value) <= 1_024;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static byte[] DecodeKey(string? encoded) => DecodeOwnedKey(encoded, new byte[32]);

    internal static byte[] DecodeOwnedKey(string? encoded, byte[] key)
    {
        if (encoded is null || encoded.Length != 44 || !Convert.TryFromBase64String(encoded, key, out int bytes) || bytes != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw Invalid();
        }
        return key;
    }

    private static InvalidOperationException Invalid() => new("GCS source settings are invalid or conflict with a declared source.");

    public void Dispose()
    {
        foreach (GcsSource source in _sources.Values)
        {
            source.Dispose();
        }
    }
}
