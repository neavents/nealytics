namespace Nealytics.Engine.Infrastructure.Storage;

using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage.Wal;

public sealed partial class WriteAheadLogger : IAsyncDisposable
{
    private readonly string _logFilePath;
    private readonly WalDirectory _directory;
    private readonly string[] _recoveryPaths;
    private readonly IWalFile _file;
    private readonly WalLimits _limits;
    private readonly WalAdmission _admission;
    private readonly WalRecordBuffer _recordBuffer;
    private readonly WalRecordBuffer _batchBuffer;
    private readonly ArrayPool<byte> _recordPool;
    private readonly WalOperationGate _operations = new WalOperationGate();
    private readonly WalFlushStatistics _statistics = new WalFlushStatistics();
    private readonly WalSynchronization _synchronization;
    private readonly object _disposeSync = new object();
    private readonly Channel<PendingAppend> _pending;
    private readonly Task _flushLoop;
    private Task? _disposeTask;
    private long _uncommittedRecords;

    public WriteAheadLogger(IOptions<TelemetryEngineOptions> options)
        : this(options, file: null)
    {
    }

    public WriteAheadLogger(IOptions<TelemetryEngineOptions> options, ILogger<WriteAheadLogger> logger)
        : this(options)
    {
        try
        {
            LogFileSelection(logger, Selection.Backend, Selection.RegisteredFile, Selection.FallbackReason);
        }
        catch
        {
            DisposeAsync().GetAwaiter().GetResult();
            throw;
        }
    }

    internal WriteAheadLogger(
        IOptions<TelemetryEngineOptions> options,
        IWalFile? file,
        ArrayPool<byte>? recordPool = null,
        ArrayPool<byte>? serializationPool = null,
        WalSynchronization? synchronization = null,
        WalFileFactory? fileFactory = null,
        Action<string>? directorySynchronize = null)
    {
        TelemetryEngineOptions engineOptions = options.Value;
        _limits = new WalLimits(engineOptions);
        WalFileFactory factory = fileFactory ?? new WalFileFactory(engineOptions);
        string directory = engineOptions.WriteAheadLogDirectory;
        _synchronization = synchronization ?? new WalSynchronization();
        _admission = new WalAdmission(_limits.PendingRecords, _limits.PendingBytes);
        _recordBuffer = new WalRecordBuffer(_limits.RecordBytes, serializationPool, reserveForEncoding: true);
        _batchBuffer = new WalRecordBuffer(_limits.FlushBytes, serializationPool);
        _recordPool = recordPool ?? ArrayPool<byte>.Shared;
        IWalFile? ownedFile = file;
        WalDirectory? ownedDirectory = null;
        try
        {
            ownedDirectory = new WalDirectory(directory, directorySynchronize);
            _directory = ownedDirectory;
            _directory.SealActive();
            _recoveryPaths = _directory.RecoveryPaths();
            _logFilePath = _directory.ActivePath;
            Selection = file is null
                ? factory.Create(_logFilePath, _limits)
                : new WalFileSelection(file, "injected", string.Empty, false);
            ownedFile = Selection.File;
            _file = ownedFile;
            _directory.Synchronize();
            _pending = Channel.CreateBounded<PendingAppend>(_limits.CreateChannelOptions());
            _flushLoop = Task.Run(RunFlushLoopAsync);
        }
        catch (Exception startupFailure)
        {
            try
            {
                if (ownedFile is not null)
                {
                    ownedFile.DisposeAsync().GetAwaiter().GetResult();
                }
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("WAL startup and file cleanup both failed.", startupFailure, cleanupFailure);
            }
            finally
            {
                using (ownedDirectory)
                {
                    _synchronization.Dispose();
                }
            }
            throw;
        }
    }

    internal WalFileSelection Selection { get; }

    [LoggerMessage(EventId = 9010, Level = LogLevel.Information,
        Message = "WAL file backend {Backend}; registered file {RegisteredFile}; startup fallback {FallbackReason}")]
    private static partial void LogFileSelection(ILogger logger, string backend, bool registeredFile, string fallbackReason);

    internal int PendingRecordCount => _admission.PendingRecords;
    internal int PendingByteCount => _admission.PendingBytes;

    public long UncommittedRecordCount => Interlocked.Read(ref _uncommittedRecords);

    internal long FlushGroupCount => _statistics.GroupCount;
    internal long FlushedRecordCount => _statistics.RecordCount;
    internal double AverageGroupSize => _statistics.AverageGroupSize;
    internal double AverageFlushMilliseconds => _statistics.AverageFlushMilliseconds;

    public async Task AcknowledgeCommitAsync(int committedCount)
    {
        if (committedCount <= 0)
        {
            return;
        }

        _operations.Enter();
        try
        {
            await _synchronization.File.WaitAsync();
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
                _synchronization.File.Release();
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
            await _synchronization.File.WaitAsync();
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
                _synchronization.File.Release();
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
            await Task.WhenAll(_flushLoop, idle);
            await _file.DisposeAsync();
        }
        finally
        {
            using (_directory)
            using (_synchronization)
            using (_batchBuffer)
            {
                _recordBuffer.Dispose();
            }
        }
    }
}
