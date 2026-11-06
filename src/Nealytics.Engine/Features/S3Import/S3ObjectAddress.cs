namespace Nealytics.Engine.Features.S3Import;

using System;
using System.Globalization;
using Nealytics.Engine.Infrastructure.Networking;

internal static class S3ObjectAddress
{
    public static Uri Create(S3Source source, string key, string? version)
    {
        string host = source.Endpoint.HostNameType == UriHostNameType.IPv6 ? source.Endpoint.Host : source.Endpoint.IdnHost;
        string authority = (source.PathStyle ? "" : source.Bucket + ".") + host
            + ":" + source.Endpoint.Port.ToString(CultureInfo.InvariantCulture);
        string path = "/" + (source.PathStyle ? source.Bucket + "/" : "") + Encode(key, true);
        string query = version is null ? "" : "?versionId=" + Encode(version, false);
        return new Uri(source.Endpoint.Scheme + "://" + authority + path + query,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }

    internal static string Encode(string value, bool preserveSlash) => ObjectPathEncoding.Encode(value, preserveSlash);
}
