namespace Nealytics.Engine.Infrastructure.Storage.Wal;

using System;
using Nealytics.Engine.Infrastructure.Configuration;

internal sealed class WalLimits
{
    public WalLimits(TelemetryEngineOptions options)
    {
        if (options.WalMaxPendingRecords < 1
            || options.WalMaxPendingBytes < 256
            || options.WalMaxRecordBytes < 256
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
    }

    public int PendingRecords { get; }
    public int PendingBytes { get; }
    public int RecordBytes { get; }
    public int FlushRecords { get; }
    public int FlushBytes { get; }
    public int FileBufferBytes { get; }
}
