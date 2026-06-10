using FluentAssertions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.IngestTelemetry;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Serialization;
using Nealytics.Engine.Tests.Shared.Base;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

public class TelemetryChannelBrokerTests : UnitTestBase
{
    private readonly IOptions<TelemetryEngineOptions> _options;
    private readonly TelemetryEngineOptions _engineOptions;

    public TelemetryChannelBrokerTests()
    {
        _engineOptions = new TelemetryEngineOptions { MemoryChannelCapacity = 100 };
        _options = Substitute.For<IOptions<TelemetryEngineOptions>>();
        _options.Value.Returns(_engineOptions);
    }

    private TelemetryChannelBroker CreateSut() => new(_options);

    [Fact]
    public void Constructor_CreatesBoundedChannel()
    {
        var sut = CreateSut();
        sut.Reader.Should().NotBeNull();
    }

    [Fact]
    public async Task PublishAsync_WritesItemToChannel()
    {
        var sut = CreateSut();
        var payload = new GlobalTelemetryPayload
        {
            ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e"
        };

        await sut.PublishAsync(payload, CancellationToken.None);

        sut.Reader.TryRead(out var result).Should().BeTrue();
        result!.ProjectId.Should().Be("p");
    }

    [Fact]
    public async Task PublishAsync_PreservesWriteOrder()
    {
        var sut = CreateSut();
        var p1 = new GlobalTelemetryPayload { ProjectId = "p1", TenantId = "t", SessionId = "s", EventType = "e" };
        var p2 = new GlobalTelemetryPayload { ProjectId = "p2", TenantId = "t", SessionId = "s", EventType = "e" };

        await sut.PublishAsync(p1, CancellationToken.None);
        await sut.PublishAsync(p2, CancellationToken.None);

        sut.Reader.TryRead(out var r1).Should().BeTrue();
        sut.Reader.TryRead(out var r2).Should().BeTrue();
        r1!.ProjectId.Should().Be("p1");
        r2!.ProjectId.Should().Be("p2");
    }

    [Fact]
    public async Task PublishAsync_Backpressures_WhenChannelFull()
    {
        _engineOptions.MemoryChannelCapacity = 1;
        var sut = CreateSut();
        var payload = new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" };

        await sut.PublishAsync(payload, CancellationToken.None);
        var publishTask = sut.PublishAsync(payload, CancellationToken.None);

        publishTask.IsCompleted.Should().BeFalse("channel full, write should block");

        sut.Reader.TryRead(out _).Should().BeTrue();
        await publishTask;
        publishTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_Throws_WhenCancelled()
    {
        _engineOptions.MemoryChannelCapacity = 1;
        var sut = CreateSut();
        var payload = new GlobalTelemetryPayload { ProjectId = "p", TenantId = "t", SessionId = "s", EventType = "e" };
        using var cts = new CancellationTokenSource();

        await sut.PublishAsync(payload, CancellationToken.None);
        var publishTask = sut.PublishAsync(payload, cts.Token);

        cts.Cancel();

        var act = async () => await publishTask;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Reader_ReturnsSameInstance_OnMultipleAccess()
    {
        var sut = CreateSut();
        sut.Reader.Should().BeSameAs(sut.Reader);
    }
}
