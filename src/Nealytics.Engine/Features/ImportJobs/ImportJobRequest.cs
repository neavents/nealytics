namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.Globalization;
using Microsoft.AspNetCore.Http;

internal sealed record ImportJobRequest(Guid JobId, Guid ChunkId, string SourceId, long SourceBytes, long Offset)
{
    public static ImportJobRequest? Parse(Guid jobId, Guid chunkId, HttpRequest request)
    {
        string source = request.Headers["X-Import-Source"].ToString();
        if (jobId == Guid.Empty || chunkId == Guid.Empty || !IsDigest(source)
            || !long.TryParse(request.Headers["X-Import-Source-Bytes"].ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out long sourceBytes)
            || !long.TryParse(request.Query["offset"].ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out long offset) || offset > sourceBytes)
        {
            return null;
        }
        return new(jobId, chunkId, source.ToUpperInvariant(), sourceBytes, offset);
    }

    internal static bool IsDigest(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }
}
