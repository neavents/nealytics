using Microsoft.Extensions.Options;
using System.Text.Json;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;
using Nealytics.Engine.Infrastructure.Storage.Wal.Native;

namespace Nealytics.Engine.Tests.Native;

public sealed class NativeStartupIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nealytics-native-startup-" + Guid.NewGuid().ToString("N"));

    public NativeStartupIntegrationTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("managed")]
    [InlineData("io_uring")]
    [InlineData("auto")]
    public async Task BoundedGroupsPersistEveryRecordThroughOnePhysicalBarrier(string backend)
    {
        GlobalTelemetryPayload[] events = Enumerable.Range(0, 4).Select(_ => Event(Guid.NewGuid())).ToArray();
        TelemetryEngineOptions options = new()
        {
            WriteAheadLogDirectory = _directory,
            WalIoBackend = backend,
            WalMaxFlushRecords = 4,
            WalMaxPendingRecords = 8
        };
        await using (WriteAheadLogger wal = new(Options.Create(options)))
        {
            Assert.Equal(backend == "managed" ? "managed" : "io_uring", wal.Selection.Backend);
            WalBatchAppendResult result = await wal.AppendBatchAsync(events, CancellationToken.None);
            result.ThrowIfFailed();
            Assert.Equal(4, result.Count);
            Assert.Equal(1, wal.FlushGroupCount);
            Assert.Equal(4, wal.FlushedRecordCount);
            Assert.Equal(4, wal.UncommittedRecordCount);
            Assert.Equal(0, wal.PendingRecordCount);
            Assert.Equal(0, wal.PendingByteCount);
        }
        options.WalIoBackend = "managed";
        await using WriteAheadLogger restarted = new(Options.Create(options));
        Assert.Equal(events.Select(item => item.EventId),
            (await restarted.ReplayUncommittedAsync()).Select(item => item.EventId));
    }

    [Theory]
    [InlineData("managed")]
    [InlineData("io_uring")]
    [InlineData("auto")]
    public async Task ExactFramedByteBudgetsApplyToManagedAndNativeWriters(string backend)
    {
        GlobalTelemetryPayload payload = new()
        {
            ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e",
            MetadataJson = JsonSerializer.Serialize(new string('x', 2_200), TelemetryAotContext.Default.String)
        };
        byte[] expected = [.. JsonSerializer.SerializeToUtf8Bytes(payload, TelemetryAotContext.Default.GlobalTelemetryPayload), (byte)'\n'];
        TelemetryEngineOptions options = new()
        {
            WriteAheadLogDirectory = _directory,
            WalIoBackend = backend,
            WalMaxRecordBytes = expected.Length,
            WalMaxFlushBytes = expected.Length,
            WalMaxPendingBytes = expected.Length * 2
        };
        await using WriteAheadLogger wal = new(Options.Create(options));
        Assert.Equal(backend == "managed" ? "managed" : "io_uring", wal.Selection.Backend);
        await wal.AppendAsync(payload, CancellationToken.None);
        Assert.Equal(1, wal.UncommittedRecordCount);
        byte[] persisted = [.. JsonSerializer.SerializeToUtf8Bytes(payload, TelemetryAotContext.Default.GlobalTelemetryPayload), (byte)'\n'];
        Assert.NotEqual(Guid.Empty, payload.WalRecordId);
        Assert.Equal(expected.Length, persisted.Length);
        Assert.Equal(persisted, await File.ReadAllBytesAsync(Path.Combine(_directory, "telemetry_wal.log")));
    }

    [Theory]
    [InlineData("io_uring")]
    [InlineData("auto")]
    [InlineData("managed")]
    public async Task SelectedBackendSealsBeforeOpeningAndCanRestartWithManagedRecovery(string backend)
    {
        TelemetryEngineOptions options = new() { WriteAheadLogDirectory = _directory };
        Guid previous = Guid.NewGuid();
        await using (WriteAheadLogger original = new(Options.Create(options)))
        {
            await original.AppendAsync(Event(previous), CancellationToken.None);
        }
        string path = Path.Combine(_directory, "telemetry_wal.log");
        byte[] previousBytes = await File.ReadAllBytesAsync(path);
        options.WalIoBackend = backend;
        Guid next = Guid.NewGuid();
        await using (WriteAheadLogger current = new(Options.Create(options)))
        {
            Assert.Equal(backend == "managed" ? "managed" : "io_uring", current.Selection.Backend);
            Assert.Empty(current.Selection.FallbackReason);
            Assert.Equal(previous, Assert.Single(await current.ReplayUncommittedAsync()).EventId);
            await current.AppendAsync(Event(next), CancellationToken.None);
            Assert.Equal(previousBytes, await File.ReadAllBytesAsync(Path.Combine(_directory, "telemetry_wal.replay")));
            Assert.Contains(next.ToString(), await File.ReadAllTextAsync(path));
            Assert.DoesNotContain(previous.ToString(), await File.ReadAllTextAsync(path));
            await current.DeleteSealedSegmentAsync();
        }
        options.WalIoBackend = "managed";
        await using WriteAheadLogger recovered = new(Options.Create(options));
        Assert.Equal(next, Assert.Single(await recovered.ReplayUncommittedAsync()).EventId);
    }

    [Fact]
    public void StartupPreflightReportsARealFileBarrierFailureWithoutFallback()
    {
        TelemetryEngineOptions options = new() { WalIoBackend = "auto" };
        WalFileFactory factory = new(options);
        NativeWalException error = Assert.Throws<NativeWalException>(() => factory.Create("/dev/full", new WalLimits(options)));
        Assert.Equal("flush", error.Operation);
        Assert.Contains(error.ErrorCode, new[] { 22, 28 });
        Assert.False(WalFileFactory.CanFallBack(error));
    }

    [Fact]
    public async Task StartupHonorsTheExplicitlySelectedKernelPolicy()
    {
        bool denied = Environment.GetEnvironmentVariable("NEALYTICS_EXPECT_NATIVE_DENIED") == "1";
        TelemetryEngineOptions options = new() { WriteAheadLogDirectory = _directory, WalIoBackend = "io_uring" };
        if (denied)
        {
            NativeWalException error = Assert.Throws<NativeWalException>(() => new WriteAheadLogger(Options.Create(options)));
            Assert.Equal("create", error.Operation);
            Assert.Contains(error.ErrorCode, new[] { 1, 13 });
        }
        else
        {
            await using WriteAheadLogger explicitNative = new(Options.Create(options));
            Assert.Equal("io_uring", explicitNative.Selection.Backend);
        }
        options.WalIoBackend = "auto";
        await using WriteAheadLogger automatic = new(Options.Create(options));
        Assert.Equal(denied ? "managed" : "io_uring", automatic.Selection.Backend);
        if (denied)
        {
            Assert.Contains("Native WAL create failed with errno", automatic.Selection.FallbackReason);
        }
        else
        {
            Assert.Empty(automatic.Selection.FallbackReason);
        }
        await automatic.AppendAsync(Event(Guid.NewGuid()), CancellationToken.None);
        Assert.Equal(1, automatic.UncommittedRecordCount);
    }

    private static GlobalTelemetryPayload Event(Guid id) => new()
    {
        EventId = id,
        ProjectId = "p",
        TenantId = "t",
        SessionId = "s",
        EventType = "e"
    };

    public void Dispose() => Directory.Delete(_directory, true);
}
