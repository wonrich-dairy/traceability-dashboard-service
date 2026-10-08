using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace TraceabilityService.Tests.Api;

/// <summary>
/// SCRUM-119 auth ACs: unauthenticated requests rejected, valid Auth Service JWTs accepted with
/// user id, role and facility available to the endpoint. /api/me has no [Authorize], so the
/// 401 cases also prove the deny-by-default fallback policy.
/// </summary>
public sealed class AuthTests : IClassFixture<TraceabilityApiFactory>
{
    private readonly TraceabilityApiFactory _factory;

    public AuthTests(TraceabilityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Me_without_token_returns_401()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_valid_token_returns_200_and_claims()
    {
        var response = await GetMeWith(TestTokens.Create());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(TestTokens.UserId, body.RootElement.GetProperty("userId").GetString());
        Assert.Equal(TestTokens.UserName, body.RootElement.GetProperty("userName").GetString());
        Assert.Equal(TestTokens.Role, body.RootElement.GetProperty("role").GetString());
        Assert.Equal(TestTokens.Facility, body.RootElement.GetProperty("facility").GetString());
    }

    [Fact]
    public async Task Me_with_token_signed_by_wrong_key_returns_401()
    {
        var token = TestTokens.Create(signingKey: "some-other-signing-key-also-at-least-32-chars");
        var response = await GetMeWith(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("not-wonrich-auth", TraceabilityApiFactory.Audience)]
    [InlineData(TraceabilityApiFactory.Issuer, "not-wonrich-services")]
    public async Task Me_with_wrong_issuer_or_audience_returns_401(string issuer, string audience)
    {
        var response = await GetMeWith(TestTokens.Create(issuer: issuer, audience: audience));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_with_expired_token_returns_401()
    {
        // Expired one minute ago; ClockSkew is zero, so there is no grace period to fall into
        var token = TestTokens.Create(issuedAt: DateTime.UtcNow.AddMinutes(-61), lifetime: TimeSpan.FromMinutes(60));
        var response = await GetMeWith(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpResponseMessage> GetMeWith(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync("/api/me");
    }
}
