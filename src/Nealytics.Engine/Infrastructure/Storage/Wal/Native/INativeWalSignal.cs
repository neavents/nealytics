namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;

internal interface INativeWalSignal : IDisposable
{
    bool Wait(int millisecondsTimeout);
    void Pulse();
}
