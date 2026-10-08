using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.IdentityModel.Tokens;

namespace Nealytics.Engine.Tests.Unit;

public class UnchangedResponseShapeTests : IClassFixture<NoDatabaseWebFactory>
{
    private readonly HttpClient _client;

    public UnchangedResponseShapeTests(NoDatabaseWebFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ADeploymentUsingNoNewFeature_GetsTheSchemaItAlwaysGot()
    {
        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(NoDatabaseWebFactory.JwtKey));
        JwtSecurityToken token = new(
            claims: [new Claim("project_id", "p"), new Claim("tenant_id", "t")],
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/schema");
        request.Headers.Add("Authorization", $"Bearer {new JwtSecurityTokenHandler().WriteToken(token)}");

        HttpResponseMessage response = await _client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("tenantAttributes").And.NotContain("tenantSet").And.NotContain("aliasEventType")
            .And.NotContain("unitDimension").And.NotContain("serverOnly");
    }
}
