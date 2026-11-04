namespace Nealytics.Engine.Features.S3Import;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed record S3Source(
    string Name, string ProjectId, Uri Endpoint, string Bucket, string Region, string Prefix,
    string AccessKey, string SecretKey, string SessionToken, bool PathStyle, long MaxObjectBytes,
    TimeSpan Timeout);

internal sealed class S3Sources
{
    private static readonly Encoding StrictUtf8 = Encoding.GetEncoding(Encoding.UTF8.CodePage,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    private readonly FrozenDictionary<string, S3Source> _sources;

    public S3Sources(TelemetryEngineOptions options, ProjectCatalog projects)
    {
        if (options.S3Sources is null || options.S3Sources.Count > 32
            || (options.S3Sources.Count != 0 && (!options.EnableNdjsonImport || !options.EnableImportJobs)))
        {
            throw new InvalidOperationException("S3 accepts at most 32 sources and requires NDJSON import jobs.");
        }
        Dictionary<string, S3Source> sources = new(StringComparer.Ordinal);
        foreach (S3SourceOptions declaration in options.S3Sources)
        {
            if (declaration is null || !Token(declaration.Name, 64)
                || string.IsNullOrWhiteSpace(declaration.ProjectId) || declaration.ProjectId.Length > 256
                || projects.Resolve(declaration.ProjectId) is null || sources.ContainsKey(declaration.Name)
                || !Uri.TryCreate(declaration.Endpoint, UriKind.Absolute, out Uri? endpoint)
                || (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && declaration.AllowInsecureHttp))
                || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0
                || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
                || !Bucket(declaration.Bucket) || !Token(declaration.Region, 64)
                || !Utf8(declaration.Prefix, 1_024) || !Token(declaration.AccessKey, 128)
                || !Visible(declaration.SecretKey, 256, false) || !Visible(declaration.SessionToken, 4_096, true)
                || (!declaration.PathStyle && endpoint.HostNameType != UriHostNameType.Dns)
                || declaration.MaxObjectBytes < 1 || declaration.RequestTimeoutSeconds is < 1 or > 120)
            {
                throw new InvalidOperationException("S3 source settings are invalid or conflict with a declared source.");
            }
            sources.Add(declaration.Name, new(declaration.Name, declaration.ProjectId, endpoint,
                declaration.Bucket, declaration.Region, declaration.Prefix, declaration.AccessKey,
                declaration.SecretKey, declaration.SessionToken, declaration.PathStyle,
                declaration.MaxObjectBytes, TimeSpan.FromSeconds(declaration.RequestTimeoutSeconds)));
        }
        _sources = sources.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public int Count => _sources.Count;

    public S3Source? Find(string name, string project) =>
        _sources.TryGetValue(name, out S3Source? source) && source.ProjectId == project ? source : null;

    internal static bool Key(S3Source source, string? key) => key is not null && key.Length != 0
        && Utf8(key, 1_024) && key.StartsWith(source.Prefix, StringComparison.Ordinal);

    internal static bool Version(string? version) => version is null
        || (version.Length != 0 && Utf8(version, 2_048));

    private static bool Token(string? value, int limit) => value is not null && value.Length is > 0
        && value.Length <= limit && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool Visible(string? value, int limit, bool empty) => value is not null
        && (empty || value.Length != 0) && value.Length <= limit
        && value.All(character => character is >= '!' and <= '~');

    private static bool Bucket(string? value) => value is not null && value.Length is >= 3 and <= 63
        && char.IsAsciiLetterOrDigit(value[0]) && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')
        && !value.Contains("..", StringComparison.Ordinal);

    private static bool Utf8(string? value, int bytes)
    {
        try
        {
            return value is not null && StrictUtf8.GetByteCount(value) <= bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
