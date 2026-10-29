namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static partial class WalDirectoryDurability
{
    public static void Synchronize(string path)
    {
        int flags = OpenFlags(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);
        int descriptor;
        do
        {
            descriptor = Open(path, flags);
        }
        while (descriptor < 0 && Marshal.GetLastPInvokeError() == 4);
        if (descriptor < 0)
        {
            throw Failure(path);
        }
        using SafeFileHandle handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        int result;
        do
        {
            result = Synchronize(handle);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        if (result < 0)
        {
            throw Failure(path);
        }
    }

    public static void AcquireLease(SafeFileHandle handle)
    {
        OpenFlags(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);
        int result;
        do
        {
            result = Lock(handle, 6);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);
        if (result < 0)
        {
            throw new IOException("Could not acquire exclusive WAL directory ownership.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    internal static int OpenFlags(bool linux, bool macOS, Architecture architecture)
    {
        if (linux)
        {
            return architecture switch
            {
                Architecture.X64 => 0x0008_0000 | 0x0001_0000,
                Architecture.Arm64 => 0x0008_0000 | 0x0000_4000,
                _ => throw new PlatformNotSupportedException("WAL directory durability requires a verified host architecture.")
            };
        }
        if (macOS)
        {
            return 0x0100_0000 | 0x0010_0000;
        }
        throw new PlatformNotSupportedException("WAL directory durability requires a verified directory synchronization backend.");
    }

    private static IOException Failure(string path) =>
        new IOException($"Could not synchronize WAL directory '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Synchronize(SafeFileHandle handle);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static partial int Lock(SafeFileHandle handle, int operation);
}
