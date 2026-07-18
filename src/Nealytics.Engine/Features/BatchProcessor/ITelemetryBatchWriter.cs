namespace Nealytics.Engine.Features.BatchProcessor;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nealytics.Engine.Infrastructure.Serialization;

public interface ITelemetryBatchWriter
{
    Task WriteAsync(IReadOnlyList<GlobalTelemetryPayload> batch, int count, CancellationToken cancellationToken);
}
