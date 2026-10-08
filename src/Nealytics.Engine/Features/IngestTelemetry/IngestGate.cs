namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Infrastructure.Serialization;

public sealed class IngestGate
{
    private readonly EventTypePattern _serverEventTypes;
    private readonly FrozenSet<string> _serverOnlyMeasures;
    private readonly string _aliasEventType;
    private readonly TimeSpan _publicTimestampTolerance;

    public IngestGate(IOptions<TelemetryEngineOptions> options, MeasureRegistry measures)
        : this(options.Value, measures)
    {
    }

    public IngestGate(TelemetryEngineOptions options, MeasureRegistry measures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(measures);

        _serverEventTypes = EventTypePattern.Parse(options.ServerEventTypes, "TelemetryEngine:ServerEventTypes");
        _serverOnlyMeasures = measures.Active
            .Where(measure => measure.ServerOnly)
            .Select(measure => measure.Name)
            .ToFrozenSet(StringComparer.Ordinal);
        _aliasEventType = options.AliasEventType?.Trim() ?? string.Empty;

        if (options.PublicKeyTimestampToleranceSeconds <= 0)
        {
            throw new InvalidOperationException(
                "TelemetryEngine:PublicKeyTimestampToleranceSeconds must be positive.");
        }

        _publicTimestampTolerance = TimeSpan.FromSeconds(options.PublicKeyTimestampToleranceSeconds);
    }

    public IngestRejection Check(
        IngestionKeyPolicy policy, GlobalTelemetryPayload? payload, DateTime receivedAt, out bool timestampAdjusted)
    {
        ArgumentNullException.ThrowIfNull(policy);
        timestampAdjusted = false;

        IngestRejection rejection = IngestValidation.Validate(payload, receivedAt, sessionRequired: !policy.IsServer);

        if (rejection != IngestRejection.None)
        {
            return rejection;
        }

        GlobalTelemetryPayload accepted = payload!;

        if (!ApiKeyValidator.MayWriteProject(policy, accepted.ProjectId))
        {
            return IngestRejection.ProjectNotPermittedForKey;
        }

        if (!policy.PermitsEventType(accepted.EventType))
        {
            return IngestRejection.EventTypeNotPermittedForKey;
        }

        if (!policy.IsServer)
        {
            if (!_serverEventTypes.IsEmpty && _serverEventTypes.Matches(accepted.EventType))
            {
                return IngestRejection.EventTypeNotPermittedForKey;
            }

            if (_serverOnlyMeasures.Count > 0 && CarriesServerOnlyMeasure(accepted.Measures))
            {
                return IngestRejection.ServerOnlyField;
            }
        }

        if (policy.Scope == IngestionKeyScope.Public
            && !string.IsNullOrEmpty(accepted.TrafficClass)
            && !string.Equals(accepted.TrafficClass, TrafficFilter.Normal, StringComparison.Ordinal))
        {
            return IngestRejection.ServerOnlyField;
        }

        if (_aliasEventType.Length > 0
            && string.Equals(accepted.EventType, _aliasEventType, StringComparison.Ordinal)
            && string.IsNullOrEmpty(accepted.UserId)
            && string.IsNullOrEmpty(accepted.ObjectId))
        {
            return IngestRejection.AliasWithoutIdentity;
        }

        if (policy.Scope == IngestionKeyScope.Public
            && (accepted.Timestamp - receivedAt).Duration() > _publicTimestampTolerance)
        {
            accepted.Timestamp = receivedAt;
            timestampAdjusted = true;
        }

        return IngestRejection.None;
    }

    private bool CarriesServerOnlyMeasure(Dictionary<string, string>? measures)
    {
        if (measures is null)
        {
            return false;
        }

        foreach (string name in measures.Keys)
        {
            if (_serverOnlyMeasures.Contains(name))
            {
                return true;
            }
        }

        return false;
    }
}
