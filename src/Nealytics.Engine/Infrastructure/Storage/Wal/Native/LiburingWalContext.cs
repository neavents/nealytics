namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal sealed class LiburingWalContext : INativeWalContext
{
    private readonly SafeFileHandle _file;
    private readonly NativeWalHandle _context;
    private readonly INativeWalApi _api;
    private bool _disposed;

    public LiburingWalContext(string path, int capacity, int entries)
        : this(path, capacity, entries, ResolveApi())
    {
    }

    internal LiburingWalContext(string path, int capacity, int entries, INativeWalApi api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(entries, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entries, 4096);
        if (_api.AbiVersion() != 1)
        {
            throw new InvalidOperationException("The native WAL bridge ABI is incompatible.");
        }

        _file = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        NativeWalHandle? created = null;
        try
        {
            int status = _api.Create(_file.DangerousGetHandle().ToInt32(),
                (uint)entries, (nuint)capacity, out nint context);
            CheckStatus(status, "create");
            if (context == 0 || context == -1)
            {
                throw new IOException("The native WAL bridge returned an invalid handle.");
            }
            created = new NativeWalHandle(context, _api);
            IsFileRegistered = _api.RegisteredFile(created) != 0;
            _context = created;
        }
        catch
        {
            created?.Dispose();
            _file.Dispose();
            throw;
        }
    }

    public bool IsFileRegistered { get; }

    public void Append(ReadOnlyMemory<byte> record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckStatus(_api.Append(_context, record), "append");
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckStatus(_api.Flush(_context), "flush");
    }

    public void Truncate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckStatus(_api.Truncate(_context), "truncate");
    }

    private static INativeWalApi ResolveApi()
    {
        if (!NativeWalPlatform.IsSupported(OperatingSystem.IsLinux(), RuntimeInformation.ProcessArchitecture))
        {
            throw new PlatformNotSupportedException("Native WAL I/O requires Linux x64 or ARM64.");
        }
        return NativeWalApi.Instance;
    }

    private static void CheckStatus(int status, string operation)
    {
        if (status < 0)
        {
            throw new IOException("Native WAL " + operation + " failed with errno " + -status + ".",
                new Win32Exception(-status));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _context.Dispose();
        _file.Dispose();
        CheckStatus(_context.CloseStatus, "destroy while I/O remains pending; process restart required");
    }
}
