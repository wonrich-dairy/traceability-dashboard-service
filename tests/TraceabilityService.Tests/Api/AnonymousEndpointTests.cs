using System.Net;
using Xunit;

namespace TraceabilityService.Tests.Api;

/// <summary>
/// The endpoints that opt out of the deny-by-default policy: container probes and Prometheus
/// scrapes carry no token.
/// </summary>
public sealed class AnonymousEndpointTests : IClassFixture<TraceabilityApiFactory>
{
    private readonly TraceabilityApiFactory _factory;

    public AnonymousEndpointTests(TraceabilityApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Health_without_token_returns_200()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Metrics_without_token_returns_200_with_request_metrics()
    {
        var client = _factory.CreateClient();

        // One request first, so the counter has a value to export
        await client.GetAsync("/health");
        var response = await client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("traceability_http_requests_total", await response.Content.ReadAsStringAsync());
    }
}
