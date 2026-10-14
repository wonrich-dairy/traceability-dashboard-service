using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using TraceabilityService.Api.Contracts;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// Inspects dead-letter topics and replays failed messages (SCRUM-140).
/// Each consumer group has its own dead-letter topic where unprocessable messages are parked.
/// </summary>
public class KafkaDeadLetterService(
    IConfiguration configuration,
    TraceabilityDbContext db,
    IAuditTrailService audit,
    ILogger<KafkaDeadLetterService> logger) : IDeadLetterService
{
    private sealed record GroupMapping(string ConsumerGroup, string SourceTopic, string DeadLetterTopic);

    private IReadOnlyList<GroupMapping> GetMappings()
    {
        return
        [
            new(
                configuration["Kafka:ConsumerGroups:StageEvents"] ?? "traceability-stage-events",
                configuration["Kafka:Topics:StageEvents"] ?? "wonrich.processing.stage-events.v1",
                configuration["Kafka:Topics:StageEventsDeadLetter"] ?? "wonrich.dlq.traceability-stage-events.v1"),
            new(
                configuration["Kafka:ConsumerGroups:HoldEvents"] ?? "traceability-hold-events",
                configuration["Kafka:Topics:HoldEvents"] ?? "wonrich.processing.hold-events.v1",
                configuration["Kafka:Topics:HoldEventsDeadLetter"] ?? "wonrich.dlq.traceability-hold-events.v1"),
            new(
                configuration["Kafka:ConsumerGroups:BatchDeterminations"] ?? "traceability-batch-determinations",
                configuration["Kafka:Topics:BatchDeterminations"] ?? "wonrich.quality-lab.batch-determinations.v1",
                configuration["Kafka:Topics:BatchDeterminationsDeadLetter"] ?? "wonrich.dlq.traceability-batch-determinations.v1"),
            new(
                configuration["Kafka:ConsumerGroups:ConsignmentTested"] ?? "traceability-consignment-tested",
                configuration["Kafka:Topics:ConsignmentTested"] ?? "wonrich.intake.consignment-tested.v1",
                configuration["Kafka:Topics:ConsignmentTestedDeadLetter"] ?? "wonrich.dlq.traceability-consignment-tested.v1"),
        ];
    }

    public async Task<IReadOnlyList<DeadLetterMessageDto>> ListDeadLettersAsync(
        string? consumerGroup = null, CancellationToken cancellationToken = default)
    {
        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            logger.LogInformation("Dead-letter inspection: Kafka:BootstrapServers is not configured.");
            return [];
        }

        var mappings = GetMappings();
        if (!string.IsNullOrWhiteSpace(consumerGroup))
        {
            mappings = mappings.Where(m => string.Equals(m.ConsumerGroup, consumerGroup, StringComparison.OrdinalIgnoreCase)).ToList();
            if (mappings.Count == 0)
            {
                throw new ArgumentException($"Unknown consumer group '{consumerGroup}'.");
            }
        }

        var results = new List<DeadLetterMessageDto>();

        foreach (var mapping in mappings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = ReadDeadLettersFromTopic(
                mapping.ConsumerGroup,
                mapping.DeadLetterTopic,
                missingTopicIsError: !string.IsNullOrWhiteSpace(consumerGroup),
                cancellationToken);
            results.AddRange(items);
        }

        return await Task.FromResult(results);
    }

    private List<DeadLetterMessageDto> ReadDeadLettersFromTopic(
        string consumerGroup, string dlqTopic, bool missingTopicIsError, CancellationToken cancellationToken)
    {
        var results = new List<DeadLetterMessageDto>();
        var partitionIds = GetPartitionIds(dlqTopic);
        if (partitionIds is null)
        {
            if (missingTopicIsError)
            {
                throw new KeyNotFoundException(
                    $"Dead-letter topic '{dlqTopic}' for consumer group '{consumerGroup}' does not exist.");
            }

            logger.LogWarning(
                "Skipping dead-letter inspection for consumer group {ConsumerGroup}: topic {DeadLetterTopic} does not exist.",
                consumerGroup,
                dlqTopic);
            return results;
        }

        var consumerConfig = KafkaClientSettings.Apply(new ConsumerConfig
        {
            GroupId = $"traceability-dlq-inspect-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            EnablePartitionEof = true,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }, configuration);

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig)
            .SetErrorHandler((_, e) => logger.LogWarning("DLQ inspect Kafka error: {Reason}", e.Reason))
            .Build();

        foreach (var partitionId in partitionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partition = new TopicPartition(dlqTopic, partitionId);
            WatermarkOffsets watermark;
            try
            {
                watermark = consumer.QueryWatermarkOffsets(partition, TimeSpan.FromSeconds(5));
            }
            catch (KafkaException ex)
            {
                throw new InvalidOperationException(
                    $"Could not query DLQ topic {dlqTopic} partition {partitionId}; returning an incomplete inspection would hide messages.", ex);
            }

            if (watermark.High <= watermark.Low)
            {
                continue;
            }

            consumer.Assign(new TopicPartitionOffset(partition, watermark.Low));
            while (!cancellationToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? cr;
                try
                {
                    cr = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException ex)
                {
                    throw new InvalidOperationException(
                        $"Could not read DLQ topic {dlqTopic} partition {partitionId} completely.", ex);
                }

                if (cr is null)
                {
                    continue;
                }
                if (cr.IsPartitionEOF)
                {
                    break;
                }

                var headers = cr.Message.Headers ?? [];
                var eventId = ConsumedEvent.Header(headers, "eventId");
                var eventType = ConsumedEvent.Header(headers, "eventType");
                var correlationId = ConsumedEvent.Header(headers, ConsumedEvent.CorrelationIdHeader);
                var originalTopic = ConsumedEvent.Header(headers, "x-original-topic");
                var originalPartitionStr = ConsumedEvent.Header(headers, "x-original-partition");
                var originalOffsetStr = ConsumedEvent.Header(headers, "x-original-offset");
                var failureReason = ConsumedEvent.Header(headers, "x-failure-reason");
                var failureTimestampStr = ConsumedEvent.Header(headers, "x-failure-timestamp");
                var attemptCountStr = ConsumedEvent.Header(headers, "x-attempt-count");

                DateTime? failureTimestamp = null;
                if (DateTime.TryParse(failureTimestampStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                {
                    failureTimestamp = ts;
                }

                int.TryParse(attemptCountStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attemptCount);
                int? originalPartition = int.TryParse(originalPartitionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var op) ? op : null;
                long? originalOffset = long.TryParse(originalOffsetStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var oo) ? oo : null;

                results.Add(new DeadLetterMessageDto
                {
                    ConsumerGroup = consumerGroup,
                    Topic = dlqTopic,
                    Partition = cr.Partition.Value,
                    Offset = cr.Offset.Value,
                    Key = cr.Message.Key,
                    EventId = eventId,
                    EventType = eventType,
                    CorrelationId = correlationId,
                    FailureReason = failureReason,
                    FailureTimestamp = failureTimestamp,
                    AttemptCount = attemptCount,
                    OriginalTopic = originalTopic,
                    OriginalPartition = originalPartition,
                    OriginalOffset = originalOffset,
                    Payload = cr.Message.Value,
                });

                if (cr.Offset.Value + 1 >= watermark.High.Value)
                {
                    break;
                }
            }
        }

        try
        {
            consumer.Close();
        }
        catch (KafkaException)
        {
        }

        return results;
    }

    private IReadOnlyList<int>? GetPartitionIds(string topic)
    {
        using var admin = new AdminClientBuilder(KafkaClientSettings.Apply(new AdminClientConfig(), configuration)).Build();
        var metadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        var topicMetadata = metadata.Topics.FirstOrDefault(t => string.Equals(t.Topic, topic, StringComparison.Ordinal));
        if (topicMetadata is null || topicMetadata.Error.Code == ErrorCode.UnknownTopicOrPart)
        {
            return null;
        }

        if (topicMetadata.Error.Code != ErrorCode.NoError)
        {
            throw new InvalidOperationException(
                $"Could not discover partitions for Kafka topic '{topic}': {topicMetadata.Error.Reason}.");
        }

        return topicMetadata.Partitions.Select(p => p.PartitionId).Order().ToArray();
    }

    public async Task<ReplayResultDto> ReplayAsync(
        ReplayDeadLetterRequest request, AuditRequester requester, CancellationToken cancellationToken = default)
    {
        var mappings = GetMappings();
        var mapping = mappings.FirstOrDefault(m => string.Equals(m.ConsumerGroup, request.ConsumerGroup, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown consumer group '{request.ConsumerGroup}'.");

        var bootstrapServers = configuration["Kafka:BootstrapServers"];
        if (string.IsNullOrWhiteSpace(bootstrapServers))
        {
            throw new InvalidOperationException("Kafka:BootstrapServers is not configured. Cannot replay dead letters.");
        }

        // 1. Fetch message from dead-letter topic
        var consumerConfig = KafkaClientSettings.Apply(new ConsumerConfig
        {
            GroupId = $"traceability-dlq-replay-reader-{Guid.NewGuid():N}",
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }, configuration);

        using var consumer = new ConsumerBuilder<string, string>(consumerConfig).Build();
        var partition = new TopicPartition(mapping.DeadLetterTopic, request.Partition);

        consumer.Assign(new TopicPartitionOffset(partition, new Offset(request.Offset)));
        var cr = consumer.Consume(TimeSpan.FromSeconds(5));
        consumer.Close();

        if (cr is null || cr.Offset.Value != request.Offset)
        {
            throw new KeyNotFoundException(
                $"No dead-letter message found on {mapping.DeadLetterTopic} [partition {request.Partition}] at offset {request.Offset}.");
        }

        var headers = cr.Message.Headers ?? [];
        var eventId = ConsumedEvent.Header(headers, "eventId");
        var eventType = ConsumedEvent.Header(headers, "eventType");
        var correlationId = ConsumedEvent.Header(headers, ConsumedEvent.CorrelationIdHeader) ?? requester.CorrelationId;
        var targetTopic = ConsumedEvent.Header(headers, "x-original-topic") ?? mapping.SourceTopic;

        // 2. Check if already processed (deduplication)
        var isDuplicate = false;
        if (!string.IsNullOrWhiteSpace(eventId) && Guid.TryParse(eventId, out var eventIdGuid))
        {
            isDuplicate = await db.ProcessedMessages.AnyAsync(
                m => m.ConsumerGroup == mapping.ConsumerGroup && m.SourceEventId == eventIdGuid, cancellationToken);
        }

        if (isDuplicate)
        {
            // Already processed: create no duplicate!
            await audit.RecordAdminActionAsync(
                AuditActions.DeadLetterReplayed,
                "DeadLetter",
                eventId ?? $"{mapping.ConsumerGroup}:{request.Partition}:{request.Offset}",
                new
                {
                    consumerGroup = mapping.ConsumerGroup,
                    targetTopic,
                    partition = request.Partition,
                    offset = request.Offset,
                    correlationId,
                    isDuplicate = true,
                    role = requester.Role,
                },
                requester,
                cancellationToken);

            logger.LogInformation("Dead-letter replay for {EventId} skipped: already processed by {ConsumerGroup}",
                eventId, mapping.ConsumerGroup);

            return new ReplayResultDto
            {
                ConsumerGroup = mapping.ConsumerGroup,
                TargetTopic = targetTopic,
                EventId = eventId,
                CorrelationId = correlationId,
                Handled = false,
                IsDuplicate = true,
                Message = "Message was already processed by this consumer group; duplicate skipped with no duplicate records created.",
            };
        }

        // 3. Replay to original topic with clean headers
        var cleanHeaders = new Headers();
        foreach (var header in headers)
        {
            if (!header.Key.StartsWith("x-failure-", StringComparison.OrdinalIgnoreCase)
                && !header.Key.StartsWith("x-original-", StringComparison.OrdinalIgnoreCase)
                && header.Key != "x-attempt-count")
            {
                cleanHeaders.Add(header.Key, header.GetValueBytes());
            }
        }

        if (cleanHeaders.All(h => h.Key != ConsumedEvent.CorrelationIdHeader) && !string.IsNullOrWhiteSpace(correlationId))
        {
            cleanHeaders.Add(ConsumedEvent.CorrelationIdHeader, Encoding.UTF8.GetBytes(correlationId));
        }

        var producerConfig = KafkaClientSettings.Apply(new ProducerConfig
        {
            Acks = Acks.All,
            EnableIdempotence = true,
        }, configuration);

        using var producer = new ProducerBuilder<string, string>(producerConfig).Build();
        var produceResult = await producer.ProduceAsync(targetTopic, new Message<string, string>
        {
            Key = cr.Message.Key,
            Value = cr.Message.Value,
            Headers = cleanHeaders,
        }, cancellationToken);

        if (produceResult.Status != PersistenceStatus.Persisted)
        {
            throw new KafkaException(new Error(ErrorCode.Local_MsgTimedOut, $"Failed to replay message to {targetTopic} ({produceResult.Status})."));
        }

        // 4. Audit the replay operation
        await audit.RecordAdminActionAsync(
            AuditActions.DeadLetterReplayed,
            "DeadLetter",
            eventId ?? $"{mapping.ConsumerGroup}:{request.Partition}:{request.Offset}",
            new
            {
                consumerGroup = mapping.ConsumerGroup,
                targetTopic,
                partition = request.Partition,
                offset = request.Offset,
                correlationId,
                isDuplicate = false,
                role = requester.Role,
            },
            requester,
            cancellationToken);

        logger.LogInformation("Dead-letter message {EventId} replayed to {TargetTopic} by {User}",
            eventId, targetTopic, requester.UserId);

        return new ReplayResultDto
        {
            ConsumerGroup = mapping.ConsumerGroup,
            TargetTopic = targetTopic,
            EventId = eventId,
            CorrelationId = correlationId,
            Handled = true,
            IsDuplicate = false,
            Message = $"Message successfully replayed to {targetTopic}.",
        };
    }
}
