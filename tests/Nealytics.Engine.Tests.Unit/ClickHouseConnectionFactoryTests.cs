using FluentAssertions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using Nealytics.Engine.Tests.Shared.Base;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

public class ClickHouseConnectionFactoryTests : UnitTestBase
{
    private readonly IOptions<TelemetryEngineOptions> _options;

    public ClickHouseConnectionFactoryTests()
    {
        var engineOptions = new TelemetryEngineOptions
        {
            ClickHouseConnectionString = "Host=127.0.0.1;Port=9000;Database=test;",
            ConnectionPoolSize = 16
        };
        _options = Substitute.For<IOptions<TelemetryEngineOptions>>();
        _options.Value.Returns(engineOptions);
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        var act = () => new ClickHouseConnectionFactory(_options);
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_CompletesSuccessfully_WhenPoolIsEmpty()
    {
        var factory = new ClickHouseConnectionFactory(_options);
        await factory.DisposeAsync();
    }
}
