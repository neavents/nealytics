namespace Nealytics.Engine.Features.S3Import;

using System;
using System.Globalization;
using System.Text;

internal static class S3ObjectAddress
{
    private static readonly Encoding StrictUtf8 = Encoding.GetEncoding(Encoding.UTF8.CodePage,
        EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

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

    internal static string Encode(string value, bool preserveSlash)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        StringBuilder result = new(bytes.Length);
        foreach (byte character in bytes)
        {
            if (character is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~'
                || (preserveSlash && character == (byte)'/'))
            {
                result.Append((char)character);
            }
            else
            {
                result.Append('%').Append(character.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return result.ToString();
    }
}
