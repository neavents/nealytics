namespace Nealytics.Engine.Features.HttpImport;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed class HttpSources : IDisposable
{
    private static readonly FrozenSet<string> ReservedHeaders = new[]
    {
        "host", "range", "accept-encoding", "connection", "keep-alive", "upgrade", "trailer",
        "transfer-encoding", "te", "expect"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private readonly FrozenDictionary<string, HttpSource> _sources;

    public HttpSources(TelemetryEngineOptions options, ProjectCatalog projects)
        : this(options, projects, Key)
    {
    }

    internal HttpSources(TelemetryEngineOptions options, ProjectCatalog projects, Func<string?, byte[]> decodeKey)
    {
        if (options.HttpSources is null || options.HttpSources.Count > 32
            || (options.HttpSources.Count != 0 && (!options.EnableNdjsonImport || !options.EnableImportJobs)))
        {
            throw new InvalidOperationException("HTTP accepts at most 32 sources and requires NDJSON import jobs.");
        }
        Dictionary<string, HttpSource> sources = new(StringComparer.Ordinal);
        try
        {
            foreach (HttpSourceOptions declaration in options.HttpSources)
            {
                if (declaration is null || !Name(declaration.Name) || sources.ContainsKey(declaration.Name)
                    || string.IsNullOrWhiteSpace(declaration.ProjectId) || declaration.ProjectId.Length > 256
                    || projects.Resolve(declaration.ProjectId) is null
                    || declaration.Url is null || declaration.Url.Length > 16_384
                    || !Uri.TryCreate(declaration.Url, UriKind.Absolute, out Uri? url) || url.AbsoluteUri.Length > 16_384
                    || (url.Scheme != "https" && !(url.Scheme == "http" && declaration.AllowInsecureHttp))
                    || url.UserInfo.Length != 0 || url.Fragment.Length != 0
                    || declaration.MaxObjectBytes < 1 || declaration.RequestTimeoutSeconds is < 1 or > 120)
                {
                    throw Invalid();
                }
                FrozenDictionary<string, string> headers = Headers(declaration.Headers);
                byte[] key = decodeKey(declaration.IdentityKey);
                sources.Add(declaration.Name, new(declaration.Name, declaration.ProjectId, url, headers,
                    key, declaration.MaxObjectBytes, TimeSpan.FromSeconds(declaration.RequestTimeoutSeconds)));
            }
            _sources = sources.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch
        {
            foreach (HttpSource source in sources.Values)
            {
                source.Dispose();
            }
            throw;
        }
    }

    public int Count => _sources.Count;

    public HttpSource? Find(string name, string project) =>
        _sources.TryGetValue(name, out HttpSource? source) && source.ProjectId == project ? source : null;

    private static bool Name(string? value) => value is not null && value.Length is > 0 and <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static FrozenDictionary<string, string> Headers(Dictionary<string, string>? declarations)
    {
        if (declarations is null || declarations.Count > 32)
        {
            throw Invalid();
        }
        Dictionary<string, string> headers = new(StringComparer.Ordinal);
        int bytes = 0;
        foreach (KeyValuePair<string, string> header in declarations)
        {
            string name = header.Key.ToLowerInvariant();
            string value = header.Value;
            if (name.Length is < 1 or > 128 || !header.Key.All(character => char.IsAsciiLetterOrDigit(character)
                    || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~')
                || ReservedHeaders.Contains(name) || name.StartsWith("if-", StringComparison.Ordinal)
                || name.StartsWith("content-", StringComparison.Ordinal) || name.StartsWith("proxy-", StringComparison.Ordinal)
                || value is null || value.Length > 4_096 || !value.All(character => character is >= ' ' and <= '~')
                || !headers.TryAdd(name, value))
            {
                throw Invalid();
            }
            bytes += name.Length + value.Length;
            if (bytes > 16_384)
            {
                throw Invalid();
            }
        }
        return headers.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static byte[] Key(string? encoded) => DecodeOwnedKey(encoded, new byte[32]);

    internal static byte[] DecodeOwnedKey(string? encoded, byte[] key)
    {
        if (encoded is null || encoded.Length != 44 || !Convert.TryFromBase64String(encoded, key, out int bytes) || bytes != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw Invalid();
        }
        return key;
    }

    private static InvalidOperationException Invalid() => new("HTTP source settings are invalid or conflict with a declared source.");

    public void Dispose()
    {
        foreach (HttpSource source in _sources.Values)
        {
            source.Dispose();
        }
    }
}
