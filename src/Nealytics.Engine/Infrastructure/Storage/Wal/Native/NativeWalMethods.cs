namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;
using System.Runtime.InteropServices;

internal static partial class NativeWalMethods
{
    private const string LibraryName = "nealytics_wal";

    [LibraryImport(LibraryName, EntryPoint = "nea_wal_abi_version")]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "nea_wal_create")]
    internal static partial int Create(int file, uint entries, nuint capacity, out nint context);

    [LibraryImport(LibraryName, EntryPoint = "nea_wal_append")]
    internal static unsafe partial int Append(NativeWalHandle context, byte* data, nuint length);

    [LibraryImport(LibraryName, EntryPoint = "nea_wal_flush")]
    internal static partial int Flush(NativeWalHandle context);

    [LibraryImport(LibraryName, EntryPoint = "nea_wal_truncate")]
    internal static partial int Truncate(NativeWalHandle context);

    [LibraryImport(LibraryName, EntryPoint = "nea_wal_registered_file")]
    internal static partial int RegisteredFile(NativeWalHandle context);

    [LibraryImport(LibraryName, EntryPoint = "nea_wal_destroy")]
    internal static partial int Destroy(nint context);
}
