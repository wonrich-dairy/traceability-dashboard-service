namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// One consumer's topic, group and dead-letter topic. The topics and groups are registered in
/// wonrich-infra (kafka/topics.env, docs/kafka.md); the brokers do not create topics on first
/// use, so a name not registered there cannot be read or published to.
/// </summary>
public sealed record KafkaConsumerOptions
{
    // The configuration key and the name in logs, e.g. BatchDeterminations.
    public required string Name { get; init; }

    public required string Topic { get; init; }

    // Also the group processed_messages records the consumer's events under.
    public required string ConsumerGroup { get; init; }

    public required string DeadLetterTopic { get; init; }

    // Off in the integration tests, which have no broker.
    public bool Enabled { get; init; } = true;

    // Retries after the first attempt, waiting RetryBaseDelay, then twice that, then four times.
    public int MaxRetries { get; init; } = 3;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Reads Kafka:Topics:{name}, Kafka:Topics:{name}DeadLetter, Kafka:ConsumerGroups:{name}
    /// and Kafka:Consumers:Enabled, falling back to the registered names.
    /// </summary>
    public static KafkaConsumerOptions From(
        IConfiguration configuration, string name, string topic, string consumerGroup, string deadLetterTopic) => new()
    {
        Name = name,
        Topic = ValueOr(configuration[$"Kafka:Topics:{name}"], topic),
        DeadLetterTopic = ValueOr(configuration[$"Kafka:Topics:{name}DeadLetter"], deadLetterTopic),
        ConsumerGroup = ValueOr(configuration[$"Kafka:ConsumerGroups:{name}"], consumerGroup),
        Enabled = configuration.GetValue("Kafka:Consumers:Enabled", true),
    };

    private static string ValueOr(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
