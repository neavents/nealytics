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

    [Fact]
    public async Task TruncateIfAllCommittedAsync_Truncates_WhenConditionTrue()
    {
        {
            await using var wal = new WriteAheadLogger(_options);
            var payload = new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" };
            await wal.AppendAsync(payload, CancellationToken.None);
            await wal.TruncateIfAllCommittedAsync(() => true);
        }

        var content = await File.ReadAllTextAsync(WalPath);
        content.Should().BeEmpty();
    }

    [Fact]
    public async Task TruncateIfAllCommittedAsync_Skips_WhenConditionFalse()
    {
        {
            await using var wal = new WriteAheadLogger(_options);
            var payload = new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" };
            await wal.AppendAsync(payload, CancellationToken.None);
            await wal.TruncateIfAllCommittedAsync(() => false);
        }

        var content = await File.ReadAllTextAsync(WalPath);
        content.Should().Contain("\"p\"", "WAL should not be truncated when condition is false");
    }

    [Fact]
    public async Task TruncateIfAllCommittedAsync_NoOps_WhenNothingAppended()
    {
        await using var wal = new WriteAheadLogger(_options);
        await wal.TruncateIfAllCommittedAsync(() => true);
        // Should not throw — just no-op
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
