using System.Text.Json;

namespace TraceabilityService.Infrastructure.Kafka;

/// <summary>
/// Applies one event type to the read model. Called inside the event's transaction, after
/// the duplicate check, so it only reads and writes; <see cref="EventProcessor"/> commits.
/// Throwing rolls everything back, and the event is retried or dead-lettered.
/// </summary>
public interface IEventHandler
{
    Task HandleAsync(ConsumedEvent consumed, CancellationToken cancellationToken);
}

/// <summary>An <see cref="IEventHandler"/> for one contract type, deserialised from camelCase JSON.</summary>
public abstract class TypedEventHandler<TEvent> : IEventHandler where TEvent : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task HandleAsync(ConsumedEvent consumed, CancellationToken cancellationToken) => HandleAsync(
        JsonSerializer.Deserialize<TEvent>(consumed.Payload, Json)
            ?? throw new JsonException("The message value is JSON null."),
        consumed,
        cancellationToken);

    protected abstract Task HandleAsync(TEvent @event, ConsumedEvent consumed, CancellationToken cancellationToken);
}

/// <summary>Which handler each event type on a topic goes to, matched exactly.</summary>
public sealed class EventHandlerMap
{
    private readonly Dictionary<string, Type> _handlers = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, Type> Handlers => _handlers;

    public EventHandlerMap Add<THandler>(string eventType) where THandler : class, IEventHandler
    {
        _handlers.Add(eventType, typeof(THandler));
        return this;
    }
}
