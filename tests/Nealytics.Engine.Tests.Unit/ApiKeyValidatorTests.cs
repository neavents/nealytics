using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;
using Nealytics.Engine.Tests.Shared.Base;
using NSubstitute;

namespace Nealytics.Engine.Tests.Unit;

public class ApiKeyValidatorTests : UnitTestBase
{
    private readonly IOptions<TelemetryEngineOptions> _options;
    private readonly ILogger<ApiKeyValidator> _logger;
    private readonly TelemetryEngineOptions _engineOptions;

    public ApiKeyValidatorTests()
    {
        _engineOptions = new TelemetryEngineOptions();
        _options = Substitute.For<IOptions<TelemetryEngineOptions>>();
        _options.Value.Returns(_engineOptions);
        _logger = Fixture.Freeze<ILogger<ApiKeyValidator>>();
    }

    private ApiKeyValidator CreateSut()
    {
        return new ApiKeyValidator(_options, _logger);
    }

    [Fact]
    public void Constructor_WithValidKeys_ParsesKeysToFrozenSet()
    {
        _engineOptions.AllowedProjectKeys = "key-a,key-b,key-c";

        var sut = CreateSut();

        sut.IsValid("key-a").Should().BeTrue();
        sut.IsValid("key-b").Should().BeTrue();
        sut.IsValid("key-c").Should().BeTrue();
        sut.IsValid("not-a-key").Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithEmptyString_ReturnsValidatorThatRejectsAll()
    {
        _engineOptions.AllowedProjectKeys = string.Empty;

        var sut = CreateSut();

        sut.IsValid("anything").Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithNullString_ReturnsValidatorThatRejectsAll()
    {
        _engineOptions.AllowedProjectKeys = null!;

        var sut = CreateSut();

        sut.IsValid("anything").Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithWhitespaceOnly_ReturnsValidatorThatRejectsAll()
    {
        _engineOptions.AllowedProjectKeys = "   ";

        var sut = CreateSut();

        sut.IsValid("anything").Should().BeFalse();
    }

    [Fact]
    public void Constructor_TrimsWhitespaceAroundKeys()
    {
        _engineOptions.AllowedProjectKeys = " key-one , key-two ,  key-three  ";

        var sut = CreateSut();

        sut.IsValid("key-one").Should().BeTrue();
        sut.IsValid("key-two").Should().BeTrue();
        sut.IsValid("key-three").Should().BeTrue();
        sut.IsValid(" key-one ").Should().BeFalse("keys should be exact match, trimmed");
    }

    [Fact]
    public void Constructor_WithSingleKey_OnlyThatKeyIsValid()
    {
        _engineOptions.AllowedProjectKeys = "sole-key";

        var sut = CreateSut();

        sut.IsValid("sole-key").Should().BeTrue();
        sut.IsValid("sole-key ").Should().BeFalse();
    }

    [Fact]
    public void Constructor_SkipsEmptyEntriesBetweenCommas()
    {
        _engineOptions.AllowedProjectKeys = "key-a,,key-b,,,key-c";

        var sut = CreateSut();

        sut.IsValid("key-a").Should().BeTrue();
        sut.IsValid("key-b").Should().BeTrue();
        sut.IsValid("key-c").Should().BeTrue();
    }

    [Fact]
    public void IsValid_WithNullKey_ReturnsFalse()
    {
        _engineOptions.AllowedProjectKeys = "key-a";
        var sut = CreateSut();

        var result = sut.IsValid(null!);

        result.Should().BeFalse();
    }

    [Fact]
    public void IsValid_WithEmptyString_ReturnsFalse()
    {
        _engineOptions.AllowedProjectKeys = "key-a";
        var sut = CreateSut();

        var result = sut.IsValid(string.Empty);

        result.Should().BeFalse();
    }
}
