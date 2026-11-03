namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;
using System.Runtime.InteropServices;

internal static class NativeWalPlatform
{
    public static bool IsSupported(bool linux, Architecture architecture) =>
        linux && (architecture == Architecture.X64 || architecture == Architecture.Arm64);

    public static INativeWalApi ResolveApi(bool linux, Architecture architecture)
    {
        if (!IsSupported(linux, architecture))
        {
            throw new PlatformNotSupportedException("Native WAL I/O requires Linux x64 or ARM64.");
        }
        return NativeWalApi.Instance;
    }
}
