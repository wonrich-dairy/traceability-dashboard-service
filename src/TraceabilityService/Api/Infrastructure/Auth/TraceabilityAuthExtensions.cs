using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
using SRC.Authorization;

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
            var signingKey = configuration["Auth:SigningKey"]
                ?? throw new InvalidOperationException(
                    "Auth:SigningKey not found. Set it with: " +
                    "dotnet user-secrets set \"Auth:SigningKey\" \"<shared signing key>\"");

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
                .SetFallbackPolicy(authenticatedUser)
                .AddPolicy("ManageUsers", policy =>
                    policy.RequireRole(WonrichRoles.SystemAdministrator))
                .AddPolicy("ProcessingTechnician", policy =>
                    policy.RequireRole(WonrichRoles.ProcessingTechnician, WonrichRoles.SystemAdministrator, WonrichRoles.ProductionManager))
                .AddPolicy("FactoryIntake", policy =>
                    policy.RequireRole(WonrichRoles.FactoryIntakeOfficer, WonrichRoles.ProcessingTechnician, WonrichRoles.SystemAdministrator))
                .AddPolicy("QualityAnalyst", policy =>
                    policy.RequireRole(WonrichRoles.QualityAnalyst, WonrichRoles.SystemAdministrator));
            return services;
        }

        public static IServiceCollection AddTraceabilityCors(this IServiceCollection services, IConfiguration configuration)
        {
            var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                          ?? ["http://localhost:5173", "http://127.0.0.1:5173"];

            services.AddCors(options =>
            {
                options.AddPolicy("TraceabilityCors", policy =>
                {
                    policy.WithOrigins(origins)
                          .AllowAnyHeader()
                          .AllowAnyMethod()
                          .AllowCredentials();
                });
            });

            return services;
        }

        public static IApplicationBuilder UseTraceabilityCors(this IApplicationBuilder app)
        {
            return app.UseCors("TraceabilityCors");
        }
    }
}
