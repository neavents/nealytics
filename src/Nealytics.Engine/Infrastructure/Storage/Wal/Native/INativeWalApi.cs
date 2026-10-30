namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;

internal interface INativeWalApi
{
    uint AbiVersion();

    int Create(int file, uint entries, nuint capacity, out nint context);

    int Append(NativeWalHandle context, ReadOnlyMemory<byte> record);

    int Flush(NativeWalHandle context);

    int Truncate(NativeWalHandle context);

    int RegisteredFile(NativeWalHandle context);

    int Destroy(nint context);
}
