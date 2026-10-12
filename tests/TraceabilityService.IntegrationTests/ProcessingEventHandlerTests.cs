using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wonrich.Processing.Contracts.Events;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// The Processing event handlers against real MySQL (SCRUM-130), through the stage and hold
/// consumers' processors without a broker. Processing's batches are at the configured facility
/// (FACTORY-01), so each test uses a production day of its own and removes what it wrote.
/// </summary>
[Collection(ApiCollection.Name)]
public class ProcessingEventHandlerTests(TraceabilityApiFactory factory)
{
    private const string Facility = ProcessingEventApplier.DefaultFacility;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task The_first_event_creates_the_batch_and_counts_it_as_pending()
    {
        await using var test = new TestDay(factory, dayOfYear: 101);

        await test.StageAsync(test.Allocated(minutes: 0));

        var state = await test.ReadAsync();
        Assert.Equal(("FM", test.Dispatch, Facility, BatchStatus.Processing, TraceCompleteness.Partial),
            (state.Batch.ProductLine, state.Batch.DispatchRef, state.Batch.Facility, state.Batch.Status, state.Batch.Completeness));
        var timelineEvent = Assert.Single(state.Timeline);
        Assert.Equal((Checkpoint.Processing, "MilkAllocatedToMixingTank", "tech-1"),
            (timelineEvent.Checkpoint, timelineEvent.EventType, timelineEvent.RecordedBy));
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Deviations: 0, Processing: 0), state.Counts);
    }

    [Fact]
    public async Task A_deviating_stage_is_recorded_and_counted_as_a_processing_deviation()
    {
        await using var test = new TestDay(factory, dayOfYear: 102);
        await test.StageAsync(test.Allocated(minutes: 0));

        await test.StageAsync(new ProcessingStageRecordedEvent
        {
            BatchId = test.BatchId, DispatchNumber = test.Dispatch, StageType = "Pasteuriser",
            StartTimeUtc = test.At(30), EndTemperatureC = 70.50m, RecordedBy = "tech-2", IsDeviation = true,
        });

        var state = await test.ReadAsync();
        var deviation = Assert.Single(state.Deviations);
        Assert.Equal((Checkpoint.Processing, "EndTemperatureC", 70.50m), (deviation.Checkpoint, deviation.Parameter, deviation.ObservedValue));
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Deviations: 1, Processing: 1), state.Counts);
        Assert.Equal(new Dictionary<string, int> { ["Unspecified"] = 1 }, state.ReasonCounts);
    }

    [Fact]
    public async Task A_hold_puts_the_batch_on_hold_and_resolving_it_returns_it_to_processing()
    {
        await using var test = new TestDay(factory, dayOfYear: 103);
        await test.StageAsync(test.Allocated(minutes: 0));

        await test.HoldAsync(test.Raised("H1", minutes: 10));
        var held = await test.ReadAsync();
        Assert.Equal(BatchStatus.OnHold, held.Batch.Status);
        Assert.Equal((Batches: 1, Pending: 0, OnHold: 1, Deviations: 1, Processing: 1), held.Counts);
        Assert.Equal(("FatPercent", 2.8m), (Assert.Single(held.Deviations).Parameter, held.Deviations[0].ObservedValue));

        await test.HoldAsync(test.Resolved("H1", minutes: 20));
        var resolved = await test.ReadAsync();
        Assert.Equal(BatchStatus.Processing, resolved.Batch.Status);
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Deviations: 1, Processing: 1), resolved.Counts);
    }

    [Fact]
    public async Task A_hold_resolved_before_it_is_raised_never_puts_the_batch_on_hold()
    {
        await using var test = new TestDay(factory, dayOfYear: 104);
        await test.StageAsync(test.Allocated(minutes: 0));

        await test.HoldAsync(test.Resolved("H1", minutes: 20));
        await test.HoldAsync(test.Raised("H1", minutes: 10));
        await test.StageAsync(test.Completed(minutes: 60));

        var state = await test.ReadAsync();
        Assert.Equal(BatchStatus.AwaitingLab, state.Batch.Status);
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Deviations: 1, Processing: 1), state.Counts);
    }

    [Fact]
    public async Task A_hold_on_a_dispatch_with_no_batch_yet_is_kept_and_attached_when_the_batch_arrives()
    {
        await using var test = new TestDay(factory, dayOfYear: 105);

        // Raised at unload: Processing puts the dispatch number in BatchId.
        await test.HoldAsync(test.Raised("H1", minutes: -30) with { BatchId = test.Dispatch });
        Assert.Equal(1, await test.PendingCountAsync());
        Assert.Null(await test.FindBatchAsync());

        await test.StageAsync(test.Allocated(minutes: 0));

        var state = await test.ReadAsync();
        Assert.Equal(0, await test.PendingCountAsync());
        Assert.Equal(["MilkAllocatedToMixingTank", "ProcessingHoldRaised"], state.Timeline.Select(e => e.EventType).Order());
        Assert.Equal(BatchStatus.OnHold, state.Batch.Status);
        // The kept hold happened before the allocation, so it is the batch's first event.
        Assert.Equal(test.At(-30), state.Batch.FirstEventAt);
        Assert.Equal((Batches: 1, Pending: 0, OnHold: 1, Deviations: 1, Processing: 1), state.Counts);
    }

    [Fact]
    public async Task A_hold_on_the_dispatch_of_an_existing_batch_goes_straight_to_it()
    {
        await using var test = new TestDay(factory, dayOfYear: 106);
        await test.StageAsync(test.Allocated(minutes: 0));

        await test.HoldAsync(test.Raised("H1", minutes: 10) with { BatchId = test.Dispatch });

        Assert.Equal(0, await test.PendingCountAsync());
        Assert.Equal(BatchStatus.OnHold, (await test.ReadAsync()).Batch.Status);
    }

    [Fact]
    public async Task A_late_earlier_event_extends_first_event_at_without_moving_the_batch_to_another_day()
    {
        await using var test = new TestDay(factory, dayOfYear: 107);
        await test.StageAsync(test.Completed(minutes: 120));

        await test.StageAsync(test.Allocated(minutes: -600));

        var state = await test.ReadAsync();
        Assert.Equal((test.At(-600), test.At(120)), (state.Batch.FirstEventAt, state.Batch.LastEventAt));
        Assert.Equal(BatchStatus.AwaitingLab, state.Batch.Status);
        // Still one batch on the batch code's day.
        Assert.Equal((Batches: 1, Pending: 1, OnHold: 0, Deviations: 0, Processing: 0), state.Counts);
    }

    [Fact]
    public async Task The_same_event_twice_is_applied_once()
    {
        await using var test = new TestDay(factory, dayOfYear: 108);
        var raised = test.Raised("H1", minutes: 10);
        await test.StageAsync(test.Allocated(minutes: 0));

        Assert.Equal(EventOutcome.Handled, await test.HoldAsync(raised));
        Assert.Equal(EventOutcome.Duplicate, await test.HoldAsync(raised));

        var state = await test.ReadAsync();
        Assert.Equal(2, state.Timeline.Count);
        Assert.Equal((Batches: 1, Pending: 0, OnHold: 1, Deviations: 1, Processing: 1), state.Counts);
    }

    [Fact]
    public async Task A_cd_batch_is_accepted()
    {
        await using var test = new TestDay(factory, dayOfYear: 109, productLine: "CD");

        await test.StageAsync(test.Allocated(minutes: 0));

        Assert.Equal("CD", (await test.ReadAsync()).Batch.ProductLine);
    }

    private sealed record State(
        BatchRecord Batch,
        List<TimelineEvent> Timeline,
        List<DeviationRecord> Deviations,
        (int Batches, int Pending, int OnHold, int Deviations, int Processing) Counts,
        Dictionary<string, int> ReasonCounts);

    /// <summary>
    /// One batch on a production day of its own, its events, and what they wrote. Disposing it
    /// removes the batch, anything kept for its dispatch, and the day's aggregate rows.
    /// </summary>
    private sealed class TestDay(TraceabilityApiFactory factory, int dayOfYear, string productLine = "FM") : IAsyncDisposable
    {
        private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
        private readonly DateOnly _day = new DateOnly(2026, 1, 1).AddDays(dayOfYear - 1);

        public string BatchId => $"{dayOfYear}-{productLine}-{_suffix}";
        public string Dispatch => $"DN-TEST-{_suffix}";

        // Minutes after 08:30 in Colombo on the production day.
        public DateTime At(int minutes) => _day.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc).AddMinutes(minutes);

        public MilkAllocatedToMixingTankEvent Allocated(int minutes) => new()
        {
            BatchId = BatchId, BatchCode = BatchId, DispatchNumber = Dispatch, AllocatedAtUtc = At(minutes), AllocatedBy = "tech-1",
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

        public Task<EventOutcome> StageAsync(ProcessingEventBase processingEvent) =>
            SendAsync(ProcessingConsumers.StageEvents, processingEvent);

        public Task<EventOutcome> HoldAsync(ProcessingEventBase processingEvent) =>
            SendAsync(ProcessingConsumers.HoldEvents, processingEvent);

        private async Task<EventOutcome> SendAsync(string consumer, ProcessingEventBase processingEvent)
        {
            var processor = factory.Services.GetRequiredKeyedService<EventProcessor>(consumer);
            var payload = JsonSerializer.Serialize(processingEvent, processingEvent.GetType(), Json);
            return await processor.HandleOnceAsync(new ConsumedEvent(
                processor.Options.Topic, Partition: 0, Offset: 0, processingEvent.BatchId, processingEvent.GetType().Name,
                processingEvent.EventId, CorrelationId: "test", payload));
        }

        public async Task<BatchRecord?> FindBatchAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            return await db.BatchRecords.AsNoTracking().SingleOrDefaultAsync(b => b.BatchId == BatchId);
        }

        public async Task<int> PendingCountAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            return await db.PendingDispatchEvents.CountAsync(p => p.DispatchNumber == Dispatch);
        }

        public async Task<State> ReadAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

            var counts = await db.DailyQualityAggregates.AsNoTracking()
                .SingleAsync(a => a.Facility == Facility && a.Date == _day && a.ProductLine == productLine);

            return new State(
                await db.BatchRecords.AsNoTracking().SingleAsync(b => b.BatchId == BatchId),
                await db.TimelineEvents.AsNoTracking().Where(e => e.BatchId == BatchId).ToListAsync(),
                await db.DeviationRecords.AsNoTracking().Where(d => d.BatchId == BatchId).ToListAsync(),
                (counts.BatchCount, counts.PendingCount, counts.OnHoldCount, counts.DeviationCount, counts.ProcessingDeviationCount),
                await db.DailyReasonCodeAggregates.AsNoTracking()
                    .Where(a => a.Facility == Facility && a.Date == _day && a.ProductLine == productLine)
                    .ToDictionaryAsync(a => a.ReasonCode, a => a.DeviationCount));
        }

        public async ValueTask DisposeAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

            var eventIds = await db.TimelineEvents.Where(e => e.BatchId == BatchId).Select(e => e.SourceEventId).ToListAsync();
            eventIds.AddRange(await db.PendingDispatchEvents.Where(p => p.DispatchNumber == Dispatch).Select(p => p.SourceEventId).ToListAsync());
            await db.ProcessedMessages.Where(m => eventIds.Contains(m.SourceEventId)).ExecuteDeleteAsync();
            await db.PendingDispatchEvents.Where(p => p.DispatchNumber == Dispatch).ExecuteDeleteAsync();

            // Cascades to the timeline and deviations.
            await db.BatchRecords.Where(b => b.BatchId == BatchId).ExecuteDeleteAsync();
            await db.DailyQualityAggregates.Where(a => a.Facility == Facility && a.Date == _day).ExecuteDeleteAsync();
            await db.DailyReasonCodeAggregates.Where(a => a.Facility == Facility && a.Date == _day).ExecuteDeleteAsync();
        }
    }
}
