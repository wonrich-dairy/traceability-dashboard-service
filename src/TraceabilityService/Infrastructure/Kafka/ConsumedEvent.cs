using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// One consumed event as the handlers see it. Both upstream relays (Processing, SCRUM-68;
/// Quality Lab, SCRUM-24) send the event type, event ID and correlation ID as headers, and
/// the event ID in the body too.
/// </summary>
/// <param name="EventType">The eventType header, e.g. BatchFailed or ProcessingCompletedEvent.</param>
/// <param name="Payload">The message value: the event as camelCase JSON.</param>
public sealed record ConsumedEvent(
    string Topic,
    int Partition,
    long Offset,
    string? Key,
    string? EventType,
    Guid EventId,
    string? CorrelationId,
    string Payload)
{
    public const string EventTypeHeader = "eventType";
    public const string EventIdHeader = "eventId";
    public const string CorrelationIdHeader = "x-correlation-id";

    /// <exception cref="InvalidEventException">No value, or no event ID anywhere.</exception>
    /// <exception cref="JsonException">The body is needed for the event ID and is not JSON.</exception>
    public static ConsumedEvent From(ConsumeResult<string, string> result)
    {
        var message = result.Message;
        if (message.Value is null)
        {
            throw new InvalidEventException("Message has no value.");
        }

        var eventType = Header(message.Headers, EventTypeHeader);
        var eventIdText = Header(message.Headers, EventIdHeader);

        // The headers are the contract, but an older relay may have left one out; the body
        // carries the event ID always, and Quality Lab's carries the event type.
        if (eventType is null || eventIdText is null)
        {
            using var body = JsonDocument.Parse(message.Value);
            eventType ??= Property(body, "eventType");
            eventIdText ??= Property(body, "eventId");
        }

        if (!Guid.TryParse(eventIdText, out var eventId))
        {
            throw new InvalidEventException("Message has no eventId header or body field, so duplicates cannot be detected.");
        }

        return new ConsumedEvent(
            result.Topic,
            result.Partition.Value,
            result.Offset.Value,
            message.Key,
            eventType,
            eventId,
            Header(message.Headers, CorrelationIdHeader),
            message.Value);
    }

    public static string? Header(Headers? headers, string key) =>
        headers is not null && headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    private static string? Property(JsonDocument body, string name) =>
        body.RootElement.ValueKind == JsonValueKind.Object
        && body.RootElement.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// The message can never be handled, e.g. it has no value or no event ID, so it is
/// dead-lettered without retrying.
/// </summary>
public sealed class InvalidEventException(string message) : Exception(message);

/// <summary>
/// No handler is registered for the event's type on this consumer. Retried, in case the
/// handler is being deployed, then dead-lettered so it can be replayed once it exists.
/// </summary>
public sealed class UnsupportedEventTypeException(string? eventType, string topic)
    : Exception($"No handler for event type '{eventType ?? "(none)"}' on {topic}.")
{
    public string? EventType { get; } = eventType;
}
