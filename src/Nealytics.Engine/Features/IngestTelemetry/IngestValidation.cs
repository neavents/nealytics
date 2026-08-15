namespace Nealytics.Engine.Features.IngestTelemetry;

using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Nealytics.Engine.Infrastructure.Serialization;

public enum IngestRejection
{
    None,
    MissingProjectId,
    MissingTenantId,
    MissingSessionId,
    MissingEventType,
    FieldTooLong,
    TimestampTooFarAhead,
    ProjectNotPermittedForKey,
}

public static class IngestValidation
{
    public const int MaxIdentifierLength = 256;

    public const int MaxEventTypeLength = 128;

    public static readonly TimeSpan MaxClockSkewAhead = TimeSpan.FromHours(24);

    public static string ResolveProjectKey(string? headerValue, string? queryValue)
    {
        if (!string.IsNullOrEmpty(headerValue))
        {
            return headerValue;
        }
        if (!string.IsNullOrEmpty(queryValue))
        {
            return queryValue;
        }
        return string.Empty;
    }

    public static bool IsValidPayload(GlobalTelemetryPayload? payload) =>
        Validate(payload, DateTime.UtcNow) == IngestRejection.None;

    public static IngestRejection Validate(GlobalTelemetryPayload? payload, DateTime utcNow)
    {
        if (payload is null)
        {
            return IngestRejection.MissingProjectId;
        }

        if (string.IsNullOrEmpty(payload.ProjectId)) return IngestRejection.MissingProjectId;
        if (string.IsNullOrEmpty(payload.TenantId)) return IngestRejection.MissingTenantId;
        if (string.IsNullOrEmpty(payload.SessionId)) return IngestRejection.MissingSessionId;
        if (string.IsNullOrEmpty(payload.EventType)) return IngestRejection.MissingEventType;

        if (payload.ProjectId.Length > MaxIdentifierLength
            || payload.TenantId.Length > MaxIdentifierLength
            || payload.SessionId.Length > MaxIdentifierLength
            || payload.EventType.Length > MaxEventTypeLength
            || payload.ObjectId?.Length > MaxIdentifierLength
            || payload.UserId?.Length > MaxIdentifierLength)
        {
            return IngestRejection.FieldTooLong;
        }

        // A timestamp is caller-supplied, and the caller is a phone whose clock can be anything.
        // Rejecting only the far future, never the past: a backfill is legitimate and a device
        // hours behind is ordinary, but a year-3000 row is not a late event, it is a broken clock.
        //
        // It is worth rejecting rather than clamping because timestamp is the partition key. One
        // such row creates a partition dated centuries out that the TTL will never expire, and it
        // widens the min/max range of nothing else -- so it survives every retention pass and is
        // invisible until someone reads system.parts.
        //
        // Compared as raw ticks against UtcNow, which is the same reading TelemetryInsertMath
        // .ToClickHouseTimestamp gives it (SpecifyKind to Utc, no conversion). Validating under a
        // different interpretation than the one that gets stored would let a row pass the check and
        // land in a partition the check would have refused.
        if (payload.Timestamp - utcNow > MaxClockSkewAhead)
        {
            return IngestRejection.TimestampTooFarAhead;
        }

        return IngestRejection.None;
    }

    public static bool ExceedsBodyLimit(long? contentLength, long maxBytes)
    {
        return contentLength.HasValue && contentLength.Value > maxBytes;
    }

    /// <summary>
    /// Bounds the body for this request regardless of whether it declared a length.
    ///
    /// <see cref="ExceedsBodyLimit"/> reads <c>Content-Length</c>, which a chunked request does not
    /// send — so on its own it is skipped entirely for those, and the body is read to completion.
    /// Measured: a chunked request well over the limit was answered <c>202</c>.
    ///
    /// The limit in <c>Program.cs</c> is <c>ConfigureKestrel</c>, i.e. server configuration, which
    /// any other host ignores. Setting it per request through the feature makes the bound a property
    /// of this endpoint rather than of whoever is hosting it.
    /// </summary>
    public static void ApplyBodyLimit(HttpContext context, long maxBytes)
    {
        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxBytes;
        }
    }

    /// <summary>
    /// The reason as a metric tag. Deliberately coarse and closed: a caller-controlled string here
    /// would be an unbounded cardinality dimension in the metrics backend.
    /// </summary>
    public static string Tag(IngestRejection rejection) => rejection switch
    {
        IngestRejection.MissingProjectId => "missing_project_id",
        IngestRejection.MissingTenantId => "missing_tenant_id",
        IngestRejection.MissingSessionId => "missing_session_id",
        IngestRejection.MissingEventType => "missing_event_type",
        IngestRejection.FieldTooLong => "field_too_long",
        IngestRejection.TimestampTooFarAhead => "timestamp_too_far_ahead",
        IngestRejection.ProjectNotPermittedForKey => "project_not_permitted_for_key",
        _ => "none",
    };
}
