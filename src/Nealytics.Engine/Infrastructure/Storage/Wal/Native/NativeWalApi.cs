namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;

internal sealed class NativeWalApi : INativeWalApi
{
    public static NativeWalApi Instance { get; } = new NativeWalApi();

    private NativeWalApi()
    {
    }

    public uint AbiVersion() => NativeWalMethods.AbiVersion();

    public int Create(int file, uint entries, nuint capacity, out nint context) =>
        NativeWalMethods.Create(file, entries, capacity, out context);

    public unsafe int Append(NativeWalHandle context, ReadOnlyMemory<byte> record)
    {
        fixed (byte* data = record.Span)
        {
            return NativeWalMethods.Append(context, data, (nuint)record.Length);
        }
    }

    public int Flush(NativeWalHandle context) => NativeWalMethods.Flush(context);

    public int Truncate(NativeWalHandle context) => NativeWalMethods.Truncate(context);

    public int RegisteredFile(NativeWalHandle context) => NativeWalMethods.RegisteredFile(context);

    public int Destroy(nint context) => NativeWalMethods.Destroy(context);
}
