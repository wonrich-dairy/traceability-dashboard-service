using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// What happens to one consumed event, for any consumer: it is dispatched on its eventType
/// header to that type's handler, inside a transaction that also records it as processed,
/// so a redelivered event is skipped. A failure is retried with backoff; a message that
/// still fails, or can never be handled, is published unchanged to the dead-letter topic.
/// Returning means the offset can be committed; throwing means it must not be, because the
/// event is neither handled nor parked. Every log line written meanwhile, the handler's
/// included, carries the event's correlation ID. Kept apart from the consume loop so it is
/// unit-tested without a broker.
/// </summary>
public sealed class EventProcessor(
    KafkaConsumerOptions options,
    IReadOnlyDictionary<string, Type> handlers,
    IServiceScopeFactory scopes,
    IDeadLetterPublisher deadLetters,
    TimeProvider time,
    ILogger<EventProcessor> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const int MaxReasonLength = 1000;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public KafkaConsumerOptions Options => options;

    public async Task ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        // Read from the header directly, so the scope is there even for a message that
        // cannot be parsed. With IncludeScopes on, every JSON log line carries it.
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = ConsumedEvent.Header(result.Message.Headers, ConsumedEvent.CorrelationIdHeader),
            ["ConsumerGroup"] = options.ConsumerGroup,
            ["Topic"] = result.Topic,
            ["Partition"] = result.Partition.Value,
            ["Offset"] = result.Offset.Value,
        });

        ConsumedEvent consumed;
        try
        {
            consumed = ConsumedEvent.From(result);
        }
        catch (Exception ex) when (CanNeverSucceed(ex))
        {
            await DeadLetterAsync(result, Describe(ex), attempts: 1, cancellationToken);
            return;
        }

        using var eventScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["EventType"] = consumed.EventType,
            ["EventId"] = consumed.EventId,
        });

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var outcome = await HandleOnceAsync(consumed, cancellationToken);
                logger.LogInformation("{EventType} {Outcome}, key {Key}", consumed.EventType, outcome, consumed.Key);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (CanNeverSucceed(ex))
            {
                await DeadLetterAsync(result, Describe(ex), attempt, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt <= options.MaxRetries)
            {
                var wait = options.RetryBaseDelay * Math.Pow(2, attempt - 1);
                logger.LogWarning(ex, "{EventType} failed on attempt {Attempt}; retrying in {Wait}", consumed.EventType, attempt, wait);
                await _delay(wait, cancellationToken);
            }
            catch (Exception ex)
            {
                await DeadLetterAsync(result, Describe(ex), attempt, cancellationToken);
                return;
            }
        }
    }

    /// <summary>
    /// One attempt: the event's handler and the processed_messages row, in one transaction.
    /// Public so the integration tests can run handlers against MySQL without a broker.
    /// </summary>
    /// <exception cref="UnsupportedEventTypeException">No handler for the event type.</exception>
    public async Task<EventOutcome> HandleOnceAsync(ConsumedEvent consumed, CancellationToken cancellationToken = default)
    {
        if (consumed.EventType is null || !handlers.TryGetValue(consumed.EventType, out var handlerType))
        {
            throw new UnsupportedEventTypeException(consumed.EventType, consumed.Topic);
        }

        // A scope per attempt: a fresh DbContext, with nothing left over from a failed one.
        await using var scope = scopes.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IEventUnitOfWork>();

        if (!await unitOfWork.BeginAsync(options.ConsumerGroup, consumed, cancellationToken))
        {
            return EventOutcome.Duplicate;
        }

        var handler = (IEventHandler)scope.ServiceProvider.GetRequiredService(handlerType);
        await handler.HandleAsync(consumed, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        scope.ServiceProvider.GetService<AfterCommit>()?.Run();
        return EventOutcome.Handled;
    }

    // The body is not valid JSON or not the contract's shape, the batch code has no day of the
    // year, or the message has no value or event ID: the same message fails the same way every
    // time, so retrying only delays it. Anything else, an unknown event type included, may pass
    // on a retry.
    private static bool CanNeverSucceed(Exception ex) => ex is JsonException or FormatException or InvalidEventException;

    private async Task DeadLetterAsync(
        ConsumeResult<string, string> result, string reason, int attempts, CancellationToken cancellationToken)
    {
        // Unchanged: same key, value and headers, so it can be replayed onto the source topic as it is.
        var headers = new Headers();
        foreach (var header in result.Message.Headers ?? [])
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        Add(headers, "x-original-topic", result.Topic);
        Add(headers, "x-original-partition", result.Partition.Value.ToString(CultureInfo.InvariantCulture));
        Add(headers, "x-original-offset", result.Offset.Value.ToString(CultureInfo.InvariantCulture));
        Add(headers, "x-failure-reason", reason.Length <= MaxReasonLength ? reason : reason[..MaxReasonLength]);
        Add(headers, "x-failure-timestamp", time.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        Add(headers, "x-attempt-count", attempts.ToString(CultureInfo.InvariantCulture));

        await deadLetters.PublishAsync(options.DeadLetterTopic, new Message<string, string>
        {
            Key = result.Message.Key,
            Value = result.Message.Value,
            Headers = headers,
        }, cancellationToken);

        logger.LogError("Sent to {DeadLetterTopic} after {Attempts} attempt(s), key {Key}: {Reason}",
            options.DeadLetterTopic, attempts, result.Message.Key, reason);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static void Add(Headers headers, string key, string value) => headers.Add(key, Encoding.UTF8.GetBytes(value));
}

public enum EventOutcome
{
    Handled,

    // Already handled by this consumer group: the upstream relays deliver at least once.
    Duplicate
}
