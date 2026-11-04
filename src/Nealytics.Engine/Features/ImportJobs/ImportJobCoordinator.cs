namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Storage.Wal;

internal sealed class ImportJobCoordinator : IDisposable
{
    private readonly ImportJobStore _store;
    private readonly ImportJobContracts _contracts;
    private readonly ImportJobLimits _limits;
    private readonly ArrayPool<byte> _pool;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ImportJobCoordinator(ImportJobStore store, ImportJobContracts contracts, ImportJobLimits limits,
        ArrayPool<byte>? pool = null)
    {
        _store = store;
        _contracts = contracts;
        _limits = limits;
        _pool = pool ?? ArrayPool<byte>.Shared;
    }

    public ImportJobResult Get(Guid id, string project)
    {
        ImportJobState? state = _store.Find(id);
        return state is null || state.ProjectId != project ? ImportJobResult.Reject(404, "job_not_found")
            : new(200, state.Response(), null);
    }

    public async Task<ImportJobResult> AppendAsync(ImportJobRequest request, string project,
        PipeReader source, Func<PipeReader, Task<NdjsonImportResponse>> ingest, CancellationToken cancellationToken)
    {
        if (!_gate.Wait(0))
        {
            await source.CompleteAsync();
            return ImportJobResult.Reject(429, "job_busy");
        }
        try
        {
            using WalRecordBuffer buffer = new(_limits.ChunkBytes, _pool);
            if (!await ReadAsync(source, buffer, cancellationToken))
            {
                return ImportJobResult.Reject(413, "chunk_too_large");
            }
            ReadOnlyMemory<byte> bytes = buffer.WrittenMemory;
            if (bytes.Length > request.SourceBytes - request.Offset
                || (bytes.IsEmpty && request.SourceBytes != 0)
                || (request.Offset + bytes.Length < request.SourceBytes && bytes.Span[^1] != (byte)'\n'))
            {
                return ImportJobResult.Reject(400, "invalid_chunk_boundary");
            }
            string hash = Convert.ToHexString(SHA256.HashData(bytes.Span));
            ImportJobState? previous = _store.Find(request.JobId);
            ImportJobResult? conflict = Check(previous, request, project, bytes.Length, hash);
            if (conflict is not null)
            {
                return conflict;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ImportJobState pending = new(1, request.JobId, project, request.SourceId, request.SourceBytes,
                _contracts.Find(project), "processing", request.Offset, previous?.AcceptedRecords ?? 0,
                request.ChunkId, request.Offset, bytes.Length, hash, null);
            _store.Save(pending);
            try
            {
                PipeReader reader = PipeReader.Create(new ReadOnlySequence<byte>(bytes));
                NdjsonImportResponse receipt;
                try
                {
                    receipt = await ingest(reader);
                }
                finally
                {
                    await reader.CompleteAsync();
                }
                ImportJobState finished = pending with
                {
                    NextByteOffset = request.Offset + receipt.NextByteOffset,
                    AcceptedRecords = pending.AcceptedRecords + receipt.AcceptedRecords,
                    Status = receipt.Error is not null ? "rejected"
                        : request.Offset + receipt.NextByteOffset == request.SourceBytes ? "complete" : "ready",
                    Receipt = receipt
                };
                _store.Save(finished);
                return new(receipt.StatusCode, finished.Response(), null);
            }
            catch
            {
                _store.MarkUncertain(request.JobId);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private ImportJobResult? Check(ImportJobState? state, ImportJobRequest request, string project, int bytes, string hash)
    {
        if (state is null)
        {
            return request.Offset != 0 ? ImportJobResult.Reject(409, "offset_conflict")
                : _store.Count == _limits.Jobs ? ImportJobResult.Reject(409, "job_capacity") : null;
        }
        if (state.ProjectId != project)
        {
            return ImportJobResult.Reject(404, "job_not_found");
        }
        if (state.SourceId != request.SourceId || state.SourceBytes != request.SourceBytes)
        {
            return ImportJobResult.Reject(409, "source_conflict");
        }
        if (state.Status is "needs_reconciliation" or "processing")
        {
            return ImportJobResult.Reject(409, "needs_reconciliation");
        }
        if (state.ChunkId == request.ChunkId)
        {
            return state.ChunkOffset != request.Offset || state.ChunkBytes != bytes || state.ChunkHash != hash
                ? ImportJobResult.Reject(409, "chunk_conflict") : new(state.Receipt!.StatusCode, state.Response(), null);
        }
        if (state.Status != "ready")
        {
            return ImportJobResult.Reject(409, "job_terminal");
        }
        if (state.Contract != _contracts.Find(project))
        {
            return ImportJobResult.Reject(409, "contract_changed");
        }
        return state.NextByteOffset != request.Offset ? ImportJobResult.Reject(409, "offset_conflict") : null;
    }

    private async Task<bool> ReadAsync(PipeReader source, WalRecordBuffer buffer, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadResult read = await source.ReadAsync(cancellationToken);
                if (read.IsCanceled)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                if (read.Buffer.Length > _limits.ChunkBytes - buffer.WrittenMemory.Length)
                {
                    return false;
                }
                foreach (ReadOnlyMemory<byte> segment in read.Buffer)
                {
                    buffer.Write(segment.Span);
                }
                source.AdvanceTo(read.Buffer.End);
                if (read.IsCompleted)
                {
                    return true;
                }
            }
        }
        finally
        {
            await source.CompleteAsync();
        }
    }

    public void Dispose() => _gate.Dispose();
}
