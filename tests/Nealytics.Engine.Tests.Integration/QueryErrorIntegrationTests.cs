using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Features.GetProjectTimeline;
using Nealytics.Engine.Features.GetSessionAnalytics;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;
using NSubstitute;

namespace Nealytics.Engine.Tests.Integration;

public class QueryErrorIntegrationTests
{
    private static ClickHouseConnectionFactory UnreachableFactory()
    {
        TelemetryEngineOptions options = new TelemetryEngineOptions
        {
            ClickHouseConnectionString = "Host=127.0.0.1;Port=9;Database=nealytics_core;User=default;Password=;"
        };
        IOptions<TelemetryEngineOptions> wrapped = Substitute.For<IOptions<TelemetryEngineOptions>>();
        wrapped.Value.Returns(options);
        return new ClickHouseConnectionFactory(wrapped);
    }

    [Fact]
    public async Task TimelineQuery_WhenClickHouseUnreachable_PropagatesException()
    {
        await using ClickHouseConnectionFactory factory = UnreachableFactory();
        GetProjectTimelineQuery query = new GetProjectTimelineQuery(factory, NullLogger<GetProjectTimelineQuery>.Instance);
        TimelineQueryRequest request = new TimelineQueryRequest { ProjectId = "p", TenantId = "t", Limit = 10 };

        Func<Task> act = () => query.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task SessionAnalyticsQuery_WhenClickHouseUnreachable_PropagatesException()
    {
        await using ClickHouseConnectionFactory factory = UnreachableFactory();
        GetSessionAnalyticsQuery query = new GetSessionAnalyticsQuery(factory, NullLogger<GetSessionAnalyticsQuery>.Instance);
        SessionAnalyticsRequest request = new SessionAnalyticsRequest
        {
            ProjectId = "p",
            TenantId = "t",
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            Limit = 10
        };

        Func<Task> act = () => query.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task TimeSeriesQuery_WhenClickHouseUnreachable_PropagatesException()
    {
        await using ClickHouseConnectionFactory factory = UnreachableFactory();
        GetEventTimeSeriesQuery query = new GetEventTimeSeriesQuery(factory, NullLogger<GetEventTimeSeriesQuery>.Instance);
        EventTimeSeriesRequest request = new EventTimeSeriesRequest
        {
            ProjectId = "p",
            TenantId = "t",
            From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            Interval = TimeSeriesInterval.Hour,
            Limit = 10
        };

        Func<Task> act = () => query.ExecuteAsync(request, CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }
}
