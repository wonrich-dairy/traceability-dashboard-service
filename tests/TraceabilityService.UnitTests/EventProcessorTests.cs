using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TraceabilityService.Application;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The consumer base every Kafka consumer shares (SCRUM-130, SCRUM-131), without a broker or
/// database: dispatch on the eventType header, the duplicate check, commit only on success,
/// retries with backoff, dead-lettering with headers, and the correlation ID on every log line.
/// </summary>
public class EventProcessorTests
{
    private const string Topic = "wonrich.quality-lab.batch-determinations.v1";
    private const string DeadLetterTopic = "wonrich.dlq.traceability-batch-determinations.v1";
    private const string EventId = "11111111-1111-1111-1111-111111111111";
    private const string Payload = """{"eventId":"11111111-1111-1111-1111-111111111111","batchId":"262-FM-B"}""";

    private static readonly DateTime Now = new(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc);

    private readonly FakeHandler _cleared = new();
    private readonly OtherHandler _failed = new();
    private readonly FakeUnitOfWork _unitOfWork = new();
    private readonly RecordingPublisher _deadLetters = new();
    private readonly RecordingLoggerProvider _logs = new();
    private readonly List<TimeSpan> _delays = [];

    [Fact]
    public async Task An_event_goes_to_its_types_handler_and_is_committed()
    {
        await Processor().ProcessAsync(Consumed(), CancellationToken.None);

        Assert.Equal(1, _cleared.Calls);
        Assert.Equal(0, _failed.Calls);
        Assert.Equal(("BatchCleared", Guid.Parse(EventId), "corr-1", Payload),
            (_cleared.Last!.EventType, _cleared.Last.EventId, _cleared.Last.CorrelationId, _cleared.Last.Payload));
        Assert.Equal(("traceability-batch-determinations", 1), (_unitOfWork.ConsumerGroup, _unitOfWork.Commits));
        Assert.Empty(_delays);
        Assert.Empty(_deadLetters.Published);
    }

    [Fact]
    public async Task Each_event_type_is_dispatched_to_its_own_handler()
    {
        await Processor().ProcessAsync(Consumed(eventType: "BatchFailed"), CancellationToken.None);

        Assert.Equal(0, _cleared.Calls);
        Assert.Equal(1, _failed.Calls);
    }

    [Fact]
    public async Task An_event_already_handled_by_the_group_is_skipped()
    {
        _unitOfWork.AlreadyProcessed = true;

        await Processor().ProcessAsync(Consumed(), CancellationToken.None);

        Assert.Equal(0, _cleared.Calls);
        Assert.Equal(0, _unitOfWork.Commits);
        Assert.Empty(_deadLetters.Published);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_with_backoff_and_only_the_attempt_that_passes_commits()
    {
        _cleared.FailNext(new TimeoutException("database"), new BatchNotFoundException("262-FM-B"));

        await Processor().ProcessAsync(Consumed(), CancellationToken.None);

        Assert.Equal(3, _cleared.Calls);
        Assert.Equal(1, _unitOfWork.Commits);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], _delays);
        Assert.Empty(_deadLetters.Published);
    }

    [Fact]
    public async Task After_three_retries_the_message_is_dead_lettered_unchanged_with_where_and_why()
    {
        _cleared.AlwaysFail(new BatchNotFoundException("262-FM-B"));

        await Processor().ProcessAsync(Consumed(partition: 2, offset: 41), CancellationToken.None);

        Assert.Equal(4, _cleared.Calls);
        Assert.Equal(0, _unitOfWork.Commits);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _delays);

        var (topic, message) = Assert.Single(_deadLetters.Published);
        Assert.Equal(DeadLetterTopic, topic);
        Assert.Equal("262-FM-B", message.Key);
        Assert.Equal(Payload, message.Value);

        var headers = Headers(message);
        Assert.Equal("BatchCleared", headers["eventType"]);
        Assert.Equal("corr-1", headers["x-correlation-id"]);
        Assert.Equal(Topic, headers["x-original-topic"]);
        Assert.Equal("2", headers["x-original-partition"]);
        Assert.Equal("41", headers["x-original-offset"]);
        Assert.Equal("4", headers["x-attempt-count"]);
        Assert.Equal("2026-10-12T09:30:00.0000000Z", headers["x-failure-timestamp"]);
        Assert.Equal("BatchNotFoundException: Batch '262-FM-B' is not in batch_records yet.", headers["x-failure-reason"]);
    }

    [Fact]
    public async Task An_unknown_event_type_is_retried_then_dead_lettered()
    {
        await Processor().ProcessAsync(Consumed(eventType: "BatchQuarantined"), CancellationToken.None);

        Assert.Equal(0, _cleared.Calls + _failed.Calls);
        Assert.Equal(3, _delays.Count);
        var (_, message) = Assert.Single(_deadLetters.Published);
        Assert.Equal("4", Headers(message)["x-attempt-count"]);
        Assert.Equal(
            $"UnsupportedEventTypeException: No handler for event type 'BatchQuarantined' on {Topic}.",
            Headers(message)["x-failure-reason"]);
    }

    [Fact]
    public async Task A_message_that_cannot_be_deserialised_is_dead_lettered_at_once()
    {
        _cleared.AlwaysFail(new JsonException("'x' is an invalid start of a value."));

        await Processor().ProcessAsync(Consumed(), CancellationToken.None);

        Assert.Equal(1, _cleared.Calls);
        Assert.Empty(_delays);
        var (_, message) = Assert.Single(_deadLetters.Published);
        Assert.Equal("1", Headers(message)["x-attempt-count"]);
        Assert.StartsWith("JsonException:", Headers(message)["x-failure-reason"]);
    }

    [Fact]
    public async Task A_message_with_no_value_is_dead_lettered_without_calling_a_handler()
    {
        await Processor().ProcessAsync(Consumed(value: null), CancellationToken.None);

        Assert.Equal(0, _cleared.Calls);
        var (_, message) = Assert.Single(_deadLetters.Published);
        Assert.Null(message.Value);
        Assert.Equal("InvalidEventException: Message has no value.", Headers(message)["x-failure-reason"]);
    }

    [Fact]
    public async Task A_message_with_no_event_id_is_dead_lettered_at_once_since_duplicates_cannot_be_detected()
    {
        await Processor().ProcessAsync(Consumed(value: """{"batchId":"262-FM-B"}""", eventIdHeader: false), CancellationToken.None);

        Assert.Equal(0, _cleared.Calls);
        Assert.Empty(_delays);
        Assert.StartsWith("InvalidEventException:", Headers(Assert.Single(_deadLetters.Published).Message)["x-failure-reason"]);
    }

    [Fact]
    public async Task The_event_id_is_read_from_the_body_when_the_header_is_missing()
    {
        await Processor().ProcessAsync(Consumed(eventIdHeader: false), CancellationToken.None);

        Assert.Equal(Guid.Parse(EventId), _cleared.Last!.EventId);
    }

    [Fact]
    public async Task When_the_dead_letter_publish_fails_the_processor_throws_so_the_offset_is_not_committed()
    {
        _cleared.AlwaysFail(new JsonException("bad"));
        _deadLetters.FailWith(new KafkaException(ErrorCode.UnknownTopicOrPart));

        await Assert.ThrowsAsync<KafkaException>(() => Processor().ProcessAsync(Consumed(), CancellationToken.None));
    }

    [Fact]
    public async Task Shutting_down_during_a_retry_stops_without_dead_lettering()
    {
        using var stopping = new CancellationTokenSource();
        _cleared.AlwaysFail(new TimeoutException("database"));
        var processor = Processor(delay: (_, token) =>
        {
            stopping.Cancel();
            return Task.Delay(Timeout.Infinite, token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(Consumed(), stopping.Token));

        Assert.Empty(_deadLetters.Published);
    }

    [Fact]
    public async Task Every_log_line_written_while_handling_carries_the_correlation_id()
    {
        // One failed attempt, so the processor's retry line is checked as well as the handler's own.
        _cleared.FailNext(new TimeoutException("database"));

        await Processor().ProcessAsync(Consumed(), CancellationToken.None);

        Assert.Contains(_logs.Lines, l => l.Category.EndsWith(nameof(FakeHandler)));
        Assert.Contains(_logs.Lines, l => l.Level == LogLevel.Warning);
        Assert.All(_logs.Lines, l => Assert.Equal("corr-1", l.Scope.GetValueOrDefault("CorrelationId")));
        Assert.All(_logs.Lines, l => Assert.Equal(Guid.Parse(EventId), l.Scope.GetValueOrDefault("EventId")));
    }

    [Fact]
    public async Task A_dead_letter_line_carries_the_correlation_id_even_when_the_message_cannot_be_parsed()
    {
        await Processor().ProcessAsync(Consumed(value: null), CancellationToken.None);

        var line = Assert.Single(_logs.Lines);
        Assert.Equal(LogLevel.Error, line.Level);
        Assert.Equal("corr-1", line.Scope.GetValueOrDefault("CorrelationId"));
    }

    private EventProcessor Processor(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var services = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(_logs))
            .AddSingleton(_cleared)
            .AddSingleton(_failed)
            .AddSingleton<IEventUnitOfWork>(_unitOfWork)
            .BuildServiceProvider();

        var loggers = services.GetRequiredService<ILoggerFactory>();
        _cleared.UseLogger(loggers.CreateLogger<FakeHandler>());
        _failed.UseLogger(loggers.CreateLogger<OtherHandler>());

        var handlers = new EventHandlerMap()
            .Add<FakeHandler>("BatchCleared")
            .Add<OtherHandler>("BatchFailed");

        return new EventProcessor(
            new KafkaConsumerOptions
            {
                Name = "BatchDeterminations",
                Topic = Topic,
                ConsumerGroup = "traceability-batch-determinations",
                DeadLetterTopic = DeadLetterTopic,
            },
            handlers.Handlers,
            services.GetRequiredService<IServiceScopeFactory>(),
            _deadLetters,
            new FixedTime(Now),
            services.GetRequiredService<ILogger<EventProcessor>>(),
            delay ?? ((wait, _) => { _delays.Add(wait); return Task.CompletedTask; }));
    }

    private static ConsumeResult<string, string> Consumed(
        string eventType = "BatchCleared", int partition = 0, long offset = 0, string? value = Payload, bool eventIdHeader = true)
    {
        var headers = new Headers
        {
            { "eventType", Encoding.UTF8.GetBytes(eventType) },
            { "x-correlation-id", Encoding.UTF8.GetBytes("corr-1") },
        };
        if (eventIdHeader)
        {
            headers.Add("eventId", Encoding.UTF8.GetBytes(EventId));
        }

        return new ConsumeResult<string, string>
        {
            Topic = Topic,
            Partition = new Partition(partition),
            Offset = new Offset(offset),
            Message = new Message<string, string> { Key = "262-FM-B", Value = value!, Headers = headers },
        };
    }

    private static Dictionary<string, string> Headers(Message<string, string> message) =>
        message.Headers.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()));

    private class FakeHandler : IEventHandler
    {
        private readonly Queue<Exception> _failures = new();
        private Exception? _always;
        private ILogger? _logger;

        public int Calls { get; private set; }
        public ConsumedEvent? Last { get; private set; }

        public void FailNext(params Exception[] failures)
        {
            foreach (var failure in failures)
            {
                _failures.Enqueue(failure);
            }
        }

        public void AlwaysFail(Exception failure) => _always = failure;

        public Task HandleAsync(ConsumedEvent consumed, CancellationToken cancellationToken)
        {
            Calls++;
            Last = consumed;
            if (_always is not null)
            {
                throw _always;
            }
            if (_failures.TryDequeue(out var failure))
            {
                throw failure;
            }

            // A handler's own line, like LabDeterminationHandler's "applied" line.
            _logger?.LogInformation("Handled {EventId}", consumed.EventId);
            return Task.CompletedTask;
        }

        public void UseLogger(ILogger logger) => _logger = logger;
    }

    private sealed class OtherHandler : FakeHandler;

    private sealed class FakeUnitOfWork : IEventUnitOfWork
    {
        public bool AlreadyProcessed { get; set; }
        public string? ConsumerGroup { get; private set; }
        public int Commits { get; private set; }

        public Task<bool> BeginAsync(string consumerGroup, ConsumedEvent consumed, CancellationToken cancellationToken)
        {
            ConsumerGroup = consumerGroup;
            return Task.FromResult(!AlreadyProcessed);
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            Commits++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPublisher : IDeadLetterPublisher
    {
        private Exception? _failure;

        public List<(string Topic, Message<string, string> Message)> Published { get; } = [];

        public void FailWith(Exception failure) => _failure = failure;

        public Task PublishAsync(string topic, Message<string, string> message, CancellationToken cancellationToken)
        {
            if (_failure is not null)
            {
                throw _failure;
            }
            Published.Add((topic, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }

    /// <summary>
    /// Records each log line with the values of every scope open when it was written, the way
    /// the JSON console formatter with IncludeScopes puts them on the line.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public List<(string Category, LogLevel Level, string Message, Dictionary<string, object?> Scope)> Lines { get; } = [];

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(RecordingLoggerProvider provider, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var scope = new Dictionary<string, object?>();
                provider._scopes.ForEachScope((value, values) =>
                {
                    if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        foreach (var (key, item) in pairs)
                        {
                            values[key] = item;
                        }
                    }
                }, scope);

                lock (provider.Lines)
                {
                    provider.Lines.Add((category, logLevel, formatter(state, exception), scope));
                }
            }
        }
    }
}
