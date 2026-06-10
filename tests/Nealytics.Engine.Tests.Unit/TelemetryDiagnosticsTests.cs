using System.Reflection;
using FluentAssertions;
using Nealytics.Engine.Infrastructure.Diagnostics;
using Nealytics.Engine.Tests.Shared.Base;

namespace Nealytics.Engine.Tests.Unit;

public class TelemetryDiagnosticsTests : UnitTestBase
{
    [Fact]
    public void Source_ActivitySourceName_IsNealyticsEngineCore()
    {
        TelemetryDiagnostics.Source.Name.Should().Be("Nealytics.Engine.Core");
    }

    [Fact]
    public void EngineMeter_Name_IsNealyticsEngineMetrics()
    {
        TelemetryDiagnostics.EngineMeter.Name.Should().Be("Nealytics.Engine.Metrics");
    }

    [Fact]
    public void AllInstruments_AreNotNull()
    {
        TelemetryDiagnostics.IngestedEventsTotal.Should().NotBeNull();
        TelemetryDiagnostics.StorageBatchesCommitted.Should().NotBeNull();
        TelemetryDiagnostics.ReadQueriesExecuted.Should().NotBeNull();
        TelemetryDiagnostics.StorageWriteDuration.Should().NotBeNull();
        TelemetryDiagnostics.QueryReadDuration.Should().NotBeNull();
        TelemetryDiagnostics.VolatileQueueDepth.Should().NotBeNull();
    }

    [Fact]
    public void IncrementQueueCounter_IncreasesDepth()
    {
        var initial = ReadQueueDepth();

        TelemetryDiagnostics.IncrementQueueCounter();

        ReadQueueDepth().Should().Be(initial + 1);
    }

    [Fact]
    public void DecrementQueueCounter_DecreasesDepth()
    {
        // Seed some depth to decrement
        TelemetryDiagnostics.IncrementQueueCounter();
        TelemetryDiagnostics.IncrementQueueCounter();
        var before = ReadQueueDepth();

        TelemetryDiagnostics.DecrementQueueCounter(1);

        ReadQueueDepth().Should().Be(before - 1);
    }

    [Fact]
    public void DecrementQueueCounter_CanGoBelowZero()
    {
        var current = ReadQueueDepth();

        TelemetryDiagnostics.DecrementQueueCounter(current + 10);

        ReadQueueDepth().Should().Be(-10);
    }

    private static int ReadQueueDepth()
    {
        var field = typeof(TelemetryDiagnostics)
            .GetField("_inMemoryQueueDepth", BindingFlags.Static | BindingFlags.NonPublic);
        return (int)field!.GetValue(null)!;
    }
}
