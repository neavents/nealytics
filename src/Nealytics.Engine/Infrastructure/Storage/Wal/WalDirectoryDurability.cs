namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal static class WalDirectoryDurability
{
    private static readonly IWalDirectoryOperations Operations = new PosixWalDirectoryOperations();

    public static void Synchronize(string path) => Synchronize(path, Operations);

    internal static void Synchronize(string path, IWalDirectoryOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        int flags = OpenFlags(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);
        WalDirectoryOpen opened;
        do
        {
            opened = operations.Open(path, flags);
        }
        while (opened.Handle is null && opened.Error == 4);
        if (opened.Handle is null)
        {
            throw Failure(path, opened.Error);
        }
        using SafeFileHandle handle = opened.Handle;
        WalDirectoryStatus result;
        do
        {
            result = operations.Synchronize(handle);
        }
        while (result.Value < 0 && result.Error == 4);
        if (result.Value < 0)
        {
            throw Failure(path, result.Error);
        }
    }

    public static void AcquireLease(SafeFileHandle handle) => AcquireLease(handle, Operations);

    internal static void AcquireLease(SafeFileHandle handle, IWalDirectoryOperations operations)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(operations);
        OpenFlags(OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.ProcessArchitecture);
        WalDirectoryStatus result;
        do
        {
            result = operations.Lock(handle, 6);
        }
        while (result.Value < 0 && result.Error == 4);
        if (result.Value < 0)
        {
            throw new IOException("Could not acquire exclusive WAL directory ownership.", new Win32Exception(result.Error));
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

    private static IOException Failure(string path, int error) =>
        new IOException($"Could not synchronize WAL directory '{path}'.", new Win32Exception(error));
}
