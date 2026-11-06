namespace Nealytics.Engine.Infrastructure.Networking;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal static class ObjectSourceTransport
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = false,
        MaxConnectionsPerServer = 1,
        MaxResponseHeadersLength = 16,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
    };

    public static async Task<bool> ReadExactlyAsync(Stream body, WalRecordBuffer buffer, int count, CancellationToken cancellationToken)
    {
        while (buffer.WrittenMemory.Length < count)
        {
            int size = Math.Min(8_192, count - buffer.WrittenMemory.Length);
            int read = await body.ReadAsync(buffer.GetMemory(size)[..size], cancellationToken);
            if (read == 0)
            {
                return false;
            }
            buffer.Advance(read);
        }
        byte[] extra = new byte[1];
        return await body.ReadAsync(extra, cancellationToken) == 0;
    }
}
