namespace Nealytics.Engine.Infrastructure.Storage.Wal.Native;

using System;

internal interface INativeWalContext : IDisposable
{
    bool IsFileRegistered { get; }

    void Append(ReadOnlyMemory<byte> record);

    void Flush();

    void Truncate();
}
