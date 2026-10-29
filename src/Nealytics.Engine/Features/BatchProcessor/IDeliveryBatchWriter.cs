namespace Nealytics.Engine.Features.BatchProcessor;

using System;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Storage.Delivery;

internal interface IDeliveryBatchWriter
{
    string SchemaFingerprint { get; }
    Task<Guid> GetSourceIdAsync(CancellationToken cancellationToken);
    Task WriteDeliveryAsync(TelemetryDeliveryUnit unit, CancellationToken cancellationToken);
}

internal interface IDeliveryCheckpoint
{
    Task CheckpointAsync();
}
