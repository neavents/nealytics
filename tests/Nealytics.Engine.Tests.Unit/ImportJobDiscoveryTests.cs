using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nealytics.Engine.Features.ImportJobs;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ImportJobDiscoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-job-discovery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultAndExplicitQueriesPreserveOnlyBoundedOrderingHints()
    {
        Assert.Equal(new ImportJobListRequest(50, Guid.Empty), Parse());
        foreach (string size in new[] { "1", "50", "100", "001" })
        {
            var request = Parse(("limit", size), ("after", Id(3).ToString("D")), ("project", "other"));
            Assert.Equal(int.Parse(size), request!.Limit);
            Assert.Equal(Id(3), request.After);
        }
        Assert.Equal(Id(0xabcdef), Parse(("after", Id(0xabcdef).ToString("D").ToUpperInvariant()))!.After);
        Assert.Equal(50, Parse(("after", Id(3).ToString("D")))!.Limit);
        Assert.Equal(Guid.Empty, Parse(("limit", "2"))!.After);
    }

    [Theory]
    [InlineData("limit", "")]
    [InlineData("limit", "0")]
    [InlineData("limit", "101")]
    [InlineData("limit", "-1")]
    [InlineData("limit", "+1")]
    [InlineData("limit", " 1")]
    [InlineData("limit", "1 ")]
    [InlineData("limit", "1.0")]
    [InlineData("limit", "2147483648")]
    [InlineData("limit", "١")]
    [InlineData("after", "")]
    [InlineData("after", "00000000-0000-0000-0000-000000000000")]
    [InlineData("after", "00000001000040008000000000000001")]
    [InlineData("after", "{00000001-0000-4000-8000-000000000001}")]
    [InlineData("after", "0000000z-0000-4000-8000-000000000001")]
    [InlineData("after", " 00000001-0000-4000-8000-000000000001")]
    [InlineData("after", "00000001-0000-4000-8000-000000000001 ")]
    public void MalformedOrderingHintsNeverBecomeDefaults(string name, string value) => Assert.Null(Parse((name, value)));

    [Theory]
    [InlineData("limit")]
    [InlineData("after")]
    public void DuplicateNullAndEmptyQueryCollectionsNeverSelectAnArbitraryValue(string name)
    {
        string value = name == "limit" ? "1" : Id(1).ToString("D");
        foreach (StringValues values in new[] { new StringValues([value, value]), StringValues.Empty, new StringValues((string?)null), new StringValues(new string[] { null! }) })
            Assert.Null(ImportJobListRequest.Parse(new QueryCollection(new Dictionary<string, StringValues> { [name] = values })));
    }

    [Fact]
    public void PagesFilterProjectBeforeOrderingAndUseOnlyAnExclusiveCursor()
    {
        using var store = Store();
        foreach (int id in new[] { 5, 1, 3 }) store.Save(State(id));
        foreach (int id in new[] { 2, 4 }) store.Save(State(id, "other"));
        var first = store.List("p", 2, Guid.Empty);
        Assert.Equal(new[] { Id(1), Id(3) }, first.Jobs.Select(job => job.JobId));
        Assert.Equal(Id(3), first.NextAfter);
        var last = store.List("p", 2, first.NextAfter!.Value);
        Assert.Equal(State(5).Response(), Assert.Single(last.Jobs));
        Assert.Null(last.NextAfter);
        Assert.Equal(new[] { Id(3), Id(5) }, store.List("p", 2, Id(2)).Jobs.Select(job => job.JobId));
        Assert.Equal(Id(5), Assert.Single(store.List("p", 2, Id(4)).Jobs).JobId);
        Assert.Empty(store.List("P", 2, Guid.Empty).Jobs);
        Assert.Empty(store.List("missing", 2, Guid.Empty).Jobs);
        var beyond = store.List("p", 2, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"));
        Assert.Empty(beyond.Jobs); Assert.Null(beyond.NextAfter);
    }

    [Fact]
    public void EmptyExactMaximumAndOverflowPagesHavePreciseContinuationAndNoWrites()
    {
        using var store = Store();
        var empty = store.List("p", 100, Guid.Empty);
        Assert.Empty(empty.Jobs); Assert.Null(empty.NextAfter);
        for (int id = 101; id > 0; id--) store.Save(State(id));
        var files = System.IO.Directory.GetFiles(Path.Combine(_directory, "import-jobs"), "*.json").ToDictionary(path => path, File.ReadAllBytes);
        var page = store.List("p", 100, Guid.Empty);
        Assert.Equal(Enumerable.Range(1, 100).Select(Id), page.Jobs.Select(job => job.JobId));
        Assert.Equal(Id(100), page.NextAfter);
        Assert.Equal(Id(101), Assert.Single(store.List("p", 100, page.NextAfter!.Value).Jobs).JobId);
        var exact = store.List("p", 100, Id(1)); Assert.Equal(100, exact.Jobs.Length); Assert.Null(exact.NextAfter);
        var singleton = store.List("p", 1, Id(100)); Assert.Single(singleton.Jobs); Assert.Null(singleton.NextAfter);
        var more = store.List("p", 1, Guid.Empty); Assert.Equal(Id(1), more.NextAfter);
        foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(files.Keys.Order(), System.IO.Directory.GetFiles(Path.Combine(_directory, "import-jobs"), "*.json").Order());
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List("p", 0, Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List("p", 101, Guid.Empty));
    }

    [Fact]
    public void PagesAreDetachedFromLaterUpdatesAndCallerArrayChanges()
    {
        using var store = Store(); var ready = State(3); store.Save(ready);
        var snapshot = store.List("p", 1, Guid.Empty);
        store.Save(ready with { Status = "processing", NextByteOffset = 0, AcceptedRecords = 0, Receipt = null });
        Assert.Equal("ready", Assert.Single(snapshot.Jobs).State);
        Assert.Equal("processing", Assert.Single(store.List("p", 1, Guid.Empty).Jobs).State);
        snapshot.Jobs[0] = State(7).Response();
        Assert.Equal(Id(3), Assert.Single(store.List("p", 1, Guid.Empty).Jobs).JobId);
        store.MarkUncertain(ready.Id);
        Assert.Equal("needs_reconciliation", Assert.Single(store.List("p", 1, Guid.Empty).Jobs).State);
        string json = JsonSerializer.Serialize(snapshot, TelemetryAotContext.Default.ImportJobPage);
        var restored = JsonSerializer.Deserialize(json, TelemetryAotContext.Default.ImportJobPage)!;
        Assert.Equal(snapshot.Jobs, restored.Jobs); Assert.Equal(snapshot.NextAfter, restored.NextAfter);
    }

    [Fact]
    public void RestartDiscoveryUsesRecoveredStatesAndFailureGuardsNeverReturnPartialPages()
    {
        using (var store = Store())
        {
            store.Save(State(1));
            store.Save(ImportJobTestSupport.Pending(ImportJobTestSupport.Request(job: Id(2))));
        }
        using (var recovered = Store())
        {
            Assert.Equal(new[] { "ready", "needs_reconciliation" }, recovered.List("p", 10, Guid.Empty).Jobs.Select(job => job.State));
            var failing = State(3);
            File.WriteAllText(Path.Combine(_directory, "import-jobs", failing.Id.ToString("N") + ".tmp"), "private failure");
            Assert.Throws<IOException>(() => recovered.Save(failing));
            Assert.Throws<IOException>(() => recovered.List("p", 10, Guid.Empty));
            recovered.Dispose();
            Assert.Throws<ObjectDisposedException>(() => recovered.List("p", 10, Guid.Empty));
        }
    }

    [Fact]
    public async Task ConcurrentProgressUpdatesLeaveWholeDetachedPageRecords()
    {
        using var store = Store(); var ready = State(1); store.Save(ready);
        var before = store.List("p", 1, Guid.Empty);
        var writer = Task.Run(() =>
        {
            for (int iteration = 0; iteration < 8; iteration++)
                store.Save(iteration % 2 == 0 ? ready : ready with
                { Status = "processing", NextByteOffset = 0, AcceptedRecords = 0, Receipt = null });
        });
        var readers = Enumerable.Range(0, 24).Select(_ => Task.Run(() => store.List("p", 1, Guid.Empty))).ToArray();
        var pages = await Task.WhenAll(readers); await writer;
        foreach (var page in pages)
        {
            var job = Assert.Single(page.Jobs); Assert.Equal(ready.Id, job.JobId); Assert.Null(page.NextAfter);
            Assert.Contains(job.State, new[] { "ready", "processing" });
            Assert.Equal(job.State == "ready" ? 3 : 0, job.NextByteOffset);
            Assert.Equal(job.State == "ready" ? 1 : 0, job.AcceptedRecords);
            Assert.Equal(job.State == "ready", job.Receipt is not null);
        }
        Assert.Equal(ready.Response(), Assert.Single(before.Jobs));
    }

    [Fact]
    public void LivePagesRequireRestartingDiscoveryToFindLaterInsertsBelowTheCursor()
    {
        using var store = Store(); store.Save(State(1)); store.Save(State(3));
        var first = store.List("p", 1, Guid.Empty); store.Save(State(0));
        Assert.Equal(Id(3), Assert.Single(store.List("p", 1, first.NextAfter!.Value).Jobs).JobId);
        Assert.Equal(Id(0), Assert.Single(store.List("p", 1, Guid.Empty).Jobs).JobId);
    }

    private ImportJobStore Store() => new(new ImportJobJournal(_directory), 4096);
    private static Guid Id(int value) => Guid.Parse(value.ToString("x8") + "-0000-4000-8000-000000000001");
    private static ImportJobState State(int value, string project = "p") => ImportJobTestSupport.Ready(
        ImportJobTestSupport.Request(job: Id(value), chunk: Id(value + 1000))) with { ProjectId = project };
    private static ImportJobListRequest? Parse(params (string Name, string Value)[] values) =>
        ImportJobListRequest.Parse(new QueryCollection(values.ToDictionary(value => value.Name, value => new StringValues(value.Value))));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_directory)) System.IO.Directory.Delete(_directory, true);
    }
}
