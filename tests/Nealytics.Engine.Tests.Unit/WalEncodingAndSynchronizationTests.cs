using System.Text.Json;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Infrastructure.Storage.Wal;

namespace Nealytics.Engine.Tests.Unit;

public sealed class WalEncodingAndSynchronizationTests
{
    [Fact]
    public void GeneratedRecordEncodingUsesTheDeclaredFastWriterSettings()
    {
        Assert.True(WalRecordEncoding.WriterOptions.SkipValidation);
        Assert.False(WalRecordEncoding.WriterOptions.Indented);
        using WalRecordBuffer buffer = new(16_384);
        GlobalTelemetryPayload payload = new()
        {
            ProjectId = "p",
            TenantId = "t",
            SessionId = "s",
            EventType = "e",
            MetadataJson = "{\"value\":\"λ\"}"
        };
        WalRecordEncoding.Serialize(payload, buffer);
        ReadOnlyMemory<byte> bytes = buffer.WrittenMemory;
        Assert.Equal((byte)'\n', bytes.Span[^1]);
        GlobalTelemetryPayload decoded = JsonSerializer.Deserialize(
            bytes.Span, TelemetryAotContext.Default.GlobalTelemetryPayload)!;
        Assert.Equal(payload.EventId, decoded.EventId);
        Assert.Equal(payload.MetadataJson, decoded.MetadataJson);
    }

    [Fact]
    public async Task FileAndSerializationGatesAreIndependentAndExclusive()
    {
        using WalSynchronization synchronization = new();
        Assert.True(await synchronization.File.WaitAsync(0));
        Assert.True(await synchronization.Serialization.WaitAsync(0));
        Assert.False(await synchronization.File.WaitAsync(0));
        Assert.False(await synchronization.Serialization.WaitAsync(0));
        synchronization.File.Release();
        synchronization.Serialization.Release();
        Assert.Throws<SemaphoreFullException>(() => synchronization.File.Release());
        Assert.Throws<SemaphoreFullException>(() => synchronization.Serialization.Release());
    }

    [Fact]
    public async Task LoggerDisposalClosesItsTransferredSynchronizationResources()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nealytics-owned-sync-" + Guid.NewGuid().ToString("N"));
        WalSynchronization synchronization = new();
        try
        {
            WriteAheadLogger wal = new(Options.Create(new TelemetryEngineOptions
            {
                WriteAheadLogDirectory = directory
            }), null, synchronization: synchronization);
            await wal.AppendAsync(new GlobalTelemetryPayload
            {
                ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e"
            }, CancellationToken.None);
            await wal.DisposeAsync();
            Assert.Throws<ObjectDisposedException>(() => synchronization.File.Wait(0));
            Assert.Throws<ObjectDisposedException>(() => synchronization.Serialization.Wait(0));
            await wal.DisposeAsync();
        }
        finally
        {
            synchronization.Dispose();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
