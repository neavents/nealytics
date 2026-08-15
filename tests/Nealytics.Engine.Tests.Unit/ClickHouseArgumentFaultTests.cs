using FluentAssertions;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

public class ClickHouseArgumentFaultTests
{
    [Fact]
    public void ANonServerException_IsNotATimeZoneFault()
    {
        ClickHouseArgumentFault.IsUnknownTimeZone(new InvalidOperationException("time zone"))
            .Should().BeFalse(
                "matching on message alone would turn any unrelated failure mentioning a time zone "
                + "into a 400, which hides a real outage behind a client error");
    }

    [Fact]
    public void ANullChain_IsHandled()
    {
        ClickHouseArgumentFault.IsUnknownTimeZone(new Exception("boom", new Exception("inner")))
            .Should().BeFalse();
    }
}
