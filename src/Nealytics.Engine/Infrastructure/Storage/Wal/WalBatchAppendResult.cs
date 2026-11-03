namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System.Runtime.ExceptionServices;

internal readonly record struct WalBatchAppendResult(int Count, ExceptionDispatchInfo? Failure)
{
    public void ThrowIfFailed() => Failure?.Throw();
}
