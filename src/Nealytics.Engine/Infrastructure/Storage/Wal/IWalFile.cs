namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Threading.Tasks;

internal interface IWalFile : IAsyncDisposable
{
    ValueTask AppendAsync(ReadOnlyMemory<byte> record);

    ValueTask FlushAsync();

    ValueTask TruncateAsync();
}
