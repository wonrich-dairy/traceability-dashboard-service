using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Application;
using TraceabilityService.Domain.Entities;
using Wonrich.QualityLab.Contracts.Events;

namespace TraceabilityService.IntegrationTests;

/// <summary>
/// The lab determination handler against real MySQL (SCRUM-131): each event's writes to
/// lab_determinations, the timeline, deviations, the batch and both aggregate tables, in
/// one transaction. Called directly, without Kafka. Each test has its own facility and
/// batch, so the shared database's other data does not affect the counts.
/// </summary>
[Collection(ApiCollection.Name)]
public class BatchDeterminationHandlerTests(TraceabilityApiFactory factory)
{
    private const string Topic = "wonrich.quality-lab.batch-determinations.v1";

    // Day 285 of 2026; the batch's first event is that morning in Colombo.
    private static readonly DateOnly ProductionDay = new(2026, 10, 12);
    private static readonly DateTime FirstEventAt = new(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_cleared_determination_clears_the_batch_and_completes_its_trace()
    {
        await using var batch = await SeedBatchAsync(snapshotResolvedAndProcessed: true);
        var cleared = Cleared(batch.BatchId, at: FirstEventAt.AddHours(6));

        Assert.Equal(BatchDeterminationOutcome.Applied, await HandleAsync(cleared));

        var state = await batch.ReadAsync();
        Assert.Equal(BatchStatus.Cleared, state.Batch.Status);
        Assert.Equal(TraceCompleteness.Complete, state.Batch.Completeness);
        Assert.Equal(cleared.DeterminedAtUtc, state.Batch.LastEventAt);

        var determination = Assert.Single(state.Determinations);
        Assert.Equal(cleared.DeterminationId, determination.DeterminationId);
        Assert.Equal(DeterminationResult.Cleared, determination.Result);
        Assert.Equal("analyst-1", determination.DeterminedBy);
        Assert.False(determination.IsSuperseded);

        var timelineEvent = Assert.Single(state.LabEvents);
        Assert.Equal(determination.TimelineEventId, timelineEvent.Id);
        Assert.Equal("BatchCleared", timelineEvent.EventType);
        Assert.Equal(cleared.EventId, timelineEvent.SourceEventId);
        Assert.Equal("v1", timelineEvent.ContractVersion);
        Assert.False(timelineEvent.IsDeviation);

        Assert.Equal((Batches: 1, Pending: 0, Cleared: 1, Failed: 0, Deviations: 0, Lab: 0), state.Counts);
        Assert.Empty(state.ReasonCounts);
        Assert.Contains(cleared.EventId, state.Processed);
    }

    [Fact]
    public async Task A_failure_with_two_parameters_records_both_deviations_and_counts_them()
    {
        await using var batch = await SeedBatchAsync();
        var failed = Failed(batch.BatchId, at: FirstEventAt.AddHours(6),
            reasons: ["LOW_FAT", "LOW_PH"],
            new OutOfSpecDetail("FatPercent", 3.20m, "Min", 3.50m),
            new OutOfSpecDetail("Ph", 6.40m, "Min", 6.60m));

        Assert.Equal(BatchDeterminationOutcome.Applied, await HandleAsync(failed));

        var state = await batch.ReadAsync();
        Assert.Equal(BatchStatus.Failed, state.Batch.Status);
        // No resolved snapshot or Processing event, so the trace still has gaps.
        Assert.Equal(TraceCompleteness.Partial, state.Batch.Completeness);
        Assert.True(Assert.Single(state.LabEvents).IsDeviation);

        Assert.Equal(
            [("LOW_FAT", "FatPercent", 3.20m, 3.50m), ("LOW_PH", "Ph", 6.40m, 6.60m)],
            state.Deviations.Select(d => (d.ReasonCode, d.Parameter, d.ObservedValue, d.Limit)).Order());
        Assert.All(state.Deviations, d => Assert.Equal(Checkpoint.Lab, d.Checkpoint));

        Assert.Equal((Batches: 1, Pending: 0, Cleared: 0, Failed: 1, Deviations: 2, Lab: 2), state.Counts);
        Assert.Equal(Reasons(("LOW_FAT", 1), ("LOW_PH", 1)), state.ReasonCounts);
    }

    [Fact]
    public async Task The_same_event_twice_is_applied_once()
    {
        await using var batch = await SeedBatchAsync();
        var failed = Failed(batch.BatchId, at: FirstEventAt.AddHours(6),
            reasons: ["LOW_FAT"], new OutOfSpecDetail("FatPercent", 3.20m, "Min", 3.50m));

        Assert.Equal(BatchDeterminationOutcome.Applied, await HandleAsync(failed));
        var first = await batch.ReadAsync();

        Assert.Equal(BatchDeterminationOutcome.Duplicate, await HandleAsync(failed));
        var second = await batch.ReadAsync();

        Assert.Equal(first.Counts, second.Counts);
        Assert.Equal(first.ReasonCounts, second.ReasonCounts);
        Assert.Single(second.Determinations);
        Assert.Single(second.LabEvents);
        Assert.Single(second.Deviations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_supersede_ends_the_same_whichever_determination_arrives_first(bool supersedeFirst)
    {
        await using var batch = await SeedBatchAsync();
        var a = Failed(batch.BatchId, at: FirstEventAt.AddHours(6),
            reasons: ["LOW_FAT"], new OutOfSpecDetail("FatPercent", 3.20m, "Min", 3.50m));
        var b = Cleared(batch.BatchId, at: FirstEventAt.AddHours(8), supersedes: a.DeterminationId);

        foreach (var determination in supersedeFirst ? new BatchDeterminationEventBase[] { b, a } : [a, b])
        {
            Assert.Equal(BatchDeterminationOutcome.Applied, await HandleAsync(determination));
        }

        var state = await batch.ReadAsync();
        Assert.Equal(BatchStatus.Cleared, state.Batch.Status);

        var stored = state.Determinations.ToDictionary(d => d.DeterminationId);
        Assert.True(stored[a.DeterminationId].IsSuperseded);
        Assert.Equal(b.DeterminedAtUtc, stored[a.DeterminationId].SupersededAt);
        Assert.False(stored[b.DeterminationId].IsSuperseded);
        Assert.Null(stored[b.DeterminationId].SupersededAt);

        // A's deviation stays as history, but only B, the current determination, is counted.
        Assert.Single(state.Deviations);
        Assert.Equal(2, state.LabEvents.Count);
        Assert.Equal((Batches: 1, Pending: 0, Cleared: 1, Failed: 0, Deviations: 0, Lab: 0), state.Counts);
        Assert.Empty(state.ReasonCounts);
    }

    [Fact]
    public async Task An_unknown_reason_code_is_stored_and_counted_under_its_own_name()
    {
        await using var batch = await SeedBatchAsync();

        Assert.Equal(BatchDeterminationOutcome.Applied,
            await HandleAsync(Failed(batch.BatchId, at: FirstEventAt.AddHours(6), reasons: ["XYZ"])));

        var state = await batch.ReadAsync();
        var deviation = Assert.Single(state.Deviations);
        Assert.Equal("XYZ", deviation.ReasonCode);
        Assert.Null(deviation.Parameter);
        Assert.Equal((Batches: 1, Pending: 0, Cleared: 0, Failed: 1, Deviations: 1, Lab: 1), state.Counts);
        Assert.Equal(Reasons(("XYZ", 1)), state.ReasonCounts);
    }

    [Fact]
    public async Task A_missing_batch_is_retryable_and_leaves_nothing_behind()
    {
        await using var batch = await SeedBatchAsync(create: false);
        var cleared = Cleared(batch.BatchId, at: FirstEventAt.AddHours(6));

        var error = await Assert.ThrowsAsync<BatchNotFoundException>(() => HandleAsync(cleared));
        Assert.IsAssignableFrom<RetryableEventException>(error);
        Assert.Equal(batch.BatchId, error.BatchId);

        // The processed_messages row was rolled back, so the retry is not taken for a duplicate.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();
            Assert.False(await db.ProcessedMessages.AnyAsync(m => m.SourceEventId == cleared.EventId));
            Assert.False(await db.TimelineEvents.AnyAsync(e => e.SourceEventId == cleared.EventId));
        }

        // Once SCRUM-130 has created the batch, the retry applies.
        await batch.CreateAsync();
        Assert.Equal(BatchDeterminationOutcome.Applied, await HandleAsync(cleared));
        Assert.Equal(BatchStatus.Cleared, (await batch.ReadAsync()).Batch.Status);
    }

    private async Task<BatchDeterminationOutcome> HandleAsync(BatchDeterminationEventBase determination)
    {
        using var scope = factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<BatchDeterminationHandler>();
        var payload = JsonSerializer.Serialize(determination, determination.GetType(), Json);
        return await handler.HandleAsync(new BatchDeterminationMessage(Topic, determination.EventType, payload));
    }

    private async Task<TestBatch> SeedBatchAsync(bool snapshotResolvedAndProcessed = false, bool create = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var batch = new TestBatch(factory, $"285-FM-{suffix}", $"TEST-LAB-{suffix}", snapshotResolvedAndProcessed);
        if (create)
        {
            await batch.CreateAsync();
        }
        return batch;
    }

    private static BatchClearedEvent Cleared(string batchId, DateTime at, Guid? supersedes = null) => new()
    {
        BatchId = batchId,
        DispatchNumber = "DSP-TEST",
        ProductLine = "FM",
        TimestampUtc = at,
        DeterminationId = Guid.NewGuid(),
        SupersedesDeterminationId = supersedes,
        DeterminedBy = "analyst-1",
        DeterminedAtUtc = at,
        CorrelationId = "test",
    };

    private static BatchFailedEvent Failed(string batchId, DateTime at, string[] reasons, params OutOfSpecDetail[] details) => new()
    {
        BatchId = batchId,
        DispatchNumber = "DSP-TEST",
        ProductLine = "FM",
        TimestampUtc = at,
        IsDeviation = details.Length > 0,
        DeterminationId = Guid.NewGuid(),
        DeterminedBy = "analyst-1",
        DeterminedAtUtc = at,
        CorrelationId = "test",
        ReasonCodes = reasons,
        OutOfSpecDetails = details,
    };

    private static Dictionary<string, int> Reasons(params (string Code, int Count)[] counts) =>
        counts.ToDictionary(c => c.Code, c => c.Count);

    private sealed record BatchState(
        BatchRecord Batch,
        List<LabDetermination> Determinations,
        List<TimelineEvent> LabEvents,
        List<DeviationRecord> Deviations,
        (int Batches, int Pending, int Cleared, int Failed, int Deviations, int Lab) Counts,
        Dictionary<string, int> ReasonCounts,
        HashSet<Guid> Processed);

    /// <summary>
    /// A batch at AwaitingLab, counted as one pending batch on its day as SCRUM-130 would
    /// count it, on a facility of its own. Disposing it removes everything the test wrote.
    /// </summary>
    private sealed class TestBatch(
        TraceabilityApiFactory factory, string batchId, string facility, bool snapshotResolvedAndProcessed)
        : IAsyncDisposable
    {
        public string BatchId { get; } = batchId;

        public async Task CreateAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

            db.BatchRecords.Add(new BatchRecord
            {
                BatchId = BatchId,
                ProductLine = "FM",
                Facility = facility,
                Status = BatchStatus.AwaitingLab,
                Completeness = TraceCompleteness.Partial,
                FirstEventAt = FirstEventAt,
                LastEventAt = FirstEventAt.AddHours(2),
            });

            db.DailyQualityAggregates.Add(new DailyQualityAggregate
            {
                Date = ProductionDay,
                ProductLine = "FM",
                Facility = facility,
                BatchCount = 1,
                PendingCount = 1,
                UpdatedAt = FirstEventAt,
            });

            if (snapshotResolvedAndProcessed)
            {
                db.UpstreamSnapshots.Add(new UpstreamSnapshot
                {
                    BatchId = BatchId,
                    ResolutionStatus = ResolutionStatus.Resolved,
                    Payload = "{}",
                    AttemptCount = 1,
                    ResolvedAt = FirstEventAt,
                });

                db.TimelineEvents.Add(new TimelineEvent
                {
                    BatchId = BatchId,
                    Checkpoint = Checkpoint.Processing,
                    EventType = "ProcessingCompleted",
                    OccurredAt = FirstEventAt.AddHours(2),
                    Payload = "{}",
                    SourceEventId = Guid.NewGuid(),
                    ContractVersion = "v1",
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task<BatchState> ReadAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

            var labEvents = await db.TimelineEvents.AsNoTracking()
                .Where(e => e.BatchId == BatchId && e.Checkpoint == Checkpoint.Lab)
                .ToListAsync();
            var eventIds = labEvents.Select(e => e.SourceEventId).ToList();

            var counts = await db.DailyQualityAggregates.AsNoTracking()
                .SingleAsync(a => a.Facility == facility && a.Date == ProductionDay && a.ProductLine == "FM");

            return new BatchState(
                await db.BatchRecords.AsNoTracking().SingleAsync(b => b.BatchId == BatchId),
                await db.LabDeterminations.AsNoTracking().Where(d => d.BatchId == BatchId).ToListAsync(),
                labEvents,
                await db.DeviationRecords.AsNoTracking().Where(d => d.BatchId == BatchId).ToListAsync(),
                (counts.BatchCount, counts.PendingCount, counts.ClearedCount, counts.FailedCount,
                    counts.DeviationCount, counts.LabDeviationCount),
                await db.DailyReasonCodeAggregates.AsNoTracking()
                    .Where(a => a.Facility == facility && a.Date == ProductionDay)
                    .ToDictionaryAsync(a => a.ReasonCode, a => a.DeviationCount),
                [.. await db.ProcessedMessages.AsNoTracking()
                    .Where(m => m.ConsumerGroup == BatchDeterminationHandler.ConsumerGroup && eventIds.Contains(m.SourceEventId))
                    .Select(m => m.SourceEventId)
                    .ToListAsync()]);
        }

        public async ValueTask DisposeAsync()
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TraceabilityDbContext>();

            var eventIds = await db.TimelineEvents
                .Where(e => e.BatchId == BatchId)
                .Select(e => e.SourceEventId)
                .ToListAsync();
            await db.ProcessedMessages.Where(m => eventIds.Contains(m.SourceEventId)).ExecuteDeleteAsync();

            // Cascades to the timeline, deviations, determinations and snapshot.
            await db.BatchRecords.Where(b => b.BatchId == BatchId).ExecuteDeleteAsync();
            await db.DailyQualityAggregates.Where(a => a.Facility == facility).ExecuteDeleteAsync();
            await db.DailyReasonCodeAggregates.Where(a => a.Facility == facility).ExecuteDeleteAsync();
        }
    }
}
