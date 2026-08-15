using FluentAssertions;
using Nealytics.Engine.Features.GetActiveUsers;
using Nealytics.Engine.Features.GetEventTimeSeries;
using Nealytics.Engine.Infrastructure.Configuration;

namespace Nealytics.Engine.Tests.Unit;

public class TimeZoneBucketingTests
{
    private static readonly DateTime Now = new(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

    private static EventTimeSeriesRequest Series(string? tz) => new()
    {
        ProjectId = "proj",
        TenantId = "tenant",
        From = Now.AddDays(-1),
        To = Now,
        Interval = TimeSeriesInterval.Day,
        TimeZone = tz,
        Limit = 100,
    };

    private static ActiveUsersRequest Active(string? tz) => new()
    {
        ProjectId = "proj",
        TenantId = "tenant",
        From = Now.AddDays(-1),
        To = Now,
        Interval = ActiveUsersInterval.Day,
        Dimension = ActiveDimension.Session,
        Mode = ActiveCountMode.Exact,
        TimeZone = tz,
        Limit = 100,
    };

    [Fact]
    public void WithoutTz_BucketsInUtc_AsBefore()
    {
        (string sql, var parameters) = GetEventTimeSeriesQuery.BuildQuery(Series(null));

        sql.Should().Contain("toStartOfDay(timestamp) AS bucket");
        parameters.Should().NotContain(p => p.Key == "tz");
    }

    [Fact]
    public void WithTz_BucketsInThatZone_AndBindsItAsAParameter()
    {
        (string sql, var parameters) = GetEventTimeSeriesQuery.BuildQuery(Series("Europe/Istanbul"));

        sql.Should().Contain("toStartOfDay(toTimeZone(timestamp, {tz:String})) AS bucket");
        sql.Should().NotContain("Europe/Istanbul",
            "the zone is a value, so it is bound rather than interpolated");
        parameters.Should().ContainSingle(p => p.Key == "tz" && (string)p.Value! == "Europe/Istanbul");
    }

    [Fact]
    public void ActiveUsers_BucketsInTheSameZone()
    {
        (string sql, var parameters) = GetActiveUsersQuery.BuildQuery(Active("Europe/Istanbul"));

        sql.Should().Contain("toStartOfDay(toTimeZone(timestamp, {tz:String})) AS bucket");
        parameters.Should().ContainSingle(p => p.Key == "tz");
    }

    [Theory]
    [InlineData("Europe/Istanbul")]
    [InlineData("UTC")]
    [InlineData("America/Argentina/Buenos_Aires")]
    [InlineData("Etc/GMT+3")]
    public void WellFormedZones_AreAccepted(string tz)
    {
        TimeBucket.IsWellFormed(tz).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Europe/Istanbul'; DROP TABLE global_events --")]
    [InlineData("2026-08-13T12:00:00Z")]
    [InlineData("Europe Istanbul")]
    [InlineData("Europe/Istanbul\n")]
    public void MalformedZones_AreRejected(string tz)
    {
        TimeBucket.IsWellFormed(tz).Should().BeFalse();
    }

    [Fact]
    public void OverlongZone_IsRejected()
    {
        TimeBucket.IsWellFormed(new string('a', TimeBucket.MaxTimeZoneLength + 1)).Should().BeFalse();
    }
}
