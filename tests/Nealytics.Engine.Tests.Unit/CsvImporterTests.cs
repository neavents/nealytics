using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Nealytics.Engine.Features.ImportCsv;
using Nealytics.Engine.Infrastructure.Serialization;
using static Nealytics.Engine.Tests.Unit.NdjsonRecordReaderTests;
using Harness = Nealytics.Engine.Tests.Unit.NdjsonImporterTests.Harness;

namespace Nealytics.Engine.Tests.Unit;

[Collection("Recovery accounting")]
public sealed class CsvImporterTests
{
    [Theory]
    [InlineData("managed", false)]
    [InlineData("custom", false)]
    [InlineData("managed", true)]
    [InlineData("custom", true)]
    public async Task AdmittedRecordsOutliveCallerDisconnectButRespectApplicationShutdown(string path, bool shutdown)
    {
        await using var harness = new Harness(path, capacity: 1);
        await harness.Broker.PublishAsync(new GlobalTelemetryPayload(), CancellationToken.None);
        using var caller = new CancellationTokenSource();
        var reader = Reader(CsvHttpTests.Header + CsvHttpTests.Row());
        var importing = harness.CsvImporter.IngestAsync(reader, Mapping(), harness.Policy, DateTime.UtcNow, caller.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (harness.Wal.UncommittedRecordCount != 1) await Task.Delay(1, deadline.Token);
        if (shutdown) harness.Stopping.Cancel();
        else
        {
            caller.Cancel();
            Assert.False(importing.IsCompleted);
            await harness.Broker.Reader.ReadAsync(deadline.Token);
            Assert.Equal(Guid.Parse(KafkaTestSupport.Identity), (await harness.Broker.Reader.ReadAsync(deadline.Token)).EventId);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importing.WaitAsync(deadline.Token));
        Assert.Equal(1, harness.Wal.UncommittedRecordCount);
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteOrFlushFaultCannotProduceASuccessReceiptOrPublishAnUndurableRecord(bool write)
    {
        var fault = new IOException("injected storage failure");
        await using var harness = new Harness(flush: write ? null : () => ValueTask.FromException(fault),
            append: write ? _ => ValueTask.FromException(fault) : null);
        var reader = Reader(CsvHttpTests.Header + CsvHttpTests.Row());
        Assert.Same(fault, await Assert.ThrowsAsync<IOException>(() => harness.CsvImporter.IngestAsync(
            reader, Mapping(), harness.Policy, DateTime.UtcNow, default)));
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.False(harness.Broker.Reader.TryRead(out _));
        Assert.Equal(1, reader.Completions);
    }

    [Theory]
    [InlineData(413, true)]
    [InlineData(400, false)]
    public async Task OnlyTheBodyLimitErrorBecomesAnOversizeReceipt(int status, bool receipt)
    {
        await using var harness = new Harness();
        var reader = new ProbeReader(new FaultReader(new Microsoft.AspNetCore.Http.BadHttpRequestException("injected body failure", status)));
        var task = harness.CsvImporter.IngestAsync(reader, Mapping(), harness.Policy, DateTime.UtcNow, default);
        if (receipt)
        {
            var result = await task;
            Assert.Equal(new CsvImportResponse(0, 0, "body_too_large"), result);
            Assert.Equal(413, result.StatusCode);
        }
        else Assert.Equal(status, (await Assert.ThrowsAsync<Microsoft.AspNetCore.Http.BadHttpRequestException>(() => task)).StatusCode);
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task CancellationAfterHeaderAndDataParsingPreventsAdmission()
    {
        await using var harness = new Harness();
        using var caller = new CancellationTokenSource();
        int advances = 0;
        var reader = new ProbeReader(new SegmentedReader(Encoding.UTF8.GetBytes(CsvHttpTests.Header + CsvHttpTests.Row())))
        {
            OnAdvance = () => { if (++advances == 2) caller.Cancel(); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.CsvImporter.IngestAsync(
            reader, Mapping(), harness.Policy, DateTime.UtcNow, caller.Token));
        Assert.Equal(0, harness.Wal.UncommittedRecordCount);
        Assert.False(harness.Broker.Reader.TryRead(out _));
        Assert.Equal(1, reader.Completions);
    }

    [Fact]
    public async Task DeclaredMappingCannotBypassDimensionAndMeasureSanitizationBeforeSerialization()
    {
        await using var harness = new Harness();
        var declaration = CsvMappingTests.Options();
        declaration.Fields["dimensions.tag"] = new() { Path = ["tag"] };
        declaration.Fields["dimensions.undeclared"] = new() { Path = ["hidden"] };
        declaration.Fields["measures.amount"] = new() { Path = ["amount"] };
        declaration.Fields["measures.other"] = new() { Path = ["hidden"] };
        string body = "id,time,tenant,tag,amount,hidden\n" + KafkaTestSupport.Identity + ",2026-01-01T00:00:00Z,t,x,7,9";
        var receipt = await harness.CsvImporter.IngestAsync(Reader(body), new CsvProjectMapping(declaration), harness.Policy, DateTime.UtcNow, default);
        Assert.True(receipt.Complete);
        var payload = System.Text.Json.JsonSerializer.Deserialize(Assert.Single(harness.File.Records),
            Nealytics.Engine.Infrastructure.Serialization.TelemetryAotContext.Default.GlobalTelemetryPayload)!;
        Assert.Equal(new KeyValuePair<string, string>("tag", "x"), Assert.Single(payload.Dimensions!));
        Assert.Equal(new KeyValuePair<string, string>("amount", "7"), Assert.Single(payload.Measures!));
    }

    private static ProbeReader Reader(string body) => new(new SegmentedReader(Encoding.UTF8.GetBytes(body)));
    private static CsvProjectMapping Mapping()
    {
        var declaration = CsvMappingTests.Options();
        declaration.Fields["dimensions.tag"] = new() { Path = ["tag"] };
        return new(declaration);
    }

    private sealed class FaultReader(Exception fault) : PipeReader
    {
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default) => ValueTask.FromException<ReadResult>(fault);
        public override void AdvanceTo(SequencePosition consumed) { }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
        public override void Complete(Exception? exception = null) { }
        public override void CancelPendingRead() { }
        public override bool TryRead(out ReadResult result) => throw fault;
    }
}
