namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

internal static class IngestionRequestAdmission
{
    public static PartitionedRateLimiter<HttpContext> Create(TelemetryEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxConcurrentIngestionRequests, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRequestBodyBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.IngestionRequestByteBudget, options.MaxRequestBodyBytes);
        int limit = (int)Math.Min(options.MaxConcurrentIngestionRequests,
            options.IngestionRequestByteBudget / options.MaxRequestBodyBytes);
        return PartitionedRateLimiter.Create<HttpContext, ClickHouseWorkload>(context =>
            context.GetEndpoint()?.Metadata.GetMetadata<ClickHouseWorkloadMetadata>()?.Workload == ClickHouseWorkload.Write
                ? RateLimitPartition.GetConcurrencyLimiter(ClickHouseWorkload.Write, _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = limit,
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst
                })
                : RateLimitPartition.GetNoLimiter(ClickHouseWorkload.Read));
    }
}
