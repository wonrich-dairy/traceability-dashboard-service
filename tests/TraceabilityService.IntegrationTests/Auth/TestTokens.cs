using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SRC.Authorization;
using Wonrich.Auth.Tokens;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// Mints tokens the way Auth Service's AccessTokenIssuer does: same claims, same JwtSecurityTokenHandler,
/// so the outbound claim renaming (ClaimTypes.Role -> "role" etc.) matches real tokens.
/// </summary>
internal static class TestTokens
{
    public const string UserId = "user-42";
    public const string UserName = "qa.analyst";
    public const string Role = WonrichRoles.QualityAnalyst;
    public const string Facility = "FACTORY-01";

    public static string Create(
        string signingKey = TraceabilityApiFactory.SigningKey,
        string issuer = TraceabilityApiFactory.Issuer,
        string audience = TraceabilityApiFactory.Audience,
        DateTime? issuedAt = null,
        TimeSpan? lifetime = null)
    {
        var notBefore = issuedAt ?? DateTime.UtcNow;
        var expires = notBefore + (lifetime ?? TimeSpan.FromMinutes(60));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, UserId),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier, UserId),
            new(ClaimTypes.Name, UserName),
            new(ClaimTypes.Role, Role),
            new(WonrichClaims.Facility, Facility)
        };

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(issuer, audience, claims, notBefore, expires, credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
