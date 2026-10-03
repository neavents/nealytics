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

    public static void ApplyBodyLimit(HttpContext context, long maxBytes)
    {
        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();

        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maxBytes;
        }
    }

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
