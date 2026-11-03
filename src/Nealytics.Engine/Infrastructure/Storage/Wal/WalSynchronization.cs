namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Threading;

internal sealed class WalSynchronization : IDisposable
{
    public SemaphoreSlim File { get; } = new SemaphoreSlim(1, 1);
    public SemaphoreSlim Serialization { get; } = new SemaphoreSlim(1, 1);

    public void Dispose()
    {
        File.Dispose();
        Serialization.Dispose();
    }
}
