namespace Nealytics.Engine.Features.S3Import;

using System;
using System.Buffers;
using System.Net.Http;
using Nealytics.Engine.Infrastructure.Networking;

internal static class S3RequestSigner
{
    public static void Sign(HttpRequestMessage request, S3Source source, DateTimeOffset timestamp, ArrayPool<byte>? pool = null) =>
        AwsV4RequestSigner.Sign(request, source.AccessKey, source.SecretKey, source.Region, source.SessionToken, timestamp, pool);
}
