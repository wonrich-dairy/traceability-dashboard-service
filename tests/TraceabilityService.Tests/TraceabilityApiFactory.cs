using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TraceabilityService.Tests;

/// <summary>
/// WebApplicationFactory as in Processing's HealthTests, plus the config Program refuses to start without.
/// UseSetting rather than ConfigureAppConfiguration: Program reads Auth:SigningKey before Build(),
/// and only host settings are visible that early.
/// </summary>
public sealed class TraceabilityApiFactory : WebApplicationFactory<Program>
{
    public const string SigningKey = "traceability-tests-signing-key-not-used-anywhere-else";
    public const string Issuer = "wonrich-auth";
    public const string Audience = "wonrich-services";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: keeps a developer's user-secrets signing key out of the test run
        builder.UseEnvironment("Testing");
        builder.UseSetting("Auth:SigningKey", SigningKey);
        builder.UseSetting("Auth:Issuer", Issuer);
        builder.UseSetting("Auth:Audience", Audience);
        builder.UseSetting("ConnectionStrings:Default", "Server=unused;Database=unused");
    }
}
