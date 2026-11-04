namespace Nealytics.Engine.Features.S3Import;

using System;

internal static class S3ChunkBoundary
{
    public static int Length(ReadOnlySpan<byte> bytes, bool final, int records)
    {
        int lines = 0;
        int boundary = 0;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n')
            {
                continue;
            }
            boundary = index + 1;
            if (++lines == records)
            {
                return boundary;
            }
        }
        return final ? bytes.Length : boundary;
    }
}
