using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using TraceabilityService.Application;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// What happens to one consumed lab determination (SCRUM-131): the handler applies it,
/// retrying with backoff when it fails, and a message that still fails, or can never be
/// read, is published unchanged to the dead-letter topic. Returning means the offset can
/// be committed; throwing means it must not be, because the message is neither applied
/// nor parked. Kept apart from the consume loop so it is unit-tested without a broker.
/// </summary>
public sealed class BatchDeterminationProcessor(
    IServiceScopeFactory scopes,
    IDeadLetterPublisher deadLetters,
    BatchDeterminationConsumerOptions options,
    TimeProvider time,
    ILogger<BatchDeterminationProcessor> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string EventTypeHeader = "eventType";

    private const int MaxReasonLength = 1000;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task ProcessAsync(ConsumeResult<string, string> result, CancellationToken cancellationToken)
    {
        // A message with no body cannot be a determination, now or after a retry.
        if (result.Message.Value is null)
        {
            await DeadLetterAsync(result, "Message has no value.", attempts: 1, cancellationToken);
            return;
        }

        var message = new BatchDeterminationMessage(
            result.Topic, HeaderValue(result.Message.Headers, EventTypeHeader), result.Message.Value);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // A scope per attempt: a fresh DbContext, with nothing left over from the failed one.
                await using var scope = scopes.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<IBatchDeterminationHandler>();

                var outcome = await handler.HandleAsync(message, cancellationToken);
                logger.LogInformation(
                    "Lab determination {Outcome} from {Topic} [{Partition}] @{Offset}, key {Key}",
                    outcome, result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key);
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
                logger.LogWarning(ex,
                    "Lab determination from {Topic} [{Partition}] @{Offset} failed on attempt {Attempt}; retrying in {Wait}",
                    result.Topic, result.Partition.Value, result.Offset.Value, attempt, wait);
                await _delay(wait, cancellationToken);
            }
            catch (Exception ex)
            {
                await DeadLetterAsync(result, Describe(ex), attempt, cancellationToken);
                return;
            }
        }
    }

    // The body is not valid JSON or not the contract's shape, or the batch code has no day of
    // the year: the same message fails the same way every time, so retrying only delays it.
    // Anything else, the database or a batch that is not here yet included, may pass on a retry.
    private static bool CanNeverSucceed(Exception ex) => ex is JsonException or FormatException;

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

        logger.LogError(
            "Lab determination from {Topic} [{Partition}] @{Offset}, key {Key}, sent to {DeadLetterTopic} after {Attempts} attempt(s): {Reason}",
            result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, options.DeadLetterTopic, attempts, reason);
    }

    private static string Describe(Exception ex) => $"{ex.GetType().Name}: {ex.Message}";

    private static void Add(Headers headers, string key, string value) => headers.Add(key, Encoding.UTF8.GetBytes(value));

    private static string? HeaderValue(Headers? headers, string key) =>
        headers is not null && headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
}
