using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Nealytics.Engine.Infrastructure.Configuration;
using Nealytics.Engine.Infrastructure.Security;

namespace Nealytics.Engine.Tests.Unit;

public class ReadAuthHardeningTests
{
    private const string Symmetric = "read-auth-test-signing-key-at-least-32-bytes!!";

    private static string Token(SigningCredentials credentials, string? issuer = null, string? audience = null, string? keyId = null)
    {
        if (keyId is not null)
        {
            credentials.Key.KeyId = keyId;
        }

        JwtSecurityToken token = new(
            issuer: issuer,
            audience: audience,
            claims: [new Claim("project_id", "p"), new Claim("tenant_id", "t")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static SigningCredentials Hmac() =>
        new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Symmetric)), SecurityAlgorithms.HmacSha256);

    private static async Task<bool> ValidAsync(string token, TokenValidationParameters parameters) =>
        (await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters)).IsValid;

    [Fact]
    public async Task TheDefaultConfiguration_AcceptsAnyIssuerAndAudience_AsBefore()
    {
        TelemetryEngineOptions options = new() { JwtSymmetricKey = Symmetric };
        TokenValidationParameters parameters = ReadTokenKeys.From(options).ValidationParameters(options);

        (await ValidAsync(Token(Hmac(), "anyone", "anything"), parameters)).Should().BeTrue();
        parameters.ValidateIssuer.Should().BeFalse();
        parameters.ValidateAudience.Should().BeFalse();
    }

    [Fact]
    public async Task AConfiguredIssuerAndAudience_AreEnforced()
    {
        TelemetryEngineOptions options = new() { JwtSymmetricKey = Symmetric, JwtIssuer = "https://id.example", JwtAudience = "analytics" };
        TokenValidationParameters parameters = ReadTokenKeys.From(options).ValidationParameters(options);

        (await ValidAsync(Token(Hmac(), "https://id.example", "analytics"), parameters)).Should().BeTrue();
        (await ValidAsync(Token(Hmac(), "https://evil.example", "analytics"), parameters)).Should().BeFalse();
        (await ValidAsync(Token(Hmac(), "https://id.example", "billing"), parameters)).Should().BeFalse();
        (await ValidAsync(Token(Hmac()), parameters)).Should().BeFalse();
    }

    [Fact]
    public async Task APemPublicKey_VerifiesRs256Tokens_WithoutASymmetricKey()
    {
        using RSA rsa = RSA.Create(2048);
        TelemetryEngineOptions options = new() { JwtPublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem() };
        TokenValidationParameters parameters = ReadTokenKeys.From(options).ValidationParameters(options);

        (await ValidAsync(Token(new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)), parameters))
            .Should().BeTrue();

        using RSA other = RSA.Create(2048);
        (await ValidAsync(Token(new SigningCredentials(new RsaSecurityKey(other), SecurityAlgorithms.RsaSha256)), parameters))
            .Should().BeFalse();
        (await ValidAsync(Token(Hmac()), parameters)).Should().BeFalse();
    }

    [Fact]
    public async Task AnEcPemPublicKey_VerifiesEs256Tokens()
    {
        using ECDsa ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TelemetryEngineOptions options = new() { JwtPublicKeyPem = ec.ExportSubjectPublicKeyInfoPem() };
        TokenValidationParameters parameters = ReadTokenKeys.From(options).ValidationParameters(options);

        (await ValidAsync(Token(new SigningCredentials(new ECDsaSecurityKey(ec), SecurityAlgorithms.EcdsaSha256)), parameters))
            .Should().BeTrue();
    }

    private sealed class JwksHandler(string document) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(document) });
        }
    }

    [Fact]
    public async Task AJwksUrl_VerifiesTokensSignedByAPublishedKey()
    {
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signing = new(rsa) { KeyId = "k1" };
        JsonWebKey published = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = "k1" });
        string document = "{\"keys\":[" + System.Text.Json.JsonSerializer.Serialize(new
        {
            kty = published.Kty, kid = published.Kid, n = published.N, e = published.E, use = "sig", alg = "RS256",
        }) + "]}";

        TelemetryEngineOptions options = new() { JwtJwksUrl = "https://id.example/.well-known/jwks.json" };
        ReadTokenKeys keys = ReadTokenKeys.From(options);
        JwksHandler handler = new(document);
        JwksKeyCache cache = new(keys.JwksAddress!, TimeSpan.FromHours(1), new HttpClient(handler), NullLogger.Instance);
        keys.UseJwks(cache);
        TokenValidationParameters parameters = keys.ValidationParameters(options);

        string token = Token(new SigningCredentials(signing, SecurityAlgorithms.RsaSha256));
        (await ValidAsync(token, parameters)).Should().BeFalse("no key has been fetched yet");

        await cache.RefreshAsync(CancellationToken.None);

        (await ValidAsync(token, parameters)).Should().BeTrue();
        handler.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("http://id.example/jwks.json")]
    [InlineData("not a url")]
    public void APlainHttpOrMalformedJwksUrl_RefusesTheBoot(string url)
    {
        Action act = () => ReadTokenKeys.From(new TelemetryEngineOptions { JwtJwksUrl = url });
        act.Should().Throw<InvalidOperationException>().WithMessage("*JwtJwksUrl*");
    }

    [Fact]
    public void ALoopbackHttpJwksUrl_IsAccepted()
    {
        ReadTokenKeys.From(new TelemetryEngineOptions { JwtJwksUrl = "http://127.0.0.1:8080/jwks" }).JwksAddress.Should().NotBeNull();
    }

    [Fact]
    public void NoKeyAtAll_RefusesTheBoot_WithTheMessageOperatorsKnow()
    {
        Action act = () => ReadTokenKeys.From(new TelemetryEngineOptions());
        act.Should().Throw<InvalidOperationException>().WithMessage("TelemetryEngine:JwtSymmetricKey must be at least 32 bytes*");
    }

    [Fact]
    public void AShortSymmetricKey_RefusesTheBoot_EvenBesideAPem()
    {
        using RSA rsa = RSA.Create(2048);
        Action act = () => ReadTokenKeys.From(new TelemetryEngineOptions { JwtSymmetricKey = "short", JwtPublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem() });
        act.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 bytes*");
    }

    [Fact]
    public void ABrokenPem_RefusesTheBoot()
    {
        Action act = () => ReadTokenKeys.From(new TelemetryEngineOptions { JwtPublicKeyPem = "-----BEGIN PUBLIC KEY-----\nnope\n-----END PUBLIC KEY-----" });
        act.Should().Throw<InvalidOperationException>().WithMessage("*JwtPublicKeyPem*");
    }

    private static ClaimsPrincipal WithScopes(params string[] values) =>
        new(new ClaimsIdentity(values.Select(v => new Claim("scope", v)), "test"));

    [Fact]
    public void ReadScopes_GrantListedEndpointsOnly()
    {
        ReadScopeFilter.Grants(WithScopes("breakdown pivot"), "scope", "pivot").Should().BeTrue();
        ReadScopeFilter.Grants(WithScopes("breakdown pivot"), "scope", "timeline").Should().BeFalse();
        ReadScopeFilter.Grants(WithScopes("breakdown", "timeline"), "scope", "timeline").Should().BeTrue();
        ReadScopeFilter.Grants(WithScopes("*"), "scope", "funnel").Should().BeTrue();
        ReadScopeFilter.Grants(WithScopes(), "scope", "funnel").Should().BeFalse();
    }
}
