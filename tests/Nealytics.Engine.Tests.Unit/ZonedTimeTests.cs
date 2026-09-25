using FluentAssertions;
using Nealytics.Engine.Infrastructure.Query;

namespace Nealytics.Engine.Tests.Unit;

public class ZonedTimeTests
{
    private static TimeZoneInfo Zone(string name) => TimeZoneInfo.FindSystemTimeZoneById(name);

    [Fact]
    public void NoZoneMeansUtc()
    {
        ZonedTime.TryResolve(null, out TimeZoneInfo? none).Should().BeTrue();
        none.Should().BeNull();
        ZonedTime.TryParseInstant("2026-09-01T10:00:00", null, out DateTime utc).Should().BeTrue();
        utc.Should().Be(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));
        utc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void AWallClockInsideASpringGapMovesForwardToTheFirstValidInstant()
    {
        DateTime utc = ZonedTime.ToUtc(new DateTime(2026, 3, 8, 2, 30, 0), Zone("America/New_York"));

        utc.Should().Be(new DateTime(2026, 3, 8, 7, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void WithoutAZoneThePrecedingWindowIsTheSameAbsoluteLength()
    {
        DateTime from = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        DateTime to = new(2026, 9, 14, 6, 0, 0, DateTimeKind.Utc);

        ZonedTime.Preceding(from, to, null).Should().Be((new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc), from));
    }

    [Fact]
    public void AnUnparseableInstantIsRefused()
    {
        ZonedTime.TryParseInstant("not a time", null, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("Europe/Istanbul", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Olympus", false)]
    [InlineData("Europe/Istanbul' OR 1", false)]
    public void ZoneNamesResolveOnlyWhenWellFormedAndKnown(string name, bool known)
    {
        ZonedTime.TryResolve(name, out _).Should().Be(known);
    }
}
