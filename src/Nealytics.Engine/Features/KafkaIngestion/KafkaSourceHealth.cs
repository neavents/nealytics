namespace Nealytics.Engine.Features.KafkaIngestion;

using System;
using System.Threading;
using Confluent.Kafka;

public sealed record KafkaSourceHealthResponse(string Name, string State, string? Issue, DateTime ChangedAt);

internal sealed class KafkaSourceHealth
{
    private enum Phase { Starting, Waiting, Assigned, Unavailable, Failed, Stopped }

    private static readonly string[] Names = ["starting", "waiting", "assigned", "unavailable", "failed", "stopped"];
    private readonly object _sync = new();
    private readonly Func<DateTime> _now;
    private int _phase = (int)Phase.Starting;
    private string? _issue;
    private int _priority;
    private DateTime _changedAt;

    public KafkaSourceHealth() : this(() => DateTime.UtcNow)
    {
    }

    internal KafkaSourceHealth(Func<DateTime> now)
    {
        ArgumentNullException.ThrowIfNull(now);
        _now = now;
        _changedAt = now();
    }

    public KafkaSourceHealthResponse Snapshot()
    {
        lock (_sync)
        {
            return new("default", Names[_phase], _issue, _changedAt);
        }
    }

    public void Subscribed() => Change(Phase.Waiting, null, 0);

    public void Assigned(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        Change(count == 0 ? Phase.Waiting : Phase.Assigned, null, 0);
    }

    public void Revoked() => Change(Phase.Waiting, "rebalance", 1);

    public void Lost() => Change(Phase.Unavailable, "assignment_lost", 3);

    public void NativeError(Error error)
    {
        if (!error.IsError)
        {
            return;
        }
        if (error.IsFatal)
        {
            Change(Phase.Failed, "fatal_error", 5);
            return;
        }
        (string issue, int priority) = error.Code switch
        {
            ErrorCode.Local_Ssl => ("tls_rejected", 4),
            ErrorCode.Local_Authentication or ErrorCode.SaslAuthenticationFailed
                or ErrorCode.UnsupportedSaslMechanism or ErrorCode.IllegalSaslState => ("authentication_failed", 4),
            ErrorCode.TopicAuthorizationFailed or ErrorCode.GroupAuthorizationFailed
                or ErrorCode.ClusterAuthorizationFailed => ("authorization_failed", 4),
            ErrorCode.Local_AllBrokersDown or ErrorCode.Local_Transport or ErrorCode.BrokerNotAvailable
                or ErrorCode.NetworkException or ErrorCode.RequestTimedOut => ("broker_unavailable", 2),
            _ => ("broker_error", 1)
        };
        Change(Phase.Unavailable, issue, priority);
    }

    public void Confirmed()
    {
        if (Volatile.Read(ref _phase) != (int)Phase.Assigned)
        {
            Change(Phase.Assigned, null, 0);
        }
    }

    public void Failed() => Change(Phase.Failed, "worker_failed", 5);

    public void Stopped() => Change(Phase.Stopped, null, 0);

    private void Change(Phase next, string? issue, int priority)
    {
        lock (_sync)
        {
            if (_phase >= (int)Phase.Failed)
            {
                return;
            }
            if (_phase == (int)Phase.Unavailable && next is Phase.Waiting or Phase.Unavailable && priority < _priority)
            {
                return;
            }
            if (_phase == (int)next && _issue == issue)
            {
                return;
            }
            _issue = issue;
            _priority = priority;
            _changedAt = _now();
            Volatile.Write(ref _phase, (int)next);
        }
    }
}
