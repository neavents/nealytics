using FluentAssertions;
using Nealytics.Engine.Features.GetActiveUsers;

namespace Nealytics.Engine.Tests.Unit;

public class ActiveUsersParsersTests
{
    [Theory]
    [InlineData("day", ActiveUsersInterval.Day)]
    [InlineData("month", ActiveUsersInterval.Month)]
    public void IntervalParser_ParsesValidValues(string raw, ActiveUsersInterval expected)
    {
        ActiveUsersIntervalParser.TryParse(raw, out var interval).Should().BeTrue();
        interval.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hour")]
    [InlineData("DAY")]
    [InlineData(null)]
    public void IntervalParser_RejectsInvalid_AndDefaultsToDay(string? raw)
    {
        ActiveUsersIntervalParser.TryParse(raw, out var interval).Should().BeFalse();
        interval.Should().Be(ActiveUsersInterval.Day);
    }

    [Theory]
    [InlineData(ActiveUsersInterval.Day, "toStartOfDay")]
    [InlineData(ActiveUsersInterval.Month, "toStartOfMonth")]
    public void IntervalParser_MapsBucketFunction(ActiveUsersInterval interval, string expected)
    {
        ActiveUsersIntervalParser.ToBucketFunction(interval).Should().Be(expected);
    }

    [Fact]
    public void IntervalParser_UndefinedEnum_Throws()
    {
        Action bucket = () => ActiveUsersIntervalParser.ToBucketFunction((ActiveUsersInterval)999);
        Action wire = () => ActiveUsersIntervalParser.ToWireFormat((ActiveUsersInterval)999);
        bucket.Should().Throw<ArgumentOutOfRangeException>();
        wire.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("user", ActiveDimension.User)]
    [InlineData("session", ActiveDimension.Session)]
    public void DimensionParser_ParsesValidValues(string raw, ActiveDimension expected)
    {
        ActiveDimensionParser.TryParse(raw, out var dimension).Should().BeTrue();
        dimension.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("users")]
    [InlineData("account")]
    public void DimensionParser_RejectsInvalid_AndDefaultsToUser(string raw)
    {
        ActiveDimensionParser.TryParse(raw, out var dimension).Should().BeFalse();
        dimension.Should().Be(ActiveDimension.User);
    }

    [Theory]
    [InlineData(ActiveDimension.User, "user_id")]
    [InlineData(ActiveDimension.Session, "session_id")]
    public void DimensionParser_MapsColumn(ActiveDimension dimension, string expected)
    {
        ActiveDimensionParser.ToColumn(dimension).Should().Be(expected);
    }

    [Fact]
    public void DimensionParser_UndefinedEnum_Throws()
    {
        Action column = () => ActiveDimensionParser.ToColumn((ActiveDimension)42);
        column.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("exact", ActiveCountMode.Exact)]
    [InlineData("approx", ActiveCountMode.Approx)]
    public void ModeParser_ParsesValidValues(string raw, ActiveCountMode expected)
    {
        ActiveCountModeParser.TryParse(raw, out var mode).Should().BeTrue();
        mode.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("approximate")]
    [InlineData("EXACT")]
    public void ModeParser_RejectsInvalid_AndDefaultsToExact(string raw)
    {
        ActiveCountModeParser.TryParse(raw, out var mode).Should().BeFalse();
        mode.Should().Be(ActiveCountMode.Exact);
    }

    [Theory]
    [InlineData(ActiveCountMode.Exact, "uniqExact")]
    [InlineData(ActiveCountMode.Approx, "uniq")]
    public void ModeParser_MapsUniqFunction(ActiveCountMode mode, string expected)
    {
        ActiveCountModeParser.ToUniqFunction(mode).Should().Be(expected);
    }

    [Fact]
    public void ModeParser_UndefinedEnum_Throws()
    {
        Action uniq = () => ActiveCountModeParser.ToUniqFunction((ActiveCountMode)7);
        uniq.Should().Throw<ArgumentOutOfRangeException>();
    }
}
