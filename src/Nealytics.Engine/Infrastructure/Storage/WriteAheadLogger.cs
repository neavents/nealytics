namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage.Wal;

public sealed partial class WriteAheadLogger : IAsyncDisposable
{
    private readonly string _logFilePath;
    private readonly string _sealedFilePath;
    private readonly IWalFile _file;
    private readonly WalLimits _limits;
    private readonly WalAdmission _admission;
    private readonly WalRecordBuffer _recordBuffer;
    private readonly ArrayPool<byte> _recordPool;
    private readonly WalOperationGate _operations = new WalOperationGate();
    private readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _serializationLock = new SemaphoreSlim(1, 1);
    private readonly object _disposeSync = new object();
    private readonly Channel<PendingAppend> _pending;
    private readonly Task _flushLoop;
    private Task? _disposeTask;
    private long _uncommittedRecords;
    private long _flushGroupCount;
    private long _flushedRecordCount;
    private long _flushDurationTicks;

    public WriteAheadLogger(IOptions<TelemetryEngineOptions> options)
        : this(options, null)
    {
    }

    internal WriteAheadLogger(IOptions<TelemetryEngineOptions> options, IWalFile? file, ArrayPool<byte>? recordPool = null)
    {
        TelemetryEngineOptions engineOptions = options.Value;
        _limits = new WalLimits(engineOptions);
        _admission = new WalAdmission(_limits.PendingRecords, _limits.PendingBytes);
        _recordBuffer = new WalRecordBuffer(_limits.RecordBytes);
        _recordPool = recordPool ?? ArrayPool<byte>.Shared;
        string directory = engineOptions.WriteAheadLogDirectory;
        Directory.CreateDirectory(directory);
        _logFilePath = Path.Combine(directory, "telemetry_wal.log");
        _sealedFilePath = Path.Combine(directory, "telemetry_wal.replay");
        SealExistingLogForRecovery();

        _file = file ?? new ManagedWalFile(_logFilePath, _limits.FileBufferBytes);
        _pending = Channel.CreateBounded<PendingAppend>(new BoundedChannelOptions(_limits.PendingRecords)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _flushLoop = Task.Run(RunFlushLoopAsync);
    }

    internal int PendingRecordCount => _admission.PendingRecords;
    internal int PendingByteCount => _admission.PendingBytes;

    public long UncommittedRecordCount => Interlocked.Read(ref _uncommittedRecords);

    internal long FlushGroupCount => Interlocked.Read(ref _flushGroupCount);
    internal long FlushedRecordCount => Interlocked.Read(ref _flushedRecordCount);
    internal double AverageGroupSize
    {
        get
        {
            long groups = Interlocked.Read(ref _flushGroupCount);
            return groups == 0 ? 0 : (double)Interlocked.Read(ref _flushedRecordCount) / groups;
        }
    }
    internal double AverageFlushMilliseconds
    {
        get
        {
            long groups = Interlocked.Read(ref _flushGroupCount);
            if (groups == 0)
            {
                return 0;
            }
            double seconds = (double)Interlocked.Read(ref _flushDurationTicks) / Stopwatch.Frequency;
            return seconds / groups * 1000.0;
        }
    }

    public async Task AcknowledgeCommitAsync(int committedCount)
    {
        if (committedCount <= 0)
        {
            return;
        }

        _operations.Enter();
        try
        {
            await _fileLock.WaitAsync();
            try
            {
                _operations.ThrowIfFailed();
                long remaining = Math.Max(0, Interlocked.Read(ref _uncommittedRecords) - committedCount);
                if (remaining == 0)
                {
                    await _file.TruncateAsync();
                }
                Interlocked.Exchange(ref _uncommittedRecords, remaining);
            }
            catch (Exception error)
            {
                throw SignalFailure(error);
            }
            finally
            {
                _fileLock.Release();
            }
        }
        finally
        {
            _operations.Exit();
        }
    }

    public async Task TruncateAsync()
    {
        _operations.Enter();
        try
        {
            await _fileLock.WaitAsync();
            try
            {
                _operations.ThrowIfFailed();
                await _file.TruncateAsync();
                Interlocked.Exchange(ref _uncommittedRecords, 0);
            }
            catch (Exception error)
            {
                throw SignalFailure(error);
            }
            finally
            {
                _fileLock.Release();
            }
        }
        finally
        {
            _operations.Exit();
        }
    }

    private Exception SignalFailure(Exception error)
    {
        Exception failure = _operations.Fail(error);
        _pending.Writer.TryComplete(failure);
        _admission.Close(failure);
        return failure;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Task idle = _operations.Stop();
        _admission.Close(new ObjectDisposedException(nameof(WriteAheadLogger)));
        _pending.Writer.TryComplete();
        try
        {
            await _flushLoop;
            await idle;
            await _file.DisposeAsync();
        }
        finally
        {
            _recordBuffer.Dispose();
            _serializationLock.Dispose();
            _fileLock.Dispose();
        }
    }
}
