using System.Text.Json;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wonrich.Processing.Contracts.Events;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.UnitTests;

/// <summary>
/// The five Processing handlers (SCRUM-130), without Docker: the stage and hold processors
/// dispatch exactly as the app does, to the real handlers, against EF Core's in-memory
/// database. Only the MySQL-specific parts are stood in for: the transaction with its
/// processed_messages duplicate check, and the aggregate upserts, which are summed instead.
/// </summary>
public sealed class ProcessingHandlerTests : IDisposable
{
    private const string BatchId = "285-FM-A";
    private const string Dispatch = "DN-20261012-01";
    private static readonly DateTime Morning = new(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 10, 12);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ServiceProvider _services;
    private readonly RecordingAggregates _aggregates = new();
    private readonly RecordingPublisher _deadLetters = new();
    private readonly EventProcessor _stage;
    private readonly EventProcessor _hold;

    public ProcessingHandlerTests()
    {
        var database = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .AddDbContext<TraceabilityDbContext>(options => options.UseInMemoryDatabase(database))
            .AddSingleton<IAggregateWriter>(_aggregates)
            .AddSingleton<ProcessedEvents>()
            .AddScoped<IEventUnitOfWork, InMemoryUnitOfWork>()
            .AddScoped<ProcessingEventApplier>();

        var stageHandlers = new EventHandlerMap();
        ProcessingConsumers.StageHandlers(stageHandlers);
        var holdHandlers = new EventHandlerMap();
        ProcessingConsumers.HoldHandlers(holdHandlers);
        foreach (var handlerType in stageHandlers.Handlers.Values.Concat(holdHandlers.Handlers.Values))
        {
            services.AddScoped(handlerType);
        }

        _services = services.BuildServiceProvider();
        _stage = Processor("traceability-stage-events", "wonrich.processing.stage-events.v1", stageHandlers);
        _hold = Processor("traceability-hold-events", "wonrich.processing.hold-events.v1", holdHandlers);
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public async Task MilkAllocatedToMixingTank_creates_the_batch_and_counts_it_as_pending()
    {
        await StageAsync(Allocated(minutes: 0));

        var batch = await BatchAsync();
        Assert.Equal(("FM", Dispatch, "FACTORY-01", BatchStatus.Processing), (batch.ProductLine, batch.DispatchRef, batch.Facility, batch.Status));
        Assert.Equal(["MilkAllocatedToMixingTank"], await TimelineAsync());
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Processing: 0), _aggregates.Totals(Day));
    }

    [Fact]
    public async Task ProcessingStageRecorded_adds_to_the_timeline_and_counts_a_deviation()
    {
        await StageAsync(Allocated(minutes: 0));

        await StageAsync(new ProcessingStageRecordedEvent
        {
            BatchId = BatchId, DispatchNumber = Dispatch, StageType = "Pasteuriser", StartTimeUtc = Morning.AddMinutes(30),
            EndTemperatureC = 70.50m, RecordedBy = "tech-2", IsDeviation = true,
        });

        Assert.Equal(["MilkAllocatedToMixingTank", "ProcessingStageRecorded"], await TimelineAsync());
        Assert.Equal(("EndTemperatureC", 70.50m), await SingleDeviationAsync());
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Processing: 1), _aggregates.Totals(Day));
        Assert.Equal(1, _aggregates.ReasonCount(Day, "Unspecified"));
    }

    [Fact]
    public async Task ProcessingCompleted_moves_the_batch_to_awaiting_lab_without_a_deviation()
    {
        await StageAsync(Allocated(minutes: 0));

        await StageAsync(Completed(minutes: 120, isDeviation: true));

        Assert.Equal(BatchStatus.AwaitingLab, (await BatchAsync()).Status);
        Assert.Equal(0, await DeviationCountAsync());
        // Processing and AwaitingLab are both pending: nothing moves.
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Processing: 0), _aggregates.Totals(Day));
    }

    [Fact]
    public async Task ProcessingHoldRaised_puts_the_batch_on_hold_and_counts_its_failed_parameter()
    {
        await StageAsync(Allocated(minutes: 0));

        await HoldAsync(Raised("H1", minutes: 10));

        Assert.Equal(BatchStatus.OnHold, (await BatchAsync()).Status);
        Assert.Equal(("FatPercent", 2.8m), await SingleDeviationAsync());
        Assert.Equal((Batches: 1, Pending: 0, OnHold: 1, Processing: 1), _aggregates.Totals(Day));
    }

    [Fact]
    public async Task ProcessingHoldResolved_returns_the_batch_to_the_status_its_other_events_give()
    {
        await StageAsync(Allocated(minutes: 0));
        await StageAsync(Completed(minutes: 60));
        await HoldAsync(Raised("H1", minutes: 70));

        await HoldAsync(Resolved("H1", minutes: 80));

        Assert.Equal(BatchStatus.AwaitingLab, (await BatchAsync()).Status);
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Processing: 1), _aggregates.Totals(Day));
    }

    [Fact]
    public async Task A_duplicate_delivery_adds_no_second_timeline_entry_and_no_second_state_change()
    {
        var allocated = Allocated(minutes: 0);
        var raised = Raised("H1", minutes: 10);

        Assert.Equal(EventOutcome.Handled, await StageAsync(allocated));
        Assert.Equal(EventOutcome.Handled, await HoldAsync(raised));
        var changes = _aggregates.Changes.Count;

        Assert.Equal(EventOutcome.Duplicate, await StageAsync(allocated));
        Assert.Equal(EventOutcome.Duplicate, await HoldAsync(raised));

        Assert.Equal(["MilkAllocatedToMixingTank", "ProcessingHoldRaised"], await TimelineAsync());
        Assert.Equal(1, await DeviationCountAsync());
        Assert.Equal(changes, _aggregates.Changes.Count);
        Assert.Equal((Batches: 1, Pending: 0, OnHold: 1, Processing: 1), _aggregates.Totals(Day));
    }

    [Fact]
    public async Task Completed_before_allocated_still_awaits_the_lab_with_the_allocation_as_first_event()
    {
        await StageAsync(Completed(minutes: 120));
        await StageAsync(Allocated(minutes: 0));

        var batch = await BatchAsync();
        Assert.Equal(BatchStatus.AwaitingLab, batch.Status);
        Assert.Equal((Morning, Morning.AddMinutes(120)), (batch.FirstEventAt, batch.LastEventAt));
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Processing: 0), _aggregates.Totals(Day));
    }

    [Fact]
    public async Task A_hold_resolved_before_it_is_raised_never_counts_the_batch_as_on_hold()
    {
        await StageAsync(Allocated(minutes: 0));

        await HoldAsync(Resolved("H1", minutes: 20));
        await HoldAsync(Raised("H1", minutes: 10));

        Assert.Equal(BatchStatus.Processing, (await BatchAsync()).Status);
        Assert.DoesNotContain(_aggregates.Changes, c => c.Delta.StatusCounts.GetValueOrDefault(StatusBucket.OnHold) != 0);
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Processing: 1), _aggregates.Totals(Day));
    }

    [Fact]
    public async Task An_unknown_event_type_on_a_topic_is_unsupported_and_ends_on_the_dead_letter_topic()
    {
        // A hold event on the stage topic: the stage consumer has no handler for it.
        var raised = Raised("H1", minutes: 10);
        await Assert.ThrowsAsync<UnsupportedEventTypeException>(() => _stage.HandleOnceAsync(Consumed(_stage, raised)));

        await _stage.ProcessAsync(new ConsumeResult<string, string>
        {
            Topic = _stage.Options.Topic,
            Partition = new Partition(0),
            Offset = new Offset(7),
            Message = new Message<string, string>
            {
                Key = BatchId,
                Value = JsonSerializer.Serialize(raised, Json),
                Headers = new Headers { { "eventType", "ProcessingHoldRaisedEvent"u8.ToArray() }, { "eventId", System.Text.Encoding.UTF8.GetBytes(raised.EventId.ToString()) } },
            },
        }, CancellationToken.None);

        var (topic, message) = Assert.Single(_deadLetters.Published);
        Assert.Equal("wonrich.dlq.traceability-stage-events.v1", topic);
        Assert.Contains(message.Headers, h => h.Key == "x-failure-reason"
            && System.Text.Encoding.UTF8.GetString(h.GetValueBytes()).StartsWith("UnsupportedEventTypeException"));
        Assert.Empty(await TimelineAsync());
    }

    private EventProcessor Processor(string group, string topic, EventHandlerMap handlers) => new(
        new KafkaConsumerOptions
        {
            Name = group, Topic = topic, ConsumerGroup = group,
            DeadLetterTopic = topic.Replace("wonrich.processing.", "wonrich.dlq.traceability-"),
        },
        handlers.Handlers,
        _services.GetRequiredService<IServiceScopeFactory>(),
        _deadLetters,
        TimeProvider.System,
        _services.GetRequiredService<ILogger<EventProcessor>>(),
        delay: (_, _) => Task.CompletedTask);

    private Task<EventOutcome> StageAsync(ProcessingEventBase e) => _stage.HandleOnceAsync(Consumed(_stage, e));

    private Task<EventOutcome> HoldAsync(ProcessingEventBase e) => _hold.HandleOnceAsync(Consumed(_hold, e));

    private static ConsumedEvent Consumed(EventProcessor processor, ProcessingEventBase e) => new(
        processor.Options.Topic, 0, 0, e.BatchId, e.GetType().Name, e.EventId, "corr-1",
        JsonSerializer.Serialize(e, e.GetType(), Json));

    private static MilkAllocatedToMixingTankEvent Allocated(int minutes) => new()
    {
        BatchId = BatchId, BatchCode = BatchId, DispatchNumber = Dispatch, AllocatedAtUtc = Morning.AddMinutes(minutes), AllocatedBy = "tech-1",
    };

    private static ProcessingCompletedEvent Completed(int minutes, bool isDeviation = false) => new()
    {
        BatchId = BatchId, DispatchNumber = Dispatch, CompletedAtUtc = Morning.AddMinutes(minutes), CompletedBy = "tech-3",
        IsDeviation = isDeviation, HasDeviation = isDeviation,
    };

    private static ProcessingHoldRaisedEvent Raised(string holdId, int minutes) => new()
    {
        BatchId = BatchId, DispatchNumber = Dispatch, HoldId = holdId, IsDeviation = true, RaisedAtUtc = Morning.AddMinutes(minutes),
        RaisedBy = "qa-1", Reason = "Failed: FatPercent 2.8 < 3.0 min", FailedParameter = "FatPercent", FailedValue = "2.8 < 3.0 min",
    };

    private static ProcessingHoldResolvedEvent Resolved(string holdId, int minutes) => new()
    {
        BatchId = BatchId, DispatchNumber = Dispatch, HoldId = holdId, RaisedAtUtc = Morning.AddMinutes(minutes - 10),
        ResolvedAtUtc = Morning.AddMinutes(minutes), ResolvedBy = "qa-2", Reason = "Previous hold", Resolution = "Released",
    };

    private async Task<T> QueryAsync<T>(Func<TraceabilityDbContext, Task<T>> query)
    {
        using var scope = _services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>());
    }

    private Task<BatchRecord> BatchAsync() => QueryAsync(db => db.BatchRecords.AsNoTracking().SingleAsync(b => b.BatchId == BatchId));

    // In the order things happened, as the trace shows them.
    private Task<List<string>> TimelineAsync() => QueryAsync(db => db.TimelineEvents.AsNoTracking()
        .Where(e => e.BatchId == BatchId).OrderBy(e => e.OccurredAt).Select(e => e.EventType).ToListAsync());

    private Task<int> DeviationCountAsync() => QueryAsync(db => db.DeviationRecords.CountAsync(d => d.BatchId == BatchId));

    private Task<(string?, decimal?)> SingleDeviationAsync() => QueryAsync(async db =>
    {
        var deviation = await db.DeviationRecords.AsNoTracking().SingleAsync(d => d.BatchId == BatchId);
        Assert.Equal(Checkpoint.Processing, deviation.Checkpoint);
        return (deviation.Parameter, deviation.ObservedValue);
    });

    /// <summary>processed_messages, shared by every scope like the table is.</summary>
    private sealed class ProcessedEvents
    {
        public HashSet<(string Group, Guid EventId)> Seen { get; } = [];
    }

    private sealed class InMemoryUnitOfWork(ProcessedEvents processed) : IEventUnitOfWork
    {
        public Task<bool> BeginAsync(string consumerGroup, ConsumedEvent consumed, CancellationToken cancellationToken) =>
            Task.FromResult(processed.Seen.Add((consumerGroup, consumed.EventId)));

        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Every change written to the aggregates, and their running totals per day.</summary>
    private sealed class RecordingAggregates : IAggregateWriter
    {
        public List<(DateOnly Day, AggregateDelta Delta)> Changes { get; } = [];

        public Task ApplyAsync(BatchRecord batch, DateOnly day, AggregateDelta delta, DateTime now, CancellationToken cancellationToken)
        {
            Changes.Add((day, delta));
            return Task.CompletedTask;
        }

        public (int Batches, int Pending, int OnHold, int Processing) Totals(DateOnly day)
        {
            var onDay = Changes.Where(c => c.Day == day).Select(c => c.Delta).ToList();
            return (
                onDay.Sum(d => d.Batches),
                onDay.Sum(d => d.StatusCounts.GetValueOrDefault(StatusBucket.Pending)),
                onDay.Sum(d => d.StatusCounts.GetValueOrDefault(StatusBucket.OnHold)),
                onDay.Sum(d => d.ProcessingDeviations));
        }

        public int ReasonCount(DateOnly day, string code) =>
            Changes.Where(c => c.Day == day).Sum(c => c.Delta.ReasonCodes.GetValueOrDefault(code));
    }

    private sealed class RecordingPublisher : IDeadLetterPublisher
    {
        public List<(string Topic, Message<string, string> Message)> Published { get; } = [];

        public Task PublishAsync(string topic, Message<string, string> message, CancellationToken cancellationToken)
        {
            Published.Add((topic, message));
            return Task.CompletedTask;
        }
    }
}
