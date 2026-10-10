using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;

namespace TraceabilityService.Api.Infrastructure
{
    /// <summary>
    /// Authentication and authorization wiring using shared Auth Service, copied from Processing Service (SCRUM-56).
    /// Tokens issued by Auth Service, validated here independently (no call-out).
    /// Uses EXACT shared files from shared/Auth (WonrichRoles, WonrichClaims) to avoid role name drift.
    /// </summary>
    public static class TraceabilityAuthExtensions
    {
        public static IServiceCollection AddTraceabilityAuthentication(this IServiceCollection services, IConfiguration configuration)
        {
            var issuer = configuration["Auth:Issuer"] ?? "wonrich-auth";
            var audience = configuration["Auth:Audience"] ?? "wonrich-services";
            // No fallback: a default key in source would let anyone who reads the repo mint valid tokens
            var signingKey = configuration["Auth:SigningKey"];
            if (string.IsNullOrWhiteSpace(signingKey))
            {
                throw new InvalidOperationException(
                    "Auth:SigningKey is not configured. "
                    + "Set it in .env (Docker), user secrets (dotnet run) or App Service settings (Azure).");
            }

            if (Encoding.UTF8.GetByteCount(signingKey) < 32)
            {
                throw new InvalidOperationException(
                    "Auth:SigningKey is too short. HS256 needs at least 32 bytes; "
                    + "use the same signing key as the Auth Service.");
            }

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = issuer,
                        ValidateAudience = true,
                        ValidAudience = audience,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                        ValidateLifetime = true,
                        // Auth service sets ClockSkew = Zero (see WonrichJwtOptions.cs) - match it exactly
                        ClockSkew = TimeSpan.Zero,
                        // CONFIRMED by AccessTokenIssuer.cs: uses ClaimTypes.NameIdentifier, ClaimTypes.Name, ClaimTypes.Role, WonrichClaims.Facility
                        RoleClaimType = ClaimTypes.Role,
                        NameClaimType = ClaimTypes.NameIdentifier
                    };
                });

            return services;
        }

        public static IServiceCollection AddTraceabilityAuthorization(this IServiceCollection services)
        {
            // Deny by default: any endpoint without [Authorize]/[AllowAnonymous] still needs a valid token.
            // Anonymous endpoints (/health, /metrics) opt out explicitly with .AllowAnonymous().
            var authenticatedUser = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .Build();

            services.AddAuthorizationBuilder()
                .SetDefaultPolicy(authenticatedUser)
                .SetFallbackPolicy(authenticatedUser);
            return services;
        }
    }
}
