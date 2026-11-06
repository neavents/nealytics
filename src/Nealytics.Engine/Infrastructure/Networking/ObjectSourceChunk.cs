namespace Nealytics.Engine.Infrastructure.Networking;

using System;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class ObjectSourceChunk(WalRecordBuffer buffer, int length) : IDisposable
{
    public ReadOnlyMemory<byte> Bytes => buffer.WrittenMemory[..length];
    public void Dispose() => buffer.Dispose();
}
