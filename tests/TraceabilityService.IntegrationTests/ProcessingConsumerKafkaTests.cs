using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProcessingService.Domain.Events;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// The Processing consumers end to end (SCRUM-130 Definition of Done): events published to a
/// real broker, read by the app's own consumers, and applied to MySQL. Each test uses a
/// production day of its own, so its aggregate rows are its alone.
/// </summary>
[Collection(KafkaCollection.Name)]
public sealed class ProcessingConsumerKafkaTests(KafkaApiFactory factory) : IDisposable
{
    private const string StageTopic = "wonrich.processing.stage-events.v1";
    private const string HoldTopic = "wonrich.processing.hold-events.v1";
    private const string Facility = ProcessingEventApplier.DefaultFacility;
    private static readonly string[] Groups = ["traceability-stage-events", "traceability-hold-events"];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IProducer<string, string> _producer =
        new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = factory.BootstrapServers }).Build();

    public void Dispose() => _producer.Dispose();

    [Fact]
    public async Task The_five_events_build_the_timeline_in_order_with_the_right_status_and_aggregates()
    {
        var batch = new TestBatch(dayOfYear: 201);

        // Across both topics, so the stage and hold consumers handle them side by side.
        await PublishAsync(StageTopic, batch.Allocated(0));
        await PublishAsync(StageTopic, batch.Stage(30, deviating: true));
        await PublishAsync(HoldTopic, batch.Raised("H1", 40));
        await PublishAsync(HoldTopic, batch.Resolved("H1", 50));
        await PublishAsync(StageTopic, batch.Completed(90));

        var state = (await EventuallyAsync(() => ReadAsync(batch), s => s?.Timeline.Count == 5 && s.Batch.Status == BatchStatus.AwaitingLab))!;

        Assert.Equal(
            ["MilkAllocatedToMixingTank", "ProcessingStageRecorded", "ProcessingHoldRaised", "ProcessingHoldResolved", "ProcessingCompleted"],
            state.Timeline);
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Deviations: 2, Processing: 2), state.Counts);
        Assert.Equal(new Dictionary<string, int> { ["Unspecified"] = 2 }, state.Reasons);
    }

    [Fact]
    public async Task A_malformed_message_lands_on_the_dead_letter_topic_and_the_next_message_is_still_applied()
    {
        var batch = new TestBatch(dayOfYear: 202);

        // Same key as the batch, so both are on one partition and the bad one comes first.
        await _producer.ProduceAsync(StageTopic, new Message<string, string>
        {
            Key = batch.BatchId,
            Value = "{not json",
            Headers = Headers(nameof(MilkAllocatedToMixingTankEvent), Guid.NewGuid()),
        });
        await PublishAsync(StageTopic, batch.Allocated(0));

        var state = (await EventuallyAsync(() => ReadAsync(batch), s => s is not null))!;
        Assert.Equal(["MilkAllocatedToMixingTank"], state.Timeline);

        var deadLetter = await ReadDeadLetterAsync("wonrich.dlq.traceability-stage-events.v1", m => m.Value == "{not json");
        var headers = deadLetter.Headers.ToDictionary(h => h.Key, h => Encoding.UTF8.GetString(h.GetValueBytes()));
        Assert.Equal(batch.BatchId, deadLetter.Key);
        Assert.Equal(StageTopic, headers["x-original-topic"]);
        Assert.Equal("1", headers["x-attempt-count"]);
        Assert.StartsWith("JsonException:", headers["x-failure-reason"]);
    }

    [Fact]
    public async Task Redelivering_from_an_earlier_offset_after_a_restart_adds_no_rows_and_moves_no_counts()
    {
        var batch = new TestBatch(dayOfYear: 203);
        await PublishAsync(StageTopic, batch.Allocated(0));
        await PublishAsync(HoldTopic, batch.Raised("H1", 40));
        await PublishAsync(StageTopic, batch.Completed(90));
        await EventuallyAsync(() => ReadAsync(batch), s => s?.Timeline.Count == 3 && s.Batch.Status == BatchStatus.OnHold);
        await EventuallyAsync(() => FullyCommittedAsync(), done => done);
        var before = await TotalsAsync(batch);

        var consumers = factory.Services.GetServices<IHostedService>().OfType<KafkaEventConsumer>().ToList();
        foreach (var consumer in consumers)
        {
            await consumer.StopAsync(CancellationToken.None);
        }

        try
        {
            // Back to the start of both topics: everything is delivered again.
            await EventuallyAsync(() => ResetOffsetsAsync(), reset => reset);
        }
        finally
        {
            // Started again whatever happened, so the other tests in the collection still have consumers.
            foreach (var consumer in consumers)
            {
                await consumer.StartAsync(CancellationToken.None);
            }
        }

        await EventuallyAsync(() => FullyCommittedAsync(), done => done);

        Assert.Equal(before, await TotalsAsync(batch));
    }

    private Task PublishAsync(string topic, ProcessingEventBase processingEvent) =>
        _producer.ProduceAsync(topic, new Message<string, string>
        {
            Key = processingEvent.BatchId,
            Value = JsonSerializer.Serialize(processingEvent, processingEvent.GetType(), Json),
            Headers = Headers(processingEvent.GetType().Name, processingEvent.EventId),
        });

    // As Processing's relay sends them (SCRUM-68).
    private static Headers Headers(string eventType, Guid eventId) => new()
    {
        { "eventType", Encoding.UTF8.GetBytes(eventType) },
        { "eventId", Encoding.UTF8.GetBytes(eventId.ToString()) },
        { "x-correlation-id", Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")) },
    };

    private static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Not reached within {Timeout}; last value: {value}");
            }
            await Task.Delay(250);
        }
    }

    private sealed record State(
        BatchRecord Batch,
        List<string> Timeline,
        (int Batches, int Pending, int OnHold, int Deviations, int Processing) Counts,
        Dictionary<string, int> Reasons);

    private async Task<State?> ReadAsync(TestBatch batch)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

        var record = await db.BatchRecords.AsNoTracking().SingleOrDefaultAsync(b => b.BatchId == batch.BatchId);
        var counts = await db.DailyQualityAggregates.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Facility == Facility && a.Date == batch.Day && a.ProductLine == "FM");
        if (record is null || counts is null)
        {
            return null;
        }

        return new State(
            record,
            await db.TimelineEvents.AsNoTracking().Where(e => e.BatchId == batch.BatchId)
                .OrderBy(e => e.OccurredAt).Select(e => e.EventType).ToListAsync(),
            (counts.BatchCount, counts.PendingCount, counts.OnHoldCount, counts.DeviationCount, counts.ProcessingDeviationCount),
            await db.DailyReasonCodeAggregates.AsNoTracking()
                .Where(a => a.Facility == Facility && a.Date == batch.Day && a.ProductLine == "FM")
                .ToDictionaryAsync(a => a.ReasonCode, a => a.DeviationCount));
    }

    // Row counts across the database, plus the batch's own state, as one comparable value.
    private async Task<string> TotalsAsync(TestBatch batch)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
        var state = await ReadAsync(batch);
        return string.Join(" | ",
            $"batches={await db.BatchRecords.CountAsync()}",
            $"timeline={await db.TimelineEvents.CountAsync()}",
            $"deviations={await db.DeviationRecords.CountAsync()}",
            $"processed={await db.ProcessedMessages.CountAsync()}",
            $"pending={await db.PendingDispatchEvents.CountAsync()}",
            $"status={state!.Batch.Status}",
            $"counts={state.Counts}",
            $"reasons={string.Join(",", state.Reasons.OrderBy(r => r.Key).Select(r => $"{r.Key}:{r.Value}"))}");
    }

    // True once both groups have committed up to the end of every partition of their topic.
    private async Task<bool> FullyCommittedAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = factory.BootstrapServers }).Build();
        using var probe = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = factory.BootstrapServers, GroupId = "traceability-tests-probe",
        }).Build();

        foreach (var (group, topic) in Groups.Zip([StageTopic, HoldTopic]))
        {
            var committed = (await admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, null)]))
                .Single().Partitions.ToDictionary(p => p.Partition.Value, p => p.Offset.Value);

            for (var partition = 0; partition < 3; partition++)
            {
                var end = probe.QueryWatermarkOffsets(new TopicPartition(topic, partition), TimeSpan.FromSeconds(10)).High.Value;
                if (end > 0 && committed.GetValueOrDefault(partition, -1) < end)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // Fails while a stopped consumer is still leaving its group; the caller retries.
    private async Task<bool> ResetOffsetsAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = factory.BootstrapServers }).Build();
        try
        {
            // One group per call: the admin API takes no more.
            foreach (var (group, topic) in Groups.Zip([StageTopic, HoldTopic]))
            {
                await admin.AlterConsumerGroupOffsetsAsync(
                [
                    new ConsumerGroupTopicPartitionOffsets(group, [.. Enumerable.Range(0, 3).Select(p => new TopicPartitionOffset(topic, p, 0))]),
                ]);
            }
            return true;
        }
        catch (KafkaException)
        {
            return false;
        }
    }

    private async Task<Message<string, string>> ReadDeadLetterAsync(string topic, Func<Message<string, string>, bool> match)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = factory.BootstrapServers,
            GroupId = $"traceability-tests-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = await Task.Run(() => consumer.Consume(TimeSpan.FromSeconds(1)));
            if (result?.Message is { } message && match(message))
            {
                return message;
            }
        }
        throw new TimeoutException($"No matching message on {topic} within {Timeout}.");
    }

    /// <summary>One batch on a production day of its own, and its events.</summary>
    private sealed class TestBatch(int dayOfYear)
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];

        public DateOnly Day { get; } = new DateOnly(2026, 1, 1).AddDays(dayOfYear - 1);
        public string BatchId => $"{dayOfYear}-FM-{_suffix}";
        private string Dispatch => $"DN-KAFKA-{_suffix}";

        private DateTime At(int minutes) => Day.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc).AddMinutes(minutes);

        public MilkAllocatedToMixingTankEvent Allocated(int minutes) => new()
        {
            BatchId = BatchId, BatchCode = BatchId, DispatchNumber = Dispatch, AllocatedAtUtc = At(minutes), AllocatedBy = "tech-1",
        };

        public ProcessingStageRecordedEvent Stage(int minutes, bool deviating) => new()
        {
            BatchId = BatchId, DispatchNumber = Dispatch, StageType = "Pasteuriser", StartTimeUtc = At(minutes),
            EndTemperatureC = deviating ? 70.50m : 73.00m, RecordedBy = "tech-2", IsDeviation = deviating,
        };

        public ProcessingCompletedEvent Completed(int minutes) => new()
        {
            BatchId = BatchId, DispatchNumber = Dispatch, CompletedAtUtc = At(minutes), CompletedBy = "tech-3",
        };

        public ProcessingHoldRaisedEvent Raised(string holdId, int minutes) => new()
        {
            BatchId = BatchId, DispatchNumber = Dispatch, HoldId = holdId, IsDeviation = true, RaisedAtUtc = At(minutes),
            RaisedBy = "qa-1", Reason = "Failed: FatPercent 2.8 < 3.0 min", FailedParameter = "FatPercent", FailedValue = "2.8 < 3.0 min",
        };

        public ProcessingHoldResolvedEvent Resolved(string holdId, int minutes) => new()
        {
            BatchId = BatchId, DispatchNumber = Dispatch, HoldId = holdId, RaisedAtUtc = At(minutes - 10),
            ResolvedAtUtc = At(minutes), ResolvedBy = "qa-2", Reason = "Previous hold", Resolution = "Released",
        };
    }
}
