namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using Microsoft.Win32.SafeHandles;

internal readonly record struct WalDirectoryOpen(SafeFileHandle? Handle, int Error);

internal readonly record struct WalDirectoryStatus(int Value, int Error);

internal interface IWalDirectoryOperations
{
    WalDirectoryOpen Open(string path, int flags);
    WalDirectoryStatus Synchronize(SafeFileHandle handle);
    WalDirectoryStatus Lock(SafeFileHandle handle, int operation);
}
