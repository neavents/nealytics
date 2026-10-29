using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public sealed class ClickHouseCapacityOptionsTests
{
    [Theory]
    [InlineData(nameof(TelemetryEngineOptions.ConnectionPoolSize), -1)]
    [InlineData(nameof(TelemetryEngineOptions.UnpooledConnectionLimit), 0)]
    [InlineData(nameof(TelemetryEngineOptions.WriteConnectionPoolSize), 0)]
    [InlineData(nameof(TelemetryEngineOptions.ProbeConnectionPoolSize), 0)]
    [InlineData(nameof(TelemetryEngineOptions.MaintenanceConnectionPoolSize), 0)]
    [InlineData(nameof(TelemetryEngineOptions.ConnectionAcquireTimeoutSeconds), 0)]
    [InlineData(nameof(TelemetryEngineOptions.ProbeTimeoutSeconds), 0)]
    [InlineData(nameof(TelemetryEngineOptions.ConnectionAcquireTimeoutSeconds), 4_294_968)]
    [InlineData(nameof(TelemetryEngineOptions.ProbeTimeoutSeconds), 4_294_968)]
    public void InvalidResourceLimitsFailBeforeOpeningAnyConnection(string property, int value)
    {
        TelemetryEngineOptions options = new();
        typeof(TelemetryEngineOptions).GetProperty(property)!.SetValue(options, value);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClickHouseConnectionFactory(Options.Create(options)));
    }

    [Theory]
    [InlineData(0, 7, 10)]
    [InlineData(16, 7, 19)]
    [InlineData(1, 7, 4)]
    public async Task AggregateCapacityIncludesEveryWorkloadAndBoundsUnpooledReads(int reads, int unpooled, long expected)
    {
        await using ClickHouseConnectionFactory factory = new(Options.Create(new TelemetryEngineOptions
        {
            ConnectionPoolSize = reads,
            UnpooledConnectionLimit = unpooled
        }));
        Assert.Equal(expected, factory.MaximumConcurrentConnections);
        Assert.Equal(0, factory.EvictIdle());
        Assert.Equal(0, factory.EvictIdle(ClickHouseWorkload.Maintenance));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task UnknownWorkloadsCannotAccessAnotherReservation(int value)
    {
        await using ClickHouseConnectionFactory factory = new(Options.Create(new TelemetryEngineOptions()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => factory.AcquireAsync((ClickHouseWorkload)value, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.EvictIdle((ClickHouseWorkload)value));
    }
}
