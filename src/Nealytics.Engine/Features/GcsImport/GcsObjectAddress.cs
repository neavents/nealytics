namespace Nealytics.Engine.Features.GcsImport;

using System;
using System.Globalization;
using Nealytics.Engine.Infrastructure.Networking;

internal static class GcsObjectAddress
{
    public static Uri Create(GcsSource source, string key, long? generation)
    {
        string host = source.Endpoint.HostNameType == UriHostNameType.IPv6 ? source.Endpoint.Host : source.Endpoint.IdnHost;
        string authority = host + ":" + source.Endpoint.Port.ToString(CultureInfo.InvariantCulture);
        string path = "/" + source.Bucket + "/" + ObjectPathEncoding.Encode(key, true);
        string query = generation is null ? "" : "?generation=" + generation.Value.ToString(CultureInfo.InvariantCulture);
        return new Uri(source.Endpoint.Scheme + "://" + authority + path + query,
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
    }
}
