namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage.Wal.Native;

internal sealed class WalFileFactory
{
    private readonly string _mode;
    private readonly int _entries;
    private readonly Func<string, int, int, NativeWalFile> _createNative;

    public WalFileFactory(
        TelemetryEngineOptions options,
        Func<string, int, int, NativeWalFile>? createNative = null)
    {
        if (options.WalIoBackend is not ("managed" or "io_uring" or "auto"))
        {
            throw new ArgumentException("WalIoBackend must be managed, io_uring or auto.", nameof(options));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WalRingEntries, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.WalRingEntries, 4096);
        _mode = options.WalIoBackend;
        _entries = options.WalRingEntries;
        _createNative = createNative ?? CreateNative;
    }

    public WalFileSelection Create(string path, WalLimits limits)
    {
        if (_mode == "managed")
        {
            return CreateManaged(path, limits, string.Empty);
        }
        try
        {
            NativeWalFile file = _createNative(path, limits.FlushBytes, _entries);
            return new WalFileSelection(file, "io_uring", string.Empty, file.IsFileRegistered);
        }
        catch (Exception error) when (_mode == "auto" && CanFallBack(error))
        {
            return CreateManaged(path, limits, error.Message);
        }
    }

    internal static bool CanFallBack(Exception error) => error switch
    {
        PlatformNotSupportedException => true,
        DllNotFoundException => true,
        NativeWalException { Operation: "create", ErrorCode: 1 or 13 or 38 or 95 } => true,
        NativeWalException { Operation: "flush", ErrorCode: 38 or 95 } => true,
        _ => false
    };

    private static WalFileSelection CreateManaged(string path, WalLimits limits, string reason) =>
        new WalFileSelection(new ManagedWalFile(path, limits.FileBufferBytes), "managed", reason, false);

    private static NativeWalFile CreateNative(string path, int capacity, int entries) =>
        PrepareNative(new LiburingWalContext(path, capacity, entries));

    internal static NativeWalFile PrepareNative(INativeWalContext context)
    {
        try
        {
            context.Flush();
        }
        catch
        {
            context.Dispose();
            throw;
        }
        return new NativeWalFile(context);
    }
}
