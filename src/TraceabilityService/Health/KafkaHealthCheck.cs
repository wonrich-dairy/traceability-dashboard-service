using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TraceabilityService.Health;

/// <summary>
/// Checks that the Kafka broker is reachable and the topic Traceability reads exists,
/// by reading broker metadata. It does not produce or consume any messages.
/// Same check as Quality Lab Service, pointed at Processing's stage events.
/// </summary>
public class KafkaHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        var topic = configuration["Kafka:Topics:StageEvents"];

        if (string.IsNullOrWhiteSpace(bootstrapServers) || string.IsNullOrWhiteSpace(topic))
        {
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                "Kafka:BootstrapServers or Kafka:Topics:StageEvents is not configured."));
        }

        try
        {
            var adminConfig = new AdminClientConfig
            {
                BootstrapServers = bootstrapServers,
                SocketTimeoutMs = 5000
            };

            if (Enum.TryParse<SecurityProtocol>(configuration["Kafka:SecurityProtocol"], true, out var protocol))
                adminConfig.SecurityProtocol = protocol;
            if (Enum.TryParse<SaslMechanism>(configuration["Kafka:SaslMechanism"], true, out var mechanism))
                adminConfig.SaslMechanism = mechanism;
            if (!string.IsNullOrWhiteSpace(configuration["Kafka:SaslUsername"]))
            {
                adminConfig.SaslUsername = configuration["Kafka:SaslUsername"];
                adminConfig.SaslPassword = configuration["Kafka:SaslPassword"];
            }

            using var admin = new AdminClientBuilder(adminConfig).Build();
            var metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(5));
            var topicMetadata = metadata.Topics.FirstOrDefault();

            if (topicMetadata is null || topicMetadata.Error.IsError)
            {
                return Task.FromResult(new HealthCheckResult(
                    context.Registration.FailureStatus,
                    $"Kafka reachable, but topic '{topic}' is not available: {topicMetadata?.Error.Reason}"));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Kafka reachable at {bootstrapServers}; topic '{topic}' has {topicMetadata.Partitions.Count} partitions."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new HealthCheckResult(
                context.Registration.FailureStatus,
                $"Kafka unreachable at {bootstrapServers}.", ex));
        }
    }
}
