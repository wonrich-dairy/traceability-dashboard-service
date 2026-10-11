using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TraceabilityService.Api.Infrastructure.Persistence;

namespace TraceabilityService.IntegrationTests;

[Collection(ApiCollection.Name)]
public class HealthEndpointTests(TraceabilityApiFactory factory)
{
    [Fact]
    public async Task Health_returns_200_and_database_check_is_healthy()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();

        var mysql = checks.Single(c => c.GetProperty("name").GetString() == "mysql");
        Assert.Equal("Healthy", mysql.GetProperty("status").GetString());

        // Kafka has no broker in CI, so it is Degraded by design and must not fail the request.
        var kafka = checks.Single(c => c.GetProperty("name").GetString() == "kafka");
        Assert.Equal("Degraded", kafka.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Version_endpoint_reports_the_build_commit()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/version");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sha", body);
    }

    [Fact]
    public async Task Startup_applies_all_migrations_to_an_empty_database()
    {
        _ = factory.CreateClient(); // starts the app, which migrates on startup

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        var pending = await db.Database.GetPendingMigrationsAsync();
        var applied = await db.Database.GetAppliedMigrationsAsync();

        // The skeleton has no migrations yet; once the data model lands this proves they all apply.
        Assert.Empty(pending);
        Assert.Equal(applied.Count(), applied.Distinct().Count());
    }

    [Fact]
    public async Task Metrics_endpoint_is_anonymous_and_exposes_the_service_metrics()
    {
        var client = factory.CreateClient();
        await client.GetAsync("/version"); // makes sure at least one request has been counted

        var response = await client.GetAsync("/metrics");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("traceability_http_requests_total", body);
    }

    [Fact]
    public async Task Responses_carry_a_correlation_id_and_reuse_the_callers()
    {
        var client = factory.CreateClient();

        var generated = await client.GetAsync("/version");
        Assert.True(generated.Headers.Contains("X-Correlation-ID"));

        var request = new HttpRequestMessage(HttpMethod.Get, "/version");
        request.Headers.Add("X-Correlation-ID", "caller-id-123");
        var echoed = await client.SendAsync(request);
        Assert.Equal("caller-id-123", echoed.Headers.GetValues("X-Correlation-ID").Single());
    }
}
