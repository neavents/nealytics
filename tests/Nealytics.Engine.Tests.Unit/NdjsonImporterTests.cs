using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.ImportNdjson;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Recovery accounting")]
public sealed class NdjsonImporterTests
{
    [Fact]
    public async Task ReceiptsWaitForTheActualDurabilityBarrier()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = new Harness(flush: async () => { entered.TrySetResult(); await release.Task; });
        var reader = Reader(NdjsonRecordReaderTests.Event());
        Task<NdjsonImportResponse> importing = harness.Importer.IngestAsync(reader, harness.Policy, DateTime.UtcNow, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(importing.IsCompleted);
        Assert.False(harness.Broker.Reader.TryRead(out _));
        release.SetResult();
        var response = await importing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Complete);
        Assert.Equal(1, response.AcceptedRecords);
        Assert.Equal(Encoding.UTF8.GetByteCount(NdjsonRecordReaderTests.Event()), response.NextByteOffset);
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.True(harness.Broker.Reader.TryRead(out var stored));
        Assert.Equal(Guid.Parse(NdjsonRecordReaderTests.Identity), stored!.EventId);
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task AdmittedImportsOwnTheirHandoffAfterCallerDisconnectButShutdownStillCancels(string path, bool shutdown)
    {
        await using var harness = new Harness(path, capacity: 1);
        await harness.Broker.PublishAsync(new GlobalTelemetryPayload(), CancellationToken.None);
        using CancellationTokenSource caller = new();
        var reader = Reader(NdjsonRecordReaderTests.Event());
        Task<NdjsonImportResponse> importing = harness.Importer.IngestAsync(reader, harness.Policy, DateTime.UtcNow, caller.Token);
        await WaitFor(() => harness.Wal.UncommittedRecordCount == 1);
        if (shutdown)
        {
            harness.Stopping.Cancel();
        }
        else
        {
            caller.Cancel();
            Assert.False(importing.IsCompleted);
            await harness.Broker.Reader.ReadAsync();
            Assert.Equal(Guid.Parse(NdjsonRecordReaderTests.Identity), (await harness.Broker.Reader.ReadAsync()).EventId);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, reader.Completions);
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
    }

    [Fact]
    public async Task CancellationAfterParsingButBeforeAdmissionCannotPersistTheRecord()
    {
        await using var harness = new Harness();
        using CancellationTokenSource caller = new();
        var reader = new NdjsonRecordReaderTests.ProbeReader(new NdjsonRecordReaderTests.SegmentedReader(Encoding.UTF8.GetBytes(NdjsonRecordReaderTests.Event())))
        {
            OnAdvance = caller.Cancel
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Importer.IngestAsync(reader, harness.Policy, DateTime.UtcNow, caller.Token));
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.False(harness.Broker.Reader.TryRead(out _));
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedStorageNeverProducesASuccessReceiptOrPublishesAnUndurableRecord(bool writeFailure)
    {
        IOException failure = new("injected persistence failure");
        await using var harness = new Harness(flush: writeFailure ? null : () => ValueTask.FromException(failure),
            append: writeFailure ? _ => ValueTask.FromException(failure) : null);
        var reader = Reader(NdjsonRecordReaderTests.Event());
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => harness.Importer.IngestAsync(reader, harness.Policy, DateTime.UtcNow, CancellationToken.None)));
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.False(harness.Broker.Reader.TryRead(out _));
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData(413, true)]
    [InlineData(400, false)]
    public async Task OnlyARealBodyLimitFailureBecomesAnOversizeReceipt(int status, bool receipt)
    {
        await using var harness = new Harness();
        var reader = new NdjsonRecordReaderTests.ProbeReader(new FaultReader(new BadHttpRequestException("body failure", status)));
        var task = harness.Importer.IngestAsync(reader, harness.Policy, DateTime.UtcNow, CancellationToken.None);
        if (receipt)
        {
            var result = await task;
            Assert.Equal(new NdjsonImportResponse(0, 0, "body_too_large"), result);
            Assert.Equal(413, result.StatusCode);
        }
        else
        {
            Assert.Equal(status, (await Assert.ThrowsAsync<BadHttpRequestException>(() => task)).StatusCode);
        }
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task SanitizationOccursBeforeDurableSerialization()
    {
        await using var harness = new Harness();
        string body = NdjsonRecordReaderTests.Event(extra: ",\"dimensions\":{\"tag\":\"x\",\"undeclared\":\"hidden\"},\"measures\":{\"amount\":\"7\",\"other\":\"9\"}");
        var response = await harness.Importer.IngestAsync(Reader(body), harness.Policy, DateTime.UtcNow, CancellationToken.None);
        Assert.True(response.Complete);
        var persisted = JsonSerializer.Deserialize(Assert.Single(harness.File.Records), TelemetryAotContext.Default.GlobalTelemetryPayload)!;
        Assert.Equal("tag", Assert.Single(persisted.Dimensions!).Key);
        Assert.Equal("x", persisted.Dimensions!["tag"]);
        Assert.Equal("amount", Assert.Single(persisted.Measures!).Key);
        Assert.Equal("7", persisted.Measures!["amount"]);
    }

    private static NdjsonRecordReaderTests.ProbeReader Reader(string text) => new(new NdjsonRecordReaderTests.SegmentedReader(Encoding.UTF8.GetBytes(text)));
    private static async Task WaitFor(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, deadline.Token);
    }

    internal sealed class Harness : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-import-unit-" + Guid.NewGuid().ToString("N"));
        public Harness(string path = "managed", int capacity = 8, Func<ValueTask>? flush = null, Func<ReadOnlyMemory<byte>, ValueTask>? append = null)
        {
            var options = new TelemetryEngineOptions
            {
                WriteAheadLogDirectory = _directory, MemoryChannelCapacity = capacity, IngestionExecutionPath = path,
                Dimensions = [new() { Name = "tag" }], Measures = [new() { Name = "amount", Type = "Float64" }]
            };
            var configured = Options.Create(options);
            var dimensions = new DimensionRegistry(options);
            var measures = new MeasureRegistry(options, dimensions);
            File = new FileBoundary(flush, append);
            Wal = new WriteAheadLogger(configured, File);
            Broker = new TelemetryChannelBroker(configured);
            var lifetime = Substitute.For<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Returns(Stopping.Token);
            var dispatcher = new DurableIngestDispatcher(Wal, Broker, new IngestionRequestReader(configured), lifetime);
            var gate = new IngestGate(options, measures);
            var dimensionSanitizer = new DimensionSanitizer(dimensions, NullLogger<DimensionSanitizer>.Instance);
            var measureSanitizer = new MeasureSanitizer(measures, NullLogger<MeasureSanitizer>.Instance);
            var limits = NdjsonImportLimits.Create(options);
            Importer = new NdjsonImporter(dispatcher, gate, dimensionSanitizer, measureSanitizer, limits, lifetime);
            CsvImporter = new Nealytics.Engine.Features.ImportCsv.CsvImporter(
                dispatcher, gate, dimensionSanitizer, measureSanitizer, limits, lifetime);
        }
        public IngestionKeyPolicy Policy { get; } = new(IngestionKeyScope.Server, "p", EventTypePattern.None);
        public WriteAheadLogger Wal { get; }
        public TelemetryChannelBroker Broker { get; }
        public NdjsonImporter Importer { get; }
        public Nealytics.Engine.Features.ImportCsv.CsvImporter CsvImporter { get; }
        public FileBoundary File { get; }
        public CancellationTokenSource Stopping { get; } = new();
        public async ValueTask DisposeAsync()
        {
            Stopping.Cancel();
            await Wal.DisposeAsync();
            Stopping.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    internal sealed class FileBoundary(Func<ValueTask>? flush, Func<ReadOnlyMemory<byte>, ValueTask>? append) : IWalFile
    {
        public List<byte[]> Records { get; } = [];
        public async ValueTask AppendAsync(ReadOnlyMemory<byte> record)
        {
            Records.Add(record.ToArray());
            if (append is not null) await append(record);
        }
        public ValueTask FlushAsync() => flush?.Invoke() ?? ValueTask.CompletedTask;
        public ValueTask TruncateAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FaultReader(Exception failure) : PipeReader
    {
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromException<ReadResult>(failure);
        public override void AdvanceTo(SequencePosition consumed) { }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
        public override void Complete(Exception? exception = null) { }
        public override void CancelPendingRead() { }
        public override bool TryRead(out ReadResult result) => throw failure;
    }
}
