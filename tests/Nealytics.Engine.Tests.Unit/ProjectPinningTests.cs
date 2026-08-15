using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;

namespace Nealytics.Engine.Tests.Unit;

/// <summary>
/// Binding a project key to the project it may write to.
///
/// A valid key can write any <c>projectId</c> it likes, which means one leaked or copy-pasted key
/// writes into a neighbour's data and nothing anywhere objects. Pinning closes that.
///
/// <b>Off unless asked for, per key.</b> Every existing deployment sends whatever projectId it
/// sends, so enforcing this for everyone would be an outage dressed as a security improvement, and
/// a security control that arrives as an outage gets reverted rather than fixed. A key with no
/// entry behaves exactly as it does today.
///
/// <b>Only the project, never the tenant.</b> One edge worker legitimately serves every venue
/// through a single key -- that is the entire architecture -- so a tenant pin would be wrong by
/// design rather than merely strict.
/// </summary>
public class ProjectPinningTests
{
    private static ApiKeyValidator Validator(
        string keys = "k-one,k-two", params (string Key, string ProjectId)[] pins) =>
        new(
            Options.Create(new TelemetryEngineOptions
            {
                AllowedProjectKeys = keys,
                Projects = [.. pins.Select(p => new ProjectKeyOptions { Key = p.Key, ProjectId = p.ProjectId })],
            }),
            NullLogger<ApiKeyValidator>.Instance);

    [Fact]
    public void WithNoPins_AnyKeyMayWriteAnyProject()
    {
        // The migration-free default. Changing this would break every deployment on upgrade.
        ApiKeyValidator validator = Validator();

        validator.MayWriteProject("k-one", "anything").Should().BeTrue();
        validator.MayWriteProject("k-two", "something-else").Should().BeTrue();
    }

    [Fact]
    public void APinnedKeyMayWriteItsOwnProject()
    {
        Validator(pins: ("k-one", "shop"))
            .MayWriteProject("k-one", "shop").Should().BeTrue();
    }

    [Fact]
    public void APinnedKeyMayNotWriteAnother()
    {
        Validator(pins: ("k-one", "shop"))
            .MayWriteProject("k-one", "rival").Should().BeFalse();
    }

    [Fact]
    public void PinningOneKeyLeavesTheOthersAlone()
    {
        // Per key, so a deployment can adopt this one service at a time instead of all at once.
        ApiKeyValidator validator = Validator(pins: ("k-one", "shop"));

        validator.MayWriteProject("k-two", "anything").Should().BeTrue();
    }

    [Fact]
    public void TheComparisonIsCaseSensitive()
    {
        // projectId is a storage key compared verbatim everywhere else in this engine. A pin that
        // was laxer than the WHERE clause would let 'Shop' through and then never find its rows.
        Validator(pins: ("k-one", "shop"))
            .MayWriteProject("k-one", "Shop").Should().BeFalse();
    }

    [Fact]
    public void PinningAKeyThatCannotAuthenticateRefusesTheBoot()
    {
        // Dead security configuration is worse than none: it reads, in a review, as a control that
        // is in force.
        Action act = () => Validator(keys: "k-one", pins: ("k-missing", "shop"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedProjectKeys*");
    }

    [Fact]
    public void PinningTheSameKeyTwiceRefusesTheBoot()
    {
        Action act = () => Validator("k-one,k-two", ("k-one", "shop"), ("k-one", "rival"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*more than one project*");
    }

    [Theory]
    [InlineData("", "shop")]
    [InlineData("k-one", "")]
    public void AHalfWrittenPinRefusesTheBoot(string key, string projectId)
    {
        // Ignoring it would leave a line of configuration that looks like a pin and protects
        // nothing.
        Action act = () => Validator(pins: (key, projectId));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PinningDoesNotChangeWhetherAKeyIsValid()
    {
        // Authentication and authorization stay separate: a pinned key is still just a valid key,
        // and an unknown key is still 401 rather than 400.
        ApiKeyValidator validator = Validator(pins: ("k-one", "shop"));

        validator.IsValid("k-one").Should().BeTrue();
        validator.IsValid("k-two").Should().BeTrue();
        validator.IsValid("nope").Should().BeFalse();
    }
}
