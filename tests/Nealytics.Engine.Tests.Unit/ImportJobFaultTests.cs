using System.IO.Pipelines;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ImportJobFaultTests
{
    [Theory]
    [InlineData("open", 1)]
    [InlineData("write", 1)]
    [InlineData("flush", 1)]
    [InlineData("publish", 1)]
    [InlineData("sync", 1)]
    [InlineData("open", 2)]
    [InlineData("write", 2)]
    [InlineData("flush", 2)]
    [InlineData("publish", 2)]
    [InlineData("sync", 2)]
    public async Task FailedStorageCannotInventAReceiptOrReplayAnUncertainChunk(string operation, int checkpoint)
    {
        int saves = 0;
        bool armed = false;
        using var harness = new ImportJobTestSupport.Harness(directory =>
        {
            var journal = new ImportJobJournal(directory, (path, mode) =>
            {
                if (mode == FileMode.CreateNew) saves++;
                bool fail = armed && saves == checkpoint;
                if (fail && operation == "open") throw new IOException("private storage detail");
                return new FaultFile(path, mode, fail ? operation : "");
            }, path =>
            {
                if (armed && saves == checkpoint && operation == "sync") throw new IOException("private storage detail");
                WalDirectoryDurability.Synchronize(path);
            }, (source, target) =>
            {
                if (armed && saves == checkpoint && operation == "publish") throw new IOException("private storage detail");
                File.Move(source, target, true);
            });
            armed = true;
            return journal;
        });
        var request = ImportJobTestSupport.Request();
        int admissions = 0;
        await Assert.ThrowsAsync<IOException>(() => harness.Append(request, ingest: source =>
        {
            admissions++;
            return ImportJobTestSupport.Accept(source);
        }));
        Assert.Equal(checkpoint - 1, admissions);
        Assert.Equal("Import job journal is unavailable until restart.", Assert.Throws<IOException>(() => harness.Store.Find(request.JobId)).Message);
        Assert.Throws<IOException>(() => harness.Store.Count);
        Assert.Throws<IOException>(() => harness.Store.Save(ImportJobTestSupport.Pending()));
        Assert.Throws<IOException>(() => { _ = harness.Store.StartAsync(CancellationToken.None); });
        harness.Restart();
        bool published = operation == "sync";
        var recovered = harness.Coordinator.Get(request.JobId, "p");
        if (checkpoint == 1 && !published)
        {
            Assert.Equal(404, recovered.StatusCode);
            Assert.Equal(202, (await harness.Append(request)).StatusCode);
        }
        else if (checkpoint == 2 && published)
        {
            Assert.Equal("ready", recovered.Job!.State);
            Assert.Equal(1, recovered.Job.AcceptedRecords);
            Assert.Equal(3, recovered.Job.NextByteOffset);
            var repeated = await harness.Append(request, ingest: _ => throw new InvalidOperationException("must not replay"));
            Assert.Equal(recovered.Job, repeated.Job);
        }
        else
        {
            Assert.Equal("needs_reconciliation", recovered.Job!.State);
            Assert.Equal(0, recovered.Job.NextByteOffset);
            Assert.Equal(0, recovered.Job.AcceptedRecords);
            Assert.Null(recovered.Job.Receipt);
            Assert.Equal("needs_reconciliation", (await harness.Append(request, ingest: _ => throw new InvalidOperationException("must not replay"))).Error);
        }
    }

    [Fact]
    public async Task CancellationDuringAdmissionPreservesOnlyPreviouslyConfirmedProgress()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var first = ImportJobTestSupport.Request();
        await harness.Append(first);
        var second = first with { Offset = 3, ChunkId = Guid.NewGuid() };
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Append(second, ingest: _ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<Nealytics.Engine.Features.ImportNdjson.NdjsonImportResponse>(cancellation.Token);
        }, token: cancellation.Token));
        Assert.Equal("needs_reconciliation", harness.Coordinator.Get(first.JobId, "p").Job!.State);
        harness.Restart();
        var status = harness.Coordinator.Get(first.JobId, "p").Job!;
        Assert.Equal(3, status.NextByteOffset);
        Assert.Equal(1, status.AcceptedRecords);
        Assert.Null(status.Receipt);
        Assert.Equal("needs_reconciliation", (await harness.Append(second)).Error);
    }

    [Fact]
    public async Task AnImpossibleIngestionReceiptCannotBecomeAConfirmedCheckpoint()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var request = ImportJobTestSupport.Request();
        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Append(request,
            ingest: _ => Task.FromResult(new Nealytics.Engine.Features.ImportNdjson.NdjsonImportResponse(1, 4, null))));
        harness.Restart();
        Assert.Equal("needs_reconciliation", harness.Coordinator.Get(request.JobId, "p").Job!.State);
        Assert.Equal("needs_reconciliation", (await harness.Append(request)).Error);
    }

    private sealed class FaultFile(string path, FileMode mode, string operation)
        : FileStream(path, mode, mode == FileMode.Open ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None)
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (operation == "write") throw new IOException("private storage detail");
            base.Write(buffer);
        }
        public override void Flush(bool flushToDisk)
        {
            if (operation == "flush") throw new IOException("private storage detail");
            base.Flush(flushToDisk);
        }
    }
}
