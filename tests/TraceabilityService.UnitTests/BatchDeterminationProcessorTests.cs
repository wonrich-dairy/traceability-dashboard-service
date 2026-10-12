using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TraceabilityService.Application;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The consumer's retry and dead-letter rules (SCRUM-131), without a broker: transient
/// failures are retried three times with backoff, then the message is published unchanged
/// to the dead-letter topic with headers saying where it came from and why it failed.
/// </summary>
public class BatchDeterminationProcessorTests
{
    private const string Topic = "wonrich.quality-lab.batch-determinations.v1";
    private const string Payload = """{"eventType":"BatchCleared","batchId":"262-FM-B"}""";

    private static readonly DateTime Now = new(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc);

    private readonly FakeHandler _handler = new();
    private readonly RecordingPublisher _deadLetters = new();
    private readonly List<TimeSpan> _delays = [];

    [Fact]
    public async Task A_message_the_handler_applies_is_not_retried_or_dead_lettered()
    {
        await Processor().ProcessAsync(Consumed(), CancellationToken.None);

        Assert.Equal(1, _handler.Calls);
        Assert.Equal(("BatchCleared", Payload), (_handler.LastMessage!.EventType, _handler.LastMessage.Payload));
        Assert.Empty(_delays);
        Assert.Empty(_deadLetters.Published);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_with_backoff_until_it_passes()
    {
        _handler.FailNext(new TimeoutException("database"), new BatchNotFoundException("262-FM-B"));

        await Processor().ProcessAsync(Consumed(), CancellationToken.None);

        Assert.Equal(3, _handler.Calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], _delays);
        Assert.Empty(_deadLetters.Published);
    }

    [Fact]
    public async Task After_three_retries_the_message_is_dead_lettered_unchanged_with_where_and_why()
    {
        _handler.AlwaysFail(new BatchNotFoundException("262-FM-B"));

        await Processor().ProcessAsync(Consumed(partition: 2, offset: 41), CancellationToken.None);

        Assert.Equal(4, _handler.Calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _delays);

        var (topic, message) = Assert.Single(_deadLetters.Published);
        Assert.Equal(BatchDeterminationConsumerOptions.DefaultDeadLetterTopic, topic);
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
    public async Task A_message_that_cannot_be_deserialised_is_dead_lettered_at_once()
    {
        _handler.AlwaysFail(new JsonException("'x' is an invalid start of a value."));

        await Processor().ProcessAsync(Consumed(), CancellationToken.None);

        Assert.Equal(1, _handler.Calls);
        Assert.Empty(_delays);
        var (_, message) = Assert.Single(_deadLetters.Published);
        Assert.Equal("1", Headers(message)["x-attempt-count"]);
        Assert.StartsWith("JsonException:", Headers(message)["x-failure-reason"]);
    }

    [Fact]
    public async Task A_message_with_no_value_is_dead_lettered_without_calling_the_handler()
    {
        await Processor().ProcessAsync(Consumed(value: null), CancellationToken.None);

        Assert.Equal(0, _handler.Calls);
        var (_, message) = Assert.Single(_deadLetters.Published);
        Assert.Null(message.Value);
        Assert.Equal("Message has no value.", Headers(message)["x-failure-reason"]);
    }

    [Fact]
    public async Task When_the_dead_letter_publish_fails_the_processor_throws_so_the_offset_is_not_committed()
    {
        _handler.AlwaysFail(new JsonException("bad"));
        _deadLetters.FailWith(new KafkaException(ErrorCode.UnknownTopicOrPart));

        await Assert.ThrowsAsync<KafkaException>(() => Processor().ProcessAsync(Consumed(), CancellationToken.None));
    }

    [Fact]
    public async Task Shutting_down_during_a_retry_stops_without_dead_lettering()
    {
        using var stopping = new CancellationTokenSource();
        _handler.AlwaysFail(new TimeoutException("database"));
        var processor = Processor(delay: (_, token) =>
        {
            stopping.Cancel();
            return Task.Delay(Timeout.Infinite, token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processor.ProcessAsync(Consumed(), stopping.Token));

        Assert.Empty(_deadLetters.Published);
    }

    private BatchDeterminationProcessor Processor(Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IBatchDeterminationHandler>(_handler)
            .BuildServiceProvider();

        return new BatchDeterminationProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            _deadLetters,
            new BatchDeterminationConsumerOptions(),
            new FixedTime(Now),
            NullLogger<BatchDeterminationProcessor>.Instance,
            delay ?? ((wait, _) => { _delays.Add(wait); return Task.CompletedTask; }));
    }

    private static ConsumeResult<string, string> Consumed(int partition = 0, long offset = 0, string? value = Payload) => new()
    {
        Topic = Topic,
        Partition = new Partition(partition),
        Offset = new Offset(offset),
        Message = new Message<string, string>
        {
            Key = "262-FM-B",
            Value = value!,
            Headers = new Headers
            {
                { "eventType", Encoding.UTF8.GetBytes("BatchCleared") },
                { "x-correlation-id", Encoding.UTF8.GetBytes("corr-1") },
            },
        },
    };

    private static Dictionary<string, string> Headers(Message<string, string> message) =>
        message.Headers.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()));

    private sealed class FakeHandler : IBatchDeterminationHandler
    {
        private readonly Queue<Exception> _failures = new();
        private Exception? _always;

        public int Calls { get; private set; }
        public BatchDeterminationMessage? LastMessage { get; private set; }

        public void FailNext(params Exception[] failures)
        {
            foreach (var failure in failures)
            {
                _failures.Enqueue(failure);
            }
        }

        public void AlwaysFail(Exception failure) => _always = failure;

        public Task<BatchDeterminationOutcome> HandleAsync(BatchDeterminationMessage message, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastMessage = message;
            if (_always is not null)
            {
                throw _always;
            }
            return _failures.TryDequeue(out var failure)
                ? throw failure
                : Task.FromResult(BatchDeterminationOutcome.Applied);
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
}
