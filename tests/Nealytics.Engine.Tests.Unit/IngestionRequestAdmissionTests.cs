using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public sealed class IngestionRequestAdmissionTests
{
    [Fact]
    public async Task ProgramInstallsAdmissionAndPublishesBoundedDefaults()
    {
        await using NoDatabaseWebFactory factory = new();
        PartitionedRateLimiter<HttpContext>? limiter = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;
        Assert.NotNull(limiter);
        TelemetryEngineOptions options = new();
        Assert.Equal(64, options.MaxConcurrentIngestionRequests);
        Assert.Equal(16_777_216, options.IngestionRequestByteBudget);
        HttpContext context = Context(ClickHouseWorkload.Write);
        using RateLimitLease lease = limiter.AttemptAcquire(context);
        Assert.True(lease.IsAcquired);
    }

    [Theory]
    [InlineData(2, 100, 1_000, 2)]
    [InlineData(10, 100, 250, 2)]
    [InlineData(10, 100, 100, 1)]
    public async Task CountAndMaximumBodyReservationsBoundAdmissionWithoutQueueing(int requests, int bodyBytes, long budget, int capacity)
    {
        using PartitionedRateLimiter<HttpContext> limiter = IngestionRequestAdmission.Create(new TelemetryEngineOptions
        {
            MaxConcurrentIngestionRequests = requests,
            MaxRequestBodyBytes = bodyBytes,
            IngestionRequestByteBudget = budget
        });
        HttpContext context = Context(ClickHouseWorkload.Write);
        List<RateLimitLease> leases = [];
        try
        {
            for (int index = 0; index < capacity; index++)
            {
                RateLimitLease lease = limiter.AttemptAcquire(context);
                leases.Add(lease);
                Assert.True(lease.IsAcquired);
            }
            ValueTask<RateLimitLease> rejected = limiter.AcquireAsync(context);
            Assert.True(rejected.IsCompletedSuccessfully);
            using RateLimitLease refusal = await rejected;
            Assert.False(refusal.IsAcquired);
            leases[0].Dispose();
            using RateLimitLease afterReturn = limiter.AttemptAcquire(context);
            Assert.True(afterReturn.IsAcquired);
            Assert.Equal(0, limiter.GetStatistics(context)!.CurrentQueuedCount);
        }
        finally
        {
            foreach (RateLimitLease lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ClickHouseWorkload.Read)]
    [InlineData(ClickHouseWorkload.Probe)]
    [InlineData(ClickHouseWorkload.Maintenance)]
    public void ReadProbeMaintenanceAndUnmarkedEndpointsDoNotBorrowIngestionCapacity(ClickHouseWorkload? workload)
    {
        using PartitionedRateLimiter<HttpContext> limiter = IngestionRequestAdmission.Create(new TelemetryEngineOptions
        {
            MaxConcurrentIngestionRequests = 1
        });
        using RateLimitLease occupied = limiter.AttemptAcquire(Context(ClickHouseWorkload.Write));
        HttpContext other = Context(workload);
        for (int index = 0; index < 4; index++)
        {
            using RateLimitLease lease = limiter.AttemptAcquire(other);
            Assert.True(lease.IsAcquired);
        }
        using RateLimitLease refused = limiter.AttemptAcquire(Context(ClickHouseWorkload.Write));
        Assert.False(refused.IsAcquired);
    }

    [Fact]
    public void AnEndpointWithoutWorkloadMetadataDoesNotBorrowIngestionCapacity()
    {
        using PartitionedRateLimiter<HttpContext> limiter = IngestionRequestAdmission.Create(new TelemetryEngineOptions());
        DefaultHttpContext context = new();
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "unmarked"));
        using RateLimitLease lease = limiter.AttemptAcquire(context);
        Assert.True(lease.IsAcquired);
    }

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(-1, 100, 100)]
    [InlineData(1, 0, 100)]
    [InlineData(1, -1, 100)]
    [InlineData(1, 100, 99)]
    [InlineData(1, 100, -1)]
    public void InvalidBudgetsFailBeforeCreatingAdmissionState(int requests, int bodyBytes, long budget)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IngestionRequestAdmission.Create(new TelemetryEngineOptions
        {
            MaxConcurrentIngestionRequests = requests,
            MaxRequestBodyBytes = bodyBytes,
            IngestionRequestByteBudget = budget
        }));
    }

    [Fact]
    public void AdmissionRequiresConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => IngestionRequestAdmission.Create(null!));
    }

    internal static HttpContext Context(ClickHouseWorkload? workload)
    {
        DefaultHttpContext context = new();
        if (workload.HasValue)
        {
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
                new EndpointMetadataCollection(new ClickHouseWorkloadMetadata(workload.Value)), "workload"));
        }
        return context;
    }
}
