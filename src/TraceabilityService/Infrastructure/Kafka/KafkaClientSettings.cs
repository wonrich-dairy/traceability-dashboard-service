using Confluent.Kafka;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// The broker address and credentials from the Kafka section, for consumers and producers.
/// SASL is only set when the protocol is not Plaintext, as in Quality Lab and Processing.
/// </summary>
public static class KafkaClientSettings
{
    public static T Apply<T>(T config, IConfiguration configuration) where T : ClientConfig
    {
        config.BootstrapServers = configuration["Kafka:BootstrapServers"];
        config.ClientId = "traceability-service";

        if (Enum.TryParse<SecurityProtocol>(configuration["Kafka:SecurityProtocol"], ignoreCase: true, out var protocol)
            && protocol != SecurityProtocol.Plaintext)
        {
            config.SecurityProtocol = protocol;
            config.SaslMechanism = Enum.TryParse<SaslMechanism>(configuration["Kafka:SaslMechanism"], ignoreCase: true, out var mechanism)
                ? mechanism
                : SaslMechanism.Plain;
            config.SaslUsername = configuration["Kafka:SaslUsername"];
            config.SaslPassword = configuration["Kafka:SaslPassword"];
        }

        return config;
    }
}
