namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;
using Microsoft.Win32.SafeHandles;

internal sealed class NativeWalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly INativeWalApi _api;

    public NativeWalHandle(nint context, INativeWalApi api) : base(ownsHandle: true)
    {
        _api = api;
        SetHandle(context);
    }

    public int CloseStatus { get; private set; }

    protected override bool ReleaseHandle()
    {
        CloseStatus = _api.Destroy(handle);
        return CloseStatus == 0;
    }
}
