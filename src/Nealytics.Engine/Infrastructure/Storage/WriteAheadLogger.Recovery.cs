namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage.Wal;

public sealed partial class WriteAheadLogger
{
    public bool HasSealedSegment
    {
        get
        {
            foreach (string path in _recoveryPaths)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public async Task<IReadOnlyList<GlobalTelemetryPayload>> ReplayUncommittedAsync()
    {
        List<GlobalTelemetryPayload> recovered = new List<GlobalTelemetryPayload>();
        await foreach (WalReplayRecord record in ReplayRecordsAsync())
        {
            recovered.Add(record.Payload);
        }
        return recovered;
    }

    internal int ReplayBatchByteLimit => _limits.FlushBytes;

    internal async IAsyncEnumerable<WalReplayRecord> ReplayRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        WalReplayReader reader = new WalReplayReader(_limits.RecordBytes);
        foreach (string path in _recoveryPaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }
            await using FileStream source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, WalReplayReader.OpenOptions);
            long ordinal = 0;
            await foreach (WalReplayRecord record in reader.ReadAsync(source, cancellationToken))
            {
                if (record.Payload.WalRecordId == Guid.Empty)
                {
                    string identity = Path.GetFileName(path) + ":" + ordinal.ToString(CultureInfo.InvariantCulture);
                    record.Payload.WalRecordId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
                }
                ordinal++;
                yield return record;
            }
        }
    }

    public async Task DeleteSealedSegmentAsync()
    {
        _operations.Enter();
        try
        {
            await _synchronization.File.WaitAsync();
            try
            {
                _operations.ThrowIfFailed();
                long bytes = 0;
                foreach (string path in _recoveryPaths)
                {
                    if (File.Exists(path))
                    {
                        bytes = checked(bytes + new FileInfo(path).Length);
                    }
                }
                _directory.DeleteRecovery(_recoveryPaths);
                _backlog.Release(bytes);
            }
            catch (Exception error)
            {
                throw SignalFailure(error);
            }
            finally
            {
                _synchronization.File.Release();
            }
        }
        finally
        {
            _operations.Exit();
        }
    }
}
