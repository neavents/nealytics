using System.IO.Pipelines;
using System.Net;
using System.Text;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Features.HttpImport;
using static Nealytics.Engine.Tests.Unit.S3TransportTestSupport;

namespace Nealytics.Engine.Tests.Unit;

public sealed class HttpCoordinatorTests
{
    [Fact]
    public async Task EmptyObjectsCanCreateAndRepeatAConfirmedTerminalCheckpoint()
    {
        using var jobs = new ImportJobTestSupport.Harness();
        var handler = Objects("");
        using var client = HttpObjectTestSupport.Client(handler);
        using var coordinator = Coordinator(jobs, client);
        using var sources = HttpObjectTestSupport.Catalog(); var source = sources.Find("source", "p")!; var snapshot = HttpObjectTestSupport.Snapshot(source, 0);
        Guid job = Guid.NewGuid(); Guid chunk = Guid.NewGuid();
        var accepted = await coordinator.AppendAsync(source, snapshot, job, chunk, 0, ImportJobTestSupport.Accept, CancellationToken.None);
        Assert.Equal(202, accepted.StatusCode);
        Assert.Equal("complete", accepted.Job!.State);
        Assert.Equal(0, accepted.Job.NextByteOffset);
        Assert.Equal(0, accepted.Job.AcceptedRecords);
        Assert.Equal(accepted, await coordinator.AppendAsync(source, snapshot, job, chunk, 0,
            _ => throw new InvalidOperationException("must not replay"), CancellationToken.None));
    }

    private static Handler Objects(string body) => new(request =>
    {
        if (request.Method == HttpMethod.Head)
        {
            var response = Response(); response.Content.Headers.ContentLength = body.Length; return response;
        }
        var range = Assert.Single(request.Headers.Range!.Ranges);
        int from = (int)range.From!.Value; int count = (int)(range.To!.Value - range.From.Value + 1);
        return Response(body.Substring(from, count), body.Length, from);
    });

    private static HttpImportCoordinator Coordinator(ImportJobTestSupport.Harness jobs, HttpObjectClient client, int bytes = 4096) =>
        new(client, jobs.Coordinator, jobs.Store, new(jobs.Settings.ImportJobMaxJobs, bytes));

    [Fact]
    public async Task HttpOperationsHaveNoWaitingQueueAndReleaseTheirGateAfterCompletion()
    {
        using var jobs = new ImportJobTestSupport.Harness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, token) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token);
            var response = Response(); response.Content.Headers.ContentLength = 6; return response;
        });
        using var client = HttpObjectTestSupport.Client(handler);
        using var coordinator = Coordinator(jobs, client);
        using var sources = HttpObjectTestSupport.Catalog(); var source = sources.Find("source", "p")!;
        var pending = coordinator.InspectAsync(source, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(500));
            var inspect = await Assert.ThrowsAsync<HttpImportException>(() => coordinator.InspectAsync(source, deadline.Token));
            Assert.Equal(429, inspect.StatusCode); Assert.Equal("http_source_busy", inspect.Error);
            var append = await Assert.ThrowsAsync<HttpImportException>(() => coordinator.AppendAsync(source, HttpObjectTestSupport.Snapshot(source, 6), Guid.NewGuid(),
                Guid.NewGuid(), 0, ImportJobTestSupport.Accept, deadline.Token));
            Assert.Equal(429, append.StatusCode);
            Assert.Single(handler.Requests);
            Assert.Equal(0, jobs.Store.Count);
            Assert.Equal(202, (await jobs.Append(ImportJobTestSupport.Request())).StatusCode);
        }
        finally { release.TrySetResult(); }
        Assert.Equal(6, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Bytes);
        Assert.Equal(6, (await coordinator.InspectAsync(source, CancellationToken.None)).Bytes);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("job")]
    [InlineData("chunk")]
    [InlineData("snapshot")]
    [InlineData("negative")]
    [InlineData("past")]
    public async Task InvalidJobMetadataNeverFetchesOrTouchesTheJournal(string invalid)
    {
        using var jobs = new ImportJobTestSupport.Harness();
        using var sources = HttpObjectTestSupport.Catalog(); var source = sources.Find("source", "p")!;
        var handler = new Handler(_ => throw new InvalidOperationException("must not send"));
        using var client = HttpObjectTestSupport.Client(handler);
        using var coordinator = Coordinator(jobs, client);
        var snapshot = HttpObjectTestSupport.Snapshot(source, 6);
        if (invalid == "snapshot") snapshot = snapshot with { SourceId = "wrong" };
        var result = await coordinator.AppendAsync(source, snapshot, invalid == "job" ? Guid.Empty : Guid.NewGuid(),
            invalid == "chunk" ? Guid.Empty : Guid.NewGuid(), invalid == "negative" ? -1 : invalid == "past" ? 7 : 0,
            _ => throw new InvalidOperationException("must not admit"), CancellationToken.None);
        Assert.Equal(ImportJobResult.Reject(400, "invalid_object_request"), result);
        Assert.Empty(handler.Requests);
        Assert.Equal(0, jobs.Store.Count);
    }

    [Fact]
    public async Task FailedFetchesAndCallerCancellationLeaveNoCheckpointAndReleaseAdmission()
    {
        using var jobs = new ImportJobTestSupport.Harness();
        bool fail = true;
        var handler = new Handler((_, token) => fail ? Task.FromException<HttpResponseMessage>(new HttpRequestException("private failure"))
            : Task.FromResult(Response("{}\n", 3)));
        using var client = HttpObjectTestSupport.Client(handler);
        using var coordinator = Coordinator(jobs, client);
        using var sources = HttpObjectTestSupport.Catalog(); var source = sources.Find("source", "p")!; var snapshot = HttpObjectTestSupport.Snapshot(source, 3);
        Guid job = Guid.NewGuid(); Guid chunk = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<HttpImportException>(() => coordinator.AppendAsync(source, snapshot, job, chunk, 0,
            ImportJobTestSupport.Accept, CancellationToken.None));
        Assert.Equal("source_unavailable", error.Error);
        Assert.Equal(0, jobs.Store.Count);
        fail = false;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.AppendAsync(source, snapshot, job, chunk, 0,
            ImportJobTestSupport.Accept, canceled.Token));
        Assert.Equal(0, jobs.Store.Count);
        Assert.Equal(202, (await coordinator.AppendAsync(source, snapshot, job, chunk, 0,
            ImportJobTestSupport.Accept, CancellationToken.None)).StatusCode);
    }

    [Fact]
    public async Task FailedAdmissionMarksOnlyAnUncertainPrefixAndReturnsSourceBuffers()
    {
        using var jobs = new ImportJobTestSupport.Harness();
        var handler = Objects("{}\n");
        var pool = new ImportJobCoordinatorTests.TrackingPool();
        using var client = HttpObjectTestSupport.Client(handler, pool: pool);
        using var coordinator = Coordinator(jobs, client);
        using var sources = HttpObjectTestSupport.Catalog(); var source = sources.Find("source", "p")!; var snapshot = HttpObjectTestSupport.Snapshot(source, 3);
        Guid job = Guid.NewGuid(); Guid chunk = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => coordinator.AppendAsync(source, snapshot, job, chunk, 0,
            _ => throw new IOException("admission failure"), CancellationToken.None));
        Assert.Equal("needs_reconciliation", jobs.Coordinator.Get(job, "p").Job!.State);
        Assert.NotEmpty(pool.Rented);
        Assert.Equal(pool.Rented.Count, pool.Returned.Count);
        Assert.All(pool.Returned, clear => Assert.True(clear));
        var repeated = await coordinator.AppendAsync(source, snapshot, job, chunk, 0,
            _ => throw new InvalidOperationException("must not replay"), CancellationToken.None);
        Assert.Equal(ImportJobResult.Reject(409, "needs_reconciliation"), repeated);
    }

    [Fact]
    public async Task ConfirmedRetryLengthsSurviveFramingChangesAndNewChunksStillFenceTheContract()
    {
        using var jobs = new ImportJobTestSupport.Harness();
        using var sources = HttpObjectTestSupport.Catalog(); var source = sources.Find("source", "p")!; var snapshot = HttpObjectTestSupport.Snapshot(source, 9);
        Guid job = Guid.NewGuid(); Guid chunk = Guid.NewGuid();
        ImportJobResult receipt;
        using (var client = HttpObjectTestSupport.Client(Objects("{}\n{}\n{}\n"), bytes: 4096, records: 2))
        using (var coordinator = Coordinator(jobs, client))
        {
            receipt = await coordinator.AppendAsync(source, snapshot, job, chunk, 0,
                _ => Task.FromResult(new NdjsonImportResponse(2, 6, null)), CancellationToken.None);
            Assert.Equal(202, receipt.StatusCode);
            Assert.Equal(6, receipt.Job!.NextByteOffset);
        }
        jobs.Restart(options => options.ImportMaxRecords = 1);
        var handler = Objects("{}\n{}\n{}\n");
        using var changedClient = HttpObjectTestSupport.Client(handler, bytes: 4096, records: 1);
        using var changedCoordinator = Coordinator(jobs, changedClient);
        var repeated = await changedCoordinator.AppendAsync(source, snapshot, job, chunk, 0,
            _ => throw new InvalidOperationException("must not replay confirmed data"), CancellationToken.None);
        Assert.Equal(receipt, repeated);
        Assert.Equal("bytes=0-5", Assert.Single(handler.Requests).Headers.Range!.ToString());
        var next = await changedCoordinator.AppendAsync(source, snapshot, job, Guid.NewGuid(), 6,
            _ => throw new InvalidOperationException("must not reinterpret new data"), CancellationToken.None);
        Assert.Equal(ImportJobResult.Reject(409, "contract_changed"), next);
    }

    [Theory]
    [InlineData(4, 409)]
    [InlineData(6, 202)]
    public async Task ConfirmedRetriesRespectInclusiveCurrentChunkBudgets(int bytes, int expected)
    {
        using var jobs = new ImportJobTestSupport.Harness();
        using var sources = HttpObjectTestSupport.Catalog(); var source = sources.Find("source", "p")!; var snapshot = HttpObjectTestSupport.Snapshot(source, 9);
        Guid job = Guid.NewGuid(); Guid chunk = Guid.NewGuid();
        using (var client = HttpObjectTestSupport.Client(Objects("{}\n{}\n{}\n"), bytes: 4096))
        using (var coordinator = Coordinator(jobs, client))
            Assert.Equal(202, (await coordinator.AppendAsync(source, snapshot, job, chunk, 0,
                _ => Task.FromResult(new NdjsonImportResponse(2, 6, null)), CancellationToken.None)).StatusCode);
        jobs.Restart(options => { options.MaxRequestBodyBytes = bytes; options.ImportMaxLineBytes = bytes; });
        var handler = Objects("{}\n{}\n{}\n");
        using var smallerClient = HttpObjectTestSupport.Client(handler, bytes: bytes);
        using var smallerCoordinator = Coordinator(jobs, smallerClient, bytes: bytes);
        var repeated = await smallerCoordinator.AppendAsync(source, snapshot, job, chunk, 0,
            _ => throw new InvalidOperationException("must not replay"), CancellationToken.None);
        Assert.Equal(expected, repeated.StatusCode);
        if (expected == 409)
        {
            Assert.Equal("contract_changed", repeated.Error);
            Assert.Empty(handler.Requests);
        }
        else Assert.Equal("bytes=0-5", Assert.Single(handler.Requests).Headers.Range!.ToString());
        Assert.Equal(6, jobs.Coordinator.Get(job, "p").Job!.NextByteOffset);
    }

    [Theory]
    [InlineData("project", "job_not_found")]
    [InlineData("chunk", "offset_conflict")]
    [InlineData("receipt", "needs_reconciliation")]
    [InlineData("source", "source_conflict")]
    [InlineData("bytes", "source_conflict")]
    [InlineData("offset", "chunk_conflict")]
    public async Task IncompatibleHistoryCannotShapeARemoteReadWithItsStoredChunkLength(string changed, string error)
    {
        using var jobs = new ImportJobTestSupport.Harness();
        using var sources = HttpObjectTestSupport.Catalog(); var source = sources.Find("source", "p")!; var snapshot = HttpObjectTestSupport.Snapshot(source, 9);
        Guid job = Guid.NewGuid(); Guid chunk = Guid.NewGuid();
        var state = ImportJobTestSupport.Ready() with
        {
            Id = job, ChunkId = chunk, SourceId = snapshot.SourceId, SourceBytes = 9,
            Contract = ImportJobTestSupport.Contracts().Find("p")
        };
        state = changed switch
        {
            "project" => state with { ProjectId = "other" },
            "chunk" => state with { ChunkId = Guid.NewGuid() },
            "receipt" => state with { Status = "processing", NextByteOffset = 0, AcceptedRecords = 0, Receipt = null },
            "source" => state with { SourceId = new string('B', 64) },
            "bytes" => state with { SourceBytes = 12 },
            "offset" => state with { ChunkOffset = 3, NextByteOffset = 6 },
            _ => state
        };
        jobs.Store.Save(state);
        var handler = Objects("{}\n{}\n{}\n");
        using var client = HttpObjectTestSupport.Client(handler, bytes: 4096);
        using var coordinator = Coordinator(jobs, client);
        var result = await coordinator.AppendAsync(source, snapshot, job, chunk, 0,
            _ => throw new InvalidOperationException("must not admit conflicting history"), CancellationToken.None);
        Assert.Equal(error, result.Error);
        Assert.All(handler.Requests, request => Assert.Equal("bytes=0-8", request.Headers.Range!.ToString()));
    }
}
