namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using System.Threading.Channels;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed class WalLimits
{
    public WalLimits(TelemetryEngineOptions options)
    {
        if (options.WalMaxRecordBytes < 256
            || options.WalMaxRecordBytes > options.WalMaxFlushBytes
            || options.WalMaxFlushBytes > options.WalMaxPendingBytes
            || options.WalMaxFlushRecords < 1
            || options.WalMaxFlushRecords > options.WalMaxPendingRecords)
        {
            throw new ArgumentException("WAL limits require positive capacities and record <= flush <= pending byte limits.", nameof(options));
        }

        PendingRecords = options.WalMaxPendingRecords;
        PendingBytes = options.WalMaxPendingBytes;
        RecordBytes = options.WalMaxRecordBytes;
        FlushRecords = options.WalMaxFlushRecords;
        FlushBytes = options.WalMaxFlushBytes;
        FileBufferBytes = options.WalFileBufferBytes > 0 ? options.WalFileBufferBytes : 65_536;
        InitialGroupCapacity = Math.Min(FlushRecords, 256);
    }

    public int PendingRecords { get; }
    public int PendingBytes { get; }
    public int RecordBytes { get; }
    public int FlushRecords { get; }
    public int FlushBytes { get; }
    public int FileBufferBytes { get; }
    public int InitialGroupCapacity { get; }

    public BoundedChannelOptions CreateChannelOptions() => new BoundedChannelOptions(PendingRecords)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    };
}
