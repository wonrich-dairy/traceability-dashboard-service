using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wonrich.QualityLab.Contracts.Events;
using TraceabilityService.Api.Infrastructure.Persistence;
using TraceabilityService.Domain.Entities;
using TraceabilityService.Infrastructure.Kafka;

namespace TraceabilityService.Application;

/// <summary>
/// The Quality Lab determination consumer (SCRUM-131): BatchCleared and BatchFailed from
/// wonrich.quality-lab.batch-determinations.v1, each to a <see cref="LabDeterminationHandler{TEvent}"/>.
/// </summary>
public static class LabDeterminationConsumer
{
    public const string Name = "BatchDeterminations";

    public static IServiceCollection AddLabDeterminationConsumer(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<AggregateWriter>();
        return services.AddEventConsumer(
            KafkaConsumerOptions.From(configuration, Name,
                topic: "wonrich.quality-lab.batch-determinations.v1",
                consumerGroup: "traceability-batch-determinations",
                deadLetterTopic: "wonrich.dlq.traceability-batch-determinations.v1"),
            handlers => handlers
                .Add<LabDeterminationHandler<BatchClearedEvent>>("BatchCleared")
                .Add<LabDeterminationHandler<BatchFailedEvent>>("BatchFailed"));
    }
}

/// <summary>
/// Applies one Quality Lab determination to the read model (SCRUM-131): the
/// lab_determinations row (and the one it supersedes), a Lab timeline event, a failure's
/// deviations, the batch's status, LastEventAt and completeness, and both daily aggregate
/// tables. Runs inside the event's transaction (<see cref="EventProcessor"/>), which also
/// skips a redelivered event. What the determination does is decided by
/// <see cref="DeterminationResolver"/>; this class only reads and writes.
/// </summary>
public sealed class LabDeterminationHandler<TEvent>(
    TraceabilityDbContext db, AggregateWriter aggregates, TimeProvider time, ILogger<LabDeterminationHandler<TEvent>> logger)
    : TypedEventHandler<TEvent> where TEvent : BatchDeterminationEventBase
{
    private readonly TraceabilityDbContext _db = db;
    private readonly TimeProvider _time = time;

    /// <exception cref="BatchNotFoundException">
    /// The batch is not here yet. The transaction rolls back, so a retry starts clean.
    /// </exception>
    protected override async Task HandleAsync(TEvent determination, ConsumedEvent consumed, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var batch = await _db.BatchRecords
            .Include(b => b.UpstreamSnapshot)
            .SingleOrDefaultAsync(b => b.BatchId == determination.BatchId, cancellationToken)
            ?? throw new BatchNotFoundException(determination.BatchId);

        var stored = await _db.LabDeterminations
            .Where(d => d.BatchId == batch.BatchId)
            .ToListAsync(cancellationToken);

        var deviations = determination is BatchFailedEvent failed ? LabDeviations.From(failed) : [];
        var result = determination is BatchFailedEvent ? DeterminationResult.Failed : DeterminationResult.Cleared;

        var resolution = DeterminationResolver.Resolve(
            batch.Status,
            await FactsOfAsync(batch.BatchId, stored, cancellationToken),
            new DeterminationFacts(
                determination.DeterminationId,
                result,
                determination.DeterminedAtUtc,
                determination.SupersedesDeterminationId,
                [.. deviations.Select(d => d.AggregateReasonCode)]));

        // The same determination under another event ID: it is already applied.
        if (resolution.IsDuplicate)
        {
            logger.LogInformation("Determination {DeterminationId} for {BatchId} is already applied", determination.DeterminationId, batch.BatchId);
            return;
        }

        // Saved first: the determination and deviation rows point at its Id.
        var timelineEvent = new TimelineEvent
        {
            BatchId = batch.BatchId,
            Checkpoint = Checkpoint.Lab,
            EventType = determination.EventType,
            OccurredAt = determination.DeterminedAtUtc,
            RecordedBy = determination.DeterminedBy,
            IsDeviation = determination.IsDeviation || deviations.Count > 0,
            Payload = consumed.Payload,
            SourceEventId = determination.EventId,
            ContractVersion = determination.SchemaVersion,
        };
        _db.TimelineEvents.Add(timelineEvent);
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var replaced in resolution.Supersede)
        {
            var row = stored.Single(d => d.DeterminationId == replaced.DeterminationId);
            row.IsSuperseded = true;
            row.SupersededAt = replaced.SupersededAt ?? row.SupersededAt;
        }

        _db.LabDeterminations.Add(new LabDetermination
        {
            DeterminationId = determination.DeterminationId,
            BatchId = batch.BatchId,
            Result = result,
            DeterminedAt = determination.DeterminedAtUtc,
            DeterminedBy = determination.DeterminedBy,
            SupersedesDeterminationId = determination.SupersedesDeterminationId,
            IsSuperseded = !resolution.IsActive,
            SupersededAt = resolution.SupersededAt,
            TimelineEventId = timelineEvent.Id,
        });

        // Kept for a determination that arrives already superseded too: they are its history.
        // Only the current determination's deviations are counted in the aggregates.
        _db.DeviationRecords.AddRange(deviations.Select(d => new DeviationRecord
        {
            BatchId = batch.BatchId,
            TimelineEventId = timelineEvent.Id,
            Checkpoint = Checkpoint.Lab,
            OccurredAt = determination.DeterminedAtUtc,
            ReasonCode = d.ReasonCode,
            Parameter = d.Parameter,
            ObservedValue = d.ObservedValue,
            Limit = d.Limit,
            Description = d.Description,
        }));

        // The production day comes from the batch's first event, before this one can move it.
        var productionDay = FactoryCalendar.ProductionDay(batch.BatchId, batch.FirstEventAt);

        batch.Status = resolution.NewStatus;
        batch.FirstEventAt = Min(batch.FirstEventAt, determination.DeterminedAtUtc);
        batch.LastEventAt = Max(batch.LastEventAt, determination.DeterminedAtUtc);
        if (batch.Completeness == TraceCompleteness.Partial && await IsCompleteAsync(batch, cancellationToken))
        {
            batch.Completeness = TraceCompleteness.Complete;
        }

        await _db.SaveChangesAsync(cancellationToken);

        await aggregates.ApplyAsync(batch, productionDay, new AggregateDelta
        {
            StatusCounts = resolution.Changes.StatusCounts,
            LabDeviations = resolution.Changes.LabDeviations,
            ReasonCodes = resolution.Changes.ReasonCodes,
        }, now, cancellationToken);

        logger.LogInformation("Determination {DeterminationId} applied to {BatchId}: {Status}, {Current}",
            determination.DeterminationId, batch.BatchId, batch.Status, resolution.IsActive ? "current" : "already superseded");
    }

    // Each stored determination with the codes of its deviation rows, which are what the
    // aggregates counted for it.
    private async Task<List<DeterminationFacts>> FactsOfAsync(
        string batchId, List<LabDetermination> stored, CancellationToken cancellationToken)
    {
        var reasons = (await _db.DeviationRecords
                .Where(r => r.BatchId == batchId && r.Checkpoint == Checkpoint.Lab)
                .Select(r => new { r.TimelineEventId, r.ReasonCode })
                .ToListAsync(cancellationToken))
            .ToLookup(r => r.TimelineEventId, r => r.ReasonCode ?? DailyReasonCodeAggregate.Unspecified);

        return [.. stored.Select(d => new DeterminationFacts(
            d.DeterminationId,
            d.Result,
            d.DeterminedAt,
            d.SupersedesDeterminationId,
            [.. reasons[d.TimelineEventId]],
            d.IsSuperseded))];
    }

    // The same rule the batch record shows: MCC and Intake come from the resolved upstream
    // snapshot, Processing and Lab from timeline events. This event is the Lab one.
    private async Task<bool> IsCompleteAsync(BatchRecord batch, CancellationToken cancellationToken) =>
        batch.UpstreamSnapshot?.ResolutionStatus == ResolutionStatus.Resolved
        && await _db.TimelineEvents.AnyAsync(
            e => e.BatchId == batch.BatchId && e.Checkpoint == Checkpoint.Processing, cancellationToken);

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
