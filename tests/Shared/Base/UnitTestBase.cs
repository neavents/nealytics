using AutoFixture;
using AutoFixture.AutoNSubstitute;
using Nealytics.Engine.Tests.Shared.Base;

namespace Nealytics.Engine.Tests.Shared.Base;

public abstract class UnitTestBase
{
    protected IFixture Fixture { get; }

    protected UnitTestBase()
    {
        Fixture = new Fixture()
            .Customize(new AutoNSubstituteCustomization { ConfigureMembers = true })
            .Customize(new DomainCustomization());

        Fixture.Behaviors.OfType<ThrowingRecursionBehavior>()
            .ToList()
            .ForEach(b => Fixture.Behaviors.Remove(b));
        Fixture.Behaviors.Add(new OmitOnRecursionBehavior());
    }
}
