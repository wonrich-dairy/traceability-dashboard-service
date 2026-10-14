using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SRC.Authorization;
using TraceabilityService.Api.Infrastructure;

namespace TraceabilityService.UnitTests;

public class TraceabilityAuthExtensionsTests
{
    // HS256 needs a key of at least 32 bytes; these sit either side of that limit.
    private const string Key31Bytes = "0123456789abcdef0123456789abcde";
    private const string Key32Bytes = "0123456789abcdef0123456789abcdef";

    private static IConfiguration ConfigWithSigningKey(string? signingKey)
    {
        var values = new Dictionary<string, string?>();
        if (signingKey is not null)
        {
            values["Auth:SigningKey"] = signingKey;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(null)]          // not configured at all
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(Key31Bytes)]
    public void Throws_when_signing_key_is_missing_blank_or_shorter_than_32_bytes(string? signingKey)
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddTraceabilityAuthentication(ConfigWithSigningKey(signingKey)));

        Assert.Contains("Auth:SigningKey", ex.Message);
    }

    [Fact]
    public void Accepts_a_32_byte_signing_key()
    {
        var services = new ServiceCollection();

        var ex = Record.Exception(() =>
            services.AddTraceabilityAuthentication(ConfigWithSigningKey(Key32Bytes)));

        Assert.Null(ex);
    }

    [Fact]
    public async Task Quality_control_policy_requires_exactly_quality_control_officer_or_system_administrator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTraceabilityAuthorization();
        var provider = services.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(TraceabilityPolicies.QualityControl);

        Assert.NotNull(policy);
        var roles = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(
            [WonrichRoles.QualityControlOfficer, WonrichRoles.SystemAdministrator],
            roles.AllowedRoles.Order());
    }

    [Fact]
    public async Task Admin_only_policy_requires_exactly_system_administrator()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTraceabilityAuthorization();
        var provider = services.BuildServiceProvider().GetRequiredService<IAuthorizationPolicyProvider>();

        var policy = await provider.GetPolicyAsync(TraceabilityPolicies.AdminOnly);

        Assert.NotNull(policy);
        var roles = Assert.Single(policy.Requirements.OfType<RolesAuthorizationRequirement>());
        Assert.Equal(
            [WonrichRoles.SystemAdministrator],
            roles.AllowedRoles.Order());
    }
}
