namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System.Runtime.InteropServices;

internal static class NativeWalPlatform
{
    public static bool IsSupported(bool linux, Architecture architecture) =>
        linux && (architecture == Architecture.X64 || architecture == Architecture.Arm64);
}
