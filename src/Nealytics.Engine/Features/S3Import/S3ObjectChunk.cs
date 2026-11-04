namespace Nealytics.Engine.Features.S3Import;

using System;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class S3ObjectChunk(WalRecordBuffer buffer, int length) : IDisposable
{
    public ReadOnlyMemory<byte> Bytes => buffer.WrittenMemory[..length];
    public void Dispose() => buffer.Dispose();
}
