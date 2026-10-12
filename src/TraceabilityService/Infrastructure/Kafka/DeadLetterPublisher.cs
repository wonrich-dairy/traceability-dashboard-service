using Confluent.Kafka;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>Publishes a message to a dead-letter topic. Throws if the broker does not take it.</summary>
public interface IDeadLetterPublisher
{
    Task PublishAsync(string topic, Message<string, string> message, CancellationToken cancellationToken);
}

/// <summary>
/// Kafka producer for dead letters, with the same settings as the upstream services' relays:
/// acks=all and idempotent, so a parked message is never lost or doubled. Built on first use,
/// so an instance without a broker never creates one.
/// </summary>
public sealed class KafkaDeadLetterPublisher(IConfiguration configuration) : IDeadLetterPublisher, IDisposable
{
    private readonly Lazy<IProducer<string, string>> _producer = new(() =>
        new ProducerBuilder<string, string>(KafkaClientSettings.Apply(new ProducerConfig
        {
            Acks = Acks.All,
            EnableIdempotence = true,
            // A topic the broker does not have fails in seconds rather than librdkafka's five minutes.
            MessageTimeoutMs = 10000,
        }, configuration)).Build());

    public async Task PublishAsync(string topic, Message<string, string> message, CancellationToken cancellationToken)
    {
        var result = await _producer.Value.ProduceAsync(topic, message, cancellationToken);
        if (result.Status != PersistenceStatus.Persisted)
        {
            throw new KafkaException(new Error(ErrorCode.Local_MsgTimedOut, $"Dead letter to {topic} was not persisted ({result.Status})."));
        }
    }

    public void Dispose()
    {
        if (_producer.IsValueCreated)
        {
            _producer.Value.Flush(TimeSpan.FromSeconds(5));
            _producer.Value.Dispose();
        }
    }
}
