using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ImportJobCoordinatorTests
{
    [Fact]
    public async Task ConfirmedChunksResumeAcrossRestartAndTheLastRetryNeverCallsIngestionAgain()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var request = ImportJobTestSupport.Request();
        int calls = 0;
        async Task<NdjsonImportResponse> Ingest(PipeReader reader)
        {
            calls++;
            Assert.Equal("processing", harness.Coordinator.Get(request.JobId, "p").Job!.State);
            Assert.True(File.Exists(Path.Combine(harness.Directory, "import-jobs", request.JobId.ToString("N") + ".json")));
            return await ImportJobTestSupport.Accept(reader);
        }
        var first = await harness.Append(request, ingest: Ingest);
        Assert.Equal(202, first.StatusCode);
        Assert.Equal("ready", first.Job!.State);
        Assert.Equal(3, first.Job.NextByteOffset);
        Assert.Equal(1, first.Job.AcceptedRecords);
        Assert.Equal(request.ChunkId, first.Job.LastChunkId);
        Assert.Equal(new NdjsonImportResponse(1, 3, null), first.Job.Receipt);
        Assert.Null(first.Error);
        harness.Restart();
        Assert.Equal(first.Job, harness.Coordinator.Get(request.JobId, "p").Job);
        Assert.Equal(first, await harness.Append(request, ingest: Ingest));
        Assert.Equal(1, calls);
        var next = request with { ChunkId = Guid.NewGuid(), Offset = 3 };
        var complete = await harness.Append(next, ingest: Ingest);
        Assert.Equal("complete", complete.Job!.State);
        Assert.Equal(6, complete.Job.NextByteOffset);
        Assert.Equal(2, complete.Job.AcceptedRecords);
        Assert.Equal(2, calls);
        harness.Restart();
        Assert.Equal(complete, await harness.Append(next, ingest: Ingest));
        Assert.Equal(2, calls);
        Assert.Equal(1, harness.Store.Count);
    }

    [Fact]
    public async Task EmptySourcesAreDurableAndTheFinalChunkMayLackANewline()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var empty = ImportJobTestSupport.Request(0);
        var result = await harness.Append(empty, "");
        Assert.Equal("complete", result.Job!.State);
        Assert.Equal(0, result.Job.AcceptedRecords);
        Assert.Equal(0, result.Job.NextByteOffset);
        Assert.Equal(new NdjsonImportResponse(0, 0, null), result.Job.Receipt);
        Assert.Equal(result, await harness.Append(empty, ""));
        var final = await harness.Append(ImportJobTestSupport.Request(2), "{}");
        Assert.Equal("complete", final.Job!.State);
        Assert.Equal(2, final.Job.NextByteOffset);
    }

    [Theory]
    [InlineData("{}", 6)]
    [InlineData("{}\n", 2)]
    [InlineData("", 6)]
    public async Task InvalidSourceBoundariesDoNotCreateJobsOrCallIngestion(string body, long total)
    {
        using var harness = new ImportJobTestSupport.Harness();
        var request = ImportJobTestSupport.Request(total);
        var result = await harness.Append(request, body, _ => throw new Exception("must not ingest"));
        Assert.Equal(ImportJobResult.Reject(400, "invalid_chunk_boundary"), result);
        Assert.Equal(0, harness.Store.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(harness.Directory, "import-jobs"), "*.json"));
    }

    [Fact]
    public async Task IdentitiesOffsetsHashesAndTerminalStatesHaveDistinctConflicts()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var request = ImportJobTestSupport.Request();
        Assert.Equal(ImportJobResult.Reject(409, "offset_conflict"), await harness.Append(request with { Offset = 1 }));
        Assert.Equal(202, (await harness.Append(request)).StatusCode);
        Assert.Equal(ImportJobResult.Reject(404, "job_not_found"), harness.Coordinator.Get(request.JobId, "q"));
        Assert.Equal(ImportJobResult.Reject(404, "job_not_found"), harness.Coordinator.Get(Guid.NewGuid(), "p"));
        Assert.Equal(ImportJobResult.Reject(404, "job_not_found"), await harness.Append(request, project: "q"));
        Assert.Equal(ImportJobResult.Reject(409, "source_conflict"), await harness.Append(request with { SourceId = new string('B', 64) }));
        Assert.Equal(ImportJobResult.Reject(409, "source_conflict"), await harness.Append(request with { SourceBytes = 7 }));
        Assert.Equal(ImportJobResult.Reject(409, "chunk_conflict"), await harness.Append(request with { Offset = 3 }));
        Assert.Equal(ImportJobResult.Reject(409, "chunk_conflict"), await harness.Append(request, " {}\n"));
        Assert.Equal(ImportJobResult.Reject(409, "chunk_conflict"), await harness.Append(request, "[]\n"));
        Assert.Equal(ImportJobResult.Reject(409, "offset_conflict"), await harness.Append(request with { ChunkId = Guid.NewGuid() }));
        var final = request with { ChunkId = Guid.NewGuid(), Offset = 3 };
        Assert.Equal(202, (await harness.Append(final)).StatusCode);
        Assert.Equal(ImportJobResult.Reject(409, "job_terminal"), await harness.Append(request with { ChunkId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task ContractChangesBlockNewChunksButRetainTheConfirmedReceipt()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var request = ImportJobTestSupport.Request();
        var first = await harness.Append(request);
        harness.Restart(options => options.ServerEventTypes = "private");
        Assert.Equal(first, await harness.Append(request));
        Assert.Equal(ImportJobResult.Reject(409, "contract_changed"),
            await harness.Append(request with { ChunkId = Guid.NewGuid(), Offset = 3 }));
        Assert.Equal(first.Job, harness.Coordinator.Get(request.JobId, "p").Job);
    }

    [Fact]
    public async Task RejectedPrefixesRemainDurableAndNeverSkipThePoisonRecord()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var request = ImportJobTestSupport.Request(6);
        var rejection = await harness.Append(request, "{}\n{}\n", _ => Task.FromResult(new NdjsonImportResponse(1, 3, "invalid_json")));
        Assert.Equal(400, rejection.StatusCode);
        Assert.Equal("rejected", rejection.Job!.State);
        Assert.Equal(3, rejection.Job.NextByteOffset);
        Assert.Equal(1, rejection.Job.AcceptedRecords);
        harness.Restart();
        Assert.Equal(rejection, await harness.Append(request, "{}\n{}\n", _ => throw new Exception("must not retry")));
        Assert.Equal(ImportJobResult.Reject(409, "job_terminal"),
            await harness.Append(request with { ChunkId = Guid.NewGuid(), Offset = 3 }));
    }

    [Fact]
    public async Task HistoryCapacityIncludesCompletedJobsAndDoesNotEvictTheirReceipts()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var first = ImportJobTestSupport.Request(3);
        var accepted = await harness.Append(first);
        Assert.Equal(202, (await harness.Append(ImportJobTestSupport.Request(3))).StatusCode);
        Assert.Equal(ImportJobResult.Reject(409, "job_capacity"), await harness.Append(ImportJobTestSupport.Request(3)));
        Assert.Equal(2, harness.Store.Count);
        Assert.Equal(accepted, await harness.Append(first));
    }

    [Fact]
    public async Task CancellationAfterReadingButBeforeTheIntentCreatesNoCheckpoint()
    {
        using var harness = new ImportJobTestSupport.Harness();
        using CancellationTokenSource caller = new();
        var reader = new NdjsonRecordReaderTests.ProbeReader(ImportJobTestSupport.Reader("{}\n")) { OnAdvance = caller.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Coordinator.AppendAsync(
            ImportJobTestSupport.Request(), "p", reader, _ => throw new Exception("must not ingest"), caller.Token));
        Assert.Equal(1, reader.Completions);
        Assert.Equal(0, harness.Store.Count);
        Assert.Equal(202, (await harness.Append(ImportJobTestSupport.Request(3))).StatusCode);
    }

    [Fact]
    public async Task OneChunkOwnsExecutionWhileStatusRemainsReadableAndFailuresBlockReplay()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var request = ImportJobTestSupport.Request(3);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = harness.Append(request, ingest: async _ => { entered.SetResult(); await release.Task; throw new IOException("secret failure"); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reader = new NdjsonRecordReaderTests.ProbeReader(ImportJobTestSupport.Reader("{}\n"));
        Assert.Equal(ImportJobResult.Reject(429, "job_busy"), await harness.Coordinator.AppendAsync(
            request, "p", reader, _ => throw new Exception("must not ingest"), CancellationToken.None));
        Assert.Equal(1, reader.Completions);
        Assert.Equal("processing", harness.Coordinator.Get(request.JobId, "p").Job!.State);
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("needs_reconciliation", harness.Coordinator.Get(request.JobId, "p").Job!.State);
        Assert.Equal(ImportJobResult.Reject(409, "needs_reconciliation"), await harness.Append(request));
        harness.Restart();
        var recovered = harness.Coordinator.Get(request.JobId, "p").Job!;
        Assert.Equal("needs_reconciliation", recovered.State);
        Assert.Equal(0, recovered.AcceptedRecords);
        Assert.Equal(0, recovered.NextByteOffset);
        Assert.Null(recovered.Receipt);
        Assert.Equal(ImportJobResult.Reject(409, "needs_reconciliation"), await harness.Append(request));
    }

    [Fact]
    public async Task APreviouslyProcessingManifestIsNeverTreatedAsAConfirmedRetry()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var state = ImportJobTestSupport.Pending();
        harness.Store.Save(state);
        Assert.Equal(ImportJobResult.Reject(409, "needs_reconciliation"), await harness.Append(
            new(state.Id, state.ChunkId, state.SourceId, state.SourceBytes, 0)));
        Assert.Equal("processing", harness.Coordinator.Get(state.Id, "p").Job!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyLimitAndCancellationReturnEveryOwnedBuffer(bool cancel)
    {
        var pool = new TrackingPool();
        using var harness = new ImportJobTestSupport.Harness(pool: pool);
        using CancellationTokenSource token = new();
        var reader = new ImportJobTestSupport.ChunkReader(
            new(new ReadOnlySequence<byte>(new byte[2048]), false, false),
            new(new ReadOnlySequence<byte>(new byte[2049]), false, true)) { OnAdvance = cancel ? token.Cancel : null };
        var operation = harness.Coordinator.AppendAsync(ImportJobTestSupport.Request(4097), "p", reader,
            _ => throw new Exception("must not ingest"), token.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        else Assert.Equal(ImportJobResult.Reject(413, "chunk_too_large"), await operation);
        Assert.Equal(1, reader.Completions);
        Assert.NotEmpty(pool.Rented);
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
        Assert.All(pool.Returned, clear => Assert.True(clear));
        Assert.Equal(0, harness.Store.Count);
    }

    [Theory]
    [InlineData("cancel-result")]
    [InlineData("reader-fault")]
    [InlineData("complete-fault")]
    public async Task InputFailuresCannotCreateAnIntentAndReleasePooledBytes(string scenario)
    {
        var pool = new TrackingPool();
        using var harness = new ImportJobTestSupport.Harness(pool: pool);
        var reader = new ImportJobTestSupport.ChunkReader(
            new(ReadOnlySequence<byte>.Empty, false, false),
            new(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("{}")), false, false),
            new(new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes("\n")), scenario == "cancel-result", true));
        if (scenario == "reader-fault") reader.ReadFailure = new IOException("secret reader failure");
        if (scenario == "complete-fault") reader.CompleteFailure = new IOException("secret completion failure");
        var task = harness.Coordinator.AppendAsync(ImportJobTestSupport.Request(), "p", reader, ImportJobTestSupport.Accept, CancellationToken.None);
        if (scenario == "cancel-result") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        else await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Equal(1, reader.Completions);
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
        Assert.All(pool.Returned, clear => Assert.True(clear));
        Assert.Equal(0, harness.Store.Count);
    }

    [Fact]
    public async Task CompleteChunkBytesAreConsumedEvenWhenTheProducerPauseThresholdIsSmaller()
    {
        using var harness = new ImportJobTestSupport.Harness();
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 16, resumeWriterThreshold: 8, useSynchronizationContext: false));
        byte[] body = Encoding.UTF8.GetBytes(new string('x', 100) + "\n");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        var task = harness.Coordinator.AppendAsync(ImportJobTestSupport.Request(body.Length), "p", pipe.Reader,
            ImportJobTestSupport.Accept, deadline.Token);
        for (int i = 0; i < body.Length; i += 8)
            await pipe.Writer.WriteAsync(body.AsMemory(i, Math.Min(8, body.Length - i)), deadline.Token);
        await pipe.Writer.CompleteAsync();
        var response = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(202, response.StatusCode);
        Assert.Equal(body.Length, response.Job!.NextByteOffset);
        Assert.Equal("complete", response.Job.State);
    }

    internal sealed class TrackingPool : ArrayPool<byte>
    {
        internal List<byte[]> Rented { get; } = [];
        internal List<bool> Returned { get; } = [];
        public override byte[] Rent(int minimumLength) { var value = new byte[minimumLength]; Rented.Add(value); return value; }
        public override void Return(byte[] array, bool clearArray = false) { Assert.Contains(array, Rented); Returned.Add(clearArray); }
    }
}
