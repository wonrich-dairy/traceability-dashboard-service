using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wonrich.Processing.Contracts.Events;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.Application;

/// <summary>
/// The two Processing consumers (SCRUM-130): stage events and hold events, one handler per
/// event type, registered under the eventType header Processing sends (the record's name).
/// </summary>
public static class ProcessingConsumers
{
    public const string StageEvents = "StageEvents";
    public const string HoldEvents = "HoldEvents";

    public static IServiceCollection AddProcessingConsumers(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<IAggregateWriter, AggregateWriter>();
        services.TryAddScoped<ProcessingEventApplier>();

        services.AddEventConsumer(
            KafkaConsumerOptions.From(configuration, StageEvents,
                topic: "wonrich.processing.stage-events.v1",
                consumerGroup: "traceability-stage-events",
                deadLetterTopic: "wonrich.dlq.traceability-stage-events.v1"),
            StageHandlers);

        services.AddEventConsumer(
            KafkaConsumerOptions.From(configuration, HoldEvents,
                topic: "wonrich.processing.hold-events.v1",
                consumerGroup: "traceability-hold-events",
                deadLetterTopic: "wonrich.dlq.traceability-hold-events.v1"),
            HoldHandlers);

        return services;
    }

    public static void StageHandlers(EventHandlerMap handlers) => handlers
        .Add<ProcessingEventHandler<MilkAllocatedToMixingTankEvent>>(nameof(MilkAllocatedToMixingTankEvent))
        .Add<ProcessingEventHandler<ProcessingStageRecordedEvent>>(nameof(ProcessingStageRecordedEvent))
        .Add<ProcessingEventHandler<ProcessingCompletedEvent>>(nameof(ProcessingCompletedEvent));

    public static void HoldHandlers(EventHandlerMap handlers) => handlers
        .Add<ProcessingEventHandler<ProcessingHoldRaisedEvent>>(nameof(ProcessingHoldRaisedEvent))
        .Add<ProcessingEventHandler<ProcessingHoldResolvedEvent>>(nameof(ProcessingHoldResolvedEvent));
}

/// <summary>Deserialises one Processing event type and hands it to <see cref="ProcessingEventApplier"/>.</summary>
public sealed class ProcessingEventHandler<TEvent>(ProcessingEventApplier applier)
    : TypedEventHandler<TEvent> where TEvent : ProcessingEventBase
{
    protected override Task HandleAsync(TEvent processingEvent, ConsumedEvent consumed, CancellationToken cancellationToken) =>
        applier.ApplyAsync(processingEvent, consumed, cancellationToken);
}

/// <summary>
/// Applies one Processing event to the read model (SCRUM-130), inside the event's transaction
/// (<see cref="EventProcessor"/>): finds or creates the batch, adds the Processing timeline
/// event and its deviation, moves FirstEventAt and LastEventAt, recomputes the status from
/// everything stored (<see cref="BatchStatusRules"/>), and updates both aggregate tables.
/// An event for a dispatch with no batch yet is kept until the batch exists.
/// </summary>
public sealed class ProcessingEventApplier(
    TraceabilityDbContext db,
    IAggregateWriter aggregates,
    TimeProvider time,
    IConfiguration configuration,
    ILogger<ProcessingEventApplier> logger)
{
    // Processing runs one factory and its events carry no facility, so it is configured.
    public const string DefaultFacility = "FACTORY-01";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The eventType headers Processing sends, for reading kept events back as their type.
    private static readonly Dictionary<string, Type> EventTypes = new[]
    {
        typeof(MilkAllocatedToMixingTankEvent), typeof(ProcessingStageRecordedEvent), typeof(ProcessingCompletedEvent),
        typeof(ProcessingHoldRaisedEvent), typeof(ProcessingHoldResolvedEvent),
    }.ToDictionary(t => t.Name);

    private readonly string _facility = configuration["Traceability:FactoryFacility"] is { Length: > 0 } f ? f : DefaultFacility;

    public async Task ApplyAsync(ProcessingEventBase processingEvent, ConsumedEvent consumed, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;

        // Before allocation Processing has no batch code, and puts the dispatch number in BatchId.
        if (string.IsNullOrWhiteSpace(processingEvent.BatchId) || processingEvent.BatchId == processingEvent.DispatchNumber)
        {
            await ApplyToDispatchAsync(processingEvent, consumed, now, cancellationToken);
            return;
        }

        await db.LockBatchAsync(processingEvent.BatchId, cancellationToken);
        var batch = await db.BatchRecords
            .Include(b => b.UpstreamSnapshot)
            .SingleOrDefaultAsync(b => b.BatchId == processingEvent.BatchId, cancellationToken);
        var created = batch is null;
        batch ??= NewBatch(processingEvent);

        if (batch.DispatchRef is null && !string.IsNullOrWhiteSpace(processingEvent.DispatchNumber))
        {
            batch.DispatchRef = processingEvent.DispatchNumber;
        }

        var events = new List<(ProcessingEventBase Event, string Payload)> { (processingEvent, consumed.Payload) };
        events.AddRange(await TakePendingAsync(batch.DispatchRef, cancellationToken));

        await ApplyToBatchAsync(batch, created, events, now, cancellationToken);
    }

    // An event raised against a dispatch: applied to its batch if one exists, kept otherwise.
    private async Task ApplyToDispatchAsync(
        ProcessingEventBase processingEvent, ConsumedEvent consumed, DateTime now, CancellationToken cancellationToken)
    {
        // One dispatch can be split across batches; an event can be on one timeline only
        // (SourceEventId is unique), so it goes to the first batch made from the dispatch.
        await db.LockDispatchAsync(processingEvent.DispatchNumber, cancellationToken);
        var batch = await db.BatchRecords
            .Include(b => b.UpstreamSnapshot)
            .Where(b => b.DispatchRef == processingEvent.DispatchNumber)
            .OrderBy(b => b.FirstEventAt)
            .ThenBy(b => b.BatchId)
            .FirstOrDefaultAsync(cancellationToken);

        if (batch is not null)
        {
            await ApplyToBatchAsync(batch, created: false, [(processingEvent, consumed.Payload)], now, cancellationToken);
            return;
        }

        db.PendingDispatchEvents.Add(new PendingDispatchEvent
        {
            DispatchNumber = processingEvent.DispatchNumber,
            EventType = consumed.EventType ?? processingEvent.GetType().Name,
            SourceEventId = processingEvent.EventId,
            Topic = consumed.Topic,
            Payload = consumed.Payload,
            ReceivedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("{EventType} for dispatch {DispatchNumber} kept until a batch from that dispatch arrives",
            consumed.EventType, processingEvent.DispatchNumber);
    }

    private async Task<List<(ProcessingEventBase Event, string Payload)>> TakePendingAsync(
        string? dispatchNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dispatchNumber))
        {
            return [];
        }

        var pending = await db.PendingDispatchEvents
            .Where(p => p.DispatchNumber == dispatchNumber)
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);

        db.PendingDispatchEvents.RemoveRange(pending);

        if (pending.Count > 0)
        {
            logger.LogInformation("Attaching {Count} kept event(s) for dispatch {DispatchNumber}", pending.Count, dispatchNumber);
        }

        return [.. pending.Select(p => (
            (ProcessingEventBase)(JsonSerializer.Deserialize(p.Payload, EventTypes[p.EventType], Json)
                ?? throw new JsonException($"Kept event {p.SourceEventId} is JSON null.")),
            p.Payload))];
    }

    private BatchRecord NewBatch(ProcessingEventBase processingEvent)
    {
        // A batch code is [day]-[product]-[letter]. CD is accepted although Processing's contract
        // comment leaves it out: ProductLines lists all six.
        var parts = processingEvent.BatchId.Split('-');
        var productLine = (parts.Length >= 3 ? ProductLines.Normalize(parts[1]) : null)
            ?? throw new FormatException($"Batch code '{processingEvent.BatchId}' has no known product line.");

        var occurredAt = ProcessingEventFacts.Of(processingEvent).OccurredAt;
        var batch = new BatchRecord
        {
            BatchId = processingEvent.BatchId,
            DispatchRef = string.IsNullOrWhiteSpace(processingEvent.DispatchNumber) ? null : processingEvent.DispatchNumber,
            ProductLine = productLine,
            Facility = _facility,
            Status = BatchStatus.Processing,
            Completeness = TraceCompleteness.Partial,
            FirstEventAt = occurredAt,
            LastEventAt = occurredAt,
        };

        // A batch from each consumer at once: the second insert breaks the unique BatchId, the
        // transaction rolls back, and the retry finds the batch.
        db.BatchRecords.Add(batch);
        logger.LogInformation("Batch {BatchId} created ({ProductLine}, {Facility})", batch.BatchId, productLine, _facility);
        return batch;
    }

    private async Task ApplyToBatchAsync(
        BatchRecord batch,
        bool created,
        IReadOnlyList<(ProcessingEventBase Event, string Payload)> events,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // A new batch is counted as pending, then moved like any other.
        var statusBefore = created ? BatchStatus.Processing : batch.Status;

        // Fixed by the batch code: the day of the year in it, in the year nearest the batch's
        // first event, so a late, earlier event cannot move the batch to another day's row.
        var productionDay = FactoryCalendar.ProductionDay(batch.BatchId, batch.FirstEventAt);

        var deviations = 0;
        foreach (var (processingEvent, payload) in events)
        {
            var facts = ProcessingEventFacts.Of(processingEvent);

            var timelineEvent = new TimelineEvent
            {
                BatchId = batch.BatchId,
                Checkpoint = Checkpoint.Processing,
                EventType = facts.EventType,
                OccurredAt = facts.OccurredAt,
                RecordedBy = facts.RecordedBy,
                IsDeviation = processingEvent.IsDeviation,
                Payload = payload,
                SourceEventId = processingEvent.EventId,
                ContractVersion = processingEvent.SchemaVersion,
            };
            db.TimelineEvents.Add(timelineEvent);

            if (facts.Deviation is { } deviation)
            {
                // Saved first: the deviation points at the timeline event's Id.
                await db.SaveChangesAsync(cancellationToken);
                db.DeviationRecords.Add(new DeviationRecord
                {
                    BatchId = batch.BatchId,
                    TimelineEventId = timelineEvent.Id,
                    Checkpoint = Checkpoint.Processing,
                    OccurredAt = facts.OccurredAt,
                    Parameter = deviation.Parameter,
                    ObservedValue = deviation.ObservedValue,
                    Description = deviation.Description,
                });
                deviations++;
            }

            // Minimum and maximum: a late event can extend either end.
            batch.FirstEventAt = facts.OccurredAt < batch.FirstEventAt ? facts.OccurredAt : batch.FirstEventAt;
            batch.LastEventAt = facts.OccurredAt > batch.LastEventAt ? facts.OccurredAt : batch.LastEventAt;
        }

        await db.SaveChangesAsync(cancellationToken);

        batch.Status = BatchStatusRules.StatusOf(await StoredFactsAsync(batch.BatchId, cancellationToken));
        if (batch.Completeness == TraceCompleteness.Partial && await IsCompleteAsync(batch, cancellationToken))
        {
            batch.Completeness = TraceCompleteness.Complete;
        }
        await db.SaveChangesAsync(cancellationToken);

        var statusCounts = new Dictionary<StatusBucket, int>();
        if (created)
        {
            statusCounts[StatusBucket.Pending] = 1;
        }
        foreach (var (bucket, change) in AggregateDelta.StatusMove(statusBefore, batch.Status))
        {
            statusCounts[bucket] = statusCounts.GetValueOrDefault(bucket) + change;
        }

        await aggregates.ApplyAsync(batch, productionDay, new AggregateDelta
        {
            Batches = created ? 1 : 0,
            StatusCounts = statusCounts,
            ProcessingDeviations = deviations,
            // Processing sends no reason codes, so its deviations are counted as Unspecified.
            ReasonCodes = deviations > 0
                ? new Dictionary<string, int> { [DailyReasonCodeAggregate.Unspecified] = deviations }
                : new Dictionary<string, int>(),
        }, now, cancellationToken);
    }

    // Everything stored for the batch that decides its status, this event included.
    private async Task<StoredBatchFacts> StoredFactsAsync(string batchId, CancellationToken cancellationToken)
    {
        var activeDetermination = await db.LabDeterminations
            .Where(d => d.BatchId == batchId && !d.IsSuperseded)
            .OrderByDescending(d => d.DeterminedAt)
            .Select(d => (DeterminationResult?)d.Result)
            .FirstOrDefaultAsync(cancellationToken);

        var holdEvents = await db.TimelineEvents
            .Where(e => e.BatchId == batchId && (e.EventType == "ProcessingHoldRaised" || e.EventType == "ProcessingHoldResolved"))
            .Select(e => new { e.EventType, e.Payload })
            .ToListAsync(cancellationToken);

        return new StoredBatchFacts
        {
            ActiveDetermination = activeDetermination,
            HoldsRaised = [.. holdEvents.Where(e => e.EventType == "ProcessingHoldRaised").Select(e => HoldId(e.Payload))],
            HoldsResolved = [.. holdEvents.Where(e => e.EventType == "ProcessingHoldResolved").Select(e => HoldId(e.Payload))],
            ProcessingCompleted = await db.TimelineEvents.AnyAsync(
                e => e.BatchId == batchId && e.EventType == "ProcessingCompleted", cancellationToken),
        };
    }

    private static string HoldId(string payload)
    {
        using var body = JsonDocument.Parse(payload);
        return body.RootElement.TryGetProperty("holdId", out var holdId) ? holdId.GetString() ?? "" : "";
    }

    // The same rule the batch record shows: MCC and Intake from the resolved upstream snapshot,
    // Processing and Lab from timeline events. This event is a Processing one.
    private async Task<bool> IsCompleteAsync(BatchRecord batch, CancellationToken cancellationToken) =>
        batch.UpstreamSnapshot?.ResolutionStatus == ResolutionStatus.Resolved
        && await db.TimelineEvents.AnyAsync(
            e => e.BatchId == batch.BatchId && e.Checkpoint == Checkpoint.Lab, cancellationToken);
}
