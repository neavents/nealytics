namespace Nealytics.Engine.Features.IngestTelemetry;

using Nealytics.Engine.Infrastructure.Serialization;

public static class IngestValidation
{
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

    public static bool IsValidPayload(GlobalTelemetryPayload? payload)
    {
        return payload is not null
            && !string.IsNullOrEmpty(payload.ProjectId)
            && !string.IsNullOrEmpty(payload.TenantId)
            && !string.IsNullOrEmpty(payload.SessionId)
            && !string.IsNullOrEmpty(payload.EventType);
    }

    public static bool ExceedsBodyLimit(long? contentLength, long maxBytes)
    {
        return contentLength.HasValue && contentLength.Value > maxBytes;
    }
}
