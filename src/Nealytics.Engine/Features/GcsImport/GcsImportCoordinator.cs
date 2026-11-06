namespace Nealytics.Engine.Features.GcsImport;

using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Infrastructure.Networking;

internal sealed class GcsImportCoordinator(
    GcsObjectClient client, ImportJobCoordinator jobs, ImportJobStore store, ImportJobLimits limits) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<GcsObjectSnapshot> InspectAsync(GcsSource source, GcsObjectRequest request, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            return await client.InspectAsync(source, request, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ImportJobResult> AppendAsync(GcsSource source, GcsObjectSnapshot snapshot, Guid jobId,
        Guid chunkId, long offset, Func<PipeReader, Task<NdjsonImportResponse>> ingest, CancellationToken cancellationToken)
    {
        if (jobId == Guid.Empty || chunkId == Guid.Empty || !snapshot.IsValid(source)
            || offset < 0 || offset > snapshot.Bytes)
        {
            return ImportJobResult.Reject(400, "invalid_object_request");
        }
        Enter();
        try
        {
            ImportJobState? previous = store.Find(jobId);
            int? exactLength = previous?.ConfirmedChunkBytes(source.ProjectId, chunkId,
                snapshot.SourceId, snapshot.Bytes, offset);
            if (exactLength > limits.ChunkBytes)
            {
                return ImportJobResult.Reject(409, "contract_changed");
            }
            using ObjectSourceChunk chunk = await client.ReadChunkAsync(source, snapshot, offset, exactLength, cancellationToken);
            return await jobs.AppendAsync(new(jobId, chunkId, snapshot.SourceId, snapshot.Bytes, offset),
                source.ProjectId, PipeReader.Create(new ReadOnlySequence<byte>(chunk.Bytes)), ingest, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Enter()
    {
        if (!_gate.Wait(0))
        {
            throw new GcsImportException(429, "gcs_busy");
        }
    }

    public void Dispose() => _gate.Dispose();
}
