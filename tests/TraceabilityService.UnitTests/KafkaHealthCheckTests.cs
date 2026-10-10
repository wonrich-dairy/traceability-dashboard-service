using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TraceabilityService.Health;

namespace TraceabilityService.UnitTests;

public class KafkaHealthCheckTests
{
    private static HealthCheckContext ContextFor(IHealthCheck check, HealthStatus failureStatus) =>
        new()
        {
            Registration = new HealthCheckRegistration("kafka", check, failureStatus, tags: null)
        };

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public async Task Returns_configured_failure_status_when_settings_are_missing()
    {
        var check = new KafkaHealthCheck(Config(new()));

        var result = await check.CheckHealthAsync(ContextFor(check, HealthStatus.Degraded));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("not configured", result.Description);
    }

    [Fact]
    public async Task Returns_configured_failure_status_when_broker_is_unreachable()
    {
        var check = new KafkaHealthCheck(Config(new()
        {
            ["Kafka:BootstrapServers"] = "localhost:1",   // nothing listens here
            ["Kafka:Topics:StageEvents"] = "wonrich.processing.stage-events.v1"
        }));

        var result = await check.CheckHealthAsync(ContextFor(check, HealthStatus.Unhealthy));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Applies_sasl_settings_and_still_reports_an_unreachable_broker()
    {
        var check = new KafkaHealthCheck(Config(new()
        {
            ["Kafka:BootstrapServers"] = "localhost:1",
            ["Kafka:Topics:StageEvents"] = "wonrich.processing.stage-events.v1",
            ["Kafka:SecurityProtocol"] = "SaslPlaintext",
            ["Kafka:SaslMechanism"] = "Plain",
            ["Kafka:SaslUsername"] = "user",
            ["Kafka:SaslPassword"] = "password"
        }));

        var result = await check.CheckHealthAsync(ContextFor(check, HealthStatus.Degraded));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("unreachable", result.Description);
    }
}
