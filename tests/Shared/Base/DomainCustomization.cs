using AutoFixture;
using Nealytics.Engine.Tests.Shared.Fakers;

namespace Nealytics.Engine.Tests.Shared.Base;

public class DomainCustomization : ICustomization
{
    public void Customize(IFixture fixture)
    {
        fixture.Register(() => new GlobalTelemetryPayloadFaker().Generate());

        fixture.Customizations.Add(new ConstrainedStringBuilder(200));
    }
}
