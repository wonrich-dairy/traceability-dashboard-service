using Confluent.Kafka;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// The consume loop every consumer shares. It subscribes as the consumer's group and commits
/// each offset only once <see cref="EventProcessor"/> has handled the message or parked it on
/// the dead-letter topic, so a crash in between means it is read again, never skipped. A
/// re-read is safe: the processor skips an event its group has already handled.
/// </summary>
public sealed class KafkaEventConsumer(
    EventProcessor processor,
    IConfiguration configuration,
    ILogger<KafkaEventConsumer> logger) : BackgroundService
{
    // How long to wait before reading a message again when it could not be dead-lettered,
    // or after the broker could not be read.
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(5);

    private readonly KafkaConsumerOptions _options = processor.Options;

    // Consume blocks its thread, so the loop gets one of its own rather than a pool thread.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(
            () => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)
        .Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("{Consumer} consumer disabled (Kafka:Consumers:Enabled is false).", _options.Name);
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration["Kafka:BootstrapServers"]))
        {
            logger.LogWarning("{Consumer} consumer not started: Kafka:BootstrapServers is not configured.", _options.Name);
            return;
        }

        var config = KafkaClientSettings.Apply(new ConsumerConfig
        {
            GroupId = _options.ConsumerGroup,
            EnableAutoCommit = false,
            // A new group starts from the oldest retained event, so the read model is built from all of them.
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }, configuration);

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) => logger.LogWarning("{Consumer} Kafka error: {Reason}", _options.Name, error.Reason))
            .Build();

        consumer.Subscribe(_options.Topic);
        logger.LogInformation("{Consumer} consumer reading {Topic} as {ConsumerGroup}; dead letters to {DeadLetterTopic}",
            _options.Name, _options.Topic, _options.ConsumerGroup, _options.DeadLetterTopic);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? result;
                try
                {
                    result = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException ex)
                {
                    logger.LogError(ex, "Could not read from {Topic}", _options.Topic);
                    await Task.Delay(Pause, stoppingToken);
                    continue;
                }

                if (result is null || result.IsPartitionEOF)
                {
                    continue;
                }

                try
                {
                    await processor.ProcessAsync(result, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Neither handled nor parked, usually because the dead-letter publish failed too.
                    // Not committed: rewind and read it again, so the message is never lost.
                    logger.LogError(ex,
                        "Message at {Topic} [{Partition}] @{Offset} was not handled or dead-lettered; reading it again in {Pause}",
                        result.Topic, result.Partition.Value, result.Offset.Value, Pause);
                    TryRewind(consumer, result);
                    await Task.Delay(Pause, stoppingToken);
                    continue;
                }

                try
                {
                    consumer.Commit(result);
                }
                catch (KafkaException ex)
                {
                    // E.g. the partition moved to another instance. That instance reads the message
                    // again, and the processor recognises it as already handled.
                    logger.LogWarning(ex, "Could not commit {Topic} [{Partition}] @{Offset}",
                        result.Topic, result.Partition.Value, result.Offset.Value);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            consumer.Close();
        }
    }

    private void TryRewind(IConsumer<string, string> consumer, ConsumeResult<string, string> result)
    {
        try
        {
            consumer.Seek(result.TopicPartitionOffset);
        }
        catch (KafkaException ex)
        {
            // The partition was revoked meanwhile; whoever has it now starts from the last commit.
            logger.LogWarning(ex, "Could not rewind {Topic} [{Partition}]", result.Topic, result.Partition.Value);
        }
    }
}
