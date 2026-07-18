using FluentAssertions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Tests.Shared.Base;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

public class WriteAheadLoggerTests : UnitTestBase, IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly IOptions<TelemetryEngineOptions> _options;
    private readonly TelemetryEngineOptions _engineOptions;

    public WriteAheadLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nealytics_wal_test_{Guid.NewGuid():N}");
        _engineOptions = new TelemetryEngineOptions { WriteAheadLogDirectory = _tempDir };
        _options = Substitute.For<IOptions<TelemetryEngineOptions>>();
        _options.Value.Returns(_engineOptions);
    }

    public async ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        await Task.CompletedTask;
    }

    private string WalPath => Path.Combine(_tempDir, "telemetry_wal.log");

    private async Task WriteAndCloseAsync(params GlobalTelemetryPayload[] payloads)
    {
        await using var wal = new WriteAheadLogger(_options);
        foreach (var p in payloads)
            await wal.AppendAsync(p, CancellationToken.None);
    }

    [Fact]
    public async Task Constructor_CreatesDirectory_IfNotExists()
    {
        Directory.Exists(_tempDir).Should().BeFalse("temp dir should not exist yet");

        await using var wal = new WriteAheadLogger(_options);

        Directory.Exists(_tempDir).Should().BeTrue("WAL should create dir if missing");
    }

    [Fact]
    public async Task AppendAsync_WritesEventToFile()
    {
        var payload = new GlobalTelemetryPayload
            { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" };
        await WriteAndCloseAsync(payload);

        File.Exists(WalPath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(WalPath);
        content.Should().Contain("\"projectId\":\"p\"");
    }

    [Fact]
    public async Task AppendAsync_WritesMultipleEvents_AsSeparateLines()
    {
        var p1 = new GlobalTelemetryPayload { ProjectId = "p1", TenantId = "t", SessionId = "s", EventType = "e" };
        var p2 = new GlobalTelemetryPayload { ProjectId = "p2", TenantId = "t", SessionId = "s", EventType = "e" };
        await WriteAndCloseAsync(p1, p2);

        var lines = await File.ReadAllLinesAsync(WalPath);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("\"p1\"");
        lines[1].Should().Contain("\"p2\"");
    }

    [Fact]
    public async Task TruncateAsync_RemovesAllContent()
    {
        {
            await using var wal = new WriteAheadLogger(_options);
            var payload = new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" };
            await wal.AppendAsync(payload, CancellationToken.None);
            await wal.TruncateAsync();
        }

        var content = await File.ReadAllTextAsync(WalPath);
        content.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplayUncommittedAsync_ReturnsAppendedEvents()
    {
        var p1 = new GlobalTelemetryPayload { ProjectId = "p1", TenantId = "t1", SessionId = "s", EventType = "e" };
        var p2 = new GlobalTelemetryPayload { ProjectId = "p2", TenantId = "t2", SessionId = "s", EventType = "e" };
        await WriteAndCloseAsync(p1, p2);

        await using var wal2 = new WriteAheadLogger(_options);
        var recovered = await wal2.ReplayUncommittedAsync();

        recovered.Should().HaveCount(2);
        recovered[0].ProjectId.Should().Be("p1");
        recovered[1].ProjectId.Should().Be("p2");
    }

    [Fact]
    public async Task ReplayUncommittedAsync_ReturnsEmpty_WhenFileDoesNotExist()
    {
        _engineOptions.WriteAheadLogDirectory = Path.Combine(_tempDir, Guid.NewGuid().ToString("N"));
        await using var wal = new WriteAheadLogger(_options);

        var recovered = await wal.ReplayUncommittedAsync();

        recovered.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplayUncommittedAsync_ReturnsEmpty_WhenFileIsEmpty()
    {
        await using var wal = new WriteAheadLogger(_options);

        var recovered = await wal.ReplayUncommittedAsync();

        recovered.Should().BeEmpty();
    }

    [Fact]
    public async Task ReplayUncommittedAsync_SkipsCorruptLines()
    {
        var payload = new GlobalTelemetryPayload { ProjectId = "good", TenantId = "t", SessionId = "s", EventType = "e" };
        await WriteAndCloseAsync(payload);

        await File.AppendAllTextAsync(WalPath, "not-valid-json\n");

        await using var wal2 = new WriteAheadLogger(_options);
        var recovered = await wal2.ReplayUncommittedAsync();

        recovered.Should().HaveCount(1);
        recovered[0].ProjectId.Should().Be("good");
    }

    private static GlobalTelemetryPayload Event(string projectId = "p") =>
        new GlobalTelemetryPayload { ProjectId = projectId, TenantId = "t", SessionId = "s", EventType = "e" };

    [Fact]
    public async Task AcknowledgeCommitAsync_Truncates_WhenAllAppendedRecordsCommitted()
    {
        {
            await using var wal = new WriteAheadLogger(_options);
            await wal.AppendAsync(Event(), CancellationToken.None);
            await wal.AppendAsync(Event(), CancellationToken.None);
            wal.UncommittedRecordCount.Should().Be(2);

            await wal.AcknowledgeCommitAsync(2);

            wal.UncommittedRecordCount.Should().Be(0);
        }

        var content = await File.ReadAllTextAsync(WalPath);
        content.Should().BeEmpty("WAL is safe to truncate once every appended record is committed");
    }

    [Fact]
    public async Task AcknowledgeCommitAsync_PreservesWal_WhenUncommittedRecordsRemain()
    {
        // This is the core of the fix: a batch of already-published events commits,
        // but a later event (still only in the WAL) MUST survive so a crash cannot lose it.
        {
            await using var wal = new WriteAheadLogger(_options);
            await wal.AppendAsync(Event("committed-1"), CancellationToken.None);
            await wal.AppendAsync(Event("committed-2"), CancellationToken.None);
            // Event that is durably in the WAL but not part of the committed batch yet
            // (mirrors the append-before-publish window in the ingestion endpoint).
            await wal.AppendAsync(Event("in-flight"), CancellationToken.None);

            // Only the two already-published events are acknowledged as committed.
            await wal.AcknowledgeCommitAsync(2);

            wal.UncommittedRecordCount.Should().Be(1);
        }

        var content = await File.ReadAllTextAsync(WalPath);
        content.Should().Contain("\"in-flight\"",
            "an uncommitted event must not be dropped from the WAL when an unrelated batch commits");
    }

    [Fact]
    public async Task AcknowledgeCommitAsync_Truncates_OnceRemainingRecordsAlsoCommit()
    {
        {
            await using var wal = new WriteAheadLogger(_options);
            await wal.AppendAsync(Event(), CancellationToken.None);
            await wal.AppendAsync(Event(), CancellationToken.None);
            await wal.AppendAsync(Event(), CancellationToken.None);

            await wal.AcknowledgeCommitAsync(2);
            wal.UncommittedRecordCount.Should().Be(1);

            await wal.AcknowledgeCommitAsync(1);
            wal.UncommittedRecordCount.Should().Be(0);
        }

        var content = await File.ReadAllTextAsync(WalPath);
        content.Should().BeEmpty();
    }

    [Fact]
    public async Task AcknowledgeCommitAsync_NoOps_WhenNothingAppended()
    {
        await using var wal = new WriteAheadLogger(_options);
        await wal.AcknowledgeCommitAsync(0);
        await wal.AcknowledgeCommitAsync(5);
        wal.UncommittedRecordCount.Should().Be(0);
    }

    [Fact]
    public async Task Startup_SealsExistingLog_AndNewAppendsSurviveRecoveryTruncation()
    {
        // Process 1 crashed with an un-flushed event in the WAL.
        await WriteAndCloseAsync(Event("crashed"));

        // Process 2 boots: the old log is sealed for replay, a fresh active log is opened,
        // and a NEW event arrives concurrently while recovery is still in progress.
        {
            await using var process2 = new WriteAheadLogger(_options);
            process2.HasSealedSegment.Should().BeTrue("the pre-existing WAL must be sealed for recovery");

            await process2.AppendAsync(Event("live"), CancellationToken.None);

            var recovered = await process2.ReplayUncommittedAsync();
            recovered.Should().ContainSingle().Which.ProjectId.Should().Be("crashed");

            // Recovery finishes and deletes ONLY the sealed segment.
            await process2.DeleteSealedSegmentAsync();
            process2.HasSealedSegment.Should().BeFalse();

            // The concurrently-appended live event was never committed, so it stays in the active log.
            process2.UncommittedRecordCount.Should().Be(1);
        }
        // process2 crashes (dispose flushes the active log to disk).

        // Process 3 boots and must recover the live event that process 2 never committed.
        await using var process3 = new WriteAheadLogger(_options);
        var recoveredAfterSecondCrash = await process3.ReplayUncommittedAsync();

        recoveredAfterSecondCrash.Should().ContainSingle(
            "the live event appended during recovery must not be lost across a second crash")
            .Which.ProjectId.Should().Be("live");
    }

    [Fact]
    public async Task DeleteSealedSegmentAsync_RemovesReplayFile()
    {
        await WriteAndCloseAsync(Event());
        await using var wal = new WriteAheadLogger(_options);

        wal.HasSealedSegment.Should().BeTrue();
        await wal.DeleteSealedSegmentAsync();
        wal.HasSealedSegment.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteSealedSegmentAsync_NoOps_WhenNoSealedSegment()
    {
        await using var wal = new WriteAheadLogger(_options);
        wal.HasSealedSegment.Should().BeFalse();

        await wal.DeleteSealedSegmentAsync();

        wal.HasSealedSegment.Should().BeFalse();
    }

    [Fact]
    public async Task Startup_WithLeftoverSealedSegment_MergesActiveLogIntoIt()
    {
        Directory.CreateDirectory(_tempDir);
        string sealedPath = Path.Combine(_tempDir, "telemetry_wal.replay");

        await File.WriteAllTextAsync(sealedPath,
            "{\"eventId\":\"" + Guid.NewGuid() + "\",\"projectId\":\"from-sealed\",\"tenantId\":\"t\",\"sessionId\":\"s\",\"eventType\":\"e\",\"metadataJson\":\"{}\",\"timestamp\":\"2026-07-10T00:00:00Z\"}\n");

        await File.WriteAllTextAsync(WalPath,
            "{\"eventId\":\"" + Guid.NewGuid() + "\",\"projectId\":\"from-active\",\"tenantId\":\"t\",\"sessionId\":\"s\",\"eventType\":\"e\",\"metadataJson\":\"{}\",\"timestamp\":\"2026-07-10T00:00:00Z\"}\n");

        await using var wal = new WriteAheadLogger(_options);
        var recovered = await wal.ReplayUncommittedAsync();

        recovered.Should().HaveCount(2, "a leftover sealed segment must be merged with the active log, not dropped");
        recovered.Select(r => r.ProjectId).Should().Contain(new[] { "from-sealed", "from-active" });
        File.Exists(WalPath).Should().BeTrue();
        new FileInfo(WalPath).Length.Should().Be(0, "the active log is consumed into the sealed segment");
    }

    [Fact]
    public async Task ReplayUncommittedAsync_SkipsBlankLines()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(WalPath,
            "{\"eventId\":\"" + Guid.NewGuid() + "\",\"projectId\":\"a\",\"tenantId\":\"t\",\"sessionId\":\"s\",\"eventType\":\"e\",\"metadataJson\":\"{}\",\"timestamp\":\"2026-07-10T00:00:00Z\"}\n" +
            "\n" +
            "{\"eventId\":\"" + Guid.NewGuid() + "\",\"projectId\":\"b\",\"tenantId\":\"t\",\"sessionId\":\"s\",\"eventType\":\"e\",\"metadataJson\":\"{}\",\"timestamp\":\"2026-07-10T00:00:00Z\"}\n");

        await using var wal = new WriteAheadLogger(_options);
        var recovered = await wal.ReplayUncommittedAsync();

        recovered.Should().HaveCount(2, "blank lines between records must be skipped, not treated as corrupt");
    }

    [Fact]
    public async Task ConcurrentAppends_AllCounted_AndDurable_UnderContention()
    {
        {
            await using var wal = new WriteAheadLogger(_options);

            var tasks = new Task[200];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = wal.AppendAsync(Event($"p{i}"), CancellationToken.None);
            }
            await Task.WhenAll(tasks);

            wal.UncommittedRecordCount.Should().Be(200,
                "every concurrent append must be counted exactly once");
        }
        // Dispose flushes the active log; a fresh instance replays it to prove durability.

        await using var reopened = new WriteAheadLogger(_options);
        var recovered = await reopened.ReplayUncommittedAsync();
        recovered.Should().HaveCount(200, "every concurrent append must produce exactly one durable record");
    }

    [Fact]
    public async Task DisposeAsync_FlushesAndClosesFileStream()
    {
        await WriteAndCloseAsync(
            new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" });

        File.Exists(WalPath).Should().BeTrue();
    }

    [Fact]
    public async Task AppendAsync_Throws_WhenCancelled()
    {
        await using var wal = new WriteAheadLogger(_options);
        var payload = new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => wal.AppendAsync(payload, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
