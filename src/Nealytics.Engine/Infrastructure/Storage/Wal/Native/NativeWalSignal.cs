namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System.Threading;

internal sealed class NativeWalSignal : INativeWalSignal
{
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);

    public bool Wait(int millisecondsTimeout) => _signal.WaitOne(millisecondsTimeout);

    public void Pulse() => _signal.Set();

    public void Dispose() => _signal.Dispose();
}
