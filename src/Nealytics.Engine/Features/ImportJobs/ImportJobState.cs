namespace Nealytics.Engine.Features.ImportJobs;

using System;
using System.IO;
using Nealytics.Engine.Features.ImportNdjson;

public sealed record ImportJobState(
    int Version, Guid Id, string ProjectId, string SourceId, long SourceBytes, string Contract,
    string Status, long NextByteOffset, long AcceptedRecords, Guid ChunkId, long ChunkOffset,
    int ChunkBytes, string ChunkHash, NdjsonImportResponse? Receipt)
{
    public ImportJobResponse Response() => new(Id, SourceId, SourceBytes, Status,
        NextByteOffset, AcceptedRecords, ChunkId, Receipt);

    public void Validate()
    {
        if (Version != 1 || Id == Guid.Empty || ChunkId == Guid.Empty
            || string.IsNullOrWhiteSpace(ProjectId) || ProjectId.Length > 256
            || !ImportJobRequest.IsDigest(SourceId) || !ImportJobRequest.IsDigest(Contract)
            || !ImportJobRequest.IsDigest(ChunkHash))
        {
            throw new InvalidDataException("Import job identity or contract is invalid.");
        }
        if (NextByteOffset < 0 || NextByteOffset > SourceBytes
            || AcceptedRecords < 0 || AcceptedRecords > NextByteOffset
            || ChunkOffset < 0 || ChunkOffset > NextByteOffset
            || ChunkBytes is < 0 or > 1_048_576 || ChunkBytes > SourceBytes - ChunkOffset)
        {
            throw new InvalidDataException("Import job counters exceed their source boundaries.");
        }
        if (Status == "processing")
        {
            if (Receipt is not null || NextByteOffset != ChunkOffset)
            {
                throw new InvalidDataException("An unconfirmed import chunk cannot contain a receipt.");
            }
            return;
        }
        if (Receipt is null || Receipt.AcceptedRecords is < 0 or > 10_000
            || Receipt.NextByteOffset < 0 || Receipt.NextByteOffset > ChunkBytes
            || Receipt.AcceptedRecords > Receipt.NextByteOffset || Receipt.AcceptedRecords > AcceptedRecords
            || NextByteOffset - ChunkOffset != Receipt.NextByteOffset
            || (Receipt.Error is not null && Receipt.Error.Length is < 1 or > 128)
            || (Receipt.Complete && Receipt.NextByteOffset != ChunkBytes)
            || Status != (Receipt.Complete ? NextByteOffset == SourceBytes ? "complete" : "ready" : "rejected"))
        {
            throw new InvalidDataException("Import job receipt does not match its committed progress.");
        }
    }
}
