using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TraceabilityService.Infrastructure.Kafka;

public static class EventConsumerServiceCollectionExtensions
{
    /// <summary>
    /// Registers one Kafka consumer: its handlers (scoped, one per event type), an
    /// <see cref="EventProcessor"/> keyed by <see cref="KafkaConsumerOptions.Name"/>, and the
    /// hosted consume loop. Call once per topic.
    /// </summary>
    public static IServiceCollection AddEventConsumer(
        this IServiceCollection services, KafkaConsumerOptions options, Action<EventHandlerMap> configureHandlers)
    {
        var map = new EventHandlerMap();
        configureHandlers(map);

        foreach (var handlerType in map.Handlers.Values.Distinct())
        {
            services.TryAddScoped(handlerType);
        }

        services.TryAddScoped<IEventUnitOfWork, EfEventUnitOfWork>();
        services.TryAddSingleton<IDeadLetterPublisher, KafkaDeadLetterPublisher>();

        services.AddKeyedSingleton(options.Name, (provider, _) => new EventProcessor(
            options,
            map.Handlers,
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IDeadLetterPublisher>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<EventProcessor>>()));

        // AddSingleton rather than AddHostedService: AddHostedService registers one instance per
        // type, and every consumer is a KafkaEventConsumer.
        services.AddSingleton<IHostedService>(provider => new KafkaEventConsumer(
            provider.GetRequiredKeyedService<EventProcessor>(options.Name),
            provider.GetRequiredService<IConfiguration>(),
            provider.GetRequiredService<ILogger<KafkaEventConsumer>>()));

        return services;
    }
}
