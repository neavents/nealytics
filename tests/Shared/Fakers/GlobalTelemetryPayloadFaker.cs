using Bogus;
using Nealytics.Engine.Infrastructure.Serialization;

namespace Nealytics.Engine.Tests.Shared.Fakers;

public sealed class GlobalTelemetryPayloadFaker : Faker<GlobalTelemetryPayload>
{
    public GlobalTelemetryPayloadFaker()
    {
        RuleFor(p => p.ProjectId, f => f.Random.AlphaNumeric(10));
        RuleFor(p => p.TenantId, f => f.Random.AlphaNumeric(10));
        RuleFor(p => p.SessionId, f => f.Random.AlphaNumeric(20));
        RuleFor(p => p.EventType, f => f.PickRandom("page_view", "click", "scroll", "submit", "load", "visible"));
        RuleFor(p => p.ItemId, f => f.Random.Bool(0.7f) ? $"/path/{f.Lorem.Word()}" : null);
        RuleFor(p => p.MetadataJson, f => $"{{ \"value\": \"{f.Lorem.Word()}\" }}");
    }

    public GlobalTelemetryPayloadFaker WithProjectId(string projectId)
    {
        RuleFor(p => p.ProjectId, projectId);
        return this;
    }

    public GlobalTelemetryPayloadFaker WithTenantId(string tenantId)
    {
        RuleFor(p => p.TenantId, tenantId);
        return this;
    }

    public GlobalTelemetryPayloadFaker WithSessionId(string sessionId)
    {
        RuleFor(p => p.SessionId, sessionId);
        return this;
    }

    public GlobalTelemetryPayloadFaker WithEventType(string eventType)
    {
        RuleFor(p => p.EventType, eventType);
        return this;
    }
}
