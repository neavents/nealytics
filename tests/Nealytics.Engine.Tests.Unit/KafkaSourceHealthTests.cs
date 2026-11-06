using System.Text.Json;
using Confluent.Kafka;
using Nealytics.Engine.Features.KafkaIngestion;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Unit;

public sealed class KafkaSourceHealthTests
{
    [Fact]
    public void StatesAreDetachedUtcObservationsAndRepeatedSuccessNeverReadsTheClock()
    {
        int ticks = 0;
        DateTime start = new(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);
        var health = new KafkaSourceHealth(() => start.AddTicks(Interlocked.Increment(ref ticks)));
        var initial = health.Snapshot();
        Assert.Equal(new KafkaSourceHealthResponse("default", "starting", null, start.AddTicks(1)), initial);
        health.Subscribed(); var subscribed = health.Snapshot();
        Assert.Equal("waiting", subscribed.State); Assert.Null(subscribed.Issue); Assert.Equal(start.AddTicks(2), subscribed.ChangedAt);
        health.Subscribed(); health.Assigned(0);
        Assert.Equal(subscribed, health.Snapshot()); Assert.Equal(2, ticks);
        health.Assigned(1); var assigned = health.Snapshot();
        Assert.Equal("assigned", assigned.State); Assert.Null(assigned.Issue); Assert.Equal(start.AddTicks(3), assigned.ChangedAt);
        for (int index = 0; index < 100; index++) health.Confirmed();
        health.Assigned(2); Assert.Equal(3, ticks); Assert.Equal(assigned, health.Snapshot());
        health.Revoked(); var revoked = health.Snapshot();
        Assert.Equal("waiting", revoked.State); Assert.Equal("rebalance", revoked.Issue); Assert.Equal(start.AddTicks(4), revoked.ChangedAt);
        health.Assigned(0); Assert.Null(health.Snapshot().Issue); Assert.Equal(start.AddTicks(5), health.Snapshot().ChangedAt);
        Assert.Equal("starting", initial.State); Assert.Equal(DateTimeKind.Utc, initial.ChangedAt.Kind);
        Assert.Throws<ArgumentOutOfRangeException>(() => health.Assigned(-1));
        Assert.Throws<ArgumentNullException>(() => new KafkaSourceHealth(null!));
    }

    [Theory]
    [InlineData(ErrorCode.Local_Ssl, "tls_rejected")]
    [InlineData(ErrorCode.Local_Authentication, "authentication_failed")]
    [InlineData(ErrorCode.SaslAuthenticationFailed, "authentication_failed")]
    [InlineData(ErrorCode.UnsupportedSaslMechanism, "authentication_failed")]
    [InlineData(ErrorCode.IllegalSaslState, "authentication_failed")]
    [InlineData(ErrorCode.TopicAuthorizationFailed, "authorization_failed")]
    [InlineData(ErrorCode.GroupAuthorizationFailed, "authorization_failed")]
    [InlineData(ErrorCode.ClusterAuthorizationFailed, "authorization_failed")]
    [InlineData(ErrorCode.Local_AllBrokersDown, "broker_unavailable")]
    [InlineData(ErrorCode.Local_Transport, "broker_unavailable")]
    [InlineData(ErrorCode.BrokerNotAvailable, "broker_unavailable")]
    [InlineData(ErrorCode.NetworkException, "broker_unavailable")]
    [InlineData(ErrorCode.RequestTimedOut, "broker_unavailable")]
    [InlineData(ErrorCode.UnknownTopicOrPart, "broker_error")]
    public void NativeErrorsRetainOnlyFixedCategoriesAndNeverTheirPrivateDetails(ErrorCode code, string issue)
    {
        var health = new KafkaSourceHealth(); health.Assigned(1);
        health.NativeError(new Error(code, "private username password endpoint payload"));
        var status = health.Snapshot();
        Assert.Equal("unavailable", status.State); Assert.Equal(issue, status.Issue);
        string json = JsonSerializer.Serialize(status, TelemetryAotContext.Default.KafkaSourceHealthResponse);
        Assert.DoesNotContain("private", json); Assert.DoesNotContain("username", json); Assert.DoesNotContain("password", json);
        Assert.Equal(status, JsonSerializer.Deserialize(json, TelemetryAotContext.Default.KafkaSourceHealthResponse));
        health.Confirmed(); Assert.Equal("assigned", health.Snapshot().State); Assert.Null(health.Snapshot().Issue);
    }

    [Fact]
    public void SecurityErrorsSurviveGenericAndRebalanceNotificationsUntilPositiveConfirmation()
    {
        int clock = 0; var health = new KafkaSourceHealth(() => DateTime.UnixEpoch.AddTicks(++clock));
        health.NativeError(new Error(ErrorCode.UnknownTopicOrPart, "hidden"));
        Assert.Equal("broker_error", health.Snapshot().Issue);
        health.NativeError(new Error(ErrorCode.Local_Transport)); Assert.Equal("broker_unavailable", health.Snapshot().Issue);
        health.NativeError(new Error(ErrorCode.Local_Ssl)); var rejected = health.Snapshot();
        foreach (var error in new[] { new Error(ErrorCode.Local_Ssl), new Error(ErrorCode.Local_AllBrokersDown),
            new Error(ErrorCode.UnknownTopicOrPart), new Error(ErrorCode.NoError) }) health.NativeError(error);
        health.Subscribed(); health.Revoked(); health.Assigned(0); health.Lost();
        Assert.Equal(rejected, health.Snapshot()); Assert.Equal(4, clock);
        health.NativeError(new Error(ErrorCode.Local_Authentication));
        Assert.Equal("authentication_failed", health.Snapshot().Issue); Assert.Equal(5, clock);
        health.Assigned(1); Assert.Equal("assigned", health.Snapshot().State); Assert.Null(health.Snapshot().Issue);
        health.Lost(); Assert.Equal("unavailable", health.Snapshot().State); Assert.Equal("assignment_lost", health.Snapshot().Issue);
        var lost = health.Snapshot(); health.NativeError(new Error(ErrorCode.Local_Transport));
        Assert.Equal(lost, health.Snapshot());
        health.Confirmed(); Assert.Equal("assigned", health.Snapshot().State);
        var healthy = health.Snapshot(); health.NativeError(new Error(ErrorCode.NoError)); Assert.Equal(healthy, health.Snapshot());
    }

    [Theory]
    [InlineData("fatal")]
    [InlineData("worker")]
    [InlineData("stopped")]
    public void TerminalStatesCannotBeOverwrittenByLateCallbacksOrShutdown(string terminal)
    {
        int clock = 0; var health = new KafkaSourceHealth(() => DateTime.UnixEpoch.AddTicks(++clock));
        if (terminal == "fatal") health.NativeError(new Error(ErrorCode.Local_Fatal, "private details", true));
        else if (terminal == "worker") health.Failed();
        else health.Stopped();
        var status = health.Snapshot();
        Assert.Equal(terminal == "stopped" ? "stopped" : "failed", status.State);
        Assert.Equal(terminal switch { "fatal" => "fatal_error", "worker" => "worker_failed", _ => null }, status.Issue);
        health.Subscribed(); health.Assigned(1); health.Assigned(0); health.Revoked(); health.Lost();
        health.NativeError(new Error(ErrorCode.Local_Ssl)); health.Confirmed(); health.Failed(); health.Stopped();
        Assert.Equal(status, health.Snapshot()); Assert.Equal(2, clock);
    }

    [Fact]
    public async Task ConcurrentReadersNeverObserveMixedStateIssueOrTimestampFields()
    {
        int clock = 0; var health = new KafkaSourceHealth(() => DateTime.UnixEpoch.AddTicks(Interlocked.Increment(ref clock)));
        var original = health.Snapshot();
        var writer = Task.Run(() =>
        {
            for (int iteration = 0; iteration < 24; iteration++)
            {
                health.NativeError(new Error(ErrorCode.Local_Ssl)); health.Confirmed(); health.Revoked();
                health.NativeError(new Error(ErrorCode.Local_Transport)); health.Assigned(1);
            }
        });
        var readers = Enumerable.Range(0, 24).Select(_ => Task.Run(() => health.Snapshot())).ToArray();
        var snapshots = await Task.WhenAll(readers); await writer;
        foreach (var status in snapshots)
        {
            Assert.Equal("default", status.Name); Assert.True(status.ChangedAt > DateTime.UnixEpoch);
            Assert.Contains((status.State, status.Issue), new[] { ("starting", (string?)null), ("assigned", null),
                ("unavailable", "tls_rejected"), ("unavailable", "broker_unavailable"), ("waiting", "rebalance") });
        }
        Assert.Equal("starting", original.State); Assert.Null(original.Issue);
    }
}
