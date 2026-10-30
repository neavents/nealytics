namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal sealed partial class PosixWalDirectoryOperations : IWalDirectoryOperations
{
    public WalDirectoryOpen Open(string path, int flags)
    {
        int descriptor = OpenNative(path, flags);
        int error = Marshal.GetLastPInvokeError();
        return descriptor < 0
            ? new WalDirectoryOpen(null, error)
            : new WalDirectoryOpen(new SafeFileHandle((IntPtr)descriptor, ownsHandle: true), error);
    }

    public WalDirectoryStatus Synchronize(SafeFileHandle handle)
    {
        int result = SynchronizeNative(handle);
        return new WalDirectoryStatus(result, Marshal.GetLastPInvokeError());
    }

    public WalDirectoryStatus Lock(SafeFileHandle handle, int operation)
    {
        int result = LockNative(handle, operation);
        return new WalDirectoryStatus(result, Marshal.GetLastPInvokeError());
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int SynchronizeNative(SafeFileHandle handle);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int LockNative(SafeFileHandle handle, int operation);
}
