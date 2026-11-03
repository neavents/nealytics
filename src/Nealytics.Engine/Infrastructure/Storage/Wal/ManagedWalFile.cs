namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.IO;
using System.Threading.Tasks;

internal sealed class ManagedWalFile : IWalFile
{
    private readonly FileStream _stream;

    public ManagedWalFile(string path, int bufferSize)
        : this(new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous))
    {
    }

    internal ManagedWalFile(FileStream stream)
    {
        _stream = stream;
        _stream.Position = _stream.Length;
    }

    public ValueTask AppendAsync(ReadOnlyMemory<byte> record) => _stream.WriteAsync(record);

    public async ValueTask FlushAsync()
    {
        await _stream.FlushAsync();
        _stream.Flush(flushToDisk: true);
    }

    public async ValueTask TruncateAsync()
    {
        _stream.SetLength(0);
        _stream.Position = 0;
        await FlushAsync();
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
