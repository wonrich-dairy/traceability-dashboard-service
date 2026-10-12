namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// Where the lab determination consumer reads from and dead-letters to (SCRUM-131). The
/// handler records processed events under the same consumer group, so the two cannot differ.
/// </summary>
public sealed record BatchDeterminationConsumerOptions
{
    public const string DefaultTopic = "wonrich.quality-lab.batch-determinations.v1";

    // Registered in wonrich-infra (kafka/topics.env, docs/kafka.md) by SCRUM-126. The brokers
    // do not create topics on first use, so a name not registered there cannot be published to.
    public const string DefaultConsumerGroup = "traceability-batch-determinations";
    public const string DefaultDeadLetterTopic = "wonrich.dlq.traceability-batch-determinations.v1";

    public string Topic { get; init; } = DefaultTopic;

    public string ConsumerGroup { get; init; } = DefaultConsumerGroup;

    public string DeadLetterTopic { get; init; } = DefaultDeadLetterTopic;

    // Off in the integration tests, which have no broker.
    public bool Enabled { get; init; } = true;

    // Retries after the first attempt, waiting RetryBaseDelay, then twice that, then four times.
    public int MaxRetries { get; init; } = 3;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public static BatchDeterminationConsumerOptions From(IConfiguration configuration) => new()
    {
        Topic = ValueOr(configuration["Kafka:Topics:BatchDeterminations"], DefaultTopic),
        DeadLetterTopic = ValueOr(configuration["Kafka:Topics:BatchDeterminationsDeadLetter"], DefaultDeadLetterTopic),
        ConsumerGroup = ValueOr(configuration["Kafka:ConsumerGroups:BatchDeterminations"], DefaultConsumerGroup),
        Enabled = configuration.GetValue("Kafka:Consumers:Enabled", true),
    };

    private static string ValueOr(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
