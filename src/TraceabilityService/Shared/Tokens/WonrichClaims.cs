using System.Security.Claims;

namespace Wonrich.Auth.Tokens;

/// <summary>
/// Claim types the Wonrich services agree on, beyond the standard JWT registered ones.
/// This file is an EXACT COPY from Auth shared service (shared/Auth/WonrichClaims.cs) - must stay in sync.
/// </summary>
public static class WonrichClaims
{
    public const string Facility = "facility";
}

public static class WonrichPrincipalExtensions
{
    public static string? UserId(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string? UserName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Name);

    public static string? Facility(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(WonrichClaims.Facility);

    public static string? Role(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Role);
}
