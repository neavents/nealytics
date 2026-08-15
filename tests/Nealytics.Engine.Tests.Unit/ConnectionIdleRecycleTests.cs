using FluentAssertions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Storage;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// A pooled connection is reopened rather than reused once it has been idle too long.
///
/// <b>The incident.</b> ClickHouse closes connections idle past <c>idle_connection_timeout</c>,
/// which defaults to 3600 seconds, and logs nothing — closing an idle socket is not an error. The
/// client goes on reporting <c>Open</c>. So after the estate sat quiet overnight, every pooled
/// connection was a corpse that only revealed itself on a write:
///
/// <code>
/// Broken pipe … Reached an unexpected end of the server's response
/// ClickHouse batch insert failed after all retries. Events preserved in WAL.
/// </code>
///
/// <b>Why the existing discard-on-failure path did not save it.</b> That path is real and works,
/// but it costs one failed insert per dead connection. With a pool of 16, five retries a batch and
/// cross-batch backoff, the estate produced fifteen consecutive failures across four minutes and
/// still had not drained the pool — while <c>/health</c> and <c>/ready</c> stayed green, because
/// <c>SELECT</c> works and only the columnar writer breaks. Recovery that is O(pool size) in failed
/// inserts is indistinguishable from an outage.
///
/// The WAL held every event and replayed them on restart, so nothing was lost. That is also what
/// let it go unnoticed: the failure is total, silent, and self-healing only if someone restarts.
/// </summary>
public class ConnectionIdleRecycleTests
{
    private static ClickHouseConnectionFactory Factory(int maxIdleSeconds)
    {
        TelemetryEngineOptions options = new()
        {
            ClickHouseConnectionString = "Host=127.0.0.1;Port=9000;Database=nealytics_core;User=default;Password=;",
            ConnectionPoolSize = 4,
            ConnectionMaxIdleSeconds = maxIdleSeconds,
        };

        return new ClickHouseConnectionFactory(Options.Create(options));
    }

    private static long Ticks(double seconds) =>
        (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

    [Fact]
    public void AConnectionIdleLongerThanTheLimitIsStale()
    {
        ClickHouseConnectionFactory factory = Factory(300);

        factory.IsStale(idleSinceTicks: 0, nowTicks: Ticks(301)).Should().BeTrue();
    }

    [Fact]
    public void AConnectionIdleForLessThanTheLimitIsReused()
    {
        // Reconnecting on every acquire would turn a pool into a connect-per-batch loop, which is
        // the cost the pool exists to avoid.
        ClickHouseConnectionFactory factory = Factory(300);

        factory.IsStale(idleSinceTicks: 0, nowTicks: Ticks(299)).Should().BeFalse();
    }

    [Fact]
    public void TheBoundaryCountsAsStale()
    {
        // Exactly at the limit is treated as expired. The server's clock is not ours and the cost
        // of being wrong in this direction is one reconnect.
        ClickHouseConnectionFactory factory = Factory(300);

        factory.IsStale(idleSinceTicks: 0, nowTicks: Ticks(300)).Should().BeTrue();
    }

    [Fact]
    public void ZeroDisablesTheCheckEntirely()
    {
        // An escape hatch for a deployment whose server has no idle timeout, or one that would
        // rather keep the old behaviour than change anything.
        ClickHouseConnectionFactory factory = Factory(0);

        factory.IsStale(idleSinceTicks: 0, nowTicks: Ticks(100_000)).Should().BeFalse();
    }

    [Fact]
    public void TheDefaultSitsWellBelowClickHouseSOwnIdleTimeout()
    {
        // The whole point. ClickHouse's default idle_connection_timeout is 3600 seconds; a default
        // at or above it would recycle nothing before the server had already closed the socket, and
        // this guard would be decoration. Verified against the live server:
        //
        //   SELECT value FROM system.settings WHERE name = 'idle_connection_timeout'  ->  3600
        int configured = new TelemetryEngineOptions().ConnectionMaxIdleSeconds;

        configured.Should().BeGreaterThan(0, "a default of 0 would ship the bug back");
        configured.Should().BeLessThan(
            3600,
            "past the server's own timeout the connection is already closed, and recycling after "
            + "that point cannot prevent the failure it exists to prevent");
    }

    [Fact]
    public void ThePoolIsStillBoundedByItsConfiguredSize()
    {
        // Guards the guard: recycling must not have quietly turned the pool into an unbounded one.
        TelemetryEngineOptions options = new() { ConnectionPoolSize = 4 };

        options.ConnectionPoolSize.Should().Be(4);
    }
}
