namespace Nealytics.Engine.Features.ImportJobs;

using System;
using Nealytics.Engine.Features.ImportNdjson;

public sealed record ImportJobResponse(
    Guid JobId, string SourceId, long SourceBytes, string State, long NextByteOffset,
    long AcceptedRecords, Guid LastChunkId, NdjsonImportResponse? Receipt);

public sealed record ImportJobError(string Error);

internal sealed record ImportJobResult(int StatusCode, ImportJobResponse? Job, string? Error)
{
    public static ImportJobResult Reject(int status, string error) => new(status, null, error);
}
